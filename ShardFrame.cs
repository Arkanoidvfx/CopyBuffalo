using System.Windows;
using System.Windows.Media;

namespace ArkCopyBuffer;

/// <summary>
/// Скин «Осколки»: кристальные пластины вокруг панели. Панель несут — задние отрываются и летят следом, в щелях виден свет, сыплются искры;
/// тянут край — его пластины встают дыбом, соседние расходятся. Остановились — защёлкиваются без качания, каждая в свой черёд.
/// Лежит в окне пламени, поверх него; в покое кадров не тратит. Стороны: 0 слева, 1 сверху, 2 справа, 3 снизу.
/// </summary>
sealed class ShardFrame : FrameworkElement
{
    const double Corner = 14, Pitch = 62, Reach = 24; // угол без обычных пластин, средняя длина пластины, дальше она не отлетает
    const int SparkCount = 8;

    sealed class Shard
    {
        public required int Side;
        public required bool Angle;                       // угловая: стоит на стыке Side и следующей по часовой
        public required double T, Length, Height, Mass, Chance;
        public Vector Offset, Speed;
        public double Turn, TurnSpeed;
        public readonly DrawingVisual Body = new(), Crack = new();
        public readonly MatrixTransform Place = new();
        public readonly ScaleTransform Grow = new(1, 1);
    }

    sealed class Spark
    {
        public Vector At, Speed;
        public double Life, Span, Turn;
        public readonly DrawingVisual Body = new();
        public readonly MatrixTransform Place = new();
    }

    readonly ContainerVisual _cracks = new(), _bodies = new(), _sparks = new();
    readonly List<Shard> _shards = [];
    readonly List<Spark> _pool = [];
    readonly Random _random = new(7);
    Color _surface, _glow;
    Size _built;
    double _emit;
    bool _held; // панель тянут: число пластин не меняется, они расходятся

    public ShardFrame()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        AddVisualChild(_cracks);
        AddVisualChild(_bodies);
        AddVisualChild(_sparks);
    }

    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => _cracks, 1 => _bodies, _ => _sparks };

    internal int Count => _shards.Count;
    internal IEnumerable<(int Side, bool Angle, Vector Offset, double Turn)> State => _shards.Select(s => (s.Side, s.Angle, s.Offset, s.Turn));
    internal int FlyingSparks => _pool.Count(s => s.Life < s.Span);
    public bool Resting => _shards.All(s => s.Offset.Length < 0.15 && s.Speed.Length < 2 && Math.Abs(s.Turn) < 0.3 && Math.Abs(s.TurnSpeed) < 4) && FlyingSparks == 0;

    public void Paint(Color surface, Color glow)
    {
        if (surface == _surface && glow == _glow) return;
        (_surface, _glow) = (surface, glow);
        foreach (var shard in _shards) Draw(shard);
        foreach (var spark in _pool) Draw(spark);
    }

    protected override Size ArrangeOverride(Size size)
    {
        if (!_held) Build(size);
        PlaceAll(size);
        return size;
    }

    static double Along(int side, Size size) => Math.Max(0, (side is 1 or 3 ? size.Width : size.Height) - 2 * Corner);

    /// <summary>Пластины одной стороны при том же числе остаются теми же; новые вырастают на месте.</summary>
    void Build(Size size)
    {
        if (size == _built || size.Width < 60 || size.Height < 60) return;
        bool first = _built == default;
        _built = size;
        for (int side = 0; side < 4; side++)
        {
            int want = Math.Max(1, (int)Math.Round(Along(side, size) / Pitch));
            var now = _shards.Where(s => s.Side == side && !s.Angle).ToList();
            if (now.Count == want) continue;
            foreach (var old in now) Discard(old);
            var dice = new Random(31 * (side + 1));
            var cuts = new double[want + 1]; // неровные куски: длинные рядом с короткими
            for (int i = 0; i < want; i++) cuts[i + 1] = cuts[i] + 0.45 + 1.1 * dice.NextDouble();
            double whole = cuts[want];
            for (int i = 0; i <= want; i++) cuts[i] /= whole;
            for (int i = 0; i < want; i++)
            {
                double share = cuts[i + 1] - cuts[i], t = (cuts[i] + cuts[i + 1]) / 2, near = Math.Pow(Math.Abs(t - 0.5) * 2, 3); // к углам пластины выше
                var shard = new Shard { Side = side, Angle = false, T = t, Length = Math.Max(6, share * Along(side, size) - 1.6), Height = 1.5 + 1.3 * near + 0.8 * dice.NextDouble(), Mass = 0.7 + 0.9 * dice.NextDouble(), Chance = dice.NextDouble() };
                Keep(shard);
                if (!first && i >= now.Count) Appear(shard);
            }
        }
        if (first)
            for (int side = 0; side < 4; side++)
                Keep(new Shard { Side = side, Angle = true, T = 0, Length = 24, Height = 6.5, Mass = 1.5, Chance = 0.5 });
    }

    static void Appear(Shard shard)
    {
        Motion.To(shard.Grow, ScaleTransform.ScaleXProperty, 1, Motion.Slow / 2, from: 0);
        Motion.To(shard.Grow, ScaleTransform.ScaleYProperty, 1, Motion.Slow / 2, from: 0);
    }

    void Keep(Shard shard)
    {
        shard.Body.Transform = new TransformGroup { Children = { shard.Grow, shard.Place } };
        shard.Crack.Opacity = 0;
        Draw(shard);
        _shards.Add(shard);
        _bodies.Children.Add(shard.Body);
        _cracks.Children.Add(shard.Crack);
    }

    void Discard(Shard shard)
    {
        _shards.Remove(shard);
        _bodies.Children.Remove(shard.Body);
        _cracks.Children.Remove(shard.Crack);
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    static SolidColorBrush Solid(Color color, double alpha = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    static StreamGeometry Shape(bool closed, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], closed, closed);
            path.PolyLineTo(points[1..], true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Ось X вдоль стороны по часовой, наружу — вверх. Две грани разной яркости и светлая кромка.</summary>
    void Draw(Shard shard)
    {
        if (_glow == default) return;
        double w = shard.Length / 2, h = shard.Height, cut = Math.Min(w * 0.7, 3 + 7 * shard.Chance), other = Math.Min(w * 0.6, 3 + 6 * (1 - shard.Chance)), peak = -w + cut + (2 * w - cut - other) * (0.2 + 0.6 * shard.Chance);
        // Скошенная плита: тёмная передняя грань и светлая фаска сверху с изломом.
        Point[] rim = [new(-w, 1.5), new(-w + cut, -h * 0.8), new(peak, -h), new(w - other, -h * 0.62), new(w, 1.5)];
        Point[] step = [new(-w + cut * 0.75, -h * 0.28), new(peak, -h * 0.5), new(w - other * 0.75, -h * 0.2)];
        if (shard.Angle)
        {
            rim = [new(-w, 10.5), new(-w * 0.5, -1.5), new(0, -h), new(w * 0.5, -1.5), new(w, 10.5), new(w * 0.55, 6.5), new(0, 2.5), new(-w * 0.55, 6.5)]; // изнутри вырезан по скруглению панели
            step = [new(-w * 0.42, 1.5), new(0, -h * 0.3), new(w * 0.42, 1.5)];
        }
        bool dark = _surface.R + _surface.G + _surface.B < 384;
        Color front = Mix(Mix(_surface, dark ? Colors.Black : Colors.White, 0.38), _glow, 0.05), bevel = Mix(_surface, _glow, dark ? 0.09 : 0.14), far = Mix(_surface, _glow, dark ? 0.04 : 0.2);
        var edge = new Pen(Solid(_glow, dark ? 0.16 : 0.28), 0.6) { LineJoin = PenLineJoin.Round };
        var light = new Pen(Solid(Colors.White, dark ? 0.09 : 0.5), 0.6);
        edge.Freeze();
        light.Freeze();
        using (var dc = shard.Body.RenderOpen())
        {
            dc.DrawGeometry(Solid(front), null, Shape(true, rim));
            if (shard.Angle) // клинок из двух граней до внутреннего угла
            {
                dc.DrawGeometry(Solid(bevel), null, Shape(true, rim[0], rim[1], rim[2], step[1]));
                dc.DrawGeometry(Solid(far), null, Shape(true, step[1], rim[2], rim[3], rim[4]));
            }
            else
            {
                dc.DrawGeometry(Solid(bevel), null, Shape(true, rim[1], rim[2], step[1], step[0]));
                dc.DrawGeometry(Solid(far), null, Shape(true, rim[2], rim[3], step[2], step[1]));
            }
            dc.DrawGeometry(null, edge, Shape(false, rim[..5]));
            dc.DrawLine(light, rim[1], rim[2]);
        }
        using (var dc = shard.Crack.RenderOpen())
        {
            if (shard.Angle) return;
            dc.DrawRectangle(Fade(0.07), null, new Rect(-w - 6, -4, 2 * w + 12, 6));
            dc.DrawRectangle(Fade(0.4), null, new Rect(-w - 3, -0.7, 2 * w + 6, 1.2));
        }

        LinearGradientBrush Fade(double alpha) // свет в щели мягко гаснет к краям пластины, а не рубится полосками
        {
            Color on = Color.FromArgb((byte)(255 * alpha), _glow.R, _glow.G, _glow.B), off = Color.FromArgb(0, _glow.R, _glow.G, _glow.B);
            var brush = new LinearGradientBrush([new(off, 0), new(on, 0.3), new(on, 0.7), new(off, 1)], 0);
            brush.Freeze();
            return brush;
        }
    }

    void Draw(Spark spark)
    {
        using var dc = spark.Body.RenderOpen();
        dc.DrawGeometry(Solid(Mix(_glow, Colors.White, 0.35)), null, Shape(true, new(-2.2, 1.2), new(0, -2.2), new(2, 1.4)));
    }

    static double Turned(Shard shard) => (shard.Side switch { 0 => 270, 1 => 0, 2 => 90, _ => 180 }) + (shard.Angle ? 45 : 0);
    static Vector Normal(Shard shard)
    {
        double angle = (Turned(shard) - 90) * Math.PI / 180;
        return new Vector(Math.Cos(angle), Math.Sin(angle));
    }

    static Point Anchor(Shard shard, Size size)
    {
        double run = Corner + shard.T * Along(shard.Side, size);
        if (shard.Angle) return shard.Side switch { 0 => new(2.4, 2.4), 1 => new(size.Width - 2.4, 2.4), 2 => new(size.Width - 2.4, size.Height - 2.4), _ => new(2.4, size.Height - 2.4) };
        return shard.Side switch { 0 => new(0, size.Height - run), 1 => new(run, 0), 2 => new(size.Width, run), _ => new(size.Width - run, size.Height) };
    }

    void PlaceAll(Size size)
    {
        foreach (var shard in _shards)
        {
            var at = Anchor(shard, size);
            var matrix = Matrix.Identity;
            matrix.Rotate(Turned(shard));
            matrix.Translate(at.X, at.Y);
            shard.Crack.Transform = new MatrixTransform(matrix);
            PlaceOne(shard, at);
        }
    }

    static void PlaceOne(Shard shard, Point at)
    {
        var matrix = Matrix.Identity;
        matrix.Rotate(Turned(shard) + shard.Turn);
        matrix.Translate(at.X + shard.Offset.X, at.Y + shard.Offset.Y);
        shard.Place.Matrix = matrix;
        shard.Crack.Opacity = Math.Min(1, shard.Offset.Length / 11 + Math.Abs(shard.Turn) / 50);
    }

    /// <summary>speed — скорости сторон панели (слева, сверху, справа, снизу), DIP/с; grab — где её держат, 0…1.</summary>
    public void Step(double dt, PlateMode mode, int hit, Point grab, double[] speed)
    {
        _held = mode == PlateMode.Resize;
        dt = Math.Min(dt, 0.05);
        if (dt <= 0) return;
        var size = RenderSize;
        var motion = mode == PlateMode.Move ? new Vector(speed[0], speed[1]) : default;
        double s = Math.Tanh(motion.Length / 420);
        var dir = motion.Length > 1 ? motion / motion.Length : default;
        foreach (var shard in _shards)
        {
            var normal = Normal(shard);
            var target = default(Vector);
            double turn = 0;
            if (mode == PlateMode.Move && s > 0.02)
            {
                double ahead = Vector.Multiply(normal, dir), trail = Math.Max(0, -ahead), flank = 1 - Math.Abs(ahead);
                target = dir * (-s * (trail * (2.5 + 6 * shard.Chance) + flank * (1 + 2.5 * shard.Chance))) + normal * (s * (trail * 2 * shard.Chance + flank)) - normal * (Math.Max(0, ahead) * s);
                turn = s * (trail * 12 * (shard.Chance - 0.5) + flank * 5 * Math.Sign(normal.X * dir.Y - normal.Y * dir.X));
            }
            else if (mode == PlateMode.Resize) (target, turn) = Opened(shard, normal, hit, grab, speed);
            double omega = (target == default && turn == 0 ? 44 : 30) / Math.Sqrt(shard.Mass); // защёлкивается быстрее, чем отрывается; лёгкие раньше тяжёлых
            (shard.Offset, shard.Speed) = PlateMotion.Follow(shard.Offset, shard.Speed, target, dt, omega);
            if (shard.Offset.Length > Reach) shard.Offset *= Reach / shard.Offset.Length;
            var (angle, rate) = PlateMotion.Follow(new Vector(shard.Turn, 0), new Vector(shard.TurnSpeed, 0), new Vector(turn, 0), dt, omega);
            (shard.Turn, shard.TurnSpeed) = (angle.X, rate.X);
            PlaceOne(shard, Anchor(shard, size));
        }
        Sparks(dt, s, dir, size);
    }

    /// <summary>Растягивание: пластины ведомой стороны встают дыбом от места захвата, соседние клонятся за ней.</summary>
    static (Vector, double) Opened(Shard shard, Vector normal, int hit, Point grab, double[] speed)
    {
        var target = default(Vector);
        double turn = 0;
        for (int side = 0; side < 4; side++)
        {
            if (!PlateMotion.Holds(hit, side)) continue;
            double outward = Math.Tanh((side < 2 ? -speed[side] : speed[side]) / 420), power = Math.Abs(outward);
            if (shard.Side == side && !shard.Angle)
            {
                double held = side switch { 0 => 1 - grab.Y, 1 => grab.X, 2 => grab.Y, _ => 1 - grab.X }, away = shard.T - held;
                target += normal * (power * (1.5 + 3.5 * shard.Chance) * (outward > 0 ? 1 : 0.35));
                turn += Math.Sign(away) * power * (3 + 7 * Math.Min(1, Math.Abs(away) * 1.6)) * (outward > 0 ? 1 : -0.6);
            }
            else if (!shard.Angle && shard.Side % 2 != side % 2)
            {
                double near = (side, shard.Side) switch { (0, 1) or (2, 3) or (1, 2) or (3, 0) => 1 - shard.T, _ => shard.T };
                var pull = side switch { 0 => new Vector(-1, 0), 1 => new Vector(0, -1), 2 => new Vector(1, 0), _ => new Vector(0, 1) };
                turn += Math.Sign(normal.X * pull.Y - normal.Y * pull.X) * -outward * 5 * near * near;
                target += normal * (power * 1.5 * near * near * shard.Chance);
            }
        }
        return (target, turn);
    }

    void Sparks(double dt, double s, Vector dir, Size size)
    {
        _emit = s > 0.6 ? _emit + dt * 12 * s : 0;
        while (_emit >= 1)
        {
            _emit--;
            var spark = _pool.FirstOrDefault(p => p.Life >= p.Span) ?? NewSpark();
            if (spark == null) break;
            var from = _shards.Where(p => !p.Angle && Vector.Multiply(Normal(p), dir) < -0.3).ToList();
            if (from.Count == 0) break;
            var source = from[_random.Next(from.Count)];
            spark.At = (Vector)Anchor(source, size) + source.Offset;
            spark.Speed = dir * -(40 + 140 * _random.NextDouble()) + new Vector(_random.NextDouble() - 0.5, _random.NextDouble() - 0.5) * 70;
            (spark.Life, spark.Span, spark.Turn) = (0, 0.15 + 0.18 * _random.NextDouble(), 360 * _random.NextDouble());
        }
        foreach (var spark in _pool)
        {
            if (spark.Life >= spark.Span) continue;
            spark.Life += dt;
            spark.At += spark.Speed * dt;
            spark.Speed *= Math.Exp(-2.2 * dt);
            double left = Math.Max(0, 1 - spark.Life / spark.Span);
            spark.Body.Opacity = 0.6 * left * left;
            var matrix = Matrix.Identity;
            matrix.Scale(0.3 + 0.4 * left, 0.3 + 0.4 * left);
            matrix.Rotate(spark.Turn + 260 * spark.Life);
            matrix.Translate(spark.At.X, spark.At.Y);
            spark.Place.Matrix = matrix;
        }
    }

    Spark? NewSpark()
    {
        if (_pool.Count >= SparkCount) return null;
        var spark = new Spark();
        spark.Body.Transform = spark.Place;
        Draw(spark);
        _pool.Add(spark);
        _sparks.Children.Add(spark.Body);
        return spark;
    }

    /// <summary>Всё на место сразу: панель скрыли, переставили или движение кончилось.</summary>
    public void Rest()
    {
        _held = false;
        foreach (var spark in _pool) { spark.Life = spark.Span; spark.Body.Opacity = 0; }
        foreach (var shard in _shards) { shard.Offset = shard.Speed = default; shard.Turn = shard.TurnSpeed = 0; }
        InvalidateArrange();
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var frame = new ShardFrame();
        frame.Paint(Color.FromRgb(32, 32, 32), Color.FromRgb(13, 255, 230));
        void Arrange(double w, double h) { frame.Measure(new Size(w, h)); frame.Arrange(new Rect(0, 0, w, h)); }
        frame.Visibility = Visibility.Visible;
        Arrange(360, 460);
        int count = frame.Count;
        check(count > 20 && frame.Resting, $"осколки: рамка собрана, {count} пластин, в покое");
        void Run(PlateMode mode, int hit, double[] speed, int frames) { for (int i = 0; i < frames; i++) frame.Step(1 / 74.0, mode, hit, new Point(0.5, 0), speed); }
        Run(PlateMode.Move, 0, [700, 0, 700, 0], 40);
        double Mean(int side) => frame.State.Where(s => s.Side == side && !s.Angle).Average(s => s.Offset.X);
        check(Mean(0) < -3 && Mean(2) > -2 && frame.FlyingSparks > 0 && frame.State.All(s => s.Offset.Length <= Reach + 0.01),
            $"осколки: панель несут вправо, левые отрываются ({Mean(0):0.0}), правые прижаты ({Mean(2):0.0}), летят искры");
        int frames = 0;
        for (; frames < 200 && !frame.Resting; frames++) Run(PlateMode.Idle, 0, [0, 0, 0, 0], 1);
        check(frame.Resting && frames < 55, $"осколки: остановились и защёлкнулись за {frames} кадров");
        Run(PlateMode.Resize, 11, [0, 0, 500, 0], 40);
        double Open(int side) => frame.State.Where(s => s.Side == side && !s.Angle).Average(s => Math.Abs(s.Turn));
        check(Open(2) > 3 && Open(0) < 0.1 && Open(1) > 0.3, "осколки: тянут правый край, его пластины встают, левые на месте");
        Arrange(520, 460);
        check(frame.Count == count, "осколки: пока тянут, пластины расходятся и число их прежнее");
        frame.Rest();
        Arrange(520, 460);
        check(frame.Count > count && frame.Resting, "осколки: отпустили, в щелях выросли новые");
        Run(PlateMode.Move, 0, [90000, -90000, 90000, -90000], 60);
        check(frame.State.All(s => s.Offset.Length <= Reach + 0.01), "осколки: рывок не уносит их за окно");
    }
}
