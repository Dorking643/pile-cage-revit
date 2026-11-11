using System.Text.Json;
using System.Text.Json.Nodes;

namespace PileCage;

public sealed class PositionPlateInput
{
    // Optional provenance for a geometry snapshot read from a loaded plate type.
    // Null keeps the legacy explicit-dimension workflow valid.
    public long? FamilyTypeId { get; set; }
    public string? FamilyTypeUniqueId { get; set; }
    public string? FamilyDocumentUniqueId { get; set; }
    public string? FamilyName { get; set; }
    public string? FamilyTypeName { get; set; }
    public double? FirstLevelFromBaseMm { get; set; }
    public double? LastLevelLimitFromBaseMm { get; set; }
    public double AxialSpacingMm { get; set; } = 2000;
    public int PerLevelCount { get; set; } = 4;
    public double WidthMm { get; set; } = 60;
    public double ThicknessMm { get; set; } = 4;
    public double NominalBlankLengthMm { get; set; } = 356;
    public double BottomLegMm { get; set; } = 100;
    public double LowerRiseMm { get; set; } = 40;
    public double MiddleLegMm { get; set; } = 60;
    public double TopLegMm { get; set; } = 100;
    public double OutermostProjectionMm { get; set; } = 40;
    public double ReferenceWallGapMm { get; set; } = 10;
    public PositionPlateInput Clone() => (PositionPlateInput)MemberwiseClone();
}

public sealed record PositionPlateSectionPoint(double Radial, double Elevation);
public sealed class PositionPlatePiece
{
    public required string Id { get; init; }
    public int LevelIndex { get; init; }
    public int MainBarIndex { get; init; }
    public double AngleRadians { get; init; }
    public double CenterElevationMm { get; init; }
    public required IReadOnlyList<Point3> Vertices { get; init; }
    public required Point3 Centroid { get; init; }
}
public sealed class PositionPlatePlan
{
    public required PositionPlateInput Parameters { get; init; }
    public int LevelCount { get; init; }
    public int TotalCount { get; init; }
    public double TotalHeightMm { get; init; }
    public double IdealCenterlineLengthMm { get; init; }
    public double CrossSectionAreaMm2 { get; init; }
    public double VolumePerPlateMm3 { get; init; }
    public double MinimumWallClearanceMm { get; init; }
    public double WallGapDifferenceMm { get; init; }
    public double MainSurfaceRadiusMm { get; init; }
    public required IReadOnlyList<PositionPlateSectionPoint> Centerline { get; init; }
    public required IReadOnlyList<PositionPlateSectionPoint> Profile { get; init; }
    public required IReadOnlyList<PositionPlatePiece> Plates { get; init; }
    public string GeometryConvention { get; init; } = "Ideal sharp-corner plate with constant thickness and mitered corners; no finite bending radius or weld solid is inferred. End inner faces are tangent to the longitudinal-bar outer surface. The 40 mm projection includes thickness; nominal 356 mm procurement length is retained independently of geometric centerline length. The circular pile surface is the hole-wall reference for the reported actual corner clearance. The input axial range is treated as one section requiring at least two levels; no automatic fabrication-section review is performed.";
}

public static class PositionPlatePlanner
{
    public static PositionPlateInput Defaults(CageInput cage)
    {
        int count = Enumerable.Range(3, Math.Max(0, cage.MainCount - 2)).FirstOrDefault(n => cage.MainCount % n == 0);
        return new() { PerLevelCount = count == 0 ? 3 : count, FirstLevelFromBaseMm = cage.MainStart + 150, LastLevelLimitFromBaseMm = cage.MainEnd - 150 };
    }
    public static PositionPlateInput Resolve(CageInput cage, PositionPlateInput input)
    {
        var p = input.Clone(); double height = p.BottomLegMm + p.LowerRiseMm + p.MiddleLegMm + p.TopLegMm;
        p.FirstLevelFromBaseMm ??= cage.MainStart + height / 2;
        p.LastLevelLimitFromBaseMm ??= cage.MainEnd - height / 2;
        return p;
    }
    public static IReadOnlyList<string> Validate(CageInput cage, PositionPlateInput input)
    {
        var errors = new List<string>();
        if (cage == null || input == null) return ["缺少圆柱配筋或定位钢片参数。"];
        var p = Resolve(cage, input);
        var values = new[] { p.FirstLevelFromBaseMm!.Value, p.LastLevelLimitFromBaseMm!.Value, p.AxialSpacingMm, p.WidthMm, p.ThicknessMm,
            p.NominalBlankLengthMm, p.BottomLegMm, p.LowerRiseMm, p.MiddleLegMm, p.TopLegMm, p.OutermostProjectionMm, p.ReferenceWallGapMm,
            cage.Diameter, cage.Height, cage.MainStart, cage.MainEnd, cage.MainDiameter, cage.SpiralDiameter, cage.Cover, cage.X, cage.Y, cage.Z };
        if (values.Any(v => !double.IsFinite(v))) return ["定位钢片及宿主参数必须为有限数值。"];
        if (cage.MainCount < 3 || p.PerLevelCount < 3 || p.PerLevelCount > cage.MainCount || cage.MainCount % p.PerLevelCount != 0)
            errors.Add("每道定位钢片不少于3块，且块数必须整除主筋数量，才能同时均布并贴合主筋。");
        if (new[] { p.AxialSpacingMm, p.WidthMm, p.ThicknessMm, p.NominalBlankLengthMm, p.BottomLegMm, p.LowerRiseMm, p.MiddleLegMm, p.TopLegMm }.Any(v => v <= 0) || p.ReferenceWallGapMm < 0)
            errors.Add("间距、板宽、板厚、名义长度和折形尺寸必须为正数，参考孔壁净距不得为负。");
        if (p.OutermostProjectionMm <= p.ThicknessMm) errors.Add("包含厚度的外凸尺寸必须大于板厚。");
        if (cage.Diameter <= 0 || cage.Height <= 0 || cage.MainDiameter <= 0 || !Enum.IsDefined(cage.CoverBasis)) errors.Add("圆柱或主筋几何数据无效。");
        double height = p.BottomLegMm + p.LowerRiseMm + p.MiddleLegMm + p.TopLegMm;
        double first = p.FirstLevelFromBaseMm.Value, last = p.LastLevelLimitFromBaseMm.Value;
        if (first - height / 2 < Math.Max(0, cage.MainStart) - 1e-8 || last + height / 2 > Math.Min(cage.Height, cage.MainEnd) + 1e-8 || last < first)
            errors.Add("钢片全高应位于本次柱内主筋范围，首道中心和末道中心上限须由低到高。");
        if (p.AxialSpacingMm < height - 1e-8) errors.Add("轴向间距小于钢片全高，会使同一方向的相邻钢片重叠。");
        if (errors.Count > 0) return errors;
        double count = Math.Floor((last - first) / p.AxialSpacingMm + 1e-9) + 1;
        if (count < 2) errors.Add("本次输入范围按一节处理，定位钢片须至少布置两道；请调整首末位置或间距。此项不代替实际分节审查。");
        if (count * p.PerLevelCount > 10000) errors.Add("定位钢片超过10000块，超出本版建模容量。");
        double mainSurface = CagePlanner.Radii(cage).Main + cage.MainDiameter / 2;
        if (mainSurface <= 0) errors.Add("主筋外表面半径无效。");
        else if (2 * Math.Atan((p.WidthMm / 2) / mainSurface) >= 2 * Math.PI / p.PerLevelCount - 1e-10)
            errors.Add("定位钢片周向宽度超出各自均布扇区，可能与相邻钢片重叠；此为几何检查，不是规范限值。");
        double wallGap = cage.Diameter / 2 - Math.Sqrt(Math.Pow(mainSurface + p.OutermostProjectionMm, 2) + Math.Pow(p.WidthMm / 2, 2));
        if (wallGap < -1e-8) errors.Add($"定位钢片实体角点穿出圆柱表面，实际最小净距为{wallGap:F3} mm。");
        if (errors.Count == 0)
        {
            var line = Centerline(p); var profile = Offset(line, p.ThicknessMm / 2).Concat(Offset(line, -p.ThicknessMm / 2).Reverse()).ToArray();
            if (SelfIntersects(profile)) errors.Add("板厚与折形尺寸组合导致尖角截面自交，请减小板厚或增加折段长度。");
        }
        return errors;
    }
    public static PositionPlatePlan Build(CageInput cage, PositionPlateInput input)
    {
        var errors = Validate(cage, input); if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
        var p = Resolve(cage, input); double t = p.ThicknessMm, half = t / 2, outer = p.OutermostProjectionMm - half;
        double h1 = p.BottomLegMm, h2 = h1 + p.LowerRiseMm, h3 = h2 + p.MiddleLegMm, height = h3 + p.TopLegMm;
        PositionPlateSectionPoint[] line = Centerline(p);
        var profile = Offset(line, half).Concat(Offset(line, -half).Reverse()).ToArray();
        double area2 = 0, cx6 = 0, cy6 = 0;
        for (int i = 0; i < profile.Length; i++)
        {
            var a = profile[i]; var b = profile[(i + 1) % profile.Length]; double cross = a.Radial * b.Elevation - b.Radial * a.Elevation;
            area2 += cross; cx6 += (a.Radial + b.Radial) * cross; cy6 += (a.Elevation + b.Elevation) * cross;
        }
        double sectionCx = cx6 / (3 * area2), sectionCz = cy6 / (3 * area2), area = Math.Abs(area2) / 2;
        double length = line.Zip(line.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b.Radial - a.Radial, 2) + Math.Pow(b.Elevation - a.Elevation, 2))).Sum();
        int levels = (int)Math.Floor((p.LastLevelLimitFromBaseMm!.Value - p.FirstLevelFromBaseMm!.Value) / p.AxialSpacingMm + 1e-9) + 1;
        double mainSurface = CagePlanner.Radii(cage).Main + cage.MainDiameter / 2;
        var pieces = new List<PositionPlatePiece>();
        for (int level = 0; level < levels; level++) for (int index = 0; index < p.PerLevelCount; index++)
        {
            int mainIndex = index * (cage.MainCount / p.PerLevelCount); double angle = 2 * Math.PI * mainIndex / cage.MainCount;
            double centerZ = cage.Z + p.FirstLevelFromBaseMm.Value + level * p.AxialSpacingMm;
            Point3 World(double radial, double tangential, double z) => new(cage.X + radial * Math.Cos(angle) - tangential * Math.Sin(angle), cage.Y + radial * Math.Sin(angle) + tangential * Math.Cos(angle), z);
            var vertices = new[] { -p.WidthMm / 2, p.WidthMm / 2 }.SelectMany(y => profile.Select(q => World(mainSurface + q.Radial, y, centerZ - height / 2 + q.Elevation))).ToArray();
            pieces.Add(new() { Id = $"P{level + 1:D3}-{index + 1:D3}", LevelIndex = level, MainBarIndex = mainIndex, AngleRadians = angle, CenterElevationMm = centerZ, Vertices = vertices,
                Centroid = World(mainSurface + sectionCx, 0, centerZ - height / 2 + sectionCz) });
        }
        double gap = cage.Diameter / 2 - Math.Sqrt(Math.Pow(mainSurface + p.OutermostProjectionMm, 2) + Math.Pow(p.WidthMm / 2, 2));
        return new() { Parameters = p, LevelCount = levels, TotalCount = pieces.Count, TotalHeightMm = height, IdealCenterlineLengthMm = length,
            CrossSectionAreaMm2 = area, VolumePerPlateMm3 = area * p.WidthMm, MinimumWallClearanceMm = gap, WallGapDifferenceMm = gap - p.ReferenceWallGapMm,
            MainSurfaceRadiusMm = mainSurface, Centerline = line, Profile = profile, Plates = pieces };
    }
    private static PositionPlateSectionPoint[] Centerline(PositionPlateInput p)
    {
        double half = p.ThicknessMm / 2, outer = p.OutermostProjectionMm - half, a = p.BottomLegMm, b = a + p.LowerRiseMm, c = b + p.MiddleLegMm;
        return [new(half, 0), new(half, a), new(outer, b), new(outer, c), new(half, c), new(half, c + p.TopLegMm)];
    }
    private static bool SelfIntersects(PositionPlateSectionPoint[] polygon)
    {
        double Cross(PositionPlateSectionPoint a, PositionPlateSectionPoint b, PositionPlateSectionPoint c) => (b.Radial - a.Radial) * (c.Elevation - a.Elevation) - (b.Elevation - a.Elevation) * (c.Radial - a.Radial);
        bool On(PositionPlateSectionPoint a, PositionPlateSectionPoint b, PositionPlateSectionPoint p) => Math.Abs(Cross(a, b, p)) < 1e-8 && p.Radial >= Math.Min(a.Radial, b.Radial) - 1e-8 && p.Radial <= Math.Max(a.Radial, b.Radial) + 1e-8 && p.Elevation >= Math.Min(a.Elevation, b.Elevation) - 1e-8 && p.Elevation <= Math.Max(a.Elevation, b.Elevation) + 1e-8;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            if (Math.Abs(a.Radial - b.Radial) + Math.Abs(a.Elevation - b.Elevation) < 1e-8) return true;
            for (int j = i + 1; j < polygon.Length; j++)
            {
                if (j == i + 1 || i == 0 && j == polygon.Length - 1) continue;
                var c = polygon[j]; var d = polygon[(j + 1) % polygon.Length];
                if (Cross(a, b, c) * Cross(a, b, d) < 0 && Cross(c, d, a) * Cross(c, d, b) < 0 || On(a, b, c) || On(a, b, d) || On(c, d, a) || On(c, d, b)) return true;
            }
        }
        return false;
    }
    private static PositionPlateSectionPoint[] Offset(PositionPlateSectionPoint[] line, double distance)
    {
        var result = new PositionPlateSectionPoint[line.Length];
        var directions = line.Zip(line.Skip(1), (a, b) => { double x = b.Radial - a.Radial, y = b.Elevation - a.Elevation, length = Math.Sqrt(x * x + y * y); return (X: x / length, Y: y / length); }).ToArray();
        for (int i = 0; i < line.Length; i++)
        {
            var point = line[i]; var a = directions[Math.Max(0, i - 1)]; var b = directions[Math.Min(i, directions.Length - 1)];
            double x1 = point.Radial - a.Y * distance, y1 = point.Elevation + a.X * distance;
            double x2 = point.Radial - b.Y * distance, y2 = point.Elevation + b.X * distance;
            double cross = a.X * b.Y - a.Y * b.X;
            double along = Math.Abs(cross) < 1e-12 ? 0 : ((x2 - x1) * b.Y - (y2 - y1) * b.X) / cross;
            result[i] = new(x1 + along * a.X, y1 + along * a.Y);
        }
        return result;
    }
    public static JsonObject Preview(CageInput cage, PositionPlateInput input)
    {
        try { var plan = Build(cage, input); return new() { ["status"] = "PASS", ["message"] = "定位钢片几何及布置检查通过。", ["plan"] = JsonSerializer.SerializeToNode(plan, Files.Json) }; }
        catch (Exception ex) { return new() { ["status"] = "FAIL", ["message"] = ex.Message }; }
    }
}
