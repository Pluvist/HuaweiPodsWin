using System.Diagnostics;
using System.Runtime.InteropServices;
using HuaweiPods.Protocol;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using XamlCompositionTarget = Microsoft.UI.Xaml.Media.CompositionTarget;

namespace HuaweiPods;

internal sealed partial class ConnectionPopup : Window
{
    private const int WindowExtendedStyle = -20;
    private const int WindowStyle = -16;
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;
    private const int CaptionAndFrameStyles = 0x00C00000 | 0x00040000 | 0x00080000;
    private const long PopupStyle = 0x80000000L;
    private const uint RefreshFrameOnly = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020;
    private const int ImmersiveDarkMode = 20;
    private const int CornerPreference = 33;
    private const int BorderColor = 34;
    private const int RoundedCorners = 2;
    // Keep the reference's notification swap, with nonlinear native window entrance/exit.
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan SwapDuration = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(3);

    private readonly Window _owner;
    private readonly nint _handle;
    private readonly DispatcherQueueTimer _boundsDeadlineTimer;
    private readonly DispatcherQueueTimer _dismissTimer;
    private readonly DispatcherQueueTimer _transitionTimer;
    private Storyboard? _opacityStoryboard;
    private BatteryState? _battery;
    private ImageSource? _artwork;
    private string _deviceName;
    private long _boundsStarted;
    private TimeSpan _boundsDuration;
    private RectInt32 _boundsFrom;
    private RectInt32 _boundsTo;
    private RectInt32 _lastAppliedBounds;
    private TimeSpan? _lastRenderingTime;
    private int _monitorTop;
    private bool _renderingBounds;
    private bool _hasAppliedBounds;
    private bool _awaitingEntranceFrame;
    private bool _boundsEaseIn;
    private bool _closeAfterBounds;
    private bool _shown;
    private bool _closed;
    private bool _hasChargingCase = true;
    private bool _replacing;
    private bool _animationsEnabled;

    public ConnectionPopup(Window owner, string deviceName)
    {
        InitializeComponent();
        SystemBackdrop = new PassiveMicaBackdrop();
        _owner = owner;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _deviceName = deviceName;
        DeviceTitle.Text = deviceName;
        AppWindow.IsShownInSwitchers = false;

        // Configure the attached presenter: replacing it can impose dialog-frame styles
        // that WinUI immediately restores even after SetWindowLongPtr removes them.
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        SetWindowLongPtrW(_handle, WindowExtendedStyle,
            GetWindowLongPtrW(_handle, WindowExtendedStyle) | NoActivate | ToolWindow);
        RemoveNativeFrame();
        int corners = RoundedCorners;
        _ = DwmSetWindowAttribute(_handle, CornerPreference, ref corners, sizeof(int));

        // Only a one-shot safeguard: movement itself follows the rendering cadence.
        _boundsDeadlineTimer = DispatcherQueue.CreateTimer();
        _boundsDeadlineTimer.IsRepeating = false;
        _boundsDeadlineTimer.Tick += (_, _) => FinishBoundsAnimation();
        _dismissTimer = DispatcherQueue.CreateTimer();
        _dismissTimer.Interval = DisplayDuration;
        _dismissTimer.IsRepeating = false;
        _dismissTimer.Tick += (_, _) => BeginDismiss();
        _transitionTimer = DispatcherQueue.CreateTimer();
        _transitionTimer.IsRepeating = false;
        _transitionTimer.Tick += (_, _) =>
        {
            if (!_closed && _replacing)
                PresentContent();
        };
        LayoutRoot.Loaded += (_, _) => SyncFrameTheme();
        LayoutRoot.ActualThemeChanged += (_, _) =>
        {
            SyncFrameTheme();
            RenderBattery();
        };
        Closed += Popup_Closed;
    }

    public void Show()
    {
        if (_shown || _closed)
            return;

        _shown = true;
        _animationsEnabled = new UISettings().AnimationsEnabled;
        _boundsTo = TargetBounds(alignDpi: true);
        PresentContent(flyIn: true);
    }

    public void Replace(string deviceName)
    {
        if (_closed)
            return;

        _deviceName = deviceName;
        _battery = null;
        _artwork = null;
        _dismissTimer.Stop();
        _transitionTimer.Stop();
        StopBoundsAnimation();
        _closeAfterBounds = false;
        _animationsEnabled = new UISettings().AnimationsEnabled;
        _replacing = true;
        _boundsTo = TargetBounds(alignDpi: false);
        if (!_animationsEnabled)
        {
            PresentContent();
            return;
        }

        StartBoundsAnimation(_boundsTo, SwapDuration);
        StartOpacityAnimation(LayoutRoot.Opacity, 0, SwapDuration);
        _transitionTimer.Interval = SwapDuration;
        _transitionTimer.Start();
    }

    private RectInt32 TargetBounds(bool alignDpi)
    {
        var display = (GetCursorPos(out var cursor)
                ? DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Primary) : null)
            ?? DisplayArea.GetFromWindowId(_owner.AppWindow.Id, DisplayAreaFallback.Nearest)
            ?? DisplayArea.Primary;
        var area = display.OuterBounds;
        _monitorTop = area.Y;

        // Move the hidden window first so its DPI matches the monitor that will show it.
        if (alignDpi)
            AppWindow.Move(new PointInt32(area.X, area.Y));
        double scale = Math.Max(GetDpiForWindow(_handle), 96u) / 96.0;
        int width = Math.Min((int)Math.Round(382 * scale), area.Width);
        int height = Math.Min((int)Math.Round(102 * scale), area.Height);
        return new RectInt32(area.X + area.Width / 2 - width / 2,
            area.Y + area.Height / 10 - height / 2, width, height);
    }

    private void PresentContent(bool flyIn = false)
    {
        if (_closed)
            return;

        StopBoundsAnimation();
        _closeAfterBounds = false;
        _replacing = false;
        DeviceTitle.Text = _deviceName;
        RenderArtwork();
        RenderBattery();
        LayoutRoot.Opacity = 0;
        // A replacement may have crossed monitors during its bounds transition.
        var destination = TargetBounds(alignDpi: false);
        MoveBounds(flyIn && _animationsEnabled ? AboveMonitor(destination) : destination);
        AppWindow.Show(false);
        // OverlappedPresenter can restore dialog-frame bits when showing the window.
        SyncFrameTheme();
        if (flyIn && _animationsEnabled)
        {
            // Allocate the storyboard before the frame clock starts, not in the first frame.
            PrepareOpacityAnimation(0, 1, FadeInDuration);
            StartBoundsAnimation(destination, FadeInDuration, waitForEntranceFrame: true);
        }
        else
        {
            StartOpacityAnimation(0, 1, FadeInDuration);
            StartDisplayTimer();
        }
    }

    private void StartDisplayTimer()
    {
        _dismissTimer.Interval = DisplayDuration;
        _dismissTimer.Start();
    }

    public void SetArtwork(ImageSource? image)
    {
        if (_closed)
            return;
        _artwork = image;
        if (!_replacing)
            RenderArtwork();
    }

    private void RenderArtwork()
    {
        Artwork.Source = _artwork;
        Artwork.Visibility = _artwork is null ? Visibility.Collapsed : Visibility.Visible;
        ArtworkPlaceholder.Visibility = _artwork is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetBattery(BatteryState? battery, bool hasChargingCase = true)
    {
        if (_closed)
            return;

        _hasChargingCase = hasChargingCase;
        _battery = battery;
        RenderBattery();
    }

    private void RenderBattery()
    {
        if (_replacing || _closed)
            return;

        LeftBatteryLabel.Text = _hasChargingCase ? "左耳" : "左镜腿";
        RightBatteryLabel.Text = _hasChargingCase ? "右耳" : "右镜腿";
        ChargingCase.Visibility = _hasChargingCase ? Visibility.Visible : Visibility.Collapsed;
        ChargingCaseColumn.Width = _hasChargingCase ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        string theme = LayoutRoot.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        SetBatteryValue(LeftBatteryIcon, LeftBatteryValue, LeftBatteryLabel.Text, _battery?.Left, theme);
        SetBatteryValue(RightBatteryIcon, RightBatteryValue, RightBatteryLabel.Text, _battery?.Right, theme);
        SetBatteryValue(CaseBatteryIcon, CaseBatteryValue, "充电盒", _battery?.Case, theme);
    }

    private static void SetBatteryValue(Image icon, TextBlock value, string label, BatteryPart? part, string theme)
    {
        if (part is { IsConnected: true })
        {
            value.Text = $"{part.Percentage}%";
            string charging = part.IsCharging ? "，充电中" : "";
            AutomationProperties.SetName(value, $"{label}电量，{value.Text}{charging}");
            int bucket = Math.Clamp((part.Percentage + 9) / 10, 1, 10);
            string type = part.IsCharging ? "charge" : "common";
            var uri = new Uri($"ms-appx:///Assets/Interface/Battery/{theme}/{type}_{bucket}.png");
            if (icon.Source is not BitmapImage current || current.UriSource != uri)
                icon.Source = new BitmapImage(uri);
            icon.Visibility = Visibility.Visible;
        }
        else
        {
            value.Text = "—";
            AutomationProperties.SetName(value, $"{label}电量，暂无数据");
            icon.Source = null;
            icon.Visibility = Visibility.Collapsed;
        }
    }

    private void StartOpacityAnimation(double from, double to, TimeSpan duration)
    {
        PrepareOpacityAnimation(from, to, duration);
        if (_animationsEnabled)
            _opacityStoryboard?.Begin();
    }

    private void PrepareOpacityAnimation(double from, double to, TimeSpan duration)
    {
        _opacityStoryboard?.Stop();
        if (!_animationsEnabled)
        {
            LayoutRoot.Opacity = to;
            return;
        }

        // The content fades along with the native Mica window's movement.
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase
            {
                EasingMode = to > from ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };
        Storyboard.SetTarget(animation, LayoutRoot);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _opacityStoryboard = new Storyboard();
        _opacityStoryboard.Children.Add(animation);
    }

    private void BeginDismiss()
    {
        if (_closed)
            return;

        if (!_animationsEnabled)
        {
            Close();
            return;
        }

        _transitionTimer.Stop();
        StartOpacityAnimation(LayoutRoot.Opacity, 0, FadeOutDuration);
        StartBoundsAnimation(AboveMonitor(CurrentBounds()), FadeOutDuration, easeIn: true, closeAfter: true);
    }

    private RectInt32 CurrentBounds()
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        return new RectInt32(position.X, position.Y, size.Width, size.Height);
    }

    private RectInt32 AboveMonitor(RectInt32 bounds)
    {
        // Use the displayed monitor even if the cursor moves while the notification is visible.
        int shadowMargin = (int)Math.Round(12 * Math.Max(GetDpiForWindow(_handle), 96u) / 96.0);
        return new RectInt32(bounds.X, _monitorTop - bounds.Height - shadowMargin, bounds.Width, bounds.Height);
    }

    private void StartBoundsAnimation(RectInt32 destination, TimeSpan duration, bool easeIn = false,
        bool closeAfter = false, bool waitForEntranceFrame = false)
    {
        StopBoundsAnimation();
        _boundsFrom = CurrentBounds();
        _lastAppliedBounds = _boundsFrom;
        _hasAppliedBounds = true;
        _boundsTo = destination;
        _boundsDuration = duration;
        _boundsEaseIn = easeIn;
        _closeAfterBounds = closeAfter;
        _boundsStarted = Stopwatch.GetTimestamp();
        _awaitingEntranceFrame = waitForEntranceFrame;
        _lastRenderingTime = null;
        _renderingBounds = true;
        XamlCompositionTarget.Rendering += BoundsRendering;
        // Rendering can pause when the desktop is locked or the window is occluded.
        // Complete once in that case instead of leaving a hidden popup subscribed forever.
        _boundsDeadlineTimer.Interval = duration + TimeSpan.FromMilliseconds(100);
        _boundsDeadlineTimer.Start();
    }

    private void MoveBounds(RectInt32 bounds)
    {
        if (!_hasAppliedBounds)
        {
            _lastAppliedBounds = CurrentBounds();
            _hasAppliedBounds = true;
        }

        bool samePosition = bounds.X == _lastAppliedBounds.X && bounds.Y == _lastAppliedBounds.Y;
        bool sameSize = bounds.Width == _lastAppliedBounds.Width && bounds.Height == _lastAppliedBounds.Height;
        if (samePosition && sameSize)
            return;

        // Avoid resize/layout work during translation, and omit repeated rounded pixels.
        uint flags = 0x0004 | 0x0010; // SWP_NOZORDER | SWP_NOACTIVATE
        if (sameSize)
            flags |= 0x0001; // SWP_NOSIZE
        if (samePosition)
            flags |= 0x0002; // SWP_NOMOVE
        if (SetWindowPos(_handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags))
            _lastAppliedBounds = bounds;
    }

    private void BoundsRendering(object? sender, object args)
    {
        if (_closed || !_renderingBounds)
        {
            StopBoundsAnimation();
            return;
        }

        if (args is RenderingEventArgs frame)
        {
            if (_lastRenderingTime == frame.RenderingTime)
                return;
            _lastRenderingTime = frame.RenderingTime;
        }

        if (_awaitingEntranceFrame)
        {
            // Window creation, Mica setup and initial layout must not consume the entrance.
            _awaitingEntranceFrame = false;
            _boundsStarted = Stopwatch.GetTimestamp();
            _opacityStoryboard?.Begin();
            StartDisplayTimer();
            _boundsDeadlineTimer.Stop();
            _boundsDeadlineTimer.Interval = _boundsDuration + TimeSpan.FromMilliseconds(100);
            _boundsDeadlineTimer.Start();
        }

        double progress = Math.Clamp(Stopwatch.GetElapsedTime(_boundsStarted) / _boundsDuration, 0, 1);
        double inverse = 1 - progress;
        double eased = _boundsEaseIn ? progress * progress * progress : 1 - inverse * inverse * inverse;
        int left = Interpolate(_boundsFrom.X, _boundsTo.X, eased);
        int top = Interpolate(_boundsFrom.Y, _boundsTo.Y, eased);
        MoveBounds(new RectInt32(left, top,
            Interpolate(_boundsFrom.X + _boundsFrom.Width, _boundsTo.X + _boundsTo.Width, eased) - left,
            Interpolate(_boundsFrom.Y + _boundsFrom.Height, _boundsTo.Y + _boundsTo.Height, eased) - top));
        if (progress == 1)
            FinishBoundsAnimation();
    }

    private static int Interpolate(int from, int to, double progress) =>
        (int)Math.Round(from + (to - from) * progress, MidpointRounding.AwayFromZero);

    private void FinishBoundsAnimation()
    {
        if (_closed || !_renderingBounds)
            return;

        if (_awaitingEntranceFrame)
        {
            // No rendering frames (for example a locked desktop): finish without replaying later.
            _awaitingEntranceFrame = false;
            LayoutRoot.Opacity = 1;
            StartDisplayTimer();
        }
        MoveBounds(_boundsTo);
        bool closeAfter = _closeAfterBounds;
        StopBoundsAnimation();
        if (closeAfter)
            Close();
    }

    private void StopBoundsAnimation()
    {
        if (_renderingBounds)
        {
            XamlCompositionTarget.Rendering -= BoundsRendering;
            _renderingBounds = false;
        }
        _awaitingEntranceFrame = false;
        _boundsDeadlineTimer.Stop();
    }

    private void Popup_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        StopBoundsAnimation();
        _dismissTimer.Stop();
        _transitionTimer.Stop();
        _opacityStoryboard?.Stop();
    }

    private void SyncFrameTheme()
    {
        if (_closed)
            return;

        RemoveNativeFrame();
        // DWM's single rounded outline is outside XAML and must follow its effective theme.
        bool dark = LayoutRoot.ActualTheme == ElementTheme.Dark;
        int immersive = dark ? 1 : 0;
        int color = dark ? 0x00303030 : 0x00DFDFDF;
        _ = DwmSetWindowAttribute(_handle, ImmersiveDarkMode, ref immersive, sizeof(int));
        _ = DwmSetWindowAttribute(_handle, BorderColor, ref color, sizeof(int));
    }

    private void RemoveNativeFrame()
    {
        // SetBorderAndTitleBar leaves WS_CAPTION's frame bits behind. DWM otherwise draws
        // a rectangular highlight inside its rounded outline, especially visible on dark Mica.
        var style = GetWindowLongPtrW(_handle, WindowStyle);
        SetWindowLongPtrW(_handle, WindowStyle, (style & ~CaptionAndFrameStyles) | unchecked((nint)PopupStyle));
        _ = SetWindowPos(_handle, 0, 0, 0, 0, 0, RefreshFrameOnly);
    }

    private sealed class PassiveMicaBackdrop : MicaBackdrop
    {
        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            base.OnTargetConnected(target, root);
            KeepMicaActive(target, root);
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target,
            XamlRoot root)
        {
            base.OnDefaultSystemBackdropConfigurationChanged(target, root);
            KeepMicaActive(target, root);
        }

        private void KeepMicaActive(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            // The popup stays outside the foreground focus chain but still uses active Mica.
            var configuration = GetDefaultSystemBackdropConfiguration(target, root);
            if (!configuration.IsInputActive)
                configuration.IsInputActive = true;
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
