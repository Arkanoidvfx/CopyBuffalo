using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArkCopyBuffer;

partial class TextMergeWindow : Window
{
    readonly HistoryStore _store;
    readonly TextMerge _model;
    readonly Stack<TextMerge.State> _undo = [], _redo = [];
    readonly DispatcherTimer _pointer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
    readonly TranslateTransform _ghostShift = new();
    readonly ScaleTransform _ghostScale = new();
    Point _dragStart;
    MergePart? _dragPart;
    FrameworkElement? _lifted;
    ListBoxItem? _orderTarget;
    bool _ready, _saving, _dragging, _landing, _after;
    Native.POINT _ghostPixel;
    bool _ghostPlaced;
    long _lastDragScroll;
    internal event Action? Completed;

    internal TextMergeWindow(HistoryStore store, IEnumerable<string> texts)
    {
        _store = store; _model = new(texts);
        InitializeComponent();
        Sources.ItemsSource = _model.Parts; SlotList.ItemsSource = _model.Slots;
        Ghost.PlacementTarget = this;
        GhostCard.RenderTransform = new TransformGroup { Children = { _ghostScale, _ghostShift } };
        GhostCard.SizeChanged += (_, _) => _ghostPlaced = false;
        _pointer.Tick += (_, _) => PositionGhost();
        GiveFeedback += (_, _) => PositionGhost();
        Ghost.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(GhostCard) is HwndSource source)
            {
                long style = (long)Native.GetWindowLongPtr(source.Handle, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(source.Handle, Native.GWL_EXSTYLE, new IntPtr(style | 0x20 | 0x08000000));
            }
            _ghostPlaced = false; PositionGhost();
        };
        _ready = true; UpdateResult();
        SourceInitialized += (_, _) => { if (App.IsDarkTheme) Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle); };
        Loaded += (_, _) =>
        {
            var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var transform = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
            var bounds = new Rect(transform.Transform(new Point(area.Left, area.Top)), transform.Transform(new Point(area.Right, area.Bottom)));
            MinWidth = Math.Min(MinWidth, bounds.Width); MinHeight = Math.Min(MinHeight, bounds.Height);
            Width = Math.Min(Width, bounds.Width); Height = Math.Min(Height, bounds.Height);
            Left = Math.Clamp(Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - Width));
            Top = Math.Clamp(Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - Height));
            Sources.SelectedIndex = 0; Sources.Focus(); Motion.Enter(Sheet, dy: 6);
        };
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        Closed += (_, _) => EndGhost();
        PreviewKeyDown += (_, e) =>
        {
            if (_saving) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape && !_dragging) { Close(); e.Handled = true; }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (key == Key.Enter) { Merge_Click(this, e); e.Handled = true; }
                else if (key == Key.Z) { Undo_Click(this, e); e.Handled = true; }
                else if (key == Key.Y) { Redo_Click(this, e); e.Handled = true; }
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Up or Key.Down)
            { MoveSelected(key == Key.Up ? -1 : 1); e.Handled = true; }
        };
    }

    void UpdateResult()
    {
        _ready = false; UseSlots.IsChecked = _model.SubstitutePaths; _ready = true;
        _model.Refresh(); Result.Text = _model.Build();
        Result.FontFamily = (FontFamily)FindResource(_model.Slots.Count > 0 && _model.SubstitutePaths ? "MonoFont" : "TextFont");
        ResultCount.Text = $"{_model.Parts.Count} записей · {Result.Text.Length:N0} символов";
        NoSlots.Visibility = _model.Slots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AssignOrderButton.IsEnabled = _model.SubstitutePaths && _model.Slots.Count > 0 && _model.Parts.Any(p => p.IsPath);
        UndoButton.IsEnabled = _undo.Count > 0; RedoButton.IsEnabled = _redo.Count > 0;
        MergeButton.IsEnabled = !_saving && !string.IsNullOrWhiteSpace(Result.Text);
    }
    void SetStatus(string text, bool error = false)
    { Status.Text = text; Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "CriticalText" : "TextSecondary"); }

    bool Change(Action action, string message)
    {
        if (_saving) return false;
        var state = _model.Capture(); var selected = Sources.SelectedItem;
        try
        {
            action(); _undo.Push(state); _redo.Clear(); UpdateResult();
            if (selected is MergePart part && _model.Parts.Contains(part)) Sources.SelectedItem = part;
            SetStatus(message); Motion.Enter(Result, dy: 3); return true;
        }
        catch (InvalidOperationException ex) { SetStatus(ex.Message, true); return false; }
    }
    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _undo.Count == 0) return;
        _redo.Push(_model.Capture()); _model.Restore(_undo.Pop()); UpdateResult(); SetStatus("Изменение отменено."); Motion.Enter(Result, dy: 3);
    }
    void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _redo.Count == 0) return;
        _undo.Push(_model.Capture()); _model.Restore(_redo.Pop()); UpdateResult(); SetStatus("Изменение повторено."); Motion.Enter(Result, dy: 3);
    }
    void Mode_Changed(object sender, RoutedEventArgs e)
    { if (_ready) Change(() => _model.SubstitutePaths = UseSlots.IsChecked == true, UseSlots.IsChecked == true ? "Пути подставляются в назначенные слоты." : "Все записи объединяются отдельными строками."); }
    void AssignOrder_Click(object sender, RoutedEventArgs e) => Change(_model.AssignInOrder, "Пути назначены по порядку карточек. Любое назначение можно изменить.");
    void Assign_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MergeSlot slot) return;
        if (Sources.SelectedItem is not MergePart { IsPath: true } path)
        { SetStatus("Выберите путь слева, затем нажмите нужный слот. Можно также перетащить путь."); return; }
        if (Change(() => _model.Assign(path, slot), "Путь подставлен. Предпросмотр обновлён.")) Motion.Pop((UIElement)sender, 1.025, 1);
    }
    void Clear_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MergeSlot slot) Change(() => _model.Clear(slot), "Слот освобождён. Путь вернулся отдельной строкой."); }
    void Remove_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is MergePart part) Change(() => _model.Remove(part), "Запись убрана из объединения. В истории она осталась."); }
    void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    void MoveSelected(int delta)
    {
        if (Sources.SelectedItem is not MergePart part) return;
        int old = _model.Parts.IndexOf(part), next = Math.Clamp(old + delta, 0, _model.Parts.Count - 1);
        if (old == next) return;
        MovePart(part, next); Sources.ScrollIntoView(part);
    }
    void MovePart(MergePart part, int index)
    {
        var positions = _model.Parts.Select(p => (Part: p, Card: Sources.ItemContainerGenerator.ContainerFromItem(p) as ListBoxItem))
            .Where(p => p.Card != null && PresentationSource.FromVisual(p.Card) != null)
            .ToDictionary(p => p.Part, p => p.Card!.TranslatePoint(new(), Sources).Y);
        if (!Change(() => _model.Move(part, index), "Порядок изменён.")) return;
        Sources.UpdateLayout();
        foreach (var (item, before) in positions)
            if (Sources.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem card)
            {
                double dy = before - card.TranslatePoint(new(), Sources).Y;
                if (Math.Abs(dy) > 0.5) Motion.To(Motion.Shift(card), TranslateTransform.YProperty, 0, Motion.Slow, from: dy);
            }
    }

    void Source_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragPart = null;
        var button = (e.OriginalSource as DependencyObject)?.FindAncestor<Button>();
        var slot = (sender as FrameworkElement)?.DataContext as MergeSlot;
        if (_saving || button != null && !(button.Tag as string == "PathValue" && slot?.Path != null)) return;
        var card = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        _dragPart = card?.DataContext as MergePart ?? ((sender as FrameworkElement)?.DataContext as MergeSlot)?.Path;
        _lifted = card ?? sender as FrameworkElement;
        _dragStart = e.GetPosition(this);
        if (_dragPart != null) Sources.SelectedItem = _dragPart;
    }
    void Source_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragPart == null || _dragging || _saving || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var part = _dragPart; _dragging = true; Mouse.Capture(null); BeginGhost(part);
        try
        {
            using var wheel = new DragWheelScroll([Sources, SlotsScroll, Result], _ => ClearOrderTarget());
            DragDrop.DoDragDrop(this, new DataObject(typeof(MergePart), part), DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _dragPart = null; _dragging = false; ClearOrderTarget(); RestoreSource();
            if (!_landing) EndGhost();
        }
        e.Handled = true;
    }
    MergePart? Dropped(DragEventArgs e) => e.Data.GetDataPresent(typeof(MergePart)) ? e.Data.GetData(typeof(MergePart)) as MergePart : null;
    void ClearOrderTarget() { if (_orderTarget != null) _orderTarget.Tag = null; _orderTarget = null; }
    void Order_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None; e.Handled = true;
        var part = Dropped(e);
        if (part == null || !_model.Parts.Contains(part)) { ClearOrderTarget(); return; }
        var card = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        if (card?.DataContext is MergePart target)
        {
            if (_orderTarget != card) { ClearOrderTarget(); _orderTarget = card; }
            _after = target != part && e.GetPosition(card).Y > card.ActualHeight / 2;
            string? tag = target != part ? _after ? "After" : "Before" : null;
            if (!Equals(card.Tag, tag)) card.Tag = tag;
        }
        else ClearOrderTarget();
        e.Effects = DragDropEffects.Move; GhostHint.Text = "Отпустите — изменить порядок";
        DragSupport.ScrollEdge(Sources, e.GetPosition(Sources), ref _lastDragScroll);
    }
    void Order_DragLeave(object sender, DragEventArgs e) => ClearOrderTarget();
    void Order_Drop(object sender, DragEventArgs e)
    {
        var part = Dropped(e); if (part == null || !_model.Parts.Contains(part)) return;
        int next = _orderTarget?.DataContext is MergePart target ? _model.Parts.IndexOf(target) + (_after ? 1 : 0) : _model.Parts.Count;
        int old = _model.Parts.IndexOf(part); if (old < next) next--;
        if (old != next) MovePart(part, next);
        Sources.SelectedItem = part; Sources.ScrollIntoView(part); Sources.UpdateLayout();
        LandGhost(Sources.ItemContainerGenerator.ContainerFromItem(part) as FrameworkElement);
        ClearOrderTarget(); e.Effects = DragDropEffects.Move; e.Handled = true;
    }
    void Slot_DragOver(object sender, DragEventArgs e)
    {
        var border = (Border)sender; var part = Dropped(e);
        bool valid = part != null && border.DataContext is MergeSlot slot && _model.CanAssign(part, slot);
        e.Effects = valid ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true;
        border.SetResourceReference(Border.BorderBrushProperty, valid ? "Accent" : "CardStroke");
        GhostHint.Text = valid ? "Отпустите — подставить путь" : "Здесь нужен путь с подходящими кавычками";
    }
    void Slot_DragLeave(object sender, DragEventArgs e) => ((Border)sender).SetResourceReference(Border.BorderBrushProperty, "CardStroke");
    void Slot_Drop(object sender, DragEventArgs e)
    {
        var border = (Border)sender; var part = Dropped(e); e.Handled = true; e.Effects = DragDropEffects.None;
        if (part != null && border.DataContext is MergeSlot slot && Change(() => _model.Assign(part, slot), "Путь подставлен. Предпросмотр обновлён."))
        { LandGhost(border); e.Effects = DragDropEffects.Link; }
        border.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
    }

    void BeginGhost(MergePart part)
    {
        EndGhost(restore: false); GhostText.Text = part.Title; GhostHint.Text = "Выберите порядок или слот";
        GhostText.FontFamily = (FontFamily)FindResource(part.IsMono ? "MonoFont" : "TextFont");
        _ghostShift.BeginAnimation(TranslateTransform.XProperty, null); _ghostShift.BeginAnimation(TranslateTransform.YProperty, null);
        _ghostShift.X = _ghostShift.Y = 0; PositionGhost(); Ghost.IsOpen = true;
        if (_lifted != null) Motion.To(_lifted, OpacityProperty, 0.38, Motion.Fast);
        Motion.To(GhostCard, OpacityProperty, 1, Motion.Normal, from: 0);
        Motion.To(_ghostScale, ScaleTransform.ScaleXProperty, 1, Motion.Normal, from: 0.94);
        Motion.To(_ghostScale, ScaleTransform.ScaleYProperty, 1, Motion.Normal, from: 0.94); _pointer.Start();
    }
    void PositionGhost()
    {
        if (_landing || !Native.GetCursorPos(out var point) || PresentationSource.FromVisual(this) == null) return;
        if (DragSupport.MoveGhost(GhostCard, point, ref _ghostPixel, ref _ghostPlaced)) return;
        var local = PointFromScreen(new Point(point.X, point.Y)); Ghost.HorizontalOffset = local.X; Ghost.VerticalOffset = local.Y;
    }
    void LandGhost(FrameworkElement? target)
    {
        if (target == null || PresentationSource.FromVisual(target) == null || !Ghost.IsOpen) { EndGhost(); return; }
        _landing = true; _pointer.Stop(); RestoreSource();
        var center = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), this);
        var current = PointFromScreen(GhostCard.PointToScreen(new Point(GhostCard.ActualWidth / 2, GhostCard.ActualHeight / 2)));
        var delta = center - current;
        Motion.To(_ghostShift, TranslateTransform.XProperty, delta.X, Motion.Slow); Motion.To(_ghostShift, TranslateTransform.YProperty, delta.Y, Motion.Slow);
        Motion.To(_ghostScale, ScaleTransform.ScaleXProperty, 0.2, Motion.Slow); Motion.To(_ghostScale, ScaleTransform.ScaleYProperty, 0.2, Motion.Slow);
        Motion.Pop(target, 1.025, 1); Motion.To(GhostCard, OpacityProperty, 0, Motion.Slow, done: () => EndGhost());
    }
    void RestoreSource()
    { if (_lifted != null) { _lifted.BeginAnimation(OpacityProperty, null); _lifted.Opacity = 1; _lifted = null; } }
    void EndGhost(bool restore = true)
    { _pointer.Stop(); _landing = false; Ghost.IsOpen = false; _ghostPlaced = false; if (restore) RestoreSource(); }

    internal async Task<bool> SaveResultAsync(bool copy = true)
    {
        if (_saving) return false;
        string text = _model.Build();
        if (string.IsNullOrWhiteSpace(text)) { SetStatus("Добавьте хотя бы один непустой текст.", true); return false; }
        if (text.Length > _store.Storage.MaxChars) { SetStatus($"Результат длиннее лимита {_store.Storage.MaxChars:N0} символов. Уберите записи или увеличьте лимит в настройках.", true); return false; }
        _saving = true; Sheet.IsEnabled = false; SetStatus("Сохраняется…");
        try
        {
            _store.Add(text, null, null, DateTime.UtcNow, "ArkCopyBuffer"); await _store.SaveAsync();
            if (copy) Paster.CopyText(text);
            SetStatus(copy ? "Результат сохранён и скопирован." : "Результат сохранён."); return true;
        }
        catch (Exception ex) { App.Log(ex); SetStatus("Не удалось завершить. " + ex.Message + " Результат остаётся здесь; можно повторить.", true); return false; }
        finally { _saving = false; Sheet.IsEnabled = true; UpdateResult(); }
    }
    async void Merge_Click(object sender, RoutedEventArgs e)
    { if (await SaveResultAsync()) { Completed?.Invoke(); Close(); } }

    internal static void SelfTest(string directory, Action<bool, string> check)
    {
        var store = new HistoryStore(System.IO.Path.Combine(directory, "merge-window", "history.dat"));
        var now = DateTime.UtcNow; const string path = @"D:\Клипы\a.mp4";
        store.Add("run \"<path>\"", "rtf", "html", now, "Code"); store.Add(path, null, null, now.AddSeconds(1), "explorer");
        var original = store.Items.Single(i => i.Text.StartsWith("run")); store.TogglePin(original);
        var window = new TextMergeWindow(store, [original.Text, path]);
        try
        {
            check(window.Result.Text == "run \"" + path + "\"" && window.NoSlots.Visibility == Visibility.Collapsed, "merge окно: точный предпросмотр однозначной подстановки");
            window.UseSlots.IsChecked = false;
            check(window.Result.Text == original.Text + Environment.NewLine + path, "merge окно: переключение на отдельные строки");
            window.Undo_Click(window, new()); check(window.Result.Text == "run \"" + path + "\"", "merge окно: Ctrl+Z восстанавливает режим и назначения");
            window.Redo_Click(window, new()); check(window.Result.Text.EndsWith(path) && window.UseSlots.IsChecked == false, "merge окно: повтор восстанавливает режим строк");
            window.Sources.SelectedItem = window._model.Parts[1]; window.Up_Click(window, new());
            check(window.Result.Text.StartsWith(path + Environment.NewLine), "merge окно: кнопки и Alt+стрелки меняют порядок");
            var saving = window.SaveResultAsync(copy: false); TextEditorWindow.PumpUntil(() => saving.IsCompleted);
            check(saving.Result && store.Items.Count == 3 && original.Text == "run \"<path>\"" && original.Rtf == "rtf" && original.Html == "html" && original.Pinned && original.CreatedAt == now,
                "merge окно: сохранение нового текста сохраняет исходники и их метаданные");
            var loaded = new HistoryStore(System.IO.Path.Combine(directory, "merge-window", "history.dat")); loaded.Load(DateTime.UtcNow);
            check(loaded.Items.Any(i => i.Text == window.Result.Text), "merge окно: результат переживает загрузку зашифрованной истории");
            store.Storage = new StorageSettings(TextThousands: 10);
            var large = new TextMergeWindow(store, [new string('x', 10001), "y"]);
            try { var task = large.SaveResultAsync(false); check(!task.Result && store.Items.Count == 3 && large.Status.Text.Contains("лимита"), "merge окно: превышение лимита отвергается до записи без обрезания"); }
            finally { large.Close(); }
            var empty = new TextMergeWindow(store, ["", ""]);
            try { check(!empty.SaveResultAsync(false).Result && !empty.MergeButton.IsEnabled, "merge окно: пустой результат не сохраняется"); }
            finally { empty.Close(); }
        }
        finally { window.Close(); }
        string failedPath = System.IO.Path.Combine(directory, "merge-error", "history.dat"); System.IO.Directory.CreateDirectory(failedPath);
        var failed = new TextMergeWindow(new HistoryStore(failedPath), ["a", "b"]); bool closed = false; failed.Closed += (_, _) => closed = true;
        try
        {
            var task = failed.SaveResultAsync(false); TextEditorWindow.PumpUntil(() => task.IsCompleted);
            check(!task.Result && !closed && failed.Result.Text == "a" + Environment.NewLine + "b" && failed.MergeButton.IsEnabled && failed.Status.Text.Contains("можно повторить"),
                "merge окно: ошибка диска сохраняет предпросмотр и возможность повторить");
            var source = new Border { Opacity = 0.38 }; failed._lifted = source; failed._pointer.Start(); failed.EndGhost();
            check(source.Opacity == 1 && !failed._pointer.IsEnabled && !failed.Ghost.IsOpen, "merge drag: отмена восстанавливает карточку и останавливает таймер");
            bool oldMotion = Motion.Enabled; Motion.Enabled = false;
            try { failed.LandGhost(null); check(!failed._landing && !failed.Ghost.IsOpen, "merge drag: отключённые анимации и отсутствующая цель завершаются безопасно"); }
            finally { Motion.Enabled = oldMotion; }
        }
        finally { failed.Close(); }

        // Render synthetic data offscreen: no HWND, input, focus or real history.
        var preview = new TextMergeWindow(store, ["ffmpeg -i \"\" -i \"<второй клип>\" -filter_complex hstack output.mp4", @"D:\Клипы\Левый ракурс.mp4", @"D:\Клипы\Правый ракурс.mp4", "Два ракурса рядом"]);
        try
        {
            preview._model.AssignInOrder(); preview.UpdateResult();
            preview.Sheet.Background = (Brush)preview.FindResource("WindowSurface");
            preview.Sheet.Measure(new Size(1132, 584)); preview.Sheet.Arrange(new Rect(0, 0, 1132, 584)); preview.Sheet.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1132, 584, 96, 96, PixelFormats.Pbgra32); bitmap.Render(preview.Sheet);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "merge-preview.png")); encoder.Save(file);
            check(preview.Sources.ItemContainerGenerator.ContainerFromIndex(0) != null && preview.Result.ActualWidth > 250 && preview.MergeButton.ActualWidth > 180,
                "merge layout: три колонки и кнопка помещаются; offscreen PNG создан без фокуса и управления");
        }
        finally { preview.Close(); }
    }
}
