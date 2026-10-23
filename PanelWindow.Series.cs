using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    const string SeriesDragFormat = "ArkCopyBuffer.ImageSeries";
    readonly ObservableCollection<ClipItem> _seriesQueue = [];
    ObservableCollection<ClipItem>? _seriesHistory;
    ListCollectionView? _seriesView;
    bool _imageSelectionClick;
    int _seriesVersion;
    ClipItem? _seriesPending, _seriesDragItem;
    Point _seriesDragStart;
    ListBoxItem? _seriesDropCard;
    bool SeriesActive => _seriesView != null;
    ClipItem? NextSeriesItem => _seriesQueue.FirstOrDefault(i => !i.SeriesInserted);
    internal event Action<IReadOnlyList<ClipItem>>? SeriesPasteAllRequested;

    void SeriesClear_Click(object sender, RoutedEventArgs e)
    {
        if (_mergeTextFilter) FinishMergeSelection(open: false);
        else EndImageSeries();
    }

    bool HandleImageSelectionMouseDown(ClipItem item, ModifierKeys modifiers)
    {
        if (_mergeTextFilter)
        {
            _imageSelectionClick = true;
            if (!modifiers.HasFlag(ModifierKeys.Control)) ToggleMergePart(item);
            return true;
        }
        if (!SeriesActive && (!modifiers.HasFlag(ModifierKeys.Alt) || modifiers.HasFlag(ModifierKeys.Control))) return false;
        if (!item.IsVisualMedia && !SeriesActive) return false;
        _imageSelectionClick = true;
        if (!modifiers.HasFlag(ModifierKeys.Control) && item.IsVisualMedia) ToggleSeriesItem(item);
        return true;
    }

    void FinishImageSelectionClick(MouseButtonEventArgs e)
    {
        if (!_imageSelectionClick) return;
        _imageSelectionClick = false;
        e.Handled = true;
    }

    void BeginImageSeries()
    {
        if (SeriesActive) return;
        if (!_store.Items.Any(i => i.IsVisualMedia)) return;
        var scroll = List.FindDescendant<ScrollViewer>();
        double offset = scroll?.VerticalOffset ?? 0;
        var selected = List.SelectedItem;
        FinishMergeSelection(open: false);
        HideShortcuts(); CancelZoom(); StopGif(); ClosePeek(); CloseValueDrop(); CloseTransient();
        _seriesHistory = new(_store.Items);
        _seriesView = new(_seriesHistory);
        _seriesView.Filter = o => Matches((ClipItem)o);
        _seriesVersion++;
        List.ItemsSource = _seriesView;
        List.Focusable = true;
        SeriesBar.Visibility = Visibility.Visible;
        KeyHints.Visibility = Visibility.Collapsed;
        RefreshViews();
        if (selected != null && List.Items.Contains(selected)) List.SelectedItem = selected;
        List.UpdateLayout(); scroll?.ScrollToVerticalOffset(offset);
        UpdateSeries(); UpdateEmptyState();
        if (IsShown) Motion.Enter(SeriesBar, 0, 8);
    }

    void EndImageSeries()
    {
        if (!SeriesActive) return;
        var active = Active;
        var selected = active.SelectedItem;
        double offset = active.FindDescendant<ScrollViewer>()?.VerticalOffset ?? 0;
        _seriesVersion++;
        _seriesPending = _seriesDragItem = null;
        foreach (var item in _seriesQueue) { item.SeriesNumber = 0; item.SeriesInserted = false; }
        _seriesQueue.Clear();
        _seriesView = null; _seriesHistory = null;
        List.ItemsSource = _view;
        List.Focusable = false;
        SeriesNext.IsEnabled = SeriesAll.IsEnabled = false;
        SeriesBar.Visibility = Visibility.Collapsed;
        KeyHints.Visibility = ShowKeyHints ? Visibility.Visible : Visibility.Collapsed;
        RefreshViews();
        if (selected != null && active.Items.Contains(selected)) active.SelectedItem = selected;
        active.UpdateLayout(); active.FindDescendant<ScrollViewer>()?.ScrollToVerticalOffset(offset);
        Search.Focus();
    }

    void RequestPaste(ClipItem item, bool plainText)
    {
        PasteRequested?.Invoke(item, plainText);
    }

    void ToggleSeriesItem(ClipItem item)
    {
        if (_seriesPending != null || !item.IsVisualMedia || !_store.Items.Contains(item)) return;
        if (!SeriesActive) BeginImageSeries();
        CancelZoom(); StopGif(); ClosePeek();
        if (_seriesQueue.Remove(item)) { item.SeriesNumber = 0; item.SeriesInserted = false; }
        else
        {
            // Новый файл встаёт по времени копирования; ручной порядок остальных сохраняется.
            int at = 0;
            while (at < _seriesQueue.Count && _seriesQueue[at].CreatedAt <= item.CreatedAt) at++;
            _seriesQueue.Insert(at, item);
            SeriesQueue.SelectedItem = item;
        }
        UpdateSeries();
        if (_seriesQueue.Count == 0) { EndImageSeries(); return; }
        if (SeriesQueue.SelectedItem is { } selected) SeriesQueue.ScrollIntoView(selected);
    }

    void UpdateSeries()
    {
        if (!SeriesActive) return;
        SeriesNext.Visibility = Visibility.Visible;
        Grid.SetColumn(SeriesAll, 1); Grid.SetColumnSpan(SeriesAll, 1); SeriesAll.Margin = new Thickness(4, 0, 0, 0);
        AutomationProperties.SetName(SeriesClear, UiText.T("Отменить выбор картинок и видео"));
        SeriesAll.ToolTip = UiText.T("Вставить картинки и видео одной пачкой по порядку (Ctrl+Enter). Успешная вставка всей очереди завершит выбор.");
        for (int i = 0; i < _seriesQueue.Count; i++) _seriesQueue[i].SeriesNumber = i + 1;
        int done = _seriesQueue.Count(i => i.SeriesInserted);
        var next = NextSeriesItem;
        SeriesStatus.Text = done == 0 ? UiText.F($"Выбрано: {_seriesQueue.Count}") : UiText.F($"Вставлено {done} из {_seriesQueue.Count}");
        SeriesHint.Text = UiText.T("Клик — выбрать или снять отметку. Ctrl+Enter — вставить все. Esc — отменить.");
        SeriesQueue.Visibility = _seriesQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SeriesNext.Content = next != null ? UiText.F($"Следующая · {done + 1}/{_seriesQueue.Count}") : UiText.T("Следующая");
        SeriesNext.IsEnabled = next != null && _seriesPending == null;
        SeriesAll.Content = _seriesPending != null ? UiText.T("Вставляем…") : UiText.F($"Вставить все · {_seriesQueue.Count - done}");
        SeriesAll.IsEnabled = SeriesNext.IsEnabled;
        AutomationProperties.SetName(SeriesNext, UiText.T("Вставить следующий файл: ") + SeriesNext.Content);
        AutomationProperties.SetName(SeriesAll, UiText.T("Вставить все оставшиеся картинки и видео: ") + (_seriesQueue.Count - done));
    }

    void SyncImageSeries()
    {
        if (_seriesHistory == null) return;
        foreach (var item in _seriesHistory.Where(i => !_store.Items.Contains(i)).ToArray()) _seriesHistory.Remove(item);
        foreach (var item in _store.Items.Reverse()) if (!_seriesHistory.Contains(item)) _seriesHistory.Insert(0, item);
        foreach (var item in _seriesQueue.Where(i => !_store.Items.Contains(i)).ToArray())
        {
            _seriesQueue.Remove(item); item.SeriesNumber = 0; item.SeriesInserted = false;
        }
        UpdateSeries();
        if (_seriesQueue.Count == 0) { EndImageSeries(); return; }
        UpdateEmptyState();
    }

    void RestoreSeriesSelection(bool advance = false)
    {
        if (!SeriesActive) return;
        if (advance && NextSeriesItem is { } next && Active.Items.Contains(next))
        {
            Active.SelectedItem = next;
            Active.ScrollIntoView(next);
            SeriesQueue.SelectedItem = next;
            SeriesQueue.ScrollIntoView(next);
        }
        else if (Active.SelectedIndex < 0 && Active.Items.Count > 0)
            Active.SelectedItem = Active.Items.OfType<ClipItem>().FirstOrDefault();
    }

    void SeriesNext_Click(object sender, RoutedEventArgs e) => PasteNextSeries();
    void SeriesAll_Click(object sender, RoutedEventArgs e)
    {
        if (_mergeTextFilter) FinishMergeSelection(open: true);
        else PasteSeries(all: true);
    }

    void PasteNextSeries() => PasteSeries(all: false);

    void PasteSeries(bool all)
    {
        SyncImageSeries();
        if (!SeriesActive || _seriesPending != null || NextSeriesItem is not { } next) return;
        UpdateSeries(); RestoreSeriesSelection(advance: true);
        var remaining = _seriesQueue.Where(i => !i.SeriesInserted).ToArray();
        if (all && remaining.Length > 1) SeriesPasteAllRequested?.Invoke(remaining);
        else PasteRequested?.Invoke(next, false);
    }

    internal (Func<bool> Current, Action<bool> Complete)? BeginSeriesPaste(ClipItem item) => BeginSeriesPaste([item]);

    internal (Func<bool> Current, Action<bool> Complete)? BeginSeriesPaste(IReadOnlyList<ClipItem> items)
    {
        if (!SeriesActive || _seriesPending != null || items.Count == 0 || !_seriesQueue.Contains(items[0]) || items[0].SeriesInserted
            || items.Count > 1 && !items.SequenceEqual(_seriesQueue.Where(i => !i.SeriesInserted))) return null;
        var batch = items.ToArray();
        var item = batch[0];
        int version = _seriesVersion;
        _seriesPending = item;
        UpdateSeries();
        bool Current() => version == _seriesVersion && ReferenceEquals(_seriesPending, item) && batch.All(_store.Items.Contains);
        bool completed = false;
        void Complete(bool pasted)
        {
            if (completed || version != _seriesVersion) return;
            completed = true;
            bool valid = Current();
            _seriesPending = null;
            if (pasted && valid) foreach (var image in batch) image.SeriesInserted = true;
            if (pasted && valid && NextSeriesItem == null) { EndImageSeries(); return; }
            UpdateSeries(); RestoreSeriesSelection(advance: pasted && valid);
        }
        return (Current, Complete);
    }

    bool HandleSeriesKey(Key key, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (alt && key is Key.Space or Key.Enter && Keyboard.FocusedElement is not ButtonBase && Active.SelectedItem is ClipItem { IsVisualMedia: true } selected)
        {
            if (!e.IsRepeat) ToggleSeriesItem(selected);
        }
        else if (!SeriesActive) return false;
        else if (key == Key.Escape) EndImageSeries();
        else if (key is Key.LeftAlt or Key.RightAlt) { }
        else if (ctrl && key == Key.Enter) { if (!e.IsRepeat) PasteSeries(all: true); }
        else if (SeriesQueue.IsKeyboardFocusWithin)
        {
            if (ctrl && key is Key.Left or Key.Right && SeriesQueue.SelectedItem is ClipItem item)
                MoveSeriesItem(item, _seriesQueue.IndexOf(item) + (key == Key.Left ? -1 : 1));
            else if (key == Key.Delete && SeriesQueue.SelectedItem is ClipItem remove) ToggleSeriesItem(remove);
            else if (key == Key.Enter && SeriesQueue.SelectedItem is ClipItem insert) { if (!e.IsRepeat) RequestPaste(insert, false); }
            else if (key is Key.Up or Key.Down or Key.Left or Key.Right)
                SeriesQueue.SelectedIndex = Math.Clamp(SeriesQueue.SelectedIndex + (key is Key.Up or Key.Left ? -1 : 1), 0, Math.Max(0, _seriesQueue.Count - 1));
            else return false;
        }
        else if (alt && key is Key.Up or Key.Down or Key.Left or Key.Right)
            Move(key switch { Key.Up => GridTab ? -Columns : -1, Key.Down => GridTab ? Columns : 1, Key.Left => -1, _ => 1 });
        else return false;
        e.Handled = true; return true;
    }

    void MoveSeriesItem(ClipItem item, int to)
    {
        int from = _seriesQueue.IndexOf(item);
        if (_seriesPending != null || from < 0 || to < 0 || to >= _seriesQueue.Count || from == to) return;
        _seriesQueue.Move(from, to);
        UpdateSeries(); SeriesQueue.SelectedItem = item; SeriesQueue.ScrollIntoView(item);
        if (SeriesQueue.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem card && IsShown) Motion.Pop(card, 1.05, 0.97);
    }

    void SeriesQueue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeriesQueue.SelectedItem is ClipItem item && Active.Items.Contains(item)) { Active.SelectedItem = item; Active.ScrollIntoView(item); }
    }

    void SeriesQueue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _seriesDragItem = _seriesPending == null ? (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext as ClipItem : null;
        _seriesDragStart = e.GetPosition(SeriesQueue);
    }

    void SeriesQueue_MouseMove(object sender, MouseEventArgs e)
    {
        if (_seriesPending != null || _seriesDragItem is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(SeriesQueue);
        if (Math.Abs(at.X - _seriesDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(at.Y - _seriesDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _seriesDragItem = null;
        _dragging = true; CancelZoom(); StopGif();
        BeginDragVisual(SeriesQueue, item);
        DragGhostText.Text = UiText.F($"Файл {item.SeriesNumber}");
        DragGhostStatus.Text = UiText.T("Перетащите между миниатюрами");
        try { DragDrop.DoDragDrop(SeriesQueue, new DataObject(SeriesDragFormat, item), DragDropEffects.Move); }
        finally { ClearSeriesDrop(); _dragging = false; EndDragVisual(); }
    }

    void ClearSeriesDrop() { if (_seriesDropCard != null) _seriesDropCard.Tag = null; _seriesDropCard = null; }

    void SeriesQueue_DragOver(object sender, DragEventArgs e)
    {
        ClearSeriesDrop();
        var card = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        bool valid = _seriesPending == null && e.Data.GetData(SeriesDragFormat) is ClipItem item && _seriesQueue.Contains(item)
            && card?.DataContext is ClipItem target && _seriesQueue.Contains(target);
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        SetDragVisualStatus(false, valid);
        if (valid && card != null) { _seriesDropCard = card; card.Tag = e.GetPosition(card).X < card.ActualWidth / 2 ? "Before" : "After"; }
        e.Handled = true;
    }

    void SeriesQueue_DragLeave(object sender, DragEventArgs e) => ClearSeriesDrop();

    void SeriesQueue_Drop(object sender, DragEventArgs e)
    {
        var card = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        if (_seriesPending == null && e.Data.GetData(SeriesDragFormat) is ClipItem item && card?.DataContext is ClipItem target
            && _seriesQueue.Contains(item) && _seriesQueue.Contains(target))
        {
            int to = _seriesQueue.IndexOf(target) + (e.GetPosition(card).X >= card.ActualWidth / 2 ? 1 : 0);
            if (_seriesQueue.IndexOf(item) < to) to--;
            MoveSeriesItem(item, to);
            e.Effects = DragDropEffects.Move;
        }
        else e.Effects = DragDropEffects.None;
        ClearSeriesDrop(); e.Handled = true;
    }
}
