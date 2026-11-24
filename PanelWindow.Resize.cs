using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ArkCopyBuffer;

/// <summary>Панель тянут за край или угол. Размер живёт до скрытия: следующий показ снова обычный (ShowAt).</summary>
partial class PanelWindow
{
    const double GripOutside = 6, GripInside = 4, GripCorner = 14, CornerInside = 10; // внутри не шире поля панели: рядом полоса прокрутки
    Size _defaultSize; // из разметки
    bool _sizing;         // панель тянут: за край или за шапку
    ResizeDrag? _resize;  // тянут за край
    Native.RECT? _sizedTo;   // где окно оставили в этом показе: настройки при закрытии возвращают его сюда, а не к каретке

    /// <summary>Код WM_NCHITTEST для точки окна (DIP): рамка идёт по краю видимой панели, а не окна с тенью. 0 — не край.</summary>
    static int EdgeHit(double x, double y, double width, double height)
    {
        const double m = ShadowMargin;
        double right = width - m, bottom = height - m;
        if (x < m - GripOutside || x >= right + GripOutside || y < m - GripOutside || y >= bottom + GripOutside) return 0;
        int cornerCol = x < m + CornerInside ? 1 : x >= right - CornerInside ? 2 : 0, cornerRow = y < m + CornerInside ? 1 : y >= bottom - CornerInside ? 2 : 0;
        if (cornerCol != 0 && cornerRow != 0) return 9 + cornerCol + 3 * cornerRow; // угол скруглён: по одной рамке в него трудно попасть
        int col = x < m + GripInside ? 1 : x >= right - GripInside ? 2 : 0;
        int row = y < m + GripInside ? 1 : y >= bottom - GripInside ? 2 : 0;
        if (col == 0 && row == 0) return 0;
        if (col == 0) col = x < m + GripCorner ? 1 : x >= right - GripCorner ? 2 : 0;
        else if (row == 0) row = y < m + GripCorner ? 1 : y >= bottom - GripCorner ? 2 : 0;
        return 9 + col + 3 * row; // HTLEFT = 10 … HTBOTTOMRIGHT = 17, слева направо по рядам
    }

    IntPtr ResizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETCURSOR = 0x20, WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1, WM_ENTERSIZEMOVE = 0x231, WM_EXITSIZEMOVE = 0x232;
        switch (msg)
        {
            // Под настройки окно подгоняет FitSettings, вручную его тогда не тянут.
            case WM_NCHITTEST when _resize == null && SettingsFlyout.Visibility != Visibility.Visible && PointInWindow(lParam) is { } p:
                int hit = EdgeHit(p.X, p.Y, ActualWidth - 2 * _pad, ActualHeight - 2 * _pad);
                handled = hit != 0;
                return hit;
            // У окна нет рамки, поэтому Windows оставляет на краях стрелку.
            case WM_SETCURSOR when (short)(long)lParam is >= 10 and <= 17 and var edge:
                Native.SetCursor(Native.LoadCursor(IntPtr.Zero, edge switch { 10 or 11 => 32644, 12 or 15 => 32645, 13 or 17 => 32642, _ => 32643 })); // IDC_SIZEWE, NS, NWSE, NESW
                handled = true;
                return 1;
            case WM_NCLBUTTONDOWN when (int)wParam is >= 10 and <= 17 and var edge:
                BeginResize(edge);
                handled = true;
                return 0;
            case WM_ENTERSIZEMOVE: // панель тащат за шапку
                _sizing = true;
                ClosePeek();
                StartPlate(0);
                break;
            case WM_EXITSIZEMOVE:
                _sizing = false;
                if (ShellPixels() is { } rect) _sizedTo = rect;
                StartOutsideTimer();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Что тянут, где нажали и где панель была (без тени), в физических пикселях.</summary>
    sealed record ResizeDrag(int Hit, Native.POINT Grab, Native.RECT Start, Native.RECT Canvas, Native.RECT Work, double Scale)
    {
        public Native.RECT Panel = Start;
        public bool Left => Hit is 10 or 13 or 16;
        public bool Right => Hit is 11 or 14 or 17;
        public bool Top => Hit is 12 or 13 or 14;
        public bool Bottom => Hit is 15 or 16 or 17;
    }

    /// <summary>
    /// Тянем не окно, а панель внутри него. Прозрачное окно Windows двигает раньше, чем WPF дорисует новый размер: при растягивании
    /// влево и вверх неподвижный край прыгал до 15 px. Поэтому окно один раз вырастает до края экрана, и каждый кадр рисуется целиком.
    /// </summary>
    void BeginResize(int hit)
    {
        SettlePlate();
        if (_resize != null || !IsShown || !Native.GetWindowRect(_hwnd, out var window) || !Native.GetCursorPos(out var grab)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int m = (int)Math.Round(ShadowMargin * scale);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromPoint(grab, Native.MONITOR_DEFAULTTONEAREST), ref info);
        var work = info.rcWork;
        var panel = new Native.RECT { Left = window.Left + m, Top = window.Top + m, Right = window.Right - m, Bottom = window.Bottom - m };
        var drag = new ResizeDrag(hit, grab, panel, window, work, scale);
        var canvas = new Native.RECT
        {
            Left = drag.Left ? Math.Min(window.Left, work.Left - m) : window.Left,
            Top = drag.Top ? Math.Min(window.Top, work.Top - m) : window.Top,
            Right = drag.Right ? Math.Max(window.Right, work.Right + m) : window.Right,
            Bottom = drag.Bottom ? Math.Max(window.Bottom, work.Bottom + m) : window.Bottom,
        };
        _resize = drag with { Canvas = canvas };
        _sizing = true;
        ClosePeek();
        DropFlows();
        Cursor = hit switch { 10 or 11 => Cursors.SizeWE, 12 or 15 => Cursors.SizeNS, 13 or 17 => Cursors.SizeNWSE, _ => Cursors.SizeNESW };
        ForceCursor = true;
        var picture = PanelPicture(panel, scale, ShadowMargin);
        LayOutResize(_resize);
        MoveWithPicture(canvas, panel, picture, m);
        if (_vfxWindow?.IsVisible == true) _vfxWindow.Grow(canvas, panel, scale);
        CaptureMouse();
        StartPlate(hit);
        CompositionTarget.Rendering += ResizeFrame;
    }

    void ResizeFrame(object? sender, EventArgs e)
    {
        if (_resize is not { } drag) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed) { EndResize(); return; }
        if (!Native.GetCursorPos(out var at)) return;
        int dx = at.X - drag.Grab.X, dy = at.Y - drag.Grab.Y;
        int minWidth = (int)((MinWidth - 2 * ShadowMargin) * drag.Scale), minHeight = (int)((MinHeight - 2 * ShadowMargin) * drag.Scale);
        var r = drag.Start;
        if (drag.Left) r.Left = Math.Clamp(r.Left + dx, Math.Min(r.Left, drag.Work.Left), r.Right - minWidth);
        if (drag.Right) r.Right = Math.Clamp(r.Right + dx, r.Left + minWidth, Math.Max(r.Right, drag.Work.Right));
        if (drag.Top) r.Top = Math.Clamp(r.Top + dy, Math.Min(r.Top, drag.Work.Top), r.Bottom - minHeight);
        if (drag.Bottom) r.Bottom = Math.Clamp(r.Bottom + dy, r.Top + minHeight, Math.Max(r.Bottom, drag.Work.Bottom));
        if (r.Equals(drag.Panel)) return;
        double wider = (r.Right - r.Left - drag.Panel.Right + drag.Panel.Left) / drag.Scale;
        drag.Panel = r;
        // Клетки сетки переезжают, только когда меняется число столбцов.
        bool moves = wider != 0 && (!GridTab || ColumnsFor(wider) != Columns);
        var places = moves ? Places() : null;
        if (moves) WatchTexts();
        LayOutResize(drag);
        if (places == null) return;
        Root.UpdateLayout();
        Slide(places);
        GlideTexts();
    }

    void LayOutResize(ResizeDrag drag)
    {
        var r = drag.Panel;
        Root.HorizontalAlignment = HorizontalAlignment.Left;
        Root.VerticalAlignment = VerticalAlignment.Top;
        Root.Margin = new Thickness((r.Left - drag.Canvas.Left) / drag.Scale, (r.Top - drag.Canvas.Top) / drag.Scale, 0, 0);
        Root.Width = (r.Right - r.Left) / drag.Scale;
        Root.Height = (r.Bottom - r.Top) / drag.Scale;
        _vfxWindow?.Follow(r);
    }

    /// <summary>Отпустили, нажали Esc (cancel: размер прежний) или панель скрылась: окно снова по размеру панели.</summary>
    void EndResize(bool cancel = false)
    {
        if (_resize is not { } drag) return;
        _resize = null;
        CompositionTarget.Rendering -= ResizeFrame;
        ClearValue(CursorProperty);
        ClearValue(ForceCursorProperty);
        ReleaseMouseCapture();
        var r = cancel ? drag.Start : drag.Panel;
        if (cancel) { drag.Panel = r; LayOutResize(drag); Root.UpdateLayout(); }
        // Отпустили: край ещё возвращается, и место под наклон остаётся, пока он не успокоится (SettlePlate).
        _pad = _plateRunning && IsShown ? PlatePad : 0;
        double margin = ShadowMargin + _pad;
        var picture = PanelPicture(r, drag.Scale, margin);
        Root.ClearValue(HorizontalAlignmentProperty);
        Root.ClearValue(VerticalAlignmentProperty);
        Root.ClearValue(WidthProperty);
        Root.ClearValue(HeightProperty);
        Root.Margin = new Thickness(margin);
        MoveWithPicture(Inflate(r, margin * drag.Scale), r, picture, (int)Math.Round(margin * drag.Scale));
        var window = Inflate(r, ShadowMargin * drag.Scale);
        EndFlows();
        _normalSize = new Size((window.Right - window.Left) / drag.Scale, (window.Bottom - window.Top) / drag.Scale);
        _sizedTo = window;
        _sizing = false;
        StartOutsideTimer();
        _vfxWindow?.Shrink(r, drag.Scale);
    }

    /// <summary>Снимок панели с тенью: им окно закрыто, пока WPF не нарисует первый кадр нового размера.</summary>
    System.Windows.Media.Imaging.BitmapSource PanelPicture(Native.RECT panel, double scale, double margin)
    {
        double w = (panel.Right - panel.Left) / scale + 2 * margin, h = (panel.Bottom - panel.Top) / scale + 2 * margin;
        var offset = VisualTreeHelper.GetOffset(Root); // VisualBrush рисует Root вместе с его местом в окне
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(Root)
            {
                Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X - margin, offset.Y - margin, w, h),
            }, null, new Rect(0, 0, w, h));
        int m = (int)Math.Round(margin * scale);
        var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(panel.Right - panel.Left + 2 * m, panel.Bottom - panel.Top + 2 * m, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        picture.Render(visual);
        return picture;
    }

    /// <summary>Место, размер и картинка окна меняются одним вызовом: иначе старая картинка на кадр показалась бы в новом месте.</summary>
    void MoveWithPicture(Native.RECT window, Native.RECT panel, System.Windows.Media.Imaging.BitmapSource picture, int margin)
    {
        if (Native.UpdateLayered(_hwnd, window, picture, panel.Left - margin - window.Left, panel.Top - margin - window.Top))
            Native.SendWindowPosChanged(_hwnd, window); // о размере, заданном вместе с картинкой, Windows окну не сообщает
        else
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, window.Left, window.Top, window.Right - window.Left, window.Bottom - window.Top, Native.SWP_NOACTIVATE);
    }

    /// <summary>Экранная точка из lParam (две знаковые половины: монитор может быть левее или выше нуля) в DIP окна.</summary>
    Point? PointInWindow(IntPtr lParam)
    {
        int packed = unchecked((int)(long)lParam);
        return ToWindow((short)packed, (short)(packed >> 16));
    }

    Point? ToWindow(int x, int y)
    {
        if (!Native.GetWindowRect(_hwnd, out var r)) return null;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point((x - r.Left) / dpi.DpiScaleX - _pad, (y - r.Top) / dpi.DpiScaleY - _pad);
    }

    /// <summary>Окно панели с тенью, без места под изгиб.</summary>
    Native.RECT? ShellPixels() => PanelPixels() is { } panel ? Inflate(panel, ShadowMargin * VisualTreeHelper.GetDpi(this).DpiScaleX) : null;

    internal static void SelfTestResize(string directory, Action<bool, string> check)
    {
        int[] hits = [EdgeHit(16, 246, 392, 492), EdgeHit(375, 246, 392, 492), EdgeHit(196, 16, 392, 492), EdgeHit(16, 16, 392, 492),
            EdgeHit(375, 16, 392, 492), EdgeHit(196, 475, 392, 492), EdgeHit(16, 475, 392, 492), EdgeHit(375, 475, 392, 492)];
        check(hits.SequenceEqual([10, 11, 12, 13, 14, 15, 16, 17]) && EdgeHit(16, 28, 392, 492) == 13 && EdgeHit(365, 475, 392, 492) == 17,
            "размер панели: края и углы тянутся в свою сторону");
        check(EdgeHit(368, 468, 392, 492) == 17 && EdgeHit(24, 24, 392, 492) == 13 && EdgeHit(360, 460, 392, 492) == 0, "размер панели: угол легко поймать и внутри панели");
        foreach (string sample in new[] { "Короткая", "Несколько слов, которые не помещаются в одну строку и переносятся", "first line\nsecond line that is long enough to wrap again", "漢字かな交じり文は単語の間に空白がない長い一行です" })
            foreach (double width in new double[] { 120, 190, 260, 400 })
            {
                var text = new TextBlock { Text = sample, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
                TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
                text.Measure(new Size(width, 1000)); text.Arrange(new Rect(0, 0, width, 1000));
                var rows = Wrap(MeasureWords(text), width, text.LineHeight);
                check((int)(rows[^1].Y / 20) + 1 == (int)Math.Round(text.DesiredSize.Height / 20), $"размер панели: переносы слов как у WPF, {sample[..5]} {width}");
            }
        check(EdgeHit(196, 246, 392, 492) == 0 && EdgeHit(2, 246, 392, 492) == 0 && EdgeHit(16, 2, 392, 492) == 0 && EdgeHit(371, 246, 392, 492) == 0,
            "размер панели: содержимое и поле тени за край не считаются");

        var store = new HistoryStore(System.IO.Path.Combine(directory, "resize", "history.dat"));
        for (int i = 0; i < 12; i++) { store.Add("pinned " + i, null, null, DateTime.Now.AddMinutes(i), null); store.TogglePin(store.Items.First(x => x.Text == "pinned " + i)); }
        var panel = new PanelWindow(store, new SymbolCatalog(System.IO.Path.Combine(directory, "resize", "symbols.json")));
        try
        {
            panel.Animations = false;
            new WindowInteropHelper(panel).EnsureHandle();
            panel.Root.Opacity = 1;
            panel.TabPinned.IsChecked = true;
            int Columns(double width) { panel.Root.Measure(new Size(width, 700)); panel.Root.Arrange(new Rect(0, 0, width, 700)); panel.Root.UpdateLayout(); return panel.Columns; }
            int OnScreen()
            {
                var tiles = panel._pinned.OfType<ClipItem>().Select(i => (ListBoxItem)panel.PinnedList.ItemContainerGenerator.ContainerFromItem(i)).ToList();
                double top = tiles[0].TranslatePoint(default, panel.PinnedList).Y;
                return tiles.Count(t => Math.Abs(t.TranslatePoint(default, panel.PinnedList).Y - top) < 1);
            }
            foreach (double width in new double[] { 392, 470, 520, 720 })
                check(Columns(width) == OnScreen(), $"размер панели: стрелки ходят по видимым столбцам плиток, ширина {width}");
            check(Columns(392) == 3, "размер панели: в обычной ширине три плитки в ряд");

            panel._normalSize = new Size(700, 800);
            panel._sizedTo = new Native.RECT { Left = 1, Top = 1, Right = 701, Bottom = 801 };
            panel.ResetSize();
            check(panel._normalSize == new Size(392, 528) && panel._sizedTo == null && panel.Width == 392 && panel.Height == 528,
                "размер панели: при новом показе снова обычный");
        }
        finally { panel.Close(); }
    }

    void ResetSize()
    {
        EndResize();
        SettlePlate();
        _sizedTo = null;
        _normalSize = _defaultSize;
        Width = _normalSize.Width; Height = _normalSize.Height;
    }
}
