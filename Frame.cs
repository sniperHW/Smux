using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Text;
using System.Threading;

namespace Smux;
public class Frame
{   
    // protocol version 1
    public const byte cmdSYN = 0; //stream open
    public const byte cmdFIN = 1; //stream close
    public const byte cmdPSH = 2; //data push
    public const byte cmdNOP = 3; //no operation
    //protocol version 2 extra commands
    //notify bytes consumed by remote peer-end
    public const byte cmdUPD = 4;
    // data size of cmdUPD,format:
    //|4B data consumed(ACK),4B window size(WINDOW)|
    public const int szCmdUPD = 8;
    //initial peer window guess,a slow-start
    public const int initialPeerWindow = 262144;
    public const int sizeOfVer = 1;
    public const int sizeOfCmd = 1;
    public const int sizeOfLength = 2;
    public const int sizeOfSid = 4;
    public const int headerSize = sizeOfVer + sizeOfCmd + sizeOfSid + sizeOfLength;

    public byte Ver{get;}
    public byte Cmd{get;}
    public uint Sid{get;}
    public byte[]? Data{get;}
    public int Length{get;}

    public int Offset{get;}

    public Frame(byte version,byte cmd,uint sid){
        Ver = version;
        Cmd = cmd;
        Sid = sid;
    }


    public Frame(byte version,byte cmd,uint sid,byte []data,int offset,int length){
        Ver = version;
        Cmd = cmd;
        Sid = sid;
        Data = data;
        Offset = offset;
        Length = length;
    }


}

public class RawHeader
{
    public byte[] H{get;}

    public byte Version()
    {
        return H[0];
    }

    public byte Cmd()
    {
        return H[1];
    }

    public ushort Length()
    {
        return (ushort)Endian.Little(BitConverter.ToInt16(H, 2));
    }

    public uint StreamID()
    {
        return (uint)Endian.Little(BitConverter.ToInt32(H, 4));        
    }

    public RawHeader()
    {   
        H = new byte[Frame.headerSize];
    } 
}

public class UpdHeader
{
    public byte[] H{get;}

    public uint Consumed
    {
        get
        {
            return (uint)Endian.Little(BitConverter.ToInt32(H, 0));
        }
    }

    public uint Window
    {
        get
        {
            return (uint)Endian.Little(BitConverter.ToInt32(H, 4));
        }
    }

    public UpdHeader(uint consumed,uint window)
    {
        H = new byte[Frame.headerSize];
        var bconsumed = BitConverter.GetBytes(consumed);
        Array.Copy(bconsumed,H,bconsumed.Length);
        var bwindow = BitConverter.GetBytes(window);
        Array.Copy(bwindow,0,H,4,bwindow.Length);

    }

    public UpdHeader()
    {
        H = new byte[Frame.headerSize];
    }     

}
