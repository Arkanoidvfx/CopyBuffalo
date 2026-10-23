using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArkCopyBuffer;

static class OcrChecks
{
    internal static void SelfTest(string dir, Action<bool, string> check)
    {
        string Fix(string s) => OcrText.NormalizeScripts(s, out _, out _);
        check(Fix("СоруBuffalo imаgе_2026.png") == "CopyBuffalo image_2026.png", "OCR: похожие кириллические буквы в латинском слове исправлены");
        check(Fix("Pусский тeкст") == "Русский текст", "OCR: похожие латинские буквы в русском слове исправлены");
        check(Fix("Pусскии\u0306") == "Русский", "OCR: составные Unicode-буквы нормализуются перед коррекцией");
        check(Fix(@"D:\Проекты\копия.png") == @"D:\Проекты\копия.png", "OCR: настоящие русские имена папок не латинизируются");
        var ambiguous = OcrText.NormalizeScripts("СОС АB GitКоманда сһеск Русскiй", out _, out bool warning);
        check(ambiguous == "СОС АB GitКоманда сһеск Русскiй" && warning, "OCR: неоднозначные и настоящие смешанные слова сохранены и отмечены");
        var code = OcrText.NormalizeScripts(@"С++ С# С:\temp\сос_1", out _, out warning);
        check(code == @"С++ С# С:\temp\сос_1" && warning, "OCR: полностью похожие буквы в коде и путях требуют проверки без угадывания");
        OcrText.Region R(string text, double x, double y, double width = 180, double confidence = .99) => new(text, confidence, x, y, width, 20);
        var columns = new[] {R("right two",500,80),R("left one",20,30),R("right one",500,30),R("left two",20,80)};
        var ordered = OcrText.Build(columns);
        check(ordered.Text == "left one\r\nleft two\r\nright one\r\nright two" && ordered.Columns, "OCR: колонки читаются сверху вниз, слева направо");
        var banners = OcrText.Build([R("Heading",0,0,900), .. columns, R("Footer",0,140,900)]);
        check(banners.Text == "Heading\r\nleft one\r\nleft two\r\nright one\r\nright two\r\nFooter", "OCR: общая шапка и подвал не ломают порядок колонок");
        var three = OcrText.Build([.. columns, R("third one",760,30,100), R("third two",760,80,100)]);
        check(three.Text.EndsWith("right one\r\nright two\r\nthird one\r\nthird two"), "OCR: три колонки упорядочиваются рекурсивно");
        var fragments = OcrText.Build([R("world",100,21,70),R("second",20,60),R("hello",20,20,60)]);
        check(fragments.Text == "hello world\r\nsecond", "OCR: фрагменты одной строки собираются по X");
        check(OcrText.Build([R("uncertain",20,20,180,.5)]).ReviewNeeded, "OCR: низкая уверенность требует проверки");
        check(OcrText.Build([]).Text.Length == 0, "OCR: пустой результат остаётся пустым");
        bool Refuses(string json)
        {
            try { OcrText.Parse(json); return false; }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException) { return true; }
        }
        check(Refuses("{\"width\":100000,\"height\":100000,\"regions\":[]}"), "OCR: огромные размеры ответа отклонены");
        check(Refuses("{\"width\":960,\"height\":320,\"regions\":[{\"text\":\"bad\",\"confidence\":2,\"box\":[0,0,1,1]}]}"), "OCR: неверная уверенность отклонена");
        check(Refuses("{\"width\":960,\"height\":320,\"regions\":[{\"text\":\"bad\",\"confidence\":1,\"box\":[0,0,-1,1]}]}"), "OCR: неверная геометрия отклонена");
        check(ScreenshotOcr.ReadLimitedAsync(new StringReader("abc"), 3, CancellationToken.None).GetAwaiter().GetResult() == "abc", "OCR: чтение ограничено размером ответа");
        try { ScreenshotOcr.ReadLimitedAsync(new StringReader("abcd"),3,CancellationToken.None).GetAwaiter().GetResult(); check(false,"OCR: переполнение ответа"); }
        catch (InvalidDataException) { check(true,"OCR: слишком большой ответ отклонён"); }
        try { ScreenshotOcr.ReadImageAsync(dir,"../outside",CancellationToken.None).GetAwaiter().GetResult();check(false,"OCR: выход из каталога"); }
        catch (ScreenshotOcr.Failure) { check(true,"OCR: путь за пределы каталога изображений отклонён"); }
        var image = new ClipItem {ImageHash="synthetic",Recognizing=true};
        check(image.Working && !JsonSerializer.Serialize(image).Contains("Recognizing"), "OCR: индикатор работает, состояние обработки не хранится на диске");
        var store = new HistoryStore(Path.Combine(dir,"ocr-menu","history.dat"));
        var panel = new PanelWindow(store,new SymbolCatalog(Path.Combine(dir,"ocr-symbols.json")));
        check(!panel.OcrEntry(image).IsEnabled && panel.OcrEntry(image).Header?.ToString() == "Распознаётся текст…", "OCR: повторный запуск на обрабатываемой карточке недоступен");
        check(!panel.TranslateImageEntry(image).IsEnabled, "Перевод: повторный запуск во время обработки недоступен");
        image.Recognizing=false;
        int invoked=0;
        panel.OcrRequested += i => {if (ReferenceEquals(i,image)) invoked++;};
        panel.OcrEntry(image).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(invoked==1 && !image.Working,"OCR: меню вызывает распознавание правильной записи без реального ввода");
        int translated=0, pasted=0;
        panel.TranslateImageRequested += i => { if (ReferenceEquals(i,image)) translated++; };
        panel.PasteRequested += (_, _) => pasted++;
        panel.TranslateImageEntry(image).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(translated==1, "Перевод: меню передаёт правильную картинку");
        var menu = new ContextMenu { PlacementTarget=panel.List, Placement=PlacementMode.MousePoint };
        panel.PrepareMenu(menu);
        check(menu.Placement==PlacementMode.MousePoint && menu.PlacementTarget==panel.List && menu.CustomPopupPlacementCallback==null
            && menu.HorizontalOffset==-12 && menu.VerticalOffset==-4, "Меню: правый клик сохраняет привязку к курсору с поправкой на тень");
        var anchored = new ContextMenu { PlacementTarget=panel.List, Placement=PlacementMode.Bottom };
        panel.PrepareMenu(anchored);
        check(anchored.Placement==PlacementMode.Custom && anchored.CustomPopupPlacementCallback!=null, "Меню: кнопки шапки сохраняют адаптивное боковое размещение");
        var templates = new[] {panel.List.ItemTemplate, (DataTemplate)panel.FindResource("PinRowTemplate"), (DataTemplate)panel.FindResource("PinTileTemplate")};
        foreach (var template in templates)
        {
            var content = new ContentControl { Content=image, ContentTemplate=template };
            void Layout() { content.Measure(new Size(340,160)); content.Arrange(new Rect(0,0,340,160)); content.UpdateLayout(); }
            Layout();
            var actions=Descendants(content).OfType<StackPanel>().Single(p=>p.Name=="ImageActions");
            var buttons=Descendants(actions).OfType<Button>().ToArray();
            check(actions.Visibility==Visibility.Visible && buttons.Length==2 && buttons.All(b=>b.ToolTip!=null), "Карточки: OCR и перевод доступны в каждом представлении картинки");
            buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            buttons[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check(invoked>1 && translated>1 && pasted==0, "Карточки: прямые кнопки запускают обработку без вставки");
            image.Recognizing=true; Layout();
            check(buttons.All(b=>!b.IsEnabled), "Карточки: обе кнопки блокируются во время обработки");
            image.Recognizing=false;
            content.Content=new ClipItem {Text="Synthetic text"}; Layout();
            var textActions=Descendants(content).OfType<StackPanel>().Single(p=>p.Name=="ImageActions");
            check(textActions.Visibility==Visibility.Collapsed && !Descendants(content).OfType<Button>().Any(b=>b.Content?.ToString()=="\uE712"), "Карточки: текст без кнопок OCR, перевода и троеточия");
        }
        string Response(string? finish, string? text) => JsonSerializer.Serialize(new { choices=new[] {new {finish_reason=finish,message=new {content=text}}} });
        check(PromptCompressor.ReadAnswer(Response("stop","Перевод 42"))=="Перевод 42", "Модель: полный ответ принят без изменения чисел");
        foreach (string? finish in new string?[] {"length","content_filter","tool_calls",null})
        {
            try { PromptCompressor.ReadAnswer(Response(finish,"Неполный текст")); check(false,"Модель: незавершённый ответ принят"); }
            catch (PromptCompressor.Failure) { check(true,"Модель: незавершённый ответ отклонён"); }
        }
        try { PromptCompressor.ReadAnswer(Response("stop","<think>hidden</think> ")); check(false,"Модель: пустой ответ принят"); }
        catch (PromptCompressor.Failure) { check(true,"Модель: пустой ответ отклонён"); }
        RenderActions(panel, dir, check);
        CheckImageTranslation(dir, check);
        panel.ShowTranslatedImageResult(true, false);
        check(panel.InfoMessage.Text.Length == 0 && panel.InfoActions.Visibility == Visibility.Collapsed
            && panel.InfoClose.Visibility == Visibility.Collapsed, "Перевод: компактное сообщение без редактора текста");
        panel.Close();
    }

    static void CheckImageTranslation(string dir, Action<bool,string> check)
    {
        var regions = new[] {new OcrText.Region("Events",.99,32,14,60,18), new OcrText.Region("Server Boosts",.99,32,46,100,18)};
        var result = OcrText.Build(regions) with { Width=260,Height=90 };
        check(result.Regions.SequenceEqual(regions), "Перевод: OCR сохраняет отдельные области и геометрию");
        check(PromptCompressor.ReadTranslations("{\"translations\":[\"События\",\"Бусты сервера\"]}",2).Length==2, "Перевод: JSON сохраняет соответствие надписей");
        foreach (string json in new[] {"{\"translations\":[\"События\"]}","{\"translations\":[\"События\",\"\"]}","{\"translations\":[\"События\",\"Бусты\",\"Лишнее\"]}"})
        {
            try { PromptCompressor.ReadTranslations(json,2); check(false,"Перевод: неверный набор принят"); }
            catch (PromptCompressor.Failure) { check(true,"Перевод: пропущенные, пустые и лишние надписи отклонены"); }
        }
        foreach (bool dark in new[] {false,true})
        {
            var visual=new DrawingVisual();
            Color background=dark ? Color.FromRgb(43,45,49) : Colors.White;
            using (var drawing=visual.RenderOpen())
            {
                drawing.DrawRectangle(new SolidColorBrush(background),null,new Rect(0,0,260,90));
                drawing.DrawRectangle(Brushes.CornflowerBlue,null,new Rect(7,16,12,12));
                foreach (var region in regions)
                    drawing.DrawText(new FormattedText(region.Text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,App.TextTypeface,14,dark ? Brushes.LightGray : Brushes.Black,1),new Point(region.X,region.Y));
            }
            var source=new RenderTargetBitmap(260,90,96,96,PixelFormats.Pbgra32); source.Render(visual);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var bytes=new MemoryStream(); encoder.Save(bytes);
            byte[] original=bytes.ToArray();
            var translated=TranslatedImage.Render(original,result,new[] {"События","Бусты сервера"},100000,CancellationToken.None);
            var normalized=ImageFiles.Normalize(translated);
            var output=new byte[260*90*4]; normalized.bgra.CopyPixels(output,260*4,0);
            int icon=(20*260+12)*4, outside=(80*260+240)*4;
            check(translated.PixelWidth==260 && translated.PixelHeight==90 && translated.IsFrozen, "Перевод: размеры сохранены, bitmap готов для передачи между потоками");
            check(output[icon]==Colors.CornflowerBlue.B && output[icon+2]==Colors.CornflowerBlue.R
                && output[outside]==background.B && output[outside+2]==background.R, "Перевод: значок и фон вне надписей не изменены");
            var unchanged=TranslatedImage.Render(original,result,regions.Select(r=>r.Text).ToArray(),100000,CancellationToken.None);
            check(ImageFiles.Normalize(unchanged).hash==ImageFiles.Normalize(ImageFiles.Decode(original)).hash, "Перевод: неизменённые надписи не перерисовываются");
            var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(translated));
            using var encoded=new MemoryStream(); png.Save(encoded);
            var data=Paster.ImageClipboard(translated,encoded.ToArray());
            check(data.GetDataPresent("PNG") && data.ContainsImage() && !data.ContainsText(), "Перевод: буфер содержит PNG и bitmap, без текстовой подмены");
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory,dark ? "translated-dark.png" : "translated-light.png"),encoded.ToArray());
            try { TranslatedImage.Render(original,result,new[] {"События"},100000,CancellationToken.None); check(false,"Перевод: неполная картинка принята"); }
            catch (ScreenshotOcr.Failure) { check(true,"Перевод: неполная картинка отклонена"); }
            using var cancel=new CancellationTokenSource(); cancel.Cancel();
            try { TranslatedImage.Render(original,result,new[] {"События","Бусты сервера"},100000,cancel.Token); check(false,"Перевод: отмена рисования проигнорирована"); }
            catch (OperationCanceledException) { check(true,"Перевод: отмена рисования соблюдается"); }
        }
    }

    static void RenderActions(PanelWindow panel, string dir, Action<bool,string> check)
    {
        var source = new Border {Width=600, Height=300, Background=Brushes.White, Padding=new Thickness(32),
            Child=new TextBlock {Text="Release notes\n\nSave changes before closing.\nVersion 42 is ready.", FontSize=24, Foreground=Brushes.Black}};
        source.Measure(new Size(600,300)); source.Arrange(new Rect(0,0,600,300));
        var bitmap=new RenderTargetBitmap(600,300,96,96,PixelFormats.Pbgra32); bitmap.Render(source);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes=new MemoryStream(); encoder.Save(bytes);
        File.WriteAllBytes(Path.Combine(dir,"actions.thumb.dat"),ImageFiles.Seal(bytes.ToArray()));
        var image=new ClipItem {ImageHash="actions",ImageWidth=600,ImageHeight=300,ImageDir=dir,Pinned=true};
        var sheet=new StackPanel {Width=380, Background=(Brush)panel.FindResource("WindowSurface"), Margin=new Thickness(16)};
        foreach (var (template,style,width) in new[] {
            (panel.List.ItemTemplate,panel.List.ItemContainerStyle,348d),
            ((DataTemplate)panel.FindResource("PinRowTemplate"),(Style)panel.FindResource("PinRowContainer"),348d),
            ((DataTemplate)panel.FindResource("PinTileTemplate"),(Style)panel.FindResource("PinTileContainer"),104d)})
        {
            var card=new ListBoxItem {Content=image,ContentTemplate=template,Style=style,Width=width,IsSelected=true,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(12)};
            sheet.Children.Add(card);
        }
        sheet.Measure(new Size(412,double.PositiveInfinity)); sheet.Arrange(new Rect(0,0,412,sheet.DesiredSize.Height)); sheet.UpdateLayout();
        // Resolve preview images synchronously for this unshown visual tree.
        foreach (var previewImage in Descendants(sheet).OfType<Image>()) previewImage.Source=image.Thumbnail;
        sheet.Measure(new Size(412,double.PositiveInfinity)); sheet.Arrange(new Rect(0,0,412,sheet.DesiredSize.Height)); sheet.UpdateLayout();
        foreach (var card in sheet.Children.OfType<ListBoxItem>())
        {
            var buttons=Descendants(card).OfType<Button>().Where(b=>b.Visibility==Visibility.Visible).ToArray();
            check(buttons.Length==3 && buttons.All(b=> { var p=b.TranslatePoint(new Point(),card); return p.X>=0 && p.Y>=0 && p.X+b.ActualWidth<=card.ActualWidth && p.Y+b.ActualHeight<=card.ActualHeight; }),
                "Карточки: булавка, OCR и перевод помещаются без обрезки");
        }
        var preview=new RenderTargetBitmap(412,(int)Math.Ceiling(sheet.ActualHeight+32),96,96,PixelFormats.Pbgra32); preview.Render(sheet);
        var output=new PngBitmapEncoder(); output.Frames.Add(BitmapFrame.Create(preview));
        using var file=File.Create(Path.Combine(AppContext.BaseDirectory,"image-actions-preview.png")); output.Save(file);
    }

    internal static async Task TranslationAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        using var model=new PromptCompressor();
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        OcrText.Region Label(string text) => new(text,1,0,0,200,20);
        try { await model.TranslateRegionsAsync(new[] {Label("Save changes before closing.")},cancel.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        string source="Save changes before closing.\nVersion 42 is ready.\nhttps://example.com/help";
        using (var bitmap=new System.Drawing.Bitmap(1200,240))
        using (var graphics=System.Drawing.Graphics.FromImage(bitmap))
        using (var font=new System.Drawing.Font("Segoe UI",28,System.Drawing.FontStyle.Regular,System.Drawing.GraphicsUnit.Pixel))
        using (var png=new MemoryStream())
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.DrawString(source,font,System.Drawing.Brushes.Black,24,24);
            bitmap.Save(png,System.Drawing.Imaging.ImageFormat.Png);
            await File.WriteAllBytesAsync(Path.Combine(dir,"sample.dat"),ImageFiles.Seal(png.ToArray()));
        }
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var watch=System.Diagnostics.Stopwatch.StartNew();
        using var ocr=new ScreenshotOcr();
        byte[] image=await ScreenshotOcr.ReadImageAsync(dir,"sample",timeout.Token);
        OcrText.Result recognized;
        string[] translations;
        System.Windows.Media.Imaging.BitmapSource translated;
        try
        {
            recognized=await ocr.RecognizeAsync(image,80_000_000,timeout.Token);
            translations=await model.TranslateRegionsAsync(recognized.Regions,timeout.Token);
            translated=TranslatedImage.Render(image,recognized,translations,80_000_000,timeout.Token);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(image); }
        string result=string.Join("\n",translations);
        if (!result.Contains("42") || !result.Contains("https://example.com/help") || !result.Any(c=>c is >= 'А' and <= 'я')
            || !result.Contains("сохран",StringComparison.OrdinalIgnoreCase) || !result.Contains("закры",StringComparison.OrdinalIgnoreCase))
            throw new Exception("Translation did not preserve the synthetic sentence, number and URL: "+result);
        var store=new HistoryStore(Path.Combine(dir,"history.dat"));
        var normalized=ImageFiles.Normalize(translated);
        ImageFiles.Write(store.ImagesDir,normalized.hash,normalized.bgra);
        store.AddImage(normalized.hash,translated.PixelWidth,translated.PixelHeight,DateTime.UtcNow,sourceApp:"synthetic translation"); await store.SaveAsync();
        var restored=new HistoryStore(Path.Combine(dir,"history.dat")); restored.Load(DateTime.UtcNow);
        if (!restored.Items.Any(i=>i.IsImage && i.ImageHash==normalized.hash) || !File.Exists(Path.Combine(dir,"sample.dat"))) throw new Exception("Translated image did not survive encrypted save/load or source was removed.");
        var exported=new PngBitmapEncoder(); exported.Frames.Add(BitmapFrame.Create(translated));
        using (var output=File.Create(Path.Combine(dir,"translated.png"))) exported.Save(output);
        var pasted=Paster.ImageClipboard(translated,ImageFiles.ReadFull(restored.ImagesDir,normalized.hash));
        if (!pasted.ContainsImage() || !pasted.GetDataPresent("PNG") || pasted.ContainsText()) throw new Exception("Clipboard format is not an image.");
        string compressed=await model.CompressAsync("Please report version 42 and preserve the URL https://example.com/help. Do not change version 42 or https://example.com/help.");
        if (!compressed.Contains("42") || !compressed.Contains("https://example.com/help")) throw new Exception("Shared runtime broke prompt compression.");
        string russian=(await model.TranslateRegionsAsync(new[] {Label("Версия 42 готова.")},timeout.Token))[0];
        if (russian!="Версия 42 готова.") throw new Exception("Russian source was changed: "+russian);
        using (var small=new System.Drawing.Bitmap(200,72))
        using (var graphics=System.Drawing.Graphics.FromImage(small))
        using (var font=new System.Drawing.Font("Segoe UI",12,System.Drawing.FontStyle.Regular,System.Drawing.GraphicsUnit.Pixel))
        using (var ink=new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(180,185,193)))
        using (var png=new MemoryStream())
        {
            graphics.Clear(System.Drawing.Color.FromArgb(43,45,49));
            graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.FillRectangle(ink,18,16,10,10); graphics.FillEllipse(ink,18,42,10,10);
            graphics.DrawString("Events",font,ink,40,12); graphics.DrawString("Server Boosts",font,ink,40,38);
            small.Save(png,System.Drawing.Imaging.ImageFormat.Png);
            var smallImage=png.ToArray();
            var smallOcr=await ocr.RecognizeAsync(smallImage,80_000_000,timeout.Token);
            var smallLabels=await model.TranslateRegionsAsync(smallOcr.Regions,timeout.Token);
            var smallResult=TranslatedImage.Render(smallImage,smallOcr,smallLabels,80_000_000,timeout.Token);
            if (!smallLabels.Any(t=>t.Contains("событ",StringComparison.OrdinalIgnoreCase))
                || !smallLabels.Any(t=>t.Contains("серв",StringComparison.OrdinalIgnoreCase))) throw new Exception("Small image labels not translated.");
            var output=new PngBitmapEncoder(); output.Frames.Add(BitmapFrame.Create(smallResult));
            using (var file=File.Create(Path.Combine(dir,"translated-small-dark.png"))) output.Save(file);
            await File.WriteAllTextAsync(Path.Combine(dir,"small-labels.json"),JsonSerializer.Serialize(new {regions=smallOcr.Regions,translations=smallLabels}));
        }
        await File.WriteAllTextAsync(Path.Combine(dir,"translation-result.json"),JsonSerializer.Serialize(new {source,recognized=recognized.Text,translations,result,compressed,russian,elapsed_ms=watch.Elapsed.TotalMilliseconds}));
        await File.WriteAllTextAsync(Path.Combine(dir,"translation-checks.log"),"PASS: cancellation, retry, encrypted image -> positioned OCR -> live local translation -> image rendering -> encrypted image history -> PNG/bitmap clipboard payload; original retained, number/URL retained, prompt compression regression; no real history, clipboard or input.");
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i=0; i<VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    internal static async Task IntegrationAsync(string corpusDir)
    {
        using var engine = new ScreenshotOcr();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(corpusDir,"manifest.json")));
        var results = new List<object>();
        foreach (var test in manifest.RootElement.EnumerateArray())
        {
            string id = test.GetProperty("id").GetString()!;
            if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')) throw new Exception("Invalid synthetic test id.");
            var image = await File.ReadAllBytesAsync(Path.Combine(corpusDir,id+".png"));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await engine.RecognizeAsync(image,80_000_000,CancellationToken.None);
            results.Add(new {id,elapsed_ms=watch.Elapsed.TotalMilliseconds,text=result.Text,review=result.ReviewNeeded,corrected=result.CorrectedCharacters,columns=result.Columns});
        }
        var good = await File.ReadAllBytesAsync(Path.Combine(corpusDir,manifest.RootElement[0].GetProperty("id").GetString()+".png"));
        var encryptedDir = Path.Combine(corpusDir,"encrypted-input");Directory.CreateDirectory(encryptedDir);
        await File.WriteAllBytesAsync(Path.Combine(encryptedDir,"synthetic.dat"),ImageFiles.Seal(good));
        var restored = await ScreenshotOcr.ReadImageAsync(encryptedDir,"synthetic",CancellationToken.None);
        if (!restored.SequenceEqual(good)) throw new Exception("Encrypted image changed.");
        try { await engine.RecognizeAsync([1,2,3],80_000_000,CancellationToken.None);throw new Exception("Corrupt input accepted."); }
        catch (ScreenshotOcr.Failure) { }
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        try { await engine.RecognizeAsync(good,80_000_000,cancel.Token);throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        var retry = await engine.RecognizeAsync(restored,80_000_000,CancellationToken.None);
        if (retry.Text.Length == 0) throw new Exception("Retry after cancellation failed.");
        if (!File.Exists(Path.Combine(encryptedDir,"synthetic.dat"))) throw new Exception("Source image was deleted.");
        try { await engine.RecognizeAsync(good,1,CancellationToken.None);throw new Exception("Pixel limit ignored."); }
        catch (ScreenshotOcr.Failure) { }
        await File.WriteAllTextAsync(Path.Combine(corpusDir,"ocr-results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
        await File.WriteAllTextAsync(Path.Combine(corpusDir,"ocr-checks.log"),"PASS: encrypted source preserved, corrupt input rejected, cancellation/retry, actual pixel bound; no clipboard/input/window access.");
    }
}
