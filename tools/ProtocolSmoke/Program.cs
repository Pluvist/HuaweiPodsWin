using HuaweiPods.Artwork;
using HuaweiPods.Protocol;

// Fixtures from Nshpiter/HuaweiPods protocol tests at dc5b9d14e9030be95b178699d674d1a3217e1e6d.
const string batteryResponse = "5A00180001080101600203606413030301010004020A140502010126EC";
const string deviceInfoResponse = "5A0025000107020201630A0F42544654303032352D3030303136330F08425446543030323519010242B3";
const string ancResponse = "5A0007002B2A010201013621";

CheckStreamFraming();
CheckIdentification();
await CheckArtworkFallback();
CheckBattery();
CheckNoiseControl();
CheckSettings();
CheckSoundPresets();
GestureAndPairedSmoke.Run();
EqualizerChecks.Run();
Console.WriteLine("HuaweiPods protocol smoke checks passed.");

void CheckStreamFraming()
{
    var packet = Convert.FromHexString(batteryResponse);
    for (var split = 1; split < packet.Length; split++)
    {
        var decoder = new HuaweiFrameDecoder();
        Equal(0, decoder.Push(packet.AsSpan(0, split)).Count, $"Incomplete frame at byte {split}");
        var frame = decoder.Push(packet.AsSpan(split)).Single();
        Equal((byte)0x08, frame.Command, $"Complete frame at byte {split}");
    }

    var corrupt = batteryResponse[..^2] + "ED";
    var combined = new HuaweiFrameDecoder().Push(Convert.FromHexString("A500" + corrupt + batteryResponse + ancResponse));
    Equal(2, combined.Count, "Discard leading bytes and a corrupted frame");
    Equal((byte)0x08, combined[0].Command, "First frame in one read");
    Equal((byte)0x2A, combined[1].Command, "Second frame in one read");

    var decoderWithNoise = new HuaweiFrameDecoder();
    Equal(0, decoderWithNoise.Push(Convert.FromHexString("5A00FFFF")).Count, "Retain incomplete header for later bytes");
    Equal((byte)0x08, decoderWithNoise.Push(packet).Single().Command, "Recover after a bogus length");

    var duplicate = HuaweiWireFormat.Encode(0x01, 0x08, 0x02, 0x01, 0x10, 0x02, 0x01, 0x20);
    Equal(0, new HuaweiFrameDecoder().Push(duplicate).Count, "Reject ambiguous duplicate fields even with a valid checksum");
}

void CheckIdentification()
{
    var identity = HuaweiProtocol.ParseDeviceInfo(Decode(deviceInfoResponse))
        ?? throw new InvalidOperationException("Missing FreeBuds 7i identity.");
    Equal("000163", identity.ModelId, "FreeBuds 7i model ID");
    Equal("02", identity.SubModelId, "FreeBuds 7i color ID");
    Equal(HeadsetModel.FreeBuds7i, HeadsetProfiles.FindByModelId(identity.ModelId)?.Model, "Model ID routing");
    Equal(HeadsetModel.FreeClip2Collector, HeadsetProfiles.FindByModelId("000173")?.Model, "FreeClip 2 Collector routing");
    Equal(HeadsetModel.FreeBudsPro3, HeadsetProfiles.FindByName("huawei freebuds pro 3")?.Model, "Name routing");
    Equal<HeadsetProfile?>(null, HeadsetProfiles.FindByName("FreeBuds 70"), "Reject a lookalike name");
    Equal("5A00210001070100020003000400050006000700080009000A000B000C000F0018001900DFF3",
        Convert.ToHexString(HuaweiProtocol.DeviceInfoQuery()), "DeviceInfo query bytes");

    var modelFields = Convert.FromHexString(deviceInfoResponse)[6..^5];
    var expectedPartialIdentity = new DeviceIdentity("000163", null);
    Equal(expectedPartialIdentity, HuaweiProtocol.ParseDeviceInfo(DecodeGenerated(0x01, 0x07, modelFields)),
        "DeviceInfo identifies the model when the color field is omitted");
    Equal(expectedPartialIdentity, HuaweiProtocol.ParseDeviceInfo(DecodeGenerated(0x01, 0x07, [.. modelFields, 0x19, 0x00])),
        "DeviceInfo accepts an empty color field as an unknown color");
    Equal<DeviceIdentity?>(null, HuaweiProtocol.ParseDeviceInfo(DecodeGenerated(0x01, 0x07, [.. modelFields, 0x19, 0x02, 0x01, 0x02])),
        "DeviceInfo rejects a malformed color field");
    modelFields[3] ^= 1;
    Equal<DeviceIdentity?>(null, HuaweiProtocol.ParseDeviceInfo(DecodeGenerated(0x01, 0x07, modelFields)),
        "DeviceInfo still rejects a mismatching model code when color is absent");
}

async Task CheckArtworkFallback()
{
    Equal("000163_02_hero.png", Path.GetFileName(await OfficialArtwork.ResolveAsync(new DeviceIdentity("000163", "02"), "000163")),
        "Use an exact known color");
    Equal("000163_01_hero.png", Path.GetFileName(await OfficialArtwork.ResolveAsync(new DeviceIdentity("000163", null), null)),
        "Use true white ahead of silver-white when DeviceInfo has no color");
    Equal("000163_01_hero.png", Path.GetFileName(await OfficialArtwork.ResolveAsync(null, "000163")),
        "Use white when only the model profile is known");
    Equal("000173_00_hero.png", Path.GetFileName(await OfficialArtwork.ResolveAsync(new DeviceIdentity("000173", "FF"), null)),
        "Use the first variant when the reported color is unavailable and no white exists");
    Equal<string?>(null, await OfficialArtwork.ResolveAsync(new DeviceIdentity("000170", null), "000163"),
        "Do not use imagery of a different model when the protocol reports a model ID");
}

void CheckBattery()
{
    Equal("5A0009000108010002000300FBB9", Convert.ToHexString(HuaweiProtocol.BatteryQuery()), "Battery query bytes");
    var battery = HuaweiProtocol.ParseBattery(Decode(batteryResponse), Profile(HeadsetModel.FreeBudsPro3))
        ?? throw new InvalidOperationException("Missing FreeBuds Pro 3 battery state.");
    Equal(96, battery.Left?.Percentage, "Left earbud charge level");
    Equal(100, battery.Right?.Percentage, "Right earbud charge level");
    Equal(19, battery.Case?.Percentage, "Case charge level");
    Equal(true, battery.Left?.IsCharging, "Left earbud charging");
    Equal(false, battery.Case?.IsCharging, "Case charging");

    var pro5Frame = Decode("5A001B0001080101640203646455030300000004020A140502000106010A4D9B");
    var pro5Battery = HuaweiProtocol.ParseBattery(pro5Frame, Profile(HeadsetModel.FreeBudsPro5));
    Equal(100, pro5Battery?.Right?.Percentage, "FreeBuds Pro 5 retains in-case percentage");
    Equal(false, pro5Battery?.Right?.IsConnected, "FreeBuds Pro 5 reports right earbud in case");
    var sameReport = HuaweiProtocol.ParseBattery(pro5Frame, Profile(HeadsetModel.FreeBuds6i));
    Equal(true, sameReport?.Right?.IsConnected, "Only Pro 5 interprets TLV 05 as per-earbud availability");

    var zeroEarbud = DecodeGenerated(0x01, 0x08, 0x02, 0x03, 0x00, 0x64, 0x00, 0x05, 0x02, 0x00, 0x00);
    var legacyZero = HuaweiProtocol.ParseBattery(zeroEarbud, Profile(HeadsetModel.FreeBuds6i));
    Equal(false, legacyZero?.Left?.IsConnected, "Zero-percent earbud is a legacy unavailable sentinel");
    Equal(true, legacyZero?.Case?.IsConnected, "Case may legitimately be at zero percent");
    var pro5Zero = HuaweiProtocol.ParseBattery(zeroEarbud, Profile(HeadsetModel.FreeBudsPro5));
    Equal(true, pro5Zero?.Left?.IsConnected, "Pro 5 out-of-case flag takes priority over the zero sentinel");

    var partial = HuaweiProtocol.ParseBattery(DecodeGenerated(0x01, 0x27, 0x02, 0x02, 0x52, 0xFF));
    Equal(82, partial?.Left?.Percentage, "Keep the present earbud in a partial battery report");
    Equal<BatteryPart?>(null, partial?.Right, "An invalid battery percentage is unavailable");
    Equal<BatteryPart?>(null, partial?.Case, "No fabricated level for an omitted case");

    var eyewearFrame = Decode("5A0014000108010144020346440003030000000402140A392E");
    var eyewearBattery = HuaweiProtocol.ParseBattery(eyewearFrame, Profile(HeadsetModel.Eyewear));
    Equal(70, eyewearBattery?.Left?.Percentage, "Eyewear temple charge level");
    Equal<BatteryPart?>(null, eyewearBattery?.Case, "Eyewear drops placeholder case");
    Equal(false, Profile(HeadsetModel.FreeBuds3).SupportsBattery, "FreeBuds 3 uses upstream HFP battery reporting");
}

void CheckNoiseControl()
{
    var freeBuds3 = Profile(HeadsetModel.FreeBuds3);
    var freeBuds6i = Profile(HeadsetModel.FreeBuds6i);
    var pro3 = Profile(HeadsetModel.FreeBudsPro3);
    var pro5 = Profile(HeadsetModel.FreeBudsPro5);
    var freeClip = Profile(HeadsetModel.FreeClip);

    Equal("5A0006002B040101017800", Convert.ToHexString(HuaweiProtocol.SetAnc(freeBuds3, NoiseControlMode.NoiseCancellation)), "FreeBuds 3 ANC on bytes");
    Equal("5A0006002B08010108A61B", Convert.ToHexString(HuaweiProtocol.SetAncDirection(freeBuds3, 8)), "FreeBuds 3 direction bytes");
    Equal("5A0007002B04010201FFFFEC", Convert.ToHexString(HuaweiProtocol.SetAnc(pro3, NoiseControlMode.NoiseCancellation)), "Enter ANC with FF");
    Equal("5A0007002B04010202FFAABF", Convert.ToHexString(HuaweiProtocol.SetAnc(freeBuds6i, NoiseControlMode.Transparency)), "Enter transparency with FF");
    Equal("5A0007002B0401020204F4CB", Convert.ToHexString(HuaweiProtocol.SetAnc(pro5, NoiseControlMode.Transparency, 0x04)), "Pro 5 adaptive transparency bytes");
    Equal("5A0005002B2A0100427E", Convert.ToHexString(HuaweiProtocol.AncQuery()), "ANC query bytes");

    var state = HuaweiProtocol.ParseAnc(Decode(ancResponse), pro3);
    Equal(NoiseControlMode.NoiseCancellation, state?.Mode, "Read back ANC mode");
    Equal((byte)0x01, state?.Level, "Read back ANC level");
    Equal("智慧动态", pro3.AncLevels.Single(option => option.Value == 0x01).Label, "Pro 3 ANC level mapping");
    Equal("轻度", freeBuds6i.AncLevels.Single(option => option.Value == 0x01).Label, "6i ANC level mapping");
    Equal(false, freeClip.SupportsAnc, "FreeClip has no ANC");
    Equal<AncState?>(null, HuaweiProtocol.ParseAnc(Decode(ancResponse), freeClip), "Ignore ANC readback for FreeClip");
    Equal<AncState?>(null, HuaweiProtocol.ParseAnc(DecodeGenerated(0x2B, 0x2A, 0x01, 0x02, 0xFF, 0x01), pro3),
        "A transition token does not confirm a discrete ANC level");

    var transparency = Decode("5A0007002B2A010202025311");
    Equal((byte)0xFF, HuaweiProtocol.ParseAnc(transparency, pro3)?.Level, "Pro 3 maps reported standard transparency to its setting");
    Equal((byte)0x02, HuaweiProtocol.ParseAnc(transparency, freeBuds6i)?.Level, "6i retains the concrete standard transparency setting");
}

void CheckSettings()
{
    var freeBuds6 = Profile(HeadsetModel.FreeBuds6);
    Equal("5A0006002B10010101A956", Convert.ToHexString(HuaweiProtocol.SetWearDetection(freeBuds6, true)), "Wear detection on bytes");
    Equal("5A0005002B110100772A", Convert.ToHexString(HuaweiProtocol.WearDetectionQuery()), "Wear detection query bytes");
    Equal(true, HuaweiProtocol.ParseWearDetection(Decode("5A0006002B11010101DFE2")), "Read back wear detection");
    Equal<bool?>(null, HuaweiProtocol.ParseWearDetection(Decode("5A0009002B107F04000186A0729D")), "A write ACK is not a wear detection readback");
    Equal("5A0006002B6C010101A411", Convert.ToHexString(HuaweiProtocol.SetLowLatency(freeBuds6, true)), "Low latency on bytes");
    Equal("5A0005002B6C0200B820", Convert.ToHexString(HuaweiProtocol.LowLatencyQuery(freeBuds6)), "FreeBuds 6 low latency query bytes");
    Equal(false, HuaweiProtocol.ParseLowLatency(Decode("5A0006002B6C020100ED60"), freeBuds6), "Read back FreeBuds 6 low latency");
    Equal<bool?>(null, HuaweiProtocol.ParseLowLatency(DecodeGenerated(0x2B, 0x6C, 0x03, 0x01, 0x01), freeBuds6),
        "Low latency capability report does not confirm the setting");
    Equal<bool?>(null, HuaweiProtocol.ParseLowLatency(Decode("5A0006002B6C020100ED60"), Profile(HeadsetModel.FreeBuds5)),
        "FreeBuds 5 only has a verified low latency setter");
}

void CheckSoundPresets()
{
    var freeBuds5 = Profile(HeadsetModel.FreeBuds5);
    var pro5 = Profile(HeadsetModel.FreeBudsPro5);
    Equal("5A0005002B4A02008C46", Convert.ToHexString(HuaweiProtocol.SoundPresetQuery(freeBuds5)), "Sound preset query bytes");
    Equal("5A0006002B490101012F1A", Convert.ToHexString(HuaweiProtocol.SetSoundPreset(freeBuds5, 0x01)), "FreeBuds 5 default preset bytes");
    Equal("5A001D002B490101C902010A050101030AFB141E0A0000E7F60A000403323031C367",
        Convert.ToHexString(HuaweiProtocol.SetSoundPreset(pro5, 0xC9)), "FreeBuds Pro 5 classical curve bytes");
    var response = Decode("5A0014002B4A010101020102030401020309040101080071B2");
    Equal((byte)0x02, HuaweiProtocol.ParseSoundPreset(response, freeBuds5), "Read back FreeBuds 5 bass preset");
    Equal<byte?>(null, HuaweiProtocol.ParseSoundPreset(response, Profile(HeadsetModel.FreeBuds3)), "Ignore sound presets for unsupported models");
}

static HuaweiFrame Decode(string capture) => new HuaweiFrameDecoder().Push(Convert.FromHexString(capture)).Single();

static HuaweiFrame DecodeGenerated(byte service, byte command, params byte[] fields) =>
    new HuaweiFrameDecoder().Push(HuaweiWireFormat.Encode(service, command, fields)).Single();

static HeadsetProfile Profile(HeadsetModel model) => HeadsetProfiles.All.Single(profile => profile.Model == model);

static void Equal<T>(T expected, T actual, string check)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{check}: expected '{expected}', received '{actual}'.");
}
