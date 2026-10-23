using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

internal static class TypographyChecks
{
    internal static void SelfTest(string directory, Action<bool, string> check)
    {
        var sans = (FontFamily)Application.Current.FindResource("TextFont");
        var mono = (FontFamily)Application.Current.FindResource("MonoFont");
        const string alphabet = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя"
            + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
            + " .,:;!?\"'«»()[]{}<>/\\|-_+=*&%#@$^~`…–—";
        foreach (var (family, name) in new[] { (sans, "IBM Plex Sans"), (mono, "IBM Plex Mono") })
        foreach (var weight in new[] { FontWeights.Normal, FontWeights.SemiBold })
        {
            bool resolved = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out var glyph);
            check(resolved && glyph!.FontUri.Scheme == "pack" && glyph.FamilyNames.Values.Any(v => v.Contains(name, StringComparison.Ordinal))
                && glyph.Weight == weight, $"typography: {name} {weight} загружается из ресурсов с настоящим начертанием");
            check(alphabet.All(c => glyph!.CharacterToGlyphMap.TryGetValue(c, out ushort index) && index != 0),
                $"typography: {name} {weight} покрывает русский алфавит с Ё/Й, латиницу и пунктуацию команд");
            check("0O1lI".Select(c => glyph!.CharacterToGlyphMap[c]).Distinct().Count() == 5,
                $"typography: {name} {weight} различает глифы 0/O и 1/l/I");
            if (family == mono)
            {
                var widths = alphabet.Select(c => glyph!.AdvanceWidths[glyph.CharacterToGlyphMap[c]]).ToArray();
                check(widths.Max() - widths.Min() < 0.000001,
                    $"typography: {name} {weight} сохраняет равную ширину кириллицы, латиницы, цифр и пробела");
            }
        }

        // Synthetic specimens only: no Window/HWND, history, clipboard or input.
        foreach (int dpi in new[] { 96, 120, 144 }) Render(sans, mono, dpi);
        RenderPanel(directory, check);
    }

    static void Render(FontFamily sans, FontFamily mono, int dpi)
    {
        const double width = 760;
        double scale = dpi / 96.0;
        var sheet = new Grid { Width = width, Background = Ink("#808080"), UseLayoutRounding = true };
        VisualTreeHelper.SetRootDpi(sheet, new DpiScale(scale, scale));
        TextOptions.SetTextFormattingMode(sheet, TextFormattingMode.Display);
        sheet.ColumnDefinitions.Add(new ColumnDefinition());
        sheet.ColumnDefinitions.Add(new ColumnDefinition());
        for (int column = 0; column < 2; column++)
        {
            bool dark = column == 0;
            var ink = Ink(dark ? "#F4F4F4" : "#242424");
            var caption = Ink(dark ? "#BEBEBE" : "#5B5B5B");
            var card = Ink(dark ? "#2D2D2D" : "#FFFFFF");
            var green = Ink(dark ? "#65F0A0" : "#0B6E3B");
            var panel = new StackPanel { Margin = new Thickness(16) };
            var surface = new Border { Background = Ink(dark ? "#202020" : "#F5F5F5"), Child = panel };
            Grid.SetColumn(surface, column); sheet.Children.Add(surface);
            panel.Children.Add(Text($"{(dark ? "Тёмная" : "Светлая")} тема · {scale * 100:0}%", sans, caption, 12));
            panel.Children.Add(Text("Буфер    Закреплённые    Символы", sans, ink, bold: true));

            void Card(TextBlock content) => panel.Children.Add(new Border
            {
                Background = card, CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 8, 0, 0), Child = content
            });
            Card(Text("Setting breakpad minidump AppID =\n2394010\nGame version is v1.0.5.102999", sans, ink));
            Card(Text("Выберите место для вставки пути.\nСъешь ещё этих мягких булок.\nЁж, йод, щука · i'll tweak it", sans, ink));
            Card(Text("[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(Difficulty=None)\n0O 1lI  [] {} ()  -- == != ->", mono, ink));
            var command = Text("", mono, ink);
            command.Inlines.Add(new Run(".\\build\\mpv.exe --config-dir=portable_config --log-file=.tmp\\osc-time.log "));
            command.Inlines.Add(new Run("\"D:\\Shadow Play\\OBS\\deadlock\\deadlock_05.10.2026_17-24-31.mp4\"") { Foreground = green });
            Card(command);
            panel.Children.Add(Text("12 px · подписи, время и сочетания клавиш", sans, caption, 12));
            panel.Children.Add(Text("Сохранено · Ctrl+Enter · 14:21", sans, caption, 12, true));
            panel.Children.Add(Text("SemiBold · АБВГД ЁЖЗИЙ · 0O 1lI", mono, ink, bold: true));
        }
        sheet.Measure(new Size(width, double.PositiveInfinity));
        sheet.Arrange(new Rect(0, 0, width, sheet.DesiredSize.Height));
        sheet.UpdateLayout();
        Save(sheet, width, sheet.ActualHeight, dpi, $"typography-preview-{dpi}.png");
    }

    static void RenderPanel(string directory, Action<bool, string> check)
    {
        var store = new HistoryStore(System.IO.Path.Combine(directory, "typography", "history.dat"));
        const string prose = "Setting breakpad minidump AppID =\n2394010\nGame version is v1.0.5.102999";
        const string code = "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(Difficulty=None)";
        var now = DateTime.UtcNow;
        foreach (string text in new[] { "Выберите место для вставки пути. Съешь ещё этих мягких булок.", code, "CoopPlayerMaxNum", prose })
            store.Add(text, null, null, now, "typography sample");
        var panel = new PanelWindow(store, new SymbolCatalog(System.IO.Path.Combine(directory, "typography", "symbols.json")));
        try
        {
            panel.Root.Measure(new Size(392, 492)); panel.Root.Arrange(new Rect(0, 0, 392, 492)); panel.Root.UpdateLayout();
            var previews = Descendants(panel.List).OfType<TextBlock>().Where(t => t.Name == "TextPreview").ToArray();
            bool Uses(string content, string name) => previews.FirstOrDefault(t => t.Text == content) is { } text
                && new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch).TryGetGlyphTypeface(out var glyph)
                && glyph.FontUri.Scheme == "pack" && glyph.FamilyNames.Values.Any(n => n.Contains(name, StringComparison.Ordinal));
            check(Uses(prose, "IBM Plex Sans") && Uses(code, "IBM Plex Mono"),
                "typography: реальные карточки истории наследуют упакованный Sans для текста и Mono для кода");
            Save(panel.Root, 392, 492, 96, "typography-panel.png");
        }
        finally { panel.Close(); }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    static void Save(Visual visual, double width, double height, int dpi, string name)
    {
        double scale = dpi / 96.0;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(file);
    }

    static TextBlock Text(string text, FontFamily font, Brush ink, double size = 14, bool bold = false) => new()
    {
        Text = text, FontFamily = font, FontSize = size, Foreground = ink,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        LineHeight = 20, Margin = new Thickness(0, 4, 0, 4)
    };

    static Brush Ink(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}
