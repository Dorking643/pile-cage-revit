using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using System.IO;

namespace PileCage;

public sealed record PositionPlateFamilyParameter(string PropertyName, string Name, double DefaultMm, bool DrivesGeometry, bool IsInstance);
public sealed record PositionPlateFamilyFlexCheck(string Case, IReadOnlyDictionary<string, double> ParameterValuesMm,
    int PositiveSolidCount, int NativeVertexCount, double MaximumVertexErrorMm, double NativeVolumeMm3, double ExpectedVolumeMm3,
    double[] NativeMinimumMm, double[] NativeMaximumMm);
public sealed record PositionPlateFamilyBuildResult(string Path, string TemplatePath, string PlacementType, int HostingBehavior,
    IReadOnlyList<PositionPlateFamilyParameter> Parameters, IReadOnlyList<PositionPlateFamilyFlexCheck> FlexChecks,
    string LocalCoordinateConvention, string GeometryConvention);

/// <summary>
/// Creates one genuine face-based family with an instance-parametric, constant-
/// thickness mitered extrusion. It never creates DirectShape geometry or a set
/// of pre-baked shape types. Call from a Revit API context, with no transaction
/// open in a project; this method edits/closes only its own new family document.
/// </summary>
public static class PositionPlateFamilyBuilder
{
    public const string FamilyName = "PC_PositionPlate_FaceBased";
    public const string MaterialParameterName = "钢片材质";
    public const string HeightParameterName = "钢片总高";
    public const string CenterlineLengthParameterName = "理想中线长度";
    private const double AnchorMm = 1000;
    private const double GeometricToleranceMm = 0.01;
    private static readonly PositionPlateFamilyParameter[] Definitions =
    [
        new(nameof(PositionPlateInput.WidthMm), "板宽", 60, true, true),
        new(nameof(PositionPlateInput.ThicknessMm), "板厚", 4, true, true),
        new(nameof(PositionPlateInput.BottomLegMm), "下直段", 100, true, true),
        new(nameof(PositionPlateInput.LowerRiseMm), "下斜升高", 40, true, true),
        new(nameof(PositionPlateInput.MiddleLegMm), "中直段", 60, true, true),
        new(nameof(PositionPlateInput.TopLegMm), "上直段", 100, true, true),
        new(nameof(PositionPlateInput.OutermostProjectionMm), "外凸尺寸", 40, true, true),
        new(nameof(PositionPlateInput.NominalBlankLengthMm), "名义下料长度", 356, false, true)
    ];
    public static IReadOnlyDictionary<string, string> ParameterNames { get; } =
        Definitions.ToDictionary(x => x.PropertyName, x => x.Name);

    public static PositionPlateFamilyBuildResult Build(Application app, string destination)
    {
        ArgumentNullException.ThrowIfNull(app);
        string path = System.IO.Path.GetFullPath(destination);
        if (!string.Equals(System.IO.Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("定位钢片族目标必须为 .rfa 文件。", nameof(destination));
        if (System.IO.File.Exists(path)) throw new IOException("拒绝覆盖已有族文件：" + path);
        if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), FamilyName, StringComparison.Ordinal))
            throw new ArgumentException($"族文件名应为 {FamilyName}.rfa，以保持加载和验收的族名一致。", nameof(destination));
        string template = FindTemplate(app);
        Document doc = app.NewFamilyDocument(template);
        var checks = new List<PositionPlateFamilyFlexCheck>();
        try
        {
            int hosting = doc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsInteger() ?? -1;
            if (hosting != 5 || doc.OwnerFamily.FamilyPlacementType != FamilyPlacementType.WorkPlaneBased)
                throw new InvalidOperationException($"模板不是实际面基族：HostingBehavior={hosting}, PlacementType={doc.OwnerFamily.FamilyPlacementType}。");
            var failureLog = new FamilyFailureLog();
            using (var tx = new Transaction(doc, "建立面基定位钢片参数与实体"))
            {
                tx.Start();
                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failureLog).SetClearAfterRollback(true));
                FamilyManager fm = doc.FamilyManager;
                fm.NewType("标准定位钢片");
                var parameters = new Dictionary<string, FamilyParameter>();
                foreach (var definition in Definitions)
                {
                    var p = fm.AddParameter(definition.Name, GroupTypeId.Geometry, SpecTypeId.Length, definition.IsInstance);
                    fm.Set(p, Ft(definition.DefaultMm));
                    fm.SetDescription(p, definition.DrivesGeometry
                        ? "驱动实际等厚定位钢片实体，单位mm；须保持轮廓有效。"
                        : "图示名义采购/下料长度；独立记录，不强迫等于尖角理想几何展开长度。");
                    parameters.Add(definition.Name, p);
                }
                FamilyParameter Formula(string name, string expression)
                {
                    var p = fm.AddParameter(name, GroupTypeId.Constraints, SpecTypeId.Length, true);
                    fm.SetFormula(p, expression); return p;
                }
                Formula("PP_斜段中线长", "sqrt((外凸尺寸 - 板厚) ^ 2 + 下斜升高 ^ 2)");
                Formula("PP_斜接修正", "板厚 / 2 * (外凸尺寸 - 板厚) / (PP_斜段中线长 + 下斜升高)");
                Formula(HeightParameterName, "下直段 + 下斜升高 + 中直段 + 上直段");
                Formula(CenterlineLengthParameterName, "下直段 + PP_斜段中线长 + 中直段 + 外凸尺寸 - 板厚 + 上直段");
                var start = Formula("PP_宽度起面", "-板宽 / 2");
                var end = Formula("PP_宽度终面", "板宽 / 2");

                var matId = Material.Create(doc, "定位钢片_钢材");
                var mat = (Material)doc.GetElement(matId); mat.MaterialClass = "Steel"; mat.Color = new Color(110, 145, 165);
                var matParameter = fm.AddParameter(MaterialParameterName, GroupTypeId.Materials, SpecTypeId.Reference.Material, true);
                fm.Set(matParameter, matId);
                fm.SetDescription(matParameter, "钢材类别；原图未给牌号，不推定Q235或其他钢级。");

                // Local X is extrusion width, local Y is pile-axis height, and
                // local Z is outward from the face-based template's host face.
                var sketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero));
                XYZ[] initial = Profile(DefaultValues()).Select(p => new XYZ(0, Ft(p.Height), Ft(p.Radial))).ToArray();
                var loop = new CurveArray();
                for (int i = 0; i < initial.Length; ++i) loop.Append(Line.CreateBound(initial[i], initial[(i + 1) % initial.Length]));
                var profiles = new CurveArrArray(); profiles.Append(loop);
                Extrusion body = doc.FamilyCreate.NewExtrusion(true, profiles, sketchPlane, Ft(60));
                body.StartOffset = Ft(-30); body.EndOffset = Ft(30);
                fm.AssociateElementParameterToFamilyParameter(body.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM), start);
                fm.AssociateElementParameterToFamilyParameter(body.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), end);
                fm.AssociateElementParameterToFamilyParameter(body.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), matParameter);
                doc.Regenerate();

                View view = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
                    .FirstOrDefault(v => !v.IsTemplate && Math.Abs(v.ViewDirection.DotProduct(XYZ.BasisX)) > .999999)
                    ?? throw new InvalidOperationException("面基模板缺少与YZ钢片截面平行的立面视图。");
                ReferencePlane heightDatum = doc.FamilyCreate.NewReferencePlane2(
                    new XYZ(Ft(-500), Ft(-AnchorMm), Ft(-500)), new XYZ(Ft(500), Ft(-AnchorMm), Ft(-500)),
                    new XYZ(0, Ft(-AnchorMm), Ft(500)), view);
                heightDatum.Name = "PP_轴向尺寸固定基准"; heightDatum.Pinned = true;
                ReferencePlane radialDatum = doc.FamilyCreate.NewReferencePlane2(
                    new XYZ(Ft(-500), Ft(-500), Ft(-AnchorMm)), new XYZ(Ft(500), Ft(-500), Ft(-AnchorMm)),
                    new XYZ(0, Ft(500), Ft(-AnchorMm)), view);
                radialDatum.Name = "PP_径向尺寸固定基准"; radialDatum.Pinned = true;
                doc.Regenerate();

                // Each shared profile vertex has exactly one axial and one
                // radial labelled ordinate. Endpoint references are taken from
                // actual sketch ModelCurves, never transient input lines.
                string[] radialFormulas = ["0 mm", "0 mm", "外凸尺寸 - 板厚", "外凸尺寸 - 板厚", "0 mm", "0 mm", "板厚", "板厚", "外凸尺寸", "外凸尺寸", "板厚", "板厚"];
                string[] heightFormulas = ["0 mm", "下直段 + PP_斜接修正", "下直段 + 下斜升高 + PP_斜接修正",
                    "下直段 + 下斜升高 + 中直段 - 板厚 / 2", "下直段 + 下斜升高 + 中直段 - 板厚 / 2", HeightParameterName,
                    HeightParameterName, "下直段 + 下斜升高 + 中直段 + 板厚 / 2", "下直段 + 下斜升高 + 中直段 + 板厚 / 2",
                    "下直段 + 下斜升高 - PP_斜接修正", "下直段 - PP_斜接修正", "0 mm"];
                var curves = body.Sketch.Profile.Cast<CurveArray>().SelectMany(x => x.Cast<Curve>())
                    .Select(c => doc.GetElement(c.Reference.ElementId) as ModelCurve
                        ?? throw new InvalidOperationException("拉伸草图曲线没有可约束的原生ModelCurve。"))
                    .ToArray();
                for (int i = 0; i < initial.Length; ++i)
                {
                    ModelCurve model = curves.FirstOrDefault(c => c.GeometryCurve.GetEndPoint(0).DistanceTo(initial[i]) < Ft(.001)
                        || c.GeometryCurve.GetEndPoint(1).DistanceTo(initial[i]) < Ft(.001))
                        ?? throw new InvalidOperationException($"找不到第{i + 1}个实际草图顶点。");
                    int endpoint = model.GeometryCurve.GetEndPoint(0).DistanceTo(initial[i]) < Ft(.001) ? 0 : 1;
                    Reference point = model.GeometryCurve.GetEndPointReference(endpoint)
                        ?? throw new InvalidOperationException($"第{i + 1}个草图顶点没有端点Reference。");
                    var heightParameter = Formula($"PP_顶点{i + 1:00}_轴向", $"{AnchorMm} mm + ({heightFormulas[i]})");
                    var radialParameter = Formula($"PP_顶点{i + 1:00}_径向", $"{AnchorMm} mm + ({radialFormulas[i]})");
                    // Revit infers horizontal/vertical edge constraints. Bind each
                    // connected equal-coordinate group once to avoid redundant dimensions.
                    if (new[] { 0, 1, 2, 3, 5, 7, 9, 10 }.Contains(i))
                        BindOrdinate(doc, view, heightDatum.GetReference(), point,
                            new XYZ(0, Ft(-AnchorMm), initial[i].Z + Ft(150 + i * 5)),
                            new XYZ(0, initial[i].Y, initial[i].Z + Ft(150 + i * 5)), heightParameter);
                    if (i % 2 == 0)
                        BindOrdinate(doc, view, radialDatum.GetReference(), point,
                            new XYZ(0, initial[i].Y + Ft(150 + i * 5), Ft(-AnchorMm)),
                            new XYZ(0, initial[i].Y + Ft(150 + i * 5), initial[i].Z), radialParameter);
                }
                doc.Regenerate();
                checks.Add(CheckNative(doc, body, DefaultValues(), "默认参数"));

                // All seven editable geometry parameters must independently
                // change the same native extrusion. Any ineffective parameter,
                // failed constraint or wrong profile prevents saving the RFA.
                double[] flex = [75, 6, 120, 50, 80, 130, 50];
                for (int i = 0; i < flex.Length; ++i)
                {
                    var values = DefaultValues(); values[Definitions[i].Name] = flex[i];
                    foreach (var definition in Definitions) fm.Set(parameters[definition.Name], Ft(values[definition.Name]));
                    doc.Regenerate();
                    checks.Add(CheckNative(doc, body, values, "单参数改参：" + Definitions[i].Name));
                }
                foreach (var definition in Definitions) fm.Set(parameters[definition.Name], Ft(definition.DefaultMm));
                doc.Regenerate(); checks.Add(CheckNative(doc, body, DefaultValues(), "恢复默认参数"));
                if (tx.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("面基定位钢片约束事务未提交：" + string.Join("；", failureLog.Errors.Distinct()));
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
            return new(path, template, doc.OwnerFamily.FamilyPlacementType.ToString(), hosting, Definitions, checks,
                "Local X = width [-W/2,+W/2]; local Y = axial height [0,H]; local Z = radial projection [0,P]. Host contact plane is Z=0. Insertion origin is the plate bottom; shift an axial center-placement point by -H/2 along the pile axis.",
                "One parameter-driven extrusion. Ideal sharp corners with constant normal thickness and mitered intersections, matching PositionPlatePlanner. No finite bend radius, weld shape, or grade is inferred. The 356 mm nominal blank length is independent of the computed centerline length.");
        }
        finally { if (doc.IsValidObject) doc.Close(false); }
    }

    private static void BindOrdinate(Document doc, View view, Reference datum, Reference vertex, XYZ a, XYZ b, FamilyParameter parameter)
    {
        var references = new ReferenceArray(); references.Append(datum); references.Append(vertex);
        Dimension dimension = doc.FamilyCreate.NewLinearDimension(view, Line.CreateBound(a, b), references);
        dimension.FamilyLabel = parameter;
    }

    private static Dictionary<string, double> DefaultValues() => Definitions.ToDictionary(x => x.Name, x => x.DefaultMm);
    private sealed record SectionPoint(double Radial, double Height);
    private static SectionPoint[] Profile(IReadOnlyDictionary<string, double> v)
    {
        double t = v["板厚"], b = v["下直段"], r = v["下斜升高"], m = v["中直段"], top = v["上直段"], p = v["外凸尺寸"];
        double h = b + r + m + top, k = t / 2 * (p - t) / (Math.Sqrt((p - t) * (p - t) + r * r) + r), upper = b + r + m;
        return [new(0, 0), new(0, b + k), new(p - t, b + r + k), new(p - t, upper - t / 2), new(0, upper - t / 2), new(0, h),
            new(t, h), new(t, upper + t / 2), new(p, upper + t / 2), new(p, b + r - k), new(t, b - k), new(t, 0)];
    }

    private static PositionPlateFamilyFlexCheck CheckNative(Document doc, Extrusion body, Dictionary<string, double> values, string caseName)
    {
        var solids = body.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = true })
            .OfType<Solid>().Where(s => s.Volume > 0).ToArray();
        if (solids.Length != 1) throw new InvalidOperationException($"{caseName}：实际钢片应为一个正体积实体，当前{solids.Length}个。");
        var unique = new List<XYZ>();
        foreach (var p in solids.SelectMany(s => s.Edges.Cast<Edge>()).SelectMany(e => new[] { e.AsCurve().GetEndPoint(0), e.AsCurve().GetEndPoint(1) }))
            if (!unique.Any(q => q.DistanceTo(p) < Ft(.00001))) unique.Add(p);
        var vertices = unique.ToArray();
        var profile = Profile(values); double halfWidth = values["板宽"] / 2;
        var expected = new[] { -halfWidth, halfWidth }.SelectMany(w => profile.Select(p => new XYZ(Ft(w), Ft(p.Height), Ft(p.Radial)))).ToArray();
        double error = Math.Max(expected.Max(p => vertices.Min(q => Mm(p.DistanceTo(q)))), vertices.Max(p => expected.Min(q => Mm(p.DistanceTo(q)))));
        double area2 = 0;
        for (int i = 0; i < profile.Length; ++i) { var a = profile[i]; var b = profile[(i + 1) % profile.Length]; area2 += a.Radial * b.Height - b.Radial * a.Height; }
        double expectedVolume = Math.Abs(area2) / 2 * values["板宽"], nativeVolume = solids.Sum(s => s.Volume) * 304.8 * 304.8 * 304.8;
        if (vertices.Length != expected.Length || error > GeometricToleranceMm || Math.Abs(nativeVolume / expectedVolume - 1) > 1e-7)
            throw new InvalidOperationException($"{caseName}：族参数没有正确驱动实际实体。顶点数{vertices.Length}/{expected.Length}，最大误差{error:G8}mm，实际/预期体积{nativeVolume:G12}/{expectedVolume:G12}mm³。");
        double[] lo = [vertices.Min(p => Mm(p.X)), vertices.Min(p => Mm(p.Y)), vertices.Min(p => Mm(p.Z))];
        double[] hi = [vertices.Max(p => Mm(p.X)), vertices.Max(p => Mm(p.Y)), vertices.Max(p => Mm(p.Z))];
        return new(caseName, new Dictionary<string, double>(values), solids.Length, vertices.Length, error, nativeVolume, expectedVolume, lo, hi);
    }

    private static string FindTemplate(Application app)
    {
        var roots = new[] { app.FamilyTemplatePath, @"__REVIT_METRIC_FAMILY_TEMPLATES__",
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Autodesk\RVT 2025\Family Templates") };
        foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!System.IO.Directory.Exists(root)) continue;
            var path = System.IO.Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).FirstOrDefault(p =>
                string.Equals(System.IO.Path.GetFileName(p), "基于面的公制常规模型.rft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(System.IO.Path.GetFileName(p), "Metric Generic Model face based.rft", StringComparison.OrdinalIgnoreCase));
            if (path != null) return path;
        }
        throw new FileNotFoundException("找不到Revit2025真正面基常规模型模板，不能用普通常规模型静默替代。");
    }

    private sealed class FamilyFailureLog : IFailuresPreprocessor
    {
        public List<string> Errors { get; } = [];
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
                if (failure.GetSeverity() == FailureSeverity.Error) Errors.Add(failure.GetDescriptionText());
            return Errors.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
    private static double Ft(double mm) => mm / 304.8;
    private static double Mm(double ft) => ft * 304.8;
}
