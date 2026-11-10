namespace ArkCopyBuffer;

partial class PanelWindow
{
    HistoryMapWindow? _historyMap;
    void OpenHistoryMap(ClipItem item)
    {
        HideAnimated();
        if (_historyMap == null)
        {
            _historyMap = new HistoryMapWindow(_store);
            _historyMap.Closed += (_, _) => _historyMap = null;
        }
        _historyMap.OpenAt(item.Id);
    }
}
