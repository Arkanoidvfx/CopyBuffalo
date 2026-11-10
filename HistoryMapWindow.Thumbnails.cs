using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

internal sealed partial class HistoryMapWindow
{
    readonly Dictionary<string, BitmapSource> mapThumbnails = [];
    readonly Dictionary<string, Task<BitmapSource?>> mapThumbnailLoads = [];
    readonly SemaphoreSlim mapThumbnailGate = new(3, 3);
    CancellationTokenSource mapThumbnailCancellation = new();
    int mapThumbnailFrame;

    // A frame belongs to exactly one generation of visible cards.
    int BeginMapFrame() => ++mapThumbnailFrame;
    void ClearMapThumbnails()
    {
        mapThumbnailFrame++; mapThumbnails.Clear(); mapThumbnailLoads.Clear();
        mapThumbnailCancellation.Cancel(); mapThumbnailCancellation.Dispose();
        if (!closed) mapThumbnailCancellation = new();
    }

    async Task<BitmapSource?> ReadMapThumbnail(string path, CancellationToken token)
    {
        await mapThumbnailGate.WaitAsync(token);
        try { return await Task.Run(() => ImageFiles.LoadBitmap(path, 160), token); }
        finally { mapThumbnailGate.Release(); }
    }

    async void RequestMapThumbnail(Image holder, MapEntry entry, int frame)
    {
        if (entry.Sensitive || entry.Kind != "image" || entry.Hash == null) return;
        bool Current() => !closed && frame == mapThumbnailFrame && !invalid.Contains(entry.Id)
            && snapshot.Any(e => ReferenceEquals(e, entry))
            && store.Items.FirstOrDefault(i => i.Id == entry.Id) is { } live && !live.IsSensitive && entry.ContentMatches(live);
        if (!Current()) return;
        if (mapThumbnails.TryGetValue(entry.Hash, out var cached)) { holder.Source = cached; return; }

        Task<BitmapSource?>? load = null;
        try
        {
            while (!mapThumbnailLoads.TryGetValue(entry.Hash, out load))
            {
                foreach (var finished in mapThumbnailLoads.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray()) mapThumbnailLoads.Remove(finished);
                if (mapThumbnailLoads.Count < 48)
                {
                    load = ReadMapThumbnail(Path.Combine(store.ImagesDir, entry.Hash + ".thumb.dat"), mapThumbnailCancellation.Token);
                    mapThumbnailLoads.Add(entry.Hash, load);
                    break;
                }
                await mapThumbnailLoads.Values.First();
                if (!Current()) return;
            }
            if (load == null) return;
            var bitmap = await load;
            if (bitmap == null || !Current()) return;
            if (mapThumbnails.Count >= 64) mapThumbnails.Remove(mapThumbnails.Keys.First());
            mapThumbnails[entry.Hash] = bitmap;
            holder.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (load != null && load.IsCompleted && mapThumbnailLoads.TryGetValue(entry.Hash, out var current) && ReferenceEquals(current, load)) mapThumbnailLoads.Remove(entry.Hash);
        }
    }
}
