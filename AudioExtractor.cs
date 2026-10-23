using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArkCopyBuffer;

static class AudioExtractor
{
    internal sealed record Track(int Index, string Codec, string Label);
    internal sealed record Result(string Hash, string FileName, long Size);

    internal static async Task<Track[]> ProbeAsync(string path, CancellationToken cancel)
    {
        var json = await RunAsync("ffprobe", ["-v", "error", "-select_streams", "a", "-show_entries",
            "stream=index,codec_name,channels:stream_tags=language,title", "-of", "json", path], cancel);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("streams").EnumerateArray().Select((s, n) =>
        {
            string codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "unknown" : "unknown";
            var parts = new List<string> { $"Дорожка {n + 1}" };
            if (s.TryGetProperty("tags", out var tags))
                foreach (var key in new[] { "language", "title" })
                    if (tags.TryGetProperty(key, out var tag) && !string.IsNullOrWhiteSpace(tag.GetString())) parts.Add(tag.GetString()!);
            parts.Add(codec.ToUpperInvariant());
            if (s.TryGetProperty("channels", out var channels)) parts.Add($"{channels.GetInt32()} кан.");
            return new Track(s.GetProperty("index").GetInt32(), codec, string.Join(" · ", parts));
        }).ToArray();
    }

    internal static string Extension(string codec) => codec switch
    {
        "aac" or "alac" => ".m4a", "mp3" => ".mp3", "opus" or "vorbis" => ".ogg",
        "flac" => ".flac", "ac3" => ".ac3", "eac3" => ".eac3", "dts" => ".dts",
        _ when codec.StartsWith("pcm_", StringComparison.Ordinal) => ".wav",
        _ => ".mka"
    };

    internal static async Task ExtractFileAsync(string source, string output, Track track, CancellationToken cancel)
    {
        if (track.Index < 0) throw new ArgumentOutOfRangeException(nameof(track));
        if (File.Exists(output)) throw new IOException("Выходной файл уже существует.");
        try
        {
            await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", source,
                "-map", $"0:{track.Index}", "-vn", "-sn", "-dn", "-c:a", "copy", output], cancel);
        }
        catch { try { File.Delete(output); } catch { } throw; }
    }

    internal static async Task<Result> ExtractAsync(ClipItem item, string imagesDir, Track track, long maxBytes, CancellationToken cancel)
    {
        var source = await Task.Run(() => Paster.PrepareFiles(item, imagesDir).paths[0], cancel);
        var output = Path.Combine(Path.GetDirectoryName(source)!, Guid.NewGuid().ToString("N") + Extension(track.Codec));
        try
        {
            await ExtractFileAsync(source, output, track, cancel);
            return await Task.Run(() =>
            {
                cancel.ThrowIfCancellationRequested();
                var size = new FileInfo(output).Length;
                if (size == 0) throw new InvalidOperationException("Звуковая дорожка пуста.");
                if (size > maxBytes) throw new InvalidOperationException("Аудиофайл превышает лимит файлов в настройках хранения.");
                var bytes = File.ReadAllBytes(output);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                ImageFiles.WriteMedia(imagesDir, hash, bytes, null);
                var name = Path.GetFileNameWithoutExtension(source);
                if (name.Length > 180) name = name[..180];
                return new Result(hash, $"{name} — дорожка {track.Index}{Extension(track.Codec)}", size);
            }, cancel);
        }
        finally { try { File.Delete(output); } catch { } }
    }

    static async Task<string> RunAsync(string tool, string[] args, CancellationToken cancel)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, tool + ".exe");
        var start = new ProcessStartInfo(File.Exists(bundled) ? bundled : tool + ".exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException($"Не найден {tool}. Установите FFmpeg (ffmpeg и ffprobe) в PATH или рядом с ArkCopyBuffer.exe.", ex); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var registration = timeout.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); timeout.Token.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        { throw new TimeoutException("FFmpeg не завершил обработку за 2 минуты."); }
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
            ? $"{tool}: ошибка обработки (код 0x{process.ExitCode:X8})." : error.Trim());
        return await stdout;
    }

    // Короткая проверка на синтетическом видео: ArkCopyBuffer.exe --audio-selftest <папка>.
    internal static async Task SelfTestAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "video with spaces.mkv");
        await RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "color=size=16x16:duration=0.1",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=0.1", "-f", "lavfi", "-i", "sine=frequency=880:duration=0.1",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "ffv1", "-c:a", "aac",
            "-metadata:s:a:0", "language=rus", "-metadata:s:a:1", "title=Commentary", source], CancellationToken.None);
        var tracks = await ProbeAsync(source, CancellationToken.None);
        if (tracks.Length != 2 || tracks[0].Index != 1 || tracks[1].Index != 2 ||
            !tracks[0].Label.Contains("rus") || !tracks[1].Label.Contains("Commentary")) throw new Exception("Выбор дорожки/метаданные.");
        var output = Path.Combine(dir, "selected.m4a");
        File.Delete(output);
        await ExtractFileAsync(source, output, tracks[1], CancellationToken.None);
        var extracted = await ProbeAsync(output, CancellationToken.None);
        if (extracted.Length != 1 || extracted[0].Codec != "aac") throw new Exception("Извлечение без перекодирования.");
        var originalHash = await RunAsync("ffmpeg", ["-v", "error", "-i", source, "-map", "0:2", "-c", "copy", "-f", "hash", "-hash", "sha256", "-"], CancellationToken.None);
        var extractedHash = await RunAsync("ffmpeg", ["-v", "error", "-i", output, "-map", "0:a:0", "-c", "copy", "-f", "hash", "-hash", "sha256", "-"], CancellationToken.None);
        if (originalHash != extractedHash) throw new Exception("Извлечена другая дорожка или изменены аудиоданные.");
        var broken = Path.Combine(dir, "broken.m4a");
        try { await ExtractFileAsync(source, broken, tracks[0] with { Index = 99 }, CancellationToken.None); throw new Exception("Некорректная дорожка принята."); }
        catch (InvalidOperationException) { if (File.Exists(broken)) throw new Exception("Неполный файл не удалён."); }
        var silent = Path.Combine(dir, "silent.mkv");
        await RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-y", "-i", source, "-map", "0:v", "-c", "copy", silent], CancellationToken.None);
        if ((await ProbeAsync(silent, CancellationToken.None)).Length != 0) throw new Exception("Видео без звука.");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "anullsrc", "-f", "null", "-"], cancel.Token);
            throw new Exception("Отмена проигнорирована.");
        }
        catch (OperationCanceledException) { }
        File.WriteAllText(Path.Combine(dir, "result.log"), "OK: multiple tracks, metadata, selected audio hash unchanged, single track, no audio, failed extraction cleanup, cancellation.");
    }
}
