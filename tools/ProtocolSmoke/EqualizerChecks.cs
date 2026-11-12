using HuaweiPods.Protocol;

internal static class EqualizerChecks
{
    // Captures from HuaweiEqualizerCodecTest, HuaweiEqualizerCodecFreeArcTest and
    // HuaweiFreeBuds7ControllerTest in upstream Nshpiter/HuaweiPods at dc5b9d1.
    private const string FreeBuds6iState =
        "5A00A9002B4A01010102016403040102030904010105010A060A28140A000A2319000A14" +
        "0718E585A8E9A291E6A0A1E58786000000000000000000000000086C640A28140A000A" +
        "2319000A14E585A8E9A291E6A0A1E58786000000000000000000000000650A2314EC0A" +
        "0AECEC00323CE6B581E8A18CE9A38EE59091000000000000000000000000660A0A0A0A" +
        "0A00F1F1001414E59D87E8A1A1E4BABAE5A3B0000000000000000000000000CA62";
    private const string FreeArcState =
        "5A0062002B4A0101010201640305010A02030904010105010A060A00000000000000000000" +
        "0718E68891E79A84E99FB3E695882031000000000000000000000824640A000000000000" +
        "00000000E68891E79A84E99FB3E695882031000000000000000000001885";
    private const string FreeBuds7State = "5A0017002B4A01010102010503070205090D0E0F1004010508003E7A";

    public static void Run()
    {
        CheckCapturedReadbacks();
        CheckCapturedWrites();
        CheckWriteConfirmation();
        CheckRouteAndValueBoundaries();
    }

    private static void CheckCapturedReadbacks()
    {
        var freeBuds6i = Profile(HeadsetModel.FreeBuds6i);
        var state = HuaweiEqualizer.Parse(Decode(FreeBuds6iState), freeBuds6i);
        Require(state is { Supported: true, BandCount: 10, SelectedId: 0x64, SelectedName: "全频校准" },
            "Captured FreeBuds 6i state");
        Require(state!.SelectedGains!.SequenceEqual(new[] { 40, 20, 10, 0, 10, 35, 25, 0, 10, 20 }),
            "FreeBuds 6i selected gains");
        Require(state.BuiltInIds.SequenceEqual(new byte[] { 0x01, 0x02, 0x03, 0x09 }), "FreeBuds 6i built-in IDs");
        Require(state.CustomPresets.Select(preset => preset.Id).SequenceEqual(new byte[] { 0x64, 0x65, 0x66 })
            && state.CustomPresets[1].Name == "流行风向" && state.CustomPresets[2].Name == "均衡人声",
            "FreeBuds 6i stored slots and names");
        Require(state.CustomPresets[1].Gains[2] == -20, "Negative custom gain sign");

        var freeArc = HuaweiEqualizer.Parse(Decode(FreeArcState), Profile(HeadsetModel.FreeArc));
        Require(freeArc is { Supported: true, BandCount: 10, SelectedId: 0x64, SelectedName: "我的音效 1" }
            && freeArc.SelectedGains!.All(gain => gain == 0) && freeArc.CustomPresets.Count == 1,
            "Captured FreeArc slot and flat curve");

        var freeBuds7 = HuaweiEqualizer.Parse(Decode(FreeBuds7State), Profile(HeadsetModel.FreeBuds7));
        Require(freeBuds7 is { Supported: true, SelectedId: 0x05, BandCount: null, SelectedGains: null }
            && freeBuds7.BuiltInIds.SequenceEqual(new byte[] { 2, 5, 9, 13, 14, 15, 16 }),
            "Captured FreeBuds 7 partial built-in state");
        Require(HuaweiEqualizer.CanEditCustom(Profile(HeadsetModel.FreeBuds7), freeBuds7),
            "A verified ten-band write route with affirmative support can edit from a built-in preset");

        var presetOnly = HuaweiWireFormat.Encode(0x2B, 0x4A, 0x02, 0x01, 0x05);
        var partial = HuaweiEqualizer.Parse(Decode(presetOnly), freeBuds6i);
        Require(partial is { Supported: null, BandCount: null } && !HuaweiEqualizer.CanEditCustom(freeBuds6i, partial),
            "A preset-only report does not establish EQ capability");
        var otherBandCount = HuaweiWireFormat.Encode(0x2B, 0x4A, 0x01, 0x01, 0x01, 0x02, 0x01, 0x01, 0x05, 0x01, 0x0B);
        var otherBands = HuaweiEqualizer.Parse(Decode(otherBandCount), freeBuds6i);
        Require(otherBands is { Supported: true, BandCount: 11 } && !HuaweiEqualizer.CanEditCustom(freeBuds6i, otherBands),
            "An explicit non-ten-band format is read-only");

        var disabled = HuaweiWireFormat.Encode(0x2B, 0x4A, 0x01, 0x01, 0x00, 0x02, 0x01, 0x01);
        Require(HuaweiEqualizer.Parse(Decode(disabled), freeBuds6i)?.Supported == false,
            "Preserve explicit unsupported status");
        var malformed = HuaweiWireFormat.Encode(0x2B, 0x4A, 0x02, 0x01, 0x64, 0x08, 0x01, 0x64);
        Require(HuaweiEqualizer.Parse(Decode(malformed), freeBuds6i) is null, "Reject truncated custom record");

        var invalidCrc = Convert.FromHexString(FreeBuds6iState);
        invalidCrc[^1] ^= 1;
        Require(new HuaweiFrameDecoder().Push(invalidCrc).Count == 0, "Reject corrupt captured EQ frame");
    }

    private static void CheckWriteConfirmation()
    {
        var freeBuds6i = Profile(HeadsetModel.FreeBuds6i);
        var original = Decode(FreeBuds6iState);
        var gains = new[] { 40, 20, 10, 0, 10, 35, 25, 0, 10, 20 };
        Require(HuaweiEqualizer.ConfirmsCustom(original, freeBuds6i, gains, " 全频校准 "),
            "Confirm matching slot, gains and trimmed name");
        Require(!HuaweiEqualizer.ConfirmsCustom(original, freeBuds6i, gains, "新名称"),
            "Reject stale readback with unchanged gains after a name-only edit");

        byte[] gainBytes = gains.Select(gain => (byte)gain).ToArray();
        var unnamed = HuaweiWireFormat.Encode(0x2B, 0x4A,
            [0x01, 0x01, 0x01, 0x02, 0x01, 0x64, 0x06, 0x0A, .. gainBytes]);
        var state = HuaweiEqualizer.Parse(Decode(unnamed), freeBuds6i);
        Require(state is { Supported: true, BandCount: 10, SelectedName: null } && HuaweiEqualizer.CanEditCustom(freeBuds6i, state),
            "Valid curve length establishes the band count");
        Require(!HuaweiEqualizer.ConfirmsCustom(Decode(unnamed), freeBuds6i, gains, "自定义音效"),
            "An absent name is not a confirmed default name");

        var customOnly = HuaweiWireFormat.Encode(0x2B, 0x4A,
            [0x01, 0x01, 0x01, 0x02, 0x01, 0x64, 0x08, 36, 0x64, 0x0A,
                .. gainBytes, (byte)'E', (byte)'Q', .. new byte[22]]);
        Require(HuaweiEqualizer.ConfirmsCustom(Decode(customOnly), freeBuds6i, gains, "EQ"),
            "The selected custom record establishes name, gains and band count");
    }

    private static void CheckCapturedWrites()
    {
        var freeBuds6i = Profile(HeadsetModel.FreeBuds6i);
        Require(Convert.ToHexString(HuaweiEqualizer.Query(freeBuds6i)) == "5A0005002B4A02008C46", "EQ query bytes");
        var gains = new[] { 40, 20, 10, 0, 10, 35, 25, 0, 10, 20 };
        Require(Convert.ToHexString(HuaweiEqualizer.SetCustom(freeBuds6i, gains, "全频校准"))
            == "5A0026002B4901016402010A050101030A28140A000A2319000A14040CE585A8E9A291E6A0A1E58786A9A9",
            "Captured FreeBuds 6i save and apply command");
        Require(Convert.ToHexString(HuaweiEqualizer.SetCustom(Profile(HeadsetModel.FreeBuds7i), new int[10], "我的音效 1"))
            == "5A0028002B4901016402010A050100030A00000000000000000000040EE68891E79A84E99FB3E695882031DB88",
            "Captured FreeBuds 7i custom operation byte");
        Require(Convert.ToHexString(HuaweiEqualizer.SetCustom(Profile(HeadsetModel.FreeArc), new int[10], "我的音效 1"))
            == "5A0028002B4901016402010A050101030A00000000000000000000040EE68891E79A84E99FB3E6958820312B6E",
            "Captured FreeArc custom operation byte");
    }

    private static void CheckRouteAndValueBoundaries()
    {
        foreach (var model in new[] { HeadsetModel.FreeBuds6, HeadsetModel.FreeClip2, HeadsetModel.FreeClip2Collector })
        {
            var profile = Profile(model);
            Require(HuaweiEqualizer.SupportsRead(profile) && !HuaweiEqualizer.SupportsWrite(profile),
                $"{model} must stay read-only");
            ExpectFailure<NotSupportedException>(() => HuaweiEqualizer.SetCustom(profile, new int[10], "EQ"),
                $"{model} direct write");
        }

        var freeBuds3 = Profile(HeadsetModel.FreeBuds3);
        ExpectFailure<NotSupportedException>(() => HuaweiEqualizer.Query(freeBuds3), "Unsupported EQ query");
        var freeBuds7 = Profile(HeadsetModel.FreeBuds7);
        var freeBuds6i = Profile(HeadsetModel.FreeBuds6i);
        Require(HuaweiEqualizer.UnitsPerDecibel(freeBuds7) == 5 && HuaweiEqualizer.UnitsPerDecibel(freeBuds6i) == 10,
            "Route-specific decibel units");
        var gains = new int[10];
        gains[0] = -30;
        gains[1] = 30;
        var name = new string('音', 10) + "AB";
        var boundary = Decode(HuaweiEqualizer.SetCustom(freeBuds7, gains, name));
        Require(boundary.GetField(0x01)[0] == 0x64 && boundary.GetField(0x05)[0] == 1
            && boundary.GetField(0x03)[0] == unchecked((byte)-30) && boundary.GetField(0x03)[1] == 30
            && boundary.GetField(0x04).Length == 32, "Fixed slot, signed ±6 dB and 32-byte UTF-8 boundary");

        gains[0] = 31;
        ExpectFailure<ArgumentOutOfRangeException>(() => HuaweiEqualizer.SetCustom(freeBuds7, gains, "EQ"), "FreeBuds 7 gain limit");
        gains[0] = -61;
        ExpectFailure<ArgumentOutOfRangeException>(() => HuaweiEqualizer.SetCustom(freeBuds6i, gains, "EQ"), "Standard gain limit");
        ExpectFailure<ArgumentOutOfRangeException>(() => HuaweiEqualizer.SetCustom(freeBuds6i, new int[9], "EQ"), "Nine bands");
        ExpectFailure<ArgumentException>(() => HuaweiEqualizer.SetCustom(freeBuds6i, new int[10], name + "C"), "33 UTF-8 bytes");
        ExpectFailure<ArgumentException>(() => HuaweiEqualizer.SetCustom(freeBuds6i, new int[10], "  "), "Empty trimmed name");
        ExpectFailure<ArgumentException>(() => HuaweiEqualizer.SetCustom(freeBuds6i, new int[10], "EQ\0"), "Embedded name terminator");
    }

    private static HeadsetProfile Profile(HeadsetModel model) =>
        HeadsetProfiles.All.Single(profile => profile.Model == model);

    private static HuaweiFrame Decode(string packet) => Decode(Convert.FromHexString(packet));

    private static HuaweiFrame Decode(byte[] packet) => new HuaweiFrameDecoder().Push(packet).Single();

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void ExpectFailure<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
