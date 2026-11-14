using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ArkCopyBuffer;

partial class TextEditorWindow : Window
{
    readonly HistoryStore _store;
    readonly ClipItem _item;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly ListCollectionView _sources;
    int _saveVersion;
    bool _ready, _closing, _allowClose;

    internal TextEditorWindow(HistoryStore store, ClipItem item)
    {
        _store = store;
        _item = item;
        _sources = new ListCollectionView(store.Items);
        InitializeComponent();
        Editor.Text = item.Text;
        Editor.IsUndoEnabled = false;
        Editor.IsUndoEnabled = true;
        _sources.Filter = value => value is ClipItem source && source != item && !source.IsMedia && !source.IsImage
            && source.Text.Contains(HistorySearch.Text, StringComparison.OrdinalIgnoreCase);
        Sources.ItemsSource = _sources;
        store.Changed += RefreshSources;
        Editor.FontFamily = (System.Windows.Media.FontFamily)FindResource(item.IsMono || item.IsNodeGraph ? "MonoFont" : "TextFont");
        Origin.Text = $"{item.SourceApp ?? UiText.T("Буфер обмена")} · {item.CreatedAt.ToLocalTime():dd.MM.yyyy, HH:mm}";
        _ready = true;
        UpdateTools();
        SetStatus(UiText.T("Сохранено"), "\uE73E", "SuccessIcon");
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SaveChangesAsync(); };
        SourceInitialized += (_, _) => { if (App.IsDarkTheme) Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle); };
        Loaded += (_, _) => { ConstrainBounds(); Editor.Focus(); Motion.Enter(Sheet, dy: 6); };
        Closing += OnClosing;
        Closed += (_, _) => { _debounce.Stop(); store.Changed -= RefreshSources; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) ToggleFindReplace_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (FindReplacePanel.IsVisible) ToggleFindReplace_Click(this, e);
                else Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && (ReplaceFrom.IsKeyboardFocused || ReplaceTo.IsKeyboardFocused))
            {
                if (!e.IsRepeat && ReplaceAllButton.IsEnabled) ReplaceAll_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Editor.MoveFocus(new TraversalRequest(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
                e.Handled = true;
            }
        };
        Editor.PreviewKeyUp += (_, _) => UpdateTools();
    }

    void ConstrainBounds()
    {
        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var transform = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
        var bounds = new Rect(transform.Transform(new Point(area.Left, area.Top)), transform.Transform(new Point(area.Right, area.Bottom)));
        Width = Math.Min(Width, bounds.Width);
        Height = Math.Min(Height, bounds.Height);
        Left = Math.Clamp(Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - Width));
        Top = Math.Clamp(Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - Height));
    }

    void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        UpdateTools();
        SetStatus(UiText.T("Ожидает сохранения…"), "\uE823", "TextSecondary");
        _debounce.Start();
    }

    void Editor_SelectionChanged(object sender, RoutedEventArgs e) { if (_ready) UpdateTools(); }
    void UpdateTools()
    {
        Count.Text = UiText.F($"{Editor.Text.Length:N0} симв.");
        UndoButton.IsEnabled = Editor.CanUndo;
        RedoButton.IsEnabled = Editor.CanRedo;
        UpdateSourceActions();
    }
    void Undo_Click(object sender, RoutedEventArgs e) { Editor.Undo(); UpdateTools(); Editor.Focus(); }
    void Redo_Click(object sender, RoutedEventArgs e) { Editor.Redo(); UpdateTools(); Editor.Focus(); }

    void ToggleFindReplace_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        bool open = FindReplacePanel.Visibility != Visibility.Visible;
        FindReplacePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        FindReplaceStatus.Visibility = Visibility.Collapsed;
        if (open) { ReplaceFrom.Focus(); ReplaceFrom.SelectAll(); }
        else Editor.Focus();
    }

    void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || ReplaceFrom.Text.Length == 0) return;
        string? result;
        try { result = PasteReplacement.ReplaceAll(Editor.Text, ReplaceFrom.Text, ReplaceTo.Text, _store.Storage.MaxChars); }
        catch (PasteReplacement.LimitExceededException)
        {
            ShowFindReplaceStatus(UiText.F($"Лимит текста в настройках: {_store.Storage.MaxChars:N0} символов. Сократите текст или увеличьте лимит."));
            return;
        }
        if (result == null) { ShowFindReplaceStatus(UiText.T("Текст не изменился.")); return; }

        FindReplaceStatus.Visibility = Visibility.Collapsed;
        int caret = Math.Min(Editor.CaretIndex, result.Length);
        using (Editor.DeclareChangeBlock())
        {
            Editor.SelectAll();
            Editor.SelectedText = result;
            Editor.Select(caret, 0);
        }
        Editor.Focus();
    }

    void ShowFindReplaceStatus(string message)
    {
        FindReplaceStatus.Text = message;
        FindReplaceStatus.Visibility = Visibility.Visible;
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(FindReplaceStatus)?.RaiseAutomationEvent(
            System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    void RefreshSources() { _sources.Refresh(); UpdateSourceActions(); }
    void HistorySearch_TextChanged(object sender, TextChangedEventArgs e) { if (_ready) RefreshSources(); }
    void Sources_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateSourceActions(); }
    void UpdateSourceActions()
    {
        var source = Sources.SelectedItem as ClipItem;
        InsertButton.IsEnabled = !_closing && source != null;
        ReplacePathButton.IsEnabled = !_closing && source != null && FindPathReplacement(Editor.Text, source.Text) != null;
        PathHint.Text = source == null ? UiText.T("Выберите запись из истории")
            : ReplacePathButton.IsEnabled ? UiText.T("Один путь или маркер — можно заменить автоматически")
            : UiText.T("Для других фрагментов используйте выделение и вставку");
    }
    void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || Sources.SelectedItem is not ClipItem source) return;
        _item.IsSensitive |= source.IsSensitive;
        int start = Editor.SelectionStart;
        using (Editor.DeclareChangeBlock())
        {
            Editor.SelectedText = source.Text;
            Editor.Select(start + source.Text.Length, 0);
        }
        UpdateTools();
        Editor.Focus();
    }
    void Sources_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>() != null) Insert_Click(sender, e);
    }
    void ReplacePath_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || Sources.SelectedItem is not ClipItem source || FindPathReplacement(Editor.Text, source.Text) is not { } change) return;
        _item.IsSensitive |= source.IsSensitive;
        using (Editor.DeclareChangeBlock())
        {
            Editor.Select(change.Start, change.Length);
            Editor.SelectedText = change.Text;
            Editor.Select(change.Start, change.Text.Length);
        }
        UpdateTools();
        Editor.Focus();
    }

    internal static (int Start, int Length, string Text)? FindPathReplacement(string command, string source)
    {
        if (!TextSubstitution.IsPath(source)) return null;
        var change = TextSubstitution.Automatic(TextSubstitution.Find(command, source));
        return change is { Priority: <= 1 } ? (change.Start, change.Length, change.Text) : null;
    }

    void SetStatus(string text, string glyph, string brush)
    {
        Status.Text = text;
        Status.SetResourceReference(TextBlock.ForegroundProperty, brush);
        StatusGlyph.Text = glyph;
        StatusGlyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    internal async Task<bool> SaveChangesAsync()
    {
        string text = Editor.Text;
        int version = ++_saveVersion;
        SetStatus(UiText.T("Сохраняется…"), "\uE895", "TextSecondary");
        try
        {
            _store.UpdateText(_item, text);
            await _store.SaveAsync();
            if (version == _saveVersion)
            {
                if (Editor.Text == text) SetStatus(UiText.T("Сохранено"), "\uE73E", "SuccessIcon");
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            if (version == _saveVersion)
                SetStatus(UiText.T("Не сохранено. ") + ex.Message, "\uEA39", "CriticalText");
            return false;
        }
    }

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _debounce.Stop();
        Editor.IsReadOnly = true;
        UndoButton.IsEnabled = RedoButton.IsEnabled = false;
        UpdateSourceActions();
        if (await SaveChangesAsync())
        {
            _allowClose = true;
            Close();
        }
        else
        {
            _closing = false;
            Editor.IsReadOnly = false;
            UpdateTools();
            Editor.Focus();
        }
    }

    internal static void SelfTest(string directory, Action<bool, string> check)
    {
        var store = new HistoryStore(System.IO.Path.Combine(directory, "editor", "history.dat"));
        var now = DateTime.UtcNow;
        const string command = "./build/mpv.exe --config-dir=.tmp/drops-play/cfg --log-file=.tmp/drops-play/play.log --script-opts=moment_gacha-dev=yes \"D:/путь/к/клипу.mp4\"";
        const string path = @"D:\Shadow Play\OBS\deadlock\deadlock_30.09.2026_01-45-23.mp4";
        store.Add(command, "old rtf", "old html", now, "Code");
        var item = store.Items.Single();
        store.TogglePin(item);
        var group = store.AddGroup("Команды")!;
        store.MovePinned(item, group.Id);
        store.SetTileColor(item, 5);
        _ = item.Kind; _ = item.IsMono; _ = item.TileTitle;
        store.Add(path, null, null, now.AddSeconds(1), "explorer");
        var source = store.Items.First();
        var editor = new TextEditorWindow(store, item);
        editor.Editor.ApplyTemplate();
        bool closed = false;
        editor.Closed += (_, _) => closed = true;
        try
        {
            editor.Sources.SelectedItem = source;
            check(editor.ReplacePathButton.IsEnabled, "редактор распознаёт один путь для автоматической замены");
            editor.ReplacePath_Click(editor, new RoutedEventArgs());
            string expected = command.Replace("D:/путь/к/клипу.mp4", path);
            check(editor.Editor.Text == expected && item.Text == command, "подстановка меняет только путь и ждёт паузу");
            PumpUntil(() => item.Text == expected && editor.Status.Text == UiText.T("Сохранено"));
            check(item.Text == expected && editor.Status.Text == UiText.T("Сохранено"), "пауза 1 сек сохраняет редактор на диск");
            var restored = new HistoryStore(System.IO.Path.Combine(directory, "editor", "history.dat"));
            restored.Load(now);
            check(restored.Items.Single(i => i.Id == item.Id).Text == expected, "автосохранение переживает загрузку истории");
            check(item.Pinned && item.PinnedGroupId == group.Id && item.TileColor == 5 && item.CreatedAt == now
                && item.LastUsedAt == now && item.Rtf == null && item.Html == null && store.Items.Count == 2,
                "редактирование сохраняет Id, время, группу, цвет и число записей; старые форматы сброшены");
            editor.Editor.Select(0, 5);
            editor.Sources.SelectedItem = source;
            editor.Insert_Click(editor, new RoutedEventArgs());
            check(editor.Editor.Text == path + expected[5..], "история заменяет выделение и сохраняет остальной текст");
            editor.Editor.Undo();
            check(editor.Editor.Text == expected, "подстановку можно отменить через Undo");
            const string documentNumbers = "ЯС42-000839-2\nЯС42-000840\nДР-123";
            editor.Editor.Text = documentNumbers;
            editor.ToggleFindReplace_Click(editor, new RoutedEventArgs());
            editor.ReplaceFrom.Text = "ЯС42-";
            editor.ReplaceTo.Text = "ЯС42вн";
            editor.ReplaceAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(editor.Editor.Text == "ЯС42вн000839-2\nЯС42вн000840\nДР-123" && item.Text == expected,
                "редактор заменяет все совпадения в черновике, сохраняя другие дефисы");
            editor.Editor.Undo();
            check(editor.Editor.Text == documentNumbers, "замена всех совпадений отменяется одним Undo");
            editor.ReplaceFrom.Text = "missing";
            editor.ReplaceAll_Click(editor, new RoutedEventArgs());
            check(editor.Editor.Text == documentNumbers && editor.FindReplaceStatus.Visibility == Visibility.Visible,
                "при отсутствии совпадений редактор сообщает об этом без изменения черновика");
            editor.Editor.Text = expected;
            editor.HistorySearch.Text = "не найдено";
            check(editor.Sources.Items.Count == 0, "поиск в боковой истории фильтрует записи");
            editor.HistorySearch.Text = "deadlock";
            check(editor.Sources.Items.Count == 1, "поиск возвращает путь и исключает редактируемую запись");
            check(FindPathReplacement("mpv \"D:/a.mp4\" \"D:/b.mp4\"", path) == null
                && FindPathReplacement("mpv --config-dir=\"D:/cfg\"", path) == null
                && FindPathReplacement(command, "обычный текст") == null
                && FindPathReplacement(command, path + "\nещё текст") == null,
                "автозамена отказывается от неоднозначных путей, параметров и многострочного текста");
            editor.Editor.Text = "первый черновик";
            var pause = System.Diagnostics.Stopwatch.StartNew();
            PumpUntil(() => pause.ElapsedMilliseconds >= 550);
            editor.Editor.Text = "второй черновик";
            pause.Restart();
            PumpUntil(() => pause.ElapsedMilliseconds >= 650);
            check(item.Text == expected, "новый ввод сбрасывает секунду ожидания автосохранения");
            PumpUntil(() => item.Text == "второй черновик" && editor.Status.Text == UiText.T("Сохранено"));
            check(item.Text == "второй черновик" && editor.Status.Text == UiText.T("Сохранено"), "сохраняется последний черновик после полной паузы");
            editor.Editor.Text = "финальная правка без паузы";
            editor.Close();
            PumpUntil(() => closed);
            check(closed && item.Text == "финальная правка без паузы", "закрытие сохраняет последнюю правку без ожидания таймера");
            restored.Load(now);
            check(restored.Items.Single(i => i.Id == item.Id).Text == item.Text && item.Kind == "text" && !item.IsMono
                && item.TileTitle == item.Text, "закрытие записало диск и сбросило кеши типа и предпросмотра");
            store.UpdateText(item, "");
            check(item.Text == "", "пустая правка не удаляет запись");
            store.Storage = new StorageSettings(TextThousands: 10);
            bool oversizedRejected = false;
            try { store.UpdateText(item, new string('x', 10001)); } catch (InvalidOperationException) { oversizedRejected = true; }
            check(oversizedRejected && item.Text == "", "превышение лимита не обрезает текст и не меняет запись");
            store.Remove(item);
            bool removedRejected = false;
            try { store.UpdateText(item, "не воскресать"); } catch (InvalidOperationException) { removedRejected = true; }
            check(removedRejected && !store.Items.Contains(item), "удалённая запись не восстанавливается редактором");
        }
        finally { editor._allowClose = true; editor.Close(); }

        var failedPath = System.IO.Path.Combine(directory, "editor-error", "history.dat");
        System.IO.Directory.CreateDirectory(failedPath); // На месте файла каталог: воспроизводим отказ записи.
        var failedStore = new HistoryStore(failedPath);
        failedStore.Add("исходник", null, null, now);
        var failedEditor = new TextEditorWindow(failedStore, failedStore.Items.Single());
        bool failedClosed = false;
        failedEditor.Closed += (_, _) => failedClosed = true;
        try
        {
            failedEditor.Editor.Text = "важная несохранённая правка";
            failedEditor.Close();
            PumpUntil(() => failedEditor.Status.Text.StartsWith(UiText.T("Не сохранено. "), StringComparison.Ordinal));
            check(!failedClosed && !failedEditor.Editor.IsReadOnly && failedEditor.Editor.Text == "важная несохранённая правка",
                "при ошибке диска окно остаётся открытым и сохраняет введённый текст");
        }
        finally { failedEditor._allowClose = true; failedEditor.Close(); }
    }

    internal static void PumpUntil(Func<bool> done)
    {
        var frame = new DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (done() || elapsed.ElapsedMilliseconds > 5000) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }
}
