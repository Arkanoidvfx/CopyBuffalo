using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace ArkCopyBuffer;

/// <summary>ZIP из текущего содержимого папки. Потоковая запись вне UI; незавершённый архив удаляется.</summary>
static class FolderArchive
{
    public static string Create(string source, string destination, CancellationToken cancel, Action<string>? progress = null)
    {
        var root = new DirectoryInfo(source);
        if (!root.Exists) throw new DirectoryNotFoundException("Исходная папка перемещена, удалена или недоступна.");
        RejectLink(root);
        destination = Path.GetFullPath(destination);
        var prefix = Path.EndsInDirectorySeparator(root.FullName) ? root.FullName : root.FullName + Path.DirectorySeparatorChar;
        if (destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Нельзя создать временный ZIP внутри самой архивируемой папки.");
        cancel.ThrowIfCancellationRequested();
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        void CheckSpace()
        {
            if (drive.AvailableFreeSpace < 64L * 1024 * 1024)
                throw new IOException("На диске временных файлов недостаточно места для ZIP.");
        }
        CheckSpace();
        var partial = destination + ".partial";
        bool created = false;
        long bytes = 0, nextSpaceCheck = 0;
        int files = 0;
        var clock = Stopwatch.StartNew();
        void Report(bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds < 150) return;
            progress?.Invoke($"Файлов: {files:N0} · {bytes / 1048576.0:0.#}\u00A0МБ");
            clock.Restart();
        }
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var baseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root.FullName));
                if (baseName.Length == 0) baseName = "Папка";
                zip.CreateEntry(baseName + "/");
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
                var buffer = new byte[81920];
                foreach (var item in root.EnumerateFileSystemInfos("*", options))
                {
                    cancel.ThrowIfCancellationRequested();
                    RejectLink(item); // не уходим по junction/symlink за пределы папки и не зацикливаемся
                    var relative = Path.GetRelativePath(root.FullName, item.FullName).Replace('\\', '/');
                    var name = baseName + "/" + relative;
                    if (item is DirectoryInfo) { zip.CreateEntry(name + "/"); Report(); continue; }
                    using var input = new FileStream(item.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                        buffer.Length, FileOptions.SequentialScan);
                    var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                    if (item.LastWriteTime.Year is >= 1980 and <= 2107) entry.LastWriteTime = item.LastWriteTime;
                    using var target = entry.Open();
                    int read;
                    while ((read = input.Read(buffer)) != 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (bytes >= nextSpaceCheck) { CheckSpace(); nextSpaceCheck = bytes + 16L * 1024 * 1024; }
                        target.Write(buffer, 0, read);
                        bytes += read;
                        Report();
                    }
                    files++;
                    Report();
                }
                cancel.ThrowIfCancellationRequested();
            }
            Report(true);
            cancel.ThrowIfCancellationRequested();
            File.Move(partial, destination); // готовый ZIP появляется только после успешного завершения
            return destination;
        }
        catch
        {
            if (created) try { File.Delete(partial); } catch { }
            throw;
        }
    }

    static void RejectLink(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"В папке есть ссылка или облачный файл: {item.Name}. Подготовьте обычную локальную копию.");
    }
}
