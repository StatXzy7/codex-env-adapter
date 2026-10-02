using System.Buffers.Binary;
using System.Text;

namespace CodexEnvAdapter;

static class DnsMessages
{
    public const ushort TypeA = 1;
    public const ushort TypeAAAA = 28;
    public const ushort TypeHttps = 65;

    public readonly record struct Question(ushort Id, ushort Type, string Name, int End);

    public static bool TryParseQuestion(ReadOnlySpan<byte> message, out Question question)
    {
        question = default;
        if (message.Length < 12)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(2, 2));
        if ((flags & 0x8000) != 0)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(message.Slice(4, 2)) != 1)
        {
            return false;
        }

        if (!TryReadName(message, 12, out var name, out var offset))
        {
            return false;
        }

        if (message.Length < offset + 4)
        {
            return false;
        }

        var type = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
        var klass = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset + 2, 2));
        if (klass != 1)
        {
            return false;
        }

        question = new Question(BinaryPrimitives.ReadUInt16BigEndian(message[..2]), type, name, offset + 4);
        return true;
    }

    public static byte[] EmptyNoError(ReadOnlySpan<byte> query, int questionEnd)
    {
        var buf = query[..questionEnd].ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10, 2), 0);
        return buf;
    }

    public static bool IsLocalName(string name)
    {
        var value = name.TrimEnd('.');
        return value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".ip6.arpa", StringComparison.OrdinalIgnoreCase);
    }

    static bool TryReadName(ReadOnlySpan<byte> message, int offset, out string name, out int next)
    {
        name = "";
        next = offset;
        var labels = new List<string>();
        var hops = 0;
        while (offset < message.Length && hops++ < 32)
        {
            var length = message[offset];
            if (length == 0)
            {
                next = offset + 1;
                name = string.Join('.', labels);
                return true;
            }

            if ((length & 0xC0) != 0)
            {
                return false;
            }

            offset++;
            if (length > 63 || offset + length > message.Length)
            {
                return false;
            }

            labels.Add(Encoding.ASCII.GetString(message.Slice(offset, length)));
            offset += length;
        }

        return false;
    }
}
