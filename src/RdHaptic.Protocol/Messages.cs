using System.Buffers.Binary;

namespace RdHaptic.Protocol;

/// <summary>
/// Wire format for the RDHAPTIC dynamic virtual channel. All integers little-endian; one message per DVC write.
/// Header: u8 version | u8 type | u16 seq.
/// </summary>
public static class Wire
{
    public const string ChannelName = "RDHAPTIC";
    public const byte Version = 1;
    public const int HeaderSize = 4;

    public static byte[] Hello(ushort seq, ushort agentVersion)
    {
        var b = Header(MessageType.Hello, seq, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(HeaderSize), agentVersion);
        return b;
    }

    public static byte[] Caps(ushort seq, IReadOnlyList<WaveformInfo> waveforms, byte intensityLevels)
    {
        var b = Header(MessageType.Caps, seq, 1 + waveforms.Count * 4 + 1);
        var s = b.AsSpan(HeaderSize);
        s[0] = (byte)waveforms.Count;
        for (int i = 0; i < waveforms.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[(1 + i * 4)..], waveforms[i].Usage);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(3 + i * 4)..], waveforms[i].DurationMs);
        }
        s[1 + waveforms.Count * 4] = intensityLevels;
        return b;
    }

    public static byte[] Play(ushort seq, Waveform waveform, byte intensityPct)
    {
        var b = Header(MessageType.Play, seq, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(HeaderSize), (ushort)waveform);
        b[HeaderSize + 2] = Math.Min(intensityPct, (byte)100);
        return b;
    }

    public static byte[] Stop(ushort seq) => Header(MessageType.Stop, seq, 0);

    public static byte[] Ping(ushort seq, uint timestampMs) => WithU32(MessageType.Ping, seq, timestampMs);

    public static byte[] Pong(ushort seq, uint timestampMs) => WithU32(MessageType.Pong, seq, timestampMs);

    public static bool TryParse(ReadOnlySpan<byte> data, out Message message)
    {
        message = default;
        if (data.Length < HeaderSize || data[0] != Version)
            return false;
        var type = (MessageType)data[1];
        var seq = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var body = data[HeaderSize..];
        switch (type)
        {
            case MessageType.Hello when body.Length >= 2:
                message = new Message(type, seq) { AgentVersion = BinaryPrimitives.ReadUInt16LittleEndian(body) };
                return true;
            case MessageType.Caps when body.Length >= 2:
                int count = body[0];
                if (body.Length < 2 + count * 4)
                    return false;
                var list = new WaveformInfo[count];
                for (int i = 0; i < count; i++)
                    list[i] = new WaveformInfo(
                        BinaryPrimitives.ReadUInt16LittleEndian(body[(1 + i * 4)..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(body[(3 + i * 4)..]));
                message = new Message(type, seq) { Waveforms = list, IntensityLevels = body[1 + count * 4] };
                return true;
            case MessageType.Play when body.Length >= 3:
                message = new Message(type, seq)
                {
                    Waveform = (Waveform)BinaryPrimitives.ReadUInt16LittleEndian(body),
                    IntensityPct = Math.Min(body[2], (byte)100),
                };
                return true;
            case MessageType.Stop:
                message = new Message(type, seq);
                return true;
            case MessageType.Ping or MessageType.Pong when body.Length >= 4:
                message = new Message(type, seq) { TimestampMs = BinaryPrimitives.ReadUInt32LittleEndian(body) };
                return true;
            default:
                return false;
        }
    }

    private static byte[] Header(MessageType type, ushort seq, int bodyLength)
    {
        var b = new byte[HeaderSize + bodyLength];
        b[0] = Version;
        b[1] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), seq);
        return b;
    }

    private static byte[] WithU32(MessageType type, ushort seq, uint value)
    {
        var b = Header(type, seq, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(HeaderSize), value);
        return b;
    }
}

public enum MessageType : byte
{
    Hello = 0x01,
    Caps = 0x02,
    Play = 0x10,
    Stop = 0x11,
    Ping = 0x20,
    Pong = 0x21,
}

public readonly record struct WaveformInfo(ushort Usage, ushort DurationMs);

public readonly record struct Message(MessageType Type, ushort Seq)
{
    public ushort AgentVersion { get; init; }
    public IReadOnlyList<WaveformInfo>? Waveforms { get; init; }
    public byte IntensityLevels { get; init; }
    public Waveform Waveform { get; init; }
    public byte IntensityPct { get; init; }
    public uint TimestampMs { get; init; }
}
