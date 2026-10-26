using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ArkCopyBuffer;

/// <summary>
/// Слушает WM_CLIPBOARDUPDATE через message-only окно и кладёт текст, файлы и картинки в историю.
/// Окно и чтение буфера — на своём STA-потоке: Excel/Word отдают RTF/HTML отложенным рендером, бывает секундами,
/// а UI-поток должен в это время открывать панель. Тяжёлое (декодирование, хэш, шифрование) — в пуле,
/// в историю — через UI-диспетчер.
/// Помеченный программами чувствительный текст сохраняет зашифрованным и маскирует в истории.
/// </summary>
sealed class ClipboardWatcher : IDisposable
{
    static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Файлы из Проводника, которые храним копией (1 день) и вставляем файлом.</summary>
    static readonly Dictionary<string, string> FileKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".gif"] = "gif",
        [".mp4"] = "video", [".mov"] = "video", [".webm"] = "video", [".mkv"] = "video",
        [".avi"] = "video", [".wmv"] = "video", [".m4v"] = "video",
        [".wav"] = "audio", [".mp3"] = "audio", [".ogg"] = "audio",
        [".m4a"] = "audio", [".flac"] = "audio", [".opus"] = "audio", [".mka"] = "audio",
        [".ac3"] = "audio", [".eac3"] = "audio", [".dts"] = "audio",
        [".txt"] = "text", [".md"] = "text", [".log"] = "text", [".csv"] = "text",
        [".json"] = "text", [".xml"] = "text", [".yaml"] = "text", [".yml"] = "text",
        [".ini"] = "text", [".cfg"] = "text", [".toml"] = "text",
    };

    readonly HistoryStore _store;
    StorageSettings _storage;
    public StorageSettings Storage { get => Volatile.Read(ref _storage); set => Volatile.Write(ref _storage, value); }
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly Dispatcher _worker;
    readonly Func<uint> _sequence;
    readonly Func<StorageSettings, (uint sequence, IntPtr owner, IDataObject? data)> _readClipboard;
    readonly SemaphoreSlim _heavyGate = new(1, 1);
    HwndSource? _source;
    uint? _lastSequence; // только поток _worker
    volatile bool _paused;
    int _pendingCaptures;
    internal bool Capturing => Volatile.Read(ref _pendingCaptures) != 0;
    public bool Paused { get => _paused; set => _paused = value; }

    ClipboardWatcher(HistoryStore store, Func<uint> sequence, Func<StorageSettings, (uint sequence, IntPtr owner, IDataObject? data)> readClipboard)
    {
        _store = store;
        _storage = store.Storage;
        _worker = Dispatcher.CurrentDispatcher;
        _sequence = sequence;
        _readClipboard = readClipboard;
    }

    public ClipboardWatcher(HistoryStore store)
        : this(store, Native.GetClipboardSequenceNumber, limits => ClipboardSnapshot.Read(limits))
    {
        Dispatcher? worker = null;
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            worker = Dispatcher.CurrentDispatcher;
            try
            {
                _source = new HwndSource(new HwndSourceParameters("ArkCopyBufferClipboard") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
                _source.AddHook(WndProc);
                if (!Native.AddClipboardFormatListener(_source.Handle))
                    throw new InvalidOperationException($"AddClipboardFormatListener failed: {Marshal.GetLastPInvokeError()}");
            }
            catch (Exception e) { error = e; }
            ready.Set();
            if (error == null) Dispatcher.Run();
        }) { IsBackground = true, Name = "ClipboardWatcher" };
        thread.SetApartmentState(ApartmentState.STA); // OLE-буфер работает только в STA
        thread.Start();
        ready.Wait();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        _worker = worker!;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE) _ = CaptureCurrent();
        return IntPtr.Zero;
    }

    // Snapshot each new copy immediately; a debounce can lose the previous screenshot.
    async Task CaptureCurrent()
    {
        if (Paused) return;
        uint sequence = _sequence();
        if (_lastSequence == sequence) return;
        _lastSequence = sequence;
        Interlocked.Increment(ref _pendingCaptures);
        try { await CaptureAsync(DateTime.UtcNow, sequence); }
        finally { Interlocked.Decrement(ref _pendingCaptures); }
    }

    async Task CaptureAsync(DateTime now, uint sequence)
    {
        var limits = Storage; // один набор лимитов для всего копирования
        // Буфер может быть занят записывающим приложением. ShareX после долгого простоя рендерит большой скриншот
        // (OleFlushClipboard) дольше секунды — повторяем по времени, а не по числу попыток.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (int attempt = 0; ; attempt++)
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) { App.Trace($"capture gave up after {attempt} attempts: clipboard busy"); return; }
            try
            {
                if (Paused || _sequence() != sequence) return; // не перечитывать новое копирование из старого повтора
                var (capturedSequence, owner, d) = _readClipboard(limits);
                _lastSequence = capturedSequence; // Associate the snapshot with its copy, never with a later clipboard change.
                if (d == null) return;
                if (OwnItem(d) is { } id)
                {
                    if (id != Guid.Empty) await RunQueued(async () => await _ui.InvokeAsync(() => _store.Touch(id, now)));
                    return;
                }
                var sourceApp = SourceApp(owner);
                if (Sensitive(d))
                {
                    if (d.GetData(DataFormats.UnicodeText) is string secret && secret.Length > 0 && secret.Length <= limits.MaxChars)
                        await RunQueued(async () => await _ui.InvokeAsync(() => _store.Add(secret, null, null, now, sourceApp, sensitive: true)));
                    return;
                }
                // FileDrop важнее UnicodeText: некоторые приложения отдают оба формата для одного файла.
                if (ReadFolder(d) is { } folder) { await RunQueued(async () => await _ui.InvokeAsync(() => _store.AddFolder(folder, now, sourceApp))); return; }
                if (ReadFiles(d, limits) is { } paths) { await RunQueued(() => AddFilesAsync(paths, now, sourceApp, limits)); return; }
                if (ReadFile(d, limits) is var (path, kind)) { await RunQueued(() => AddFileAsync(path, kind, now, sourceApp, limits)); return; }
                if (ReadText(d, limits) is var (text, rtf, html)) { await RunQueued(async () => await _ui.InvokeAsync(() => _store.Add(text, rtf, html, now, sourceApp))); return; }
                if (d.GetDataPresent("GIF") && d.GetData("GIF") is MemoryStream { Length: > 0 } gifStream && gifStream.Length <= limits.MaxFileBytes)
                {
                    var gif = gifStream.ToArray();
                    await RunQueued(() => AddGifAsync(gif, now, sourceApp, limits));
                    return;
                }
                if (ReadImageData(d, limits) is { } image) await RunQueued(() => AddImageAsync(image, now, sourceApp, limits));
                return;
            }
            catch (ExternalException) { await Task.Delay(Math.Min(5 << Math.Min(attempt, 5), 100)); }
            catch (Exception e) { App.Trace($"capture skipped: {e.GetType().Name}: {e.Message}"); return; } // владелец умер до отложенного рендера и т.п.
        }
    }

    /// <summary>Запись, которую положили в буфер мы сами (вставка из панели): Paster помечает её Id.</summary>
    static Guid? OwnItem(IDataObject d) => // HGLOBAL может быть округлён вверх — берём первые 16 байт
        d.GetDataPresent(Paster.OwnFormat) && d.GetData(Paster.OwnFormat) is MemoryStream { Length: >= 16 } ms ? new Guid(ms.ToArray().AsSpan(0, 16)) : null;

    static string? SourceApp(IntPtr owner)
    {
        if (owner == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(owner, out uint pid);
        if (pid == 0) return null;
        try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); return process.ProcessName; }
        catch { return null; } // владелец буфера успел закрыться
    }

    async Task AddImageAsync((MemoryStream data, bool dib) image, DateTime now, string? sourceApp, StorageSettings limits)
    {
        // Декодирование и кодирование PNG большой картинки — сотни мс: в пуле, поток буфера свободен для следующего копирования.
        var dir = _store.ImagesDir;
        var result = await Task.Run(() =>
        {
            if (DecodeImage(image, limits) is not { } bitmap) return ((string hash, int w, int h)?)null;
            var (bgra, hash) = ImageFiles.Normalize(bitmap);
            ImageFiles.Write(dir, hash, bgra, limits.ThumbnailPixels);
            return (hash, bgra.PixelWidth, bgra.PixelHeight);
        });
        if (result is var (hash, w, h)) await _ui.InvokeAsync(() => _store.AddImage(hash, w, h, now, sourceApp: sourceApp));
    }

    async Task AddGifAsync(byte[] gif, DateTime now, string? sourceApp, StorageSettings limits)
    {
        if (gif is not [(byte)'G', (byte)'I', (byte)'F', ..] || gif.Length < 10) return; // расширение/формат ещё не значит, что внутри GIF
        int width = BitConverter.ToUInt16(gif, 6), height = BitConverter.ToUInt16(gif, 8);
        if ((long)width * height > limits.MaxImagePixels) return;
        var dir = _store.ImagesDir;
        var (hash, w, h) = await Task.Run(() =>
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(gif));
            var frame = BitmapDecoder.Create(new MemoryStream(gif), BitmapCreateOptions.None, BitmapCacheOption.OnDemand).Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > limits.MaxImagePixels)
                throw new InvalidDataException("GIF frame exceeds pixel limit");
            var (first, _) = ImageFiles.Normalize(frame);
            ImageFiles.WriteGif(dir, hash, gif, first, limits.ThumbnailPixels);
            // Размер — из заголовка (логический экран GIF): первый кадр бывает меньше.
            return (hash, width, height);
        });
        await _ui.InvokeAsync(() => _store.AddImage(hash, w, h, now, gif: true, sourceApp: sourceApp));
    }

    /// <summary>Копирование одной папки: сохраняем путь, не обходим содержимое.</summary>
    internal static string? ReadFolder(IDataObject d) =>
        d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths
        && Directory.Exists(paths[0]) ? Path.GetFullPath(paths[0]) : null;

    /// <summary>
    /// Один файл из Проводника с известным расширением и в пределах выбранного лимита.
    /// Браузеры при «Копировать картинку» отдают только статичный кадр — его запишет ReadImage.
    /// </summary>
    internal static (string path, string kind)? ReadFile(IDataObject d, StorageSettings? settings = null)
    {
        var limits = settings ?? new StorageSettings();
        if (!d.GetDataPresent(DataFormats.FileDrop) || d.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files) return null;
        var file = new FileInfo(files[0]);
        return FileKinds.TryGetValue(file.Extension, out var kind) && file.Exists
            && file.Length <= limits.MaxFileBytes && (file.Length > 0 || kind == "text")
            ? (file.FullName, kind) : null;
    }

    /// <summary>Несколько обычных файлов — одним пакетом; папки и наборы сверх лимита не сохраняем.</summary>
    internal static string[]? ReadFiles(IDataObject d, StorageSettings? settings = null)
    {
        var limits = settings ?? new StorageSettings();
        if (!d.GetDataPresent(DataFormats.FileDrop) || d.GetData(DataFormats.FileDrop) is not string[] files ||
            files.Length < 2 || files.Length > limits.FileCount) return null;
        long total = 0;
        foreach (var path in files)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > limits.MaxFileBytes - total) return null;
            total += file.Length;
        }
        return files;
    }

    async Task AddFilesAsync(string[] paths, DateTime now, string? sourceApp, StorageSettings limits)
    {
        var (hash, size) = await Task.Run(() =>
        {
            var bundle = FileBundle.Pack(paths, limits.MaxFileBytes, limits.FileCount);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bundle));
            ImageFiles.WriteMedia(_store.ImagesDir, hash, bundle, null);
            return (hash, bundle.LongLength);
        });
        var names = string.Join(", ", paths.Take(2).Select(Path.GetFileName));
        if (paths.Length > 2) names += $" +{paths.Length - 2}";
        await _ui.InvokeAsync(() => _store.AddMedia(hash, "files", names, size, false, now, sourceApp, paths.Length));
    }

    async Task AddFileAsync(string path, string kind, DateTime now, string? sourceApp, StorageSettings limits)
    {
        // Чтение и шифрование большого файла — в пуле.
        var bytes = await Task.Run(() => File.ReadAllBytes(path));
        if (bytes.LongLength > limits.MaxFileBytes) return; // файл мог вырасти после ReadFile
        if (kind == "gif") { await AddGifAsync(bytes, now, sourceApp, limits); return; }

        var dir = _store.ImagesDir;
        var (hash, hasThumb) = await Task.Run(() =>
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            // Кадр видео даёт кэш миниатюр Проводника; остальные файлы показываем значком.
            var thumb = kind == "video" ? Native.ShellThumbnail(path, limits.ThumbnailPixels) : null;
            ImageFiles.WriteMedia(dir, hash, bytes, thumb, limits.ThumbnailPixels);
            return (hash, thumb != null);
        });
        await _ui.InvokeAsync(() => _store.AddMedia(hash, kind, Path.GetFileName(path), bytes.LongLength, hasThumb, now, sourceApp));
    }

    /// <summary>Общая очередь сохраняет порядок всех копирований и ограничивает тяжёлую обработку.</summary>
    async Task RunQueued(Func<Task> work)
    {
        await _heavyGate.WaitAsync();
        try { await work(); }
        catch (Exception e) { App.Trace($"capture skipped: {e.GetType().Name}: {e.Message}"); }
        finally { _heavyGate.Release(); }
    }
    internal static bool Sensitive(IDataObject d) =>
        d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")
        || d.GetDataPresent("Clipboard Viewer Ignore")
        || d.GetDataPresent("org.nspasteboard.ConcealedType")
        || d.GetDataPresent("application/x-nspasteboard-concealed-type")
        || (d.GetDataPresent("CanIncludeInClipboardHistory")
            && d.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4
            && BitConverter.ToInt32(ms.ToArray(), 0) == 0)
        || new[] { "x-kde-passwordManagerHint", "application/x-kde-passwordManagerHint" }.Any(format =>
            d.GetDataPresent(format) && d.GetData(format) is MemoryStream { Length: <= 32 } hint
            && System.Text.Encoding.UTF8.GetString(hint.ToArray()).TrimEnd('\0') == "secret");

    static (string text, string? rtf, string? html)? ReadText(IDataObject d, StorageSettings limits)
    {
        if (d.GetData(DataFormats.UnicodeText) is not string text || string.IsNullOrWhiteSpace(text) || text.Length > limits.MaxChars)
            return null;
        return (text, Optional(d, DataFormats.Rtf, limits), Optional(d, DataFormats.Html, limits));
    }

    static string? Optional(IDataObject d, string format, StorageSettings limits)
    {
        try { return d.GetDataPresent(format) && d.GetData(format) is string s && s.Length <= limits.MaxChars ? s : null; }
        catch { return null; }
    }

    /// <summary>
    /// PNG — если источник его дал (браузеры, «Ножницы»): сохраняет прозрачность.
    /// Иначе CF_DIB, разобранный вручную: Clipboard.GetImage() в WPF портит часть DIB.
    /// </summary>
    internal static BitmapSource? ReadImage(IDataObject d) => ReadImageData(d, new()) is { } image ? DecodeImage(image, new()) : null;

    /// <summary>Только данные и проверка размера по заголовку: декодировать — не в потоке буфера.</summary>
    static (MemoryStream data, bool dib)? ReadImageData(IDataObject d, StorageSettings limits)
    {
        foreach (var fmt in new[] { "PNG", "image/png" })
            if (d.GetDataPresent(fmt) && d.GetData(fmt) is MemoryStream png && FitsPng(png, limits)) return (png, false);
        if (d.GetDataPresent(DataFormats.Dib) && d.GetData(DataFormats.Dib) is MemoryStream dib && FitsDib(dib, limits)) return (dib, true);
        return null;
    }

    static BitmapSource? DecodeImage((MemoryStream data, bool dib) image, StorageSettings limits)
    {
        var bmp = image.dib ? DecodeDib(image.data.ToArray())
            : BitmapFrame.Create(image.data, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if ((long)bmp.PixelWidth * bmp.PixelHeight > limits.MaxImagePixels) return null;
        bmp.Freeze();
        return bmp;
    }

    internal static bool FitsPng(MemoryStream stream, StorageSettings? settings = null)
    {
        var limits = settings ?? new StorageSettings();
        Span<byte> header = stackalloc byte[24];
        if (!ReadHeader(stream, header) || !header[..8].SequenceEqual(PngSignature) || !header[12..16].SequenceEqual("IHDR"u8)) return false;
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        return width > 0 && height > 0 && (ulong)width * height <= (ulong)limits.MaxImagePixels;
    }

    internal static bool FitsDib(MemoryStream stream, StorageSettings? settings = null)
    {
        var limits = settings ?? new StorageSettings();
        Span<byte> header = stackalloc byte[12];
        if (stream.Length < 40 || !ReadHeader(stream, header) || BinaryPrimitives.ReadInt32LittleEndian(header[..4]) < 40) return false;
        long width = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
        long height = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(header[8..12]));
        return width > 0 && height > 0 && width * height <= limits.MaxImagePixels;
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

    internal static void SelfTest(string dir, Action<bool, string> check)
    {
        static (IDataObject data, string hash) Image(byte blue, bool dib = false)
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32,
                null, new byte[] { blue, 0, 0, 255 }, 4);
            BitmapEncoder encoder = dib ? new BmpBitmapEncoder() : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var bytes = stream.ToArray();
            var data = new DataObject();
            data.SetData(dib ? DataFormats.Dib : "PNG", new MemoryStream(dib ? bytes[14..] : bytes));
            return (data, ImageFiles.Normalize(bitmap).hash);
        }

        static void Complete(Task task)
        {
            var frame = new DispatcherFrame();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (task.IsCompleted || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            if (!task.IsCompleted) throw new TimeoutException("Synthetic clipboard capture did not finish");
            task.GetAwaiter().GetResult();
        }

        var store = new HistoryStore(Path.Combine(dir, "rapid-capture", "history.dat"));
        var a = Image(10);
        var b = Image(20, dib: true);
        var c = Image(30);
        IDataObject current = a.data;
        uint sequence = uint.MaxValue;
        int reads = 0;
        var watcher = new ClipboardWatcher(store, () => sequence, _ => { reads++; return (sequence, IntPtr.Zero, current); });
        watcher._heavyGate.Wait(); // Hold image processing while the next screenshots arrive.
        var first = watcher.CaptureCurrent();
        check(watcher.Capturing, "pending clipboard work blocks automatic update shutdown");
        check(reads == 1 && !first.IsCompleted, "rapid capture snapshots the first PNG before background processing");
        current = b.data;
        sequence++;
        var second = watcher.CaptureCurrent();
        check(reads == 2 && !second.IsCompleted, "rapid capture snapshots the next DIB immediately, including sequence wrap to zero");
        current = c.data;
        sequence++;
        var third = watcher.CaptureCurrent();
        check(watcher.CaptureCurrent().IsCompleted && reads == 3, "duplicate clipboard notifications do not queue another image");
        watcher._heavyGate.Release();
        Complete(Task.WhenAll(first, second, third));
        check(!watcher.Capturing, "completed clipboard work releases the update shutdown guard");
        check(store.Items.Select(i => i.ImageHash).SequenceEqual([c.hash, b.hash, a.hash]),
            "rapid PNG/DIB screenshots all reach history in copy order while processing is busy");
        store.Save();
        var loaded = new HistoryStore(Path.Combine(dir, "rapid-capture", "history.dat"));
        loaded.Load(DateTime.UtcNow);
        check(loaded.Items.Select(i => i.ImageHash).SequenceEqual([c.hash, b.hash, a.hash])
            && loaded.Items.All(i => ImageFiles.Normalize(ImageFiles.Decode(ImageFiles.ReadFull(loaded.ImagesDir, i.ImageHash!))).hash == i.ImageHash),
            "every rapid screenshot survives encrypted history and pixel roundtrip");

        int attempts = 0;
        bool busy = true;
        var retryStore = new HistoryStore(Path.Combine(dir, "capture-retry", "history.dat"));
        var retry = new ClipboardWatcher(retryStore, () => sequence, _ =>
        {
            attempts++;
            if (busy) throw new ExternalException("Synthetic clipboard lock");
            return (sequence, IntPtr.Zero, a.data);
        });
        var stale = retry.CaptureCurrent();
        sequence++;
        busy = false;
        var fresh = retry.CaptureCurrent();
        Complete(Task.WhenAll(stale, fresh));
        check(attempts == 2 && retryStore.Items.Count == 1, "a locked old copy cannot retry against a newer clipboard sequence");
        busy = true;
        sequence++;
        var locked = retry.CaptureCurrent();
        busy = false;
        Complete(locked);
        check(attempts == 4, "a busy clipboard is retried when its sequence is unchanged");
        retry.Paused = true;
        sequence++;
        Complete(retry.CaptureCurrent());
        check(attempts == 4, "paused capture does not read the clipboard");
        retry.Paused = false;
        var ignored = new DataObject();
        ignored.SetData("ExcludeClipboardContentFromMonitorProcessing", true);
        ignored.SetData("PNG", c.data.GetData("PNG"));
        var excluded = new ClipboardWatcher(retryStore, () => sequence, _ => (sequence, IntPtr.Zero, ignored));
        Complete(excluded.CaptureCurrent());
        check(retryStore.Items.Count == 1, "sensitive non-text clipboard payloads are still excluded");

        var burstStore = new HistoryStore(Path.Combine(dir, "mixed-capture", "history.dat"));
        var burst = new ClipboardWatcher(burstStore, () => sequence, _ => (sequence, IntPtr.Zero, current));
        var folderPath = Path.Combine(dir, "copied-folder");
        Directory.CreateDirectory(folderPath);
        var filePath = Path.Combine(dir, "copied.txt");
        File.WriteAllText(filePath, "synthetic copied file");
        var otherPath = Path.Combine(dir, "other.bin");
        File.WriteAllBytes(otherPath, [1, 2, 3]);
        static IDataObject Text(string text, string? rtf = null, string? html = null)
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            if (rtf != null) data.SetData(DataFormats.Rtf, rtf);
            if (html != null) data.SetData(DataFormats.Html, html);
            return data;
        }
        static IDataObject Files(params string[] paths)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            return data;
        }
        const string url = "https://github.com/VirusTotal/virustotal-mcp";
        const string skillUrl = url + "/blob/main/skills/virustotal-threat-intelligence/SKILL.md";
        const string rich = "Copied rich text";
        const string rtf = "{\\rtf1 Copied rich text}";
        const string html = "<b>Copied rich text</b>";
        var gif = new DataObject();
        gif.SetData("GIF", new MemoryStream(Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7")));
        IDataObject[] copies = [a.data, Text(url), Text(skillUrl), b.data, Text(rich, rtf, html),
            Files(folderPath), Files(filePath), Files(filePath, otherPath), gif, Text("    code\n\tvalue"), c.data];
        burst._heavyGate.Wait();
        var tasks = new List<Task>();
        foreach (var copy in copies)
        {
            current = copy;
            sequence++;
            tasks.Add(burst.CaptureCurrent());
        }
        check(burstStore.Items.Count == 0 && tasks.All(t => !t.IsCompleted),
            "all clipboard kinds snapshot immediately and share the busy processing queue");
        burst._heavyGate.Release();
        Complete(Task.WhenAll(tasks));
        check(burstStore.Items.Count == copies.Length, "mixed rapid burst loses no URL, text, PNG, DIB, GIF, folder, file or bundle");
        var items = burstStore.Items.Reverse().ToArray();
        check(items[1].Text == url && items[2].Text == skillUrl,
            "both reported VirusTotal URLs survive rapid copying with a common prefix");
        check(items[0].ImageHash == a.hash && items[3].ImageHash == b.hash && items[^1].ImageHash == c.hash
            && items[4].Text == rich && items[4].Rtf == rtf && items[4].Html == html
            && items[5].MediaKind == "folder" && items[6].MediaKind == "text" && items[7].MediaKind == "files"
            && items[8].IsGif && items[9].Text == "    code\n\tvalue",
            "mixed clipboard payloads retain copy order, formatting, paths and exact code whitespace");
        burstStore.Save();
        var burstLoaded = new HistoryStore(Path.Combine(dir, "mixed-capture", "history.dat"));
        burstLoaded.Load(DateTime.UtcNow);
        check(burstLoaded.Items.Select(i => i.Id).SequenceEqual(burstStore.Items.Select(i => i.Id))
            && burstLoaded.Items.Any(i => i.Text == url) && burstLoaded.Items.Any(i => i.Text == skillUrl),
            "every mixed burst entry survives encrypted save and reload in copy order");

        var rendered = new ClipboardWatcher(burstStore, () => sequence, _ => { sequence++; return (sequence, IntPtr.Zero, Text("delayed render")); });
        Complete(rendered.CaptureCurrent());
        check(rendered.CaptureCurrent().IsCompleted && burstStore.Items.Count == copies.Length + 1,
            "delayed rendering sequence changes do not capture the same payload twice");

        var secretStore = new HistoryStore(Path.Combine(dir, "sensitive-capture", "history.dat"));
        var secretWatcher = new ClipboardWatcher(secretStore, () => sequence, _ => (sequence, IntPtr.Zero, current));
        foreach (var marker in new[] { "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore",
            "CanIncludeInClipboardHistory", "org.nspasteboard.ConcealedType", "application/x-nspasteboard-concealed-type", "x-kde-passwordManagerHint" })
        {
            var marked = new DataObject();
            marked.SetData(DataFormats.UnicodeText, "synthetic secret " + marker);
            marked.SetData(DataFormats.Rtf, "private rich text");
            marked.SetData(marker, marker == "CanIncludeInClipboardHistory" ? new MemoryStream(new byte[4])
                : marker == "x-kde-passwordManagerHint" ? new MemoryStream("secret"u8.ToArray()) : (object)true);
            current = marked;
            sequence++;
            Complete(secretWatcher.CaptureCurrent());
            check(secretStore.Items[0] is { IsSensitive: true, Preview: "********", Rtf: null, Html: null },
                "marked password is retained masked without rich formats: " + marker);
        }
        current = Paster.TextClipboard("internal copy", excludeFromHistory: true);
        sequence++;
        Complete(secretWatcher.CaptureCurrent());
        check(secretStore.Items.Count == 6, "our ignored internal writes still do not create password history entries");
    }

    /// <summary>Не ждём: поток может висеть в отложенном рендере чужого приложения, а выход не должен.</summary>
    public void Dispose()
    {
        _worker.BeginInvoke(() =>
        {
            if (_source == null) return;
            Native.RemoveClipboardFormatListener(_source.Handle);
            _source.Dispose();
        });
        _worker.BeginInvokeShutdown(DispatcherPriority.Background);
    }
}
