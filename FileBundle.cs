using System.IO;
using System.IO.Compression;

namespace ArkCopyBuffer;

/// <summary>Несколько скопированных файлов в одном зашифрованном объекте истории.</summary>
static class FileBundle
{
    public const long MaxBytes = 200L * 1024 * 1024; // верхняя граница; старые пакеты читаются после снижения лимита захвата
    public const int MaxFiles = 100;
    static readonly DateTimeOffset ZipTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Pack(IReadOnlyList<string> paths, long maxBytes = 100L * 1024 * 1024, int maxFiles = MaxFiles)
    {
        if (maxBytes is < 1 or > MaxBytes || maxFiles is < 2 or > MaxFiles || paths.Count < 2 || paths.Count > maxFiles)
            throw new InvalidDataException("Invalid file bundle limits");
        using var output = new MemoryStream();
        long total = 0;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < paths.Count; i++)
            {
                using var input = File.OpenRead(paths[i]);
                var entry = zip.CreateEntry($"{i:D3}/{Path.GetFileName(paths[i])}", CompressionLevel.NoCompression);
                entry.LastWriteTime = ZipTime; // одинаковый набор файлов даёт одинаковый хэш
                using var target = entry.Open();
                CopyLimited(input, target, ref total, maxBytes);
            }
        return output.ToArray();
    }

    public static string[] Extract(byte[] bytes, string dir, int expectedCount)
    {
        if (expectedCount is < 2 or > MaxFiles || bytes.Length > MaxBytes + 1024 * 1024)
            throw new InvalidDataException("Invalid file bundle");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count != expectedCount) throw new InvalidDataException("File count changed");
        dir = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        var paths = new string[expectedCount];
        long total = 0;
        for (int i = 0; i < expectedCount; i++)
        {
            var entry = zip.Entries[i];
            var name = entry.Name;
            if (entry.FullName != $"{i:D3}/{name}" || name is "" or "." or ".." ||
                Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                entry.Length > MaxBytes - total)
                throw new InvalidDataException("Invalid file bundle entry");
            var folder = Path.Combine(dir, $"{i:D3}");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Length)
            {
                using var input = entry.Open();
                using var target = File.Create(path);
                long before = total;
                CopyLimited(input, target, ref total, MaxBytes);
                if (total - before != entry.Length) throw new InvalidDataException("Incomplete file bundle entry");
            }
            else total += entry.Length;
            paths[i] = path;
        }
        return paths;
    }

    static void CopyLimited(Stream input, Stream output, ref long total, long maxBytes)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            if (read > maxBytes - total) throw new InvalidDataException("File bundle exceeds size limit");
            output.Write(buffer, 0, read);
            total += read;
        }
    }
}
