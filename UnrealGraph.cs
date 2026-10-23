using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArkCopyBuffer;

/// <summary>Просмотр текстового экспорта графов Unreal и Unity. Исходный текст остаётся в буфере для вставки обратно.</summary>
static class UnrealGraph
{
    const double NodeWidth = 190;
    const double HeaderHeight = 28;
    const double PinHeight = 21;
    static readonly Brush Background = Brush(21, 24, 30);
    static readonly Brush Grid = Brush(31, 35, 43);
    static readonly Brush NodeFill = Brush(43, 48, 58);
    static readonly Brush NodeOutline = Brush(94, 105, 120);
    static readonly Brush Text = Brush(238, 242, 247);
    static readonly Brush Subtle = Brush(178, 190, 205);

    sealed class Node
    {
        public string Title = "Node";
        public double X, Y, DrawX, DrawY, Height;
        public readonly List<Pin> Pins = [];
        public List<Pin> Inputs = [], Outputs = [];
    }

    sealed class Pin
    {
        public string Id = "", Name = "", Category = "";
        public bool Output;
        public readonly List<string> Links = [];
    }

    static Brush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    static string Field(string line, string key)
    {
        var match = Regex.Match(line, @"(?:^|[\s,(])" + Regex.Escape(key) + @"=(?:""(?<q>[^""]*)""|(?<v>[^,)]*))");
        return match.Success ? (match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["v"].Value.Trim()) : "";
    }

    static string Pretty(string name)
    {
        foreach (var prefix in new[] { "MaterialExpression", "K2Node_", "MaterialGraphNode_" })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
        return Regex.Replace(name.Replace('_', ' '), @"(?<=[a-z0-9])(?=[A-Z])", " ");
    }

    static List<Node> ParseUnreal(string source)
    {
        var nodes = new List<Node>();
        using var reader = new StringReader(source);
        Node? current = null;
        int depth = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var s = line.TrimStart();
            if (s.StartsWith("Begin Object ", StringComparison.Ordinal))
            {
                var cls = Regex.Match(s, @"Class=/Script/[^\s]+").Value;
                var type = cls[(cls.LastIndexOf('.') + 1)..];
                if (depth == 0 && nodes.Count < 300 &&
                    (type.StartsWith("MaterialGraphNode", StringComparison.Ordinal) || type.StartsWith("K2Node_", StringComparison.Ordinal)))
                {
                    current = new Node { Title = type.EndsWith("_Root", StringComparison.Ordinal) ? "Material" : Pretty(type) };
                    nodes.Add(current);
                }
                else if (depth == 1 && current != null && type.StartsWith("MaterialExpression", StringComparison.Ordinal))
                    current.Title = Pretty(type);
                depth++;
                continue;
            }
            if (s.StartsWith("End Object", StringComparison.Ordinal))
            {
                depth = Math.Max(0, depth - 1);
                if (depth == 0) current = null;
                continue;
            }
            if (current == null || depth != 1) continue;
            if (s.StartsWith("NodePosX=", StringComparison.Ordinal))
                double.TryParse(s.AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out current.X);
            else if (s.StartsWith("NodePosY=", StringComparison.Ordinal))
                double.TryParse(s.AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out current.Y);
            else if (s.StartsWith("FunctionReference=", StringComparison.Ordinal))
            {
                var function = Field(s, "MemberName");
                if (function.Length > 0) current.Title = Pretty(function);
            }
            else if (s.StartsWith("CustomProperties Pin ", StringComparison.Ordinal) && current.Pins.Count < 100)
            {
                var pin = new Pin
                {
                    Id = Field(s, "PinId"), Name = Field(s, "PinName"), Category = Field(s, "PinType.PinCategory"),
                    Output = Field(s, "Direction") == "EGPD_Output"
                };
                int start = s.IndexOf("LinkedTo=(", StringComparison.Ordinal);
                if (start >= 0)
                {
                    int end = s.IndexOf(')', start + 10);
                    if (end > start)
                        foreach (Match link in Regex.Matches(s[(start + 10)..end], @"\w+\s+([A-Fa-f0-9]{32})"))
                            pin.Links.Add(link.Groups[1].Value);
                }
                if (pin.Id.Length == 32) current.Pins.Add(pin);
            }
        }
        return nodes;
    }

    static JsonElement Get(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;

    static string String(JsonElement value, string name)
    {
        var field = Get(value, name);
        return field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    }

    static double Number(JsonElement value, string name)
    {
        var field = Get(value, name);
        return field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number) ? number : 0;
    }

    static int Integer(JsonElement value, string name) => (int)Number(value, name);

    static IEnumerable<JsonElement> Elements(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    static JsonElement Inner(JsonElement entry)
    {
        var json = String(entry, "JSONnodeData");
        if (json.Length == 0) return default;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    static List<Node> ParseLegacyUnity(JsonElement root)
    {
        var nodes = new List<Node>();
        var nodeById = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Elements(Get(root, "m_SerializableNodes")))
        {
            if (nodes.Count >= 300) break;
            var value = Inner(entry);
            string id = String(value, "m_GuidSerialized");
            if (id.Length == 0) continue;
            var position = Get(Get(value, "m_DrawState"), "m_Position");
            string title = String(value, "m_Name");
            if (title.Length == 0) title = Pretty(String(Get(entry, "typeInfo"), "fullName").Split('.').Last());
            var node = new Node { Title = title, X = Number(position, "x"), Y = Number(position, "y") };
            nodes.Add(node);
            nodeById[id] = node;
            foreach (var slotEntry in Elements(Get(value, "m_SerializableSlots")))
            {
                if (node.Pins.Count >= 100) break;
                var slot = Inner(slotEntry);
                if (slot.ValueKind != JsonValueKind.Object) continue;
                node.Pins.Add(new Pin { Id = id + ":" + Integer(slot, "m_Id"), Name = String(slot, "m_DisplayName"),
                    Category = String(Get(slotEntry, "typeInfo"), "fullName"), Output = Integer(slot, "m_SlotType") == 1 });
            }
        }
        foreach (var entry in Elements(Get(root, "m_SerializableEdges")))
        {
            var edge = Inner(entry);
            var output = Get(edge, "m_OutputSlot");
            var input = Get(edge, "m_InputSlot");
            string outputNode = String(output, "m_NodeGUIDSerialized");
            string inputNode = String(input, "m_NodeGUIDSerialized");
            if (!nodeById.TryGetValue(outputNode, out var from) || !nodeById.TryGetValue(inputNode, out var to)) continue;
            int outputSlot = Integer(output, "m_SlotId"), inputSlot = Integer(input, "m_SlotId");
            var a = from.Pins.FirstOrDefault(p => p.Id.EndsWith(":" + outputSlot, StringComparison.Ordinal));
            if (a == null) { a = new Pin { Id = outputNode + ":" + outputSlot, Name = "Output", Output = true }; from.Pins.Add(a); }
            var b = to.Pins.FirstOrDefault(p => p.Id.EndsWith(":" + inputSlot, StringComparison.Ordinal));
            if (b == null) { b = new Pin { Id = inputNode + ":" + inputSlot, Name = "Input" }; to.Pins.Add(b); }
            a.Links.Add(b.Id);
            b.Links.Add(a.Id);
        }
        return nodes;
    }

    static List<Node> ParseUnity(string source)
    {
        // Shader Graph MultiJson: первый объект — CopyPasteGraph, затем объекты нод и слотов.
        var objects = new List<JsonElement>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(source), new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read() && objects.Count < 5000)
        {
            if (reader.TokenType != JsonTokenType.StartObject) continue;
            using var document = JsonDocument.ParseValue(ref reader);
            objects.Add(document.RootElement.Clone());
        }
        var root = objects.FirstOrDefault(value => String(value, "m_Type").EndsWith("CopyPasteGraph", StringComparison.Ordinal)
            || Get(value, "m_SerializableNodes").ValueKind == JsonValueKind.Array);
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (Get(root, "m_SerializableNodes").ValueKind == JsonValueKind.Array) return ParseLegacyUnity(root);
        var byId = new Dictionary<string, JsonElement>();
        foreach (var value in objects)
        {
            var id = String(value, "m_ObjectId");
            if (id.Length > 0) byId[id] = value;
        }

        var nodes = new List<Node>();
        var nodeById = new Dictionary<string, Node>();
        foreach (var reference in Elements(Get(root, "m_Nodes")))
        {
            string id = String(reference, "m_Id");
            if (!byId.TryGetValue(id, out var value) || nodes.Count >= 300) continue;
            var position = Get(Get(value, "m_DrawState"), "m_Position");
            var title = String(value, "m_Name");
            if (title.Length == 0) title = Pretty(String(value, "m_Type").Split('.').Last());
            var node = new Node { Title = title, X = Number(position, "x"), Y = Number(position, "y") };
            nodes.Add(node);
            nodeById[id] = node;
            foreach (var slotReference in Elements(Get(value, "m_Slots")))
            {
                if (node.Pins.Count >= 100 || !byId.TryGetValue(String(slotReference, "m_Id"), out var slot)) continue;
                int slotId = Integer(slot, "m_Id");
                node.Pins.Add(new Pin { Id = id + ":" + slotId, Name = String(slot, "m_DisplayName"),
                    Category = String(slot, "m_Type"), Output = Integer(slot, "m_SlotType") == 1 });
            }
        }

        foreach (var edge in Elements(Get(root, "m_Edges")))
        {
            var output = Get(edge, "m_OutputSlot");
            var input = Get(edge, "m_InputSlot");
            string outputNode = String(Get(output, "m_Node"), "m_Id");
            string inputNode = String(Get(input, "m_Node"), "m_Id");
            if (!nodeById.TryGetValue(outputNode, out var from) || !nodeById.TryGetValue(inputNode, out var to)) continue;
            int outputSlot = Integer(output, "m_SlotId"), inputSlot = Integer(input, "m_SlotId");
            var a = from.Pins.FirstOrDefault(p => p.Id == outputNode + ":" + outputSlot);
            if (a == null) { a = new Pin { Id = outputNode + ":" + outputSlot, Name = "Output", Output = true }; from.Pins.Add(a); }
            var b = to.Pins.FirstOrDefault(p => p.Id == inputNode + ":" + inputSlot);
            if (b == null) { b = new Pin { Id = inputNode + ":" + inputSlot, Name = "Input" }; to.Pins.Add(b); }
            a.Links.Add(b.Id);
            b.Links.Add(a.Id);
        }
        return nodes;
    }

    static Brush PinColor(string category) => category.ToLowerInvariant() switch
    {
        "mask" or "float" or "real" or "double" => Brush(154, 213, 124),
        "vector" or "color" => Brush(242, 184, 98),
        "exec" => Brush(235, 237, 240),
        "object" => Brush(113, 183, 225),
        _ => Brush(165, 185, 215)
    };

    static void Label(DrawingContext dc, string value, double x, double y, double maxWidth, Brush color, double size = 12, bool right = false)
    {
        if (value.Length > 24) value = value[..23] + "…";
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            App.TextTypeface, size, color, 1) { MaxTextWidth = maxWidth, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, new Point(right ? x - text.Width : x, y));
    }

    static DrawingImage? Draw(List<Node> nodes)
    {
        if (nodes.Count == 0) return null;
        double minX = nodes.Min(n => n.X), minY = nodes.Min(n => n.Y);
        foreach (var node in nodes)
        {
            node.Inputs = node.Pins.Where(p => !p.Output && p.Links.Count > 0).Take(40)
                .Concat(node.Pins.Where(p => !p.Output && p.Links.Count == 0).Take(2)).ToList();
            node.Outputs = node.Pins.Where(p => p.Output && p.Links.Count > 0).Take(40)
                .Concat(node.Pins.Where(p => p.Output && p.Links.Count == 0).Take(2)).ToList();
            node.Height = Math.Max(62, HeaderHeight + 10 + Math.Max(node.Inputs.Count, node.Outputs.Count) * PinHeight);
            node.DrawX = Math.Clamp(node.X - minX, 0, 20000) + 40;
            node.DrawY = Math.Clamp(node.Y - minY, 0, 20000) + 40;
        }
        double width = nodes.Max(n => n.DrawX + NodeWidth) + 40;
        double height = nodes.Max(n => n.DrawY + n.Height) + 40;
        var points = new Dictionary<string, (Point point, bool output, Brush color)>();
        foreach (var node in nodes)
        {
            for (int i = 0; i < node.Inputs.Count; i++)
                points[node.Inputs[i].Id] = (new Point(node.DrawX, node.DrawY + HeaderHeight + 17 + i * PinHeight), false, PinColor(node.Inputs[i].Category));
            for (int i = 0; i < node.Outputs.Count; i++)
                points[node.Outputs[i].Id] = (new Point(node.DrawX + NodeWidth, node.DrawY + HeaderHeight + 17 + i * PinHeight), true, PinColor(node.Outputs[i].Category));
        }

        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));
            var gridPen = new Pen(Grid, 1);
            for (double x = 0; x < width; x += 80) dc.DrawLine(gridPen, new Point(x, 0), new Point(x, height));
            for (double y = 0; y < height; y += 80) dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));

            var drawn = new HashSet<string>();
            foreach (var node in nodes)
            foreach (var pin in node.Pins)
            foreach (var link in pin.Links)
            {
                if (!points.TryGetValue(pin.Id, out var a) || !points.TryGetValue(link, out var b)) continue;
                var key = string.CompareOrdinal(pin.Id, link) < 0 ? pin.Id + link : link + pin.Id;
                if (!drawn.Add(key)) continue;
                if (!a.output) (a, b) = (b, a);
                double bend = Math.Max(40, Math.Abs(b.point.X - a.point.X) * 0.45);
                var path = new StreamGeometry();
                using (var g = path.Open())
                {
                    g.BeginFigure(a.point, false, false);
                    g.BezierTo(new Point(a.point.X + bend, a.point.Y), new Point(b.point.X - bend, b.point.Y), b.point, true, false);
                }
                dc.DrawGeometry(null, new Pen(a.color, 2.5), path);
            }

            foreach (var node in nodes)
            {
                var box = new Rect(node.DrawX, node.DrawY, NodeWidth, node.Height);
                dc.DrawRoundedRectangle(NodeFill, new Pen(NodeOutline, 1), box, 6, 6);
                dc.DrawRoundedRectangle(node.Title == "Material" ? Brush(115, 69, 49) : Brush(52, 83, 103),
                    null, new Rect(node.DrawX + 1, node.DrawY + 1, NodeWidth - 2, HeaderHeight), 5, 5);
                Label(dc, node.Title, node.DrawX + 10, node.DrawY + 5, NodeWidth - 20, Text, 13);
                for (int i = 0; i < node.Inputs.Count; i++)
                {
                    var pin = node.Inputs[i];
                    double y = node.DrawY + HeaderHeight + 17 + i * PinHeight;
                    dc.DrawEllipse(PinColor(pin.Category), null, new Point(node.DrawX, y), 4, 4);
                    Label(dc, pin.Name, node.DrawX + 12, y - 9, NodeWidth - 24, Subtle);
                }
                for (int i = 0; i < node.Outputs.Count; i++)
                {
                    var pin = node.Outputs[i];
                    double y = node.DrawY + HeaderHeight + 17 + i * PinHeight;
                    dc.DrawEllipse(PinColor(pin.Category), null, new Point(node.DrawX + NodeWidth, y), 4, 4);
                    Label(dc, pin.Name, node.DrawX + NodeWidth - 12, y - 9, NodeWidth - 24, Subtle, right: true);
                }
            }
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    public static ImageSource? Render(string text, bool unity = false)
    {
        try { return Draw(unity ? ParseUnity(text) : ParseUnreal(text)); }
        catch (Exception ex) { App.Trace($"Node graph preview: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Полный просмотр: тёмная рамка (граф всегда тёмный), перетаскивание и колесо мгновенно,
    /// плавающая панель масштаба (минус, процент, плюс, вписать) — плавно. Esc закрывает, Ctrl+0 вписывает.
    /// </summary>
    public static void Open(ClipItem item)
    {
        if (item.GraphPreview is not DrawingImage graph) return;
        var bounds = graph.Drawing.Bounds;
        var zoom = new ScaleTransform(1, 1);
        var pan = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(zoom);
        transform.Children.Add(pan);
        var image = new Image { Source = graph, Width = bounds.Width, Height = bounds.Height,
            Stretch = Stretch.None, RenderTransform = transform, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        System.Windows.Automation.AutomationProperties.SetName(image, item.GraphLabel);
        var viewport = new Canvas { Background = Background, ClipToBounds = true, Cursor = Cursors.Hand };
        viewport.Children.Add(image);

        var percent = new TextBlock { Text = "100 %", FontFamily = (FontFamily)Application.Current.FindResource("TextFont"), FontSize = 13 };
        void Settle()
        {
            Motion.Settle(zoom, ScaleTransform.ScaleXProperty);
            Motion.Settle(zoom, ScaleTransform.ScaleYProperty);
            Motion.Settle(pan, TranslateTransform.XProperty);
            Motion.Settle(pan, TranslateTransform.YProperty);
        }
        // ponytail: масштаб и сдвиг идут одной кривой, точка под курсором на полпути чуть «плывёт»; для 250 мс незаметно.
        void Set(double scale, double x, double y, bool animate)
        {
            int ms = animate ? Motion.Slow : 0;
            Motion.To(zoom, ScaleTransform.ScaleXProperty, scale, ms);
            Motion.To(zoom, ScaleTransform.ScaleYProperty, scale, ms);
            Motion.To(pan, TranslateTransform.XProperty, x, ms);
            Motion.To(pan, TranslateTransform.YProperty, y, ms);
            percent.Text = $"{scale * 100:0} %";
        }
        void ZoomAt(double next, Point at, bool animate)
        {
            Settle();
            double old = zoom.ScaleX;
            next = Math.Clamp(next, 0.1, 3);
            Set(next, at.X - (at.X - pan.X) * next / old, at.Y - (at.Y - pan.Y) * next / old, animate);
        }
        Point Center() => new(viewport.ActualWidth / 2, viewport.ActualHeight / 2);
        void Fit(bool animate)
        {
            Settle();
            double fit = Math.Clamp(Math.Min((viewport.ActualWidth - 40) / bounds.Width,
                (viewport.ActualHeight - 40) / bounds.Height), 0.1, 1);
            Set(fit, (viewport.ActualWidth - bounds.Width * fit) / 2, (viewport.ActualHeight - bounds.Height * fit) / 2, animate);
        }

        Button Tool(object content, string name, Action click)
        {
            var button = new Button { Content = content, ToolTip = name, Style = (Style)Application.Current.FindResource("GraphToolButton") };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => click();
            return button;
        }
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Tool("\uE738", "Уменьшить (Ctrl+−)", () => ZoomAt(zoom.ScaleX / 1.25, Center(), true)));
        tools.Children.Add(Tool(percent, "Масштаб 100 %", () => ZoomAt(1, Center(), true)));
        tools.Children.Add(Tool("\uE710", "Увеличить (Ctrl++)", () => ZoomAt(zoom.ScaleX * 1.25, Center(), true)));
        tools.Children.Add(new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0, 4, 0), Background = Brush(58, 58, 58) });
        tools.Children.Add(Tool("\uE9A6", "Вписать в окно (Ctrl+0)", () => Fit(true)));
        var toolbar = new Border
        {
            Child = tools, Padding = new Thickness(4), CornerRadius = new CornerRadius(8), Margin = new Thickness(16),
            Background = Brush(44, 44, 44), BorderBrush = Brush(58, 58, 58), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Direction = 270, Opacity = 0.4 }
        };
        var hints = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 24), Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        foreach (var hint in new[] { "Колесо: масштаб", "Перетаскивание: сдвиг", "Esc: закрыть" })
            hints.Children.Add(new TextBlock { Text = hint, Foreground = Subtle, FontSize = 12, Margin = new Thickness(0, 0, 16, 0) });
        var layout = new System.Windows.Controls.Grid(); // Grid — имя поля-кисти в этом классе
        layout.Children.Add(viewport);
        layout.Children.Add(hints);
        layout.Children.Add(toolbar);

        var window = new Window { Title = item.GraphLabel, Width = Math.Min(1100, SystemParameters.WorkArea.Width - 80),
            Height = Math.Min(750, SystemParameters.WorkArea.Height - 80), WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Background, Content = layout, FontFamily = (FontFamily)Application.Current.FindResource("TextFont"),
            UseLayoutRounding = true };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        // Окно графа всегда тёмное — и системная рамка тоже, иначе светлая полоса над тёмным холстом.
        window.SourceInitialized += (_, _) => Native.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        window.Loaded += (_, _) => Fit(false);
        window.PreviewKeyDown += (_, e) =>
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (e.Key == Key.Escape) window.Close();
            else if (ctrl && e.Key is Key.D0 or Key.NumPad0) Fit(true);
            else if (ctrl && e.Key is Key.OemPlus or Key.Add) ZoomAt(zoom.ScaleX * 1.25, Center(), true);
            else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) ZoomAt(zoom.ScaleX / 1.25, Center(), true);
            else return;
            e.Handled = true;
        };
        Point last = default;
        viewport.MouseLeftButtonDown += (_, e) =>
        {
            Settle(); // перетаскивание посреди плавного масштаба — продолжаем с текущего места
            last = e.GetPosition(viewport);
            viewport.CaptureMouse();
            viewport.Cursor = Cursors.SizeAll;
            e.Handled = true;
        };
        viewport.MouseMove += (_, e) =>
        {
            if (!viewport.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(viewport);
            pan.X += point.X - last.X;
            pan.Y += point.Y - last.Y;
            last = point;
        };
        viewport.MouseLeftButtonUp += (_, e) =>
        {
            viewport.ReleaseMouseCapture();
            e.Handled = true;
        };
        viewport.LostMouseCapture += (_, _) => viewport.Cursor = Cursors.Hand;
        viewport.PreviewMouseWheel += (_, e) =>
        {
            ZoomAt(zoom.ScaleX * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(viewport), false); // колесо — мгновенно
            e.Handled = true;
        };
        window.Show();
    }
}
