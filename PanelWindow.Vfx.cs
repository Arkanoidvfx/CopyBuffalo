using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    readonly DispatcherTimer _vfxTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Stopwatch _vfxClock = new();
    BitmapSource[]? _vfxFrames;
    VfxWindow? _vfxWindow;
    bool _vfxReady;
    int _vfxVersion;
    int _vfxFrame = -1;

    public bool VfxEnabled { get => VfxToggle.IsChecked == true; set => VfxToggle.IsChecked = value; }
    public int VfxOpacity { get => (int)VfxOpacitySlider.Value; set => VfxOpacitySlider.Value = Math.Clamp(value, 10, 100); }
    public int VfxSpeed { get => (int)VfxSpeedSlider.Value; set => VfxSpeedSlider.Value = Math.Clamp(value, 25, 200); }
    public event Action<bool, int, int>? VfxChanged;

    void InitializeVfx()
    {
        _vfxReady = true;
        _vfxTimer.Tick += (_, _) => DrawVfxFrame();
        IsVisibleChanged += (_, _) => RefreshVfx();
        LocationChanged += (_, _) => { if (_vfxWindow?.IsVisible == true) _vfxWindow.Align(this); };
        SizeChanged += (_, _) => { if (_vfxWindow?.IsVisible == true) _vfxWindow.Align(this); };
        Closed += (_, _) =>
        {
            _vfxReady = false;
            _vfxVersion++;
            _vfxTimer.Stop();
            _vfxClock.Stop();
            _vfxWindow?.Close();
            _vfxWindow = null;
        };
        VfxToggle.Checked += AnimateSwitch;
        VfxToggle.Unchecked += AnimateSwitch;
        RefreshVfx();
    }

    void Vfx_Changed(object sender, RoutedEventArgs e) => ApplyVfxSettings();
    void Vfx_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyVfxSettings();
    void ApplyVfxSettings()
    {
        if (!_vfxReady) return;
        RefreshVfx();
        VfxChanged?.Invoke(VfxEnabled, VfxOpacity, VfxSpeed);
    }

    async void RefreshVfx()
    {
        if (!_vfxReady) return;
        int version = ++_vfxVersion;
        _vfxTimer.Stop();
        _vfxClock.Stop();
        VfxOpacityValue.Text = $"{VfxOpacity}%";
        VfxSpeedValue.Text = $"{VfxSpeed}%";
        VfxPreview.Opacity = VfxOpacity / 100.0;
        VfxStatus.SetResourceReference(ForegroundProperty, "TextSecondary");
        if (!VfxEnabled || !IsVisible)
        {
            _vfxWindow?.Hide();
            if (!VfxEnabled) VfxPreview.Frame = null;
            VfxStatus.Text = VfxEnabled ? "Готов к показу" : "Выключен";
            return;
        }
        try
        {
            if (_vfxFrames == null)
            {
                VfxStatus.Text = "Загрузка эффекта…";
                var frames = await NetherBorder.Frames.Value;
                if (version != _vfxVersion || !_vfxReady || !IsVisible || !VfxEnabled) return;
                _vfxFrames = frames;
            }
            _vfxWindow ??= new VfxWindow(Root) { Owner = this };
            new System.Windows.Interop.WindowInteropHelper(_vfxWindow).EnsureHandle();
            _vfxWindow.Flame.Opacity = VfxOpacity / 100.0;
            _vfxWindow.Align(this);
            _vfxFrame = -1;
            _vfxClock.Restart();
            DrawVfxFrame();
            if (!_vfxWindow.IsVisible) _vfxWindow.Show();
            _vfxWindow.Align(this);
            if (Animations) _vfxTimer.Start();
            else _vfxClock.Stop();
            VfxStatus.Text = Animations ? "Включён · 64 кадра · только по краям" : "Статичная рамка · анимация окна выключена";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            if (version != _vfxVersion || !_vfxReady) return;
            _vfxWindow?.Hide();
            VfxStatus.Text = "Не удалось загрузить эффект. Попробуйте перезапустить приложение.";
            VfxStatus.SetResourceReference(ForegroundProperty, "CriticalText");
        }
    }

    void DrawVfxFrame()
    {
        if (_vfxFrames == null || _vfxWindow == null) return;
        int frame = Animations ? (int)(_vfxClock.Elapsed.TotalMilliseconds / (40 / .85) * VfxSpeed / 100) % _vfxFrames.Length : 0;
        if (frame == _vfxFrame) return;
        _vfxFrame = frame;
        _vfxWindow.Flame.Frame = _vfxFrames[frame];
        VfxPreview.Frame = _vfxFrames[frame];
    }

    internal static void SelfTestVfx(string dir, Action<bool, string> check)
    {
        static RenderTargetBitmap Render(System.Windows.Media.Visual visual, int width, int height, int dpi = 96)
        {
            var image = new RenderTargetBitmap(width * dpi / 96, height * dpi / 96, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(visual);
            image.Freeze();
            return image;
        }
        static RenderTargetBitmap Border(BitmapSource frame, int width = 392, int height = 492, int dpi = 96)
        {
            width += (int)VfxWindow.ExtraInset * 2;
            height += (int)VfxWindow.ExtraInset * 2;
            var border = new NetherBorder { Frame = frame };
            border.Measure(new Size(width, height)); border.Arrange(new Rect(0, 0, width, height));
            return Render(border, width, height, dpi);
        }
        static RenderTargetBitmap Composite(BitmapSource panel, BitmapSource border, System.Windows.Media.Brush? background = null)
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(background ?? System.Windows.Media.Brushes.Black, null, new Rect(0, 0, border.PixelWidth, border.PixelHeight));
                dc.DrawImage(panel, new Rect(VfxWindow.ExtraInset, VfxWindow.ExtraInset, panel.PixelWidth, panel.PixelHeight));
                dc.PushOpacity(.85); // Match the default VFX intensity in the preview artifacts.
                dc.DrawImage(border, new Rect(0, 0, border.PixelWidth, border.PixelHeight));
                dc.Pop();
            }
            return Render(visual, border.PixelWidth, border.PixelHeight);
        }
        static byte[] Pixels(BitmapSource image)
        {
            var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(bytes, image.PixelWidth * 4, 0);
            return bytes;
        }
        static void Save(BitmapSource image, string name)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, name));
            encoder.Save(file);
        }

        var frames = NetherBorder.Frames.Value.GetAwaiter().GetResult();
        check(frames.Length == 64 && frames.All(f => f is { IsFrozen: true, PixelWidth: 256, PixelHeight: 256 }),
            "VFX: all 64 original full-sized Nether frames are cached without cutting or repeating patches");
        bool validAlpha = true;
        foreach (var cached in frames)
        {
            validAlpha &= cached.Format == System.Windows.Media.PixelFormats.Pbgra32;
            var rgba = Pixels(cached);
            for (int i = 0; i < rgba.Length; i += 4)
                validAlpha &= rgba[i] <= rgba[i + 3] && rgba[i + 1] <= rgba[i + 3] && rgba[i + 2] <= rgba[i + 3];
        }
        check(validAlpha, "VFX: every cached frame has valid premultiplied colour and no colour in fully transparent pixels");
        var pinhole = new byte[7 * 7 * 4];
        for (int i = 0; i < pinhole.Length; i += 4) pinhole[i] = pinhole[i + 1] = pinhole[i + 2] = 255;
        for (int y = 2; y <= 4; y++)
            for (int x = 2; x <= 4; x++)
            {
                int i = (y * 7 + x) * 4;
                pinhole[i] = 160; pinhole[i + 1] = 192; pinhole[i + 2] = 8; pinhole[i + 3] = 255;
            }
        Array.Fill(pinhole, (byte)255, (3 * 7 + 3) * 4, 3);
        pinhole[(3 * 7 + 3) * 4 + 3] = 0;
        var soft = Pixels(NetherBorder.PrepareFrame(BitmapSource.Create(7, 7, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pinhole, 28)));
        int middle = (3 * 7 + 3) * 4;
        check(soft[middle] == 120 && soft[middle + 1] == 144 && soft[middle + 2] == 6 && soft[middle + 3] == 191,
            "VFX: a tiny alpha hole softens with the flame colour instead of leaking undefined transparent RGB");
        check(soft.AsSpan(0, 4).SequenceEqual(new byte[4]), "VFX: distant transparent space remains clear after feathering");
        var largeOpening = new byte[9 * 9 * 4];
        for (int i = 0; i < largeOpening.Length; i += 4) { largeOpening[i + 1] = 192; largeOpening[i + 3] = 255; }
        for (int y = 2; y <= 6; y++)
            for (int x = 2; x <= 6; x++) largeOpening[(y * 9 + x) * 4 + 3] = 0;
        var preserved = Pixels(NetherBorder.PrepareFrame(BitmapSource.Create(9, 9, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, largeOpening, 36)));
        check(preserved[(4 * 9 + 4) * 4 + 3] == 0, "VFX: large natural transparent openings are preserved");
        var colouredBorder = Border(frames[6]);
        var colouredPixels = Pixels(colouredBorder);
        int sample = 0;
        for (int i = 0; i < colouredPixels.Length; i += 4)
            if (colouredPixels[i + 3] > colouredPixels[sample + 3]) sample = i;
        bool blendsCorrectly = true;
        foreach (var colour in new[] { System.Windows.Media.Colors.White, System.Windows.Media.Colors.Red,
                     System.Windows.Media.Color.FromRgb(27, 66, 113), System.Windows.Media.Colors.Black })
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(colour), null, new Rect(0, 0, colouredBorder.PixelWidth, colouredBorder.PixelHeight));
                dc.PushOpacity(.5); dc.DrawImage(colouredBorder, new Rect(0, 0, colouredBorder.PixelWidth, colouredBorder.PixelHeight)); dc.Pop();
            }
            var composited = Pixels(Render(visual, colouredBorder.PixelWidth, colouredBorder.PixelHeight));
            byte[] background = [colour.B, colour.G, colour.R];
            double alpha = colouredPixels[sample + 3] / 255.0 * .5;
            for (int channel = 0; channel < 3; channel++)
                blendsCorrectly &= Math.Abs(composited[sample + channel] - (colouredPixels[sample + channel] * .5 + background[channel] * (1 - alpha))) <= 2;
        }
        check(blendsCorrectly, "VFX: half-opacity flame blends correctly over white, red, blue and black backgrounds");
        var first = Border(frames[0]);
        var pixels = Pixels(first);
        int outside = 0, inside = 0, farOutside = 0;
        for (int y = 0; y < first.PixelHeight; y++)
            for (int x = 0; x < first.PixelWidth; x++)
            {
                byte alpha = pixels[(y * first.PixelWidth + x) * 4 + 3];
                if ((x < 80 || x >= first.PixelWidth - 80 || y < 80 || y >= first.PixelHeight - 80) && alpha > 0) outside++;
                if ((x < 68 || x >= first.PixelWidth - 68 || y < 68 || y >= first.PixelHeight - 68) && alpha > 10) farOutside++;
                if (x >= 88 && x < first.PixelWidth - 88 && y >= 88 && y < first.PixelHeight - 88 && alpha > 0) inside++;
            }
        check(outside > 100 && inside == 0, "VFX: visible flame lies outside the panel; the content opening is fully transparent");
        check(farOutside > 100, "VFX: visible wisps extend more than 12 DIP beyond the border instead of forming a thin outline");
        bool clearEdges = true;
        for (int x = 0; x < first.PixelWidth; x++)
            clearEdges &= pixels[x * 4 + 3] == 0 && pixels[((first.PixelHeight - 1) * first.PixelWidth + x) * 4 + 3] == 0;
        for (int y = 0; y < first.PixelHeight; y++)
            clearEdges &= pixels[y * first.PixelWidth * 4 + 3] == 0 && pixels[(y * first.PixelWidth + first.PixelWidth - 1) * 4 + 3] == 0;
        check(clearEdges, "VFX: the enlarged overlay leaves clear padding around the complete flame");
        check(!pixels.SequenceEqual(Pixels(Border(frames[32]))), "VFX: advancing the loop changes the actual rendered flame pixels");
        var dpiImage = Border(frames[0], dpi: 144);
        var dpiPixels = Pixels(dpiImage);
        check(dpiImage.PixelWidth == 780 && dpiImage.PixelHeight == 930
            && dpiPixels[((dpiImage.PixelHeight / 2) * dpiImage.PixelWidth + dpiImage.PixelWidth / 2) * 4 + 3] == 0,
            "VFX: 150% DPI preserves panel bounds and the clear centre");

        var store = new HistoryStore(System.IO.Path.Combine(dir, "vfx", "history.dat"));
        store.Add("Два быстрых скриншота\nОба остаются в истории", null, null, DateTime.UtcNow, "ShareX");
        store.Add("Призрачное пламя по краям окна", null, null, DateTime.UtcNow, "VFX sample");
        var panel = new PanelWindow(store, new SymbolCatalog(System.IO.Path.Combine(dir, "vfx", "symbols.json")));
        bool motion = Motion.Enabled;
        try
        {
            panel.ShowSettingsPage(2);
            check(panel.VfxSettingsView.Visibility == Visibility.Visible && panel.VisualSettingsView.Visibility == Visibility.Collapsed
                && panel.StorageSettingsView.Visibility == Visibility.Collapsed, "VFX: settings tab selects its own page and hides the other two");
            int changed = 0;
            panel.VfxChanged += (_, _, _) => changed++;
            panel.VfxOpacity = -1; panel.VfxSpeed = 999;
            check(panel.VfxOpacity == 10 && panel.VfxSpeed == 200 && changed == 2, "VFX: settings clamp persisted values and notify persistence");
            panel.VfxEnabled = false;
            check(panel.VfxPreview.Frame == null && panel.VfxStatus.Text == "Выключен", "VFX: disabling removes the preview immediately");
            panel._vfxTimer.Start(); panel._vfxClock.Start(); panel.RefreshVfx();
            check(!panel._vfxTimer.IsEnabled && !panel._vfxClock.IsRunning && panel._vfxWindow == null,
                "VFX: hidden panels stop playback and do not create an overlay");
            panel.VfxEnabled = true; panel.VfxOpacity = 85; panel.VfxSpeed = 100;
            panel._vfxFrames = frames;
            panel._vfxWindow = new VfxWindow(panel.Root);
            new System.Windows.Interop.WindowInteropHelper(panel._vfxWindow).EnsureHandle();
            long style = (long)Native.GetWindowLongPtr(panel._vfxWindow.Handle, Native.GWL_EXSTYLE);
            check((style & 0x08000020) == 0x08000020 && (style & Native.WS_EX_TOOLWINDOW) != 0
                && !panel._vfxWindow.ShowActivated && !panel._vfxWindow.IsHitTestVisible && !panel._vfxWindow.ShowInTaskbar,
                "VFX: the unshown overlay HWND is click-through, nonactivating and absent from taskbar/Alt+Tab");
            Motion.Enabled = false;
            panel._vfxClock.Start(); panel.DrawVfxFrame();
            check(panel._vfxFrame == 0 && ReferenceEquals(panel._vfxWindow.Flame.Frame, frames[0]),
                "VFX: disabled app animations display a still frame instead of playing");
            panel._vfxWindow.Close();
            panel._vfxWindow = null;
            Motion.Enabled = true;
            panel.ShowActivated = false;
            var handle = new System.Windows.Interop.WindowInteropHelper(panel).EnsureHandle();
            Native.SetWindowPos(handle, IntPtr.Zero, -32000, -32000, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
            panel.Show(); // Same offscreen/nonactivating approach as WarmUp; never call ShowAt or send input.
            panel.UpdateLayout();
            panel.RefreshVfx();
            check(panel._vfxWindow is { IsVisible: true } && panel._vfxWindow.Owner == panel && panel._vfxTimer.IsEnabled,
                "VFX: an offscreen nonactivating panel shows its owned overlay and starts playback");
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foregroundPid);
            check(foregroundPid != Environment.ProcessId, "VFX: offscreen panel/overlay playback does not take foreground focus");
            var effectRoot = (FrameworkElement)panel._vfxWindow!.Content;
            var panelOrigin = panel.Root.PointToScreen(new Point());
            var overlayOrigin = effectRoot.PointToScreen(new Point());
            check((panelOrigin - overlayOrigin).Length < 1 && Math.Abs(panel.Root.ActualWidth - effectRoot.ActualWidth) < 1
                && Math.Abs(panel.Root.ActualHeight - effectRoot.ActualHeight) < 1,
                "VFX: the enlarged native overlay keeps its content opening aligned with the panel");
            panel.Root.Opacity = .5;
            panel.Root.RenderTransformOrigin = new Point(.3, 1);
            System.Windows.Data.BindingOperations.GetBindingExpression(effectRoot, OpacityProperty)!.UpdateTarget();
            System.Windows.Data.BindingOperations.GetBindingExpression(effectRoot, RenderTransformOriginProperty)!.UpdateTarget();
            check(effectRoot.Opacity == .5 && effectRoot.RenderTransformOrigin == new Point(.3, 1)
                && ReferenceEquals(effectRoot.RenderTransform, panel.Root.RenderTransform),
                "VFX: overlay shares the panel fade, scale, shift and caret-relative animation pivot");
            panel.Root.Opacity = 1;
            panel.Root.RenderTransformOrigin = new Point();
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            tick.Tick += (_, _) => { tick.Stop(); frame.Continue = false; };
            tick.Start(); Dispatcher.PushFrame(frame);
            check(panel._vfxFrame > 0, "VFX: the native overlay advances through the animation while shown");
            panel.Hide();
            check(!panel._vfxWindow.IsVisible && !panel._vfxTimer.IsEnabled && !panel._vfxClock.IsRunning,
                "VFX: hiding a previously shown panel hides its overlay and stops all playback");
            Motion.Enabled = motion;
            panel._vfxFrame = 16;
            panel.VfxPreview.Frame = frames[16];
            panel.SettingsFlyout.Visibility = Visibility.Visible;
            panel.Root.Measure(new Size(392, 492)); panel.Root.Arrange(new Rect(0, 0, 392, 492)); panel.Root.UpdateLayout();
            check(panel.VfxSettingsTab.ActualWidth > 60 && panel.StorageSettingsTab.ActualWidth > 60 && panel.VisualSettingsTab.ActualWidth > 60,
                "VFX: all three settings tabs fit the existing flyout");
            panel.SettingsFlyout.Visibility = Visibility.Visible;
            panel.Root.UpdateLayout();
            var settingsImage = Render(panel.Root, 392, 492);
            Save(Composite(settingsImage, Border(frames[16])), "vfx-settings.png");
            panel.SettingsFlyout.Visibility = Visibility.Collapsed;
            panel.Root.UpdateLayout();
            var panelImage = Render(panel.Root, 392, 492);
            Save(Composite(panelImage, Border(frames[16])), "vfx-panel.png");
            Save(dpiImage, "vfx-border-144.png");
            using var originalStream = typeof(NetherBorder).Assembly.GetManifestResourceStream("ArkCopyBuffer.Assets.Vfx.Nether.Nether_Hover_6.png")!;
            var originalFrame = BitmapFrame.Create(originalStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            originalFrame.Freeze();
            var before = Border(originalFrame);
            var after = Border(frames[6]);
            var comparison = new System.Windows.Media.DrawingVisual();
            using (var dc = comparison.RenderOpen())
            {
                int row = 0;
                foreach (var colour in new[] { System.Windows.Media.Colors.White,
                             System.Windows.Media.Color.FromRgb(27, 66, 113), System.Windows.Media.Color.FromRgb(24, 24, 24) })
                {
                    for (int column = 0; column < 2; column++)
                    {
                        var background = new System.Windows.Media.SolidColorBrush(colour);
                        var image = Composite(panelImage, column == 0 ? before : after, background);
                        dc.PushClip(new System.Windows.Media.RectangleGeometry(new Rect(column * 520, row * 204, 520, 204)));
                        dc.DrawRectangle(background, null, new Rect(column * 520, row * 204, 520, 204));
                        dc.DrawText(new System.Windows.Media.FormattedText(column == 0 ? "Original alpha" : "Softened alpha",
                            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, App.TextTypeface, 14,
                            row == 0 ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White, 1), new Point(column * 520 + 12, row * 204 + 3));
                        dc.DrawImage(image, new Rect(column * 520, row * 204 + 24, image.PixelWidth, image.PixelHeight));
                        dc.Pop();
                    }
                    row++;
                }
            }
            Save(Render(comparison, 1040, 612), "vfx-alpha-comparison.png");
            var animation = new GifBitmapEncoder();
            foreach (var original in frames)
            {
                animation.Frames.Add(BitmapFrame.Create(Composite(panelImage, Border(original))));
            }
            using var gif = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "vfx-flame.gif"));
            animation.Save(gif);
        }
        finally
        {
            Motion.Enabled = motion;
            panel.Close();
        }
        check(!panel._vfxTimer.IsEnabled && panel._vfxWindow == null && !panel._vfxReady, "VFX: closing releases the overlay and playback timer");
    }
}
