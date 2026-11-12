using System.Text;

namespace HuaweiPods.Protocol;

public enum NoiseControlMode : byte
{
    Off = 0,
    NoiseCancellation = 1,
    Transparency = 2
}

public sealed record BatteryPart(int Percentage, bool IsCharging, bool IsConnected);
public sealed record BatteryState(BatteryPart? Left, BatteryPart? Right, BatteryPart? Case);
public sealed record AncState(NoiseControlMode Mode, byte? Level = null);
public sealed record DeviceIdentity(string ModelId, string? SubModelId);

public static partial class HuaweiProtocol
{
    private const byte DeviceService = 0x01;
    private const byte DeviceInfoCommand = 0x07;
    private const byte BatteryCommand = 0x08;
    private const byte BatteryReport = 0x27;
    private const byte AudioService = 0x2B;
    private const byte AncSetCommand = 0x04;
    private const byte AncDirectionCommand = 0x08;
    private const byte AncStateCommand = 0x2A;
    private const byte WearSetCommand = 0x10;
    private const byte WearStateCommand = 0x11;
    private const byte LowLatencyCommand = 0x6C;
    private const byte EnterMode = 0xFF;

    public static byte[] BatteryQuery() =>
        HuaweiWireFormat.Encode(DeviceService, BatteryCommand, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00);

    public static byte[] DeviceInfoQuery() => HuaweiWireFormat.Encode(DeviceService, DeviceInfoCommand,
        0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00, 0x05, 0x00,
        0x06, 0x00, 0x07, 0x00, 0x08, 0x00, 0x09, 0x00, 0x0A, 0x00,
        0x0B, 0x00, 0x0C, 0x00, 0x0F, 0x00, 0x18, 0x00, 0x19, 0x00);

    public static byte[] AncQuery() => HuaweiWireFormat.Encode(AudioService, AncStateCommand, 0x01, 0x00);

    public static byte[] WearDetectionQuery() => HuaweiWireFormat.Encode(AudioService, WearStateCommand, 0x01, 0x00);

    public static byte[] LowLatencyQuery(HeadsetProfile profile)
    {
        RequireSupport(profile.SupportsLowLatencyReadback);
        return HuaweiWireFormat.Encode(AudioService, LowLatencyCommand, 0x02, 0x00);
    }

    /// <summary>With no level, switches the main mode. An explicit level adjusts an already active mode.</summary>
    public static byte[] SetAnc(HeadsetProfile profile, NoiseControlMode mode, byte? level = null)
    {
        RequireSupport(profile.SupportsAnc);
        if (mode is not (NoiseControlMode.Off or NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency))
            throw new ArgumentOutOfRangeException(nameof(mode));

        if (mode == NoiseControlMode.Transparency)
            RequireSupport(profile.SupportsTransparency);

        if (profile.Model == HeadsetModel.FreeBuds3)
            return HuaweiWireFormat.Encode(AudioService, AncSetCommand, 0x01, 0x01, (byte)mode);

        if (mode == NoiseControlMode.Off)
            return HuaweiWireFormat.Encode(AudioService, AncSetCommand, 0x01, 0x02, 0x00, 0x00);

        var options = mode == NoiseControlMode.Transparency ? profile.TransparencyLevels : profile.AncLevels;
        if (level is not (null or EnterMode) && !options.Any(option => option.Value == level))
            throw new ArgumentOutOfRangeException(nameof(level));

        return HuaweiWireFormat.Encode(AudioService, AncSetCommand, 0x01, 0x02, (byte)mode, level ?? EnterMode);
    }

    public static byte[] SetAncDirection(HeadsetProfile profile, int direction)
    {
        RequireSupport(profile.SupportsDirection);
        ArgumentOutOfRangeException.ThrowIfNegative(direction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(direction, 8);
        return HuaweiWireFormat.Encode(AudioService, AncDirectionCommand, 0x01, 0x01, (byte)direction);
    }

    public static byte[] SetWearDetection(HeadsetProfile profile, bool enabled)
    {
        RequireSupport(profile.SupportsWearDetection);
        return EncodeBoolean(WearSetCommand, enabled);
    }

    public static byte[] SetLowLatency(HeadsetProfile profile, bool enabled)
    {
        RequireSupport(profile.SupportsLowLatency);
        return EncodeBoolean(LowLatencyCommand, enabled);
    }

    public static BatteryState? ParseBattery(HuaweiFrame frame, HeadsetProfile? profile = null)
    {
        if (profile?.SupportsBattery == false || frame.Service != DeviceService || frame.Command is not (BatteryCommand or BatteryReport))
            return null;

        var levels = frame.GetField(0x02);
        if (levels.Length < 2)
            return null;

        var charging = frame.GetField(0x03);
        // Other models do not reliably use the Pro 5's per-side out-of-case flags.
        var reported = profile?.UsesReportedEarbudAvailability == true ? frame.GetField(0x05) : [];
        var left = ReadBatteryPart(levels, charging, 0, isEarbud: true, reportedConnected: ReadEarbudAvailability(reported, 0));
        var right = ReadBatteryPart(levels, charging, 1, isEarbud: true, reportedConnected: ReadEarbudAvailability(reported, 1));
        var chargingCase = profile?.HasChargingCase != false ? ReadBatteryPart(levels, charging, 2, isEarbud: false) : null;

        return left is null && right is null && chargingCase is null ? null : new BatteryState(left, right, chargingCase);
    }

    public static AncState? ParseAnc(HuaweiFrame frame, HeadsetProfile profile)
    {
        if (!profile.SupportsAncReadback || frame.Service != AudioService || frame.Command != AncStateCommand)
            return null;

        var value = frame.GetField(0x01);
        if (value.Length != 2)
            return null;

        // Reports store [submode, mode], the reverse of a set command.
        var mode = (NoiseControlMode)value[1];
        var level = value[0];
        // The standard transparency report uses 02 even on profiles that select it with FF.
        if (mode == NoiseControlMode.Transparency && level == 0x02 && profile.DefaultTransparencyLevel == EnterMode)
            level = EnterMode;

        return mode switch
        {
            NoiseControlMode.Off => new AncState(mode),
            NoiseControlMode.NoiseCancellation when profile.AncLevels.Any(option => option.Value == level) => new AncState(mode, level),
            NoiseControlMode.Transparency when profile.SupportsTransparency => new AncState(mode, level),
            _ => null
        };
    }

    public static DeviceIdentity? ParseDeviceInfo(HuaweiFrame frame)
    {
        if (frame.Service != DeviceService || frame.Command != DeviceInfoCommand)
            return null;

        var modelCode = frame.GetField(0x02);
        var model = frame.GetField(0x0A);
        var prefix = frame.GetField(0x0F);
        var subModel = frame.GetField(0x19);
        if (modelCode.Length != 2 || model.Length is < 8 or > 64 || subModel.Length > 1)
            return null;

        var separator = model.Length - 7;
        if (model[separator] != '-' || !model[..separator].SequenceEqual(prefix))
            return null;

        foreach (var character in prefix)
        {
            if (character is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'))
                return null;
        }

        var modelId = model[(separator + 1)..];
        foreach (var character in modelId)
        {
            if (character is not (>= (byte)'A' and <= (byte)'F' or >= (byte)'0' and <= (byte)'9'))
                return null;
        }

        var id = Encoding.ASCII.GetString(modelId);
        if (id == "00000A" || !id.EndsWith(Convert.ToHexString(modelCode), StringComparison.Ordinal))
            return null;

        return new DeviceIdentity(id, subModel.IsEmpty ? null : Convert.ToHexString(subModel));
    }

    public static bool? ParseWearDetection(HuaweiFrame frame) =>
        frame.Service == AudioService && frame.Command == WearStateCommand ? ReadBoolean(frame.GetField(0x01)) : null;

    public static bool? ParseLowLatency(HuaweiFrame frame, HeadsetProfile profile) =>
        profile.SupportsLowLatencyReadback && frame.Service == AudioService && frame.Command == LowLatencyCommand
            ? ReadBoolean(frame.GetField(0x02))
            : null;

    private static BatteryPart? ReadBatteryPart(ReadOnlySpan<byte> levels, ReadOnlySpan<byte> charging, int index,
        bool isEarbud, bool? reportedConnected = null)
    {
        if (index >= levels.Length || levels[index] > 100)
            return null;

        var percentage = levels[index];
        var connected = reportedConnected ?? (!isEarbud || percentage > 0);
        return new BatteryPart(percentage, connected && index < charging.Length && charging[index] != 0, connected);
    }

    private static bool? ReadEarbudAvailability(ReadOnlySpan<byte> reported, int index) => index >= reported.Length ? null : reported[index] switch
    {
        0 => true,
        1 => false,
        _ => null
    };

    private static bool? ReadBoolean(ReadOnlySpan<byte> value) => value.Length != 1 ? null : value[0] switch
    {
        0 => false,
        1 => true,
        _ => null
    };

    private static byte[] EncodeBoolean(byte command, bool enabled) =>
        HuaweiWireFormat.Encode(AudioService, command, 0x01, 0x01, enabled ? (byte)1 : (byte)0);

    private static void RequireSupport(bool supported)
    {
        if (!supported)
            throw new NotSupportedException("该型号不支持此功能。");
    }
}
