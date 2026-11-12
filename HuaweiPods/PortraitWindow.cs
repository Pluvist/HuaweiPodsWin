using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace HuaweiPods;

internal sealed class PortraitWindow : IDisposable
{
    private const int RatioWidth = 11;
    private const int RatioHeight = 16;
    private const uint SizingMessage = 0x0214;
    private const nuint SubclassId = 1;

    private readonly AppWindow _appWindow;
    private readonly nint _window;
    private readonly SubclassProcedure _procedure;

    public PortraitWindow(Window window)
    {
        _appWindow = window.AppWindow;
        _window = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _procedure = WindowProcedure;
        if (!SetWindowSubclass(_window, _procedure, SubclassId, 0))
            throw new Win32Exception("无法锁定竖版窗口比例。");

        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsMaximizable = false;

        var area = WorkArea;
        var size = GetSize(50 * DpiScale, area);
        _appWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2,
            area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
        _appWindow.Changed += AppWindow_Changed;
    }

    private RectInt32 WorkArea => DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
    private double DpiScale => Math.Max(GetDpiForWindow(_window), 96u) / 96.0;

    private SizeInt32 GetSize(double requestedUnits, RectInt32 area)
    {
        int maximum = Math.Max(1, Math.Min(area.Width / RatioWidth, area.Height / RatioHeight));
        int minimum = Math.Min((int)Math.Round(36 * DpiScale), maximum);
        int units = Math.Clamp((int)Math.Round(requestedUnits), minimum, maximum);
        return new SizeInt32(units * RatioWidth, units * RatioHeight);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || sender.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
            return;
        var current = sender.Size;
        if (current.Width * RatioHeight != current.Height * RatioWidth)
            sender.Resize(GetSize(NearestUnits(current.Width, current.Height), WorkArea));
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message != SizingMessage)
            return DefSubclassProc(window, message, wParam, lParam);

        var edge = (SizingEdge)wParam;
        var bounds = Marshal.PtrToStructure<SizingRect>(lParam);
        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        double units = edge switch
        {
            SizingEdge.Left or SizingEdge.Right => width / (double)RatioWidth,
            SizingEdge.Top or SizingEdge.Bottom => height / (double)RatioHeight,
            _ => NearestUnits(width, height)
        };
        var size = GetSize(units, WorkArea);

        if (edge is SizingEdge.Left or SizingEdge.TopLeft or SizingEdge.BottomLeft)
            bounds.Left = bounds.Right - size.Width;
        else
            bounds.Right = bounds.Left + size.Width;
        if (edge is SizingEdge.Top or SizingEdge.TopLeft or SizingEdge.TopRight)
            bounds.Top = bounds.Bottom - size.Height;
        else
            bounds.Bottom = bounds.Top + size.Height;

        Marshal.StructureToPtr(bounds, lParam, false);
        return 1;
    }

    private static double NearestUnits(int width, int height) =>
        (width * RatioWidth + height * RatioHeight) / (double)(RatioWidth * RatioWidth + RatioHeight * RatioHeight);

    public void Dispose()
    {
        _appWindow.Changed -= AppWindow_Changed;
        RemoveWindowSubclass(_window, _procedure, SubclassId);
    }

    private enum SizingEdge
    {
        Left = 1,
        Right,
        Top,
        TopLeft,
        TopRight,
        Bottom,
        BottomLeft,
        BottomRight
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizingRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
}
