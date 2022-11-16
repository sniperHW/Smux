using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Text;
using System.Threading;
using System.Collections.Generic;

namespace Smux;

public class Session
{
    private class writeRequest : IComparable 
    {
        public uint prio;
        public Frame frame;
        private SemaphoreSlim semaphore = new SemaphoreSlim(0);
        public int Result{get;set;}
        public void OnResult(int result)
        {   
            Result = result;
            semaphore.Release();
        }
        public writeRequest(uint prio,Frame frame)
        {
            this.prio = prio;
            this.frame = frame;
        }
        public async Task  WaitAsync(CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);
        }

        public int CompareTo(Object? obj) 
        {
            if(obj == null) return 1;
            
            var other = obj as writeRequest;

            if(other is null) {
                throw new ArgumentException("Object is not a writeRequest");
            } else {
                return prio.CompareTo(other.prio);
            }   
        }

    }


    private NetworkStream netstream;
    private bool netstreamClosed = false;
    public Config Config{get;}
    private uint nextStreamID;
    private Mutex nextStreamIDLock = new Mutex();
    private int bucket;
    private BufferBlock<byte> bucketNotify = new BufferBlock<byte>(new DataflowBlockOptions()
    {
        BoundedCapacity = 1
    });
    private Dictionary<uint,Stream> streams = new Dictionary<uint,Stream>();
    private Mutex streamLock = new Mutex();
    private CancellationTokenSource die = new CancellationTokenSource();
    private int dieOnce = 0;

    private int goAway;

    private BufferBlock<writeRequest> shaper = new BufferBlock<writeRequest>();

    private BufferBlock<writeRequest> writes = new BufferBlock<writeRequest>();

    private BufferBlock<Stream> acceptCh = new BufferBlock<Stream>();

    private int dataReady = 0;

    public Session(Config config,NetworkStream s,bool client)
    {
        Config = config;
        bucket = config.MaxReceiveBuffer;
        netstream = s;

        if(client)
        {
            nextStreamID = 1;
        } else {
            nextStreamID = 0;
        }

        #pragma warning disable CS4014
        Task.Run(shaperLoop);
        Task.Run(recvLoop);
        Task.Run(sendLoop);
        if(!Config.KeepAliveDisabled){
            Task.Run(keepalive);
            Task.Run(ping);
        }
        #pragma warning restore CS4014
    }

    public void returnTokens(int n)
    {
        if(Interlocked.Add(ref bucket,n) > 0)
        {
            bucketNotify.Post((byte)0);
        }
    }

    public void Close()
    {
        if(Interlocked.CompareExchange(ref dieOnce,1,0) == 0)
        {
            streamLock.WaitOne();
            foreach( KeyValuePair<uint,Stream> kvp in streams ){
                kvp.Value.SessionClose();
            }
            streamLock.ReleaseMutex();
            netstream.Socket.Close();
            die.Cancel();
        }
    }

    public void StreamClose(uint sid)
    {
        streamLock.WaitOne();
        if(streams.ContainsKey(sid)){
            var stream = streams[sid];
            var n = stream.RecycleTokens();
            if(n > 0 && Interlocked.Add(ref bucket,n) > 0)
            {
                bucketNotify.Post((byte)0);
            }
        }
        streamLock.ReleaseMutex();
    }


    public async Task<int> WriteFrame(Frame f)
    {
        return await WriteFrameInternal(f,0,null);
    }

    public async Task<int> WriteFrameInternal(Frame f,uint prio,int? timeout)
    {
        var req = new writeRequest(prio,f);
        await shaper.SendAsync(req);
        if(timeout is null) {
            await req.WaitAsync(die.Token);
        } else {
            var source = CancellationTokenSource.CreateLinkedTokenSource(die.Token);
            source.CancelAfter((int)timeout);
            await req.WaitAsync(source.Token);
        }
        return req.Result;
    }

    private async Task readfullAsync(byte[] b)
    {
        var offset = 0;
        for(;offset < b.Length;)
        {
            var n = await netstream.ReadAsync(b,offset,b.Length-offset,die.Token);
            if(n <= 0) {
                throw new SmuxException("ErrClosedPipe");
            }
            offset += n;
        }
    }

    public async void recvLoop()
    {
        try{
            var hdr = new RawHeader();
            var updHdr = new UpdHeader();
            for(;;){
                for(;bucket <= 0;){
                    await bucketNotify.ReceiveAsync(die.Token);
                }
                await readfullAsync(hdr.H);
                if(hdr.Version() != (byte)Config.Version) {
                    throw new SmuxException("ErrInvalidProtocol");
                }

                dataReady = 1;

                var sid = hdr.StreamID();

                switch(hdr.Cmd())
                {
                    case Frame.cmdNOP:
                    break;
                    case Frame.cmdSYN:
                        streamLock.WaitOne();
                        if(!streams.ContainsKey(sid)) {
                            var stream = new Stream(sid,Config.MaxFrameSize,this);
                            streams[sid] = stream;
                            acceptCh.Post(stream);        
                        }
                        streamLock.ReleaseMutex();
                        break;
                    case Frame.cmdFIN:
                        streamLock.WaitOne();
                        if(streams.ContainsKey(sid)) {
                            var stream = streams[sid];
                            stream.Fin();
                            stream.NotifyReadEvent();       
                        }
                        streamLock.ReleaseMutex();                    
                        break;
                    case Frame.cmdPSH:
                        if(hdr.Length() > 0){
                            var buff = new byte[hdr.Length()];
                            await readfullAsync(buff);
                            streamLock.WaitOne();
                            if(streams.ContainsKey(sid)) {  
                                var stream = streams[sid];
                                stream.pushBytes(buff);
                                stream.NotifyReadEvent(); 
                                Interlocked.Add(ref bucket,-buff.Length);     
                            }
                            streamLock.ReleaseMutex(); 
                        }
                        break;
                    case Frame.cmdUPD:
                        await readfullAsync(updHdr.H);
                        streamLock.WaitOne();
                        if(streams.ContainsKey(sid)) {  
                            var stream = streams[sid];
                            stream.Update(updHdr.Consumed,updHdr.Window);    
                        }
                        streamLock.ReleaseMutex(); 
                        break;
                    default:
                        throw new SmuxException("ErrInvalidProtocol");
                }
            }
        }
        catch(Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private async void ping()
    {
        for(;;)
        {
            try
            {
                await Task.Delay((int)Config.KeepAliveInterval,die.Token);
                await WriteFrameInternal(new Frame((byte)Config.Version, Frame.cmdNOP, 0),0, (int)Config.KeepAliveInterval);
                bucketNotify.Post((byte)0);
            }
            catch(OperationCanceledException)
            {
                if(die.IsCancellationRequested)
                {
                    break;
                } 
                else 
                {
                    bucketNotify.Post((byte)0);
                }
            }catch(Exception)
            {
                break;
            }
        }
    }


    private async void keepalive()
    {
        for(;;)
        {
            try
            {
                await Task.Delay((int)Config.KeepAliveTimeout,die.Token);
                if(Interlocked.CompareExchange(ref dataReady,0,1) != 1)
                {
                    if(bucket > 0)
                    {
                        Close();
                        break;
                    }
                }
            }
            catch(Exception)
            {
                break;
            }
        }    
    }

    private async void sendLoop()
    {
        try{
            using MemoryStream memoryStream = new MemoryStream();
            for(;;){
                var req = await writes.ReceiveAsync(die.Token);
                memoryStream.WriteByte(req.frame.Ver);
                memoryStream.WriteByte(req.frame.Cmd);
                if(req.frame.Data is null){
                    memoryStream.Write(BitConverter.GetBytes(Endian.Little((short)0)));
                } else {
                    memoryStream.Write(BitConverter.GetBytes(Endian.Little((short)req.frame.Length)));
                }
                memoryStream.Write(BitConverter.GetBytes(Endian.Little((int)req.frame.Sid)));
                if(req.frame.Data != null){
                    memoryStream.Write(req.frame.Data,req.frame.Offset,req.frame.Length);
                }
                await netstream.WriteAsync(memoryStream.GetBuffer(),0,(int)memoryStream.Position,die.Token);
                req.OnResult((int)memoryStream.Position - Frame.headerSize);
                memoryStream.Position = 0;
                memoryStream.SetLength(0);
            }
        }catch(Exception e){
            Console.WriteLine(e);
            return;
        }
    }

    private async void shaperLoop()
    {
        var reqs = new List<writeRequest>();
        try{
            for(;;){
                var req = await shaper.ReceiveAsync(die.Token);
                reqs.Add(req);
                for(;;){
                    if(shaper.TryReceive(out req)){
                        reqs.Add(req);
                    } else {
                        break;
                    }
                }
                //将prio小的排在前面
                reqs.Sort();

                for( ;reqs.Count > 0;) {
                    req = reqs[0];
                    writes.Post(req);
                    reqs.RemoveAt(0);
                }
            }
        }
        catch(Exception)
        {
            return;
        }
    }

    public async Task<Stream> OpenStreamAsync()
    {
        if(netstreamClosed)
        {
            throw new SmuxException("ErrClosedPipe");
        }

        nextStreamIDLock.WaitOne();
        if(goAway > 0) 
        {
            nextStreamIDLock.ReleaseMutex();
            throw new SmuxException("ErrGoAway");
        }

        nextStreamID += 2;
        var sid = nextStreamID;
        if(sid == sid % 2) {
            goAway = 1;
            nextStreamIDLock.ReleaseMutex();
            throw new SmuxException("ErrGoAway");            
        }
        nextStreamIDLock.ReleaseMutex();

        var stream = new Stream(sid,Config.MaxFrameSize,this);

        await WriteFrame(new Frame((byte)Config.Version,Frame.cmdSYN,sid));

        streamLock.WaitOne();
        streams[sid] = stream;
        streamLock.ReleaseMutex();
        return stream;
    }

    public async Task<Stream> AcceptStreamAsync()
    {
        var stream = await acceptCh.ReceiveAsync(die.Token);
        return stream;
    }

    static public Session Server(NetworkStream s,Config config)
    {
        config.Verify();
        return new Session(config,s,false);
    }


    static public Session Client(NetworkStream s,Config config)
    {
        config.Verify();
        return new Session(config,s,true);
    }

}