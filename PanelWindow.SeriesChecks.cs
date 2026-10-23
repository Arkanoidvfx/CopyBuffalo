using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

partial class PanelWindow
{
    internal static void SelfTestImageSeries(string directory, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "image-series");
        var store = new HistoryStore(Path.Combine(path, "history.dat"));
        var now = DateTime.UtcNow;
        store.Add("обычный текст", null, null, now);
        var text = store.Items[0];
        var panel = new PanelWindow(store, new SymbolCatalog(Path.Combine(path, "symbols.json")));
        var images = new List<ClipItem>();
        try
        {
            panel.BeginImageSeries();
            check(!panel.SeriesActive, "серия: без картинок остаётся обычная история");
            foreach (var color in new[] { "#A35118", "#226872", "#704B9E", "#315FA2", "#447347" })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle((Brush)new BrushConverter().ConvertFromString(color)!, null, new Rect(0, 0, 220, 100));
                    dc.DrawText(new FormattedText($"Снимок {images.Count + 1}", CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                        new Typeface((FontFamily)panel.FindResource("TextFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 20, Brushes.White, 1), new Point(16, 18));
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), null, new Rect(16, 58, 140, 22), 4, 4);
                }
                var bitmap = new RenderTargetBitmap(220, 100, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual); bitmap.Freeze();
                var (bgra, hash) = ImageFiles.Normalize(bitmap);
                ImageFiles.Write(store.ImagesDir, hash, bgra);
                store.AddImage(hash, 220, 100, now.AddSeconds(images.Count + 1), sourceApp: images.Count < 3 ? "Sample A" : "Sample B");
                var item = store.Items.Single(i => i.ImageHash == hash); item.ImageDir = store.ImagesDir; images.Add(item);
            }
            panel.BeginImageSeries();
            check(panel.SeriesActive && panel.List.Items.Count == 5 && panel.List.Items.Cast<ClipItem>().All(i => i.IsVisualMedia) && panel.Search.IsEnabled && panel.TabPinned.IsEnabled,
                "выбор: видны только картинки и видео, поиск и вкладки остаются доступны");
            panel.ToggleSeriesItem(text); panel.ToggleSeriesItem(new ClipItem { ImageHash = "missing" });
            check(panel._seriesQueue.Count == 0, "серия: текст и отсутствующие записи не выбираются");
            panel._seriesDragItem = images[0];
            panel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonDownEvent });
            check(panel._seriesDragItem == null, "серия: новый клик сбрасывает прежний источник перетаскивания");
            panel.EndImageSeries();
            panel.RenderSeriesPreview("alt-selection-default.png", check);
            int ordinaryRequests = 0;
            panel.PasteRequested += (_, _) => ordinaryRequests++;
            panel.BeginMergeSelection();
            check(panel.HandleImageSelectionMouseDown(images[4], ModifierKeys.Alt) && panel.SeriesActive && images[4].SeriesNumber == 1 && ordinaryRequests == 0 && !panel._collectingMerge,
                "Alt+клик: первая картинка отмечается и завершает пустой выбор текста без вставки");
            var mouseUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent };
            panel.FinishImageSelectionClick(mouseUp);
            check(mouseUp.Handled && !panel._imageSelectionClick && ordinaryRequests == 0,
                "Alt+клик: отпускание мыши после отпускания Alt не вставляет запись");
            panel.BeginMergeSelection(); panel.FinishMergeSelection(open: true);
            check(panel.SeriesActive && panel._mergeWindow == null && !panel._collectingMerge, "Alt: повторное нажатие и отпускание не открывает объединение текста и не сбрасывает медиа");
            panel.HandleImageSelectionMouseDown(images[4], ModifierKeys.Alt);
            panel.FinishImageSelectionClick(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent });
            check(!panel.SeriesActive && !images[4].InSeries && panel.SeriesBar.Visibility == Visibility.Collapsed && panel.List.Items.Contains(text), "Alt+клик: последняя отметка возвращает смешанную историю и скрывает действия");
            check(!panel.HandleImageSelectionMouseDown(images[0], ModifierKeys.None) && !panel.HandleImageSelectionMouseDown(images[0], ModifierKeys.Control)
                && !panel.HandleImageSelectionMouseDown(images[0], ModifierKeys.Control | ModifierKeys.Alt),
                "Alt+клик: обычный клик, Ctrl и AltGr не включают выбор");
            panel.RequestPaste(images[0], false);
            check(ordinaryRequests == 1 && !panel.SeriesActive, "обычный клик: вставка одной картинки сохранена");
            foreach (var image in images.AsEnumerable().Reverse()) panel.ToggleSeriesItem(image);
            check(panel._seriesQueue.SequenceEqual(images) && images.Select(i => i.SeriesNumber).SequenceEqual([1, 2, 3, 4, 5]),
                "серия: выбор с конца истории даёт порядок от первого снимка к последнему и номера 1–5");
            panel.RequestPaste(text, false);
            check(ordinaryRequests == 2 && panel._seriesQueue.SequenceEqual(images), "Alt+выбор: обычная вставка текста не меняет отмеченные картинки");
            panel.ToggleSeriesItem(images[1]);
            check(images[1].SeriesNumber == 0 && images[2].SeriesNumber == 2, "серия: снятие отметки убирает её и перенумеровывает остаток");
            panel.ToggleSeriesItem(images[1]);
            var oldOrder = panel.List.Items.Cast<ClipItem>().ToArray();
            store.Add("новое копирование", null, null, now.AddMinutes(1), "Sample A");
            check(panel.List.Items.Cast<ClipItem>().SequenceEqual(oldOrder) && panel._seriesHistory!.Any(i => i.Text == "новое копирование"),
                "Alt+выбор: новый текст скрыт до снятия выбора, прежний порядок медиа сохранён");
            panel.MoveSeriesItem(images[4], 0);
            check(panel._seriesQueue[0] == images[4] && images[4].SeriesNumber == 1, "серия: ручная перестановка меняет номера и первую картинку");
            panel.MoveSeriesItem(images[4], 4);
            var snapshot = panel.List.Items.Cast<ClipItem>().ToArray();
            store.Touch(images[0].Id, now.AddMinutes(1));
            panel.ResetSelection();
            check(panel.List.Items.Cast<ClipItem>().SequenceEqual(snapshot) && panel._seriesQueue.SequenceEqual(images),
                "серия: собственная вставка и сброс выделения не перемешивают список или очередь");
            using (var json = JsonDocument.Parse(JsonSerializer.Serialize(images[0])))
                check(!json.RootElement.TryGetProperty("SeriesNumber", out _) && !json.RootElement.TryGetProperty("SeriesInserted", out _) && !json.RootElement.TryGetProperty("InSeries", out _),
                    "серия: отметки и прогресс не попадают в сохраняемую историю");
            panel.RenderSeriesPreview("image-series-select.png", check);
            var row = (ListBoxItem)panel.List.ItemContainerGenerator.ContainerFromItem(images[0]);
            check(row.Template.FindName("SeriesBadge", row) is Border { Visibility: Visibility.Visible }, "серия: настоящая карточка показывает номер выбора");
            foreach (string style in new[] { "ClipItemContainer", "PinTileContainer", "PinRowContainer" })
            {
                var recycled = new ListBoxItem { Style = (Style)panel.FindResource(style), DataContext = images[0] };
                recycled.ApplyTemplate(); recycled.Measure(new Size(330, 140)); recycled.Arrange(new Rect(0, 0, 330, 140));
                recycled.UpdateLayout(); panel.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                check(recycled.Template.FindName("SeriesBadge", recycled) is Border { Visibility: Visibility.Visible }, "Alt+клик: номер виден в " + style);
                recycled.DataContext = text; recycled.UpdateLayout();
                panel.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                check(recycled.Template.FindName("SeriesBadge", recycled) is Border { Visibility: Visibility.Collapsed }
                    && recycled.Template.FindName("Bd", recycled) is Border { Opacity: 1 }, "выбор: переиспользованная карточка очищает номер в " + style);
            }
            (Func<bool> Current, Action<bool> Complete)? ticket = null;
            panel.SetKindFilter("image"); panel.RenderSeriesPreview("alt-selection-images.png", check); panel.SetKindFilter(null);
            int requests = 0;
            ClipItem? requested = null;
            panel.PasteRequested += (item, _) => { requests++; requested = item; ticket = panel.BeginSeriesPaste(item); };
            panel.SetSourceFilter("Sample B");
            panel.PasteNextSeries();
            var first = ticket!.Value;
            check(requested == images[0] && first.Current() && !panel.SeriesNext.IsEnabled && panel._sourceFilter == "Sample B",
                "выбор: вставка берёт начало очереди независимо от фильтра и блокирует повтор");
            panel.PasteNextSeries();
            check(requests == 1, "серия: повторное нажатие во время подготовки не пропускает картинку");
            first.Complete(false);
            check(panel.NextSeriesItem == images[0] && panel.SeriesNext.IsEnabled && !images[0].SeriesInserted,
                "серия: ошибка или только копирование оставляет текущую картинку для повтора");
            panel.SetSourceFilter(null);
            panel.PasteNextSeries(); ticket!.Value.Complete(true); ticket.Value.Complete(true);
            check(images[0].SeriesInserted && panel.NextSeriesItem == images[1] && panel.List.SelectedItem == images[1]
                && panel.SeriesStatus.Text == "Вставлено 1 из 5", "серия: только успешная вставка один раз продвигает счётчик и выделение");
            panel.RenderSeriesPreview("image-series-progress.png", check);
            var manual = panel.BeginSeriesPaste(images[4]);
            check(manual != null && manual.Value.Current(), "Alt+выбор: обычная вставка отмеченной картинки работает и вне очереди");
            manual!.Value.Complete(false);
            panel.List.SelectedItem = images[4]; panel.RestoreSeriesSelection(advance: true);
            check(panel.List.SelectedItem == images[1] && panel.SeriesQueue.SelectedItem == images[1], "серия: повторное открытие возвращает к следующей картинке");
            panel.MoveSeriesItem(images[4], 0);
            check(panel._seriesQueue[0] == images[4], "выбор: после вставки можно переставлять оставшиеся картинки");
            panel.MoveSeriesItem(images[4], 4); panel.ToggleSeriesItem(images[4]); panel.ToggleSeriesItem(images[4]);
            check(panel._seriesQueue.SequenceEqual(images), "выбор: Alt+кликом можно менять состав после частичной вставки");
            panel.PasteNextSeries(); var removed = ticket!.Value;
            store.Remove(images[1]);
            check(!removed.Current(), "серия: удаление источника отменяет ожидающую вставку");
            removed.Complete(true);
            check(!images[1].SeriesInserted && panel.NextSeriesItem == images[2] && panel._seriesPending == null,
                "серия: удалённая картинка не засчитывается и не блокирует остаток");
            panel.PasteNextSeries(); var stale = ticket!.Value;
            panel.EndImageSeries(); panel.BeginImageSeries(); panel.ToggleSeriesItem(images[2]);
            stale.Complete(true);
            check(!stale.Current() && !images[2].SeriesInserted && panel._seriesPending == null, "серия: запоздалое завершение не меняет новую серию");
            panel.PasteNextSeries(); ticket!.Value.Complete(true);
            check(panel.NextSeriesItem == null && !panel.SeriesActive && !panel.SeriesNext.IsEnabled, "серия: успешная вставка последнего файла выключает выбор");
            panel.EndImageSeries();
            check(images.All(i => i.SeriesNumber == 0 && !i.SeriesInserted) && panel.Search.IsEnabled && panel.TabPinned.IsEnabled && ReferenceEquals(panel.List.ItemsSource, panel._view),
                "серия: закрытие очищает только отметки и возвращает обычную историю");
            panel.RequestPaste(text, false);
            check(requested == text && ticket == null, "серия: обычная вставка после выхода работает прежним путём");
            var ordered = new[] { images[4], images[2], images[3] };
            var exported = Paster.PrepareImageSeries(ordered, store.ImagesDir);
            var exportDir = Path.GetDirectoryName(exported[0])!;
            try
            {
                check(exported.Select(Path.GetFileName).SequenceEqual(["0001.png", "0002.png", "0003.png"])
                    && exported.Select((p, i) => File.ReadAllBytes(p).SequenceEqual(ImageFiles.ReadFull(store.ImagesDir, ordered[i].ImageHash!))).All(b => b),
                    "все картинки: пронумерованные PNG сохраняют байты, прозрачность и заданный порядок");
                var payload = Paster.FileData(exported);
                check(payload.GetFileDropList().Cast<string>().SequenceEqual(exported) && !payload.GetDataPresent(DataFormats.UnicodeText),
                    "все картинки: одна пачка FileDrop содержит файлы по порядку, без вставки путей текстом");
                var before = Directory.GetDirectories(Path.GetDirectoryName(exportDir)!, "series-*").Order().ToArray();
                bool failed = false;
                try { Paster.PrepareImageSeries([ordered[0], new ClipItem { ImageHash = "series-missing" }], store.ImagesDir); }
                catch (FileNotFoundException) { failed = true; }
                check(failed && Directory.GetDirectories(Path.GetDirectoryName(exportDir)!, "series-*").Order().SequenceEqual(before),
                    "все картинки: недоступный второй источник удаляет только новую неполную пачку");
                foreach (var invalid in new ClipItem[][] { [], [text], [ordered[0], ordered[0]] })
                {
                    bool rejected = false;
                    try { Paster.PrepareImageSeries(invalid, store.ImagesDir); } catch (InvalidDataException) { rejected = true; }
                    check(rejected, "все картинки: пустая очередь, текст и повторный Id отклоняются до подготовки файлов");
                }
                bool staleBatch = false;
                try { Paster.SetImageSeriesClipboardAsync(ordered, store.ImagesDir, () => false).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { staleBatch = true; }
                check(staleBatch, "все картинки: устаревший запрос не подготавливает файлы и не меняет буфер");
                var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
                ImageFiles.WriteGif(store.ImagesDir, "SERIES-GIF", gif, ImageFiles.Decode(gif));
                var gifPaths = Paster.PrepareImageSeries([new ClipItem { ImageHash = "SERIES-GIF", IsGif = true }], store.ImagesDir);
                try { check(gifPaths[0].EndsWith("0001.gif", StringComparison.Ordinal) && File.ReadAllBytes(gifPaths[0]).SequenceEqual(gif), "все картинки: GIF остаётся анимированным исходным файлом с расширением gif"); }
                finally { Directory.Delete(Path.GetDirectoryName(gifPaths[0])!, recursive: true); }
            }
            finally { Directory.Delete(exportDir, recursive: true); }

            IReadOnlyList<ClipItem>? batch = null;
            int batchRequests = 0;
            panel.SeriesPasteAllRequested += items => { batchRequests++; batch = items.ToArray(); ticket = panel.BeginSeriesPaste(items); };
            panel.BeginImageSeries();
            foreach (var image in ordered) panel.ToggleSeriesItem(image);
            panel.MoveSeriesItem(images[4], 0);
            panel.PasteSeries(all: true);
            check(batch!.SequenceEqual(ordered) && ticket!.Value.Current() && !panel.SeriesNext.IsEnabled && !panel.SeriesAll.IsEnabled,
                "все картинки: отдельный запрос передаёт снимок всей очереди в ручном порядке и блокирует обе кнопки");
            panel.PasteSeries(all: true); panel.PasteNextSeries();
            check(batchRequests == 1, "все картинки: повторные нажатия не запускают вторую пачку");
            ticket!.Value.Complete(false);
            check(ordered.All(i => !i.SeriesInserted) && panel.SeriesAll.IsEnabled, "все картинки: ошибка оставляет всю пачку для повтора");
            panel.PasteNextSeries(); ticket!.Value.Complete(true);
            panel.PasteSeries(all: true);
            check(batch!.SequenceEqual(ordered.Skip(1)), "все картинки: уже вставленная картинка не попадает в следующую пачку");
            ticket!.Value.Complete(true); ticket.Value.Complete(true);
            check(!panel.SeriesActive && ordered.All(i => !i.InSeries && !i.SeriesInserted) && !panel.SeriesAll.IsEnabled,
                "все картинки: успешная пачка выключает выбор и очищает все отметки");
            panel.EndImageSeries(); panel.BeginImageSeries();
            foreach (var image in ordered) panel.ToggleSeriesItem(image);
            panel.PasteSeries(all: true); var staleAll = ticket!.Value;
            store.Remove(images[3]);
            check(!staleAll.Current(), "все картинки: удаление любого источника отменяет всю ожидающую пачку");
            staleAll.Complete(true);
            check(ordered.All(i => !i.SeriesInserted) && panel.SeriesAll.IsEnabled, "все картинки: отменённая пачка не засчитывает оставшиеся картинки");
            panel.PasteSeries(all: true); staleAll = ticket!.Value;
            panel.EndImageSeries(); staleAll.Complete(true);
            check(!staleAll.Current() && images.All(i => !i.SeriesInserted), "все картинки: закрытие серии отменяет устаревший результат всей пачки");
            panel.BeginImageSeries(); panel.ToggleSeriesItem(images[0]); store.Remove(images[0]);
            check(panel._seriesQueue.Count == 0 && !panel.SeriesNext.IsEnabled, "серия: удаление выбранного изображения безопасно опустошает очередь");
            SelfTestKindFilter(directory, check);
            SelfTestMediaSelection(directory, check);
        }
        finally { panel.EndImageSeries(); panel.Close(); }
    }

    internal static void SelfTestMediaSelection(string directory, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "alt-media");
        var store = new HistoryStore(Path.Combine(path, "history.dat"));
        var now = DateTime.UtcNow;
        byte[] videoBytes = [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4];
        var videoHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(videoBytes));
        ImageFiles.WriteMedia(store.ImagesDir, videoHash, videoBytes, null);
        store.AddMedia(videoHash, "video", "ролик.webm", videoBytes.Length, false, now);
        var video = store.Items[0];
        var panel = new PanelWindow(store, new SymbolCatalog(Path.Combine(path, "symbols.json")));
        try
        {
            panel.HandleImageSelectionMouseDown(video, ModifierKeys.Alt);
            check(panel.SeriesActive && video.InSeries && panel.List.Items.Cast<ClipItem>().SequenceEqual([video]), "Alt: выбор начинается с видео даже без картинок в истории");
            store.Add("текст рядом", null, null, now.AddSeconds(1));
            var text = store.Items[0];
            store.AddMedia("ALT-AUDIO", "audio", "sound.mp3", 4, false, now.AddSeconds(2));
            var audio = store.Items[0];
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 20, 60, 120, 255 }, 4);
            var (pixels, imageHash) = ImageFiles.Normalize(bitmap);
            ImageFiles.Write(store.ImagesDir, imageHash, pixels);
            store.AddImage(imageHash, 1, 1, now.AddSeconds(3));
            var image = store.Items[0];
            check(panel.List.Items.Cast<ClipItem>().SequenceEqual([image, video]) && !panel.List.Items.Contains(text) && !panel.List.Items.Contains(audio),
                "Alt: новые картинки доступны, текст и аудио скрыты при выборе медиа");
            foreach (var item in store.Items.ToArray()) store.TogglePin(item);
            panel.TabPinned.IsChecked = true; panel.RefreshViews();
            check(panel.PinnedList.Items.OfType<ClipItem>().All(i => i.IsVisualMedia) && panel.PinnedList.Items.OfType<ClipItem>().Count() == 2,
                "Alt: закреплённые скрывают всё кроме картинок и видео");
            panel.HandleImageSelectionMouseDown(image, ModifierKeys.Alt);
            check(panel._seriesQueue.SequenceEqual([video, image]), "Alt: картинки и видео выбираются вместе по времени копирования");
            panel.TabAll.IsChecked = true; panel.RefreshViews(); panel.RenderSeriesPreview("alt-media-selection.png", check);
            var queueVideo = (ListBoxItem)panel.SeriesQueue.ItemContainerGenerator.ContainerFromItem(video);
            check(queueVideo.FindDescendant<TextBlock>() is { Visibility: Visibility.Visible, Text.Length: > 0 }, "Alt: видео без миниатюры имеет видимый значок в очереди");
            var exported = Paster.PrepareImageSeries([video, image], store.ImagesDir);
            try
            {
                check(exported.Select(Path.GetFileName).SequenceEqual(["0001.webm", "0002.png"])
                    && File.ReadAllBytes(exported[0]).SequenceEqual(videoBytes)
                    && File.ReadAllBytes(exported[1]).SequenceEqual(ImageFiles.ReadFull(store.ImagesDir, imageHash)),
                    "все медиа: видео и PNG сохраняют исходные байты, расширения и порядок одной пачки");
                foreach (var invalid in new[] { audio, new ClipItem { MediaKind = "video", ImageHash = videoHash }, new ClipItem { MediaKind = "video", ImageHash = videoHash, FileName = "x.bad ext" } })
                {
                    var before = Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(exported[0]))!, "series-*").Order().ToArray();
                    bool rejected = false;
                    try { Paster.PrepareImageSeries([image, invalid], store.ImagesDir); } catch (InvalidDataException) { rejected = true; }
                    check(rejected && Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(exported[0]))!, "series-*").Order().SequenceEqual(before),
                        "все медиа: аудио и неверные расширения отклоняются без неполной пачки");
                }
            }
            finally { Directory.Delete(Path.GetDirectoryName(exported[0])!, recursive: true); }
            var ticket = panel.BeginSeriesPaste([video, image]); ticket!.Value.Complete(true);
            check(!panel.SeriesActive && !video.InSeries && !image.InSeries && !panel.SeriesAll.IsEnabled, "все медиа: успешная пачка картинок и видео автоматически выключает выбор");
            panel.EndImageSeries();
            check(panel.List.Items.Count == 4 && panel.List.Items.Contains(text) && panel.List.Items.Contains(audio), "Alt: снятие выбора возвращает текст и аудио без изменения записей");
            check(!panel.HandleImageSelectionMouseDown(text, ModifierKeys.Alt), "Alt: текстовый клик остаётся у объединения текста");
            panel.BeginMergeSelection(); panel.ToggleMergePart(text);
            panel.HandleImageSelectionMouseDown(video, ModifierKeys.Alt);
            check(!panel.SeriesActive && panel._mergeParts.SequenceEqual([text]), "Alt: выбор видео не уничтожает уже выбранный текст");
            panel.FinishMergeSelection(open: false);
        }
        finally { panel.EndImageSeries(); panel.FinishMergeSelection(open: false); panel.Close(); }
    }

    void RenderSeriesPreview(string name, Action<bool, string> check)
    {
        // Unshown virtualized lists do not run the normal visible-window recycling pass between renders.
        var source = List.ItemsSource; List.ItemsSource = null; List.ItemsSource = source;
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        Root.Measure(new Size(392, 492)); Root.Arrange(new Rect(0, 0, 392, 492)); Root.UpdateLayout();
        if (NextSeriesItem is { } next) { List.ScrollIntoView(next); Root.UpdateLayout(); }
        // Offscreen fixtures use the same encrypted thumbnails, without opening a window or touching the clipboard.
        void LoadThumbnails(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Image image && image.DataContext is ClipItem item) image.Source = item.Thumbnail;
                LoadThumbnails(child);
            }
        }
        LoadThumbnails(SeriesQueue); LoadThumbnails(List);
        Root.UpdateLayout();
        if (NextSeriesItem is { } ready) { List.ScrollIntoView(ready); Root.UpdateLayout(); }
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        Root.UpdateLayout();
        var bounds = SeriesNext.TransformToAncestor(Root).TransformBounds(new Rect(SeriesNext.RenderSize));
        var allBounds = SeriesAll.TransformToAncestor(Root).TransformBounds(new Rect(SeriesAll.RenderSize));
        check(bounds.Bottom <= Root.ActualHeight && bounds.Left >= 0 && bounds.Right <= Root.ActualWidth
            && allBounds.Bottom <= Root.ActualHeight && allBounds.Right <= Root.ActualWidth && allBounds.Left >= bounds.Right
            && List.ActualHeight >= 130 && SeriesQueue.Items.Count == _seriesQueue.Count, "серия: миниатюры, обе кнопки и история помещаются в панель 392×492");
        var bitmap = new RenderTargetBitmap(392, 492, 96, 96, PixelFormats.Pbgra32); bitmap.Render(Root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(file);
    }
}
