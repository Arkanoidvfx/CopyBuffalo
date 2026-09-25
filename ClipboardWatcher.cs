using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

/// <summary>
/// Слушает WM_CLIPBOARDUPDATE через message-only окно и кладёт текст, файлы и картинки в историю.
/// Пропускает то, что приложения просят не сохранять (менеджеры паролей).
/// </summary>
sealed class ClipboardWatcher : IDisposable
{
    const int MaxChars = 1_000_000;          // лимит на каждый текстовый формат; больше — формат не сохраняем
    const long MaxImagePixels = 40_000_000;  // ~8K×5K; больше — не сохраняем (память и диск)
    const long MaxFileBytes = 100L * 1024 * 1024; // файлы больше — не копируем в историю
    static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Файлы из Проводника, которые храним копией (1 день) и вставляем файлом.</summary>
    static readonly Dictionary<string, string> FileKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".gif"] = "gif",
        [".mp4"] = "video", [".mov"] = "video", [".webm"] = "video", [".mkv"] = "video",
        [".avi"] = "video", [".wmv"] = "video", [".m4v"] = "video",
        [".wav"] = "audio", [".mp3"] = "audio", [".ogg"] = "audio",
        [".txt"] = "text", [".md"] = "text", [".log"] = "text", [".csv"] = "text",
        [".json"] = "text", [".xml"] = "text", [".yaml"] = "text", [".yml"] = "text",
        [".ini"] = "text", [".cfg"] = "text", [".toml"] = "text",
    };

    readonly HistoryStore _store;
    readonly HwndSource _source;
    readonly SemaphoreSlim _heavyGate = new(1, 1);
    public bool Paused { get; set; }

    public ClipboardWatcher(HistoryStore store)
    {
        _store = store;
        _source = new HwndSource(new HwndSourceParameters("ArkCopyBufferClipboard") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        _source.AddHook(WndProc);
        if (!Native.AddClipboardFormatListener(_source.Handle))
            throw new InvalidOperationException($"AddClipboardFormatListener failed: {Marshal.GetLastPInvokeError()}");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE && !Paused) _ = CaptureAsync();
        return IntPtr.Zero;
    }

    async Task CaptureAsync()
    {
        // Буфер может быть занят записывающим приложением — несколько попыток.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var d = Clipboard.GetDataObject();
                if (d == null || Excluded(d)) return;
                // FileDrop важнее UnicodeText: некоторые приложения отдают оба формата для одного файла.
                if (ReadFile(d) is var (path, kind)) { await AddFileAsync(path, kind); return; }
                if (ReadText(d) is var (text, rtf, html)) { _store.Add(text, rtf, html, DateTime.UtcNow); return; }
                if (d.GetDataPresent("GIF") && d.GetData("GIF") is MemoryStream { Length: > 0 and <= MaxFileBytes } gifStream)
                { await AddGifAsync(gifStream.ToArray()); return; }
                if (ReadImage(d) is { } image) await AddImageAsync(image);
                return;
            }
            catch (ExternalException) { await Task.Delay(40); }
            catch (Exception e) { App.Trace($"capture skipped: {e.GetType().Name}: {e.Message}"); return; } // владелец умер до отложенного рендера и т.п.
        }
    }

    async Task AddImageAsync(BitmapSource image)
    {
        // Кодирование PNG большой картинки — сотни мс, уводим с UI-потока.
        await RunHeavy(async () =>
        {
            var dir = _store.ImagesDir;
            var (hash, w, h) = await Task.Run(() =>
            {
                var (bgra, hash) = ImageFiles.Normalize(image);
                ImageFiles.Write(dir, hash, bgra);
                return (hash, bgra.PixelWidth, bgra.PixelHeight);
            });
            _store.AddImage(hash, w, h, DateTime.UtcNow);
        });
    }

    Task AddGifAsync(byte[] gif) => RunHeavy(() => WriteGifAsync(gif));

    async Task WriteGifAsync(byte[] gif)
    {
        if (gif is not [(byte)'G', (byte)'I', (byte)'F', ..] || gif.Length < 10) return; // расширение/формат ещё не значит, что внутри GIF
        int width = BitConverter.ToUInt16(gif, 6), height = BitConverter.ToUInt16(gif, 8);
        if ((long)width * height > MaxImagePixels) return;
        var dir = _store.ImagesDir;
        var (hash, w, h) = await Task.Run(() =>
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(gif));
            var frame = BitmapDecoder.Create(new MemoryStream(gif), BitmapCreateOptions.None, BitmapCacheOption.OnDemand).Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxImagePixels)
                throw new InvalidDataException("GIF frame exceeds pixel limit");
            var (first, _) = ImageFiles.Normalize(frame);
            ImageFiles.WriteGif(dir, hash, gif, first);
            // Размер — из заголовка (логический экран GIF): первый кадр бывает меньше.
            return (hash, width, height);
        });
        _store.AddImage(hash, w, h, DateTime.UtcNow, gif: true);
    }

    /// <summary>
    /// Один файл из Проводника с известным расширением и не больше 100 МБ.
    /// Браузеры при «Копировать картинку» отдают только статичный кадр — его запишет ReadImage.
    /// </summary>
    internal static (string path, string kind)? ReadFile(IDataObject d)
    {
        if (!d.GetDataPresent(DataFormats.FileDrop) || d.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files) return null;
        var file = new FileInfo(files[0]);
        return FileKinds.TryGetValue(file.Extension, out var kind) && file.Exists
            && file.Length <= MaxFileBytes && (file.Length > 0 || kind == "text")
            ? (file.FullName, kind) : null;
    }

    async Task AddFileAsync(string path, string kind)
    {
        // Чтение и шифрование до 100 МБ — не на UI-потоке.
        await RunHeavy(async () =>
        {
            var bytes = await Task.Run(() => File.ReadAllBytes(path));
            if (bytes.LongLength > MaxFileBytes) return; // файл мог вырасти после ReadFile
            if (kind == "gif") { await WriteGifAsync(bytes); return; }

            var dir = _store.ImagesDir;
            var (hash, hasThumb) = await Task.Run(() =>
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                // Кадр видео даёт кэш миниатюр Проводника; остальные файлы показываем значком.
                var thumb = kind == "video" ? Native.ShellThumbnail(path, 256) : null;
                ImageFiles.WriteMedia(dir, hash, bytes, thumb);
                return (hash, thumb != null);
            });
            _store.AddMedia(hash, kind, Path.GetFileName(path), bytes.LongLength, hasThumb, DateTime.UtcNow);
        });
    }

    async Task RunHeavy(Func<Task> work)
    {
        await _heavyGate.WaitAsync();
        try { await work(); }
        finally { _heavyGate.Release(); }
    }
    static bool Excluded(IDataObject d) =>
        d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")
        || d.GetDataPresent("Clipboard Viewer Ignore")
        || (d.GetDataPresent("CanIncludeInClipboardHistory")
            && d.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4
            && BitConverter.ToInt32(ms.ToArray(), 0) == 0);

    static (string text, string? rtf, string? html)? ReadText(IDataObject d)
    {
        if (d.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text) || text.Length > MaxChars)
            return null;
        return (text, Optional(d, DataFormats.Rtf), Optional(d, DataFormats.Html));
    }

    static string? Optional(IDataObject d, string format)
    {
        try { return d.GetDataPresent(format) && d.GetData(format) is string s && s.Length <= MaxChars ? s : null; }
        catch { return null; }
    }

    /// <summary>
    /// PNG — если источник его дал (браузеры, «Ножницы»): сохраняет прозрачность.
    /// Иначе CF_DIB, разобранный вручную: Clipboard.GetImage() в WPF портит часть DIB.
    /// </summary>
    internal static BitmapSource? ReadImage(IDataObject d)
    {
        BitmapSource? bmp = null;
        foreach (var fmt in new[] { "PNG", "image/png" })
            if (bmp == null && d.GetDataPresent(fmt) && d.GetData(fmt) is MemoryStream png && FitsPng(png))
                bmp = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        if (bmp == null && d.GetDataPresent(DataFormats.Dib) && d.GetData(DataFormats.Dib) is MemoryStream dib && FitsDib(dib))
            bmp = DecodeDib(dib.ToArray());

        if (bmp == null || (long)bmp.PixelWidth * bmp.PixelHeight > MaxImagePixels) return null;
        bmp.Freeze();
        return bmp;
    }

    internal static bool FitsPng(MemoryStream stream)
    {
        Span<byte> header = stackalloc byte[24];
        if (!ReadHeader(stream, header) || !header[..8].SequenceEqual(PngSignature) || !header[12..16].SequenceEqual("IHDR"u8)) return false;
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        return width > 0 && height > 0 && (ulong)width * height <= MaxImagePixels;
    }

    internal static bool FitsDib(MemoryStream stream)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.Length < 40 || !ReadHeader(stream, header) || BinaryPrimitives.ReadInt32LittleEndian(header[..4]) < 40) return false;
        long width = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
        long height = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(header[8..12]));
        return width > 0 && height > 0 && width * height <= MaxImagePixels;
    }

    static bool ReadHeader(MemoryStream stream, Span<byte> header)
    {
        if (stream.Length < header.Length) return false;
        long position = stream.Position;
        try { stream.Position = 0; stream.ReadExactly(header); return true; }
        finally { stream.Position = position; }
    }

    /// <summary>CF_DIB — это BMP без 14-байтного заголовка файла. Дописываем его и отдаём BMP-декодеру.</summary>
    static BitmapSource DecodeDib(byte[] dib)
    {
        int headerSize = BitConverter.ToInt32(dib, 0);
        short bitCount = BitConverter.ToInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int colorsUsed = BitConverter.ToInt32(dib, 32);
        int palette = bitCount <= 8 ? (colorsUsed != 0 ? colorsUsed : 1 << bitCount) * 4 : 0;
        int masks = headerSize == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS со старым заголовком
        int pixelOffset = 14 + headerSize + masks + palette;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B'; file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        return BitmapFrame.Create(new MemoryStream(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    public void Dispose()
    {
        Native.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}
