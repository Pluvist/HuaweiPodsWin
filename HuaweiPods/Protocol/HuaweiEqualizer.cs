using System.Text;

namespace HuaweiPods.Protocol;

public sealed record EqualizerPreset(byte Id, string? Name, IReadOnlyList<int> Gains);
public sealed record EqualizerState(bool? Supported, byte SelectedId, IReadOnlyList<byte> BuiltInIds,
    int? BandCount, string? SelectedName, IReadOnlyList<int>? SelectedGains, IReadOnlyList<EqualizerPreset> CustomPresets);

// HuaweiEqualizerCodec / FreeBuds7Controller from the upstream verified captures.
public static class HuaweiEqualizer
{
    public const int BandCount = 10;
    public const int CustomNameReadbackByteLimit = 24;
    private const byte FirstCustomSlot = 0x64;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool SupportsRead(HeadsetProfile profile) => profile.Model is
        HeadsetModel.FreeBuds4 or HeadsetModel.FreeBuds4E or HeadsetModel.FreeBuds5 or
        HeadsetModel.FreeBudsSe4Anc or HeadsetModel.FreeBuds6i or HeadsetModel.FreeBuds6 or
        HeadsetModel.FreeBuds7 or HeadsetModel.FreeBuds7i or HeadsetModel.FreeBudsPro5 or
        HeadsetModel.FreeClip2 or HeadsetModel.FreeClip2Collector or HeadsetModel.FreeArc;

    public static bool SupportsWrite(HeadsetProfile profile) => Operation(profile) is not null;
    public static bool CanEditCustom(HeadsetProfile profile, EqualizerState? state) =>
        SupportsWrite(profile) && state is { Supported: true, BandCount: null or BandCount };

    public static int UnitsPerDecibel(HeadsetProfile profile) => profile.Model == HeadsetModel.FreeBuds7 ? 5 : 10;

    public static byte[] Query(HeadsetProfile profile)
    {
        if (!SupportsRead(profile))
            throw new NotSupportedException("该型号不支持读取自定义均衡器。");
        return HuaweiWireFormat.Encode(0x2B, 0x4A, 0x02, 0x00);
    }

    public static byte[] SetCustom(HeadsetProfile profile, IReadOnlyList<int> gains, string name)
    {
        byte operation = Operation(profile) ?? throw new NotSupportedException("该型号没有已验证的直接均衡器写入协议。");
        int limit = UnitsPerDecibel(profile) * 6;
        if (gains.Count != BandCount || gains.Any(value => value < -limit || value > limit))
            throw new ArgumentOutOfRangeException(nameof(gains), "均衡器须为 10 段，各段范围为 −6 至 +6 dB。");
        var encodedName = Utf8.GetBytes(name.Trim());
        if (encodedName.Length is 0 or > 32 || encodedName.Contains((byte)0))
            throw new ArgumentException("音效名称不能为空，且不能超过 32 个 UTF-8 字节。", nameof(name));

        var fields = new List<byte> { 0x01, 0x01, FirstCustomSlot, 0x02, 0x01, BandCount, 0x05, 0x01, operation, 0x03, BandCount };
        fields.AddRange(gains.Select(value => unchecked((byte)(sbyte)value)));
        fields.Add(0x04);
        fields.Add((byte)encodedName.Length);
        fields.AddRange(encodedName);
        return HuaweiWireFormat.Encode(0x2B, 0x49, fields.ToArray());
    }

    public static bool ConfirmsCustom(HuaweiFrame frame, HeadsetProfile profile, IReadOnlyList<int> gains, string name)
    {
        var state = Parse(frame, profile);
        return SupportsWrite(profile)
            && state is { Supported: true, BandCount: BandCount, SelectedId: FirstCustomSlot,
                SelectedName: { } confirmedName, SelectedGains: { } confirmedGains }
            && string.Equals(confirmedName, name.Trim(), StringComparison.Ordinal)
            && confirmedGains.SequenceEqual(gains);
    }

    public static EqualizerState? Parse(HuaweiFrame frame, HeadsetProfile profile)
    {
        if (!SupportsRead(profile) || frame.Service != 0x2B || frame.Command != 0x4A)
            return null;
        var selected = frame.GetField(0x02);
        if (selected.Length != 1)
            return null;
        byte selectedId = selected[0];
        var supported = frame.GetField(0x01);
        bool? isSupported = supported.Length == 1 && supported[0] <= 1 ? supported[0] == 1 : null;
        var count = frame.GetField(0x05);
        var curve = frame.GetField(0x06);
        var presets = new List<EqualizerPreset>();
        var custom = frame.GetField(0x08);
        if (custom.Length % 36 != 0 || custom.Length > 36 * 3)
            return null;
        for (int offset = 0; offset < custom.Length; offset += 36)
        {
            var record = custom.Slice(offset, 36);
            byte id = record[0];
            if (id is < FirstCustomSlot or > 0x66 || record[1] != BandCount || presets.Any(preset => preset.Id == id))
                return null;
            var gains = ReadGains(record.Slice(2, BandCount));
            if (gains is null)
                return null;
            presets.Add(new EqualizerPreset(id, ReadName(record.Slice(12, CustomNameReadbackByteLimit)), gains));
        }

        var current = presets.FirstOrDefault(preset => preset.Id == selectedId);
        var reportedCurve = curve.Length is >= 1 and <= BandCount ? ReadGains(curve) : null;
        int? bands = count.Length == 1 && count[0] > 0 ? count[0]
            : reportedCurve?.Length ?? current?.Gains.Count;
        var selectedGains = current?.Gains ?? (reportedCurve?.Length == bands ? reportedCurve : null);
        return new EqualizerState(isSupported, selectedId, frame.GetField(0x03).ToArray(), bands,
            current?.Name ?? ReadName(frame.GetField(0x07)), selectedGains, presets);
    }

    private static byte? Operation(HeadsetProfile profile) => profile.Model switch
    {
        HeadsetModel.FreeBuds7i => 0,
        HeadsetModel.FreeBudsSe4Anc or HeadsetModel.FreeBuds6i or HeadsetModel.FreeBuds7 or
            HeadsetModel.FreeBudsPro5 or HeadsetModel.FreeArc => 1,
        _ => null
    };

    private static int[]? ReadGains(ReadOnlySpan<byte> bytes)
    {
        var gains = bytes.ToArray().Select(value => (int)unchecked((sbyte)value)).ToArray();
        return gains.All(value => value is >= -60 and <= 60) ? gains : null;
    }

    private static string? ReadName(ReadOnlySpan<byte> bytes)
    {
        int terminator = bytes.IndexOf((byte)0);
        try
        {
            var name = Utf8.GetString(terminator >= 0 ? bytes[..terminator] : bytes).Trim();
            return name.Length == 0 ? null : name;
        }
        catch (DecoderFallbackException)
        {
            // Keep the curve if a fixed-width record truncated the final UTF-8 character.
            return null;
        }
    }
}
