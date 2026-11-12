using System.Diagnostics;
using System.IO;
using HuaweiPods.Artwork;
using HuaweiPods.Bluetooth;
using HuaweiPods.Protocol;
using Microsoft.UI.Xaml;

namespace HuaweiPods;

public sealed partial class MainWindow
{
    private sealed class ReconnectPopupState(ConnectionPopup window, PairedBluetoothDevice device)
    {
        public ConnectionPopup Window { get; } = window;
        public PairedBluetoothDevice Device { get; } = device;
        public string Key { get; } = BluetoothConnectionWatcher.GetDeviceKey(device);
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationTokenSource? Probe { get; set; }
        public int ArtworkRequest { get; set; }
    }

    private BluetoothConnectionWatcher? _reconnectWatcher;
    private ReconnectPopupState? _reconnectPopup;
    private readonly HashSet<string> _firstConnectionKeys = new(StringComparer.OrdinalIgnoreCase);

    private void StartReconnectWatcher()
    {
        if (_windowLifetime.IsCancellationRequested)
            return;

        if (_reconnectWatcher is null)
        {
            _reconnectWatcher = new BluetoothConnectionWatcher(DispatcherQueue);
            _reconnectWatcher.ConnectionChanged += OnBluetoothConnectionChanged;
        }

        try
        {
            _reconnectWatcher.Start();
        }
        catch (Exception error)
        {
            _reconnectWatcher.Dispose();
            _reconnectWatcher = null;
            Debug.WriteLine(error);
        }
    }

    private void StopReconnectWatcher()
    {
        _reconnectWatcher?.Dispose();
        _reconnectWatcher = null;
        _reconnectPopup?.Window.Close();
    }

    private ReconnectPopupState? GetReconnectPopup(PairedBluetoothDevice? device) =>
        device is not null && _reconnectPopup is { } popup
            && StringComparer.OrdinalIgnoreCase.Equals(popup.Key, BluetoothConnectionWatcher.GetDeviceKey(device))
            ? popup : null;

    private static RememberedHeadset? GetConnectionHistory(DevicePreference preference) =>
        preference.LastConnection ?? (preference.LowLatency is not null && preference.LowLatencyModel is { } model
            ? new RememberedHeadset(model) : null);

    private DevicePreference GetReconnectPreference(PairedBluetoothDevice device)
    {
        var key = DeviceListItem.GetKey(device);
        var preference = _preferences.Get(key);
        if (key != device.Id && GetConnectionHistory(preference) is null)
            return preference with { LastConnection = GetConnectionHistory(_preferences.Get(device.Id)) };
        return preference;
    }

    private void RememberConnection(DeviceListItem selection, HeadsetProfile profile)
    {
        var preference = GetReconnectPreference(selection.Device);
        var last = GetConnectionHistory(preference);
        var model = profile.Model.ToString();
        var identity = _identity;
        if (last?.Model == model && last.Identity is { } previous
            && (identity is null || (identity.ModelId == previous.ModelId && identity.SubModelId is null)))
            identity = previous;

        var remembered = new RememberedHeadset(model, identity);
        if (last is null)
            _firstConnectionKeys.Add(BluetoothConnectionWatcher.GetDeviceKey(selection.Device));
        if (_preferences.Get(selection.Key).LastConnection != remembered)
        {
            preference = preference with { LastConnection = remembered };
            try
            {
                _preferences.Set(selection.Key, preference);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(error);
            }
        }

        if (GetReconnectPopup(selection.Device) is { } popup)
        {
            if (_battery is not null)
                popup.Window.SetBattery(_battery, profile.HasChargingCase);
            _ = LoadReconnectArtworkAsync(popup, profile, preference, identity);
        }
    }

    private async void OnBluetoothConnectionChanged(PairedBluetoothDevice device)
    {
        if (_windowLifetime.IsCancellationRequested)
            return;

        try
        {
            var key = DeviceListItem.GetKey(device);
            var connectionKey = BluetoothConnectionWatcher.GetDeviceKey(device);
            if (!device.IsConnected)
            {
                _firstConnectionKeys.Remove(connectionKey);
                _pausedAutoDevices.Remove(key);
                GetReconnectPopup(device)?.Window.Close();
                return;
            }

            var preference = GetReconnectPreference(device);
            var history = GetConnectionHistory(preference);
            if (_firstConnectionKeys.Contains(connectionKey) || FindProfile(history?.Model) is not { } profile
                || GetReconnectPopup(device) is not null)
                return;

            _pausedAutoDevices.Remove(key);
            var name = string.IsNullOrWhiteSpace(device.Name) ? profile.DisplayName : device.Name;
            var previous = _reconnectPopup;
            var window = previous?.Window ?? new ConnectionPopup(this, name);
            var popup = new ReconnectPopupState(window, device);
            _reconnectPopup = popup;
            if (previous is null)
            {
                window.Closed += (_, _) =>
                {
                    if (_reconnectPopup is not { } current || current.Window != window)
                        return;
                    _reconnectPopup = null;
                    current.Lifetime.Cancel();
                    current.Lifetime.Dispose();
                };
            }
            else
            {
                previous.Lifetime.Cancel();
                previous.Lifetime.Dispose();
            }
            try
            {
                if (previous is not null)
                    window.Replace(name);
                popup.Window.SetBattery(null, profile.HasChargingCase);
                popup.Window.Show();
            }
            catch
            {
                popup.Window.Close();
                throw;
            }

            _ = LoadReconnectArtworkAsync(popup, profile, preference, history?.Identity);
            await RefreshReconnectBatteryAsync(popup, profile);
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
        }
    }

    private async Task LoadReconnectArtworkAsync(ReconnectPopupState popup, HeadsetProfile profile,
        DevicePreference preference, DeviceIdentity? identity)
    {
        var cancellationToken = popup.Lifetime.Token;
        int request = ++popup.ArtworkRequest;
        string? modelId = identity?.ModelId ?? OfficialArtwork.GetCatalogModelId(profile);
        if (identity?.SubModelId is null && modelId is not null && preference.ArtworkModelId == modelId
            && preference.ArtworkSubModelId is { } manualColor)
            identity = new DeviceIdentity(modelId, manualColor);

        try
        {
            var image = await LoadArtworkImageAsync(identity, modelId, cancellationToken);
            if (_reconnectPopup == popup && request == popup.ArtworkRequest)
                popup.Window.SetArtwork(image);
        }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested)
                Debug.WriteLine(error);
        }
    }

    private async Task RefreshReconnectBatteryAsync(ReconnectPopupState popup, HeadsetProfile profile)
    {
        if ((_session is not null || _connectAttempt is not null) && GetReconnectPopup(_selected?.Device) == popup)
        {
            // The main session already owns this endpoint, including when its handshake is still running.
            if (profile.SupportsBattery && _session is { } current && _connectAttempt is null)
            {
                var cancellationToken = popup.Lifetime.Token;
                while (_writing && _session == current)
                    await Task.Delay(100, cancellationToken);

                if (_reconnectPopup == popup && _session == current)
                    await RunOperationAsync((session, _) => session.WriteAsync(HuaweiProtocol.BatteryQuery()), quiet: true);
            }
            return;
        }

        if (CanAutoConnect)
        {
            await ConnectDeviceAsync(CreateDeviceListItem(popup.Device), automatic: true);
            return;
        }

        if (!profile.SupportsBattery)
            return;

        // A different headset may own the main window. Read the reconnecting headset independently.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(popup.Lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        popup.Probe = deadline;
        try
        {
            using var connection = await HuaweiRfcommConnection.ConnectAsync(popup.Device, deadline.Token,
                allowVendorServices: true);
            await connection.WriteAsync(HuaweiProtocol.BatteryQuery(), deadline.Token);
            var decoder = new HuaweiFrameDecoder();
            var buffer = new byte[4096];
            while (!deadline.IsCancellationRequested)
            {
                int length = await connection.ReadAsync(buffer, deadline.Token);
                if (length == 0)
                    return;

                foreach (var frame in decoder.Push(buffer.AsSpan(0, length)))
                {
                    if (HuaweiProtocol.ParseBattery(frame, profile) is not { } battery)
                        continue;
                    if (_reconnectPopup == popup && !deadline.IsCancellationRequested)
                        popup.Window.SetBattery(battery, profile.HasChargingCase);
                    return;
                }
            }
        }
        finally
        {
            popup.Probe = null;
        }
    }
}
