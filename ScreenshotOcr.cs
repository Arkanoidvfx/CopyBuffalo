using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ArkCopyBuffer;

sealed class ScreenshotOcr : IDisposable
{
    internal const int MaxInputBytes = 128 * 1024 * 1024;
    readonly SemaphoreSlim _gate = new(1);
    readonly CancellationTokenSource _shutdown = new();
    internal sealed class Failure(string message, Exception? inner = null) : Exception(message, inner);

    internal static Task<byte[]> ReadImageAsync(string imagesDir, string hash, CancellationToken cancel) => Task.Run(() =>
    {
        string dir = Path.GetFullPath(imagesDir);
        string path = Path.GetFullPath(Path.Combine(dir, hash + ".dat"));
        if (!string.Equals(Path.GetDirectoryName(path), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new Failure(UiText.T("Некорректная ссылка на изображение."));
        if (new FileInfo(path).Length > MaxInputBytes + 4096L) throw new Failure(UiText.T("Для OCR нужен файл изображения не больше 128 МБ."));
        cancel.ThrowIfCancellationRequested();
        return ImageFiles.ReadFull(dir, hash);
    }, cancel);

    internal async Task<OcrText.Result> RecognizeAsync(byte[] image, long maxPixels, CancellationToken cancel)
    {
        if (image.Length == 0 || image.Length > MaxInputBytes) throw new Failure(UiText.T("Для OCR нужен файл изображения не больше 128 МБ."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel, _shutdown.Token);
        await _gate.WaitAsync(timeout.Token);
        try
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            string root = Path.Combine(AppContext.BaseDirectory, "ocr");
            string exe = Path.Combine(root, "copybuffalo-ocr.exe");
            if (!File.Exists(exe) || new[] { "det", "rec" }.Any(dir =>
                !File.Exists(Path.Combine(root, dir, "inference.onnx")) || !File.Exists(Path.Combine(root, dir, "inference.yml"))))
                throw new Failure(UiText.T("Не найден комплект OCR. Установите свежую сборку CopyBuffalo с папкой ocr."));
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = root
            };
            start.ArgumentList.Add(Path.Combine(root, "det"));
            start.ArgumentList.Add(Path.Combine(root, "rec"));
            using var job = new SafeFileHandle(Native.CreateKillOnCloseJob(), ownsHandle: true);
            using var process = new Process { StartInfo = start };
            using var registration = timeout.Token.Register(() => Kill(process));
            try
            {
                process.Start();
                Native.AssignToJob(job.DangerousGetHandle(), process);
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                timeout.Token.ThrowIfCancellationRequested();
                var output = ReadLimitedAsync(process.StandardOutput, 4_000_000, timeout.Token);
                var error = ReadLimitedAsync(process.StandardError, 16_000, timeout.Token);
                async Task Send()
                {
                    await process.StandardInput.BaseStream.WriteAsync(image, timeout.Token);
                    process.StandardInput.Close();
                }
                await Task.WhenAll(Send(), process.WaitForExitAsync(timeout.Token), output, error);
                timeout.Token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new Failure(UiText.T("OCR не смог обработать изображение. Попробуйте выделить меньшую область или другой снимок."));
                string json = await output;
                return await Task.Run(() => OcrText.Parse(json, maxPixels), timeout.Token);
            }
            finally { Kill(process); }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        { throw new Failure(UiText.T("OCR не завершился за минуту. Попробуйте меньшую область изображения.")); }
        catch (Failure) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        { throw new Failure(UiText.T("Не удалось распознать текст. Исходное изображение сохранено."), ex); }
        finally { _gate.Release(); }
    }

    internal static async Task<string> ReadLimitedAsync(TextReader reader, int limit, CancellationToken cancel)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancel)) > 0)
        {
            if (count > limit - text.Length) throw new InvalidDataException("OCR output size limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose() => _shutdown.Cancel();
}
