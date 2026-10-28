using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ArkCopyBuffer;

public partial class App : Application
{
    internal static Typeface TextTypeface { get; private set; } = null!;
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArkCopyBuffer");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string SettingsKey = @"Software\ArkCopyBuffer";

    Mutex? _single;
    HistoryStore _store = null!;
    StorageSettings _storage = new();
    ClipboardWatcher _watcher = null!;
    WinVHook? _hook;
    PanelWindow _panel = null!;
    readonly PromptCompressor _compressor = new();
    readonly ScreenshotOcr _ocr = new();
    CancellationTokenSource? _ocrCancel;
    Forms.NotifyIcon _tray = null!;
    ContextMenu _trayMenu = null!;  // WPF-меню в стиле панели вместо светлого WinForms ContextMenuStrip
    MenuItem _pauseItem = null!, _autostartItem = null!;
    TextBlock _trayStatus = null!;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _pruneTimer = new() { Interval = TimeSpan.FromHours(1) };
    IntPtr _target; // окно, куда вставлять
    int _pasteVersion;
    CancellationTokenSource? _archiveCancel;
    readonly CancellationTokenSource _audioCancel = new();
    bool _started;

    public static bool IsDarkTheme { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Immutable typeface is shared by background graph drawing; resource lookup stays on the UI thread.
        TextTypeface = new Typeface((FontFamily)FindResource("TextFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ||
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
        {
            MessageBox.Show(UiText.T("Нужна Windows 10 (1809 или новее) либо Windows 11, 64-битный процессор Intel/AMD."),
                "CopyBuffalo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        if (e.Args is ["--localization-selftest", var localeDirectory, var language] && UiText.Languages.Contains(language))
        {
            UiText.Language = language;
            System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            ApplyTheme();
            try { LocalizationChecks.Run(localeDirectory); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(localeDirectory); File.WriteAllText(Path.Combine(localeDirectory, "ERROR.txt"), ex.ToString()); Shutdown(1); }
            return;
        }

        if (e.Args is ["--audio-selftest", var testDir])
        {
            try { Task.Run(() => AudioExtractor.SelfTestAsync(testDir)).GetAwaiter().GetResult(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(testDir); File.WriteAllText(Path.Combine(testDir, "result.log"), ex.ToString()); Shutdown(1); }
            return;
        }

        if (e.Args is ["--ocr-selftest", var corpusDir])
        {
            try { Task.Run(() => OcrChecks.IntegrationAsync(corpusDir)).GetAwaiter().GetResult(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(corpusDir); File.WriteAllText(Path.Combine(corpusDir, "ocr-test-error.log"), ex.ToString()); Shutdown(1); }
            return;
        }

        if (e.Args is ["--translation-selftest", var translationDir])
        {
            try { Task.Run(() => OcrChecks.TranslationAsync(translationDir)).GetAwaiter().GetResult(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(translationDir); File.WriteAllText(Path.Combine(translationDir, "translation-error.log"), ex.ToString()); Shutdown(1); }
            return;
        }

        if (e.Args.Contains("--selftest"))
        {
            if (e.Args is ["--selftest", _, var testLanguage] && UiText.Languages.Contains(testLanguage)) UiText.Language = testLanguage;
            System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            ApplyTheme(); // Offscreen layout tests need the same brushes as the running app.
            string log = e.Args.Length >= 2 ? Path.Combine(Path.GetFullPath(e.Args[1]), "selftest.log") : Path.Combine(DataDir, "selftest.log");
            Shutdown(HistoryStore.SelfTest(log));
            return;
        }

        if (e.Args is ["--update-helper-selftest", var updateDir])
        {
            try { Task.Run(() => UpdateChecks.HelperAsync(updateDir)).GetAwaiter().GetResult(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(updateDir); File.WriteAllText(Path.Combine(updateDir, "test-error.txt"), ex.ToString()); Shutdown(1); }
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

        DispatcherUnhandledException += (_, ex) =>
        {
            Log(ex.Exception);
            ex.Handled = true;
            if (_started) return;
            // Не запустились (история не читается, буфер не слушается): не висим невидимкой без трея, держа мьютекс.
            MessageBox.Show(UiText.F($"CopyBuffalo не запустился: {ex.Exception.Message}\n\nПодробности: {Path.Combine(DataDir, "error.log")}"),
                "CopyBuffalo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ApplyTheme();
        });

        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey)) _storage = StorageSettings.Read(key);
        _store = new HistoryStore(HistoryStore.DefaultPath) { Storage = _storage };
        _store.Load(DateTime.UtcNow);
        Trace($"start: {HistoryStore.DefaultPath}, items={_store.Items.Count}");
        // Не откладываем при каждом изменении: если буфер меняется чаще раза в секунду (RDP, виртуалка, синхронизация), сохранение не наступило бы никогда.
        _store.Changed += () => { if (!_saveTimer.IsEnabled) _saveTimer.Start(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _pruneTimer.Tick += (_, _) => { if (_store.Prune(DateTime.UtcNow)) Save(); Paster.CleanGifTemp(_storage.TempDays); };
        _pruneTimer.Start();

        Paster.CleanGifTemp(_storage.TempDays);
        _watcher = new ClipboardWatcher(_store);
        _panel = new PanelWindow(_store, new SymbolCatalog(Path.Combine(DataDir, "symbols.json")));
        _panel.Storage = _storage;
        _panel.LanguageChanged += language =>
        {
            // Язык интерфейса читается только при старте: выбор сохраняем вместе с перезапуском (панель уже спросила).
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
                key.SetValue("Language", language, RegistryValueKind.String);
                _panel.SetLanguagePreference(language);
                Restart();
            }
            catch (Exception ex) { Notify(InfoKind.Caution, UiText.T("Не удалось сохранить язык."), ex.Message); }
        };
        _panel.StorageChanged += settings =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            settings.Write(key);
            _storage = settings;
            _watcher.Storage = settings;
            _store.ApplyStorage(settings, DateTime.UtcNow);
            Paster.CleanGifTemp(settings.TempDays);
        };
        _panel.PasteRequested += OnPasteRequested;
        _panel.SeriesPasteAllRequested += items => { if (items.Count > 0) OnPasteRequested(items[0], false, items); };
        _panel.TypeRequested += OnTypeRequested;
        _panel.CompressRequested += OnCompressRequested;
        _panel.TranslateTextRequested += OnTranslateTextRequested;
        _panel.ArchiveRequested += OnArchiveRequested;
        _panel.ExtractAudioRequested += OnExtractAudioRequested;
        _panel.OcrRequested += OnOcrRequested;
        _panel.TranslateImageRequested += item => ProcessImage(item, translate: true);
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            // Своя настройка, а не системные «Эффекты анимации»: у многих они выключены ради скорости, а анимацию панели просили отдельно.
            _panel.Animations = key?.GetValue("Animations") is not int a || a != 0;
            _panel.ShowKeyHints = key?.GetValue("KeyHints") is not int hints || hints != 0;
            _panel.PinnedAsList = key?.GetValue("PinnedView") is int view && view == 1;
            _panel.VfxEnabled = key?.GetValue("VfxEnabled") is not int vfx || vfx != 0;
            _panel.VfxOpacity = key?.GetValue("VfxOpacity") is int vfxOpacity ? vfxOpacity : 85;
            _panel.VfxSpeed = key?.GetValue("VfxSpeed") is int vfxSpeed ? vfxSpeed : 100;
            _panel.TextPeekEnabled = key?.GetValue("TextPeek") is not int peek || peek != 0;
            _panel.TextPeekDelayMs = key?.GetValue("TextPeekDelayMs") is int peekDelay ? peekDelay : 600;
            _panel.TextPeekWidth = key?.GetValue("TextPeekWidth") is int peekWidth ? peekWidth : 380;
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
        _panel.AnimationsChanged += on =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("Animations", on ? 1 : 0, RegistryValueKind.DWord);
        };
        _panel.KeyHintsChanged += on =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("KeyHints", on ? 1 : 0, RegistryValueKind.DWord);
        };
        _panel.VfxChanged += (on, opacity, speed) =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("VfxEnabled", on ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("VfxOpacity", opacity, RegistryValueKind.DWord);
            key.SetValue("VfxSpeed", speed, RegistryValueKind.DWord);
        };
        _panel.PinnedViewChanged += list =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("PinnedView", list ? 1 : 0, RegistryValueKind.DWord);
        };
        _panel.TextPeekChanged += (on, delay, width) =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("TextPeek", on ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("TextPeekDelayMs", delay, RegistryValueKind.DWord);
            key.SetValue("TextPeekWidth", width, RegistryValueKind.DWord);
        };
        _panel.PauseToggleRequested += SetPaused;
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
            _tray.ShowBalloonTip(5000, "CopyBuffalo", UiText.T("Не удалось перехватить Win+V. История пишется, открыть можно из трея."), Forms.ToolTipIcon.Warning);
        }
        _started = true;
        InitializeUpdates();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updateTimer.Stop();
        _updateIdleTimer.Stop();
        _updater?.Dispose();
        _archiveCancel?.Cancel();
        _audioCancel.Cancel();
        _ocrCancel?.Cancel();
        _ocr.Dispose();
        _hook?.Dispose();
        _watcher?.Dispose();
        _compressor.Dispose();
        // Синхронно и дожидаясь фоновой записи. Только после полного старта: недогруженная история затёрла бы файл.
        if (_started) try { _store.Save(); } catch (Exception ex) { Log(ex); }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _trayIcon?.Dispose();
        _pausedIcon?.Dispose();
        base.OnExit(e);
    }

    void Toggle(IntPtr foreground, long pressed)
    {
        double Ms(long from) => System.Diagnostics.Stopwatch.GetElapsedTime(from).TotalMilliseconds;
        double queued = Ms(pressed);
        Trace($"Win+V: fg={ProcessOf(foreground)} visible={_panel.IsVisible} active={_panel.IsActive}");
        // Видна, но фокус у другого окна — показываем заново, а не прячем. Фокус — по Windows на момент нажатия:
        // IsActive WPF после первого показа бывает устаревшим.
        Native.GetWindowThreadProcessId(foreground, out var fgPid);
        if (_panel.IsVisible && fgPid == Environment.ProcessId) { _panel.HideAnimated(); return; }
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

    /// <summary>Панель открыта — сообщение в её InfoBar; скрыта (вставка уже ушла в окно) — балуном трея.</summary>
    void Notify(InfoKind kind, string title, string message, int ms = 2500)
    {
        if (_panel.IsShown) _panel.ShowInfo(kind, title, message);
        else _tray.ShowBalloonTip(ms, title, message,
            kind is InfoKind.Caution or InfoKind.Critical ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.None);
    }

    void OnPasteRequested(ClipItem item, bool plainText) => OnPasteRequested(item, plainText, null);

    async void OnPasteRequested(ClipItem item, bool plainText, IReadOnlyList<ClipItem>? batch)
    {
        _archiveCancel?.Cancel();
        int version = ++_pasteVersion;
        var series = batch == null ? _panel.BeginSeriesPaste(item) : _panel.BeginSeriesPaste(batch);
        if (batch != null && series == null) return;
        bool Current() => version == _pasteVersion && (series?.Current.Invoke() ?? true);
        bool pasted = false;
        try
        {
            _panel.HideAnimated();
            var target = _target;
            try
            {
                if (batch == null) await Paster.SetClipboardAsync(item, plainText, _store.ImagesDir, Current);
                else
                {
                    await Paster.SetImageSeriesClipboardAsync(batch, _store.ImagesDir, Current);
                    if (!Current()) return;
                    var copiedAt = DateTime.UtcNow;
                    foreach (var image in batch) _store.Touch(image.Id, copiedAt);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (DirectoryNotFoundException) when (item.IsFolder)
            {
                Notify(InfoKind.Caution, UiText.T("Исходная папка недоступна."), UiText.T("Она могла быть перемещена, удалена или находиться на отключённом диске."));
                return;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or System.Security.Cryptography.CryptographicException)
            {
                // Файл картинки пропал или не расшифровывается — запись бесполезна.
                Log(ex);
                if (batch == null)
                {
                    _store.Remove(item);
                    Notify(InfoKind.Caution, UiText.T("Файл записи повреждён или не найден."), UiText.T("Запись удалена."));
                }
                else Notify(InfoKind.Caution, UiText.T("Не удалось подготовить картинки и видео."), UiText.T("Вставьте их по одному, чтобы найти повреждённую запись."));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Временная копия занята/диск полон — сама запись цела, не удаляем.
                Log(ex);
                Notify(InfoKind.Caution, UiText.T("Не удалось подготовить файл для вставки."), UiText.T("Повторите позже."));
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log(ex);
                Notify(InfoKind.Caution, UiText.T("Буфер обмена занят."), UiText.T("Повторите вставку."));
                return;
            }
            if (target == IntPtr.Zero) return; // открыто из трея — только копируем
            if (!Current()) return;
            pasted = await Paster.PasteInto(target, Current);
            if (!Current()) { pasted = false; return; }
            Trace($"paste into {ProcessOf(target)}: {pasted}, fgNow={ProcessOf(Native.GetForegroundWindow())}");
            if (!pasted) Notify(InfoKind.Info, UiText.T("Скопировано."), UiText.T("Не удалось вставить в нужное окно. Нажмите Ctrl+V."));
        }
        finally { if (series is { } ticket) ticket.Complete(pasted); }
    }

    async void OnTypeRequested(string text)
    {
        _archiveCancel?.Cancel();
        ++_pasteVersion;
        _panel.HideAnimated();
        // Открыто из трея — печатать некуда, кладём в буфер.
        if (_target == IntPtr.Zero || !await Paster.TypeInto(_target, text))
        {
            try { Paster.CopyText(text); }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log(ex);
                Notify(InfoKind.Caution, UiText.T("Буфер обмена занят."), UiText.T("Повторите действие."));
                return;
            }
            if (_target != IntPtr.Zero)
                Notify(InfoKind.Info, UiText.T("Скопировано."), UiText.T("Не удалось напечатать в нужном окне. Нажмите Ctrl+V."));
        }
    }

    /// <summary>Сжатый промпт — новой записью наверх истории и сразу в буфер обмена; итог — в панели, если она открыта.</summary>
    async void OnArchiveRequested(ClipItem item)
    {
        if (!item.IsFolder || _archiveCancel != null) return;
        using var cancel = new CancellationTokenSource();
        _archiveCancel = cancel;
        int version = ++_pasteVersion;
        var target = _target;
        Native.GetWindowThreadProcessId(target, out uint targetPid);
        bool Current() => version == _pasteVersion && !cancel.IsCancellationRequested && _store.Items.Contains(item);
        item.Archiving = true;
        string? archive = null;
        bool copied = false;
        _panel.SetArchiveProgress(item.FileName ?? UiText.T("Подготовка папки…"), cancel.Cancel);
        try
        {
            archive = await Paster.CreateFolderArchiveAsync(item.Text, cancel.Token, message =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_archiveCancel, cancel) && Current()) _panel.SetArchiveProgress(message, cancel.Cancel);
                }));
            if (!Current()) throw new OperationCanceledException();
            Paster.SetArchiveClipboard(item, archive, Current);
            copied = true;
            _store.Touch(item.Id, DateTime.UtcNow);
            _panel.SetArchiveProgress(null);
            // За время упаковки пользователь мог уйти в другое окно: не возвращаем туда фокус насильно.
            Native.GetWindowThreadProcessId(target, out uint currentPid);
            bool canPaste = target != IntPtr.Zero && target == _target && targetPid == currentPid
                && (_panel.IsActive || Native.GetForegroundWindow() == target);
            if (canPaste)
            {
                _panel.HideAnimated();
                if (await Paster.PasteInto(target, Current)) return;
            }
            if (Current()) Notify(InfoKind.Success, UiText.T("ZIP готов и скопирован."), UiText.T("Нажмите Ctrl+V в приложении, принимающем файлы."));
        }
        catch (OperationCanceledException)
        {
            if (version == _pasteVersion && _started) Notify(InfoKind.Info, UiText.T("Создание ZIP отменено."), UiText.T("Исходная папка сохранена в истории."));
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log(ex);
            Notify(InfoKind.Caution, UiText.T("Буфер обмена занят."), UiText.T("Повторите отправку архивом."));
        }
        catch (Exception ex)
        {
            Log(ex);
            Notify(InfoKind.Caution, UiText.T("Не удалось создать ZIP."), ex.Message, 5000);
        }
        finally
        {
            if (!copied && archive != null)
                try { File.Delete(archive); Directory.Delete(Path.GetDirectoryName(archive)!); } catch { }
            item.Archiving = false;
            _archiveCancel = null;
            _panel.SetArchiveProgress(null);
        }
    }

    async void OnExtractAudioRequested(ClipItem item, AudioExtractor.Track track)
    {
        using var blobFiles = _store.PreserveBlobFiles();
        if (item.MediaKind != "video" || item.ExtractingAudio) return;
        item.ExtractingAudio = true;
        try
        {
            var result = await AudioExtractor.ExtractAsync(item, _store.ImagesDir, track, _storage.MaxFileBytes, _audioCancel.Token);
            _audioCancel.Token.ThrowIfCancellationRequested();
            _store.AddMedia(result.Hash, "audio", result.FileName, result.Size, false, DateTime.UtcNow);
            var audio = _store.Items.First(i => i.ImageHash == result.Hash);
            try { await Paster.SetClipboardAsync(audio, false, _store.ImagesDir); }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log(ex);
                Notify(InfoKind.Caution, UiText.T("Звук извлечён в историю."), UiText.T("Буфер обмена занят. Скопируйте аудиозапись из истории."));
                return;
            }
            Notify(InfoKind.Success, UiText.T("Звук извлечён и скопирован."), result.FileName);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log(ex); Notify(InfoKind.Caution, UiText.T("Не удалось извлечь звук."), ex.Message, 5000); }
        finally { item.ExtractingAudio = false; }
    }

    void OnOcrRequested(ClipItem item) => ProcessImage(item, translate: false);

    async void ProcessImage(ClipItem item, bool translate)
    {
        using var blobFiles = _store.PreserveBlobFiles();
        if (!item.IsImage || item.ImageHash == null || !_store.Items.Contains(item)) return;
        if (_ocrCancel != null) { Notify(InfoKind.Info, UiText.T("OCR уже работает."), UiText.T("Дождитесь результата или отмените текущую обработку.")); return; }
        using var cancel = new CancellationTokenSource();
        _ocrCancel = cancel;
        item.Recognizing = true;
        _panel.SetOcrProgress(cancel.Cancel);
        uint sequence = Native.GetClipboardSequenceNumber();
        int pasteVersion = _pasteVersion;
        string hash = item.ImageHash;
        bool Current() => _started && !cancel.IsCancellationRequested && _store.Items.Contains(item) && item.ImageHash == hash;
        byte[]? image = null;
        string failureTitle = translate ? UiText.T("Не удалось перевести текст.") : UiText.T("Не удалось распознать текст.");
        try
        {
            if ((long)item.ImageWidth * item.ImageHeight > _storage.MaxImagePixels)
                throw new ScreenshotOcr.Failure(UiText.T("Изображение превышает лимит размера в настройках хранения."));
            image = await ScreenshotOcr.ReadImageAsync(_store.ImagesDir, hash, cancel.Token);
            if (!Current()) return;
            var result = await _ocr.RecognizeAsync(image, _storage.MaxImagePixels, cancel.Token);
            if (!Current()) return;
            if (string.IsNullOrWhiteSpace(result.Text)) { Notify(InfoKind.Info, UiText.T("Текст на изображении не найден."), UiText.T("Попробуйте другой снимок или более крупный текст.")); return; }
            ClipItem output;
            if (translate)
            {
                _panel.SetOcrProgress(cancel.Cancel, translating: true);
                var labels = await _compressor.TranslateRegionsAsync(result.Regions, cancel.Token);
                if (!Current()) return;
                int thumbnailPixels = _storage.ThumbnailPixels;
                long maxPixels = _storage.MaxImagePixels;
                var rendered = await Task.Run(() =>
                {
                    var bitmap = TranslatedImage.Render(image, result, labels, maxPixels, cancel.Token);
                    var normalized = ImageFiles.Normalize(bitmap);
                    cancel.Token.ThrowIfCancellationRequested();
                    ImageFiles.Write(_store.ImagesDir, normalized.hash, normalized.bgra, thumbnailPixels);
                    return (Hash: normalized.hash, Width: bitmap.PixelWidth, Height: bitmap.PixelHeight);
                }, cancel.Token);
                if (!Current()) return;
                _store.AddImage(rendered.Hash, rendered.Width, rendered.Height, DateTime.UtcNow, sourceApp: item.SourceApp);
                output = _store.Items.First(i => i.ImageHash == rendered.Hash);
            }
            else
            {
                if (result.Text.Length > _storage.MaxChars) throw new ScreenshotOcr.Failure(UiText.T("Полученный текст превышает лимит хранения. Он не был обрезан; увеличьте лимит в настройках."));
                _store.Add(result.Text, null, null, DateTime.UtcNow, item.SourceApp);
                bool singleLine = result.Text.IndexOfAny(['\r', '\n', '\t']) < 0;
                output = _store.Items.Where(i => !i.IsImage && !i.IsMedia && (i.Text == result.Text || singleLine && i.Text.Trim() == result.Text))
                    .OrderByDescending(i => i.LastUsedAt).First();
            }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(image);
            image = null;
            try { await _store.SaveAsync(); }
            catch (Exception ex)
            {
                Log(ex);
                if (Current()) Notify(InfoKind.Caution, UiText.T("Результат не сохранён на диск."), UiText.T("Он остаётся в истории этой сессии. Повторите обработку, чтобы сохранить его."), 5000);
                return;
            }
            if (!Current()) return;
            bool copied = false;
            bool MayCopy() => Current() && _store.Items.Contains(output) && sequence != 0
                && sequence == Native.GetClipboardSequenceNumber() && pasteVersion == _pasteVersion;
            // A new copy or paste while OCR was running belongs to the user, not to this result.
            if (MayCopy())
            {
                try
                {
                    if (translate) await Paster.SetClipboardAsync(output, false, _store.ImagesDir, MayCopy);
                    else Paster.CopyText(result.Text, excludeFromHistory: true);
                    copied = true;
                }
                catch (OperationCanceledException) { }
                catch (System.Runtime.InteropServices.ExternalException) { }
            }
            if (!Current()) return;
            if (translate && _panel.IsShown) _panel.ShowTranslatedImageResult(copied, result.Regions.Any(r => r.Confidence < .85));
            else if (_panel.IsShown) _panel.ShowOcrResult(output, result, copied);
            else Notify(result.ReviewNeeded || !copied ? InfoKind.Caution : InfoKind.Success,
                translate ? (copied ? UiText.T("Картинка с переводом скопирована.") : UiText.T("Картинка с переводом в истории.")) : copied ? UiText.T("Текст распознан и скопирован.") : UiText.T("Распознанный текст в истории."),
                result.ReviewNeeded ? UiText.T("Проверьте похожие буквы и порядок строк.") : UiText.T("Откройте историю по Win+V."));
        }
        catch (OperationCanceledException) { }
        catch (ScreenshotOcr.Failure ex) { if (Current()) Notify(InfoKind.Caution, failureTitle, ex.Message, 5000); }
        catch (PromptCompressor.Failure ex) { Log(ex); if (Current()) Notify(InfoKind.Caution, failureTitle, ex.Message, 5000); }
        catch (Exception ex) { Log(ex); if (Current()) Notify(InfoKind.Caution, failureTitle, UiText.T("Исходное изображение осталось в истории."), 5000); }
        finally
        {
            if (image != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(image);
            item.Recognizing = false;
            _ocrCancel = null;
            _panel.SetOcrProgress(null);
        }
    }

    async void OnCompressRequested(ClipItem item)
    {
        if (item.IsFolder || item.Compressing) return;
        if (_archiveCancel != null) { ++_pasteVersion; _archiveCancel.Cancel(); }
        item.Compressing = true;
        try
        {
            var compressed = await _compressor.CompressAsync(item.Text);
            if (compressed.Length == 0 || compressed.Length >= item.Text.Length)
            {
                Notify(InfoKind.Info, UiText.T("Промпт уже компактный."), UiText.T("Короче не получилось."));
                return;
            }
            _store.Add(compressed, null, null, DateTime.UtcNow, sensitive: item.IsSensitive);
            try { Paster.CopyText(compressed, sensitive: item.IsSensitive); }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log(ex);
                Notify(InfoKind.Caution, UiText.T("Сжатый промпт в истории,"), UiText.T("но буфер обмена занят."));
                return;
            }
            Notify(InfoKind.Success, UiText.T("Промпт сжат:"), UiText.F($"{item.Text.Length:N0} → {compressed.Length:N0} символов. Уже в буфере обмена."));
        }
        catch (PromptCompressor.Failure ex)
        {
            Log(ex);
            Notify(InfoKind.Critical, UiText.T("Не удалось сжать промпт."), ex.Message, 5000);
        }
        catch (Exception ex)
        {
            Log(ex);
            Notify(InfoKind.Critical, UiText.T("Не удалось сжать промпт."), UiText.F($"Подробности: {Path.Combine(DataDir, "error.log")}"), 5000);
        }
        finally { item.Compressing = false; }
    }

    async void OnTranslateTextRequested(ClipItem item, System.Globalization.CultureInfo target)
    {
        if (item.Compressing) return;
        item.Compressing = true; // одна операция модели на запись: сжатие и перевод не пересекаются
        using var cancel = new CancellationTokenSource();
        _panel.SetTextTranslationProgress(cancel.Cancel);
        try
        {
            // Запуск llama-server и ожидание модели — в пуле: панель не должна замирать даже на синхронных участках.
            string text = item.Text, language = target.EnglishName;
            var translated = await Task.Run(() => _compressor.TranslateTextAsync(text, language, cancel.Token));
            _panel.SetTextTranslationProgress(null);
            _store.Add(translated, null, null, DateTime.UtcNow, sensitive: item.IsSensitive);
            try { Paster.CopyText(translated, sensitive: item.IsSensitive); }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log(ex);
                Notify(InfoKind.Caution, UiText.T("Перевод в истории,"), UiText.T("но буфер обмена занят."));
                return;
            }
            Notify(InfoKind.Success, UiText.T("Перевод готов."), UiText.T("Перевод в истории и в буфере обмена."));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (PromptCompressor.Failure ex)
        {
            Log(ex);
            Notify(InfoKind.Critical, UiText.T("Не удалось перевести текст."), ex.Message, 5000);
        }
        catch (Exception ex)
        {
            Log(ex);
            Notify(InfoKind.Critical, UiText.T("Не удалось перевести текст."), UiText.F($"Подробности: {Path.Combine(DataDir, "error.log")}"), 5000);
        }
        finally
        {
            _panel.SetTextTranslationProgress(null);
            item.Compressing = false;
        }
    }

    void Save()
    {
        try { _store.SaveAsync().ContinueWith(t => Log(t.Exception!), TaskContinuationOptions.OnlyOnFaulted); }
        catch (Exception ex) { Log(ex); } // снимок не сериализовался
    }

    // ---------- Трей ----------

    Forms.NotifyIcon CreateTray()
    {
        _trayMenu = CreateTrayMenu();
        var tray = new Forms.NotifyIcon { Text = "CopyBuffalo: Win+V", Icon = CreateTrayIcon(), Visible = true };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenFromTray();
            else if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu();
        };
        return tray;
    }

    void OpenFromTray()
    {
        _target = IntPtr.Zero;
        var (a, _) = CaretLocator.Locate(IntPtr.Zero);
        _panel.ShowAt(a);
    }

    /// <summary>Меню трея в стиле панели: шапка со статусом записи, пункты с галочками, тема и акцент системы.</summary>
    ContextMenu CreateTrayMenu()
    {
        var menu = new ContextMenu();
        _trayStatus = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        _trayStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var header = new StackPanel { Children = { new TextBlock { Text = "CopyBuffalo", FontSize = 13, FontWeight = FontWeights.SemiBold }, _trayStatus } };
        menu.Items.Add(new MenuItem { Header = header, Focusable = false, IsHitTestVisible = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(PanelWindow.Entry(UiText.T("Открыть историю"), "\uE77F", OpenFromTray, "Win+V"));
        _pauseItem = PanelWindow.Entry(UiText.T("Пауза записи"), "", () => SetPaused(!_watcher.Paused));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        _autostartItem = PanelWindow.Entry(UiText.T("Запускать вместе с Windows"), "", () => SetAutostart(!IsAutostart()));
        menu.Items.Add(_autostartItem);
        menu.Items.Add(PanelWindow.Entry(UiText.T("Настройки"), "\uE713", () => { OpenFromTray(); _panel.OpenSettings(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(PanelWindow.Entry(UiText.T("Очистить историю…"), "\uE74D", () => { OpenFromTray(); _panel.AskClear(); }));
        menu.Items.Add(PanelWindow.Entry(UiText.T("Перезапустить"), "\uE72C", Restart));
        menu.Items.Add(PanelWindow.Entry(UiText.T("Выход"), "\uE7E8", Shutdown));
        Motion.AnimateOpen(menu, 8); // меню трея открывается над значком — выезжает вверх
        UpdateTrayStatus();
        return menu;
    }

    void ShowTrayMenu()
    {
        _autostartItem.IsChecked = IsAutostart();
        UpdateTrayStatus();
        _trayMenu.Placement = PlacementMode.MousePoint;
        _trayMenu.IsOpen = true;
        // Без переднего плана меню не закрывается кликом мимо: окно-владелец значка трея чужое для WPF.
        if (PresentationSource.FromVisual(_trayMenu) is HwndSource source) Native.SetForegroundWindow(source.Handle);
    }

    void UpdateTrayStatus()
    {
        bool paused = _watcher.Paused;
        _trayStatus.Text = paused ? UiText.T("Запись на паузе") : UiText.T("Запись идёт");
        _pauseItem.IsChecked = paused;
    }

    /// <summary>Пауза записи — видна везде: полоса в панели, бейдж на значке, подсказка и статус в меню трея.</summary>
    void SetPaused(bool paused)
    {
        _watcher.Paused = paused;
        _tray.Icon = paused ? PausedTrayIcon() : CreateTrayIcon();
        _tray.Text = paused ? UiText.T("CopyBuffalo: запись на паузе") : "CopyBuffalo: Win+V";
        UpdateTrayStatus();
        _panel.SetPaused(paused);
    }

    static System.Drawing.Icon? _pausedIcon;

    /// <summary>Значок на паузе: приглушённый, с жёлтым бейджем паузы в правом нижнем углу.</summary>
    static System.Drawing.Icon PausedTrayIcon()
    {
        if (_pausedIcon != null) return _pausedIcon;
        using var source = new System.Drawing.Icon(CreateTrayIcon(), Forms.SystemInformation.SmallIconSize);
        using var bitmap = source.ToBitmap();
        int w = bitmap.Width, h = bitmap.Height;
        using var result = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(result))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var dim = new System.Drawing.Imaging.ImageAttributes();
            dim.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.55f });
            g.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, w, h), 0, 0, w, h, System.Drawing.GraphicsUnit.Pixel, dim);
            float d = w * 0.58f, x = w - d, y = h - d, bar = d * 0.15f, gap = d * 0.16f, bh = d * 0.46f;
            using var yellow = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(252, 225, 0));
            using var dark = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(26, 26, 26));
            g.FillEllipse(yellow, x, y, d, d);
            g.FillRectangle(dark, x + d / 2 - gap / 2 - bar, y + (d - bh) / 2, bar, bh);
            g.FillRectangle(dark, x + d / 2 + gap / 2, y + (d - bh) / 2, bar, bh);
        }
        IntPtr handle = result.GetHicon();
        try { _pausedIcon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { Native.DestroyIcon(handle); }
        return _pausedIcon;
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

    /// <summary>Галочка — только если в автозапуске именно этот EXE: после переноса файла там остаётся старый путь.</summary>
    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("ArkCopyBuffer") is string path
            && string.Equals(path.Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
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
                ["DialogSurface"] = 0xFF2B2B2B, ["SliderThumbFill"] = 0xFF454545, ["TextOnAccent"] = 0xFF000000,
                ["CautionFill"] = 0xFF433519, ["CautionIcon"] = 0xFFFCE100, ["SuccessFill"] = 0xFF393D1B, ["SuccessIcon"] = 0xFF6CCB5F,
                ["DropValueText"] = 0xFF65F0A0, ["DropValueFill"] = 0xFF203C2D,
                ["CriticalFill"] = 0xFF442726, ["CriticalIcon"] = 0xFFFF99A4, ["CriticalText"] = 0xFFFF99A4, ["InfoGlyph"] = 0xFF000000,
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
                ["DialogSurface"] = 0xFFFFFFFF, ["SliderThumbFill"] = 0xFFFFFFFF, ["TextOnAccent"] = 0xFFFFFFFF,
                ["CautionFill"] = 0xFFFFF4CE, ["CautionIcon"] = 0xFF9D5D00, ["SuccessFill"] = 0xFFDFF6DD, ["SuccessIcon"] = 0xFF0F7B0F,
                ["DropValueText"] = 0xFF0B6E3B, ["DropValueFill"] = 0xFFE3F4E9,
                ["CriticalFill"] = 0xFFFDE7E9, ["CriticalIcon"] = 0xFFC42B1C, ["CriticalText"] = 0xFFC42B1C, ["InfoGlyph"] = 0xFFFFFFFF,
            };

        foreach (var (name, argb) in t)
            Resources[name] = Frozen(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        Resources["Accent"] = Frozen(accent);
        Resources["AccentRing"] = Frozen(Color.FromArgb(0x99, accent.R, accent.G, accent.B)); // обводка выбранной записи
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

    /// <summary>Из любого потока (фоновое сохранение). Время местное: формат "u" дописывал Z, будто это UTC.</summary>
    internal static void Log(Exception ex)
    {
        lock (TraceLock)
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "error.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\n\n");
            }
            catch { }
    }
}
