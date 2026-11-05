using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ArkCopyBuffer;

/// <summary>
/// Булавка включена: панель под напряжением. Пламя при этом останавливается (RefreshVfx), кромка берёт его бирюзовый цвет. Вокруг лежит ореол, по кромке бежит заряд; первый круг быстрый, от щелчка по булавке или открытия панели.
/// Всё нарисовано под поверхностью панели, поэтому текст не тонируется.
/// Закреплённая панель может стоять на экране часами, поэтому заряд бежит, только пока с ней работают: фокус у неё или мышь над ней.
/// В остальное время кромка светится неподвижно и не тратит кадров (движение стоит 15-20% ядра, покой около нуля). Без анимации окна заряда нет вовсе.
/// </summary>
partial class PanelWindow
{
    const double PinTailLength = 240, PinHaloRest = 0.9;
    const int PinChargeMs = 1000, PinFps = 30;
    static readonly TimeSpan PinOrbitLap = TimeSpan.FromSeconds(10);
    static readonly IEasingFunction PinChargeEase = CreatePinEase();
    bool _pinEnergyOn, _pinCharging, _pinMoving;

    static IEasingFunction CreatePinEase()
    {
        var ease = new CubicBezierEase(0, 0, 0.35, 0.85); // замедляется почти до скорости обычного круга, без остановки на стыке
        ease.Freeze();
        return ease;
    }

    (Rectangle Shape, double Length)[] PinTrail => [(PinBloom, 150), (PinFlare, 110), (PinTail, PinTailLength), (PinCore, 70)];

    bool PinAwake => ForegroundIsOurs() || Root.IsMouseOver;

    void InitializePinEnergy()
    {
        IsVisibleChanged += (_, _) => RefreshPinEnergy();
        Root.SizeChanged += (_, _) => { if (_pinEnergyOn) StartPinCharge(); };
        Root.MouseEnter += (_, _) => SetPinMotion(PinAwake);
        Root.MouseLeave += (_, _) => SetPinMotion(PinAwake);
    }

    void RefreshPinEnergy()
    {
        bool on = KeepOpen && IsVisible;
        if (on == _pinEnergyOn) return;
        _pinEnergyOn = on;
        if (on)
        {
            PinEnergy.Visibility = Visibility.Visible;
            Motion.To(PinEnergy, OpacityProperty, 1, Motion.Slow);
            StartPinCharge();
        }
        else if (IsVisible) Motion.To(PinEnergy, OpacityProperty, 0, Motion.Normal, done: () => { if (!_pinEnergyOn) StopPinEnergy(); });
        else StopPinEnergy();
    }

    /// <summary>Переключили анимацию окна, пока булавка включена.</summary>
    void RestartPinEnergy()
    {
        if (!_pinEnergyOn) return;
        _pinEnergyOn = false;
        StopPinEnergy();
        RefreshPinEnergy();
    }

    void StartPinCharge()
    {
        StopPinMotion();
        if (!Motion.Enabled || Root.ActualWidth <= 0) return;
        _pinCharging = true;
        PinSpark.Visibility = Visibility.Visible;
        Motion.To(PinHalo, OpacityProperty, PinHaloRest, PinChargeMs, from: 1);
        RunPinOrbit(TimeSpan.FromMilliseconds(PinChargeMs), PinChargeEase, forever: false);
    }

    /// <summary>Фокус или мышь пришли к панели либо ушли от неё: заряд бежит или гаснет. Первый быстрый круг досматривается до конца.</summary>
    internal void SetPinMotion(bool awake)
    {
        if (!_pinEnergyOn || _pinCharging || !Motion.Enabled || awake == _pinMoving) return;
        _pinMoving = awake;
        if (!awake) { FadeOutPinSpark(); return; }
        PinSpark.Visibility = Visibility.Visible;
        Motion.To(PinSpark, OpacityProperty, 1, Motion.Slow, from: 0);
        RunPinOrbit(PinOrbitLap, null, forever: true);
        var breath = new DoubleAnimation(PinHaloRest, 0.55, TimeSpan.FromSeconds(3.2))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(breath, PinFps);
        PinHalo.BeginAnimation(OpacityProperty, breath);
    }

    void FadeOutPinSpark() => Motion.To(PinSpark, OpacityProperty, 0, Motion.Slow, done: () => { if (!_pinMoving) StopPinMotion(); });

    void RunPinOrbit(TimeSpan lap, IEasingFunction? ease, bool forever)
    {
        foreach (var (shape, length) in PinTrail)
        {
            var (from, span) = PlacePinDash(shape, length);
            var orbit = new DoubleAnimation(from, from - span, lap) { EasingFunction = ease };
            if (forever) { orbit.RepeatBehavior = RepeatBehavior.Forever; Timeline.SetDesiredFrameRate(orbit, PinFps); }
            else if (shape == PinCore) orbit.Completed += (_, _) =>
            {
                _pinCharging = false;
                if (!_pinEnergyOn) return;
                if (PinAwake) SetPinMotion(true); else FadeOutPinSpark();
            };
            shape.BeginAnimation(Shape.StrokeDashOffsetProperty, orbit);
        }
    }

    /// <summary>Один штрих на весь периметр; все штрихи ведёт общая передняя точка, поэтому длинный хвост тянется за ядром.</summary>
    (double From, double Span) PlacePinDash(Rectangle shape, double length)
    {
        double unit = shape.StrokeThickness, outset = 3 - 2 * shape.Margin.Left; // контейнер шире панели на 1.5 с каждой стороны, сам штрих ещё на свой Margin
        double width = Root.ActualWidth + outset - unit, height = Root.ActualHeight + outset - unit;
        double period = 2 * (width + height) - (8 - 2 * Math.PI) * shape.RadiusX + 1; // +1: лишний пиксель незаметен, а нехватка дала бы второй штрих
        shape.StrokeDashArray = new DoubleCollection { length / unit, (period - length) / unit };
        return (-(PinTailLength - length) / unit, period / unit);
    }

    /// <summary>Заряд в заданной точке круга: для кадров в проверках.</summary>
    internal void PlacePinOrbit(double progress)
    {
        foreach (var (shape, length) in PinTrail)
        {
            var (from, span) = PlacePinDash(shape, length);
            shape.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            shape.StrokeDashOffset = from - span * progress;
        }
    }

    void StopPinMotion()
    {
        _pinCharging = _pinMoving = false;
        foreach (var (shape, _) in PinTrail) shape.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        PinHalo.BeginAnimation(OpacityProperty, null);
        PinHalo.Opacity = PinHaloRest;
        PinSpark.BeginAnimation(OpacityProperty, null);
        PinSpark.Opacity = 1;
        PinSpark.Visibility = Visibility.Collapsed;
    }

    void StopPinEnergy()
    {
        StopPinMotion();
        PinEnergy.BeginAnimation(OpacityProperty, null);
        PinEnergy.Opacity = 0;
        PinEnergy.Visibility = Visibility.Collapsed;
    }
}
