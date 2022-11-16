using System.Net;
using System.Net.Sockets;
namespace Smux;


public class SmuxException : Exception
{
    private string msg;

    public SmuxException(string msg)
    {
        this.msg = msg;
    }

    override public string ToString()
    {
        return this.msg;
    }
}

public class Config
{
    public int Version = 2;
    public bool KeepAliveDisabled = false;
    public long KeepAliveInterval = 10*1000;//10 second
    public long KeepAliveTimeout = 30*1000;//30 second
    public int MaxFrameSize = 32768;
    public int MaxReceiveBuffer = 4194304;
    public int MaxStreamBuffer = 65536;

    public Config()
    {

    }

    public void Verify()
    {

        if(!(Version == 1 || Version == 2)) {
            throw new SmuxException("unsupported protocol version");
        }
        
        if(!KeepAliveDisabled) {
            if(KeepAliveInterval == 0){
                throw new SmuxException("keep-alive interval must be positive");
            }
            if(KeepAliveTimeout < KeepAliveInterval) {
                throw new SmuxException("keep-alive timeout must be larger than keep-alive interval");
            }             
        }
        if(MaxFrameSize <= 0) {
            throw new SmuxException("max frame size must be positive");
        }
        if(MaxFrameSize > 65535) {
            throw new SmuxException("max frame size must not be larger than 65535");
        }
        
        if(MaxReceiveBuffer <= 0) {
            throw new SmuxException("max receive buffer must be positive");
        }
        if(MaxStreamBuffer <= 0) {
            throw new SmuxException("max stream buffer must be positive");
        }
        if(MaxStreamBuffer > MaxReceiveBuffer) {
            throw new SmuxException("max stream buffer must not be larger than max receive buffer");
        }
        if(MaxStreamBuffer > 2147483647) {
            throw new SmuxException("max stream buffer cannot be larger than 2147483647");
        }               
    }
}