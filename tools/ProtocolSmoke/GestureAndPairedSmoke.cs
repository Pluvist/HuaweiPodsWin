using HuaweiPods.Protocol;

internal static class GestureAndPairedSmoke
{
    internal static void Run()
    {
        var freeBuds3 = Profile(HeadsetModel.FreeBuds3);
        var legacy = Find(freeBuds3, "double-tap-left");
        Equal(false, legacy.SupportsReadback, "FreeBuds 3 double tap is write-only");
        Equal(0, HuaweiGestures.GetQueries(freeBuds3).Count, "No unverified FreeBuds 3 queries");
        Equal("5A000600011F0101046307", Hex(HuaweiGestures.Set(freeBuds3, legacy, 0x04)), "Legacy next-track code");

        var freeBuds4e = Profile(HeadsetModel.FreeBuds4E);
        Equal("5A0006002B16010103AE8D",
            Hex(HuaweiGestures.Set(freeBuds4e, Find(freeBuds4e, "hold-left"), 0x03)), "FreeBuds 4E long-press noise control");
        Equal(new GestureState("hold-right", 0x0E),
            HuaweiGestures.Parse(Frame("5A0018002B1701010302010E030D000102030405060708090A0E0FC50C"), freeBuds4e)[1],
            "FreeBuds 4E reports right long-press song recognition");

        var freeBuds7 = Profile(HeadsetModel.FreeBuds7);
        Equal("5A000900011F010101020101FC28",
            Hex(HuaweiGestures.Set(freeBuds7, Find(freeBuds7, "double-tap"), 1)), "FreeBuds 7 shared double-tap switch");
        var pinch = Find(freeBuds7, "pinch-play");
        Equal("5A000F002B920101000201020301FF0401FF47FB",
            Hex(HuaweiGestures.Set(freeBuds7, pinch, 0)), "FreeBuds 7 pinch disabled");
        Equal(new GestureState(pinch.Id, 1), HuaweiGestures.Parse(
            Frame("5A0017002B9301010002010203010204010205060001020304FF3C67"), freeBuds7).Single(),
            "FreeBuds 7 pinch slot and context");

        var freeBuds7i = Profile(HeadsetModel.FreeBuds7i);
        Equal("5A0009002B1E0101000202009D9B",
            Hex(HuaweiGestures.Set(freeBuds7i, Find(freeBuds7i, "swipe-volume"), 1)), "FreeBuds 7i shared swipe volume write");
        Equal("5A000700012001000200E8975A00070001260100020025125A0007002B1F01000200328A",
            Hex(HuaweiGestures.GetQueries(freeBuds7i).SelectMany(query => query).ToArray()), "FreeBuds 7i query sequence");

        var collector = Profile(HeadsetModel.FreeClip2Collector);
        Equal(new GestureState("swipe-right", 0xFF), HuaweiGestures.Parse(
            Frame("5A000E002B1F0101000201FF03030001FF7060"), collector)[1], "FreeClip 2 collector shares verified swipe readback");
        var eyewear = Profile(HeadsetModel.Eyewear2);
        Equal("5A0009002B1E020201020201DB3C",
            Hex(HuaweiGestures.Set(eyewear, Find(eyewear, "swipe-right"), 0x01)), "Eyewear 2 repeats the captured side-action fields");
        Equal(0, HuaweiGestures.GetQueries(eyewear).Count, "No unverified Eyewear 2 queries");
        Equal(0, HuaweiGestures.GetDefinitions(Profile(HeadsetModel.FreeBuds5)).Count, "No unsupported gesture controls");

        Equal("5A0008002B3101000D0100AEEE", Hex(HuaweiPairedDevices.Query(freeBuds7i)), "FreeBuds 7i paired-device query");
        Equal(new HeadsetPairedDevice("AA:BB:CC:DD:EE:FF", "PHONE-A", true),
            HuaweiPairedDevices.Parse(Frame("5A0029002B310201040301030406AABBCCDDEEFF050101060101070100080101090750484F4E452D410A01014729"),
                freeBuds7i)?.Single(), "FreeBuds 7i connected paired device");
        Equal(new HeadsetPairedDevice("11:22:33:44:55:66", "PHONE-B", false),
            HuaweiPairedDevices.Parse(Frame("5A0029002B310201040301020406112233445566050100060104070100080101090750484F4E452D420A01014F90"),
                freeBuds7i)?.Single(), "FreeBuds 7i disconnected paired device");
        Equal("5A000B002B3104061122334455660557",
            Hex(HuaweiPairedDevices.Remove(freeBuds7i, "11:22:33:44:55:66")), "FreeBuds 7i paired-device removal");
        Equal(null, HuaweiPairedDevices.Parse(Frame("5A000B002B3104061122334455660557"), freeBuds7i),
            "A removal echo is not a paired-device row");
        Equal(false, HuaweiPairedDevices.IsSupported(Profile(HeadsetModel.FreeBudsPro5)), "Multipoint toggle alone is not list support");
    }

    private static string Hex(byte[] packet) => Convert.ToHexString(packet);

    private static HuaweiFrame Frame(string capture) =>
        new HuaweiFrameDecoder().Push(Convert.FromHexString(capture)).Single();

    private static HeadsetProfile Profile(HeadsetModel model) => HeadsetProfiles.All.Single(profile => profile.Model == model);

    private static GestureDefinition Find(HeadsetProfile profile, string id) =>
        HuaweiGestures.GetDefinitions(profile).Single(definition => definition.Id == id);

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }
}
