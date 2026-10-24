using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    ListBoxItem? _valueTarget;
    ClipItem? _valueItem;
    string? _valueOriginal, _valueSource;
    TextSubstitution.Change? _automaticValue;
    bool _chooseAllValues, _valueSaving, _suppressClick;
    readonly DispatcherTimer _externalValueWatch = new() { Interval = TimeSpan.FromMilliseconds(120) };
    (ClipItem Item, string Before, string After, string? Rtf, string? Html)? _valueUndo;
    ClipItem? _parsedItem;
    string? _parsedOriginal, _parsedSource;
    IReadOnlyList<TextSubstitution.Change>? _parsedChanges;
    long _lastDragScroll;
    Action<string> _copyValue = text => Paster.CopyText(text, excludeFromHistory: true);

    internal sealed record ValuePreview(TextSubstitution.Change Change, string Caption, string Before, string Inserted, string After)
    {
        public bool CanApply => Change.CanApply;
        public string Description => Caption + UiText.T(". Итог: ") + Before + Inserted + After;
    }

    internal static ValuePreview PreviewChange(string original, TextSubstitution.Change change, int number = 0)
    {
        int start = change.Start, end = start + change.Length;
        string inserted = change.Text;
        if (change.Quote != '\0' && start > 0 && end < original.Length
            && original[start - 1] == change.Quote && original[end] == change.Quote)
        {
            start--; end++;
            inserted = change.Quote + inserted + change.Quote;
        }
        string before = original[Math.Max(0, start - 96)..start];
        string after = original[end..Math.Min(original.Length, end + 80)];
        if (start > 96) before = "…" + before;
        if (original.Length - end > 80) after += "…";
        // Only the view is shortened; the drop always applies the complete Change.
        if (inserted.Length > 240) inserted = inserted[..112] + "…" + inserted[^112..];
        string label = change.Label.Length == 0 ? UiText.T("Пустые кавычки") : UiText.F($"Заменить «{Short(change.Label)}»");
        return new(change, (number > 0 ? number + " · " : "") + label, Flat(before), Flat(inserted), Flat(after));

        static string Flat(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        static string Short(string text) => Flat(text.Length > 52 ? text[..24] + "…" + text[^24..] : text);
    }

    IReadOnlyList<TextSubstitution.Change> ValueChanges(ClipItem item, string source)
    {
        if (_parsedItem == item && _parsedOriginal == item.Text && _parsedSource == source && _parsedChanges != null) return _parsedChanges;
        _parsedItem = item; _parsedOriginal = item.Text; _parsedSource = source;
        return _parsedChanges = TextSubstitution.Find(item.Text, source);
    }

    string? ReadValue(IDataObject data)
    {
        if (data.GetData(typeof(ClipItem)) is ClipItem item)
            return _store.Items.Contains(item) && (!item.IsImage && !item.IsMedia || item.IsFolder) ? item.Text : null;
        if (data.GetDataPresent(DataFormats.FileDrop))
            return data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths ? paths[0] : null;
        return data.GetData(DataFormats.UnicodeText) as string;
    }

    bool PreviewValue(ListBoxItem? target, IDataObject data, bool fromPaste = false)
    {
        if (_valueSaving || _textEditor != null || target?.DataContext is not ClipItem item || item.IsImage || item.IsMedia
            || !_store.Items.Contains(item) || ReferenceEquals(data.GetData(typeof(ClipItem)), item)) return false;
        string? source = ReadValue(data);
        // ponytail: parse at most 20k characters on the UI thread; larger documents use the editor.
        if (source == null || source.Length > 20000 || item.Text.Length > 20000) return false;
        bool chooseAll = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_valueTarget == target && _valueOriginal == item.Text && _valueSource == source && _chooseAllValues == chooseAll)
            return ValueChoices.Items.Count > 0;
        var changes = ValueChanges(item, source);
        if (!changes.Any(c => c.CanApply)) return false;
        CloseValueDrop();
        CancelZoom();
        StopGif();
        _valueTarget = target;
        _valueItem = item;
        _valueOriginal = item.Text;
        _valueSource = source;
        _chooseAllValues = chooseAll;
        ShowValueChoices(item.Text, source, changes, chooseAll, fromPaste);
        target.Tag = "Replace";
        double top = target.TranslatePoint(new Point(), Root).Y;
        ValueDrop.PlacementTarget = Root;
        ValueDrop.CustomPopupPlacementCallback = (popup, size, _) =>
            SidePlacements(popup, size, (top - 4) * size.Width / Root.ActualWidth);
        ValueDrop.IsOpen = true;
        if (!fromPaste && data.GetData(typeof(ClipItem)) is not ClipItem) _externalValueWatch.Start();
        Motion.Enter(ValueDropCard, dy: 4);
        return true;
    }

    void ShowValueChoices(string original, string source, IReadOnlyList<TextSubstitution.Change> changes, bool chooseAll = false, bool fromPaste = false)
    {
        var slots = changes.Where(c => c.Priority < 3).ToArray();
        _automaticValue = chooseAll || slots.Length > 1 ? null : TextSubstitution.Automatic(changes);
        string value = TextSubstitution.IsPath(source) ? UiText.T("путь") : UiText.T("значение");
        ValueDropTitle.Text = _automaticValue != null ? UiText.T("Так будет после замены") : UiText.F($"Куда вставить {value}?");
        ValueDropHint.Text = _automaticValue != null
            ? UiText.T("Зелёным — вставка. Отпустите на карточке. Shift — другие места.")
            : fromPaste ? UiText.T("Выберите результат. Зелёным — вставка.") : UiText.F($"Перенесите {value} на нужный результат. Зелёным — вставка.");
        var choices = _automaticValue != null ? new[] { _automaticValue } : !chooseAll && slots.Length > 0 ? slots : changes.ToArray();
        ValueChoices.ItemsSource = choices.Select((c, i) => PreviewChange(original, c, choices.Length > 1 ? i + 1 : 0)).ToArray();
        ValueScroll.ScrollToTop();
    }

    void CloseValueDrop()
    {
        _externalValueWatch.Stop();
        if (_valueTarget?.Tag as string == "Replace") _valueTarget.Tag = null;
        _valueTarget = null;
        _valueItem = null;
        _valueOriginal = _valueSource = null;
        _automaticValue = null;
        ValueDrop.IsOpen = false;
        ValueChoices.ItemsSource = null;
    }

    void Values_DragOver(object sender, DragEventArgs e)
    {
        var target = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        if (PreviewValue(target, e.Data)) e.Effects = _automaticValue != null ? DragDropEffects.Copy : DragDropEffects.None;
        else { CloseValueDrop(); e.Effects = DragDropEffects.None; }
        SetDragVisualStatus(e.Effects == DragDropEffects.Copy, false, _valueTarget != null && _automaticValue == null);
        ScrollDrag((ListBox)sender, e);
        e.Handled = true;
    }

    void Values_Drop(object sender, DragEventArgs e)
    {
        var target = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
        if (target == _valueTarget && _automaticValue != null) CommitValue(_automaticValue);
        else CloseValueDrop();
        e.Handled = true;
    }

    void ValueChoice_DragOver(object sender, DragEventArgs e)
    {
        bool valid = sender is Button { DataContext: ValuePreview { CanApply: true } } && _valueItem?.Text == _valueOriginal
            && ReadValue(e.Data) == _valueSource;
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        if (valid && sender is Button button)
        {
            if (button.Tag as string != "Ready")
            {
                button.Tag = "Ready";
                Motion.To(Motion.Shift(button), TranslateTransform.XProperty, 2, Motion.Fast);
            }
            if (!button.IsKeyboardFocused) button.Focus();
        }
        SetDragVisualStatus(valid, false);
        e.Handled = true;
    }

    void ValueChoice_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            button.Tag = null;
            Motion.To(Motion.Shift(button), TranslateTransform.XProperty, 0, Motion.Fast);
        }
    }

    void ValueChoice_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button { DataContext: ValuePreview { CanApply: true } preview } && ReadValue(e.Data) == _valueSource) CommitValue(preview.Change);
        else CloseValueDrop();
        e.Handled = true;
    }

    void ValueChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ValuePreview preview }) CommitValue(preview.Change);
    }

    async void CommitValue(TextSubstitution.Change change)
    {
        var item = _valueItem;
        var target = _valueTarget;
        string? before = _valueOriginal;
        CloseValueDrop();
        Search.Focus();
        if (_valueSaving || item == null || before == null) return;
        if (item.Text != before || !_store.Items.Contains(item))
        {
            ShowInfo(InfoKind.Caution, UiText.T("Запись изменилась"), UiText.T("Перетащите значение ещё раз."));
            return;
        }
        try
        {
            string after = change.Apply(before);
            var undo = (item, before, after, item.Rtf, item.Html);
            Point? destination = target != null && PresentationSource.FromVisual(target) != null
                ? target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), this) : null;
            _store.UpdateText(item, after);
            _valueUndo = undo;
            MergeDragVisual(target, destination);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (IsShown && Active.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem card) AnimateValueInsertion(card);
            });
            await SaveValueAsync(UiText.T("Значение заменено"), CopyValue(after));
        }
        catch (Exception ex) { App.Log(ex); ShowInfo(InfoKind.Critical, UiText.T("Замена не выполнена"), ex.Message); }
    }

    void AnimateValueInsertion(ListBoxItem card)
    {
        card.ApplyTemplate();
        Motion.Pop(card, 1.04, 1);
        if (card.Template.FindName("Glow", card) is Border glow)
        {
            glow.SetResourceReference(Border.BackgroundProperty, "DropValueText");
            Motion.To(glow, OpacityProperty, 0, Motion.Slow * 2, from: 0.32,
                done: () => glow.SetResourceReference(Border.BackgroundProperty, "Accent"));
        }
    }

    string? CopyValue(string text)
    {
        try { _copyValue(text); return null; }
        catch (Exception ex) { App.Log(ex); return ex.Message; }
    }

    void ShowValueResult(string title, string? copyError = null)
    {
        _transient = copyError == null
            ? new Info(InfoKind.Success, title, "", Closable: false, Compact: true)
            : new Info(InfoKind.Caution, title, UiText.T("Не удалось скопировать в буфер. ") + copyError, (UiText.T("Скопировать"), RetryValueCopy));
        RefreshInfo();
    }

    void RetryValueCopy()
    {
        if (_valueUndo is not { } undo || !_store.Items.Contains(undo.Item) || undo.Item.Text != undo.After)
        {
            ShowInfo(InfoKind.Caution, UiText.T("Запись изменилась"), UiText.T("Скопируйте её заново из истории."));
            return;
        }
        ShowValueResult(UiText.T("Значение заменено"), CopyValue(undo.After));
    }

    async Task SaveValueAsync(string title, string? copyError = null)
    {
        _valueSaving = true;
        try
        {
            await _store.SaveAsync();
            await _mergeAnimation;
            ShowValueResult(title, copyError);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _transient = new Info(InfoKind.Critical, UiText.T("Не сохранено на диск"), ex.Message,
                (UiText.T("Повторить"), async () => await SaveValueAsync(title, copyError)), _valueUndo != null ? (UiText.T("Отменить"), UndoValue) : null);
        }
        finally { _valueSaving = false; RefreshInfo(); }
    }

    async void UndoValue()
    {
        if (_valueSaving || _valueUndo is not { } undo) return;
        if (!_store.Items.Contains(undo.Item) || undo.Item.Text != undo.After)
        {
            _valueUndo = null;
            ShowInfo(InfoKind.Caution, UiText.T("Отмена недоступна"), UiText.T("Запись уже изменена или удалена."));
            return;
        }
        try
        {
            _store.UpdateText(undo.Item, undo.Before);
            undo.Item.Rtf = undo.Rtf;
            undo.Item.Html = undo.Html;
            _valueUndo = null;
            await SaveValueAsync(UiText.T("Замена отменена"));
        }
        catch (Exception ex) { App.Log(ex); ShowInfo(InfoKind.Critical, UiText.T("Отмена не выполнена"), ex.Message); }
    }

    void PasteValue()
    {
        if (Active.SelectedItem is not ClipItem item) return;
        try
        {
            Active.ScrollIntoView(item);
            Active.UpdateLayout();
            var target = Active.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
            if (Clipboard.GetDataObject() is { } data && PreviewValue(target, data, fromPaste: true))
            {
                if (_automaticValue != null) CommitValue(_automaticValue);
                else Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ValueDropCard.MoveFocus(new(FocusNavigationDirection.First)));
            }
            else ShowInfo(InfoKind.Info, UiText.T("Не найдено значение для замены"), UiText.T("Используйте маркер <значение>, {{значение}} или аргумент команды. Можно открыть редактор: Ctrl+E."));
        }
        catch (Exception ex) { App.Log(ex); ShowInfo(InfoKind.Critical, UiText.T("Не удалось прочитать буфер"), ex.Message); }
    }

    void ScrollDrag(ListBox list, DragEventArgs e)
    {
        DragSupport.ScrollEdge(list, e.GetPosition(list), ref _lastDragScroll);
    }

    void CheckValuePreview(Action<bool, string> check)
    {
        const string command = @".\build\mpv.exe --config-dir=portable_config --log-file=.tmp\osc-time.log ""fsd""";
        const string path = @"D:\Shadow Play\OBS\deadlock\deadlock_05.10.2026_17-24-31.mp4";
        var change = TextSubstitution.Automatic(TextSubstitution.Find(command, path))!;
        var preview = PreviewChange(command, change);
        check(preview.Inserted == '"' + path + '"' && preview.Before + preview.Inserted + preview.After == change.Apply(command),
            "preview: команда mpv полностью совпадает с результатом, путь и обе кавычки выделены вместе");
        foreach (string text in new[] { "mpv \"\"", "mpv '<clip>'", "mpv <clip>", "run --output=\"old\"" })
        {
            var option = TextSubstitution.Find(text, path).Single();
            var rendered = PreviewChange(text, option);
            check(rendered.Before + rendered.Inserted + rendered.After == option.Apply(text)
                && rendered.Inserted == (text.Contains('\'') ? '\'' + path + '\'' : '"' + path + '"'),
                "preview: кавычки и контекст сохранены для " + text);
        }
        string large = "mpv --before=" + new string('a', 500) + " \"fsd\" --after=" + new string('b', 500);
        var largeChange = TextSubstitution.Automatic(TextSubstitution.Find(large, path))!;
        var cropped = PreviewChange(large, largeChange);
        check(cropped.Before.StartsWith('…') && cropped.After.EndsWith('…') && cropped.Before.Length == 97 && cropped.After.Length == 81
            && cropped.Inserted == '"' + path + '"' && largeChange.Apply(large).Length == large.Length - 3 + path.Length,
            "preview: большая команда сокращается вокруг вставки без изменения сохраняемого текста");
        string longPath = "D:/" + new string('x', 500) + "/clip.mp4";
        var longChange = TextSubstitution.Automatic(TextSubstitution.Find("mpv \"\"", longPath))!;
        var longPreview = PreviewChange("mpv \"\"", longChange);
        check(longPreview.Inserted.Length <= 240 && longPreview.Inserted.StartsWith('"') && longPreview.Inserted.EndsWith('"')
            && longChange.Apply("mpv \"\"") == "mpv \"" + longPath + "\"", "preview: огромный путь ограничен только в отображении");

        ShowValueChoices(command, path, TextSubstitution.Find(command, path));
        Render("value-preview.png");
        var presenter = (ContentPresenter)ValueChoices.ItemContainerGenerator.ContainerFromIndex(0);
        var textBlock = (TextBlock)ValueChoices.ItemTemplate.FindName("CommandPreview", presenter);
        var runs = textBlock.Inlines.OfType<Run>().ToArray();
        check(runs.Length == 3 && runs[1].Text == preview.Inserted && runs[1].Foreground == (Brush)FindResource("DropValueText")
            && textBlock.ActualHeight > 30 && ValueDropCard.ActualWidth == 380,
            "preview layout: итог связан с тремя Run, вставка зелёная, переносы видны; PNG создан без окна и фокуса");
        const string two = "mpv --config-dir=portable_config \"\" \"\"";
        ShowValueChoices(two, path, TextSubstitution.Find(two, path));
        var choices = ValueChoices.Items.Cast<ValuePreview>().ToArray();
        check(_automaticValue == null && choices.Length == 2 && choices[0].Change.Apply(two) == "mpv --config-dir=portable_config \"" + path + "\" \"\""
            && choices[1].Change.Apply(two) == "mpv --config-dir=portable_config \"\" \"" + path + "\""
            && choices[0].Caption.StartsWith("1 · ") && choices[1].Caption.StartsWith("2 · "),
            "preview: две пары кавычек требуют выбора и показывают разные результаты без посторонних параметров");
        Render("value-preview-choices.png");
        var secondPresenter = (ContentPresenter)ValueChoices.ItemContainerGenerator.ContainerFromIndex(1);
        var button = (Button)ValueChoices.ItemTemplate.FindName("ValueChoice", secondPresenter);
        button.Tag = "Ready";
        Render("value-preview-ready.png");
        var frame = (Border)button.Template.FindName("Frame", button);
        check(frame.Background == (Brush)FindResource("DropValueFill") && frame.BorderBrush == (Brush)FindResource("DropValueText"),
            "preview: готовый к приёму вариант получает зелёные фон и рамку без перехвата фокуса");
        ShowValueChoices(two, path, TextSubstitution.Find(two, path), chooseAll: true);
        check(ValueChoices.Items.Count == 3 && _automaticValue == null, "preview: Shift возвращает все варианты включая именованные параметры");
        const string mixed = "mpv \"D:/old.mp4\" \"fsd\"";
        ShowValueChoices(mixed, path, TextSubstitution.Find(mixed, path));
        check(ValueChoices.Items.Count == 2 && _automaticValue == null, "preview: два разных аргумента в кавычках тоже требуют выбора");
        CloseValueDrop();

        void Render(string name)
        {
            ValueDropCard.Measure(new Size(396, double.PositiveInfinity));
            ValueDropCard.Arrange(new Rect(new Point(), ValueDropCard.DesiredSize));
            ValueDropCard.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(396, (int)Math.Ceiling(ValueDropCard.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(ValueDropCard);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(file);
        }
    }

    internal static void SelfTestSubstitution(string directory, Action<bool, string> check)
    {
        var store = new HistoryStore(System.IO.Path.Combine(directory, "substitution", "history.dat"));
        const string original = "mpv \"<clip>\"";
        const string path = @"D:\Test Clips\clip.mp4";
        var now = DateTime.UtcNow;
        store.Add(original, "rtf", "html", now, "test");
        var item = store.Items.Single();
        store.TogglePin(item);
        var group = store.AddGroup("Test")!;
        store.MovePinned(item, group.Id);
        store.SetTileColor(item, 3);
        var panel = new PanelWindow(store, new SymbolCatalog(System.IO.Path.Combine(directory, "substitution-symbols.json")));
        string? copied = null;
        panel._copyValue = text => copied = text; // Never alter the user's clipboard in self-tests.
        try
        {
            panel.CheckValuePreview(check);
            foreach (bool preferLeft in new[] { false, true })
            {
                var placements = OrderSidePlacements(new Size(400, 300), new Size(360, 500), 72, 4, preferLeft);
                check(placements[0].Point == new Point(preferLeft ? -396 : 356, 72)
                    && placements[1].Point == new Point(preferLeft ? 356 : -396, 72)
                    && placements.All(p => p.PrimaryAxis == PopupPrimaryAxis.Horizontal),
                    $"всплывающие окна: {(preferLeft ? "слева" : "справа")} сначала, другая сторона запасная");
            }
            panel.CheckDragVisual(check);
            panel.CheckHoverSuppression(check);
            var source = new DataObject(typeof(ClipItem), item);
            check(panel.ReadValue(source) == original && !panel.PreviewValue(new ListBoxItem { DataContext = item }, source),
                "запись нельзя перетащить саму на себя");
            var files = new DataObject(); files.SetData(DataFormats.FileDrop, new[] { path });
            check(panel.ReadValue(files) == path, "один файл из Проводника используется как путь");
            files.SetData(DataFormats.FileDrop, new[] { path, @"D:\other.mp4" });
            files.SetData(DataFormats.UnicodeText, "текст рядом");
            check(panel.ReadValue(files) == null, "несколько файлов не подменяются случайным текстом");
            panel._valueItem = item; panel._valueOriginal = original;
            panel.CommitValue(TextSubstitution.Automatic(TextSubstitution.Find(original, path))!);
            string changed = "mpv \"" + path + "\"";
            check(copied == changed, "подстановка сразу копирует итог до ожидания записи на диск");
            TextEditorWindow.PumpUntil(() => panel._transient?.Title == UiText.T("Значение заменено"));
            panel.InfoBar.Measure(new Size(330, double.PositiveInfinity));
            panel.InfoBar.Arrange(new Rect(0, 0, 330, panel.InfoBar.DesiredSize.Height));
            panel.InfoBar.UpdateLayout();
            TextEditorWindow.PumpUntil(() => panel.InfoTitle.Text.Contains(UiText.T("Значение заменено")));
            check(panel._transient is { Compact: true, Closable: false, Primary: null, Secondary: null }
                && panel.InfoActions.Visibility == Visibility.Collapsed && panel.InfoBar.ActualHeight <= 32
                && panel._infoTimer.IsEnabled && panel._infoTimer.Interval == TimeSpan.FromSeconds(2),
                "подстановка: компактное сообщение до 32 px без кнопок, таймер на две секунды");
            var noticeBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(330, (int)Math.Ceiling(panel.InfoBar.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            noticeBitmap.Render(panel.InfoBar);
            var noticeEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            noticeEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(noticeBitmap));
            using (var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "value-success.png"))) noticeEncoder.Save(file);
            var clipboard = Paster.TextClipboard(changed, excludeFromHistory: true);
            check(clipboard.GetData(DataFormats.UnicodeText) as string == changed && clipboard.GetDataPresent("Clipboard Viewer Ignore")
                && !Paster.TextClipboard(changed).GetDataPresent("Clipboard Viewer Ignore"),
                "копирование подстановки помечено без повторного захвата истории; обычное копирование сохранено");
            foreach (string style in new[] { "ClipItemContainer", "PinTileContainer", "PinRowContainer" })
            {
                var card = new ListBoxItem { Style = (Style)panel.FindResource(style), Content = new TextBlock { Text = changed } };
                card.Measure(new Size(330, 104)); card.Arrange(new Rect(0, 0, 330, 104));
                panel.AnimateValueInsertion(card);
                var glow = (Border)card.Template.FindName("Glow", card);
                check(glow.HasAnimatedProperties && glow.Background == (Brush)panel.FindResource("DropValueText") && !glow.IsHitTestVisible,
                    "анимация подстановки: зелёная вспышка без перехвата ввода — " + style);
                ResetContainer(card);
                bool motion = Motion.Enabled; Motion.Enabled = false;
                try
                {
                    panel.AnimateValueInsertion(card);
                    check(!glow.HasAnimatedProperties && glow.Opacity == 0 && glow.Background == (Brush)panel.FindResource("Accent"),
                        "отключённая анимация подстановки оставляет карточку без вспышки — " + style);
                }
                finally { Motion.Enabled = motion; ResetContainer(card); }
            }
            check(item.Text == changed && item.CreatedAt == now && item.LastUsedAt == now && item.Pinned
                && item.PinnedGroupId == group.Id && item.TileColor == 3 && item.Rtf == null && item.Html == null,
                "прямая подстановка сохраняет запись, время, закрепление, группу и цвет");
            var restored = new HistoryStore(System.IO.Path.Combine(directory, "substitution", "history.dat"));
            restored.Load(now);
            check(restored.Items.Single(i => i.Id == item.Id).Text == changed, "прямая подстановка сохранена на диск");
            panel.UndoValue();
            TextEditorWindow.PumpUntil(() => panel._transient?.Title == UiText.T("Замена отменена"));
            check(item.Text == original && item.Rtf == "rtf" && item.Html == "html", "отмена восстанавливает текст и форматирование");
            panel._valueItem = item; panel._valueOriginal = original;
            store.UpdateText(item, "другая правка");
            panel.CommitValue(TextSubstitution.Automatic(TextSubstitution.Find(original, path))!);
            check(item.Text == "другая правка" && panel._transient?.Title == UiText.T("Запись изменилась"), "устаревшее перетаскивание не затирает новую правку");
            check(copied == changed && !panel._infoTimer.IsEnabled, "устаревшая подстановка не меняет буфер и останавливает таймер успеха");
            const string twoSlots = "mpv \"\" \"\"";
            store.UpdateText(item, twoSlots);
            panel._valueItem = item; panel._valueOriginal = twoSlots;
            var second = PreviewChange(twoSlots, TextSubstitution.Find(twoSlots, path)[1]);
            panel.ValueChoice_Click(new Button { DataContext = second }, new RoutedEventArgs());
            TextEditorWindow.PumpUntil(() => panel._transient?.Title == UiText.T("Значение заменено"));
            check(item.Text == "mpv \"\" \"" + path + "\"" && copied == item.Text, "preview: выбор второй карточки заменяет вторую пару кавычек и копирует итог");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            TextEditorWindow.PumpUntil(() => panel._transient == null);
            check(panel._transient == null && !panel._infoTimer.IsEnabled && elapsed.ElapsedMilliseconds is >= 1700 and < 3500,
                "сообщение подстановки действительно исчезает через две секунды");
            panel._copyValue = _ => throw new System.Runtime.InteropServices.ExternalException("synthetic clipboard busy");
            string retryOriginal = item.Text;
            panel._valueItem = item; panel._valueOriginal = retryOriginal;
            panel.CommitValue(TextSubstitution.Find(retryOriginal, @"D:\other.mp4").Last());
            TextEditorWindow.PumpUntil(() => panel._transient?.Kind == InfoKind.Caution);
            check(panel._transient?.Primary?.Text == UiText.T("Скопировать") && !panel._infoTimer.IsEnabled && item.Text.Contains(@"D:\other.mp4"),
                "занятый буфер не отменяет правку: сообщение с повтором копирования не исчезает автоматически");
            panel._copyValue = text => copied = text;
            panel.RetryValueCopy();
            check(copied == item.Text && panel._transient?.Compact == true && panel._infoTimer.IsEnabled,
                "повтор копирует сохранённый итог и показывает короткое подтверждение");
        }
        finally { panel.CloseValueDrop(); panel.Close(); }

        string failedPath = System.IO.Path.Combine(directory, "substitution-error", "history.dat");
        System.IO.Directory.CreateDirectory(failedPath);
        var failedStore = new HistoryStore(failedPath);
        failedStore.Add(original, null, null, now);
        var failedItem = failedStore.Items.Single();
        var failedPanel = new PanelWindow(failedStore, new SymbolCatalog(System.IO.Path.Combine(directory, "failed-substitution-symbols.json")));
        failedPanel._copyValue = text => copied = text;
        try
        {
            failedPanel._valueItem = failedItem; failedPanel._valueOriginal = original;
            failedPanel.CommitValue(TextSubstitution.Automatic(TextSubstitution.Find(original, path))!);
            TextEditorWindow.PumpUntil(() => failedPanel._transient?.Title == UiText.T("Не сохранено на диск"));
            check(failedPanel._transient?.Title == UiText.T("Не сохранено на диск") && failedPanel._valueUndo != null
                && failedPanel._transient.Primary?.Text == UiText.T("Повторить") && failedPanel._transient.Secondary?.Text == UiText.T("Отменить"),
                "ошибка диска оставляет подстановку в памяти с повтором записи и отменой");
        }
        finally { failedPanel.CloseValueDrop(); failedPanel.Close(); }
    }
}
