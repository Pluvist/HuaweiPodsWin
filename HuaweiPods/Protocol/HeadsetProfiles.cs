namespace HuaweiPods.Protocol;

public enum HeadsetModel
{
    FreeBuds3,
    FreeBuds4,
    FreeBuds4E,
    FreeBuds5,
    FreeBuds5i,
    FreeBudsSe4Anc,
    FreeBuds6,
    FreeBuds6i,
    FreeBuds7,
    FreeBuds7i,
    FreeBudsPro3,
    FreeBudsPro4,
    FreeBudsPro5,
    FreeClip,
    FreeClip2,
    FreeClip2Collector,
    FreeArc,
    Eyewear,
    Eyewear2,
    Eyewear3
}

public sealed record ModeOption(byte Value, string Label);

public sealed record HeadsetProfile(HeadsetModel Model, string DisplayName, string? ModelId = null)
{
    public bool HasChargingCase { get; init; } = true;
    public bool SupportsBattery { get; init; } = true;
    public bool SupportsAnc { get; init; }
    public bool SupportsTransparency => TransparencyLevels.Count > 0;
    public bool SupportsAncReadback => AncLevels.Count > 0;
    public bool SupportsDirection => Model == HeadsetModel.FreeBuds3;
    public bool SupportsWearDetection { get; init; }
    public bool SupportsWearDetectionReadback => SupportsWearDetection;
    public bool SupportsLowLatency { get; init; }
    public bool SupportsLowLatencyReadback { get; init; }
    public IReadOnlyList<ModeOption> AncLevels { get; init; } = [];
    public IReadOnlyList<ModeOption> TransparencyLevels { get; init; } = [];
    public IReadOnlyList<ModeOption> SoundPresets => HeadsetProfiles.SoundPresetsFor(Model);
    public byte? DefaultAncLevel => Model == HeadsetModel.FreeBudsSe4Anc ? (byte)1 : AncLevels.FirstOrDefault()?.Value;
    public byte? DefaultTransparencyLevel => TransparencyLevels.FirstOrDefault()?.Value;

    internal bool UsesReportedEarbudAvailability => Model == HeadsetModel.FreeBudsPro5;
}

// Model IDs and settings are based on the upstream HuaweiPods device routes and captures.
public static class HeadsetProfiles
{
    private static readonly IReadOnlyList<ModeOption> TwoAncLevels = Options(new(0x01, "轻度"), new(0x00, "均衡"));
    private static readonly IReadOnlyList<ModeOption> ThreeAncLevels = Options(new(0x03, "智慧动态"), new(0x01, "轻度"), new(0x00, "均衡"));
    private static readonly IReadOnlyList<ModeOption> FourAncLevels = Options(new(0x03, "智慧动态"), new(0x01, "轻度"), new(0x00, "均衡"), new(0x02, "深度"));
    private static readonly IReadOnlyList<ModeOption> AlternateAncLevels = Options(new(0x01, "智慧动态"), new(0x00, "轻度"), new(0x02, "均衡"), new(0x03, "深度"));
    private static readonly IReadOnlyList<ModeOption> StandardTransparency = Options(new ModeOption(0xFF, "通透"));
    private static readonly IReadOnlyList<ModeOption> VoiceTransparency = Options(new(0x02, "通透"), new(0x01, "透传人声"));
    private static readonly ModeOption Bass = new(0x02, "低音增强");
    private static readonly ModeOption Treble = new(0x03, "高音增强");
    private static readonly ModeOption ClearVoice = new(0x09, "清晰人声");
    private static readonly ModeOption DefaultSound = new(0x01, "默认");
    private static readonly ModeOption SportEnhance = new(0x0A, "运动增强");
    private static readonly IReadOnlyList<ModeOption> ThreeSoundPresets = Options(DefaultSound, Bass, Treble);
    private static readonly IReadOnlyList<ModeOption> FourSoundPresets = Options(DefaultSound, Bass, Treble, ClearVoice);
    private static readonly IReadOnlyList<ModeOption> FreeBuds6SoundPresets = Options(new(0x00, "默认"), Bass, Treble, ClearVoice, new(0xC9, "古典"));
    private static readonly IReadOnlyList<ModeOption> FreeArcSoundPresets = Options(DefaultSound, SportEnhance, Bass, Treble, ClearVoice);
    private static readonly IReadOnlyList<ModeOption> FreeClip2SoundPresets = Options(DefaultSound, SportEnhance, Treble, ClearVoice);
    private static readonly IReadOnlyList<ModeOption> FreeBuds7SoundPresets = Options(
        new(0x05, "均衡"), new(0x09, "人声"), new(0x02, "低音"), new(0xC9, "古典"),
        new(0x0D, "影院"), new(0x0F, "播客"), new(0x0E, "游戏"), new(0x10, "运动"));
    private static readonly IReadOnlyList<ModeOption> Pro5SoundPresets = Options(
        new(0x05, "悦彰均衡"), new(0x09, "悦彰人声"), new(0x02, "悦彰低音"), new(0xC9, "悦彰古典"),
        new(0x0D, "影院"), new(0x0F, "播客"), new(0x0E, "游戏"), new(0x10, "运动"), new(0x11, "AI 音效"));

    public static IReadOnlyList<HeadsetProfile> All { get; } = Array.AsReadOnly(new HeadsetProfile[]
    {
        new(HeadsetModel.FreeBuds3, "HUAWEI FreeBuds 3") { SupportsAnc = true, SupportsBattery = false },
        new(HeadsetModel.FreeBuds4, "HUAWEI FreeBuds 4", "000129")
        {
            SupportsAnc = true,
            AncLevels = TwoAncLevels,
            SupportsWearDetection = true
        },
        new(HeadsetModel.FreeBuds4E, "HUAWEI FreeBuds 4E", "000135")
        {
            SupportsAnc = true,
            AncLevels = TwoAncLevels,
            SupportsWearDetection = true
        },
        new(HeadsetModel.FreeBuds5, "HUAWEI FreeBuds 5", "000141")
        {
            SupportsAnc = true,
            AncLevels = ThreeAncLevels,
            SupportsWearDetection = true,
            SupportsLowLatency = true
        },
        new(HeadsetModel.FreeBuds5i, "HUAWEI FreeBuds 5i", "000145")
        {
            SupportsAnc = true,
            AncLevels = FourAncLevels,
            TransparencyLevels = StandardTransparency,
            SupportsWearDetection = true,
            SupportsLowLatency = true
        },
        new(HeadsetModel.FreeBudsSe4Anc, "HUAWEI FreeBuds SE 4 ANC", "000169")
        {
            SupportsAnc = true,
            AncLevels = FourAncLevels,
            TransparencyLevels = StandardTransparency,
            SupportsLowLatency = true
        },
        new(HeadsetModel.FreeBuds6, "HUAWEI FreeBuds 6", "000155")
        {
            SupportsAnc = true,
            AncLevels = ThreeAncLevels,
            SupportsWearDetection = true,
            SupportsLowLatency = true,
            SupportsLowLatencyReadback = true
        },
        new(HeadsetModel.FreeBuds6i, "HUAWEI FreeBuds 6i", "000153")
        {
            SupportsAnc = true,
            AncLevels = FourAncLevels,
            TransparencyLevels = VoiceTransparency,
            SupportsWearDetection = true,
            SupportsLowLatency = true
        },
        new(HeadsetModel.FreeBuds7, "HUAWEI FreeBuds 7", "000174")
        {
            SupportsAnc = true,
            AncLevels = ThreeAncLevels,
            TransparencyLevels = Options(new ModeOption(0x02, "通透")),
            SupportsWearDetection = true,
            SupportsLowLatency = true,
            SupportsLowLatencyReadback = true
        },
        new(HeadsetModel.FreeBuds7i, "HUAWEI FreeBuds 7i", "000163")
        {
            SupportsAnc = true,
            AncLevels = AlternateAncLevels,
            TransparencyLevels = StandardTransparency,
            SupportsWearDetection = true,
            SupportsLowLatency = true
        },
        new(HeadsetModel.FreeBudsPro3, "HUAWEI FreeBuds Pro 3", "000149")
        {
            SupportsAnc = true,
            AncLevels = AlternateAncLevels,
            TransparencyLevels = Options(new(0xFF, "通透"), new(0x01, "透传人声")),
            SupportsWearDetection = true,
            SupportsLowLatency = true
        },
        new(HeadsetModel.FreeBudsPro4, "HUAWEI FreeBuds Pro 4") { SupportsAnc = true },
        new(HeadsetModel.FreeBudsPro5, "HUAWEI FreeBuds Pro 5", "00016D")
        {
            SupportsAnc = true,
            AncLevels = FourAncLevels,
            TransparencyLevels = Options(new(0x02, "通透"), new(0x01, "透传人声"), new(0x04, "智慧动态透传")),
            SupportsWearDetection = true,
            SupportsLowLatency = true
        },
        new(HeadsetModel.FreeClip, "HUAWEI FreeClip"),
        new(HeadsetModel.FreeClip2, "HUAWEI FreeClip 2", "000167")
        {
            SupportsWearDetection = true,
            SupportsLowLatency = true,
            SupportsLowLatencyReadback = true
        },
        new(HeadsetModel.FreeClip2Collector, "HUAWEI FreeClip 2 典藏版", "000173")
        {
            SupportsWearDetection = true,
            SupportsLowLatency = true,
            SupportsLowLatencyReadback = true
        },
        new(HeadsetModel.FreeArc, "HUAWEI FreeArc", "00015D"),
        new(HeadsetModel.Eyewear, "HUAWEI Eyewear") { HasChargingCase = false },
        new(HeadsetModel.Eyewear2, "HUAWEI Eyewear 2", "00014F") { HasChargingCase = false, SupportsLowLatency = true },
        new(HeadsetModel.Eyewear3, "HUAWEI Eyewear 3", "000139") { HasChargingCase = false }
    });

    public static HeadsetProfile? FindByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = NormalizeName(name);
        if (normalized.StartsWith("huawei", StringComparison.Ordinal))
            normalized = normalized["huawei".Length..];

        if (normalized is "华为智能眼镜" or "华为智能眼镜第一代")
            return All.First(profile => profile.Model == HeadsetModel.Eyewear);

        return All.FirstOrDefault(profile => NormalizeName(profile.DisplayName)["huawei".Length..] == normalized);
    }

    public static HeadsetProfile? FindByModelId(string? modelId) =>
        modelId is null ? null : All.FirstOrDefault(profile => string.Equals(profile.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<ModeOption> SoundPresetsFor(HeadsetModel model) => model switch
    {
        HeadsetModel.FreeBuds4 or HeadsetModel.FreeBuds4E => ThreeSoundPresets,
        HeadsetModel.FreeBuds5 or HeadsetModel.FreeBuds5i or HeadsetModel.FreeBudsSe4Anc or
        HeadsetModel.FreeBuds6i or HeadsetModel.FreeBuds7i => FourSoundPresets,
        HeadsetModel.FreeBuds6 => FreeBuds6SoundPresets,
        HeadsetModel.FreeBuds7 => FreeBuds7SoundPresets,
        HeadsetModel.FreeBudsPro5 => Pro5SoundPresets,
        HeadsetModel.FreeClip2 or HeadsetModel.FreeClip2Collector => FreeClip2SoundPresets,
        HeadsetModel.FreeArc => FreeArcSoundPresets,
        _ => []
    };

    private static string NormalizeName(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IReadOnlyList<ModeOption> Options(params ModeOption[] options) => Array.AsReadOnly(options);
}
