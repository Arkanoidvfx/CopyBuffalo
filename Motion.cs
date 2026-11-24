using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ArkCopyBuffer;

/// <summary>
/// Кривая cubic-bezier, как в CSS и Fluent. WPF умеет её только через KeySpline в ключевых кадрах,
/// а здесь нужна обычная DoubleAnimation с From = null (продолжение от текущего значения).
/// </summary>
sealed class CubicBezierEase : EasingFunctionBase
{
    readonly double _x1, _y1, _x2, _y2;

    public CubicBezierEase(double x1, double y1, double x2, double y2)
    {
        (_x1, _y1, _x2, _y2) = (x1, y1, x2, y2);
        EasingMode = EasingMode.EaseIn; // EaseInCore = сама кривая, без отражений
    }

    protected override double EaseInCore(double t)
    {
        if (t <= 0 || t >= 1) return t;
        // Ищем параметр u, при котором x(u) = t: Ньютон, при плохой производной — деление пополам.
        double u = t;
        for (int i = 0; i < 8; i++)
        {
            double dx = Bezier(u, _x1, _x2) - t;
            if (Math.Abs(dx) < 1e-5) return Bezier(u, _y1, _y2);
            double d = Slope(u, _x1, _x2);
            if (Math.Abs(d) < 1e-6) break;
            u -= dx / d;
        }
        double lo = 0, hi = 1;
        u = t;
        for (int i = 0; i < 30; i++)
        {
            double x = Bezier(u, _x1, _x2);
            if (Math.Abs(x - t) < 1e-5) break;
            if (x < t) lo = u; else hi = u;
            u = (lo + hi) / 2;
        }
        return Bezier(u, _y1, _y2);
    }

    static double Bezier(double u, double p1, double p2) => ((1 - 3 * p2 + 3 * p1) * u + (3 * p2 - 6 * p1)) * u * u + 3 * p1 * u;
    static double Slope(double u, double p1, double p2) => 3 * (1 - 3 * p2 + 3 * p1) * u * u + 2 * (3 * p2 - 6 * p1) * u + 3 * p1;

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(_x1, _y1, _x2, _y2);
}

/// <summary>
/// Общие правила движения: вход — резкий старт и мягкая остановка, выход — разгон. Длительности 83/167/250 мс.
/// Выключено (переключатель «Анимация окна» в настройках) — значения ставятся сразу.
/// </summary>
static class Motion
{
    public const int Fast = 83, Normal = 167, Slow = 250;
    public static readonly IEasingFunction In = Frozen(new CubicBezierEase(0, 0, 0, 1));
    public static readonly IEasingFunction Out = Frozen(new CubicBezierEase(0.7, 0, 1, 0.5));

    public static bool Enabled { get; set; } = true;

    static IEasingFunction Frozen(CubicBezierEase ease) { ease.Freeze(); return ease; }

    /// <summary>Анимирует свойство от текущего значения (или from) к to; без движения — сразу to.</summary>
    public static void To(IAnimatable target, DependencyProperty property, double to, int ms,
        IEasingFunction? ease = null, double? from = null, Action? done = null, int delayMs = 0)
    {
        if (!Enabled || ms <= 0)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            done?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            From = from, EasingFunction = ease ?? In, BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        if (done != null) animation.Completed += (_, _) => done();
        target.BeginAnimation(property, animation);
    }

    /// <summary>Снять анимацию, оставив её текущее значение: для прерывания (колесо во время плавного масштаба).</summary>
    public static void Settle(IAnimatable target, DependencyProperty property)
    {
        var d = (DependencyObject)target;
        object value = d.GetValue(property);
        target.BeginAnimation(property, null);
        d.SetValue(property, value);
    }

    /// <summary>Свой, незамороженный сдвиг: трансформ из шаблона или стиля заморожен и анимироваться не может.</summary>
    public static TranslateTransform Shift(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } shift) return shift;
        var old = element.RenderTransform as TranslateTransform;
        shift = new TranslateTransform(old?.X ?? 0, old?.Y ?? 0);
        element.RenderTransform = shift;
        return shift;
    }

    public static ScaleTransform Scale(UIElement element)
    {
        if (element.RenderTransform is ScaleTransform { IsFrozen: false } scale) return scale;
        var old = element.RenderTransform as ScaleTransform;
        scale = new ScaleTransform(old?.ScaleX ?? 1, old?.ScaleY ?? 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return scale;
    }

    /// <summary>Появление: прозрачность и сдвиг на dy к месту.</summary>
    public static void Enter(UIElement element, double dx = 0, double dy = 8, int ms = Normal, int delayMs = 0)
    {
        var shift = Shift(element);
        To(element, UIElement.OpacityProperty, 1, ms, from: 0, delayMs: delayMs);
        To(shift, TranslateTransform.XProperty, 0, ms, from: dx, delayMs: delayMs);
        To(shift, TranslateTransform.YProperty, 0, ms, from: dy, delayMs: delayMs);
    }

    /// <summary>Щелчок: 0.7 → 1.2 → 1 (булавка, счётчик на вкладке).</summary>
    public static void Pop(UIElement element, double peak = 1.2, double start = 0.7)
    {
        if (!Enabled) return;
        var scale = Scale(element);
        var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(Slow) };
        pop.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromPercent(0)));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(0.6), In));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), In));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>
    /// Раскрыть по высоте (новая запись, InfoBar): Height 0 → естественная, затем Height снова «авто».
    /// Высоту меняем только у одного элемента за раз: это пересчёт разметки на каждом кадре.
    /// </summary>
    public static void Expand(FrameworkElement element, int ms = Slow, Action? done = null)
    {
        element.BeginAnimation(FrameworkElement.HeightProperty, null);
        element.Visibility = Visibility.Visible;
        element.Measure(new Size(element.ActualWidth > 0 ? element.ActualWidth : double.PositiveInfinity, double.PositiveInfinity));
        double height = element.DesiredSize.Height;
        if (!Enabled || height <= 0) { done?.Invoke(); return; }
        To(element, FrameworkElement.HeightProperty, height, ms, from: 0, done: () =>
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            done?.Invoke();
        });
    }

    /// <summary>Свернуть по высоте и спрятать.</summary>
    public static void Collapse(FrameworkElement element, int ms = Normal, Action? done = null)
    {
        void Finish()
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Visibility = Visibility.Collapsed;
            done?.Invoke();
        }
        if (!Enabled || element.Visibility != Visibility.Visible || element.ActualHeight <= 0) { Finish(); return; }
        To(element, UIElement.OpacityProperty, 0, ms, Out);
        To(element, FrameworkElement.HeightProperty, 0, ms, Out, from: element.ActualHeight, done: Finish);
    }

    static readonly IEasingFunction Unfold = Frozen(new CubicBezierEase(0.16, 1, 0.3, 1));
    const int MenuOpenMs = 230, MenuItemMs = 190, MenuStaggerMs = 16, MenuCloseMs = 130;

    /// <summary>
    /// Меню разворачивается от своего якоря, строки догоняют друг друга; закрытие — снимок меню тает и подтягивается к якорю:
    /// само меню WPF прячет сразу, и пункт срабатывает без ожидания анимации. fromY &lt; 0 — меню под якорем, &gt; 0 — над ним.
    /// Снимок снят с того, что на экране в этот миг: закрыть можно и посреди открытия, без скачка.
    /// </summary>
    public static void AnimateOpen(ContextMenu menu, double fromY)
    {
        System.Windows.Interop.HwndSource? popup = null;
        bool above = fromY > 0;
        IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            // WM_WINDOWPOSCHANGING с SWP_HIDEWINDOW: окно меню ещё на экране. Closed приходит, когда его уже нет, и меню мигало бы на кадр.
            if (msg == 0x46 && (System.Runtime.InteropServices.Marshal.ReadInt32(l, IntPtr.Size * 2 + 16) & 0x80) != 0 && Enabled
                && Native.GetWindowRect(hwnd, out var place) && Picture(menu) is { } last) FadeAway(hwnd, last, place, above ? 5 : -5);
            return IntPtr.Zero;
        }
        void Hide() => menu.Opacity = Enabled ? 0 : 1; // до начала анимации меню успевает показать кадр целиком
        Hide();
        menu.Closed += (_, _) => { popup?.RemoveHook(Hook); popup = null; Hide(); };
        menu.Opened += (_, _) =>
        {
            menu.Opacity = 1;
            popup = PresentationSource.FromVisual(menu) as System.Windows.Interop.HwndSource;
            popup?.AddHook(Hook);
            if (menu.Template?.FindName("MenuRoot", menu) is not FrameworkElement root) return;
            var place = Place(menu);
            if (fromY == 0 && Native.GetCursorPos(out var cursor)) above = cursor.Y > (place.Top + place.Bottom) / 2; // по месту мыши: меню могло открыться вверх
            if (!Enabled) return;
            double seen = ReplaceGhost(place); // прежнее меню ещё тает на этом месте: продолжаем с его яркости, а не с нуля
            // База каждого свойства — начало движения: до первого такта анимации видно её, и снимок меню, закрытого сразу, был бы готовым меню.
            To(root, UIElement.OpacityProperty, 1, 120, from: seen);
            root.Opacity = seen;
            if (seen > 0) return;
            var scale = new ScaleTransform(0.965, 0.82);
            var shift = new TranslateTransform(0, above ? 8 : -8);
            root.RenderTransformOrigin = new Point(0.5, above ? 1 : 0);
            root.RenderTransform = new TransformGroup { Children = { scale, shift } };
            To(scale, ScaleTransform.ScaleYProperty, 1, MenuOpenMs, Unfold, from: 0.82);
            To(scale, ScaleTransform.ScaleXProperty, 1, MenuOpenMs, Unfold, from: 0.965);
            To(shift, TranslateTransform.YProperty, 0, MenuOpenMs, Unfold, from: above ? 8 : -8);
            var rows = menu.Items.OfType<FrameworkElement>().ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                int order = Math.Min(above ? rows.Count - 1 - i : i, 9);
                var row = Shift(rows[i]);
                To(rows[i], UIElement.OpacityProperty, rows[i].IsEnabled ? 1 : 0.6, MenuItemMs, from: 0, delayMs: order * MenuStaggerMs);
                rows[i].Opacity = 0; // до своей очереди строки не видно
                To(row, TranslateTransform.YProperty, 0, MenuItemMs + 40, Unfold, from: above ? 10 : -10, delayMs: order * MenuStaggerMs);
                row.Y = above ? 10 : -10;
            }
        };
    }

    static Native.RECT Place(FrameworkElement element)
    {
        var corner = element.PointToScreen(default);
        var dpi = VisualTreeHelper.GetDpi(element);
        return new Native.RECT { Left = (int)corner.X, Top = (int)corner.Y, Right = (int)(corner.X + element.ActualWidth * dpi.DpiScaleX), Bottom = (int)(corner.Y + element.ActualHeight * dpi.DpiScaleY) };
    }

    static System.Windows.Media.Imaging.BitmapSource? Picture(FrameworkElement element)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1) return null;
        var dpi = VisualTreeHelper.GetDpi(element);
        var picture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        picture.Render(element);
        picture.Freeze();
        return picture;
    }

    /// <summary>Тающий снимок закрытого меню; одновременно он один.</summary>
    sealed class Ghost
    {
        public required IntPtr Hwnd;
        public required Native.RECT Place;
        public double Alpha = 1;
        public int FramesLeft = int.MaxValue;
    }
    static Ghost? _ghost;

    /// <summary>Яркость снимка, который тает на том же месте, или 0. Снимок уходит, когда новое меню уже нарисовано.</summary>
    static double ReplaceGhost(Native.RECT place)
    {
        if (_ghost is not { } ghost) return 0;
        ghost.FramesLeft = 2;
        return Math.Abs(ghost.Place.Left - place.Left) < 4 && Math.Abs(ghost.Place.Top - place.Top) < 4 ? ghost.Alpha : 0;
    }

    /// <summary>Снимок picture встаёт под окно under (оно прячется следом) и тает.</summary>
    static void FadeAway(IntPtr under, System.Windows.Media.Imaging.BitmapSource picture, Native.RECT place, double drift)
    {
        place.Right = place.Left + picture.PixelWidth;
        place.Bottom = place.Top + picture.PixelHeight;
        var hwnd = Native.ShowPicture(place, picture, under);
        if (hwnd == IntPtr.Zero) return;
        Native.DwmFlush(); // снимок уже на экране, прежде чем окно спрячется: иначе между ними бывал пустой кадр
        if (_ghost != null) _ghost.FramesLeft = 0;
        var ghost = _ghost = new Ghost { Hwnd = hwnd, Place = place };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Frame(object? sender, EventArgs e)
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / MenuCloseMs), gone = t * t * (3 - 2 * t);
            int dy = (int)Math.Round(drift * gone);
            var at = new Native.RECT { Left = place.Left, Right = place.Right, Top = place.Top + dy, Bottom = place.Bottom + dy };
            ghost.Alpha = 1 - gone;
            if (t < 1 && ghost.FramesLeft-- > 0) { Native.UpdateLayered(hwnd, at, picture, 0, 0, (byte)(255 * ghost.Alpha)); return; }
            CompositionTarget.Rendering -= Frame;
            Native.DestroyWindow(hwnd);
            if (_ghost == ghost) _ghost = null;
        }
        CompositionTarget.Rendering += Frame;
    }
}
