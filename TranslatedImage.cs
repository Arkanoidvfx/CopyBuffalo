using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

static class TranslatedImage
{
    internal static BitmapSource Render(byte[] image, OcrText.Result ocr, IReadOnlyList<string> translations,
        long maxPixels, CancellationToken cancel)
    {
        if (ocr.Width <= 0 || ocr.Height <= 0 || (long)ocr.Width * ocr.Height > Math.Min(maxPixels, 80_000_000)
            || ocr.Regions.Count == 0 || translations.Count != ocr.Regions.Count)
            throw new ScreenshotOcr.Failure(UiText.T("Не удалось сопоставить перевод с изображением."));
        var decoded = ImageFiles.Decode(image);
        if (decoded.PixelWidth != ocr.Width || decoded.PixelHeight != ocr.Height)
            throw new ScreenshotOcr.Failure(UiText.T("Размер изображения изменился во время перевода."));
        var source = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        int stride = checked(ocr.Width * 4);
        var pixels = new byte[checked(stride * ocr.Height)];
        source.CopyPixels(pixels, stride, 0);
        var bounds = new Rect(0, 0, ocr.Width, ocr.Height);
        var labels = new List<(Rect Erase, Point Origin, FormattedText Text, Color Background)>();
        for (int i = 0; i < ocr.Regions.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var r = ocr.Regions[i];
            var box = Rect.Intersect(bounds, new Rect(r.X, r.Y, r.Width, r.Height));
            if (box.IsEmpty || box.Width < 1 || box.Height < 1)
                throw new ScreenshotOcr.Failure(UiText.T("Надпись находится за границами картинки."));
            if (string.IsNullOrWhiteSpace(translations[i])) throw new ScreenshotOcr.Failure(UiText.T("Перевод надписи пуст."));
            if (translations[i] == r.Text) continue;
            var (background, foreground) = Sample(pixels, stride, box);
            double right = Math.Min(ocr.Width, box.X + box.Width * 1.8);
            foreach (var next in ocr.Regions)
                if (next.X >= box.Right && Math.Min(next.Bottom, box.Bottom) - Math.Max(next.Y, box.Y) > box.Height * .3)
                    right = Math.Min(right, next.X - 2);
            double available = Math.Max(box.Width, right - box.X);
            double size = Math.Max(1, box.Height * .8), minimum = Math.Min(5, size * .6);
            FormattedText text;
            while (true)
            {
                text = new FormattedText(translations[i], CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                    App.TextTypeface, size, new SolidColorBrush(foreground), 1) { LineHeight = box.Height };
                if (text.WidthIncludingTrailingWhitespace <= available && text.Height <= box.Height + .1) break;
                size -= .5;
                if (size < minimum) throw new ScreenshotOcr.Failure(UiText.T("Перевод не помещается на картинке без потери читаемости. Выделите меньшую область или увеличьте изображение."));
            }
            double width = Math.Max(box.Width, text.WidthIncludingTrailingWhitespace);
            var erase = Rect.Intersect(bounds, new Rect(Math.Floor(box.X - 1), Math.Floor(box.Y - 1),
                Math.Ceiling(width + 3), Math.Ceiling(box.Height + 3)));
            labels.Add((erase, new Point(box.X, box.Y), text, background));
        }
        if (labels.Count == 0) return decoded;
        foreach (var label in labels)
        {
            for (int y = (int)label.Erase.Top; y < Math.Ceiling(label.Erase.Bottom); y++)
            {
                cancel.ThrowIfCancellationRequested();
                for (int x = (int)label.Erase.Left; x < Math.Ceiling(label.Erase.Right); x++)
                {
                    int offset = y * stride + x * 4;
                    pixels[offset] = label.Background.B; pixels[offset + 1] = label.Background.G;
                    pixels[offset + 2] = label.Background.R; pixels[offset + 3] = label.Background.A;
                }
            }
        }
        var clean = BitmapSource.Create(ocr.Width, ocr.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        clean.Freeze();
        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(clean, bounds);
            foreach (var label in labels) drawing.DrawText(label.Text, label.Origin);
        }
        cancel.ThrowIfCancellationRequested();
        var result = new RenderTargetBitmap(ocr.Width, ocr.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze();
        return result;
    }

    // Dominant quantized color is the background; a frequent contrasting cluster is the ink (Page2Language approach).
    static (Color Background, Color Foreground) Sample(byte[] pixels, int stride, Rect box)
    {
        var bins = new Dictionary<int, (int Count, long R, long G, long B, long A)>();
        int step = Math.Max(1, (int)Math.Sqrt(box.Width * box.Height / 4096));
        for (int y = (int)box.Top; y < box.Bottom; y += step)
        for (int x = (int)box.Left; x < box.Right; x += step)
        {
            int offset = y * stride + x * 4;
            byte b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2], a = pixels[offset + 3];
            int key = a < 32 ? 0 : (a >> 4) << 12 | (r >> 4) << 8 | (g >> 4) << 4 | b >> 4;
            bins.TryGetValue(key, out var bin);
            bins[key] = (bin.Count + 1, bin.R + r, bin.G + g, bin.B + b, bin.A + a);
        }
        Color Average((int Count, long R, long G, long B, long A) bin) => Color.FromArgb(
            (byte)(bin.A / bin.Count), (byte)(bin.R / bin.Count), (byte)(bin.G / bin.Count), (byte)(bin.B / bin.Count));
        var dominant = bins.MaxBy(b => b.Value.Count);
        Color background = Average(dominant.Value);
        Color foreground = Luminance(background) > .35 ? Colors.Black : Colors.White;
        double best = 0;
        foreach (var bin in bins.Where(b => b.Key != dominant.Key && b.Value.Count >= 2))
        {
            var color = Average(bin.Value);
            double contrast = (Math.Max(Luminance(color), Luminance(background)) + .05) / (Math.Min(Luminance(color), Luminance(background)) + .05);
            double score = (contrast - 1) * Math.Log2(bin.Value.Count + 1);
            if (color.A >= 128 && score > best && (background.A < 32 || contrast >= 3)) { foreground = color; best = score; }
        }
        return (background, foreground);
    }

    static double Luminance(Color c)
    {
        double Linear(byte value) { double v = value / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }
}
