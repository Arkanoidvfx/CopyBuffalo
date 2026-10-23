using Microsoft.Win32;

namespace ArkCopyBuffer;

/// <summary>Лимиты хранения. Значения из реестра всегда ограничены диапазонами интерфейса.</summary>
sealed record StorageSettings(
    int HistoryCount = 100,
    int HistoryDays = 7,
    int MediaDays = 1,
    int TextThousands = 1000,
    int ImageMegapixels = 40,
    int ThumbnailPixels = 320,
    int FileMegabytes = 100,
    int FileCount = 100,
    int TempDays = 1)
{
    public int MaxChars => TextThousands * 1000;
    public long MaxImagePixels => (long)ImageMegapixels * 1_000_000;
    public long MaxFileBytes => (long)FileMegabytes * 1024 * 1024;

    public static StorageSettings Read(RegistryKey? key)
    {
        int Value(string name, int fallback, int min, int max) =>
            key?.GetValue(name) is int n ? Math.Clamp(n, min, max) : fallback;
        return new(
            Value("HistoryCount", 100, 20, 1000),
            Value("HistoryDays", 7, 1, 90),
            Value("MediaDays", 1, 1, 30),
            Value("TextThousands", 1000, 10, 2000),
            Value("ImageMegapixels", 40, 1, 80),
            Value("ThumbnailPixels", 320, 160, 640),
            Value("FileMegabytes", 100, 10, 200),
            Value("FileCount", 100, 2, 100),
            Value("TempDays", 1, 1, 7));
    }

    public void Write(RegistryKey key)
    {
        key.SetValue("HistoryCount", HistoryCount, RegistryValueKind.DWord);
        key.SetValue("HistoryDays", HistoryDays, RegistryValueKind.DWord);
        key.SetValue("MediaDays", MediaDays, RegistryValueKind.DWord);
        key.SetValue("TextThousands", TextThousands, RegistryValueKind.DWord);
        key.SetValue("ImageMegapixels", ImageMegapixels, RegistryValueKind.DWord);
        key.SetValue("ThumbnailPixels", ThumbnailPixels, RegistryValueKind.DWord);
        key.SetValue("FileMegabytes", FileMegabytes, RegistryValueKind.DWord);
        key.SetValue("FileCount", FileCount, RegistryValueKind.DWord);
        key.SetValue("TempDays", TempDays, RegistryValueKind.DWord);
    }
}
