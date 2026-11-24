using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    internal event Action<int, int, bool>? PanelTransparencyChanged;
    bool _syncPanelTransparency;

    void InitializePanelTransparency()
    {
        PanelShadow.SizeChanged += (_, e) =>
        {
            var size = e.NewSize;
            // The shadow layer must not paint a second, opaque copy of the window under the translucent surface.
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-ShadowMargin, -ShadowMargin, size.Width + 2 * ShadowMargin, size.Height + 2 * ShadowMargin)),
                new RectangleGeometry(new Rect(size), 8, 8));
            outside.Freeze();
            PanelShadow.Clip = outside;
        };
        BackgroundTransparencySlider.ValueChanged += (_, _) => ChangePanelTransparency();
        TextTransparencySlider.ValueChanged += (_, _) => ChangePanelTransparency(textChanged: true);
        LinkTransparencyToggle.Checked += (_, _) => ChangePanelTransparency();
        LinkTransparencyToggle.Unchecked += (_, _) => ChangePanelTransparency();
        LinkTransparencyToggle.Checked += AnimateSwitch;
        LinkTransparencyToggle.Unchecked += AnimateSwitch;
        SetPanelTransparency(0, 0, false);
    }

    void ChangePanelTransparency(bool textChanged = false)
    {
        if (_syncPanelTransparency) return;
        int background = (int)BackgroundTransparencySlider.Value;
        int text = (int)TextTransparencySlider.Value;
        bool linked = LinkTransparencyToggle.IsChecked == true;
        if (linked && textChanged) background = text;
        SetPanelTransparency(background, text, linked);
        PanelTransparencyChanged?.Invoke((int)BackgroundTransparencySlider.Value, (int)TextTransparencySlider.Value, linked);
    }

    internal void SetPanelTransparency(int background, int text, bool linked)
    {
        _syncPanelTransparency = true;
        try
        {
            BackgroundTransparencySlider.Value = background;
            TextTransparencySlider.Value = linked ? BackgroundTransparencySlider.Value : text;
            LinkTransparencyToggle.IsChecked = linked;
            BackgroundTransparencyValue.Text = $"{(int)BackgroundTransparencySlider.Value}%";
            TextTransparencyValue.Text = $"{(int)TextTransparencySlider.Value}%";
        }
        finally { _syncPanelTransparency = false; }
        RefreshPanelTransparency();
    }

    internal void RefreshPanelTransparency()
    {
        double background = 1 - BackgroundTransparencySlider.Value / 100;
        double text = 1 - TextTransparencySlider.Value / 100;
        PanelShadow.Opacity = background;
        SetBrush("PanelSurface", "WindowSurface", background);
        SetBrush("PanelTexture", "SkinTexture", background);
        SetBrush("PanelSheen", "SkinSheen", background);
        SetBrush("PanelInner", "SkinInner", background);
        SetBrush("PanelCardFill", "CardFill", background);
        SetBrush("PanelCardHover", "CardHover", background);
        SetBrush("PanelCardSubtleHover", "SubtleHover", background);
        SetBrush("PanelFooterFill", "ControlFill", background);
        SetBrush("PanelEntryText", "TextPrimary", text);
        SetBrush("PanelEntryTextSecondary", "TextSecondary", text);

        void SetBrush(string key, string themeKey, double opacity)
        {
            if (TryFindResource(themeKey) is not Brush theme) return;
            if (theme is SolidColorBrush solid && Resources[key] is SolidColorBrush current && current.Color == solid.Color && current.Opacity == opacity) return;
            var brush = theme.Clone();
            brush.Opacity = opacity;
            brush.Freeze();
            Resources[key] = brush;
        }
    }

    internal static void SelfTestPanelTransparency(string directory, Action<bool, string> check)
    {
        var store = new HistoryStore(Path.Combine(directory, "opacity-history.dat"));
        store.Add("preview", null, null, DateTime.UtcNow);
        var panel = new PanelWindow(store, new SymbolCatalog(Path.Combine(directory, "opacity-symbols.json")));
        try
        {
            double Opacity(string key) => ((Brush)panel.FindResource(key)).Opacity;
            check(Opacity("PanelSurface") == 1 && Opacity("PanelEntryText") == 1 && panel.LinkTransparencyToggle.IsChecked == false,
                "panel transparency defaults to full visibility");
            panel.SetPanelTransparency(40, 20, false);
            check(Opacity("PanelSurface") == 0.6 && Opacity("PanelCardFill") == 0.6 && Opacity("PanelEntryText") == 0.8,
                "panel transparency independently adjusts the surface, cards and entry text");
            panel.Root.Opacity = 1;
            panel.Root.Measure(new Size(392, 492));
            panel.Root.Arrange(new Rect(0, 0, 392, 492));
            panel.Root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(392, 492, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel.Root);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(120, 330, 1, 1), pixel, 4, 0);
            check(pixel[3] is >= 152 and <= 154, $"panel shadow leaves the background at the requested alpha (got {pixel[3]})");
            check(Opacity("WindowSurface") == 1 && Opacity("TextPrimary") == 1,
                "panel transparency leaves shared theme brushes unchanged");
            var changes = new List<(int, int, bool)>();
            panel.PanelTransparencyChanged += (background, text, linked) => changes.Add((background, text, linked));
            panel.LinkTransparencyToggle.IsChecked = true;
            panel.TextTransparencySlider.Value = 65;
            panel.BackgroundTransparencySlider.Value = 30;
            panel.LinkTransparencyToggle.IsChecked = false;
            panel.TextTransparencySlider.Value = 25;
            check(changes.SequenceEqual([(40, 40, true), (65, 65, true), (30, 30, true), (30, 30, false), (30, 25, false)]),
                "panel transparency links either slider and publishes each preference once");
            panel.SetPanelTransparency(101, -1, false);
            check(panel.BackgroundTransparencySlider.Value == 75 && panel.TextTransparencySlider.Value == 0 && changes.Count == 5,
                "panel transparency bounds loaded settings without writing them back");
        }
        finally { panel.Close(); }
    }
}
