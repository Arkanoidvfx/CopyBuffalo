using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

public sealed class ClipItem : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();
    string _text = "";
    public string Text { get => _text; init => _text = value; }
    public string? Rtf { get; set; }
    public string? Html { get; set; }
    bool _isSensitive;
    public bool IsSensitive
    {
        get => _isSensitive;
        set { if (_isSensitive == value) return; _isSensitive = value; PropertyChanged?.Invoke(this, new(null)); }
    }
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
            return (DateTime.Today - t.Date).Days switch { 0 => t.ToString("HH:mm"), 1 => UiText.T("вчера"), _ => t.ToString("dd.MM") };
        }
    }

    /// <summary>После полуночи «15:32» должно стать «вчера», хотя LastUsedAt не менялся.</summary>
    internal void RefreshTimeLabel() => PropertyChanged?.Invoke(this, new(nameof(TimeLabel)));

    int _seriesNumber;
    bool _seriesInserted;
    [JsonIgnore]
    public int SeriesNumber
    {
        get => _seriesNumber;
        internal set { if (_seriesNumber == value) return; _seriesNumber = value; PropertyChanged?.Invoke(this, new(nameof(SeriesNumber))); PropertyChanged?.Invoke(this, new(nameof(InSeries))); }
    }
    [JsonIgnore] public bool InSeries => SeriesNumber > 0;
    [JsonIgnore]
    public bool SeriesInserted
    {
        get => _seriesInserted;
        internal set { if (_seriesInserted == value) return; _seriesInserted = value; PropertyChanged?.Invoke(this, new(nameof(SeriesInserted))); }
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

    /// <summary>Тип файла или "folder" для ссылки на папку (её путь в Text, содержимое не сохраняется).</summary>
    public string? MediaKind { get; init; }
    public string? FileName { get; init; }
    public long FileSize { get; init; }
    /// <summary>Число файлов в одном пакете; 0 у старых и одиночных записей.</summary>
    public int FileCount { get; init; }
    /// <summary>Имя процесса, владевшего буфером в момент копирования.</summary>
    public string? SourceApp { get; set; }
    /// <summary>У видео есть миниатюра-кадр (если её дал Проводник).</summary>
    public bool HasThumb { get; init; }

    bool _pinned;
    public bool Pinned
    {
        get => _pinned;
        set { _pinned = value; PropertyChanged?.Invoke(this, new(nameof(Pinned))); }
    }

    bool _compressing;
    /// <summary>Идёт «Сжать промпт»: карточка показывает бегущую полосу.</summary>
    [JsonIgnore] public bool Compressing
    {
        get => _compressing;
        set { _compressing = value; PropertyChanged?.Invoke(this, new(nameof(Compressing))); PropertyChanged?.Invoke(this, new(nameof(Working))); }
    }
    bool _archiving;
    [JsonIgnore] public bool Archiving
    {
        get => _archiving;
        set { _archiving = value; PropertyChanged?.Invoke(this, new(nameof(Archiving))); PropertyChanged?.Invoke(this, new(nameof(Working))); }
    }
    bool _extractingAudio;
    [JsonIgnore] public bool ExtractingAudio
    {
        get => _extractingAudio;
        set { _extractingAudio = value; PropertyChanged?.Invoke(this, new(nameof(ExtractingAudio))); PropertyChanged?.Invoke(this, new(nameof(Working))); }
    }
    bool _recognizing;
    [JsonIgnore] public bool Recognizing
    {
        get => _recognizing;
        set { _recognizing = value; PropertyChanged?.Invoke(this, new(nameof(Recognizing))); PropertyChanged?.Invoke(this, new(nameof(Working))); }
    }
    [JsonIgnore] public bool Working => Compressing || Archiving || ExtractingAudio || Recognizing;

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
            PropertyChanged?.Invoke(this, new(nameof(TileBand)));
            PropertyChanged?.Invoke(this, new(nameof(HasTileColor)));
        }
    }
    public static readonly string[] TileColorNames = [UiText.T("Красный"), UiText.T("Оранжевый"), UiText.T("Жёлтый"), UiText.T("Зелёный"), UiText.T("Бирюзовый"), UiText.T("Синий"), UiText.T("Фиолетовый")];
    static readonly Color[] TileColors = [
        Color.FromRgb(232, 100, 105), Color.FromRgb(239, 151, 87), Color.FromRgb(225, 187, 81),
        Color.FromRgb(110, 190, 123), Color.FromRgb(77, 191, 185), Color.FromRgb(103, 156, 233),
        Color.FromRgb(175, 129, 224)];
    public static Brush ColorAccent(int color) => new SolidColorBrush(TileColors[color]);
    /// <summary>Цвет плитки: лёгкая заливка 16% и полоса 3 px сверху полным цветом (раньше сплошные 41% — мутно).</summary>
    [JsonIgnore] public Brush? TileTint => TileColor < 0 ? null : new SolidColorBrush(Color.FromArgb(41, TileColors[TileColor].R, TileColors[TileColor].G, TileColors[TileColor].B));
    [JsonIgnore] public Brush? TileBand => TileColor < 0 ? null : new SolidColorBrush(TileColors[TileColor]);
    [JsonIgnore] public bool HasTileColor => TileColor >= 0;

    string? _kind, _title, _sub;
    /// <summary>Тип для плитки и строки закреплённого: text, code, url, path, color, image, media, graph.</summary>
    [JsonIgnore] public string Kind { get { if (IsSensitive) return "text"; Describe(); return _kind!; } }
    /// <summary>Главное: домен ссылки, имя файла, HEX, первая строка текста.</summary>
    [JsonIgnore] public string TileTitle { get { if (IsSensitive) return "********"; Describe(); return _title!; } }
    /// <summary>Второстепенное: путь ссылки, папка файла, остаток текста.</summary>
    [JsonIgnore] public string TileSub { get { if (IsSensitive) return ""; Describe(); return _sub!; } }
    [JsonIgnore] public bool IsMapLink => !IsSensitive && !IsMedia && !IsImage && !IsNodeGraph && MapLink.TryDescribe(Text, out _, out _);
    [JsonIgnore] public string KindGlyph => Kind switch
    {
        "url" => "\uE71B", "path" => "\uE8A5", "code" => "\uE943", "color" => "\uE790",
        "image" => "\uE91B", "media" => Glyph, "graph" => "\uE8A7", _ => "\uE8C4",
    };
    /// <summary>Образец для записи-цвета (#RGB, #RRGGBB, #AARRGGBB).</summary>
    [JsonIgnore] public Brush? Swatch => Kind == "color" && ColorConverter.ConvertFromString(TileTitle) is Color c ? new SolidColorBrush(c) : null;

    static readonly System.Text.RegularExpressions.Regex HexColor = new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");

    void Describe()
    {
        if (_kind != null) return;
        (_kind, _title, _sub) = DescribeText();
    }

    (string kind, string title, string sub) DescribeText()
    {
        if (IsNodeGraph) return ("graph", GraphLabel, UiText.T("Граф нод"));
        if (IsFolder) return ("media", FileName ?? UiText.T("Папка"), Text);
        if (IsMedia) return ("media", FileName ?? "", MediaMeta);
        if (IsImage) return ("image", IsGif ? "GIF" : UiText.T("Изображение"), $"{ImageWidth} × {ImageHeight}");
        var s = Text.Trim();
        if (!s.Contains('\n'))
        {
            if (MapLink.TryDescribe(s, out var mapTitle, out var mapDetail)) return ("url", mapTitle, mapDetail);
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
                var rest = (uri.PathAndQuery + uri.Fragment).Trim('/');
                return ("url", host, rest.Length > 0 ? Uri.UnescapeDataString(rest) : uri.Scheme);
            }
            // Путь из «Копировать как путь» приходит в кавычках — снимаем их.
            var bare = s.Trim('"', '\'');
            if (LooksLikeCode(bare))
            {
                var trimmed = bare.TrimEnd('\\', '/');
                int cut = trimmed.LastIndexOfAny(['\\', '/']);
                return cut > 0 && cut < trimmed.Length - 1
                    ? ("path", trimmed[(cut + 1)..], trimmed[..cut])
                    : ("path", trimmed, "");
            }
            if (HexColor.IsMatch(s)) return ("color", s.ToUpperInvariant(), UiText.T("Цвет"));
        }
        var preview = Preview;
        int newline = preview.IndexOf('\n');
        string first = (newline < 0 ? preview : preview[..newline]).Trim();
        string restText = newline < 0 ? "" : string.Join(" ", preview[(newline + 1)..].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (restText.Length == 0) restText = UiText.F($"Текст, {Text.Length:N0} симв.");
        return (IsMono ? "code" : "text", first, restText);
    }

    [JsonIgnore] public bool IsMedia => MediaKind != null;
    [JsonIgnore] public bool IsFolder => MediaKind == "folder";
    [JsonIgnore] public bool IsImage => ImageHash != null && !IsMedia;
    [JsonIgnore] public bool IsVisualMedia => IsImage || MediaKind == "video" && ImageHash != null;
    [JsonIgnore] public bool IsUnrealGraph => Text.StartsWith("Begin Object Class=/Script/", StringComparison.Ordinal)
        && (Text.Contains("MaterialGraphNode", StringComparison.Ordinal) || Text.Contains("K2Node_", StringComparison.Ordinal))
        && Text.Contains("CustomProperties Pin", StringComparison.Ordinal);
    [JsonIgnore] public bool IsUnityGraph => Text.Contains("UnityEditor.ShaderGraph.CopyPasteGraph", StringComparison.Ordinal)
        || (Text.Contains("\"m_SerializableNodes\"", StringComparison.Ordinal)
            && Text.Contains("\"m_SerializableEdges\"", StringComparison.Ordinal));
    [JsonIgnore] public bool IsNodeGraph => !IsSensitive && (IsUnrealGraph || IsUnityGraph);
    bool? _mono;
    /// <summary>Путь или код — моноширинным шрифтом.</summary>
    [JsonIgnore] public bool IsMono => !IsSensitive && (_mono ??= !IsMedia && !IsImage && !IsNodeGraph && LooksLikeCode(Text));

    /// <summary>
    /// Путь Windows/UNC/Unix одной строкой или многострочный текст, где хотя бы половина строк похожа на код.
    /// ponytail: грубая эвристика; список с отступами тоже станет моноширинным — терпимо.
    /// </summary>
    internal static bool LooksLikeCode(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return false;
        if (!s.Contains('\n')) return s.Length < 400 && System.Text.RegularExpressions.Regex.IsMatch(s, @"^(?:[A-Za-z]:\\|\\\\|~?/\S)[^\r\n]*$");
        var lines = s.Split('\n', 21).Take(20).Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        int code = lines.Count(l => l.StartsWith("    ") || l.StartsWith('\t')
            || l.TrimEnd() is var t && (t.EndsWith(';') || t.EndsWith('{') || t.EndsWith('}') || t.EndsWith(')')));
        return lines.Count >= 2 && code * 2 >= lines.Count;
    }
    [JsonIgnore] public string GraphLabel => IsUnityGraph ? UiText.T("Ноды Unity Shader Graph") : UiText.T("Ноды Unreal Engine");
    /// <summary>Живёт 1 день и вставляется файлом.</summary>
    [JsonIgnore] public bool IsShortLived => IsGif || IsMedia && !IsFolder;
    /// <summary>Плитке показывать картинку, а не текст.</summary>
    [JsonIgnore] public bool ShowTileImage => IsImage || HasThumb;
    [JsonIgnore] public string? Badge => IsGif ? "GIF" : MediaKind switch { "video" => UiText.T("Видео"), "audio" => UiText.T("Аудио"), "text" => UiText.T("Текстовый файл"), "files" => UiText.T("Файлы"), "folder" => UiText.T("Папка"), _ => null };
    [JsonIgnore] public bool HasBadge => Badge != null;
    [JsonIgnore] public string Glyph => MediaKind switch { "audio" => "\uE8D6", "text" => "\uE8A5", "files" => "\uE8B7", "folder" => "\uE8B7", _ => "\uE714" };
    [JsonIgnore] public string FileCountMeta => MediaKind == "files" ? UiText.F($" · {FileCount} шт.") : "";
    [JsonIgnore] public string MediaMeta => IsFolder ? UiText.T("Папка · ссылка на оригинал")
        : MediaKind == "text" && FileSize < 1024
        ? UiText.F($"{Badge} · {FileSize} Б")
        : MediaKind == "text" && FileSize < 1048576
        ? UiText.F($"{Badge} · {FileSize / 1024.0:0.#} КБ")
        : UiText.F($"{Badge}{FileCountMeta} · {FileSize / 1048576.0:0.#} МБ");

    /// <summary>Текст для карточки: без хвостовых пробелов, не длиннее пары экранов.</summary>
    [JsonIgnore]
    public string CardText => Text.Length > 600 ? Text[..600] : Text;

    [JsonIgnore]
    public string Preview => IsSensitive ? "********"
        : IsMedia ? FileName ?? ""
        : IsImage ? $"{(IsGif ? "GIF" : UiText.T("Изображение"))} {ImageWidth} × {ImageHeight}"
        : IsNodeGraph ? GraphLabel
        : MapLink.TryDescribe(Text, out var mapTitle, out var mapDetail)
            ? $"{mapTitle}\n{(mapTitle.EndsWith(UiText.T("маршрут"), StringComparison.Ordinal) ? mapDetail.Replace(" → ", "\n→ ") : mapDetail)}"
        : CardText.Replace('\t', ' ').Trim();

    [JsonIgnore] internal string? ImageDir { get; set; }
    ImageSource? _thumb;
    ImageSource? _graphPreview;

    [JsonIgnore] public ImageSource? GraphPreview => IsSensitive ? null : _graphPreview ??= IsNodeGraph ? UnrealGraph.Render(Text, IsUnityGraph) : null;

    /// <summary>Миниатюра. Биндинг с IsAsync=True: грузится в фоне, UI не ждёт.</summary>
    [JsonIgnore]
    public ImageSource? Thumbnail => _thumb ??= ImageDir != null && ImageHash != null
        ? ImageFiles.LoadBitmap(Path.Combine(ImageDir, ImageHash + ".thumb.dat"))
        : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ReplaceText(string text)
    {
        _text = text;
        Rtf = Html = null; // Старые форматы содержат прежний текст и больше не подходят для вставки.
        _kind = _title = _sub = null;
        _mono = null;
        _graphPreview = null;
        PropertyChanged?.Invoke(this, new(null));
    }
}

public sealed class PinnedGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Свёрнута: в панели только заголовок со счётчиком.</summary>
    public bool Collapsed { get; set; }
    [JsonIgnore] public bool IsGroupHeader => true;
    /// <summary>Сколько в группе записей; считает панель при раскладке.</summary>
    [JsonIgnore] public int Count { get; set; }
    /// <summary>Пустая настоящая группа: под заголовком пунктирная зона «Перетащите запись сюда».</summary>
    [JsonIgnore] public bool IsEmptyGroup { get; set; }
}

/// <summary>Файлы картинок: PNG, зашифрованный DPAPI (на скриншотах бывает приватное).</summary>
static class ImageFiles
{
    static readonly byte[] Entropy = "ArkCopyBuffer.img.v1"u8.ToArray();
    const int DefaultThumbSize = 320;

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
        Span<byte> dimensions = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, bgra.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], bgra.PixelHeight);
        sha.AppendData(dimensions);
        for (int y = 0; y < bgra.PixelHeight; y += rowsPerChunk)
        {
            int rows = Math.Min(rowsPerChunk, bgra.PixelHeight - y);
            bgra.CopyPixels(new System.Windows.Int32Rect(0, y, bgra.PixelWidth, rows), pixels, stride, 0);
            sha.AppendData(pixels.AsSpan(0, stride * rows));
        }
        return (bgra, Convert.ToHexString(sha.GetHashAndReset()));
    }

    /// <summary>Пишет полную картинку (PNG) и миниатюру, если их ещё нет. Вызывать не из UI-потока.</summary>
    public static void Write(string dir, string hash, BitmapSource bgra, int thumbSize = DefaultThumbSize) =>
        Write(dir, hash, () => EncodePng(bgra), bgra, thumbSize);

    /// <summary>Видео/аудио: полный файл — исходные байты, миниатюра — если есть.</summary>
    public static void WriteMedia(string dir, string hash, byte[] file, BitmapSource? thumb, int thumbSize = DefaultThumbSize) =>
        Write(dir, hash, () => file, thumb, thumbSize);

    /// <summary>GIF: полный файл — исходные байты (анимация), миниатюра — первый кадр.</summary>
    public static void WriteGif(string dir, string hash, byte[] gif, BitmapSource firstFrame, int thumbSize = DefaultThumbSize) =>
        Write(dir, hash, () => gif, firstFrame, thumbSize);

    static void Write(string dir, string hash, Func<byte[]> fullBytes, BitmapSource? bgra, int thumbSize)
    {
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, hash + ".dat");
        var thumb = Path.Combine(dir, hash + ".thumb.dat");
        if (!File.Exists(full)) WriteAtomic(full, Seal(fullBytes()));
        if (bgra != null && !File.Exists(thumb))
        {
            double k = Math.Min(1.0, (double)thumbSize / Math.Max(bgra.PixelWidth, bgra.PixelHeight));
            var small = k < 1 ? new TransformedBitmap(bgra, new ScaleTransform(k, k)) : bgra;
            WriteAtomic(thumb, Seal(EncodePng(small)));
        }
    }

    /// <summary>Полный файл: PNG для картинок, исходный GIF для гифок.</summary>
    public static byte[] ReadFull(string dir, string hash) => Open(File.ReadAllBytes(Path.Combine(dir, hash + ".dat")));

    public static BitmapSource? LoadBitmap(string sealedPath, int decodeWidth = 0)
    {
        try { return Decode(Open(File.ReadAllBytes(sealedPath)), decodeWidth); }
        catch { return null; } // файл удалён/битый — карточка покажется без миниатюры
    }

    /// <param name="decodeWidth">0 — исходный размер; иначе сразу уменьшить при декодировании (экономит память).</param>
    public static BitmapSource Decode(byte[] png, int decodeWidth = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
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
    static readonly byte[] Entropy = "ArkCopyBuffer.v1"u8.ToArray();
    /// <summary>По умолчанию кириллица и &lt;&gt;&amp; из HTML уходят в \uXXXX — файл в 2–3 раза больше. JSON не попадает в HTML, экранировать незачем.</summary>
    static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly string _path;
    internal StorageSettings Storage { get; set; } = new();
    Task _writing = Task.CompletedTask; // цепочка записей на диск: строго по порядку снимков
    int _blobWriters;
    readonly HashSet<string> _deferredDeletes = [];

    internal IDisposable PreserveBlobFiles()
    {
        _blobWriters++;
        return new BlobWrite(this);
    }

    sealed class BlobWrite(HistoryStore store) : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (--store._blobWriters != 0) return;
            foreach (var hash in store._deferredDeletes)
                if (!store.Items.Any(i => i.ImageHash == hash)) ImageFiles.Delete(store.ImagesDir, hash);
            store._deferredDeletes.Clear();
        }
    }
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

    // Compare single-line clipboard text as displayed, without changing the stored text.
    // Keep tabs and multiline indentation exact: whitespace may be meaningful in code.
    static string TextKey(string text)
    {
        if (text.IndexOfAny(['\r', '\n', '\t']) >= 0) return text;
        var trimmed = text.Trim();
        return trimmed.Length > 0 ? trimmed : text;
    }

    public void Add(string text, string? rtf, string? html, DateTime nowUtc, string? sourceApp = null, bool sensitive = false)
    {
        string key = TextKey(text);
        sensitive |= Items.Any(i => !i.IsImage && !i.IsMedia && i.IsSensitive && i.Text == text);
        var matches = Items.Where(i => !i.IsImage && !i.IsMedia
            && (sensitive ? i.Text == text : !i.IsSensitive && TextKey(i.Text) == key)).ToArray();
        if (sensitive)
            foreach (var match in matches) { match.IsSensitive = true; match.Rtf = match.Html = null; }
        var existing = matches
            .OrderByDescending(i => i.Pinned).ThenByDescending(i => i.LastUsedAt).FirstOrDefault();
        if (existing != null)
        {
            existing.IsSensitive |= sensitive;
            // Повтор (в т.ч. наша же вставка) — поднимаем. Форматы не затираем null'ом:
            // после «вставить как текст» в буфере только текст, а форматирование записи терять нельзя.
            existing.Rtf = existing.IsSensitive ? null : rtf ?? existing.Rtf;
            existing.Html = existing.IsSensitive ? null : html ?? existing.Html;
            existing.SourceApp = sourceApp;
            Bump(existing, nowUtc);
        }
        else Insert(new ClipItem { Text = text, Rtf = sensitive ? null : rtf, Html = sensitive ? null : html, IsSensitive = sensitive, SourceApp = sourceApp, CreatedAt = nowUtc, LastUsedAt = nowUtc }, nowUtc);
    }

    /// <summary>Перезаписать текст на месте: Id, время, группа и порядок остаются прежними.</summary>
    public void UpdateText(ClipItem item, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Items.Contains(item)) throw new InvalidOperationException(UiText.T("Запись уже удалена из истории. Скопируйте текст перед закрытием окна."));
        if (item.IsImage || item.IsMedia) throw new InvalidOperationException(UiText.T("Редактировать можно только текстовые записи."));
        if (item.Text == text) return;
        if (text.Length > Storage.MaxChars) throw new InvalidOperationException(UiText.F($"Лимит текста в настройках: {Storage.MaxChars:N0} символов. Сократите текст или увеличьте лимит."));
        item.ReplaceText(text);
        Changed?.Invoke();
    }

    /// <summary>Только путь папки: ни обхода файлов, ни снимка содержимого при копировании.</summary>
    public void AddFolder(string path, DateTime nowUtc, string? sourceApp = null)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var existing = Items.FirstOrDefault(i => i.IsFolder && string.Equals(i.Text, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { existing.SourceApp = sourceApp; Bump(existing, nowUtc); return; }
        var name = Path.GetFileName(path);
        Insert(new ClipItem { Text = path, MediaKind = "folder", FileName = name.Length > 0 ? name : path,
            SourceApp = sourceApp, CreatedAt = nowUtc, LastUsedAt = nowUtc }, nowUtc);
    }

    /// <summary>Картинка, файлы которой уже записаны ImageFiles.Write.</summary>
    public void AddImage(string hash, int width, int height, DateTime nowUtc, bool gif = false, string? sourceApp = null) =>
        AddBlob(new ClipItem { ImageHash = hash, ImageWidth = width, ImageHeight = height, IsGif = gif, SourceApp = sourceApp, CreatedAt = nowUtc, LastUsedAt = nowUtc }, nowUtc);

    /// <summary>Видео, аудио или текстовый файл, уже записанный ImageFiles.WriteMedia.</summary>
    public void AddMedia(string hash, string kind, string fileName, long size, bool hasThumb, DateTime nowUtc, string? sourceApp = null, int fileCount = 0) =>
        AddBlob(new ClipItem { ImageHash = hash, MediaKind = kind, FileName = fileName, FileSize = size, FileCount = fileCount, HasThumb = hasThumb, SourceApp = sourceApp, CreatedAt = nowUtc, LastUsedAt = nowUtc }, nowUtc);

    void AddBlob(ClipItem fresh, DateTime nowUtc)
    {
        var existing = Items.FirstOrDefault(i => i.ImageHash == fresh.ImageHash);
        if (existing != null) { existing.SourceApp = fresh.SourceApp; Bump(existing, nowUtc); return; }
        fresh.ImageDir = ImagesDir;
        Insert(fresh, nowUtc);
    }

    /// <summary>Наша же вставка вернулась в буфер — только поднимаем запись, без повторного разбора данных.</summary>
    public void Touch(Guid id, DateTime nowUtc)
    {
        if (Items.FirstOrDefault(i => i.Id == id) is { } item) Bump(item, nowUtc);
    }

    void Bump(ClipItem item, DateTime nowUtc)
    {
        item.LastUsedAt = nowUtc;
        Place(item);
        Prune(nowUtc);
        Changed?.Invoke();
    }

    void Insert(ClipItem item, DateTime nowUtc)
    {
        Place(item);
        Prune(nowUtc);
        Changed?.Invoke();
    }

    /// <summary>
    /// Ставит запись по LastUsedAt, а не просто наверх: большая картинка дописывается в фоне
    /// и может прийти позже текста, скопированного уже после неё.
    /// </summary>
    void Place(ClipItem item)
    {
        int from = Items.IndexOf(item);
        int to = 0;
        while (to < Items.Count && (Items[to] == item || Items[to].LastUsedAt > item.LastUsedAt)) to++;
        if (from < 0) Items.Insert(to, item);
        else Items.Move(from, to > from ? to - 1 : to); // to считан вместе с самой записью
    }

    /// <summary>Единая точка удаления: вместе с записью уходят файлы картинки.</summary>
    void Drop(ClipItem item)
    {
        if (Items.Remove(item) && item.ImageHash != null)
        {
            if (_blobWriters != 0) _deferredDeletes.Add(item.ImageHash);
            else ImageFiles.Delete(ImagesDir, item.ImageHash);
        }
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
        else
        {
            item.PinnedGroupId = null;
            Prune(DateTime.UtcNow);
        }
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
            string name = UiText.F($"Группа {number}");
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

    public void SetGroupCollapsed(PinnedGroup group, bool collapsed)
    {
        if (!Groups.Contains(group)) return;
        group.Collapsed = collapsed;
        Changed?.Invoke();
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
        var drop = unpinned.Where((i, idx) => idx >= Storage.HistoryCount ||
            nowUtc - i.LastUsedAt > TimeSpan.FromDays(i.IsShortLived ? Storage.MediaDays : Storage.HistoryDays)).ToList();
        foreach (var i in drop) Drop(i);
        return drop.Count > 0;
    }

    internal void ApplyStorage(StorageSettings settings, DateTime nowUtc)
    {
        Storage = settings;
        if (Prune(nowUtc)) Changed?.Invoke();
    }

    /// <summary>
    /// Снимок — здесь, в UI-потоке (коллекция не потокобезопасна); шифрование и запись — в фоне, по порядку.
    /// История с большими RTF/HTML — это мегабайты, UI не должен их ждать.
    /// </summary>
    public Task SaveAsync()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new HistoryFile { Items = Items.ToList(), Groups = Groups }, JsonOptions);
        return _writing = _writing.ContinueWith(_ => Write(json), TaskScheduler.Default);
    }

    /// <summary>Синхронно, дожидаясь и предыдущих фоновых записей (выход, самопроверка).</summary>
    public void Save() => SaveAsync().GetAwaiter().GetResult();

    void Write(byte[] json)
    {
        var data = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, _path, overwrite: true); // атомарная замена: обрыв посреди записи не портит историю
    }

    /// <summary>Антивирус/бэкап при входе в систему может держать файл: ждём, а не стартуем с пустой историей (её бы потом записали поверх).</summary>
    static byte[] ReadWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException) when (attempt < 10) { Thread.Sleep(200); }
        }
    }

    public void Load(DateTime nowUtc)
    {
        Items.Clear();
        Groups.Clear();
        if (File.Exists(_path))
        {
            try
            {
                var json = ProtectedData.Unprotect(ReadWithRetry(_path), Entropy, DataProtectionScope.CurrentUser);
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
                var backup = Path.Combine(dir, $"history.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.dat");
                bool movedImages = Directory.Exists(ImagesDir);
                if (movedImages) Directory.Move(ImagesDir, backup + ".images");
                try { File.Move(_path, backup); }
                catch
                {
                    if (movedImages) Directory.Move(backup + ".images", ImagesDir);
                    throw;
                }
                Items.Clear();
                Groups.Clear();
            }
        }
        bool deduplicated = false;
        try { deduplicated = ConsolidateTextDuplicates(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the original entries if a backup cannot be written.
            App.Log(ex);
        }
        bool recovered = RecoverOrphanFiles();
        if (Prune(nowUtc) | recovered | deduplicated) Save();
    }

    bool ConsolidateTextDuplicates()
    {
        var duplicates = Items.Where(i => !i.IsImage && !i.IsMedia)
            .GroupBy(i => (i.IsSensitive, Key: i.IsSensitive ? i.Text : TextKey(i.Text)))
            .SelectMany(group =>
            {
                var keep = group.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.LastUsedAt).First();
                // Separate pinned placements can be intentional; only redundant unpinned records disappear.
                return group.Where(i => i != keep && !i.Pinned).Select(drop => (Keep: keep, Drop: drop));
            }).ToList();
        if (duplicates.Count == 0) return false;
        File.Copy(_path, Path.Combine(Path.GetDirectoryName(_path)!, $"history.before-dedup-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.dat"));
        foreach (var (keep, drop) in duplicates)
        {
            keep.IsSensitive |= drop.IsSensitive;
            keep.Rtf = keep.IsSensitive ? null : keep.Rtf ?? drop.Rtf;
            keep.Html = keep.IsSensitive ? null : keep.Html ?? drop.Html;
            keep.SourceApp ??= drop.SourceApp;
            if (keep.LastUsedAt < drop.LastUsedAt) { keep.LastUsedAt = drop.LastUsedAt; Place(keep); }
            Items.Remove(drop);
        }
        return true;
    }

    /// <summary>
    /// Файлы в images/ без записи: история не успела сохраниться (сбой, убитый процесс) или была битой.
    /// Картинки и GIF возвращаем в историю — размер есть в заголовке, время — у файла. Остальное (видео, файлы
    /// без имени, чужой ключ, недописанные .tmp) удаляем. Вызывать до запуска захвата буфера.
    /// </summary>
    bool RecoverOrphanFiles()
    {
        if (!Directory.Exists(ImagesDir)) return false;
        foreach (var tmp in Directory.GetFiles(ImagesDir, "*.tmp")) try { File.Delete(tmp); } catch { }
        var known = Items.Where(i => i.ImageHash != null).Select(i => i.ImageHash!).ToHashSet();
        bool recovered = false;
        foreach (var group in Directory.GetFiles(ImagesDir).GroupBy(f => Path.GetFileName(f).Split('.')[0]))
        {
            if (known.Contains(group.Key)) continue;
            if (Orphan(group.Key) is { } item) { Place(item); recovered = true; }
            else foreach (var f in group) try { File.Delete(f); } catch { }
        }
        return recovered;
    }

    ClipItem? Orphan(string hash)
    {
        var path = Path.Combine(ImagesDir, hash + ".dat");
        try
        {
            var bytes = ImageFiles.Open(File.ReadAllBytes(path));
            var time = File.GetLastWriteTimeUtc(path);
            (int w, int h, bool gif)? size = bytes switch
            {
                [137, 80, 78, 71, 13, 10, 26, 10, ..] when bytes.Length >= 24 =>
                    (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)), false),
                [(byte)'G', (byte)'I', (byte)'F', ..] when bytes.Length >= 10 => (BitConverter.ToUInt16(bytes, 6), BitConverter.ToUInt16(bytes, 8), true),
                _ => null,
            };
            return size is var (w, h, gif)
                ? new ClipItem { ImageHash = hash, ImageWidth = w, ImageHeight = h, IsGif = gif, CreatedAt = time, LastUsedAt = time, ImageDir = ImagesDir }
                : null;
        }
        catch { return null; } // нет полного файла, битый или чужой ключ DPAPI
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
            int defaultCount = new StorageSettings().HistoryCount;
            TypographyChecks.SelfTest(dir, Check);
            UpdateChecks.SelfTest(dir, Check);
            TextEditorWindow.SelfTest(dir, Check);
            TextSubstitution.SelfTest(Check);
            OcrChecks.SelfTest(dir, Check);
            ClipboardWatcher.SelfTest(dir, Check);
            StabilityChecks.Run(dir, Check);
            PanelWindow.SelfTestAnimation(dir, Check);
            PanelWindow.SelfTestSensitive(dir, Check);
            PanelWindow.SelfTestVfx(dir, Check);
            TextMerge.SelfTest(Check);
            DragWheelScroll.SelfTest(Check);
            TextMergeWindow.SelfTest(dir, Check);
            PanelWindow.SelfTestMergeSelection(dir, Check);
            PanelWindow.SelfTestSubstitution(dir, Check);
            PanelWindow.SelfTestImageSeries(dir, Check);

            s.Add("a", "{\\rtf a}", null, t0);
            s.Add("b", null, null, t0.AddMinutes(1));
            s.Add("a", null, null, t0.AddMinutes(2));
            Check(s.Items.Count == 2 && s.Items[0].Text == "a", "дубль поднимается наверх, не дублируется");
            Check(s.Items[0].Rtf == "{\\rtf a}", "повтор без форматов не затирает RTF");

            var textDedupe = new HistoryStore(Path.Combine(dir, "text-dedupe", "history.dat"));
            const string slackText = "i'll tweak it";
            textDedupe.Add(" " + slackText, "rtf", null, t0, "slack");
            var slackItem = textDedupe.Items.Single();
            textDedupe.TogglePin(slackItem);
            var slackGroup = textDedupe.AddGroup("Slack")!;
            textDedupe.MovePinned(slackItem, slackGroup.Id);
            textDedupe.SetTileColor(slackItem, 3);
            textDedupe.Add(slackText + "\u00a0", null, "html", t0.AddSeconds(1), "ChatGPT");
            Check(textDedupe.Items.Count == 1 && slackItem.Text == " " + slackText && slackItem.Id == textDedupe.Items[0].Id
                && slackItem.CreatedAt == t0 && slackItem.LastUsedAt == t0.AddSeconds(1) && slackItem.Pinned
                && slackItem.PinnedGroupId == slackGroup.Id && slackItem.TileColor == 3 && slackItem.Rtf == "rtf" && slackItem.Html == "html",
                "дубли текста: крайние пробелы/NBSP не создают новую запись, исходный текст и закрепление сохраняются");
            foreach (string distinct in new[] { "i'll  tweak it", "I'll tweak it", "\t" + slackText,
                "print('a')\nprint('b')", " print('a')\nprint('b')", "print('a')\r\nprint('b')" })
                textDedupe.Add(distinct, null, null, t0.AddSeconds(2));
            Check(textDedupe.Items.Count == 7, "дубли текста: внутренние пробелы, регистр, табуляции и многострочные отступы различаются");
            var oldText = new ClipItem { Text = slackText, CreatedAt = t0.AddSeconds(3), LastUsedAt = t0.AddSeconds(3), Html = "latest-html", SourceApp = "slack" };
            textDedupe.Items.Insert(0, oldText); // Legacy copies had exact-only matching.
            var secondPin = new ClipItem { Text = slackText, Pinned = true, PinnedGroupId = slackGroup.Id, PinnedOrder = 99, TileColor = 4,
                CreatedAt = t0, LastUsedAt = t0 };
            textDedupe.Items.Add(secondPin);
            textDedupe.AddMedia("synthetic-file", "text", "message.txt", 42, false, t0);
            textDedupe.Save();
            byte[] beforeDedupe = File.ReadAllBytes(Path.Combine(dir, "text-dedupe", "history.dat"));
            var restoredText = new HistoryStore(Path.Combine(dir, "text-dedupe", "history.dat"));
            restoredText.Load(t0.AddSeconds(4));
            var retainedPin = restoredText.Items.Single(i => i.Id == slackItem.Id);
            Check(restoredText.Items.Count == 9 && restoredText.Items.All(i => i.Id != oldText.Id) && retainedPin.LastUsedAt == oldText.LastUsedAt
                && retainedPin.Pinned && retainedPin.PinnedGroupId == slackGroup.Id && retainedPin.TileColor == 3
                && restoredText.Items.Any(i => i.Id == secondPin.Id && i.PinnedOrder == 99 && i.TileColor == 4)
                && restoredText.Items.Any(i => i.IsMedia && i.FileName == "message.txt") && restoredText.Items[0].Id == retainedPin.Id,
                "дубли при загрузке: лишняя обычная копия объединяется, обе закреплённые позиции и файлы остаются");
            var dedupeBackups = Directory.GetFiles(Path.Combine(dir, "text-dedupe"), "history.before-dedup-*.dat");
            Check(dedupeBackups.Length == 1 && File.ReadAllBytes(dedupeBackups[0]).SequenceEqual(beforeDedupe),
                "дубли при загрузке: резервная копия сохраняет исходный зашифрованный файл байт-в-байт");
            restoredText.Load(t0.AddSeconds(5));
            Check(restoredText.Items.Count == 9 && Directory.GetFiles(Path.Combine(dir, "text-dedupe"), "history.before-dedup-*.dat").Length == 1,
                "дубли при загрузке: очистка сохранена и повторная загрузка не создаёт новые резервные копии");

            s.TogglePin(s.Items.Single(i => i.Text == "b"));
            s.Add("old", null, null, t0.AddMinutes(3));
            s.Prune(t0.AddDays(8));
            Check(s.Items.Select(i => i.Text).SequenceEqual(["b"]), "старше 7 дней удаляются, закреплённые остаются");

            for (int i = 0; i < 150; i++) s.Add($"n{i}", null, null, t0.AddDays(10).AddSeconds(i));
            Check(s.Items.Count(i => !i.Pinned) == defaultCount, "лимит 100 незакреплённых");
            Check(s.Items.Any(i => i.Text == "n149") && !s.Items.Any(i => i.Text == "n49"), "лимит режет самые старые");
            Check(s.Items.Any(i => i.Text == "b" && i.Pinned), "закреплённая переживает лимит");
            var unpin = new HistoryStore(Path.Combine(dir, "unpin", "history.dat"));
            var unpinNow = DateTime.UtcNow.AddMinutes(-2);
            for (int i = 0; i <= defaultCount; i++)
            {
                unpin.Add($"pin{i}", null, null, unpinNow.AddSeconds(i));
                unpin.TogglePin(unpin.Items.First());
            }
            foreach (var item in unpin.Items.ToList()) unpin.TogglePin(item);
            Check(unpin.Items.Count == defaultCount && !unpin.Items.Any(i => i.Text == "pin0"),
                "после массового открепления лимит истории применяется сразу");
            var changed = new HistoryStore(Path.Combine(dir, "custom", "history.dat"));
            for (int i = 0; i < 25; i++) changed.Add($"custom{i}", null, null, t0.AddSeconds(i));
            changed.TogglePin(changed.Items.First());
            bool pruningNotified = false;
            changed.Changed += () => pruningNotified = true;
            changed.ApplyStorage(new StorageSettings(HistoryCount: 20, HistoryDays: 1), t0.AddHours(1));
            Check(pruningNotified && changed.Items.Count(i => !i.Pinned) == 20 &&
                changed.Items.Any(i => i.Pinned), "новый лимит применяется сразу, закреплённые остаются");
            Check(changed.Prune(t0.AddDays(2)) && changed.Items.Count == 1 && changed.Items[0].Pinned,
                "новый срок удаления не затрагивает закреплённые записи");
            Check(ClipItem.LooksLikeCode(@"D:\Projects\ArkCopyBuffer\publish") && ClipItem.LooksLikeCode("if (x)\n{\n    y();\n}")
                && !ClipItem.LooksLikeCode("Сделай ревью: проверь ветку с занятым буфером.") && !ClipItem.LooksLikeCode("Привет!\nКак дела?"),
                "пути и код — моноширинным, обычный текст — нет");
            Check(new ClipItem { Text = "https://github.com/Arkanoidvfx/MicNoize" } is { Kind: "url", TileTitle: "github.com", TileSub: "Arkanoidvfx/MicNoize" }
                && new ClipItem { Text = "\"D:\\Projects\\notes.md\"" } is { Kind: "path", TileTitle: "notes.md", TileSub: "D:\\Projects" }
                && new ClipItem { Text = "#f26424" } is { Kind: "color", TileTitle: "#F26424" }
                && new ClipItem { Text = "ревью\nбез дыр" } is { Kind: "text", TileTitle: "ревью", TileSub: "без дыр" },
                "плитка закреплённого: ссылка, путь в кавычках, цвет, текст");
            var mapRoute = "https://www.google.com/maps/dir/%D0%A8%D0%B8%D0%BB%D0%B5%D1%80%D0%BE%D0%B2%D0%B0+42,+%D0%91%D0%B5%D0%BE%D0%B3%D1%80%D0%B0%D0%B4/Vizim+doctors+1500+Tulip/@44.8246915,20.3870547,14z/data=!4m14?entry=ttu";
            var mapItem = new ClipItem { Text = mapRoute };
            string googleRouteTitle = UiText.T("Google Карты") + UiText.T(" · маршрут");
            string googlePlaceTitle = UiText.T("Google Карты") + UiText.T(" · место");
            string yandexRouteTitle = UiText.T("Яндекс Карты") + UiText.T(" · маршрут");
            string yandexPlaceTitle = UiText.T("Яндекс Карты") + UiText.T(" · место");
            Check(mapItem is { Kind: "url", TileSub: "Шилерова 42, Београд → Vizim doctors 1500 Tulip" } && mapItem.TileTitle == googleRouteTitle
                && mapItem.IsMapLink && mapItem.Preview == googleRouteTitle + "\nШилерова 42, Београд\n→ Vizim doctors 1500 Tulip"
                && mapItem.Text == mapRoute,
                "Google: длинный маршрут читается в истории, исходный URL сохранён");
            Check(new ClipItem { Text = $"[{mapRoute}]({mapRoute.Replace("&", "\\&")})" } is { Kind: "url", TileTitle: var markdownTitle } && markdownTitle == googleRouteTitle
                && new ClipItem { Text = "https://www.google.com/maps/dir/?api=1&origin=Belgrade+Center&destination=Airport" } is { TileSub: "Belgrade Center → Airport" }
                && new ClipItem { Text = "https://www.google.com/maps/place/%D0%A8%D0%B8%D0%BB%D0%B5%D1%80%D0%BE%D0%B2%D0%B0+42/" } is { TileTitle: var placeTitle, TileSub: "Шилерова 42" } && placeTitle == googlePlaceTitle,
                "Google: Markdown, маршрут с параметрами и адрес");
            Check(new ClipItem { Text = "https://yandex.ru/maps/?rtext=%D0%9C%D0%BE%D1%81%D0%BA%D0%B2%D0%B0~%D0%A2%D0%B2%D0%B5%D1%80%D1%8C&rtt=auto" } is { TileTitle: var yandexTitle, TileSub: "Москва → Тверь" } && yandexTitle == yandexRouteTitle
                && new ClipItem { Text = "https://yandex.ru/maps/213/moscow/?rtext=55.586561%2C37.674627~55.583744%2C37.674338" } is { TileSub: "55.586561,37.674627 → 55.583744,37.674338" }
                && new ClipItem { Text = "https://yandex.ru/maps/213/moscow/org/%D1%82%D1%83%D0%BB%D1%8C%D1%81%D0%BA%D0%B8%D0%B9_%D0%B4%D0%BE%D0%BC/12345/" } is { TileTitle: var yandexPlace, TileSub: "тульский дом" } && yandexPlace == yandexPlaceTitle,
                "Яндекс: маршрут по названиям или координатам и место");
            Check(PromptCompressor.Clean("<think>\nx\n</think>\n<prompt>\nсжато <prompt> внутри\n</prompt>\n", []) == "сжато <prompt> внутри",
                "сжатый промпт: вырезаны размышления и обёртка, теги внутри текста целы");
            var code = new List<string>();
            Check(PromptCompressor.Protect("вот:\n```py\nx = 1\n```\nи всё", code) == "вот:\n[CODE 1]\nи всё" && code is ["```py\nx = 1\n```"],
                "сжатый промпт: блок кода уходит модели меткой");
            Check(PromptCompressor.Clean("Исправь [CODE 1], чтобы работало.", code) == "Исправь, чтобы работало.\r\n```py\r\nx = 1\r\n```",
                "сжатый промпт: метка посреди фразы → блок отдельными строками после неё");
            Check(PromptCompressor.Clean("Исправь функцию.", code).EndsWith("\r\n```py\r\nx = 1\r\n```"),
                "сжатый промпт: потерянный моделью блок кода дописывается в конец");

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
            var expectedPixels = new byte[8 + largePixels.Length];
            BinaryPrimitives.WriteInt32LittleEndian(expectedPixels, 513);
            BinaryPrimitives.WriteInt32LittleEndian(expectedPixels.AsSpan(4), 513);
            largePixels.CopyTo(expectedPixels, 8);
            Check(ImageFiles.Normalize(large).hash == Convert.ToHexString(SHA256.HashData(expectedPixels)), "хэш картинки верен при обработке по частям");
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
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hugePng.AsSpan(16, 4), 1001);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hugePng.AsSpan(20, 4), 1000);
            Check(ClipboardWatcher.FitsPng(new MemoryStream(hugePng)) &&
                !ClipboardWatcher.FitsPng(new MemoryStream(hugePng), new StorageSettings(ImageMegapixels: 1)),
                "пользовательский лимит картинки применяется до декодирования");
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hugePng.AsSpan(16, 4), 100_000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hugePng.AsSpan(20, 4), 100_000);
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
            Check(s.Items[0] is { IsMedia: true, IsImage: false, Preview: "song.mp3" } && s.Items[0].Badge == UiText.T("Аудио"), "аудио — медиа-запись, не картинка");
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
            var oversizedPath = Path.Combine(dir, "oversized.txt");
            using (var largeFile = File.Create(oversizedPath)) largeFile.SetLength(11L * 1024 * 1024);
            var oversizedDrop = new System.Windows.DataObject();
            oversizedDrop.SetFileDropList([oversizedPath]);
            Check(ClipboardWatcher.ReadFile(oversizedDrop) != null &&
                ClipboardWatcher.ReadFile(oversizedDrop, new StorageSettings(FileMegabytes: 10)) == null,
                "пользовательский лимит размера файла применяется при захвате");
            var textBytes = File.ReadAllBytes(textPath);
            var textHash = Convert.ToHexString(SHA256.HashData(textBytes));
            var fileStore = new HistoryStore(Path.Combine(dir, "files", "history.dat"));
            ImageFiles.WriteMedia(fileStore.ImagesDir, textHash, textBytes, null);
            fileStore.AddMedia(textHash, "text", "note.txt", textBytes.Length, false, t0);
            fileStore.Save();
            var fileStore2 = new HistoryStore(Path.Combine(dir, "files", "history.dat"));
            fileStore2.Load(t0);
            Check(fileStore2.Items.Single() is { FileName: "note.txt", IsMedia: true } && fileStore2.Items.Single().Badge == UiText.T("Текстовый файл")
                && ImageFiles.ReadFull(fileStore2.ImagesDir, textHash).SequenceEqual(textBytes),
                "текстовый файл виден после перезапуска и хранится без изменения содержимого");

            var folderPath = Path.Combine(dir, "Исходная папка");
            Directory.CreateDirectory(Path.Combine(folderPath, "вложенная", "пустая"));
            var folderFile = Path.Combine(folderPath, "вложенная", "текст.txt");
            File.WriteAllText(folderFile, "до копирования");
            var folderBytes = new byte[200_000];
            Random.Shared.NextBytes(folderBytes);
            File.WriteAllBytes(Path.Combine(folderPath, "данные.bin"), folderBytes);
            File.SetAttributes(Path.Combine(folderPath, "данные.bin"), FileAttributes.Hidden);
            var folderDrop = new System.Windows.DataObject();
            folderDrop.SetFileDropList([folderPath]);
            folderDrop.SetData(System.Windows.DataFormats.UnicodeText, "текст рядом с папкой");
            Check(ClipboardWatcher.ReadFolder(folderDrop) == folderPath && ClipboardWatcher.ReadFolder(textDrop) == null,
                "FileDrop папки распознаётся отдельно от текста и файлов");
            var folderStore = new HistoryStore(Path.Combine(dir, "folder-history", "history.dat"));
            folderStore.AddFolder(folderPath, t0, "explorer");
            folderStore.AddFolder(folderPath + Path.DirectorySeparatorChar, t0.AddSeconds(1), "explorer");
            var folderItem = folderStore.Items.Single();
            Check(folderItem is { IsFolder: true, IsImage: false, IsShortLived: false, ImageHash: null } && folderItem.Badge == UiText.T("Папка")
                && folderItem.TileTitle == "Исходная папка" && !Directory.Exists(folderStore.ImagesDir),
                "папка хранится одной ссылкой на путь без копирования содержимого");
            folderStore.Add(folderPath, null, null, t0.AddSeconds(2));
            Check(folderStore.Items.Count == 2, "папка и текст с тем же путём не склеиваются");
            folderStore.Save();
            var folderLoaded = new HistoryStore(Path.Combine(dir, "folder-history", "history.dat"));
            folderLoaded.Load(t0.AddDays(2));
            Check(folderLoaded.Items.Any(i => i.IsFolder && i.Text == folderPath && i.SourceApp == "explorer"),
                "ссылка на папку переживает перезапуск и хранится по сроку обычных записей");
            File.WriteAllText(folderFile, "актуальное содержимое");
            var zipPath = FolderArchive.Create(folderPath, Path.Combine(dir, "папка.zip"), CancellationToken.None);
            using (var zip = ZipFile.OpenRead(zipPath))
            using (var text = new StreamReader(zip.GetEntry("Исходная папка/вложенная/текст.txt")!.Open()))
            {
                Check(text.ReadToEnd() == "актуальное содержимое" && zip.GetEntry("Исходная папка/вложенная/пустая/") != null,
                    "ZIP сохраняет структуру, Unicode, пустые папки и текущее содержимое");
                using var blob = zip.GetEntry("Исходная папка/данные.bin")!.Open();
                using var restored = new MemoryStream();
                blob.CopyTo(restored);
                Check(restored.ToArray().SequenceEqual(folderBytes), "ZIP сохраняет скрытые и двоичные файлы без потерь");
            }
            var zipDrop = Paster.FileData([zipPath]);
            Check(zipDrop.GetFileDropList().Cast<string>().SequenceEqual([zipPath]) &&
                !zipDrop.GetDataPresent(System.Windows.DataFormats.UnicodeText), "ZIP передаётся как файл, а не текстовый путь");
            bool staleArchiveStopped = false;
            try { Paster.SetArchiveClipboard(folderItem, zipPath, () => false); }
            catch (OperationCanceledException) { staleArchiveStopped = true; }
            Check(staleArchiveStopped, "отменённая отправка ZIP не перезаписывает буфер обмена");
            File.WriteAllText(folderFile, "обновлено повторно");
            var nextZip = FolderArchive.Create(folderPath, Path.Combine(dir, "следующая.zip"), CancellationToken.None);
            using (var zip = ZipFile.OpenRead(nextZip))
            using (var text = new StreamReader(zip.GetEntry("Исходная папка/вложенная/текст.txt")!.Open()))
                Check(text.ReadToEnd() == "обновлено повторно", "повторная отправка создаёт свежий архив");
            var failedZip = Path.Combine(dir, "failed.zip");
            bool lockedRejected = false;
            using (var locked = new FileStream(folderFile, FileMode.Open, FileAccess.Read, FileShare.None))
                try { FolderArchive.Create(folderPath, failedZip, CancellationToken.None); }
                catch (IOException) { lockedRejected = true; }
            Check(lockedRejected && !File.Exists(failedZip) && !File.Exists(failedZip + ".partial"),
                "занятый файл даёт ошибку без неполного ZIP");
            using (var cancellation = new CancellationTokenSource())
            {
                bool cancelled = false;
                try { FolderArchive.Create(folderPath, failedZip, cancellation.Token, _ => cancellation.Cancel()); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !File.Exists(failedZip) && !File.Exists(failedZip + ".partial"),
                    "отмена удаляет временный архив и не публикует готовый файл");
            }
            bool selfArchiveRejected = false;
            try { FolderArchive.Create(folderPath, Path.Combine(folderPath, "self.zip"), CancellationToken.None); }
            catch (IOException) { selfArchiveRejected = true; }
            Check(selfArchiveRejected, "ZIP не создаётся внутри архивируемой папки");
            bool missingFolderRejected = false;
            try { FolderArchive.Create(Path.Combine(dir, "отсутствует"), failedZip, CancellationToken.None); }
            catch (DirectoryNotFoundException) { missingFolderRejected = true; }
            Check(missingFolderRejected && folderStore.Items.Contains(folderItem), "недоступная папка не удаляет ссылку из истории");
            var loop = Path.Combine(folderPath, "loop");
            bool linkCreated = false;
            try { Directory.CreateSymbolicLink(loop, folderPath); linkCreated = true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { log.AppendLine("skip symlink: " + ex.GetType().Name); }
            if (linkCreated)
            {
                bool linkRejected = false;
                try { FolderArchive.Create(folderPath, failedZip, CancellationToken.None); }
                catch (IOException) { linkRejected = true; }
                finally { Directory.Delete(loop); }
                Check(linkRejected && !File.Exists(failedZip + ".partial"), "ссылка на родителя не зацикливает упаковку");
            }

            var filesA = Path.Combine(dir, "bundle-a", "same.txt");
            var filesB = Path.Combine(dir, "bundle-b", "same.txt");
            var filesC = Path.Combine(dir, "bundle-b", "пустой.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(filesA)!);
            Directory.CreateDirectory(Path.GetDirectoryName(filesB)!);
            File.WriteAllText(filesA, "первый");
            File.WriteAllText(filesB, "второй");
            File.WriteAllBytes(filesC, []);
            var multiDrop = new System.Windows.DataObject();
            multiDrop.SetFileDropList([filesA, filesB, filesC]);
            multiDrop.SetData(System.Windows.DataFormats.UnicodeText, "текст рядом с файлами");
            var files = ClipboardWatcher.ReadFiles(multiDrop);
            Check(files is { Length: 3 }, "несколько файлов из FileDrop распознаются одним пакетом");
            Check(ClipboardWatcher.ReadFiles(multiDrop, new StorageSettings(FileCount: 2)) == null,
                "пользовательский лимит числа файлов применяется при захвате");
            var bundle = FileBundle.Pack(files!);
            bool sizeRejected = false;
            try { FileBundle.Pack(files!, maxBytes: 1); }
            catch (InvalidDataException) { sizeRejected = true; }
            Check(sizeRejected, "пакет повторно проверяет выбранный лимит размера при записи");
            Check(FileBundle.Pack(files!).SequenceEqual(bundle), "повторный пакет имеет тот же хэш");
            var bundleHash = Convert.ToHexString(SHA256.HashData(bundle));
            var bundleStore = new HistoryStore(Path.Combine(dir, "bundle", "history.dat"));
            ImageFiles.WriteMedia(bundleStore.ImagesDir, bundleHash, bundle, null);
            bundleStore.AddMedia(bundleHash, "files", "same.txt, same.txt +1", bundle.Length, false, t0, "explorer", files!.Length);
            bundleStore.Save();
            var bundleLoaded = new HistoryStore(Path.Combine(dir, "bundle", "history.dat"));
            bundleLoaded.Load(t0);
            Check(bundleLoaded.Items.Single() is { FileCount: 3, SourceApp: "explorer" } && bundleLoaded.Items.Single().Badge == UiText.T("Файлы"),
                "пакет и приложение-источник переживают перезапуск");
            var unpacked = FileBundle.Extract(ImageFiles.ReadFull(bundleLoaded.ImagesDir, bundleHash), Path.Combine(dir, "extracted"), 3);
            Check(unpacked.Select(Path.GetFileName).SequenceEqual(["same.txt", "same.txt", "пустой.bin"])
                && !string.Equals(unpacked[0], unpacked[1], StringComparison.OrdinalIgnoreCase)
                && File.ReadAllText(unpacked[0]) == "первый" && File.ReadAllText(unpacked[1]) == "второй"
                && new FileInfo(unpacked[2]).Length == 0,
                "пакет восстанавливает все имена и байты, включая одноимённые и пустые файлы");
            using var malformed = new MemoryStream();
            using (var zip = new ZipArchive(malformed, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntry("000/../outside.txt");
                zip.CreateEntry("001/ok.txt");
            }
            bool unsafeRejected = false;
            try { FileBundle.Extract(malformed.ToArray(), Path.Combine(dir, "unsafe"), 2); }
            catch (InvalidDataException) { unsafeRejected = true; }
            Check(unsafeRejected && !File.Exists(Path.Combine(dir, "outside.txt")), "пакет не распаковывает путь вне временной папки");

            var sourceStore = new HistoryStore(Path.Combine(dir, "sources", "history.dat"));
            sourceStore.Add("same", null, null, t0, "Code");
            sourceStore.Add("same", null, null, t0.AddSeconds(1), "chrome");
            Check(sourceStore.Items.Single().SourceApp == "chrome", "повторная копия запоминает новый источник");
            sourceStore.Save();
            var sourceLoaded = new HistoryStore(Path.Combine(dir, "sources", "history.dat"));
            sourceLoaded.Load(t0);
            Check(sourceLoaded.Items.Single().SourceApp == "chrome", "приложение-источник сохраняется в истории");

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
            Check(layout.AddGroup("")?.Name == UiText.F($"Группа {1}") && layout.AddGroup(" ")?.Name == UiText.F($"Группа {2}"), "пустые названия получают имена по порядку");
            Check(layout.AddGroup(UiText.F($"Группа {2}")) == null && layout.NextGroupName() == UiText.F($"Группа {3}"), "автонумерация не дублирует названия");
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

            var order = new HistoryStore(Path.Combine(dir, "order", "history.dat"));
            order.Add("позже", null, null, t0.AddMinutes(2));
            order.Add("раньше", null, null, t0.AddMinutes(1)); // так приходит картинка, дописанная в фоне
            Check(order.Items.Select(i => i.Text).SequenceEqual(["позже", "раньше"]), "запись встаёт по времени копирования, а не просто наверх");
            order.Touch(order.Items[1].Id, t0.AddMinutes(3));
            Check(order.Items[0].Text == "раньше", "своя вставка поднимает запись по Id");
            Check(JsonSerializer.Serialize("Привет <b>", JsonOptions) == "\"Привет <b>\"", "кириллица и HTML в истории без \\u-экранирования");

            var recover = new HistoryStore(Path.Combine(dir, "recover", "history.dat"));
            ImageFiles.Write(recover.ImagesDir, h1, n1);
            ImageFiles.WriteMedia(recover.ImagesDir, "VIDEOTEST", [1, 2, 3], null);
            File.WriteAllBytes(Path.Combine(recover.ImagesDir, h1 + ".dat.tmp"), [1]);
            recover.Load(DateTime.UtcNow);
            Check(recover.Items.Count == 1 && recover.Items[0] is { IsImage: true, ImageWidth: 2, ImageHeight: 2 } && recover.Items[0].ImageHash == h1,
                "картинка без записи в истории возвращается, а не удаляется");
            Check(!File.Exists(Path.Combine(recover.ImagesDir, "VIDEOTEST.dat")) && !File.Exists(Path.Combine(recover.ImagesDir, h1 + ".dat.tmp")),
                "файлы без записи, которые не восстановить, и недописанные .tmp удаляются");
            var recover2 = new HistoryStore(recover._path);
            recover2.Load(DateTime.UtcNow);
            Check(recover2.Items.Count == 1, "восстановленная картинка записана в историю");

            ClipboardWatcher.SelfTestListener(dir, Check);

            var oldPath = Path.Combine(dir, "legacy", "history.dat"); // своя папка: иначе images/ соседнего теста — «осиротевшие» для неё
            Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
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
            Check(sym2.Items.Any(i => i.Char == "→" && i.Group == UiText.T("Стрелки") && i.Pinned), "закреплённый символ остаётся и в своей группе");
            var panel = new PanelWindow(s2, sym2);
            int outsideDelayChanged = 0;
            panel.OutsideHideDelayChanged += ms => outsideDelayChanged = ms;
            Check(panel.AutoHideOutside && panel.OutsideHideDelayMs == 3000, "автоскрытие по выходу мыши включено, задержка 3 секунды");
            panel.OutsideHideDelayMs = 2500;
            Check(panel.OutsideHideDelayMs == 2500 && outsideDelayChanged == 2500, "задержка автоскрытия меняется в настройках");
            s2.Add("from code", null, null, t0.AddDays(10).AddMinutes(11), "Code");
            s2.Add("from browser", null, null, t0.AddDays(10).AddMinutes(12), "chrome");
            panel.SetSourceFilter("code");
            Check(panel.List.Items.Cast<ClipItem>().Select(i => i.Text).SequenceEqual(["from code"]),
                "фильтр приложения не зависит от регистра и скрывает другие источники");
            panel.SetSourceFilter(null);
            Check(panel.List.Items.Count > 1, "сброс фильтра возвращает остальные записи");
            bool stalePasteStopped = false;
            try { Paster.SetClipboardAsync(new ClipItem { Text = "устаревшая вставка" }, false, dir, () => false).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { stalePasteStopped = true; }
            Check(stalePasteStopped, "устаревшая вставка не меняет буфер");
            sym2.TogglePin("→");
            Check(new SymbolCatalog(symPath).Items.Count(i => i.Group == SymbolCatalog.PinnedGroup) == 1, "открепление сохраняется");
            var dash = sym2.Items.First(i => i.Char == "—");
            Check(SymbolCatalog.Matches(dash, "2014") && SymbolCatalog.Matches(dash, "U+2014") && SymbolCatalog.Matches(dash, UiText.T("Пунктуация"))
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
