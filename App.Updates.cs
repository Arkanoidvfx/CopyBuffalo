using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ArkCopyBuffer;

public partial class App
{
    AppUpdater? _updater;
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    readonly DispatcherTimer _updateIdleTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    DateTime _updateHiddenSince = DateTime.UtcNow;
    bool _installingUpdate, _autoUpdateFailed;

    void InitializeUpdates()
    {
        _updater = new AppUpdater(Path.Combine(DataDir, "updates"));
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
            _panel.AutoUpdate = key?.GetValue("AutoUpdate") is not int value || value != 0;
        _panel.AutoUpdateChanged += enabled =>
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
                key.SetValue("AutoUpdate", enabled ? 1 : 0, RegistryValueKind.DWord);
                _autoUpdateFailed = false;
                _updateHiddenSince = DateTime.UtcNow;
            }
            catch (Exception ex) { Notify(InfoKind.Caution, UiText.T("Настройка не сохранена."), ex.Message); }
        };
        _updater.Changed += () => _panel.SetUpdateStatus(
            (_autoUpdateFailed ? UiText.T("Автоустановка приостановлена после ошибки. ") : "") + _updater.Status,
            _updater.Ready != null, _updater.Busy || _installingUpdate);
        _panel.UpdateRequested += async () =>
        {
            if (_updater.Ready == null) await _updater.CheckAsync();
            else await InstallUpdateAsync(automatic: false);
        };
        _updateTimer.Tick += async (_, _) => { if (!_installingUpdate) await _updater.CheckAsync(); };
        _updateIdleTimer.Tick += async (_, _) =>
        {
            if (_panel.IsVisible || UpdateOperationActive) _updateHiddenSince = DateTime.UtcNow;
            if (_panel.AutoUpdate && !_autoUpdateFailed && _updater.Ready != null && !_updater.Busy &&
                !_panel.IsVisible && !UpdateOperationActive && DateTime.UtcNow - _updateHiddenSince >= TimeSpan.FromSeconds(30))
                await InstallUpdateAsync(automatic: true);
        };
        string error = Path.Combine(DataDir, "updates", "install-error.txt");
        if (File.Exists(error))
        {
            _autoUpdateFailed = true;
            _panel.SetUpdateStatus(UiText.T("Предыдущее обновление не установилось. Проверьте вручную; подробности в updates/install-error.txt."), false, false);
        }
        _updateTimer.Start();
        _updateIdleTimer.Start();
        _ = _updater.CheckAsync();
    }

    bool UpdateOperationActive => _watcher.Capturing || _panel.UpdateBlocked || _archiveCancel != null || _ocrCancel != null ||
        _store.Items.Any(item => item.Compressing || item.ExtractingAudio || item.Recognizing);

    internal static bool CanApplyUpdate(bool automatic, bool enabled, bool visible, bool operationActive) =>
        !operationActive && (!automatic || enabled && !visible);

    async Task InstallUpdateAsync(bool automatic)
    {
        if (_installingUpdate || _updater == null || _updater.Busy || _updater.Ready == null) return;
        if (UpdateOperationActive)
        {
            if (!automatic) Notify(InfoKind.Info, UiText.T("Завершите текущую операцию."), UiText.T("Затем нажмите «Обновить и перезапустить» ещё раз."));
            return;
        }
        _installingUpdate = true;
        try
        {
            await _updater.CheckAsync(); // Use a newer release if one appeared while the app was in the tray.
            if (_updater.Ready == null) return;
            await _updater.PrepareInstallAsync();
            if (!CanApplyUpdate(automatic, _panel.AutoUpdate, _panel.IsVisible, UpdateOperationActive)) return;
            _store.Save(); // A failed save must leave the application running.
            using var helper = await _updater.StartInstallerAsync();
            // The panel may reopen while PowerShell starts; never close newly active work.
            if (!CanApplyUpdate(automatic, _panel.AutoUpdate, _panel.IsVisible, UpdateOperationActive))
            {
                helper.Kill();
                return;
            }
            Shutdown();
        }
        catch (Exception ex)
        {
            _autoUpdateFailed = true;
            Notify(InfoKind.Caution, UiText.T("Обновление не установлено."), ex.Message);
        }
        finally
        {
            _installingUpdate = false;
            _panel.SetUpdateStatus(_updater.Status, _updater.Ready != null, _updater.Busy);
        }
    }
}
