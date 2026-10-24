using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using static ArkCopyBuffer.Native;

namespace ArkCopyBuffer;

static class Paster
{
    /// <summary>Расшифрованные GIF/видео/аудио/текстовые файлы для вставки файлом.</summary>
    static readonly string GifTempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ArkCopyBuffer");

    /// <summary>Id записи в нашем же DataObject: ClipboardWatcher узнаёт свою вставку и не разбирает картинку/файл заново.</summary>
    public const string OwnFormat = "ArkCopyBuffer.Item";

    /// <summary>Расшифрованные копии старше заданного срока. Зовётся при запуске и раз в час.</summary>
    public static void CleanGifTemp(int days = 1)
    {
        if (!System.IO.Directory.Exists(GifTempDir)) return;
        foreach (var sub in System.IO.Directory.GetDirectories(GifTempDir))
            if (DateTime.UtcNow - System.IO.Directory.GetLastWriteTimeUtc(sub) > TimeSpan.FromDays(days))
                try { System.IO.Directory.Delete(sub, recursive: true); } catch { }
    }

    /// <summary>Кладёт запись в буфер. Расшифровка и подготовка файлов — вне UI-потока.</summary>
    public static async Task SetClipboardAsync(ClipItem item, bool plainText, string imagesDir, Func<bool>? isCurrent = null)
    {
        (string[] paths, byte[]? gif)? files = null;
        if (item.IsFolder && !plainText)
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(item.Text)) throw new DirectoryNotFoundException(UiText.T("Исходная папка недоступна."));
            });
            files = ([item.Text], null);
        }
        else if (item.IsShortLived) files = await Task.Run(() => PrepareFiles(item, imagesDir));
        var data = new DataObject();
        if (files is { } ready)
        {
            data = FileData(ready.paths, ready.gif);
        }
        else if (item.ImageHash != null)
        {
            // PNG — для браузеров, мессенджеров, графических редакторов (с прозрачностью); DIB — для всех остальных.
            var (bitmap, png) = await Task.Run(() =>
            {
                var bytes = ImageFiles.ReadFull(imagesDir, item.ImageHash);
                return (ImageFiles.Decode(bytes), bytes);
            });
            data = ImageClipboard(bitmap, png);
        }
        else data.SetData(DataFormats.UnicodeText, item.Text);
        if (!plainText && !item.IsSensitive && item.ImageHash == null)
        {
            if (item.Rtf != null) data.SetData(DataFormats.Rtf, item.Rtf);
            if (item.Html != null) data.SetData(DataFormats.Html, item.Html);
        }
        if (item.IsSensitive) MarkSensitive(data);
        WriteItemClipboard(data, item.Id, isCurrent);
    }

    internal static DataObject ImageClipboard(BitmapSource bitmap, byte[] png)
    {
        var data = new DataObject();
        data.SetImage(bitmap);
        data.SetData("PNG", new MemoryStream(png));
        return data;
    }

    public static async Task SetImageSeriesClipboardAsync(IReadOnlyList<ClipItem> items, string imagesDir, Func<bool> isCurrent)
    {
        if (!isCurrent()) throw new OperationCanceledException();
        var paths = await Task.Run(() => PrepareImageSeries(items, imagesDir));
        try { WriteItemClipboard(FileData(paths), items[0].Id, isCurrent); }
        catch { try { Directory.Delete(Path.GetDirectoryName(paths[0])!, recursive: true); } catch { } throw; }
    }

    internal static string[] PrepareImageSeries(IReadOnlyList<ClipItem> items, string imagesDir)
    {
        if (items.Count == 0 || items.Any(i => !i.IsVisualMedia) || items.Select(i => i.Id).Distinct().Count() != items.Count)
            throw new InvalidDataException(UiText.T("Выберите разные картинки или видео для вставки."));
        var dir = Path.Combine(GifTempDir, "series-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(dir)!);
            var paths = new string[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                string extension = items[i].IsImage ? items[i].IsGif ? ".gif" : ".png" : Path.GetExtension(items[i].FileName ?? "");
                if (extension.Length is < 2 or > 16 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
                    throw new InvalidDataException(UiText.T("У видео отсутствует или повреждено расширение файла."));
                var bytes = ImageFiles.ReadFull(imagesDir, items[i].ImageHash!);
                try
                {
                    if (drive.AvailableFreeSpace < bytes.LongLength + 64L * 1024 * 1024)
                        throw new IOException(UiText.T("На диске временных файлов недостаточно места для картинок и видео."));
                    paths[i] = Path.Combine(dir, $"{i + 1:D4}{extension}");
                    File.WriteAllBytes(paths[i], bytes);
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
            return paths;
        }
        catch { try { Directory.Delete(dir, recursive: true); } catch { } throw; }
    }

    internal static DataObject FileData(string[] paths, byte[]? gif = null)
    {
        var data = new DataObject();
        var drop = new System.Collections.Specialized.StringCollection();
        drop.AddRange(paths);
        data.SetFileDropList(drop);
        if (gif != null) data.SetData("GIF", new MemoryStream(gif));
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // копировать, не перемещать
        return data;
    }

    static void WriteItemClipboard(DataObject data, Guid id, Func<bool>? isCurrent)
    {
        data.SetData(OwnFormat, new MemoryStream(id.ToByteArray()));
        if (isCurrent != null && !isCurrent()) throw new OperationCanceledException();
        WriteClipboard(data);
    }

    public static void SetArchiveClipboard(ClipItem item, string archive, Func<bool> isCurrent) =>
        WriteItemClipboard(FileData([archive]), item.Id, isCurrent);

    public static Task<string> CreateFolderArchiveAsync(string source, CancellationToken cancel, Action<string>? progress = null) => Task.Run(() =>
    {
        var dir = Path.Combine(GifTempDir, "archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (name.Length == 0) name = UiText.T("Папка");
            if (name.Length > 240) name = name[..240];
            var zip = FolderArchive.Create(source, Path.Combine(dir, name + ".zip"), cancel, progress);
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
            return zip;
        }
        catch { try { Directory.Delete(dir, recursive: true); } catch { } throw; }
    }, cancel);

    internal static (string[] paths, byte[]? gif) PrepareFiles(ClipItem item, string imagesDir)
    {
        var bytes = ImageFiles.ReadFull(imagesDir, item.ImageHash!);
        var dir = System.IO.Path.Combine(GifTempDir, item.ImageHash![..16]);
        System.IO.Directory.CreateDirectory(dir);
        string[] paths;
        if (item.MediaKind == "files") paths = FileBundle.Extract(bytes, dir, item.FileCount);
        else
        {
            var name = System.IO.Path.GetFileName(item.FileName ?? "animation.gif");
            var path = System.IO.Path.Combine(dir, name);
            // Не перезаписываем файл, который может держать приложение предыдущей вставки.
            if (new System.IO.FileInfo(path) is not { Exists: true } existing || existing.Length != bytes.Length)
                System.IO.File.WriteAllBytes(path, bytes);
            paths = [path];
        }
        System.IO.Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
        return (paths, item.IsGif ? bytes : null);
    }

    internal static void MarkSensitive(DataObject data)
    {
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1 }));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
    }

    internal static DataObject TextClipboard(string text, bool excludeFromHistory = false, bool sensitive = false)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        if (sensitive) MarkSensitive(data);
        if (excludeFromHistory)
        {
            data.SetData("Clipboard Viewer Ignore", new MemoryStream(new byte[] { 1 }));
            data.SetData(OwnFormat, new MemoryStream(Guid.Empty.ToByteArray()));
        }
        return data;
    }

    public static void CopyText(string text, bool excludeFromHistory = false, bool sensitive = false) => WriteClipboard(TextClipboard(text, excludeFromHistory, sensitive));

    static void WriteClipboard(DataObject data)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); return; }
            catch (ExternalException) when (attempt < 5) { Thread.Sleep(40); }
        }
    }

    /// <returns>false, если нужное окно недоступно — запись осталась в буфере.</returns>
    public static Task<bool> PasteInto(IntPtr target, Func<bool>? isCurrent = null) =>
        SendTo(target, () => SendKeys((VK_CONTROL, true), (VK_V, true), (VK_V, false), (VK_CONTROL, false)), isCurrent);

    /// <summary>Печатает символ(ы) в окно, минуя буфер обмена — история не засоряется.</summary>
    public static Task<bool> TypeInto(IntPtr target, string text) => SendTo(target, () => SendText(text));

    static async Task<bool> SendTo(IntPtr target, Func<bool> send, Func<bool>? isCurrent = null)
    {
        if (isCurrent != null && !isCurrent()) return false;
        if (target == IntPtr.Zero || !IsWindow(target)) return false;
        if (IsInputBlocked(target)) return false;

        SetForegroundWindow(target);
        for (int i = 0; i < 20 && GetForegroundWindow() != target; i++) await Task.Delay(10);
        if (GetForegroundWindow() != target) return false;
        // WinUI/UWP (новый Блокнот и т.п.) возвращают фокус в поле ввода асинхронно — даём кадр-другой.
        await Task.Delay(50);

        // Shift от Shift+Enter ещё может быть зажат — тогда уйдёт Ctrl+Shift+V. Ждём отпускания, потом отпускаем сами.
        int[] mods = [VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN];
        for (int i = 0; i < 30 && mods.Any(IsDown); i++) await Task.Delay(10);
        if (GetForegroundWindow() != target) return false;
        var held = mods.Where(IsDown).Select(vk => (vk, false)).ToArray();
        if (isCurrent != null && !isCurrent()) return false;
        if (held.Length > 0 && !SendKeys([(VK_MASK, true), (VK_MASK, false), .. held])) return false; // маска — чтобы отпускание Win не открыло Start

        return send();
    }
}
