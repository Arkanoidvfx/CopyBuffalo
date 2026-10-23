using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArkCopyBuffer;

internal sealed class AppUpdater : IDisposable
{
    const string Repository = "https://github.com/Arkanoidvfx/CopyBuffalo";
    const string AssetName = "ArkCopyBuffer-Setup.exe";
    internal static Version CurrentVersion => typeof(App).Assembly.GetName().Version!;
    internal static string VersionText => CurrentVersion.ToString(3);
    internal sealed record Package(Version Version, Uri Url, long Size, string Hash);
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly CancellationTokenSource _stop = new();
    readonly string _directory;
    internal Package? Ready { get; private set; }
    internal bool Busy { get; private set; }
    internal string Status { get; private set; } = UiText.T("Проверка при запуске и каждые 15 минут.");
    internal event Action? Changed;
    string Installer => Path.Combine(_directory, "setup.exe");

    internal AppUpdater(string directory)
    {
        _directory = directory;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ArkCopyBuffer/{VersionText}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal static Package? ParseRelease(JsonElement release, Version current)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            string digest = asset.GetProperty("digest").GetString() ?? "";
            long size = asset.GetProperty("size").GetInt64();
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (url.Scheme != "https" || url.Host != "github.com" ||
                !url.AbsoluteUri.StartsWith(Repository + "/releases/download/", StringComparison.Ordinal) ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 ||
                !digest[7..].All(Uri.IsHexDigit) || size is <= 0 or > 1_073_741_824)
                throw new InvalidDataException(UiText.T("Некорректный пакет обновления."));
            return new(version, url, size, digest[7..]);
        }
        throw new InvalidDataException(UiText.T("В релизе нет установщика ArkCopyBuffer-Setup.exe."));
    }

    internal static async Task VerifyAsync(string path, Package package, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        if (file.Length != package.Size || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(package.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(UiText.T("Проверка SHA-256 обновления не пройдена."));
    }

    void SetStatus(string value) { Status = value; Changed?.Invoke(); }

    internal async Task CheckAsync()
    {
        if (Busy) return;
        Busy = true;
        SetStatus(UiText.T("Проверяем обновления…"));
        string partial = Installer + ".partial";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var token = timeout.Token;
            using var response = await _http.GetAsync("https://api.github.com/repos/Arkanoidvfx/CopyBuffalo/releases/latest", token);
            if (response.StatusCode == HttpStatusCode.NotFound) { Ready = null; SetStatus(UiText.T("У вас последняя версия.")); return; }
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var package = ParseRelease(json.RootElement, CurrentVersion);
            if (package == null) { Ready = null; SetStatus(UiText.T("У вас последняя версия.")); return; }
            if (Ready == package) { SetStatus(UiText.F($"Версия {package.Version} готова к установке.")); return; }
            Ready = null;
            Directory.CreateDirectory(_directory);
            if (new DriveInfo(Path.GetPathRoot(_directory)!).AvailableFreeSpace < package.Size + 512L * 1024 * 1024)
                throw new IOException(UiText.T("Недостаточно места для обновления: нужно ещё 512 МБ сверх размера пакета."));
            SetStatus(UiText.F($"Скачиваем {package.Version} в фоне…"));
            using var download = await _http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, token);
            download.EnsureSuccessStatusCode();
            await using (var input = await download.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(partial))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += count;
                    if (total > package.Size) throw new InvalidDataException(UiText.T("Размер обновления превышает заявленный."));
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            await VerifyAsync(partial, package, token);
            File.Move(partial, Installer, overwrite: true);
            Ready = package;
            SetStatus(UiText.F($"Версия {package.Version} готова к установке."));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus(Ready == null ? UiText.F($"Не удалось проверить обновления: {ex.Message}") : UiText.F($"Обновление {Ready.Version} готово. Нет связи с сервером.")); }
        finally
        {
            try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Busy = false;
            Changed?.Invoke();
        }
    }

    internal async Task PrepareInstallAsync()
    {
        if (Busy || Ready == null) throw new InvalidOperationException(UiText.T("Обновление ещё не готово."));
        try { await VerifyAsync(Installer, Ready, _stop.Token); }
        catch { Ready = null; SetStatus(UiText.T("Скачанный пакет повреждён. Проверьте обновления ещё раз.")); throw; }
    }

    // A separate Windows PowerShell process waits for a clean save/exit before Inno replaces files.
    internal async Task<Process> StartInstallerAsync()
    {
        var package = Ready ?? throw new InvalidOperationException(UiText.T("Обновление не найдено."));
        string ready = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".ready");
        string script = BuildInstallScript(Installer, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            Environment.ProcessPath!, Environment.ProcessId, package.Hash, Path.Combine(_directory, "install-error.txt"));
        script = $"[IO.File]::WriteAllText('{ready.Replace("'", "''")}', 'ready')\n" + script;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new IOException(UiText.T("Не удалось запустить обновление."));
        try
        {
            for (int attempt = 0; attempt < 50 && !process.HasExited; attempt++)
            {
                if (File.Exists(ready)) return process;
                await Task.Delay(100, _stop.Token);
            }
            throw new IOException(UiText.T("Помощник обновления не запустился. Приложение продолжает работать."));
        }
        catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
        finally { if (File.Exists(ready)) File.Delete(ready); }
    }

    internal static string BuildInstallScript(string installer, string target, string exe, int pid, string hash, string error)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $setup = {{Quote(installer)}}
            $target = {{Quote(target)}}
            $exe = {{Quote(exe)}}
            $errorFile = {{Quote(error)}}
            $old = $null
            try {
                try { $old = [Diagnostics.Process]::GetProcessById({{pid}}) } catch [ArgumentException] { }
                if ($old -and !$old.WaitForExit(30000)) { throw 'Приложение не завершилось: обновление отменено.' }
                $stream = [IO.File]::OpenRead($setup)
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                finally { $stream.Dispose(); $sha.Dispose() }
                if ($actual -ne {{Quote(hash)}}) { throw 'SHA-256 обновления изменился.' }
                $info = New-Object Diagnostics.ProcessStartInfo
                $info.FileName = $setup
                $info.Arguments = '/UPDATE=1 /VERYSILENT /SUPPRESSMSGBOXES /SP- /NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /DIR="' + $target + '" /LOG="' + $setup + '.log"'
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
                $process = [Diagnostics.Process]::Start($info)
                $process.WaitForExit()
                if ($process.ExitCode -ne 0) { throw ('Установщик завершился с кодом ' + $process.ExitCode) }
                [IO.File]::Delete($errorFile)
            } catch {
                [IO.File]::WriteAllText($errorFile, $_.Exception.Message)
            } finally {
                if (!$old -or $old.HasExited) {
                    $restart = New-Object Diagnostics.ProcessStartInfo
                    $restart.FileName = $exe
                    $restart.WorkingDirectory = $target
                    $restart.UseShellExecute = $false
                    $restart.CreateNoWindow = $true
                    $restart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
                    [Diagnostics.Process]::Start($restart) | Out-Null
                }
            }
            """;
    }

    public void Dispose() { _stop.Cancel(); _http.Dispose(); }
}
