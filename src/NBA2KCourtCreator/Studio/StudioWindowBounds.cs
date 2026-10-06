using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NBA2KCourtCreator.Studio;

/// <summary>Respect the current monitor's work area, including taskbar and per-monitor DPI.</summary>
public static class StudioWindowBounds
{
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public PointI Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    internal readonly record struct MonitorArea(Rect Monitor, Rect Work);
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("IsAttached", typeof(bool), typeof(StudioWindowBounds), new PropertyMetadata(false));
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out RectI rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    public static Size FitSize(Size requested, Size workArea)
    {
        if (!double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height)) throw new ArgumentOutOfRangeException(nameof(workArea));
        double Fit(double value, double available) => double.IsFinite(value) && value > 0 ? Math.Min(value, available) : available;
        return new Size(Fit(requested.Width, Math.Max(1, workArea.Width - 24)), Fit(requested.Height, Math.Max(1, workArea.Height - 24)));
    }
    internal static bool IsAttached(Window window) => (bool)window.GetValue(AttachedProperty);
    internal static Rect ClampPlacement(Rect requested, Rect workArea)
    {
        if (!ValidArea(requested) || !ValidArea(workArea)) throw new ArgumentOutOfRangeException(nameof(workArea));
        var width = Math.Min(requested.Width, workArea.Width); var height = Math.Min(requested.Height, workArea.Height);
        return new Rect(Math.Clamp(requested.Left, workArea.Left, workArea.Right - width), Math.Clamp(requested.Top, workArea.Top, workArea.Bottom - height), width, height);
    }
    private static bool ValidArea(Rect area) => !area.IsEmpty && double.IsFinite(area.Left) && double.IsFinite(area.Top)
        && double.IsFinite(area.Right) && double.IsFinite(area.Bottom) && area.Width > 0 && area.Height > 0
        && area.Left >= int.MinValue && area.Top >= int.MinValue && area.Right <= int.MaxValue && area.Bottom <= int.MaxValue
        && area.Width <= int.MaxValue && area.Height <= int.MaxValue;

    internal static bool ApplyMinMax(IntPtr destination, MonitorArea? area, Size minimum, DpiScale dpi)
    {
        if (destination == IntPtr.Zero || area is not { } monitor || !ValidArea(monitor.Monitor) || !ValidArea(monitor.Work)
            || !monitor.Monitor.Contains(monitor.Work) || !double.IsFinite(dpi.DpiScaleX) || !double.IsFinite(dpi.DpiScaleY)
            || dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0) return false;
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(destination);
        bounds.MaxPosition = new PointI { X = (int)(monitor.Work.Left - monitor.Monitor.Left), Y = (int)(monitor.Work.Top - monitor.Monitor.Top) };
        bounds.MaxSize = new PointI { X = (int)monitor.Work.Width, Y = (int)monitor.Work.Height };
        int Minimum(double value, double scale, int maximum) => (int)Math.Min(maximum, Math.Ceiling((double.IsFinite(value) && value > 0 ? value : 0) * scale));
        bounds.MinTrackSize = new PointI { X = Minimum(minimum.Width, dpi.DpiScaleX, bounds.MaxSize.X), Y = Minimum(minimum.Height, dpi.DpiScaleY, bounds.MaxSize.Y) };
        Marshal.StructureToPtr(bounds, destination, false);
        return true;
    }

    public static void Attach(Window window)
    {
        if (IsAttached(window)) return;
        window.SetValue(AttachedProperty, true);
        var closed = false;
        var nominalMinimum = new Size(window.MinWidth, window.MinHeight);
        HwndSource? source = null; HwndSourceHook? hook = null; DispatcherOperation? pending = null;
        void QueueFit()
        {
            if (closed || pending is not null) return;
            pending = window.Dispatcher.InvokeAsync(() =>
            {
                pending = null;
                if (closed || window.WindowState != WindowState.Normal) return;
                var handle = new WindowInteropHelper(window).Handle;
                if (ReadArea(handle) is not { } area) return;
                var dpi = VisualTreeHelper.GetDpi(window);
                if (dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0) return;
                var available = new Size(area.Work.Width / dpi.DpiScaleX, area.Work.Height / dpi.DpiScaleY);
                var capacity = FitSize(new Size(double.NaN, double.NaN), available);
                var minimum = new Size(Math.Min(nominalMinimum.Width, capacity.Width), Math.Min(nominalMinimum.Height, capacity.Height));
                var requested = new Size(Math.Max(minimum.Width, double.IsFinite(window.Width) ? window.Width : window.ActualWidth), Math.Max(minimum.Height, double.IsFinite(window.Height) ? window.Height : window.ActualHeight));
                var size = FitSize(requested, available);
                window.MinWidth = minimum.Width; window.MinHeight = minimum.Height;
                window.Width = size.Width; window.Height = size.Height;
                if (!GetWindowRect(handle, out var rectangle)) return;
                var bounds = ToRect(rectangle);
                if (!ValidArea(bounds)) return;
                var placement = ClampPlacement(bounds, area.Work);
                // HWND coordinates remain physical pixels across monitors with different DPI.
                if (placement.Left != bounds.Left || placement.Top != bounds.Top)
                    SetWindowPos(handle, IntPtr.Zero, (int)placement.Left, (int)placement.Top, 0, 0, 0x0015);
            }, DispatcherPriority.Loaded);
        }
        window.SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == 0x0024 && window.WindowStyle == WindowStyle.None)
                { if (ApplyMinMax(lParam, ReadArea(hwnd), nominalMinimum, VisualTreeHelper.GetDpi(window))) handled = true; }
                else if (message is 0x02E0 or 0x007E or 0x001A) QueueFit();
                return IntPtr.Zero;
            };
            source?.AddHook(hook); QueueFit();
        };
        window.Loaded += (_, _) => QueueFit();
        window.StateChanged += (_, _) => QueueFit();
        window.Closed += (_, _) =>
        {
            closed = true; pending?.Abort(); pending = null;
            if (source is { IsDisposed: false } && hook is not null) source.RemoveHook(hook);
            source = null; hook = null;
        };
    }
    private static Rect ToRect(RectI value) => value.Right > value.Left && value.Bottom > value.Top
        ? new Rect(value.Left, value.Top, (double)value.Right - value.Left, (double)value.Bottom - value.Top) : Rect.Empty;
    private static MonitorArea? ReadArea(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(handle, 2);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        var area = new MonitorArea(ToRect(info.Monitor), ToRect(info.Work));
        return ValidArea(area.Monitor) && ValidArea(area.Work) && area.Monitor.Contains(area.Work) ? area : null;
    }
}
