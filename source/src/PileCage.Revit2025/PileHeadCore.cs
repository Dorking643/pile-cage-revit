namespace PileCage;

public enum PileHeadForm { Straight, Bent90, Splayed, Waterproof }
public enum PileHeadSegmentKind { Line, Arc }
public sealed record PileHeadPoint(double X, double Y, double Z);
/// <summary>Radial is outward displacement from the existing bar; Z is height above the column top. All lengths are mm.</summary>
public sealed record PileHeadRadialPoint(double Radial, double Z);
public sealed record PileHeadBarSource(string BarId, PileHeadPoint LowerEnd, PileHeadPoint AxisOrigin,
    double ColumnTopZ, double Diameter, double StandardBendDiameter);
public sealed record PileHeadIssue(string Code, string Message, bool IsError);
public sealed record PileHeadBounds(PileHeadPoint Min, PileHeadPoint Max);

public sealed class PileHeadParameters
{
    public PileHeadForm Form { get; set; } = PileHeadForm.Straight;
    /// <summary>Required height above column top for Straight/Splayed/Waterproof. The splayed path is deliberately longer than this vertical projection.</summary>
    public double AnchorageLength { get; set; }
    public double VerticalProjection { get; set; }
    /// <summary>Straight length after the 90-degree bend, excluding the circular arc.</summary>
    public double HorizontalStraightLength { get; set; }
    public double SplayAngleDegrees { get; set; } = 75;
    /// <summary>Null uses (the actual type's StandardBendDiameter + bar diameter)/2 for each bar.</summary>
    public double? CenterlineBendRadius { get; set; }
    /// <summary>Already computed la or laE supplied by the separate rule layer; the geometry layer never invents it.</summary>
    public double? ReferenceLa { get; set; }
    /// <summary>Already computed lab or labE, not la, supplied by the separate rule layer.</summary>
    public double? ReferenceLab { get; set; }
    public double? CapWidth { get; set; }
    public double? CapDepth { get; set; }
    public double? CapHeight { get; set; }
    public double? PileEmbedment { get; set; }
    /// <summary>Optional geometric side/top clear distance. It is not a normative default.</summary>
    public double CapCover { get; set; }
}

public sealed class PileHeadSegment
{
    public PileHeadSegmentKind Kind { get; init; }
    public required PileHeadPoint Start { get; init; }
    public required PileHeadPoint Mid { get; init; }
    public required PileHeadPoint End { get; init; }
    public required PileHeadRadialPoint LocalStart { get; init; }
    public required PileHeadRadialPoint LocalMid { get; init; }
    public required PileHeadRadialPoint LocalEnd { get; init; }
    public PileHeadPoint? Center { get; init; }
    public double CenterlineRadius { get; init; }
    public double SweepRadians { get; init; }
    public double Length { get; init; }
}

public sealed class PileHeadBarPath
{
    public required string BarId { get; init; }
    public double Diameter { get; init; }
    public required PileHeadPoint RadialDirection { get; init; }
    public required PileHeadPoint LowerEnd { get; init; }
    public required PileHeadPoint TopCrossing { get; init; }
    public required PileHeadPoint End { get; init; }
    public required IReadOnlyList<PileHeadSegment> Segments { get; init; }
    public double HeadLength { get; init; }
    public double HeadVerticalProjection { get; init; }
    public double MinimumCenterlineBendRadius { get; init; }
    public double ActualCenterlineBendRadius { get; init; }
    public required PileHeadBounds SteelBounds { get; init; }
    public required PileHeadBounds HeadSteelBounds { get; init; }
}

public sealed class PileHeadPlan
{
    public required IReadOnlyList<PileHeadBarPath> Bars { get; init; }
    public required PileHeadBounds SteelBounds { get; init; }
    public required PileHeadBounds HeadSteelBounds { get; init; }
    public PileHeadBounds? CapBounds { get; init; }
    public required IReadOnlyList<PileHeadIssue> Issues { get; init; }
    public string GeometryConvention { get; init; } = "Existing bar lower endpoints and XY positions are preserved. Bends are true circular centerline arcs above the column top. Splayed anchorage uses a vertical projection at least equal to the supplied required anchorage length; the figure's inclined dimension denotes length along the bar, so this projection convention is conservative and is not attributed to the figure or standard. Cap bounds are centered on the supplied column axis, with bottom at column top minus pile embedment. Waterproof reinforcement follows the straight form; waterproof layers are separate model elements.";
}

/// <summary>Pure geometry and explicitly labelled checks against the supplied detail drawing. No Revit or material-rule dependency.</summary>
public static class PileHeadPlanner
{
    private const double Epsilon = 1e-8;
    public static IReadOnlyList<PileHeadIssue> Validate(PileHeadParameters p, IReadOnlyList<PileHeadBarSource> sources)
    {
        var issues = BasicChecks(p, sources);
        if (issues.Any(i => i.IsError)) return issues;
        var paths = sources.Select(s => BuildBar(p, s)).ToArray();
        var cap = Cap(p, sources[0]);
        if (cap != null)
        {
            foreach (var bar in paths)
            {
                var b = bar.HeadSteelBounds;
                // The existing vertical bar enters through the pile/cap interface; bottom cover is
                // not applied at that opening. Check cap sides and top including bar radius.
                if (b.Min.X < cap.Min.X + p.CapCover - Epsilon || b.Max.X > cap.Max.X - p.CapCover + Epsilon ||
                    b.Min.Y < cap.Min.Y + p.CapCover - Epsilon || b.Max.Y > cap.Max.Y - p.CapCover + Epsilon ||
                    b.Max.Z > cap.Max.Z - p.CapCover + Epsilon)
                    issues.Add(new("CAP_FIT", $"{bar.BarId}：桩顶钢筋实体包络超出所输入承台的侧面或顶面净空。", true));
            }
        }
        return issues;
    }

    public static PileHeadPlan Build(PileHeadParameters p, IReadOnlyList<PileHeadBarSource> sources)
    {
        var issues = Validate(p, sources);
        if (issues.Any(i => i.IsError)) throw new ArgumentException(string.Join("\n", issues.Where(i => i.IsError).Select(i => i.Message)));
        var paths = sources.Select(s => BuildBar(p, s)).ToArray();
        return new PileHeadPlan
        {
            Bars = paths, SteelBounds = Union(paths.Select(b => b.SteelBounds)), HeadSteelBounds = Union(paths.Select(b => b.HeadSteelBounds)),
            CapBounds = Cap(p, sources[0]), Issues = issues
        };
    }

    private static List<PileHeadIssue> BasicChecks(PileHeadParameters p, IReadOnlyList<PileHeadBarSource> sources)
    {
        var issues = new List<PileHeadIssue>();
        void Error(string code, string message) => issues.Add(new(code, message, true));
        if (p == null) { Error("PARAMETERS", "缺少桩顶参数。"); return issues; }
        if (sources == null || sources.Count == 0) { Error("SOURCES", "没有可用的既有纵向钢筋。"); return issues; }
        if (!Enum.IsDefined(p.Form)) Error("FORM", "桩顶形式无效。");
        if (new[] { p.AnchorageLength, p.VerticalProjection, p.HorizontalStraightLength, p.SplayAngleDegrees, p.CapCover }.Any(v => !double.IsFinite(v)))
            Error("FINITE", "桩顶参数必须为有限数值。");
        if (p.CapCover < 0) Error("CAP_COVER", "承台侧面和顶面净距不得为负。");
        foreach (var value in new[] { p.CenterlineBendRadius, p.ReferenceLa, p.ReferenceLab })
            if (value.HasValue && (!double.IsFinite(value.Value) || value.Value <= 0)) Error("REFERENCE", "给定的弯曲半径及锚固参考长度必须是有限正数。");
        var capValues = new[] { p.CapWidth, p.CapDepth, p.CapHeight, p.PileEmbedment };
        if (capValues.Any(v => v.HasValue))
        {
            if (capValues.Any(v => !v.HasValue)) Error("CAP_INCOMPLETE", "承台宽、长、高及桩嵌入高度需同时输入。");
            else if (capValues.Any(v => !double.IsFinite(v!.Value)) || p.CapWidth <= 0 || p.CapDepth <= 0 || p.CapHeight <= 0 || p.PileEmbedment < 0 || p.PileEmbedment >= p.CapHeight)
                Error("CAP_DIMENSIONS", "承台尺寸应为正数，桩嵌入高度应在零至承台高度之间。");
        }
        if (p.Form == PileHeadForm.Bent90)
        {
            if (p.VerticalProjection <= 0 || p.HorizontalStraightLength <= 0) Error("BENT_LENGTH", "90°弯锚的竖向投影和弯后水平直段必须大于零。");
        }
        else if (p.AnchorageLength <= 0) Error("ANCHORAGE_LENGTH", "柱顶以上的锚固竖向投影必须大于零。");
        if (p.Form == PileHeadForm.Splayed && (p.SplayAngleDegrees < 75 || p.SplayAngleDegrees > 90))
            Error("FIGURE_ANGLE", "按给定图示检查：外斜段与水平面的夹角应为75°至90°。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source == null) { Error("SOURCE_NULL", "既有纵筋信息不能为空。"); continue; }
            if (string.IsNullOrWhiteSpace(source.BarId) || !ids.Add(source.BarId)) Error("SOURCE_ID", "既有纵筋标识必须非空且唯一。");
            if (source.LowerEnd == null || source.AxisOrigin == null || !Finite(source.LowerEnd) || !Finite(source.AxisOrigin) ||
                new[] { source.ColumnTopZ, source.Diameter, source.StandardBendDiameter }.Any(v => !double.IsFinite(v)))
            { Error("SOURCE_FINITE", source.BarId + "：既有钢筋和柱轴线必须具有有限坐标及尺寸。"); continue; }
            if (source.Diameter <= 0 || source.StandardBendDiameter <= 0) Error("SOURCE_DIAMETER", source.BarId + "：实际钢筋直径及类型弯曲直径应大于零。");
            if (source.LowerEnd.Z >= source.ColumnTopZ) Error("SOURCE_HEIGHT", source.BarId + "：纵筋下端必须位于柱顶以下。");
            var first = sources[0];
            if (first?.AxisOrigin != null && (Math.Abs(source.AxisOrigin.X - first.AxisOrigin.X) > Epsilon || Math.Abs(source.AxisOrigin.Y - first.AxisOrigin.Y) > Epsilon || Math.Abs(source.ColumnTopZ - first.ColumnTopZ) > Epsilon))
                Error("COMMON_COLUMN", "本次桩顶生成的纵筋必须使用同一圆柱轴线和柱顶高程。");
            bool bent = p.Form == PileHeadForm.Bent90 || (p.Form == PileHeadForm.Splayed && p.SplayAngleDegrees < 90);
            if (bent)
            {
                if (Hypot(source.LowerEnd.X - source.AxisOrigin.X, source.LowerEnd.Y - source.AxisOrigin.Y) <= Epsilon)
                    Error("RADIAL_DIRECTION", source.BarId + "：轴心纵筋不能唯一确定径向外弯方向。");
                double minimumRadius = (source.StandardBendDiameter + source.Diameter) / 2, radius = p.CenterlineBendRadius ?? minimumRadius;
                if (radius < minimumRadius - Epsilon) Error("TYPE_BEND_RADIUS", source.BarId + "：中心线弯曲半径小于实际类型弯曲内径的一半加钢筋半径。");
                if (p.Form == PileHeadForm.Bent90 && p.VerticalProjection < radius - Epsilon)
                    Error("BEND_BELOW_TOP", source.BarId + "：所给竖向投影不足以容纳圆弧，会使弯曲进入柱内。");
                if (p.Form == PileHeadForm.Splayed && p.AnchorageLength < radius * Math.Sin((90 - p.SplayAngleDegrees) * Math.PI / 180) - Epsilon)
                    Error("SPLAY_ARC_HEIGHT", source.BarId + "：锚固竖向投影不足以容纳竖直至外斜的圆弧过渡。");
            }
            // These ratios are sourced from the supplied detail figures, not inferred as universal code provisions.
            if (p.Form is PileHeadForm.Straight or PileHeadForm.Splayed)
            {
                if (p.AnchorageLength < 35 * source.Diameter - Epsilon) Error("FIGURE_35D", source.BarId + "：按给定图示检查，直锚/外斜锚固长度应至少为35d；外斜采用不小于该值的竖向投影。");
            }
            if (p.Form == PileHeadForm.Bent90)
            {
                if (p.VerticalProjection < 20 * source.Diameter - Epsilon) Error("FIGURE_20D", source.BarId + "：按给定图示检查，90°弯锚竖向投影应至少为20d。");
                if (p.HorizontalStraightLength < 15 * source.Diameter - Epsilon) Error("FIGURE_15D", source.BarId + "：按给定图示检查，圆弧后的水平直段应至少为15d。");
                if (p.ReferenceLab.HasValue && p.VerticalProjection < 0.6 * p.ReferenceLab.Value - Epsilon)
                    Error("FIGURE_06LAB", source.BarId + "：按给定图示检查，竖向投影小于所提供lab或labE的0.6倍。");
            }
            else if (p.ReferenceLa.HasValue && p.AnchorageLength < p.ReferenceLa.Value - Epsilon)
                Error("REFERENCE_LA", source.BarId + "：锚固长度小于规则层所提供的la或laE。");
        }
        if (p.Form == PileHeadForm.Bent90 && !p.ReferenceLab.HasValue)
            issues.Add(new("REFERENCE_LAB_MISSING", "未提供lab/labE；几何层只检查图示20d、15d及几何关系，不推算设计锚固长度。", false));
        if (p.Form != PileHeadForm.Bent90 && !p.ReferenceLa.HasValue)
            issues.Add(new("REFERENCE_LA_MISSING", "未提供la/laE；几何层不推算设计锚固长度。", false));
        if (p.Form == PileHeadForm.Splayed)
            issues.Add(new("SPLAY_PROJECTION_CONVENTION", "图示la/laE标注沿斜筋。本实现保守采用竖向投影不小于所需锚固长度，实际沿筋长度另行报告。", false));
        return issues;
    }

    private static PileHeadBarPath BuildBar(PileHeadParameters p, PileHeadBarSource source)
    {
        double dx = source.LowerEnd.X - source.AxisOrigin.X, dy = source.LowerEnd.Y - source.AxisOrigin.Y, radialDistance = Hypot(dx, dy);
        var radial = radialDistance > Epsilon ? new PileHeadPoint(dx / radialDistance, dy / radialDistance, 0) : new PileHeadPoint(1, 0, 0);
        PileHeadPoint World(PileHeadRadialPoint q) => new(source.LowerEnd.X + q.Radial * radial.X, source.LowerEnd.Y + q.Radial * radial.Y, source.ColumnTopZ + q.Z);
        var segments = new List<PileHeadSegment>();
        void Line(PileHeadRadialPoint start, PileHeadRadialPoint end)
        {
            double length = Hypot(end.Radial - start.Radial, end.Z - start.Z);
            if (length <= Epsilon) return;
            var mid = new PileHeadRadialPoint((start.Radial + end.Radial) / 2, (start.Z + end.Z) / 2);
            segments.Add(new() { Kind = PileHeadSegmentKind.Line, Start = World(start), Mid = World(mid), End = World(end), LocalStart = start, LocalMid = mid, LocalEnd = end, Length = length });
        }
        void Arc(double radius, double startZ, double angle)
        {
            if (angle <= Epsilon) return;
            PileHeadRadialPoint At(double a) => new(radius * (1 - Math.Cos(a)), startZ + radius * Math.Sin(a));
            var start = At(0); var mid = At(angle / 2); var end = At(angle);
            segments.Add(new() { Kind = PileHeadSegmentKind.Arc, Start = World(start), Mid = World(mid), End = World(end), LocalStart = start, LocalMid = mid, LocalEnd = end,
                Center = World(new(radius, startZ)), CenterlineRadius = radius, SweepRadians = angle, Length = radius * angle });
        }
        double minimum = (source.StandardBendDiameter + source.Diameter) / 2, radius = p.CenterlineBendRadius ?? minimum;
        var bottom = new PileHeadRadialPoint(0, source.LowerEnd.Z - source.ColumnTopZ);
        if (p.Form == PileHeadForm.Bent90)
        {
            Line(bottom, new(0, p.VerticalProjection - radius));
            Arc(radius, p.VerticalProjection - radius, Math.PI / 2);
            Line(new(radius, p.VerticalProjection), new(radius + p.HorizontalStraightLength, p.VerticalProjection));
        }
        else if (p.Form == PileHeadForm.Splayed && p.SplayAngleDegrees < 90)
        {
            double alpha = p.SplayAngleDegrees * Math.PI / 180, sweep = Math.PI / 2 - alpha;
            Line(bottom, new(0, 0)); Arc(radius, 0, sweep);
            double r0 = radius * (1 - Math.Cos(sweep)), z0 = radius * Math.Sin(sweep);
            double straight = Math.Max(0, (p.AnchorageLength - z0) / Math.Sin(alpha));
            Line(new(r0, z0), new(r0 + straight * Math.Cos(alpha), p.AnchorageLength));
        }
        else Line(bottom, new(0, p.AnchorageLength));
        var top = World(new(0, 0)); var end = segments[^1].End;
        var points = segments.SelectMany(s => new[] { s.Start, s.Mid, s.End }).ToArray();
        var headPoints = points.Where(q => q.Z >= source.ColumnTopZ - Epsilon).Append(top).ToArray();
        return new PileHeadBarPath
        {
            BarId = source.BarId, Diameter = source.Diameter, RadialDirection = radial, LowerEnd = source.LowerEnd, TopCrossing = top, End = end, Segments = segments,
            HeadLength = segments.Sum(s => s.Length) - (source.ColumnTopZ - source.LowerEnd.Z), HeadVerticalProjection = end.Z - source.ColumnTopZ,
            MinimumCenterlineBendRadius = minimum, ActualCenterlineBendRadius = segments.Any(s => s.Kind == PileHeadSegmentKind.Arc) ? radius : 0,
            SteelBounds = Bounds(points, source.Diameter / 2), HeadSteelBounds = Bounds(headPoints, source.Diameter / 2)
        };
    }

    private static bool Finite(PileHeadPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static double Hypot(double x, double y)
    {
        double a = Math.Max(Math.Abs(x), Math.Abs(y)), b = Math.Min(Math.Abs(x), Math.Abs(y));
        return a == 0 ? 0 : a * Math.Sqrt(1 + (b / a) * (b / a));
    }
    private static PileHeadBounds Bounds(IEnumerable<PileHeadPoint> points, double radius)
    {
        var values = points.ToArray();
        return new(new(values.Min(q => q.X) - radius, values.Min(q => q.Y) - radius, values.Min(q => q.Z) - radius),
            new(values.Max(q => q.X) + radius, values.Max(q => q.Y) + radius, values.Max(q => q.Z) + radius));
    }
    private static PileHeadBounds Union(IEnumerable<PileHeadBounds> bounds)
    {
        var values = bounds.ToArray();
        return new(new(values.Min(b => b.Min.X), values.Min(b => b.Min.Y), values.Min(b => b.Min.Z)),
            new(values.Max(b => b.Max.X), values.Max(b => b.Max.Y), values.Max(b => b.Max.Z)));
    }
    private static PileHeadBounds? Cap(PileHeadParameters p, PileHeadBarSource source) => p.CapWidth.HasValue && p.CapDepth.HasValue && p.CapHeight.HasValue && p.PileEmbedment.HasValue
        ? new(new(source.AxisOrigin.X - p.CapWidth.Value / 2, source.AxisOrigin.Y - p.CapDepth.Value / 2, source.ColumnTopZ - p.PileEmbedment.Value),
            new(source.AxisOrigin.X + p.CapWidth.Value / 2, source.AxisOrigin.Y + p.CapDepth.Value / 2, source.ColumnTopZ - p.PileEmbedment.Value + p.CapHeight.Value)) : null;
}
