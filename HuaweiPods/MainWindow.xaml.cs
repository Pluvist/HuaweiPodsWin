using System.IO;
using HuaweiPods.Artwork;
using HuaweiPods.Bluetooth;
using HuaweiPods.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.System;

namespace HuaweiPods;

public sealed record DeviceListItem(PairedBluetoothDevice Device, HeadsetProfile? Profile)
{
    public string Key => GetKey(Device);
    public string Name => string.IsNullOrWhiteSpace(Device.Name) ? "未命名蓝牙设备" : Device.Name;
    public string Description => Profile?.DisplayName ?? "可尝试自动识别，或手动指定型号";

    internal static string GetKey(PairedBluetoothDevice device) => device.Address ?? device.Id;
}

public sealed partial class MainWindow : Window
{
    private sealed record ModelChoice(string Name, HeadsetProfile? Profile);
    private sealed record ColorChoice(string Name, DeviceIdentity? Identity);
    private readonly DevicePreferences _preferences = new();
    private readonly StartupRegistration _startupRegistration = new();
    private readonly ModelChoice[] _modelChoices =
        [new("自动识别", null), .. HeadsetProfiles.All.Select(profile => new ModelChoice(profile.DisplayName, profile))];
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _discoveryTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly HashSet<string> _pausedAutoDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _windowLifetime = new();
    private readonly PortraitWindow _portraitWindow;
    private IReadOnlyList<PairedBluetoothDevice> _paired = [];
    private DeviceListItem? _selected;
    private HeadsetSession? _session;
    private CancellationTokenSource? _connectAttempt;
    private HeadsetProfile? _profile;
    private DeviceIdentity? _identity;
    private CancellationTokenSource? _artworkRequest;
    private BatteryState? _battery;
    private AncState? _noise;
    private NoiseControlMode? _pendingNoiseMode;
    private StartupRegistrationStatus _startupStatus;
    private bool? _wear;
    private bool? _lowLatency;
    private bool? _lastLatencyWrite;
    private byte? _soundPreset;
    private Func<HuaweiFrame, bool>? _responseMatches;
    private TaskCompletionSource<bool>? _response;
    private string? _lastAutoDevice;
    private bool _ready;
    private bool _discovering;
    private bool _automaticAttempt;
    private bool _readingIdentity;
    private bool _loadingDevices;
    private bool _writing;
    private bool _rendering;
    private bool _noiseDirty;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HuaweiPods.ico"));
        _portraitWindow = new PortraitWindow(this);
        Closed += Window_Closed;
        _pollTimer.Tick += PollTimer_Tick;
        _discoveryTimer.Tick += DiscoveryTimer_Tick;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_ready)
            return;
        _ready = true;
        ModelPicker.ItemsSource = _modelChoices;
        Navigation.SelectedItem = HomeNav;
        Root.ActualThemeChanged += (_, _) => RenderBattery();
        RenderConnection();
        StartReconnectWatcher();
        await LoadDevicesAsync();
        if (!_windowLifetime.IsCancellationRequested)
        {
            _discoveryTimer.Start();
            await DiscoverAndConnectAsync();
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _discoveryTimer.Stop();
        _portraitWindow.Dispose();
        _windowLifetime.Cancel();
        StopReconnectWatcher();
        _connectAttempt?.Cancel();
        _artworkRequest?.Cancel();
        DropSession();
        _windowLifetime.Dispose();
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.AddedItems.FirstOrDefault() is not ListViewItem { Tag: string page })
            return;

        if (sender is ListView list && list == Navigation)
            FooterNavigation.SelectedIndex = -1;
        else
            Navigation.SelectedIndex = -1;

        HomePane.Visibility = VisibleIf(page == "home");
        DevicesPane.Visibility = VisibleIf(page == "devices");
        SettingsPane.Visibility = VisibleIf(page == "settings");
        AboutPane.Visibility = VisibleIf(page == "about");
        Notice.Visibility = VisibleIf(page is "home" or "devices" or "settings");
        if (page == "settings")
            RefreshStartupSwitch();
        PageScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void RefreshStartupSwitch()
    {
        try
        {
            RenderStartupSwitch(_startupRegistration.Read());
            StartupSwitch.IsEnabled = true;
        }
        catch (Exception error) when (IsStartupRegistrationError(error))
        {
            StartupSwitch.IsEnabled = false;
            StartupStatus.Text = "暂时无法读取 Windows 启动项。";
            ShowNotice("读取开机自启设置失败", error.Message);
        }
    }

    private void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        bool enabled = StartupSwitch.IsOn;
        if (!_ready || !StartupSwitch.IsEnabled || enabled == (_startupStatus != StartupRegistrationStatus.Disabled))
            return;

        try
        {
            _startupRegistration.SetEnabled(enabled);
            var status = _startupRegistration.Read();
            if (status != (enabled ? StartupRegistrationStatus.CurrentExecutable : StartupRegistrationStatus.Disabled))
                throw new IOException("Windows 启动项未保存为所选状态。");
            RenderStartupSwitch(status);
            Notice.IsOpen = false;
        }
        catch (Exception error) when (IsStartupRegistrationError(error))
        {
            RefreshStartupSwitch();
            StartupSwitch.IsOn = _startupStatus != StartupRegistrationStatus.Disabled;
            ShowNotice("更新开机自启设置失败", error.Message);
        }
    }

    private void RenderStartupSwitch(StartupRegistrationStatus status)
    {
        _startupStatus = status;
        StartupSwitch.IsOn = status != StartupRegistrationStatus.Disabled;
        StartupStatus.Text = status == StartupRegistrationStatus.DifferentExecutable
            ? "当前启动项指向另一份 HuaweiPods。关闭后重新开启可切换到当前版本。"
            : "登录 Windows 后自动启动 HuaweiPods。";
    }

    private static bool IsStartupRegistrationError(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException;

    private async void OpenBluetoothSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:bluetooth")))
                ShowNotice("无法打开蓝牙设置", "请从 Windows 设置打开“蓝牙和设备”。");
        }
        catch (Exception error)
        {
            ShowNotice("无法打开蓝牙设置", error.Message);
        }
    }

    private async void ReloadDevices_Click(object sender, RoutedEventArgs e) => await LoadDevicesAsync();

    private async Task LoadDevicesAsync()
    {
        if (_loadingDevices || _session is not null || _connectAttempt is not null)
            return;

        _loadingDevices = true;
        RenderConnection();
        DeviceListHint.Text = "正在查找 Windows 已配对设备…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            _paired = await HuaweiRfcommConnection.GetPairedDevicesAsync(timeout.Token);
            RebuildDeviceList();
        }
        catch (Exception error)
        {
            if (!_windowLifetime.IsCancellationRequested)
            {
                DeviceListHint.Text = "暂时无法读取已配对设备。";
                ShowNotice("读取设备失败", error is OperationCanceledException ? "查询超时，请检查蓝牙后重试。" : error.Message);
            }
        }
        finally
        {
            _loadingDevices = false;
            if (!_windowLifetime.IsCancellationRequested)
                RenderConnection();
        }
    }

    private DeviceListItem CreateDeviceListItem(PairedBluetoothDevice device)
    {
        string key = DeviceListItem.GetKey(device);
        var preference = GetReconnectPreference(device);
        var profile = _session is not null && key == _selected?.Key
            ? _profile
            : GetManualProfile(preference) ?? HeadsetProfiles.FindByName(device.Name)
                ?? FindProfile(GetConnectionHistory(preference)?.Model);
        return new DeviceListItem(device, profile);
    }

    private void RebuildDeviceList()
    {
        var items = _paired
            .Select(CreateDeviceListItem)
            .Where(item => ShowOtherDevices.IsOn || item.Profile is not null)
            .ToArray();
        var selection = items.FirstOrDefault(item => item.Key == _selected?.Key)
            ?? items.FirstOrDefault(item => item.Profile is not null);

        _rendering = true;
        DeviceList.ItemsSource = items;
        DeviceList.SelectedItem = selection;
        _rendering = false;
        if (_session is null)
            SelectDevice(selection);
        else
            _selected = selection ?? _selected;

        DeviceListHint.Text = items.Length > 0 ? $"{items.Length} 台设备"
            : _paired.Count == 0 ? "请先在 Windows 蓝牙设置中配对。"
            : "未找到名称匹配的华为耳机。耳机改名后，可显示其他设备。";
    }

    private void ShowOtherDevices_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready && !_rendering && _session is null && _connectAttempt is null)
            RebuildDeviceList();
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_rendering)
            SelectDevice(DeviceList.SelectedItem as DeviceListItem);
    }

    private void SelectDevice(DeviceListItem? selection)
    {
        _selected = selection;
        var manual = selection is null ? null : GetManualProfile(_preferences.Get(selection.Key));
        _rendering = true;
        ModelPicker.SelectedItem = _modelChoices.First(choice => choice.Profile == manual);
        _rendering = false;
        _profile = manual ?? selection?.Profile;
        RenderConnection();
        _ = UpdateArtworkAsync();
    }

    private static HeadsetProfile? GetManualProfile(DevicePreference preference) => FindProfile(preference.Model);

    private static HeadsetProfile? FindProfile(string? model) =>
        HeadsetProfiles.All.FirstOrDefault(profile => profile.Model.ToString() == model);

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _rendering || _selected is null || _session is not null
            || ModelPicker.SelectedItem is not ModelChoice choice)
            return;

        SavePreference(_preferences.Get(_selected.Key) with { Model = choice.Profile?.Model.ToString() });
        RebuildDeviceList();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } selection)
            return;

        if (_connectAttempt is not null)
        {
            _pausedAutoDevices.Add(selection.Key);
            _connectAttempt.Cancel();
            DropSession();
            RenderConnection();
            _ = UpdateArtworkAsync();
            return;
        }

        if (_session is not null)
        {
            _pausedAutoDevices.Add(selection.Key);
            DropSession();
            RenderConnection();
            _ = UpdateArtworkAsync();
            return;
        }

        _pausedAutoDevices.Remove(selection.Key);
        await ConnectDeviceAsync(selection, automatic: false);
    }

    private async void DiscoveryTimer_Tick(object? sender, object e)
    {
        StartReconnectWatcher();
        await DiscoverAndConnectAsync();
    }

    private bool CanAutoConnect => !_windowLifetime.IsCancellationRequested && !_loadingDevices
        && !_writing && _connectAttempt is null && _session is null;

    private async Task DiscoverAndConnectAsync()
    {
        if (_discovering || !CanAutoConnect)
            return;

        _discovering = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var devices = await HuaweiRfcommConnection.GetConnectedHuaweiDevicesAsync(timeout.Token);
            if (!CanAutoConnect)
                return;

            _pausedAutoDevices.IntersectWith(devices.Select(DeviceListItem.GetKey));
            var candidates = devices.Select(CreateDeviceListItem)
                .Where(item => !_pausedAutoDevices.Contains(item.Key) && GetReconnectPopup(item.Device)?.Probe is null)
                .OrderByDescending(item => item.Profile is not null)
                .ToArray();
            if (candidates.Length == 0)
                return;

            // Rotate past a failed endpoint so a nearby phone cannot starve other candidates.
            int last = Array.FindIndex(candidates, item => item.Key == _lastAutoDevice);
            var candidate = candidates[(last + 1) % candidates.Length];
            _lastAutoDevice = candidate.Key;
            await ConnectDeviceAsync(candidate, automatic: true);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
        }
        finally
        {
            _discovering = false;
        }
    }

    private async Task ConnectDeviceAsync(DeviceListItem selection, bool automatic)
    {
        GetReconnectPopup(selection.Device)?.Probe?.Cancel();
        var previousSelection = _selected;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(automatic ? 8 : 22));
        _automaticAttempt = automatic;
        _connectAttempt = attempt;
        if (!automatic)
            Notice.IsOpen = false;
        SelectDevice(selection);
        try
        {
            var connection = await HuaweiRfcommConnection.ConnectAsync(selection.Device, timeout.Token,
                allowVendorServices: _profile is not null);
            if (timeout.IsCancellationRequested)
            {
                connection.Dispose();
                timeout.Token.ThrowIfCancellationRequested();
            }

            var session = new HeadsetSession(connection);
            _session = session;
            _ = session.ReadAsync(
                frame => DispatcherQueue.TryEnqueue(() => ReceiveFrame(session, frame)),
                error => DispatcherQueue.TryEnqueue(() => ConnectionLost(session, error)));

            if (connection.UsesSerialPort)
            {
                _readingIdentity = true;
                try
                {
                    await SendAndConfirmAsync(session, HuaweiProtocol.DeviceInfoQuery(), null,
                        frame => HuaweiProtocol.ParseDeviceInfo(frame) is not null);
                }
                finally
                {
                    _readingIdentity = false;
                }
            }

            if (_session != session || attempt.IsCancellationRequested)
                return;

            if (_profile is not { } profile)
            {
                DropSession();
                if (!automatic)
                    ShowNotice("未识别到耳机型号", "如果这是支持的华为耳机，请手动选择型号后再连接。",
                        InfoBarSeverity.Informational);
                return;
            }

            if (automatic && _identity is null && _battery is null)
            {
                // A socket can belong to another accessory. Require a Huawei reply before accepting it.
                bool confirmed = profile.SupportsBattery && await SendAndConfirmAsync(session,
                    HuaweiProtocol.BatteryQuery(), null, frame => HuaweiProtocol.ParseBattery(frame, profile) is not null);
                if (!confirmed)
                {
                    DropSession();
                    return;
                }
            }

            if (_session != session || attempt.IsCancellationRequested)
                return;

            await RestoreLatencyAsync(session, profile);
            await QueryStatusAsync(session, queryBattery: !automatic || _battery is null);
            if (_session == session)
            {
                RememberConnection(selection, profile);
                _paired = _paired.Where(device => DeviceListItem.GetKey(device) != selection.Key)
                    .Append(selection.Device).ToArray();
                RebuildDeviceList();
                Notice.IsOpen = false;
                _pollTimer.Start();
                if (!automatic && Navigation.SelectedItem is ListViewItem { Tag: "devices" })
                    Navigation.SelectedItem = HomeNav;
            }
        }
        catch (Exception error)
        {
            DropSession();
            if (!automatic && !attempt.IsCancellationRequested)
            {
                string message = error is OperationCanceledException
                    ? "连接超时。请确认耳机已开机、靠近电脑并已在 Windows 中配对。"
                    : error.Message;
                ShowNotice("连接失败", message);
            }
        }
        finally
        {
            _connectAttempt = null;
            _automaticAttempt = false;
            if (!_windowLifetime.IsCancellationRequested)
            {
                if (automatic && _session is null)
                    SelectDevice(previousSelection);
                else
                {
                    RenderConnection();
                    _ = UpdateArtworkAsync();
                }
            }
        }
    }

    private void DropSession()
    {
        _pollTimer.Stop();
        _response?.TrySetCanceled();
        var session = _session;
        _session = null;
        session?.Dispose();
        _battery = null;
        _noise = null;
        _pendingNoiseMode = null;
        _noiseDirty = false;
        _wear = null;
        _lowLatency = null;
        _lastLatencyWrite = null;
        _soundPreset = null;
        _identity = null;
        _profile = _selected?.Profile;
        ResetAdvancedSettings();
    }

    private void ConnectionLost(HeadsetSession session, Exception error)
    {
        if (_session != session)
            return;
        DropSession();
        RenderConnection();
        _ = UpdateArtworkAsync();
        if (!_automaticAttempt)
            ShowNotice("耳机控制连接已断开", error.Message);
    }

    private async Task QueryStatusAsync(HeadsetSession session, bool queryBattery = true)
    {
        if (_profile is not { } profile)
            return;

        var queries = new List<byte[]>();
        if (profile.SupportsBattery && queryBattery)
            queries.Add(HuaweiProtocol.BatteryQuery());
        if (profile.SupportsAncReadback)
            queries.Add(HuaweiProtocol.AncQuery());
        if (profile.SupportsWearDetectionReadback)
            queries.Add(HuaweiProtocol.WearDetectionQuery());
        if (profile.SupportsLowLatencyReadback)
            queries.Add(HuaweiProtocol.LowLatencyQuery(profile));
        if (profile.SoundPresets.Count > 0)
            queries.Add(HuaweiProtocol.SoundPresetQuery(profile));
        queries.AddRange(GetAdvancedQueries(profile));

        foreach (byte[] query in queries.DistinctBy(packet => Convert.ToHexString(packet)))
        {
            if (_session != session)
                return;
            await session.WriteAsync(query);
            await Task.Delay(140);
        }
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(async (session, profile) =>
        {
            _noiseDirty = false;
            await QueryStatusAsync(session);
        });
    }

    private async void PollTimer_Tick(object? sender, object e)
    {
        if (_profile?.SupportsBattery == true && !_writing && _connectAttempt is null)
            await RunOperationAsync((session, profile) => session.WriteAsync(HuaweiProtocol.BatteryQuery()), quiet: true);
    }

    private void ReceiveFrame(HeadsetSession session, HuaweiFrame frame)
    {
        if (_session != session)
            return;

        if (_readingIdentity && HuaweiProtocol.ParseDeviceInfo(frame) is { } identity)
        {
            if (identity.SubModelId is not null || _identity?.ModelId != identity.ModelId)
                _identity = identity;
            if (_selected is { } selected && GetManualProfile(_preferences.Get(selected.Key)) is null)
            {
                var identified = HeadsetProfiles.FindByModelId(identity.ModelId);
                if (identified is not null || _profile?.ModelId is not null)
                    _profile = identified;
            }
        }

        if (_profile is { } profile)
        {
            if (HuaweiProtocol.ParseBattery(frame, profile) is { } battery)
            {
                _battery = battery;
                RenderBattery();
                GetReconnectPopup(_selected?.Device)?.Window.SetBattery(battery, profile.HasChargingCase);
            }
            if (profile.SupportsAncReadback && HuaweiProtocol.ParseAnc(frame, profile) is { } noise)
            {
                _noise = noise;
                RenderNoise(refreshSelection: !_noiseDirty);
            }
            if (profile.SupportsWearDetection && HuaweiProtocol.ParseWearDetection(frame) is { } wear)
            {
                _wear = wear;
                RenderOptions();
            }
            if (profile.SupportsLowLatencyReadback && HuaweiProtocol.ParseLowLatency(frame, profile) is { } lowLatency)
            {
                _lowLatency = lowLatency;
                RenderOptions();
            }
            if (profile.SoundPresets.Count > 0 && HuaweiProtocol.ParseSoundPreset(frame, profile) is { } preset)
            {
                _soundPreset = preset;
                RenderOptions();
            }
        }

        ReceiveAdvancedFrame(frame);
        if (_responseMatches?.Invoke(frame) == true)
            _response?.TrySetResult(true);
    }

    private async Task<bool> SendAndConfirmAsync(HeadsetSession session, byte[] packet, byte[]? readback,
        Func<HuaweiFrame, bool> matches)
    {
        _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _responseMatches = matches;
        try
        {
            await session.WriteAsync(packet);
            if (readback is not null)
            {
                await Task.Delay(180);
                if (_session != session)
                    return false;
                await session.WriteAsync(readback);
            }

            try
            {
                return await _response.Task.WaitAsync(TimeSpan.FromSeconds(readback is null ? 2 : 3));
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            _response = null;
            _responseMatches = null;
        }
    }

    private async Task RunOperationAsync(Func<HeadsetSession, HeadsetProfile, Task> operation, bool quiet = false)
    {
        if (_writing || _connectAttempt is not null || _session is not { } session || _profile is not { } profile)
            return;
        _writing = true;
        if (!quiet)
            Notice.IsOpen = false;
        RenderConnection(refreshNoise: false);
        try
        {
            await operation(session, profile);
        }
        catch (Exception error)
        {
            if (_session == session)
                ShowNotice("耳机操作失败", error.Message);
        }
        finally
        {
            _writing = false;
            if (!_windowLifetime.IsCancellationRequested)
                RenderConnection(refreshNoise: false);
        }
    }

    private async void NoiseMode_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _rendering || sender is not RadioButton segment
            || !Enum.TryParse<NoiseControlMode>(segment.Tag as string, out var mode)
            || mode == _noise?.Mode)
            return;

        await RunOperationAsync(async (session, profile) =>
        {
            if (!profile.SupportsAnc || (mode == NoiseControlMode.Transparency && !profile.SupportsTransparency))
                return;

            _pendingNoiseMode = mode;
            RenderNoise(refreshSelection: false);
            try
            {
                byte[] packet = HuaweiProtocol.SetAnc(profile, mode);
                if (profile.SupportsAncReadback)
                {
                    bool confirmed = await SendAndConfirmAsync(session, packet, HuaweiProtocol.AncQuery(),
                        frame => HuaweiProtocol.ParseAnc(frame, profile)?.Mode == mode);
                    if (_session != session)
                        return;
                    if (!confirmed)
                    {
                        ShowUnconfirmed("噪声模式");
                        return;
                    }
                }
                else
                {
                    await session.WriteAsync(packet);
                    if (_session != session)
                        return;
                    _noise = new AncState(mode);
                }

                _noiseDirty = false;
            }
            finally
            {
                _pendingNoiseMode = null;
            }
        });
    }

    private void NoiseLevels_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rendering && _session is not null)
        {
            _noiseDirty = true;
            ApplyNoiseButton.IsEnabled = NoiseLevels.IsEnabled && NoiseLevels.SelectedItem is ModeOption;
        }
    }

    private void NoiseDirection_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_rendering && _session is not null)
            _noiseDirty = true;
    }

    private async void ApplyNoise_Click(object sender, RoutedEventArgs e)
    {
        if (_noise?.Mode is not { } mode)
            return;

        await RunOperationAsync(async (session, profile) =>
        {
            if (profile.SupportsDirection && mode == NoiseControlMode.NoiseCancellation)
            {
                await session.WriteAsync(HuaweiProtocol.SetAncDirection(profile, (int)NoiseDirection.Value - 1));
            }
            else if (profile.SupportsAncReadback && NoiseLevels.SelectedItem is ModeOption level)
            {
                bool confirmed = await SendAndConfirmAsync(session, HuaweiProtocol.SetAnc(profile, mode, level.Value),
                    HuaweiProtocol.AncQuery(), frame => HuaweiProtocol.ParseAnc(frame, profile) is { } state
                        && state.Mode == mode && state.Level == level.Value);
                if (!confirmed && _session == session)
                {
                    ShowUnconfirmed("噪声级别");
                    return;
                }
            }

            if (_session == session)
            {
                _noiseDirty = false;
                RenderNoise();
            }
        });
    }

    private bool CanChangeDeviceOptions =>
        _ready && !_rendering && !_writing && _session is not null && _connectAttempt is null;

    private async void WearSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDeviceOptions || WearSwitch.IsOn == (_wear ?? false))
            return;

        bool enabled = WearSwitch.IsOn;
        await RunOperationAsync(async (session, profile) =>
        {
            bool confirmed = await SendAndConfirmAsync(session, HuaweiProtocol.SetWearDetection(profile, enabled),
                HuaweiProtocol.WearDetectionQuery(), frame => HuaweiProtocol.ParseWearDetection(frame) == enabled);
            if (!confirmed && _session == session)
                ShowUnconfirmed("佩戴检测");
        });
    }

    private async void SoundPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!CanChangeDeviceOptions || SoundPresets.SelectedItem is not ModeOption preset
            || preset.Value == _soundPreset)
            return;

        await RunOperationAsync(async (session, profile) =>
        {
            bool confirmed = await SendAndConfirmAsync(session, HuaweiProtocol.SetSoundPreset(profile, preset.Value),
                HuaweiProtocol.SoundPresetQuery(profile), frame => HuaweiProtocol.ParseSoundPreset(frame, profile) == preset.Value);
            if (!confirmed && _session == session)
                ShowUnconfirmed("音效预设");
        });
    }

    private async void LatencySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDeviceOptions || LatencySwitch.IsOn == (_lowLatency ?? _lastLatencyWrite ?? false))
            return;

        bool enabled = LatencySwitch.IsOn;
        await RunOperationAsync(async (session, profile) =>
        {
            var packet = HuaweiProtocol.SetLowLatency(profile, enabled);
            bool confirmed = true;
            if (profile.SupportsLowLatencyReadback)
                confirmed = await SendAndConfirmAsync(session, packet, HuaweiProtocol.LowLatencyQuery(profile),
                    frame => HuaweiProtocol.ParseLowLatency(frame, profile) == enabled);
            else
                await session.WriteAsync(packet);

            if (_session != session)
                return;

            if (!confirmed)
            {
                ShowUnconfirmed("低时延");
                return;
            }

            _lastLatencyWrite = enabled;
            if (_selected is { } selection)
                SavePreference(_preferences.Get(selection.Key) with { LowLatency = enabled, LowLatencyModel = profile.Model.ToString() });
        });
    }

    private void RestoreLatency_Toggled(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDeviceOptions || _selected is not { } selection)
            return;

        var preference = _preferences.Get(selection.Key);
        if (preference.RestoreLowLatency != RestoreLatency.IsOn)
            SavePreference(preference with { RestoreLowLatency = RestoreLatency.IsOn });
    }

    private async Task RestoreLatencyAsync(HeadsetSession session, HeadsetProfile profile)
    {
        if (!profile.SupportsLowLatency || _selected is null)
            return;
        var preference = _preferences.Get(_selected.Key);
        if (!preference.RestoreLowLatency || preference.LowLatency is not { } enabled
            || preference.LowLatencyModel != profile.Model.ToString())
            return;

        await session.WriteAsync(HuaweiProtocol.SetLowLatency(profile, enabled));
        if (_session == session)
            _lastLatencyWrite = enabled;
    }

    private void SavePreference(DevicePreference preference)
    {
        if (_selected is null)
            return;
        try
        {
            _preferences.Set(_selected.Key, preference);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowNotice("设置未写入磁盘", "当前应用会话仍可使用这项设置。" + error.Message);
        }
    }

    private async Task UpdateArtworkAsync()
    {
        _artworkRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        _artworkRequest = request;
        bool connected = _session is not null;
        var identity = _identity;
        var selection = _selected;
        string? modelId = identity?.ModelId
            ?? (_profile is { } profile ? OfficialArtwork.GetCatalogModelId(profile) : null);
        DeviceArtwork.Source = null;
        DeviceArtwork.Visibility = Visibility.Collapsed;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        ArtworkStatus.Text = connected ? "正在查找官方图片…" : "连接耳机后显示官方图片";
        ColorPicker.Visibility = Visibility.Collapsed;
        try
        {
            if (identity?.SubModelId is null && selection is not null && modelId is not null)
            {
                var colors = await OfficialArtwork.GetBundledVariantsAsync(modelId, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (colors.Count > 0)
                {
                    var preference = _preferences.Get(selection.Key);
                    var choices = new List<ColorChoice> { new("随耳机识别", null) };
                    choices.AddRange(colors.Select(color => new ColorChoice(color.Description, color.Identity)));
                    var manualIdentity = choices.FirstOrDefault(choice => choice.Identity is { } variant
                        && variant.ModelId == preference.ArtworkModelId && variant.SubModelId == preference.ArtworkSubModelId)?.Identity;
                    identity = manualIdentity ?? identity;
                    _rendering = true;
                    ColorPicker.ItemsSource = choices;
                    ColorPicker.SelectedItem = choices.First(choice => choice.Identity == manualIdentity);
                    ColorPicker.Visibility = Visibility.Visible;
                    ColorPicker.IsEnabled = _connectAttempt is null;
                    _rendering = false;
                }
            }

            if (!connected)
                return;
            var image = await LoadArtworkImageAsync(identity, modelId, request.Token);
            if (image is null)
            {
                ArtworkStatus.Text = "暂无此型号的官方图片";
                return;
            }

            DeviceArtwork.Source = image;
            DeviceArtwork.Visibility = Visibility.Visible;
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine(error);
                ArtworkStatus.Text = "暂时无法加载官方图片";
            }
        }
        finally
        {
            if (_artworkRequest == request)
                _artworkRequest = null;
        }
    }

    private static async Task<BitmapImage?> LoadArtworkImageAsync(DeviceIdentity? identity, string? modelId,
        CancellationToken cancellationToken)
    {
        var path = await OfficialArtwork.ResolveAsync(identity, modelId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (path is null)
            return null;

        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        // Both artwork surfaces are small, and the bundled hero images start at 504 pixels.
        var image = new BitmapImage { DecodePixelWidth = 480 };
        await image.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }

    private async void ColorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering || _selected is null || _identity?.SubModelId is not null || ColorPicker.SelectedItem is not ColorChoice choice)
            return;
        SavePreference(_preferences.Get(_selected.Key) with
        {
            ArtworkModelId = choice.Identity?.ModelId,
            ArtworkSubModelId = choice.Identity?.SubModelId
        });
        await UpdateArtworkAsync();
    }

    private void RenderConnection(bool refreshNoise = true)
    {
        bool connected = _session is not null;
        bool connecting = _connectAttempt is not null;
        bool selecting = !connected && !connecting && !_loadingDevices;
        bool hasStatusQueries = _profile is { } profile && (profile.SupportsBattery || profile.SupportsAncReadback
            || profile.SupportsWearDetectionReadback || profile.SupportsLowLatencyReadback || profile.SoundPresets.Count > 0
            || AdvancedSettings.For(profile).Count > 0 || HuaweiGestures.GetQueries(profile).Count > 0);
        DeviceList.IsEnabled = selecting;
        ShowOtherDevices.IsEnabled = selecting;
        ReloadDevicesButton.IsEnabled = selecting;
        ModelPicker.IsEnabled = selecting;
        ColorPicker.IsEnabled = !connecting;
        ModelPicker.Visibility = VisibleIf(_selected is not null);
        ConnectionActions.Visibility = VisibleIf(_selected is not null);
        ConnectButton.Content = connecting ? "取消连接" : connected ? "断开" : "连接";
        ConnectButton.IsEnabled = !_writing && !_loadingDevices;
        ConnectionProgress.Visibility = VisibleIf(connecting);
        ConnectionProgress.IsActive = connecting;
        RefreshStatusButton.Visibility = VisibleIf(connected && hasStatusQueries);
        RefreshStatusButton.IsEnabled = !_writing && !connecting;

        DeviceNameText.Text = _selected?.Name ?? "选择你的耳机";
        HomeDeviceTitle.Text = _selected?.Name ?? "华为耳机";
        HomeDeviceConnection.Text = (connecting, connected, _selected) switch
        {
            (true, _, _) => "正在读取耳机…",
            (_, true, _) => "已连接",
            (_, _, { }) => "未连接",
            _ => "连接耳机后查看电量和控制选项"
        };
        ConnectionText.Text = (connecting, connected, _selected) switch
        {
            (true, _, _) => _automaticAttempt ? "正在自动读取耳机数据…" : "正在建立耳机控制连接…",
            (_, true, _) => $"控制连接已建立 · {_profile?.DisplayName ?? "识别型号中"}",
            (_, _, { } device) => (_profile?.DisplayName ?? "待识别型号")
                + (device.Device.IsConnected ? " · Windows 蓝牙已连接" : " · 已在 Windows 配对"),
            _ => "每 10 秒检查 Windows 已连接的 HUAWEI 设备，也可以手动选择耳机连接。"
        };

        RenderBattery();
        RenderNoise(refreshNoise);
        RenderOptions();
        RenderAdvancedSettings();
    }

    private void RenderBattery()
    {
        bool hasCase = _profile?.HasChargingCase != false;
        LeftBatteryLabel.Text = hasCase ? "左耳" : "左镜腿";
        RightBatteryLabel.Text = hasCase ? "右耳" : "右镜腿";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LeftBatteryBar, LeftBatteryLabel.Text + "电量");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RightBatteryBar, RightBatteryLabel.Text + "电量");
        CaseBatteryCard.Visibility = VisibleIf(hasCase);
        if (hasCase && BatteryTiles.ColumnDefinitions.Count == 2)
            BatteryTiles.ColumnDefinitions.Add(CaseBatteryColumn);
        else if (!hasCase && BatteryTiles.ColumnDefinitions.Count == 3)
            BatteryTiles.ColumnDefinitions.RemoveAt(2);
        RenderBatteryPart(LeftBatteryText, LeftBatteryBar, LeftBatteryIcon, LeftChargeText, _battery?.Left);
        RenderBatteryPart(RightBatteryText, RightBatteryBar, RightBatteryIcon, RightChargeText, _battery?.Right);
        RenderBatteryPart(CaseBatteryText, CaseBatteryBar, CaseBatteryIcon, CaseChargeText, _battery?.Case);
        BatteryHint.Visibility = VisibleIf(_session is not null && _battery is null);
        BatteryHint.Text = _profile?.SupportsBattery == false ? "该机型的电量使用旧版 HFP 通道，当前 Windows 版未接入。"
            : _battery is null ? "暂无电量数据。可打开充电盒后刷新。"
            : hasCase ? "充电盒和盒内耳机的电量取决于耳机最近一次上报。" : "显示眼镜最近一次上报的电量。";
    }

    private void RenderBatteryPart(TextBlock text, ProgressBar progress, Image icon, TextBlock charge, BatteryPart? part)
    {
        bool available = part?.IsConnected == true;
        bool charging = available && part!.IsCharging;
        int percentage = available ? part!.Percentage : 0;
        text.Text = available ? $"{percentage}%" : "—";
        progress.Value = percentage;
        progress.Opacity = available ? 1 : 0.32;
        charge.Text = charging ? "充电中" : "";
        icon.Visibility = VisibleIf(available);
        if (!available)
            return;

        string theme = Root.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        string name = charging ? "charge" : "common";
        int bucket = Math.Clamp((percentage + 9) / 10, 1, 10);
        var uri = new Uri($"ms-appx:///Assets/Interface/Battery/{theme}/{name}_{bucket}.png");
        if (icon.Source is not BitmapImage current || current.UriSource != uri)
            icon.Source = new BitmapImage(uri);
    }

    private void RenderNoise(bool refreshSelection = true)
    {
        var profile = _profile;
        bool connected = _session is not null;
        NoiseControlsGroup.Visibility = VisibleIf(!connected || profile?.SupportsAnc == true);
        if (!connected || profile?.SupportsAnc != true)
        {
            HomeNoiseStatus.Text = connected ? "此型号不支持主动降噪" : "连接耳机后可切换模式";
            NoiseSection.Visibility = Visibility.Collapsed;
            NoiseModeThumb.Visibility = Visibility.Collapsed;
            return;
        }

        _rendering = true;
        NoiseSection.Visibility = Visibility.Visible;
        var confirmedMode = _noise?.Mode;
        var displayedMode = _pendingNoiseMode ?? confirmedMode;
        NoiseTransparencySegment.Visibility = VisibleIf(profile.SupportsTransparency);
        NoiseTransparencyColumn.Width = profile.SupportsTransparency ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        NoiseOffSegment.IsChecked = displayedMode == NoiseControlMode.Off;
        NoiseTransparencySegment.IsChecked = displayedMode == NoiseControlMode.Transparency;
        NoiseCancellationSegment.IsChecked = displayedMode == NoiseControlMode.NoiseCancellation;
        NoiseModeThumb.Visibility = VisibleIf(displayedMode is not null);
        if (displayedMode is { } active)
        {
            int column = active switch
            {
                NoiseControlMode.Transparency => 1,
                NoiseControlMode.NoiseCancellation => 2,
                _ => 0
            };
            if (Grid.GetColumn(NoiseModeThumb) != column)
                Grid.SetColumn(NoiseModeThumb, column);
        }

        IReadOnlyList<ModeOption> levels = confirmedMode switch
        {
            NoiseControlMode.NoiseCancellation => profile.AncLevels,
            NoiseControlMode.Transparency => profile.TransparencyLevels,
            _ => []
        };
        bool hasLevels = levels.Count > 1 || confirmedMode == NoiseControlMode.NoiseCancellation && levels.Count > 0;
        bool hasDirection = profile.SupportsDirection && confirmedMode == NoiseControlMode.NoiseCancellation;
        NoiseLevels.Visibility = VisibleIf(hasLevels);
        if (!ReferenceEquals(NoiseLevels.ItemsSource, levels))
        {
            NoiseLevels.ItemsSource = levels;
            refreshSelection = true;
        }
        if (refreshSelection)
            NoiseLevels.SelectedItem = levels.FirstOrDefault(level => level.Value == _noise?.Level);

        bool enabled = !_writing && _connectAttempt is null;
        NoiseOffSegment.IsEnabled = NoiseTransparencySegment.IsEnabled = NoiseCancellationSegment.IsEnabled = enabled;
        NoiseLevels.IsEnabled = enabled && hasLevels;
        NoiseDirection.Visibility = VisibleIf(hasDirection);
        NoiseDirection.IsEnabled = enabled;
        ApplyNoiseButton.Visibility = VisibleIf(hasDirection || hasLevels);
        ApplyNoiseButton.IsEnabled = enabled && (hasDirection || NoiseLevels.SelectedItem is ModeOption);
        ApplyNoiseButton.Content = hasDirection ? "应用降噪方向" : "应用级别";
        string? levelLabel = levels.FirstOrDefault(level => level.Value == _noise?.Level)?.Label;
        HomeNoiseStatus.Text = _pendingNoiseMode is { } pending
            ? $"正在切换至{NoiseModeLabel(pending)}…"
            : confirmedMode is { } confirmed
                ? (profile.SupportsAncReadback ? $"当前 · {NoiseModeLabel(confirmed)}" : $"上次发送 · {NoiseModeLabel(confirmed)}")
                    + (levelLabel is not null ? $" · {levelLabel}" : "")
                : profile.SupportsAncReadback ? "等待耳机上报当前模式" : "请选择噪声模式";
        _rendering = false;
    }

    private static string NoiseModeLabel(NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.Transparency => "透传",
        NoiseControlMode.NoiseCancellation => "降噪",
        _ => "关闭"
    };

    private void RenderOptions()
    {
        bool wear = _profile?.SupportsWearDetection == true;
        bool latency = _profile?.SupportsLowLatency == true;
        IReadOnlyList<ModeOption> presets = _profile?.SoundPresets ?? [];
        bool sound = presets.Count > 0;
        bool enabled = !_writing && _connectAttempt is null;
        OptionsSection.Visibility = VisibleIf(_session is not null && (wear || latency || sound));
        WearSettings.Visibility = VisibleIf(wear);
        LatencySettings.Visibility = VisibleIf(latency);
        SoundSettings.Visibility = VisibleIf(sound);
        WearStatus.Text = _wear is { } worn ? $"耳机已上报：{OnOff(worn)}" : "等待耳机上报佩戴检测状态。";
        LatencyStatus.Text = _lowLatency is { } reported ? $"耳机已上报：{OnOff(reported)}"
            : _lastLatencyWrite is { } sent ? $"上次发送：{OnOff(sent)}"
            : _profile?.SupportsLowLatencyReadback == true ? "等待耳机上报低时延状态。" : "当前状态未知。此机型仅支持发送低时延设置。";
        _rendering = true;
        if (!ReferenceEquals(SoundPresets.ItemsSource, presets))
            SoundPresets.ItemsSource = presets;
        var preset = presets.FirstOrDefault(option => option.Value == _soundPreset);
        if (!_writing)
        {
            WearSwitch.IsOn = _wear ?? false;
            LatencySwitch.IsOn = _lowLatency ?? _lastLatencyWrite ?? false;
            SoundPresets.SelectedItem = preset;
        }
        SoundStatus.Text = preset is not null ? $"耳机已上报：{preset.Label}"
            : _soundPreset is not null ? "耳机正在使用其他音效。" : "等待耳机上报音效预设。";
        WearSwitch.IsEnabled = enabled && wear;
        LatencySwitch.IsEnabled = RestoreLatency.IsEnabled = enabled && latency;
        SoundPresets.IsEnabled = enabled && sound;
        RestoreLatency.IsOn = _selected is not null && _preferences.Get(_selected.Key).RestoreLowLatency;
        _rendering = false;
    }

    private void ShowUnconfirmed(string setting) => ShowNotice($"{setting}尚未确认",
        "已发送设置，但未收到匹配的耳机状态。可刷新状态后重试。", InfoBarSeverity.Warning);

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        Notice.Severity = severity;
        Notice.Title = title;
        Notice.Message = message;
        Notice.IsOpen = true;
    }

    private static string OnOff(bool value) => value ? "开启" : "关闭";
    private static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
