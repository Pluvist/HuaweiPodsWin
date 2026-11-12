namespace HuaweiPods.Protocol;

public static partial class HuaweiProtocol
{
    private const byte SoundPresetSetCommand = 0x49;
    private const byte SoundPresetStateCommand = 0x4A;
    private const byte ClassicalPreset = 0xC9;

    public static byte[] SoundPresetQuery(HeadsetProfile profile)
    {
        RequireSupport(profile.SoundPresets.Count > 0);
        return HuaweiWireFormat.Encode(AudioService, SoundPresetStateCommand, 0x02, 0x00);
    }

    public static byte[] SetSoundPreset(HeadsetProfile profile, byte preset)
    {
        RequireSupport(profile.SoundPresets.Count > 0);
        if (!profile.SoundPresets.Any(option => option.Value == preset))
            throw new ArgumentOutOfRangeException(nameof(preset));

        // This preset is selected by sending the complete captured curve.
        if (preset == ClassicalPreset)
            return Convert.FromHexString("5A001D002B490101C902010A050101030AFB141E0A0000E7F60A000403323031C367");

        return HuaweiWireFormat.Encode(AudioService, SoundPresetSetCommand, 0x01, 0x01, preset);
    }

    public static byte? ParseSoundPreset(HuaweiFrame frame, HeadsetProfile profile)
    {
        if (profile.SoundPresets.Count == 0 || frame.Service != AudioService || frame.Command != SoundPresetStateCommand)
            return null;

        var selected = frame.GetField(0x02);
        return selected.Length == 1 ? selected[0] : null;
    }
}
