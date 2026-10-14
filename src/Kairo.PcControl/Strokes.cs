namespace Kairo.PcControl;

/// <summary>
/// Points for a stroke of <c>drag_path</c>: a model that can say "an ellipse in this box" or "a smooth line through these 6 points"
/// draws a recognisable figure with a few numbers, where a polyline through the same points would be a jagged guess.
/// </summary>
internal static class Strokes
{
    /// <summary>The most points a stroke may have, after shapes and smoothing have added theirs.</summary>
    public const int MaxPoints = 1500;

    /// <summary>A closed ellipse inscribed in the box (clockwise from the top).</summary>
    public static List<(int X, int Y)> Ellipse(int x1, int y1, int x2, int y2, int segments = 48)
    {
        double cx = (x1 + x2) / 2.0, cy = (y1 + y2) / 2.0, rx = Math.Abs(x2 - x1) / 2.0, ry = Math.Abs(y2 - y1) / 2.0;
        var points = new List<(int, int)>();
        for (var i = 0; i <= segments; i++)
        {
            var angle = -Math.PI / 2 + 2 * Math.PI * i / segments;
            points.Add(((int)Math.Round(cx + rx * Math.Cos(angle)), (int)Math.Round(cy + ry * Math.Sin(angle))));
        }
        return points;
    }

    /// <summary>A closed rectangle (the corners, and the first corner again).</summary>
    public static List<(int X, int Y)> Rectangle(int x1, int y1, int x2, int y2) =>
        [(x1, y1), (x2, y1), (x2, y2), (x1, y2), (x1, y1)];

    /// <summary>
    /// A smooth curve through the points (Catmull-Rom). When the first and the last point are the same the curve is closed and has no
    /// corner there.
    /// </summary>
    public static List<(int X, int Y)> Smooth(IReadOnlyList<(int X, int Y)> points, int perSpan = 8)
    {
        if (points.Count < 3)
            return [.. points];
        var closed = points[0] == points[^1] && points.Count > 3;
        var p = closed ? points.Take(points.Count - 1).ToList() : [.. points];
        var n = p.Count;
        (int X, int Y) At(int i) => closed ? p[((i % n) + n) % n] : p[Math.Clamp(i, 0, n - 1)];

        var result = new List<(int, int)> { p[0] };
        var spans = closed ? n : n - 1;
        for (var i = 0; i < spans; i++)
        {
            var (p0, p1, p2, p3) = (At(i - 1), At(i), At(i + 1), At(i + 2));
            for (var s = 1; s <= perSpan; s++)
            {
                var t = (double)s / perSpan;
                result.Add((Catmull(p0.X, p1.X, p2.X, p3.X, t), Catmull(p0.Y, p1.Y, p2.Y, p3.Y, t)));
            }
        }
        return result;
    }

    private static int Catmull(double p0, double p1, double p2, double p3, double t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return (int)Math.Round(0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3));
    }
}
