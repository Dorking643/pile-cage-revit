using System;
using System.Collections.Generic;
using System.Linq;

namespace PileCage;

/// <summary>All lengths are mm; grades must be explicitly selected, never inferred from the pile host.</summary>
public sealed class AnchorRuleInput
{
    public double DiameterMm { get; set; }
    public string ConcreteGrade { get; set; } = "";
    public string SteelGrade { get; set; } = "";
    public string SeismicGrade { get; set; } = "";
    public bool EpoxyCoated { get; set; }
    public bool EasilyDisturbed { get; set; }
}

public sealed class AnchorRuleResult
{
    public double DiameterMm { get; init; }
    public string ConcreteGrade { get; init; } = "";
    public string SteelGrade { get; init; } = "";
    public string SeismicGrade { get; init; } = "";
    public bool UsesSeismicAnchorage { get; init; }
    public double ConcreteTensileDesignStrengthMpa { get; init; }
    public double SteelTensileDesignStrengthMpa { get; init; }
    /// <summary>lab = alpha*fy/ft*d, before any rounding or correction.</summary>
    public double BasicLengthMm { get; init; }
    /// <summary>la = max(zetaA*lab, 200), before output rounding.</summary>
    public double ModifiedLengthMm { get; init; }
    /// <summary>labE = zetaAE*lab, retaining the user's detail-figure notation.</summary>
    public double BasicSeismicLengthMm { get; init; }
    /// <summary>laE = zetaAE*la, before output rounding.</summary>
    public double SeismicLengthMm { get; init; }
    /// <summary>Selected la or laE, before output rounding.</summary>
    public double SelectedAnchorageLengthMm { get; init; }
    /// <summary>Selected lab or labE, before output rounding.</summary>
    public double SelectedBasicLengthMm { get; init; }
    public double RequiredStraightLengthMm { get; init; }
    public double RequiredBendVerticalProjectionMm { get; init; }
    public double RequiredBendHorizontalStraightMm { get; init; }
    public double MinimumInnerBendDiameterMm { get; init; }
    public double MinimumCenterlineBendRadiusMm { get; init; }
    public Dictionary<string, double> Factors { get; init; } = new();
    public string[] Citations { get; init; } = Array.Empty<string>();
    public string[] Assumptions { get; init; } = Array.Empty<string>();
    public string Scope { get; init; } = "";
}

/// <summary>
/// Bounded ordinary ribbed, individually anchored tensile reinforcement calculation.
/// The normative lab/la chain is kept distinct from the user's pile-head detail
/// thresholds. This does not decide a foundation's seismic grade or structural adequacy.
/// </summary>
public static class AnchorRules
{
    public static readonly string[] ConcreteGrades = { "C25", "C30", "C35", "C40", "C45", "C50", "C55", "C60" };
    public static readonly string[] SteelGrades = { "HRB400", "HRB500" };
    public static readonly string[] SeismicGrades = { "NonSeismic", "I", "II", "III", "IV" };
    private static readonly IReadOnlyDictionary<string, double> Ft = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["C25"] = 1.27, ["C30"] = 1.43, ["C35"] = 1.57, ["C40"] = 1.71,
        ["C45"] = 1.80, ["C50"] = 1.89, ["C55"] = 1.96, ["C60"] = 2.04
    };
    private static readonly IReadOnlyDictionary<string, double> Fy = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["HRB400"] = 360, ["HRB500"] = 435
    };
    private static readonly IReadOnlyDictionary<string, double> ZetaAe = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["NonSeismic"] = 1, ["I"] = 1.15, ["II"] = 1.15, ["III"] = 1.05, ["IV"] = 1
    };

    public static AnchorRuleResult Calculate(AnchorRuleInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var d = input.DiameterMm;
        if (!double.IsFinite(d) || d <= 0)
            throw new ArgumentException("主筋公称直径必须为有限正数。");
        var concrete = (input.ConcreteGrade ?? "").Trim().ToUpperInvariant();
        var steel = (input.SteelGrade ?? "").Trim().ToUpperInvariant();
        var seismic = (input.SeismicGrade ?? "").Trim();
        if (!Ft.TryGetValue(concrete, out var ft))
            throw new ArgumentException("请选择承台混凝土强度C25～C60（间隔5）；不从桩身材料推断。");
        if (!Fy.TryGetValue(steel, out var fy))
            throw new ArgumentException("当前锚固计算仅支持HRB400、HRB500普通带肋主筋。");
        if (!ZetaAe.TryGetValue(seismic, out var zetaAe))
            throw new ArgumentException("请选择NonSeismic或I、II、III、IV抗震条件；不能从设防烈度或宿主类别猜测。");
        if (steel == "HRB500" && concrete == "C25")
            throw new ArgumentException("GB/T 50010-2010（2024年版）4.1.2：采用500MPa及以上钢筋时，混凝土不应低于C30。");

        const double alpha = 0.14;
        double diameterFactor = d > 25 ? 1.10 : 1.0;
        double epoxyFactor = input.EpoxyCoated ? 1.25 : 1.0;
        double disturbanceFactor = input.EasilyDisturbed ? 1.10 : 1.0;
        // The optional beneficial cover and excess-area reductions are deliberately
        // not applied; there is no verified anchorage-zone cover/area input.
        double zetaA = Math.Max(.6, diameterFactor * epoxyFactor * disturbanceFactor);
        double lab = alpha * fy / ft * d;
        double la = Math.Max(200, zetaA * lab);
        double labE = zetaAe * lab;
        double laE = zetaAe * la;
        bool seismicUse = seismic != "NonSeismic";
        double selectedLa = seismicUse ? laE : la;
        double selectedLab = seismicUse ? labE : lab;
        // Figure constraints are independent from general code formulas:
        // straight/splayed >= la(laE) and >=35d;
        // 90-degree projection >=0.6lab(labE), >=20d; tail >=15d.
        // Retain the 200 mm general tensile anchorage floor as well.
        double straight = Math.Ceiling(Math.Max(selectedLa, 35 * d));
        double bendProjection = Math.Ceiling(Math.Max(200, Math.Max(.6 * selectedLab, 20 * d)));
        double bendTail = Math.Ceiling(15 * d);
        // GB 50204-2015 5.3.1, ordinary 400/500 MPa reinforcement.
        // A larger minimum stored on the actual native type must also be respected.
        double innerBendFactor = steel == "HRB500" ? (d < 28 ? 6 : 7) : 4;
        double innerBendDiameter = innerBendFactor * d;
        if (new[] { lab, la, labE, laE, straight, bendProjection, bendTail }.Any(x => !double.IsFinite(x)))
            throw new ArgumentException("输入导致锚固长度溢出，无法生成。");

        return new AnchorRuleResult
        {
            DiameterMm = d, ConcreteGrade = concrete, SteelGrade = steel, SeismicGrade = seismic,
            UsesSeismicAnchorage = seismicUse, ConcreteTensileDesignStrengthMpa = ft,
            SteelTensileDesignStrengthMpa = fy, BasicLengthMm = lab, ModifiedLengthMm = la,
            BasicSeismicLengthMm = labE, SeismicLengthMm = laE,
            SelectedAnchorageLengthMm = selectedLa, SelectedBasicLengthMm = selectedLab,
            RequiredStraightLengthMm = straight, RequiredBendVerticalProjectionMm = bendProjection,
            RequiredBendHorizontalStraightMm = bendTail,
            MinimumInnerBendDiameterMm = innerBendDiameter,
            MinimumCenterlineBendRadiusMm = (innerBendDiameter + d) / 2,
            Factors = new Dictionary<string, double>
            {
                ["alpha"] = alpha, ["diameter_gt_25"] = diameterFactor,
                ["epoxy"] = epoxyFactor, ["construction_disturbance"] = disturbanceFactor,
                ["cover_reduction"] = 1, ["excess_area_reduction"] = 1,
                ["zeta_a"] = zetaA, ["zeta_aE"] = zetaAe,
                ["minimum_inner_bend_diameter_factor"] = innerBendFactor,
                ["minimum_tension_anchorage_mm"] = 200, ["required_output_rounding_mm"] = 1
            },
            Citations = new[]
            {
                "GB/T 50010-2010（2024年版）名称/实施日期：住建部2024年第62号，2024-08-01实施；https://www.mohurd.gov.cn/file/2024/20240822/a015dc08-eaf2-474a-81c1-c4454e3b220c.pdf",
                "GB/T 50010-2010（2024年版）4.1.2、表4.1.4-2：承台混凝土适用强度及ft；https://gf.cabr-fire.com/m/article-12593.htm",
                "GB/T 50010-2010（2024年版）表4.2.3-1：HRB400/HRB500的fy=360/435MPa；https://gf.cabr-fire.com/m/article-12594.htm",
                "GB/T 50010-2010（2024年版）8.3.1～8.3.3：lab、la、alpha、修正系数及弯锚投影0.6lab；https://gf.cabr-fire.com/m/article-12617.htm",
                "GB/T 50010-2010（2024年版）11.1.7：laE=zeta_aE*la；https://gf.cabr-fire.com/m/article-12633.htm",
                "GB 55008-2021 4.4.5：受拉钢筋锚固的计算因素及200mm下限；https://gf.cabr-fire.com/article-39674.htm",
                "JGJ 94-2008 4.2.4：桩顶纵向主筋锚入承台的35d及抗拔桩转受拉锚固计算；https://gf.cabr-fire.com/article-16619.htm",
                "GB 50204-2015 5.3.1：HRB400弯弧内径不小于4d；HRB500在d<28时6d、d>=28时7d；https://gf.cabr-fire.com/m/article-12545.htm",
                "用户提供的桩顶连接图：直/斜锚la(laE)且35d；90度外弯竖投影0.6lab(labE)且20d、水平端部15d；图集版本及页码未据截图单独认定。"
            },
            Assumptions = new[]
            {
                "采用承台锚固区的混凝土强度；非桩身强度，抗震选项由设计条件显式给定。",
                "受拉、普通带肋、单根钢筋；不含预应力、并筋、疲劳、机械锚头或受压锚固设计。",
                "不采用保护层较厚或超配面积的有利缩短系数；两项系数均按1.0保留。",
                "labE按图示记号展开为zeta_aE*lab；不能把0.6lab替换成0.6la。",
                "Required数值仅在最终输出时向上取整至1mm；未预先取整lab/d。",
                "斜锚若用竖向投影满足直锚计算值，是额外保守的几何约定；图中的长度标注沿斜筋。",
                "90度外弯按用户图阈值；实际中心线弯曲半径还应不小于Revit钢筋类型的最小值，取两者较大值。",
                "本计算不完成承台承载力、抗拔受力、锚固区横向钢筋、保护层、耐久性及防水构造完整审查。"
            },
            Scope = "桩顶连接参数化建模的锚固长度计算；通用受拉公式与用户图示阈值分别标源，不等同于完整结构设计或规范符合性结论。"
        };
    }
}

