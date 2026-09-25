using System.Runtime.InteropServices;
using System.Windows;
using static ArkCopyBuffer.Native;

namespace ArkCopyBuffer;

static class Paster
{
    /// <summary>Расшифрованные GIF/видео/аудио/текстовые файлы для вставки файлом. Чистится при запуске (старше суток).</summary>
    static readonly string GifTempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ArkCopyBuffer");

    public static void CleanGifTemp()
    {
        if (!System.IO.Directory.Exists(GifTempDir)) return;
        foreach (var sub in System.IO.Directory.GetDirectories(GifTempDir))
            if (DateTime.UtcNow - System.IO.Directory.GetLastWriteTimeUtc(sub) > TimeSpan.FromDays(1))
                try { System.IO.Directory.Delete(sub, recursive: true); } catch { }
    }

    /// <summary>Кладёт запись в буфер. copy:true — данные переживут выход программы.</summary>
    public static void SetClipboard(ClipItem item, bool plainText, string imagesDir)
    {
        var data = new DataObject();
        if (item.IsShortLived)
        {
            // GIF, видео, аудио и текстовые файлы вставляются исходным файлом.
            // Статичный кадр GIF не кладём: иначе часть приложений предпочтёт его и отправит картинку без анимации.
            var bytes = ImageFiles.ReadFull(imagesDir, item.ImageHash!);
            var dir = System.IO.Path.Combine(GifTempDir, item.ImageHash![..16]); // своя папка — чтобы сохранить исходное имя файла
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, item.FileName ?? "animation.gif");
            System.IO.File.WriteAllBytes(path, bytes);
            data.SetFileDropList([path]);
            if (item.IsGif) data.SetData("GIF", new System.IO.MemoryStream(bytes));
            data.SetData("Preferred DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(1))); // копировать, не перемещать
        }
        else if (item.ImageHash != null)
        {
            // PNG — для браузеров, мессенджеров, графических редакторов (с прозрачностью); DIB — для всех остальных.
            var png = ImageFiles.ReadFull(imagesDir, item.ImageHash);
            data.SetImage(ImageFiles.Decode(png));
            data.SetData("PNG", new System.IO.MemoryStream(png));
        }
        else data.SetData(DataFormats.UnicodeText, item.Text);
        if (!plainText && item.ImageHash == null)
        {
            if (item.Rtf != null) data.SetData(DataFormats.Rtf, item.Rtf);
            if (item.Html != null) data.SetData(DataFormats.Html, item.Html);
        }
        WriteClipboard(data);
    }

    public static void CopyText(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        WriteClipboard(data);
    }

    static void WriteClipboard(DataObject data)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); return; }
            catch (ExternalException) when (attempt < 5) { Thread.Sleep(40); }
        }
    }

    /// <returns>false, если нужное окно недоступно — запись осталась в буфере.</returns>
    public static Task<bool> PasteInto(IntPtr target) =>
        SendTo(target, () => SendKeys((VK_CONTROL, true), (VK_V, true), (VK_V, false), (VK_CONTROL, false)));

    /// <summary>Печатает символ(ы) в окно, минуя буфер обмена — история не засоряется.</summary>
    public static Task<bool> TypeInto(IntPtr target, string text) => SendTo(target, () => SendText(text));

    static async Task<bool> SendTo(IntPtr target, Func<bool> send)
    {
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
        if (held.Length > 0 && !SendKeys([(VK_MASK, true), (VK_MASK, false), .. held])) return false; // маска — чтобы отпускание Win не открыло Start

        return send();
    }
}
