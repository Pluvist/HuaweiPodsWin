using System.Globalization;
using System.Text;
using HuaweiPods.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HuaweiPods;

public sealed partial class MainWindow
{
    private const byte FirstEqualizerSlot = 0x64;
    private static readonly UTF8Encoding EqualizerNameEncoding = new(false, true);

    private sealed record EqualizerCurve(byte? Slot, string Label, IReadOnlyList<int>? Gains);
    private sealed record EqualizerView(Border Card, TextBlock Status, ComboBox Curves, Grid Bands,
        TextBlock[] GainValues, TextBlock Empty, Button? Edit);

    private EqualizerState? _equalizerState;
    private EqualizerView? _equalizerView;
    private ContentDialog? _equalizerEditor;

    private void AddEqualizerControls(HeadsetProfile profile)
    {
        if (!HuaweiEqualizer.SupportsRead(profile))
            return;

        var secondary = (Style)Root.Resources["SecondaryText"];
        var status = new TextBlock { Style = secondary };
        var content = new StackPanel { Spacing = 9 };
        content.Children.Add(new TextBlock
        {
            Text = "自定义均衡器",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(status);

        var curves = new ComboBox { Header = "查看曲线", DisplayMemberPath = nameof(EqualizerCurve.Label),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        curves.SelectionChanged += (_, _) => RenderEqualizerCurve();
        content.Children.Add(curves);

        var bands = new Grid { ColumnSpacing = 6, RowSpacing = 8 };
        const int columns = 5;
        for (int column = 0; column < columns; column++)
            bands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < 2; row++)
            bands.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var values = new TextBlock[HuaweiEqualizer.BandCount];
        for (int index = 0; index < values.Length; index++)
        {
            var cell = new StackPanel { Spacing = 1 };
            cell.Children.Add(new TextBlock { Text = $"频段 {index + 1}", Style = secondary, FontSize = 11 });
            values[index] = new TextBlock { FontSize = 12 };
            cell.Children.Add(values[index]);
            Grid.SetColumn(cell, index % columns);
            Grid.SetRow(cell, index / columns);
            bands.Children.Add(cell);
        }
        content.Children.Add(bands);

        var empty = new TextBlock { Text = "暂无增益数据", Style = secondary };
        content.Children.Add(empty);
        Button? edit = null;
        if (HuaweiEqualizer.SupportsWrite(profile))
        {
            edit = new Button { Content = "编辑自定义音效 1", HorizontalAlignment = HorizontalAlignment.Left };
            edit.Click += async (_, _) => await EditEqualizerAsync();
            content.Children.Add(edit);
        }

        var card = new Border { Style = (Style)Root.Resources["Card"], Child = content };
        _equalizerView = new EqualizerView(card, status, curves, bands, values, empty, edit);
        AdvancedSettingsPanel.Children.Add(card);
        UpdateEqualizerChoices();
        RenderEqualizer();
    }

    private void ReceiveEqualizerFrame(HuaweiFrame frame)
    {
        if (_profile is not { } profile || HuaweiEqualizer.Parse(frame, profile) is not { } state)
            return;

        _equalizerState = state;
        UpdateEqualizerChoices();
        RenderEqualizer();
    }

    private void UpdateEqualizerChoices()
    {
        if (_equalizerView is not { } view)
            return;

        byte? slot = (view.Curves.SelectedItem as EqualizerCurve)?.Slot;
        var choices = new List<EqualizerCurve> { new(null, "当前曲线", _equalizerState?.SelectedGains) };
        if (_equalizerState is { } state)
        {
            choices.AddRange(state.CustomPresets.OrderBy(preset => preset.Id).Select(preset =>
                new EqualizerCurve(preset.Id, $"音效 {preset.Id - FirstEqualizerSlot + 1} · {preset.Name ?? "自定义音效"}", preset.Gains)));
        }
        view.Curves.ItemsSource = choices;
        view.Curves.SelectedItem = choices.FirstOrDefault(curve => curve.Slot == slot) ?? choices[0];
        view.Curves.Visibility = VisibleIf(choices.Count > 1);
    }

    private void RenderEqualizer()
    {
        if (_equalizerView is not { } view || _profile is not { } profile)
            return;

        var state = _equalizerState;
        bool visible = _session is not null && state?.Supported != false && HuaweiEqualizer.SupportsRead(profile);
        view.Card.Visibility = VisibleIf(visible);
        if (!visible)
            return;

        string? presetName = state?.SelectedName
            ?? profile.SoundPresets.FirstOrDefault(option => option.Value == state?.SelectedId)?.Label;
        view.Status.Text = state is null ? "等待耳机上报均衡器状态。"
            : presetName is not null ? $"当前音效 · {presetName}"
            : "已读取耳机的均衡器状态。";
        bool otherBandCount = state?.BandCount is int bands && bands != HuaweiEqualizer.BandCount;
        bool editable = HuaweiEqualizer.CanEditCustom(profile, state);
        if (view.Edit is null || otherBandCount)
            view.Status.Text += " 当前连接仅支持查看。";
        else if (state is not null && !editable)
            view.Status.Text += " 均衡器状态未齐，请刷新后再编辑。";

        if (view.Edit is { } edit)
        {
            edit.Visibility = VisibleIf(!otherBandCount);
            edit.IsEnabled = CanChangeDeviceOptions && _equalizerEditor is null && editable;
        }

        RenderEqualizerCurve();
    }

    private void RenderEqualizerCurve()
    {
        if (_equalizerView is not { } view || _profile is not { } profile)
            return;

        var gains = (view.Curves.SelectedItem as EqualizerCurve)?.Gains;
        view.Bands.Visibility = VisibleIf(gains is { Count: > 0 });
        view.Empty.Visibility = VisibleIf(gains is not { Count: > 0 });
        if (gains is null)
            return;

        int units = HuaweiEqualizer.UnitsPerDecibel(profile);
        for (int index = 0; index < view.GainValues.Length; index++)
        {
            var value = view.GainValues[index];
            value.Text = index < gains.Count ? FormatEqualizerGain(gains[index] / (double)units) : "—";
        }
    }

    private void ResetEqualizer()
    {
        if (_equalizerView is { } view)
            view.Card.Visibility = Visibility.Collapsed;
        _equalizerView = null;
        _equalizerState = null;
        if (!_writing)
            _equalizerEditor?.Hide();
    }

    private async Task EditEqualizerAsync()
    {
        if (!CanChangeDeviceOptions || _equalizerEditor is not null || _session is not { } session
            || _profile is not { } profile || _equalizerState is not { } state
            || !HuaweiEqualizer.CanEditCustom(profile, state))
            return;

        var saved = state.CustomPresets.FirstOrDefault(preset => preset.Id == FirstEqualizerSlot);
        var initial = saved?.Gains ?? state.SelectedGains;
        int units = HuaweiEqualizer.UnitsPerDecibel(profile);
        var name = new TextBox { Header = "音效名称", Text = saved?.Name ?? "自定义音效 1",
            MaxLength = HuaweiEqualizer.CustomNameReadbackByteLimit };
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var rows = new StackPanel { Spacing = 4 };
        var sliders = new Slider[HuaweiEqualizer.BandCount];
        for (int index = 0; index < sliders.Length; index++)
        {
            double initialGain = initial?.Count == sliders.Length ? Math.Clamp(initial[index] / (double)units, -6, 6) : 0;
            var slider = new Slider { Minimum = -6, Maximum = 6, Value = initialGain,
                StepFrequency = 1.0 / units, SmallChange = 1.0 / units, MinWidth = 0 };
            AutomationProperties.SetName(slider, $"频段 {index + 1} 增益");
            sliders[index] = slider;

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"频段 {index + 1}", VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);
            var gain = new TextBlock { Text = FormatEqualizerGain(initialGain), MinWidth = 51,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            slider.ValueChanged += (_, args) => gain.Text = FormatEqualizerGain(args.NewValue);
            Grid.SetColumn(gain, 2);
            row.Children.Add(gain);
            rows.Children.Add(row);
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "保存会覆盖耳机上的自定义音效 1。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(name);
        content.Children.Add(details);
        content.Children.Add(new ScrollViewer { Content = rows,
            MaxHeight = Math.Clamp(Root.ActualHeight - 255, 130, 300),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled });
        content.Children.Add(feedback);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "编辑自定义音效 1", Content = content,
            PrimaryButtonText = "保存并应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };

        bool ValidateName()
        {
            string value = name.Text.Trim();
            int bytes;
            try
            {
                bytes = EqualizerNameEncoding.GetByteCount(value);
            }
            catch (EncoderFallbackException)
            {
                details.Text = "名称包含无效字符。";
                return false;
            }
            details.Text = $"{bytes} / {HuaweiEqualizer.CustomNameReadbackByteLimit} UTF-8 字节";
            return bytes is > 0 and <= HuaweiEqualizer.CustomNameReadbackByteLimit && !value.Contains('\0');
        }

        dialog.IsPrimaryButtonEnabled = ValidateName();
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = ValidateName();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            args.Cancel = true;
            try
            {
                if (_session != session || _profile != profile)
                {
                    args.Cancel = false;
                    return;
                }
                if (!CanChangeDeviceOptions)
                {
                    feedback.Text = "耳机正在处理其他命令，请稍后重试。";
                    return;
                }
                if (!HuaweiEqualizer.CanEditCustom(profile, _equalizerState))
                {
                    feedback.Text = "均衡器状态未齐，请刷新后再编辑。";
                    return;
                }
                if (!ValidateName())
                    return;

                dialog.IsPrimaryButtonEnabled = false;
                name.IsEnabled = false;
                rows.IsHitTestVisible = false;
                foreach (var slider in sliders)
                    slider.IsEnabled = false;
                feedback.Text = "正在保存并读取耳机增益…";
                int[] gains = sliders.Select(slider => (int)Math.Round(slider.Value * units)).ToArray();
                string presetName = name.Text.Trim();
                bool confirmed = false;
                await RunOperationAsync(async (activeSession, activeProfile) =>
                {
                    if (activeSession != session || activeProfile != profile)
                        return;
                    confirmed = await SendAndConfirmAsync(session, HuaweiEqualizer.SetCustom(profile, gains, presetName),
                        HuaweiEqualizer.Query(profile), frame => HuaweiEqualizer.ConfirmsCustom(frame, profile, gains, presetName));
                });

                if (_session != session || confirmed)
                    args.Cancel = false;
                if (_session == session && confirmed)
                    ShowNotice("已保存并应用自定义音效 1", "耳机已回读匹配的音效名称和 10 段增益。", InfoBarSeverity.Success);
                if (args.Cancel)
                    feedback.Text = "耳机未回读匹配的名称和增益。可以保留编辑值再次保存。";
            }
            finally
            {
                name.IsEnabled = true;
                rows.IsHitTestVisible = true;
                foreach (var slider in sliders)
                    slider.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = ValidateName();
                deferral.Complete();
            }
        };

        _equalizerEditor = dialog;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _equalizerEditor = null;
            if (!_windowLifetime.IsCancellationRequested)
                RenderEqualizer();
        }
    }

    private static string FormatEqualizerGain(double gain) =>
        gain.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " dB";
}
