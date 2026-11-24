using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ArkCopyBuffer;

/// <summary>
/// Клавиша в строке подсказок, как в MicNoise (tacho::keycap): нажатая проседает чуть глубже, чем лежит, загорается акцентом с ореолом
/// и оседает; отпущенная пружинит чуть выше места. Реагирует на настоящие клавиши, колесо и щелчок мышью по самой клавише.
/// </summary>
sealed class KeyCap : FrameworkElement
{
    const double CapHeight = 17, Travel = 3;
    const int PressMs = 280, ReleaseMs = 240;

    public static readonly DependencyProperty FillProperty = Brush(nameof(Fill), "ControlFill"), StrokeProperty = Brush(nameof(Stroke), "WindowStroke"),
        InkProperty = Brush(nameof(Ink), "TextPrimary"), LitProperty = Brush(nameof(Lit), "Accent"), LitInkProperty = Brush(nameof(LitInk), "TextOnAccent");
    static readonly DependencyProperty DepthProperty = Number("Depth", 0), ScaleProperty = Number("Scale", 1), GlowProperty = Number("Glow", 0);
    static DependencyProperty Brush(string name, string key) => DependencyProperty.Register(name, typeof(Brush), typeof(KeyCap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    static DependencyProperty Number(string name, double value) => DependencyProperty.Register(name, typeof(double), typeof(KeyCap), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public Brush Lit { get => (Brush)GetValue(LitProperty); set => SetValue(LitProperty, value); }
    public Brush LitInk { get => (Brush)GetValue(LitInkProperty); set => SetValue(LitInkProperty, value); }

    readonly string _label;
    bool _down, _pressedHere;
    internal bool Down => _down;
    internal double Depth => (double)GetValue(DepthProperty);

    public KeyCap(string label)
    {
        _label = label;
        foreach (var (property, key) in new[] { (FillProperty, "ControlFill"), (StrokeProperty, "WindowStroke"), (InkProperty, "TextPrimary"), (LitProperty, "Accent"), (LitInkProperty, "TextOnAccent") })
            SetResourceReference(property, key);
        Height = CapHeight + Travel + 1;
        VerticalAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
    }

    public void Press(bool down)
    {
        if (_down == down) return;
        _down = down;
        Move();
    }

    /// <summary>Мгновенное действие (шаг колеса, щелчок): нажалась и отпустилась.</summary>
    public void Tap()
    {
        if (_down) { _down = false; }
        _down = true;
        Move();
        var release = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        release.Tick += (_, _) => { release.Stop(); Press(false); };
        release.Start();
    }

    void Move()
    {
        double depth = Depth;
        if (!Motion.Enabled)
        {
            foreach (var property in new[] { DepthProperty, ScaleProperty, GlowProperty }) BeginAnimation(property, null);
            SetValue(DepthProperty, _down ? Travel : 0.0);
            SetValue(GlowProperty, _down ? 1.0 : 0.0);
            return;
        }
        if (_down)
        {
            BeginAnimation(DepthProperty, Frames(PressMs, (0, depth), (0.4, Travel * 1.5), (1, Travel)));
            BeginAnimation(ScaleProperty, Frames(PressMs, (0, 1), (0.4, 0.9), (1, 1)));
            BeginAnimation(GlowProperty, Frames(PressMs, (0, (double)GetValue(GlowProperty)), (0.35, 1)));
        }
        else
        {
            BeginAnimation(DepthProperty, Frames(ReleaseMs, (0, depth), (0.5, -1.2), (1, 0)));
            BeginAnimation(ScaleProperty, Frames(ReleaseMs, (0, (double)GetValue(ScaleProperty)), (0.4, 1)));
            BeginAnimation(GlowProperty, Frames(ReleaseMs, (0, (double)GetValue(GlowProperty)), (0.5, 0)));
        }
    }

    static DoubleAnimationUsingKeyFrames Frames(int ms, params (double At, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(ms) };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var (at, value) in frames) animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromPercent(at), ease));
        return animation;
    }

    FormattedText Label(Brush? ink) => new(_label, CultureInfo.CurrentUICulture, FlowDirection, App.TextTypeface, 11, ink ?? Brushes.White, null,
        TextFormattingMode.Display, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size available) => new(Math.Ceiling(Label(null).Width) + 12, CapHeight + Travel + 1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, depth = Math.Max(-1.2, Depth), scale = (double)GetValue(ScaleProperty), glow = Math.Clamp((double)GetValue(GlowProperty), 0, 1);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        var cap = new Rect(w / 2 - w * scale / 2, 1 + depth + CapHeight * (1 - scale) / 2, w * scale, CapHeight * scale);
        if (glow > 0)
        {
            dc.PushOpacity(0.12 * glow);
            foreach (double e in new[] { 6.0, 4.0, 2.0 }) dc.DrawRoundedRectangle(Lit, null, Rect.Inflate(cap, e, e), 5 + e, 5 + e);
            dc.Pop();
        }
        // Боковина под клавишей: нажатая в неё утопает.
        var side = new Rect(0, 1 + Travel, w, CapHeight);
        dc.DrawRoundedRectangle(Stroke, null, side, 4, 4);
        if (glow > 0) { dc.PushOpacity(0.5 * glow); dc.DrawRoundedRectangle(Lit, null, side, 4, 4); dc.Pop(); }
        dc.DrawRoundedRectangle((Brush?)TryFindResource("KeyCapBase") ?? Brushes.Transparent, null, cap, 4, 4); // боковина не просвечивает сквозь полупрозрачную клавишу
        dc.DrawRoundedRectangle(Fill, new Pen(Stroke, 1), new Rect(cap.X + 0.5, cap.Y + 0.5, Math.Max(0, cap.Width - 1), Math.Max(0, cap.Height - 1)), 4, 4);
        if (glow > 0) { dc.PushOpacity(glow); dc.DrawRoundedRectangle(Lit, null, cap, 4, 4); dc.Pop(); }
        var text = Label(Ink);
        var at = new Point(Math.Round(cap.X + (cap.Width - text.Width * scale) / 2), Math.Round(cap.Y + (cap.Height - text.Height * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale, at.X, at.Y));
        dc.DrawText(text, at);
        if (glow > 0) { dc.PushOpacity(glow); dc.DrawText(Label(LitInk), at); dc.Pop(); }
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!CaptureMouse()) return;
        _pressedHere = true;
        Press(true);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (!_pressedHere) return;
        _pressedHere = false;
        Press(false);
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        bool motion = Motion.Enabled;
        try
        {
            Motion.Enabled = false;
            var keys = new KeyCaps { Text = "Ctrl+1-9" };
            check(keys.Caps.Count == 2 && new KeyCaps { Text = "Enter" }.Caps.Count == 1, "клавиши: сочетание показано по частям");
            var cap = keys.Caps[0];
            cap.Press(true);
            check(cap.Down && cap.Depth == Travel, "клавиши: нажатая проседает");
            cap.Press(false);
            check(!cap.Down && cap.Depth == 0, "клавиши: отпущенная возвращается");
            keys.Measure(new Size(400, 100));
            check(keys.DesiredSize.Width > 40 && keys.DesiredSize.Height == CapHeight + Travel + 1, "клавиши: место под ход клавиши");
        }
        finally { Motion.Enabled = motion; }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);
    sealed class Peer(KeyCap owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner._label;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
}

/// <summary>Сочетание по частям: «Ctrl+1-9» — две клавиши.</summary>
sealed class KeyCaps : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(KeyCaps),
        new PropertyMetadata("", (d, _) => ((KeyCaps)d).Fill()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    internal IReadOnlyList<KeyCap> Caps => Children.OfType<KeyCap>().ToList();

    public KeyCaps() => Orientation = Orientation.Horizontal;

    void Fill()
    {
        Children.Clear();
        foreach (var part in Text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Children.Add(new KeyCap(part) { Margin = new Thickness(Children.Count == 0 ? 0 : 3, 0, 0, 0) });
    }
}
