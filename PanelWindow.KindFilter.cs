using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    static readonly (string? Key, string Label, string Glyph)[] KindFilters =
    [
        (null, UiText.T("Все"), "\uE71D"), ("text", UiText.T("Текст"), "\uE8A5"), ("image", UiText.T("Картинки"), "\uE91B"),
        ("video", UiText.T("Видео"), "\uE714"), ("audio", UiText.T("Аудио"), "\uE8D6"), ("files", UiText.T("Файлы"), "\uE8B7"),
        ("folder", UiText.T("Папки"), "\uE8B7"), ("graph", UiText.T("Схемы"), "\uE8A7"), ("other", UiText.T("Другое"), "\uE712")
    ];
    string? _kindFilter;

    static bool MatchesKind(ClipItem item, string? kind) => kind switch
    {
        null => true,
        "text" => !item.IsImage && !item.IsMedia && !item.IsNodeGraph,
        "image" => item.IsImage,
        "video" or "audio" or "folder" => item.MediaKind == kind,
        "files" => item.MediaKind is "text" or "files",
        "graph" => !item.IsImage && !item.IsMedia && item.IsNodeGraph,
        "other" => item.IsMedia && item.MediaKind is not ("video" or "audio" or "folder" or "text" or "files"),
        _ => false
    };

    void Kind_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = KindButton, Placement = PlacementMode.MousePoint };
        foreach (var kind in KindFilters)
        {
            var entry = Entry(kind.Key == null ? UiText.T("Все — без фильтра") : kind.Label, kind.Glyph, () => SetKindFilter(kind.Key));
            entry.IsChecked = _kindFilter == kind.Key;
            menu.Items.Add(entry);
        }
        menu.Closed += (_, _) => Search.Focus();
        OpenMenu(menu);
    }

    void SetKindFilter(string? kind)
    {
        if (!KindFilters.Any(option => option.Key == kind)) throw new ArgumentException(UiText.T("Неизвестный вид записи."), nameof(kind));
        _kindFilter = kind;
        string label = KindFilters.First(option => option.Key == kind).Label;
        KindButton.Content = (kind == null ? UiText.T("Виды") : label) + " ▾";
        KindButton.ToolTip = kind == null ? UiText.T("Вид записи: все, без фильтра") : UiText.T("Вид записи: ") + label;
        AutomationProperties.SetName(KindButton, UiText.T("Вид записи: ") + label);
        RefreshViews();
    }

    internal static void SelfTestKindFilter(string directory, Action<bool, string> check)
    {
        var samples = new (string Kind, ClipItem Item)[]
        {
            ("text", new() { Text = "needle text", SourceApp = "alpha" }),
            ("image", new() { ImageHash = "IMAGE", SourceApp = "alpha" }),
            ("text", new() { Text = "https://example.test/", SourceApp = "beta" }),
            ("image", new() { ImageHash = "GIF", IsGif = true, SourceApp = "beta" }),
            ("text", new() { Text = @"D:\sample\path.txt" }),
            ("text", new() { Text = "#AABBCC" }),
            ("video", new() { MediaKind = "video", ImageHash = "VIDEO" }),
            ("audio", new() { MediaKind = "audio", ImageHash = "AUDIO" }),
            ("files", new() { MediaKind = "text", ImageHash = "TEXTFILE", FileName = "note.txt" }),
            ("files", new() { MediaKind = "files", ImageHash = "FILES", FileCount = 2 }),
            ("folder", new() { MediaKind = "folder", Text = @"D:\folder" }),
            ("graph", new() { Text = "Begin Object Class=/Script/UnrealEd.MaterialGraphNode\nCustomProperties Pin" }),
            ("other", new() { MediaKind = "binary", ImageHash = "UNKNOWN" })
        };
        var store = new HistoryStore(System.IO.Path.Combine(directory, "kind-filter", "history.dat"));
        foreach (var sample in samples) store.Items.Add(sample.Item);
        var panel = new PanelWindow(store, new SymbolCatalog(System.IO.Path.Combine(directory, "kind-filter", "symbols.json")));
        try
        {
            check(KindFilters.Take(3).Select(k => k.Key).SequenceEqual([null, "text", "image"]) && panel._kindFilter == null,
                "виды: порядок Все → Текст → Картинки и отсутствие фильтра по умолчанию");
            foreach (var kind in KindFilters)
            {
                panel.SetKindFilter(kind.Key);
                var expected = samples.Where(s => kind.Key == null || s.Kind == kind.Key).Select(s => s.Item);
                check(panel.List.Items.Cast<ClipItem>().SequenceEqual(expected), "виды: " + kind.Label + " фильтрует без перестановки истории");
            }
            panel.SetKindFilter("image"); panel.SetSourceFilter("ALPHA");
            check(panel.List.Items.Cast<ClipItem>().SequenceEqual([samples[1].Item]), "виды: фильтр картинок сочетается с приложением без учёта регистра");
            panel.Search.Text = "needle";
            check(panel.List.Items.Count == 0 && panel.Empty.Visibility == Visibility.Visible, "виды: поиск не отключает выбранный вид, пустой результат показан явно");
            panel.SetKindFilter("text");
            check(panel.List.Items.Cast<ClipItem>().SequenceEqual([samples[0].Item]), "виды: текст, приложение и поиск применяются одновременно");
            panel.Search.Text = ""; panel.SetSourceFilter(null); panel.SetKindFilter("image");
            panel.ToggleSeriesItem(samples[1].Item);
            panel.SetKindFilter("text");
            check(panel._seriesQueue.SequenceEqual([samples[1].Item]) && samples[1].Item.InSeries && panel.Search.IsEnabled,
                "выбор: смена вида и поиск сохраняют скрытые отметки");
            panel.SetKindFilter("image");
            store.TogglePin(samples[1].Item); panel.TabPinned.IsChecked = true; panel.RefreshViews();
            check(panel.PinnedList.Items.OfType<ClipItem>().SequenceEqual([samples[1].Item]) && samples[1].Item.InSeries,
                "виды: закреплённые поддерживают общий фильтр и сохраняют отметку");
            bool invalid = false;
            try { panel.SetKindFilter("invalid"); } catch (ArgumentException) { invalid = true; }
            check(invalid && panel._kindFilter == "image", "виды: неизвестный фильтр не меняет состояние");
            panel.EndImageSeries(); panel.SetKindFilter(null); panel.TabAll.IsChecked = true;
            check(panel.List.Items.Count == samples.Length && panel._kindFilter == null, "виды: Все возвращает полную историю");
        }
        finally { panel.EndImageSeries(); panel.Close(); }
    }
}
