using System.Windows.Automation.Peers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArkCopyBuffer;

/// <summary>
/// Быстрая правка текста прямо в панели: одно поле InlineEdit ложится поверх строки записи.
/// Enter — новая строка, Ctrl+Enter или клик мимо — сохранить, Esc — отменить.
/// Поле одно на все списки, а не в шаблоне строки: строки переиспользуются (Recycling) и теряли бы набранное.
/// </summary>
partial class PanelWindow
{
    ClipItem? _inlineItem;
    ListBox? _inlineList;
    bool _committingInlineEdit;
    readonly DispatcherTimer _inlineHoldTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(500) };
    bool _inlineHoldOpened;
    Action<string, bool> _copyInlineText = (text, sensitive) => Paster.CopyText(text, excludeFromHistory: true, sensitive: sensitive);

    public static readonly DependencyProperty InlineEditOnHoldProperty = DependencyProperty.Register(
        nameof(InlineEditOnHold), typeof(bool), typeof(PanelWindow), new PropertyMetadata(false,
            (d, e) => ((PanelWindow)d).InlineEditOnHoldChanged?.Invoke((bool)e.NewValue)));

    public bool InlineEditOnHold
    {
        get => (bool)GetValue(InlineEditOnHoldProperty);
        set => SetValue(InlineEditOnHoldProperty, value);
    }
    public event Action<bool>? InlineEditOnHoldChanged;

    static bool InlineEditable(ClipItem item) => !item.IsImage && !item.IsMedia && !item.IsNodeGraph;

    bool CanBeginInlineEdit(ClipItem item) => InlineEditable(item) && _textEditor == null
        && !SeriesActive && !_collectingMerge && !_stackOpen;

    void InitializeInlineEdit()
    {
        _inlineHoldTimer.Tick += (_, _) => OpenInlineEditOnHold();
        PreviewMouseLeftButtonDown += (_, _) =>
        {
            _inlineHoldOpened = false;
            _inlineHoldTimer.Stop();
        };
        PreviewMouseLeftButtonUp += (_, _) => _inlineHoldTimer.Stop();
        Closed += (_, _) => _inlineHoldTimer.Stop();
    }

    void StartInlineEditHold(ClipItem item)
    {
        if (InlineEditOnHold && Keyboard.Modifiers == ModifierKeys.None && CanBeginInlineEdit(item)) _inlineHoldTimer.Start();
    }

    void OpenInlineEditOnHold()
    {
        _inlineHoldTimer.Stop();
        if (!InlineEditOnHold || !IsShown || SettingsFlyout.IsVisible || _dragging || Mouse.LeftButton != MouseButtonState.Pressed
            || Keyboard.Modifiers != ModifierKeys.None || _dragItem is not { } item || !CanBeginInlineEdit(item)) return;
        var list = Active;
        if ((list.InputHitTest(Mouse.GetPosition(list)) as DependencyObject).FindAncestor<ListBoxItem>()?.DataContext != item) return;
        BeginInlineEdit(item);
        if (_inlineItem != item) return;
        _inlineHoldOpened = true;
        _dragStart = null;
        _dragItem = null;
    }

    void InlineEdit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is { } item) BeginInlineEdit(item);
    }

    internal void BeginInlineEdit(ClipItem item)
    {
        EndInlineEdit(save: true);
        if (_inlineItem != null || !CanBeginInlineEdit(item)) return;
        var list = Active;
        list.ScrollIntoView(item);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem) return;
        CloseValueDrop();
        CancelZoom();
        ClosePeek();
        _inlineItem = item;
        _inlineList = list;
        InlineEditor.FontFamily = (FontFamily)FindResource(item.IsMono ? "MonoFont" : "TextFont");
        InlineEditor.MaxLength = _store.Storage.MaxChars; // лимит хранилища: длиннее UpdateText не примет, и набранное пропало бы
        InlineEditor.Text = item.Text;
        InlineEditor.IsUndoEnabled = false; // Ctrl+Z не должен возвращать текст прошлой записи
        InlineEditor.IsUndoEnabled = true;
        list.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)InlineEdit_Scrolled);
        InlineEdit.Visibility = Visibility.Visible;
        PlaceInlineEdit();
        InlineEditor.Focus();
        InlineEditor.CaretIndex = InlineEditor.Text.Length;
    }

    // ponytail: поле следует за строкой только по ScrollChanged (прокрутка, новая запись сверху); другой пересчёт вёрстки — подписаться на LayoutUpdated.
    void InlineEdit_Scrolled(object sender, ScrollChangedEventArgs e) => PlaceInlineEdit();

    /// <summary>Поле встаёт на строку записи и растёт вниз; строка ушла из вида или сменилась вкладка — правка сохраняется.</summary>
    void PlaceInlineEdit()
    {
        if (_inlineItem == null || _inlineList is not { } list) return;
        if (list != Active || list.ItemContainerGenerator.ContainerFromItem(_inlineItem) is not ListBoxItem row || !row.IsVisible)
        {
            EndInlineEdit(save: true);
            return;
        }
        var at = row.TranslatePoint(default, list);
        if (at.Y + row.ActualHeight <= 0 || at.Y >= list.ActualHeight) { EndInlineEdit(save: true); return; }
        // У нижнего края поле поднимается, чтобы оставалось место на несколько строк.
        double top = Math.Max(0, Math.Min(at.Y, list.ActualHeight - Math.Min(160, list.ActualHeight)));
        bool tile = row.ActualWidth < list.ActualWidth / 2; // плитка закреплённых узкая — поле занимает всю ширину списка
        double left = tile ? 3 : at.X, right = tile ? 8 : Math.Max(0, list.ActualWidth - at.X - row.ActualWidth);
        InlineEdit.Margin = new Thickness(list.Margin.Left + left, list.Margin.Top + top, list.Margin.Right + right, 0);
        InlineEdit.MinHeight = Math.Min(row.ActualHeight, 120);
        InlineEdit.MaxHeight = Math.Max(InlineEdit.MinHeight, list.ActualHeight - top);
    }

    /// <summary>Клавиши поля раньше общих клавиш панели: иначе Enter вставлял бы запись, а стрелки ходили по списку.</summary>
    bool HandleInlineEditKey(Key key, KeyEventArgs e)
    {
        if (_inlineItem == null) return false;
        if (key == Key.Escape && !SettingsFlyout.IsVisible)
        {
            EndInlineEdit(save: false);
            e.Handled = true;
            return true;
        }
        if (!InlineEdit.IsKeyboardFocusWithin) return false;
        if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { EndInlineEdit(save: true); e.Handled = true; }
        return true;
    }

    void InlineEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not DependencyObject target || target.IsWithin(InlineEdit) || target.FindAncestor<ContextMenu>() != null) return;
        EndInlineEdit(save: true);
    }

    void InlineSave_Click(object sender, RoutedEventArgs e) => EndInlineEdit(save: true);

    void ShowInlineError(string message)
    {
        InlineError.Text = message;
        InlineError.Visibility = Visibility.Visible;
        UIElementAutomationPeer.CreatePeerForElement(InlineError)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    void InlineCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_inlineItem is not { } item || InlineEditor.Text is not { Length: > 0 } draft) return;
        try { _copyInlineText(PasteReplacementRule.For(item, textOverride: draft) ?? draft, item.IsSensitive); }
        catch (PasteReplacement.LimitExceededException)
        {
            ShowInlineError(UiText.T("После автозамены текст слишком длинный."));
            return;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowInlineError(UiText.Get("SE55E27836BAFB755"));
            return;
        }
        EndInlineEdit(save: _store.Items.Contains(item));
    }

    internal void EndInlineEdit(bool save)
    {
        if (_inlineItem is not { } item || _committingInlineEdit) return;
        string text = InlineEditor.Text;
        // Пустое поле — не правка: удаление записи остаётся явным действием (Del).
        bool changed = save && text.Length > 0 && text != item.Text;
        if (changed)
        {
            _committingInlineEdit = true; // Changed может пересобрать список и вызвать ScrollChanged
            try { _store.UpdateText(item, text); }
            catch (Exception ex)
            {
                App.Log(ex);
                ShowInlineError(ex.Message);
                return;
            }
            finally { _committingInlineEdit = false; }
        }
        _inlineItem = null;
        _inlineList?.RemoveHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)InlineEdit_Scrolled);
        _inlineList = null;
        if (InlineEdit.IsKeyboardFocusWithin) Search.Focus();
        InlineEdit.Visibility = Visibility.Collapsed;
        InlineEditor.Text = ""; // текст (возможно, секрет) не остаётся в скрытом поле
        InlineError.Text = "";
        InlineError.Visibility = Visibility.Collapsed;
        if (changed) _ = SaveInlineEditAsync();
        StartOutsideTimer();
    }

    async Task SaveInlineEditAsync()
    {
        try { await _store.SaveAsync(); }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowInfo(InfoKind.Critical, UiText.T("Не сохранено на диск"), ex.Message);
        }
    }

    internal static void SelfTestInlineEdit(string directory, Action<bool, string> check)
    {
        var store = new HistoryStore(System.IO.Path.Combine(directory, "inline-edit", "history.dat"));
        var item = new ClipItem { Text = "arcade" };
        var image = new ClipItem { ImageHash = "IMAGE" };
        store.Items.Add(item); store.Items.Add(image);
        var panel = new PanelWindow(store, new SymbolCatalog(System.IO.Path.Combine(directory, "inline-edit", "symbols.json")));
        try
        {
            void EnterDraft(string text)
            {
                panel._inlineItem = item;
                panel.InlineEdit.Visibility = Visibility.Visible;
                panel.InlineEditor.Text = text;
            }
            EnterDraft("arcade\nsecond line");
            panel.InlineSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(item.Text == "arcade\nsecond line" && panel._inlineItem == null && panel.InlineEdit.Visibility == Visibility.Collapsed
                && panel.InlineEditor.Text.Length == 0, "быстрая правка: кнопка сохраняет новую строку, закрывает и очищает поле");
            EnterDraft("dropped");
            panel.EndInlineEdit(save: false);
            check(item.Text == "arcade\nsecond line", "быстрая правка: Esc не меняет запись");
            EnterDraft("");
            panel.InlineSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(item.Text == "arcade\nsecond line", "быстрая правка: пустое поле не стирает запись");
            panel.BeginInlineEdit(image);
            check(panel._inlineItem == null && InlineEditable(item) && !InlineEditable(image), "быстрая правка: только текстовые записи");

            item.IsSensitive = true;
            panel.PasteReplacementRule = new(true, "arcade", "buffalo");
            EnterDraft("arcade\nedited");
            panel.InlineEditor.Select(0, 3);
            panel._copyInlineText = (_, _) => throw new System.Runtime.InteropServices.ExternalException("synthetic clipboard busy");
            panel.InlineCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(panel._inlineItem == item && panel.InlineEditor.Text == "arcade\nedited" && item.Text == "arcade\nsecond line"
                && panel.InlineError.Visibility == Visibility.Visible && panel.InfoBar.Visibility == Visibility.Collapsed,
                "быстрая правка: занятый буфер показывает ошибку внутри редактора и оставляет черновик открытым для повтора");

            string? copied = null;
            bool sensitive = false;
            panel._copyInlineText = (text, marked) => { copied = text; sensitive = marked; };
            panel.InlineCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(copied == "arcade\nedited" && sensitive && store.Items.Count == 2 && item.Text == copied && panel._inlineItem == null
                && panel.InlineError.Visibility == Visibility.Collapsed,
                "быстрая правка: копирование берёт весь текст, сохраняет его в той же записи и передаёт отметку конфиденциальности");

            item.IsSensitive = false;
            panel.PasteReplacementRule = new(true, "-", "вн");
            EnterDraft("ЯС42-000839-2");
            panel.InlineCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(copied == "ЯС42вн000839вн2" && item.Text == "ЯС42-000839-2" && store.Items.Count == 2 && panel._inlineItem == null,
                "быстрая правка: правило применяется ко всем совпадениям черновика в буфере, запись хранит набранный текст");

            panel.PasteReplacementRule = new(true, "-", new string('x', PasteReplacement.MaxRuleLength));
            string oversizedDraft = new('-', PasteReplacement.MaxExpandedText / PasteReplacement.MaxRuleLength + 1);
            EnterDraft(oversizedDraft);
            panel.InlineCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(copied == "ЯС42вн000839вн2" && item.Text == "ЯС42-000839-2" && panel.InlineEditor.Text == oversizedDraft
                && panel._inlineItem == item && panel.InlineError.Text == UiText.T("После автозамены текст слишком длинный."),
                "быстрая правка: слишком длинная автозамена оставляет буфер, запись и черновик в прежнем состоянии");
            panel.EndInlineEdit(save: false);

            panel.StartInlineEditHold(item);
            check(!panel._inlineHoldTimer.IsEnabled, "быстрая правка: при выключенном удержании жест не запускается");
            panel.InlineEditOnHold = true;
            panel.StartInlineEditHold(image);
            check(!panel._inlineHoldTimer.IsEnabled, "быстрая правка: удержание картинки не запускает правку");

            EnterDraft("removed entry draft");
            store.Items.Remove(item);
            panel.InlineSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var keySource = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            check(panel._inlineItem == item && panel.InlineError.Visibility == Visibility.Visible && !panel.InlineEdit.IsKeyboardFocusWithin
                && panel.HandleInlineEditKey(Key.Escape, escape) && escape.Handled && panel._inlineItem == null,
                "быстрая правка: Esc закрывает черновик после ошибки сохранения, даже если фокус вне редактора");

            EnterDraft("removed entry draft");
            panel.InlineCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(copied == "removed entry draft" && store.Items.Count == 1 && panel._inlineItem == null,
                "быстрая правка: копирование сохраняет черновик удалённой записи в буфере и закрывает поле");
        }
        finally { panel.Close(); }
    }
}
