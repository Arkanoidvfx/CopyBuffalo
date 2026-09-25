using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static ArkCopyBuffer.Native;

namespace ArkCopyBuffer;

partial class PanelWindow : Window
{
    const int GridColumns = 3;
    const double ShadowMargin = 16; // поле под тень вокруг панели (Root.Margin в XAML)
    bool _hiding;
    bool _confirmingClear;
    CancellationTokenSource? _gifPlayback;
    Image? _gifTarget;
    readonly DispatcherTimer _zoomTimer = new();
    readonly DispatcherTimer _outsideTimer = new();
    bool _menuOpen;
    Image? _zoomTarget;
    ToolTip? _zoomTip;
    Border? _zoomProgress;

    /// <summary>
    /// Анимация окна. Своя настройка, а не системные «Эффекты анимации»: у многих они выключены ради скорости,
    /// а анимацию панели просили отдельно. Выключается пунктом в трее.
    /// </summary>
    public bool Animations { get; set; } = true;
    public int ShowDurationMs { get => (int)ShowTiming.Value; set => ShowTiming.Value = Math.Clamp(value, 0, 1000); }
    public int HideDurationMs { get => (int)HideTiming.Value; set => HideTiming.Value = Math.Clamp(value, 0, 1000); }
    public static readonly DependencyProperty ImageZoomDelayMsProperty = DependencyProperty.Register(
        nameof(ImageZoomDelayMs), typeof(int), typeof(PanelWindow), new PropertyMetadata(1000));
    public int ImageZoomDelayMs
    {
        get => (int)GetValue(ImageZoomDelayMsProperty);
        set { int ms = Math.Clamp(value, 100, 3000); SetValue(ImageZoomDelayMsProperty, ms); ZoomDelaySlider.Value = ms; }
    }
    public int ImageZoomScale { get => (int)ZoomScaleSlider.Value; set => ZoomScaleSlider.Value = Math.Clamp(value, 1, 5); }
    public bool AutoHideOutside { get => OutsideHideToggle.IsChecked == true; set => OutsideHideToggle.IsChecked = value; }
    public int OutsideHideDelayMs { get => (int)OutsideHideSlider.Value; set => OutsideHideSlider.Value = Math.Clamp(value, 500, 10000); }
    public event Action<int, int>? TimingsChanged;
    public event Action<int>? ImageZoomDelayChanged;
    public event Action<int>? ImageZoomScaleChanged;
    public event Action<bool>? AutoHideOutsideChanged;
    public event Action<int>? OutsideHideDelayChanged;

    readonly HistoryStore _store;
    readonly ICollectionView _view;       // вкладка «Буфер обмена»: всё
    readonly ObservableCollection<object> _pinned = new();
    readonly PinnedGroup _ungrouped = new() { Id = Guid.Empty, Name = "Без группы" };
    Point? _dragStart;
    ClipItem? _dragItem;
    ListBoxItem? _dropTarget;
    bool _dragging;
    bool _editingGroup;
    readonly SymbolCatalog _symbols;
    readonly ListCollectionView _symbolView; // вкладка «Символы»: группы, закреплённые сверху
    IntPtr _hwnd;

    /// <summary>Выбрана запись: (запись, вставить без форматирования).</summary>
    public event Action<ClipItem, bool>? PasteRequested;
    /// <summary>Выбран символ: напечатать его в окно.</summary>
    public event Action<string>? TypeRequested;

    bool PinnedTab => TabPinned.IsChecked == true;
    bool SymbolsTab => TabSymbols.IsChecked == true;
    bool GridTab => PinnedTab || SymbolsTab;
    ListBox Active => SymbolsTab ? SymbolList : PinnedTab ? PinnedList : List;
    RadioButton[] Tabs => [TabAll, TabPinned, TabSymbols];

    /// <summary>Столбцов в текущей сетке: у плиток — 3, у символов — сколько клеток влезло по ширине.</summary>
    int Columns => SymbolsTab ? Math.Max(1, (int)(SymbolList.ActualWidth / 40)) : GridColumns;

    public PanelWindow(HistoryStore store, SymbolCatalog symbols)
    {
        InitializeComponent();
        _zoomTimer.Tick += (_, _) => OpenZoom();
        _outsideTimer.Tick += (_, _) =>
        {
            _outsideTimer.Stop();
            if (!IsVisible || _hiding || !AutoHideOutside || PointerInsidePanel()) return;
            if (_confirmingClear || _editingGroup || _dragging || _menuOpen) { _outsideTimer.Start(); return; }
            HideAnimated();
        };
        Root.MouseEnter += (_, _) => _outsideTimer.Stop();
        Root.MouseLeave += (_, _) => UpdateOutsideTimer();
        _store = store;
        _symbols = symbols;

        _symbolView = new ListCollectionView(symbols.Items) { Filter = o => SymbolCatalog.Matches((SymbolItem)o, Search.Text) };
        _symbolView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SymbolItem.Group)));
        SymbolList.ItemsSource = _symbolView;

        _view = CollectionViewSource.GetDefaultView(store.Items);
        _view.Filter = o => Matches((ClipItem)o);
        List.ItemsSource = _view;

        PinnedList.ItemsSource = _pinned;
        UpdatePinnedLayout();

        store.Changed += () => { UpdatePinnedLayout(); UpdateEmptyState(); };

        foreach (var list in new[] { List, PinnedList, SymbolList })
        {
            list.AddHandler(ListBoxItem.MouseLeftButtonUpEvent, new MouseButtonEventHandler(Item_Click), handledEventsToo: false);
            list.AddHandler(ListBoxItem.MouseRightButtonUpEvent, new MouseButtonEventHandler(Item_RightClick), handledEventsToo: false);
        }
        PinnedList.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(Pinned_EmptyRightClick), handledEventsToo: true);
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => UpdateOutsideTimer();
        SourceInitialized += (_, _) => InitNative();
    }

    // Картинки при поиске скрываем: искать в них нечего. Видео/аудио ищутся по имени файла.
    bool Matches(ClipItem i) => Search.Text.Length == 0
        || !i.IsImage && (i.IsMedia ? i.FileName ?? "" : i.Text).Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase);

    void UpdatePinnedLayout()
    {
        var selected = PinnedList.SelectedItem as ClipItem;
        _pinned.Clear();
        if (_store.Groups.Count > 0 && Search.Text.Length == 0) _pinned.Add(_ungrouped);
        foreach (var item in _store.PinnedIn(null).Where(Matches)) _pinned.Add(item);
        foreach (var group in _store.Groups)
        {
            var items = _store.PinnedIn(group.Id).Where(Matches).ToList();
            if (Search.Text.Length > 0 && items.Count == 0) continue;
            _pinned.Add(group);
            foreach (var item in items) _pinned.Add(item);
        }
        if (selected != null && _pinned.Contains(selected)) PinnedList.SelectedItem = selected;
    }

    void InitNative()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Инструментальное окно: нет в Alt+Tab и на панели задач.
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
    }

    /// <summary>Появление: размер 80% → 100%, видимость 0% → 100% за 0,1 с, с замедлением к концу.</summary>
    void AnimateIn()
    {
        _hiding = false;
        Animate(0.8, 1, 0, 1, TimeSpan.FromMilliseconds(ShowDurationMs), EasingMode.EaseOut, null);
    }

    /// <summary>Исчезновение: размер 100% → 50%, видимость 100% → 0% за 0,1 с, с ускорением; затем Hide.</summary>
    public void HideAnimated()
    {
        if (!IsVisible || _hiding) return;
        _outsideTimer.Stop();
        CancelZoom();
        StopGif();
        SettingsFlyout.Visibility = Visibility.Collapsed;
        _hiding = true;
        Animate(null, 0.5, null, 0, TimeSpan.FromMilliseconds(HideDurationMs), EasingMode.EaseIn, () =>
        {
            if (!_hiding) return; // пока исчезала, снова вызвали Win+V — не прячем
            _hiding = false;
            Hide();
        });
    }

    /// <summary>
    /// Scale и Opacity всего Root. From = null — от текущего значения (исчезновение посреди появления не дёргается).
    /// Анимация выключена в трее — сразу конечное состояние.
    /// </summary>
    void Animate(double? scaleFrom, double scaleTo, double? opacityFrom, double opacityTo, Duration duration, EasingMode mode, Action? done)
    {
        if (!Animations)
        {
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Root.BeginAnimation(OpacityProperty, null);
            RootScale.ScaleX = RootScale.ScaleY = Root.Opacity = 1;
            done?.Invoke();
            return;
        }
        var ease = new CubicEase { EasingMode = mode };
        DoubleAnimation Anim(double? from, double to) => new(to, duration) { From = from, EasingFunction = ease };
        var fade = Anim(opacityFrom, opacityTo);
        if (done != null) fade.Completed += (_, _) => done();
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(scaleFrom, scaleTo));
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(scaleFrom, scaleTo));
        Root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Показать у каретки (rect в физических пикселях экрана). Вкладка — та, что была открыта в прошлый раз.</summary>
    public void ShowAt(RECT anchor)
    {
        _outsideTimer.Stop();
        SettingsFlyout.Visibility = Visibility.Collapsed;
        // Списки живые (ObservableCollection), пересобирать их при каждом показе не нужно —
        // только если остался текст поиска (TextChanged сам вызовет RefreshViews).
        if (Search.Text.Length > 0) Search.Text = "";
        else ResetSelection();

        new WindowInteropHelper(this).EnsureHandle();
        Place(anchor);
        // Ставим стартовое состояние напрямую до Show: нулевая анимация может примениться
        // лишь на следующем кадре и показать старое содержимое после долгого простоя.
        _hiding = false;
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Root.BeginAnimation(OpacityProperty, null);
        RootScale.ScaleX = RootScale.ScaleY = Animations ? 0.8 : 1;
        Root.Opacity = Animations ? 0 : 1;
        Show();
        Place(anchor); // WPF при первом показе может переставить окно — фиксируем ещё раз
        if (!_hiding) AnimateIn();
        BringToFront();
        Search.Focus();
        UpdateOutsideTimer();
    }

    bool PointerInsidePanel()
    {
        var p = Mouse.GetPosition(Root);
        return p.X >= 0 && p.Y >= 0 && p.X < Root.ActualWidth && p.Y < Root.ActualHeight;
    }

    void UpdateOutsideTimer()
    {
        if (!IsVisible || _hiding || !AutoHideOutside || _menuOpen || PointerInsidePanel()) { _outsideTimer.Stop(); return; }
        if (_outsideTimer.IsEnabled) return;
        _outsideTimer.Interval = TimeSpan.FromMilliseconds(OutsideHideDelayMs);
        _outsideTimer.Start();
    }

    void RefreshViews()
    {
        _view.Refresh();
        UpdatePinnedLayout();
        _symbolView.Refresh();
        ResetSelection();
    }

    void ResetSelection()
    {
        foreach (var list in new[] { List, PinnedList, SymbolList })
            list.SelectedItem = list == PinnedList ? _pinned.OfType<ClipItem>().FirstOrDefault() : list.Items.Count > 0 ? list.Items[0] : null;
        if (Active.SelectedItem != null) Active.ScrollIntoView(Active.SelectedItem);
        UpdateEmptyState();
    }

    /// <summary>
    /// Прогрев при старте: создаём окно и прогоняем разметку за краем экрана,
    /// чтобы первый Win+V не ждал создания HWND, JIT и первого layout.
    /// </summary>
    public void WarmUp()
    {
        new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);
        ShowActivated = false;
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = true;
    }

    void Place(RECT a)
    {
        var mon = MonitorFromPoint(new POINT { X = a.Left, Y = a.Bottom }, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        GetDpiForMonitor(mon, 0, out var dpi, out _);
        double scale = dpi / 96.0;
        // ponytail: при разном DPI мониторов содержимое скрытого окна масштабируется по DPI прошлого монитора до первого перерисовывания; хватит, пока нет жалоб.
        // Считаем видимую панель (без поля под тень), окно — она плюс поле.
        int m = (int)(ShadowMargin * scale);
        int w = (int)(Width * scale) - 2 * m, h = (int)(Height * scale) - 2 * m, gap = (int)(6 * scale);
        var work = mi.rcWork;

        int x = a.Left;
        int y = a.Bottom + gap;
        bool above = y + h > work.Bottom;
        if (above) y = a.Top - gap - h; // не влезает снизу — над кареткой
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        // Растёт и сжимается от каретки: по горизонтали — её место на панели, по вертикали — край, ближний к ней.
        Root.RenderTransformOrigin = new Point(Math.Clamp((a.Left - x) / (double)w, 0, 1), above ? 1 : 0);
        SetWindowPos(_hwnd, HWND_TOPMOST, x - m, y - m, w + 2 * m, h + 2 * m, SWP_NOACTIVATE);
    }

    /// <summary>
    /// Хук — не «последний ввод» для нашего процесса, поэтому SetForegroundWindow может отказать.
    /// Подцепляемся к потоку текущего активного окна на время переключения.
    /// </summary>
    void BringToFront()
    {
        Activate();
        if (GetForegroundWindow() == _hwnd) return;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        try { SetForegroundWindow(_hwnd); BringWindowToTop(_hwnd); }
        finally { AttachThreadInput(me, fgThread, false); }
        Activate();
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PinnedList == null) return; // IsChecked из XAML срабатывает до создания остальных элементов
        CancelZoom();
        StopGif();
        if (SettingsFlyout != null) SettingsFlyout.Visibility = Visibility.Collapsed;
        foreach (var list in new[] { List, PinnedList, SymbolList })
            list.Visibility = list == Active ? Visibility.Visible : Visibility.Collapsed;
        PinnedBackground.Visibility = PinnedTab ? Visibility.Visible : Visibility.Collapsed;
        // «Очистить всё» относится только к истории: закреплённые и символы она не трогает.
        ClearButton.Visibility = TabAll.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GroupButton.Visibility = PinnedTab ? Visibility.Visible : Visibility.Collapsed;
        if (Active.SelectedIndex < 0 && Active.Items.Count > 0) Active.SelectedIndex = 0;
        UpdateEmptyState();
        Search.Focus();
    }

    void UpdateEmptyState()
    {
        if (PinnedList == null) return;
        bool empty = SymbolsTab ? _symbolView.IsEmpty : PinnedTab ? _pinned.Count == 0 : _view.IsEmpty;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Active.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = _store.Items.Any(i => !i.Pinned);
        if (!empty) return;
        bool searching = Search.Text.Length > 0;
        (EmptyTitle.Text, EmptyHint.Text) = (searching, PinnedTab) switch
        {
            (true, _) when SymbolsTab => ("Ничего не найдено", "Ищите по символу, коду (2014) или группе («валют»)."),
            (true, _) => ("Ничего не найдено", "Попробуйте другой запрос."),
            (false, true) => ("Закреплённых пока нет", "Нажмите на булавку у записи или Ctrl+P, и она появится здесь."),
            _ => ("Здесь пока пусто", "Скопируйте текст или картинку, и они появятся в этом списке."),
        };
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PinnedList == null) return;
        RefreshViews();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (SettingsFlyout.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { SettingsFlyout.Visibility = Visibility.Collapsed; Search.Focus(); e.Handled = true; }
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var selected = Active.SelectedItem as ClipItem;
        var symbol = Active.SelectedItem as SymbolItem;
        // В сетке ←/→ ходят по клеткам, пока в поиске пусто; иначе остаются курсору в поле поиска.
        bool gridArrows = GridTab && Search.Text.Length == 0;
        int step = GridTab ? Columns : 1;

        switch (e.Key)
        {
            case Key.Escape:
                if (Search.Text.Length > 0) Search.Text = ""; else HideAnimated();
                break;
            case Key.Tab:
                int i = Array.FindIndex(Tabs, t => t.IsChecked == true);
                Tabs[(i + (shift ? Tabs.Length - 1 : 1)) % Tabs.Length].IsChecked = true;
                break;
            case Key.Down: Move(step); break;
            case Key.Up: Move(-step); break;
            case Key.Right when gridArrows: Move(1); break;
            case Key.Left when gridArrows: Move(-1); break;
            case Key.PageDown: Move(GridTab ? step * 3 : 5); break;
            case Key.PageUp: Move(GridTab ? -step * 3 : -5); break;
            case Key.Enter when symbol != null:
                TypeRequested?.Invoke(symbol.Char);
                break;
            case Key.P when ctrl && symbol != null:
                KeepingSelection(() => _symbols.TogglePin(symbol.Char));
                break;
            case Key.Enter when selected != null:
                PasteRequested?.Invoke(selected, shift);
                break;
            case Key.Delete when selected != null && Search.Text.Length == 0:
                KeepingSelection(() => _store.Remove(selected));
                break;
            case Key.P when ctrl && selected != null:
                KeepingSelection(() => _store.TogglePin(selected));
                break;
            case >= Key.D1 and <= Key.D9 when ctrl:
                int idx = e.Key - Key.D1;
                var shortcuts = PinnedTab ? Active.Items.OfType<ClipItem>().Cast<object>().ToList() : Active.Items.Cast<object>().ToList();
                if (idx >= shortcuts.Count) break;
                if (shortcuts[idx] is SymbolItem s) TypeRequested?.Invoke(s.Char);
                else if (shortcuts[idx] is ClipItem clip) PasteRequested?.Invoke(clip, shift);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void Move(int delta)
    {
        var list = Active;
        if (list.Items.Count == 0) return;
        int index = Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1);
        if (list == PinnedList && list.Items[index] is PinnedGroup)
        {
            int direction = Math.Sign(delta);
            while (index >= 0 && index < list.Items.Count && list.Items[index] is PinnedGroup) index += direction;
            if (index < 0 || index >= list.Items.Count) return;
        }
        list.SelectedIndex = index;
        list.ScrollIntoView(list.SelectedItem);
    }

    /// <summary>Удаление/открепление: выделение остаётся на той же позиции, а не прыгает в начало.</summary>
    void KeepingSelection(Action change)
    {
        var list = Active;
        int idx = list.SelectedIndex;
        change();
        if (list.Items.Count > 0) list.SelectedIndex = Math.Clamp(idx, 0, list.Items.Count - 1);
    }

    static ClipItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItem;

    void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) return;
        switch ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext)
        {
            case ClipItem item: PasteRequested?.Invoke(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); break;
            case SymbolItem s: TypeRequested?.Invoke(s.Char); break;
        }
    }

    void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        var container = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        if (container?.DataContext is ClipItem item) ShowMenu(item, container, PlacementMode.MousePoint);
        else if (container?.DataContext is PinnedGroup group && group.Id != Guid.Empty) ShowGroupMenu(group, container);
        else if (container?.DataContext is PinnedGroup) ShowEmptyMenu(container);
        else if (container?.DataContext is SymbolItem s) ShowSymbolMenu(s, container);
        else return;
        e.Handled = true;
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _store.TogglePin(item);
        Search.Focus();
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && ItemOf(sender) is { } item) ShowMenu(item, button, PlacementMode.Bottom);
    }

    void Graph_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { IsNodeGraph: true } item) UnrealGraph.Open(item);
    }

    static MenuItem Entry(string header, string glyph, Action action)
    {
        var mi = new MenuItem { Header = header, Tag = glyph };
        mi.Click += (_, _) => action();
        return mi;
    }

    void ShowSymbolMenu(SymbolItem s, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Entry("Вставить", "\uE77F", () => TypeRequested?.Invoke(s.Char)));
        menu.Items.Add(Entry(s.Pinned ? "Открепить" : "Закрепить", s.Pinned ? "\uE77A" : "\uE718", () => _symbols.TogglePin(s.Char)));
        menu.Items.Add(Entry("Копировать", "\uE8C8", () => Clipboard.SetText(s.Char)));
        menu.Closed += (_, _) => Search.Focus();
        OpenMenu(menu);
    }

    void ShowMenu(ClipItem item, UIElement target, PlacementMode placement)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        menu.Items.Add(Entry("Вставить", "\uE77F", () => PasteRequested?.Invoke(item, false)));
        if (item.IsNodeGraph) menu.Items.Add(Entry("Открыть граф", "\uE8A7", () => UnrealGraph.Open(item)));
        if (item.ImageHash == null) menu.Items.Add(Entry("Вставить как текст", "\uE8D2", () => PasteRequested?.Invoke(item, true)));
        menu.Items.Add(Entry(item.Pinned ? "Открепить" : "Закрепить", item.Pinned ? "\uE77A" : "\uE718", () => _store.TogglePin(item)));
        if (item.Pinned)
        {
            var colors = new StackPanel { Orientation = Orientation.Horizontal };
            colors.Children.Add(new TextBlock { Text = "Цвет", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            for (int color = -1; color < ClipItem.TileColorNames.Length; color++)
            {
                int chosen = color;
                var swatch = new Button
                {
                    Width = 22, Height = 22, Margin = new Thickness(1),
                    Style = (Style)FindResource("ColorSwatchButton"),
                    Background = color < 0 ? (Brush)FindResource("CardFill") : ClipItem.ColorAccent(color),
                    BorderBrush = color == item.TileColor ? (Brush)FindResource("TextPrimary") : Brushes.Transparent,
                    BorderThickness = new Thickness(color == item.TileColor ? 2 : 0),
                    Content = color < 0 ? "×" : null,
                    ToolTip = color < 0 ? "Без цвета" : ClipItem.TileColorNames[color]
                };
                AutomationProperties.SetName(swatch, (string)swatch.ToolTip);
                swatch.Click += (_, _) => { _store.SetTileColor(item, chosen); menu.IsOpen = false; };
                colors.Children.Add(swatch);
            }
            menu.Items.Add(new MenuItem { Header = colors, Tag = "\uE790", StaysOpenOnClick = true });
        }
        menu.Items.Add(Entry("Удалить", "\uE74D", () => _store.Remove(item)));
        menu.Closed += (_, _) => Search.Focus();
        OpenMenu(menu);
    }

    void ShowGroupMenu(PinnedGroup group, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Entry("Переименовать", "\uE70F", () =>
        {
            if (PromptGroupName("Переименовать группу", group.Name) is { } name) _store.RenameGroup(group, name);
        }));
        menu.Items.Add(Entry("Удалить группу · плитки останутся", "\uE74D", () => _store.DeleteGroup(group)));
        menu.Closed += (_, _) => { if (!_editingGroup) Search.Focus(); };
        OpenMenu(menu);
    }

    void Group_Click(object sender, RoutedEventArgs e)
    {
        CreateGroup();
    }

    void Pinned_EmptyRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!PinnedTab) return;
        var source = e.OriginalSource as DependencyObject;
        if (source?.FindAncestor<ListBoxItem>() != null || source?.FindAncestor<ScrollBar>() != null) return;
        ShowEmptyMenu(PinnedList);
        e.Handled = true;
    }

    void ShowEmptyMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Entry("Создать группу", "\uE710", CreateGroup));
        menu.Closed += (_, _) => { if (!_editingGroup) Search.Focus(); };
        OpenMenu(menu);
    }

    void OpenMenu(ContextMenu menu)
    {
        _menuOpen = true;
        _outsideTimer.Stop();
        menu.Closed += (_, _) => { _menuOpen = false; UpdateOutsideTimer(); };
        menu.IsOpen = true;
    }

    void CreateGroup()
    {
        if (PromptGroupName("Новая группа", "", allowEmpty: true) is { } name) _store.AddGroup(name);
    }

    string? PromptGroupName(string title, string current, bool allowEmpty = false)
    {
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 320, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            Foreground = (Brush)FindResource("TextPrimary"), FontFamily = (FontFamily)FindResource("TextFont")
        };
        var grid = new Grid { Margin = new Thickness(10) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var box = new TextBox { Text = current, Tag = allowEmpty ? "Название (можно пустым)" : "Название группы", MaxLength = 40, Height = 32, Margin = new Thickness(0, 10, 0, 0), Style = (Style)FindResource("SearchBox") };
        AutomationProperties.SetName(box, "Название группы");
        var error = new TextBlock { Text = allowEmpty ? $"Пустое поле → {_store.NextGroupName()}" : "", Foreground = (Brush)FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 5, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Отмена", Style = (Style)FindResource("SubtleTextButton"), Height = 30 };
        var save = new Button { Content = "Сохранить", Style = (Style)FindResource("SubtleTextButton"), Height = 30, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        save.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (!allowEmpty && name.Length == 0 || _store.Groups.Any(g => g.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) && g.Name != current))
            {
                error.Text = name.Length == 0 ? "Введите название группы" : "Такая группа уже есть";
                box.Focus();
                return;
            }
            dialog.DialogResult = true;
        };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        var label = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold };
        Grid.SetRow(box, 1); Grid.SetRow(error, 2); Grid.SetRow(actions, 3);
        grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(error); grid.Children.Add(actions);
        dialog.Content = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12),
            Background = (Brush)FindResource("WindowSurface"), BorderBrush = (Brush)FindResource("WindowStroke"),
            BorderThickness = new Thickness(1), Child = grid
        };
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
            else if (e.Key == Key.Escape) { dialog.DialogResult = false; e.Handled = true; }
        };
        _editingGroup = true;
        try { dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); }; return dialog.ShowDialog() == true ? box.Text.Trim() : null; }
        finally { _editingGroup = false; Search.Focus(); }
    }

    void Pinned_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _dragItem = null;
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<Button>() != null) return;
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext is ClipItem item)
        {
            _dragStart = e.GetPosition(PinnedList);
            _dragItem = item;
        }
    }

    void Pinned_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || _dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(PinnedList);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = true;
        CancelZoom();
        StopGif();
        try { DragDrop.DoDragDrop(PinnedList, new DataObject(typeof(ClipItem), _dragItem), DragDropEffects.Move); }
        finally { _dragStart = null; _dragItem = null; ClearDropTarget(); _dragging = false; }
    }

    void ClearDropTarget()
    {
        if (_dropTarget != null) _dropTarget.Tag = null;
        _dropTarget = null;
    }

    void Pinned_DragOver(object sender, DragEventArgs e)
    {
        ClearDropTarget();
        if (e.Data.GetData(typeof(ClipItem)) is not ClipItem dragged || !dragged.Pinned || !_store.Items.Contains(dragged))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var target = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        if (target?.DataContext is ClipItem item && item != dragged)
        {
            target.Tag = e.GetPosition(target).X < target.ActualWidth / 2 ? "Before" : "After";
            _dropTarget = target;
        }
        else if (target?.DataContext is PinnedGroup)
        {
            target.Tag = "Group";
            _dropTarget = target;
        }
        e.Effects = _dropTarget != null ? DragDropEffects.Move : DragDropEffects.None;
        var scroll = PinnedList.FindDescendant<ScrollViewer>();
        if (scroll != null)
        {
            double y = e.GetPosition(PinnedList).Y;
            if (y < 24) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 12);
            else if (y > PinnedList.ActualHeight - 24) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 12);
        }
        e.Handled = true;
    }

    void Pinned_DragLeave(object sender, DragEventArgs e)
    {
        if (!PinnedList.IsMouseOver) ClearDropTarget();
    }

    void Pinned_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ClipItem)) is ClipItem dragged && _dropTarget is { } target)
        {
            if (target.DataContext is ClipItem item)
                _store.MovePinned(dragged, item.PinnedGroupId, item, (string?)target.Tag == "After");
            else if (target.DataContext is PinnedGroup group)
                _store.MovePinned(dragged, group.Id == Guid.Empty ? null : group.Id);
        }
        ClearDropTarget();
        e.Handled = true;
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearHistory();
        Search.Focus();
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Visibility = SettingsFlyout.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SettingsFlyout.Visibility == Visibility.Visible) ShowTiming.Focus();
        else Search.Focus();
    }

    void Timing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ShowTimingValue == null || HideTimingValue == null) return; // ValueChanged срабатывает во время загрузки XAML
        ShowTimingValue.Text = $"{ShowDurationMs} мс";
        HideTimingValue.Text = $"{HideDurationMs} мс";
        TimingsChanged?.Invoke(ShowDurationMs, HideDurationMs);
    }

    void OutsideHideToggle_Changed(object sender, RoutedEventArgs e)
    {
        UpdateOutsideTimer();
        AutoHideOutsideChanged?.Invoke(((ToggleButton)sender).IsChecked == true);
    }

    void OutsideHideDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = (int)((Slider)sender).Value;
        if (OutsideHideValue == null) return;
        OutsideHideValue.Text = $"{ms / 1000.0:0.#} с";
        OutsideHideDelayChanged?.Invoke(ms);
    }

    void ZoomDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = (int)((Slider)sender).Value;
        SetValue(ImageZoomDelayMsProperty, ms);
        if (ZoomDelayValue == null) return; // ValueChanged при загрузке XAML
        ZoomDelayValue.Text = $"{ms / 1000.0:0.#} с";
        ImageZoomDelayChanged?.Invoke(ms);
    }

    void ZoomScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int scale = (int)((Slider)sender).Value;
        if (ZoomScaleValue == null) return; // ValueChanged при загрузке XAML
        ZoomScaleValue.Text = $"{260 * scale} × {120 * scale}";
        ImageZoomScaleChanged?.Invoke(scale);
    }

    void StartZoom(Image target)
    {
        CancelZoom();
        if (target.ToolTip is not ToolTip tip || target.Parent is not Grid grid ||
            grid.Children.OfType<Border>().FirstOrDefault(b => b.Name is "ZoomProgress" or "GraphZoomProgress") is not Border progress) return;

        _zoomTarget = target;
        _zoomTip = tip;
        _zoomProgress = progress;
        progress.Visibility = Visibility.Visible;
        var bar = new ScaleTransform(0, 1);
        progress.RenderTransform = bar;
        bar.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ImageZoomDelayMs)));
        _zoomTimer.Interval = TimeSpan.FromMilliseconds(ImageZoomDelayMs);
        _zoomTimer.Start();
    }

    void OpenZoom()
    {
        _zoomTimer.Stop();
        if (_zoomTarget is not { IsMouseOver: true } target || _zoomTip is not { Content: Image preview } tip)
        {
            CancelZoom();
            return;
        }

        HideZoomProgress();
        int scale = ImageZoomScale;
        preview.Width = 260 * scale;
        preview.Height = 120 * scale;
        var transform = new ScaleTransform(1.0 / scale, 1.0 / scale);
        preview.RenderTransform = transform;
        tip.PlacementTarget = target;
        tip.IsOpen = true;
        var grow = new DoubleAnimation(1.0 / scale, 1, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
    }

    void ShrinkZoom(Image target)
    {
        if (!ReferenceEquals(_zoomTarget, target)) return;
        _zoomTarget = null;
        _zoomTimer.Stop();
        HideZoomProgress();
        if (_zoomTip is not { IsOpen: true, Content: Image preview } tip ||
            preview.RenderTransform is not ScaleTransform transform)
        {
            _zoomTip = null;
            return;
        }

        var shrink = new DoubleAnimation { To = 1.0 / ImageZoomScale,
            Duration = TimeSpan.FromMilliseconds(180), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        shrink.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_zoomTip, tip) || _zoomTarget != null) return;
            tip.IsOpen = false;
            _zoomTip = null;
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, shrink.Clone());
    }

    void HideZoomProgress()
    {
        if (_zoomProgress?.RenderTransform is ScaleTransform bar)
        {
            bar.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            bar.ScaleX = 0;
            _zoomProgress.Visibility = Visibility.Collapsed;
        }
        _zoomProgress = null;
    }

    void CancelZoom()
    {
        _zoomTimer.Stop();
        HideZoomProgress();
        if (_zoomTip != null) _zoomTip.IsOpen = false;
        _zoomTarget = null;
        _zoomTip = null;
    }

    async void Image_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Image image) StartZoom(image);
        if (sender is not Image target || target.DataContext is not ClipItem { IsGif: true, ImageDir: not null, ImageHash: not null } item)
            return;

        StopGif();
        var playback = new CancellationTokenSource();
        _gifPlayback = playback;
        _gifTarget = target;
        try
        {
            await Task.Run(async () =>
            {
                var bytes = ImageFiles.ReadFull(item.ImageDir, item.ImageHash);
                using var stream = new MemoryStream(bytes);
                using var gif = System.Drawing.Image.FromStream(stream);
                int count = gif.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time);
                if (count < 2) return;
                byte[]? delays = null;
                try { delays = gif.GetPropertyItem(0x5100)?.Value; } catch (ArgumentException) { }
                double scale = Math.Min(1, Math.Min(320.0 / gif.Width, 160.0 / gif.Height));
                var size = new System.Drawing.Size(Math.Max(1, (int)(gif.Width * scale)), Math.Max(1, (int)(gif.Height * scale)));

                while (!playback.IsCancellationRequested)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        playback.Token.ThrowIfCancellationRequested();
                        gif.SelectActiveFrame(System.Drawing.Imaging.FrameDimension.Time, frame);
                        using var bitmap = new System.Drawing.Bitmap(gif, size);
                        using var png = new MemoryStream();
                        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                        var source = ImageFiles.Decode(png.ToArray());
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (!playback.IsCancellationRequested && ReferenceEquals(target.DataContext, item) && target.IsMouseOver)
                                target.SetCurrentValue(Image.SourceProperty, source);
                            else if (!playback.IsCancellationRequested)
                                StopGif();
                        });
                        int delay = delays != null && (long)frame * 4 + 4 <= delays.Length
                            ? BitConverter.ToInt32(delays, frame * 4) * 10 : 100;
                        await Task.Delay(Math.Max(20, delay), playback.Token);
                    }
                }
            }, playback.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Trace($"GIF preview: {ex.Message}"); }
        finally
        {
            if (ReferenceEquals(_gifPlayback, playback)) StopGif();
            playback.Dispose();
        }
    }

    void Image_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Image image) ShrinkZoom(image);
        if (ReferenceEquals(_gifTarget, sender)) StopGif();
    }

    void Image_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_zoomTarget, sender)) CancelZoom();
        if (ReferenceEquals(_gifTarget, sender)) StopGif();
    }

    void StopGif()
    {
        var playback = _gifPlayback;
        var target = _gifTarget;
        _gifPlayback = null;
        _gifTarget = null;
        playback?.Cancel();
        if (target != null)
            BindingOperations.GetBindingExpression(target, Image.SourceProperty)?.UpdateTarget();
    }

    public void ClearHistory()
    {
        if (!_store.Items.Any(i => !i.Pinned)) return;
        const string prompt = "Удалить все незакреплённые записи из истории? Закреплённые останутся.";
        const string title = "Очистить историю";
        _confirmingClear = true;
        try
        {
            var answer = IsVisible
                ? MessageBox.Show(this, prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) _store.ClearUnpinned();
        }
        finally { _confirmingClear = false; }
    }
}

static class VisualTreeExtensions
{
    public static T? FindDescendant<T>(this DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (child.FindDescendant<T>() is { } nested) return nested;
        }
        return null;
    }

    public static T? FindAncestor<T>(this DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
