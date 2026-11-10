using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ArkCopyBuffer;

internal sealed partial class HistoryMapWindow
{
    internal static void SelfTest(string directory, Action<bool, string> check)
    {
        var station = new System.Text.StringBuilder(256);
        if (!GetUserObjectInformation(GetProcessWindowStation(), 2, station, station.Capacity * 2, out _) || station.ToString().Equals("WinSta0", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("History map clipboard checks require an isolated noninteractive window station.");
        Directory.CreateDirectory(directory);
        bool motion = Motion.Enabled; Motion.Enabled = false;
        var store = new HistoryStore(Path.Combine(directory, "map.dat"));
        var time = DateTime.UtcNow.AddDays(-2);
        for (int i = 0; i < 28; i++) store.Items.Add(new ClipItem { Text = i % 3 == 0 ? "Research notes\nMaterials, references and next steps" : i % 3 == 1 ? "git log --oneline --max-count=12" : "https://example.test/projects/clipboard", SourceApp = i % 3 == 0 ? "Notepad" : i % 3 == 1 ? "Terminal" : "Browser", CreatedAt = time.AddMinutes(i * 120), LastUsedAt = time.AddMinutes(i * 120) });
        var anchor = store.Items[14]; var snap = MapEntry.Capture(anchor);
        anchor.LastUsedAt = DateTime.UtcNow;
        check(snap.Used != anchor.LastUsedAt, "snapshot keeps last-use date stable after copying");
        check(!snap.Matches("", null, null, snap.Date(false).Date.AddDays(1), null, false), "date filter uses local calendar day");
        var dated = snap with { Created = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(), Used = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Local).ToUniversalTime() };
        var firstDay = new DateTime(2026, 10, 24);
        check(dated.Matches("", null, null, firstDay, firstDay, true) && !dated.Matches("", null, null, firstDay, firstDay, false), "date mode selects first-copy versus latest-use day across DST weekend");
        var midnight = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        check((snap with { Used = midnight.AddTicks(-1) }).Date(false).Date == new DateTime(2026, 10, 25) && (snap with { Used = midnight }).Date(false).Date == new DateTime(2026, 10, 26), "local midnight separates adjacent calendar days");
        check(snap.Matches("EXAMPLE.TEST", snap.Source, snap.Kind, null, null, false) && !snap.Matches("", "absent-source", null, null, null, false), "search and source filters compose independently");
        var a = new ClipItem { Text = "duplicate", CreatedAt = time, LastUsedAt = time };
        var b = new ClipItem { Text = "duplicate", CreatedAt = time, LastUsedAt = time };
        check(HistoryMap.Layout([MapEntry.Capture(a), MapEntry.Capture(b)], false).Select(n => n.Entry.Id).Distinct().Count() == 2, "duplicate contents retain distinct IDs");
        var secret = MapEntry.Capture(new ClipItem { Text = "secret", IsSensitive = true });
        check(!secret.Matches("secret", null, null, null, null, false), "sensitive contents excluded from search");
        anchor.LastUsedAt = snap.Used;
        PanelWindow.CheckSearchPaste(directory, check);
        var window = new HistoryMapWindow(store);
        void Pump(Func<bool> complete)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!complete() && DateTime.UtcNow < until) { var frame = new DispatcherFrame(); window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(5); }
            check(complete(), "map async work completed");
        }
        bool ControlsOutsideCanvas(HistoryMapWindow target)
        {
            target.UpdateLayout();
            var origin = target.toolSurface.TranslatePoint(new Point(0, 0), target.canvas);
            var controls = new Rect(origin, new Size(target.toolSurface.ActualWidth, target.toolSurface.ActualHeight));
            var map = new Rect(0, 0, target.canvas.ActualWidth, target.canvas.ActualHeight);
            return !controls.IntersectsWith(map);
        }
        try
        {
            window.original = window.selected = anchor.Id;
            window.Show(); window.RefreshSnapshot(); Pump(() => window.nodes.Length == 28);
            window.UpdateLayout(); window.CenterOn(anchor.Id);
            check(!window.rail.IsVisible && !window.inspector.IsVisible && !window.filterPanel.IsVisible && !window.clearFilters.IsVisible && window.copy.IsVisible, "wide map opens with auxiliary panes and inactive filter cue hidden and Copy available");
            window.CaptureMap(Path.Combine(directory, "map-first-wide.png"));
            window.filtersToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); window.UpdateLayout();
            check(window.filterPanel.IsVisible && window.search.IsVisible, "search controls open on demand");
            window.filtersToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); window.UpdateLayout();
            check(!window.filterPanel.IsVisible, "search controls close without closing the map");
            var n = window.nodes.Single(n => n.Entry.Id == anchor.Id);
            check(Math.Abs((n.Position.X + 140) * window.scale + window.offset.X - window.Center.X) < 1 && Math.Abs((n.Position.Y + 70) * window.scale + window.offset.Y - window.Center.Y) < 1, "selected ID centered exactly on both axes");
            int index = Array.FindIndex(window.nodes, node => node.Entry.Id == anchor.Id);
            var visibleIds = window.canvas.Children.OfType<System.Windows.Controls.Button>().Select(button => button.Tag).OfType<Guid>().ToHashSet();
            check(visibleIds.Contains(anchor.Id) && visibleIds.Contains(window.nodes[index - 1].Entry.Id) && visibleIds.Contains(window.nodes[index + 1].Entry.Id), "opening view shows centered item with its chronological neighbors");
            window.search.Text = "Research"; Pump(() => window.nodes.Length < 28);
            check(!window.filterPanel.IsVisible && window.clearFilters.IsVisible && window.clearFilters.ToolTip!.ToString()!.Contains("Research"), "collapsed search exposes the active-filter cue and details");
            window.clearFilters.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(() => window.nodes.Length == 28);
            check(window.search.Text.Length == 0 && !window.clearFilters.IsVisible && !window.filterPanel.IsVisible, "clear action removes hidden search without opening controls");
            window.sources.SelectedIndex = 1; Pump(() => window.nodes.Length < 28);
            window.kinds.SelectedIndex = 1;
            check(window.clearFilters.IsVisible && window.clearFilters.ToolTip!.ToString()!.Contains(window.sources.SelectedItem!.ToString()!), "source and type filters expose the active-filter cue");
            window.clearFilters.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(() => window.nodes.Length == 28);
            check(window.sources.SelectedIndex == 0 && window.kinds.SelectedIndex == 0 && !window.clearFilters.IsVisible, "clear action restores source and type filters");
            window.Select(anchor.Id); window.CenterOn(anchor.Id);
            var pointer = new Point(147, 193); var beforeZoom = (pointer - window.offset) / window.scale;
            window.Zoom(.8, pointer);
            check(!window.overview && Math.Abs(window.scale - .8) < .0001 && ((pointer - window.offset) / window.scale - beforeZoom).Length < .01, "detail zoom preserves the pointer anchor");
            int nearCount = window.canvas.Children.OfType<System.Windows.Controls.Button>().Count();
            var zoomWorldPoint = new Point((pointer.X - window.offset.X) / window.XScale, (pointer.Y - window.offset.Y) / window.scale);
            string clipboardBeforeZoom = System.Windows.Clipboard.GetText();
            window.Zoom(.25, pointer); window.UpdateLayout();
            var smallCards = window.canvas.Children.OfType<System.Windows.Controls.Button>().ToArray();
            var smallRects = smallCards.Select(b => new Rect(System.Windows.Controls.Canvas.GetLeft(b), System.Windows.Controls.Canvas.GetTop(b), b.Width, b.Height)).ToArray();
            var zoomWorldAfter = new Point((pointer.X - window.offset.X) / window.XScale, (pointer.Y - window.offset.Y) / window.scale);
            check(!window.overview && Math.Abs(window.scale - .25) < .0001 && (zoomWorldAfter - zoomWorldPoint).Length < .01, "25-percent zoom stays on the map and preserves the pointer anchor");
            check(smallCards.Length > nearCount + 2, "zooming out reveals substantially more nearby history");
            check(smallCards.All(b => b.Width >= 200 && b.Height >= 90) && smallRects.SelectMany((a, i) => smallRects.Skip(i + 1).Select(b => a.IntersectsWith(b))).All(overlap => !overlap), "zoomed-out cards remain large and do not overlap");
            check(smallRects.Select(r => Math.Round(r.Top)).Distinct().Count() >= 4 && smallRects.Max(r => r.Bottom) - smallRects.Min(r => r.Top) >= window.canvas.ActualHeight * .4, "zoomed-out entries use several rows and the available vertical space");
            var sampleCard = smallCards.FirstOrDefault(b => b.Content is System.Windows.Controls.StackPanel panel && panel.Children.OfType<System.Windows.Controls.TextBlock>().Count() >= 2);
            check(sampleCard?.Content is System.Windows.Controls.StackPanel content && content.Children.OfType<System.Windows.Controls.TextBlock>().Take(2).All(t => t.FontSize >= 10 && t.Text.Length > 0 && t.TranslatePoint(new Point(0, 0), content).Y + t.ActualHeight <= content.ActualHeight + 1), "zoomed-out cards retain readable date and content without vertical clipping");
            check(sampleCard?.Content is System.Windows.Controls.StackPanel multiline && multiline.Children.OfType<System.Windows.Controls.TextBlock>().Last() is { FontSize: >= 12, TextWrapping: TextWrapping.Wrap } excerpt && excerpt.MaxHeight >= excerpt.LineHeight * 2, "compact entries show readable multiline previews");
            check(sampleCard?.ToolTip is System.Windows.Controls.TextBlock tooltip && tooltip.Text.Length > 0 && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(sampleCard)), "zoomed-out cards expose full preview and an accessible name");
            var panNode = window.nodes.Single(item => item.Entry.Id == anchor.Id); var beforePan = window.ScreenRect(panNode);
            window.offset += new Vector(45, 30); window.Draw(); var afterPan = window.ScreenRect(panNode);
            check(Math.Abs(afterPan.X - beforePan.X - 45) < .01 && Math.Abs(afterPan.Y - beforePan.Y - 30) < .01 && window.selected == anchor.Id, "panning moves cards on both axes without changing selection");
            window.offset -= new Vector(45, 30); window.Draw();
            var otherCard = window.canvas.Children.OfType<System.Windows.Controls.Button>().First(b => b.Tag is Guid id && id != anchor.Id);
            var otherId = (Guid)otherCard.Tag; otherCard.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            check(window.selected == otherId && System.Windows.Clipboard.GetText() == clipboardBeforeZoom, "selecting a visible nearby entry never copies");
            window.Select(anchor.Id);
            window.CenterOn(anchor.Id); window.Zoom(.8, window.Center);
            var focusedBefore = window.ScreenRect(window.nodes.Single(item => item.Entry.Id == anchor.Id));
            var focusedPoint = new Point(focusedBefore.X + focusedBefore.Width / 2, focusedBefore.Y + focusedBefore.Height / 2);
            window.Zoom(.25, focusedPoint, pointerTarget: true);
            var focusedAfter = window.ScreenRect(window.nodes.Single(item => item.Entry.Id == anchor.Id));
            check((new Point(focusedAfter.X + focusedAfter.Width / 2, focusedAfter.Y + focusedAfter.Height / 2) - focusedPoint).Length < 1, "pointer zoom keeps the item under the cursor fixed during row reflow");
            foreach (double level in new[] { .25, .30, .35, .40, .45, .50, .51, .52, .53, .54 })
            {
                window.Zoom(level, pointer);
                var positions = window.nodes.Select(window.ScreenRect).ToArray();
                check(positions.SelectMany((left, i) => positions.Skip(i + 1).Select(right => left.IntersectsWith(right))).All(overlap => !overlap), $"timeline entries do not overlap at {level:P0}");
            }
            window.Zoom(.25, pointer); window.UpdateLayout();
            window.Zoom(.1, pointer);
            check(!window.overview && Math.Abs(window.scale - .25) < .0001 && window.selected == anchor.Id && window.calendar.SelectedDates.Count == 0 && System.Windows.Clipboard.GetText() == clipboardBeforeZoom, "minimum zoom remains on the map without changing selection, dates or clipboard");
            var allNodes = window.nodes;
            foreach (int sampleSize in new[] { 8, 18 })
            {
                window.nodes = HistoryMap.Layout(window.snapshot.Take(sampleSize), false);
                window.overview = false; window.Fit(); window.UpdateLayout();
                var viewport = new Rect(-1, -1, window.canvas.ActualWidth + 2, window.canvas.ActualHeight + 2);
                check(!window.overview && window.nodes.All(item => viewport.Contains(window.ScreenRect(item))), $"Fit frames all {sampleSize} reflowed cards without switching to overview");
            }
            window.nodes = allNodes; window.CenterOn(anchor.Id);
            window.ShowOverview();
            check(window.overview && window.summaryCards.Count > 0 && window.summaryCards.Sum(c => c.Group.Entries.Length) == 28, "explicit overview provides readable exhaustive date groups");
            check(window.summaryCards.All(c => c.Bounds.Width >= 200 && c.Bounds.Height >= 150), "overview cards retain readable dimensions independent of zoom scale");
            var group = window.summaryCards.First(c => c.Group.Entries.Any(e => e.Id == anchor.Id)).Group;
            string clipboardBefore = System.Windows.Clipboard.GetText();
            window.OpenGroup(group); window.OpenGroup(group); Pump(() => window.nodes.Length == group.Entries.Length && !window.overview && window.copy.IsEnabled && !window.groupPending);
            check(window.groupHistory.Count == 1, "rapid repeated drill requests do not duplicate navigation history");
            check(window.clearFilters.IsVisible && !window.filterPanel.IsVisible && window.calendar.SelectedDates.Count > 0, "date drill exposes the active-filter cue with filters collapsed");
            check(window.scale == 1 && window.selected == anchor.Id && System.Windows.Clipboard.GetText() == clipboardBefore, "group drill-in preserves selected ID and opens readable entries without copying");
            window.BackToOverview(); Pump(() => window.nodes.Length == 28 && window.overview);
            check(window.calendar.SelectedDates.Count == 0 && window.selected == anchor.Id && !window.clearFilters.IsVisible, "Back restores the previous dates, selected item and filter cue");
            window.OpenGroup(group); Pump(() => window.nodes.Length == group.Entries.Length && !window.groupPending);
            window.clearFilters.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(() => window.nodes.Length == 28);
            check(window.calendar.SelectedDates.Count == 0 && window.groupHistory.Count == 0 && !window.clearFilters.IsVisible, "clear action resets date drill-down and its navigation history");
            window.ShowOverview();
            window.Fit(); var bounds = HistoryMap.Bounds(window.nodes);
            var top = window.Screen(bounds.TopLeft); var bottom = window.Screen(bounds.BottomRight);
            check(window.overview && window.summaryCards.Sum(c => c.Group.Entries.Length) == 28 || top.X >= 0 && top.Y >= 0 && bottom.X <= window.canvas.ActualWidth && bottom.Y <= window.canvas.ActualHeight, "Fit represents every match with readable groups or full cards");
            check(ControlsOutsideCanvas(window) && (!window.overview || window.summaryCards.All(c => c.Bounds.Bottom <= window.canvas.ActualHeight + 1)), "map controls remain outside the canvas and overview cards fit inside it");
            window.railOpen = window.inspectorOpen = true; window.railChosen = true; window.ResizePanes(); window.UpdateLayout();
            var oldWidth = window.canvas.ActualWidth; window.railOpen = false; window.inspectorOpen = false; window.ResizePanes(); window.UpdateLayout();
            check(window.canvas.ActualWidth > oldWidth + 400, "both side panels collapse at desktop width to expand the map");
            window.railOpen = window.inspectorOpen = true; window.ResizePanes(); window.UpdateLayout(); window.CenterOn(anchor.Id);
            store.Touch(anchor.Id, DateTime.UtcNow);
            check(window.snapshot.Single(e => e.Id == anchor.Id).Used == snap.Used && window.nodes.Any(n => n.Entry.Id == anchor.Id), "touch does not move or hide snapshot node");
            check(window.refresh.Content!.ToString() == UiText.T("Есть изменения"), "updates indicator after touch");
            var copying = window.Copy(); Pump(() => copying.IsCompleted); copying.GetAwaiter().GetResult();
            check(System.Windows.Clipboard.GetText() == anchor.Text, "explicit map copy writes text without pasting");
            var cancelled = Paster.SetClipboardAsync(a, false, store.ImagesDir, () => false); Pump(() => cancelled.IsCompleted);
            check(cancelled.IsCanceled && System.Windows.Clipboard.GetText() == anchor.Text, "cancelled copy leaves clipboard intact");
            foreach (bool dark in new[] { true, false })
            foreach (var width in new[] { 1280, 800, 801, 802 })
            {
                ((App)Application.Current).ApplyTheme(dark);
                var settled = false; window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => settled = true)); Pump(() => settled);
                window.railOpen = width == 802;
                window.railChosen = width == 802;
                window.inspectorOpen = width == 801;
                window.Width = width >= 801 && width < 1000 ? 800 : width; window.Height = width >= 801 && width < 1000 ? 480 : 820; window.UpdateLayout(); window.ResizePanes(); window.UpdateLayout(); window.CenterOn(anchor.Id); window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.root.ActualWidth, (int)window.root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(window.root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(window.root.Margin.Left, window.root.Margin.Top, window.root.ActualWidth, window.root.ActualHeight), Stretch = Stretch.Fill }, null, new Rect(0, 0, window.root.ActualWidth, window.root.ActualHeight)); bitmap.Render(drawing);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(directory, $"map-{width}-{(dark ? "dark" : "light")}.png")); png.Save(file);
                if (width is 1280 or 800)
                {
                    window.Zoom(.25, window.Center); window.UpdateLayout();
                    window.CaptureMap(Path.Combine(directory, $"zoom-25-{width}-{(dark ? "dark" : "light")}.png"));
                }
                if (width == 1280)
                foreach (double density in new[] { 1.25, 1.5, 2.0 })
                {
                    var scaled = new RenderTargetBitmap((int)Math.Ceiling(window.root.ActualWidth * density), (int)Math.Ceiling(window.root.ActualHeight * density), 96 * density, 96 * density, PixelFormats.Pbgra32);
                    scaled.Render(drawing);
                    var scaledPng = new PngBitmapEncoder(); scaledPng.Frames.Add(BitmapFrame.Create(scaled));
                    using var scaledFile = File.Create(Path.Combine(directory, $"map-{width}-{(dark ? "dark" : "light")}-{density * 100:0}pct.png")); scaledPng.Save(scaledFile);
                }
            }
            window.search.Text = "no-such-synthetic-content"; Pump(() => window.nodes.Length == 0);
            check(window.heading.Text == "" && !window.copy.IsEnabled, "empty search does not suggest a stale-content refresh");
            window.UpdateLayout();
            var emptyBitmap = new RenderTargetBitmap((int)window.root.ActualWidth, (int)window.root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var emptyVisual = new DrawingVisual(); using (var dc = emptyVisual.RenderOpen()) dc.DrawRectangle(new VisualBrush(window.root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(window.root.Margin.Left, window.root.Margin.Top, window.root.ActualWidth, window.root.ActualHeight), Stretch = Stretch.Fill }, null, new Rect(0, 0, window.root.ActualWidth, window.root.ActualHeight)); emptyBitmap.Render(emptyVisual);
            var emptyPng = new PngBitmapEncoder(); emptyPng.Frames.Add(BitmapFrame.Create(emptyBitmap)); using (var file = File.Create(Path.Combine(directory, "map-empty.png"))) emptyPng.Save(file);
            window.search.Clear(); Pump(() => window.nodes.Length == 28);
            window.Select(anchor.Id); store.UpdateText(anchor, "Changed contents");
            check(window.invalid.Contains(anchor.Id) && !window.copy.IsEnabled && window.preview.Text == "", "content edit revokes preview and copy");
            check(!window.results.Items.Cast<MapEntry>().Any(e => e.Id == anchor.Id), "invalidated content removed from search list");
            window.RefreshSnapshot(); Pump(() => window.nodes.Length == 28);
            anchor.SourceApp = "Updated source"; window.StoreChanged();
            check(!window.copy.IsEnabled && window.preview.Text.Length == 0, "source change invalidates stale context");
            window.RefreshSnapshot(); Pump(() => window.nodes.Length == 28);
            anchor.IsSensitive = true;
            check(!window.copy.IsEnabled && window.picture.Source == null && window.preview.Text.Length == 0, "sensitivity change revokes cached preview immediately");
            window.RefreshSnapshot(); Pump(() => window.nodes.Length == 28);
            store.Prune(DateTime.UtcNow.AddYears(1));
            check(window.nodes.Length == 0 && !window.copy.IsEnabled && !window.home.IsEnabled, "expiry without Changed removes nodes and actions");
            var large = Enumerable.Range(0, 6000).Select(i => new MapEntry(Guid.NewGuid(), "item", null, null, "item", "", "text", null, null, false, time, time.AddSeconds(i))).ToArray();
            check(HistoryMap.Layout(large, false).Length == large.Length, "large history preserves every entry");
            window.nodes = HistoryMap.Layout(large, false); window.overview = false; window.Fit(); window.UpdateLayout();
            check(window.overview && window.canvas.Children.Count > 0 && window.canvas.Children.Count < 100 && window.summaryCards.Sum(c => c.Group.Entries.Length) == 6000, "large fitted map uses bounded readable groups with exact counts");
            check(window.nodes.Select(n => n.Position.Y).Distinct().Count() > 1, "nearby entries occupy distinct positions around the timeline");
            check(window.nodes.Zip(window.nodes.Skip(1)).All(p => p.First.Position.X < p.Second.Position.X), "earlier entries remain left of later entries");
            check(window.nodes.Zip(window.nodes.Skip(1)).All(p => p.Second.Position.X - p.First.Position.X >= 280), "neighboring cards do not overlap");
            window.overview = true; window.Fit(); window.UpdateLayout();
            check(window.canvas.Children.OfType<System.Windows.Controls.Button>().Count() == large.Select(e => e.Date(false).Date).Distinct().Count(), "day overview aggregates every local day into one reachable cluster");
            window.nodes = HistoryMap.Layout(large.Select((e, i) => e with { Used = time.AddDays(i) }), false); window.Fit();
            check(window.canvas.Children.Count > 0 && window.canvas.Children.Count < 100, "many pinned dates keep day overview rendering bounded");
            check(window.summaryCards.Sum(c => c.Group.Entries.Length) == 6000 && window.summaryCards.All(c => c.Group.Entries.Length < 6000), "long history ranges partition all entries into smaller drillable groups");
            var nested = HistoryMap.Groups(window.summaryCards[0].Group.Entries, false, 4);
            check(nested.Length > 1 && nested.Sum(g => g.Entries.Length) == window.summaryCards[0].Group.Entries.Length, "range drill-down progresses without losing entries");
            for (int i = 0; i < 104; i++) store.Items.Add(new ClipItem { Text = i % 3 == 0 ? "Review notes\nCheck the layout, keyboard navigation and zoom behavior" : i % 3 == 1 ? "https://example.test/projects/history-map/navigation" : "D:\\Projects\\Sample\\notes\\history-map-review.md", SourceApp = "", CreatedAt = time.AddDays(i < 4 ? -30 + i * 7 : 0).AddMinutes(i), LastUsedAt = time.AddDays(i < 4 ? -30 + i * 7 : 0).AddMinutes(i) });
            window.original = window.selected = store.Items[40].Id; window.Width = 1280; window.Height = 820; window.railOpen = window.inspectorOpen = true; window.ResizePanes(); window.RefreshSnapshot(); Pump(() => window.nodes.Length == 104); window.UpdateLayout();
            foreach (bool dark in new[] { true, false })
            {
                ((App)Application.Current).ApplyTheme(dark); window.UpdateLayout();
                window.CenterOn(window.selected); window.CaptureMap(Path.Combine(directory, $"review-centered-{(dark ? "dark" : "light")}.png"));
                window.Fit(); window.CaptureMap(Path.Combine(directory, $"review-fit-{(dark ? "dark" : "light")}.png"));
                window.overview = true; window.Fit(); window.CaptureMap(Path.Combine(directory, $"review-days-{(dark ? "dark" : "light")}.png"));
            }
            window.railOpen = window.inspectorOpen = false; window.ResizePanes(); window.UpdateLayout(); window.CenterOn(window.selected);
            window.CaptureMap(Path.Combine(directory, "review-workspace.png"));
            var compact = new HistoryMapWindow(store) { Width = 800, Height = 820 };
            try
            {
                compact.original = compact.selected = store.Items[40].Id; compact.Show(); compact.RefreshSnapshot(); Pump(() => compact.nodes.Length == 104); compact.UpdateLayout(); compact.ResizePanes(); compact.UpdateLayout(); compact.CenterOn(compact.selected);
                check(!compact.rail.IsVisible && !compact.inspector.IsVisible, "first compact opening keeps auxiliary panes hidden");
                compact.CaptureMap(Path.Combine(directory, "map-first-compact.png"));
                compact.previewToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); compact.UpdateLayout();
                check(compact.inspector.IsVisible && !compact.rail.IsVisible, "preview opens on demand");
                compact.Fit(); compact.CaptureMap(Path.Combine(directory, "semantic-compact.png"));
                compact.Width = 640; compact.Height = 480; compact.UpdateLayout(); compact.Fit(); compact.UpdateLayout();
                compact.CaptureMap(Path.Combine(directory, "semantic-short.png"));
                check(ControlsOutsideCanvas(compact) && compact.summaryCards.All(c => c.Bounds.Bottom <= compact.canvas.ActualHeight + 1 && c.Bounds.Right <= compact.canvas.ActualWidth + 1), "short narrow overview keeps controls outside the map and groups inside");
                var selectedGroup = compact.summaryCards.First(c => c.Group.Entries.Any(e => e.Id == compact.selected)).Group;
                compact.Zoom(compact.scale * 1.25, compact.Center); Pump(() => !compact.groupPending && compact.nodes.Length == selectedGroup.Entries.Length);
                check(compact.calendar.SelectedDates.Min() == selectedGroup.From && compact.calendar.SelectedDates.Max() == selectedGroup.To, "toolbar and keyboard zoom open the selected group rather than the canvas-center group");
                compact.BackToOverview(); Pump(() => !compact.groupPending && compact.nodes.Length == 104);
                var rangeCard = compact.summaryCards.First(c => c.Group.From != c.Group.To);
                var range = rangeCard.Group;
                compact.Zoom(compact.scale * 1.25, new Point(rangeCard.Bounds.X + 20, rangeCard.Bounds.Y + 20), pointerTarget: true); Pump(() => !compact.groupPending && compact.nodes.Length == range.Entries.Length);
                check(compact.calendar.SelectedDates.Min() == range.From && compact.month == range.From, "wheel zoom targets the pointed group and updates the calendar month");
                check(compact.overview && compact.summaryCards.Count > 1, "opening a date range drills into smaller readable groups");
                check(compact.clearFilters.IsVisible && !compact.filterPanel.IsVisible, "compact filtered map exposes a clear action while search remains closed");
                compact.UpdateLayout();
                check(compact.mapBack.IsVisible && ControlsOutsideCanvas(compact) && compact.summaryCards.All(c => c.Bounds.Bottom <= compact.canvas.ActualHeight + 1), "active-filter range overview keeps Back available and controls outside the map");
                compact.CaptureMap(Path.Combine(directory, "map-filter-active.png"));
                compact.CaptureMap(Path.Combine(directory, "semantic-range.png"));
                compact.BackToOverview(); Pump(() => !compact.groupPending && compact.nodes.Length == 104);
                compact.Width = 800; compact.Height = 820; compact.UpdateLayout();
                compact.datesToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); compact.UpdateLayout();
                check(compact.rail.IsVisible && !compact.inspector.IsVisible, "explicit compact Dates choice opens the calendar drawer");
                compact.Width = 1280; compact.UpdateLayout(); compact.Width = 800; compact.UpdateLayout();
                check(compact.rail.IsVisible, "explicit pane choices survive width changes");
            }
            finally { compact.Close(); }

            var imageDir = Path.Combine(directory, "thumbnail-fixture"); Directory.CreateDirectory(imageDir);
            var imageStore = new HistoryStore(Path.Combine(imageDir, "map.dat"));
            var pixels = Enumerable.Range(0, 64).SelectMany(_ => new byte[] { 24, 135, 220, 255 }).ToArray();
            var bitmapFixture = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 32); bitmapFixture.Freeze();
            ImageFiles.Write(imageStore.ImagesDir, "synthetic-map-image", bitmapFixture);
            var imageItem = new ClipItem { ImageHash = "synthetic-map-image", ImageWidth = 8, ImageHeight = 8, ImageDir = imageStore.ImagesDir, CreatedAt = time, LastUsedAt = time };
            var protectedImage = new ClipItem { ImageHash = "synthetic-map-image", ImageWidth = 8, ImageHeight = 8, ImageDir = imageStore.ImagesDir, CreatedAt = time, LastUsedAt = time, IsSensitive = true };
            imageStore.Items.Add(imageItem); imageStore.Items.Add(protectedImage);
            var fixtureColors = new[] { Colors.CornflowerBlue, Colors.SeaGreen, Colors.Coral, Colors.MediumPurple };
            for (int i = 0; i < fixtureColors.Length; i++)
            {
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(fixtureColors[i]), null, new Rect(0, 0, 120, 72));
                    dc.DrawRectangle(Brushes.White, null, new Rect(10 + i * 6, 14, 34, 34));
                    dc.DrawLine(new Pen(Brushes.White, 4), new Point(55, 22), new Point(105, 22));
                    dc.DrawLine(new Pen(Brushes.White, 4), new Point(55, 36), new Point(90 + i * 4, 36));
                }
                var thumbnailFixture = new RenderTargetBitmap(120, 72, 96, 96, PixelFormats.Pbgra32); thumbnailFixture.Render(drawing); thumbnailFixture.Freeze();
                string hash = "map-sample-" + i; ImageFiles.Write(imageStore.ImagesDir, hash, thumbnailFixture);
                imageStore.Items.Add(new ClipItem { ImageHash = hash, ImageWidth = 120, ImageHeight = 72, ImageDir = imageStore.ImagesDir, CreatedAt = time.AddMinutes(i + 1), LastUsedAt = time.AddMinutes(i + 1) });
            }
            var imageWindow = new HistoryMapWindow(imageStore);
            try
            {
                imageWindow.original = imageWindow.selected = imageItem.Id; imageWindow.Show(); imageWindow.RefreshSnapshot(); Pump(() => imageWindow.nodes.Length == 6);
                imageWindow.UpdateLayout(); imageWindow.CenterOn(imageItem.Id); imageWindow.Zoom(.25, imageWindow.Center); imageWindow.UpdateLayout();
                Pump(() => imageWindow.mapThumbnailLoads.Count == 0 && imageWindow.mapThumbnails.Count >= 4);
                imageWindow.CaptureMap(Path.Combine(directory, "zoom-images-wide.png"));
                imageWindow.Width = 800; imageWindow.UpdateLayout(); imageWindow.CaptureMap(Path.Combine(directory, "zoom-images-compact.png"));
                var thumbnails = imageWindow.canvas.Children.OfType<System.Windows.Controls.Button>().Select(b => b.Content).OfType<System.Windows.Controls.DockPanel>().SelectMany(p => p.Children.OfType<System.Windows.Controls.Image>()).ToArray();
                check(thumbnails.Count(i => i.Source != null) >= 4, "zoomed-out image cards display distinct synthetic thumbnails");
                check(thumbnails.All(i => i.Stretch == Stretch.Uniform), "image previews preserve their full aspect ratio without cropping");
                var entry = imageWindow.snapshot.Single(e => e.Id == imageItem.Id);
                var holder = new System.Windows.Controls.Image(); int frame = imageWindow.BeginMapFrame();
                imageWindow.RequestMapThumbnail(holder, entry, frame); Pump(() => holder.Source != null);
                check(holder.Source is BitmapSource, "visible non-sensitive image thumbnail loads asynchronously");
                var secretHolder = new System.Windows.Controls.Image();
                imageWindow.RequestMapThumbnail(secretHolder, imageWindow.snapshot.Single(e => e.Id == protectedImage.Id), frame);
                check(secretHolder.Source == null, "sensitive image never receives a cached thumbnail");
                Pump(() => imageWindow.mapThumbnailLoads.Count == 0 && imageWindow.mapThumbnailGate.CurrentCount == 3);
                imageWindow.ClearMapThumbnails(); frame = imageWindow.BeginMapFrame();
                var invalidHolder = new System.Windows.Controls.Image();
                for (int i = 0; i < 3; i++) imageWindow.mapThumbnailGate.Wait();
                Task<BitmapSource?>? invalidWork = null;
                try { imageWindow.RequestMapThumbnail(invalidHolder, entry, frame); invalidWork = imageWindow.mapThumbnailLoads[entry.Hash!]; imageItem.IsSensitive = true; }
                finally { imageWindow.mapThumbnailGate.Release(3); }
                Pump(() => invalidWork!.IsCompleted);
                check(invalidWork!.IsCanceled && invalidHolder.Source == null && imageWindow.invalid.Contains(imageItem.Id), "sensitivity change cancels an in-flight image thumbnail");
                imageItem.IsSensitive = false; imageWindow.RefreshSnapshot(); Pump(() => imageWindow.nodes.Length == 6);
                Pump(() => imageWindow.mapThumbnailLoads.Count == 0 && imageWindow.mapThumbnailGate.CurrentCount == 3);
                entry = imageWindow.snapshot.Single(e => e.Id == imageItem.Id); imageWindow.ClearMapThumbnails(); frame = imageWindow.BeginMapFrame();
                var closedHolder = new System.Windows.Controls.Image();
                for (int i = 0; i < 3; i++) imageWindow.mapThumbnailGate.Wait();
                Task<BitmapSource?>? closedWork = null;
                try { imageWindow.RequestMapThumbnail(closedHolder, entry, frame); closedWork = imageWindow.mapThumbnailLoads[entry.Hash!]; imageWindow.Close(); }
                finally { imageWindow.mapThumbnailGate.Release(3); }
                Pump(() => closedWork!.IsCompleted);
                check(closedWork!.IsCanceled && closedHolder.Source == null && imageWindow.mapThumbnails.Count == 0, "closing map cancels queued thumbnail work and clears cache");
            }
            finally { if (!imageWindow.closed) imageWindow.Close(); }
        }
        finally { window.Close(); Motion.Enabled = motion; ((App)Application.Current).ApplyTheme(); }
        check(window.observed.Count == 0 && window.closed, "window cleanup detaches subscriptions");
    }
    void CaptureMap(string path)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight), Stretch = Stretch.Fill }, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight)); bitmap.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder value, int length, out int needed);
}

partial class PanelWindow
{
    internal static void CheckSearchPaste(string directory, Action<bool, string> check)
    {
        var panel = new PanelWindow(new HistoryStore(Path.Combine(directory, "search.dat")), new SymbolCatalog(Path.Combine(directory, "symbols.json")));
        try
        {
            const string text = "Кириллица 中文 \U0001F9AC";
            System.Windows.Clipboard.SetText(text);
            System.Windows.Input.ApplicationCommands.Paste.Execute(null, panel.Search);
            check(panel.Search.Text == text, "native paste accepts Unicode into empty search");
            panel.Search.SelectAll(); System.Windows.Clipboard.SetText("replacement");
            System.Windows.Input.ApplicationCommands.Paste.Execute(null, panel.Search);
            check(panel.Search.Text == "replacement", "native paste replaces search selection");
            System.Windows.Input.ApplicationCommands.Undo.Execute(null, panel.Search);
            check(panel.Search.Text == text, "native undo restores previous search");
            check(panel.List.Focusable && panel.PinnedList.Focusable && panel.List.FocusVisualStyle != null, "history lists expose a visible keyboard focus mode");
        }
        finally { panel.Close(); }
    }
}
