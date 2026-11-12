using Microsoft.UI.Dispatching;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace HuaweiPods.Bluetooth;

/// <summary>Reports live Windows Bluetooth Classic connection transitions on the supplied dispatcher.</summary>
/// <remarks>Call Start, Stop and Dispose on the dispatcher thread. Initial enumeration is silent.</remarks>
internal sealed class BluetoothConnectionWatcher(DispatcherQueue dispatcherQueue) : IDisposable
{
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";
    private const string DeviceAddressProperty = "System.Devices.Aep.DeviceAddress";

    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private DeviceWatcher? _watcher;
    private bool _initialEnumerationComplete;
    private bool _disposed;

    public event Action<PairedBluetoothDevice>? ConnectionChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is { Status: not (DeviceWatcherStatus.Aborted or DeviceWatcherStatus.Stopped) })
            return;

        Stop();
        var watcher = DeviceInformation.CreateWatcher(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            [IsConnectedProperty, DeviceAddressProperty],
            DeviceInformationKind.AssociationEndpoint);
        _watcher = watcher;
        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved;
        watcher.EnumerationCompleted += OnEnumerationCompleted;
        try
        {
            watcher.Start();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        var watcher = _watcher;
        _watcher = null;
        _endpoints.Clear();
        _initialEnumerationComplete = false;
        if (watcher is null)
            return;

        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            watcher.Stop();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        ConnectionChanged = null;
    }

    public static string GetDeviceKey(PairedBluetoothDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.Address))
            return device.Id;

        Span<char> address = stackalloc char[12];
        int digits = 0;
        foreach (char character in device.Address.Trim())
        {
            if (character is ':' or '-')
                continue;
            if (!char.IsAsciiHexDigit(character) || digits == address.Length)
                return device.Id;
            address[digits++] = char.ToUpperInvariant(character);
        }

        return digits == address.Length ? new string(address) : device.Id;
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation information) =>
        Dispatch(sender, () => RecordEndpoint(information));

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update) =>
        Dispatch(sender, () =>
        {
            if (_endpoints.TryGetValue(update.Id, out var endpoint))
            {
                endpoint.Information.Update(update);
                RecordEndpoint(endpoint.Information);
            }
        });

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update) =>
        Dispatch(sender, () => RemoveEndpoint(update.Id));

    private void OnEnumerationCompleted(DeviceWatcher sender, object args) =>
        Dispatch(sender, () => _initialEnumerationComplete = true);

    private void Dispatch(DeviceWatcher sender, Action work) =>
        dispatcherQueue.TryEnqueue(() =>
        {
            if (_watcher == sender)
                work();
        });

    private void RecordEndpoint(DeviceInformation information)
    {
        _endpoints.TryGetValue(information.Id, out var previous);
        information.Properties.TryGetValue(IsConnectedProperty, out var connectedProperty);
        information.Properties.TryGetValue(DeviceAddressProperty, out var addressProperty);
        string? address = addressProperty is string reportedAddress && !string.IsNullOrWhiteSpace(reportedAddress)
            ? reportedAddress : previous?.Device.Address;
        bool? connected = connectedProperty is bool reportedConnected ? reportedConnected : previous?.IsConnected;
        if (connected is null && previous is null && _initialEnumerationComplete)
            connected = false;

        var device = new PairedBluetoothDevice(information.Id, information.Name, connected == true, address);
        ApplyEndpoint(new Endpoint(information, device, connected), previous);
    }

    private void ApplyEndpoint(Endpoint endpoint, Endpoint? previous)
    {
        var device = endpoint.Device;
        _endpoints[device.Id] = endpoint;
        if (!_initialEnumerationComplete)
            return;

        if (endpoint.IsConnected == true && (previous is null || previous.IsConnected == false)
            && !HasConnectedEndpoint(GetDeviceKey(device), device.Id))
            ConnectionChanged?.Invoke(device);
        else if (endpoint.IsConnected == false && previous?.IsConnected == true)
            ReportDisconnected(previous.Device);
    }

    private void RemoveEndpoint(string id)
    {
        if (_endpoints.Remove(id, out var previous) && previous.IsConnected == true)
            ReportDisconnected(previous.Device);
    }

    private void ReportDisconnected(PairedBluetoothDevice previous)
    {
        if (_initialEnumerationComplete && !HasConnectedEndpoint(GetDeviceKey(previous)))
            ConnectionChanged?.Invoke(previous with { IsConnected = false });
    }

    private bool HasConnectedEndpoint(string key, string? excludedId = null) =>
        _endpoints.Values.Any(endpoint => endpoint.IsConnected == true
            && !string.Equals(endpoint.Device.Id, excludedId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(GetDeviceKey(endpoint.Device), key, StringComparison.OrdinalIgnoreCase));

    private sealed record Endpoint(DeviceInformation Information, PairedBluetoothDevice Device, bool? IsConnected);
}
