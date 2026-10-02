using System.Buffers.Binary;
using System.Net;

namespace CodexEnvAdapter;

static class DirectPacket
{
    public const int Tcp = 6;
    public const int Udp = 17;

    public static bool IsPublic(ReadOnlySpan<byte> ip)
    {
        if (ip.Length != 4)
        {
            return false;
        }

        var a = ip[0];
        var b = ip[1];
        if (a is 0 or 10 or 127)
        {
            return false;
        }

        if (a == 100 && b is >= 64 and <= 127)
        {
            return false;
        }

        if (a == 169 && b == 254)
        {
            return false;
        }

        if (a == 172 && b is >= 16 and <= 31)
        {
            return false;
        }

        if (a == 192 && b == 168)
        {
            return false;
        }

        return a < 224;
    }

    public static bool TryParseIpv4(ReadOnlySpan<byte> packet, int protocol, out int header, out int transport)
    {
        header = 0;
        transport = 0;
        if (packet.Length < 28 || packet[0] >> 4 != 4 || packet[9] != protocol)
        {
            return false;
        }

        var ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || packet.Length < ihl + 8)
        {
            return false;
        }

        var frag = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
        if ((frag & 0x1FFF) != 0)
        {
            return false;
        }

        transport = ihl;
        return true;
    }

    public static void ReflectToProxy(Span<byte> packet, int tcp, ushort proxyPort)
    {
        SwapAddresses(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(tcp + 2, 2), proxyPort);
    }

    public static void ReflectFromProxy(Span<byte> packet, int tcp, ushort serverPort)
    {
        SwapAddresses(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(tcp, 2), serverPort);
    }

    public static byte[] BuildUdpReply(ReadOnlySpan<byte> request, int udp, ReadOnlySpan<byte> payload)
    {
        var total = 20 + 8 + payload.Length;
        if (total > ushort.MaxValue)
        {
            throw new InvalidOperationException("DNS 应答过长。");
        }

        var buf = new byte[total];
        buf[0] = 0x45;
        buf[8] = 64;
        buf[9] = Udp;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)total);
        request.Slice(16, 4).CopyTo(buf.AsSpan(12, 4));
        request.Slice(12, 4).CopyTo(buf.AsSpan(16, 4));
        request.Slice(udp + 2, 2).CopyTo(buf.AsSpan(20, 2));
        request.Slice(udp, 2).CopyTo(buf.AsSpan(22, 2));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(24, 2), (ushort)(8 + payload.Length));
        payload.CopyTo(buf.AsSpan(28));
        return buf;
    }

    public static IPAddress ToAddress(ReadOnlySpan<byte> ip) => new(ip.ToArray());

    static void SwapAddresses(Span<byte> packet)
    {
        Span<byte> tmp = stackalloc byte[4];
        packet.Slice(12, 4).CopyTo(tmp);
        packet.Slice(16, 4).CopyTo(packet.Slice(12, 4));
        tmp.CopyTo(packet.Slice(16, 4));
    }
}
