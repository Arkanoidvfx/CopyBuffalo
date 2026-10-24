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
        var formats = new HashSet<string>(source.GetFormats());
        if (formats.Count == 0 && CountClipboardFormats() > 0) throw new ExternalException("Clipboard formats are not ready");
        var data = new DataObject();
        foreach (var format in new[] { "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore" })
            if (formats.Contains(format)) { data.SetData(format, true); return (sequence, owner, data); }
        CopyStream("CanIncludeInClipboardHistory", 4, prefix: true);
        if (data.GetData("CanIncludeInClipboardHistory") is MemoryStream { Length: >= 4 } privacy
            && BitConverter.ToInt32(privacy.ToArray(), 0) == 0) return (sequence, owner, data);
        CopyStream(Paster.OwnFormat, 16, prefix: true);
        if (data.GetData(Paster.OwnFormat) is MemoryStream { Length: >= 16 }) return (sequence, owner, data);

        if (formats.Contains(DataFormats.FileDrop) && ReadData(DataFormats.FileDrop) is string[] paths
            && paths.Length <= limits.FileCount) data.SetData(DataFormats.FileDrop, paths.ToArray(), false);
        if (formats.Contains(DataFormats.UnicodeText) && ReadData(DataFormats.UnicodeText) is string text
            && !string.IsNullOrWhiteSpace(text) && text.Length <= limits.MaxChars)
        {
            data.SetData(DataFormats.UnicodeText, text, false);
            foreach (var format in new[] { DataFormats.Rtf, DataFormats.Html })
            {
                try
                {
                    if (formats.Contains(format) && ReadData(format) is string rich && rich.Length <= limits.MaxChars)
                        data.SetData(format, rich, false);
                }
                catch (ExternalException) { } // Optional formatting must not discard already captured text.
            }
            return (sequence, owner, data);
        }
        CopyStream("GIF", limits.MaxFileBytes);
        if (data.GetData("GIF") is MemoryStream { Length: > 0 }) return (sequence, owner, data);
        foreach (var format in new[] { "PNG", "image/png", DataFormats.Dib })
        {
            if (!formats.Contains(format) || ReadData(format) is not MemoryStream stream) continue;
            if (format == DataFormats.Dib ? !ClipboardWatcher.FitsDib(stream, limits) : !ClipboardWatcher.FitsPng(stream, limits)) continue;
            data.SetData(format, new MemoryStream(stream.ToArray(), writable: false), false);
            break;
        }
        return (sequence, owner, data);

        object ReadData(string format) => source.GetData(format) ?? throw new ExternalException($"Clipboard data is not ready: {format}");

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
