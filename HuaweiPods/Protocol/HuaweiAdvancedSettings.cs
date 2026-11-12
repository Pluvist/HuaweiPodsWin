namespace HuaweiPods.Protocol;

public sealed record DeviceSettingDefinition(
    string Key,
    string Title,
    string Group,
    bool IsToggle,
    IReadOnlyList<ModeOption> Options,
    Func<byte[]> Query,
    Func<byte, byte[]> Write,
    Func<HuaweiFrame, byte?> Read,
    string? Description = null,
    IReadOnlyList<string>? RefreshKeys = null);

/// <summary>
/// Direct device settings verified in HuaweiPods dc5b9d1 controllers and captured protocol tests.
/// The model whitelist is deliberate: sharing a command ID does not establish model support.
/// </summary>
public static class AdvancedSettings
{
    private static readonly IReadOnlyList<ModeOption> BooleanOptions = Options(new(0, "关闭"), new(1, "开启"));
    private static readonly IReadOnlyList<ModeOption> SpatialOptions = Options(new(0, "关闭"), new(2, "固定"), new(1, "头部跟踪"));
    private static readonly IReadOnlyList<ModeOption> SceneOptions = Options(
        new(0, "默认空间"), new(1, "有声剧场"), new(2, "电影院"), new(3, "音乐厅"));
    private static readonly IReadOnlyList<ModeOption> HeadActionOptions = Options(
        new(0, "无"), new(1, "接听来电"), new(2, "拒接来电"));
    private static readonly IReadOnlyList<string> HeadKeys = Array.AsReadOnly(new[] { "head_motion", "head_nod", "head_shake" });
    private const string SpatialQuery = "5A000A002BB4010118020003009B3F";
    private const string HeadQuery = "5A0006002BB401010B289B";

    private static readonly DeviceSettingDefinition HighQuality = Define(
        "high_quality", "高清音质", "音质与音效", BooleanOptions,
        "5A0005002BA30101F794", value => SetByte(0xA2, 1, value), frame => ReadByte(frame, 0xA3, 2),
        isToggle: true, description: "设置耳机端高清音质；实际蓝牙编码取决于 Windows 和蓝牙驱动。");
    private static readonly DeviceSettingDefinition AdaptiveVolume = AamToggle(
        "adaptive_volume", "智感音量自适应", 0x02, "5A0008002BB401010202003619");
    private static readonly DeviceSettingDefinition DropReminder = AamToggle(
        "drop_reminder", "掉落提醒", 0x07, "5A0008002BB40101070200DDE9");
    private static readonly DeviceSettingDefinition HeadMotion = AamToggle(
        "head_motion", "头动控制", 0x0B, HeadQuery);
    private static readonly DeviceSettingDefinition SingleEarAnc = AamToggle(
        "single_ear_anc", "单耳降噪", 0x05, "5A0008002BB40101050200B389", "降噪",
        "佩戴一只耳机时启用降噪。");
    private static readonly DeviceSettingDefinition AutoSwitch = Define(
        "auto_switch", "音频连接自动切换", "连接与设备", BooleanOptions,
        "5A0005002B2F0100A98E", value => SetByte(0x2E, 1, value), frame => ReadByte(frame, 0x2F, 1), true);
    private static readonly DeviceSettingDefinition DualDevice = AutoSwitch with { Title = "双设备连接" };
    private static readonly DeviceSettingDefinition CaseSound = Define(
        "case_sound", "充电盒提示音", "充电盒与佩戴", BooleanOptions,
        "5A0007002BB1020003007FAB", value => SetByte(0xB1, 1, value), frame => ReadByte(frame, 0xB1, 2), true);
    private static readonly DeviceSettingDefinition Pro5CaseSound = Define(
        "case_sound", "充电盒提示音", "充电盒与佩戴", BooleanOptions,
        "5A0006002BB40101108BC1", value => SetByte(0xB1, 1, value),
        frame => ReadAamLeadingByte(frame, 0x10, extendedLength: 12), true);
    private static readonly DeviceSettingDefinition VoiceControl = Define(
        "voice_control", "语音控制", "智能功能", BooleanOptions,
        "5A0008002BB401010302000129", value => SetAam(0x03, 2, value),
        frame => ReadAamLeadingByte(frame, 0x03, extendedLength: 2), true);
    private static readonly DeviceSettingDefinition EarTipMaterial = Define(
        "ear_tip_material", "耳塞材质", "充电盒与佩戴", Options(new(1, "硅胶"), new(2, "记忆海绵")),
        "5A0008002BB40101080200F1D8", value => SetAam(0x08, 2, value), frame => ReadAam(frame, 0x08, 2));
    private static readonly DeviceSettingDefinition Spatial = Define(
        "spatial_audio", "空间音频", "音质与音效", SpatialOptions, SpatialQuery,
        value => SetAam(0x18, 2, value), frame => ReadSpatial(frame, 2));
    private static readonly DeviceSettingDefinition SpatialScene = Define(
        "spatial_scene", "空间音频场景", "音质与音效", SceneOptions, SpatialQuery,
        value => SetAam(0x18, 3, value), frame => ReadSpatial(frame, 3),
        description: "在开启空间音频时生效。");
    private static readonly DeviceSettingDefinition NoisyVolume = Define(
        "noisy_volume", "嘈杂环境增大媒体音量", "智能功能", BooleanOptions,
        "5A0005002B8801009182", value => SetByte(0x87, 1, value), frame => ReadByte(frame, 0x88, 1), true,
        "允许进一步增大媒体音量，可能影响音质。");
    private static readonly DeviceSettingDefinition WindNoise = AamToggle(
        "wind_noise", "防风噪", 0x1D, "5A0008002BB401011D0200594B", "降噪");
    private static readonly DeviceSettingDefinition ClipHeadMotion = Define(
        "head_motion", "头动控制", "头动控制", BooleanOptions, HeadQuery,
        value => SetAam(0x0B, 2, value), frame => ReadClipHeadMotion(frame, 2), true, refreshKeys: HeadKeys);
    private static readonly DeviceSettingDefinition HeadNod = Define(
        "head_nod", "点头", "头动控制", HeadActionOptions, HeadQuery,
        value => SetAam(0x0B, 3, value), frame => ReadClipHeadMotion(frame, 3), refreshKeys: HeadKeys);
    private static readonly DeviceSettingDefinition HeadShake = Define(
        "head_shake", "摇头", "头动控制", HeadActionOptions, HeadQuery,
        value => SetAam(0x0B, 4, value), frame => ReadClipHeadMotion(frame, 4), refreshKeys: HeadKeys);

    private static readonly IReadOnlyList<DeviceSettingDefinition> FreeBuds5Settings = List(HighQuality);
    private static readonly IReadOnlyList<DeviceSettingDefinition> FreeBuds6Settings = List(AdaptiveVolume, DropReminder, CaseSound);
    private static readonly IReadOnlyList<DeviceSettingDefinition> FreeBuds7Settings = List(
        Spatial with { RefreshKeys = Array.AsReadOnly(new[] { "high_quality" }) },
        HighQuality with { RefreshKeys = Array.AsReadOnly(new[] { "spatial_audio" }) },
        AdaptiveVolume, HeadMotion, DropReminder, SingleEarAnc, AutoSwitch, CaseSound);
    private static readonly IReadOnlyList<DeviceSettingDefinition> FreeBuds7iSettings = List(Spatial, HighQuality, HeadMotion);
    private static readonly IReadOnlyList<DeviceSettingDefinition> Pro5Settings = List(
        Spatial, HighQuality, AdaptiveVolume, HeadMotion, VoiceControl, DualDevice, Pro5CaseSound, EarTipMaterial);
    private static readonly IReadOnlyList<DeviceSettingDefinition> Clip2Settings = List(
        Spatial with { RefreshKeys = Array.AsReadOnly(new[] { "spatial_scene" }) }, SpatialScene,
        AdaptiveVolume, DropReminder, NoisyVolume, ClipHeadMotion, HeadNod, HeadShake, DualDevice, CaseSound);
    private static readonly IReadOnlyList<DeviceSettingDefinition> WindSettings = List(WindNoise);

    public static IReadOnlyList<DeviceSettingDefinition> For(HeadsetProfile profile) => profile.Model switch
    {
        HeadsetModel.FreeBuds5 or HeadsetModel.FreeBuds5i => FreeBuds5Settings,
        HeadsetModel.FreeBuds6 => FreeBuds6Settings,
        HeadsetModel.FreeBuds7 => FreeBuds7Settings,
        HeadsetModel.FreeBuds7i => FreeBuds7iSettings,
        HeadsetModel.FreeBudsPro5 => Pro5Settings,
        HeadsetModel.FreeClip2 or HeadsetModel.FreeClip2Collector => Clip2Settings,
        HeadsetModel.FreeBudsSe4Anc => WindSettings,
        _ => []
    };

    public static DeviceSettingDefinition Get(HeadsetProfile profile, string key) =>
        For(profile).FirstOrDefault(setting => setting.Key == key)
        ?? throw new NotSupportedException("该型号没有已验证的此项设置协议。");

    public static byte[] Write(HeadsetProfile profile, string key, byte value) => Get(profile, key).Write(value);

    private static DeviceSettingDefinition AamToggle(string key, string title, byte feature, string query,
        string group = "智能功能", string? description = null) => Define(
        key, title, group, BooleanOptions, query, value => SetAam(feature, 2, value),
        frame => ReadAam(frame, feature, 2), true, description);

    private static DeviceSettingDefinition Define(string key, string title, string group,
        IReadOnlyList<ModeOption> options, string query, Func<byte, byte[]> write, Func<HuaweiFrame, byte?> read,
        bool isToggle = false, string? description = null, IReadOnlyList<string>? refreshKeys = null) =>
        new(key, title, group, isToggle, options, () => Convert.FromHexString(query),
            value => options.Any(option => option.Value == value)
                ? write(value) : throw new ArgumentOutOfRangeException(nameof(value), "未验证的设置值。"),
            frame => read(frame) is { } value && options.Any(option => option.Value == value) ? value : null,
            description, refreshKeys);

    private static byte[] SetByte(byte command, byte field, byte value) => HuaweiWireFormat.Encode(0x2B, command, field, 1, value);
    private static byte[] SetAam(byte feature, byte field, byte value) => HuaweiWireFormat.Encode(0x2B, 0xB4, 1, 1, feature, field, 1, value);
    private static byte? Single(ReadOnlySpan<byte> field) => field.Length == 1 ? field[0] : null;
    private static bool Matches(HuaweiFrame frame, byte command) =>
        frame.Service == 0x2B && frame.Command == command && frame.GetField(0x7F).IsEmpty;
    private static byte? ReadByte(HuaweiFrame frame, byte command, byte field) =>
        Matches(frame, command) ? Single(frame.GetField(field)) : null;
    private static bool IsAam(HuaweiFrame frame, byte feature) => Matches(frame, 0xB4) && Single(frame.GetField(1)) == feature;
    private static byte? ReadAam(HuaweiFrame frame, byte feature, byte field) => IsAam(frame, feature) ? Single(frame.GetField(field)) : null;

    private static byte? ReadAamLeadingByte(HuaweiFrame frame, byte feature, int extendedLength)
    {
        if (!IsAam(frame, feature))
            return null;
        var field = frame.GetField(2);
        // Pro 5 voice and case reports carry additional metadata after the enabled byte.
        return field.Length == 1 || field.Length == extendedLength ? field[0] : null;
    }

    private static byte? ReadSpatial(HuaweiFrame frame, byte field)
    {
        if (ReadAam(frame, 0x18, 2) is not (0 or 1 or 2) || ReadAam(frame, 0x18, 3) is not (0 or 1 or 2 or 3))
            return null;
        return Single(frame.GetField(field));
    }

    private static byte? ReadClipHeadMotion(HuaweiFrame frame, byte field)
    {
        // A full report is needed: a write echo containing only one action is not a readback.
        if (ReadAam(frame, 0x0B, 2) is not (0 or 1) || ReadAam(frame, 0x0B, 3) is not (0 or 1 or 2)
            || ReadAam(frame, 0x0B, 4) is not (0 or 1 or 2))
            return null;
        return Single(frame.GetField(field));
    }

    private static IReadOnlyList<ModeOption> Options(params ModeOption[] options) => Array.AsReadOnly(options);
    private static IReadOnlyList<DeviceSettingDefinition> List(params DeviceSettingDefinition[] settings) => Array.AsReadOnly(settings);
}
