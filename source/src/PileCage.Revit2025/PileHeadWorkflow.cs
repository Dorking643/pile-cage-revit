using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PileCage;

/// <summary>Independent pile-head command. All document access runs on Revit's API thread.</summary>
public static class PileHeadWorkflow
{
    private static readonly Guid HeadSchemaId = new("CFB47EF1-179A-41CC-BF39-A972B8BCFD02");
    private static readonly Guid CageSchemaId = new("D71C2A12-2A7C-4DD3-9965-65B5025D7F31");
    private static double Ft(double mm) => mm / 304.8;
    private static double Mm(double ft) => ft * 304.8;
    private static XYZ X(PileHeadPoint p) => new(Ft(p.X), Ft(p.Y), Ft(p.Z));
    private static PileHeadPoint P(XYZ p) => new(Mm(p.X), Mm(p.Y), Mm(p.Z));
    private static JsonNode? J<T>(T value) => JsonSerializer.SerializeToNode(value, NativeEngine.JsonOptions);
    private static string S(JsonNode? o, string key, string fallback = "") => o?[key]?.GetValue<string>() ?? fallback;
    private static Document Doc(UIApplication ui) => ui.ActiveUIDocument?.Document is Document d && !d.IsFamilyDocument && !d.IsReadOnly ? d : throw new InvalidOperationException("请打开可编辑的 Revit 项目。");
    private static JsonObject Tag(Element e, Guid id)
    {
        var schema = Schema.Lookup(id);
        if (schema == null) return new();
        var entity = e.GetEntity(schema);
        return entity.IsValid() ? JsonNode.Parse(entity.Get<string>("record"))!.AsObject() : new();
    }
    private static void SetTag(Element e, JsonObject data)
    {
        var schema = Schema.Lookup(HeadSchemaId);
        if (schema == null)
        {
            var builder = new SchemaBuilder(HeadSchemaId); builder.SetSchemaName("PileHeadConstructionV1");
            builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("record", typeof(string)); schema = builder.Finish();
        }
        var entity = new Entity(schema); entity.Set("record", data.ToJsonString(NativeEngine.JsonOptions)); e.SetEntity(entity);
    }
    public static bool HasHeadOverrides(IEnumerable<Rebar> bars) => bars.Any(b => Tag(b, HeadSchemaId)["source"] != null);
    private static IList<Curve> Curves(Rebar b) => b.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
    private static Rebar[] AllBars(Document doc, long hostId) => new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(b => b.GetHostId().Value == hostId).OrderBy(b => b.Id.Value).ToArray();

    // The legacy column-selection command has no cage picker. It may reuse one existing
    // scoped cage, but must never silently combine scoped and unrelated main bars.
    private static string? StandaloneScope(Document doc, long hostId, IReadOnlyList<PileHeadBarSource> sources)
    {
        var bars = sources.Select(b => (Rebar)doc.GetElement(new ElementId(long.Parse(b.BarId)))).ToArray();
        var scopes = bars.Select(b => S(Tag(b, HeadSchemaId), "scope_id")).ToArray();
        var nonempty = scopes.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (nonempty.Length == 0) return null; // Preserve the unscoped legacy workflow.
        if (nonempty.Length != 1 || scopes.Any(s => string.IsNullOrWhiteSpace(s)))
            throw new InvalidOperationException("该柱含多组桩顶钢筋，或已设置锚固与未分组主筋混合。独立桩顶入口不能整柱合并处理，请明确指定钢筋笼分组。");
        string scope = nonempty[0];
        if (!Guid.TryParse(scope, out _) || bars.Any(b => S(Tag(b, CageSchemaId), "cage_guid") != scope))
            throw new InvalidOperationException("桩顶主筋的分组与钢筋笼标识不一致，请先核对原模型。");
        _ = Record(doc, hostId, scope); // Reuse only an existing, unambiguous persisted scope.
        return scope;
    }

    public static PileHeadUiSnapshot Inspect(UIApplication ui, ElementId id, IReadOnlyCollection<long>? selectedMainIds = null)
    {
        var doc = Doc(ui); var host = SimpleColumnWorkflow.ReadColumn(ui, id);
        var selected = selectedMainIds?.ToHashSet();
        if (selected != null && (selected.Count == 0 || selected.Count != selectedMainIds!.Count || selected.Any(value => value <= 0)))
            throw new ArgumentException("本次纵筋标识必须非空、为正且不得重复。");
        var candidates = AllBars(doc, id.Value);
        if (selected != null && selected.Any(value => !candidates.Any(b => b.Id.Value == value)))
            throw new InvalidOperationException("指定的本次纵筋已不存在或不属于当前圆柱。");
        var sources = new List<PileHeadBarSource>(); PileHeadForm? existing = null;
        foreach (var bar in candidates.Where(b => selected == null || selected.Contains(b.Id.Value)))
        {
            var tag = Tag(bar, HeadSchemaId); var cage = Tag(bar, CageSchemaId);
            var curves = Curves(bar);
            var oldSource = tag["source"]?.Deserialize<PileHeadBarSource>(NativeEngine.JsonOptions);
            bool straightVertical = curves.Count == 1 && curves[0] is Line && Math.Abs(curves[0].GetEndPoint(0).X - curves[0].GetEndPoint(1).X) < Ft(0.05) && Math.Abs(curves[0].GetEndPoint(0).Y - curves[0].GetEndPoint(1).Y) < Ft(0.05);
            if (oldSource == null && S(cage, "kind") != "longitudinal" && !straightVertical)
            {
                if (selected != null) throw new InvalidOperationException("指定的本次钢筋不属于可处理的纵向主筋。");
                continue;
            }
            if (!bar.IsRebarFreeForm() || bar.Quantity != 1) throw new InvalidOperationException("当前支持单根自由形状主筋；所选柱含受形状驱动或成组主筋，请先使用圆形柱配筋按钮生成。");
            if (oldSource == null && !straightVertical) throw new InvalidOperationException("发现未由本构造模块管理的非直主筋，不能判断其原有端部，请先核对模型。");
            var type = (RebarBarType)doc.GetElement(bar.GetTypeId());
            if (Math.Abs(Mm(type.BarModelDiameter - type.BarNominalDiameter)) > 0.01) throw new InvalidOperationException("当前桩顶模块要求主筋模型直径与公称直径一致，请核对钢筋类型。");
            var lower = oldSource?.LowerEnd ?? P(curves[0].GetEndPoint(0).Z <= curves[0].GetEndPoint(1).Z ? curves[0].GetEndPoint(0) : curves[0].GetEndPoint(1));
            var actualLower = curves.SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) }).OrderBy(p => p.Z).First();
            if (oldSource != null && Mm(actualLower.DistanceTo(X(lower))) > 0.05) throw new InvalidOperationException("主筋下端已被外部修改，与桩顶记录不符，请先核对模型。");
            if (lower.Z < host.Z - 0.05 || lower.Z >= host.Z + host.Height) throw new InvalidOperationException("主筋下端不在所选柱范围内。");
            sources.Add(new PileHeadBarSource(bar.Id.Value.ToString(), lower, new PileHeadPoint(host.X, host.Y, host.Z), host.Z + host.Height, Mm(type.BarNominalDiameter), Mm(type.StandardBendDiameter)));
            if (Enum.TryParse<PileHeadForm>(S(tag, "form"), out var f)) existing = f;
        }
        if (sources.Count == 0) throw new InvalidOperationException("所选圆柱没有可处理的纵向主筋，请先生成三类钢筋。");
        if (selectedMainIds == null) _ = StandaloneScope(doc, id.Value, sources);
        if (sources.Max(b => b.Diameter) - sources.Min(b => b.Diameter) > 0.01) throw new InvalidOperationException("当前桩顶模块要求主筋直径一致。");
        return new PileHeadUiSnapshot
        {
            HostId = host.ElementId, HostDocumentUniqueId = doc.ProjectInformation.UniqueId,
            Diameter = host.Diameter, Height = host.Height, MainCount = sources.Count, MainDiameter = sources[0].Diameter,
            SteelGrade = "", SourceSummary = "由所选圆柱和原生主筋读取；材料等级按设计选择。", Bars = sources, ExistingForm = existing
        };
    }

    public static JsonObject Preview(PileHeadUiSnapshot snapshot, PileHeadUiInput input)
    {
        try { return PileHeadCalculation.Calculate(snapshot, input).Preview; }
        catch (Exception ex) { return new JsonObject { ["status"] = "FAIL", ["message"] = ex.Message }; }
    }

    public static JsonObject Generate(UIApplication ui, PileHeadUiInput input, IReadOnlyCollection<long>? selectedMainIds = null, string? scopeId = null, bool deferDisplay = false)
    {
        var report = new JsonObject { ["status"] = "FAIL", ["transaction_committed"] = false, ["input"] = J(input), ["scope_id"] = scopeId };
        try
        {
            var doc = Doc(ui);
            if (input.HostDocumentUniqueId != doc.ProjectInformation.UniqueId) throw new InvalidOperationException("项目已切换，请重新选择圆形柱。");
            bool scoped = !string.IsNullOrWhiteSpace(scopeId);
            if (scoped != (selectedMainIds != null) || (scoped && !Guid.TryParse(scopeId, out _)))
                throw new ArgumentException("限定本次钢筋时必须同时提供有效钢筋笼标识与精确纵筋标识。");
            var snapshot = Inspect(ui, new ElementId(input.HostId), selectedMainIds);
            if (!scoped)
            {
                var existingScope = StandaloneScope(doc, input.HostId, snapshot.Bars);
                if (existingScope != null)
                {
                    scopeId = existingScope; selectedMainIds = snapshot.Bars.Select(b => long.Parse(b.BarId)).ToArray(); scoped = true;
                    report["scope_id"] = scopeId; report["existing_scope_reused"] = true;
                }
            }
            if (scoped && snapshot.Bars.Any(b => S(Tag(doc.GetElement(new ElementId(long.Parse(b.BarId))), CageSchemaId), "cage_guid") != scopeId))
                throw new InvalidOperationException("指定的纵筋不属于本次新建钢筋笼。");
            var calculation = PileHeadCalculation.Calculate(snapshot, input);
            var plan = PileHeadPlanner.Build(calculation.Parameters, snapshot.Bars);
            var errors = plan.Issues.Where(i => i.IsError).ToArray();
            if (errors.Length > 0) throw new ArgumentException(string.Join("\n", errors.Select(i => i.Message)));
            if (input.Form == PileHeadForm.Waterproof && (!double.IsFinite(input.WaterproofThickness) || !double.IsFinite(input.RigidWaterproofThickness) || !double.IsFinite(input.WaterproofExtension) || input.WaterproofThickness <= 0 || input.RigidWaterproofThickness <= 0 || input.WaterproofExtension <= 0))
                throw new ArgumentException("防水层厚度和延伸宽度必须是有限正数。");
            var mains = snapshot.Bars.Select(b => (Rebar)doc.GetElement(new ElementId(long.Parse(b.BarId)))).ToArray();
            var mainIds = mains.Select(b => b.Id.Value).ToHashSet();
            var others = AllBars(doc, input.HostId).Where(b => !mainIds.Contains(b.Id.Value)).ToArray();
            var otherBefore = others.ToDictionary(b => b.UniqueId, Fingerprint);
            // Persist this cage's transverse bars; foreign bars are protected for this operation.
            var persistentOthers = scoped ? others.Where(b => S(Tag(b, CageSchemaId), "cage_guid") == scopeId).ToArray() : others;
            var persistentBefore = persistentOthers.ToDictionary(b => b.UniqueId, Fingerprint);
            var beforeIds = mains.Select(b => b.UniqueId).ToArray();
            var host = SimpleColumnWorkflow.ReadColumn(ui, new ElementId(input.HostId));
            report["document_path"] = doc.PathName; report["host"] = J(host); report["calculation"] = calculation.Preview.DeepClone();
            report["plan"] = J(plan); report["main_unique_ids_before"] = J(beforeIds);
            report["unchanged_bars_before"] = J(otherBefore);
            using var group = new TransactionGroup(doc, "桩顶构造：" + input.Form); group.Start();
            try
            {
                View3D view;
                using (var transaction = new Transaction(doc, "应用桩顶锚固与承台构造"))
                {
                    transaction.Start();
                    var failures = new HeadFailures();
                    transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    foreach (var path in plan.Bars)
                    {
                        var bar = (Rebar)doc.GetElement(new ElementId(long.Parse(path.BarId)));
                        if (path.Segments.Any(s => Ft(s.Length) <= ui.Application.ShortCurveTolerance)) throw new InvalidOperationException("桩顶路径存在小于Revit最短曲线容差的线段/圆弧，请避免外斜角过度接近90°；90°可直接选择直锚。");
                        var native = path.Segments.Select(s => s.Kind.ToString() == "Line" ? (Curve)Line.CreateBound(X(s.Start), X(s.End)) : Arc.Create(X(s.Start), X(s.End), X(s.Mid))).ToList();
                        using var accessor = bar.GetFreeFormAccessor();
                        var validation = accessor.SetCurves(new List<IList<Curve>> { native });
                        if (validation != RebarFreeFormValidationResult.Success) throw new InvalidOperationException("主筋 " + path.BarId + " 的原生曲线设置失败：" + validation);
                        var source = snapshot.Bars.Single(b => b.BarId == path.BarId);
                        SetTag(bar, new JsonObject { ["role"] = "main", ["host_id"] = input.HostId, ["scope_id"] = scopeId ?? "", ["form"] = input.Form.ToString(), ["source"] = J(source) });
                    }
                    // Only replace explicit accessory solids owned by this module for this host.
                    var prior = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>().Where(e => InScope(Tag(e, HeadSchemaId), input.HostId, scopeId)).ToArray();
                    var allowed = prior.Select(e => e.Id.Value).ToHashSet();
                    foreach (var e in prior)
                        if (doc.Delete(e.Id).Any(id => !allowed.Contains(id.Value))) throw new InvalidOperationException("替换构造实体涉及其他依赖对象，已回滚。");
                    var solids = CreateAccessories(doc, host, input, snapshot.Bars, scopeId);
                    view = EnsureView(doc, host, input, scoped ? mains.Concat(persistentOthers) : AllBars(doc, input.HostId), solids, scopeId);
                    var records = new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>().Where(e => InScope(Tag(e, HeadSchemaId), input.HostId, scopeId)).ToArray();
                    if (records.Length > 1) throw new InvalidOperationException("存在重复桩顶构造记录。");
                    var storage = records.SingleOrDefault() ?? DataStorage.Create(doc);
                    SetTag(storage, new JsonObject { ["role"] = "record", ["host_id"] = input.HostId, ["scope_id"] = scopeId ?? "", ["main_element_ids"] = J(mains.Select(b => b.Id.Value).ToArray()), ["form"] = input.Form.ToString(), ["input"] = J(input), ["calculation"] = calculation.Preview.DeepClone(), ["plan"] = J(plan), ["view_id"] = view.Id.Value, ["other_fingerprints"] = J(persistentBefore), ["main_unique_ids"] = J(beforeIds) });
                    doc.Regenerate();
                    var commitStatus = transaction.Commit();
                    report["revit_failures"] = failures.Messages.DeepClone();
                    if (commitStatus != TransactionStatus.Committed) throw new InvalidOperationException("Revit 未提交桩顶构造事务。" + failures.Messages);
                }
                report["readback"] = Read(ui, input.HostId, scopeId);
                if (S(report["readback"], "status") != "PASS") throw new InvalidOperationException(S(report["readback"], "message", "桩顶构造回读失败。"));
                if (others.Any(b => !b.IsValidObject || Fingerprint(b) != otherBefore[b.UniqueId])) throw new InvalidOperationException("应用本次锚固时其他钢筋发生变化，已停止并回滚。");
                report["all_other_host_bars_unchanged_during_generation"] = true;
                if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Revit 未提交桩顶构造事务组。");
                report["transaction_committed"] = true; report["status"] = "PASS";
                report["message"] = $"已应用{snapshot.MainCount}根主筋的桩顶构造，保留{others.Length}个箍筋对象。";
                if (!deferDisplay)
                    try { Show(ui, input.HostId, scopeId); }
                    catch (Exception ex) { report["display_error"] = ex.Message; }
            }
            catch
            {
                if (group.GetStatus() == TransactionStatus.Started) report["rollback_status"] = group.RollBack().ToString();
                report["transaction_committed"] = group.GetStatus() == TransactionStatus.Committed;
                report["rollback_other_bars_unchanged"] = others.All(b => b.IsValidObject && otherBefore.TryGetValue(b.UniqueId, out var f) && Fingerprint(b) == f);
                throw;
            }
        }
        catch (Exception ex) { report["status"] = "FAIL"; report["message"] = ex.Message; report["error"] = ex.ToString(); }
        report["assembly_path"] = typeof(PileHeadWorkflow).Assembly.Location;
        report["assembly_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PileHeadWorkflow).Assembly.Location)));
        return report;
    }

    private static string Fingerprint(Rebar bar)
    {
        var data = new StringBuilder().Append(bar.UniqueId).Append('|').Append(bar.GetTypeId().Value).Append('|').Append(bar.GetHostId().Value).Append('|').Append(bar.TotalLength.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        foreach (var c in Curves(bar)) for (int i = 0; i <= 4; i++)
        { var p = c.Evaluate(i / 4.0, true); data.Append('|').Append(p.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(p.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(p.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToString())));
    }

    private static bool InScope(JsonObject tag, long hostId, string? scopeId) => tag["host_id"]?.GetValue<long>() == hostId && S(tag, "scope_id") == (scopeId ?? "");
    public static bool HasScope(UIApplication ui, long hostId, string scopeId) => new FilteredElementCollector(Doc(ui)).OfClass(typeof(DataStorage)).Cast<DataStorage>().Any(e => InScope(Tag(e, HeadSchemaId), hostId, scopeId));
    private static JsonObject Record(Document doc, long hostId, string? scopeId)
    {
        var records = new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>().Select(e => Tag(e, HeadSchemaId))
            .Where(t => t["host_id"]?.GetValue<long>() == hostId && S(t, "role") == "record" && (scopeId == null || InScope(t, hostId, scopeId))).ToArray();
        if (records.Length == 0) throw new InvalidOperationException("当前柱或指定钢筋笼没有桩顶构造记录。");
        if (records.Length != 1) throw new InvalidOperationException("当前柱含多份桩顶构造记录，请明确指定scope_id或cage_guid后回读或展示。");
        return records[0];
    }
    public static void Show(UIApplication ui, long hostId, string? scopeId = null)
    {
        var doc = Doc(ui); var record = Record(doc, hostId, scopeId);
        var view = (View3D)doc.GetElement(new ElementId(record["view_id"]!.GetValue<long>()));
        ui.ActiveUIDocument.ActiveView = view; Zoom(ui, view); ui.ActiveUIDocument.Selection.SetElementIds(new List<ElementId>()); ui.ActiveUIDocument.RefreshActiveView();
    }
    public static JsonObject Read(UIApplication ui, long hostId, string? scopeId = null)
    {
        try
        {
            var doc = Doc(ui);
            var record = Record(doc, hostId, scopeId);
            scopeId = S(record, "scope_id");
            var input = record["input"]!.Deserialize<PileHeadUiInput>(NativeEngine.JsonOptions)!;
            var recordedMainIds = record["main_element_ids"]?.Deserialize<long[]>(NativeEngine.JsonOptions)
                ?? record["main_unique_ids"]!.Deserialize<string[]>(NativeEngine.JsonOptions)!.Select(uid =>
                    (doc.GetElement(uid) as Rebar)?.Id.Value ?? throw new InvalidOperationException("记录中的原纵筋已不存在。")).ToArray();
            var snapshot = Inspect(ui, new ElementId(hostId), recordedMainIds);
            if (!string.IsNullOrWhiteSpace(scopeId) && snapshot.Bars.Any(b => S(Tag(doc.GetElement(new ElementId(long.Parse(b.BarId))), CageSchemaId), "cage_guid") != scopeId))
                throw new InvalidOperationException("记录中的纵筋已不属于指定钢筋笼。");
            var calculation = PileHeadCalculation.Calculate(snapshot, input);
            var plan = PileHeadPlanner.Build(calculation.Parameters, snapshot.Bars);
            var view = (View3D)doc.GetElement(new ElementId(record["view_id"]!.GetValue<long>()));
            var rows = new JsonArray();
            foreach (var path in plan.Bars)
            {
                var bar = (Rebar)doc.GetElement(new ElementId(long.Parse(path.BarId)));
                var actual = Curves(bar);
                if (actual.Count != path.Segments.Count) throw new InvalidOperationException("主筋曲线段数与生成方案不一致。");
                double maxError = 0; var curveRows = new JsonArray();
                for (int i = 0; i < actual.Count; i++)
                {
                    var s = path.Segments[i];
                    Curve expected = s.Kind.ToString() == "Line" ? Line.CreateBound(X(s.Start), X(s.End)) : Arc.Create(X(s.Start), X(s.End), X(s.Mid));
                    var points = new JsonArray();
                    for (int j = 0; j <= 16; j++)
                    {
                        var a = actual[i].Evaluate(j / 16.0, true); var e = expected.Evaluate(j / 16.0, true);
                        maxError = Math.Max(maxError, Mm(a.DistanceTo(e))); points.Add(J(P(a)));
                    }
                    curveRows.Add(new JsonObject { ["kind"] = actual[i].GetType().Name, ["length_mm"] = Mm(actual[i].Length), ["radius_mm"] = actual[i] is Arc arc ? Mm(arc.Radius) : null, ["points"] = points });
                }
                if (maxError > 0.05) throw new InvalidOperationException("主筋原生路径偏差超过0.05 mm。");
                var solidCheck = SolidCheck(bar, view);
                rows.Add(new JsonObject { ["element_id"] = bar.Id.Value, ["unique_id"] = bar.UniqueId, ["host_id"] = bar.GetHostId().Value, ["diameter_mm"] = snapshot.MainDiameter, ["length_mm"] = Mm(bar.TotalLength), ["max_path_error_mm"] = maxError, ["curves"] = curveRows, ["solid_check"] = solidCheck });
            }
            var expectedIds = record["main_unique_ids"]!.Deserialize<string[]>(NativeEngine.JsonOptions)!;
            if (!expectedIds.Order().SequenceEqual(rows.Select(r => S(r, "unique_id")).Order())) throw new InvalidOperationException("主筋对象标识发生变化。");
            var before = record["other_fingerprints"]!.Deserialize<Dictionary<string, string>>(NativeEngine.JsonOptions)!;
            var mainSet = snapshot.Bars.Select(b => long.Parse(b.BarId)).ToHashSet();
            var others = AllBars(doc, hostId).Where(b => !mainSet.Contains(b.Id.Value) && (string.IsNullOrWhiteSpace(scopeId) ? before.ContainsKey(b.UniqueId) : S(Tag(b, CageSchemaId), "cage_guid") == scopeId)).ToArray();
            if (others.Length != before.Count || others.Any(b => !before.TryGetValue(b.UniqueId, out var fingerprint) || fingerprint != Fingerprint(b))) throw new InvalidOperationException("外箍或内箍的标识/中心线发生变化。");
            var otherRows = new JsonArray();
            foreach (var b in others)
            {
                var nativePaths = new JsonArray();
                foreach (var c in Curves(b)) nativePaths.Add(new JsonObject { ["kind"] = c.GetType().Name, ["length_mm"] = Mm(c.Length), ["points"] = new JsonArray(J(P(c.GetEndPoint(0))), J(P(c.Evaluate(0.5, true))), J(P(c.GetEndPoint(1)))) });
                var bt = (RebarBarType)doc.GetElement(b.GetTypeId());
                otherRows.Add(new JsonObject { ["element_id"] = b.Id.Value, ["unique_id"] = b.UniqueId, ["class_name"] = b.GetType().FullName, ["quantity"] = b.Quantity, ["host_id"] = b.GetHostId().Value, ["diameter_mm"] = Mm(bt.BarNominalDiameter), ["model_diameter_mm"] = Mm(bt.BarModelDiameter), ["length_mm"] = Mm(b.TotalLength), ["curves"] = nativePaths, ["fingerprint"] = Fingerprint(b), ["solid_check"] = SolidCheck(b, view) });
            }
            var accessories = new JsonArray();
            foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>().Where(e => InScope(Tag(e, HeadSchemaId), hostId, scopeId)))
            {
                var body = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }).OfType<Solid>().ToArray();
                double v = body.Sum(s => s.Volume); if (v <= 0) throw new InvalidOperationException("构造体没有正体积。");
                var bounds = e.get_BoundingBox(null);
                var cylinders = new JsonArray(); var arcs = new JsonArray();
                foreach (var solid in body)
                {
                    foreach (var c in solid.Faces.Cast<Face>().OfType<CylindricalFace>()) cylinders.Add(new JsonObject { ["radius_mm"] = Mm(c.get_Radius(0).GetLength()), ["origin"] = J(P(c.Origin)), ["axis"] = J(new PileHeadPoint(c.Axis.X, c.Axis.Y, c.Axis.Z)) });
                    foreach (var edge in solid.Edges.Cast<Edge>()) if (edge.AsCurve() is Arc a) arcs.Add(new JsonObject { ["radius_mm"] = Mm(a.Radius), ["center"] = J(P(a.Center)), ["length_mm"] = Mm(a.Length) });
                }
                accessories.Add(new JsonObject { ["element_id"] = e.Id.Value, ["unique_id"] = e.UniqueId, ["name"] = e.Name, ["role"] = S(Tag(e, HeadSchemaId), "role"), ["volume_mm3"] = v * Math.Pow(304.8, 3), ["min"] = J(P(bounds.Min)), ["max"] = J(P(bounds.Max)), ["cylindrical_faces"] = cylinders, ["arc_edges"] = arcs, ["bounds_method"] = "Element bounding box is display data; cylindrical faces/arc edges carry native analytic circle geometry" });
            }
            if (accessories.Count != (input.Form == PileHeadForm.Waterproof ? 3 : 1)) throw new InvalidOperationException("构造实体数量不符。");
            return new JsonObject { ["status"] = "PASS", ["document_path"] = doc.PathName, ["scope_id"] = scopeId, ["form"] = input.Form.ToString(), ["host_id"] = hostId, ["main_count"] = rows.Count, ["main_ids_preserved"] = true, ["other_bars_unchanged"] = true, ["main_bars"] = rows, ["other_bars"] = otherRows, ["accessories"] = accessories, ["view_id"] = view.Id.Value, ["calculation"] = calculation.Preview.DeepClone(), ["input"] = J(input) };
        }
        catch (Exception ex) { return new JsonObject { ["status"] = "FAIL", ["message"] = ex.Message, ["error"] = ex.ToString() }; }
    }

    private static JsonObject SolidCheck(Rebar b, View3D view)
    {
        double volume = 0; int count = 0;
        void Collect(GeometryElement? geometry)
        {
            if (geometry == null) return;
            foreach (var o in geometry)
            {
                if (o is Solid solid && solid.Volume > 1e-12) { count++; volume += solid.Volume; }
                else if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry());
            }
        }
        Collect(b.GetFullGeometryForView(view));
        var type = (RebarBarType)b.Document.GetElement(b.GetTypeId());
        double expected = Math.PI * type.BarModelDiameter * type.BarModelDiameter / 4 * b.TotalLength;
        double error = Math.Abs(volume / expected - 1);
        if (count == 0 || !double.IsFinite(error) || error > 0.001) throw new InvalidOperationException($"钢筋{b.Id.Value}实体核查未通过（数量{count}、体积差{error:P4}）。");
        return new JsonObject { ["count"] = count, ["volume_mm3"] = volume * Math.Pow(304.8, 3), ["relative_volume_error"] = error };
    }

    private static Solid CircleSolid(double x, double y, double z, double radius, double height)
    {
        var center = new XYZ(Ft(x), Ft(y), Ft(z)); var loop = new CurveLoop();
        loop.Append(Arc.Create(center, Ft(radius), 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
        loop.Append(Arc.Create(center, Ft(radius), Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
        return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, Ft(height));
    }
    private static Solid Box(double x, double y, double z, double width, double depth, double height)
    {
        var pts = new[] { new XYZ(Ft(x - width / 2), Ft(y - depth / 2), Ft(z)), new XYZ(Ft(x + width / 2), Ft(y - depth / 2), Ft(z)), new XYZ(Ft(x + width / 2), Ft(y + depth / 2), Ft(z)), new XYZ(Ft(x - width / 2), Ft(y + depth / 2), Ft(z)) };
        var loop = new CurveLoop(); for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
        return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, Ft(height));
    }
    private static List<DirectShape> CreateAccessories(Document doc, NativeHostInfo host, PileHeadUiInput p, IReadOnlyList<PileHeadBarSource> bars, string? scopeId = null)
    {
        double top = host.Z + host.Height, bottom = top - p.PileEmbedment, radius = host.Diameter / 2;
        var result = new List<DirectShape>();
        void Add(string role, string name, Solid solid, BuiltInCategory category)
        {
            var shape = DirectShape.CreateElement(doc, new ElementId(category)); shape.Name = name;
            shape.ApplicationId = "PileCage.PileHead"; shape.ApplicationDataId = host.UniqueId + ":" + (scopeId ?? "") + ":" + role;
            shape.SetShape(new GeometryObject[] { solid }); SetTag(shape, new JsonObject { ["host_id"] = host.ElementId, ["scope_id"] = scopeId ?? "", ["role"] = role }); result.Add(shape);
        }
        var cap = Box(host.X, host.Y, bottom, p.CapWidth, p.CapDepth, p.CapHeight);
        if (p.PileEmbedment > 0) cap = BooleanOperationsUtils.ExecuteBooleanOperation(cap, CircleSolid(host.X, host.Y, bottom - 1, radius, p.PileEmbedment + 1), BooleanOperationsType.Difference);
        if (p.Form == PileHeadForm.Waterproof)
        {
            double t = p.WaterproofThickness, rigid = p.RigidWaterproofThickness;
            var membrane = Box(host.X, host.Y, bottom - t, p.CapWidth + 2 * p.WaterproofExtension, p.CapDepth + 2 * p.WaterproofExtension, t);
            membrane = BooleanOperationsUtils.ExecuteBooleanOperation(membrane, CircleSolid(host.X, host.Y, bottom - t - 1, radius + rigid, t + 2), BooleanOperationsType.Difference);
            Add("membrane", "承台底防水层 · 示意厚度", membrane, BuiltInCategory.OST_GenericModel);
            var rigidBody = CircleSolid(host.X, host.Y, bottom, radius + rigid, p.PileEmbedment + rigid);
            rigidBody = BooleanOperationsUtils.ExecuteBooleanOperation(rigidBody, CircleSolid(host.X, host.Y, bottom - 1, radius, p.PileEmbedment + 1), BooleanOperationsType.Difference);
            foreach (var bar in bars) rigidBody = BooleanOperationsUtils.ExecuteBooleanOperation(rigidBody, CircleSolid(bar.LowerEnd.X, bar.LowerEnd.Y, top - 1, bar.Diameter / 2, rigid + 2), BooleanOperationsType.Difference);
            cap = BooleanOperationsUtils.ExecuteBooleanOperation(cap, rigidBody, BooleanOperationsType.Difference);
            Add("rigid_waterproof", "桩顶刚性防水层 · 顶面及桩周衔接", rigidBody, BuiltInCategory.OST_GenericModel);
        }
        Add("cap", "承台 · 桩顶构造几何", cap, BuiltInCategory.OST_StructuralFoundation);
        return result;
    }

    private static View3D EnsureView(Document doc, NativeHostInfo host, PileHeadUiInput p, IEnumerable<Rebar> bars, IEnumerable<DirectShape> accessories, string? scopeId = null)
    {
        string suffix = " · " + host.ElementId + (string.IsNullOrWhiteSpace(scopeId) ? "" : " · " + scopeId);
        string label = p.Form switch { PileHeadForm.Straight => "1 直锚", PileHeadForm.Bent90 => "2 外弯锚", PileHeadForm.Splayed => "3 外斜锚", _ => "4 桩顶防水" };
        string name = "桩顶构造 · " + label + suffix;
        var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate && v.Name.StartsWith("桩顶构造 · ") && v.Name.EndsWith(suffix));
        if (view == null)
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            view = View3D.CreateIsometric(doc, type.Id); view.Name = name;
        }
        view.Name = name;
        view.DetailLevel = ViewDetailLevel.Fine; view.DisplayStyle = DisplayStyle.Shading;
        view.SetOrientation(new ViewOrientation3D(new XYZ(15, -20, 12), new XYZ(-1, 1.5, 3).Normalize(), new XYZ(-1, 1.5, -13.0 / 12).Normalize()));
        var margin = Math.Max(p.CapWidth, p.CapDepth) * 0.10 + p.WaterproofExtension;
        var box = new BoundingBoxXYZ { Min = new XYZ(Ft(host.X - p.CapWidth / 2 - margin), Ft(host.Y - p.CapDepth / 2 - margin), Ft(host.Z + host.Height - Math.Min(host.Height, host.Diameter * 2))), Max = new XYZ(Ft(host.X + p.CapWidth / 2 + margin), Ft(host.Y + p.CapDepth / 2 + margin), Ft(host.Z + host.Height - p.PileEmbedment + p.CapHeight + 150)) };
        view.SetSectionBox(box); view.IsSectionBoxActive = true;
        foreach (var c in new[] { BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids }) if (view.CanCategoryBeHidden(new ElementId(c))) view.SetCategoryHidden(new ElementId(c), true);
        var pattern = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(e => e.GetFillPattern().IsSolidFill);
        void Style(Element e, Color color, int transparency)
        {
            var ogs = new OverrideGraphicSettings().SetProjectionLineColor(color).SetSurfaceTransparency(transparency);
            if (pattern != null) ogs.SetSurfaceForegroundPatternId(pattern.Id).SetSurfaceForegroundPatternColor(color);
            view.SetElementOverrides(e.Id, ogs);
        }
        Style(doc.GetElement(new ElementId(host.ElementId)), new Color(165, 171, 175), 85);
        foreach (var b in bars) { string kind = S(Tag(b, CageSchemaId), "kind"); Style(b, kind == "helix" ? new Color(215, 113, 25) : kind == "ring" ? new Color(37, 143, 85) : new Color(43, 92, 164), 0); b.SetUnobscuredInView(view, true); }
        foreach (var e in accessories) { string role = S(Tag(e, HeadSchemaId), "role"); Style(e, role == "cap" ? new Color(170, 177, 186) : role == "membrane" ? new Color(45, 137, 175) : new Color(200, 158, 70), role == "cap" ? 82 : 20); }
        return view;
    }
    private static void Zoom(UIApplication ui, View3D view)
    {
        var box = view.GetSectionBox(); var center = (box.Min + box.Max) / 2; var right = view.RightDirection; var up = view.UpDirection;
        var points = new List<XYZ>(); foreach (var x in new[] { box.Min.X, box.Max.X }) foreach (var y in new[] { box.Min.Y, box.Max.Y }) foreach (var z in new[] { box.Min.Z, box.Max.Z }) points.Add(new XYZ(x, y, z));
        var h = points.Select(p => (p - center).DotProduct(right)).ToArray(); var v = points.Select(p => (p - center).DotProduct(up)).ToArray();
        ui.ActiveUIDocument.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id)?.ZoomAndCenterRectangle(center + right * h.Min() + up * v.Min(), center + right * h.Max() + up * v.Max());
    }
    public static JsonObject Run(UIApplication ui, JsonObject request)
    {
        string operation = S(request, "operation");
        try
        {
            long hostId = request["host_id"]?.GetValue<long>() ?? ui.ActiveUIDocument?.Selection.GetElementIds().FirstOrDefault()?.Value ?? 0;
            if (operation == "pilehead_prepare_copy")
            {
                var doc = Doc(ui); WorkspacePath(doc.PathName);
                if (!File.Exists(Path.Combine(App.Runtime, "enable-test-queue.flag"))) throw new InvalidOperationException("验证队列未启用。");
                var source = doc.GetElement(new ElementId(hostId)) as FamilyInstance ?? throw new ArgumentException("缺少源圆形柱。");
                var origin = SimpleColumnWorkflow.ReadColumn(ui, source.Id);
                double x = request["x"]!.GetValue<double>(), y = request["y"]!.GetValue<double>();
                using var group = new TransactionGroup(doc, "创建独立构造验证柱"); group.Start();
                try
                {
                    FamilyInstance created;
                    using (var t = new Transaction(doc, "新建构造验证柱"))
                    {
                        t.Start();
                        var level = (Level)doc.GetElement(source.LevelId);
                        created = doc.Create.NewFamilyInstance(new XYZ(Ft(x), Ft(y), Ft(origin.Z)), source.Symbol, level, StructuralType.Column);
                        created.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.Set(Ft(origin.Z) - level.Elevation);
                        created.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(level.Id);
                        created.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(Ft(origin.Z + origin.Height) - level.Elevation);
                        doc.Regenerate(); t.Commit();
                    }
                    var h = SimpleColumnWorkflow.ReadColumn(ui, created.Id);
                    var cage = request["cage_input"]!.Deserialize<CageInput>(NativeEngine.JsonOptions)!;
                    cage.HostId = h.ElementId; cage.HostDocumentUniqueId = doc.ProjectInformation.UniqueId; cage.X = h.X; cage.Y = h.Y; cage.Z = h.Z; cage.Diameter = h.Diameter; cage.Height = h.Height;
                    var result = SimpleColumnWorkflow.Generate(ui, cage);
                    if (!result.Success) throw new InvalidOperationException(result.Message);
                    if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("验证柱事务组未提交。");
                    return new JsonObject { ["status"] = "PASS", ["host"] = J(h), ["cage_guid"] = result.CageGuid, ["cage"] = result.Details.DeepClone() };
                }
                catch { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); throw; }
            }
            if (operation == "pilehead_open_ui") { PileHeadWindow.ShowFor(ui); return new JsonObject { ["status"] = "PASS" }; }
            if (operation == "pilehead_ui_state")
            {
                var window = PileHeadWindow.Instance ?? throw new InvalidOperationException("桩顶构造界面未打开。");
                if (request["capture_path"] != null) window.CaptureScreenshot(WorkspacePath(S(request, "capture_path")));
                return new JsonObject { ["status"] = "PASS", ["input"] = J(window.Input), ["last_result"] = window.LastResult?.DeepClone() };
            }
            if (operation == "pilehead_ui_load")
            {
                var window = PileHeadWindow.Instance ?? throw new InvalidOperationException("桩顶构造界面未打开。");
                window.LoadInput(request["input"]!.Deserialize<PileHeadUiInput>(NativeEngine.JsonOptions)!);
                return new JsonObject { ["status"] = "PASS", ["input"] = J(window.Input) };
            }
            if (operation == "pilehead_inspect") { var snapshot = Inspect(ui, new ElementId(hostId)); return new JsonObject { ["status"] = "PASS", ["snapshot"] = J(snapshot) }; }
            if (operation == "pilehead_read") return Read(ui, hostId, ScopeFromRequest(request));
            if (operation == "pilehead_generate")
            {
                WorkspacePath(Doc(ui).PathName);
                var input = request["input"]!.Deserialize<PileHeadUiInput>(NativeEngine.JsonOptions)!;
                return Generate(ui, input);
            }
            if (operation == "pilehead_export")
            {
                var read = Read(ui, hostId, ScopeFromRequest(request)); if (S(read, "status") != "PASS") return read;
                var view = (View3D)Doc(ui).GetElement(new ElementId(read["view_id"]!.GetValue<long>()));
                ui.ActiveUIDocument!.ActiveView = view; Zoom(ui, view); ui.ActiveUIDocument.RefreshActiveView();
                var path = WorkspacePath(S(request, "path")); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var options = new ImageExportOptions { FilePath = path, ExportRange = ExportRange.VisibleRegionOfCurrentView, ZoomType = ZoomFitType.FitToPage, PixelSize = 1600, FitDirection = FitDirectionType.Vertical, HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG, ImageResolution = ImageResolution.DPI_150 };
                Doc(ui).ExportImage(options);
                return new JsonObject { ["status"] = "PASS", ["view_id"] = view.Id.Value, ["path_prefix"] = path };
            }
            throw new ArgumentException("未知桩顶构造操作：" + operation);
        }
        catch (Exception ex) { return new JsonObject { ["status"] = "FAIL", ["operation"] = operation, ["message"] = ex.Message, ["error"] = ex.ToString() }; }
    }
    private static string? ScopeFromRequest(JsonObject request)
    {
        string value = S(request, "scope_id", S(request, "cage_guid"));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    private static string WorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("验证文件需保存在工作区。");
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(App.Root, path));
        if (!full.StartsWith(App.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("验证文件需保存在工作区。");
        return full;
    }
    private sealed class HeadFailures : IFailuresPreprocessor
    {
        public JsonArray Messages { get; } = new();
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            bool error = false;
            foreach (var m in a.GetFailureMessages())
            {
                Messages.Add(new JsonObject { ["severity"] = m.GetSeverity().ToString(), ["message"] = m.GetDescriptionText() });
                if (m.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(m); else error = true;
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
