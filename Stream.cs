using System;
//using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Text;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
namespace Smux;


public class Stream 
{
    private StreamInternal s;

    public uint Id{get=>s.Id;}
    public int? ReadTimeout{get=>s.ReadTimeout;set=>s.ReadTimeout=value;}
    public int? WriteTimeout{get=>s.ReadTimeout;set=>s.WriteTimeout=value;} 

    internal Stream(StreamInternal s)
    {
        this.s = s;
    }

    public Task<int> ReadAsync(byte[] b)
    {
        return s.ReadAsync(b);
    }

    public Task<int> WriteAsync(byte[] b)
    {
        return s.WriteAsync(b);
    }

    public Task Close()
    {
        return s.Close();
    }
}

internal class StreamInternal
{
    private class buffer {
        public int Offset;
        public byte[] Bytes;

        public buffer(byte[] bytes,int offset)
        {
            Bytes = bytes;
            Offset = offset;
        }
    }

    public uint Id{get;}
    private SessionInternal sess;
    private Mutex bufferLock = new Mutex();

    private List<buffer> buffers = new List<buffer>();

    private int frameSize;
    //notify a read event
    private BufferBlock<byte> chReadEvent = new BufferBlock<byte>();
    private CancellationTokenSource die = new CancellationTokenSource();

    private int closeOnce = 0;

    private CancellationTokenSource fin;

    private int? readtimeout;
    public  int? ReadTimeout
    {
        get
        {
            return readtimeout;
        }
        set 
        {
            if(value > 0){
                readtimeout = value;
            }
        }
    }

    private int? writetimeout;
    public  int? WriteTimeout
    {
        get
        {
            return writetimeout;
        }
        set 
        {
            if(value > 0){
                writetimeout = value;
            }
        }
    }

    private uint numRead = 0;
    private uint numWritten = 0;
    private uint incr = 0;
    //UPD command
    private uint peerConsumed = 0;
    private uint peerWindow = Frame.initialPeerWindow;

    private BufferBlock<byte> chUpdate = new BufferBlock<byte>();

    public StreamInternal(uint id,int frameSize,SessionInternal sess)
    {
        fin = CancellationTokenSource.CreateLinkedTokenSource(die.Token);
        Id = id;
        this.frameSize = frameSize;
        this.sess = sess;
    }

    private async Task waitRead()
    {
        var source = fin;
        if(ReadTimeout > 0) {
            source = CancellationTokenSource.CreateLinkedTokenSource(fin.Token);
            source.CancelAfter((int)ReadTimeout);   
        }
        await chReadEvent.ReceiveAsync(source.Token);
    }

    public async Task<int> ReadAsync(byte[] b)
    {
        for(;;)
        {
            try{
                var n = await tryRead(b);
                if(n > 0) {
                    return n;
                } else {
                    await waitRead();
                }
            }
            catch(OperationCanceledException)
            {
                if(die.IsCancellationRequested)
                {
                    throw new SmuxException("ErrClosedPipe");
                }

                if(fin.IsCancellationRequested)
                {
                    bufferLock.WaitOne();
                    if(buffers.Count > 0)
                    {
                        bufferLock.ReleaseMutex();
                    } else {
                        bufferLock.ReleaseMutex();
                        throw new SmuxException("ErrEof");
                    }
                }

                throw new SmuxException("ErrReadTimeout");
            }
        }
    }

    private async Task<int> tryRead(byte[] b)
    {
        if(sess.Config.Version == 2)
        {
            return await tryReadv2(b);
        }

        int  n = 0;

        bufferLock.WaitOne();
        if(buffers.Count > 0)
        {
            var buff = buffers[0];
            n = b.Length;
            if(buff.Bytes.Length - buff.Offset < n) 
            {
                n = buff.Bytes.Length - buff.Offset;
            }
            Array.Copy(buff.Bytes,buff.Offset,b,0,n);
            buff.Offset += n;
            if(buff.Offset >= buff.Bytes.Length){
                buffers.RemoveAt(0);
            }
        }
        bufferLock.ReleaseMutex();

        if(n > 0) {
            sess.returnTokens(n);
        }

        return n;
    }

    private async Task<int> tryReadv2(byte[] b)
    {
        if(b.Length == 0) {
            return 0;
        }

        uint notifyConsumed = 0;
        int  n = 0;

        bufferLock.WaitOne();
        if(buffers.Count > 0)
        {
            var buff = buffers[0];
            n = b.Length;
            if(buff.Bytes.Length - buff.Offset < n) 
            {
                n = buff.Bytes.Length - buff.Offset;
            }
            Array.Copy(buff.Bytes,buff.Offset,b,0,n);
            buff.Offset += n;
            if(buff.Offset >= buff.Bytes.Length){
                buffers.RemoveAt(0);

            }
        }
        numRead += (uint)n;
        incr += (uint)n;
        if(incr >= (uint)(sess.Config.MaxStreamBuffer/2) || numRead == (uint)n) {
            notifyConsumed = numRead;
            incr = 0;
        }
        bufferLock.ReleaseMutex();

        if(n > 0) {
            sess.returnTokens(n);
            if(notifyConsumed > 0) {
                await sendWindowUpdate(notifyConsumed);
            }
        }
        return n;
    }

    private async Task sendWindowUpdate(uint consumed)
    {
        var hdr = new UpdHeader(consumed,(uint)sess.Config.MaxStreamBuffer);
        var frame = new Frame((byte)sess.Config.Version,Frame.cmdUPD,Id,hdr.H,0,hdr.H.Length);
        await sess.WriteFrameInternal(frame,0,ReadTimeout);
    }

    public async Task<int> WriteAsync(byte[] b) 
    {

        if(die.IsCancellationRequested)
        {
            throw new SmuxException("ErrClosedPipe");
        }
        try{
            if(sess.Config.Version == 2)
            {
                return await writeV2(b);
            }

            var sent = 0;
            for(;sent < b.Length;)
            {
                var sz = b.Length-sent;
                if(sz > frameSize) {
                    sz = frameSize;
                }
                var frame = new Frame((byte)sess.Config.Version,Frame.cmdPSH,Id,b,sent,sz);          
                var n = await sess.WriteFrameInternal(frame,numWritten,WriteTimeout);
                sent += n;
            }
            return sent;
        }
        catch(OperationCanceledException)
        {
            if(die.IsCancellationRequested)
            {
                throw new SmuxException("ErrClosedPipe");
            }
            if(fin.IsCancellationRequested)
            {
                throw new SmuxException("ErrEof");
            }
            throw new SmuxException("ErrWriteTimeout");
        }
    }
 
    private async Task<int> writeV2(byte[] b) 
    {
        if(b.Length == 0) 
        {
            return 0;
        }

        var sent = 0;
        for(;sent < b.Length;)
        {
            var inflight = numWritten - peerConsumed;
            if(inflight < 0) 
            {
              throw new SmuxException("Smux ErrConsumed"); 
            }
            var win = (int)(peerWindow - inflight);
            if(win > 0)
            {
                int avalabile;
                if(win > b.Length - sent)
                {
                    avalabile = b.Length - sent;
                } 
                else 
                {
                    avalabile = win;
                }

                for(;avalabile > 0;) {
                    var sz = avalabile;
                    if(sz > frameSize) {
                        sz = frameSize;
                    }
                    var frame = new Frame((byte)sess.Config.Version,Frame.cmdPSH,Id,b,sent,sz);
                    var n = await sess.WriteFrameInternal(frame,numWritten,WriteTimeout);
                    sent += n;
                    avalabile -= n;
                }
            }
            //尚未发送完成，等待窗口可用
            if(sent < b.Length) 
            {
                var source = fin;
                if(WriteTimeout > 0) {
                    source = CancellationTokenSource.CreateLinkedTokenSource(fin.Token);
                    source.CancelAfter((int)WriteTimeout);   
                }
                await chUpdate.ReceiveAsync(fin.Token);
            }
        }
        return sent; 
    }

    public void pushBytes(byte[] buf)
    {
        bufferLock.WaitOne();
        buffers.Add(new buffer(buf,0));
        bufferLock.ReleaseMutex();
    }

    public void Update(uint consumed,uint window)
    {
        peerConsumed = consumed;
        peerWindow = window;
        chUpdate.Post((byte)0);
    }

    public void Fin()
    {
        fin.Cancel();
    }

    public void NotifyReadEvent()
    {
        chReadEvent.Post((byte)0);
    }

    public void SessionClose()
    {
        die.Cancel();
    }

    public int RecycleTokens()
    {
        var n = 0;
        bufferLock.WaitOne();
        for(;buffers.Count>0;){
            var buff = buffers[0];
            n += (int)(buff.Bytes.Length - buff.Offset);
            buffers.RemoveAt(0);
        }
        bufferLock.ReleaseMutex();
        return n;
    }

    public async Task Close()
    {
        if(Interlocked.CompareExchange(ref closeOnce,1,0) == 0)
        {
            if(!die.IsCancellationRequested)
            {
                die.Cancel();
                try
                {
                    await sess.WriteFrame(new Frame((byte)sess.Config.Version,Frame.cmdFIN,Id));
                }
                catch(Exception e)
                {
                    Console.WriteLine(e);
                }
                finally
                {
                    sess.StreamClose(Id);
                }
            }
        }
    }
}