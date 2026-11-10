using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace ArkCopyBuffer;

internal sealed partial class HistoryMapWindow
{
    readonly Stack<(DateTime[] Dates, Guid Selected, DateTime Month)> groupHistory = new();
    readonly List<(Rect Bounds, MapGroup Group)> summaryCards = new();
    bool groupPending;

    void ShowOverview() { overview = true; scale = .6; Draw(); }
    async void OpenGroup(MapGroup group)
    {
        if (groupPending) return;
        var ids = nodes.Select(n => n.Entry.Id).ToHashSet();
        var live = group.Entries.Where(e => ids.Contains(e.Id)).ToArray();
        if (live.Length == 0) return;
        groupPending = true;
        groupHistory.Push((calendar.SelectedDates.ToArray(), selected, month));
        updating = true;
        calendar.SelectedDates.Clear(); calendar.SelectedDates.AddRange(group.From, group.To);
        month = group.From; DrawCalendar();
        updating = false;
        if (!live.Any(e => e.Id == selected)) selected = live[0].Id;
        overview = group.From != group.To;
        ClearPreview(false);
        debounce.Stop();
        try { await Filter(true); }
        finally { groupPending = false; if (!closed) Draw(); }
    }
    async void BackToOverview()
    {
        if (groupPending) return;
        if (!groupHistory.TryPop(out var previous)) return;
        groupPending = true;
        updating = true;
        calendar.SelectedDates.Clear();
        if (previous.Dates.Length > 0) calendar.SelectedDates.AddRange(previous.Dates.Min(), previous.Dates.Max());
        month = previous.Month; DrawCalendar();
        updating = false; selected = previous.Selected; overview = true;
        ClearPreview(false);
        debounce.Stop();
        try { await Filter(true); }
        finally { groupPending = false; if (!closed) Draw(); }
    }
    void DrawGroups(Rect visible)
    {
        summaryCards.Clear();
        double gap = visible.Height < 260 ? 6 : 12, top = visible.Height < 260 ? 28 : 42;
        int columns = Math.Max(1, (int)((visible.Width - gap) / 236));
        int capacity = Math.Clamp(columns * Math.Max(1, (int)((visible.Height - top) / 176)), 2, 12);
        var groups = HistoryMap.Groups(nodes.Select(n => n.Entry), First, capacity);
        columns = Math.Min(columns, groups.Length);
        int rows = Math.Max(1, (int)Math.Ceiling(groups.Length / (double)columns));
        double width = (visible.Width - gap * (columns + 1)) / columns;
        double height = Math.Min(176, Math.Max(54, (visible.Height - top - gap * (rows + 1)) / rows));
        var hint = new TextBlock { Text = T("Выберите группу, чтобы увидеть записи"), FontSize = visible.Height < 260 ? 12 : 14, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
        Place(hint, new Point(gap, gap), Math.Max(1, visible.Width - gap * 2), visible.Height < 260 ? 18 : 24);
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var at = new Point(gap + i % columns * (width + gap), top + gap + i / columns * (height + gap));
            string format = "d MMM yyyy";
            string title = group.From == group.To ? group.From.ToString("ddd, d MMM yyyy", Culture) : group.From.ToString(format, Culture) + " – " + group.To.ToString(format, Culture);
            var sourceGroups = group.Entries.GroupBy(e => SourceLabel(e.Source)).OrderByDescending(g => g.Count()).ToArray();
            string sourceText = string.Join(" · ", sourceGroups.Take(2).Select(g => g.Key + " (" + g.Count() + ")"));
            if (sourceGroups.Length > 2) sourceText += " +" + (sourceGroups.Length - 2);
            string sample = (group.Entries.FirstOrDefault(e => e.Id == selected && !e.Sensitive) ?? group.Entries.FirstOrDefault(e => !e.Sensitive))?.Preview ?? T("Защищённое содержимое");
            string countText = UiText.F($"Записей: {group.Entries.Length}");
            double padding = height < 120 ? 8 : 12;
            var content = new StackPanel { Width = Math.Max(1, width - padding * 2 - 2), Height = Math.Max(1, height - padding * 2 - 2) };
            content.Children.Add(new TextBlock { Text = title, FontSize = height < 120 ? 13 : 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) });
            content.Children.Add(new TextBlock { Text = countText, FontSize = 14, Margin = new Thickness(0, 0, 0, height < 120 ? 0 : 8) });
            if (height >= 100) content.Children.Add(new TextBlock { Text = sourceText, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) });
            if (height >= 150) content.Children.Add(new TextBlock { Text = sample, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 36 });
            var button = new Button { Tag = group.Entries[0].Id, Content = content, Style = (Style)FindResource("StandardButton"), Padding = new Thickness(height < 120 ? 8 : 12), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, Background = Brush("WindowSurface"), Foreground = Brush("TextPrimary"), BorderBrush = Brush(group.Entries.Any(e => e.Id == selected) ? "Accent" : "ControlStroke"), BorderThickness = new Thickness(group.Entries.Any(e => e.Id == selected) ? 2 : 1), Focusable = true, Cursor = Cursors.Hand };
            string description = title + "\n" + countText + "\n" + sourceText + "\n" + sample;
            button.ToolTip = new TextBlock { Text = description, MaxWidth = 360, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(button, description);
            button.Click += (_, _) => OpenGroup(group);
            summaryCards.Add((new Rect(at, new Size(width, height)), group));
            Place(button, at, width, height);
        }
    }
}
