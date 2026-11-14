using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    ListCollectionView? _settingsHistory;
    UIElement? _settingsHistoryReturnFocus;
    (int Start, int Length) _settingsHistorySelection;
    bool PickingSettingsHistory => _settingsHistory != null;

    internal bool HandleSettingsHistoryHotkey(IntPtr foreground)
    {
        if (!IsShown || _hwnd != foreground || SettingsFlyout.Visibility != Visibility.Visible) return false;
        if (PickingSettingsHistory) { CloseSettingsHistory(); return true; }

        _settingsHistoryReturnFocus = Keyboard.FocusedElement is UIElement focused && focused.IsWithin(SettingsPages)
            ? focused : VisualSettingsTab;
        if (_settingsHistoryReturnFocus is TextBox field)
            _settingsHistorySelection = (field.SelectionStart, field.SelectionLength);
        _outsideTimer.Stop();
        _deactivateTimer.Stop();
        HideShortcuts();
        _settingsHistory = new ListCollectionView(_store.Items)
        {
            Filter = value => value is ClipItem item && CanCopyToSettings(item)
                && (SettingsHistorySearch.Text.Length == 0 || item.Text.Contains(SettingsHistorySearch.Text, StringComparison.OrdinalIgnoreCase))
        };
        SettingsHistoryList.ItemsSource = _settingsHistory;
        SettingsHistoryList.SelectedIndex = 0;
        SettingsPages.Visibility = Visibility.Hidden;
        SettingsHistoryPicker.Visibility = Visibility.Visible;
        SettingsHistorySearch.Focus();
        return true;
    }

    static bool CanCopyToSettings(ClipItem item) => !item.IsSensitive && !item.IsImage && !item.IsMedia && !item.IsNodeGraph && item.Text.Length > 0;

    void CloseSettingsHistory(bool restoreFocus = true)
    {
        if (!PickingSettingsHistory) return;
        _settingsHistory = null;
        SettingsHistoryList.ItemsSource = null;
        SettingsHistorySearch.Clear();
        SettingsHistoryStatus.Text = "";
        SettingsHistoryPicker.Visibility = Visibility.Collapsed;
        SettingsPages.Visibility = Visibility.Visible;
        var focused = _settingsHistoryReturnFocus;
        _settingsHistoryReturnFocus = null;
        if (!restoreFocus || focused == null) return;
        if (!focused.Focus()) VisualSettingsTab.Focus();
        if (focused is TextBox field) field.Select(_settingsHistorySelection.Start, _settingsHistorySelection.Length);
    }

    void SettingsHistorySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_settingsHistory == null) return;
        _settingsHistory.Refresh();
        SettingsHistoryList.SelectedIndex = 0;
        SettingsHistoryStatus.Text = "";
    }

    void SettingsHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsHistoryCopy != null) SettingsHistoryCopy.IsEnabled = SettingsHistoryList.SelectedItem is ClipItem;
    }

    void HandleSettingsHistoryKey(Key key, KeyEventArgs e)
    {
        if (key == Key.Escape) CloseSettingsHistory();
        else if (key == Key.Enter && Keyboard.FocusedElement is not ButtonBase)
        {
            if (!e.IsRepeat && SettingsHistoryList.SelectedItem is ClipItem item) CopySettingsHistory(item);
        }
        else if ((key is Key.Up or Key.Down) && SettingsHistorySearch.IsKeyboardFocusWithin && SettingsHistoryList.HasItems)
        {
            SettingsHistoryList.SelectedIndex = Math.Clamp(SettingsHistoryList.SelectedIndex + (key == Key.Down ? 1 : -1), 0, SettingsHistoryList.Items.Count - 1);
            SettingsHistoryList.ScrollIntoView(SettingsHistoryList.SelectedItem);
        }
        else return;
        e.Handled = true;
    }

    void SettingsHistoryList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject).FindAncestor<ListBoxItem>()?.DataContext is not ClipItem item) return;
        e.Handled = true;
        CopySettingsHistory(item);
    }

    void SettingsHistoryCopy_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsHistoryList.SelectedItem is ClipItem item) CopySettingsHistory(item);
    }

    void SettingsHistoryCancel_Click(object sender, RoutedEventArgs e) => CloseSettingsHistory();

    void CopySettingsHistory(ClipItem item)
    {
        if (!PickingSettingsHistory || !CanCopyToSettings(item) || !_store.Items.Contains(item)) return;
        try { Paster.CopyText(item.Text, excludeFromHistory: true); }
        catch (ExternalException ex)
        {
            App.Log(ex);
            SettingsHistoryStatus.Text = UiText.T("Не удалось скопировать. Повторите попытку.");
            UIElementAutomationPeer.CreatePeerForElement(SettingsHistoryStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            return;
        }
        CloseSettingsHistory();
    }
}
