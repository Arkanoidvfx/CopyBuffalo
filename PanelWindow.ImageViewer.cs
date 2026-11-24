using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    Window? _imageViewer;

    ClipItem? ImageForFullScreen(DependencyObject? pointer)
    {
        var list = Active;
        if (pointer.IsWithin(list) && pointer.FindAncestor<ListBoxItem>()?.DataContext is ClipItem { IsImage: true } pointed)
            return pointed;
        if (_zoomTip?.IsOpen == true && _zoomTarget?.DataContext is ClipItem { IsImage: true } previewed)
            return previewed;
        return list.SelectedItem is ClipItem { IsImage: true } selected ? selected : null;
    }

    void ImageViewer_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemOf(sender) is { IsImage: true } item) OpenImageViewer(item);
    }

    async void OpenImageViewer(ClipItem item)
    {
        if (!IsShown || _imageViewer != null || !item.IsImage || _stackOpen || _collectingMerge) return;
        CancelZoom();
        StopGif();
        ClosePeek();
        _outsideTimer.Stop();
        _deactivateTimer.Stop();

        var screen = System.Windows.Forms.Screen.FromHandle(_hwnd).Bounds;
        var viewer = new Window
        {
            Owner = this, Title = UiText.T("На весь экран"), ShowInTaskbar = false, Topmost = true,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Brushes.Black,
            FontFamily = (FontFamily)FindResource("TextFont"), FontSize = 14, UseLayoutRounding = true
        };
        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(image, item.Preview);
        var status = new TextBlock
        {
            Text = UiText.T("Загрузка изображения…"), Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20)
        };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var close = new Button
        {
            Content = UiText.T("Закрыть"), Style = (Style)FindResource("StandardButton"), Margin = new Thickness(12, 0, 0, 0)
        };
        close.Click += (_, _) => viewer.Close();
        var header = new DockPanel { LastChildFill = true };
        header.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        header.Children.Add(close);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(new TextBlock
        {
            Text = item.Preview, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        });
        var chrome = new Border { Child = header, Padding = new Thickness(16, 8, 16, 8) };
        chrome.SetResourceReference(BackgroundProperty, "WindowSurface");
        DockPanel.SetDock(chrome, Dock.Top);
        var canvas = new Grid();
        canvas.Children.Add(image);
        canvas.Children.Add(status);
        var content = new DockPanel();
        content.Children.Add(chrome);
        content.Children.Add(canvas);
        viewer.Content = content;

        CancellationTokenSource? playback = new();
        var token = playback.Token;
        viewer.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(viewer).Handle;
            if (!Native.SetWindowPos(handle, Native.HWND_TOPMOST, screen.X, screen.Y, screen.Width, screen.Height, Native.SWP_NOACTIVATE))
                viewer.WindowState = WindowState.Maximized;
        };
        viewer.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Escape or Key.F11)) return;
            e.Handled = true;
            if (!e.IsRepeat) viewer.Close();
        };
        viewer.Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(_imageViewer, viewer) && !viewer.IsActive) viewer.Close();
        });
        viewer.Closed += (_, _) =>
        {
            playback?.Cancel();
            image.Source = null;
            _imageViewer = null;
            if (!IsShown) return;
            if (IsActive) Search.Focus();
            OnFocusMaybeLost();
        };

        _imageViewer = viewer;
        try
        {
            viewer.Show();
            viewer.UpdateLayout();
            close.Focus();
            var dpi = VisualTreeHelper.GetDpi(viewer);
            var bounds = new Size(canvas.ActualWidth * dpi.DpiScaleX, canvas.ActualHeight * dpi.DpiScaleY);
            void ShowFrame(BitmapSource frame)
            {
                image.Source = frame;
                status.Visibility = Visibility.Collapsed;
            }
            if (item.IsGif)
            {
                // GIF playback rasterizes every frame. Bound its cost even on high-resolution displays.
                double scale = Math.Min(1, Math.Min(1920 / bounds.Width, 1080 / bounds.Height));
                await PlayPreviewGif(item, new Size(bounds.Width * scale, bounds.Height * scale), ShowFrame, token);
            }
            else if (await LoadPreviewImage(item, bounds, token) is { } full && !token.IsCancellationRequested)
                ShowFrame(full);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.Trace($"Image preview: {ex.Message}");
            if (!viewer.IsVisible && ReferenceEquals(_imageViewer, viewer)) viewer.Close();
        }
        finally
        {
            playback.Dispose();
            playback = null;
            if (!token.IsCancellationRequested && image.Source == null)
                status.Text = UiText.T("Не удалось открыть изображение.");
        }
    }

    static (int Width, int Height) PreviewDecodeSize(int width, int height, Size bounds)
    {
        if (width <= 0 || height <= 0 || bounds.Width < 1 || bounds.Height < 1) return (0, 0);
        // WPF truncates an omitted dimension, which can reduce a thin image to zero pixels.
        double scale = Math.Min(1, Math.Min(bounds.Width / width, bounds.Height / height));
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

    internal static Task<BitmapSource?> LoadPreviewImage(ClipItem item, Size bounds, CancellationToken token = default)
    {
        var (width, height) = PreviewDecodeSize(item.ImageWidth, item.ImageHeight, bounds);
        if (item.ImageDir == null || item.ImageHash == null || (width == 0 && height == 0)) return Task.FromResult<BitmapSource?>(null);
        string path = Path.Combine(item.ImageDir, item.ImageHash + ".dat");
        return Task.Run(() => ImageFiles.LoadBitmap(path, width, height), token);
    }

    Task PlayPreviewGif(ClipItem item, Size bounds, Action<BitmapSource> showFrame, CancellationToken token)
    {
        if (item.ImageDir is not { } directory || item.ImageHash is not { } hash || bounds.Width < 1 || bounds.Height < 1)
            return Task.CompletedTask;
        return Task.Run(async () =>
        {
            var bytes = ImageFiles.ReadFull(directory, hash);
            using var stream = new MemoryStream(bytes);
            using var gif = System.Drawing.Image.FromStream(stream);
            int count = gif.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time);
            byte[]? delays = null;
            try { delays = gif.GetPropertyItem(0x5100)?.Value; } catch (ArgumentException) { }
            double scale = Math.Min(1, Math.Min(bounds.Width / gif.Width, bounds.Height / gif.Height));
            var size = new System.Drawing.Size(Math.Max(1, (int)(gif.Width * scale)), Math.Max(1, (int)(gif.Height * scale)));
            for (int frame = 0; count > 0; frame = (frame + 1) % count)
            {
                token.ThrowIfCancellationRequested();
                gif.SelectActiveFrame(System.Drawing.Imaging.FrameDimension.Time, frame);
                using var bitmap = new System.Drawing.Bitmap(gif, size);
                using var png = new MemoryStream();
                bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                var source = ImageFiles.Decode(png.ToArray());
                await Dispatcher.InvokeAsync(() => showFrame(source), DispatcherPriority.Background, token);
                if (count == 1) return;
                int delay = delays != null && (long)frame * 4 + 4 <= delays.Length ? BitConverter.ToInt32(delays, frame * 4) * 10 : 100;
                await Task.Delay(Math.Max(20, delay), token);
            }
        }, token);
    }

    internal static void SelfTestImageViewer(string directory, Action<bool, string> check)
    {
        var bounds = new Size(1920, 1040);
        check(PreviewDecodeSize(4000, 2000, bounds) == (1920, 960)
            && PreviewDecodeSize(1, 32000, bounds) == (1, 1040)
            && PreviewDecodeSize(40, 20, bounds) == (40, 20)
            && PreviewDecodeSize(0, 20, bounds) == (0, 0), "image viewer: decoding fits the screen without upscaling the bitmap");
        string images = Path.Combine(directory, "viewer-images");
        var original = BitmapSource.Create(80, 120, 96, 96, PixelFormats.Bgra32, null, new byte[80 * 120 * 4], 80 * 4);
        ImageFiles.Write(images, "view", original);
        var image = new ClipItem { ImageDir = images, ImageHash = "view", ImageWidth = 80, ImageHeight = 120 };
        var decoded = LoadPreviewImage(image, new Size(100, 60)).GetAwaiter().GetResult();
        check(decoded is { PixelWidth: 40, PixelHeight: 60, IsFrozen: true }, "image viewer: reads the encrypted attachment at display resolution");
        var thin = BitmapSource.Create(1, 4096, 96, 96, PixelFormats.Bgra32, null, new byte[4096 * 4], 4);
        ImageFiles.Write(images, "thin", thin);
        var thinItem = new ClipItem { ImageDir = images, ImageHash = "thin", ImageWidth = 1, ImageHeight = 4096 };
        check(LoadPreviewImage(thinItem, bounds).GetAwaiter().GetResult() is { PixelWidth: 1, PixelHeight: 1040 },
            "image viewer: extremely thin images retain one pixel on the short side");
        ImageFiles.Delete(images, "view");
        check(LoadPreviewImage(image, bounds).GetAwaiter().GetResult() == null, "image viewer: a missing attachment does not throw");
    }

    // Called only by the guarded History Map integration test on its isolated station.
    internal static void CheckImageViewerLifecycle(string directory, Action<bool, string> check)
    {
        var store = new HistoryStore(Path.Combine(directory, "viewer-lifecycle", "history.dat"));
        var original = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 25, 80, 140, 255 }, 4);
        ImageFiles.Write(store.ImagesDir, "viewer", original);
        var item = new ClipItem { ImageDir = store.ImagesDir, ImageHash = "viewer", ImageWidth = 1, ImageHeight = 1 };
        store.Items.Add(item);
        var text = new ClipItem { Text = "Other selected item" };
        store.Items.Add(text);
        var panel = new PanelWindow(store, new SymbolCatalog(Path.Combine(directory, "viewer-lifecycle", "symbols.json")));
        try
        {
            panel.Show();
            panel.Active.SelectedItem = text;
            panel.UpdateLayout();
            var row = (ListBoxItem)panel.Active.ItemContainerGenerator.ContainerFromItem(item);
            check(panel._zoomTip == null && panel.ImageForFullScreen(row) == item && panel.ImageForFullScreen(null) == null,
                "image viewer: F11 targets the hovered image before mini-preview, even with text selected");
            var menu = panel.ShowMenu(item, row, PlacementMode.MousePoint);
            bool menuClosedBeforeViewer = false;
            menu.Closed += (_, _) => menuClosedBeforeViewer = panel._imageViewer == null;
            var fullScreen = menu.Items.OfType<MenuItem>().Single(entry => entry.InputGestureText == "F11");
            var invoke = (IInvokeProvider)new MenuItemAutomationPeer(fullScreen).GetPattern(PatternInterface.Invoke);
            invoke.Invoke();
            var waitForMenu = System.Diagnostics.Stopwatch.StartNew();
            while (panel._imageViewer == null && waitForMenu.Elapsed < TimeSpan.FromSeconds(5))
            {
                panel.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
            check(!menu.IsOpen && menuClosedBeforeViewer && panel._imageViewer != null,
                "image viewer: the actual context menu command waits until the popup closes");
            var viewer = panel._imageViewer ?? throw new InvalidOperationException("Image viewer did not open");
            var handle = new WindowInteropHelper(viewer).Handle;
            var monitor = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(panel).Handle).Bounds;
            check(Native.GetWindowRect(handle, out var rect) && rect.Left == monitor.Left && rect.Top == monitor.Top &&
                rect.Right == monitor.Right && rect.Bottom == monitor.Bottom && viewer.Owner == panel && panel.Busy,
                "image viewer: owned borderless window fills the panel's monitor");
            var canvasImage = ((DependencyObject)viewer.Content).FindDescendant<Image>()!;
            var until = System.Diagnostics.Stopwatch.StartNew();
            while (canvasImage.Source == null && until.Elapsed < TimeSpan.FromSeconds(5))
            {
                panel.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
            check(canvasImage.Source is BitmapSource, "image viewer: asynchronous decode paints the visible window");
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(viewer), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            viewer.RaiseEvent(escape);
            check(escape.Handled && panel._imageViewer == null && panel.IsVisible && panel.Active.SelectedItem == text && canvasImage.Source == null,
                "image viewer: Escape returns to the selected record and releases the decoded image");
            panel.OpenImageViewer(item);
            check(panel._imageViewer is { IsVisible: true }, "image viewer: a closed viewer can be reopened");
            panel._imageViewer!.Close();
            check(panel._imageViewer == null, "image viewer: closing during the decode releases the window");
        }
        finally { panel.Close(); }
    }
}
