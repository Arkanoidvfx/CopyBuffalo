using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ArkCopyBuffer;

enum PlateMode { Idle, Move, Resize }

/// <summary>Скорости панели и её наклон. Наклон догоняет скорость без перелёта: это жёсткая плита, она не качается.</summary>
sealed class PlateMotion
{
    public Vector Tilt, Flare;     // 0…1 по модулю: доля наибольшего наклона и раскрытия стороны
    public readonly double[] Speed = new double[4]; // слева, сверху, справа, снизу, DIP/с
    Vector _tiltSpeed, _flareSpeed;
    readonly double[] _edges = new double[4];
    bool _placed;

    /// <summary>Стороны, которые тянет код WM_NCHITTEST (10 слева … 17 справа снизу).</summary>
    internal static bool Holds(int hit, int side) => side switch
    {
        0 => hit is 10 or 13 or 16, 1 => hit is 12 or 13 or 14, 2 => hit is 11 or 14 or 17, _ => hit is 15 or 16 or 17,
    };

    public bool Resting => Tilt.Length < 0.01 && _tiltSpeed.Length < 0.1 && Flare.Length < 0.01 && _flareSpeed.Length < 0.1;

    /// <summary>Панель перенесли (новый показ): её прыжок — не движение.</summary>
    public void Forget()
    {
        _placed = false;
        Tilt = Flare = _tiltSpeed = _flareSpeed = default;
        Array.Clear(Speed);
    }

    public void Step(double dt, Rect panel, PlateMode mode, int hit)
    {
        double[] edges = [panel.Left, panel.Top, panel.Right, panel.Bottom];
        if (!_placed) { edges.CopyTo(_edges, 0); Array.Clear(Speed); _placed = true; }
        if (dt <= 0) return;
        dt = Math.Min(dt, 0.05); // после паузы пружина не взрывается
        double blend = 1 - Math.Exp(-dt / 0.02);
        for (int side = 0; side < 4; side++)
        {
            Speed[side] += ((edges[side] - _edges[side]) / dt - Speed[side]) * blend;
            _edges[side] = edges[side];
        }
        var tilt = mode == PlateMode.Move ? Soft(new Vector(Speed[0], Speed[1])) : default;
        var flare = default(Vector);
        if (mode == PlateMode.Resize) // наружу — сторона раскрывается к зрителю, внутрь — уходит от него
            flare = new Vector(Open(0, -1) + Open(2, 1), Open(1, -1) + Open(3, 1));
        double Open(int side, int outward) => Holds(hit, side) ? Math.Tanh(Speed[side] * outward / 420) : 0;
        (Tilt, _tiltSpeed) = Follow(Tilt, _tiltSpeed, tilt, dt);
        (Flare, _flareSpeed) = Follow(Flare, _flareSpeed, flare, dt);
    }

    /// <summary>Обычный перенос мышью (200–400 px/с) уже виден, рывок упирается в предел.</summary>
    static Vector Soft(Vector speed) => speed.Length < 1 ? default : speed * (Math.Tanh(speed.Length / 420) / speed.Length);

    /// <summary>Критическое затухание: точное решение, поэтому шаг кадра любой длины не даёт перелёта.</summary>
    internal static (Vector, Vector) Follow(Vector x, Vector v, Vector target, double dt, double omega = 30)
    {
        var d = x - target;
        var c = v + d * omega;
        double e = Math.Exp(-omega * dt);
        return (target + (d + c * dt) * e, (v - c * omega * dt) * e);
    }
}

/// <summary>Наклон всей панели шейдером (Assets/Vfx/Plate.hlsl). Разметка не пересчитывается.</summary>
sealed class PlateEffect : ShaderEffect
{
    const double TiltPerDip = 8e-5, FlareMost = 0.018, SheenMost = 0.02;
    static readonly PixelShader Shader = Load();
    static PixelShader Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ArkCopyBuffer.Assets.Vfx.Plate.ps") ?? throw new InvalidDataException("Missing Plate.ps");
        var shader = new PixelShader();
        shader.SetStreamSource(stream);
        shader.Freeze();
        return shader;
    }

    static DependencyProperty Constant<T>(string name, int register) => DependencyProperty.Register(name, typeof(T), typeof(PlateEffect),
        new UIPropertyMetadata(default(T), PixelShaderConstantCallback(register)));
    static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(PlateEffect), 0);
    static readonly DependencyProperty SizeProperty = Constant<Point>("Size", 0), InsetProperty = Constant<Point>("Inset", 1), GrabProperty = Constant<Point>("Grab", 2),
        TiltProperty = Constant<Point>("Tilt", 3), FlareProperty = Constant<Point>("Flare", 4), SideToProperty = Constant<Point>("SideTo", 5), SheenProperty = Constant<double>("Sheen", 6);

    public PlateEffect(double inset)
    {
        PixelShader = Shader;
        SetValue(InsetProperty, new Point(inset, inset));
        foreach (var property in new[] { InputProperty, SizeProperty, InsetProperty, GrabProperty, TiltProperty, FlareProperty, SideToProperty, SheenProperty }) UpdateShaderValue(property);
    }

    public void Hold(Point grab, int hit)
    {
        SetValue(GrabProperty, grab);
        SetValue(SideToProperty, new Point(PlateMotion.Holds(hit, 2) ? 1 : 0, PlateMotion.Holds(hit, 3) ? 1 : 0));
    }

    public void Show(Size panel, Vector tilt, Vector flare, bool sheen)
    {
        var inset = (Point)GetValue(InsetProperty);
        SetValue(SizeProperty, new Point(panel.Width + 2 * inset.X, panel.Height + 2 * inset.Y));
        SetValue(TiltProperty, (Point)(tilt * TiltPerDip));
        SetValue(FlareProperty, (Point)(flare * FlareMost));
        SetValue(SheenProperty, sheen ? SheenMost * Math.Min(1, tilt.Length + flare.Length) : 0);
    }
}

/// <summary>
/// Реакция на перенос и растягивание: панель наклоняется, как жёсткая плита, взятая там, где мышь, по ней проходит отблеск,
/// у мыши светится край рамки, а осколки (ShardFrame) отстают и защёлкиваются. Выключается в настройках VFX и вместе с анимацией окна.
/// </summary>
partial class PanelWindow
{
    const double PlatePad = 48; // место под наклон вокруг панели сверх поля тени
    readonly PlateMotion _plate = new();
    readonly Rectangle _plateBounds = new() { Margin = new Thickness(-PlatePad), Fill = Brushes.Transparent, IsHitTestVisible = false, Visibility = Visibility.Collapsed }; // шейдер получает область известного размера
    PlateEffect? _platePanel, _plateOverlay;
    bool _plateRunning;
    // Слабый свет там, где держат: пятно под мышью и ближайший к ней участок рамки.
    readonly Grid _touchEdge = new() { IsHitTestVisible = false, Opacity = 0, Visibility = Visibility.Collapsed };
    readonly Ellipse _touchSpot = new() { Width = 72, Height = 72, IsHitTestVisible = false, Opacity = 0, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform() };
    double _pad; // сейчас вокруг панели больше места, чем ShadowMargin
    TimeSpan? _plateTime;
    Point _plateGrab;
    int _plateHit;

    public bool FrameReaction { get => ReactionToggle.IsChecked == true; set => ReactionToggle.IsChecked = value; }
    bool Reacts => Motion.Enabled && FrameReaction;

    void InitializePlate()
    {
        Root.Children.Insert(0, _plateBounds);
        foreach (var (outset, thickness, opacity) in new[] { (3.5, 5.0, 0.04), (1.0, 1.5, 0.2) }) // штрихи вместо размытия: его пересчитывали бы каждый кадр
            _touchEdge.Children.Add(new Rectangle { Margin = new Thickness(-outset), RadiusX = 8 + outset, RadiusY = 8 + outset, StrokeThickness = thickness, Opacity = opacity });
        Root.Children.Insert(Root.Children.IndexOf(PinEnergy) + 1, _touchEdge); // под поверхностью: светится только край снаружи
        Root.Children.Add(_touchSpot);
        IsVisibleChanged += (_, _) => { if (!IsVisible) SettlePlate(); };
        Closed += (_, _) => { if (_plateRunning) CompositionTarget.Rendering -= PlateTick; };
    }

    /// <summary>hit 0 — панель несут за шапку, иначе тянут край.</summary>
    void StartPlate(int hit)
    {
        if (!Reacts || !IsShown || !Native.GetCursorPos(out var p) || PanelPixels() is not { } panel) return;
        _platePanel ??= new PlateEffect(PlatePad);
        _plateOverlay ??= new PlateEffect(NetherBorder.OuterInset);
        double Part(int at, int from, int to) => Math.Clamp((at - from) / (double)Math.Max(1, to - from), 0, 1);
        _plateGrab = new Point(Part(p.X, panel.Left, panel.Right), Part(p.Y, panel.Top, panel.Bottom));
        _plateHit = hit;
        _platePanel.Hold(_plateGrab, hit);
        _plateOverlay.Hold(_plateGrab, hit);
        _plate.Forget();
        _plateTime = null;
        ShowPlate();
        _plateBounds.Visibility = Visibility.Visible;
        Root.Effect = _platePanel;
        if (_vfxWindow?.Content is UIElement overlay) overlay.Effect = _plateOverlay;
        LightTouch(p, panel);
        if (_plateRunning) return;
        _plateRunning = true;
        CompositionTarget.Rendering += PlateTick;
    }

    void ShowPlate()
    {
        _platePanel!.Show(Root.RenderSize, _plate.Tilt, _plate.Flare, sheen: true);
        _plateOverlay!.Show(Root.RenderSize, _plate.Tilt, _plate.Flare, sheen: false);
    }

    void PlateTick(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_plateTime == now) return;
        double dt = _plateTime is { } last ? (now - last).TotalSeconds : 0;
        _plateTime = now;
        var mode = _resize != null ? PlateMode.Resize : _sizing ? PlateMode.Move : PlateMode.Idle;
        if (!IsShown || PanelPixels() is not { } panel) { SettlePlate(); return; }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (mode == PlateMode.Idle) FadeTouch();
        else if (Native.GetCursorPos(out var touch)) PlaceTouch(touch, panel, scale);
        _plate.Step(dt, new Rect(panel.Left / scale, panel.Top / scale, (panel.Right - panel.Left) / scale, (panel.Bottom - panel.Top) / scale), mode, _plateHit);
        var shards = ShardsShown ? _vfxWindow!.Shards : null;
        shards?.Step(dt, mode, _plateHit, _plateGrab, _plate.Speed);
        if (mode == PlateMode.Idle && _plate.Resting && _touchEdge.Opacity == 0 && shards?.Resting != false) { SettlePlate(); return; }
        ShowPlate();
    }

    /// <summary>Движение кончилось или панель переставляют: эффекты сняты, окно снова по размеру панели с тенью.</summary>
    void SettlePlate()
    {
        if (_plateRunning)
        {
            _plateRunning = false;
            CompositionTarget.Rendering -= PlateTick;
            _plate.Forget();
            Root.Effect = null;
            _plateBounds.Visibility = _touchEdge.Visibility = _touchSpot.Visibility = Visibility.Collapsed;
            if (_vfxWindow?.Content is UIElement overlay) overlay.Effect = null;
            _vfxWindow?.Shards.Rest();
        }
        if (_resize == null) PadWindow(0);
    }

    const double TouchReach = 56;
    bool _touchLit;

    void LightTouch(Native.POINT at, Native.RECT panel)
    {
        var color = Skin.ColorOf(FindResource("PinGlow")); // тема могла смениться
        GradientStopCollection Stops(byte middle) => [new(color, 0), new(Color.FromArgb(middle, color.R, color.G, color.B), 0.45), new(Color.FromArgb(0, color.R, color.G, color.B), 1)];
        foreach (Rectangle stroke in _touchEdge.Children)
            stroke.Stroke = new RadialGradientBrush(Stops(120)) { MappingMode = BrushMappingMode.Absolute, RadiusX = TouchReach, RadiusY = TouchReach };
        _touchSpot.Fill = new RadialGradientBrush(Stops(70));
        _touchEdge.Visibility = _touchSpot.Visibility = Visibility.Visible;
        _touchLit = true;
        PlaceTouch(at, panel, VisualTreeHelper.GetDpi(this).DpiScaleX);
        Motion.To(_touchEdge, OpacityProperty, 1, Motion.Fast);
        Motion.To(_touchSpot, OpacityProperty, 0.035, Motion.Fast);
    }

    void PlaceTouch(Native.POINT at, Native.RECT panel, double scale)
    {
        // Упёрлись в наименьший размер: мышь ушла с края, а свет остаётся на нём.
        double x = Math.Clamp((at.X - panel.Left) / scale, 0, Root.ActualWidth), y = Math.Clamp((at.Y - panel.Top) / scale, 0, Root.ActualHeight);
        foreach (Rectangle stroke in _touchEdge.Children)
            if (stroke.Stroke is RadialGradientBrush brush) brush.Center = brush.GradientOrigin = new Point(x - stroke.Margin.Left, y - stroke.Margin.Top);
        var shift = (TranslateTransform)_touchSpot.RenderTransform;
        (shift.X, shift.Y) = (x - _touchSpot.Width / 2, y - _touchSpot.Height / 2);
    }

    void FadeTouch()
    {
        if (!_touchLit) return;
        _touchLit = false;
        Motion.To(_touchEdge, OpacityProperty, 0, Motion.Slow / 2);
        Motion.To(_touchSpot, OpacityProperty, 0, Motion.Slow / 2);
    }

    /// <summary>Место вокруг панели под наклон: сама панель на экране не двигается.</summary>
    void PadWindow(double pad)
    {
        if (_pad == pad || PanelPixels() is not { } panel) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var picture = PanelPicture(panel, scale, ShadowMargin);
        _pad = pad;
        Root.Margin = new Thickness(ShadowMargin + pad);
        MoveWithPicture(Inflate(panel, (ShadowMargin + pad) * scale), panel, picture, (int)Math.Round(ShadowMargin * scale));
    }

    /// <summary>Видимая панель на экране, в физических пикселях.</summary>
    Native.RECT? PanelPixels()
    {
        if (_resize is { } drag) return drag.Panel;
        if (!Native.GetWindowRect(_hwnd, out var r)) return null;
        return Inflate(r, -(ShadowMargin + _pad) * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    static Native.RECT Inflate(Native.RECT r, double by)
    {
        int d = (int)Math.Round(by);
        return new Native.RECT { Left = r.Left - d, Top = r.Top - d, Right = r.Right + d, Bottom = r.Bottom + d };
    }

    internal static void SelfTestPlate(Action<bool, string> check)
    {
        static PlateMotion Run(PlateMode mode, int hit, Func<double, Rect> panel, int frames)
        {
            var motion = new PlateMotion();
            for (int i = 0; i <= frames; i++) motion.Step(1 / 74.0, panel(i / 74.0), mode, hit);
            return motion;
        }
        static Rect At(double x, double y) => new(x, y, 360, 460);
        var right = Run(PlateMode.Move, 0, t => At(300 * t, 0), 60);
        check(right.Tilt.X > 0.5 && Math.Abs(right.Tilt.Y) < 0.01 && right.Flare.Length < 0.01, "плита: панель несут вправо, она наклоняется вправо");
        var up = Run(PlateMode.Move, 0, t => At(0, -900 * t), 60);
        check(up.Tilt.Y < -0.9 && Math.Abs(up.Tilt.X) < 0.01, "плита: панель несут вверх, она наклоняется вверх");
        var side = Run(PlateMode.Resize, 11, t => new Rect(0, 0, 360 + 600 * t, 460), 60);
        check(side.Flare.X > 0.7 && Math.Abs(side.Flare.Y) < 0.01 && side.Tilt.Length < 0.01, "плита: правый край тянут наружу, он раскрывается");
        var inward = Run(PlateMode.Resize, 10, t => new Rect(400 * t, 0, 900 - 400 * t, 460), 60);
        check(inward.Flare.X < -0.5, "плита: левый край толкают внутрь, он уходит");
        bool overshot = false;
        double stop = 300 * 60 / 74.0;
        int frames = 0;
        for (; frames < 300 && !right.Resting; frames++) { right.Step(1 / 74.0, At(stop, 0), PlateMode.Idle, 0); overshot |= right.Tilt.X < -0.005; }
        check(right.Resting && !overshot && frames < 30, $"плита: отпустили, вернулась без качания за {frames} кадров");
        var wild = Run(PlateMode.Move, 0, t => At(40000 * t, -40000 * t), 120);
        check(wild.Tilt.Length <= 1.001, "плита: рывок не наклоняет сильнее предела");
        var jump = new PlateMotion();
        jump.Step(1 / 74.0, At(0, 0), PlateMode.Move, 0); jump.Forget(); jump.Step(1 / 74.0, At(5000, 0), PlateMode.Move, 0);
        check(jump.Tilt.Length < 0.01, "плита: новый показ в другом месте движением не считается");

        // Сам шейдер: наклон вправо сжимает правую сторону и раздвигает левую, а края остаются прямыми.
        var shape = new Grid { Width = 200, Height = 200, Margin = new Thickness(50) };
        shape.Children.Add(new Rectangle { Margin = new Thickness(-40), Fill = Brushes.Transparent });
        shape.Children.Add(new Rectangle { Fill = Brushes.Red });
        var effect = new PlateEffect(40);
        effect.Hold(new Point(0.5, 0.5), 0);
        effect.Show(new Size(200, 200), new Vector(8, 0), default, sheen: false);
        shape.Effect = effect;
        var host = new Border { Child = shape };
        host.Measure(new Size(300, 300)); host.Arrange(new Rect(0, 0, 300, 300)); host.UpdateLayout();
        var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
        picture.Render(host);
        var pixels = new byte[300 * 300 * 4];
        picture.CopyPixels(pixels, 300 * 4, 0);
        int Height(int x) => Enumerable.Range(0, 300).Count(y => pixels[(y * 300 + x) * 4 + 2] > 128);
        int left = Height(60), middle = Height(150), far = Height(236);
        check(left > middle + 4 && middle > far + 4 && Math.Abs(left + far - 2 * middle) < 8, $"плита: шейдер наклоняет в перспективе, {left} > {middle} > {far}");
    }
}
