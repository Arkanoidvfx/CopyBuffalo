using Interop.UIAutomationClient;
using static ArkCopyBuffer.Native;

namespace ArkCopyBuffer;

/// <summary>
/// Где показать панель: у текстовой каретки, как Win+V. Координаты — физические пиксели экрана.
/// 1) Win32-каретка (GetGUIThreadInfo) — классические приложения.
/// 2) UI Automation TextPattern2 — Chromium, Electron, WinUI.
/// 3) Иначе — курсор мыши.
/// </summary>
static class CaretLocator
{
    const int UiaTimeoutMs = 120;
    static CUIAutomation8? _uia;
    /// <summary>Процессы, где UIA не уложился в таймаут: 10 минут для них сразу берём мышь, не ждём снова.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, DateTime> SlowUia = new();

    /// <summary>Первое создание CUIAutomation — сотни мс; делаем его при старте, а не на первом Win+V.</summary>
    public static void WarmUp() => Task.Run(() => { try { _uia ??= new CUIAutomation8(); } catch { } });

    public static (RECT rect, bool isCaret) Locate(IntPtr foreground)
    {
        if (Win32Caret(foreground) is RECT r1) return (r1, true);

        // UIA может зависнуть на подвисшем приложении — ждём не дольше таймаута.
        // ponytail: зависший вызов держит поток пула до ответа приложения; свой поток с отменой — если станет проблемой.
        GetWindowThreadProcessId(foreground, out var pid);
        if (!(SlowUia.TryGetValue(pid, out var until) && until > DateTime.UtcNow))
        {
            var task = Task.Run(UiaCaret);
            if (!task.Wait(UiaTimeoutMs)) SlowUia[pid] = DateTime.UtcNow.AddMinutes(10);
            else if (task.Result is RECT r2) return (r2, true);
        }

        GetCursorPos(out var p);
        return (new RECT { Left = p.X, Top = p.Y, Right = p.X, Bottom = p.Y }, false);
    }

    static RECT? Win32Caret(IntPtr foreground)
    {
        var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info) || info.hwndCaret == IntPtr.Zero)
            return null;
        var c = info.rcCaret;
        if (c.Bottom <= c.Top) return null;
        var tl = new POINT { X = c.Left, Y = c.Top };
        var br = new POINT { X = c.Right, Y = c.Bottom };
        ClientToScreen(info.hwndCaret, ref tl);
        ClientToScreen(info.hwndCaret, ref br);
        // ponytail: у DPI-unaware приложений координаты виртуализированы и могут уехать; проверяем хотя бы, что точка на экране.
        return OnAnyMonitor(tl) ? new RECT { Left = tl.X, Top = tl.Y, Right = br.X, Bottom = br.Y } : null;
    }

    static RECT? UiaCaret()
    {
        try
        {
            _uia ??= new CUIAutomation8();
            var focused = _uia.GetFocusedElement();
            if (focused?.GetCurrentPattern(UIA_PatternIds.UIA_TextPattern2Id) is not IUIAutomationTextPattern2 tp) return null;

            var caret = tp.GetCaretRange(out _);
            // Пустой диапазон каретки часто не имеет прямоугольников: берём символ справа, иначе символ слева.
            var right = caret.Clone();
            right.ExpandToEnclosingUnit(TextUnit.TextUnit_Character);
            if (Rect(right) is RECT r) return new RECT { Left = r.Left, Top = r.Top, Right = r.Left, Bottom = r.Bottom };

            var left = caret.Clone();
            left.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -1);
            if (Rect(left) is RECT l) return new RECT { Left = l.Right, Top = l.Top, Right = l.Right, Bottom = l.Bottom };
            return null;
        }
        catch { return null; }
    }

    static RECT? Rect(IUIAutomationTextRange range)
    {
        if (range.GetBoundingRectangles() is not double[] b || b.Length < 4 || b[3] <= 0) return null;
        var r = new RECT { Left = (int)b[0], Top = (int)b[1], Right = (int)(b[0] + b[2]), Bottom = (int)(b[1] + b[3]) };
        return OnAnyMonitor(new POINT { X = r.Left, Y = r.Top }) ? r : null;
    }

    static bool OnAnyMonitor(POINT p) => MonitorFromPoint(p, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
}
