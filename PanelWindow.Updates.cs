using System.Windows;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    Info? _updateInfo;
    internal bool UpdateBlocked => Busy || SeriesActive || _mergeTextFilter;
    public event Action<bool>? AutoUpdateChanged;
    public event Action? UpdateRequested;
    internal bool AutoUpdate
    {
        get => AutoUpdateToggle.IsChecked == true;
        set => AutoUpdateToggle.IsChecked = value;
    }
    internal void SetUpdateStatus(string status, bool ready, bool busy)
    {
        UpdateStatus.Text = status;
        UpdateButton.Content = ready ? "Обновить и перезапустить" : "Проверить обновления";
        UpdateButton.IsEnabled = !busy;
        UpdateVersion.Text = $"ArkCopyBuffer {AppUpdater.VersionText}";
        _updateInfo = ready ? new Info(InfoKind.Info, "Доступно обновление.", status,
            ("Обновить и перезапустить", () => UpdateRequested?.Invoke())) : null;
        RefreshInfo();
    }
    void AutoUpdateToggle_Changed(object sender, RoutedEventArgs e) => AutoUpdateChanged?.Invoke(AutoUpdate);
    void UpdateButton_Click(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke();
}
