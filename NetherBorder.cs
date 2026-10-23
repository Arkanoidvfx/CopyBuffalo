using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

/// <summary>One complete Nether frame per tick, using Moment Player's original 170% fit.</summary>
sealed class NetherBorder : FrameworkElement
{
    internal const double OuterInset = 80;
    public double Inset { get; set; } = OuterInset;
    internal static readonly Lazy<Task<BitmapSource[]>> Frames = new(() => Task.Run(() =>
    {
        var frames = new BitmapSource[64];
        for (int frame = 0; frame < frames.Length; frame++)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ArkCopyBuffer.Assets.Vfx.Nether.Nether_Hover_{frame}.png")
                ?? throw new InvalidDataException($"Missing Nether frame {frame}");
            var source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (source.PixelWidth != 256 || source.PixelHeight != 256) throw new InvalidDataException("Invalid Nether frame size");
            frames[frame] = PrepareFrame(source);
        }
        return frames;
    }));

    // A one-source-pixel Gaussian softens magnified pinholes; filter premultiplied colour with alpha to avoid fringes.
    internal static BitmapSource PrepareFrame(BitmapSource source)
    {
        var premultiplied = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        premultiplied.CopyPixels(pixels, stride, 0);
        var horizontal = new ushort[pixels.Length];
        var filtered = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int centre = y * stride + x * 4;
                int left = y * stride + Math.Max(0, x - 1) * 4;
                int right = y * stride + Math.Min(width - 1, x + 1) * 4;
                for (int channel = 0; channel < 4; channel++)
                    horizontal[centre + channel] = (ushort)(pixels[left + channel] + 2 * pixels[centre + channel] + pixels[right + channel]);
            }
        for (int y = 0; y < height; y++)
        {
            int above = Math.Max(0, y - 1) * stride, row = y * stride, below = Math.Min(height - 1, y + 1) * stride;
            for (int i = 0; i < stride; i++)
                filtered[row + i] = (byte)((horizontal[above + i] + 2 * horizontal[row + i] + horizontal[below + i] + 8) / 16);
        }
        var bitmap = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Pbgra32, null, filtered, stride);
        bitmap.Freeze();
        return bitmap;
    }

    BitmapSource? _frame;
    internal BitmapSource? Frame
    {
        get => _frame;
        set { if (ReferenceEquals(_frame, value)) return; _frame = value; InvalidateVisual(); }
    }

    public NetherBorder() { IsHitTestVisible = false; Focusable = false; }

    protected override void OnRender(DrawingContext dc)
    {
        if (_frame == null || ActualWidth <= Inset * 2 || ActualHeight <= Inset * 2) return;
        // Only the external halo is drawn: no coloured film over text or controls.
        var opening = new Rect(Inset, Inset, ActualWidth - Inset * 2, ActualHeight - Inset * 2);
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(RenderSize)), new RectangleGeometry(opening, 8, 8)));
        double width = opening.Width * 1.7, height = opening.Height * 1.7;
        dc.DrawImage(_frame, new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height));
        dc.Pop();
    }
}

/// <summary>Click-through owned layer; it never activates or takes input from the clipboard panel.</summary>
sealed class VfxWindow : Window
{
    internal const double ExtraInset = NetherBorder.OuterInset - 16;
    internal readonly NetherBorder Flame = new() { Margin = new Thickness(-NetherBorder.OuterInset) };
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;

    internal VfxWindow(FrameworkElement panelRoot)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        IsHitTestVisible = false;
        Focusable = false;
        Width = 392 + ExtraInset * 2;
        Height = 492 + ExtraInset * 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var root = new Grid { Margin = new Thickness(NetherBorder.OuterInset) };
        root.Children.Add(Flame);
        foreach (var property in new[] { OpacityProperty, RenderTransformProperty, RenderTransformOriginProperty })
            BindingOperations.SetBinding(root, property, new Binding(property.Name) { Source = panelRoot });
        Content = root;
        SourceInitialized += (_, _) =>
        {
            const long transparent = 0x20, noActivate = 0x08000000;
            long style = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE,
                new IntPtr((style | transparent | noActivate | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW));
        };
    }

    internal void Align(Window panel)
    {
        var origin = panel.PointToScreen(new Point());
        var end = panel.PointToScreen(new Point(panel.ActualWidth, panel.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(panel);
        double padX = ExtraInset * dpi.DpiScaleX, padY = ExtraInset * dpi.DpiScaleY;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, (int)Math.Round(origin.X - padX), (int)Math.Round(origin.Y - padY),
            (int)Math.Round(end.X - origin.X + padX * 2), (int)Math.Round(end.Y - origin.Y + padY * 2), Native.SWP_NOACTIVATE);
    }
}
