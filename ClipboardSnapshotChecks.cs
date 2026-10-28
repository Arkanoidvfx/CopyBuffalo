using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

static class ClipboardSnapshotChecks
{
    sealed class DelayedData(string missing) : IDataObject
    {
        readonly DataObject data = new();
        public bool AllowAnsiFallback { get; init; }
        public object? GetData(string format, bool autoConvert) => format == missing
            ? AllowAnsiFallback && autoConvert ? "??????" : null
            : data.GetData(format, autoConvert);
        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => GetData(format.FullName!);
        public bool GetDataPresent(string format, bool autoConvert) => data.GetDataPresent(format, autoConvert);
        public bool GetDataPresent(string format) => data.GetDataPresent(format);
        public bool GetDataPresent(Type format) => data.GetDataPresent(format);
        public string[] GetFormats(bool autoConvert) => data.GetFormats(autoConvert);
        public string[] GetFormats() => data.GetFormats();
        public void SetData(string format, object value, bool autoConvert) => data.SetData(format, value, autoConvert);
        public void SetData(string format, object value) => data.SetData(format, value);
        public void SetData(Type format, object value) => data.SetData(format, value);
        public void SetData(object value) => data.SetData(value);
    }

    internal static void Run(Action<bool, string> check)
    {
        const string text = "Привет, ёжик — 中文 😀";
        var plain = Paster.TextClipboard(text);
        check(ClipboardSnapshot.Materialize(plain, new()).GetData(DataFormats.UnicodeText) as string == text,
            "snapshot: Unicode preserves Cyrillic, CJK and emoji");
        var delayed = new DelayedData(DataFormats.UnicodeText) { AllowAnsiFallback = true };
        delayed.SetData(DataFormats.UnicodeText, text, false);
        delayed.SetData(DataFormats.Text, "??????", false);
        bool retry = false;
        try { ClipboardSnapshot.Materialize(delayed, new()); }
        catch (ExternalException) { retry = true; }
        check(retry, "snapshot: unavailable native Unicode retries instead of saving ANSI question marks");

        var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 0, 0, 255, 0, 0 }, 8);
        foreach (var missing in new[] { "GIF", "PNG", "image/png", DataFormats.Dib })
        {
            var source = new DelayedData(missing);
            source.SetData(missing, new MemoryStream(new byte[40]), false);
            source.SetData(DataFormats.Bitmap, bitmap, false);
            var captured = ClipboardSnapshot.Materialize(source, new());
            var image = ClipboardWatcher.ReadImage(captured);
            check(image is { PixelWidth: 2, PixelHeight: 1 }, $"snapshot: unavailable {missing} falls back to bitmap");
            byte[] pixels = new byte[8];
            new FormatConvertedBitmap(image!, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 8, 0);
            check(pixels[2] == 255 && pixels[5] == 255 && pixels[3] == 255 && pixels[7] == 255,
                $"snapshot: bitmap fallback keeps colors and ignores invalid HBITMAP alpha ({missing})");
        }
        var pendingImage = new DelayedData("PNG");
        pendingImage.SetData("PNG", new MemoryStream(new byte[40]), false);
        retry = false;
        try { ClipboardSnapshot.Materialize(pendingImage, new()); }
        catch (ExternalException) { retry = true; }
        check(retry, "snapshot: unavailable image with no fallback remains retryable");
    }
}
