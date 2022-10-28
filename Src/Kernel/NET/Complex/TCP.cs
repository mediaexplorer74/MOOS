using MOOS.Driver;
using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

namespace MOOS.NET
{
    public unsafe class TcpClient
    {
        public TCPStatus State;

        public bool Connected => State == TCPStatus.Established;

        public IPAddress LocalAddr;
        public IPAddress NextAddr;
        public IPAddress RemoteAddr;

        public ushort LocalPort;
        public ushort RemotePort;

        public uint SndNxt;
        public uint RcvNxt;      

        public bool Send(byte[] buffer)
        {
            if (Connected)
            {
                fixed (byte* p = buffer)
                    TCP.Send(this, p, buffer.Length);
                return true;
            }
            return false;
        }

        public TcpClient()
        {
            OnData = null;
        }
        
        public static TcpClient Connect(IPAddress address,ushort port) 
        {
            return TCP.Connect(address, port, port);
        }

        public event Network.OnDataHandler OnData;

        internal unsafe void _OnData(byte* buffer, int length)
        {
            byte[] Data = new byte[length];
            fixed (byte* dest = Data)
            {
                Native.Movsb(dest, buffer, (ulong)length);
            }
            OnData?.Invoke(Data);
            Data.Dispose();
        }

        public void Close()
        {
            if (Connected)
            {
                TCP.Close(this);
            }
        }
    }

    public enum TCPStatus
    {
        Closed,
        Listen,
        SynSent,
        SynReceived,
        Established,
        FinWait1,
        FinWait2,
        CloseWait,
        Closing,
        LastAcknowledge,
        TimeWait,
    }

    public enum TCPOption
    {
        End,
        Nop,
        MSS
    }

    [Flags]
    public enum TCPFlags : byte
    {
        TCP_FIN = (1 << 0),
        TCP_SYN = (1 << 1),
        TCP_RST = (1 << 2),
        TCP_PSH = (1 << 3),
        TCP_ACK = (1 << 4),
        TCP_URG = (1 << 5),
    }

    internal unsafe class TCP
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TCPHeader
        {
            public ushort SourcePort;
            public ushort DestPort;
            public uint Seq;
            public uint Ack;
            public byte Off;
            public TCPFlags Flags;
            public ushort WindowSize;
            public ushort Checksum;
            public ushort Urgent;
        }

        private const ushort TCP_WINDOW_SIZE = 8192;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ChecksumHeader
        {
            public uint Source;
            public uint Bits1;
            public uint Dest;
            public uint Bits2;
            public byte Reserved;
            public byte Protocol;
            public ushort Length;
        }

        public static List<TcpClient> Clients;

        internal static void HandlePacket(byte* buffer, int length)
        {
            TCPHeader* hdr = (TCPHeader*)buffer;
            buffer += hdr->Off >> 2;
            length -= hdr->Off >> 2;

            Ethernet.SwapLeftRight(ref hdr->SourcePort);
            Ethernet.SwapLeftRight(ref hdr->DestPort);
            Ethernet.SwapLeftRight32(ref hdr->Seq);
            Ethernet.SwapLeftRight32(ref hdr->Ack);
            Ethernet.SwapLeftRight(ref hdr->WindowSize);
            Ethernet.SwapLeftRight(ref hdr->Checksum);
            Ethernet.SwapLeftRight(ref hdr->Urgent);

            TcpClient conn = null;
            for(int i = 0; i < Clients.Count; i++) 
            {
                if (Clients[i].LocalPort == hdr->DestPort) 
                {
                    conn = Clients[i];
                    break;
                }
            }

            if (conn == null)
            {
                return;
            }

            TCPFlags flags = hdr->Flags;

            if (conn.State == TCPStatus.SynSent)
            {
                if (flags == (TCPFlags.TCP_SYN | TCPFlags.TCP_ACK))
                {
                    conn.SndNxt = hdr->Ack;
                    conn.RcvNxt = hdr->Seq + 1;

                    SendPacket(conn, TCPFlags.TCP_ACK);

                    conn.State = TCPStatus.Established;
                    Console.WriteLine("Connection established");
                }
                else
                {
                    conn.State = TCPStatus.Closed;

                    Console.WriteLine("Connection failed");
                }
            }
            else if (conn.State == TCPStatus.Established)
            {
                if (flags.HasFlag(TCPFlags.TCP_ACK))
                {
                    conn.SndNxt = hdr->Ack;
                }
                if (flags.HasFlag(TCPFlags.TCP_PSH))
                {
                    conn.RcvNxt = (uint)(hdr->Seq + length);
                    conn._OnData(buffer, length);
                    SendPacket(conn, TCPFlags.TCP_ACK);
                }
                if (flags.HasFlag(TCPFlags.TCP_FIN))
                {
                    conn.RcvNxt++;
                    SendPacket(conn, TCPFlags.TCP_ACK);
                    conn.State = TCPStatus.Closed;
                }
                if (flags.HasFlag(TCPFlags.TCP_RST))
                {
                    conn.State = TCPStatus.Closed;
                    Console.WriteLine($"Error: TCP error flags: 0b{Convert.ToString((ulong)flags, 2)}");
                }
            }
            else if (conn.State == TCPStatus.FinWait1)
            {
                if (flags == TCPFlags.TCP_ACK)
                {
                    conn.State = TCPStatus.FinWait2;
                }
                else
                {
                    conn.RcvNxt++;
                    SendPacket(conn, TCPFlags.TCP_ACK);
                    conn.State = TCPStatus.Closed;
                }
            }
            else if (conn.State == TCPStatus.FinWait2)
            {
                if (flags.HasFlag(TCPFlags.TCP_FIN)) 
                {
                    conn.RcvNxt++;
                    SendPacket(conn, TCPFlags.TCP_ACK);
                    conn.State = TCPStatus.Closed;
                }
            }
        }

        public static TcpClient Connect(IPAddress addr, ushort port, ushort localPort)
        {
            TcpClient conn = new();

            conn.LocalAddr = Network.IP;

            conn.NextAddr = addr;

            conn.RemoteAddr = addr;

            conn.LocalPort = localPort;
            conn.RemotePort = port;

            var rnd = new Random();
            uint seq = (uint)rnd.Next(0, int.MaxValue);

            conn.SndNxt = seq;
            conn.RcvNxt = 0;

            Clients.Add(conn);

            SendPacket(conn, TCPFlags.TCP_SYN);
            conn.State = TCPStatus.SynSent;

            ulong t = Timer.Ticks + 3000;
            while ((Timer.Ticks < t) && !conn.Connected)
            {
                Native.Hlt();
            }
            if (conn.State == TCPStatus.SynSent)
            {
                conn.State = TCPStatus.Closed;
                Console.WriteLine("Connection timeout");
            }

            return conn;
        }

        private static void SendPacket(TcpClient conn, TCPFlags flags)
        {
            SendPacket(conn, conn.SndNxt, flags, null, 0);
        }

        private static void SendPacket(TcpClient conn, uint seq, TCPFlags flags, void* data, uint count)
        {
            byte* buffer = (byte*)Allocator.Allocate(TCP_WINDOW_SIZE);

            TCPHeader* hdr = (TCPHeader*)buffer;

            hdr->SourcePort = Ethernet.SwapLeftRight(conn.LocalPort);
            hdr->DestPort = Ethernet.SwapLeftRight(conn.RemotePort);
            hdr->Seq = Ethernet.SwapLeftRight32(seq);
            hdr->Ack = Ethernet.SwapLeftRight32(((flags & (byte)TCPFlags.TCP_ACK) != 0) ? conn.RcvNxt : 0);
            hdr->WindowSize = Ethernet.SwapLeftRight(TCP_WINDOW_SIZE);
            hdr->Checksum = Ethernet.SwapLeftRight(0);
            hdr->Urgent = Ethernet.SwapLeftRight(0);

            hdr->Off = 0;
            hdr->Flags = flags;

            byte* p = buffer + sizeof(TCPHeader);

            if (flags.HasFlag(TCPFlags.TCP_SYN) != 0)
            {
                p[0] = (byte)TCPOption.MSS;
                p[1] = 4;
                *(ushort*)(p + 2) = Ethernet.SwapLeftRight(1460);
                p += p[1];
            }

            while (((p - buffer) & 3) != 0)
            {
                *p++ = 0;
            }

            hdr->Off = (byte)((p - buffer) << 2);

            if (data != null)
            {
                Native.Movsb(p, data, count);
            }

            byte* end = p + count;
            int length = (int)(end - buffer);
            ushort checksum = CalculateChecksum(conn.LocalAddr.AddressV4, conn.RemoteAddr.AddressV4, buffer, length);
            hdr->Checksum = Ethernet.SwapLeftRight(checksum);

            IPv4.SendPacket(conn.RemoteAddr, (byte)IPv4.IPv4Protocol.TCP, buffer, (int)end - (int)buffer);

            Allocator.Free((nint)buffer);
        }

        private static ushort CalculateChecksum(uint srcIP,uint destIP, byte* buffer, int length)
        {
            ChecksumHeader* phdr = (ChecksumHeader*)(buffer - sizeof(ChecksumHeader));
            phdr->Source = srcIP;
            phdr->Dest = destIP;

            phdr->Reserved = 0;
            phdr->Protocol = (byte)IPv4.IPv4Protocol.TCP;
            phdr->Length = Ethernet.SwapLeftRight((ushort)length);

            byte* data = buffer - sizeof(ChecksumHeader);
            
            uint len = length + sizeof(ChecksumHeader);
            ushort* p = (ushort*)data;

            uint sum = 0;

            while (len > 1)
            {
                sum += *p++;
                len -= 2;
            }

            if (len != 0)
            {
                sum += *(byte*)p;
            }

            sum = (sum & 0xffff) + (sum >> 16);
            sum += (sum >> 16);

            ushort temp = (ushort)~sum;
            return (ushort)(((temp & 0x00ff) << 8) | ((temp & 0xff00) >> 8)); 
        }

        const int MSS = 536;

        public static void Send(TcpClient conn, byte* data, int length)
        {
            if (conn.Connected)
            {
                for (int i = 0; i < length; i += MSS)
                {
                    int count = i + MSS > length ? length % MSS : MSS;

                    SendPacket(conn, conn.SndNxt, TCPFlags.TCP_ACK | TCPFlags.TCP_PSH, data, (uint)count);
                    conn.SndNxt += (uint)count;
                }
            }
        }

        public static void Close(TcpClient conn)
        {
            SendPacket(conn, TCPFlags.TCP_FIN | TCPFlags.TCP_ACK);
            conn.SndNxt++;
            conn.State = TCPStatus.FinWait1;
        }
    }
}