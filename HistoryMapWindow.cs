using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ArkCopyBuffer;

internal sealed partial class HistoryMapWindow : Window
{
    readonly HistoryStore store;
    readonly Grid body = new(), root = new();
    readonly StackPanel rail = new(), inspector = new();
    readonly Grid railHost = new();
    readonly Border toolSurface = new();
    readonly Canvas canvas = new() { ClipToBounds = true, Focusable = true };
    readonly TextBox search = new(), preview = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 160, MaxHeight = 320 };
    readonly ComboBox sources = new(), kinds = new(), mode = new();
    readonly Calendar calendar = new() { SelectionMode = CalendarSelectionMode.SingleRange };
    readonly ListBox results = new() { DisplayMemberPath = nameof(MapEntry.Preview), MinHeight = 80 };
    readonly TextBlock status = new(), metadata = new(), heading = new(), count = new(), dates = new(), zoomLabel = new();
    readonly Image picture = new() { MaxHeight = 240, Stretch = Stretch.Uniform };
    readonly Button copy, home, refresh, datesToggle, previewToggle, dayOverview, zoomOut, zoomIn, mapBack, filtersToggle, clearFilters;
    readonly WrapPanel filterPanel = new();
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    readonly DispatcherTimer reveal = new() { Interval = PanelWindow.SensitivePeekDelay };
    readonly HashSet<ClipItem> observed = [];
    readonly HashSet<Guid> invalid = [];
    MapEntry[] snapshot = [];
    MapNode[] nodes = [];
    Guid original, selected;
    int queryVersion, copyVersion, previewVersion;
    CancellationTokenSource? filtering;
    bool closed, updating, narrow, railOpen, railChosen = true, overview, inspectorOpen;
    double scale = 1;
    Point offset, drag;
    bool First => mode.SelectedIndex == 1;
    string? Source => sources.SelectedIndex <= 0 ? null : sources.SelectedItem as string;
    string? Kind => kinds.SelectedIndex <= 0 ? null : kinds.SelectedItem as string;
    string T(string s) => UiText.T(s);
    System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo(UiText.Language);
    Brush Brush(string key) => (Brush)FindResource(key);

    public HistoryMapWindow(HistoryStore store)
    {
        this.store = store;
        Title = T("Карта истории"); Width = 1280; Height = 820;
        MinWidth = 640; MinHeight = 480; UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "WindowSurface");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        FontFamily = (FontFamily)FindResource("TextFont"); FontSize = 14;
        root.Margin = new Thickness(12);
        root.SetResourceReference(Panel.BackgroundProperty, "WindowSurface");
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root;
        var chrome = new StackPanel { Margin = new Thickness(0, 0, 0, 8) }; root.Children.Add(chrome);
        var header = new WrapPanel(); chrome.Children.Add(header);
        var toolbar = filterPanel; toolbar.Margin = new Thickness(0, 6, 0, 0); toolbar.Visibility = Visibility.Collapsed; chrome.Children.Add(toolbar);
        filtersToggle = Button("Поиск в истории", () => { toolbar.Visibility = toolbar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; if (toolbar.Visibility == Visibility.Visible) { toolbar.UpdateLayout(); search.Focus(); } }); header.Children.Add(filtersToggle);
        clearFilters = Button("Сбросить фильтры", () => { groupHistory.Clear(); overview = false; updating = true; search.Clear(); calendar.SelectedDates.Clear(); sources.SelectedIndex = kinds.SelectedIndex = 0; updating = false; Schedule(); }); clearFilters.Visibility = Visibility.Collapsed; header.Children.Add(clearFilters);
        search.Width = 230; search.Style = (Style)FindResource("SearchBox"); search.Tag = T("Поиск в истории");
        AutomationProperties.SetName(search, T("Поиск в истории")); toolbar.Children.Add(search);
        toolbar.Children.Add(SelectorButton(sources, "Последний источник"));
        toolbar.Children.Add(SelectorButton(kinds, "Тип"));
        toolbar.Children.Add(SelectorButton(mode, "Дата записи"));
        AutomationProperties.SetName(sources, T("Последний источник")); AutomationProperties.SetName(kinds, T("Тип")); AutomationProperties.SetName(mode, T("Дата записи"));
        mode.ItemsSource = new[] { T("Последнее использование"), T("Первое копирование") }; mode.SelectedIndex = 0;
        refresh = Button("Обновить карту", () => RefreshSnapshot()); refresh.Visibility = Visibility.Collapsed; header.Children.Add(refresh);
        home = Button("К исходной записи", () => { groupHistory.Clear(); overview = false; search.Clear(); calendar.SelectedDates.Clear(); sources.SelectedIndex = kinds.SelectedIndex = 0; selected = original; RefreshSnapshot(); }); toolbar.Children.Add(home);
        ToolTipService.SetShowOnDisabled(home, true);
        home.IsEnabledChanged += (_, _) => home.ToolTip = home.IsEnabled ? null : T("Исходная запись удалена или срок хранения истёк.");
        datesToggle = Button("Даты", () => { railOpen = !rail.IsVisible; railChosen = true; ResizePanes(); }); header.Children.Add(datesToggle);
        previewToggle = Button("Просмотр", () => { if (narrow && rail.IsVisible) { railOpen = false; railChosen = true; inspectorOpen = true; } else inspectorOpen = !inspectorOpen; ResizePanes(); }); header.Children.Add(previewToggle);
        body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 1); root.Children.Add(body);
        railHost.Margin = new Thickness(0, 0, 16, 0);
        railHost.RowDefinitions.Add(new() { Height = GridLength.Auto }); railHost.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(railHost); railHost.Children.Add(new ScrollViewer { Content = rail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        rail.Children.Add(Label("По дням", 18)); BuildCalendar();
        rail.Children.Add(Button("Все даты", () => calendar.SelectedDates.Clear()));
        dates.TextWrapping = TextWrapping.Wrap; dates.Margin = new Thickness(0, 8, 0, 8); rail.Children.Add(dates);
        rail.Children.Add(count); Grid.SetRow(results, 1); railHost.Children.Add(results);
        StyleResults();
        preview.Style = (Style)FindResource("LocalizedTextBox"); preview.Height = double.NaN; preview.Padding = new Thickness(12);
        preview.SetResourceReference(Control.BackgroundProperty, "ControlFill"); preview.SetResourceReference(Control.ForegroundProperty, "TextPrimary"); preview.SetResourceReference(Control.BorderBrushProperty, "ControlStroke");
        reveal.Tick += (_, _) => { reveal.Stop(); var entry = snapshot.FirstOrDefault(e => e.Id == selected); var live = store.Items.FirstOrDefault(i => i.Id == selected); if (IsActive && entry is { Sensitive: true } && live != null && entry.ContentMatches(live) && !invalid.Contains(selected)) preview.Text = entry.Text; };
        preview.MouseEnter += (_, _) => reveal.Start(); preview.GotKeyboardFocus += (_, _) => reveal.Start();
        preview.MouseLeave += (_, _) => MaskPreview(); preview.LostKeyboardFocus += (_, _) => MaskPreview(); Deactivated += (_, _) => MaskPreview();
        VirtualizingPanel.SetIsVirtualizing(results, true); VirtualizingPanel.SetVirtualizationMode(results, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(results, true);
        canvas.SetResourceReference(BackgroundProperty, "CardFill"); Grid.SetColumn(canvas, 1); body.Children.Add(canvas);
        var mapHost = new Grid(); mapHost.RowDefinitions.Add(new() { Height = GridLength.Auto }); mapHost.RowDefinitions.Add(new());
        body.Children.Remove(canvas); Grid.SetColumn(mapHost, 1); body.Children.Add(mapHost); Grid.SetColumn(canvas, 0); Grid.SetRow(canvas, 1); mapHost.Children.Add(canvas);
        var mapTools = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        zoomOut = Button("−", () => Zoom(scale / 1.25, Center)); zoomOut.ToolTip = T("Уменьшить масштаб"); AutomationProperties.SetName(zoomOut, (string)zoomOut.ToolTip); mapTools.Children.Add(zoomOut);
        zoomLabel.VerticalAlignment = VerticalAlignment.Center; zoomLabel.MinWidth = 46; zoomLabel.TextAlignment = TextAlignment.Center; mapTools.Children.Add(zoomLabel);
        zoomIn = Button("+", () => Zoom(scale * 1.25, Center)); zoomIn.ToolTip = T("Увеличить масштаб"); AutomationProperties.SetName(zoomIn, (string)zoomIn.ToolTip); mapTools.Children.Add(zoomIn);
        mapTools.Children.Add(Button("Вписать карту", Fit));
        dayOverview = Button("Обзор дней", ShowOverview); mapTools.Children.Add(dayOverview);
        mapBack = Button("Назад к обзору", BackToOverview); mapBack.Visibility = Visibility.Collapsed; header.Children.Add(mapBack);
        toolSurface.Child = mapTools; toolSurface.HorizontalAlignment = HorizontalAlignment.Center; toolSurface.VerticalAlignment = VerticalAlignment.Center; toolSurface.Margin = new Thickness(4, 0, 0, 0); toolSurface.CornerRadius = new CornerRadius(6); toolSurface.SetResourceReference(Border.BackgroundProperty, "WindowSurface"); header.Children.Add(toolSurface);
        AutomationProperties.SetName(canvas, T("Карта истории")); AutomationProperties.SetName(results, T("Результаты поиска"));
        inspector.Margin = new Thickness(18, 0, 0, 0); var inspectorScroll = new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(inspectorScroll, 2); body.Children.Add(inspectorScroll);
        heading.FontSize = 20; heading.FontWeight = FontWeights.SemiBold; heading.TextWrapping = TextWrapping.Wrap;
        inspector.Children.Add(heading); inspector.Children.Add(picture); inspector.Children.Add(preview);
        metadata.TextWrapping = TextWrapping.Wrap; metadata.Margin = new Thickness(0, 16, 0, 16); inspector.Children.Add(metadata);
        copy = Button("Копировать", async () => await Copy()); header.Children.Add(copy);
        inspector.Children.Add(Button("Центрировать здесь", () => CenterOn(selected)));
        status.Margin = new Thickness(4, 4, 0, 0); status.TextWrapping = TextWrapping.Wrap; status.ToolTip = T("Текущие сохранённые записи, а не прошлые версии содержимого."); status.Visibility = Visibility.Collapsed; Grid.SetRow(status, 2); root.Children.Add(status);
        search.TextChanged += (_, _) => Schedule();
        sources.SelectionChanged += (_, _) => Schedule(); kinds.SelectionChanged += (_, _) => Schedule(); mode.SelectionChanged += (_, _) => Schedule();
        calendar.SelectedDatesChanged += (_, _) => { DrawCalendar(); Schedule(); };
        results.SelectionChanged += (_, _) => { if (!updating && results.SelectedItem is MapEntry e) Select(e.Id); };
        results.MouseDoubleClick += (_, _) => CenterOn(selected);
        debounce.Tick += async (_, _) => { debounce.Stop(); await Filter(); };
        canvas.SizeChanged += (_, e) => { offset += new Vector((e.NewSize.Width - e.PreviousSize.Width) / 2, (e.NewSize.Height - e.PreviousSize.Height) / 2); Draw(); }; SizeChanged += (_, _) => ResizePanes();
        canvas.MouseWheel += (_, e) => { Zoom(scale * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(canvas), pointerTarget: true); e.Handled = true; };
        canvas.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource != canvas) return; canvas.Focus(); if (overview) return; drag = e.GetPosition(canvas); canvas.CaptureMouse(); e.Handled = true; };
        canvas.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(canvas);
            if (!canvas.IsMouseCaptured) return;
            offset += p - drag; drag = p; Draw();
        };
        canvas.MouseLeftButtonUp += (_, _) => canvas.ReleaseMouseCapture();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { filterPanel.Visibility = Visibility.Visible; filterPanel.UpdateLayout(); search.Focus(); search.SelectAll(); e.Handled = true; }
            else if (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt && groupHistory.Count > 0) { BackToOverview(); e.Handled = true; }
            else if (e.Key == Key.F5) { RefreshSnapshot(); e.Handled = true; }
            else if (e.Key == Key.Escape) { if (search.Text.Length > 0) search.Clear(); else if (filterPanel.Visibility == Visibility.Visible) { filterPanel.Visibility = Visibility.Collapsed; canvas.Focus(); } else Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.FocusedElement is not ButtonBase && (canvas.IsKeyboardFocused || results.IsKeyboardFocusWithin)) { CenterOn(selected); e.Handled = true; }
            else if (canvas.IsKeyboardFocused && !overview && e.Key is Key.Left or Key.Right or Key.Up or Key.Down) { offset += e.Key switch { Key.Left => new Vector(80, 0), Key.Right => new Vector(-80, 0), Key.Up => new Vector(0, 80), _ => new Vector(0, -80) }; Draw(); e.Handled = true; }
            else if (canvas.IsKeyboardFocused && e.Key is Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus) { Zoom(scale * (e.Key is Key.Add or Key.OemPlus ? 1.25 : .8), Center); e.Handled = true; }
        };
        store.Items.CollectionChanged += CollectionChanged; store.Changed += StoreChanged; Observe();
        Closed += (_, _) => { closed = true; ClearMapThumbnails(); queryVersion++; copyVersion++; previewVersion++; filtering?.Cancel(); filtering?.Dispose(); debounce.Stop(); reveal.Stop(); store.Items.CollectionChanged -= CollectionChanged; store.Changed -= StoreChanged; foreach (var i in observed) i.PropertyChanged -= ItemChanged; observed.Clear(); };
    }

    TextBlock Label(string text, double size) => new() { Text = T(text), FontSize = size, Margin = new Thickness(0, 0, 0, 8) };
    Button Button(string text, Action action)
    {
        var b = new Button { Content = T(text), Style = (Style)FindResource("StandardButton"), Focusable = true, Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6), MinHeight = 32 };
        AutomationProperties.SetName(b, T(text)); b.Click += (_, _) => action(); return b;
    }
    Point Center => new(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
    public void OpenAt(Guid id)
    {
        groupHistory.Clear(); overview = false;
        original = selected = id;
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var cursor = System.Windows.Forms.Cursor.Position;
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = cursor.X, Y = cursor.Y }, Native.MONITOR_DEFAULTTONEAREST);
        double dpiScale = Native.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1;
        Width = Math.Min(1280, screen.Width / dpiScale - 32); Height = Math.Min(820, screen.Height / dpiScale - 32);
        MinWidth = Math.Min(640, Width); MinHeight = Math.Min(480, Height);
        Left = screen.Left / dpiScale + (screen.Width / dpiScale - Width) / 2;
        Top = screen.Top / dpiScale + (screen.Height / dpiScale - Height) / 2;
        updating = true; search.Clear(); calendar.SelectedDates.Clear(); sources.SelectedIndex = kinds.SelectedIndex = 0; updating = false;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate(); RefreshSnapshot();
    }
    void ResizePanes()
    {
        narrow = ActualWidth < 1100;
        bool showRail = railOpen && (!narrow || railChosen);
        rail.Visibility = showRail ? Visibility.Visible : Visibility.Collapsed;
        railHost.Visibility = rail.Visibility;
        inspector.Visibility = inspectorOpen && (!narrow || !showRail) ? Visibility.Visible : Visibility.Collapsed;
        body.ColumnDefinitions[0].Width = new GridLength(rail.IsVisible ? 230 : 0);
        body.ColumnDefinitions[2].Width = new GridLength(inspector.IsVisible ? (narrow ? 250 : 290) : 0);
        datesToggle.BorderBrush = Brush(showRail ? "Accent" : "ControlStroke"); previewToggle.BorderBrush = Brush(inspector.IsVisible ? "Accent" : "ControlStroke");
        railHost.RowDefinitions[0].Height = ActualHeight < 650 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        results.Height = double.NaN;
    }
    void Observe()
    {
        var live = store.Items.ToHashSet();
        foreach (var old in observed.Where(i => !live.Contains(i)).ToArray()) { old.PropertyChanged -= ItemChanged; observed.Remove(old); }
        foreach (var i in store.Items) if (observed.Add(i)) i.PropertyChanged += ItemChanged;
    }
    void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { Observe(); StoreChanged(); }
    void ItemChanged(object? sender, PropertyChangedEventArgs e) => StoreChanged();
    void StoreChanged()
    {
        if (closed) return;
        var live = store.Items.ToDictionary(i => i.Id);
        var removed = snapshot.Where(e => !live.ContainsKey(e.Id)).Select(e => e.Id).ToHashSet();
        foreach (var entry in snapshot) if (live.TryGetValue(entry.Id, out var i) && !entry.ContentMatches(i)) invalid.Add(entry.Id);
        if (removed.Count > 0 || invalid.Count > 0)
        {
            ClearMapThumbnails();
            queryVersion++; copyVersion++; previewVersion++;
            snapshot = snapshot.Where(e => !removed.Contains(e.Id)).ToArray();
            nodes = nodes.Where(n => !removed.Contains(n.Entry.Id)).ToArray();
            nodes = nodes.Where(n => !invalid.Contains(n.Entry.Id)).ToArray();
            updating = true; results.ItemsSource = nodes.Select(n => n.Entry).ToArray(); results.SelectedItem = nodes.FirstOrDefault(n => n.Entry.Id == selected)?.Entry; updating = false;
            if (removed.Contains(selected) || invalid.Contains(selected)) ClearPreview();
            UpdateCounts(); Draw();
        }
        home.IsEnabled = live.ContainsKey(original);
        refresh.Content = T("Есть изменения");
        refresh.Visibility = Visibility.Visible;
    }
    void RefreshSnapshot()
    {
        ClearMapThumbnails();
        copyVersion++; previewVersion++; invalid.Clear();
        snapshot = store.Items.Select(MapEntry.Capture).ToArray();
        DrawCalendar();
        if (!snapshot.Any(e => e.Id == selected)) selected = snapshot.Any(e => e.Id == original) ? original : snapshot.OrderByDescending(e => e.Used).FirstOrDefault()?.Id ?? Guid.Empty;
        month = snapshot.FirstOrDefault(e => e.Id == selected)?.Date(First) ?? DateTime.Today; DrawCalendar();
        updating = true;
        var source = Source; var kind = Kind;
        sources.ItemsSource = new[] { T("Все источники") }.Concat(snapshot.Select(e => e.Source).Distinct().Order()).ToArray();
        kinds.ItemsSource = new[] { T("Все типы") }.Concat(snapshot.Select(e => e.Kind).Distinct().Order()).ToArray();
        sources.SelectedIndex = 0; kinds.SelectedIndex = 0;
        if (source != null && sources.Items.Contains(source)) sources.SelectedItem = source;
        if (kind != null && kinds.Items.Contains(kind)) kinds.SelectedItem = kind;
        updating = false; refresh.Content = T("Обновить карту"); refresh.Visibility = Visibility.Collapsed; home.IsEnabled = snapshot.Any(e => e.Id == original);
        status.Text = ""; status.Visibility = Visibility.Collapsed;
        _ = Filter(true);
    }
    void UpdateFilterIndicator()
    {
        var active = new List<string>();
        if (search.Text.Trim().Length > 0) active.Add(T("Поиск в истории") + ": " + search.Text.Trim());
        if (Source is { } source) active.Add(T("Последний источник") + ": " + SourceLabel(source));
        if (Kind is { } kind) active.Add(T("Тип") + ": " + KindLabel(kind));
        if (calendar.SelectedDates.Count > 0) active.Add(T("Даты") + ": " + calendar.SelectedDates.Min().ToString("d", Culture) + " – " + calendar.SelectedDates.Max().ToString("d", Culture));
        clearFilters.Visibility = active.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        clearFilters.ToolTip = string.Join("\n", active);
    }
    void Schedule() { if (updating || closed) return; UpdateFilterIndicator(); queryVersion++; filtering?.Cancel(); debounce.Stop(); debounce.Start(); }
    async Task Filter(bool center = false)
    {
        UpdateFilterIndicator();
        int version = ++queryVersion;
        filtering?.Cancel(); filtering?.Dispose(); filtering = new(); var token = filtering.Token;
        var data = snapshot; var query = search.Text.Trim(); var source = Source; var kind = Kind; bool first = First;
        DateTime? from = calendar.SelectedDates.Count > 0 ? calendar.SelectedDates.Min() : null;
        DateTime? to = calendar.SelectedDates.Count > 0 ? calendar.SelectedDates.Max() : null;
        MapNode[] layout;
        try { layout = await Task.Run(() => HistoryMap.Layout(data.Where(e => { token.ThrowIfCancellationRequested(); return e.Matches(query, source, kind, from, to, first); }), first), token); }
        catch (OperationCanceledException) { return; }
        if (closed || version != queryVersion) return;
        nodes = layout.Where(n => !invalid.Contains(n.Entry.Id)).ToArray();
        updating = true; results.ItemsSource = nodes.Select(n => n.Entry).ToArray(); results.SelectedItem = nodes.FirstOrDefault(n => n.Entry.Id == selected)?.Entry; updating = false;
        UpdateCounts();
        if (!nodes.Any(n => n.Entry.Id == selected)) selected = nodes.FirstOrDefault()?.Entry.Id ?? Guid.Empty;
        Select(selected);
        if (overview) ShowOverview(); else if (center || nodes.Length > 0) CenterOn(selected); else Draw();
    }
    void MaskPreview() { reveal.Stop(); if (snapshot.FirstOrDefault(e => e.Id == selected) is { Sensitive: true }) preview.Text = "********"; }
    void UpdateCounts()
    {
        count.Text = UiText.F($"Записей: {nodes.Length}"); DrawCalendar();
        dates.Text = string.Join("\n", nodes.GroupBy(n => n.Entry.Date(First).Date).OrderByDescending(g => g.Key).Take(4).Select(g => $"{g.Key.ToString("d", Culture)} · {g.Count()}"));
    }
    void ClearPreview(bool changed = true) { reveal.Stop(); copy.IsEnabled = false; preview.Clear(); picture.Source = null; heading.Text = changed ? T("Обновите карту для просмотра изменений") : ""; metadata.Text = ""; }
    async void Select(Guid id)
    {
        reveal.Stop(); selected = id; int version = ++previewVersion; copyVersion++;
        updating = true; results.SelectedItem = results.Items.Cast<MapEntry>().FirstOrDefault(e => e.Id == id); updating = false;
        var entry = snapshot.FirstOrDefault(e => e.Id == id); var live = store.Items.FirstOrDefault(i => i.Id == id);
        if (entry == null || live == null || invalid.Contains(id) || !entry.ContentMatches(live)) { ClearPreview(entry != null); Draw(); return; }
        heading.Text = entry.Sensitive ? T("Защищённое содержимое") : KindLabel(entry.Kind);
        preview.Text = entry.Sensitive ? "********" : entry.Hash == null ? entry.Text : entry.Preview;
        preview.FontFamily = (FontFamily)FindResource(entry.Mono ? "MonoFont" : "TextFont");
        metadata.Text = T("Последний источник") + ": " + SourceLabel(entry.Source) + "\n" + T("Первое копирование") + ": " + entry.Created.ToLocalTime().ToString("g", Culture) + "\n" + T("Последнее использование") + ": " + entry.Used.ToLocalTime().ToString("g", Culture);
        copy.IsEnabled = true; picture.Source = null; Draw();
        if (entry.Hash != null && !entry.Sensitive)
        {
            try
            {
                var thumb = await Task.Run(() => ImageFiles.LoadBitmap(System.IO.Path.Combine(store.ImagesDir, entry.Hash + ".thumb.dat")));
                if (!closed && version == previewVersion && !invalid.Contains(id) && store.Items.Any(i => i.Id == id)) picture.Source = thumb;
            }
            catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or NotSupportedException) { if (version == previewVersion) SetStatus("Просмотр недоступен"); }
        }
    }
    async Task Copy()
    {
        var entry = snapshot.FirstOrDefault(e => e.Id == selected); var item = store.Items.FirstOrDefault(i => i.Id == selected);
        if (entry == null || item == null || invalid.Contains(selected) || !entry.ContentMatches(item)) { ClearPreview(); return; }
        int version = ++copyVersion; copy.IsEnabled = false;
        bool Current() => !closed && version == copyVersion && store.Items.Contains(item) && !invalid.Contains(item.Id) && entry.ContentMatches(item);
        try { await Paster.SetClipboardAsync(item, false, store.ImagesDir, Current); if (Current()) SetStatus("Скопировано"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) { App.Log(ex); SetStatus("Не удалось скопировать. Повторите попытку."); } }
        finally { if (Current()) copy.IsEnabled = true; }
    }
    void SetStatus(string text) { status.Text = T(text); status.Visibility = Visibility.Visible; }
}
