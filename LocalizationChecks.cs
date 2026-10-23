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
            string[] Slots(string value) => Regex.Matches(value, @"(?<!\{)\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})").Select(m => m.Groups[1].Value).Order().ToArray();
            Require(Slots(source).SequenceEqual(Slots(en[key])), "Format arguments preserved: " + key);
        }
        Require(UiText.T("Буфер") == (UiText.IsEnglish ? "Clipboard" : "Буфер"), "Selected language is used");
        Require(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == UiText.Language, "Built-in WPF controls use the selected UI culture");
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
        const string content = "Пользовательский текст — не переводить. 日本語 🙂";
        unicode.Add(content, null, null, DateTime.UtcNow, "Demo"); unicode.Save();
        var loaded = new HistoryStore(Path.Combine(data, "unicode.dat")); loaded.Load(DateTime.UtcNow);
        Require(loaded.Items.Single().Text == content, "User content remains unchanged in either UI language");
        File.WriteAllText(Path.Combine(directory, "PASS.txt"), $"PASS: {en.Count} resource pairs, placeholders, language selection, UI renders and synthetic history roundtrip ({UiText.Language}). No window shown or real clipboard used.");
        string storePath() => Path.Combine(data, "history.dat");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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
            LocalizationChecks.Require(Records(1) == (UiText.IsEnglish ? "entry" : "запись") && Records(21) == (UiText.IsEnglish ? "entries" : "запись"), "Entry plurals");
            LocalizationChecks.Require(LinesWord(1) == (UiText.IsEnglish ? "line" : "строка") && LinesWord(21) == (UiText.IsEnglish ? "lines" : "строка"), "Line plurals");
            LocalizationChecks.Save(panel.Root, 392, 492, Path.Combine(directory, "history.png"));
            LocalizationChecks.Require(panel.TabAll.Content.ToString() == UiText.T("Буфер"), "History tab label");
            panel.Search.ApplyTemplate();
            var clear = (Button)panel.Search.Template.FindName("PART_Clear", panel.Search);
            LocalizationChecks.Require(clear.ToolTip?.ToString() == UiText.T("Очистить поиск (Esc)"), "Application-level styles use the selected language from startup");
            panel.OpenSettings();
            string? chosen = null;
            panel.LanguageChanged += language => { chosen = language; panel.SetLanguagePreference(language); };
            var target = UiText.IsEnglish ? panel.RussianLanguage : panel.EnglishLanguage;
            target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LocalizationChecks.Require(chosen == (UiText.IsEnglish ? "ru" : "en"), "Language selection emits preference without restarting");
            panel.SetLanguagePreference(UiText.Language);
            LocalizationChecks.Save(panel.Root, 392, 492, Path.Combine(directory, "language.png"));
            panel.AutoUpdate = true;
            panel.SetUpdateStatus(UiText.T("У вас последняя версия."), false, false);
            panel.VisualSettingsView.ScrollToEnd(); panel.Root.UpdateLayout();
            panel.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            panel.VisualSettingsView.ScrollToVerticalOffset(panel.VisualSettingsView.ExtentHeight); panel.Root.UpdateLayout();
            LocalizationChecks.Save(panel.Root, 392, 492, Path.Combine(directory, "updates.png"));
            var button = panel.UpdateButton.TranslatePoint(new Point(), panel.Root);
            LocalizationChecks.Require(button.Y >= 0 && button.Y + panel.UpdateButton.ActualHeight <= panel.Root.ActualHeight, "Update button fits");
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
            LocalizationChecks.Require(window.Title == UiText.T("Редактировать текст — ArkCopyBuffer") && window.Editor.Text == item.Text, "Editor title is localized without changing the entry");
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
            LocalizationChecks.Require(window.Title == UiText.T("Объединить тексты — ArkCopyBuffer"), "Merge window title");
        }
        finally { window.Close(); }
    }
}
