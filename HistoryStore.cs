using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

public sealed class ClipItem : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; init; } = "";
    public string? Rtf { get; set; }
    public string? Html { get; set; }
    public DateTime CreatedAt { get; init; }
    DateTime _lastUsedAt;
    /// <summary>Когда запись последний раз попала в буфер (копирование или вставка через панель).</summary>
    public DateTime LastUsedAt
    {
        get => _lastUsedAt;
        set { _lastUsedAt = value; PropertyChanged?.Invoke(this, new(nameof(TimeLabel))); }
    }

    /// <summary>Сегодня — «15:32», вчера — «вчера», раньше — «22.09».</summary>
    [JsonIgnore]
    public string TimeLabel
    {
        get
        {
            var t = LastUsedAt.ToLocalTime();
            return (DateTime.Today - t.Date).Days switch { 0 => t.ToString("HH:mm"), 1 => "вчера", _ => t.ToString("dd.MM") };
        }
    }

    /// <summary>
    /// Ключ двоичных данных (картинка, GIF, видео, аудио, текстовый файл): SHA-256. Файлы — images/{hash}.dat и {hash}.thumb.dat.
    /// Для картинок хэш пикселей, для остального — хэш байтов файла.
    /// </summary>
    public string? ImageHash { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    /// <summary>GIF хранится байт-в-байт (анимация), живёт 1 день вместо 7.</summary>
    public bool IsGif { get; init; }

    /// <summary>Файл из Проводника: "video", "audio" или "text". Живёт 1 день, вставляется файлом.</summary>
    public string? MediaKind { get; init; }
    public string? FileName { get; init; }
    public long FileSize { get; init; }
    /// <summary>У видео есть миниатюра-кадр (если её дал Проводник).</summary>
    public bool HasThumb { get; init; }

    bool _pinned;
    public bool Pinned
    {
        get => _pinned;
        set { _pinned = value; PropertyChanged?.Invoke(this, new(nameof(Pinned))); }
    }

    public Guid? PinnedGroupId { get; set; }
    public int PinnedOrder { get; set; }
    int _tileColor = -1;
    public int TileColor
    {
        get => _tileColor;
        set
        {
            _tileColor = value is >= 0 and < 7 ? value : -1;
            PropertyChanged?.Invoke(this, new(nameof(TileTint)));
        }
    }
    public static readonly string[] TileColorNames = ["Красный", "Оранжевый", "Жёлтый", "Зелёный", "Бирюзовый", "Синий", "Фиолетовый"];
    static readonly Color[] TileColors = [
        Color.FromRgb(232, 100, 105), Color.FromRgb(239, 151, 87), Color.FromRgb(225, 187, 81),
        Color.FromRgb(110, 190, 123), Color.FromRgb(77, 191, 185), Color.FromRgb(103, 156, 233),
        Color.FromRgb(175, 129, 224)];
    public static Brush ColorAccent(int color) => new SolidColorBrush(TileColors[color]);
    [JsonIgnore] public Brush? TileTint => TileColor < 0 ? null : new SolidColorBrush(Color.FromArgb(105, TileColors[TileColor].R, TileColors[TileColor].G, TileColors[TileColor].B));

    [JsonIgnore] public bool IsMedia => MediaKind != null;
    [JsonIgnore] public bool IsImage => ImageHash != null && !IsMedia;
    [JsonIgnore] public bool IsUnrealGraph => Text.StartsWith("Begin Object Class=/Script/", StringComparison.Ordinal)
        && (Text.Contains("MaterialGraphNode", StringComparison.Ordinal) || Text.Contains("K2Node_", StringComparison.Ordinal))
        && Text.Contains("CustomProperties Pin", StringComparison.Ordinal);
    [JsonIgnore] public bool IsUnityGraph => Text.Contains("UnityEditor.ShaderGraph.CopyPasteGraph", StringComparison.Ordinal)
        || (Text.Contains("\"m_SerializableNodes\"", StringComparison.Ordinal)
            && Text.Contains("\"m_SerializableEdges\"", StringComparison.Ordinal));
    [JsonIgnore] public bool IsNodeGraph => IsUnrealGraph || IsUnityGraph;
    [JsonIgnore] public string GraphLabel => IsUnityGraph ? "Ноды Unity Shader Graph" : "Ноды Unreal Engine";
    /// <summary>Живёт 1 день и вставляется файлом.</summary>
    [JsonIgnore] public bool IsShortLived => IsGif || IsMedia;
    /// <summary>Плитке показывать картинку, а не текст.</summary>
    [JsonIgnore] public bool ShowTileImage => IsImage || HasThumb;
    [JsonIgnore] public string? Badge => IsGif ? "GIF" : MediaKind switch { "video" => "Видео", "audio" => "Аудио", "text" => "Текстовый файл", _ => null };
    [JsonIgnore] public bool HasBadge => Badge != null;
    [JsonIgnore] public string Glyph => MediaKind switch { "audio" => "\uE8D6", "text" => "\uE8A5", _ => "\uE714" };
    [JsonIgnore] public string MediaMeta => MediaKind == "text" && FileSize < 1024
        ? $"{Badge} · {FileSize} Б"
        : MediaKind == "text" && FileSize < 1048576
        ? $"{Badge} · {FileSize / 1024.0:0.#} КБ"
        : $"{Badge} · {FileSize / 1048576.0:0.#} МБ";

    /// <summary>Текст для карточки: без хвостовых пробелов, не длиннее пары экранов.</summary>
    [JsonIgnore]
    public string Preview => IsMedia ? FileName ?? ""
        : IsImage ? $"{(IsGif ? "GIF" : "Изображение")} {ImageWidth} × {ImageHeight}"
        : IsNodeGraph ? GraphLabel
        : (Text.Length > 600 ? Text[..600] : Text).Replace('\t', ' ').Trim();

    [JsonIgnore] internal string? ImageDir { get; set; }
    ImageSource? _thumb;
    ImageSource? _graphPreview;

    [JsonIgnore] public ImageSource? GraphPreview => _graphPreview ??= IsNodeGraph ? UnrealGraph.Render(Text, IsUnityGraph) : null;

    /// <summary>Миниатюра. Биндинг с IsAsync=True: грузится в фоне, UI не ждёт.</summary>
    [JsonIgnore]
    public ImageSource? Thumbnail => _thumb ??= ImageDir != null && ImageHash != null
        ? ImageFiles.LoadBitmap(Path.Combine(ImageDir, ImageHash + ".thumb.dat"))
        : null;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PinnedGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    [JsonIgnore] public bool IsGroupHeader => true;
}

/// <summary>Файлы картинок: PNG, зашифрованный DPAPI (на скриншотах бывает приватное).</summary>
static class ImageFiles
{
    static readonly byte[] Entropy = "ArkCopyBuffer.img.v1"u8.ToArray();
    const int ThumbWidth = 320;

    public static byte[] Seal(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public static byte[] Open(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    /// <summary>Хэш пикселей в едином формате: одна и та же картинка из PNG и из DIB даёт один хэш.</summary>
    public static (BitmapSource bgra, string hash) Normalize(BitmapSource src)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        int rowsPerChunk = Math.Max(1, 1048576 / stride);
        var pixels = new byte[stride * Math.Min(rowsPerChunk, bgra.PixelHeight)];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int y = 0; y < bgra.PixelHeight; y += rowsPerChunk)
        {
            int rows = Math.Min(rowsPerChunk, bgra.PixelHeight - y);
            bgra.CopyPixels(new System.Windows.Int32Rect(0, y, bgra.PixelWidth, rows), pixels, stride, 0);
            sha.AppendData(pixels.AsSpan(0, stride * rows));
        }
        return (bgra, Convert.ToHexString(sha.GetHashAndReset()));
    }

    /// <summary>Пишет полную картинку (PNG) и миниатюру, если их ещё нет. Вызывать не из UI-потока.</summary>
    public static void Write(string dir, string hash, BitmapSource bgra) => Write(dir, hash, () => EncodePng(bgra), bgra);

    /// <summary>Видео/аудио: полный файл — исходные байты, миниатюра — если есть.</summary>
    public static void WriteMedia(string dir, string hash, byte[] file, BitmapSource? thumb) => Write(dir, hash, () => file, thumb);

    /// <summary>GIF: полный файл — исходные байты (анимация), миниатюра — первый кадр.</summary>
    public static void WriteGif(string dir, string hash, byte[] gif, BitmapSource firstFrame) => Write(dir, hash, () => gif, firstFrame);

    static void Write(string dir, string hash, Func<byte[]> fullBytes, BitmapSource? bgra)
    {
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, hash + ".dat");
        var thumb = Path.Combine(dir, hash + ".thumb.dat");
        if (!File.Exists(full)) WriteAtomic(full, Seal(fullBytes()));
        if (bgra != null && !File.Exists(thumb))
        {
            double k = Math.Min(1.0, (double)ThumbWidth / bgra.PixelWidth);
            var small = k < 1 ? new TransformedBitmap(bgra, new ScaleTransform(k, k)) : bgra;
            WriteAtomic(thumb, Seal(EncodePng(small)));
        }
    }

    /// <summary>Полный файл: PNG для картинок, исходный GIF для гифок.</summary>
    public static byte[] ReadFull(string dir, string hash) => Open(File.ReadAllBytes(Path.Combine(dir, hash + ".dat")));

    public static BitmapSource? LoadBitmap(string sealedPath)
    {
        try { return Decode(Open(File.ReadAllBytes(sealedPath))); }
        catch { return null; } // файл удалён/битый — карточка покажется без миниатюры
    }

    public static BitmapSource Decode(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static void Delete(string dir, string hash)
    {
        foreach (var f in new[] { hash + ".dat", hash + ".thumb.dat" })
            try { File.Delete(Path.Combine(dir, f)); } catch { }
    }

    static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static void WriteAtomic(string path, byte[] data)
    {
        File.WriteAllBytes(path + ".tmp", data);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>
/// История буфера. Только UI-поток. Порядок в Items — по LastUsedAt, свежие сверху.
/// Незакреплённые живут 7 дней и не больше 100 штук; закреплённые — всегда.
/// На диске — JSON, зашифрованный DPAPI (CurrentUser): в истории бывают пароли и токены.
/// Картинки — отдельными файлами в images/, чтобы не переписывать их при каждом сохранении.
/// </summary>
public sealed class HistoryStore
{
    sealed class HistoryFile
    {
        public List<ClipItem> Items { get; set; } = [];
        public List<PinnedGroup> Groups { get; set; } = [];
    }
    public const int MaxUnpinned = 100;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    /// <summary>GIF, видео, аудио: тяжёлые и обычно нужны «здесь и сейчас».</summary>
    public static readonly TimeSpan ShortMaxAge = TimeSpan.FromDays(1);
    static readonly byte[] Entropy = "ArkCopyBuffer.v1"u8.ToArray();

    readonly string _path;
    public string ImagesDir { get; }
    public ObservableCollection<ClipItem> Items { get; } = new();
    public List<PinnedGroup> Groups { get; } = [];
    public event Action? Changed;

    public HistoryStore(string path)
    {
        _path = path;
        ImagesDir = Path.Combine(Path.GetDirectoryName(path)!, "images");
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArkCopyBuffer", "history.dat");

    public void Add(string text, string? rtf, string? html, DateTime nowUtc)
    {
        var existing = Items.FirstOrDefault(i => !i.IsImage && i.Text == text);
        if (existing != null)
        {
            // Повтор (в т.ч. наша же вставка) — поднимаем. Форматы не затираем null'ом:
            // после «вставить как текст» в буфере только текст, а форматирование записи терять нельзя.
            existing.Rtf = rtf ?? existing.Rtf;
            existing.Html = html ?? existing.Html;
            Bump(existing, nowUtc);
        }
        else Insert(new ClipItem { Text = text, Rtf = rtf, Html = html, CreatedAt = nowUtc, LastUsedAt = nowUtc }, nowUtc);
    }

    /// <summary>Картинка, файлы которой уже записаны ImageFiles.Write.</summary>
    public void AddImage(string hash, int width, int height, DateTime nowUtc, bool gif = false) =>
        AddBlob(new ClipItem { ImageHash = hash, ImageWidth = width, ImageHeight = height, IsGif = gif, CreatedAt = nowUtc, LastUsedAt = nowUtc }, nowUtc);

    /// <summary>Видео, аудио или текстовый файл, уже записанный ImageFiles.WriteMedia.</summary>
    public void AddMedia(string hash, string kind, string fileName, long size, bool hasThumb, DateTime nowUtc) =>
        AddBlob(new ClipItem { ImageHash = hash, MediaKind = kind, FileName = fileName, FileSize = size, HasThumb = hasThumb, CreatedAt = nowUtc, LastUsedAt = nowUtc }, nowUtc);

    void AddBlob(ClipItem fresh, DateTime nowUtc)
    {
        var existing = Items.FirstOrDefault(i => i.ImageHash == fresh.ImageHash);
        if (existing != null) { Bump(existing, nowUtc); return; }
        fresh.ImageDir = ImagesDir;
        Insert(fresh, nowUtc);
    }

    void Bump(ClipItem item, DateTime nowUtc)
    {
        item.LastUsedAt = nowUtc;
        Items.Move(Items.IndexOf(item), 0);
        Prune(nowUtc);
        Changed?.Invoke();
    }

    void Insert(ClipItem item, DateTime nowUtc)
    {
        Items.Insert(0, item);
        Prune(nowUtc);
        Changed?.Invoke();
    }

    /// <summary>Единая точка удаления: вместе с записью уходят файлы картинки.</summary>
    void Drop(ClipItem item)
    {
        if (Items.Remove(item) && item.ImageHash != null) ImageFiles.Delete(ImagesDir, item.ImageHash);
    }

    public void Remove(ClipItem item) { Drop(item); Changed?.Invoke(); }

    public void TogglePin(ClipItem item)
    {
        if (!Items.Contains(item)) return;
        item.Pinned = !item.Pinned;
        if (item.Pinned)
        {
            item.PinnedGroupId = null;
            item.PinnedOrder = Items.Where(i => i.Pinned && i != item && i.PinnedGroupId == null)
                .Select(i => i.PinnedOrder).DefaultIfEmpty(-1).Max() + 1;
        }
        else item.PinnedGroupId = null;
        Changed?.Invoke();
    }

    public IReadOnlyList<ClipItem> PinnedIn(Guid? groupId) => Items
        .Where(i => i.Pinned && i.PinnedGroupId == groupId)
        .OrderBy(i => i.PinnedOrder).ThenByDescending(i => i.LastUsedAt).ToList();

    public void SetTileColor(ClipItem item, int color)
    {
        if (!Items.Contains(item) || color is < -1 or >= 7) return;
        item.TileColor = color;
        Changed?.Invoke();
    }

    public PinnedGroup? AddGroup(string name)
    {
        name = name.Trim();
        if (name.Length == 0) name = NextGroupName();
        if (name.Length > 40 || Groups.Any(g => string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase))) return null;
        var group = new PinnedGroup { Name = name };
        Groups.Add(group);
        Changed?.Invoke();
        return group;
    }

    public string NextGroupName()
    {
        for (int number = 1; ; number++)
        {
            string name = $"Группа {number}";
            if (Groups.All(g => !string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase))) return name;
        }
    }

    public bool RenameGroup(PinnedGroup group, string name)
    {
        name = name.Trim();
        if (!Groups.Contains(group) || name.Length is < 1 or > 40 ||
            Groups.Any(g => g != group && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase))) return false;
        group.Name = name;
        Changed?.Invoke();
        return true;
    }

    public void DeleteGroup(PinnedGroup group)
    {
        if (!Groups.Remove(group)) return;
        int next = PinnedIn(null).Select(i => i.PinnedOrder).DefaultIfEmpty(-1).Max() + 1;
        foreach (var item in PinnedIn(group.Id)) { item.PinnedGroupId = null; item.PinnedOrder = next++; }
        Changed?.Invoke();
    }

    public void MovePinned(ClipItem item, Guid? groupId, ClipItem? target = null, bool after = false)
    {
        if (!Items.Contains(item) || !item.Pinned || target == item || groupId != null && Groups.All(g => g.Id != groupId) ||
            target != null && (!target.Pinned || target.PinnedGroupId != groupId || !Items.Contains(target))) return;
        var oldGroup = item.PinnedGroupId;
        var destination = PinnedIn(groupId).Where(i => i != item).ToList();
        int index = target == null ? destination.Count : destination.IndexOf(target) + (after ? 1 : 0);
        if (index < 0) return;
        destination.Insert(index, item);
        item.PinnedGroupId = groupId;
        for (int i = 0; i < destination.Count; i++) destination[i].PinnedOrder = i;
        if (oldGroup != groupId)
        {
            var source = PinnedIn(oldGroup);
            for (int i = 0; i < source.Count; i++) source[i].PinnedOrder = i;
        }
        Changed?.Invoke();
    }

    public void ClearUnpinned()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList()) Drop(i);
        Changed?.Invoke();
    }

    /// <returns>true, если что-то удалено.</returns>
    public bool Prune(DateTime nowUtc)
    {
        var unpinned = Items.Where(i => !i.Pinned).OrderByDescending(i => i.LastUsedAt).ToList();
        var drop = unpinned.Where((i, idx) => idx >= MaxUnpinned || nowUtc - i.LastUsedAt > (i.IsShortLived ? ShortMaxAge : MaxAge)).ToList();
        foreach (var i in drop) Drop(i);
        return drop.Count > 0;
    }

    public void Save()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new HistoryFile { Items = Items.ToList(), Groups = Groups });
        var data = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, _path, overwrite: true); // атомарная замена: обрыв посреди записи не портит историю
    }

    public void Load(DateTime nowUtc)
    {
        Items.Clear();
        Groups.Clear();
        if (File.Exists(_path))
        {
            try
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                using var document = JsonDocument.Parse(json);
                bool legacy = document.RootElement.ValueKind == JsonValueKind.Array;
                var file = legacy
                    ? new HistoryFile { Items = JsonSerializer.Deserialize<List<ClipItem>>(json) ?? [] }
                    : JsonSerializer.Deserialize<HistoryFile>(json) ?? new HistoryFile();
                Groups.AddRange((file.Groups ?? []).Where(g => g.Id != Guid.Empty && !string.IsNullOrWhiteSpace(g.Name)));
                var list = file.Items ?? [];
                foreach (var i in list.OrderByDescending(i => i.LastUsedAt)) { i.ImageDir = ImagesDir; Items.Add(i); }
                foreach (var i in Items.Where(i => i.PinnedGroupId != null && Groups.All(g => g.Id != i.PinnedGroupId))) i.PinnedGroupId = null;
                if (legacy)
                {
                    int order = 0;
                    foreach (var i in Items.Where(i => i.Pinned)) i.PinnedOrder = order++;
                }
            }
            catch (Exception e) when (e is CryptographicException or JsonException)
            {
                // Не теряем молча: откладываем битый файл рядом и начинаем с пустой истории.
                var dir = Path.GetDirectoryName(_path)!;
                File.Move(_path, Path.Combine(dir, $"history.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.dat"), overwrite: true);
                Items.Clear();
                Groups.Clear();
            }
        }
        if (Prune(nowUtc)) Save();
        DeleteOrphanImages();
    }

    /// <summary>Файлы картинок без записи (сбой до сохранения истории, битая история) — удаляем.</summary>
    void DeleteOrphanImages()
    {
        if (!Directory.Exists(ImagesDir)) return;
        var known = Items.Where(i => i.ImageHash != null).Select(i => i.ImageHash!).ToHashSet();
        foreach (var f in Directory.GetFiles(ImagesDir))
        {
            var hash = Path.GetFileName(f).Split('.')[0];
            if (!known.Contains(hash)) try { File.Delete(f); } catch { }
        }
    }

    /// <summary>Самопроверка: ArkCopyBuffer.exe --selftest (exit code 0 = ок).</summary>
    public static int SelfTest(string logPath)
    {
        var log = new StringBuilder();
        void Check(bool ok, string what) { log.AppendLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) throw new Exception(what); }

        var dir = Path.Combine(Path.GetTempPath(), "ArkCopyBuffer-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var s = new HistoryStore(Path.Combine(dir, "history.dat"));

            s.Add("a", "{\\rtf a}", null, t0);
            s.Add("b", null, null, t0.AddMinutes(1));
            s.Add("a", null, null, t0.AddMinutes(2));
            Check(s.Items.Count == 2 && s.Items[0].Text == "a", "дубль поднимается наверх, не дублируется");
            Check(s.Items[0].Rtf == "{\\rtf a}", "повтор без форматов не затирает RTF");

            s.TogglePin(s.Items.Single(i => i.Text == "b"));
            s.Add("old", null, null, t0.AddMinutes(3));
            s.Prune(t0.AddDays(8));
            Check(s.Items.Select(i => i.Text).SequenceEqual(["b"]), "старше 7 дней удаляются, закреплённые остаются");

            for (int i = 0; i < 150; i++) s.Add($"n{i}", null, null, t0.AddDays(10).AddSeconds(i));
            Check(s.Items.Count(i => !i.Pinned) == MaxUnpinned, "лимит 100 незакреплённых");
            Check(s.Items.Any(i => i.Text == "n149") && !s.Items.Any(i => i.Text == "n49"), "лимит режет самые старые");
            Check(s.Items.Any(i => i.Text == "b" && i.Pinned), "закреплённая переживает лимит");

            // Картинки: одна и та же картинка в разных форматах пикселей → один хэш, одна запись.
            var px = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 10, 20, 30, 255 };
            var bgra = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, px, 8);
            var bgr = new FormatConvertedBitmap(bgra, PixelFormats.Bgr32, null, 0);
            var (n1, h1) = ImageFiles.Normalize(bgra);
            var (_, h2) = ImageFiles.Normalize(bgr);
            Check(h1 == h2, "хэш картинки не зависит от формата пикселей");
            var largePixels = new byte[513 * 513 * 4];
            Random.Shared.NextBytes(largePixels);
            var large = BitmapSource.Create(513, 513, 96, 96, PixelFormats.Bgra32, null, largePixels, 513 * 4);
            Check(ImageFiles.Normalize(large).hash == Convert.ToHexString(SHA256.HashData(largePixels)), "хэш картинки верен при обработке по частям");
            ImageFiles.Write(s.ImagesDir, h1, n1);
            s.AddImage(h1, 2, 2, t0.AddDays(10).AddMinutes(5));
            s.AddImage(h1, 2, 2, t0.AddDays(10).AddMinutes(6));
            Check(s.Items.Count(i => i.IsImage) == 1 && s.Items[0].IsImage, "дубль картинки поднимается, не дублируется");
            var png = ImageFiles.ReadFull(s.ImagesDir, h1);
            Check(ImageFiles.Normalize(ImageFiles.Decode(png)).hash == h1, "картинка читается обратно без потерь");
            Check(ClipboardWatcher.FitsPng(new MemoryStream(png)), "обычный PNG проходит проверку размера до декодирования");
            var clipboardImage = new System.Windows.DataObject();
            clipboardImage.SetData("PNG", new MemoryStream(png));
            Check(ClipboardWatcher.ReadImage(clipboardImage) is { PixelWidth: 2, PixelHeight: 2 }, "PNG из буфера декодируется после проверки размера");
            var bmpEncoder = new BmpBitmapEncoder();
            bmpEncoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bgra, PixelFormats.Bgr24, null, 0)));
            using var bmpStream = new MemoryStream();
            bmpEncoder.Save(bmpStream);
            var dibClipboardImage = new System.Windows.DataObject();
            dibClipboardImage.SetData(System.Windows.DataFormats.Dib, new MemoryStream(bmpStream.ToArray()[14..]));
            var decodedDib = ClipboardWatcher.ReadImage(dibClipboardImage);
            Check(decodedDib is { PixelWidth: 2, PixelHeight: 2 }, "DIB из буфера декодируется");
            Check(Task.Run(() => ImageFiles.Normalize(decodedDib!).hash).GetAwaiter().GetResult() == h1,
                "DIB из буфера обрабатывается в фоновом потоке");
            var hugePng = (byte[])png.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hugePng.AsSpan(16, 4), 100_000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hugePng.AsSpan(20, 4), 100_000);
            Check(!ClipboardWatcher.FitsPng(new MemoryStream(hugePng)), "слишком большой PNG отсекается до декодирования");
            var hugeClipboardImage = new System.Windows.DataObject();
            hugeClipboardImage.SetData("PNG", new MemoryStream(hugePng));
            Check(ClipboardWatcher.ReadImage(hugeClipboardImage) == null, "большой PNG из буфера пропускается без декодирования");
            var dibHeader = new byte[40];
            BitConverter.GetBytes(40).CopyTo(dibHeader, 0);
            BitConverter.GetBytes(2).CopyTo(dibHeader, 4);
            BitConverter.GetBytes(2).CopyTo(dibHeader, 8);
            Check(ClipboardWatcher.FitsDib(new MemoryStream(dibHeader)), "обычный DIB проходит проверку размера");
            BitConverter.GetBytes(100_000).CopyTo(dibHeader, 4);
            BitConverter.GetBytes(100_000).CopyTo(dibHeader, 8);
            Check(!ClipboardWatcher.FitsDib(new MemoryStream(dibHeader)), "слишком большой DIB отсекается до копирования");
            Check(!File.ReadAllBytes(Path.Combine(s.ImagesDir, h1 + ".dat")).AsSpan().StartsWith(png.AsSpan(0, 8)), "файл картинки зашифрован");

            var gifEncoder = new GifBitmapEncoder();
            gifEncoder.Frames.Add(BitmapFrame.Create(n1));
            using var gifStream = new MemoryStream();
            gifEncoder.Save(gifStream);
            var gifFrame = BitmapDecoder.Create(new MemoryStream(gifStream.ToArray()), BitmapCreateOptions.None, BitmapCacheOption.OnDemand).Frames[0];
            Check(ImageFiles.Normalize(gifFrame).bgra.PixelWidth == 2, "GIF декодируется по требованию после проверки размера");

            var gifBytes = "GIF89a-test"u8.ToArray();
            ImageFiles.WriteGif(s.ImagesDir, "GIFTEST", gifBytes, n1);
            s.AddImage("GIFTEST", 2, 2, t0.AddDays(10).AddMinutes(7), gif: true);
            Check(ImageFiles.ReadFull(s.ImagesDir, "GIFTEST").SequenceEqual(gifBytes), "GIF хранится байт-в-байт");
            s.Prune(t0.AddDays(10).AddMinutes(7).AddHours(25));
            Check(!s.Items.Any(i => i.IsGif) && s.Items.Any(i => i.Text == "n149"), "GIF живёт 1 день, текст того же возраста остаётся");
            Check(!File.Exists(Path.Combine(s.ImagesDir, "GIFTEST.dat")), "файлы просроченного GIF удалены");

            ImageFiles.WriteMedia(s.ImagesDir, "MP3TEST", [1, 2, 3], null);
            s.AddMedia("MP3TEST", "audio", "song.mp3", 3, false, t0.AddDays(10).AddMinutes(8));
            Check(s.Items[0] is { IsMedia: true, IsImage: false, Badge: "Аудио", Preview: "song.mp3" }, "аудио — медиа-запись, не картинка");
            Check(!File.Exists(Path.Combine(s.ImagesDir, "MP3TEST.thumb.dat")), "у аудио без миниатюры нет файла миниатюры");
            s.Prune(t0.AddDays(10).AddMinutes(8).AddHours(25));
            Check(!s.Items.Any(i => i.IsMedia) && !File.Exists(Path.Combine(s.ImagesDir, "MP3TEST.dat")), "аудио живёт 1 день");

            var textPath = Path.Combine(dir, "note.txt");
            File.WriteAllText(textPath, "Пример текста");
            var textDrop = new System.Windows.DataObject();
            textDrop.SetFileDropList([textPath]);
            textDrop.SetData(System.Windows.DataFormats.UnicodeText, "текст рядом с файлом");
            Check(ClipboardWatcher.ReadFile(textDrop) is (var capturedPath, "text") && capturedPath == textPath,
                "текстовый файл из FileDrop распознаётся даже при наличии UnicodeText");
            var emptyTextPath = Path.Combine(dir, "empty.txt");
            File.WriteAllBytes(emptyTextPath, []);
            var emptyTextDrop = new System.Windows.DataObject();
            emptyTextDrop.SetFileDropList([emptyTextPath]);
            Check(ClipboardWatcher.ReadFile(emptyTextDrop)?.kind == "text", "пустой текстовый файл тоже виден");
            var textBytes = File.ReadAllBytes(textPath);
            var textHash = Convert.ToHexString(SHA256.HashData(textBytes));
            var fileStore = new HistoryStore(Path.Combine(dir, "files", "history.dat"));
            ImageFiles.WriteMedia(fileStore.ImagesDir, textHash, textBytes, null);
            fileStore.AddMedia(textHash, "text", "note.txt", textBytes.Length, false, t0);
            fileStore.Save();
            var fileStore2 = new HistoryStore(Path.Combine(dir, "files", "history.dat"));
            fileStore2.Load(t0);
            Check(fileStore2.Items.Single() is { Badge: "Текстовый файл", FileName: "note.txt", IsMedia: true }
                && ImageFiles.ReadFull(fileStore2.ImagesDir, textHash).SequenceEqual(textBytes),
                "текстовый файл виден после перезапуска и хранится без изменения содержимого");

            s.Save();
            Check(!File.ReadAllText(s._path).Contains("n149"), "файл зашифрован");
            File.WriteAllBytes(Path.Combine(s.ImagesDir, "DEADBEEF.dat"), [1, 2, 3]);
            var s2 = new HistoryStore(s._path);
            s2.Load(t0.AddDays(10).AddMinutes(10));
            Check(s2.Items.Count == s.Items.Count && s2.Items[0].IsImage && s2.Items.Single(i => i.Text == "b").Pinned,
                "сохранение/загрузка сохраняет порядок, булавки и картинки");
            Check(!File.Exists(Path.Combine(s.ImagesDir, "DEADBEEF.dat")), "осиротевшие файлы картинок удаляются");

            var layout = new HistoryStore(Path.Combine(dir, "layout", "history.dat"));
            foreach (var text in new[] { "one", "two", "three" })
            {
                layout.Add(text, null, null, t0);
                layout.TogglePin(layout.Items.First());
            }
            var one = layout.Items.Single(i => i.Text == "one");
            var two = layout.Items.Single(i => i.Text == "two");
            var three = layout.Items.Single(i => i.Text == "three");
            var group = layout.AddGroup("Работа")!;
            Check(layout.AddGroup("работа") == null, "названия групп не дублируются");
            Check(layout.AddGroup("")?.Name == "Группа 1" && layout.AddGroup(" ")?.Name == "Группа 2", "пустые названия получают имена по порядку");
            Check(layout.AddGroup("Группа 2") == null && layout.NextGroupName() == "Группа 3", "автонумерация не дублирует названия");
            layout.MovePinned(two, group.Id);
            layout.MovePinned(one, group.Id, two);
            layout.MovePinned(three, group.Id, two, after: true);
            layout.SetTileColor(two, 5);
            Check(layout.PinnedIn(group.Id).Select(i => i.Text).SequenceEqual(["one", "two", "three"]), "плитки переносятся и упорядочиваются внутри группы");
            layout.Add("two", null, null, t0.AddMinutes(1));
            Check(layout.PinnedIn(group.Id).Select(i => i.Text).SequenceEqual(["one", "two", "three"]), "повторное копирование не меняет порядок плиток");
            layout.Save();
            var layout2 = new HistoryStore(layout._path);
            layout2.Load(t0);
            Check(layout2.Groups.Count == 3 && layout2.Groups[0].Name == "Работа" && layout2.PinnedIn(group.Id).Select(i => i.Text).SequenceEqual(["one", "two", "three"])
                  && layout2.Items.Single(i => i.Text == "two").TileColor == 5, "группа, порядок и цвет переживают перезапуск");
            layout2.DeleteGroup(layout2.Groups[0]);
            Check(layout2.PinnedIn(null).Count == 3 && layout2.Groups.Count == 2, "удаление группы сохраняет плитки");

            var oldPath = Path.Combine(dir, "legacy.dat");
            File.WriteAllBytes(oldPath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new List<ClipItem> { one }), Entropy, DataProtectionScope.CurrentUser));
            var legacy = new HistoryStore(oldPath);
            legacy.Load(t0);
            Check(legacy.Items.Count == 1 && legacy.Items[0].Pinned && legacy.Groups.Count == 0, "старый формат истории читается");

            s2.Remove(s2.Items[0]);
            Check(!File.Exists(Path.Combine(s.ImagesDir, h1 + ".dat")) && !File.Exists(Path.Combine(s.ImagesDir, h1 + ".thumb.dat")),
                "удаление записи удаляет файлы картинки");

            var symPath = Path.Combine(dir, "symbols.json");
            var sym = new SymbolCatalog(symPath);
            sym.TogglePin("→"); sym.TogglePin("₽");
            var sym2 = new SymbolCatalog(symPath);
            Check(sym2.Items.Take(2).Select(i => i.Char).SequenceEqual(["→", "₽"]) && sym2.Items[0].Group == SymbolCatalog.PinnedGroup,
                "закреплённые символы сохраняются и идут первой группой");
            Check(sym2.Items.Any(i => i.Char == "→" && i.Group == "Стрелки" && i.Pinned), "закреплённый символ остаётся и в своей группе");
            var panel = new PanelWindow(s2, sym2);
            int outsideDelayChanged = 0;
            panel.OutsideHideDelayChanged += ms => outsideDelayChanged = ms;
            Check(panel.AutoHideOutside && panel.OutsideHideDelayMs == 3000, "автоскрытие по выходу мыши включено, задержка 3 секунды");
            panel.OutsideHideDelayMs = 2500;
            Check(panel.OutsideHideDelayMs == 2500 && outsideDelayChanged == 2500, "задержка автоскрытия меняется в настройках");
            sym2.TogglePin("→");
            Check(new SymbolCatalog(symPath).Items.Count(i => i.Group == SymbolCatalog.PinnedGroup) == 1, "открепление сохраняется");
            var dash = sym2.Items.First(i => i.Char == "—");
            Check(SymbolCatalog.Matches(dash, "2014") && SymbolCatalog.Matches(dash, "U+2014") && SymbolCatalog.Matches(dash, "пункт")
                  && !SymbolCatalog.Matches(dash, "валют"), "поиск символа по коду и группе");

            File.WriteAllText(s._path, "мусор");
            var s3 = new HistoryStore(s._path);
            s3.Load(t0);
            Check(s3.Items.Count == 0 && Directory.GetFiles(dir, "history.corrupt-*").Length == 1, "битый файл откладывается, старт с пустой");
            return 0;
        }
        catch (Exception e)
        {
            log.AppendLine(e.ToString());
            return 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, log.ToString());
        }
    }
}
