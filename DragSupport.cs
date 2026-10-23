using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ArkCopyBuffer;

static class DragSupport
{
    // Move the already-rendered popup HWND; offsets re-enter WPF placement/layout.
    internal static bool MoveGhost(FrameworkElement card, Native.POINT point, ref Native.POINT previous, ref bool placed)
    {
        if (PresentationSource.FromVisual(card) is not HwndSource source) return false;
        if (placed && previous.X == point.X && previous.Y == point.Y) return true;
        if (!Native.SetWindowPos(source.Handle, IntPtr.Zero, point.X, point.Y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x4 /* NOZORDER */ | 0x400 /* NOSENDCHANGING */)) return false;
        previous = point; placed = true; return true;
    }
    internal static void ScrollEdge(FrameworkElement area, Point point, ref long previous)
    {
        int direction = point.Y < 24 ? -1 : point.Y > area.ActualHeight - 24 ? 1 : 0;
        if (direction == 0 || Environment.TickCount64 - previous < 80) return;
        if ((area as ScrollViewer ?? area.FindDescendant<ScrollViewer>()) is not { } scroll) return;
        previous = Environment.TickCount64;
        if (direction < 0) scroll.LineUp(); else scroll.LineDown();
    }
}

/// <summary>OLE consumes wheel routing during DoDragDrop. Hook only this gesture;
/// handle wheels over our visible scroll areas, never clicks, movement or other apps.</summary>
sealed class DragWheelScroll : IDisposable
{
    readonly FrameworkElement[] _areas;
    readonly Action<FrameworkElement>? _scrolled;
    readonly Native.LowLevelKeyboardProc _callback;
    IntPtr _hook;
    ScrollViewer? _last;
    double _remainder;
    bool _disposed;

    internal DragWheelScroll(FrameworkElement[] areas, Action<FrameworkElement>? scrolled = null)
    {
        _areas = areas; _scrolled = scrolled; _callback = OnMouse;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _callback, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) App.Log(new InvalidOperationException($"Drag wheel hook: {Marshal.GetLastPInvokeError()}"));
    }
    IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (int)message == Native.WM_MOUSEWHEEL && !_disposed)
        {
            try
            {
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
                if (pid == Environment.ProcessId)
                {
                    var mouse = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(data);
                    foreach (var area in _areas)
                    {
                        if (!area.IsVisible || PresentationSource.FromVisual(area) == null) continue;
                        var point = area.PointFromScreen(new Point(mouse.pt.X, mouse.pt.Y));
                        if (!new Rect(0, 0, area.ActualWidth, area.ActualHeight).Contains(point)) continue;
                        var scroll = area as ScrollViewer ?? area.FindDescendant<ScrollViewer>();
                        if (scroll == null || scroll.ScrollableHeight <= 0) continue;
                        int delta = DecodeDelta(mouse.mouseData);
                        area.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                        {
                            if (_disposed) return;
                            if (_last != scroll) { _last = scroll; _remainder = 0; }
                            ApplyWheel(scroll, delta, SystemParameters.WheelScrollLines, ref _remainder);
                            _scrolled?.Invoke(area);
                        });
                        return new IntPtr(1);
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
        return Native.CallNextHookEx(_hook, code, message, data);
    }
    internal static int DecodeDelta(uint mouseData) => unchecked((short)(mouseData >> 16));
    internal static int WheelSteps(int delta, ref double remainder)
    {
        remainder += delta / 120.0;
        int steps = (int)remainder; remainder -= steps; return steps;
    }
    internal static void ApplyWheel(ScrollViewer scroll, int delta, int lines, ref double remainder)
    {
        if (lines == 0) return;
        int steps = WheelSteps(delta, ref remainder);
        for (int n = 0; n < Math.Abs(steps); n++)
            if (lines < 0) { if (steps > 0) scroll.PageUp(); else scroll.PageDown(); }
            else for (int line = 0; line < lines; line++) { if (steps > 0) scroll.LineUp(); else scroll.LineDown(); }
    }
    public void Dispose()
    {
        _disposed = true;
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        GC.KeepAlive(_callback);
    }
    internal static void SelfTest(Action<bool, string> check)
    {
        check(DecodeDelta(0x00780000) == 120 && DecodeDelta(0xFF880000) == -120, "drag wheel: signed delta supports both directions");
        double remainder = 0;
        check(WheelSteps(60, ref remainder) == 0 && WheelSteps(60, ref remainder) == 1 && remainder == 0,
            "drag wheel: precise wheel deltas accumulate without losing half-steps");
        check(WheelSteps(-240, ref remainder) == -2 && remainder == 0, "drag wheel: multiple reverse notches retain their magnitude");
        var content = new StackPanel(); for (int i = 0; i < 60; i++) content.Children.Add(new Border { Height = 30 });
        var scroll = new ScrollViewer { Content = content, CanContentScroll = false };
        void Layout() { scroll.Measure(new Size(200, 180)); scroll.Arrange(new Rect(0, 0, 200, 180)); scroll.UpdateLayout(); }
        Layout(); ApplyWheel(scroll, -120, 3, ref remainder); Layout();
        double after = scroll.VerticalOffset;
        check(after > 0, "drag wheel: held-card wheel scrolls the viewport down");
        ApplyWheel(scroll, 120, 3, ref remainder); Layout();
        check(scroll.VerticalOffset < after, "drag wheel: reverse wheel scrolls back up");
        ApplyWheel(scroll, -120, -1, ref remainder); Layout();
        check(scroll.VerticalOffset >= scroll.ViewportHeight * 0.8, "drag wheel: system page scrolling is respected");
        after = scroll.VerticalOffset; ApplyWheel(scroll, -120, 0, ref remainder); Layout();
        check(scroll.VerticalOffset == after, "drag wheel: disabled system wheel scrolling remains disabled");
        long previous = Environment.TickCount64; DragSupport.ScrollEdge(scroll, new Point(40, 179), ref previous); Layout();
        check(scroll.VerticalOffset == after, "drag scroll: edge scrolling is throttled between OLE events");
        using var scoped = new DragWheelScroll([scroll]);
        check(scoped._hook != IntPtr.Zero, "drag wheel: Windows gesture-scoped mouse hook installs successfully (hidden target never consumes input)");
        scoped.Dispose();
        check(scoped._hook == IntPtr.Zero && scoped._disposed, "drag wheel: cancellation/completion removes the hook and ignores queued work");
    }
}
