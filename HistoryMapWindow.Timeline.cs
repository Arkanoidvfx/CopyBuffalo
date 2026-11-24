using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace ArkCopyBuffer;

internal sealed record HistoryMatch(string Before, string Hit, string After)
{
    public static HistoryMatch Excerpt(MapEntry entry, string query)
    {
        if (entry.Sensitive || query.Length == 0) return new(entry.Preview, "", "");
        var text = entry.Hash == null ? entry.Text : entry.Preview;
        int hit = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (hit < 0) return new(entry.Preview, "", "");
        int start = hit == 0 ? 0 : Math.Max(text.LastIndexOf('\n', hit - 1) + 1, hit - 22);
        int end = Math.Min(text.Length - hit - query.Length, 100);
        return new((start > 0 ? "…" : "") + text[start..hit], text.Substring(hit, query.Length), text.Substring(hit + query.Length, end));
    }
}

internal sealed class HistoryTimelineEntry(MapEntry entry, string day, string time, string source, string query)
{
    public MapEntry Entry { get; } = entry;
    public string Day { get; } = day;
    public string Time { get; } = time;
    public string Source { get; } = source;
    HistoryMatch? excerpt;
    public HistoryMatch Excerpt => excerpt ??= HistoryMatch.Excerpt(Entry, query);
    public bool HasImage => Entry is { Sensitive: false, Kind: "image", Hash: not null };
    public string Glyph => Entry.Sensitive ? "\uE72E" : Entry.Kind switch
    {
        "image" => "\uEB9F", "files" or "folder" => "\uE8B7", "video" => "\uE714",
        "audio" => "\uE8D6", "graph" => "\uE8FD", _ => "\uE8A5"
    };
    public string AccessibleName => $"{Day} {Time} · {Source} · {Entry.Preview}";
    public override string ToString() => AccessibleName;
}

public sealed class HistoryTimelineImage : Image
{
    public HistoryTimelineImage()
    {
        Loaded += (_, _) => Load();
        DataContextChanged += (_, _) => Load();
    }

    void Load()
    {
        Source = null;
        if (IsLoaded && DataContext is HistoryTimelineEntry { HasImage: true } row && Window.GetWindow(this) is HistoryMapWindow window)
            window.RequestTimelineThumbnail(this, row);
    }
}

internal sealed partial class HistoryMapWindow
{
    readonly ListBox timeline = new();
    readonly DockPanel timelinePane = new();
    readonly TextBlock timelineMonth = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock timelineCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly Grid mapHost = new();
    readonly GridSplitter timelineDivider = new() { Width = 12, Focusable = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndCurrent, KeyboardIncrement = 20 };
    double listShare = .39;
    readonly RadioButton timelineTab, mapTab;
    bool timelineMode = true;
    MapNode[]? timelineNodes;
    HistoryTimelineEntry[] timelineRows = [];

    void BuildTimeline()
    {
        timeline.SetResourceReference(Control.BackgroundProperty, "WindowSurface");
        timeline.BorderThickness = new Thickness(0);
        AutomationProperties.SetName(timeline, T("Хронология"));
        var style = new Style(typeof(ListBoxItem), (Style)FindResource("TimelineRow"));
        style.Setters.Add(new EventSetter(MouseEnterEvent, new MouseEventHandler((sender, _) =>
        {
            if (sender is ListBoxItem { Content: HistoryTimelineEntry row }) ShowReading(row.Entry);
        })));
        timeline.ItemContainerStyle = style;
        VirtualizingPanel.SetVirtualizationMode(timeline, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(timeline, ScrollBarVisibility.Disabled);
        timeline.SelectionChanged += (_, _) =>
        {
            if (!updating && timeline.SelectedItem is HistoryTimelineEntry row && selected != row.Entry.Id) Select(row.Entry.Id);
        };
        var heading = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        timelineCount.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        DockPanel.SetDock(timelineCount, Dock.Right); heading.Children.Add(timelineCount); heading.Children.Add(timelineMonth);
        DockPanel.SetDock(heading, Dock.Top); timelinePane.Children.Add(heading);
        timelinePane.Children.Add(timeline); mapHost.Children.Add(timelinePane);
        timelineDivider.Style = (Style)FindResource("HistoryDivider");
        AutomationProperties.SetName(timelineDivider, T("Ширина списка")); timelineDivider.ToolTip = T("Ширина списка");
        timelineDivider.Visibility = Visibility.Collapsed;
        Grid.SetColumn(timelineDivider, 1); mapHost.Children.Add(timelineDivider);
        timeline.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            int first = (int)e.VerticalOffset;
            if (first >= 0 && first < timelineRows.Length) SetTimelineMonth(timelineRows[first]);
        }));
    }

    void SetTimelineMonth(HistoryTimelineEntry row) => timelineMonth.Text = Culture.TextInfo.ToTitleCase(row.Entry.Date(First).ToString("Y", Culture));

    async void FocusSearchResult()
    {
        string query = search.Text.Trim();
        if (debounce.IsEnabled || query != appliedQuery) { debounce.Stop(); await Filter(); }
        if (closed || !timelineMode || !search.IsKeyboardFocusWithin || query != appliedQuery || query != search.Text.Trim() || timeline.SelectedItem == null) return;
        timeline.ScrollIntoView(timeline.SelectedItem); timeline.UpdateLayout();
        (timeline.ItemContainerGenerator.ContainerFromItem(timeline.SelectedItem) as ListBoxItem)?.Focus();
    }

    void CompactToolbar(Button button, string glyph, bool compact)
    {
        var name = AutomationProperties.GetName(button);
        button.Content = compact ? glyph : name;
        button.SetResourceReference(Control.FontFamilyProperty, compact ? "IconFont" : "TextFont");
        button.ToolTip = compact ? name : null;
        ToolTipService.SetInitialShowDelay(button, 0);
    }

    internal void RequestTimelineThumbnail(Image image, HistoryTimelineEntry row) =>
        _ = RequestMapThumbnail(image, row.Entry, null, () => image.IsLoaded && ReferenceEquals(image.DataContext, row));

    void SetTimeline(bool enabled)
    {
        timelineMode = enabled; overview = false;
        TimelineLayout();
        CenterOn(selected);
    }

    void TimelineLayout()
    {
        bool split = timelineMode && nodes.Length > 0 && mapHost.ActualWidth >= 700;
        timelinePane.Visibility = timelineMode && nodes.Length != 0 ? Visibility.Visible : Visibility.Collapsed;
        canvas.Visibility = timelineMode && nodes.Length != 0 ? Visibility.Collapsed : Visibility.Visible;
        toolSurface.Visibility = timelineMode ? Visibility.Collapsed : Visibility.Visible;
        if (split != (timelineDivider.Visibility == Visibility.Visible))
        {
            var left = mapHost.ColumnDefinitions[0]; var right = mapHost.ColumnDefinitions[1];
            if (!split) listShare = left.Width.Value / (left.Width.Value + right.Width.Value);
            left.MinWidth = split ? 230 : 0; right.MinWidth = split ? 300 : 0;
            left.Width = new GridLength(split ? listShare : 1, GridUnitType.Star);
            right.Width = split ? new GridLength(1 - listShare, GridUnitType.Star) : new GridLength(0);
            timelineDivider.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        }
        Grid.SetColumn(reader, split ? 1 : 0); Grid.SetRow(reader, split ? 0 : 1);
        reader.Margin = split ? new Thickness(16, 0, 0, 0) : new Thickness(0, 8, 0, 0);
        reader.Height = split ? double.NaN : Math.Clamp(mapHost.ActualHeight * (timelineMode ? .48 : .30), 90, 320);
        timelineTab.IsChecked = timelineMode; mapTab.IsChecked = !timelineMode;
    }

    void SyncTimeline()
    {
        updating = true;
        if (!ReferenceEquals(timelineNodes, nodes))
        {
            timelineNodes = nodes;
            DateTime? last = null;
            timelineRows = nodes.Reverse().Select(node =>
            {
                var e = node.Entry;
                var date = e.Date(First);
                string day = last == date.Date ? "" : date.ToString("dddd, d MMMM", Culture);
                last = date.Date;
                return new HistoryTimelineEntry(e, day, date.ToString("t", Culture), string.IsNullOrWhiteSpace(e.Source) ? KindLabel(e.Kind) : e.Source, appliedQuery);
            }).ToArray();
            timeline.ItemsSource = timelineRows;
            timelineCount.Text = UiText.F($"Записей: {nodes.Length}");
            if (timelineRows.Length > 0) SetTimelineMonth(timelineRows[0]);
        }
        int index = selectedIndex < 0 ? -1 : nodes.Length - 1 - selectedIndex;
        if (timeline.SelectedIndex != index)
        {
            timeline.SelectedIndex = index;
            if (index >= 0) timeline.ScrollIntoView(timelineRows[index]);
        }
        updating = false;
    }
}
