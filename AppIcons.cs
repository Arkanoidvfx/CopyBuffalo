using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ArkCopyBuffer;

/// <summary>
/// Значки программ, из которых копировали. Своих картинок в сборке нет: значок берётся из самой программы, один раз, и лежит
/// рядом с историей маленьким PNG (128 px, обычно 5–20 КБ). Программу узнаём при копировании; для старых записей — среди
/// запущенных, в App Paths и по ярлыку меню «Пуск».
/// </summary>
static class AppIcons
{
    const int Size = 128;
    static readonly ConcurrentDictionary<string, BitmapSource?> Known = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, byte> Looking = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Папка кэша; null (проверки) — только память, чужой диск не трогаем.</summary>
    internal static string? Folder { get; set; }
    /// <summary>Значок нашёлся; из потока поиска.</summary>
    internal static event Action<string>? Found;

    /// <summary>Значок или null; ненайденный ищется в фоне.</summary>
    public static ImageSource? Get(string app)
    {
        if (Known.TryGetValue(app, out var icon)) return icon;
        if (Looking.TryAdd(app, 0)) Task.Run(() => Look(app, null));
        return null;
    }

    /// <summary>Программа только что скопировала: путь известен точно.</summary>
    internal static void Learn(string app, string? path)
    {
        if (Folder == null || path == null || Known.TryGetValue(app, out var icon) && icon != null || File.Exists(CachePath(app))) return;
        if (Looking.TryAdd(app, 0)) Task.Run(() => Look(app, path));
    }

    internal static void Put(string app, BitmapSource icon) => Known[app] = icon;

    static string CachePath(string app) => Path.Combine(Folder!, string.Concat(app.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : char.ToLowerInvariant(c))) + ".png");

    static void Look(string app, string? path)
    {
        BitmapSource? icon = null;
        try
        {
            icon = Cached(app);
            if (icon == null && (path ?? Locate(app)) is { } found && Native.ShellIcon(found, 256) is { } raw)
            {
                icon = Fit(raw);
                Store(app, icon);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            App.Trace($"icon {app}: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            Known[app] = icon;
            Looking.TryRemove(app, out _);
        }
        if (icon != null) Found?.Invoke(app);
    }

    static BitmapSource? Cached(string app)
    {
        if (Folder == null || !File.Exists(CachePath(app))) return null;
        var bitmap = new BitmapImage();
        using var stream = File.OpenRead(CachePath(app));
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    static void Store(string app, BitmapSource icon)
    {
        if (Folder == null) return;
        Directory.CreateDirectory(Folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(icon));
        using var file = File.Create(CachePath(app));
        encoder.Save(file);
    }

    static string? Locate(string app)
    {
        var processes = Process.GetProcessesByName(app);
        try
        {
            foreach (var process in processes.OrderByDescending(p => HasWindow(p)))
                if (Native.ProcessPath((uint)process.Id) is { } running) return running;
        }
        finally { foreach (var process in processes) process.Dispose(); }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{app}.exe");
            if (key?.GetValue(null) is string registered && File.Exists(registered.Trim('"'))) return registered.Trim('"');
        }
        foreach (var menu in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
        {
            string folder = Environment.GetFolderPath(menu);
            if (!Directory.Exists(folder)) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
            if (Directory.EnumerateFiles(folder, "*.lnk", options).FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f).Replace(" ", ""), app, StringComparison.OrdinalIgnoreCase)) is { } shortcut)
                return shortcut;
        }
        return null;
    }

    static bool HasWindow(Process process)
    {
        try { return process.MainWindowHandle != IntPtr.Zero; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; } // закрылась или не наша по правам
    }

    /// <summary>Без прозрачных полей (мелкий значок Windows кладёт в середину большого листа) и не больше Size.</summary>
    internal static BitmapSource Fit(BitmapSource raw)
    {
        int w = raw.PixelWidth, h = raw.PixelHeight;
        var pixels = new byte[w * h * 4];
        raw.CopyPixels(pixels, w * 4, 0);
        int left = w, top = h, right = -1, bottom = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (pixels[(y * w + x) * 4 + 3] > 8) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (right < 0) return raw;
        BitmapSource icon = new CroppedBitmap(raw, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        double scale = (double)Size / Math.Max(icon.PixelWidth, icon.PixelHeight);
        if (scale < 1) icon = new TransformedBitmap(icon, new ScaleTransform(scale, scale));
        icon = new CachedBitmap(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        icon.Freeze();
        return icon;
    }

    /// <summary>Значок, размытый один раз в картинку с полями под размытие: эффект на живом элементе считался бы в каждом кадре.</summary>
    internal static BitmapSource Soft(ImageSource icon)
    {
        var visual = new DrawingVisual { Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 9 } };
        using (var dc = visual.RenderOpen())
        {
            double scale = (Size - 24.0) / Math.Max(icon.Width, icon.Height), w = icon.Width * scale, h = icon.Height * scale;
            dc.DrawImage(icon, new Rect((Size - w) / 2, (Size - h) / 2, w, h));
        }
        var soft = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        soft.Render(visual);
        soft.Freeze();
        return soft;
    }

    /// <summary>chrome → Chrome; у имён со своими заглавными ничего не меняем.</summary>
    internal static string Title(string app) => app.Length > 0 && app.All(c => !char.IsUpper(c)) ? char.ToUpperInvariant(app[0]) + app[1..] : app;

    internal static void SelfTest(string directory, Action<bool, string> check)
    {
        var pixels = new byte[64 * 64 * 4];
        for (int y = 24; y < 40; y++)
            for (int x = 20; x < 44; x++) pixels[(y * 64 + x) * 4 + 3] = 255;
        var padded = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Pbgra32, null, pixels, 64 * 4);
        var fitted = Fit(padded);
        check(fitted.PixelWidth == 24 && fitted.PixelHeight == 16 && fitted.IsFrozen, "значки: прозрачные поля обрезаны");
        var soft = Soft(fitted);
        var blurred = new byte[Size * Size * 4];
        soft.CopyPixels(blurred, Size * 4, 0);
        check(soft is { IsFrozen: true, PixelWidth: Size } && blurred[(Size / 2 * Size + Size / 2) * 4 + 3] > 200 && blurred[(Size / 2 * Size + 10) * 4 + 3] is > 0 and < 128 && blurred[3] == 0,
            "значки: знак за записями размыт и не обрезан");
        check(Title("chrome") == "Chrome" && Title("ShareX") == "ShareX" && Title("") == "", "значки: имя программы с заглавной");
        string? was = Folder;
        try
        {
            Folder = Path.Combine(directory, "icons");
            using var found = new ManualResetEventSlim();
            void Done(string app) { if (app == "explorer") found.Set(); }
            Found += Done;
            Learn("explorer", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
            bool got = found.Wait(8000);
            Found -= Done;
            check(got && Get("explorer") is BitmapSource { PixelWidth: > 8 and <= Size } && File.Exists(CachePath("explorer")) && new FileInfo(CachePath("explorer")).Length < 60_000, "значки: значок программы найден и лежит маленьким PNG");
            check(Cached("explorer") is { PixelWidth: > 8 }, "значки: значок читается из кэша");
            check(Get("no-such-program-42") == null, "значки: неизвестная программа остаётся без значка");
        }
        finally { Folder = was; }
    }
}
