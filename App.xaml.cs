using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ArkCopyBuffer;

public partial class App : Application
{
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArkCopyBuffer");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string SettingsKey = @"Software\ArkCopyBuffer";

    Mutex? _single;
    HistoryStore _store = null!;
    ClipboardWatcher _watcher = null!;
    WinVHook? _hook;
    PanelWindow _panel = null!;
    Forms.NotifyIcon _tray = null!;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _pruneTimer = new() { Interval = TimeSpan.FromHours(1) };
    IntPtr _target; // окно, куда вставлять

    public static bool IsDarkTheme { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--selftest"))
        {
            Shutdown(HistoryStore.SelfTest(Path.Combine(DataDir, "selftest.log")));
            return;
        }

        if (e.Args is ["--restart", var oldPid] && int.TryParse(oldPid, out int pid))
        {
            try { using var old = Process.GetProcessById(pid); if (!old.WaitForExit(10000)) { Shutdown(); return; } }
            catch (ArgumentException) { } // старый процесс уже завершился
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = AppContext.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden }); }
            catch (Exception ex) { Log(ex); }
            Shutdown();
            return;
        }

        _single = new Mutex(true, @"Local\ArkCopyBuffer", out bool first);
        if (!first) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ApplyTheme();
        });

        _store = new HistoryStore(HistoryStore.DefaultPath);
        _store.Load(DateTime.UtcNow);
        Trace($"start: {HistoryStore.DefaultPath}, items={_store.Items.Count}");
        _store.Changed += () => { _saveTimer.Stop(); _saveTimer.Start(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _pruneTimer.Tick += (_, _) => { if (_store.Prune(DateTime.UtcNow)) Save(); };
        _pruneTimer.Start();

        Paster.CleanGifTemp();
        _watcher = new ClipboardWatcher(_store);
        _panel = new PanelWindow(_store, new SymbolCatalog(Path.Combine(DataDir, "symbols.json")));
        _panel.PasteRequested += OnPasteRequested;
        _panel.TypeRequested += OnTypeRequested;
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            _panel.Animations = key?.GetValue("Animations") is not int a || a != 0;
            _panel.ShowDurationMs = key?.GetValue("ShowDurationMs") is int show ? show : 100;
            _panel.HideDurationMs = key?.GetValue("HideDurationMs") is int hide ? hide : 100;
            _panel.ImageZoomDelayMs = key?.GetValue("ImageZoomDelayMs") is int zoom ? zoom : 1000;
            _panel.ImageZoomScale = key?.GetValue("ImageZoomScale") is int scale ? scale : 3;
            _panel.AutoHideOutside = key?.GetValue("AutoHideOutside") is int outside ? outside != 0
                : key?.GetValue("HideOnDeactivate") is not int h || h != 0;
            _panel.OutsideHideDelayMs = key?.GetValue("OutsideHideDelayMs") is int delay ? delay : 3000;
        }
        _panel.TimingsChanged += (show, hide) =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("ShowDurationMs", show, RegistryValueKind.DWord);
            key.SetValue("HideDurationMs", hide, RegistryValueKind.DWord);
        };
        _panel.AutoHideOutsideChanged += hide =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("AutoHideOutside", hide ? 1 : 0, RegistryValueKind.DWord);
        };
        _panel.OutsideHideDelayChanged += delay =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("OutsideHideDelayMs", delay, RegistryValueKind.DWord);
        };
        _panel.ImageZoomDelayChanged += delay =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("ImageZoomDelayMs", delay, RegistryValueKind.DWord);
        };
        _panel.ImageZoomScaleChanged += scale =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("ImageZoomScale", scale, RegistryValueKind.DWord);
        };
        _panel.WarmUp();
        CaretLocator.WarmUp();
        _tray = CreateTray();

        try
        {
            _hook = new WinVHook(foreground =>
            {
                long pressed = System.Diagnostics.Stopwatch.GetTimestamp();
                Dispatcher.BeginInvoke(() => Toggle(foreground, pressed));
            });
        }
        catch (Exception ex)
        {
            Log(ex);
            _tray.ShowBalloonTip(5000, "ArkCopyBuffer", "Не удалось перехватить Win+V. История пишется, открыть можно из трея.", Forms.ToolTipIcon.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _watcher?.Dispose();
        if (_saveTimer.IsEnabled) Save();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    void Toggle(IntPtr foreground, long pressed)
    {
        double Ms(long from) => System.Diagnostics.Stopwatch.GetElapsedTime(from).TotalMilliseconds;
        double queued = Ms(pressed);
        Trace($"Win+V: fg={ProcessOf(foreground)} visible={_panel.IsVisible} active={_panel.IsActive}");
        // Видна, но не активна (фокус увели) — показываем заново, а не прячем.
        if (_panel.IsVisible && _panel.IsActive) { _panel.HideAnimated(); return; }
        _target = foreground;
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        var (anchor, isCaret) = CaretLocator.Locate(foreground);
        double locate = Ms(t);
        t = System.Diagnostics.Stopwatch.GetTimestamp();
        _panel.ShowAt(anchor);
        double show = Ms(t);
        // Первый кадр на экране — когда WPF отрисует после показа.
        EventHandler? rendered = null;
        rendered = (_, _) =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= rendered;
            Trace($"  timing: queue={queued:0}ms caret={locate:0}ms show={show:0}ms firstFrame={Ms(pressed):0}ms (caret found={isCaret})");
        };
        System.Windows.Media.CompositionTarget.Rendering += rendered;
    }

    static readonly object TraceLock = new();
    static readonly bool TraceOn = Environment.GetEnvironmentVariable("ARKCB_TRACE") == "1"
        || File.Exists(Path.Combine(DataDir, "trace.on"));

    /// <summary>Отладочный журнал: ARKCB_TRACE=1 или файл trace.on рядом с историей → trace.log.</summary>
    internal static void Trace(string line)
    {
        if (!TraceOn) return;
        lock (TraceLock) try { File.AppendAllText(Path.Combine(DataDir, "trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}\n"); } catch { }
    }

    static string ProcessOf(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
    }

    async void OnPasteRequested(ClipItem item, bool plainText)
    {
        _panel.HideAnimated();
        try { Paster.SetClipboard(item, plainText, _store.ImagesDir); }
        catch (Exception ex) when (ex is System.IO.IOException or System.Security.Cryptography.CryptographicException)
        {
            // Файл картинки пропал или не расшифровывается — запись бесполезна.
            Log(ex);
            _store.Remove(item);
            _tray.ShowBalloonTip(2500, "ArkCopyBuffer", "Картинка не найдена на диске, запись удалена.", Forms.ToolTipIcon.Warning);
            return;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log(ex);
            _tray.ShowBalloonTip(2500, "ArkCopyBuffer", "Буфер обмена занят. Повторите вставку.", Forms.ToolTipIcon.Warning);
            return;
        }
        if (_target == IntPtr.Zero) return; // открыто из трея — только копируем
        bool pasted = await Paster.PasteInto(_target);
        Trace($"paste into {ProcessOf(_target)}: {pasted}, fgNow={ProcessOf(Native.GetForegroundWindow())}");
        if (!pasted)
            _tray.ShowBalloonTip(2500, "Скопировано", "Не удалось вставить в нужное окно. Нажмите Ctrl+V.", Forms.ToolTipIcon.None);
    }

    async void OnTypeRequested(string text)
    {
        _panel.HideAnimated();
        // Открыто из трея — печатать некуда, кладём в буфер.
        if (_target == IntPtr.Zero || !await Paster.TypeInto(_target, text))
        {
            try { Paster.CopyText(text); }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log(ex);
                _tray.ShowBalloonTip(2500, "ArkCopyBuffer", "Буфер обмена занят. Повторите действие.", Forms.ToolTipIcon.Warning);
                return;
            }
            if (_target != IntPtr.Zero)
                _tray.ShowBalloonTip(2500, "Скопировано", "Не удалось напечатать в нужном окне. Нажмите Ctrl+V.", Forms.ToolTipIcon.None);
        }
    }

    void Save()
    {
        try { _store.Save(); }
        catch (Exception ex) { Log(ex); }
    }

    // ---------- Трей ----------

    Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => { _target = IntPtr.Zero; var (a, _) = CaretLocator.Locate(IntPtr.Zero); _panel.ShowAt(a); });

        var pause = new Forms.ToolStripMenuItem("Пауза записи") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => _watcher.Paused = pause.Checked;
        menu.Items.Add(pause);

        var autostart = new Forms.ToolStripMenuItem("Запускать вместе с Windows") { Checked = IsAutostart(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => SetAutostart(autostart.Checked);
        menu.Items.Add(autostart);

        var animations = new Forms.ToolStripMenuItem("Анимация окна") { Checked = _panel.Animations, CheckOnClick = true };
        animations.CheckedChanged += (_, _) =>
        {
            _panel.Animations = animations.Checked;
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("Animations", animations.Checked ? 1 : 0, RegistryValueKind.DWord);
        };
        menu.Items.Add(animations);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Очистить историю (кроме закреплённых)", null, (_, _) => _panel.ClearHistory());
        menu.Items.Add("Перезапустить", null, (_, _) => Restart());
        menu.Items.Add("Выход", null, (_, _) => Shutdown());

        var tray = new Forms.NotifyIcon { Text = "ArkCopyBuffer: Win+V", Icon = CreateTrayIcon(), ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            _target = IntPtr.Zero;
            var (a, _) = CaretLocator.Locate(IntPtr.Zero);
            _panel.ShowAt(a);
        };
        return tray;
    }

    void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
            if (Process.Start(new ProcessStartInfo(exe, $"--restart {Environment.ProcessId}")
                { WorkingDirectory = AppContext.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden }) != null)
                Shutdown();
        }
        catch (Exception ex) { Log(ex); }
    }

    static System.Drawing.Icon? _trayIcon;

    /// <summary>Оранжевая иконка буфера с возвратной стрелкой.</summary>
    static System.Drawing.Icon CreateTrayIcon()
    {
        if (_trayIcon != null) return _trayIcon;
        using var stream = typeof(App).Assembly.GetManifestResourceStream("ArkCopyBuffer.Assets.App.ico")
            ?? throw new InvalidOperationException("Embedded app icon missing");
        return _trayIcon = new System.Drawing.Icon(stream);
    }

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("ArkCopyBuffer") != null;
    }

    static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue("ArkCopyBuffer", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("ArkCopyBuffer", throwOnMissingValue: false);
    }

    // ---------- Тема ----------

    /// <summary>Кисти-токены Windows 11 под текущую тему приложений и акцентный цвет.</summary>
    void ApplyTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        IsDarkTheme = key?.GetValue("AppsUseLightTheme") is not int v || v == 0;

        var accent = ReadAccent();
        // Как в Win11: в тёмной теме акцент светлее, в светлой — темнее.
        accent = IsDarkTheme ? Mix(accent, Colors.White, 0.35) : Mix(accent, Colors.Black, 0.15);

        var t = IsDarkTheme
            ? new Dictionary<string, uint>
            {
                ["WindowSurface"] = 0xFF202020, ["WindowStroke"] = 0x1FFFFFFF,
                ["TextPrimary"] = 0xFFFFFFFF, ["TextSecondary"] = 0xC5FFFFFF, ["TextDisabled"] = 0x5DFFFFFF,
                ["CardFill"] = 0x0DFFFFFF, ["CardHover"] = 0x18FFFFFF, ["CardStroke"] = 0x19000000,
                ["SubtleHover"] = 0x0FFFFFFF, ["SubtlePressed"] = 0x0AFFFFFF,
                ["ControlFill"] = 0x0FFFFFFF, ["ControlFillHover"] = 0x15FFFFFF, ["ControlFillFocused"] = 0xB31E1E1E,
                ["ControlStroke"] = 0x12FFFFFF, ["ControlStrongStroke"] = 0x8BFFFFFF,
                ["ScrollThumb"] = 0x72FFFFFF, ["MenuSurface"] = 0xFF2C2C2C, ["MenuStroke"] = 0x33000000,
            }
            : new Dictionary<string, uint>
            {
                ["WindowSurface"] = 0xFFF3F3F3, ["WindowStroke"] = 0x24000000,
                ["TextPrimary"] = 0xE4000000, ["TextSecondary"] = 0x9E000000, ["TextDisabled"] = 0x5C000000,
                ["CardFill"] = 0xB3FFFFFF, ["CardHover"] = 0xF2FFFFFF, ["CardStroke"] = 0x0F000000,
                ["SubtleHover"] = 0x09000000, ["SubtlePressed"] = 0x06000000,
                ["ControlFill"] = 0xB3FFFFFF, ["ControlFillHover"] = 0x80F9F9F9, ["ControlFillFocused"] = 0xFFFFFFFF,
                ["ControlStroke"] = 0x0F000000, ["ControlStrongStroke"] = 0x72000000,
                ["ScrollThumb"] = 0x72000000, ["MenuSurface"] = 0xFFF9F9F9, ["MenuStroke"] = 0x0F000000,
            };

        foreach (var (name, argb) in t)
            Resources[name] = Frozen(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        Resources["Accent"] = Frozen(accent);
    }

    static Color ReadAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        return key?.GetValue("AccentColor") is int abgr
            ? Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16))
            : Color.FromRgb(0x00, 0x67, 0xC0); // синий Windows по умолчанию
    }

    static Color Mix(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "error.log"), $"{DateTime.Now:u} {ex}\n\n");
        }
        catch { }
    }
}
