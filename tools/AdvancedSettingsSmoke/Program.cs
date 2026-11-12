using HuaweiPods.Protocol;

// Independent golden packets/reports from the upstream controller tests, not generated expectations.
int checks = 0;
HeadsetProfile Profile(HeadsetModel model) => HeadsetProfiles.All.Single(profile => profile.Model == model);
DeviceSettingDefinition Setting(HeadsetModel model, string key) => AdvancedSettings.Get(Profile(model), key);
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
void Packet(byte[] actual, string expected) => Check(Convert.ToHexString(actual) == expected, $"Packet mismatch: {Convert.ToHexString(actual)} != {expected}");
void Reject<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { checks++; return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
byte? Read(DeviceSettingDefinition setting, byte[] bytes) => new HuaweiFrameDecoder().Push(bytes).Select(setting.Read).LastOrDefault(value => value.HasValue);
void Report(HeadsetModel model, string key, string hex, byte expected)
{
    var setting = Setting(model, key);
    var bytes = Convert.FromHexString(hex);
    Check(Read(setting, bytes) == expected, $"{model}/{key} report mismatch.");
    Check(Read(setting, bytes[..^1]) is null, $"{key} accepted truncated frame.");
    var corrupt = bytes.ToArray();
    corrupt[^1] ^= 1;
    Check(Read(setting, corrupt) is null, $"{key} accepted bad CRC.");
    Check(Read(setting, corrupt.Concat(bytes).ToArray()) == expected, $"{key} failed to recover after corrupt frame.");
}

var fixtures = new (HeadsetModel Model, string Key, string Query, string Off, string On)[]
{
    (HeadsetModel.FreeBuds5, "high_quality", "5A0005002BA30101F794", "5A0006002BA2010100A5CE", "5A0006002BA2010101B5EF"),
    (HeadsetModel.FreeBuds6, "adaptive_volume", "5A0008002BB401010202003619", "5A0009002BB401010202010013E1", "5A0009002BB401010202010103C0"),
    (HeadsetModel.FreeBuds6, "drop_reminder", "5A0008002BB40101070200DDE9", "5A0009002BB4010107020100AFA4", "5A0009002BB4010107020101BF85"),
    (HeadsetModel.FreeBuds6, "case_sound", "5A0007002BB1020003007FAB", "5A0006002BB101010025B5", "5A0006002BB10101013594"),
    (HeadsetModel.FreeBuds7, "head_motion", "5A0006002BB401010B289B", "5A0009002BB401010B020100E096", "5A0009002BB401010B020101F0B7"),
    (HeadsetModel.FreeBuds7, "single_ear_anc", "5A0008002BB40101050200B389", "5A0009002BB401010502010042CC", "5A0009002BB401010502010152ED"),
    (HeadsetModel.FreeBuds7, "auto_switch", "5A0005002B2F0100A98E", "5A0006002B2E01010037C4", "5A0006002B2E01010127E5"),
    (HeadsetModel.FreeBudsPro5, "voice_control", "5A0008002BB401010302000129", "5A0009002BB40101030201006555", "5A0009002BB40101030201017574"),
    (HeadsetModel.FreeBudsPro5, "case_sound", "5A0006002BB40101108BC1", "5A0006002BB101010025B5", "5A0006002BB10101013594"),
    (HeadsetModel.FreeClip2, "noisy_volume", "5A0005002B8801009182", "5A0006002B870101002EC5", "5A0006002B870101013EE4"),
    (HeadsetModel.FreeBudsSe4Anc, "wind_noise", "5A0008002BB401011D0200594B", "5A0009002BB401011D020100DCA8", "5A0009002BB401011D020101CC89")
};
foreach (var fixture in fixtures)
{
    var setting = Setting(fixture.Model, fixture.Key);
    Packet(setting.Query(), fixture.Query);
    Packet(setting.Write(0), fixture.Off);
    Packet(setting.Write(1), fixture.On);
    // A caller cannot corrupt subsequent query/write packets.
    var mutableQuery = setting.Query(); mutableQuery[0] = 0;
    var mutableWrite = setting.Write(1); mutableWrite[0] = 0;
    Packet(setting.Query(), fixture.Query);
    Packet(setting.Write(1), fixture.On);
}
Packet(Setting(HeadsetModel.FreeClip2, "head_nod").Write(0), "5A0009002BB401010B030100D7A6");
Packet(Setting(HeadsetModel.FreeClip2, "head_shake").Write(1), "5A0009002BB401010B0401014217");
Packet(Setting(HeadsetModel.FreeClip2, "head_shake").Write(2), "5A0009002BB401010B0401027274");
Packet(Setting(HeadsetModel.FreeBudsPro5, "ear_tip_material").Write(1), "5A0009002BB40101080201016B6B");
Packet(Setting(HeadsetModel.FreeBudsPro5, "ear_tip_material").Write(2), "5A0009002BB40101080201025B08");
foreach (var model in new[] { HeadsetModel.FreeBuds7, HeadsetModel.FreeBuds7i, HeadsetModel.FreeBudsPro5, HeadsetModel.FreeClip2, HeadsetModel.FreeClip2Collector })
{
    var spatial = Setting(model, "spatial_audio");
    Packet(spatial.Query(), "5A000A002BB4010118020003009B3F");
    Packet(spatial.Write(0), "5A0009002BB401011802010060ED");
    Packet(spatial.Write(1), "5A0009002BB401011802010170CC");
    Packet(spatial.Write(2), "5A0009002BB401011802010240AF");
    Report(model, "spatial_audio", "5A000C002BB4010118020100030100D72A", 0);
    Report(model, "spatial_audio", "5A000C002BB401011802010103010281DC", 1);
    Report(model, "spatial_audio", "5A000C002BB40101180201020301030A21", 2);
    Check(Read(spatial, spatial.Write(2)) is null, "Spatial write echo is not a complete state report.");
}
var scene = Setting(HeadsetModel.FreeClip2, "spatial_scene");
foreach (var (value, packet) in new (byte, string)[] {
    (0,"5A0009002BB401011803010057DD"), (1,"5A0009002BB401011803010147FC"),
    (2,"5A0009002BB4010118030102779F"), (3,"5A0009002BB401011803010367BE") })
    Packet(scene.Write(value), packet);

Report(HeadsetModel.FreeBuds5i, "high_quality", "5A0009002BA3010101020101B623", 1);
Report(HeadsetModel.FreeBuds5, "high_quality", "5A0009002BA3010101020100A602", 0);
Report(HeadsetModel.FreeBuds6, "adaptive_volume", "5A0009002BB401010202010103C0", 1);
Report(HeadsetModel.FreeBuds6, "drop_reminder", "5A0009002BB4010107020100AFA4", 0);
Report(HeadsetModel.FreeBuds6, "case_sound", "5A0009002BB1020101030101186B", 1);
Report(HeadsetModel.FreeBuds7, "case_sound", "5A0009002BB10201000301016EDF", 0);
Report(HeadsetModel.FreeBuds7, "head_motion", "5A000F002BB401010B020101030101040102C55B", 1);
Report(HeadsetModel.FreeBuds7i, "head_motion", "5A000F002BB401010B0201000301010401027D3A", 0);
Report(HeadsetModel.FreeBuds7, "single_ear_anc", "5A0009002BB401010502010042CC", 0);
Report(HeadsetModel.FreeBuds7, "auto_switch", "5A0006002B2F0101015151", 1);
Report(HeadsetModel.FreeBudsPro5, "voice_control", "5A000D002BB401010302020100030101B3FD", 1);
Report(HeadsetModel.FreeBudsPro5, "voice_control", "5A000D002BB40101030202000003010119AC", 0);
Report(HeadsetModel.FreeBudsPro5, "case_sound", "5A0014002BB4010110020C01010F000000000000000000DB1D", 1);
Report(HeadsetModel.FreeBudsPro5, "ear_tip_material", "5A0009002BB40101080201016B6B", 1);
Report(HeadsetModel.FreeClip2, "head_motion", "5A000F002BB401010B020101030101040102C55B", 1);
Report(HeadsetModel.FreeClip2, "head_nod", "5A000F002BB401010B020101030101040102C55B", 1);
Report(HeadsetModel.FreeClip2, "head_shake", "5A000F002BB401010B020101030101040102C55B", 2);
Report(HeadsetModel.FreeClip2, "spatial_scene", "5A000C002BB40101180201020301030A21", 3);
Report(HeadsetModel.FreeBudsSe4Anc, "wind_noise", "5A0009002BB401011D020101CC89", 1);

// Unknown values, ACKs, unrelated AAM features and partial/duplicate head actions must not become UI state.
var head = Setting(HeadsetModel.FreeClip2, "head_nod");
foreach (var fields in new byte[][] {
    [1,1,0x0B, 2,1,1, 3,1,0x7F, 4,1,2],
    [1,1,0x0B, 2,1,0x7F, 3,1,1, 4,1,2],
    [1,1,0x0B, 2,1,1, 3,1,1],
    [1,1,0x0B, 2,1,1, 3,1,1, 3,1,2],
    [1,1,0x18, 2,1,1, 3,1,1, 4,1,2] })
    Check(Read(head, HuaweiWireFormat.Encode(0x2B,0xB4,fields)) is null, "Invalid head-motion state accepted.");
Check(Read(Setting(HeadsetModel.FreeClip2,"noisy_volume"), HuaweiWireFormat.Encode(0x2B,0x88,1,1,1)) == 1, "Noisy volume state field mismatch.");
Check(Read(Setting(HeadsetModel.FreeClip2,"noisy_volume"), HuaweiWireFormat.Encode(0x2B,0x88,1,1,2)) is null, "Unknown boolean became enabled.");
Check(Read(Setting(HeadsetModel.FreeBudsSe4Anc,"wind_noise"), Convert.FromHexString("5A0009002BB401010B020101F0B7")) is null, "Other AAM feature became wind-noise state.");

var expectedCounts = new Dictionary<HeadsetModel,int> {
    [HeadsetModel.FreeBuds5]=1, [HeadsetModel.FreeBuds5i]=1, [HeadsetModel.FreeBuds6]=3,
    [HeadsetModel.FreeBuds7]=8, [HeadsetModel.FreeBuds7i]=3, [HeadsetModel.FreeBudsPro5]=8,
    [HeadsetModel.FreeClip2]=10, [HeadsetModel.FreeClip2Collector]=10, [HeadsetModel.FreeBudsSe4Anc]=1 };
var allKeys = HeadsetProfiles.All.SelectMany(AdvancedSettings.For).Select(setting => setting.Key).Distinct().ToArray();
foreach (var profile in HeadsetProfiles.All)
{
    var definitions = AdvancedSettings.For(profile);
    Check(definitions.Count == expectedCounts.GetValueOrDefault(profile.Model), $"Wrong capability count for {profile.Model}.");
    Check(definitions.Select(setting=>setting.Key).Distinct().Count()==definitions.Count, "Duplicate control key.");
    foreach (var setting in definitions)
    {
        Reject<ArgumentOutOfRangeException>(()=>setting.Write(0xFF));
        Check(Read(setting, Convert.FromHexString("5A0009002B497F04000186A0B3C6")) is null, "Generic ACK accepted as settings state.");
        Check(Read(setting, HuaweiWireFormat.Encode(0x2B,0xB4,1,1,0x02,2,1,1,0x7F,4,0,1,0x86,0xA0)) is null, "ACK plus fields accepted.");
        foreach(var related in setting.RefreshKeys ?? []) Check(definitions.Any(item=>item.Key==related), "Refresh target unavailable on model.");
    }
    foreach(var key in allKeys.Except(definitions.Select(setting=>setting.Key)))
        Reject<NotSupportedException>(()=>AdvancedSettings.Write(profile,key,0));
}
Reject<NotSupportedException>(()=>AdvancedSettings.Write(new HeadsetProfile((HeadsetModel)999,"Unknown"),"high_quality",1));
Console.WriteLine($"PASS: {checks} checks; captured writes/queries/readback, CRC/truncation/ACK rejection, model-specific capabilities and invalid-write guards.");
