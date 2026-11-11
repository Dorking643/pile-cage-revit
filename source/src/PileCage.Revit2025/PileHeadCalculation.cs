using System.Text.Json;
using System.Text.Json.Nodes;

namespace PileCage;

public sealed class PileHeadCalculationResult
{
    public required AnchorRuleResult Anchorage { get; init; }
    public required PileHeadParameters Parameters { get; init; }
    public required PileHeadPlan Plan { get; init; }
    public required JsonObject Preview { get; init; }
}

/// <summary>Shared pure calculation used by the UI preview and the native generation workflow.</summary>
public static class PileHeadCalculation
{
    public static PileHeadCalculationResult Calculate(PileHeadUiSnapshot snapshot, PileHeadUiInput input)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(input);
        if (snapshot.HostId <= 0 || snapshot.HostId != input.HostId || snapshot.HostDocumentUniqueId != input.HostDocumentUniqueId)
            throw new ArgumentException("请选择与当前参数一致的圆形柱。");
        if (snapshot.Bars == null || snapshot.Bars.Count == 0)
            throw new ArgumentException("所选柱中没有可用的原主筋。");
        if (snapshot.Bars.Any(bar => bar == null || !double.IsFinite(bar.Diameter) || bar.Diameter <= 0 || !double.IsFinite(bar.StandardBendDiameter) || bar.StandardBendDiameter <= 0))
            throw new ArgumentException("原主筋直径及实际类型弯曲直径必须为有限正数。");
        double diameter = snapshot.Bars[0].Diameter;
        if (snapshot.Bars.Any(bar => Math.Abs(bar.Diameter - diameter) > 1e-6))
            throw new ArgumentException("本次桩顶构造仅支持同一直径的原主筋，请分别处理不同直径组。");
        if (string.IsNullOrWhiteSpace(input.ConcreteGrade) || string.IsNullOrWhiteSpace(input.SteelGrade) || string.IsNullOrWhiteSpace(input.SeismicGrade))
            throw new ArgumentException("请选择承台混凝土、主筋牌号和抗震条件，随后自动计算锚固长度。");
        if (input.CapWidth < snapshot.Diameter || input.CapDepth < snapshot.Diameter)
            throw new ArgumentException("承台宽度和长度不能小于所选桩径。");
        if (input.Form == PileHeadForm.Waterproof && new[] { input.WaterproofThickness, input.RigidWaterproofThickness, input.WaterproofExtension }.Any(value => !double.IsFinite(value) || value <= 0))
            throw new ArgumentException("防水示意层厚度及径向延伸应为有限正数。");
        if (input.Form == PileHeadForm.Waterproof && (input.RigidWaterproofThickness > input.CapHeight - input.PileEmbedment || snapshot.Diameter + 2 * input.RigidWaterproofThickness > Math.Min(input.CapWidth, input.CapDepth)))
            throw new ArgumentException("刚性防水示意层超出承台顶部或侧面，请核对厚度与承台尺寸。");

        var anchorage = AnchorRules.Calculate(new AnchorRuleInput
        {
            DiameterMm = diameter, ConcreteGrade = input.ConcreteGrade, SteelGrade = input.SteelGrade,
            SeismicGrade = input.SeismicGrade, EpoxyCoated = input.EpoxyCoated, EasilyDisturbed = input.EasilyDisturbed
        });
        bool needsBend = input.Form == PileHeadForm.Bent90 || input.Form == PileHeadForm.Splayed && input.SplayAngleDegrees < 90;
        // Increase above the exact native boundary without relaxing the minimum.
        double radius = Math.Ceiling(Math.Max(anchorage.MinimumCenterlineBendRadiusMm, snapshot.Bars.Max(bar => (bar.StandardBendDiameter + bar.Diameter) / 2))) + 1;
        var parameters = new PileHeadParameters
        {
            Form = input.Form, AnchorageLength = anchorage.RequiredStraightLengthMm,
            VerticalProjection = anchorage.RequiredBendVerticalProjectionMm,
            HorizontalStraightLength = anchorage.RequiredBendHorizontalStraightMm,
            SplayAngleDegrees = input.SplayAngleDegrees, CenterlineBendRadius = needsBend ? radius : null,
            ReferenceLa = anchorage.SelectedAnchorageLengthMm, ReferenceLab = anchorage.SelectedBasicLengthMm,
            CapWidth = input.CapWidth, CapDepth = input.CapDepth, CapHeight = input.CapHeight,
            PileEmbedment = input.PileEmbedment, CapCover = input.Cover
        };
        var blocking = PileHeadPlanner.Validate(parameters, snapshot.Bars).Where(issue => issue.IsError).ToArray();
        if (blocking.Length > 0)
            throw new ArgumentException(blocking[0].Message + (blocking.Length > 1 ? $"（共{blocking.Length}项几何检查不通过）" : ""));
        var plan = PileHeadPlanner.Build(parameters, snapshot.Bars);
        string source = $"{anchorage.SteelGrade} / {anchorage.ConcreteGrade}；{(anchorage.UsesSeismicAnchorage ? "采用抗震锚固" : "采用非抗震锚固")}。\n"
            + "锚固计算：GB/T 50010-2010（2024年版）8.3.1～8.3.3、11.1.7；35d：JGJ 94-2008 4.2.4。\n"
            + "90°构造的20d、15d阈值采用所给连接图；弯曲内径按GB 50204-2015 5.3.1与实际钢筋类型取较大值。";
        if (input.Form == PileHeadForm.Splayed)
            source += "\n外斜按竖向投影满足计算锚长，实际沿筋长度更长；此为本次保守几何约定。";
        var preview = new JsonObject
        {
            ["status"] = "PASS", ["message"] = "自动锚固计算及承台几何净空检查通过。",
            ["anchor_length_mm"] = parameters.AnchorageLength,
            ["vertical_projection_mm"] = parameters.VerticalProjection,
            ["horizontal_length_mm"] = parameters.HorizontalStraightLength,
            ["centerline_bend_radius_mm"] = parameters.CenterlineBendRadius,
            ["bend_radius_geometry_margin_mm"] = needsBend ? 1 : 0,
            ["source_summary"] = source,
            ["main_count"] = snapshot.Bars.Count, ["main_diameter_mm"] = diameter,
            ["rules"] = JsonSerializer.SerializeToNode(anchorage, Files.Json),
            ["parameters"] = JsonSerializer.SerializeToNode(parameters, Files.Json),
            ["warnings"] = JsonSerializer.SerializeToNode(plan.Issues.Where(issue => !issue.IsError).Select(issue => issue.Message).ToArray(), Files.Json),
            ["head_path_lengths_mm"] = JsonSerializer.SerializeToNode(plan.Bars.Select(bar => bar.HeadLength).ToArray(), Files.Json)
        };
        return new PileHeadCalculationResult { Anchorage = anchorage, Parameters = parameters, Plan = plan, Preview = preview };
    }
}
