using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ArkCopyBuffer;

/// <summary>
/// Перенос строк при растягивании панели: слово, ушедшее на другую строку, скользит туда, записи ниже и клетки сетки
/// съезжают на новые места. Переносы считаем сами по ширинам слов, измеренным один раз: спрашивать место каждого слова
/// у TextBlock стоило 15–35 мс на шаг. Анимация выключена — всё встаёт сразу.
/// </summary>
partial class PanelWindow
{
    // Обычная кривая появления проходит почти весь путь за 40 мс: перенос выглядел бы тем же скачком.
    const int GlideMs = 160;
    static readonly IEasingFunction GlideEase = FrozenGlide();
    static IEasingFunction FrozenGlide() { var ease = new CubicEase { EasingMode = EasingMode.EaseOut }; ease.Freeze(); return ease; }

    readonly record struct Word(FormattedText Text, double Width, bool Break); // ширина с пробелами после слова; Break — перед словом перевод строки
    sealed class Flow
    {
        public string Text = "";
        public List<Word> Words = [];
        public List<Point> At = [];
        public WordLayer? Layer;
    }
    readonly Dictionary<TextBlock, Flow> _flows = [];
    DispatcherTimer? _flowsEnd;

    /// <summary>Слова записи поверх её TextBlock, пока панель тянут: каждое едет само, без вёрстки и перерисовки.</summary>
    sealed class WordLayer : FrameworkElement
    {
        readonly ContainerVisual _root = new();
        readonly List<TranslateTransform> _shifts = [];
        readonly double _baseline;

        public WordLayer(List<Word> words, List<Point> at, double baseline)
        {
            IsHitTestVisible = false;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            _baseline = baseline;
            for (int i = 0; i < words.Count; i++)
            {
                var shift = new TranslateTransform(at[i].X, Y(at[i], words[i]));
                var visual = new DrawingVisual { Transform = shift };
                using (var dc = visual.RenderOpen()) dc.DrawText(words[i].Text, default);
                _root.Children.Add(visual);
                _shifts.Add(shift);
            }
            AddVisualChild(_root);
        }

        double Y(Point at, Word word) => at.Y + _baseline - word.Text.Baseline;
        public void Place(Vector offset, Size size)
        {
            _root.Offset = offset;
            _root.Clip = new RectangleGeometry(new Rect(size));
        }
        public void Move(int i, Point to, Word word)
        {
            Motion.To(_shifts[i], TranslateTransform.XProperty, to.X, GlideMs, GlideEase);
            Motion.To(_shifts[i], TranslateTransform.YProperty, Y(to, word), GlideMs, GlideEase);
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _root;
        protected override Size MeasureOverride(Size availableSize) => default;
    }

    /// <summary>Запомнить, где стоят слова видимых записей: до шага ширины.</summary>
    void WatchTexts()
    {
        if (!Motion.Enabled || GridTab) return;
        for (int i = 0; i < Active.Items.Count; i++)
        {
            if (Active.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem { IsVisible: true } row
                || row.FindDescendant<ContentPresenter>() is not { ContentTemplate: { } template } content
                || template.FindName("TextPreview", content) is not TextBlock { IsVisible: true, TextWrapping: TextWrapping.Wrap, Parent: Grid } text) continue;
            if (!_flows.TryGetValue(text, out var flow)) _flows[text] = flow = new Flow();
            if (flow.Text == text.Text) continue; // переработанная виртуализацией строка показывает другую запись
            DropLayer(text, flow);
            flow.Text = text.Text;
            flow.Words = MeasureWords(text);
            flow.At = Wrap(flow.Words, Room(text), text.LineHeight);
        }
    }

    void GlideTexts()
    {
        foreach (var (text, flow) in _flows)
        {
            if (flow.Text != text.Text) { DropLayer(text, flow); continue; }
            var now = Wrap(flow.Words, Room(text), text.LineHeight);
            if (flow.Layer == null && !now.SequenceEqual(flow.At) && text.Parent is Grid cell)
            {
                flow.Layer = new WordLayer(flow.Words, flow.At, text.BaselineOffset - (text.ActualHeight - text.LineHeight)); // BaselineOffset у TextBlock — от последней строки
                Grid.SetColumn(flow.Layer, Grid.GetColumn(text));
                Grid.SetRow(flow.Layer, Grid.GetRow(text));
                cell.Children.Add(flow.Layer);
                text.Opacity = 0;
            }
            if (flow.Layer is { } layer)
            {
                layer.Place(VisualTreeHelper.GetOffset(text) - (Vector)LayoutInformation.GetLayoutSlot(text).TopLeft, text.RenderSize);
                for (int i = 0; i < now.Count; i++)
                    if (now[i] != flow.At[i]) layer.Move(i, now[i], flow.Words[i]);
            }
            flow.At = now;
        }
    }

    static void DropLayer(TextBlock text, Flow flow)
    {
        if (flow.Layer == null) return;
        (flow.Layer.Parent as Grid)?.Children.Remove(flow.Layer);
        flow.Layer = null;
        text.ClearValue(OpacityProperty);
    }

    void DropFlows()
    {
        _flowsEnd?.Stop();
        _flowsEnd = null;
        foreach (var (text, flow) in _flows) DropLayer(text, flow);
        _flows.Clear();
    }

    /// <summary>Край отпустили: слова доезжают, и записи снова рисует TextBlock.</summary>
    void EndFlows()
    {
        if (_flows.Count == 0) return;
        _flowsEnd = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GlideMs + 20) };
        _flowsEnd.Tick += (_, _) => DropFlows();
        _flowsEnd.Start();
    }

    /// <summary>Слова с шириной. Иероглифы переносятся поодиночке, поэтому каждый сам по себе.</summary>
    static List<Word> MeasureWords(TextBlock text)
    {
        var words = new List<Word>();
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(text).PixelsPerDip;
        var mode = TextOptions.GetTextFormattingMode(text);
        string s = text.Text;
        static bool Glued(char c) => !char.IsWhiteSpace(c) && (c < 0x2E80 || char.IsSurrogate(c));
        bool lineBreak = false;
        for (int i = 0; i < s.Length && words.Count < 200;)
        {
            if (char.IsWhiteSpace(s[i])) { lineBreak |= s[i] == '\n'; i++; continue; }
            int end = i + 1;
            if (Glued(s[i])) while (end < s.Length && Glued(s[end])) end++;
            while (end < s.Length && s[end] == ' ') end++;
            var formatted = new FormattedText(s[i..end], System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection, typeface,
                text.FontSize, text.Foreground, null, mode, dpi);
            words.Add(new Word(formatted, formatted.WidthIncludingTrailingWhitespace, lineBreak));
            lineBreak = false;
            i = end;
        }
        return words;
    }

    /// <summary>Место под текст, а не ширина самого текста.</summary>
    static double Room(TextBlock text) => LayoutInformation.GetLayoutSlot(text).Width - text.Margin.Left - text.Margin.Right;

    static List<Point> Wrap(List<Word> words, double room, double lineHeight)
    {
        var at = new List<Point>(words.Count);
        double x = 0, y = 0;
        foreach (var word in words)
        {
            // Пробелы в конце строки в её ширину не входят.
            if (word.Break || x > 0 && x + word.Text.Width > room) { x = 0; y += lineHeight; }
            at.Add(new Point(x, y));
            x += word.Width;
        }
        return at;
    }

    /// <summary>Где стоят строки, видимые клетки и заголовки групп, без учёта ещё не доехавших сдвигов.</summary>
    Dictionary<FrameworkElement, Point> Places()
    {
        var places = new Dictionary<FrameworkElement, Point>();
        if (!Motion.Enabled || Active.FindDescendant<ScrollViewer>() is not { } scroll) return places;
        void Add(FrameworkElement item)
        {
            var at = LayoutPlace(item, scroll);
            if (at.Y + item.ActualHeight > -100 && at.Y < scroll.ActualHeight + 100) places[item] = at;
        }
        for (int i = 0; i < Active.Items.Count; i++)
            if (Active.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem { IsVisible: true } item) Add(item);
        foreach (var group in Active.Items.Groups ?? (IEnumerable<object>)[])
            if (Active.ItemContainerGenerator.ContainerFromItem(group) is GroupItem { IsVisible: true } item && item.FindDescendant<ContentPresenter>() is { } header) Add(header);
        return places;
    }

    // Не TranslatePoint: он идёт через корень окна, а у Root бывает шейдер, и WPF на каждый вызов считает границы всех 636 клеток (350 мс на шаг).
    static Point LayoutPlace(FrameworkElement item, ScrollViewer scroll) =>
        item.TransformToAncestor(scroll).Transform(default) - (item.RenderTransform is TranslateTransform t ? new Vector(t.X, t.Y) : default);

    void Slide(Dictionary<FrameworkElement, Point> before)
    {
        if (before.Count == 0 || Active.FindDescendant<ScrollViewer>() is not { } scroll) return;
        foreach (var (item, from) in before)
        {
            if (!scroll.IsAncestorOf(item)) continue; // строку переработала виртуализация
            var by = from - LayoutPlace(item, scroll);
            if (by.Length < 1) continue;
            var shift = Motion.Shift(item);
            Motion.To(shift, TranslateTransform.XProperty, 0, GlideMs, GlideEase, from: by.X + shift.X);
            Motion.To(shift, TranslateTransform.YProperty, 0, GlideMs, GlideEase, from: by.Y + shift.Y);
        }
    }
}
