using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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

    static bool InlineEditable(ClipItem item) => !item.IsImage && !item.IsMedia && !item.IsNodeGraph;

    void InlineEdit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is { } item) BeginInlineEdit(item);
    }

    internal void BeginInlineEdit(ClipItem item)
    {
        EndInlineEdit(save: true);
        if (!InlineEditable(item) || _textEditor != null || SeriesActive || _collectingMerge || _stackOpen) return;
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
        if (_inlineItem == null || !InlineEditor.IsKeyboardFocusWithin) return false;
        if (key == Key.Escape) { EndInlineEdit(save: false); e.Handled = true; }
        else if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { EndInlineEdit(save: true); e.Handled = true; }
        return true;
    }

    void InlineEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Ушли в другое приложение (NewFocus == null) или в меню поля — правка продолжается.
        if (e.NewFocus is not DependencyObject target || target is ContextMenu || target.FindAncestor<ContextMenu>() != null) return;
        EndInlineEdit(save: true);
    }

    internal void EndInlineEdit(bool save)
    {
        if (_inlineItem is not { } item) return;
        _inlineItem = null; // первым: Search.Focus() ниже снова приведёт сюда через LostFocus
        _inlineList?.RemoveHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)InlineEdit_Scrolled);
        _inlineList = null;
        string text = InlineEditor.Text;
        InlineEdit.Visibility = Visibility.Collapsed;
        if (InlineEditor.IsKeyboardFocusWithin) Search.Focus();
        InlineEditor.Text = ""; // текст (возможно, секрет) не остаётся в скрытом поле
        // Пустое поле — не правка: удаление записи остаётся явным действием (Del).
        if (save && text.Length > 0 && text != item.Text) _ = SaveInlineEditAsync(item, text);
        StartOutsideTimer();
    }

    async Task SaveInlineEditAsync(ClipItem item, string text)
    {
        try
        {
            _store.UpdateText(item, text);
            await _store.SaveAsync();
        }
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
            void Edit(string text, bool save)
            {
                panel._inlineItem = item;
                panel.InlineEdit.Visibility = Visibility.Visible;
                panel.InlineEditor.Text = text;
                panel.EndInlineEdit(save);
            }
            Edit("arcade\nsecond line", save: true);
            check(item.Text == "arcade\nsecond line" && panel._inlineItem == null && panel.InlineEdit.Visibility == Visibility.Collapsed
                && panel.InlineEditor.Text.Length == 0, "быстрая правка: новая строка сохраняется, поле закрыто и очищено");
            Edit("dropped", save: false);
            check(item.Text == "arcade\nsecond line", "быстрая правка: Esc не меняет запись");
            Edit("", save: true);
            check(item.Text == "arcade\nsecond line", "быстрая правка: пустое поле не стирает запись");
            panel.BeginInlineEdit(image);
            check(panel._inlineItem == null && InlineEditable(item) && !InlineEditable(image), "быстрая правка: только текстовые записи");
        }
        finally { panel.Close(); }
    }
}
