using Autodesk.Revit.DB;

namespace PileCage;

/// <summary>
/// Acceptance of the supported B-rep of a single complete vertical circular
/// cylinder. Uses analytic face types and their actual trimmed boundaries,
/// not Solid.Volume as an exact analytic-volume oracle. This intentionally
/// rejects unsupported split caps or non-analytic boundary representations.
/// </summary>
internal static class HostCylinderTopology
{
    private const double TwoPi = 2 * Math.PI;
    private const double CoordinateToleranceMm = 0.01;
    private const double DirectionTolerance = 1e-8;
    private readonly record struct Interval(double Start, double End);

    public static void Validate(Solid solid, XYZ origin, double radius, double low, double high)
    {
        double tol = CoordinateToleranceMm / 304.8;
        if (!double.IsFinite(radius) || radius <= tol || !double.IsFinite(low) || !double.IsFinite(high) || high - low <= tol)
            Fail("圆柱半径或高度无效");
        double angleTol = Math.Max(1e-9, tol / radius);
        var faces = solid.Faces.Cast<Face>().ToArray();
        var caps = faces.OfType<PlanarFace>().ToArray();
        var sides = faces.OfType<CylindricalFace>().ToArray();
        if (caps.Length != 2 || sides.Length < 1 || caps.Length + sides.Length != faces.Length)
            Fail("须恰有上下两个平面封口，其余面均为圆柱侧面；不支持孔洞、切口或其他曲面");
        int lowerCaps = 0, upperCaps = 0;
        foreach (var cap in caps)
        {
            var normal = cap.FaceNormal.Normalize();
            if (Math.Abs(normal.Z) < 1 - DirectionTolerance) Fail("封口面必须水平");
            bool isLow = Math.Abs(cap.Origin.Z - low) <= tol;
            bool isHigh = Math.Abs(cap.Origin.Z - high) <= tol;
            if (isLow == isHigh) Fail("封口面不在圆柱上下边界");
            if (isLow) { lowerCaps++; if (normal.Z > -1 + DirectionTolerance) Fail("下封口面的外法向不正确"); }
            if (isHigh) { upperCaps++; if (normal.Z < 1 - DirectionTolerance) Fail("上封口面的外法向不正确"); }
            if (cap.EdgeLoops.Size != 1) Fail("封口面必须只有一个完整圆周边界，不得含孔洞或内部边界");
            var intervals = new List<Interval>();
            foreach (Edge edge in cap.EdgeLoops.Cast<EdgeArray>().Single())
                AddCircleArc(edge.AsCurve(), origin, radius, isLow ? low : high, tol, angleTol, intervals);
            RequireFullCircle(intervals, angleTol, "封口面圆周");
        }
        if (lowerCaps != 1 || upperCaps != 1) Fail("上下封口面数量不正确");

        var allLower = new List<Interval>();
        var allUpper = new List<Interval>();
        foreach (var side in sides)
        {
            var axis = side.Axis.Normalize();
            var r0 = side.get_Radius(0); var r1 = side.get_Radius(1);
            if (Math.Abs(axis.Z) < 1 - DirectionTolerance ||
                Math.Abs(r0.GetLength() - radius) > tol || Math.Abs(r1.GetLength() - radius) > tol ||
                Math.Abs(r0.Normalize().DotProduct(r1.Normalize())) > DirectionTolerance ||
                Math.Abs(axis.DotProduct(r0.Normalize())) > DirectionTolerance ||
                Math.Abs(axis.DotProduct(r1.Normalize())) > DirectionTolerance ||
                Math.Sqrt(Math.Pow(side.Origin.X - origin.X, 2) + Math.Pow(side.Origin.Y - origin.Y, 2)) > tol)
                Fail("所有侧面必须为同轴、同半径、无倾斜的圆柱面");
            VerifyOutwardSideNormal(side, origin);
            var lower = new List<Interval>(); var upper = new List<Interval>();
            foreach (var loop in side.EdgeLoops.Cast<EdgeArray>())
            foreach (Edge edge in loop)
            {
                var curve = edge.AsCurve();
                if (curve is Arc arc)
                {
                    bool onLow = Math.Abs(arc.Center.Z - low) <= tol;
                    bool onHigh = Math.Abs(arc.Center.Z - high) <= tol;
                    if (onLow == onHigh) Fail("侧面的圆弧边界必须位于上下封口，不得存在中间高度的孔洞或切口边界");
                    AddCircleArc(arc, origin, radius, onLow ? low : high, tol, angleTol, onLow ? lower : upper);
                }
                else if (curve is Line line && line.IsBound)
                {
                    var p = line.GetEndPoint(0); var q = line.GetEndPoint(1);
                    if (Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2)) > tol ||
                        Math.Abs(Math.Min(p.Z, q.Z) - low) > tol || Math.Abs(Math.Max(p.Z, q.Z) - high) > tol ||
                        Math.Abs(Math.Sqrt(Math.Pow(p.X - origin.X, 2) + Math.Pow(p.Y - origin.Y, 2)) - radius) > tol ||
                        Math.Abs(Math.Sqrt(Math.Pow(q.X - origin.X, 2) + Math.Pow(q.Y - origin.Y, 2)) - radius) > tol)
                        Fail("侧面的直线边界只能是位于圆柱表面且贯通全高的竖向接缝");
                }
                else Fail("侧面出现非圆弧、非竖向接缝的边界，无法确认为完整直圆柱");
            }
            var mergedLower = DisjointCoverage(lower, angleTol, "单块侧面下边界");
            var mergedUpper = DisjointCoverage(upper, angleTol, "单块侧面上边界");
            if (mergedLower.Count == 0 || mergedLower.Count != mergedUpper.Count)
                Fail("单块侧面的上下圆周覆盖不对应");
            for (int i = 0; i < mergedLower.Count; i++)
                if (Math.Abs(mergedLower[i].Start - mergedUpper[i].Start) > angleTol ||
                    Math.Abs(mergedLower[i].End - mergedUpper[i].End) > angleTol)
                    Fail("单块侧面的上下圆周角区间不一致");
            allLower.AddRange(lower); allUpper.AddRange(upper);
        }
        RequireFullCircle(allLower, angleTol, "所有侧面的下边界");
        RequireFullCircle(allUpper, angleTol, "所有侧面的上边界");
    }

    private static void VerifyOutwardSideNormal(CylindricalFace face, XYZ origin)
    {
        var bounds = face.GetBoundingBox();
        foreach (double u in new[] { 0.5, 0.25, 0.75 })
        foreach (double v in new[] { 0.5, 0.25, 0.75 })
        {
            var uv = new UV(bounds.Min.U + u * (bounds.Max.U - bounds.Min.U), bounds.Min.V + v * (bounds.Max.V - bounds.Min.V));
            if (!face.IsInside(uv)) continue;
            var point = face.Evaluate(uv);
            var radial = new XYZ(point.X - origin.X, point.Y - origin.Y, 0).Normalize();
            if (face.ComputeNormal(uv).Normalize().DotProduct(radial) < 1 - DirectionTolerance)
                Fail("圆柱侧面外法向不正确，可能存在内部孔面或反向面");
            return;
        }
        Fail("无法在侧面实际修剪区域内核验外法向");
    }

    private static void AddCircleArc(Curve curve, XYZ origin, double radius, double z, double tol, double angleTol, List<Interval> intervals)
    {
        if (curve is not Arc arc) { Fail("圆柱封口边界必须为解析圆弧"); return; }
        if (Math.Abs(arc.Radius - radius) > tol || Math.Abs(arc.Center.Z - z) > tol ||
            Math.Sqrt(Math.Pow(arc.Center.X - origin.X, 2) + Math.Pow(arc.Center.Y - origin.Y, 2)) > tol ||
            Math.Abs(arc.Normal.Normalize().Z) < 1 - DirectionTolerance)
            Fail("边界圆弧与圆柱轴线、半径或封口高度不一致");
        double span = arc.Length / arc.Radius;
        if (!double.IsFinite(span) || span <= 0 || span > TwoPi + angleTol) Fail("圆周边界的角跨度无效");
        if (!arc.IsBound || span >= TwoPi - angleTol)
        {
            if (Math.Abs(span - TwoPi) > angleTol) Fail("无界圆弧不是完整圆周");
            intervals.Add(new(0, TwoPi)); return;
        }
        var start = arc.GetEndPoint(0);
        double phase = Math.Atan2(start.Y - origin.Y, start.X - origin.X);
        // Arc parameter increases in the right-handed XDirection/YDirection
        // frame. Reverse-normal circles traverse the XY angle clockwise.
        double low = arc.Normal.Z >= 0 ? phase : phase - span;
        low %= TwoPi; if (low < 0) low += TwoPi;
        if (low <= angleTol || TwoPi - low <= angleTol) low = 0;
        double high = low + span;
        if (Math.Abs(high - TwoPi) <= angleTol) high = TwoPi;
        if (high <= TwoPi) intervals.Add(new(low, high));
        else { intervals.Add(new(low, TwoPi)); intervals.Add(new(0, high - TwoPi)); }
    }

    private static List<Interval> DisjointCoverage(List<Interval> intervals, double tol, string context)
    {
        var sorted = intervals.OrderBy(interval => interval.Start).ThenBy(interval => interval.End).ToArray();
        var merged = new List<Interval>();
        foreach (var interval in sorted)
        {
            if (interval.End <= interval.Start) Fail(context + "含空角区间");
            if (merged.Count == 0) { merged.Add(interval); continue; }
            var last = merged[^1];
            if (interval.Start < last.End - tol) Fail(context + "存在重叠覆盖或重复面");
            if (interval.Start <= last.End + tol) merged[^1] = new(last.Start, Math.Max(last.End, interval.End));
            else merged.Add(interval);
        }
        return merged;
    }
    private static void RequireFullCircle(List<Interval> intervals, double tol, string context)
    {
        var merged = DisjointCoverage(intervals, tol, context);
        if (merged.Count != 1 || merged[0].Start > tol || Math.Abs(merged[0].End - TwoPi) > tol)
            Fail(context + "未无重叠地覆盖完整360度圆周");
    }
    private static void Fail(string detail) => throw new InvalidOperationException("所选宿主未通过完整直圆柱面与边界核查：" + detail + "。");
}
