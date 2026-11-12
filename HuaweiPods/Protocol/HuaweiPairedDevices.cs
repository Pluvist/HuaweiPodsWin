using System.Text;

namespace HuaweiPods.Protocol;

public sealed record HeadsetPairedDevice(string Address, string Name, bool IsConnected);

/// <summary>Reads and manages the devices remembered by a FreeBuds 7i headset.</summary>
public static class HuaweiPairedDevices
{
    private const byte Service = 0x2B;
    private const byte Command = 0x31;
    private const byte AddressField = 0x04;

    public static bool IsSupported(HeadsetProfile profile) => profile.Model == HeadsetModel.FreeBuds7i;

    public static byte[] Query(HeadsetProfile profile)
    {
        EnsureSupported(profile);
        return HuaweiWireFormat.Encode(Service, Command, 0x01, 0x00, 0x0D, 0x01, 0x00);
    }

    // The headset sends one row per frame. Merge non-null results by address within the query window.
    public static IReadOnlyList<HeadsetPairedDevice>? Parse(HuaweiFrame frame, HeadsetProfile profile)
    {
        if (!IsSupported(profile) || frame.Service != Service || frame.Command != Command)
            return null;

        var addressBytes = frame.GetField(AddressField);
        if (!IsDeviceAddress(addressBytes) || frame.GetField(0x05) is not [0x00 or 0x01])
            return null;

        string address = BitConverter.ToString(addressBytes.ToArray()).Replace('-', ':');
        string name = Encoding.UTF8.GetString(frame.GetField(0x09)).TrimEnd('\0').Trim();
        bool connected = frame.GetField(0x05) is [0x01];
        return [new HeadsetPairedDevice(address, name.Length == 0 ? address : name, connected)];
    }

    public static byte[] Remove(HeadsetProfile profile, string address)
    {
        EnsureSupported(profile);
        var parts = address.Split(':');
        if (parts.Length != 6 || parts.Any(part => part.Length != 2 || part.Any(character => !char.IsAsciiHexDigit(character))))
            throw new ArgumentException("需要完整的蓝牙设备地址。", nameof(address));

        byte[] bytes = Convert.FromHexString(string.Concat(parts));
        if (!IsDeviceAddress(bytes))
            throw new ArgumentException("蓝牙设备地址无效。", nameof(address));

        return HuaweiWireFormat.Encode(Service, Command, [AddressField, 0x06, .. bytes]);
    }

    private static bool IsDeviceAddress(ReadOnlySpan<byte> address) =>
        address.Length == 6 && address is not ([0, 0, 0, 0, 0, 0] or [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

    private static void EnsureSupported(HeadsetProfile profile)
    {
        if (!IsSupported(profile))
            throw new NotSupportedException("该型号不支持读取耳机的配对设备列表。");
    }
}
