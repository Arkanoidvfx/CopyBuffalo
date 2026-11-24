using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArkCopyBuffer;

internal sealed partial class HistoryMapWindow
{
    readonly Border reader = new();
    readonly StackPanel readerActions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly ScrollViewer imageViewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly TextBlock imageState = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    Button imageZoom = null!;
    bool enlarged;
    Point panOrigin;
    Vector panOffset;
    string readingQuery = "";
    readonly TextBlock readingLabel = new() { FontWeight = FontWeights.SemiBold },
        readingContext = new() { TextTrimming = TextTrimming.CharacterEllipsis },
        positionLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 56, TextAlignment = TextAlignment.Center };
    readonly TextBox readingText = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly ContentControl readingMedia = new() { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly TextBlock readingTitle = new() { FontSize = 23, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly SemaphoreSlim originalDecode = new(1);
    CancellationTokenSource? readingLoad;
    readonly Button previousEntry, nextEntry;
    MapEntry? readingEntry, pinnedEntry;
    int selectedIndex = -1;

    void BuildReader(Grid host)
    {
        var rows = new Grid();
        rows.RowDefinitions.Add(new() { Height = GridLength.Auto }); rows.RowDefinitions.Add(new()); rows.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var caption = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var hint = new TextBlock { Text = T("Наведите для чтения · Нажмите для выбора"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var footer = new DockPanel();
        DockPanel.SetDock(readerActions, Dock.Right); footer.Children.Add(readerActions);
        imageZoom = Button("Увеличить вдвое", ToggleReadingZoom);
        imageZoom.ToolTip = imageZoom.Content; imageZoom.Content = "2×"; imageZoom.Visibility = Visibility.Collapsed;
        ToolTipService.SetInitialShowDelay(imageZoom, 0); readerActions.Children.Add(imageZoom);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(0, 0, 8, 0);
        footer.Children.Add(hint); Grid.SetRow(footer, 2); rows.Children.Add(footer);
        readingLabel.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); readingLabel.Margin = new Thickness(10, 4, 0, 0);
        DockPanel.SetDock(readingLabel, Dock.Right); caption.Children.Add(readingLabel);
        readingContext.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var names = new StackPanel(); names.Children.Add(readingTitle); names.Children.Add(readingContext);
        caption.Children.Add(names); rows.Children.Add(caption);
        readingText.Style = (Style)FindResource("LocalizedTextBox");
        readingText.Background = Brushes.Transparent; readingText.BorderThickness = new Thickness(0);
        readingText.Padding = new Thickness(0); readingText.FontSize = 18;
        readingText.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        readingText.IsInactiveSelectionHighlightEnabled = true;
        AutomationProperties.SetName(readingText, T("Просмотр"));
        var content = new Grid();
        imageViewport.Content = readingMedia;
        imageViewport.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!enlarged || e.OriginalSource is not Image) return;
            panOrigin = e.GetPosition(imageViewport);
            panOffset = new Vector(imageViewport.HorizontalOffset, imageViewport.VerticalOffset);
            e.Handled = imageViewport.CaptureMouse();
        };
        imageViewport.MouseMove += (_, e) =>
        {
            if (!imageViewport.IsMouseCaptured) return;
            var offset = panOffset + (panOrigin - e.GetPosition(imageViewport));
            imageViewport.ScrollToHorizontalOffset(offset.X); imageViewport.ScrollToVerticalOffset(offset.Y);
        };
        imageViewport.MouseLeftButtonUp += (_, _) => { if (imageViewport.IsMouseCaptured) imageViewport.ReleaseMouseCapture(); };
        imageViewport.Visibility = imageState.Visibility = Visibility.Collapsed;
        content.Children.Add(imageViewport); content.Children.Add(readingText); content.Children.Add(imageState);
        Grid.SetRow(content, 1); rows.Children.Add(content);
        reader.Child = rows; reader.Padding = new Thickness(20, 14, 20, 12); reader.CornerRadius = new CornerRadius(8);
        reader.SetResourceReference(Border.BackgroundProperty, "CardFill");
        reader.BorderThickness = new Thickness(1); reader.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
        Grid.SetRow(reader, 1); host.Children.Add(reader);
        host.SizeChanged += (_, _) =>
        {
            hint.Visibility = host.ActualHeight >= 420 && host.ActualWidth >= 620 ? Visibility.Visible : Visibility.Collapsed;
        };
        host.MouseLeave += (_, _) => ShowReading(pinnedEntry);
        Deactivated += (_, _) => ShowReading(pinnedEntry);
    }

    void ShowReading(MapEntry? entry)
    {
        bool pinned = ReferenceEquals(entry, pinnedEntry);
        readingLabel.Text = T(pinned ? "Выбрано" : "Просмотр");
        if (ReferenceEquals(entry, readingEntry) && readingQuery == appliedQuery) return;
        if (copyFeedback.IsEnabled) ResetCopyFeedback();
        readingLoad?.Cancel();
        if (enlarged) ToggleReadingZoom();
        imageViewport.Visibility = imageZoom.Visibility = imageState.Visibility = Visibility.Collapsed;
        readingEntry = entry;
        readingQuery = appliedQuery;
        readingTitle.Text = entry == null ? "" : entry.Sensitive ? T("Защищённое содержимое") : entry.Kind == "image" ? entry.Preview : KindLabel(entry.Kind);
        readingContext.Text = entry == null ? "" : SourceLabel(entry.Source) + "   ·   " + entry.Date(First).ToString("g", Culture);
        readingText.FontFamily = (FontFamily)FindResource(entry?.Mono == true ? "MonoFont" : "TextFont");
        readingText.Text = entry == null ? "" : entry.Sensitive ? "********" : entry.Hash == null ? entry.Text : entry.Preview;
        readingText.Select(0, 0);
        readingText.ScrollToHome();
        readingMedia.Content = null;
        readingText.Visibility = Visibility.Visible;
        if (entry is { Sensitive: false, Kind: "image", Hash: not null })
        {
            var image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            readingMedia.Content = image;
            readingText.Visibility = Visibility.Collapsed;
            imageViewport.Visibility = Visibility.Visible;
            LoadReadingImage(entry, image);
        }
        else if (entry is { Sensitive: false } && readingQuery.Length > 0)
        {
            int index = readingText.Text.IndexOf(readingQuery, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return;
            readingText.Select(index, readingQuery.Length);
            string query = readingQuery;
            Dispatcher.InvokeAsync(() =>
            {
                if (closed || !ReferenceEquals(readingEntry, entry) || readingQuery != query || !readingText.IsVisible) return;
                int line = readingText.GetLineIndexFromCharacterIndex(index);
                if (line >= 0) readingText.ScrollToLine(line);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    void ToggleReadingZoom()
    {
        if (readingMedia.Content is not Image image || image.Source == null) return;
        enlarged = !enlarged;
        image.Cursor = enlarged ? Cursors.Hand : null;
        if (!enlarged && imageViewport.IsMouseCaptured) imageViewport.ReleaseMouseCapture();
        image.Width = enlarged ? Math.Max(1, image.ActualWidth * 2) : double.NaN;
        image.Height = enlarged ? Math.Max(1, image.ActualHeight * 2) : double.NaN;
        image.StretchDirection = enlarged ? StretchDirection.Both : StretchDirection.DownOnly;
        imageViewport.HorizontalScrollBarVisibility = imageViewport.VerticalScrollBarVisibility = enlarged ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        imageZoom.Content = enlarged ? T("Вписать") : "2×";
        imageZoom.ToolTip = T(enlarged ? "Вписать" : "Увеличить вдвое");
        AutomationProperties.SetName(imageZoom, (string)imageZoom.ToolTip);
    }

    async void LoadReadingImage(MapEntry entry, Image image)
    {
        var live = store.Items.FirstOrDefault(i => i.Id == entry.Id);
        if (live == null || live.IsSensitive || !entry.ContentMatches(live)) return;
        using var load = new CancellationTokenSource();
        readingLoad = load;
        try
        {
            imageState.Text = T("Загрузка изображения…"); imageState.Visibility = Visibility.Visible;
            await RequestMapThumbnail(image, entry, null, () => !load.IsCancellationRequested && ReferenceEquals(readingEntry, entry));
            if (load.IsCancellationRequested) return;
            if (image.Source != null) imageState.Visibility = Visibility.Collapsed;
            await originalDecode.WaitAsync(load.Token);
            try
            {
                var bitmap = await PanelWindow.LoadPreviewImage(live, new Size(1920, 1200), load.Token);
                if (load.IsCancellationRequested || closed || !ReferenceEquals(readingEntry, entry) || invalid.Contains(entry.Id) || !entry.ContentMatches(live)) return;
                if (bitmap != null) image.Source = bitmap;
                if (image.Source == null) imageState.Text = T("Просмотр недоступен");
                else { imageState.Visibility = Visibility.Collapsed; imageZoom.Visibility = Visibility.Visible; }
            }
            finally { originalDecode.Release(); }
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        finally { if (ReferenceEquals(readingLoad, load)) readingLoad = null; }
    }

    Button StepButton(int direction)
    {
        var button = Button(direction < 0 ? "Предыдущая запись" : "Следующая запись", () => StepEntry(direction));
        button.ToolTip = button.Content;
        ToolTipService.SetInitialShowDelay(button, 0);
        button.Content = new TextBlock { Text = direction < 0 ? "\uE76B" : "\uE76C", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12 };
        return button;
    }

    void StepEntry(int direction)
    {
        int index = selectedIndex + (timelineMode ? -direction : direction);
        if (overview || index < 0 || index >= nodes.Length) return;
        if (!timelineMode) canvas.Focus();
        Select(nodes[index].Entry.Id);
        CenterOn(selected);
    }
}
