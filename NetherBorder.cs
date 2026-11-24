using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

/// <summary>Complete Nether frames, crossfaded on every tick, using Moment Player's original 170% fit.</summary>
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

    // Changing a brush repaints the window without a layout pass or a new OnRender.
    readonly ImageBrush _frame = new(), _next = new();

    internal BitmapSource? Frame
    {
        get => (BitmapSource?)_frame.ImageSource;
        set => Show(value);
    }

    /// <summary>Crossfades two neighbouring frames: the flipbook runs at 21 fps or less and would otherwise step visibly.</summary>
    internal void Show(BitmapSource? frame, BitmapSource? next = null, double blend = 0)
    {
        bool appeared = _frame.ImageSource == null && frame != null;
        _frame.ImageSource = frame;
        _next.ImageSource = next;
        // Two translucent layers lose alpha mid-blend; 0.85 keeps the flame's typical alpha (0.1-0.7) within about 10%.
        _frame.Opacity = Math.Pow(1 - blend, 0.85);
        _next.Opacity = Math.Pow(blend, 0.85);
        if (appeared) InvalidateVisual();
    }

    public NetherBorder() { IsHitTestVisible = false; Focusable = false; }

    protected override void OnRender(DrawingContext dc)
    {
        if (_frame.ImageSource == null || ActualWidth <= Inset * 2 || ActualHeight <= Inset * 2) return;
        // Only the external halo is drawn: no coloured film over text or controls.
        var opening = new Rect(Inset, Inset, ActualWidth - Inset * 2, ActualHeight - Inset * 2);
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(RenderSize)), new RectangleGeometry(opening, 8, 8)));
        double width = opening.Width * 1.7, height = opening.Height * 1.7;
        var image = new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
        dc.DrawRectangle(_frame, null, image);
        dc.DrawRectangle(_next, null, image);
        dc.Pop();
    }
}

/// <summary>Click-through owned layer; it never activates or takes input from the clipboard panel.</summary>
sealed class VfxWindow : Window
{
    internal const double ExtraInset = NetherBorder.OuterInset - 16;
    readonly Grid _root = new() { Margin = new Thickness(NetherBorder.OuterInset) };
    (double X, double Y, double Scale)? _grown; // панель тянут за край: окно выросло вместе с её окном
    internal readonly ShardFrame Shards = new();
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
        _root.Children.Add(new System.Windows.Shapes.Rectangle { Margin = new Thickness(-NetherBorder.OuterInset), Fill = Brushes.Transparent }); // область шейдера не зависит от того, что показано
        _root.Children.Add(Flame);
        _root.Children.Add(Shards);
        foreach (var property in new[] { OpacityProperty, RenderTransformProperty, RenderTransformOriginProperty })
            BindingOperations.SetBinding(_root, property, new Binding(property.Name) { Source = panelRoot });
        Content = _root;
        SourceInitialized += (_, _) =>
        {
            const long transparent = 0x20, noActivate = 0x08000000;
            long style = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE,
                new IntPtr((style | transparent | noActivate | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW));
        };
    }

    /// <summary>
    /// Менять размер окна на каждом шаге нельзя: вместе с панелью это 21 кадр/с вместо 64. Поэтому оно, как и окно панели, растёт один раз,
    /// а пламя двигается внутри. Прозрачная картинка — чтобы старое пламя не показалось на кадр в новом месте.
    /// </summary>
    internal void Grow(Native.RECT canvas, Native.RECT panel, double scale)
    {
        int pad = (int)Math.Round(ExtraInset * scale);
        var window = new Native.RECT { Left = canvas.Left - pad, Top = canvas.Top - pad, Right = canvas.Right + pad, Bottom = canvas.Bottom + pad };
        _grown = (window.Left, window.Top, scale);
        Follow(panel);
        Resize(window);
    }

    internal void Follow(Native.RECT panel)
    {
        if (_grown is not { } at) return;
        _root.HorizontalAlignment = HorizontalAlignment.Left;
        _root.VerticalAlignment = VerticalAlignment.Top;
        _root.Margin = new Thickness((panel.Left - at.X) / at.Scale, (panel.Top - at.Y) / at.Scale, 0, 0);
        _root.Width = (panel.Right - panel.Left) / at.Scale;
        _root.Height = (panel.Bottom - panel.Top) / at.Scale;
    }

    internal void Shrink(Native.RECT panel, double scale)
    {
        if (_grown == null) return;
        _grown = null;
        _root.ClearValue(HorizontalAlignmentProperty);
        _root.ClearValue(VerticalAlignmentProperty);
        _root.ClearValue(WidthProperty);
        _root.ClearValue(HeightProperty);
        _root.Margin = new Thickness(NetherBorder.OuterInset);
        Resize(Place(panel, scale));
    }

    /// <summary>Панель разом сменит место и размер: прежнее пламя не должно лечь поверх неё, следующий кадр нарисует новое.</summary>
    internal void Blank() { if (IsVisible && Native.GetWindowRect(Handle, out var here)) Native.UpdateLayered(Handle, here, null, 0, 0); }

    void Resize(Native.RECT window)
    {
        if (Native.UpdateLayered(Handle, window, null, 0, 0)) Native.SendWindowPosChanged(Handle, window);
        else Native.SetWindowPos(Handle, Native.HWND_TOPMOST, window.Left, window.Top, window.Right - window.Left, window.Bottom - window.Top, Native.SWP_NOACTIVATE);
    }

    internal void Align(Native.RECT panel, double scale)
    {
        if (_grown != null) return;
        var window = Place(panel, scale);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, window.Left, window.Top, window.Right - window.Left, window.Bottom - window.Top, Native.SWP_NOACTIVATE);
    }

    static Native.RECT Place(Native.RECT panel, double scale)
    {
        int pad = (int)Math.Round(NetherBorder.OuterInset * scale);
        return new Native.RECT { Left = panel.Left - pad, Top = panel.Top - pad, Right = panel.Right + pad, Bottom = panel.Bottom + pad };
    }
}
