using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Windows.Data;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    readonly List<ClipItem> _mergeParts = [];
    readonly HashSet<ListBoxItem> _mergeMarks = [];
    readonly DispatcherTimer _altWatch = new() { Interval = TimeSpan.FromMilliseconds(80) };
    bool _collectingMerge, _altClick, _mergeTextFilter;
    ObservableCollection<ClipItem>? _mergeHistory;
    ListCollectionView? _mergeView;
    TextMergeWindow? _mergeWindow;
    void InitializeMergeSelection()
    {
        PreviewKeyUp += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftAlt or Key.RightAlt && !Native.IsDown(0x12))
            {
                ReleaseMergeAlt(); e.Handled = true;
            }
        };
        _altWatch.Tick += (_, _) =>
        {
            if (!ForegroundIsOurs() && _mergeWindow == null) FinishMergeSelection(open: false);
            else if (!Native.IsDown(0x12)) ReleaseMergeAlt();
        };
        List.LayoutUpdated += (_, _) => { if (_collectingMerge) RefreshMergeMarks(); };
        PinnedList.LayoutUpdated += (_, _) => { if (_collectingMerge) RefreshMergeMarks(); };
    }
    void ReleaseMergeAlt()
    {
        if (!_mergeTextFilter) FinishMergeSelection(open: false);
    }
    bool HandleMergeKey(Key key, KeyEventArgs e)
    {
        if (SeriesActive) return false;
        if (key is Key.LeftAlt or Key.RightAlt && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { BeginMergeSelection(); e.Handled = true; return true; }
        if (!_collectingMerge) return false;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && (key is Key.Space or Key.Enter && Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase
                || key == Key.Space && Search.IsKeyboardFocused && Search.Text.Length > 0)) return false;
        switch (key)
        {
            case Key.Escape: FinishMergeSelection(open: false); break;
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): FinishMergeSelection(open: true); break;
            case Key.Enter or Key.Space:
                if (Active.SelectedItem is ClipItem item)
                {
                    if (item.IsVisualMedia && !_mergeTextFilter) { if (!e.IsRepeat) ToggleSeriesItem(item); }
                    else ToggleMergePart(item);
                }
                break;
            case Key.Down: Move(GridTab ? Columns : 1); break;
            case Key.Up: Move(GridTab ? -Columns : -1); break;
            case Key.Right when GridTab && Search.Text.Length == 0: Move(1); break;
            case Key.Left when GridTab && Search.Text.Length == 0: Move(-1); break;
            default: return false;
        }
        e.Handled = true; return true;
    }
    void BeginMergeSelection()
    {
        if (SeriesActive || _collectingMerge || _textEditor != null || _mergeWindow != null || SymbolsTab || SettingsFlyout.Visibility == Visibility.Visible) return;
        _collectingMerge = true; _mergeParts.Clear();
        HideShortcuts(); CloseValueDrop(); CancelZoom(); StopGif();
        _outsideTimer.Stop(); _deactivateTimer.Stop(); _altWatch.Start();
    }
    void ToggleMergePart(ClipItem item)
    {
        if (!_collectingMerge || _mergeWindow != null || !MatchesKind(item, "text") || !_store.Items.Contains(item)) return;
        if (!_mergeParts.Remove(item))
        {
            if (_mergeParts.Count >= 50) { ShowInfo(InfoKind.Caution, "Выбрано 50 текстов", "Завершите это объединение, затем можно собрать следующее."); return; }
            _mergeParts.Add(item);
        }
        if (!_mergeTextFilter)
        {
            KeepSelectionPosition(item, () =>
            {
                _mergeTextFilter = true;
                _mergeHistory = new(_store.Items);
                _mergeView = new(_mergeHistory) { Filter = o => Matches((ClipItem)o) };
                List.ItemsSource = _mergeView;
                List.Focusable = true;
                HideShortcuts(); CloseTransient();
                UpdatePinnedLayout(); UpdateMergeSelection(); UpdateEmptyState();
            });
        }
        KeepSelectionPosition(item, () => { RefreshMergeMarks(); UpdateMergeSelection(); });
    }
    void UpdateMergeSelection()
    {
        if (!_mergeTextFilter) return;
        SeriesBar.Visibility = Visibility.Visible; KeyHints.Visibility = Visibility.Collapsed;
        SeriesQueue.Visibility = SeriesNext.Visibility = Visibility.Collapsed;
        SeriesStatus.Text = $"Выбрано текстов: {_mergeParts.Count}";
        SeriesHint.Text = "Клик — выбрать или снять отметку. Ctrl+Enter — объединить. Esc — отменить.";
        SeriesAll.Content = $"Объединить · {_mergeParts.Count}";
        SeriesAll.ToolTip = "Открыть окно объединения текстов (Ctrl+Enter). Успешное объединение завершит выбор.";
        SeriesAll.IsEnabled = _mergeParts.Count >= 2 && _mergeWindow == null;
        Grid.SetColumn(SeriesAll, 0); Grid.SetColumnSpan(SeriesAll, 2); SeriesAll.Margin = new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(SeriesAll, "Объединить выбранные тексты");
        System.Windows.Automation.AutomationProperties.SetName(SeriesClear, "Отменить выбор текстов");
    }
    void SyncMergeSelection()
    {
        if (_mergeHistory == null) return;
        KeepSelectionPosition(Active.SelectedItem as ClipItem, () =>
        {
            foreach (var item in _mergeHistory.Where(i => !_store.Items.Contains(i)).ToArray()) _mergeHistory.Remove(item);
            foreach (var item in _store.Items.Reverse()) if (!_mergeHistory.Contains(item)) _mergeHistory.Insert(0, item);
            _mergeParts.RemoveAll(i => !_store.Items.Contains(i));
            UpdatePinnedLayout(); RefreshMergeMarks(); UpdateMergeSelection(); UpdateEmptyState();
        });
    }
    void KeepSelectionPosition(ClipItem? anchor, Action update)
    {
        var list = Active;
        var scroll = list.FindDescendant<ScrollViewer>();
        double offset = scroll?.VerticalOffset ?? 0;
        double? top = scroll != null && anchor != null && list.ItemContainerGenerator.ContainerFromItem(anchor) is FrameworkElement card
            ? card.TranslatePoint(new Point(), scroll).Y : null;
        var selected = list.SelectedItem;
        update();
        if (selected != null && list.Items.Contains(selected)) list.SelectedItem = selected;
        list.UpdateLayout();
        if (scroll == null) return;
        if (top is { } y && anchor != null && list.Items.Contains(anchor))
        {
            list.ScrollIntoView(anchor); list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(anchor) is FrameworkElement moved)
                offset = scroll.VerticalOffset + moved.TranslatePoint(new Point(), scroll).Y - Math.Clamp(y, 0, Math.Max(0, scroll.ViewportHeight - moved.ActualHeight));
        }
        scroll.ScrollToVerticalOffset(Math.Max(0, offset)); list.UpdateLayout();
    }
    void ClearMergeMarks()
    {
        foreach (var card in _mergeMarks)
        {
            if (card.Tag as string == "Merge") card.Tag = null;
            if (card.Template?.FindName("Key", card) is Border key) key.Visibility = Visibility.Collapsed;
            if (card.Template?.FindName("Bd", card) is Border body) body.ClearValue(Border.PaddingProperty);
        }
        _mergeMarks.Clear();
    }
    void RefreshMergeMarks()
    {
        if (!_collectingMerge || _shortcutContainers.Count > 0) return;
        foreach (var card in _mergeMarks.ToArray())
            if (card.DataContext is not ClipItem item || !_mergeParts.Contains(item))
            {
                card.Tag = null;
                if (card.Template?.FindName("Key", card) is Border oldKey) oldKey.Visibility = Visibility.Collapsed;
                if (card.Template?.FindName("Bd", card) is Border oldBody) oldBody.ClearValue(Border.PaddingProperty);
                _mergeMarks.Remove(card);
            }
        foreach (var list in new[] { List, PinnedList })
            foreach (var item in _mergeParts)
                if (list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem card)
                {
                    bool added = _mergeMarks.Add(card); card.Tag = "Merge";
                    if (card.Template?.FindName("Key", card) is Border { Child: TextBlock number } key)
                    {
                        number.Text = (_mergeParts.IndexOf(item) + 1).ToString();
                        key.BeginAnimation(OpacityProperty, null); key.Opacity = 1; key.Visibility = Visibility.Visible;
                        if (added) Motion.Pop(key, 1.08, 0.94);
                    }
                    if (list == List && card.Template?.FindName("Bd", card) is Border body) body.Padding = new Thickness(36, 6, 6, 6);
                }
    }
    void FinishMergeSelection(bool open)
    {
        if (!_collectingMerge) return;
        if (open)
        {
            if (_mergeWindow != null) return;
            var selectedTexts = _mergeParts.Where(_store.Items.Contains).Select(p => p.Text).ToArray();
            if (selectedTexts.Length < 2) return;
            var window = CreateMergeWindow(selectedTexts);
            window.Owner = this;
            window.Show();
            return;
        }
        _collectingMerge = false; _altWatch.Stop(); ClearMergeMarks();
        _mergeParts.Clear(); CloseTransient();
        if (_mergeTextFilter)
        {
            KeepSelectionPosition(Active.SelectedItem as ClipItem, () =>
            {
                _mergeTextFilter = false; _mergeView = null; _mergeHistory = null;
                List.ItemsSource = _view; List.Focusable = false;
                SeriesBar.Visibility = Visibility.Collapsed;
                KeyHints.Visibility = ShowKeyHints ? Visibility.Visible : Visibility.Collapsed;
                _view.Refresh(); UpdatePinnedLayout(); UpdateEmptyState();
            });
        }
        StartOutsideTimer();
    }
    TextMergeWindow CreateMergeWindow(string[] texts)
    {
        var window = new TextMergeWindow(_store, texts);
        _mergeWindow = window;
        UpdateMergeSelection();
        window.Closed += (_, _) => { _mergeWindow = null; UpdateMergeSelection(); StartOutsideTimer(); };
        window.Completed += CompleteMergeSelection;
        return window;
    }
    void CompleteMergeSelection()
    {
        FinishMergeSelection(open: false);
        ShowInfo(InfoKind.Success, "Тексты объединены", "Результат в истории и буфере обмена.");
    }

    internal static void SelfTestMergeSelection(string directory, Action<bool, string> check)
    {
        var store = new HistoryStore(System.IO.Path.Combine(directory, "merge-selection", "history.dat"));
        store.Add("первый", null, null, DateTime.UtcNow); store.Add("второй", null, null, DateTime.UtcNow);
        var first = store.Items[0]; var second = store.Items[1];
        var extraTexts = new ClipItem[] { new() { Text = "https://example.test/" }, new() { Text = @"D:\sample\file.txt" }, new() { Text = "    print('code');\n    done();" } };
        var otherKinds = new ClipItem[]
        {
            new() { ImageHash = "MERGE-IMAGE" }, new() { ImageHash = "MERGE-VIDEO", MediaKind = "video" },
            new() { ImageHash = "MERGE-AUDIO", MediaKind = "audio" }, new() { ImageHash = "MERGE-FILES", MediaKind = "files" },
            new() { MediaKind = "folder", Text = @"D:\sample" },
            new() { Text = "Begin Object Class=/Script/UnrealEd.MaterialGraphNode\nCustomProperties Pin" }
        };
        foreach (var item in extraTexts.Concat(otherKinds)) store.Items.Add(item);
        var panel = new PanelWindow(store, new SymbolCatalog(System.IO.Path.Combine(directory, "merge-selection", "symbols.json")));
        try
        {
            int pasted = 0; panel.PasteRequested += (_, _) => pasted++;
            panel.BeginMergeSelection();
            check(!panel._mergeTextFilter && panel.List.Items.Count == store.Items.Count, "Alt текст: одно нажатие Alt ещё не фильтрует список");
            panel.List.SelectedItem = first; panel.ToggleMergePart(first);
            check(panel._mergeTextFilter && panel.List.SelectedItem == first && panel.List.Items.Cast<ClipItem>().All(i => MatchesKind(i, "text"))
                && extraTexts.All(panel.List.Items.Contains) && otherKinds.All(i => !panel.List.Items.Contains(i)),
                "Alt текст: первая отметка скрывает медиа, файлы, папки и графы, сохраняя текст, код, ссылки, пути и выделение");
            panel.ToggleMergePart(first);
            check(panel._mergeParts.Count == 0 && panel._mergeTextFilter && otherKinds.All(i => !panel.List.Items.Contains(i)),
                "Alt текст: снятие последней отметки сохраняет текстовый фильтр до конца выбора");
            panel.HandleImageSelectionMouseDown(otherKinds[1], ModifierKeys.Alt);
            check(!panel.SeriesActive && panel._mergeTextFilter, "Alt текст: пустой выбор текста не переключается на медиа до отпускания Alt");
            panel._imageSelectionClick = false;
            foreach (var item in otherKinds) panel.ToggleMergePart(item);
            check(panel._mergeParts.Count == 0, "Alt текст: скрытые нетекстовые записи не добавляются в объединение");
            panel.ToggleMergePart(first); panel.ToggleMergePart(second);
            check(panel._collectingMerge && panel._mergeParts.SequenceEqual([first, second]) && panel.Busy, "Alt: выбор сохраняет порядок и удерживает панель открытой");
            foreach (var item in store.Items.ToArray()) store.TogglePin(item);
            panel.TabPinned.IsChecked = true; panel.UpdatePinnedLayout();
            check(panel.PinnedList.Items.OfType<ClipItem>().Count() == 5 && panel.PinnedList.Items.OfType<ClipItem>().All(i => MatchesKind(i, "text")),
                "Alt текст: закреплённые используют тот же текстовый фильтр");
            panel.TabAll.IsChecked = true;
            first.SourceApp = "MergeTest"; panel.SetSourceFilter("mergetest"); panel.Search.Text = first.Text;
            check(panel.List.Items.Cast<ClipItem>().SequenceEqual([first]) && panel._mergeParts.SequenceEqual([first, second]),
                "Alt текст: поиск и фильтр приложения сочетаются с выбором без потери отметок");
            panel.Search.Text = ""; panel.SetSourceFilter(null);
            store.AddMedia("MERGE-NEW-VIDEO", "video", "new.mp4", 4, false, DateTime.UtcNow);
            var newVideo = store.Items[0];
            store.Add("новый текст", null, null, DateTime.UtcNow);
            check(panel.List.Items.Contains(store.Items[0]) && !panel.List.Items.Contains(newVideo),
                "Alt текст: новые тексты доступны, новые видео скрыты до окончания выбора");
            panel.ToggleMergePart(first); panel.ToggleMergePart(first);
            check(panel._mergeParts.SequenceEqual([second, first]), "Alt: повторный клик снимает выбор, новый добавляет запись последней");
            panel.ToggleMergePart(new ClipItem { Text = "удалённый текст" });
            check(panel._mergeParts.Count == 2, "Alt: удалённая запись не попадает в объединение");
            panel._altClick = true; panel.FinishMergeSelection(open: false);
            var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            panel.Item_Click(panel.List, release);
            check(pasted == 0 && release.Handled && !panel._altClick, "Alt: отпускание мыши после Alt не вставляет текст случайно");
            check(!panel._collectingMerge && panel._mergeParts.Count == 0 && !panel._altWatch.IsEnabled && panel._mergeWindow == null, "Alt: Esc/потеря фокуса очищают выбор без открытия окна");
            check(!panel._mergeTextFilter && panel.List.Items.Count == store.Items.Count && panel.PinnedList.Items.OfType<ClipItem>().Count() == store.Items.Count - 2,
                "Alt текст: отмена возвращает все типы в истории и закреплённых");
            panel.BeginMergeSelection(); panel.ToggleMergePart(first); panel.ReleaseMergeAlt(); panel.FinishMergeSelection(open: true);
            check(panel._mergeTextFilter && panel._mergeParts.SequenceEqual([first]) && panel._mergeWindow == null,
                "Alt текст: отпускание Alt и попытка объединить одну запись сохраняют выбор без окна");
            panel.FinishMergeSelection(open: false);
            panel.BeginMergeSelection(); panel.ToggleMergePart(first); store.Remove(first); panel.FinishMergeSelection(open: false);
            check(!panel._mergeTextFilter && panel.List.Items.Count == store.Items.Count && otherKinds.All(panel.List.Items.Contains),
                "Alt текст: удаление выбранного источника не оставляет фильтр после отмены");
            var badge = new Border { Visibility = Visibility.Collapsed, Opacity = 0 };
            RevealShortcut(badge);
            check(badge.Visibility == Visibility.Visible && badge.Opacity == 1, "Ctrl: цифры видны сразу, без таймера и задержки анимации");
        }
        finally { panel.FinishMergeSelection(open: false); panel.Close(); }
        SelfTestStickySelection(directory, check);
    }

    static void SelfTestStickySelection(string directory, Action<bool, string> check)
    {
        var store = new HistoryStore(System.IO.Path.Combine(directory, "sticky-selection", "history.dat"));
        for (int i = 0; i < 80; i++) store.Items.Add(i % 2 == 0
            ? new ClipItem { Text = $"Текст {i}\nВторая строка для прокрутки\nТретья строка", CreatedAt = DateTime.UtcNow.AddSeconds(-i), LastUsedAt = DateTime.UtcNow }
            : new ClipItem { MediaKind = "audio", FileName = $"звук {i}.mp3", ImageHash = "AUDIO-" + i, CreatedAt = DateTime.UtcNow, LastUsedAt = DateTime.UtcNow });
        var panel = new PanelWindow(store, new SymbolCatalog(System.IO.Path.Combine(directory, "sticky-selection", "symbols.json")));
        try
        {
            panel.Root.Measure(new Size(392, 492)); panel.Root.Arrange(new Rect(0, 0, 392, 492)); panel.Root.UpdateLayout();
            var first = store.Items[40]; var second = store.Items[42];
            panel.List.SelectedItem = first; panel.List.ScrollIntoView(first); panel.List.UpdateLayout();
            var scroll = panel.List.FindDescendant<ScrollViewer>()!;
            var card = (FrameworkElement)panel.List.ItemContainerGenerator.ContainerFromItem(first);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + card.TranslatePoint(new Point(), scroll).Y - 80); panel.List.UpdateLayout();
            double top = card.TranslatePoint(new Point(), scroll).Y;
            panel.BeginMergeSelection(); panel._altWatch.Stop(); panel.ToggleMergePart(first);
            var filtered = panel.List.Items.Cast<ClipItem>().ToArray();
            card = (FrameworkElement)panel.List.ItemContainerGenerator.ContainerFromItem(first);
            check(scroll.VerticalOffset > 0 && Math.Abs(card.TranslatePoint(new Point(), scroll).Y - top) < 2,
                "выбор текста: первая отметка и фильтрация сохраняют позицию карточки при прокрутке");
            panel.ReleaseMergeAlt();
            check(panel._mergeTextFilter && panel.SeriesAll.Content?.ToString() == "Объединить · 1" && !panel.SeriesAll.IsEnabled,
                "выбор текста: отпускание Alt сохраняет выбор и явную кнопку объединения");
            check(panel.HandleImageSelectionMouseDown(second, ModifierKeys.None) && panel._mergeParts.SequenceEqual([first, second]) && panel.SeriesAll.IsEnabled,
                "выбор текста: обычный клик после первой отметки выбирает следующий текст");
            double offset = scroll.VerticalOffset;
            store.Touch(first.Id, DateTime.UtcNow.AddMinutes(1)); panel.ResetSelection();
            check(panel.List.Items.Cast<ClipItem>().SequenceEqual(filtered) && Math.Abs(scroll.VerticalOffset - offset) < 2,
                $"выбор текста: Touch и сброс выделения не меняют порядок и прокрутку (order={panel.List.Items.Cast<ClipItem>().SequenceEqual(filtered)}, offset={offset:F1}→{scroll.VerticalOffset:F1})");
            var bounds = panel.SeriesAll.TransformToAncestor(panel.Root).TransformBounds(new Rect(panel.SeriesAll.RenderSize));
            check(bounds.Width > 250 && bounds.Bottom <= 492 && panel.List.ActualHeight >= 130, "выбор текста: широкая кнопка объединения и список помещаются в панели");
            var preview = new System.Windows.Media.Imaging.RenderTargetBitmap(392, 492, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); preview.Render(panel.Root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
            using (var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "sticky-text-selection.png"))) encoder.Save(file);
            var editor = panel.CreateMergeWindow([first.Text, second.Text]); editor.Close();
            check(panel._mergeTextFilter && panel._mergeParts.Count == 2 && panel.SeriesAll.IsEnabled,
                "выбор текста: отмена окна объединения сохраняет выбор для повторного открытия");
            panel.CompleteMergeSelection();
            check(!panel._collectingMerge && !panel._mergeTextFilter && panel._mergeView == null && panel.SeriesBar.Visibility == Visibility.Collapsed
                && panel.List.Items.Count == store.Items.Count, "выбор текста: успешное завершение выключает режим и возвращает обычную историю");
            var image = new ClipItem { ImageHash = "STICKY-IMAGE" }; var video = new ClipItem { MediaKind = "video", ImageHash = "STICKY-VIDEO", FileName = "clip.mp4" };
            store.Items.Add(image); store.Items.Add(video);
            panel.HandleImageSelectionMouseDown(image, ModifierKeys.Alt);
            check(panel.HandleImageSelectionMouseDown(video, ModifierKeys.None) && panel._seriesQueue.Count == 2 && panel.SeriesNext.Visibility == Visibility.Visible,
                "выбор медиа: обычный клик отмечает видео после первой картинки, кнопки восстановлены после текста");
            var ticket = panel.BeginSeriesPaste(panel._seriesQueue.ToArray()); ticket!.Value.Complete(false);
            check(panel.SeriesActive && panel._seriesQueue.Count == 2, "выбор медиа: ошибка вставки сохраняет режим и очередь");
            ticket = panel.BeginSeriesPaste(panel._seriesQueue.ToArray()); ticket!.Value.Complete(true);
            check(!panel.SeriesActive && panel._seriesQueue.Count == 0, "выбор медиа: успешная вставка всей очереди автоматически выключает режим");
        }
        finally { panel.EndImageSeries(); panel.FinishMergeSelection(open: false); panel.Close(); }
    }
}
