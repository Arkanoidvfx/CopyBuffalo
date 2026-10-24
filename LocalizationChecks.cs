using System.Collections;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

internal static class LocalizationChecks
{
    internal static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var english = UiText.Resources.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true)!;
        var russian = UiText.Resources.GetResourceSet(CultureInfo.GetCultureInfo("ru"), true, true)!;
        var en = english.Cast<DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string)p.Value!);
        var ru = russian.Cast<DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string)p.Value!);
        Require(en.Count > 500 && en.Keys.ToHashSet().SetEquals(ru.Keys), "Both languages contain the complete catalog");
        foreach (var (key, source) in ru)
        {
            Require(key == UiText.Key(source), "Stable resource key: " + key);
            Require(en[key].Length > 0 && !Regex.IsMatch(en[key], "[А-Яа-яЁё]"), "English translation: " + key);
            string[] Slots(string value) => Regex.Matches(value, @"(?<!\{)\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})").Select(m => m.Value).Order().ToArray();
            Require(Slots(source).SequenceEqual(Slots(en[key])), "Format arguments preserved: " + key);
        }
        foreach (string language in UiText.Languages)
        {
            var catalog = UiText.Resources.GetResourceSet(language == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language), true, false)
                ?? throw new InvalidOperationException("Missing language catalog: " + language);
            var entries = catalog.Cast<DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string)p.Value!);
            Require(en.Keys.ToHashSet().SetEquals(entries.Keys), "Complete catalog: " + language);
            foreach (var (key, text) in entries)
            {
                Require(!string.IsNullOrWhiteSpace(text), "Nonempty translation: " + language + ":" + key);
                string[] Slots(string value) => Regex.Matches(value, @"(?<!\{)\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})").Select(m => m.Value).Order().ToArray();
                Require(Slots(en[key]).SequenceEqual(Slots(text)), "Format arguments: " + language + ":" + key);
            }
        }
        string expectedClipboard = UiText.Language switch { "en" => "Clipboard", "ru" => "Буфер", "zh-Hans" => "剪贴板", "zh-Hant" => "剪貼簿", "ja" => "クリップボード", "hi" => "क्लिपबोर्ड", _ => throw new InvalidOperationException() };
        Require(UiText.T("Буфер") == expectedClipboard, "Selected language is used");
        Require(CultureInfo.CurrentUICulture.Name == UiText.Language, "Built-in WPF controls use the selected UI culture");
        CheckFonts();
        var data = Path.Combine(directory, "synthetic");
        var store = new HistoryStore(Path.Combine(data, "history.dat"));
        var now = DateTime.Today.AddHours(14).AddMinutes(30).ToUniversalTime();
        foreach (var text in new[] { "Thanks! I'll send the updated files tomorrow.", @"D:\Projects\Demo\exports\final-cut.mp4", "ffmpeg -i input.mp4 -vn audio.wav", "Weekend plans\nPick up groceries\nBack up project files\nCall Alex" })
            store.Add(text, null, null, now = now.AddMinutes(1), "Demo");
        store.Save();
        var before = SHA256.HashData(File.ReadAllBytes(storePath()));
        PanelWindow.CheckLocalization(store, directory);
        TextMergeWindow.RenderLocalization(store, directory);
        Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(storePath()))), "Rendering and language selection do not change saved history");
        string snapshot = JsonSerializer.Serialize(new { store.Items, store.Groups });
        TextEditorWindow.RenderLocalization(store, directory);
        var editorSaved = new HistoryStore(storePath()); editorSaved.Load(DateTime.UtcNow);
        Require(snapshot == JsonSerializer.Serialize(new { editorSaved.Items, editorSaved.Groups }), "Closing the editor preserves history content and metadata");
        var unicode = new HistoryStore(Path.Combine(data, "unicode.dat"));
        const string content = "Пользовательский текст — не переводить. 中文測試 日本語 हिन्दी क्षत्रिय प्रशिक्षण 🙂";
        unicode.Add(content, null, null, DateTime.UtcNow, "Demo"); unicode.Save();
        var loaded = new HistoryStore(Path.Combine(data, "unicode.dat")); loaded.Load(DateTime.UtcNow);
        Require(loaded.Items.Single().Text == content, "User content remains unchanged in either UI language");
        File.WriteAllText(Path.Combine(directory, "PASS.txt"), $"PASS: {en.Count} strings in {UiText.Languages.Length} catalogs, format specifiers, bundled fonts, selection, UI renders and synthetic history ({UiText.Language}). No window shown or real clipboard used.");
        string storePath() => Path.Combine(data, "history.dat");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void CheckFonts()
    {
        var faces = new List<GlyphTypeface>();
        foreach (var family in new[] { "IBM Plex Sans", "IBM Plex Mono", "Noto Sans CJK SC", "Noto Sans CJK TC", "Noto Sans CJK JP", "IBM Plex Sans Devanagari" })
        {
            var font = new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#" + family);
            foreach (var weight in new[] { FontWeights.Normal, family.StartsWith("Noto", StringComparison.Ordinal) ? FontWeights.Bold : FontWeights.SemiBold })
            {
                Require(new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out var glyph)
                    && glyph.FontUri.Scheme == "pack" && glyph.FamilyNames.Values.Contains(family), "Bundled font face: " + family);
                faces.Add(glyph!);
            }
        }
        foreach (string symbolFamily in new[] { "Segoe UI", "Segoe UI Symbol" })
            if (new Typeface(symbolFamily).TryGetGlyphTypeface(out var symbols)) faces.Add(symbols);
        var values = UiText.Resources.GetResourceSet(CultureInfo.GetCultureInfo(UiText.Language), true, true)!
            .Cast<DictionaryEntry>().Select(p => (string)p.Value!).Concat(UiText.Languages.Select(l => CultureInfo.GetCultureInfo(l).NativeName));
        foreach (var rune in string.Concat(values).EnumerateRunes().Distinct())
        {
            if (System.Text.Rune.IsWhiteSpace(rune) || System.Text.Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            Require(faces.Any(g => g.CharacterToGlyphMap.TryGetValue(rune.Value, out ushort glyph) && glyph != 0), "UI glyph: U+" + rune.Value.ToString("X"));
        }
    }

    internal static void Save(FrameworkElement element, int width, int height, string path)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}

partial class PanelWindow
{
    internal static void CheckLocalization(HistoryStore store, string directory)
    {
        var panel = new PanelWindow(store, new SymbolCatalog(Path.Combine(directory, "synthetic", "symbols.json")));
        try
        {
            Motion.Enabled = false; panel.Root.Opacity = 1;
            LocalizationChecks.Require(panel.Title == UiText.T("Буфер обмена"), "Panel title");
            LocalizationChecks.Require(Records(1) == UiText.T("запись") && Records(21) == UiText.T(UiText.Language == "ru" ? "запись" : "записей"), "Entry plurals");
            LocalizationChecks.Require(LinesWord(1) == UiText.T("строка") && LinesWord(21) == UiText.T(UiText.Language == "ru" ? "строка" : "строк"), "Line plurals");
            LocalizationChecks.Save(panel.Root, 392, 492, Path.Combine(directory, "history.png"));
            LocalizationChecks.Require(panel.TabAll.Content.ToString() == UiText.T("Буфер"), "History tab label");
            panel.Search.ApplyTemplate();
            var clear = (Button)panel.Search.Template.FindName("PART_Clear", panel.Search);
            LocalizationChecks.Require(clear.ToolTip?.ToString() == UiText.T("Очистить поиск (Esc)"), "Application-level styles use the selected language from startup");
            var editMenu = panel.Search.ContextMenu;
            LocalizationChecks.Require(editMenu != null && editMenu.Items.OfType<MenuItem>().Any(m => m.Header?.ToString() == UiText.T("Вырезать"))
                && editMenu.Items.OfType<MenuItem>().Any(m => m.Header?.ToString() == UiText.T("Выделить всё")), "Standard editing menu is localized");
            panel.OpenSettings();
            string? chosen = null;
            panel.LanguageChanged += language => { chosen = language; panel.SetLanguagePreference(language); };
            string other = UiText.Language == "en" ? "ru" : "en";
            var target = panel.LanguageChoices.Children.OfType<Button>().Single(b => (string)b.Tag == other);
            LocalizationChecks.Require(panel.LanguageChoices.Children.Count == UiText.Languages.Length, "All language choices are reachable");
            target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LocalizationChecks.Require(chosen == other, "Language selection emits preference without restarting");
            panel.SetLanguagePreference(UiText.Language);
            LocalizationChecks.Save(panel.Root, 392, 492, Path.Combine(directory, "language.png"));
            panel.AutoUpdate = true;
            panel.SetUpdateStatus(UiText.T("У вас последняя версия."), false, false);
            LocalizationChecks.Require(panel.UpdateVersion.Text == $"CopyBuffalo {AppUpdater.VersionText}", "Update section uses the product name");
            panel.VisualSettingsView.ScrollToEnd(); panel.Root.UpdateLayout();
            panel.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            panel.VisualSettingsView.ScrollToVerticalOffset(panel.VisualSettingsView.ExtentHeight); panel.Root.UpdateLayout();
            LocalizationChecks.Save(panel.Root, 392, 492, Path.Combine(directory, "updates.png"));
            var button = panel.UpdateButton.TranslatePoint(new Point(), panel.Root);
            LocalizationChecks.Require(button.Y >= 0 && button.Y + panel.UpdateButton.ActualHeight <= panel.Root.ActualHeight, "Update button fits");
            panel.ShowStorageSettings_Click(panel.StorageSettingsTab, new RoutedEventArgs());
            LocalizationChecks.Save(panel.Root, 392, 492, Path.Combine(directory, "storage.png"));
            panel.ShowVfxSettings_Click(panel.VfxSettingsTab, new RoutedEventArgs());
            LocalizationChecks.Save(panel.Root, 392, 492, Path.Combine(directory, "vfx.png"));
        }
        finally { panel.Close(); }
    }
}

partial class TextEditorWindow
{
    internal static void RenderLocalization(HistoryStore store, string directory)
    {
        var item = store.Items.First();
        var window = new TextEditorWindow(store, item);
        bool closed = false; window.Closed += (_, _) => closed = true;
        try
        {
            LocalizationChecks.Save(window.Sheet, 1060, 640, Path.Combine(directory, "editor.png"));
            LocalizationChecks.Require(window.Title == UiText.T("Редактировать текст — CopyBuffalo") && window.Editor.Text == item.Text, "Editor title is localized without changing the entry");
        }
        finally { window.Close(); PumpUntil(() => closed); }
    }
}

partial class TextMergeWindow
{
    internal static void RenderLocalization(HistoryStore store, string directory)
    {
        var window = new TextMergeWindow(store, ["ffmpeg -i \"\" -i \"<second clip>\" -filter_complex hstack output.mp4", @"D:\Clips\left.mp4", @"D:\Clips\right.mp4", "Two camera angles, side by side."]);
        try
        {
            window._model.AssignInOrder(); window.UpdateResult();
            window.Sheet.Background = (Brush)window.FindResource("WindowSurface");
            LocalizationChecks.Save(window.Sheet, 1132, 584, Path.Combine(directory, "merge.png"));
            LocalizationChecks.Require(window.Title == UiText.T("Объединить тексты — CopyBuffalo"), "Merge window title");
        }
        finally { window.Close(); }
    }
}
