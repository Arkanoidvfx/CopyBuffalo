using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    readonly DispatcherTimer _dragVisualTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
    ListBoxItem? _liftedCard;
    bool _mergingDrag;
    Native.POINT _ghostPixel;
    bool _ghostPlaced, _textDragFilter;
    TaskCompletionSource? _mergeDone;
    Task _mergeAnimation = Task.CompletedTask;

    void InitializeDragVisual()
    {
        _dragVisualTimer.Tick += (_, _) => PositionDragVisual();
        GiveFeedback += (_, _) => PositionDragVisual(); // Follow actual OLE movement; timer is the fallback.
        DragGhostCard.SizeChanged += (_, _) => _ghostPlaced = false;
        DragGhost.Opened += (_, _) =>
        {
            // The preview HWND must let OLE hit the actual card underneath it.
            if (PresentationSource.FromVisual(DragGhostCard) is HwndSource source)
            {
                long style = (long)Native.GetWindowLongPtr(source.Handle, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(source.Handle, Native.GWL_EXSTYLE, new IntPtr(style | 0x20 | 0x08000000));
            }
            _ghostPlaced = false; PositionDragVisual();
        };
    }

    void BeginDragVisual(ListBox list, ClipItem item)
    {
        FinishDragVisual();
        ClosePeek(); HideShortcuts();
        SetTextDragFilter(!item.IsImage && !item.IsMedia);
        _liftedCard = list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        if (_liftedCard != null) Motion.To(_liftedCard, OpacityProperty, 0.38, Motion.Fast);
        string text = item.IsMedia ? item.FileName ?? item.TileTitle : item.Preview;
        DragGhostText.FontFamily = (FontFamily)FindResource(item.IsMono ? "MonoFont" : "TextFont");
        DragGhostText.Text = text.Length > 180 ? text[..180] + "…" : text;
        DragGhost.PlacementTarget = this;
        SetDragVisualStatus(false, false);
        DragGhostShift.BeginAnimation(TranslateTransform.XProperty, null);
        DragGhostShift.BeginAnimation(TranslateTransform.YProperty, null);
        DragGhostShift.X = DragGhostShift.Y = 0;
        PositionDragVisual();
        DragGhost.IsOpen = true;
        Motion.To(DragGhostCard, OpacityProperty, 1, Motion.Normal, from: 0);
        Motion.To(DragGhostScale, ScaleTransform.ScaleXProperty, 1, Motion.Normal, from: 0.94);
        Motion.To(DragGhostScale, ScaleTransform.ScaleYProperty, 1, Motion.Normal, from: 0.94);
        Motion.To(DragGhostTilt, RotateTransform.AngleProperty, -2, Motion.Normal, from: 0);
        _dragVisualTimer.Start();
    }

    void PositionDragVisual()
    {
        if (_mergingDrag || !Native.GetCursorPos(out var cursor) || PresentationSource.FromVisual(this) == null) return;
        if (DragSupport.MoveGhost(DragGhostCard, cursor, ref _ghostPixel, ref _ghostPlaced)) return;
        var local = PointFromScreen(new Point(cursor.X, cursor.Y));
        DragGhost.HorizontalOffset = local.X; DragGhost.VerticalOffset = local.Y;
    }

    void SetDragVisualStatus(bool replace, bool move, bool choose = false)
    {
        DragGhostStatus.Text = replace ? "Отпустите — заменить" : move ? "Отпустите — переместить"
            : choose ? "Перетащите на нужное значение" : "Наведите на карточку";
        DragGhostGlyph.Text = replace ? "\uE710" : move ? "\uE8AB" : choose ? "\uE70F" : "\uE7C3";
    }

    void MergeDragVisual(FrameworkElement? target, Point? destination = null)
    {
        bool connected = target != null && PresentationSource.FromVisual(target) != null;
        if (!connected && destination == null) { EndDragVisual(); return; }
        if (!DragGhost.IsOpen) { if (connected) Motion.Pop(target!, 1.025, 1); return; }
        _dragVisualTimer.Stop();
        _mergingDrag = true;
        RestoreLiftedCard();
        _mergeDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _mergeAnimation = _mergeDone.Task;
        var center = destination ?? target!.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), this);
        var currentCenter = PointFromScreen(DragGhostCard.PointToScreen(new Point(DragGhostCard.ActualWidth / 2, DragGhostCard.ActualHeight / 2)));
        var delta = center - currentCenter;
        Motion.To(DragGhostShift, TranslateTransform.XProperty, delta.X, Motion.Slow);
        Motion.To(DragGhostShift, TranslateTransform.YProperty, delta.Y, Motion.Slow);
        Motion.To(DragGhostTilt, RotateTransform.AngleProperty, 0, Motion.Slow);
        Motion.To(DragGhostScale, ScaleTransform.ScaleXProperty, 0.2, Motion.Slow);
        Motion.To(DragGhostScale, ScaleTransform.ScaleYProperty, 0.2, Motion.Slow);
        if (connected) Motion.Pop(target!, 1.035, 1);
        Motion.To(DragGhostCard, OpacityProperty, 0, Motion.Slow, done: FinishDragVisual);
    }

    void RestoreLiftedCard()
    {
        if (_liftedCard == null) return;
        _liftedCard.BeginAnimation(OpacityProperty, null);
        _liftedCard.Opacity = 1;
        _liftedCard = null;
    }

    void EndDragVisual()
    {
        if (_mergingDrag) return;
        _dragVisualTimer.Stop();
        RestoreLiftedCard();
        if (DragGhost.IsOpen) Motion.To(DragGhostCard, OpacityProperty, 0, Motion.Fast, done: FinishDragVisual);
        else FinishDragVisual();
    }

    void FinishDragVisual()
    {
        _dragVisualTimer.Stop();
        RestoreLiftedCard();
        DragGhostCard.BeginAnimation(OpacityProperty, null);
        DragGhostCard.Opacity = 0;
        DragGhost.IsOpen = false;
        _ghostPlaced = false;
        _mergingDrag = false;
        SetTextDragFilter(false);
        _parsedItem = null; _parsedOriginal = _parsedSource = null; _parsedChanges = null;
        _mergeDone?.TrySetResult();
        _mergeDone = null;
        _mergeAnimation = Task.CompletedTask;
    }

    void SetTextDragFilter(bool enabled)
    {
        if (_textDragFilter == enabled) return;
        _textDragFilter = enabled;
        var selected = List.SelectedItem;
        _view.Refresh(); _seriesView?.Refresh(); _mergeView?.Refresh(); UpdatePinnedLayout(); UpdateEmptyState();
        if (selected != null && List.Items.Contains(selected)) List.SelectedItem = selected;
        if (IsVisible) Active.UpdateLayout();
    }

    void CheckHoverSuppression(Action<bool, string> check)
    {
        var tip = new ToolTip { Content = new Image() };
        var target = new Image { ToolTip = tip };
        var progress = new Border { Name = "ZoomProgress", Visibility = Visibility.Collapsed, RenderTransform = new ScaleTransform(0, 1) };
        var grid = new Grid(); grid.Children.Add(target); grid.Children.Add(progress);
        try
        {
            foreach (string mode in new[] { "drag", "merge animation", "Alt selection" })
            {
                _dragging = mode == "drag"; _mergingDrag = mode == "merge animation"; _collectingMerge = mode == "Alt selection";
                StartZoom(target);
                check(_zoomTarget == null && _zoomFull == null && !_zoomTimer.IsEnabled && progress.Visibility == Visibility.Collapsed,
                    $"hover zoom: {mode} blocks scheduling/decoding before hover zoom starts");
                _zoomTarget = target; _zoomTip = tip; _zoomProgress = progress;
                progress.Visibility = Visibility.Visible; _zoomTimer.Start(); _peekTimer.Start();
                OpenZoom();
                check(_zoomTarget == null && !_zoomTimer.IsEnabled && !_peekTimer.IsEnabled && !tip.IsOpen && progress.Visibility == Visibility.Collapsed,
                    $"hover zoom: {mode} cancels a previously queued preview and its progress indicator");
            }
            progress.Name = "GraphZoomProgress"; StartZoom(target);
            check(!_zoomTimer.IsEnabled && _zoomTarget == null, "hover zoom: graph previews obey the same Alt/drag suppression");
        }
        finally { _dragging = _mergingDrag = _collectingMerge = false; CancelZoom(); }
        if (!HoverPreviewBlocked)
        {
            try
            {
                StartZoom(target);
                if (!HoverPreviewBlocked) // Physical input may change while the synthetic check runs.
                    check(_zoomTarget == target && _zoomTimer.IsEnabled, "hover zoom: ordinary hover scheduling resumes after drag/Alt state ends");
            }
            finally { CancelZoom(); }
        }
    }

    void CheckDragVisual(Action<bool, string> check)
    {
        check(!DragGhost.IsHitTestVisible && !DragGhost.Focusable, "карточка перетаскивания не перехватывает мышь и фокус");
        check(DragGhostCard.CacheMode is BitmapCache && _dragVisualTimer.Interval.TotalMilliseconds == 8,
            "drag performance: floating card is bitmap-cached and follows the pointer at 8ms intervals");
        Native.POINT pixel = default; bool placed = false;
        var foreground = Native.GetForegroundWindow();
        check(!DragSupport.MoveGhost(DragGhostCard, new Native.POINT { X = -200, Y = 300 }, ref pixel, ref placed)
            && foreground == Native.GetForegroundWindow(), "drag ghost: disconnected visual has no native movement or focus side effects");
        var media = new[]
        {
            new ClipItem { Text = "synthetic image", ImageHash = "fake", Pinned = true },
            new ClipItem { Text = "synthetic video", MediaKind = "video", Pinned = true },
            new ClipItem { Text = "synthetic audio", MediaKind = "audio", Pinned = true },
            new ClipItem { Text = "synthetic folder", MediaKind = "folder", Pinned = true },
        };
        foreach (var item in media) _store.Items.Add(item);
        var before = _store.Items.ToArray();
        try
        {
            SetTextDragFilter(true);
            check(List.Items.Cast<ClipItem>().All(i => !i.IsImage && !i.IsMedia) && _pinned.OfType<ClipItem>().All(i => !i.IsImage && !i.IsMedia),
                "drag filter: images, video, audio and file references disappear in history and pinned views");
            check(_store.Items.SequenceEqual(before) && List.Items.Count > 0,
                "drag filter: original records/order are untouched and compatible text stays visible");
            _mergingDrag = true; EndDragVisual();
            check(_textDragFilter && !List.Items.Contains(media[0]), "drag filter: incompatible records stay hidden until the merge animation finishes");
            FinishDragVisual();
            check(media.All(i => List.Items.Contains(i) && _pinned.Contains(i)), "drag filter: completing a drop restores all hidden records");
            SetTextDragFilter(true); EndDragVisual();
            check(!_textDragFilter && media.All(List.Items.Contains), "drag filter: cancelling a drag restores media immediately when no preview is open");
            SetTextDragFilter(true); _sourceFilter = "missing-source"; _view.Refresh(); UpdatePinnedLayout();
            check(List.Items.Count == 0 && !_pinned.OfType<ClipItem>().Any(), "drag filter: source/search filtering is preserved during a drag");
        }
        finally
        {
            _sourceFilter = null; SetTextDragFilter(false);
            foreach (var item in media) _store.Items.Remove(item);
            UpdatePinnedLayout();
        }
        const string path = @"D:\Test Clips\clip.mp4";
        var plain = new ClipItem { Text = string.Concat(Enumerable.Repeat("обычный текст без слотов ", 650)) };
        var empty = ValueChanges(plain, path);
        check(empty.Count == 0 && ReferenceEquals(empty, ValueChanges(plain, path)),
            "drag performance: a non-mergeable text target is parsed once, including negative results");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) _ = TextSubstitution.Find(plain.Text, path);
        double baseline = watch.Elapsed.TotalMilliseconds; watch.Restart();
        for (int i = 0; i < 100; i++) _ = ValueChanges(plain, path);
        double cached = watch.Elapsed.TotalMilliseconds;
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "drag-performance.txt"),
            $"Synthetic repeated-target parsing (100 iterations; {plain.Text.Length} chars):\nuncached_ms={baseline:F3}\ncached_ms={cached:F3}\nThis measures parser work, not real pointer FPS. No input/focus automation.\n");
        plain.ReplaceText("run \"<clip>\"");
        var changed = ValueChanges(plain, path);
        check(changed.Count > 0 && !ReferenceEquals(empty, changed) && ReferenceEquals(changed, ValueChanges(plain, path)),
            "drag performance: editing the target invalidates the cache; unchanged positive previews are reused");
        check(!ReferenceEquals(changed, ValueChanges(plain, @"D:\other.mp4")), "drag performance: changing the source path invalidates cached substitutions");
        SetDragVisualStatus(true, false);
        check(DragGhostStatus.Text == "Отпустите — заменить", "подсказка показывает замену значения");
        SetDragVisualStatus(false, true);
        check(DragGhostStatus.Text == "Отпустите — переместить", "подсказка отличает перестановку от замены");
        SetDragVisualStatus(false, false, true);
        check(DragGhostStatus.Text == "Перетащите на нужное значение", "неоднозначная цель предлагает точный выбор");
        var lifted = new ListBoxItem { Opacity = 0.38 };
        _liftedCard = lifted;
        _mergingDrag = true;
        _mergeDone = new();
        var completion = _mergeDone.Task;
        _dragVisualTimer.Start();
        EndDragVisual();
        check(_mergingDrag && !completion.IsCompleted, "отпускание мыши не обрывает анимацию слияния");
        FinishDragVisual();
        check(lifted.Opacity == 1 && !_mergingDrag && !_dragVisualTimer.IsEnabled && completion.IsCompleted
            && !DragGhost.IsOpen, "отмена и завершение возвращают источник и останавливают таймер");
        bool motion = Motion.Enabled;
        try
        {
            Motion.Enabled = false;
            Motion.To(DragGhostScale, ScaleTransform.ScaleXProperty, 1, Motion.Slow, from: 0.2);
            check(DragGhostScale.ScaleX == 1 && !DragGhostScale.HasAnimatedProperties,
                "отключённые анимации применяют состояние без движения");
        }
        finally { Motion.Enabled = motion; }
    }
}
