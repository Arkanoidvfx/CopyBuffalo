using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArkCopyBuffer;

internal sealed partial class HistoryMapWindow
{
    string SourceLabel(string source) => string.IsNullOrWhiteSpace(source) ? T("Источник не записан") : source;
    double XScaleAt(double value) => value * (1 - .35 * Math.Clamp((.55 - value) / .1, 0, 1));
    double XScale => XScaleAt(scale);
    Point Screen(Point p) => new(p.X * XScale + offset.X, p.Y * scale + offset.Y);
    int CompactRows(double height) => Math.Clamp((int)((canvas.ActualHeight - 16) / (height + 14)), 2, 6);
    Rect ScreenRect(MapNode n)
    {
        var center = Screen(new Point(n.Position.X + 140, n.Position.Y + 70));
        if (scale >= .55)
        {
            double w = Math.Min(340 * scale - 12, Math.Max(228, 280 * scale)), h = Math.Max(116, 140 * scale);
            return new Rect(center.X - w / 2, center.Y - h / 2, w, h);
        }
        double blend = Math.Clamp((.55 - scale) / .1, 0, 1);
        double width = 280 * scale + (200 - 280 * scale) * blend, height = 140 * scale + (90 - 140 * scale) * blend;
        int rows = CompactRows(height);
        int index = (int)Math.Round(n.Position.X / 340);
        center.Y += (((index % rows) - (rows - 1) / 2.0) * (height + 14) - n.Position.Y * scale) * Math.Min(1, blend * 4);
        return new Rect(center.X - width / 2, center.Y - height / 2, width, height);
    }

    void CenterOn(Guid id)
    {
        overview = false;
        var n = nodes.FirstOrDefault(n => n.Entry.Id == id); if (n == null) { Draw(); return; }
        scale = 1;
        offset = new Point(Center.X - (n.Position.X + 140) * scale, Center.Y - (n.Position.Y + 70) * scale); Draw();
        if (!timelineMode) Motion.To(canvas, OpacityProperty, 1, Motion.Normal, from: .75);
    }
    void Fit()
    {
        if (nodes.Length == 0) return;
        if (overview) { ShowOverview(); return; }
        var oldScale = scale; var oldOffset = offset;
        Rect Measure(double value)
        {
            scale = value; offset = new Point(); var bounds = Rect.Empty;
            foreach (var node in nodes) bounds.Union(ScreenRect(node));
            return bounds;
        }
        bool Fits(Rect bounds) => bounds.Width <= Math.Max(1, canvas.ActualWidth - 32) && bounds.Height <= Math.Max(1, canvas.ActualHeight - 32);
        double low = .25, high = 1;
        if (!Fits(Measure(low)))
        {
            low = .55;
            if (!Fits(Measure(low))) { scale = oldScale; offset = oldOffset; ShowOverview(); return; }
        }
        for (int i = 0; i < 18; i++)
        {
            double middle = (low + high) / 2;
            if (Fits(Measure(middle))) low = middle; else high = middle;
        }
        var fitted = Measure(low);
        offset = new Point(Center.X - fitted.Left - fitted.Width / 2, Center.Y - fitted.Top - fitted.Height / 2);
        Draw();
    }
    void Zoom(double next, Point at, bool pointerTarget = false)
    {
        if (groupPending) return;
        if (overview)
        {
            if (next > scale)
            {
                var group = (pointerTarget ? summaryCards.FirstOrDefault(c => c.Bounds.Contains(at)).Group : null)
                    ?? summaryCards.FirstOrDefault(c => c.Group.Entries.Any(e => e.Id == selected)).Group
                    ?? summaryCards.FirstOrDefault().Group;
                if (group != null) OpenGroup(group);
            }
            else BackToOverview();
            return;
        }
        next = Math.Clamp(next, .25, 2.5);
        var pointed = pointerTarget ? nodes.FirstOrDefault(n => ScreenRect(n).Contains(at)) : null;
        var before = pointed == null ? default : ScreenRect(pointed);
        offset = new Point(at.X - (at.X - offset.X) * XScaleAt(next) / XScale, at.Y - (at.Y - offset.Y) * next / scale);
        scale = next;
        if (pointed != null)
        {
            var after = ScreenRect(pointed);
            offset += new Vector(before.X + before.Width / 2 - after.X - after.Width / 2, before.Y + before.Height / 2 - after.Y - after.Height / 2);
        }
        Draw();
    }
    void Draw()
    {
        bool focused = canvas.IsKeyboardFocusWithin;
        object? tag = (Keyboard.FocusedElement as FrameworkElement)?.Tag;
        DrawMap();
        if (focused)
        {
            var target = canvas.Children.OfType<Button>().FirstOrDefault(b => tag != null && Equals(b.Tag, tag));
            if (target != null) target.Focus(); else canvas.Focus();
        }
    }
    void DrawMap()
    {
        int thumbnailFrame = BeginMapFrame();
        summaryCards.Clear();
        canvas.Children.Clear();
        selectedIndex = Array.FindIndex(nodes, n => n.Entry.Id == selected);
        pinnedEntry = selectedIndex < 0 ? null : nodes[selectedIndex].Entry;
        TimelineLayout();
        if (timelineMode) SyncTimeline();
        reader.Visibility = !overview && nodes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Panel actions = reader.Visibility == Visibility.Visible ? readerActions : header;
        copy.Visibility = nodes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(copy.Parent, actions))
        {
            ((Panel)copy.Parent).Children.Remove(copy);
            actions.Children.Add(copy);
        }
        ShowReading(overview ? null : pinnedEntry);
        int position = selectedIndex < 0 ? 0 : timelineMode ? nodes.Length - selectedIndex : selectedIndex + 1;
        previousEntry.IsEnabled = !overview && position > 1;
        nextEntry.IsEnabled = !overview && position > 0 && position < nodes.Length;
        positionLabel.Text = overview || position == 0 ? UiText.F($"Записей: {nodes.Length}") : $"{position} / {nodes.Length}";
        zoomLabel.Text = overview ? T("Обзор") : (scale * 100).ToString("0'%'", Culture);
        canvas.Cursor = overview ? Cursors.Arrow : Cursors.SizeAll;
        zoomOut.IsEnabled = !groupPending && nodes.Length > 0 && (overview ? groupHistory.Count > 0 : scale > .25);
        zoomIn.IsEnabled = !groupPending && nodes.Length > 0 && (overview || scale < 2.5);
        mapBack.IsEnabled = !groupPending;
        mapBack.Visibility = groupHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        dayOverview.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        dayOverview.BorderBrush = Brush(overview ? "Accent" : "ControlStroke");
        if (nodes.Length == 0)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            bool filtered = clearFilters.Visibility == Visibility.Visible;
            stack.Children.Add(new TextBlock { Text = filtered ? "\uE721" : "\uE77F", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 28, TextAlignment = TextAlignment.Center, Foreground = Brush("TextSecondary") });
            var title = Label(filtered ? "Ничего не найдено" : invalid.Count > 0 ? "Обновите карту для просмотра изменений" : "История пуста", 22);
            title.FontWeight = FontWeights.SemiBold; title.TextAlignment = TextAlignment.Center; title.Margin = new Thickness(0, 10, 0, 6); stack.Children.Add(title);
            if (filtered)
            {
                if (clearFilters.Parent is Panel parent) parent.Children.Remove(clearFilters);
                stack.Children.Add(clearFilters);
            }
            else if (invalid.Count == 0) stack.Children.Add(Label("Скопируйте текст или изображение, чтобы увидеть его здесь.", 14));
            var empty = new Grid(); empty.Children.Add(stack);
            Place(empty, new Point(), Math.Max(1, canvas.ActualWidth), Math.Max(1, canvas.ActualHeight)); return;
        }
        if (clearFilters.Parent != header)
        {
            if (clearFilters.Parent is Panel parent) parent.Children.Remove(clearFilters);
            header.Children.Insert(header.Children.IndexOf(filtersToggle) + 1, clearFilters);
        }
        var visible = new Rect(0, 0, Math.Max(1, canvas.ActualWidth), Math.Max(1, canvas.ActualHeight));
        if (timelineMode) return;
        if (overview) { DrawGroups(visible); return; }

        // Lines indicate time order, not inferred relationships.
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        for (int i = 1; i < nodes.Length; i++)
        {
            var previous = ScreenRect(nodes[i - 1]); var current = ScreenRect(nodes[i]);
            if (scale < .45 && i % CompactRows(previous.Height) == 0) continue;
            var a = scale < .55 ? new Point(previous.X + previous.Width / 2, previous.Y < current.Y ? previous.Bottom : previous.Top) : new Point(previous.Right, previous.Y + previous.Height / 2);
            var b = scale < .55 ? new Point(current.X + current.Width / 2, previous.Y < current.Y ? current.Top : current.Bottom) : new Point(current.Left, current.Y + current.Height / 2);
            if (b.X < 0 || a.X > visible.Width || Math.Max(a.Y, b.Y) < 0 || Math.Min(a.Y, b.Y) > visible.Height) continue;
            path.BeginFigure(a, false, false);
            path.BezierTo(new Point((a.X + b.X) / 2, a.Y), new Point((a.X + b.X) / 2, b.Y), b, true, false);
        }
        geometry.Freeze(); canvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = Brush("ControlStroke"), StrokeThickness = 1, IsHitTestVisible = false });
        foreach (var node in nodes)
        {
            var rect = ScreenRect(node); if (!visible.IntersectsWith(rect)) continue;
            bool compact = scale < .55;
            double padding = compact ? 4 : Math.Max(7, 12 * scale), font = compact ? 13 : Math.Clamp(17 * scale, 14, 20), line = Math.Ceiling(font * 1.3);
            bool showImage = node.Entry.Kind == "image" && node.Entry.Hash != null && !node.Entry.Sensitive && rect.Width >= 150 && rect.Height >= 70;
            var stack = new StackPanel { Width = Math.Max(1, rect.Width - padding * 2 - 2), Height = Math.Max(1, rect.Height - padding * 2 - 2) };
            var stamp = node.Entry.Date(First).ToString(compact ? "d MMM" : "d MMM · HH:mm", Culture);
            stack.Children.Add(new TextBlock { Text = stamp, FontSize = Math.Clamp(12 * scale, 10, 15), Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 0, 0, compact ? 0 : Math.Max(6, 10 * scale)), TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = node.Entry.Preview, FontFamily = (FontFamily)FindResource(node.Entry.Mono ? "MonoFont" : "TextFont"), FontSize = font, FontWeight = node.Entry.Id == selected ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = line, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = line * (compact ? (rect.Height >= 80 ? 3 : 1) : scale >= .9 ? 3 : 2) });
            Image? thumbnail = null;
            object content = stack;
            if (showImage)
            {
                var card = new DockPanel { Width = stack.Width, Height = stack.Height };
                var caption = new TextBlock { Text = stamp + "  ·  " + (string.IsNullOrWhiteSpace(node.Entry.Source) ? T("Изображение") : node.Entry.Source), FontSize = 12, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis };
                DockPanel.SetDock(caption, Dock.Top); card.Children.Add(caption);
                thumbnail = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 0) };
                card.Children.Add(thumbnail); content = card;
            }
            var button = new Button { Tag = node.Entry.Id, Content = content, Style = (Style)FindResource("StandardButton"), Padding = new Thickness(padding), HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brush("WindowSurface"), Foreground = Brush("TextPrimary"), BorderBrush = Brush(node.Entry.Id == selected ? "Accent" : "ControlStroke"), BorderThickness = new Thickness(node.Entry.Id == selected ? 2 : 1), Focusable = true };
            if (!compact && scale >= .9 && !string.IsNullOrWhiteSpace(node.Entry.Source))
                stack.Children.Add(new TextBlock { Text = node.Entry.Source, FontSize = 12, Foreground = Brush("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
            button.MouseEnter += (_, _) => ShowReading(node.Entry);
            button.GotKeyboardFocus += (_, _) => ShowReading(node.Entry);
            button.LostKeyboardFocus += (_, _) => ShowReading(pinnedEntry);
            button.Cursor = Cursors.Hand;
            AutomationProperties.SetName(button, node.Entry.Date(First).ToString("g", Culture) + " · " + SourceLabel(node.Entry.Source) + " · " + node.Entry.Preview);
            button.Click += (_, _) => Select(node.Entry.Id); button.MouseDoubleClick += (_, _) => CenterOn(node.Entry.Id);
            Place(button, rect.TopLeft, rect.Width, rect.Height);
            if (thumbnail != null) _ = RequestMapThumbnail(thumbnail, node.Entry, thumbnailFrame);
        }
    }
    void Place(FrameworkElement element, Point at, double width, double height) { element.Width = Math.Max(0, width); element.Height = Math.Max(0, height); Canvas.SetLeft(element, at.X); Canvas.SetTop(element, at.Y); canvas.Children.Add(element); }
}
