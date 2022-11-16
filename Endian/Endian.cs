using System.Net;
namespace Smux;

public class Endian
{
    static private byte[]v = BitConverter.GetBytes((ushort)1);

    static public short Little(short n)
    {
        if(v[0] == (byte)1) return n;
        else return IPAddress.NetworkToHostOrder(n);
    }

    static public int Little(int n)
    {
        if(v[0] == (byte)1) return n;
        else return IPAddress.NetworkToHostOrder(n);        
    }

    static public long Little(long n)
    {
        if(v[0] == (byte)1) return n;
        else return IPAddress.NetworkToHostOrder(n);        
    }    

    static public short Big(short n)
    {
        if(v[1] == (byte)1) return n;
        else return IPAddress.HostToNetworkOrder(n);
    }

    static public int Big(int n)
    {
        if(v[1] == (byte)1) return n;
        else return IPAddress.HostToNetworkOrder(n);
    }

    static public long Big(long n)
    {
        if(v[1] == (byte)1) return n;
        else return IPAddress.HostToNetworkOrder(n);
    }    

}
