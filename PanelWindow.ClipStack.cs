using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using static ArkCopyBuffer.Native;

namespace ArkCopyBuffer;

/// <summary>Как вставить стопку записей: подряд идущие тексты одним блоком, картинки и видео одной пачкой, остальное по одной.</summary>
internal static class ClipStack
{
    /// <summary>Браузер читает буфер не сразу после Ctrl+V: следующая вставка не должна подменить содержимое раньше времени.</summary>
    internal const int PasteGapMs = 300;

    internal readonly record struct Step(IReadOnlyList<ClipItem> Items, string? Text);

    static bool IsText(ClipItem item) => !item.IsImage && !item.IsMedia && !item.IsNodeGraph;

    internal static List<Step> Plan(IReadOnlyList<ClipItem> items)
    {
        var steps = new List<Step>();
        for (int i = 0, end; i < items.Count; i = end)
        {
            bool text = IsText(items[i]), visual = items[i].IsVisualMedia;
            for (end = i + 1; end < items.Count && (text && IsText(items[end]) || visual && items[end].IsVisualMedia); end++) { }
            var run = items.Skip(i).Take(end - i).ToArray();
            // Одна запись идёт обычным путём: сохраняет форматирование и поднимается в истории.
            steps.Add(new(run, text && run.Length > 1 ? Join(run.Select(item => item.Text)) : null));
        }
        return steps;
    }

    /// <summary>Однострочные (ссылки, команды) столбиком, многострочные через пустую строку: блоки не слипаются.</summary>
    internal static string Join(IEnumerable<string> texts)
    {
        var parts = texts.Select(text => text.Trim('\r', '\n')).ToArray();
        return string.Join(parts.Any(part => part.Contains('\n')) ? "\r\n\r\n" : "\r\n", parts);
    }
}

/// <summary>
/// Стопка: Shift + колесо (или Shift+стрелка) копит записи от той, что под курсором; отпущенный Shift вставляет все разом в порядке копирования.
/// По журналу почти половина вставок идёт сериями в одно окно: открыть панель, клик, снова открыть, снова клик.
/// Первый щелчок задаёт направление до конца жеста, обратный снимает последнюю запись; Esc отменяет.
/// Мышь можно увести на другую запись и продолжить оттуда, клик добавляет или снимает запись.
/// </summary>
partial class PanelWindow
{
    internal event Action<IReadOnlyList<ClipItem>>? StackPasteRequested;
    readonly List<ClipItem> _stack = [];  // в порядке выбора: колесо вверх снимает последнюю
    List<ClipItem> _stackOrder = [];      // в порядке вставки: так же нумеруются карточки
    bool _stackOpen;
    int _stackWheel;
    int _stackDirection = 1;              // 1 — вниз по списку, -1 — вверх. Смена только с новым жестом: перекрут через ноль не вставит лишнего
    Point? _stackPointer;
    // Опрос, а не KeyUp: у закреплённой панели фокус может быть у другого окна, и клавиатурных событий она не получит.
    readonly DispatcherTimer _stackWatch = new() { Interval = TimeSpan.FromMilliseconds(16) };

    void InitializeClipStack()
    {
        PreviewMouseWheel += Stack_Wheel;
        _stackWatch.Tick += (_, _) =>
        {
            if (!IsDown(VK_SHIFT)) CommitStack();
            else if (IsDown(0x1B) && !ForegroundIsOurs()) CloseStack(); // фокус у нас: Esc услышит OnKey
        };
        Closed += (_, _) => _stackWatch.Stop();
    }

    /// <summary>Пароль в стопку не попадает: склеенный блок раскрыл бы его. Остальное вставляется, включая файлы.</summary>
    static bool CanStack(ClipItem item) => !item.IsSensitive;

    void Stack_Wheel(object sender, MouseWheelEventArgs e)
    {
        var pointed = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext as ClipItem;
        if (!_stackOpen)
        {
            // Без записи под курсором это обычная прокрутка списка.
            if (e.Delta == 0 || !IsDown(VK_SHIFT) || IsDown(VK_CONTROL) || IsDown(VK_MENU) || pointed == null || !CanStack(pointed) || !OpenStack()) return;
            _stackDirection = e.Delta < 0 ? 1 : -1;
            _stackWheel = Math.Sign(e.Delta) * Mouse.MouseWheelDeltaForOneLine - e.Delta; // первое касание сразу берёт запись под курсором
        }
        // Колёса с мелким шагом и тачпады шлют доли щелчка: копим до целого, иначе стопка росла бы от касания.
        _stackWheel += e.Delta;
        int notches = _stackWheel / Mouse.MouseWheelDeltaForOneLine;
        _stackWheel -= notches * Mouse.MouseWheelDeltaForOneLine;
        var at = e.GetPosition(this);
        // Запись под курсором берём, только если курсор сдвинул человек: при автопрокрутке под ним оказалась бы случайная.
        bool moved = _stackPointer is not { } last || (at - last).Length > 4;
        _stackPointer = at;
        int grow = -notches * _stackDirection; // колесо вниз даёт отрицательные щелчки
        for (; grow > 0; grow--, moved = false) GrowStack(moved || _stack.Count == 0 ? pointed : null);
        for (; grow < 0; grow++) ShrinkStack();
        e.Handled = true;
    }

    /// <summary>Клавиатура: Shift+↓ или Shift+↑ начинает с выбранной записи; пока стопка открыта, стрелки меняют её, Esc отменяет, остальное глушим.</summary>
    bool HandleStackKey(Key key, KeyEventArgs e)
    {
        if (!_stackOpen)
        {
            if (key is not (Key.Down or Key.Up) || Keyboard.Modifiers != ModifierKeys.Shift || Active.SelectedItem is not ClipItem selected || !CanStack(selected) || !OpenStack()) return false;
            _stackDirection = key == Key.Down ? 1 : -1;
            GrowStack(selected);
        }
        else if (key is Key.Down or Key.Up)
        {
            if ((key == Key.Down ? 1 : -1) != _stackDirection) ShrinkStack();
            else GrowStack(_stack.Count == 0 ? Active.SelectedItem as ClipItem : null);
        }
        else if (key == Key.Escape) CloseStack();
        e.Handled = true;
        return true;
    }

    bool OpenStack()
    {
        if (!IsShown || SeriesActive || _mergeTextFilter || Busy || SettingsFlyout.Visibility == Visibility.Visible || SymbolsTab) return false;
        BeginStack();
        return true;
    }

    void BeginStack()
    {
        ClosePeek(); CancelZoom(); StopGif(); HideShortcuts();
        _outsideTimer.Stop();
        _stackOpen = true;
        _stack.Clear();
        _stackWheel = 0;
        _stackDirection = 1;
        _stackPointer = null;
        KeyHints.Visibility = ShowKeyHints ? Visibility.Hidden : Visibility.Collapsed; // Hidden держит высоту: список под курсором не сдвигается
        StackBar.Visibility = Visibility.Visible;
        RefreshStack();
        _stackWatch.Start();
    }

    void GrowStack(ClipItem? pointed)
    {
        if (_stack.Count >= 50) return; // тот же предел, что у выбора через Alt
        var next = pointed != null && CanStack(pointed) && !_stack.Contains(pointed) ? pointed : NextInStackDirection();
        if (next == null) return;
        _stack.Add(next);
        Active.ScrollIntoView(next);
        RefreshStack();
    }

    /// <summary>Сосед последней записи. Пустая стопка соседа не имеет: начать можно только с записи под курсором.</summary>
    ClipItem? NextInStackDirection()
    {
        if (_stack.Count == 0) return null;
        var items = Active.Items.OfType<ClipItem>().ToList();
        for (int at = items.IndexOf(_stack[^1]) + _stackDirection; at >= 0 && at < items.Count; at += _stackDirection)
            if (CanStack(items[at]) && !_stack.Contains(items[at])) return items[at];
        return null;
    }

    void ShrinkStack()
    {
        if (_stack.Count == 0) return;
        _stack.RemoveAt(_stack.Count - 1);
        RefreshStack();
    }

    void ToggleStack(ClipItem item)
    {
        if (!_stack.Remove(item) && CanStack(item)) _stack.Add(item);
        RefreshStack();
    }

    void RefreshStack()
    {
        // История: новые сверху, вставляем в порядке копирования. Закреплённые расставлены вручную: как видно на экране.
        var ordered = _stack.OrderBy(item => Active.Items.IndexOf(item));
        _stackOrder = (PinnedTab ? ordered : ordered.Reverse()).ToList();
        RefreshMergeMarks();
        StackHint.Text = _stack.Count == 0 ? UiText.T("Отпустите, чтобы закрыть") : UiText.F($"Отпустите, чтобы вставить: {_stack.Count}");
        UIElementAutomationPeer.CreatePeerForElement(StackHint)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    void CommitStack()
    {
        if (!_stackOpen) return;
        var items = _stackOrder.Where(_store.Items.Contains).ToList();
        CloseStack();
        if (items.Count > 0) StackPasteRequested?.Invoke(items);
    }

    void CloseStack()
    {
        _stackWatch.Stop();
        if (!_stackOpen) return;
        _stackOpen = false;
        _stack.Clear();
        _stackOrder = [];
        ClearMergeMarks();
        StackBar.Visibility = Visibility.Collapsed;
        KeyHints.Visibility = ShowKeyHints ? Visibility.Visible : Visibility.Collapsed;
        RefreshHintFocus(Keyboard.Modifiers); // у закреплённой панели отпущенный Shift мог уйти другому окну
        // Пока стопка копилась, Busy держал панель: если за это время мышь или фокус ушли, отсчёт скрытия начинается сейчас.
        StartOutsideTimer();
        OnFocusMaybeLost();
    }

    internal static void SelfTestClipStack(string directory, Action<bool, string> check)
    {
        ClipItem Text(string text) => new() { Text = text };
        ClipItem Image(string hash) => new() { ImageHash = hash, ImageWidth = 1, ImageHeight = 1 };
        var (a, b, c, shot1, shot2) = (Text("a"), Text("b\r\n"), Text("c"), Image("A1"), Image("A2"));
        var plan = ClipStack.Plan([a, b, shot1, shot2, c]);
        check(plan.Count == 3 && plan[0].Text == "a\r\nb" && plan[0].Items.Count == 2 && plan[1].Text == null && plan[1].Items.SequenceEqual([shot1, shot2])
            && plan[2].Text == null && plan[2].Items.Single() == c, "стопка: подряд идущие тексты одним блоком, картинки пачкой, порядок сохраняется");
        check(ClipStack.Plan([a]).Single() is { Text: null } && ClipStack.Plan([shot1]).Single() is { Text: null }, "стопка: одна запись идёт обычным путём, с форматированием");
        check(ClipStack.Join(["one", "two"]) == "one\r\ntwo" && ClipStack.Join(["line 1\nline 2", "two"]) == "line 1\nline 2\r\n\r\ntwo", "стопка: многострочные блоки разделяет пустая строка");
        check(ClipStack.Plan([]).Count == 0, "стопка: пустая стопка ничего не вставляет");

        var store = new HistoryStore(Path.Combine(directory, "clip-stack", "history.dat"));
        var panel = new PanelWindow(store, new SymbolCatalog(Path.Combine(directory, "clip-stack", "symbols.json")));
        bool animations = Motion.Enabled;
        Motion.Enabled = false;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var (text, i) in new[] { "oldest", "second", "third" }.Select((text, i) => (text, i))) store.Add(text, null, null, now.AddSeconds(i));
            store.Add("hunter2", null, null, now.AddSeconds(3), sensitive: true);
            store.Add("newest", null, null, now.AddSeconds(4));
            ClipItem Clip(string text) => store.Items.First(item => item.Text == text);
            IReadOnlyList<ClipItem>? pasted = null;
            int pastes = 0;
            panel.StackPasteRequested += items => { pastes++; pasted = items; };
            string[] Pasted() => pasted?.Select(item => item.Text).ToArray() ?? [];
            void Begin() { panel.BeginStack(); panel._stackWatch.Stop(); } // в проверке Shift не зажат: опрос завершил бы стопку при первой прокачке очереди

            check(!panel.OpenStack(), "стопка: скрытая панель её не начинает");
            Begin();
            check(panel.Busy && panel.StackBar.Visibility == Visibility.Visible && panel.KeyHints.Visibility == Visibility.Hidden
                && panel.StackHint.Text == UiText.T("Отпустите, чтобы закрыть"), "стопка: удерживает панель, подсказка заменяет строку клавиш и держит её высоту");
            panel.GrowStack(Clip("newest"));
            panel.GrowStack(null);
            check(panel._stack.SequenceEqual([Clip("newest"), Clip("third")]), "стопка: следующая запись снизу, пароль пропускается");
            check(panel.StackHint.Text == UiText.F($"Отпустите, чтобы вставить: {2}"), "стопка: подсказка показывает, сколько будет вставлено");
            panel.GrowStack(null);
            panel.ShrinkStack();
            check(panel._stack.Count == 2 && !panel._stack.Contains(Clip("second")), "стопка: щелчок вверх снимает последнюю");
            panel.GrowStack(Clip("oldest"));
            check(panel._stack[^1] == Clip("oldest"), "стопка: увёл курсор на другую запись, она добавляется оттуда");
            panel.GrowStack(Clip("newest"));
            check(panel._stack.Count == 3, "стопка: запись не попадает в стопку дважды");
            panel.ToggleStack(Clip("third"));
            check(!panel._stack.Contains(Clip("third")), "стопка: клик снимает запись");
            panel.ToggleStack(Clip("third"));
            int entries = store.Items.Count;
            panel.CommitStack();
            check(pastes == 1 && Pasted().SequenceEqual(["oldest", "third", "newest"]) && !panel._stackOpen && !panel.Busy && !panel._stackWatch.IsEnabled,
                "стопка: отпускание вставляет всё в порядке копирования");
            check(store.Items.Count == entries && panel.StackBar.Visibility == Visibility.Collapsed && panel._stack.Count == 0, "стопка: история не меняется, подсказка закрыта");

            Begin(); panel.GrowStack(Clip("newest")); panel.ShrinkStack(); panel.CommitStack();
            check(pastes == 1, "стопка: снял всё, ничего не вставляется");
            Begin(); panel.GrowStack(Clip("newest")); panel.CloseStack();
            check(pastes == 1 && !panel._stackOpen, "стопка: отмена ничего не вставляет");
            Begin(); panel.GrowStack(Clip("second")); store.Remove(Clip("second")); panel.CommitStack();
            check(pastes == 1, "стопка: удалённая за это время запись не вставляется");
            Begin(); panel.GrowStack(Clip("hunter2"));
            check(!panel._stack.Contains(Clip("hunter2")), "стопка: пароль не добавляется и напрямую");
            panel.CloseStack();
            check(!CanStack(Clip("hunter2")) && CanStack(Clip("oldest")), "стопка: только пароль закрыт для неё");

            Begin();
            panel.GrowStack(null);
            check(panel._stack.Count == 0, "стопка: без записи под курсором ничего не добавляется, начало списка не подхватывается");
            panel.GrowStack(Clip("third"));
            panel.ShrinkStack();
            panel.GrowStack(null);
            check(panel._stack.Count == 0, "стопка: снял всё и покрутил дальше без записи под курсором, она снова пуста");
            panel.CloseStack();

            Begin();
            panel._stackDirection = -1;
            panel.GrowStack(Clip("third"));
            panel.GrowStack(null);
            check(panel._stack.SequenceEqual([Clip("third"), Clip("newest")]) && panel._stackOrder.SequenceEqual([Clip("third"), Clip("newest")]),
                "стопка: вверх идёт к записям выше, пароль пропускается, порядок вставки тот же, порядок копирования");
            panel.GrowStack(null);
            check(panel._stack.Count == 2, "стопка: вверху списка упор");
            panel.CloseStack();

            Begin();
            void Wheel(int delta)
            {
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                panel.RaiseEvent(wheel);
                check(wheel.Handled, "стопка: пока она открыта, список не прокручивается");
            }
            panel.GrowStack(Clip("newest"));
            Wheel(-40); Wheel(-40);
            check(panel._stack.Count == 1, "стопка: доли щелчка с тачпада ещё ничего не добавляют");
            Wheel(-40);
            check(panel._stack.Count == 2, "стопка: целый щелчок вниз добавляет запись");
            Wheel(120);
            check(panel._stack.Count == 1, "стопка: щелчок вверх убирает");
            Wheel(120);
            Wheel(120);
            check(panel._stack.Count == 0, "стопка: перекрут через ноль не начинает стопку в другую сторону");
            panel.GrowStack(Clip("newest"));
            using var keySource = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero);
            var keyDown = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            check(panel.HandleStackKey(Key.Down, keyDown) && keyDown.Handled && panel._stack.Count == 2, "стопка: стрелка вниз добавляет");
            check(panel.HandleStackKey(Key.Up, keyDown) && panel._stack.Count == 1, "стопка: стрелка вверх снимает");
            check(panel.HandleStackKey(Key.Tab, keyDown) && panel._stackOpen, "стопка: другие клавиши не уводят с выбора");
            check(panel.HandleStackKey(Key.Escape, keyDown) && !panel._stackOpen && pastes == 1, "стопка: Esc закрывает без вставки");
            check(!panel.HandleStackKey(Key.Down, keyDown), "стопка: без Shift стрелка снова двигает выделение");

            panel.BeginStack();
            panel.GrowStack(Clip("third"));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (panel._stackOpen && DateTime.UtcNow < deadline) panel.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            check(pastes == 2 && Pasted().SequenceEqual(["third"]), "стопка: опрос замечает отпущенный Shift и вставляет");

            panel.TabPinned.IsChecked = true;
            store.TogglePin(Clip("oldest")); store.TogglePin(Clip("third"));
            panel.UpdatePinnedLayout();
            var shown = panel.PinnedList.Items.OfType<ClipItem>().ToArray();
            Begin(); panel.GrowStack(shown[1]); panel.GrowStack(shown[0]); panel.CommitStack();
            check(pastes == 3 && pasted!.SequenceEqual(shown[..2]), "стопка: у закреплённых порядок как на экране");
            panel.TabAll.IsChecked = true;

            check(Paster.Quoted("a b") == "\"a b\"" && Paster.Quoted(" a\r\n") == "\"a\"" && Paster.Quoted("\"a\"") == "\"a\"" && Paster.Quoted("\"") == "\"\"\"",
                "кавычки: текст оборачивается, лишние пробелы снимаются, уже взятый в кавычки не удваивается");
            var quoted = new List<ClipItem>();
            var plain = new List<ClipItem>();
            panel.QuotedPasteRequested += quoted.Add;
            panel.PasteRequested += (item, _) => plain.Add(item);
            var imageClip = new ClipItem { ImageHash = "AA", ImageWidth = 1, ImageHeight = 1 };
            var folderClip = new ClipItem { Text = directory, MediaKind = "folder" };
            var videoClip = new ClipItem { MediaKind = "video", FileName = "a.mp4" };
            var graphClip = new ClipItem { Text = "Begin Object Class=/Script/MaterialGraphNode\nCustomProperties Pin" };
            foreach (var clip in new[] { Clip("oldest"), folderClip, imageClip, videoClip, graphClip }) panel.RequestPaste(clip, false, quoted: true);
            check(quoted.SequenceEqual([Clip("oldest"), folderClip]) && plain.SequenceEqual([imageClip, videoClip, graphClip]),
                "кавычки: для текста и пути папки; картинка, файл и граф вставляются как обычно");
            panel.RequestPaste(Clip("oldest"), false);
            check(quoted.Count == 2 && plain[^1] == Clip("oldest"), "кавычки: без Ctrl обычная вставка");

            panel.AutoHideOutside = true;
            check(panel.HidesWhenAway, "булавка панели: выключена, панель скрывается сама");
            int saves = 0;
            panel.KeepOpenChanged += _ => saves++;
            ((System.Windows.Automation.Provider.IToggleProvider)new ToggleButtonAutomationPeer(panel.KeepOpenButton)).Toggle();
            check(saves == 1 && panel.KeepOpen, "булавка панели: переключение без мыши (экранный диктор) тоже сохраняется");
            check(!panel.HidesWhenAway, "булавка панели: включена, не скрывается ни от мыши, ни от потери фокуса");
            new System.Windows.Interop.WindowInteropHelper(panel).EnsureHandle();
            SetWindowPos(panel._hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);
            panel.ShowActivated = false;
            check(panel.PinEnergy.Visibility == Visibility.Collapsed, "булавка панели: пока панель скрыта, кромка не рисуется");
            panel.Show();
            panel.HideAfterUse();
            check(panel.IsShown, "булавка панели: вставка оставляет панель на экране");
            panel.OnFocusMaybeLost();
            check(!panel._deactivateTimer.IsEnabled, "булавка панели: уход фокуса не запускает скрытие");
            panel.StartOutsideTimer();
            check(!panel._outsideTimer.IsEnabled, "булавка панели: уход мыши не запускает скрытие");

            panel.UpdateLayout();
            check(panel.PinEnergy.Visibility == Visibility.Visible && panel.PinEnergy.Opacity == 1 && panel.PinSpark.Visibility == Visibility.Collapsed && !panel.PinCore.HasAnimatedProperties,
                "булавка панели: без анимации кромка светится неподвижно");
            panel.Animations = true;
            panel.UpdateLayout();
            check(panel.PinSpark.Visibility == Visibility.Visible && panel.PinCore.HasAnimatedProperties && panel.PinTail.HasAnimatedProperties && panel.PinBloom.HasAnimatedProperties,
                "булавка панели: заряд бежит по кромке");
            var dash = panel.PinCore.StrokeDashArray;
            double unit = panel.PinCore.StrokeThickness;
            double perimeter = 2 * (panel.PinCore.ActualWidth - unit + panel.PinCore.ActualHeight - unit) - (8 - 2 * Math.PI) * panel.PinCore.RadiusX;
            check(Math.Abs((dash[0] + dash[1]) * unit - perimeter) <= 2, "булавка панели: один штрих на весь периметр, на стыке круга нет рывка");
            TextEditorWindow.PumpUntil(() => panel.PinSpark.Visibility == Visibility.Collapsed);
            check(panel.PinEnergy.Visibility == Visibility.Visible && !panel._pinCharging && !panel.PinCore.HasAnimatedProperties && !panel.PinHalo.HasAnimatedProperties,
                "булавка панели: с панелью не работают, заряд погас, кромка светится и кадров не тратит");
            panel.SetPinMotion(true);
            check(panel.PinSpark.Visibility == Visibility.Visible && panel.PinCore.HasAnimatedProperties && panel.PinHalo.HasAnimatedProperties,
                "булавка панели: мышь или фокус вернулись, заряд снова бежит");
            panel.SetPinMotion(false);
            TextEditorWindow.PumpUntil(() => panel.PinSpark.Visibility == Visibility.Collapsed);
            check(!panel.PinCore.HasAnimatedProperties && panel.PinEnergy.Visibility == Visibility.Visible, "булавка панели: мышь и фокус ушли, заряд гаснет");
            panel.SetPinMotion(true);
            panel.Hide();
            check(panel.PinEnergy.Visibility == Visibility.Collapsed && !panel.PinCore.HasAnimatedProperties && !panel.PinHalo.HasAnimatedProperties,
                "булавка панели: скрытая панель не тратит кадры");
            panel.Animations = false;
            panel.Show();
            panel.KeepOpen = false;
            check(panel.PinEnergy.Visibility == Visibility.Collapsed, "булавка панели: сняли булавку, кромка погасла");
            panel.HideAfterUse();
            check(!panel.IsVisible || !panel.IsShown, "булавка панели: без неё вставка прячет панель");

            TextEditorWindow.PumpUntil(() => !panel.IsVisible);
            panel.Show();
            panel.UpdateLayout();
            panel.OutsideHideDelayMs = 500;
            var origin = panel.Root.PointToScreen(new Point(0, 0));
            // Как открывается панель без каретки: курсор в 6 px над её верхним краем (Place). Опрос не должен закрыть её под неподвижной мышью.
            POINT? cursor = new POINT { X = (int)origin.X + 10, Y = (int)origin.Y - 6 };
            var away = new POINT { X = (int)origin.X - 200, Y = (int)origin.Y };
            panel._cursorPosition = () => cursor;
            panel.StartOutsideTimer();
            check(panel._outsideTimer.IsEnabled, "автоскрытие: опрос курсора идёт, пока панель открыта");
            panel._outsideTimer.Stop(); // время задаём сами: проверка не зависит от загрузки машины
            long t = panel._cursorSeenAt + 5000;
            panel.CheckCursorAway(t);
            check(panel.IsShown && panel._cursorSeenAt == t, "автоскрытие: курсор у края панели считается в зоне, отсчёт не идёт");
            cursor = away;
            panel.CheckCursorAway(t + 499);
            check(panel.IsShown, "автоскрытие: курсор вне зоны, но задержка ещё не вышла");
            cursor = new POINT { X = (int)origin.X + 10, Y = (int)origin.Y + 10 };
            panel.CheckCursorAway(t + 450);
            cursor = away;
            panel.CheckCursorAway(t + 900);
            check(panel.IsShown, "автоскрытие: курсор вернулся в зону, отсчёт начинается заново");
            cursor = null;
            panel.CheckCursorAway(t + 5000);
            check(panel.IsShown, "автоскрытие: курсор неизвестен (окно UAC), панель не закрывается");
            cursor = away;
            panel.StartOutsideTimer();
            TextEditorWindow.PumpUntil(() => !panel.IsVisible);
            check(!panel.IsVisible, "автоскрытие: курсор вне зоны дольше задержки, панель закрывается");

            panel.RefreshHintFocus(ModifierKeys.Shift);
            check(panel.HintStack.Opacity == 1 && panel.HintPaste.Opacity < 1 && panel.HintQuick.Opacity < 1 && panel.HintSelect.Opacity < 1, "строка клавиш: зажатый Shift оставляет яркой только свою подсказку");
            panel.RefreshHintFocus(ModifierKeys.Control | ModifierKeys.Shift);
            check(panel.HintQuick.Opacity < 1 && panel.HintStack.Opacity < 1, "строка клавиш: для двух модификаторов подсказки нет");
            panel.RefreshHintFocus(ModifierKeys.None);
            check(panel.HintPaste.Opacity == 1 && panel.HintStack.Opacity == 1, "строка клавиш: отпустил, все подсказки снова яркие");
        }
        finally { Motion.Enabled = animations; panel.Close(); }
    }
}
