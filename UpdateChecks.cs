using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace ArkCopyBuffer;

internal static class UpdateChecks
{
    internal static async Task HelperAsync(string dir)
    {
        string fixture = Path.Combine(dir, "fixture", "UpdateFixture.exe");
        string target = Path.Combine(dir, "target with 'quote");
        string error = Path.Combine(dir, "install-error.txt");
        string restart = Path.Combine(dir, "fixture", "restarted.txt");
        string invoked = Path.Combine(dir, "fixture", "invoked.txt");
        Directory.CreateDirectory(target);
        string hash;
        await using (var stream = File.OpenRead(fixture)) hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        async Task Run(string expectedHash, int oldPid = int.MaxValue)
        {
            File.Delete(restart); File.Delete(invoked); File.Delete(error);
            string script = AppUpdater.BuildInstallScript(fixture, target, fixture, oldPid, expectedHash, error);
            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            for (int i = 0; i < 50 && !File.Exists(restart); i++) await Task.Delay(100);
            if (!File.Exists(restart)) throw new Exception("Helper did not restart fixture.");
        }
        string exited = Path.Combine(dir, "fixture", "old-exited.txt");
        File.Delete(exited);
        using (var old = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fixture, "--wait")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!)
        {
            await Run(hash, old.Id);
            if (!File.Exists(exited)) throw new Exception("Helper did not wait for the old process.");
        }
        if (File.Exists(error) || !File.Exists(Path.Combine(target, "payload.txt"))) throw new Exception("Helper installation failed.");
        string arguments = File.ReadAllText(invoked);
        if (!arguments.Contains("/UPDATE=1") || !arguments.Contains("/DIR=" + target)) throw new Exception("Helper argument quoting failed.");
        File.WriteAllText(Path.Combine(dir, "fixture", "fail"), "");
        await Run(hash);
        if (!File.Exists(error) || !File.ReadAllText(error).Contains("5")) throw new Exception("Installer failure was not retained.");
        File.Delete(Path.Combine(dir, "fixture", "fail"));
        await Run(new string('0', 64));
        if (!File.Exists(error) || File.Exists(invoked)) throw new Exception("A modified installer was executed.");
        File.WriteAllText(Path.Combine(dir, "result.txt"), "PASS: clean exit wait, quoted paths, silent installation, restart, installer failure/restart, tampered installer rejection/restart.");
    }

    internal static void SelfTest(string dir, Action<bool, string> check)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "update-fixture.exe");
        byte[] bytes = [1, 2, 3, 4];
        File.WriteAllBytes(path, bytes);
        string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes));
        object Release(string tag = "v0.3.0", bool prerelease = false, string? hash = null, string? url = null, long size = 4, bool draft = false, string name = "CopyBuffalo-Setup.exe") => new
        {
            tag_name = tag, prerelease, draft,
            assets = new[] { new { name, browser_download_url = url ?? "https://github.com/Arkanoidvfx/CopyBuffalo/releases/download/v0.3.0/CopyBuffalo-Setup.exe", digest = hash ?? digest, size } }
        };
        AppUpdater.Package? Parse(object release) => AppUpdater.ParseRelease(JsonSerializer.SerializeToElement(release), new Version(0, 2, 0, 0));
        void Reject(object release, string label)
        {
            try { Parse(release); check(false, label); }
            catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException) { check(true, label); }
        }
        var package = Parse(Release())!;
        check(package.Version == new Version(0, 3, 0) && package.Size == 4, "update selects a newer stable core installer");
        Reject(Release(name: "ArkCopyBuffer-Setup.exe"), "update requires the CopyBuffalo installer asset name");
        check(Parse(Release("v0.2.0")) == null && Parse(Release("v0.1.0")) == null, "update rejects equal versions and downgrades");
        check(Parse(Release(prerelease: true)) == null && Parse(Release(draft: true)) == null, "update skips draft and prerelease versions");
        check(Parse(Release("v0.3.0-beta")) == null, "update refuses malformed version tags");
        Reject(Release(hash: "sha256:bad"), "update requires a full SHA-256 digest");
        Reject(Release(hash: "sha256:" + new string('z', 64)), "update refuses a non-hex digest");
        Reject(Release(url: "https://github.com/other/project/releases/download/v1/setup.exe"), "update rejects a different repository");
        Reject(Release(url: "http://github.com/Arkanoidvfx/CopyBuffalo/releases/download/v1/setup.exe"), "update requires HTTPS");
        Reject(Release(size: 0), "update rejects empty packages");
        Reject(Release(size: 1_073_741_825), "update bounds package size");
        check(App.CanApplyUpdate(true, true, false, false), "automatic update permits idle tray installation");
        check(!App.CanApplyUpdate(true, false, false, false), "disabling automatic updates cancels prepared auto installation");
        check(!App.CanApplyUpdate(true, true, true, false), "reopening the panel cancels prepared auto installation");
        check(!App.CanApplyUpdate(true, true, false, true) && !App.CanApplyUpdate(false, true, true, true), "manual and automatic updates wait for active operations");
        check(App.CanApplyUpdate(false, false, true, false), "manual update remains available with automatic updates disabled");
        Task.Run(() => AppUpdater.VerifyAsync(path, package, CancellationToken.None)).GetAwaiter().GetResult();
        check(true, "downloaded update passes exact size and SHA-256 verification");
        File.WriteAllBytes(path, [4, 3, 2, 1]);
        try { Task.Run(() => AppUpdater.VerifyAsync(path, package, CancellationToken.None)).GetAwaiter().GetResult(); check(false, "modified package rejected"); }
        catch (InvalidDataException) { check(true, "modified update rejected before installer launch"); }
        File.WriteAllBytes(path, [1]);
        try { Task.Run(() => AppUpdater.VerifyAsync(path, package, CancellationToken.None)).GetAwaiter().GetResult(); check(false, "truncated package rejected"); }
        catch (InvalidDataException) { check(true, "truncated update rejected before installer launch"); }
        string script = AppUpdater.BuildInstallScript("D:\\a'b\\setup.exe", "D:\\app files", "D:\\app files\\app.exe", 123, package.Hash, "D:\\error.txt");
        check(script.Contains("'D:\\a''b\\setup.exe'") && script.Contains("/UPDATE=1") && script.Contains("/NOCLOSEAPPLICATIONS") && script.Contains("/NORESTART"), "update helper quotes paths and never forces another process or OS restart");
        var panel = new PanelWindow(new HistoryStore(Path.Combine(dir, "update-history.dat")), new SymbolCatalog(Path.Combine(dir, "update-symbols.json")));
        try
        {
            check(panel.AutoUpdate, "automatic updates are enabled by default");
            int changes = 0;
            panel.AutoUpdateChanged += _ => changes++;
            panel.AutoUpdate = false;
            check(!panel.AutoUpdate && changes == 1, "automatic updates can be disabled");
            panel.SetUpdateStatus("Версия 0.3.0 готова к установке.", true, false);
            bool motion = Motion.Enabled;
            Motion.Enabled = false;
            panel.OpenSettings();
            panel.Root.Opacity = 1;
            panel.Root.Measure(new Size(392, 492)); panel.Root.Arrange(new Rect(0, 0, 392, 492)); panel.Root.UpdateLayout();
            check(panel.UpdateButton.IsEnabled && panel.UpdateButton.Content.ToString() == UiText.T("Обновить и перезапустить"), "ready update exposes one restart action");
            panel.AutoUpdate = true;
            panel.VisualSettingsView.ScrollToEnd(); panel.Root.UpdateLayout();
            panel.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            panel.Root.UpdateLayout();
            panel.VisualSettingsView.ScrollToVerticalOffset(panel.VisualSettingsView.ExtentHeight);
            panel.Root.UpdateLayout();
            var buttonTop = panel.UpdateButton.TranslatePoint(new Point(), panel.VisualSettingsView);
            check(buttonTop.Y >= 0 && buttonTop.Y + panel.UpdateButton.ActualHeight <= panel.VisualSettingsView.ActualHeight + 1,
                $"update action fits the scrolled settings viewport ({buttonTop.Y:0}/{panel.UpdateButton.ActualHeight:0}/{panel.VisualSettingsView.ActualHeight:0})");
            check(panel.UpdateButton.TranslatePoint(new Point(), panel.Root).Y + panel.UpdateButton.ActualHeight <= panel.Root.ActualHeight,
                "settings scrolling keeps the update action inside the panel bounds");
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(392, 492, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(panel.Root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            // Keep the offscreen review image outside the disposable synthetic history directory.
            string preview = Path.Combine(AppContext.BaseDirectory, "update-settings-preview.png");
            using (var output = File.Create(preview)) encoder.Save(output);
            Motion.Enabled = motion;
            panel.SetUpdateStatus("Скачиваем…", false, true);
            check(!panel.UpdateButton.IsEnabled, "update button is disabled during background work");
        }
        finally { panel.Close(); }
    }
}
