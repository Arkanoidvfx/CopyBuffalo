using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace ArkCopyBuffer;

static class ClipboardSnapshot
{
    [DllImport("ole32.dll")]
    static extern int OleGetClipboard(out System.Runtime.InteropServices.ComTypes.IDataObject data);
    [DllImport("user32.dll")] static extern int CountClipboardFormats();

    internal static (uint sequence, IntPtr owner, IDataObject data) Read(StorageSettings limits)
    {
        System.Windows.Forms.Application.OleRequired();
        // WPF GetDataObject retries with Thread.Sleep(100), blocking notifications for up to a second.
        // Retry asynchronously in the watcher instead; never hold OpenClipboard across OLE rendering.
        Marshal.ThrowExceptionForHR(OleGetClipboard(out var ole));
        var sequence = Native.GetClipboardSequenceNumber();
        var owner = Native.GetClipboardOwner();
        var source = new DataObject(ole);
        if (source.GetFormats().Length == 0 && CountClipboardFormats() > 0) throw new ExternalException("Clipboard formats are not ready");
        return (sequence, owner, Materialize(source, limits));
    }

    internal static IDataObject Materialize(IDataObject source, StorageSettings limits)
    {
        var formats = new HashSet<string>(source.GetFormats());
        var data = new DataObject();
        foreach (var format in new[] { "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore", "org.nspasteboard.ConcealedType", "application/x-nspasteboard-concealed-type" })
            if (formats.Contains(format)) data.SetData(format, true);
        CopyStream("CanIncludeInClipboardHistory", 4, prefix: true);
        CopyStream(Paster.OwnFormat, 16, prefix: true);
        if (data.GetData(Paster.OwnFormat) is MemoryStream { Length: >= 16 }) return data;
        foreach (var format in new[] { "x-kde-passwordManagerHint", "application/x-kde-passwordManagerHint" }) CopyStream(format, 32);
        bool sensitive = ClipboardWatcher.Sensitive(data);

        if (!sensitive && formats.Contains(DataFormats.FileDrop) && ReadData(DataFormats.FileDrop) is string[] paths
            && paths.Length <= limits.FileCount) data.SetData(DataFormats.FileDrop, paths.ToArray(), false);
        if (formats.Contains(DataFormats.UnicodeText) && ReadData(DataFormats.UnicodeText) is string text
            && text.Length > 0 && text.Length <= limits.MaxChars && (sensitive || !string.IsNullOrWhiteSpace(text)))
        {
            data.SetData(DataFormats.UnicodeText, text, false);
            if (sensitive) return data;
            foreach (var format in new[] { DataFormats.Rtf, DataFormats.Html })
            {
                try
                {
                    if (formats.Contains(format) && ReadData(format) is string rich && rich.Length <= limits.MaxChars)
                        data.SetData(format, rich, false);
                }
                catch (ExternalException) { } // Optional formatting must not discard already captured text.
            }
            return data;
        }
        if (sensitive) return data;
        ExternalException? imageError = null;
        try { CopyStream("GIF", limits.MaxFileBytes); }
        catch (ExternalException ex) { imageError = ex; }
        if (data.GetData("GIF") is MemoryStream { Length: > 0 }) return data;
        foreach (var format in new[] { "PNG", "image/png", DataFormats.Dib })
        {
            if (!formats.Contains(format) || ReadImageData(format) is not MemoryStream stream) continue;
            if (format == DataFormats.Dib ? !ClipboardWatcher.FitsDib(stream, limits) : !ClipboardWatcher.FitsPng(stream, limits)) continue;
            data.SetData(format, new MemoryStream(stream.ToArray(), writable: false), false);
            return data;
        }
        // ShareX и другие WinForms-приложения отдают через OLE только CF_BITMAP: синтезированного CF_DIB там нет.
        if (formats.Contains(DataFormats.Bitmap) && ReadImageData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bitmap
            && (long)bitmap.PixelWidth * bitmap.PixelHeight <= limits.MaxImagePixels)
        {
            var encoder = new System.Windows.Media.Imaging.BmpBitmapEncoder(); // альфа HBITMAP недостоверна — Bgr32
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
                new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgr32, null, 0)));
            using var bmp = new MemoryStream();
            encoder.Save(bmp);
            data.SetData(DataFormats.Dib, new MemoryStream(bmp.ToArray()[14..], writable: false), false); // BMP без заголовка файла = CF_DIB
            return data;
        }
        if (imageError != null) throw imageError;
        return data;

        // Never replace temporarily unavailable Unicode with a lossy ANSI fallback.
        object ReadData(string format) => source.GetData(format, !source.GetDataPresent(format, false))
            ?? throw new ExternalException($"Clipboard data is not ready: {format}");

        object? ReadImageData(string format)
        {
            try { return ReadData(format); }
            catch (ExternalException ex) { imageError = ex; return null; }
        }

        void CopyStream(string format, long maxBytes, bool prefix = false)
        {
            if (!formats.Contains(format) || ReadData(format) is not MemoryStream stream || stream.Length == 0
                || (!prefix && stream.Length > maxBytes)) return;
            var bytes = stream.ToArray();
            if (prefix && bytes.LongLength > maxBytes) bytes = bytes[..(int)maxBytes];
            data.SetData(format, new MemoryStream(bytes, writable: false), false);
        }
    }
}
