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
    int CompactRows(double height) => Math.Clamp((int)((canvas.ActualHeight - 80) / (height + 14)), 4, 6);
    Rect ScreenRect(MapNode n)
    {
        if (scale >= .55) return new Rect(Screen(n.Position), new Size(280 * scale, 140 * scale));
        var center = Screen(new Point(n.Position.X + 140, n.Position.Y + 70));
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
        Motion.To(canvas, OpacityProperty, 1, Motion.Normal, from: .75);
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
        bool Fits(Rect bounds) => bounds.Width <= Math.Max(1, canvas.ActualWidth - 64) && bounds.Height <= Math.Max(1, canvas.ActualHeight - 64);
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
            var empty = Label("Нет совпадений. Измените даты или очистите поиск.", 16); empty.Width = Math.Max(120, canvas.ActualWidth - 48); empty.TextWrapping = TextWrapping.Wrap;
            Canvas.SetLeft(empty, 24); Canvas.SetTop(empty, 32); canvas.Children.Add(empty); return;
        }
        var visible = new Rect(0, 0, Math.Max(1, canvas.ActualWidth), Math.Max(1, canvas.ActualHeight));
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
            double padding = compact ? 4 : Math.Max(8, 16 * scale), font = compact ? 12 : Math.Clamp(16 * scale, 12, 20), line = Math.Ceiling(font * 1.3);
            bool showImage = node.Entry.Kind == "image" && node.Entry.Hash != null && !node.Entry.Sensitive && rect.Width >= 150 && rect.Height >= 70;
            double imageSize = showImage ? Math.Min(82, rect.Height - padding * 2 - 2) : 0;
            var stack = new StackPanel { Width = Math.Max(1, rect.Width - padding * 2 - 2 - (showImage ? imageSize + 10 : 0)), Height = Math.Max(1, rect.Height - padding * 2 - 2) };
            stack.Children.Add(new TextBlock { Text = node.Entry.Date(First).ToString(compact ? "d MMM" : "d MMM · HH:mm", Culture), FontSize = Math.Clamp(12 * scale, 10, 15), Opacity = .78, Margin = new Thickness(0, 0, 0, compact ? 0 : Math.Max(6, 10 * scale)), TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = node.Entry.Preview, FontFamily = (FontFamily)FindResource(node.Entry.Mono ? "MonoFont" : "TextFont"), FontSize = font, FontWeight = node.Entry.Id == selected ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = line, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = line * (compact ? (rect.Height >= 80 ? 3 : 1) : scale >= .9 ? 3 : 2) });
            Image? thumbnail = null;
            object content = stack;
            if (showImage)
            {
                thumbnail = new Image { Width = imageSize, Height = imageSize, Stretch = Stretch.Uniform, ClipToBounds = true, Margin = new Thickness(0, 0, 10, 0) };
                var card = new DockPanel(); DockPanel.SetDock(thumbnail, Dock.Left); card.Children.Add(thumbnail); card.Children.Add(stack); content = card;
            }
            var button = new Button { Tag = node.Entry.Id, Content = content, Style = (Style)FindResource("StandardButton"), Padding = new Thickness(padding), HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brush("WindowSurface"), Foreground = Brush("TextPrimary"), BorderBrush = Brush(node.Entry.Id == selected ? "Accent" : "ControlStroke"), BorderThickness = new Thickness(node.Entry.Id == selected ? 2 : 1), Focusable = true };
            button.ToolTip = new TextBlock { Text = SourceLabel(node.Entry.Source) + "\n" + node.Entry.Preview, MaxWidth = 340, TextWrapping = TextWrapping.Wrap };
            button.Cursor = Cursors.Hand;
            AutomationProperties.SetName(button, node.Entry.Date(First).ToString("g", Culture) + " · " + SourceLabel(node.Entry.Source) + " · " + node.Entry.Preview);
            button.Click += (_, _) => Select(node.Entry.Id); button.MouseDoubleClick += (_, _) => CenterOn(node.Entry.Id);
            Place(button, rect.TopLeft, rect.Width, rect.Height);
            if (thumbnail != null) RequestMapThumbnail(thumbnail, node.Entry, thumbnailFrame);
        }
    }
    void Place(FrameworkElement element, Point at, double width, double height) { element.Width = Math.Max(0, width); element.Height = Math.Max(0, height); Canvas.SetLeft(element, at.X); Canvas.SetTop(element, at.Y); canvas.Children.Add(element); }
}
