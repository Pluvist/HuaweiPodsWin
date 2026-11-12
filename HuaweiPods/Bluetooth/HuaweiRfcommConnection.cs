using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace HuaweiPods.Bluetooth;

public sealed record PairedBluetoothDevice(string Id, string Name, bool IsConnected, string? Address = null);

public sealed class HuaweiRfcommConnection : IDisposable
{
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";
    private const string DeviceAddressProperty = "System.Devices.Aep.DeviceAddress";
    private const string BluetoothBaseUuidSuffix = "-0000-1000-8000-00805f9b34fb";
    private static readonly Guid SerialPortUuid = new("00001101-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SocketConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly BluetoothDevice _device;
    private readonly RfcommDeviceService _service;
    private readonly StreamSocket _socket;
    private readonly Stream _input;
    private readonly Stream _output;
    private int _disposed;

    public bool UsesSerialPort { get; }

    private HuaweiRfcommConnection(BluetoothDevice device, RfcommDeviceService service, StreamSocket socket)
    {
        _device = device;
        _service = service;
        _socket = socket;
        UsesSerialPort = service.ServiceId.Uuid == SerialPortUuid;
        _input = socket.InputStream.AsStreamForRead(bufferSize: 0);
        _output = socket.OutputStream.AsStreamForWrite(bufferSize: 0);
    }

    public static Task<IReadOnlyList<PairedBluetoothDevice>> GetPairedDevicesAsync(
        CancellationToken cancellationToken = default) =>
        GetDevicesAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), cancellationToken);

    public static async Task<IReadOnlyList<PairedBluetoothDevice>> GetConnectedHuaweiDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = await GetDevicesAsync(
            BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
            cancellationToken);
        return devices
            .Where(device => device.IsConnected
                && device.Name?.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
    }

    private static async Task<IReadOnlyList<PairedBluetoothDevice>> GetDevicesAsync(
        string selector, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = await DeviceInformation.FindAllAsync(
            selector,
            new[] { IsConnectedProperty, DeviceAddressProperty },
            DeviceInformationKind.AssociationEndpoint).AsTask(cancellationToken);

        return devices
            .Select(device =>
            {
                device.Properties.TryGetValue(IsConnectedProperty, out var connected);
                device.Properties.TryGetValue(DeviceAddressProperty, out var address);
                return new PairedBluetoothDevice(
                    device.Id, device.Name, connected is true, address as string);
            })
            .OrderByDescending(device => device.IsConnected)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static async Task<HuaweiRfcommConnection> ConnectAsync(
        PairedBluetoothDevice device, CancellationToken cancellationToken = default,
        bool allowVendorServices = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();

        // WinRT can request device access here. The caller starts this operation on the UI thread.
        var bluetoothDevice = await BluetoothDevice.FromIdAsync(device.Id).AsTask(cancellationToken)
            ?? throw new IOException("无法访问此设备。请在 Windows 蓝牙设置中检查配对状态。");

        var connected = false;
        try
        {
            IOException? lastFailure = null;
            foreach (var cacheMode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
            {
                RfcommDeviceServicesResult result;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(DiscoveryTimeout);
                    var discovery = allowVendorServices
                        ? bluetoothDevice.GetRfcommServicesAsync(cacheMode)
                        : bluetoothDevice.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(SerialPortUuid), cacheMode);
                    result = await discovery.AsTask(deadline.Token).WaitAsync(deadline.Token);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    lastFailure ??= new IOException(
                        "无法查询耳机的蓝牙控制服务。请确认 Windows 蓝牙已开启且耳机在附近。", error);
                    continue;
                }

                var services = result.Services.ToArray();
                RfcommDeviceService? activeService = null;
                try
                {
                    if (result.Error != BluetoothError.Success)
                    {
                        lastFailure ??= new IOException($"Windows 无法查询蓝牙控制服务（{result.Error}）。");
                        continue;
                    }

                    var candidates = services
                        .Where(service => IsControlCandidate(service.ServiceId.Uuid, allowVendorServices))
                        .OrderByDescending(service => service.ServiceId.Uuid == SerialPortUuid);
                    foreach (var service in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var connection = await ConnectServiceAsync(bluetoothDevice, service, cancellationToken);
                            activeService = service;
                            connected = true;
                            return connection;
                        }
                        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                        {
                            lastFailure = new IOException(
                                "蓝牙 RFCOMM 控制连接失败。请确认耳机在附近，并关闭其他正在控制耳机的应用。", error);
                        }
                    }
                }
                finally
                {
                    foreach (var service in services)
                    {
                        if (!ReferenceEquals(service, activeService))
                            service.Dispose();
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw lastFailure ?? new IOException(
                "设备未公开兼容的蓝牙 RFCOMM 控制服务。请确认耳机已唤醒并已在 Windows 配对。");
        }
        finally
        {
            if (!connected)
                bluetoothDevice.Dispose();
        }
    }

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _input.ReadAsync(buffer, cancellationToken).AsTask();
    }

    public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _output.WriteAsync(bytes, cancellationToken).AsTask();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _socket.Dispose();
        _input.Dispose();
        _output.Dispose();
        _service.Dispose();
        _device.Dispose();
    }

    private static bool IsControlCandidate(Guid uuid, bool allowVendorServices)
    {
        // HuaweiPods uses SPP first. Skip Bluetooth SIG services such as HFP and PBAP
        // when considering vendor-specific service records.
        return uuid == SerialPortUuid || (allowVendorServices && !uuid.ToString("D").EndsWith(
            BluetoothBaseUuidSuffix, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<HuaweiRfcommConnection> ConnectServiceAsync(
        BluetoothDevice device, RfcommDeviceService service, CancellationToken cancellationToken)
    {
        var socket = new StreamSocket();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(SocketConnectTimeout);
            await socket.ConnectAsync(
                service.ConnectionHostName,
                service.ConnectionServiceName,
                service.MaxProtectionLevel).AsTask(deadline.Token).WaitAsync(deadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return new HuaweiRfcommConnection(device, service, socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
