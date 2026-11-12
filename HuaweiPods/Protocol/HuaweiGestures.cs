namespace HuaweiPods.Protocol;

public sealed class GestureDefinition
{
    internal GestureDefinition(string id, string label, IReadOnlyList<ModeOption> options, GestureWire wire)
    {
        Id = id;
        Label = label;
        Options = options;
        Wire = wire;
    }

    public string Id { get; }
    public string Label { get; }
    public IReadOnlyList<ModeOption> Options { get; }
    public bool IsToggle => Wire.Encoding is GestureEncoding.SharedDoubleTap or GestureEncoding.SharedSwipe or GestureEncoding.Pinch;
    public bool SupportsReadback => Wire.ReadCommand is not null;
    internal GestureWire Wire { get; }
}

public sealed record GestureState(string Id, byte Value);

internal enum GestureEncoding
{
    SideAction,
    EyewearSwipe,
    SharedDoubleTap,
    SharedSwipe,
    Pinch
}

internal sealed record GestureWire(byte Service, byte SetCommand, byte? ReadCommand, GestureEncoding Encoding,
    byte SideOrSlot = 0, byte Context = 0, byte EnabledValue = 0);

/// <summary>Gesture commands and model-specific choices verified in the upstream HuaweiPods captures.</summary>
public static class HuaweiGestures
{
    private const byte DeviceService = 0x01;
    private const byte AudioService = 0x2B;
    private const byte NoAction = 0xFF;

    private static readonly ModeOption None = new(NoAction, "无");
    private static readonly ModeOption PlayPause = new(0x01, "播放/暂停");
    private static readonly ModeOption Next = new(0x02, "下一曲");
    private static readonly ModeOption Previous = new(0x07, "上一曲");
    private static readonly ModeOption Assistant = new(0x00, "唤醒语音助手");
    private static readonly ModeOption Volume = new(0x00, "调节音量");
    private static readonly ModeOption Track = new(0x01, "切换曲目");

    private static readonly IReadOnlyList<ModeOption> ToggleOptions = Options(new(0, "关闭"), new(1, "开启"));
    private static readonly IReadOnlyList<ModeOption> LegacyDouble = Options(new(0x04, "下一曲"), PlayPause, Assistant,
        new(0x03, "噪声控制"), None);
    private static readonly IReadOnlyList<ModeOption> ModernDouble = Options(PlayPause, Next, Previous, Assistant, None);
    private static readonly IReadOnlyList<ModeOption> MediaDouble = Options(PlayPause, Next);
    private static readonly IReadOnlyList<ModeOption> ClipDouble = Options(PlayPause, Next, new(0x07, "空间音频开关"), Assistant, None);
    private static readonly IReadOnlyList<ModeOption> EyewearDouble = Options(PlayPause, Assistant, None);
    private static readonly IReadOnlyList<ModeOption> Triple = Options(Next, Previous, None);
    private static readonly IReadOnlyList<ModeOption> Hold4 = Options(new(0x03, "噪声控制"), new(0x0E, "听歌识曲"), None);
    private static readonly IReadOnlyList<ModeOption> HoldModern = Options(Assistant, new(0x0A, "噪声控制"), None);
    private static readonly IReadOnlyList<ModeOption> HoldArc = Options(Assistant, None);
    private static readonly IReadOnlyList<ModeOption> SwipeMedia = Options(Volume, Track, None);
    private static readonly IReadOnlyList<ModeOption> SwipeClip = Options(Volume, None);
    private static readonly IReadOnlyList<ModeOption> SwipeArc = Options(Volume, Track);

    private static readonly IReadOnlyList<GestureDefinition> FreeBuds4Definitions = ReadOnly(
        [.. DoubleTap(ModernDouble), .. Hold(Hold4, true)]);
    private static readonly IReadOnlyList<GestureDefinition> FreeClip2Definitions = ReadOnly(
        [.. DoubleTap(ClipDouble), .. TripleTap(), .. Swipe(SwipeClip)]);

    private static readonly IReadOnlyDictionary<HeadsetModel, IReadOnlyList<GestureDefinition>> Definitions =
        new Dictionary<HeadsetModel, IReadOnlyList<GestureDefinition>>
        {
            [HeadsetModel.FreeBuds3] = ReadOnly(DoubleTap(LegacyDouble, readback: false)),
            [HeadsetModel.FreeBuds4] = FreeBuds4Definitions,
            [HeadsetModel.FreeBuds4E] = FreeBuds4Definitions,
            [HeadsetModel.FreeBuds5i] = ReadOnly(DoubleTap(ModernDouble)),
            [HeadsetModel.FreeBuds6i] = ReadOnly(
                [.. DoubleTap(MediaDouble), .. TripleTap(), .. Hold(HoldModern, false), .. Swipe(SwipeMedia)]),
            [HeadsetModel.FreeBudsPro3] = ReadOnly(
                [.. Hold(HoldModern, false), .. PinchControls(false, includeCalls: true), SwipeVolume(false)]),
            [HeadsetModel.FreeBudsPro5] = ReadOnly(
                [.. TripleTap(), .. PinchControls(false, includeCalls: true), SwipeVolume(true)]),
            [HeadsetModel.FreeBuds7] = ReadOnly(
                [Toggle("double-tap", "双击播放/暂停", new(DeviceService, 0x1F, 0x20, GestureEncoding.SharedDoubleTap, EnabledValue: 1)),
                    .. TripleTap(), .. PinchControls(true, includeCalls: false), SwipeVolume(true)]),
            [HeadsetModel.FreeBuds7i] = ReadOnly(
                [.. DoubleTap(ModernDouble), .. TripleTap(), .. Hold(HoldModern, false), SwipeVolume(true)]),
            [HeadsetModel.FreeClip2] = FreeClip2Definitions,
            [HeadsetModel.FreeClip2Collector] = FreeClip2Definitions,
            [HeadsetModel.FreeArc] = ReadOnly(
                [.. DoubleTap(ModernDouble), .. TripleTap(), .. Hold(HoldArc, true), .. Swipe(SwipeArc)]),
            [HeadsetModel.Eyewear2] = ReadOnly(
                [.. DoubleTap(EyewearDouble, readback: false, eyewear: true), .. Swipe(SwipeMedia, eyewear: true)])
        };

    public static IReadOnlyList<GestureDefinition> GetDefinitions(HeadsetProfile profile) =>
        Definitions.GetValueOrDefault(profile.Model) ?? [];

    public static IReadOnlyList<byte[]> GetQueries(HeadsetProfile profile)
    {
        var queries = new List<byte[]>();
        foreach (var definition in GetDefinitions(profile))
        {
            var query = BuildQueryCore(definition.Wire);
            if (query is not null && !queries.Any(previous => previous.AsSpan().SequenceEqual(query)))
                queries.Add(query);
        }

        return queries;
    }

    public static byte[]? BuildQuery(HeadsetProfile profile, GestureDefinition definition) =>
        GetDefinitions(profile).Contains(definition) ? BuildQueryCore(definition.Wire) : null;

    public static byte[] Set(HeadsetProfile profile, GestureDefinition definition, byte value)
    {
        if (!GetDefinitions(profile).Contains(definition))
            throw new NotSupportedException("该型号不支持此手势。");
        if (!definition.Options.Any(option => option.Value == value))
            throw new ArgumentOutOfRangeException(nameof(value), "该型号不支持此手势选项。");

        var wire = definition.Wire;
        byte action = definition.IsToggle ? (value == 1 ? wire.EnabledValue : NoAction) : value;
        byte slot = wire.SideOrSlot;
        return wire.Encoding switch
        {
            GestureEncoding.SideAction => HuaweiWireFormat.Encode(wire.Service, wire.SetCommand, slot, 0x01, action),
            GestureEncoding.EyewearSwipe => HuaweiWireFormat.Encode(wire.Service, wire.SetCommand,
                slot, slot, action, slot, slot, action),
            GestureEncoding.SharedDoubleTap => HuaweiWireFormat.Encode(wire.Service, wire.SetCommand,
                0x01, 0x01, action, 0x02, 0x01, action),
            // The captured swipe write declares 0x02 here, but sends one action byte.
            GestureEncoding.SharedSwipe => HuaweiWireFormat.Encode(wire.Service, wire.SetCommand,
                0x01, 0x01, action, 0x02, 0x02, action),
            GestureEncoding.Pinch => HuaweiWireFormat.Encode(wire.Service, wire.SetCommand,
                0x01, 0x01, slot, 0x02, 0x01, wire.Context, 0x03, 0x01, action, 0x04, 0x01, action),
            _ => throw new NotSupportedException("未知的手势协议。")
        };
    }

    public static IReadOnlyList<GestureState> Parse(HuaweiFrame frame, HeadsetProfile profile)
    {
        List<GestureState>? states = null;
        foreach (var definition in GetDefinitions(profile))
        {
            var wire = definition.Wire;
            if (frame.Service != wire.Service || frame.Command != wire.ReadCommand)
                continue;

            byte? value = wire.Encoding switch
            {
                GestureEncoding.SideAction => ReadByte(frame, wire.SideOrSlot),
                GestureEncoding.SharedDoubleTap or GestureEncoding.SharedSwipe => ReadShared(frame, 0x01, 0x02, wire.EnabledValue),
                GestureEncoding.Pinch when ReadByte(frame, 0x01) == wire.SideOrSlot && ReadByte(frame, 0x02) == wire.Context
                    => ReadShared(frame, 0x03, 0x04, wire.EnabledValue),
                _ => null
            };
            if (value is { } reported && definition.Options.Any(option => option.Value == reported))
                (states ??= []).Add(new GestureState(definition.Id, reported));
        }

        return states is null ? Array.Empty<GestureState>() : states;
    }

    private static byte[]? BuildQueryCore(GestureWire wire)
    {
        if (wire.ReadCommand is not { } command)
            return null;

        return wire.Encoding == GestureEncoding.Pinch
            ? HuaweiWireFormat.Encode(wire.Service, command, 0x01, 0x01, wire.SideOrSlot, 0x02, 0x01, wire.Context)
            : HuaweiWireFormat.Encode(wire.Service, command, 0x01, 0x00, 0x02, 0x00);
    }

    private static byte? ReadByte(HuaweiFrame frame, byte type) =>
        frame.GetField(type) is [var value] ? value : null;

    private static byte? ReadShared(HuaweiFrame frame, byte left, byte right, byte enabledValue)
    {
        if (ReadByte(frame, left) is not { } action || ReadByte(frame, right) != action)
            return null;

        return action == enabledValue ? (byte)1 : action == NoAction ? (byte)0 : null;
    }

    private static GestureDefinition[] DoubleTap(IReadOnlyList<ModeOption> options, bool readback = true, bool eyewear = false) =>
        Sides("double-tap", "双击", options, DeviceService, 0x1F, readback ? (byte)0x20 : null, eyewear: eyewear);

    private static GestureDefinition[] TripleTap() => Sides("triple-tap", "三击", Triple, DeviceService, 0x25, 0x26);

    private static GestureDefinition[] Hold(IReadOnlyList<ModeOption> options, bool readback) =>
        Sides("hold", "长按", options, AudioService, 0x16, readback ? (byte)0x17 : null);

    private static GestureDefinition[] Swipe(IReadOnlyList<ModeOption> options, bool eyewear = false) =>
        Sides("swipe", "滑动", options, AudioService, 0x1E, eyewear ? null : (byte)0x1F,
            eyewear ? GestureEncoding.EyewearSwipe : GestureEncoding.SideAction, eyewear);

    private static GestureDefinition[] Sides(string id, string label, IReadOnlyList<ModeOption> options,
        byte service, byte write, byte? query, GestureEncoding encoding = GestureEncoding.SideAction, bool eyewear = false)
    {
        string side = eyewear ? "镜腿" : "耳";
        return
        [
            new GestureDefinition(id + "-left", label + "左" + side, options, new GestureWire(service, write, query, encoding, 1)),
            new GestureDefinition(id + "-right", label + "右" + side, options, new GestureWire(service, write, query, encoding, 2))
        ];
    }

    private static GestureDefinition SwipeVolume(bool readback) =>
        Toggle("swipe-volume", "滑动调节音量",
            new GestureWire(AudioService, 0x1E, readback ? (byte)0x1F : null, GestureEncoding.SharedSwipe));

    private static GestureDefinition[] PinchControls(bool readback, bool includeCalls)
    {
        var media = new[]
        {
            Pinch("pinch-play", "捏合：播放/暂停", 0, 2, 0x02, readback),
            Pinch("pinch-next", "双捏：下一曲", 1, 2, 0x04, readback),
            Pinch("pinch-previous", "三捏：上一曲", 2, 2, 0x03, readback)
        };
        return includeCalls
            ? [Pinch("pinch-answer", "捏合：接听/结束通话", 0, 1, 0x00, readback),
                Pinch("pinch-reject", "双捏：拒接来电", 1, 1, 0x01, readback), .. media]
            : media;
    }

    private static GestureDefinition Pinch(string id, string label, byte slot, byte context, byte action, bool readback) =>
        Toggle(id, label, new GestureWire(AudioService, 0x92, readback ? (byte)0x93 : null, GestureEncoding.Pinch, slot, context, action));

    private static GestureDefinition Toggle(string id, string label, GestureWire wire) =>
        new(id, label, ToggleOptions, wire);

    private static IReadOnlyList<GestureDefinition> ReadOnly(GestureDefinition[] definitions) => Array.AsReadOnly(definitions);

    private static IReadOnlyList<ModeOption> Options(params ModeOption[] options) => Array.AsReadOnly(options);
}
