using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

/// <summary>Вид InfoBar: цвет подложки и значок.</summary>
enum InfoKind { Neutral, Info, Caution, Success, Critical }

partial class PanelWindow : Window
{
    const int GridColumns = 3;
    const double ShadowMargin = 16; // поле под тень вокруг панели (Root.Margin в XAML)
    const double EnterScale = 0.96, EnterShift = 6; // появление: небольшой рост и сдвиг от каретки, без «прыжка» с 80%
    bool _hiding;
    CancellationTokenSource? _gifPlayback;
    Image? _gifTarget;
    readonly DispatcherTimer _zoomTimer = new();
    readonly DispatcherTimer _peekTimer = new(); // задержка перед просмотром текста
    public static readonly DependencyProperty SensitiveRevealedProperty = DependencyProperty.RegisterAttached(
        "SensitiveRevealed", typeof(bool), typeof(PanelWindow), new PropertyMetadata(false));
    public static bool GetSensitiveRevealed(DependencyObject element) => (bool)element.GetValue(SensitiveRevealedProperty);
    public static void SetSensitiveRevealed(DependencyObject element, bool value) => element.SetValue(SensitiveRevealedProperty, value);
    long _peekStartedAt;
    internal static readonly TimeSpan SensitivePeekDelay = TimeSpan.FromSeconds(2);
    internal static bool SensitivePeekReady(long startedAt) => startedAt != 0
        && System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >= SensitivePeekDelay;
    ListBoxItem? _peekHover;
    ClipItem? _peekItem;
    readonly DispatcherTimer _outsideTimer = new();   // мышь ушла с панели → скрыть через заданную задержку
    readonly DispatcherTimer _deactivateTimer = new() { Interval = TimeSpan.FromMilliseconds(150) }; // клик вне панели
    bool _menuOpen;
    TextEditorWindow? _textEditor;
    Image? _zoomTarget;
    ToolTip? _zoomTip;
    Border? _zoomProgress;
    Task<System.Windows.Media.Imaging.BitmapSource?>? _zoomFull; // полная картинка для увеличения, грузится во время задержки
    bool _pinnedDirty;       // закреплённые пересобираем при показе, а не на каждое копирование в скрытом окне
    DateTime _labelsDay;     // день, на который посчитаны подписи времени
    WinEventProc? _onForeground; // держим ссылку: хук вызывает делегат, GC не должен его собрать
    readonly List<ListBoxItem> _shortcutContainers = [];
    readonly HashSet<ClipItem> _leaving = [];  // уезжают с анимацией, ещё в списке
    bool _clearing;
    int _tabIndex;

    /// <summary>Сообщение в InfoBar: заголовок жирным, текст, до двух кнопок.</summary>
    sealed record Info(InfoKind Kind, string Title, string Message,
        (string Text, Action? Act)? Primary = null, (string Text, Action? Act)? Secondary = null, bool Closable = true, bool Compact = false);
    Info? _transient; // подтверждение, итог операции, ошибка — до закрытия или скрытия панели
    Info? _archiveInfo;
    Info? _ocrInfo;
    Info? _shown;
    readonly DispatcherTimer _infoTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _paused;

    /// <summary>
    /// Движение в панели (появление, вкладки, записи, меню). Своя настройка, а не системные «Эффекты анимации»:
    /// у многих они выключены ради скорости, а анимацию панели просили отдельно.
    /// </summary>
    public bool Animations
    {
        get => Motion.Enabled;
        set { Motion.Enabled = value; AnimationsToggle.IsChecked = value; RefreshVfx(); }
    }
    public bool ShowKeyHints { get => KeyHintsToggle.IsChecked == true; set => KeyHintsToggle.IsChecked = value; }
    bool _pinnedAsList;
    /// <summary>Закреплённые списком вместо плиток: переключатель в шапке, запоминается.</summary>
    public bool PinnedAsList { get => _pinnedAsList; set { _pinnedAsList = value; ApplyPinnedView(); } }
    public event Action<bool>? PinnedViewChanged;
    /// <summary>Видна и не прячется: сообщение можно показать в InfoBar, а не балуном трея.</summary>
    public bool IsShown => IsVisible && !_hiding;
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
    public bool TextPeekEnabled { get => TextPeekToggle.IsChecked == true; set => TextPeekToggle.IsChecked = value; }
    public int TextPeekDelayMs { get => (int)TextPeekDelaySlider.Value; set => TextPeekDelaySlider.Value = Math.Clamp(value, 100, 3000); }
    public int TextPeekWidth { get => (int)TextPeekWidthSlider.Value; set => TextPeekWidthSlider.Value = Math.Clamp(value, 300, 640); }
    /// <summary>Просмотр текста: (включён, задержка мс, ширина px) — App сохраняет в реестр.</summary>
    public event Action<bool, int, int>? TextPeekChanged;
    public event Action<bool>? AnimationsChanged;
    public event Action<bool>? KeyHintsChanged;
    StorageSettings _storage = new();
    bool _loadingStorage;
    public event Action<StorageSettings>? StorageChanged;
    public StorageSettings Storage
    {
        get => _storage;
        set
        {
            _loadingStorage = true;
            _storage = value;
            HistoryCountSlider.Value = value.HistoryCount;
            HistoryDaysSlider.Value = value.HistoryDays;
            MediaDaysSlider.Value = value.MediaDays;
            TextLimitSlider.Value = value.TextThousands;
            ImageLimitSlider.Value = value.ImageMegapixels;
            ThumbnailSlider.Value = value.ThumbnailPixels;
            FileSizeSlider.Value = value.FileMegabytes;
            FileCountSlider.Value = value.FileCount;
            TempDaysSlider.Value = value.TempDays;
            _loadingStorage = false;
            UpdateStorageLabels();
        }
    }
    /// <summary>Пауза записи из меню «⋯» или InfoBar; App переключает её везде (трей, значок, панель).</summary>
    public event Action<bool>? PauseToggleRequested;

    readonly HistoryStore _store;
    string? _sourceFilter;
    readonly ICollectionView _view;       // вкладка «Буфер обмена»: всё
    readonly ObservableCollection<object> _pinned = new();
    readonly PinnedGroup _ungrouped = new() { Id = Guid.Empty, Name = UiText.T("Без группы") };
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
    /// <summary>«Сжать промпт»: текст записи переписать короче локальной моделью.</summary>
    public event Action<ClipItem>? CompressRequested;
    internal event Action<ClipItem>? OcrRequested;
    internal event Action<ClipItem>? TranslateImageRequested;
    public event Action<ClipItem>? ArchiveRequested;
    internal event Action<ClipItem, AudioExtractor.Track>? ExtractAudioRequested;

    bool PinnedTab => TabPinned.IsChecked == true;
    bool SymbolsTab => TabSymbols.IsChecked == true;
    bool GridTab => PinnedTab && !_pinnedAsList || SymbolsTab;
    ListBox Active => SymbolsTab ? SymbolList : PinnedTab ? PinnedList : List;
    RadioButton[] Tabs => [TabAll, TabPinned, TabSymbols];

    /// <summary>Столбцов в текущей сетке: у плиток — 3, у символов — сколько клеток влезло по ширине.</summary>
    int Columns => SymbolsTab ? Math.Max(1, (int)(SymbolList.ActualWidth / 40)) : GridColumns;

    public PanelWindow(HistoryStore store, SymbolCatalog symbols)
    {
        InitializeComponent();
        InitializeLanguage();
        InitializeDragVisual();
        _infoTimer.Tick += (_, _) =>
        {
            _infoTimer.Stop();
            if (_shown?.Compact == true && ReferenceEquals(_shown, _transient)) CloseTransient();
        };
        Closed += (_, _) => _infoTimer.Stop();
        _zoomTimer.Tick += (_, _) => OpenZoom();
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); OpenPeek(); };
        _outsideTimer.Tick += (_, _) =>
        {
            _outsideTimer.Stop();
            if (!IsVisible || _hiding || !AutoHideOutside || PointerInsidePanel()) return;
            if (Busy) { _outsideTimer.Start(); return; }
            HideAnimated();
        };
        Root.MouseEnter += (_, _) => _outsideTimer.Stop();
        // Настройки закрываются кликом по панели мимо них, как всплывающие окна Windows 11.
        // Клик только закрывает: не вставляет запись и не переключает вкладку, по которой случайно попал.
        Root.PreviewMouseDown += (_, e) =>
        {
            if (ValueDrop.IsOpen) CloseValueDrop();
            if (SettingsFlyout.Visibility != Visibility.Visible || (e.OriginalSource as DependencyObject).IsWithin(SettingsFlyout)) return;
            CloseSettings();
            e.Handled = true;
        };
        Root.MouseLeave += (_, _) => StartOutsideTimer();
        _deactivateTimer.Tick += (_, _) =>
        {
            _deactivateTimer.Stop();
            if (!IsVisible || _hiding || !AutoHideOutside || Busy || ForegroundIsOurs()) return;
            App.Trace("  auto-hide: focus left the panel");
            HideAnimated();
        };
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

        store.Changed += () =>
        {
            SyncImageSeries();
            SyncMergeSelection();
            if (!IsVisible) { _pinnedDirty = true; return; }
            if (_mergeTextFilter) return;
            if (_textDragFilter || _mergeTextFilter || _sourceFilter != null || _kindFilter != null || Search.Text.Length > 0) _view.Refresh();
            UpdatePinnedLayout();
            UpdateEmptyState();
        };

        foreach (var list in new[] { List, PinnedList, SymbolList })
        {
            list.AddHandler(ListBoxItem.MouseLeftButtonUpEvent, new MouseButtonEventHandler(Item_Click), handledEventsToo: false);
            list.AddHandler(ListBoxItem.MouseRightButtonUpEvent, new MouseButtonEventHandler(Item_RightClick), handledEventsToo: false);
        }
        PinnedList.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(Pinned_EmptyRightClick), handledEventsToo: true);
        store.Items.CollectionChanged += OnItemsChanged;

        Search.ApplyTemplate();
        if (Search.Template.FindName("PART_Clear", Search) is Button clearSearch)
            clearSearch.Click += (_, _) => { Search.Text = ""; Search.Focus(); };
        foreach (var toggle in new[] { AnimationsToggle, OutsideHideToggle, KeyHintsToggle, TextPeekToggle, AutoUpdateToggle })
        {
            toggle.Checked += AnimateSwitch;
            toggle.Unchecked += AnimateSwitch;
        }
        TabsHost.SizeChanged += (_, _) => MoveTabIndicator(false);

        InitializeMergeSelection();
        SeriesQueue.ItemsSource = _seriesQueue;
        InitializeVfx();
        PreviewKeyUp += (_, e) => { if (e.Key is Key.LeftCtrl or Key.RightCtrl) { HideShortcuts(); RefreshMergeMarks(); } };
        PreviewKeyDown += OnKey;
        ValueDropCard.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseValueDrop(); Search.Focus(); e.Handled = true; }
        };
        _externalValueWatch.Tick += (_, _) =>
        {
            if (!Native.IsDown(1) || Native.IsDown(0x1B)) CloseValueDrop();
        };
        PreviewMouseLeftButtonDown += (_, _) => { _seriesDragItem = null; _imageSelectionClick = false; };
        PreviewMouseLeftButtonUp += (_, e) => { _dragStart = null; _dragItem = null; _seriesDragItem = null; FinishImageSelectionClick(e); };
        // Клик вне панели = фокус ушёл к чужому окну. Прячем почти сразу; пауза отсекает мгновенный возврат фокуса
        // и даёт закрыться контекстному меню, которое гасится тем же кликом. Главный сигнал — EVENT_SYSTEM_FOREGROUND
        // (InitNative): при первом показе WPF мог не прислать Deactivated и держать IsActive = true.
        Deactivated += (_, _) => { HideShortcuts(); ClosePeek(); OnFocusMaybeLost(); };
        SourceInitialized += (_, _) => InitNative();
    }

    // Картинки при текстовом поиске скрываем: искать в них нечего. Фильтр приложения работает для всех типов.
    // Ordinal: тексты до миллиона символов, культурное сравнение на каждую букву поиска заметно тормозит.
    bool Matches(ClipItem i) => (!_textDragFilter || !i.IsImage && !i.IsMedia)
        && (!SeriesActive || i.IsVisualMedia)
        && (!_mergeTextFilter || MatchesKind(i, "text"))
        && MatchesKind(i, _kindFilter)
        && (_sourceFilter == null || string.Equals(i.SourceApp, _sourceFilter, StringComparison.OrdinalIgnoreCase))
        && (Search.Text.Length == 0
            || !i.IsImage && ((i.IsMedia ? i.FileName ?? "" : i.Text).Contains(Search.Text, StringComparison.OrdinalIgnoreCase)
                || i.IsFolder && i.Text.Contains(Search.Text, StringComparison.OrdinalIgnoreCase)
                || i.Kind == "url" && (i.TileTitle.Contains(Search.Text, StringComparison.OrdinalIgnoreCase)
                    || i.TileSub.Contains(Search.Text, StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// Раскладка закреплённых: заголовок группы со счётчиком, под ним её записи (у свёрнутой — ничего).
    /// При поиске группы раскрыты, пустые скрыты.
    /// </summary>
    void UpdatePinnedLayout()
    {
        _pinnedDirty = false;
        var selected = PinnedList.SelectedItem as ClipItem;
        bool searching = Search.Text.Length > 0 || _sourceFilter != null || _kindFilter != null || SeriesActive || _mergeTextFilter;
        _pinned.Clear();
        void AddGroup(PinnedGroup group, List<ClipItem> items)
        {
            group.Count = items.Count;
            group.IsEmptyGroup = items.Count == 0 && !searching && group.Id != Guid.Empty;
            _pinned.Add(group);
            if (searching || !group.Collapsed) foreach (var item in items) _pinned.Add(item);
        }
        var loose = _store.PinnedIn(null).Where(Matches).ToList();
        if (_store.Groups.Count > 0 && !searching) AddGroup(_ungrouped, loose);
        else foreach (var item in loose) _pinned.Add(item);
        foreach (var group in _store.Groups)
        {
            var items = _store.PinnedIn(group.Id).Where(Matches).ToList();
            if (searching && items.Count == 0) continue;
            AddGroup(group, items);
        }
        if (selected != null && _pinned.Contains(selected)) PinnedList.SelectedItem = selected;
    }

    /// <summary>Плитки или список: меняются контейнер, шаблон и панель раскладки у того же ListBox.</summary>
    void ApplyPinnedView()
    {
        PinnedList.ItemContainerStyle = (Style)FindResource(_pinnedAsList ? "PinRowContainer" : "PinTileContainer");
        PinnedList.ItemTemplate = (DataTemplate)FindResource(_pinnedAsList ? "PinRowTemplate" : "PinTileTemplate");
        PinnedList.ItemsPanel = (ItemsPanelTemplate)FindResource(_pinnedAsList ? "PinRowPanel" : "PinTilePanel");
        PinnedList.Margin = _pinnedAsList ? new Thickness(0, -3, 0, 0) : new Thickness(-3, -3, 0, 0);
        ViewButton.Content = _pinnedAsList ? "\uF0E2" : "\uE8FD";
        string hint = _pinnedAsList ? UiText.T("Показать плитками") : UiText.T("Показать списком");
        ViewButton.ToolTip = hint;
        AutomationProperties.SetName(ViewButton, hint + UiText.T(" закреплённые"));
    }

    void View_Click(object sender, RoutedEventArgs e)
    {
        HideShortcuts();
        PinnedAsList = !_pinnedAsList;
        PinnedViewChanged?.Invoke(_pinnedAsList);
        Motion.Enter(PinnedList, 0, 8);
        Search.Focus();
    }

    static PinnedGroup? GroupOf(object sender) => (sender as FrameworkElement)?.DataContext as PinnedGroup;

    void GroupChevron_Click(object sender, RoutedEventArgs e)
    {
        if (GroupOf(sender) is not { } group) return;
        if (group.Id == Guid.Empty) { group.Collapsed = !group.Collapsed; UpdatePinnedLayout(); UpdateEmptyState(); }
        else _store.SetGroupCollapsed(group, !group.Collapsed); // сохраняется вместе с историей
        Search.Focus();
    }

    void GroupMore_Click(object sender, RoutedEventArgs e)
    {
        if (GroupOf(sender) is not { } group || sender is not UIElement button) return;
        if (group.Id == Guid.Empty) ShowEmptyMenu(button);
        else ShowGroupMenu(group, button);
    }

    void InitNative()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _onForeground = (_, _, _, _, _, _, _) => OnFocusMaybeLost();
        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0, WINEVENT_OUTOFCONTEXT);
        // Инструментальное окно: нет в Alt+Tab и на панели задач.
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
    }

    /// <summary>Сдвиг при появлении — со стороны каретки: панель под ней выезжает вниз, над ней — вверх.</summary>
    double ShiftFrom => Root.RenderTransformOrigin.Y > 0.5 ? EnterShift : -EnterShift;

    /// <summary>Появление: размер 96% → 100%, сдвиг 6 px к месту, видимость 0 → 1; кривая входа (резкий старт, мягкая остановка).</summary>
    void AnimateIn()
    {
        _hiding = false;
        Animate(EnterScale, 1, ShiftFrom, 0, 0, 1, ShowDurationMs, Motion.In, null);
    }

    /// <summary>
    /// После долгого простоя первый кадр рисуется медленно: память процесса выгружена, кэши глифов и картинок остыли.
    /// Часы анимации идут с BeginAnimation, и 100 мс съедал этот кадр — панель появлялась рывком, мерцая.
    /// Поэтому первый кадр рисуем почти прозрачным, а анимацию запускаем со второго, когда отрисовка уже идёт.
    /// </summary>
    void AnimateInAfterFirstFrame()
    {
        if (!Animations) { AnimateIn(); return; }
        AfterFrames(2, () => { if (IsVisible && !_hiding) AnimateIn(); });
    }

    /// <summary>Через n кадров отрисовки: действие ждёт, пока предыдущее состояние действительно окажется на экране.</summary>
    static void AfterFrames(int n, Action action)
    {
        int frames = 0;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            if (++frames < n) return;
            CompositionTarget.Rendering -= tick;
            action();
        };
        CompositionTarget.Rendering += tick;
    }

    /// <summary>Исчезновение: размер → 96%, видимость → 0 с разгоном; затем Hide.</summary>
    public void HideAnimated()
    {
        if (!IsVisible || _hiding) return;
        FinishMergeSelection(open: false);
        FinishDragVisual();
        CloseValueDrop();
        _outsideTimer.Stop();
        _deactivateTimer.Stop();
        HideShortcuts();
        CancelZoom();
        StopGif();
        SettingsFlyout.Visibility = Visibility.Collapsed;
        _hiding = true;
        Animate(null, EnterScale, null, 0, null, 0, HideDurationMs, Motion.Out, () =>
        {
            if (!_hiding) return; // пока исчезала, снова вызвали Win+V — не прячем
            // Прозрачное окно (layered) при следующем Show на кадр показывает то, что было на экране перед Hide.
            // Completed приходит раньше, чем отрисован последний кадр, — прятали полупрозрачную панель, и она мелькала
            // при следующем появлении. Сначала дорисовываем полностью прозрачный кадр, потом Hide.
            Root.BeginAnimation(OpacityProperty, null);
            Root.Opacity = 0;
            AfterFrames(2, () =>
            {
                if (!_hiding) return;
                _hiding = false;
                Hide();
            });
        });
    }

    /// <summary>
    /// Scale, сдвиг и Opacity всего Root. From = null — от текущего значения (исчезновение посреди появления не дёргается).
    /// Анимация выключена — сразу конечное состояние.
    /// </summary>
    void Animate(double? scaleFrom, double scaleTo, double? shiftFrom, double shiftTo, double? opacityFrom, double opacityTo,
        int ms, IEasingFunction ease, Action? done)
    {
        if (!Animations || ms <= 0)
        {
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RootShift.BeginAnimation(TranslateTransform.YProperty, null);
            Root.BeginAnimation(OpacityProperty, null);
            RootScale.ScaleX = RootScale.ScaleY = Root.Opacity = 1;
            RootShift.Y = 0;
            done?.Invoke();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(ms);
        DoubleAnimation Anim(double? from, double to) => new(to, duration) { From = from, EasingFunction = ease };
        var fade = Anim(opacityFrom, opacityTo);
        if (done != null) fade.Completed += (_, _) => done();
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(scaleFrom, scaleTo));
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(scaleFrom, scaleTo));
        RootShift.BeginAnimation(TranslateTransform.YProperty, Anim(shiftFrom, shiftTo));
        Root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Показать у каретки (rect в физических пикселях экрана). Вкладка — та, что была открыта в прошлый раз.</summary>
    public void ShowAt(RECT anchor)
    {
        _outsideTimer.Stop();
        _deactivateTimer.Stop();
        HideShortcuts();
        SettingsFlyout.Visibility = Visibility.Collapsed;
        _transient = null; // вчерашний итог или неподтверждённая очистка не должны встречать при следующем открытии
        RefreshInfo();
        if (_labelsDay != DateTime.Today)
        {
            _labelsDay = DateTime.Today;
            foreach (var item in _store.Items) item.RefreshTimeLabel();
        }
        if (_pinnedDirty) UpdatePinnedLayout();
        // Списки живые (ObservableCollection), пересобирать их при каждом показе не нужно —
        // только если остался текст поиска (TextChanged сам вызовет RefreshViews).
        if (SeriesActive) { SyncImageSeries(); RestoreSeriesSelection(advance: true); }
        else
        {
            if (_sourceFilter != null) SetSourceFilter(null);
            if (Search.Text.Length > 0) Search.Text = "";
            else ResetSelection();
        }

        new WindowInteropHelper(this).EnsureHandle();
        Place(anchor);
        // Ставим стартовое состояние напрямую до Show: нулевая анимация может примениться
        // лишь на следующем кадре и показать старое содержимое после долгого простоя.
        _hiding = false;
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootShift.BeginAnimation(TranslateTransform.YProperty, null);
        Root.BeginAnimation(OpacityProperty, null);
        RootScale.ScaleX = RootScale.ScaleY = Animations ? EnterScale : 1;
        RootShift.Y = Animations ? ShiftFrom : 0;
        // 1/255, а не 0: полностью прозрачное WPF может не рисовать, а первый кадр как раз должен всё прогреть.
        Root.Opacity = Animations ? 0.004 : 1;
        Show();
        Place(anchor); // WPF при первом показе может переставить окно — фиксируем ещё раз
        if (!_hiding) AnimateInAfterFirstFrame();
        BringToFront();
        Search.Focus();
        bool ours = ForegroundIsOurs();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Active.UpdateLayout(); ShowShortcuts(); }
        App.Trace($"  shown: foreground={(ours ? "panel" : "other")} wpfActive={IsActive}");
        // Курсор уже вне панели — отсчёт сразу, а не с первого ухода мыши: иначе, не наведя мышь, панель не пряталась никогда.
        // С клавиатурой это не мешает: любое нажатие останавливает отсчёт (OnKey).
        StartOutsideTimer();
    }

    /// <summary>Своё модальное окно, меню или перетаскивание: уход фокуса/мыши тут не значит «ушли из панели».</summary>
    bool Busy => _editingGroup || _textEditor != null || _mergeWindow != null || _collectingMerge || _dragging || _mergingDrag || _valueSaving || ValueDrop.IsOpen || _menuOpen || _archiveInfo != null;

    /// <summary>
    /// Передний план сейчас у нашего окна (панель, её диалог, окно графа). Спрашиваем Windows, а не IsActive:
    /// WPF после первого показа мог держать IsActive = true, хотя фокус давно у другого приложения.
    /// </summary>
    public static bool ForegroundIsOurs()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == Environment.ProcessId;
    }

    void OnFocusMaybeLost()
    {
        if (!IsVisible || _hiding || !AutoHideOutside) return;
        _deactivateTimer.Stop();
        _deactivateTimer.Start();
    }

    /// <summary>
    /// Настоящий курсор (GetCursorPos): Mouse.GetPosition вне окна отдаёт последнюю точку, которую окно видело,
    /// часто ещё внутри панели — из-за этого скрытие то срабатывало, то нет.
    /// </summary>
    bool PointerInsidePanel()
    {
        if (!IsVisible || !GetCursorPos(out var p)) return false;
        var a = Root.PointToScreen(new Point(0, 0));
        var b = Root.PointToScreen(new Point(Root.ActualWidth, Root.ActualHeight));
        return p.X >= Math.Min(a.X, b.X) && p.X < Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y < Math.Max(a.Y, b.Y);
    }

    /// <summary>
    /// Мышь вне панели (при показе или после ухода): скрыть через заданную задержку, если не вернётся
    /// и если с панелью не начали работать с клавиатуры — нажатие клавиши отсчёт останавливает.
    /// </summary>
    void StartOutsideTimer()
    {
        _outsideTimer.Stop();
        if (!IsVisible || _hiding || !AutoHideOutside || _menuOpen || PointerInsidePanel()) return;
        _outsideTimer.Interval = TimeSpan.FromMilliseconds(OutsideHideDelayMs);
        _outsideTimer.Start();
    }

    void RefreshViews()
    {
        _view.Refresh();
        _seriesView?.Refresh();
        _mergeView?.Refresh();
        UpdatePinnedLayout();
        _symbolView.Refresh();
        ResetSelection();
    }

    void ResetSelection()
    {
        if (SeriesActive) { RestoreSeriesSelection(); UpdateEmptyState(); return; }
        if (_mergeTextFilter)
        {
            if (Active.SelectedIndex < 0) Active.SelectedItem = Active.Items.OfType<ClipItem>().FirstOrDefault();
            UpdateEmptyState(); return;
        }
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
        GroupButton.Visibility = PinnedTab ? Visibility.Visible : Visibility.Collapsed;
        ViewButton.Visibility = GroupButton.Visibility;
        SourceButton.Visibility = SymbolsTab ? Visibility.Collapsed : Visibility.Visible;
        KindButton.Visibility = SourceButton.Visibility;
        HideShortcuts();
        if (Active.SelectedIndex < 0 && Active.Items.Count > 0) Active.SelectedIndex = 0;
        // Полоска едет к новой вкладке, содержимое въезжает с той стороны, куда переключились.
        int index = Array.IndexOf(Tabs, sender);
        MoveTabIndicator(IsShown);
        if (IsShown && index >= 0 && index != _tabIndex) Motion.Enter(Active, index > _tabIndex ? 12 : -12, 0);
        if (index >= 0) _tabIndex = index;
        UpdateEmptyState();
        Search.Focus();
    }

    void MoveTabIndicator(bool animate)
    {
        var tab = Tabs.FirstOrDefault(t => t.IsChecked == true);
        if (tab == null || tab.ActualWidth == 0) return;
        double x = tab.TranslatePoint(new Point(tab.ActualWidth / 2 - TabIndicator.Width / 2, 0), TabsHost).X;
        var shift = Motion.Shift(TabIndicator);
        if (animate) Motion.To(shift, TranslateTransform.XProperty, x, Motion.Slow);
        else { shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = x; }
    }

    void UpdateEmptyState()
    {
        if (PinnedList == null) return;
        bool empty = SymbolsTab ? _symbolView.IsEmpty : PinnedTab ? _pinned.Count == 0 : (_seriesView ?? _mergeView ?? _view).IsEmpty;
        bool appeared = empty && Empty.Visibility != Visibility.Visible;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Active.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) return;
        if (appeared && IsShown) Motion.Enter(Empty, 0, 8, Motion.Slow);
        bool searching = Search.Text.Length > 0 || _sourceFilter != null || _kindFilter != null;
        (EmptyTitle.Text, EmptyHint.Text) = (searching, PinnedTab) switch
        {
            (true, _) when SymbolsTab => (UiText.T("Ничего не найдено"), UiText.T("Ищите по символу, коду (2014) или группе («валют»).")),
            (true, _) => (UiText.T("Ничего не найдено"), UiText.T("Измените поиск, приложение или вид записей.")),
            (false, true) => (UiText.T("Закреплённых пока нет"), UiText.T("Нажмите на булавку у записи или Ctrl+P, и она появится здесь.")),
            _ => (UiText.T("Здесь пока пусто"), UiText.T("Скопируйте текст или картинку, и они появятся в этом списке.")),
        };
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PinnedList == null) return;
        RefreshViews();
    }

    void Source_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SourceButton, Placement = PlacementMode.MousePoint };
        var all = Entry(UiText.T("Все приложения"), "\uE71D", () => SetSourceFilter(null));
        all.IsChecked = _sourceFilter == null;
        menu.Items.Add(all);
        var groups = _store.Items.Where(i => !string.IsNullOrWhiteSpace(i.SourceApp))
            .GroupBy(i => i.SourceApp!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (groups.Count == 0) menu.Items.Add(new MenuItem { Header = UiText.T("Новые записи покажут приложение"), IsEnabled = false });
        foreach (var group in groups)
        {
            string app = group.Key;
            var option = Entry($"{app}  ({group.Count()})", "\uE80F", () => SetSourceFilter(app));
            option.IsChecked = string.Equals(_sourceFilter, app, StringComparison.OrdinalIgnoreCase);
            menu.Items.Add(option);
        }
        menu.Closed += (_, _) => Search.Focus();
        OpenMenu(menu);
    }

    internal void SetSourceFilter(string? source)
    {
        _sourceFilter = source;
        SourceButton.Content = (source is { Length: > 8 } ? source[..7] + "…" : source ?? UiText.T("Все")) + " ▾";
        SourceButton.ToolTip = source == null ? UiText.T("Фильтр по приложению") : UiText.F($"Приложение: {source}");
        AutomationProperties.SetName(SourceButton, UiText.F($"Приложение: {source ?? UiText.T("все")}"));
        RefreshViews();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        // Отпускание Win после Win+V: хук досылает VK_MASK (гасит «Пуск»), а Win ещё может повторяться.
        // Это не работа с панелью — иначе отсчёт скрытия гас сразу после показа и панель не пряталась без мыши.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // 0xE8 не назначена, WPF отдаёт её как Key.None.
        if (key is Key.LWin or Key.RWin or Key.None || KeyInterop.VirtualKeyFromKey(key) == VK_MASK) return;
        _outsideTimer.Stop(); // с панелью работают с клавиатуры — мышь снаружи не повод прятать
        if (key is Key.LeftAlt or Key.RightAlt) { CancelZoom(); StopGif(); }
        // Ctrl badges are visible immediately, without a timer or stagger.
        if (key is Key.LeftCtrl or Key.RightCtrl)
        {
            CancelZoom(); StopGif(); ClosePeek();
            if (!e.IsRepeat && _shortcutContainers.Count == 0) ShowShortcuts();
            return;
        }
        if (HandleMergeKey(key, e)) return;
        if (e.Key == Key.Escape && ValueDrop.IsOpen) { CloseValueDrop(); e.Handled = true; return; }
        if (e.Key == Key.F6) { CycleFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; return; }
        if (SettingsFlyout.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; }
            return;
        }
        if (HandleSeriesKey(key, e)) return;
        // Фокус на кнопке (F6): Enter/пробел нажимают её, Esc возвращает в поиск.
        if (Keyboard.FocusedElement is ButtonBase)
        {
            if (e.Key is Key.Enter or Key.Space) return;
            if (e.Key == Key.Escape) { Search.Focus(); e.Handled = true; return; }
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
            case Key.Apps:
            case Key.F10 when shift:
                if (Active.SelectedItem is { } menuItem && Active.ItemContainerGenerator.ContainerFromItem(menuItem) is ListBoxItem container)
                {
                    if (selected != null) ShowMenu(selected, container, PlacementMode.Bottom);
                    else if (symbol != null) ShowSymbolMenu(symbol, container, PlacementMode.Bottom);
                    else if (container.DataContext is PinnedGroup group && group.Id != Guid.Empty) ShowGroupMenu(group, container);
                    else if (container.DataContext is PinnedGroup) ShowEmptyMenu(container);
                }
                break;
            case Key.V when ctrl && selected != null && Search.Text.Length == 0:
                PasteValue();
                break;
            case Key.Z when ctrl && _valueUndo != null && Search.Text.Length == 0:
                UndoValue();
                break;
            case Key.E when ctrl && selected is { IsImage: false, IsMedia: false }:
                EditText(selected);
                break;
            case Key.Escape:
                if (_transient != null) CloseTransient();
                else if (Search.Text.Length > 0) Search.Text = "";
                else if (_sourceFilter != null) SetSourceFilter(null);
                else if (_kindFilter != null) SetKindFilter(null);
                else HideAnimated();
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
                RequestPaste(selected, shift);
                break;
            case Key.Delete when selected != null && Search.Text.Length == 0:
                RemoveAnimated(selected);
                break;
            case Key.P when ctrl && selected != null:
                KeepingSelection(() => _store.TogglePin(selected));
                if (selected.Pinned) Motion.Pop(TabPinned, 1.12, 1);
                break;
            case >= Key.D1 and <= Key.D9 when ctrl:
                int idx = e.Key - Key.D1;
                var shortcuts = PinnedTab ? Active.Items.OfType<ClipItem>().Cast<object>().ToList() : Active.Items.Cast<object>().ToList();
                if (idx >= shortcuts.Count) break;
                if (shortcuts[idx] is SymbolItem s) TypeRequested?.Invoke(s.Char);
                else if (shortcuts[idx] is ClipItem clip) RequestPaste(clip, shift);
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
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ButtonBase>() != null) return;
        if (_altClick) { _altClick = false; e.Handled = true; return; }
        if (_collectingMerge) { e.Handled = true; return; }
        if (_dragging || _suppressClick) return;
        switch ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext)
        {
            case ClipItem item: RequestPaste(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); break;
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
        if (ItemOf(sender) is { } item)
        {
            _store.TogglePin(item);
            if (sender is UIElement button) Motion.Pop(button);
            if (item.Pinned) Motion.Pop(TabPinned, 1.12, 1); // запись ушла во вкладку «Закреплённые» — вкладка подпрыгивает
        }
        Search.Focus();
    }

    void Ocr_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is { IsImage: true, Recognizing: false } item) OcrRequested?.Invoke(item);
    }

    void TranslateImage_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is { IsImage: true, Recognizing: false } item) TranslateImageRequested?.Invoke(item);
    }

    void Graph_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { IsNodeGraph: true } item) OpenGraph(item);
    }

    /// <summary>Панель поверх всех окон — прячем её, иначе окно графа откроется под ней.</summary>
    void OpenGraph(ClipItem item)
    {
        HideAnimated();
        UnrealGraph.Open(item);
    }

    /// <summary>Пункт меню: значок Segoe Fluent Icons, текст, справа сочетание клавиш.</summary>
    internal static MenuItem Entry(string header, string glyph, Action action, string gesture = "")
    {
        var mi = new MenuItem { Header = header, Tag = glyph, InputGestureText = gesture };
        mi.Click += (_, _) => action();
        return mi;
    }

    void ShowSymbolMenu(SymbolItem s, UIElement target, PlacementMode placement = PlacementMode.MousePoint)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        menu.Items.Add(Entry(UiText.T("Вставить"), "\uE77F", () => TypeRequested?.Invoke(s.Char), "Enter"));
        menu.Items.Add(Entry(UiText.T("Копировать"), "\uE8C8", () => Clipboard.SetText(s.Char)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry(s.Pinned ? UiText.T("Открепить") : UiText.T("Закрепить"), s.Pinned ? "\uE77A" : "\uE718", () => _symbols.TogglePin(s.Char), "Ctrl+P"));
        menu.Closed += (_, _) => Search.Focus();
        OpenMenu(menu);
    }

    /// <summary>Группы: вставка, преобразование, закрепление, удаление; справа сочетания.</summary>
    void ShowMenu(ClipItem item, UIElement target, PlacementMode placement)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        menu.Items.Add(Entry(UiText.T("Вставить"), "\uE77F", () => RequestPaste(item, false), "Enter"));
        if (item.IsImage) menu.Items.Add(Entry(item.InSeries ? UiText.T("Снять отметку") : UiText.T("Выбрать картинку"), "\uE762", () => ToggleSeriesItem(item), UiText.T("Ctrl+клик")));
        if (item.ImageHash == null) menu.Items.Add(Entry(item.IsFolder ? UiText.T("Вставить путь") : UiText.T("Вставить как текст"), "\uE8D2", () => RequestPaste(item, true), "Shift+Enter"));
        if (item.ImageHash == null) menu.Items.Add(new Separator());
        if (!item.IsImage && !item.IsMedia)
            menu.Items.Add(Entry(UiText.T("Редактировать текст"), "\uE70F", () => EditText(item)));
        if (item.IsNodeGraph) menu.Items.Add(Entry(UiText.T("Открыть граф"), "\uE8A7", () => OpenGraph(item)));
        if (item.IsImage)
        {
            menu.Items.Add(OcrEntry(item));
            menu.Items.Add(TranslateImageEntry(item));
        }
        if (item.MediaKind == "video")
        {
            var audio = Entry(item.ExtractingAudio ? UiText.T("Извлекается звук…") : UiText.T("Извлечь звук"), "\uE8D6",
                () => ChooseAudioTrack(item, menu));
            audio.StaysOpenOnClick = true;
            audio.IsEnabled = !item.ExtractingAudio;
            menu.Items.Add(audio);
        }
        if (item.IsFolder)
        {
            var archive = Entry(item.Archiving ? UiText.T("Создаётся ZIP…") : UiText.T("Отправить архивом .zip"), "\uE7B8", () => ArchiveRequested?.Invoke(item));
            archive.IsEnabled = _archiveInfo == null;
            archive.ToolTip = UiText.T("Создать ZIP из текущего содержимого папки и вставить как файл");
            menu.Items.Add(archive);
        }
        if (item.ImageHash == null && !item.IsMedia && !item.IsNodeGraph)
        {
            var compress = Entry(item.Compressing ? UiText.T("Сжимается…") : UiText.T("Сжать промпт"), "\uE73F", () => CompressRequested?.Invoke(item));
            compress.IsEnabled = !item.Compressing;
            menu.Items.Add(compress);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry(item.Pinned ? UiText.T("Открепить") : UiText.T("Закрепить"), item.Pinned ? "\uE77A" : "\uE718", () =>
        {
            _store.TogglePin(item);
            if (item.Pinned) Motion.Pop(TabPinned, 1.12, 1);
        }, "Ctrl+P"));
        if (item.Pinned)
        {
            var colors = new StackPanel { Orientation = Orientation.Horizontal };
            colors.Children.Add(new TextBlock { Text = UiText.T("Цвет"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
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
                    ToolTip = color < 0 ? UiText.T("Без цвета") : ClipItem.TileColorNames[color]
                };
                AutomationProperties.SetName(swatch, (string)swatch.ToolTip);
                swatch.Click += (_, _) => { _store.SetTileColor(item, chosen); menu.IsOpen = false; };
                colors.Children.Add(swatch);
            }
            menu.Items.Add(new MenuItem { Header = colors, Tag = "\uE790", StaysOpenOnClick = true });
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry(UiText.T("Удалить"), "\uE74D", () => RemoveAnimated(item), "Del"));
        menu.Closed += (_, _) => { if (_textEditor == null) Search.Focus(); };
        OpenMenu(menu);
    }

    internal MenuItem OcrEntry(ClipItem item)
    {
        var entry = Entry(item.Recognizing ? UiText.T("Распознаётся текст…") : UiText.T("Распознать текст"), "\uE8D2", () => OcrRequested?.Invoke(item));
        entry.IsEnabled = !item.Recognizing;
        entry.ToolTip = UiText.T("Распознать русский и английский текст локально. Результат появится в истории и будет скопирован, если буфер не изменился.");
        return entry;
    }

    internal MenuItem TranslateImageEntry(ClipItem item)
    {
        var entry = Entry(UiText.T("Перевести текст на русский"), "\uE8C1", () => TranslateImageRequested?.Invoke(item));
        entry.IsEnabled = !item.Recognizing;
        entry.ToolTip = UiText.T("Перевести надписи прямо на картинке. Новое изображение сохранится в истории и скопируется в буфер.");
        return entry;
    }

    async void ChooseAudioTrack(ClipItem item, ContextMenu menu)
    {
        if (item.ExtractingAudio) return;
        item.ExtractingAudio = true;
        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = UiText.T("Чтение звуковых дорожек…"), IsEnabled = false });
        menu.Closed += (_, _) => { cancel.Cancel(); cancel.Dispose(); };
        AudioExtractor.Track[] tracks;
        try
        {
            var source = await Task.Run(() => Paster.PrepareFiles(item, _store.ImagesDir).paths[0], token);
            tracks = await AudioExtractor.ProbeAsync(source, token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            menu.IsOpen = false;
            App.Log(ex);
            ShowInfo(InfoKind.Caution, UiText.T("Не удалось прочитать звуковые дорожки."), ex.Message);
            return;
        }
        finally { item.ExtractingAudio = false; }
        menu.Items.Clear();
        if (tracks.Length == 0)
        {
            menu.IsOpen = false;
            ShowInfo(InfoKind.Info, UiText.T("В видео нет звука."), UiText.T("Звуковые дорожки не найдены."));
        }
        else if (tracks.Length == 1)
        {
            menu.IsOpen = false;
            ExtractAudioRequested?.Invoke(item, tracks[0]);
        }
        else
            foreach (var track in tracks)
                menu.Items.Add(Entry(track.Label, "\uE8D6", () => ExtractAudioRequested?.Invoke(item, track)));
    }

    internal void EditText(ClipItem item)
    {
        if (_textEditor != null) { _textEditor.Activate(); return; }
        CloseValueDrop();
        CancelZoom();
        ClosePeek();
        _outsideTimer.Stop();
        _deactivateTimer.Stop();
        var editor = new TextEditorWindow(_store, item) { Owner = this };
        _textEditor = editor;
        editor.Closed += (_, _) => { _textEditor = null; RefreshViews(); StartOutsideTimer(); };
        editor.Show();
    }

    /// <summary>«⋯» в шапке: пауза записи, настройки и очистка истории — опасное действие не лежит в одном клике.</summary>
    void More_HeaderClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton };
        var pause = Entry(UiText.T("Пауза записи"), "", () => PauseToggleRequested?.Invoke(!_paused));
        pause.IsChecked = _paused;
        menu.Items.Add(pause);
        menu.Items.Add(Entry(UiText.T("Настройки"), "\uE713", OpenSettings));
        menu.Items.Add(new Separator());
        var clear = Entry(UiText.T("Очистить историю…"), "\uE74D", AskClear);
        clear.IsEnabled = _store.Items.Any(i => !i.Pinned);
        menu.Items.Add(clear);
        menu.Closed += (_, _) =>
        {
            if (SettingsFlyout.Visibility != Visibility.Visible && !InfoSecondary.IsKeyboardFocused) Search.Focus();
        };
        OpenMenu(menu);
    }

    void CloseSettings()
    {
        Storage = _storage; // закрытие без «Применить» отменяет изменения бегунков
        SettingsFlyout.Visibility = Visibility.Collapsed;
        Search.Focus();
    }

    /// <summary>Настройки: выезжают из-под «⋯» (из меню панели или трея).</summary>
    public void OpenSettings()
    {
        Storage = _storage;
        ShowVisualSettings_Click(VisualSettingsTab, new RoutedEventArgs());
        SettingsFlyout.Visibility = Visibility.Visible;
        Motion.Enter(SettingsFlyout, 0, -8);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => VisualSettingsTab.Focus());
    }

    void ShowVisualSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsPage(0);
    void ShowStorageSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsPage(1);
    void ShowVfxSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsPage(2);
    void ShowSettingsPage(int page)
    {
        VisualSettingsView.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        StorageSettingsView.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        VfxSettingsView.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        VisualSettingsTab.Style = (Style)FindResource(page == 0 ? "AccentButton" : "StandardButton");
        StorageSettingsTab.Style = (Style)FindResource(page == 1 ? "AccentButton" : "StandardButton");
        VfxSettingsTab.Style = (Style)FindResource(page == 2 ? "AccentButton" : "StandardButton");
    }

    void Storage_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StorageApplyButton == null || _loadingStorage) return; // события при загрузке XAML и установке сохранённых значений
        UpdateStorageLabels();
    }

    void UpdateStorageLabels()
    {
        HistoryCountValue.Text = $"{(int)HistoryCountSlider.Value}";
        HistoryDaysValue.Text = UiText.F($"{(int)HistoryDaysSlider.Value} дн.");
        MediaDaysValue.Text = UiText.F($"{(int)MediaDaysSlider.Value} дн.");
        TextLimitValue.Text = UiText.F($"{(int)TextLimitSlider.Value} тыс.");
        ImageLimitValue.Text = UiText.F($"{(int)ImageLimitSlider.Value} Мп");
        ThumbnailValue.Text = $"{(int)ThumbnailSlider.Value} px";
        FileSizeValue.Text = UiText.F($"{(int)FileSizeSlider.Value} МБ");
        FileCountValue.Text = $"{(int)FileCountSlider.Value}";
        TempDaysValue.Text = UiText.F($"{(int)TempDaysSlider.Value} дн.");
        bool dirty = DraftStorage() != _storage;
        StorageApplyButton.IsEnabled = dirty;
        StorageStatus.Text = dirty ? UiText.T("Изменения не применены") : UiText.T("Настройки сохранены");
    }

    StorageSettings DraftStorage() => new(
        (int)HistoryCountSlider.Value, (int)HistoryDaysSlider.Value, (int)MediaDaysSlider.Value,
        (int)TextLimitSlider.Value, (int)ImageLimitSlider.Value, (int)ThumbnailSlider.Value, (int)FileSizeSlider.Value,
        (int)FileCountSlider.Value, (int)TempDaysSlider.Value);

    void StorageApply_Click(object sender, RoutedEventArgs e)
    {
        var settings = DraftStorage();
        if (settings == _storage) return;
        StorageChanged?.Invoke(settings);
        Storage = settings;
    }

    void ShowGroupMenu(PinnedGroup group, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Entry(UiText.T("Переименовать"), "\uE70F", () =>
        {
            if (PromptGroupName(UiText.T("Переименовать группу"), group.Name) is { } name) _store.RenameGroup(group, name);
        }));
        menu.Items.Add(Entry(UiText.T("Удалить группу · плитки останутся"), "\uE74D", () => _store.DeleteGroup(group)));
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
        menu.Items.Add(Entry(UiText.T("Создать группу"), "\uE710", CreateGroup));
        menu.Closed += (_, _) => { if (!_editingGroup) Search.Focus(); };
        OpenMenu(menu);
    }

    // Button flyouts share the text-peek rule; context menus keep their cursor anchor.
    CustomPopupPlacement[] SidePlacements(Size popup, Size target, double top, double overlap = 0)
    {
        var center = Root.PointToScreen(new Point(Root.ActualWidth / 2, Root.ActualHeight / 2));
        var monitor = MonitorFromPoint(new POINT { X = (int)center.X, Y = (int)center.Y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        bool preferLeft = GetMonitorInfo(monitor, ref info)
            && center.X >= (info.rcWork.Left + info.rcWork.Right) / 2.0;
        return OrderSidePlacements(popup, target, top, overlap, preferLeft);
    }

    internal static CustomPopupPlacement[] OrderSidePlacements(Size popup, Size target, double top, double overlap, bool preferLeft)
    {
        var right = new CustomPopupPlacement(new Point(target.Width - overlap, top), PopupPrimaryAxis.Horizontal);
        var left = new CustomPopupPlacement(new Point(-popup.Width + overlap, top), PopupPrimaryAxis.Horizontal);
        return preferLeft ? [left, right] : [right, left];
    }

    internal void PrepareMenu(ContextMenu menu)
    {
        if (menu.Placement == PlacementMode.MousePoint)
        {
            // Match the visible border to the pointer, excluding the template's shadow margin.
            menu.HorizontalOffset = -12;
            menu.VerticalOffset = -4;
            return;
        }
        double top = menu.PlacementTarget.TranslatePoint(new Point(), Root).Y;
        menu.PlacementTarget = Root;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
            SidePlacements(popup, target, (top - 4) * target.Width / Root.ActualWidth);
    }

    void OpenMenu(ContextMenu menu)
    {
        CancelZoom();
        StopGif();
        ClosePeek();
        PrepareMenu(menu);
        _menuOpen = true;
        _outsideTimer.Stop();
        menu.Closed += (_, _) => { _menuOpen = false; StartOutsideTimer(); };
        Motion.AnimateOpen(menu, menu.Placement == PlacementMode.MousePoint ? 0 : -8);
        menu.IsOpen = true;
    }

    void CreateGroup()
    {
        if (PromptGroupName(UiText.T("Новая группа"), "", allowEmpty: true) is { } name) _store.AddGroup(name);
    }

    /// <summary>
    /// Имя группы в разметке ContentDialog: заголовок, подпись над полем, подсказка или ошибка под ним,
    /// снизу акцентная «Сохранить» и «Отмена». Появляется с Scale 1.05 → 1, уходит обратно.
    /// </summary>
    string? PromptGroupName(string title, string current, bool allowEmpty = false)
    {
        const double Shadow = 24; // поле под тень вокруг диалога в прозрачном окне
        var dialog = new Window
        {
            Title = title, Owner = this, Width = 332 + 2 * Shadow, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            FontFamily = (FontFamily)FindResource("TextFont"), FontSize = 14, UseLayoutRounding = true
        };
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        dialog.SetResourceReference(ForegroundProperty, "TextPrimary");
        string hint = allowEmpty ? UiText.F($"Если оставить пустым, будет «{_store.NextGroupName()}»") : UiText.T("До 40 символов");
        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        var label = new TextBlock { Text = UiText.T("Название"), FontSize = 13, Margin = new Thickness(0, 0, 0, 6) };
        var box = new TextBox { Text = current, MaxLength = 40, Style = (Style)FindResource("FieldBox") };
        AutomationProperties.SetLabeledBy(box, label);
        var help = new TextBlock { Text = hint, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var save = new Button { Content = UiText.T("Сохранить"), Style = (Style)FindResource("AccentButton") };
        var cancel = new Button { Content = UiText.T("Отмена"), Style = (Style)FindResource("StandardButton"), Margin = new Thickness(8, 0, 0, 0) };
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(cancel, 1);
        actions.Children.Add(save);
        actions.Children.Add(cancel);
        var footer = new Border { Padding = new Thickness(24, 20, 24, 20), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0, 0, 7, 7), Child = actions };
        footer.SetResourceReference(Border.BackgroundProperty, "WindowSurface");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 20), Children = { heading, label, box, help } };
        var card = new Border
        {
            Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { body, footer } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.4 }
        };
        card.SetResourceReference(Border.BackgroundProperty, "DialogSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "WindowStroke");
        dialog.Content = card;

        bool closing = false;
        void Close(bool result)
        {
            if (closing) return;
            closing = true;
            var scale = Motion.Scale(card);
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1.05, Motion.Normal, Motion.Out);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1.05, Motion.Normal, Motion.Out);
            Motion.To(card, OpacityProperty, 0, Motion.Normal, Motion.Out, done: () => dialog.DialogResult = result);
        }
        void Fail(string message)
        {
            help.Text = message;
            help.SetResourceReference(TextBlock.ForegroundProperty, "CriticalText");
            box.Tag = "Error";
            box.Focus();
        }
        box.TextChanged += (_, _) =>
        {
            if (box.Tag == null) return;
            box.Tag = null;
            help.Text = hint;
            help.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        };
        cancel.Click += (_, _) => Close(false);
        save.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (!allowEmpty && name.Length == 0) { Fail(UiText.T("Введите название группы")); return; }
            if (_store.Groups.Any(g => g.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) && g.Name != current))
            {
                Fail(UiText.F($"Группа «{name}» уже есть"));
                return;
            }
            Close(true);
        };
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !cancel.IsKeyboardFocused) { save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        };
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
            var scale = Motion.Scale(card);
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Slow, from: 1.05);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Slow, from: 1.05);
            Motion.To(card, OpacityProperty, 1, Motion.Slow, from: 0);
        };
        _editingGroup = true;
        try { return dialog.ShowDialog() == true ? box.Text.Trim() : null; }
        finally { _editingGroup = false; Search.Focus(); }
    }

    void Pinned_MouseDown(object sender, MouseButtonEventArgs e)
    {
        CloseValueDrop();
        _dragStart = null;
        _dragItem = null;
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ButtonBase>() != null) return;
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext is ClipItem selected
            && HandleImageSelectionMouseDown(selected, Keyboard.Modifiers))
        {
            ((ListBox)sender).SelectedItem = selected;
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext is ClipItem part)
            {
                BeginMergeSelection();
                ((ListBox)sender).SelectedItem = part;
                ToggleMergePart(part);
                _altClick = true;
            }
            e.Handled = true;
            return;
        }
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<Button>() != null) return;
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext is ClipItem item)
        {
            _dragStart = e.GetPosition(this);
            _dragItem = item;
        }
    }

    void Pinned_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || _dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = true;
        _suppressClick = true;
        CancelZoom();
        StopGif();
        BeginDragVisual((ListBox)sender, _dragItem);
        try
        {
            using var wheel = new DragWheelScroll([ValueScroll, Active], area =>
            { if (area != ValueScroll) { ClearDropTarget(); CloseValueDrop(); } });
            DragDrop.DoDragDrop((ListBox)sender, new DataObject(typeof(ClipItem), _dragItem), DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _dragStart = null; _dragItem = null; ClearDropTarget(); CloseValueDrop(); _dragging = false;
            EndDragVisual();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _suppressClick = false);
        }
    }

    void ClearDropTarget()
    {
        if (_dropTarget?.Tag as string is "Before" or "After" or "Group") _dropTarget.Tag = null;
        _dropTarget = null;
    }

    void Pinned_DragOver(object sender, DragEventArgs e)
    {
        var target = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        if (target?.DataContext is ClipItem)
        {
            var at = e.GetPosition(target);
            bool center = _pinnedAsList ? at.Y >= 12 && at.Y <= target.ActualHeight - 12
                : at.X >= 18 && at.X <= target.ActualWidth - 18;
            if (center && PreviewValue(target, e.Data))
            {
                ClearDropTarget();
                e.Effects = _automaticValue != null ? DragDropEffects.Copy : DragDropEffects.None;
                SetDragVisualStatus(_automaticValue != null, false, _automaticValue == null);
                ScrollDrag(PinnedList, e);
                e.Handled = true;
                return;
            }
        }
        CloseValueDrop();
        if (e.Data.GetData(typeof(ClipItem)) is not ClipItem dragged || !dragged.Pinned || !_store.Items.Contains(dragged))
        {
            ClearDropTarget();
            e.Effects = DragDropEffects.None;
            SetDragVisualStatus(false, false);
            e.Handled = true;
            return;
        }
        string? tag = null;
        if (target?.DataContext is ClipItem item && item != dragged)
        {
            var at = e.GetPosition(target); // плитки встают слева/справа, строки списка — выше/ниже
            tag = (_pinnedAsList ? at.Y < target.ActualHeight / 2 : at.X < target.ActualWidth / 2) ? "Before" : "After";
        }
        else if (target?.DataContext is PinnedGroup)
        {
            tag = "Group";
        }
        if (tag == null) ClearDropTarget();
        else
        {
            if (_dropTarget != target) { ClearDropTarget(); _dropTarget = target; }
            if (!Equals(target!.Tag, tag)) target.Tag = tag;
        }
        e.Effects = _dropTarget != null ? DragDropEffects.Move : DragDropEffects.None;
        SetDragVisualStatus(false, _dropTarget != null);
        ScrollDrag(PinnedList, e);
        e.Handled = true;
    }

    void Pinned_DragLeave(object sender, DragEventArgs e)
    {
        if (!((ListBox)sender).IsMouseOver) ClearDropTarget();
    }

    void Pinned_Drop(object sender, DragEventArgs e)
    {
        if (_valueTarget != null) { Values_Drop(sender, e); return; }
        if (e.Data.GetData(typeof(ClipItem)) is ClipItem dragged && _dropTarget is { } target)
        {
            MergeDragVisual(target);
            if (target.DataContext is ClipItem item)
                _store.MovePinned(dragged, item.PinnedGroupId, item, (string?)target.Tag == "After");
            else if (target.DataContext is PinnedGroup group)
                _store.MovePinned(dragged, group.Id == Guid.Empty ? null : group.Id);
        }
        ClearDropTarget();
        e.Handled = true;
    }

    void AnimationsToggle_Changed(object sender, RoutedEventArgs e)
    {
        Motion.Enabled = AnimationsToggle.IsChecked == true;
        RefreshVfx();
        AnimationsChanged?.Invoke(Motion.Enabled);
    }

    void KeyHintsToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool on = KeyHintsToggle.IsChecked == true;
        KeyHints.Visibility = on && !SeriesActive && !_mergeTextFilter ? Visibility.Visible : Visibility.Collapsed;
        KeyHintsChanged?.Invoke(on);
    }

    /// <summary>
    /// Бегунок переключателя: триггер шаблона уже поставил его к новому краю, а мы стартуем сдвигом
    /// со старого места (FLIP) — так он и растягивается при нажатии от своего края.
    /// </summary>
    void AnimateSwitch(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { IsVisible: true } toggle
            || toggle.Template.FindName("Knob", toggle) is not FrameworkElement knob
            || toggle.Template.FindName("Track", toggle) is not Border track) return;
        double distance = track.ActualWidth - track.BorderThickness.Left - track.BorderThickness.Right
            - knob.ActualWidth - knob.Margin.Left - knob.Margin.Right;
        Motion.To(Motion.Shift(knob), TranslateTransform.XProperty, 0, Motion.Normal,
            from: toggle.IsChecked == true ? -distance : distance);
    }

    void Timing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ShowTimingValue == null || HideTimingValue == null) return; // ValueChanged срабатывает во время загрузки XAML
        ShowTimingValue.Text = UiText.F($"{ShowDurationMs} мс");
        HideTimingValue.Text = UiText.F($"{HideDurationMs} мс");
        TimingsChanged?.Invoke(ShowDurationMs, HideDurationMs);
    }

    void OutsideHideToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!AutoHideOutside) { _outsideTimer.Stop(); _deactivateTimer.Stop(); }
        AutoHideOutsideChanged?.Invoke(((ToggleButton)sender).IsChecked == true);
    }

    void OutsideHideDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = (int)((Slider)sender).Value;
        if (OutsideHideValue == null) return;
        OutsideHideValue.Text = UiText.F($"{ms / 1000.0:0.#} с");
        OutsideHideDelayChanged?.Invoke(ms);
    }

    void ZoomDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = (int)((Slider)sender).Value;
        SetValue(ImageZoomDelayMsProperty, ms);
        if (ZoomDelayValue == null) return; // ValueChanged при загрузке XAML
        ZoomDelayValue.Text = UiText.F($"{ms / 1000.0:0.#} с");
        ImageZoomDelayChanged?.Invoke(ms);
    }

    void ZoomScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int scale = (int)((Slider)sender).Value;
        if (ZoomScaleValue == null) return; // ValueChanged при загрузке XAML
        ZoomScaleValue.Text = $"{260 * scale} × {120 * scale}";
        ImageZoomScaleChanged?.Invoke(scale);
    }

    void TextPeekToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (TextPeekToggle.IsChecked != true && TextPeek != null) ClosePeek();
        RaiseTextPeekChanged();
    }

    void TextPeekDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TextPeekDelayValue == null) return; // ValueChanged при загрузке XAML
        TextPeekDelayValue.Text = UiText.F($"{TextPeekDelayMs / 1000.0:0.#} с");
        RaiseTextPeekChanged();
    }

    void TextPeekWidth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TextPeekWidthValue == null) return;
        TextPeekWidthValue.Text = $"{TextPeekWidth} px";
        RaiseTextPeekChanged();
    }

    void RaiseTextPeekChanged()
    {
        if (TextPeekWidthSlider != null) TextPeekChanged?.Invoke(TextPeekEnabled, TextPeekDelayMs, TextPeekWidth);
    }

    bool HoverPreviewBlocked => _dragging || _mergingDrag || _collectingMerge || Native.IsDown(VK_MENU) || Native.IsDown(VK_CONTROL) || Native.IsDown(0x01);

    void StartZoom(Image target)
    {
        CancelZoom();
        if (HoverPreviewBlocked) return;
        if (target.ToolTip is not ToolTip tip || target.Parent is not Grid grid ||
            grid.Children.OfType<Border>().FirstOrDefault(b => b.Name is "ZoomProgress" or "GraphZoomProgress") is not Border progress) return;

        _zoomTarget = target;
        _zoomTip = tip;
        _zoomProgress = progress;
        _zoomFull = target.DataContext is ClipItem { IsImage: true, IsGif: false, ImageDir: not null } item ? LoadZoomImage(item) : null;
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
        if (HoverPreviewBlocked) { CancelZoom(); return; }
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
        if (_zoomFull != null) ShowFullZoom(_zoomFull, tip, preview);
    }

    /// <summary>
    /// Миниатюра — 320 px, в окне увеличения до 1300 px она мылится. Полную картинку декодируем сразу
    /// под размер окна увеличения (с учётом DPI), в фоне, пока идёт задержка наведения.
    /// </summary>
    Task<System.Windows.Media.Imaging.BitmapSource?> LoadZoomImage(ClipItem item)
    {
        int scale = ImageZoomScale;
        double fit = Math.Min(260.0 * scale, 120.0 * scale * item.ImageWidth / Math.Max(1, item.ImageHeight));
        int width = (int)Math.Min(item.ImageWidth, Math.Ceiling(fit * VisualTreeHelper.GetDpi(this).DpiScaleX));
        var path = Path.Combine(item.ImageDir!, item.ImageHash + ".dat");
        return Task.Run(() => ImageFiles.LoadBitmap(path, width));
    }

    async void ShowFullZoom(Task<System.Windows.Media.Imaging.BitmapSource?> load, ToolTip tip, Image preview)
    {
        if (await load is { } full && !HoverPreviewBlocked && ReferenceEquals(_zoomTip, tip) && tip.IsOpen)
            preview.SetCurrentValue(Image.SourceProperty, full);
    }

    /// <summary>Закрыть и вернуть привязку к миниатюре: полная картинка не должна жить в скрытой подсказке.</summary>
    static void CloseZoomTip(ToolTip tip)
    {
        tip.IsOpen = false;
        if (tip.Content is Image preview) BindingOperations.GetBindingExpression(preview, Image.SourceProperty)?.UpdateTarget();
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
            CloseZoomTip(tip);
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
        ClosePeek();
        _zoomTimer.Stop();
        HideZoomProgress();
        if (_zoomTip != null) CloseZoomTip(_zoomTip);
        _zoomTarget = null;
        _zoomTip = null;
        _zoomFull = null;
    }

    async void Image_MouseEnter(object sender, MouseEventArgs e)
    {
        if (HoverPreviewBlocked) { CancelZoom(); StopGif(); return; }
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

    // ---------- InfoBar ----------

    /// <summary>Сообщение в панели вместо балуна трея: пока она открыта, человек смотрит сюда.</summary>
    public void ShowInfo(InfoKind kind, string title, string message)
    {
        _transient = new Info(kind, title, message);
        RefreshInfo();
    }

    public void SetArchiveProgress(string? message, Action? cancel = null)
    {
        if (_archiveInfo == null && message != null) _transient = null;
        _archiveInfo = message == null ? null : new Info(InfoKind.Info, UiText.T("Создаётся ZIP."), message,
            (UiText.T("Отменить"), cancel), Closable: false);
        RefreshInfo();
    }

    internal void SetOcrProgress(Action? cancel, bool translating = false)
    {
        if (cancel != null) _transient = null;
        _ocrInfo = cancel == null ? null : new Info(InfoKind.Info, translating ? UiText.T("Текст переводится на русский.") : UiText.T("Распознаётся текст."),
            translating ? UiText.T("Перевод выполняется локальной моделью. Можно продолжать работу.") : UiText.T("Можно продолжать работу — изображение обрабатывается локально."), (UiText.T("Отменить"), cancel), Closable: false);
        RefreshInfo();
    }

    internal void ShowTranslatedImageResult(bool copied, bool review)
    {
        string title = copied ? UiText.T("Картинка с переводом скопирована.") : UiText.T("Картинка с переводом в истории.");
        if (review) title += UiText.T(" Проверьте надписи.");
        _transient = new Info(review || !copied ? InfoKind.Caution : InfoKind.Success, title, "", Closable: false, Compact: true);
        RefreshInfo();
    }

    internal void ShowOcrResult(ClipItem item, OcrText.Result result, bool copied)
    {
        string message = result.ReviewNeeded ? UiText.T("Проверьте похожие буквы и порядок строк перед вставкой.") : UiText.T("Русский и английский текст извлечён из изображения.");
        if (!copied) message += UiText.T(" Буфер не изменён — скопируйте текст из истории.");
        _transient = new Info(result.ReviewNeeded || !copied ? InfoKind.Caution : InfoKind.Success,
            copied ? UiText.T("Текст распознан и скопирован.") : UiText.T("Распознанный текст в истории."), message,
            (UiText.T("Проверить текст"), () => { if (_store.Items.Contains(item)) EditText(item); }));
        RefreshInfo();
    }

    /// <summary>Пауза записи: жёлтая полоса «Возобновить», пока пауза не снята (App.SetPaused).</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        RefreshInfo();
    }

    Info? PauseInfo => _paused
        ? new Info(InfoKind.Caution, UiText.T("Запись на паузе."), UiText.T("Новые копии не сохраняются."), (UiText.T("Возобновить"), () => PauseToggleRequested?.Invoke(false)), Closable: false)
        : null;

    /// <summary>Показать главное сообщение: разовое важнее паузы; закрыли разовое — снова видна пауза.</summary>
    void RefreshInfo()
    {
        var info = _transient ?? _archiveInfo ?? _ocrInfo ?? PauseInfo ?? _updateInfo;
        if (!ReferenceEquals(_shown, info))
        {
            _infoTimer.Stop();
            if (info?.Compact == true) _infoTimer.Start();
        }
        if (info == null)
        {
            _shown = null;
            if (IsShown) Motion.Collapse(InfoBar);
            else { InfoBar.BeginAnimation(HeightProperty, null); InfoBar.Visibility = Visibility.Collapsed; }
            return;
        }
        bool appear = InfoBar.Visibility != Visibility.Visible || _shown == null;
        _shown = info;
        (string fill, string icon, string glyph) = info.Kind switch
        {
            InfoKind.Caution => ("CautionFill", "CautionIcon", "\uE769"),
            InfoKind.Success => ("SuccessFill", "SuccessIcon", "\uE73E"),
            InfoKind.Critical => ("CriticalFill", "CriticalIcon", "\uE711"),
            InfoKind.Info => ("CardFill", "Accent", "\uE946"),
            _ => ("CardFill", "Accent", ""),
        };
        InfoBar.SetResourceReference(Border.BackgroundProperty, fill);
        InfoBar.Padding = info.Compact ? new Thickness(8, 5, 8, 5) : new Thickness(12, 9, 8, 9);
        InfoIcon.SetResourceReference(Border.BackgroundProperty, icon);
        InfoGlyph.Text = glyph;
        InfoIcon.Visibility = info.Kind == InfoKind.Neutral ? Visibility.Collapsed : Visibility.Visible;
        InfoTitle.Text = info.Title + " ";
        InfoMessage.Text = info.Message;
        InfoPrimary.Visibility = info.Primary != null ? Visibility.Visible : Visibility.Collapsed;
        InfoPrimary.Content = info.Primary?.Text;
        // Одна кнопка — обычная («Возобновить»), две — первая главная («Очистить» / «Отмена»).
        InfoPrimary.Style = (Style)FindResource(info.Secondary == null ? "StandardButton" : "AccentButton");
        InfoSecondary.Visibility = info.Secondary != null ? Visibility.Visible : Visibility.Collapsed;
        InfoSecondary.Content = info.Secondary?.Text;
        InfoActions.Visibility = info.Primary != null ? Visibility.Visible : Visibility.Collapsed;
        InfoClose.Visibility = info.Closable && info.Secondary == null ? Visibility.Visible : Visibility.Collapsed;
        if (appear && IsShown)
        {
            Motion.Expand(InfoBar);
            if (InfoIcon.Visibility == Visibility.Visible) Motion.Pop(InfoIcon, 1.15, 0.3);
        }
        else
        {
            InfoBar.BeginAnimation(HeightProperty, null);
            InfoBar.BeginAnimation(OpacityProperty, null);
            InfoBar.Visibility = Visibility.Visible;
        }
    }

    void CloseTransient()
    {
        _transient = null;
        RefreshInfo();
    }

    void RunInfoAction(Action? action)
    {
        if (_shown != null && ReferenceEquals(_shown, _transient)) _transient = null;
        action?.Invoke();
        RefreshInfo();
        Search.Focus();
    }

    void InfoPrimary_Click(object sender, RoutedEventArgs e) => RunInfoAction(_shown?.Primary?.Act);
    void InfoSecondary_Click(object sender, RoutedEventArgs e) => RunInfoAction(_shown?.Secondary?.Act);
    void InfoClose_Click(object sender, RoutedEventArgs e) { CloseTransient(); Search.Focus(); }

    // ---------- Очистка и удаление ----------

    static string Records(int n) => UiText.Language != "ru" ? UiText.T(n == 1 ? "запись" : "записей") : (n % 10, n % 100) switch
    {
        (1, not 11) => UiText.T("запись"),
        (>= 2 and <= 4, not (>= 12 and <= 14)) => UiText.T("записи"),
        _ => UiText.T("записей"),
    };

    /// <summary>Очистка истории: подтверждение строкой в панели, а не системным окном. Фокус на «Отмена», как раньше «Нет».</summary>
    public void AskClear()
    {
        int count = _store.Items.Count(i => !i.Pinned);
        if (count == 0) return;
        TabAll.IsChecked = true;
        _transient = new Info(InfoKind.Neutral, UiText.F($"Удалить {count} {Records(count)}?"), UiText.T("Закреплённые останутся."),
            (UiText.T("Очистить"), new Action(ClearAnimated)), (UiText.T("Отмена"), null));
        RefreshInfo();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => InfoSecondary.Focus());
    }

    /// <summary>Видимые записи уезжают вправо по очереди (шаг 30 мс), затем история очищается и выплывает пустое состояние.</summary>
    void ClearAnimated()
    {
        if (_clearing) return;
        var leaving = _view.Cast<ClipItem>().Where(i => !i.Pinned)
            .Select(i => List.ItemContainerGenerator.ContainerFromItem(i) as ListBoxItem).OfType<ListBoxItem>().ToList();
        void Done()
        {
            _clearing = false;
            foreach (var c in leaving) ResetContainer(c);
            _store.ClearUnpinned();
        }
        if (!Motion.Enabled || !IsShown || leaving.Count == 0) { Done(); return; }
        _clearing = true;
        for (int i = 0; i < leaving.Count; i++)
        {
            var c = leaving[i];
            Motion.To(Motion.Shift(c), TranslateTransform.XProperty, 28, Motion.Normal, Motion.Out, delayMs: i * 30);
            Motion.To(c, OpacityProperty, 0, Motion.Normal, Motion.Out, delayMs: i * 30, done: i == leaving.Count - 1 ? Done : (Action?)null);
        }
    }

    /// <summary>Удаление записи: уезжает вправо и тает, затем строка схлопывается. Выделение остаётся на той же позиции.</summary>
    void RemoveAnimated(ClipItem item)
    {
        if (!_leaving.Add(item)) return;
        var list = Active;
        int index = list.SelectedIndex;
        var container = list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        void Done()
        {
            _leaving.Remove(item);
            if (container != null) ResetContainer(container);
            _store.Remove(item);
            if (list.Items.Count > 0) list.SelectedIndex = Math.Clamp(index, 0, list.Items.Count - 1);
        }
        if (!Motion.Enabled || !IsShown || container == null) { Done(); return; }
        Motion.To(Motion.Shift(container), TranslateTransform.XProperty, 28, Motion.Normal, Motion.Out);
        Motion.To(container, OpacityProperty, 0, Motion.Normal, Motion.Out, done: () =>
        {
            if (list == List) Motion.To(container, HeightProperty, 0, Motion.Normal, Motion.Out, from: container.ActualHeight, done: Done);
            else Done();
        });
    }

    /// <summary>Контейнеры переиспользуются: снять с них анимации до того, как их получит другая запись.</summary>
    static void ResetContainer(ListBoxItem c)
    {
        c.ClearValue(SensitiveRevealedProperty);
        c.BeginAnimation(OpacityProperty, null);
        c.BeginAnimation(HeightProperty, null);
        c.ClearValue(OpacityProperty);
        c.ClearValue(HeightProperty);
        c.ClearValue(RenderTransformProperty);
        if (c.Template?.FindName("Glow", c) is Border glow)
        {
            glow.BeginAnimation(OpacityProperty, null);
            glow.Opacity = 0;
            glow.SetResourceReference(Border.BackgroundProperty, "Accent");
        }
    }

    // ---------- Новые записи и выбор ----------

    /// <summary>Пока панель открыта, новая запись раскрывается сверху (остальные съезжают) и вспыхивает акцентом.</summary>
    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SeriesActive || _mergeTextFilter || !IsShown || !Motion.Enabled || e.NewStartingIndex != 0
            || e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move)
            || e.NewItems?[0] is not ClipItem item) return;
        bool added = e.Action == NotifyCollectionChangedAction.Add;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem c) return;
            if (added) Motion.Expand(c);
            if (c.Template.FindName("Glow", c) is UIElement glow)
            {
                if (glow is Border border) border.SetResourceReference(Border.BackgroundProperty, "Accent");
                Motion.To(glow, OpacityProperty, 0, 1200, from: 0.28);
            }
        });
    }

    /// <summary>Полоска выбранной записи вырастает по высоте — видно, куда перешёл выбор.</summary>
    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsShown || e.AddedItems.Count == 0
            || List.ItemContainerGenerator.ContainerFromItem(e.AddedItems[0]) is not ListBoxItem c
            || c.Template.FindName("Pill", c) is not UIElement pill) return;
        Motion.To(Motion.Scale(pill), ScaleTransform.ScaleYProperty, 1, Motion.Normal, from: 0);
    }

    // ---------- Просмотр текста при наведении ----------

    static readonly Brush PeekFade = CreatePeekFade();
    static Brush CreatePeekFade()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Colors.Black, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 0.82));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }

    static string LinesWord(int n) => UiText.Language != "ru" ? UiText.T(n == 1 ? "строка" : "строк") : (n % 10, n % 100) switch
    {
        (1, not 11) => UiText.T("строка"),
        (>= 2 and <= 4, not (>= 12 and <= 14)) => UiText.T("строки"),
        _ => UiText.T("строк"),
    };

    /// <summary>
    /// Просмотр нужен, только если текст не поместился. ponytail: оценка по длине и строкам, без измерения шрифтом;
    /// плитка вмещает ~6 строк по ~16 символов, закреплённая строка — заголовок и подробности,
    /// обычная строка — до трёх строк текста.
    /// </summary>
    bool NeedsPeek(ClipItem i, bool regularList) => regularList
        ? i.Kind is "text" or "code" or "url" or "path"
            && (i.Text.Length > (i.Kind == "code" ? 100 : 130) || i.Preview.Count(ch => ch == '\n') >= 3)
        : i.Kind switch
    {
        "text" or "code" => _pinnedAsList
            ? i.Text.Trim().Length > 40 || i.Text.Trim().Contains('\n')
            : i.Text.Length > 90 || i.Text.Count(ch => ch == '\n') >= 5,
        "url" or "path" => i.Text.Trim().Length > (_pinnedAsList ? 44 : 34),
        _ => false,
    };

    /// <summary>Навели на запись: полоска внизу заполняется за время задержки (как у картинок), затем просмотр.</summary>
    void Text_PeekMove(object sender, MouseEventArgs e)
    {
        if (HoverPreviewBlocked) { ClosePeek(); return; }
        var container = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        if (ReferenceEquals(container, _peekHover) && ReferenceEquals(container?.DataContext, _peekItem)) return;
        ClosePeek();
        if (container?.DataContext is not ClipItem item || _dragging || e.LeftButton == MouseButtonState.Pressed
            || _shortcutContainers.Count > 0 || !item.IsSensitive && (!TextPeekEnabled || !NeedsPeek(item, ReferenceEquals(sender, List)))) return;
        _peekHover = container;
        _peekItem = item;
        item.PropertyChanged += PeekItem_Changed;
        container.Unloaded += PeekContainer_Unloaded;
        container.DataContextChanged += PeekContainer_DataContextChanged;
        container.MouseLeave += Text_PeekLeave;
        _peekStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var delay = item.IsSensitive ? SensitivePeekDelay : TimeSpan.FromMilliseconds(TextPeekDelayMs);
        if (container.Template.FindName("PeekProgress", container) is Border bar)
        {
            var scale = new ScaleTransform(0, 1);
            bar.RenderTransform = scale;
            bar.Visibility = Visibility.Visible;
            if (Motion.Enabled) scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, delay));
        }
        _peekTimer.Interval = delay;
        _peekTimer.Start();
    }

    void Text_PeekLeave(object sender, MouseEventArgs e) => ClosePeek();

    /// <summary>Колесо над записью с открытым просмотром листает текст, а не список.</summary>
    void Text_PeekWheel(object sender, MouseWheelEventArgs e)
    {
        if (HoverPreviewBlocked) { ClosePeek(); return; }
        if (!TextPeek.IsOpen || _peekItem == null || PeekScroll.ExtentHeight <= PeekScroll.ViewportHeight + 1)
        {
            ClosePeek();
            return;
        }
        PeekScroll.ScrollToVerticalOffset(PeekScroll.VerticalOffset - e.Delta / 120.0 * 60);
        e.Handled = true;
    }

    void OpenPeek()
    {
        if (HoverPreviewBlocked) { ClosePeek(); return; }
        if (_peekHover is not { IsMouseOver: true } container || _peekItem is not { } item || !IsShown
            || !_store.Items.Contains(item) || !ReferenceEquals(container.DataContext, item)
            || item.IsSensitive && !SensitivePeekReady(_peekStartedAt)) { ClosePeek(); return; }
        if (container.Template.FindName("PeekProgress", container) is Border bar) bar.Visibility = Visibility.Collapsed;

        if (item.IsSensitive)
        {
            TextPeek.IsOpen = false;
            PeekText.Text = "";
            SetSensitiveRevealed(container, true);
            return;
        }

        bool titled = item.Kind is "url" or "path";
        bool mono = item.Kind is "code" or "path";
        int lines = item.Text.Trim().Count(ch => ch == '\n') + 1;
        PeekKind.Text = item.Kind switch { "code" => UiText.T("Код"), "url" => UiText.T("Ссылка"), "path" => UiText.T("Путь к файлу"), _ => UiText.T("Текст") };
        PeekMeta.Text = lines > 1 ? UiText.F($"{item.Text.Length:N0} симв., {lines} {LinesWord(lines)}") : UiText.F($"{item.Text.Length:N0} симв.");
        PeekTitle.Visibility = titled ? Visibility.Visible : Visibility.Collapsed;
        PeekTitle.Text = item.TileTitle;
        var text = titled ? item.Text.Trim().Trim('"') : item.Text.Trim('\r', '\n');
        PeekText.Text = (text.Length > 20000 ? text[..20000] + "…" : text).Replace("\t", "    ");
        PeekText.FontFamily = (FontFamily)FindResource(mono ? "MonoFont" : "TextFont");
        PeekText.FontSize = 13;
        PeekBand.Background = item.TileBand;
        PeekCard.Width = TextPeekWidth;
        PeekScroll.ScrollToTop();

        double top = container.TranslatePoint(new Point(0, 0), Root).Y;
        TextPeek.PlacementTarget = Root;
        TextPeek.CustomPopupPlacementCallback = (popup, target, _) =>
        {
            double k = popup.Width / (PeekCard.Width + 24);
            return SidePlacements(popup, target, (top - 4) * k, 4 * k);
        };
        TextPeek.IsOpen = false; // переоткрыть — пересчитать место под новую запись
        TextPeek.IsOpen = true;
        Motion.Enter(PeekCard, 0, 6);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdatePeekFade);
    }

    void ClosePeek()
    {
        _peekTimer.Stop();
        _peekStartedAt = 0;
        if (_peekItem != null) _peekItem.PropertyChanged -= PeekItem_Changed;
        if (_peekHover != null)
        {
            _peekHover.ClearValue(SensitiveRevealedProperty);
            _peekHover.Unloaded -= PeekContainer_Unloaded;
            _peekHover.DataContextChanged -= PeekContainer_DataContextChanged;
            _peekHover.MouseLeave -= Text_PeekLeave;
        }
        if (_peekItem?.IsSensitive == true)
        {
            TextPeek.IsOpen = false;
            PeekText.Text = "";
        }
        if (_peekHover?.Template?.FindName("PeekProgress", _peekHover) is Border bar)
        {
            bar.RenderTransform = null;
            bar.Visibility = Visibility.Collapsed;
        }
        _peekHover = null;
        _peekItem = null;
        if (TextPeek.IsOpen)
            Motion.To(PeekCard, OpacityProperty, 0, Motion.Fast, Motion.Out, done: () => { if (_peekItem == null) TextPeek.IsOpen = false; });
    }

    void PeekItem_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(ClipItem.IsSensitive) or nameof(ClipItem.Preview)) ClosePeek();
    }
    void PeekContainer_Unloaded(object sender, RoutedEventArgs e) => ClosePeek();
    void PeekContainer_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => ClosePeek();

    void PeekScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdatePeekFade();

    /// <summary>Текст ниже края тает, а не обрывается; подсказка про колесо — только если есть что листать.</summary>
    void UpdatePeekFade()
    {
        bool scrollable = PeekScroll.ExtentHeight > PeekScroll.ViewportHeight + 1;
        bool more = PeekScroll.ExtentHeight - PeekScroll.VerticalOffset - PeekScroll.ViewportHeight > 1;
        PeekScroll.OpacityMask = scrollable && more ? PeekFade : null;
        PeekHint.Visibility = scrollable ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Номера для Ctrl+1…9 ----------

    static void RevealShortcut(Border key)
    {
        key.BeginAnimation(OpacityProperty, null); key.Opacity = 1; key.Visibility = Visibility.Visible;
        var scale = Motion.Scale(key);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Fast, from: 0.96);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Fast, from: 0.96);
    }

    void ShowShortcuts()
    {
        HideShortcuts();
        ClosePeek();
        if (SeriesActive || _mergeTextFilter || !IsShown || SettingsFlyout.Visibility == Visibility.Visible) return;
        var list = Active;
        var items = (PinnedTab ? list.Items.OfType<ClipItem>().Cast<object>() : list.Items.Cast<object>()).Take(9).ToList();
        for (int i = 0; i < items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromItem(items[i]) is not ListBoxItem c
                || c.Template.FindName("Key", c) is not Border { Child: TextBlock number } key) continue;
            number.Text = (i + 1).ToString();
            RevealShortcut(key);
            // Сразу освобождаем место номеру, без задержки разметки.
            if (list == List && c.Template.FindName("Bd", c) is Border bd)
            {
                var room = new Thickness(36, 6, 6, 6);
                bd.BeginAnimation(Border.PaddingProperty, null);
                bd.Padding = room;
            }
            _shortcutContainers.Add(c);
        }
    }

    void HideShortcuts()
    {
        foreach (var c in _shortcutContainers)
        {
            if (c.Template?.FindName("Key", c) is Border key)
            {
                key.BeginAnimation(OpacityProperty, null);
                key.ClearValue(OpacityProperty);
                key.Visibility = Visibility.Collapsed;
            }
            if (c.Template?.FindName("Bd", c) is Border bd)
            {
                var now = bd.Padding;
                bd.BeginAnimation(Border.PaddingProperty, null);
                bd.ClearValue(Border.PaddingProperty);
                if (Motion.Enabled && IsShown)
                    bd.BeginAnimation(Border.PaddingProperty, new ThicknessAnimation(now, bd.Padding, TimeSpan.FromMilliseconds(Motion.Fast))
                        { EasingFunction = Motion.In, FillBehavior = FillBehavior.Stop });
            }
        }
        _shortcutContainers.Clear();
    }

    // ---------- Клавиатура ----------

    /// <summary>F6 / Shift+F6: поиск → источник → «⋯» → «+ группа» → кнопки InfoBar. Tab занят переключением вкладок.</summary>
    void CycleFocus(bool back)
    {
        var stops = new List<Control> { Search };
        if (SourceButton.IsVisible) stops.Add(SourceButton);
        if (KindButton.IsVisible) stops.Add(KindButton);
        if (SeriesActive || _mergeTextFilter)
        {
            stops.Add(List);
            if (SeriesActive && _seriesQueue.Count > 0) stops.Add(SeriesQueue);
            if (SeriesActive && SeriesNext.IsEnabled) stops.Add(SeriesNext);
            if (SeriesAll.IsEnabled) stops.Add(SeriesAll);
            stops.Add(SeriesClear);
        }
        stops.Add(MoreButton);
        if (GroupButton.IsVisible) stops.Add(GroupButton);
        if (ViewButton.IsVisible) stops.Add(ViewButton);
        if (InfoBar.IsVisible)
            foreach (var b in new[] { InfoPrimary, InfoSecondary, InfoClose }) if (b.IsVisible) stops.Add(b);
        stops.RemoveAll(c => !c.IsEnabled);
        int i = stops.FindIndex(c => c.IsKeyboardFocusWithin);
        int next = i < 0 ? 0 : (i + (back ? stops.Count - 1 : 1)) % stops.Count;
        stops[next].Focus();
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

    /// <summary>Лежит ли элемент внутри ancestor (или это он сам).</summary>
    public static bool IsWithin(this DependencyObject? d, DependencyObject ancestor)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == ancestor) return true;
        return false;
    }

    public static T? FindAncestor<T>(this DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
