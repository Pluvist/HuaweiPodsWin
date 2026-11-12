using System.Buffers.Binary;

namespace HuaweiPods.Protocol;

public sealed class HuaweiFrame
{
    private readonly Dictionary<byte, byte[]> _fields;

    internal HuaweiFrame(byte service, byte command, Dictionary<byte, byte[]> fields)
    {
        Service = service;
        Command = command;
        _fields = fields;
    }

    public byte Service { get; }
    public byte Command { get; }

    public ReadOnlySpan<byte> GetField(byte type) => _fields.GetValueOrDefault(type);
}

/// <summary>Decodes the Huawei TLV frames carried by an RFCOMM byte stream.</summary>
public sealed class HuaweiFrameDecoder
{
    private byte[] _pending = [];

    public IReadOnlyList<HuaweiFrame> Push(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
            return [];

        var bytes = new byte[_pending.Length + chunk.Length];
        _pending.CopyTo(bytes, 0);
        chunk.CopyTo(bytes.AsSpan(_pending.Length));

        var frames = new List<HuaweiFrame>();
        var offset = 0;
        var incompleteOffset = -1;
        while (offset < bytes.Length)
        {
            var magicIndex = bytes.AsSpan(offset).IndexOf(HuaweiWireFormat.Magic);
            if (magicIndex < 0)
                break;

            var frameOffset = offset + magicIndex;
            offset = frameOffset + 1;
            var candidate = bytes.AsSpan(frameOffset);
            if (candidate.Length >= 2 && candidate[1] != 0)
                continue;

            if (candidate.Length < 4)
            {
                if (incompleteOffset < 0)
                    incompleteOffset = frameOffset;
                continue;
            }

            var frameLength = BinaryPrimitives.ReadUInt16LittleEndian(candidate[2..4]) + HuaweiWireFormat.LengthOverhead;
            if (frameLength < HuaweiWireFormat.MinimumFrameSize)
                continue;

            if (candidate.Length < frameLength)
            {
                // Keep the partial frame unless a later, complete frame proves a new boundary.
                if (incompleteOffset < 0)
                    incompleteOffset = frameOffset;
                continue;
            }

            var frameBytes = candidate[..frameLength];
            var expectedChecksum = BinaryPrimitives.ReadUInt16BigEndian(frameBytes[^2..]);
            if (HuaweiWireFormat.ComputeChecksum(frameBytes[..^2]) != expectedChecksum)
                continue;

            offset = frameOffset + frameLength;
            if (ReadFrame(frameBytes) is { } frame)
            {
                incompleteOffset = -1;
                frames.Add(frame);
            }
        }

        _pending = incompleteOffset >= 0 ? bytes[incompleteOffset..] : [];
        return frames;
    }

    private static HuaweiFrame? ReadFrame(ReadOnlySpan<byte> frame)
    {
        var fields = new Dictionary<byte, byte[]>();
        var payload = frame[6..^2];
        while (!payload.IsEmpty)
        {
            if (payload.Length < 2 || payload[1] > payload.Length - 2)
                return null;

            var end = 2 + payload[1];
            if (!fields.TryAdd(payload[0], payload[2..end].ToArray()))
                return null;

            payload = payload[end..];
        }

        return new HuaweiFrame(frame[4], frame[5], fields);
    }
}

internal static class HuaweiWireFormat
{
    public const byte Magic = 0x5A;
    public const int LengthOverhead = 5;
    public const int MinimumFrameSize = 8;

    public static byte[] Encode(byte service, byte command, params byte[] fields)
    {
        var frame = new byte[MinimumFrameSize + fields.Length];
        frame[0] = Magic;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), checked((ushort)(frame.Length - LengthOverhead)));
        frame[4] = service;
        frame[5] = command;
        fields.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(^2), ComputeChecksum(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }

    public static ushort ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        // CRC-16/XMODEM, polynomial 0x1021, initial value 0.
        ushort checksum = 0;
        foreach (var value in bytes)
        {
            checksum ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                checksum = (ushort)((checksum & 0x8000) != 0
                    ? (checksum << 1) ^ 0x1021
                    : checksum << 1);
            }
        }

        return checksum;
    }
}
