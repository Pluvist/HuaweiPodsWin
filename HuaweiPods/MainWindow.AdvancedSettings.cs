using HuaweiPods.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HuaweiPods;

public sealed partial class MainWindow
{
    private sealed record AdvancedDefinition(string Key, string Title, string Group, bool IsToggle,
        IReadOnlyList<ModeOption> Options, Func<byte[]?> Query, Func<byte, byte[]> Write,
        Func<HuaweiFrame, byte?> Read, string? Description = null, IReadOnlyList<string>? RefreshKeys = null);

    private sealed class AdvancedRow(AdvancedDefinition definition, Control input, TextBlock status)
    {
        public AdvancedDefinition Definition { get; } = definition;
        public Control Input { get; } = input;
        public TextBlock Status { get; } = status;
        public byte? Value { get; set; }
        public byte? Pending { get; set; }
        public bool LastSent { get; set; }
        public byte[]? Query { get; } = definition.Query();
    }

    private readonly Dictionary<string, AdvancedRow> _advancedRows = new();
    private HeadsetSession? _advancedSession;
    private HeadsetModel? _advancedModel;
    private bool _updatingAdvanced;
    private readonly Dictionary<string, HeadsetPairedDevice> _headsetDevices = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HeadsetPairedDevice>? _headsetDeviceRefresh;
    private bool _headsetDeviceReply;
    private int? _headsetDeviceRefreshCount;
    private int _headsetDeviceGeneration;
    private StackPanel? _headsetDeviceRows;
    private TextBlock? _headsetDeviceStatus;
    private Button? _headsetDeviceRefreshButton;
    private ContentDialog? _pairedDeviceDialog;

    private void ResetAdvancedSettings()
    {
        _advancedSession = null;
        _advancedModel = null;
        _advancedRows.Clear();
        _headsetDevices.Clear();
        _headsetDeviceRefresh = null;
        _headsetDeviceReply = false;
        _headsetDeviceRefreshCount = null;
        _headsetDeviceGeneration++;
        _pairedDeviceDialog?.Hide();
        _pairedDeviceDialog = null;
        _headsetDeviceRows = null;
        _headsetDeviceStatus = null;
        _headsetDeviceRefreshButton = null;
        ResetEqualizer();
        AdvancedSettingsPanel.Children.Clear();
        AdvancedSettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void RenderAdvancedSettings()
    {
        if (_session is not { } session || _profile is not { } profile)
        {
            if (_advancedSession is not null)
                ResetAdvancedSettings();
            return;
        }

        if (_advancedSession != session || _advancedModel != profile.Model)
        {
            ResetAdvancedSettings();
            _advancedSession = session;
            _advancedModel = profile.Model;
            var definitions = AdvancedSettings.For(profile).Select(setting => new AdvancedDefinition(
                setting.Key, setting.Title, setting.Group, setting.IsToggle, setting.Options,
                () => setting.Query(), setting.Write, setting.Read, setting.Description, setting.RefreshKeys)).ToList();
            foreach (var gesture in HuaweiGestures.GetDefinitions(profile))
            {
                byte? ReadGesture(HuaweiFrame frame)
                {
                    foreach (var state in HuaweiGestures.Parse(frame, profile))
                        if (state.Id == gesture.Id)
                            return state.Value;
                    return null;
                }
                definitions.Add(new AdvancedDefinition("gesture." + gesture.Id, gesture.Label, "手势控制",
                    gesture.IsToggle, gesture.Options, () => HuaweiGestures.BuildQuery(profile, gesture),
                    value => HuaweiGestures.Set(profile, gesture, value), ReadGesture));
            }

            foreach (var group in definitions.GroupBy(definition => definition.Group))
            {
                AdvancedSettingsPanel.Children.Add(AdvancedHeading(group.Key));
                foreach (var definition in group)
                    AddAdvancedRow(definition);
            }
            AddEqualizerControls(profile);
            if (HuaweiPairedDevices.IsSupported(profile))
                AddHeadsetDeviceList();
            AdvancedSettingsPanel.Visibility = VisibleIf(AdvancedSettingsPanel.Children.Count > 0);
        }

        foreach (var row in _advancedRows.Values)
            RenderAdvancedRow(row);
        RenderEqualizer();
        if (_headsetDeviceRefreshButton is not null)
            _headsetDeviceRefreshButton.IsEnabled = !_writing && _connectAttempt is null;
        if (_headsetDeviceRows is not null)
            foreach (var button in _headsetDeviceRows.Children.OfType<StackPanel>()
                .SelectMany(panel => panel.Children).OfType<Button>())
                button.IsEnabled = !_writing && _connectAttempt is null;
    }

    private TextBlock AdvancedHeading(string title) => new()
    {
        Text = title,
        Style = (Style)Root.Resources["SectionTitle"],
        Margin = new Thickness(4, 4, 0, 0)
    };

    private TextBlock AdvancedSecondary(string text) => new()
    {
        Text = text,
        Style = (Style)Root.Resources["SecondaryText"],
        FontSize = 12
    };

    private void AddAdvancedRow(AdvancedDefinition definition)
    {
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = definition.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(definition.Description))
            text.Children.Add(AdvancedSecondary(definition.Description));
        var status = AdvancedSecondary("等待耳机上报状态");
        text.Children.Add(status);
        Control input;
        FrameworkElement content;
        if (definition.IsToggle)
        {
            input = new ToggleSwitch { OffContent = "", OnContent = "", MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
            content = grid;
        }
        else
        {
            input = new ComboBox { ItemsSource = definition.Options, DisplayMemberPath = "Label",
                HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择设置" };
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(text);
            panel.Children.Add(input);
            content = panel;
        }
        AutomationProperties.SetName(input, definition.Title);
        var row = new AdvancedRow(definition, input, status);
        _advancedRows.Add(definition.Key, row);
        if (input is ToggleSwitch toggle)
            toggle.Toggled += async (_, _) => await ChangeAdvancedSettingAsync(row, toggle.IsOn ? (byte)1 : (byte)0);
        else if (input is ComboBox combo)
            combo.SelectionChanged += async (_, _) =>
            {
                if (combo.SelectedItem is ModeOption selected)
                    await ChangeAdvancedSettingAsync(row, selected.Value);
            };
        AdvancedSettingsPanel.Children.Add(new Border { Style = (Style)Root.Resources["Card"], Child = content });
    }

    private void RenderAdvancedRow(AdvancedRow row)
    {
        bool wasUpdating = _updatingAdvanced;
        _updatingAdvanced = true;
        try
        {
            byte? value = row.Pending ?? row.Value;
            row.Input.IsEnabled = !_writing && _connectAttempt is null && _session == _advancedSession;
            if (row.Input is ToggleSwitch toggle)
                toggle.IsOn = value == 1;
            else if (row.Input is ComboBox combo)
                combo.SelectedItem = row.Definition.Options.FirstOrDefault(option => option.Value == value);
            row.Status.Text = row.Pending is not null ? "正在应用…"
                : row.Value is null ? row.Query is null ? "当前状态未知，设置后显示上次发送" : "等待耳机上报状态"
                : (row.LastSent ? "上次发送：" : "耳机已上报：")
                    + (row.Definition.Options.FirstOrDefault(option => option.Value == row.Value)?.Label ?? "其他状态");
        }
        finally
        {
            _updatingAdvanced = wasUpdating;
        }
    }

    private async Task ChangeAdvancedSettingAsync(AdvancedRow row, byte value)
    {
        if (_updatingAdvanced || !CanChangeDeviceOptions || _session != _advancedSession
            || !_advancedRows.TryGetValue(row.Definition.Key, out var current) || current != row
            || value == row.Pending || value == row.Value
            || (row.Definition.IsToggle && row.Value is null && value == 0))
            return;

        row.Pending = value;
        try
        {
            await RunOperationAsync(async (session, profile) =>
            {
                var query = row.Query;
                bool confirmed;
                if (query is null)
                {
                    await session.WriteAsync(row.Definition.Write(value));
                    confirmed = true;
                }
                else
                    confirmed = await SendAndConfirmAsync(session, row.Definition.Write(value), query,
                        frame => row.Definition.Read(frame) == value);
                if (_session != session || _advancedSession != session)
                    return;
                if (confirmed)
                {
                    row.Value = value;
                    row.LastSent = query is null;
                }
                else
                    ShowUnconfirmed(row.Definition.Title);

                var refreshedQueries = new HashSet<string>();
                foreach (string key in row.Definition.RefreshKeys ?? [])
                {
                    if (_session != session)
                        return;
                    if (_advancedRows.TryGetValue(key, out var affected) && affected.Query is { } refresh
                        && refreshedQueries.Add(Convert.ToHexString(refresh)))
                        await session.WriteAsync(refresh);
                }
            });
        }
        finally
        {
            row.Pending = null;
            if (!_windowLifetime.IsCancellationRequested && _advancedRows.GetValueOrDefault(row.Definition.Key) == row)
                RenderAdvancedRow(row);
        }
    }

    private IEnumerable<byte[]> GetAdvancedQueries(HeadsetProfile profile)
    {
        foreach (var setting in AdvancedSettings.For(profile))
            yield return setting.Query();
        foreach (var query in HuaweiGestures.GetQueries(profile))
            yield return query;
        if (HuaweiEqualizer.SupportsRead(profile))
            yield return HuaweiEqualizer.Query(profile);
        if (HuaweiPairedDevices.IsSupported(profile))
            yield return HuaweiPairedDevices.Query(profile);
    }

    private void ReceiveAdvancedFrame(HuaweiFrame frame)
    {
        if (_session is null || _profile is not { } profile)
            return;
        if (_advancedSession != _session || _advancedModel != profile.Model)
            RenderAdvancedSettings();
        foreach (var row in _advancedRows.Values)
        {
            if (row.Definition.Read(frame) is { } value)
            {
                row.Value = value;
                row.LastSent = false;
                RenderAdvancedRow(row);
            }
        }
        ReceiveEqualizerFrame(frame);
        if (HuaweiPairedDevices.Parse(frame, profile) is { } devices)
        {
            _headsetDeviceReply = true;
            var target = _headsetDeviceRefresh ?? _headsetDevices;
            foreach (var device in devices)
                target[device.Address] = device;
            if (_headsetDeviceRefresh is null)
                RenderHeadsetDeviceList();
        }
    }

    private void AddHeadsetDeviceList()
    {
        AdvancedSettingsPanel.Children.Add(AdvancedHeading("已配对设备"));
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(AdvancedSecondary("查看耳机最近上报的配对设备。部分设备可能延迟返回，刷新会保留先前记录。"));
        _headsetDeviceStatus = AdvancedSecondary("等待耳机返回设备列表");
        panel.Children.Add(_headsetDeviceStatus);
        _headsetDeviceRows = new StackPanel { Spacing = 12 };
        panel.Children.Add(_headsetDeviceRows);
        _headsetDeviceRefreshButton = new Button { Content = "刷新设备列表" };
        _headsetDeviceRefreshButton.Click += async (_, _) =>
            await RunOperationAsync(RefreshHeadsetDeviceListAsync);
        panel.Children.Add(_headsetDeviceRefreshButton);
        AdvancedSettingsPanel.Children.Add(new Border { Style = (Style)Root.Resources["Card"], Child = panel });
    }

    private async Task RefreshHeadsetDeviceListAsync(HeadsetSession session, HeadsetProfile profile)
    {
        int generation = ++_headsetDeviceGeneration;
        var refreshed = new Dictionary<string, HeadsetPairedDevice>(StringComparer.OrdinalIgnoreCase);
        _headsetDeviceRefresh = refreshed;
        _headsetDeviceReply = false;
        _headsetDeviceRefreshCount = null;
        if (_headsetDeviceStatus is not null)
            _headsetDeviceStatus.Text = "正在读取…";
        try
        {
            await session.WriteAsync(HuaweiPairedDevices.Query(profile));
            await Task.Delay(1500, _windowLifetime.Token);
            if (_session != session || generation != _headsetDeviceGeneration)
                return;
            if (_headsetDeviceReply)
            {
                foreach (var entry in refreshed)
                    _headsetDevices[entry.Key] = entry.Value;
            }
            _headsetDeviceRefreshCount = refreshed.Count;
        }
        finally
        {
            if (_session == session && generation == _headsetDeviceGeneration)
            {
                _headsetDeviceRefresh = null;
                RenderHeadsetDeviceList();
                if (!_headsetDeviceReply && _headsetDeviceStatus is not null)
                    _headsetDeviceStatus.Text = "本次未返回设备，保留之前读取的记录。";
            }
        }
    }

    private void RenderHeadsetDeviceList()
    {
        if (_headsetDeviceRows is null || _headsetDeviceStatus is null)
            return;
        _headsetDeviceStatus.Text = _headsetDeviceRefreshCount is { } count
            ? $"本次返回 {count} 台 · 已读取 {_headsetDevices.Count} 台设备"
            : _headsetDevices.Count > 0 ? $"已读取 {_headsetDevices.Count} 台设备" : "等待耳机返回设备列表";
        _headsetDeviceRows.Children.Clear();
        foreach (var device in _headsetDevices.Values)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(device.Name) ? device.Address : device.Name,
                TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(AdvancedSecondary(device.IsConnected ? "已连接" : "已记住"));
            var remove = new Button { Content = "移除配对", Tag = device, IsEnabled = !_writing };
            remove.Click += async (_, _) => await RemoveHeadsetDeviceAsync(device);
            panel.Children.Add(remove);
            _headsetDeviceRows.Children.Add(panel);
        }
    }

    private async Task RemoveHeadsetDeviceAsync(HeadsetPairedDevice device)
    {
        if (!CanChangeDeviceOptions || _session is not { } session || _pairedDeviceDialog is not null)
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "移除这台设备？",
            Content = $"{device.Name}\n{device.Address}\n移除后，这台设备需要重新与耳机配对。",
            PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        _pairedDeviceDialog = dialog;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _session != session)
                return;
            await RunOperationAsync(async (current, profile) =>
            {
                if (current != session)
                    return;
                await current.WriteAsync(HuaweiPairedDevices.Remove(profile, device.Address));
                await RefreshHeadsetDeviceListAsync(current, profile);
                if (_session == current)
                    ShowNotice("已发送移除请求", "耳机尚未确认移除结果，这条记录暂时保留。",
                        InfoBarSeverity.Informational);
            });
        }
        finally
        {
            if (_pairedDeviceDialog == dialog)
                _pairedDeviceDialog = null;
        }
    }
}
