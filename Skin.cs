using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

/// <summary>
/// Скин «Обсидиан»: вулканическое стекло под цвет пламени Nether. Меняет те же кисти-токены, что и тема (App.ApplyTheme),
/// поэтому одевает все окна, меню и поля сразу. Скин тёмный при любой теме Windows: светлого обсидиана не бывает.
/// </summary>
static class Skin
{
    internal static bool On { get; set; }
    static readonly Lazy<BitmapSource> Facets = new(CreateFacets);

    /// <summary>Основной цвет кисти: у градиента — верхний.</summary>
    internal static Color ColorOf(object? brush) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        GradientBrush { GradientStops.Count: > 0 } gradient => gradient.GradientStops[0].Color,
        _ => Colors.Gray,
    };

    static Color Argb(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    static Brush Flat(uint v) => Frozen(new SolidColorBrush(Argb(v)));
    static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    /// <summary>Сверху вниз: так падает свет на все детали скина.</summary>
    static Brush Lit(params (uint Color, double At)[] stops) => Line(new Point(0, 0), new Point(0, 1), stops);
    static Brush Line(Point from, Point to, params (uint Color, double At)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        foreach (var (color, at) in stops) brush.GradientStops.Add(new GradientStop(Argb(color), at));
        return Frozen(brush);
    }

    static Brush Radial(Point center, double radiusX, double radiusY, uint inner, uint outer)
    {
        var brush = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = radiusX, RadiusY = radiusY };
        brush.GradientStops.Add(new GradientStop(Argb(inner), 0));
        brush.GradientStops.Add(new GradientStop(Argb(outer), 1));
        return Frozen(brush);
    }

    /// <summary>Без скина поверхность панели гладкая.</summary>
    internal static void Plain(ResourceDictionary resources)
    {
        foreach (var name in new[] { "SkinTexture", "SkinSheen", "SkinInner" }) resources[name] = Brushes.Transparent;
    }

    internal static void Obsidian(ResourceDictionary resources)
    {
        var tile = new ImageBrush(Facets.Value) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 512, 512), Stretch = Stretch.None };
        foreach (var (name, brush) in new (string, Brush)[]
        {
            // Стекло: сверху светлее, к низу уходит в глубину; поверх — сколы и отсвет пламени.
            ("WindowSurface", Line(new Point(0, 0), new Point(0.35, 1), (0xFF192325, 0), (0xFF12191B, 0.45), (0xFF0D1315, 1))),
            ("SkinTexture", Frozen(tile)),
            ("SkinSheen", Radial(new Point(0.28, -0.12), 0.95, 0.62, 0x1C36D6C4, 0x0036D6C4)),
            ("SkinInner", Lit((0x4C9CF2E6, 0), (0x128FD8CE, 0.16), (0x00000000, 0.6), (0x40000000, 1))),
            ("WindowStroke", Line(new Point(0, 0), new Point(1, 1), (0x6651C9BB, 0), (0x30567E79, 0.4), (0x22465E5B, 1))),
            // Грани карточек и кнопок: свет на верхней кромке, тень под нижней.
            ("CardFill", Lit((0x1FA9E4DC, 0), (0x10789F9A, 0.55), (0x0B5D7D79, 1))),
            ("CardHover", Lit((0x34A6F0E5, 0), (0x1F7CBDB4, 0.55), (0x18609690, 1))),
            ("CardStroke", Lit((0x3D9AE6DA, 0), (0x14749E98, 0.3), (0x48000000, 1))),
            ("SubtleHover", Flat(0x1A8FE5D9)), ("SubtlePressed", Flat(0x118FE5D9)),
            ("ControlFill", Lit((0x22A0DCD4, 0), (0x0F5F8680, 1))), ("ControlFillHover", Lit((0x36A0EBE0, 0), (0x1B639A92, 1))),
            ("FieldFill", Lit((0x73060B0C, 0), (0x3D0C1415, 0.5), (0x1F6A9690, 1))), ("FieldFillHover", Lit((0x73060B0C, 0), (0x3D142021, 0.5), (0x3A7CB3AB, 1))), // поля утоплены, кнопки выпуклые
            ("ControlFillFocused", Lit((0xD60A1011, 0), (0xC2101719, 1))),
            ("ControlStroke", Lit((0x3892DDD2, 0), (0x16648984, 0.5), (0x3C000000, 1))), ("ControlStrongStroke", Flat(0x8C79AFA8)),
            ("ScrollThumb", Lit((0x9462B9AE, 0), (0x70448A82, 1))),
            ("MenuSurface", Lit((0xFF1A2426, 0), (0xFF131B1D, 1))), ("MenuStroke", Lit((0x6660CBBD, 0), (0x33426C67, 1))),
            ("DialogSurface", Lit((0xFF192325, 0), (0xFF11181A, 1))), ("SliderThumbFill", Flat(0xFF24363A)),
            ("TextPrimary", Flat(0xFFE9F5F2)), ("TextSecondary", Flat(0xE2C0D7D3)), ("TextDisabled", Flat(0x66B6CFCB)), ("TextOnAccent", Flat(0xFF04201C)),
            ("Accent", Flat(0xFF3DDCC8)), ("AccentRing", Line(new Point(0, 0), new Point(1, 1), (0xD03DDCC8, 0), (0x703DDCC8, 1))),
            ("CautionFill", Flat(0xFF38301A)), ("CautionIcon", Flat(0xFFF2CF63)), ("SuccessFill", Flat(0xFF173428)), ("SuccessIcon", Flat(0xFF63D8A1)),
            ("DropValueText", Flat(0xFF6DEBC0)), ("DropValueFill", Flat(0xFF15352F)),
            ("CriticalFill", Flat(0xFF3A2325)), ("CriticalIcon", Flat(0xFFFF9AA2)), ("CriticalText", Flat(0xFFFF9AA2)), ("InfoGlyph", Flat(0xFF07100F)),
        }) resources[name] = brush;
    }

    /// <summary>
    /// Раковистый скол обсидиана: плитка изогнутых граней, каждая наклонена к свету по-своему, по редким стыкам идёт тонкий блик.
    /// Полупрозрачный слой поверх градиента; мелкое зерно прячет полосы градиента. Считается один раз, около 60 мс.
    /// </summary>
    static BitmapSource CreateFacets()
    {
        const int size = 512, cells = 5;
        const double step = (double)size / cells;
        var dice = new Random(41);
        var points = new (double X, double Y, double TiltX, double TiltY, bool Seam)[cells * cells];
        for (int i = 0; i < points.Length; i++)
            points[i] = ((i % cells + 0.12 + 0.76 * dice.NextDouble()) * step, (i / cells + 0.12 + 0.76 * dice.NextDouble()) * step,
                dice.NextDouble() * 2 - 1, dice.NextDouble() * 2 - 1, dice.NextDouble() > 0.6);
        var pixels = new byte[size * size * 4];
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                // Волна с периодом плитки гнёт прямые стыки в дуги, и шва между плитками нет.
                double x = px + 17 * Math.Sin(2 * Math.PI * (py / (double)size * 2 + px / (double)size)), y = py + 14 * Math.Sin(2 * Math.PI * (px / (double)size * 2 - py / (double)size));
                int cx = (int)Math.Floor(x / step), cy = (int)Math.Floor(y / step);
                double first = double.MaxValue, second = double.MaxValue, nearX = 0, nearY = 0;
                int near = 0;
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int gx = cx + ox, gy = cy + oy, index = ((gy % cells + cells) % cells) * cells + (gx % cells + cells) % cells;
                        double dx = x - (points[index].X + Math.Floor(gx / (double)cells) * size), dy = y - (points[index].Y + Math.Floor(gy / (double)cells) * size), d = Math.Sqrt(dx * dx + dy * dy);
                        if (d < first) { second = first; first = d; (near, nearX, nearY) = (index, dx, dy); }
                        else if (d < second) second = d;
                    }
                var cell = points[near];
                double shade = (nearX * cell.TiltX + nearY * cell.TiltY) / step * 0.062; // грань светлеет к одному краю
                double seam = second - first;
                if (seam < 2.6) shade += cell.Seam ? 0.055 * (1 - seam / 2.6) : -0.05 * (1 - seam / 2.6);
                shade += (dice.NextDouble() - 0.5) * 0.016;
                byte alpha = (byte)Math.Clamp(Math.Abs(shade) * 255, 0, 40);
                int at = (py * size + px) * 4;
                if (shade > 0) (pixels[at], pixels[at + 1], pixels[at + 2]) = ((byte)(alpha * 0.92), alpha, (byte)(alpha * 0.8)); // свет с оттенком бирюзы, premultiplied
                pixels[at + 3] = alpha;
            }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var app = (App)Application.Current;
        bool was = On;
        try
        {
            On = false; app.ApplyTheme(true);
            var keys = app.Resources.Keys.Cast<object>().OfType<string>().Where(k => app.Resources[k] is Brush).ToHashSet();
            var plain = keys.ToDictionary(k => k, k => app.Resources[k]);
            On = true; app.ApplyTheme(false);
            check(App.IsDarkTheme && app.Resources["SkinTexture"] is ImageBrush && keys.Count(k => !ReferenceEquals(plain[k], app.Resources[k])) == keys.Count && keys.All(k => app.Resources[k] is Brush { IsFrozen: true }),
                "скин: заменяет все кисти темы, светлая тема Windows его не меняет");
            double Luma(Color c) { double F(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); } return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B); }
            Color Over(Color top, Color below) { double a = top.A / 255.0; byte M(byte t, byte b) => (byte)(t * a + b * (1 - a)); return Color.FromRgb(M(top.R, below.R), M(top.G, below.G), M(top.B, below.B)); }
            double Contrast(Color a, Color b) { double x = Luma(a), y = Luma(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
            var surface = ColorOf(app.Resources["WindowSurface"]);
            var card = Over(ColorOf(app.Resources["CardHover"]), surface); // самое светлое, на чём стоит текст
            foreach (var (name, least) in new[] { ("TextPrimary", 7.0), ("TextSecondary", 4.5), ("Accent", 4.5) })
            {
                var text = Over(ColorOf(app.Resources[name]), card);
                check(Contrast(text, card) >= least, $"скин: {name} читается, контраст {Contrast(text, card):0.0}");
            }
            check(Contrast(ColorOf(app.Resources["TextOnAccent"]), ColorOf(app.Resources["Accent"])) >= 4.5, "скин: текст на кнопке акцента читается");
            On = false; app.ApplyTheme(false);
            check(!App.IsDarkTheme && ReferenceEquals(app.Resources["SkinTexture"], Brushes.Transparent), "скин: выключен, тема Windows вернулась");
        }
        finally { On = was; app.ApplyTheme(); }
    }
}
