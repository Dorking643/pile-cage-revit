using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PileCage;

public sealed record FamilyLibraryItem(string Id, string Name, string Description, string Kind,
    string Grade = "", double DiameterMm = 0);

/// <summary>Native RebarBarType library RVT and a single genuine face-hosted RFA, embedded in the DLL.</summary>
public static class FamilyLibraryService
{
    public const string RebarFile = "PC_Chinese_Rebar_Types.rvt";
    public const string PlateFile = "PC_PositionPlate_FaceBased.rfa";
    public const string PlateId = "position_plate_face_based";
    public const string LibraryVersion = "PC_CN_REBAR_2026_10_V2";
    public const string PlateFormulaVersion = "PC_POSITION_PLATE_MITER_12_V1";
    private static readonly IReadOnlyList<FamilyLibraryItem> Catalog = CreateCatalog();
    public static IReadOnlyList<FamilyLibraryItem> GetCatalog() => Catalog;
    private static IReadOnlyList<FamilyLibraryItem> CreateCatalog()
    {
        var items = new List<FamilyLibraryItem>();
        foreach (var grade in new[] { "HPB300", "HRB400", "HRB400E", "HRB500", "HRB500E" })
        foreach (int d in grade == "HPB300" ? new[] { 6, 8, 10, 12, 16, 20, 22 } : new[] { 8, 10, 12, 14, 16, 18, 20, 22, 25, 28, 32, 36, 40 })
            items.Add(new($"{grade}_{d}", $"{grade} Φ{d}", $"{(grade == "HPB300" ? "光圆" : "带肋")} · 公称/模型直径 {d} mm", "钢筋类型", grade, d));
        items.Add(new(PlateId, "定位钢片（面基参数族）", "一个族，可调板宽、板厚、折段长度和外凸尺寸", "面基定位钢片族"));
        return items.AsReadOnly();
    }
    public static string AssetPath(string file)
    {
        var assembly = typeof(FamilyLibraryService).Assembly;
        using var stream = assembly.GetManifestResourceStream("PileCage.Library." + file)
            ?? throw new InvalidOperationException("本插件未包含族库文件，请安装完整版本：" + file);
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PileCage", "FamilyLibrary", sha[..16]);
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, file);
        if (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != sha)
            File.WriteAllBytes(path, bytes);
        return path;
    }
    private static string Standard(string grade) => grade == "HPB300" ? "GB 1499.1-2024" : "GB 1499.2-2024";
    private static string MaterialName(string grade) => $"PC_钢筋_{grade}_{Standard(grade)}";
    private static double BendMm(FamilyLibraryItem item) => (item.Grade == "HPB300" ? 2.5 : 6) * item.DiameterMm;
    private static string Description(FamilyLibraryItem item) => $"{item.Grade}；{Standard(item.Grade)}；公称直径 {item.DiameterMm:0} mm；几何库预设";
    private static string? MaterialConflict(Material actual, string grade)
    {
        if (actual.Name != MaterialName(grade) || actual.MaterialClass != "Steel" || actual.MaterialCategory != grade)
            return "同名钢筋材质的牌号、分类或标准标识不一致：" + MaterialName(grade);
        // This library identifies grade; it intentionally does not invent an
        // analytical material or a measured/design strength for the project.
        if (actual.StructuralAssetId != ElementId.InvalidElementId)
            return "同名钢筋材质包含库未定义的分析物性，请重命名后加载：" + actual.Name;
        return null;
    }
    private static string? Conflict(RebarBarType actual, FamilyLibraryItem expected)
    {
        bool plain = expected.Grade == "HPB300";
        if (Math.Abs(actual.BarNominalDiameter * 304.8 - expected.DiameterMm) > .001 ||
            Math.Abs(actual.BarModelDiameter * 304.8 - expected.DiameterMm) > .001 ||
            actual.DeformationType != (plain ? RebarDeformationType.Plain : RebarDeformationType.Deformed))
            return "已有同名钢筋类型的直径或表面形式不同：" + expected.Name + "。请重命名现有类型后重试。";
        if (new[] { actual.StandardBendDiameter, actual.StandardHookBendDiameter, actual.StirrupTieBendDiameter }
            .Any(d => !double.IsFinite(d) || Math.Abs(d * 304.8 - BendMm(expected)) > .001))
            return "已有同名钢筋类型的弯曲直径不同：" + expected.Name + "。请重命名现有类型后重试。";
        var materialId = actual.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.AsElementId();
        if (materialId == null || actual.Document.GetElement(materialId) is not Material material)
            return "已有同名钢筋类型未明确指定库钢级材质：" + expected.Name;
        var materialError = MaterialConflict(material, expected.Grade);
        if (materialError != null) return materialError;
        if (actual.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() != Description(expected) ||
            !(actual.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString() ?? "").Contains(LibraryVersion, StringComparison.Ordinal))
            return "已有同名钢筋类型的牌号或库版本标识不同：" + expected.Name;
        return null;
    }
    public static JsonObject Load(UIApplication ui, IEnumerable<string> selectedIds)
    {
        Document? source = null;
        try
        {
            var doc = ui.ActiveUIDocument?.Document ?? throw new InvalidOperationException("请先打开 Revit 项目。");
            if (doc.IsFamilyDocument || doc.IsReadOnly) throw new InvalidOperationException("请在可编辑的项目中加载族库。");
            var ids = selectedIds.Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Any(id => Catalog.All(item => item.Id != id))) throw new ArgumentException("族库选择包含无效项目。");
            if (ids.Length == 0) return new() { ["status"] = "PASS", ["message"] = "未选择族，模型未修改。", ["loaded"] = 0, ["reused"] = 0 };
            var chosen = Catalog.Where(item => ids.Contains(item.Id)).ToList();
            var existing = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToDictionary(t => t.Name);
            var missing = new List<FamilyLibraryItem>(); int reused = 0, loaded = 0;
            foreach (var item in chosen.Where(i => i.Kind == "钢筋类型"))
            {
                if (existing.TryGetValue(item.Name, out var type))
                { var error = Conflict(type, item); if (error != null) throw new InvalidOperationException(error); reused++; }
                else missing.Add(item);
            }
            string? platePath = chosen.Any(i => i.Id == PlateId) ? AssetPath(PlateFile) : null;
            var family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == "PC_PositionPlate_FaceBased");
            JsonObject? plateDefinition = null;
            if (platePath != null)
            {
                using var assetDoc = ui.Application.OpenDocumentFile(platePath);
                try { plateDefinition = ValidatePlateDefinition(assetDoc); }
                finally { assetDoc.Close(false); }
                if (family != null) plateDefinition = ValidateProjectPlateFamily(family);
            }
            if (missing.Count > 0)
            {
                source = ui.Application.OpenDocumentFile(AssetPath(RebarFile));
                foreach (string grade in missing.Select(i => i.Grade).Distinct())
                {
                    var sourceMaterial = new FilteredElementCollector(source).OfClass(typeof(Material)).Cast<Material>()
                        .SingleOrDefault(m => m.Name == MaterialName(grade)) ?? throw new InvalidOperationException("内置库缺少钢级材质：" + grade);
                    var sourceError = MaterialConflict(sourceMaterial, grade);
                    if (sourceError != null) throw new InvalidOperationException(sourceError);
                    var destinationMaterial = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                        .SingleOrDefault(m => m.Name == sourceMaterial.Name);
                    if (destinationMaterial != null && MaterialConflict(destinationMaterial, grade) is string error)
                        throw new InvalidOperationException(error);
                }
            }
            using var group = new TransactionGroup(doc, "加载内置钢筋及定位钢片族"); group.Start();
            using (var tx = new Transaction(doc, "点选加载族库"))
            {
                tx.Start();
                if (source != null)
                {
                    var sourceTypes = new FilteredElementCollector(source).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToDictionary(t => t.Name);
                    foreach (var item in missing)
                    {
                        if (!sourceTypes.TryGetValue(item.Name, out var sourceType)) throw new InvalidOperationException("内置钢筋库缺少：" + item.Name);
                        var sourceError = Conflict(sourceType, item); if (sourceError != null) throw new InvalidOperationException("内置库校验失败：" + sourceError);
                        using var options = new CopyPasteOptions();
                        options.SetDuplicateTypeNamesHandler(new CheckedDuplicates(doc, chosen));
                        var copied = ElementTransformUtils.CopyElements(source, new[] { sourceType.Id }, doc, Transform.Identity, options);
                        var type = copied.Select(doc.GetElement).OfType<RebarBarType>().Single(t => t.Name == item.Name);
                        var error = Conflict(type, item); if (error != null) throw new InvalidOperationException(error);
                        loaded++;
                    }
                }
                if (platePath != null)
                {
                    if (family == null)
                    {
                        if (!doc.LoadFamily(platePath, new PreserveFamily(), out family) || family == null || !HasPlatePlacement(family))
                            throw new InvalidOperationException("面基定位钢片族加载或参数检查失败。");
                        loaded++;
                    }
                    else reused++;
                }
                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("加载族事务未提交。");
            }
            // EditFamily cannot run while a project transaction is modifiable.
            // The outer group remains available for rollback if this fails.
            if (platePath != null) plateDefinition = ValidateProjectPlateFamily(family!);
            if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("加载族事务组未提交。");
            return new() { ["status"] = "PASS", ["message"] = $"已加载 {loaded} 项，复用 {reused} 项。", ["loaded"] = loaded, ["reused"] = reused,
                ["selected_ids"] = JsonSerializer.SerializeToNode(ids), ["document_path"] = doc.PathName,
                ["library_version"] = LibraryVersion, ["plate_definition"] = plateDefinition };
        }
        catch (Exception ex) { return new() { ["status"] = "FAIL", ["message"] = ex.Message, ["error"] = ex.ToString() }; }
        finally { source?.Close(false); }
    }
    public static bool IsPlateFamily(Family family)
    {
        try { ValidateProjectPlateFamily(family); return true; }
        catch { return false; }
    }
    private static bool HasPlatePlacement(Family family) => family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased &&
        family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsInteger() == 5;
    private static JsonObject ValidateProjectPlateFamily(Family family)
    {
        if (!HasPlatePlacement(family) || family.Document.IsModifiable || !family.IsEditable)
            throw new InvalidOperationException("同名定位钢片必须是可核查的真正面基族，请重命名冲突族后加载。");
        var edited = family.Document.EditFamily(family);
        try { return ValidatePlateDefinition(edited); }
        finally { edited.Close(false); }
    }
    private sealed class CheckedDuplicates(Document destination, IReadOnlyList<FamilyLibraryItem> selected) : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            // Only known, preflighted dependencies may be reused. Unknown
            // duplicate types abort instead of silently binding foreign data.
            foreach (var id in args.GetTypeIds())
            {
                var element = args.Document.GetElement(id);
                if (element is Material material)
                {
                    string? grade = selected.Select(i => i.Grade).Distinct().FirstOrDefault(g => material.Name == MaterialName(g));
                    var target = new FilteredElementCollector(destination).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => m.Name == material.Name);
                    if (grade == null || target == null || MaterialConflict(material, grade) != null || MaterialConflict(target, grade) != null) return DuplicateTypeAction.Abort;
                }
                else if (element is RebarBarType type)
                {
                    var item = selected.FirstOrDefault(i => i.Name == type.Name && i.Kind == "钢筋类型");
                    var target = new FilteredElementCollector(destination).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().FirstOrDefault(t => t.Name == type.Name);
                    if (item == null || target == null || Conflict(type, item) != null || Conflict(target, item) != null) return DuplicateTypeAction.Abort;
                }
                else return DuplicateTypeAction.Abort;
            }
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }
    private sealed class PreserveFamily : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return false; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Project; overwriteParameterValues = false; return false; }
    }
    private static JsonObject ValidatePlateDefinition(Document doc)
    {
        if (!doc.IsFamilyDocument || !HasPlatePlacement(doc.OwnerFamily)) throw new InvalidOperationException("定位钢片不是面基族。");
        var fm = doc.FamilyManager;
        var parameters = fm.Parameters.Cast<FamilyParameter>().ToDictionary(p => p.Definition.Name);
        string[] inputs = ["板宽", "板厚", "下直段", "下斜升高", "中直段", "上直段", "外凸尺寸", "名义下料长度"];
        FamilyParameter Required(string name, ForgeTypeId dataType, bool formula)
        {
            if (!parameters.TryGetValue(name, out var p) || !p.IsInstance || p.Definition.GetDataType() != dataType ||
                (formula ? string.IsNullOrWhiteSpace(p.Formula) : !string.IsNullOrWhiteSpace(p.Formula)))
                throw new InvalidOperationException("定位钢片参数类型、实例属性或公式不符：" + name);
            return p;
        }
        foreach (string name in inputs) Required(name, SpecTypeId.Length, false);
        Required(PositionPlateFamilyBuilder.MaterialParameterName, SpecTypeId.Reference.Material, false);
        var formulas = new Dictionary<string, string>
        {
            ["PP_斜段中线长"] = "sqrt((外凸尺寸 - 板厚) ^ 2 + 下斜升高 ^ 2)",
            ["PP_斜接修正"] = "板厚 / 2 * (外凸尺寸 - 板厚) / (PP_斜段中线长 + 下斜升高)",
            ["钢片总高"] = "下直段 + 下斜升高 + 中直段 + 上直段",
            ["理想中线长度"] = "下直段 + PP_斜段中线长 + 中直段 + 外凸尺寸 - 板厚 + 上直段",
            ["PP_宽度起面"] = "-板宽 / 2", ["PP_宽度终面"] = "板宽 / 2"
        };
        string[] radial = ["0 mm", "0 mm", "外凸尺寸 - 板厚", "外凸尺寸 - 板厚", "0 mm", "0 mm", "板厚", "板厚", "外凸尺寸", "外凸尺寸", "板厚", "板厚"];
        string[] height = ["0 mm", "下直段 + PP_斜接修正", "下直段 + 下斜升高 + PP_斜接修正",
            "下直段 + 下斜升高 + 中直段 - 板厚 / 2", "下直段 + 下斜升高 + 中直段 - 板厚 / 2", "钢片总高", "钢片总高",
            "下直段 + 下斜升高 + 中直段 + 板厚 / 2", "下直段 + 下斜升高 + 中直段 + 板厚 / 2",
            "下直段 + 下斜升高 - PP_斜接修正", "下直段 - PP_斜接修正", "0 mm"];
        for (int i = 0; i < 12; i++)
        {
            formulas[$"PP_顶点{i + 1:00}_轴向"] = $"1000 mm + ({height[i]})";
            formulas[$"PP_顶点{i + 1:00}_径向"] = $"1000 mm + ({radial[i]})";
        }
        static string Normalize(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        foreach (var pair in formulas)
            if (Normalize(Required(pair.Key, SpecTypeId.Length, true).Formula) != Normalize(pair.Value))
                throw new InvalidOperationException("定位钢片公式版本不符：" + pair.Key);
        if (fm.CurrentType == null) throw new InvalidOperationException("定位钢片没有可读取的当前族类型。");
        double Value(string name) => (fm.CurrentType.AsDouble(parameters[name]) ?? throw new InvalidOperationException("缺少长度值：" + name)) * 304.8;
        var values = inputs.ToDictionary(n => n, Value);
        if (values.Values.Any(v => !double.IsFinite(v) || v <= 0) || values["外凸尺寸"] <= values["板厚"])
            throw new InvalidOperationException("定位钢片的默认参数不满足有效正尺寸。");
        double w = values["板宽"], t = values["板厚"], b = values["下直段"], r = values["下斜升高"], m = values["中直段"], a = values["上直段"], p = values["外凸尺寸"];
        double h = b + r + m + a, k = t / 2 * (p - t) / (Math.Sqrt((p - t) * (p - t) + r * r) + r), u = b + r + m;
        double[][] yz = [[0, 0], [b + k, 0], [b + r + k, p - t], [u - t / 2, p - t], [u - t / 2, 0], [h, 0],
            [h, t], [u + t / 2, t], [u + t / 2, p], [b + r - k, p], [b - k, t], [0, t]];
        var expected = new[] { -w / 2, w / 2 }.SelectMany(x => yz.Select(q => new XYZ(x, q[0], q[1]) / 304.8)).ToArray();
        var bodies = new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>()
            .Where(e => fm.GetAssociatedFamilyParameter(e.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM))?.Definition.Name == "PP_宽度起面" &&
                fm.GetAssociatedFamilyParameter(e.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM))?.Definition.Name == "PP_宽度终面" &&
                fm.GetAssociatedFamilyParameter(e.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM))?.Definition.Name == PositionPlateFamilyBuilder.MaterialParameterName).ToArray();
        if (bodies.Length != 1) throw new InvalidOperationException("定位钢片必须有一个与宽度和材质参数关联的实际拉伸。");
        var solids = bodies[0].get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = true })
            .OfType<Solid>().Where(s => s.Volume > 0).ToArray();
        if (solids.Length != 1) throw new InvalidOperationException("定位钢片实际拉伸实体数不符。");
        var vertices = new List<XYZ>();
        foreach (var q in solids[0].Edges.Cast<Edge>().SelectMany(e => new[] { e.AsCurve().GetEndPoint(0), e.AsCurve().GetEndPoint(1) }))
            if (!vertices.Any(v => v.DistanceTo(q) * 304.8 < .00001)) vertices.Add(q);
        if (vertices.Count != 24) throw new InvalidOperationException("定位钢片实际轮廓顶点数不符。");
        double error = Math.Max(expected.Max(q => vertices.Min(v => v.DistanceTo(q))), vertices.Max(v => expected.Min(q => v.DistanceTo(q)))) * 304.8;
        double expectedVolume = w * t * (b + m + a + p - t + Math.Sqrt((p - t) * (p - t) + r * r));
        double volume = solids[0].Volume * Math.Pow(304.8, 3);
        if (error > .01 || Math.Abs(volume / expectedVolume - 1) > 1e-7)
            throw new InvalidOperationException("定位钢片实际轮廓或体积与参数公式版本不一致。");
        string signature = string.Join("\n", formulas.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + Normalize(x.Value)));
        return new() { ["formula_version"] = PlateFormulaVersion, ["formula_sha256"] = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature))),
            ["instance_length_parameter_count"] = inputs.Length, ["formula_count"] = formulas.Count, ["native_vertex_count"] = vertices.Count,
            ["maximum_native_vertex_error_mm"] = error, ["native_volume_mm3"] = volume, ["independent_expected_volume_mm3"] = expectedVolume };
    }
    public static JsonObject BuildAssets(UIApplication ui)
    {
        string folder = Path.Combine(App.Root, "assets", "family-library"); Directory.CreateDirectory(folder);
        string barsPath = Path.Combine(folder, RebarFile);
        string platePath = Path.Combine(folder, PlateFile);
        JsonNode plate;
        if (File.Exists(platePath))
        {
            var familyDoc = ui.Application.OpenDocumentFile(platePath);
            try { plate = new JsonObject { ["Path"] = platePath, ["reused_existing_rfa"] = true, ["definition"] = ValidatePlateDefinition(familyDoc) }; }
            finally { familyDoc.Close(false); }
        }
        else plate = JsonSerializer.SerializeToNode(PositionPlateFamilyBuilder.Build(ui.Application, platePath), NativeEngine.JsonOptions)!;
        using (var doc = ui.Application.NewProjectDocument(UnitSystem.Metric))
        {
            using (var tx = new Transaction(doc, "制作国内钢筋类型库"))
            {
                tx.Start();
                var materials = new Dictionary<string, ElementId>();
                foreach (string grade in Catalog.Where(i => i.Kind == "钢筋类型").Select(i => i.Grade).Distinct())
                {
                    var id = Material.Create(doc, MaterialName(grade)); var material = (Material)doc.GetElement(id);
                    material.MaterialClass = "Steel"; material.MaterialCategory = grade; material.Color = new Color(115, 125, 135);
                    materials.Add(grade, id);
                }
                foreach (var item in Catalog.Where(i => i.Kind == "钢筋类型"))
                {
                    var type = RebarBarType.Create(doc); type.Name = item.Name;
                    using var diameters = new BarTypeDiameterOptions { BarNominalDiameter = item.DiameterMm / 304.8, BarModelDiameter = item.DiameterMm / 304.8,
                        StandardBendDiameter = BendMm(item) / 304.8, StandardHookBendDiameter = BendMm(item) / 304.8, StirrupTieBendDiameter = BendMm(item) / 304.8 };
                    type.SetBarTypeDiameters(diameters);
                    type.DeformationType = item.Grade == "HPB300" ? RebarDeformationType.Plain : RebarDeformationType.Deformed;
                    // Shape defaults are explicit modeling presets, not inferred design/anchorage requirements.
                    type.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)!.Set(materials[item.Grade]);
                    type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)!.Set($"{LibraryVersion}；国产钢筋几何预设；弯曲直径需按具体构造核定；材料仅钢级标识，未配置分析物性或进行设计验算。");
                    type.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)!.Set(Description(item));
                    type.get_Parameter(BuiltInParameter.ALL_MODEL_URL)?.Set(item.Grade == "HPB300"
                        ? "https://openstd.samr.gov.cn/bzgk/std/newGbInfo?hcno=47B1573A1FBA680A92EF49BC33BEA59F"
                        : "https://openstd.samr.gov.cn/bzgk/std/newGbInfo?hcno=B54C4769E4CA87CFF4F0E6D00C20AFF8");
                    var error = Conflict(type, item); if (error != null) throw new InvalidOperationException(error);
                }
                tx.Commit();
            }
            doc.SaveAs(barsPath, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 }); doc.Close(false);
        }
        return new() { ["status"] = "PASS", ["rebar_library"] = barsPath, ["rebar_type_count"] = Catalog.Count(i => i.Kind == "钢筋类型"),
            ["library_version"] = LibraryVersion, ["material_grade_count"] = 5, ["plate"] = plate };
    }
}
