using System.Windows;

namespace ArkCopyBuffer;

// Snapshot strings are immutable; no WPF objects or live store access on worker threads.
internal sealed record MapEntry(Guid Id, string Text, string? Rtf, string? Html, string Preview,
    string Source, string Kind, string? Hash, string? FileName, bool Sensitive, DateTime Created, DateTime Used, bool Mono = false)
{
    public static MapEntry Capture(ClipItem i) => new(i.Id, i.Text, i.Rtf, i.Html, i.Preview,
        i.SourceApp ?? "", i.IsImage ? "image" : i.IsNodeGraph ? "graph" : i.MediaKind ?? "text",
        i.ImageHash, i.FileName, i.IsSensitive, i.CreatedAt, i.LastUsedAt, i.IsMono);
    public DateTime Date(bool first) => (first ? Created : Used).ToLocalTime();
    public bool ContentMatches(ClipItem i) => Text == i.Text && Rtf == i.Rtf && Html == i.Html
        && Hash == i.ImageHash && FileName == i.FileName && Sensitive == i.IsSensitive && Source == (i.SourceApp ?? "");
    public bool Matches(string query, string? source, string? kind, DateTime? from, DateTime? to, bool first) =>
        (source == null || Source == source) && (kind == null || Kind == kind)
        && (!from.HasValue || Date(first).Date >= from.Value.Date) && (!to.HasValue || Date(first).Date <= to.Value.Date)
        && (query.Length == 0 || !Sensitive && (Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Preview.Contains(query, StringComparison.OrdinalIgnoreCase) || (FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)));
}

internal sealed record MapNode(MapEntry Entry, Point Position);
internal sealed record MapGroup(DateTime From, DateTime To, MapEntry[] Entries);
internal static class HistoryMap
{
    public static MapGroup[] Groups(IEnumerable<MapEntry> entries, bool first, int capacity)
    {
        var days = entries.GroupBy(e => e.Date(first).Date).OrderBy(g => g.Key).ToArray();
        int batch = Math.Max(1, (int)Math.Ceiling(days.Length / (double)Math.Max(1, capacity)));
        return days.Chunk(batch).Select(chunk => new MapGroup(chunk[0].Key, chunk[^1].Key, chunk.SelectMany(g => g).ToArray())).ToArray();
    }

    public static MapNode[] Layout(IEnumerable<MapEntry> entries, bool first, Guid anchor = default)
    {
        var ordered = entries.OrderBy(e => first ? e.Created : e.Used).ThenBy(e => e.Id).ToArray();
        // Positions stay stable as the focus changes; connections follow recorded chronology.
        double[] heights = [0, -95, -45, 45, 95];
        return ordered.Select((e, index) => new MapNode(e, new Point(index * 340.0, heights[index % heights.Length]))).ToArray();
    }

    public static Rect Bounds(IEnumerable<MapNode> nodes)
    {
        var bounds = Rect.Empty;
        foreach (var node in nodes) bounds.Union(new Rect(node.Position, new Size(280, 140)));
        return bounds;
    }
}
