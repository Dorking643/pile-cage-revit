using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PileCage;

public sealed class NativeResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string CageGuid { get; set; } = "";
    public long HostId { get; set; }
    public int MainCount { get; set; }
    public int SpiralCount { get; set; }
    public int RingCount { get; set; }
    public string ReportPath { get; set; } = "";
    public JsonObject Details { get; set; } = new();
}

public sealed record NativeHostInfo(long ElementId, string UniqueId, double Diameter, double Height, double X, double Y, double Z);
public sealed record NativeBarType(long Id, string Name, double Diameter);

/// <summary>All public methods must be called from a Revit API context (ExternalEvent or command).</summary>
public static class NativeEngine
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly Guid SchemaId = new("D71C2A12-2A7C-4DD3-9965-65B5025D7F31");
    private const string FamilyTemplate = @"__REVIT_METRIC_FAMILY_TEMPLATES__\公制结构基础.rft";
    private static double Ft(double mm) => mm / 304.8;
    private static double Mm(double ft) => ft * 304.8;
    private static XYZ XYZmm(Point3 p) => new(Ft(p.X), Ft(p.Y), Ft(p.Z));
    private static JsonArray Point(XYZ p) => new(Mm(p.X), Mm(p.Y), Mm(p.Z));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string S(JsonNode? n, string key, string fallback = "") => n?[key]?.GetValue<string>() ?? fallback;
    private static double N(JsonNode? n, string key, double fallback = 0)
    {
        var node = n?[key];
        if (node == null) return fallback;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<float>(out var f)) return f;
            if (value.TryGetValue<decimal>(out var m)) return (double)m;
        }
        throw new InvalidOperationException("回读字段不是数值：" + key);
    }

    public static IReadOnlyList<NativeBarType> GetBarTypes(UIApplication ui) =>
        new FilteredElementCollector(Current(ui)).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
            .Select(t => new NativeBarType(t.Id.Value, t.Name, Mm(t.BarNominalDiameter))).OrderBy(t => t.Diameter).ToList();

    private static Document Current(UIApplication ui)
    {
        var document = ui.ActiveUIDocument?.Document ?? throw new InvalidOperationException("请先打开 Revit 项目，再生成钢筋笼。");
        if (document.IsFamilyDocument || document.IsReadOnly) throw new InvalidOperationException("需要可编辑的 Revit 项目文档。");
        return document;
    }

    public static NativeResult Apply(UIApplication ui, CageInput input, bool fullRebuild = false, bool injectFailure = false, bool acceptWarnings = false, bool deferDisplay = false, bool deferReport = false)
    {
        try
        {
            var snapshot = input.Clone();
            var plan = CagePlanner.Build(snapshot);
            var findings = RuleChecker.Check(snapshot, RuleStore.Load());
            var blocks = findings.Where(f => f.Severity == "Block").ToList();
            if (blocks.Count > 0) throw new ArgumentException(string.Join("\n", blocks.Select(f => f.Message)));
            if (!acceptWarnings && findings.Any(f => f.Severity == "Warn")) throw new ArgumentException("推荐性规则出现提示，请确认后再生成：\n" + string.Join("\n", findings.Where(f => f.Severity == "Warn").Select(f => f.Message)));
            var result = ApplyInternal(ui, snapshot, plan, injectFailure, "apply", findings, deferDisplay, deferReport);
            if (result.Success) input.CageGuid = result.CageGuid;
            return result;
        }
        catch (Exception ex)
        {
            var result = new NativeResult { CageGuid = input.CageGuid, Message = ex.Message, Details = new JsonObject { ["status"] = "FAIL", ["transaction_committed"] = false, ["transaction_state"] = "not_started", ["error"] = ex.ToString() } };
            return deferReport ? result : Finish(result);
        }
    }

    public static NativeResult Probe(UIApplication ui)
    {
        var input = new CageInput
        {
            CageId = "NATIVE-PROBE-3", CageGuid = "4c870cc7-d5ad-4e06-9baa-d6851f698f6b",
            Diameter = 1200, Height = 3000, Cover = 60, MainCount = 1, MainDiameter = 20,
            MainStart = 100, MainEnd = 2900, SpiralDiameter = 8, SpiralStart = 100,
            Zones = new() { new ZoneInput { Id = "Z1", Length = 1200, Pitch = 150 }, new ZoneInput { Id = "Z2", Length = 1500, Pitch = 300 } },
            RingsEnabled = true, RingDiameter = 12, RingStart = 250, RingEnd = 250, RingSpacing = 1000
        };
        try
        {
            var plan = CagePlanner.Build(input);
            plan.Bars = plan.Bars.Where(b => b.Kind != "longitudinal").Concat(plan.Bars.Where(b => b.Kind == "longitudinal").Take(1)).ToList();
            return ApplyInternal(ui, input, plan, false, "probe_three_native_rebars");
        }
        catch (Exception ex) { return Finish(new NativeResult { CageGuid = input.CageGuid, Message = ex.Message, Details = new JsonObject { ["status"] = "FAIL", ["transaction_committed"] = false, ["error"] = ex.ToString() } }); }
    }

    private sealed class NoFinePositiveSolidException : InvalidOperationException
    {
        public string BarId { get; }
        public NoFinePositiveSolidException(string barId)
            : base($"{barId}：中心线存在，但 Revit 未生成满足正体积门槛的精细视图实体。已撤回本次生成。")
        {
            BarId = barId;
        }
    }

    private static NativeResult ApplyInternal(UIApplication ui, CageInput source, CagePlan plan, bool injectFailure, string operation, List<RuleFinding>? findings = null, bool deferDisplay = false, bool deferReport = false)
    {
        var totalClock = Stopwatch.StartNew();
        var workingSource = source.Clone();
        var eligible = plan.Bars.Where(b => b.Kind == "helix" &&
            b.Pieces.Zip(b.Pieces.Skip(1), (a, b) => Math.Abs(a.Pitch - b.Pitch) > 1e-9).Any(v => v))
            .Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var refinedBars = new HashSet<string>(StringComparer.Ordinal);
        var previousAttempts = new List<JsonObject>();
        NativeResult result;
        while (true)
        {
            // Every attempt owns and disposes its own TransactionGroup before
            // returning. A retry starts only after the previous complete model
            // mutation was rolled back; no Rebar/host/type handles are reused.
            result = ApplySingleAttempt(ui, workingSource, plan, injectFailure,
                operation, findings, true, true, refinedBars);
            result.Details["attempt_number"] = previousAttempts.Count + 1;
            result.Details["attempt_refined_bar_ids"] = JsonSerializer.SerializeToNode(refinedBars.OrderBy(x => x).ToArray());
            var failedBarId = S(result.Details, "failure_bar_id");
            bool canRetry = !result.Success &&
                S(result.Details, "failure_code") == "NO_FINE_POSITIVE_SOLID" &&
                S(result.Details["geometry_validation"], "status") == "PASS" &&
                result.Details["rolled_back"]?.GetValue<bool>() == true &&
                result.Details["transaction_committed"]?.GetValue<bool>() == false &&
                result.Details["original_ids_preserved"]?.GetValue<bool>() == true &&
                result.Details["rollback_error"] == null &&
                eligible.Contains(failedBarId) && !refinedBars.Contains(failedBarId) &&
                previousAttempts.Count < eligible.Count;
            if (!canRetry) break;

            previousAttempts.Add((JsonObject)result.Details.DeepClone());
            refinedBars.Add(failedBarId);
            // Keep the same attempted cage identity. The failed attempt's host
            // ID is deliberately NOT copied: it belongs to rolled-back state.
            workingSource.CageGuid = result.CageGuid;
        }
        result.Details["solid_fallback_policy"] = "original-first; retry only typed missing Fine positive solid after verified rollback; refine only newly failing pitch-transition helix; unchanged geometric and solid gates";
        result.Details["solid_fallback_used"] = previousAttempts.Count > 0;
        result.Details["solid_fallback_retry_count"] = previousAttempts.Count;
        result.Details["solid_fallback_attempt_limit"] = eligible.Count + 1;
        result.Details["solid_fallback_refined_bar_ids"] = JsonSerializer.SerializeToNode(refinedBars.OrderBy(x => x).ToArray());
        if (previousAttempts.Count > 0)
        {
            result.Details["first_attempt"] = previousAttempts[0].DeepClone();
            // Includes every failed prior attempt in full; final attempt is the
            // current top-level report. No errors or readback samples are lost.
            result.Details["attempt_details"] = new JsonArray(previousAttempts.Select(a => (JsonNode?)a.DeepClone()).ToArray());
        }
        result.Details["last_attempt_elapsed_ms"] = result.Details["elapsed_ms"]?.DeepClone();
        result.Details["elapsed_ms"] = totalClock.Elapsed.TotalMilliseconds;
        if (result.Success)
        {
            source.CageGuid = result.CageGuid;
            if (previousAttempts.Count > 0) result.Message += " 已在完整回滚后对失败螺旋的接口邻接圆弧局部细分重试，并通过原有几何与实体门槛。";
            if (!deferDisplay) ShowCage(ui, Current(ui), result.CageGuid, result.Details);
        }
        return deferReport ? result : Finish(result);
    }

    private static NativeResult ApplySingleAttempt(UIApplication ui, CageInput source, CagePlan plan, bool injectFailure, string operation, List<RuleFinding>? findings, bool deferDisplay, bool deferReport, IReadOnlySet<string> refinedBars)
    {
        var clock = Stopwatch.StartNew();
        var doc = Current(ui);
        // Snapshot removes cross-thread mutation risk from the modeless WPF editor.
        var input = JsonSerializer.Deserialize<CageInput>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;
        if (string.IsNullOrWhiteSpace(input.CageGuid))
        {
            var sameId = new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .Select(ReadTag).Where(t => t["input"] != null && S(t["input"], "CageId") == input.CageId).ToArray();
            if (sameId.Length > 0) throw new InvalidOperationException("当前项目已有相同钢筋笼编号。请载入其带管理 GUID 的参数并使用更新本笼，或更换新笼编号。");
            input.CageGuid = Guid.NewGuid().ToString("D");
        }
        if (!Guid.TryParse(input.CageGuid, out _)) throw new ArgumentException("钢筋笼管理标识不是有效 GUID。");
        var result = new NativeResult { CageGuid = input.CageGuid };
        result.Details = new JsonObject
        {
            ["status"] = "FAIL", ["operation"] = operation, ["cage_guid"] = input.CageGuid, ["cage_id"] = input.CageId,
            ["utc"] = DateTime.UtcNow.ToString("o"), ["revit_version"] = ui.Application.VersionNumber,
            ["revit_build"] = ui.Application.VersionBuild, ["document_path"] = doc.PathName,
            ["document_unique_id"] = doc.ProjectInformation.UniqueId, ["transaction_committed"] = false,
            ["input"] = JsonSerializer.SerializeToNode(input, JsonOptions), ["expected_physical_bar_count"] = plan.Bars.Count,
            ["rule_findings"] = JsonSerializer.SerializeToNode(findings ?? new List<RuleFinding>(), JsonOptions),
            ["geometry_convention"] = "Vertical straight bars; closed rings omit fabrication lap/hooks. One freeform Rebar contains native three-point spatial arcs across all pitch zones (regular arc span <=15 degrees). Pitch discontinuities use a small C1 quadratic z(theta) reference represented by a native arc, with transition widths and actual type bend-radius checks recorded in construction. Every actual centerline and fine-view solid is checked; this is an approximation, not an exact analytic helix. If a pitch-transition helix has no positive Fine-view solid, a fully rolled-back attempt may be retried with four subdivisions of its neighboring regular arcs; construction records the actual subdivisions and all acceptance gates remain unchanged."
        };
        var oldBars = ManagedBars(doc, input.CageGuid);
        if (PileHeadWorkflow.HasHeadOverrides(oldBars.Values))
            throw new InvalidOperationException("该钢筋笼已应用桩顶构造，请使用桩顶构造按钮修改端部；重新生成整笼前请按原流程清理钢筋。");
        var oldIds = oldBars.Values.Select(r => r.Id.Value).ToArray();
        var metadata = Metadata(doc, input.CageGuid);
        var oldMetadata = metadata == null ? null : ReadTag(metadata);
        result.Details["before_element_ids"] = JsonSerializer.SerializeToNode(oldIds);
        using var group = new TransactionGroup(doc, "灌注桩钢筋笼：安全生成或更新");
        group.Start();
        try
        {
            Element? existingHost = null;
            long priorHostId = oldMetadata?["host_id"]?.GetValue<long>() ?? 0;
            bool priorOwnedHost = oldMetadata?["owns_host"]?.GetValue<bool>() ?? false;
            NativeHostInfo? priorHostInfo = null;
            bool updateManagedHostGeometry = false;
            // The UI may retain the last created HostId for display. CreateHost
            // still means managed-host mode, so changed dimensions can rebuild it.
            bool useManagedHost = input.CreateHost && priorOwnedHost && (input.HostId == 0 || priorHostId == input.HostId);
            if (useManagedHost)
            {
                existingHost = doc.GetElement(new ElementId(priorHostId)) as FamilyInstance ?? throw new InvalidOperationException("原受管混凝土宿主缺失或类型已改变。");
                if (S(ReadTag(existingHost), "cage_guid") != input.CageGuid) throw new InvalidOperationException("原混凝土宿主管理标识不匹配，拒绝更新。");
                priorHostInfo = InspectHost(existingHost);
                updateManagedHostGeometry = !HostMatches(priorHostInfo, input);
            }
            else if (input.HostId > 0)
            {
                if (string.IsNullOrWhiteSpace(input.HostDocumentUniqueId) || input.HostDocumentUniqueId != doc.ProjectInformation.UniqueId)
                    throw new InvalidOperationException("已有宿主选择来自另一项目或缺少项目身份。请在当前项目重新选择宿主。");
                existingHost = doc.GetElement(new ElementId(input.HostId)) ?? throw new InvalidOperationException("所选宿主已不存在，请重新选择。");
                VerifyHost(existingHost, input);
            }
            else if (!input.CreateHost) throw new InvalidOperationException("请选择经核验的已有宿主，或开启创建混凝土宿主。");
            var family = existingHost == null || updateManagedHostGeometry ? EnsureHostFamily(ui, doc, input) : null;
            result.Details["host_reused"] = existingHost != null;
            result.Details["managed_host_geometry_updated"] = updateManagedHostGeometry;
            Element host;
            FailureCollector failures = new();
            using (var transaction = new Transaction(doc, "原生钢筋与管理信息"))
            {
                transaction.Start();
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                var allowedDeletes = oldBars.Values.Select(r => r.Id.Value).ToHashSet();
                foreach (var barId in oldIds)
                {
                    if (doc.GetElement(new ElementId(barId)) == null) continue;
                    var deleted = doc.Delete(new ElementId(barId));
                    if (deleted.Any(id => !allowedDeletes.Contains(id.Value)))
                        throw new InvalidOperationException("更新钢筋会连带删除管理范围外的接头、标注或其他依赖对象，已停止并回滚。");
                }
                long oldHostId = oldMetadata?["host_id"]?.GetValue<long>() ?? 0;
                bool ownedOldHost = oldMetadata?["owns_host"]?.GetValue<bool>() ?? false;
                if (ownedOldHost && oldHostId > 0 && (existingHost == null || oldHostId != existingHost.Id.Value))
                {
                    var oldHost = doc.GetElement(new ElementId(oldHostId));
                    if (oldHost != null && S(ReadTag(oldHost), "cage_guid") == input.CageGuid)
                    {
                        var foreignBars = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>()
                            .Any(r => r.GetHostId() == oldHost.Id && S(ReadTag(r), "cage_guid") != input.CageGuid);
                        if (foreignBars) throw new InvalidOperationException("原宿主承载了本插件管理范围以外的钢筋，停止替换宿主。");
                        result.Details["old_host_dependents_before_delete"] = DependentInfo(oldHost);
                        var deleted = doc.Delete(oldHost.Id);
                        result.Details["old_host_deleted_ids"] = JsonSerializer.SerializeToNode(deleted.Select(id => id.Value).ToArray());
                        if (deleted.Any(id => id.Value != oldHostId))
                            throw new InvalidOperationException("替换原宿主会连带删除其他构件，已停止并回滚。请先处理相关依赖。");
                    }
                }
                if (updateManagedHostGeometry)
                {
                    var managed = (FamilyInstance)existingHost!;
                    if (new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Any(r => r.GetHostId() == managed.Id))
                        throw new InvalidOperationException("受管宿主还承载其他钢筋，不能改变其尺寸或位置。");
                    var dependents = managed.GetDependentElements(null).Where(id => id != managed.Id).ToArray();
                    if (!family!.IsActive) family.Activate();
                    managed.Symbol = family;
                    var delta = new XYZ(Ft(input.X - priorHostInfo!.X), Ft(input.Y - priorHostInfo.Y), Ft(input.Z - priorHostInfo.Z));
                    if (delta.GetLength() > 1e-12) ElementTransformUtils.MoveElement(doc, managed.Id, delta);
                    doc.Regenerate();
                    if (dependents.Any(id => doc.GetElement(id) == null)) throw new InvalidOperationException("改变受管宿主会删除已有依赖对象，已停止并回滚。");
                }
                host = existingHost ?? CreateHost(doc, family!, input);
                doc.Regenerate();
                VerifyHost(host, input);
                result.HostId = host.Id.Value;
                var construction = new JsonArray();
                result.Details["construction"] = construction;
                foreach (var bar in plan.Bars)
                {
                    var typeId = bar.Kind == "longitudinal" ? input.MainTypeId : bar.Kind == "helix" ? input.SpiralTypeId : input.RingTypeId;
                    var type = EnsureBarType(doc, bar.Diameter, typeId);
                    var curves = MakeCurves(bar, input, type, doc.Application.ShortCurveTolerance, refinedBars.Contains(bar.Id) ? 4 : 1, out var method);
                    method["bar_id"] = bar.Id;
                    construction.Add(method);
                    var rebar = Rebar.CreateFreeForm(doc, type, host, new List<IList<Curve>> { curves }, out var validation);
                    if (validation != RebarFreeFormValidationResult.Success || rebar == null)
                        throw new InvalidOperationException($"{bar.Id}: Rebar.CreateFreeForm 返回 {validation}。");
                    SetTag(rebar, new JsonObject { ["cage_guid"] = input.CageGuid, ["cage_id"] = input.CageId, ["bar_id"] = bar.Id, ["kind"] = bar.Kind });
                    rebar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(input.CageId + ":" + bar.Id);
                    rebar.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("灌注桩钢筋笼；按确认参数建模；闭合环未增加加工接头；" + bar.Kind);
                }
                doc.Regenerate();
                metadata ??= DataStorage.Create(doc);
                SetTag(metadata, new JsonObject
                {
                    ["cage_guid"] = input.CageGuid, ["bar_id"] = "__metadata__", ["host_id"] = host.Id.Value,
                    ["owns_host"] = existingHost == null || (ownedOldHost && oldHostId == host.Id.Value),
                    ["input"] = JsonSerializer.SerializeToNode(input, JsonOptions), ["plan"] = JsonSerializer.SerializeToNode(plan, JsonOptions),
                    ["construction"] = construction.DeepClone(), ["geometry_convention"] = result.Details["geometry_convention"]!.DeepClone()
                });
                var view = EnsureView(doc, host, input.CageGuid);
                result.Details["view_id"] = view.Id.Value;
                if (injectFailure) throw new InvalidOperationException("INJECTED_FAILURE_AFTER_NATIVE_MUTATION：验收用故障注入。");
                var status = transaction.Commit();
                result.Details["revit_failures"] = failures.Messages;
                if (status != TransactionStatus.Committed) throw new InvalidOperationException("Revit 事务未提交：" + status);
            }
            // Read after the transaction committed, while the outer group can still roll back.
            var actual = ReadBars(doc, input.CageGuid);
            result.Details["host"] = HostJson(InspectHost(host));
            result.Details["bars"] = actual;
            result.Details["geometry_validation"] = VerifyReadback(plan, actual, input, host.Id.Value);
            result.Details["solid_validation"] = VerifySolids(doc, input.CageGuid, (View3D)doc.GetElement(new ElementId(result.Details["view_id"]!.GetValue<long>())));
            var assimilation = group.Assimilate();
            if (assimilation != TransactionStatus.Committed) throw new InvalidOperationException("事务组提交失败：" + assimilation);
            result.Success = true;
            result.MainCount = plan.Bars.Count(b => b.Kind == "longitudinal");
            result.SpiralCount = plan.Bars.Count(b => b.Kind == "helix");
            result.RingCount = plan.Bars.Count(b => b.Kind == "ring");
            result.Message = $"{input.CageId}：主筋 {result.MainCount} 根，螺旋筋 {result.SpiralCount} 个原生对象，内加劲环 {result.RingCount} 道。实际回读通过。";
            if (plan.Bars.Any(b => b.Pieces.Zip(b.Pieces.Skip(1), (a, b) => Math.Abs(a.Pitch - b.Pitch) > 1e-9).Any(v => v)))
                result.Message += " 螺距交界采用容差内短弧平滑连接，宽度和实测误差见报告。";
            result.Details["status"] = "PASS";
            result.Details["transaction_committed"] = true;
            source.CageGuid = input.CageGuid;
            if (!deferDisplay) ShowCage(ui, doc, input.CageGuid, result.Details);
        }
        catch (Exception ex)
        {
            var status = group.GetStatus();
            try { if (status == TransactionStatus.Started) status = group.RollBack(); }
            catch (Exception rollback) { result.Details["rollback_error"] = rollback.ToString(); status = group.GetStatus(); }
            result.Message = "生成或更新失败：" + ex.Message;
            result.Details["error"] = ex.ToString();
            if (ex is NoFinePositiveSolidException noSolid)
            {
                result.Details["failure_code"] = "NO_FINE_POSITIVE_SOLID";
                result.Details["failure_bar_id"] = noSolid.BarId;
            }
            result.Details["rollback_status"] = status.ToString();
            result.Details["rolled_back"] = status == TransactionStatus.RolledBack;
            result.Details["transaction_committed"] = status == TransactionStatus.Committed;
            var after = ManagedBars(doc, input.CageGuid).Values.Select(r => r.Id.Value).OrderBy(x => x).ToArray();
            result.Details["after_rollback_element_ids"] = JsonSerializer.SerializeToNode(after);
            result.Details["original_ids_preserved"] = oldIds.OrderBy(x => x).SequenceEqual(after);
        }
        result.Details["elapsed_ms"] = clock.Elapsed.TotalMilliseconds;
        return deferReport ? result : Finish(result);
    }

    public static NativeResult Read(UIApplication ui, string cageGuid)
    {
        var result = new NativeResult { CageGuid = cageGuid };
        try
        {
            var doc = Current(ui);
            var metadata = Metadata(doc, cageGuid) ?? throw new InvalidOperationException("当前项目没有该钢筋笼的持久化记录。");
            var tag = ReadTag(metadata);
            var input = tag["input"]!.Deserialize<CageInput>(JsonOptions)!;
            var plan = tag["plan"]!.Deserialize<CagePlan>(JsonOptions)!;
            result.HostId = tag["host_id"]!.GetValue<long>();
            var host = doc.GetElement(new ElementId(result.HostId)) ?? throw new InvalidOperationException("已管理钢筋笼的宿主缺失。");
            VerifyHost(host, input);
            var bars = ReadBars(doc, cageGuid);
            JsonObject? headReadback = null;
            JsonObject validation;
            if (PileHeadWorkflow.HasHeadOverrides(ManagedBars(doc, cageGuid).Values))
            {
                headReadback = PileHeadWorkflow.Read(ui, result.HostId,
                    PileHeadWorkflow.HasScope(ui, result.HostId, cageGuid) ? cageGuid : "");
                if (S(headReadback, "status") != "PASS") throw new InvalidOperationException("锚固主筋回读失败：" + S(headReadback, "message"));
                var expectedMainIds = bars.Where(b => S(b, "kind") == "longitudinal").Select(b => b!["element_id"]!.GetValue<long>()).Order().ToArray();
                var headIds = headReadback["main_bars"]!.AsArray().Select(b => b!["element_id"]!.GetValue<long>()).ToHashSet();
                if (expectedMainIds.Length != plan.Bars.Count(b => b.Kind == "longitudinal") || expectedMainIds.Any(id => !headIds.Contains(id)))
                    throw new InvalidOperationException("本笼纵筋未全部通过桩顶构造回读。");
                var stirrupPlan = new CagePlan { Bars = plan.Bars.Where(b => b.Kind != "longitudinal").ToList() };
                var stirrupBars = new JsonArray(bars.Where(b => S(b, "kind") != "longitudinal").Select(b => b!.DeepClone()).ToArray());
                validation = VerifyReadback(stirrupPlan, stirrupBars, input, result.HostId);
                validation["scope"] = "Original cage plan verifies stirrups; anchorage_readback verifies every longitudinal bar.";
            }
            else validation = VerifyReadback(plan, bars, input, result.HostId);
            result.Details = new JsonObject { ["status"] = "PASS", ["operation"] = "read", ["cage_guid"] = cageGuid, ["host"] = HostJson(InspectHost(host)), ["input"] = tag["input"]!.DeepClone(), ["bars"] = bars, ["geometry_validation"] = validation, ["construction"] = tag["construction"]?.DeepClone(), ["geometry_convention"] = tag["geometry_convention"]?.DeepClone() };
            // The saved cage input records the expectation, even if all plate elements or their own record were deleted.
            if (input.AdditionalData.TryGetValue("position_plates", out var plateInput) && plateInput.ValueKind != JsonValueKind.Null)
            {
                var plates = PositionPlateWorkflow.Read(ui, cageGuid, input);
                result.Details["position_plate_readback"] = plates;
                if (S(plates, "status") != "PASS") throw new InvalidOperationException("定位钢片回读失败：" + S(plates, "message"));
            }
            result.Success = true;
            if (headReadback != null) result.Details["anchorage_readback"] = headReadback;
            result.MainCount = plan.Bars.Count(b => b.Kind == "longitudinal"); result.SpiralCount = plan.Bars.Count(b => b.Kind == "helix"); result.RingCount = plan.Bars.Count(b => b.Kind == "ring");
            result.Message = $"回读通过：主筋 {result.MainCount}，连续螺旋筋 {result.SpiralCount}，内环 {result.RingCount}。";
        }
        catch (Exception ex) { result.Message = ex.Message; result.Details = new JsonObject { ["status"] = "FAIL", ["operation"] = "read", ["error"] = ex.ToString() }; }
        return Finish(result);
    }

    public static JsonObject Run(UIApplication ui, string requestJson)
    {
        var request = JsonNode.Parse(requestJson)!.AsObject();
        var operation = S(request, "operation", "probe");
        if (operation.StartsWith("revision_", StringComparison.Ordinal)) return UiRevisionAcceptance.Run(ui, request);
        if (operation.StartsWith("library_", StringComparison.Ordinal)) return FamilyLibraryAcceptance.Run(ui, request);
        if (operation.StartsWith("pilehead_", StringComparison.Ordinal)) return PileHeadWorkflow.Run(ui, request);
        if (operation == "simple_diagnose") return ColumnDiagnostics.Run(ui, request);
        if (operation == "simple_register_ribbon")
        {
            App.EnsureDispatcher();
            return RibbonConfiguration.Register(ui);
        }
        if (operation == "simple_open_ui")
        {
            App.EnsureDispatcher();
            SimpleColumnWindow.ShowFor(ui);
            return new JsonObject { ["status"] = "PASS", ["assembly"] = typeof(NativeEngine).Assembly.Location,
                ["assembly_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(NativeEngine).Assembly.Location))) };
        }
        if (operation == "simple_ui_state")
        {
            var window = SimpleColumnWindow.Instance ?? throw new InvalidOperationException("圆形柱配筋窗口尚未打开。");
            if (request["capture_path"] != null) window.CaptureScreenshot(WorkspacePath(S(request, "capture_path")));
            return new JsonObject { ["status"] = "PASS", ["input"] = JsonSerializer.SerializeToNode(window.Input, JsonOptions),
                ["anchorage"] = JsonSerializer.SerializeToNode(window.AnchorageSettings, JsonOptions), ["anchorage_summary"] = window.AnchorageSummary,
                ["position_plates"] = JsonSerializer.SerializeToNode(window.PositionPlateSettings, JsonOptions), ["position_plate_summary"] = window.PositionPlateSummary,
                ["last_result"] = JsonSerializer.SerializeToNode(window.LastResult, JsonOptions) };
        }
        if (operation == "simple_inventory")
        {
            var doc = Current(ui); WorkspacePath(doc.PathName);
            return new JsonObject { ["status"] = "PASS", ["document_path"] = doc.PathName,
                ["elements"] = JsonSerializer.SerializeToNode(new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements()
                    .OrderBy(e => e.Id.Value).Select(e => new { id = e.Id.Value, unique_id = e.UniqueId, type = e.GetType().Name }).ToArray(), JsonOptions) };
        }
        if (operation.StartsWith("simple_", StringComparison.Ordinal)) return SimpleColumnWorkflow.Run(ui, request);
        if (operation == "run_assembly")
        {
            if (!File.Exists(Path.Combine(App.Runtime, "enable-test-queue.flag"))) throw new InvalidOperationException("开发程序集桥接仅在显式启用测试队列时可用。");
            var path = WorkspacePath(S(request, "assembly_path"));
            var expected = S(request, "assembly_sha256");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (expected.Length != 64 || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("开发程序集 SHA256 核验失败。");
            var nested = request["request"]?.DeepClone().AsObject() ?? throw new ArgumentException("缺少桥接 request。");
            if (S(nested, "operation") == "run_assembly") throw new ArgumentException("开发程序集桥接不得递归。");
            var assembly = Assembly.LoadFrom(path);
            if (assembly.GetName().Name == typeof(NativeEngine).Assembly.GetName().Name) throw new ArgumentException("开发程序集必须使用独立 AssemblyName，避免 CLR 复用已加载旧版。");
            var method = assembly.GetType("PileCage.NativeEngine", true)!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
            try { return (JsonObject)method.Invoke(null, new object[] { ui, nested.ToJsonString(JsonOptions) })!; }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        if (request["create_document"]?.GetValue<bool>() == true)
        {
            var path = WorkspacePath(S(request, "save_as", "runtime/models/native_probe.rvt"));
            if (File.Exists(path)) throw new InvalidOperationException("新建验收项目的目标已存在，拒绝覆盖：" + path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var created = ui.Application.NewProjectDocument(UnitSystem.Metric);
            created.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false });
            created.Close(false);
            ui.OpenAndActivateDocument(path);
        }
        if (operation is "probe" or "apply" or "rollback_test")
        {
            var path = Current(ui).PathName;
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("测试队列只能修改已保存在本工作区中的受控项目；可使用 create_document=true。");
            WorkspacePath(path);
        }
        NativeResult result;
        if (operation == "probe") result = Probe(ui);
        else if (operation == "read") result = Read(ui, S(request, "cage_guid"));
        else if (operation == "apply" || operation == "rollback_test")
        {
            var input = request["input"]?.Deserialize<CageInput>(JsonOptions);
            if (input == null && request["input_path"] != null) input = JsonSerializer.Deserialize<CageInput>(File.ReadAllText(WorkspacePath(S(request, "input_path"))), JsonOptions);
            if (input == null) throw new ArgumentException("apply 需要 input 或工作区内的 input_path。");
            result = Apply(ui, input, true, operation == "rollback_test", request["accept_warnings"]?.GetValue<bool>() == true);
        }
        else if (operation == "inspect_host")
        {
            var id = request["host_id"]!.GetValue<long>();
            result = new NativeResult { Success = true, Details = HostJson(InspectHost(Current(ui).GetElement(new ElementId(id)))) };
        }
        else throw new ArgumentException("未知队列操作：" + operation);
        if (result.Success && request["save"]?.GetValue<bool>() == true)
        {
            try
            {
                var doc = Current(ui);
                if (string.IsNullOrWhiteSpace(doc.PathName)) doc.SaveAs(WorkspacePath(S(request, "save_as", "runtime/models/pilecage.rvt")), new SaveAsOptions { OverwriteExistingFile = false });
                else
                {
                    WorkspacePath(doc.PathName); // Test automation must not save any previous workspace project.
                    doc.Save();
                }
                result.Details["saved_path"] = doc.PathName;
                result.Details["save_succeeded"] = true;
            }
            catch (Exception ex)
            {
                // Model commit and file save are separate outcomes. Preserve the
                // actual commit result instead of reporting a misleading rollback.
                result.Success = false;
                result.Details["status"] = "FAIL";
                result.Details["save_succeeded"] = false;
                result.Details["save_error"] = ex.ToString();
                result.Message += " 模型操作后保存文件失败：" + ex.Message;
            }
        }
        result.Details["message"] = result.Message;
        result.Details["report_path"] = result.ReportPath;
        result.Details["cage_guid"] = result.CageGuid;
        if (!string.IsNullOrWhiteSpace(result.ReportPath))
        {
            try { File.WriteAllText(result.ReportPath, result.Details.ToJsonString(JsonOptions)); }
            catch (Exception ex) { result.Details["report_write_succeeded"] = false; result.Details["report_write_error"] = ex.ToString(); }
        }
        return result.Details;
    }

    private static string WorkspacePath(string path)
    {
        var resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(App.Root, path));
        if (!resolved.StartsWith(App.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("文件必须保存在新工作区 P6+1 内。");
        return resolved;
    }
    private static NativeResult Finish(NativeResult result)
    {
        // Diagnostics I/O must never overwrite an already committed model result.
        try
        {
            Directory.CreateDirectory(Path.Combine(App.Runtime, "reports"));
            var assembly = typeof(NativeEngine).Assembly;
            result.Details["assembly_path"] = assembly.Location;
            result.Details["assembly_version"] = assembly.GetName().Version?.ToString();
            result.Details["assembly_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
            result.ReportPath = Path.Combine(App.Runtime, "reports", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + (result.Success ? "PASS" : "FAIL") + ".json");
            result.Details["message"] = result.Message;
            result.Details["cage_guid"] = result.CageGuid;
            result.Details["report_write_succeeded"] = true;
            File.WriteAllText(result.ReportPath, result.Details.ToJsonString(JsonOptions));
        }
        catch (Exception ex)
        {
            result.Details["report_write_succeeded"] = false;
            result.Details["report_write_error"] = ex.ToString();
            result.ReportPath = "";
            result.Message += " 报告写入失败，模型事务结果保持原状：" + ex.Message;
        }
        return result;
    }

    private static Schema Schema()
    {
        var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);
        if (schema != null) return schema;
        var builder = new SchemaBuilder(SchemaId);
        builder.SetSchemaName("PileCagePlusOneManagedV1"); builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField("record", typeof(string)); return builder.Finish();
    }
    private static void SetTag(Element element, JsonObject data)
    {
        var entity = new Entity(Schema()); entity.Set("record", data.ToJsonString(JsonOptions)); element.SetEntity(entity);
    }
    private static JsonObject ReadTag(Element element)
    {
        var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId); if (schema == null) return new();
        var entity = element.GetEntity(schema); return entity.IsValid() ? JsonNode.Parse(entity.Get<string>("record"))!.AsObject() : new();
    }
    private static Dictionary<string, Rebar> ManagedBars(Document document, string guid)
    {
        var result = new Dictionary<string, Rebar>();
        foreach (var rebar in new FilteredElementCollector(document).OfClass(typeof(Rebar)).Cast<Rebar>())
        {
            var data = ReadTag(rebar); if (S(data, "cage_guid") != guid) continue;
            if (!result.TryAdd(S(data, "bar_id"), rebar)) throw new InvalidOperationException("管理范围中存在重复钢筋编号，停止更新。");
        }
        return result;
    }
    private static DataStorage? Metadata(Document document, string guid)
    {
        var rows = new FilteredElementCollector(document).OfClass(typeof(DataStorage)).Cast<DataStorage>().Where(d => S(ReadTag(d), "cage_guid") == guid).ToArray();
        if (rows.Length > 1) throw new InvalidOperationException("存在重复钢筋笼管理记录。");
        return rows.FirstOrDefault();
    }

    private static FamilySymbol EnsureHostFamily(UIApplication ui, Document document, CageInput input)
    {
        var name = "PC1_Pile_" + Hash(input.Diameter.ToString("R", CultureInfo.InvariantCulture) + "/" + input.Height.ToString("R", CultureInfo.InvariantCulture))[..12];
        var found = new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s => s.FamilyName == name);
        if (found != null) return found;
        if (!File.Exists(FamilyTemplate)) throw new FileNotFoundException("缺少圆形混凝土宿主使用的结构基础族模板。", FamilyTemplate);
        var familyDocument = ui.Application.NewFamilyDocument(FamilyTemplate);
        try
        {
            using (var transaction = new Transaction(familyDocument, "创建圆形混凝土桩宿主族"))
            {
                transaction.Start(); familyDocument.FamilyManager.NewType("圆形桩");
                var materialKind = familyDocument.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_STRUCT_MATERIAL_TYPE);
                if (materialKind != null && !materialKind.IsReadOnly) materialKind.Set((int)StructuralMaterialType.Concrete);
                var canHost = familyDocument.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_CAN_HOST_REBAR);
                if (canHost != null && !canHost.IsReadOnly) canHost.Set(1);
                var materialId = Material.Create(familyDocument, "PC1 混凝土"); var material = (Material)familyDocument.GetElement(materialId);
                material.MaterialClass = "Concrete";
                material.StructuralAssetId = PropertySetElement.Create(familyDocument, new StructuralAsset("PC1 混凝土物理资产", StructuralAssetClass.Concrete)).Id;
                var plane = SketchPlane.Create(familyDocument, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                var loop = new CurveArray(); double radius = Ft(input.Diameter / 2);
                loop.Append(Arc.Create(XYZ.Zero, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                loop.Append(Arc.Create(XYZ.Zero, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                var profile = new CurveArrArray(); profile.Append(loop);
                var extrusion = familyDocument.FamilyCreate.NewExtrusion(true, profile, plane, Ft(input.Height));
                extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(materialId);
                var materialParameter = familyDocument.FamilyManager.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                if (materialParameter != null && !materialParameter.IsReadOnly) familyDocument.FamilyManager.Set(materialParameter, materialId);
                transaction.Commit();
            }
            var directory = Path.Combine(App.Runtime, "families"); Directory.CreateDirectory(directory);
            familyDocument.SaveAs(Path.Combine(directory, name + ".rfa"), new SaveAsOptions { OverwriteExistingFile = true });
            var family = familyDocument.LoadFamily(document, new LoadOptions());
            return (FamilySymbol)document.GetElement(family.GetFamilySymbolIds().First());
        }
        finally { familyDocument.Close(false); }
    }
    private static FamilyInstance CreateHost(Document document, FamilySymbol symbol, CageInput input)
    {
        if (!symbol.IsActive) symbol.Activate();
        var level = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => Math.Abs(l.Elevation - Ft(input.Z))).FirstOrDefault() ?? Level.Create(document, 0);
        var host = document.Create.NewFamilyInstance(new XYZ(Ft(input.X), Ft(input.Y), Ft(input.Z)), symbol, level, StructuralType.Footing);
        SetTag(host, new JsonObject { ["cage_guid"] = input.CageGuid, ["bar_id"] = "__host__", ["owns_host"] = true });
        return host;
    }

    public static NativeHostInfo InspectHost(Element element)
    {
        if (element == null || !RebarHostData.IsValidHost(element)) throw new InvalidOperationException("所选构件不能作为 Revit 钢筋宿主。");
        var solids = new List<Solid>();
        void Gather(GeometryElement geometry)
        {
            foreach (var item in geometry)
            {
                if (item is Solid solid && solid.Volume > 1e-9) solids.Add(solid);
                else if (item is GeometryInstance instance) Gather(instance.GetInstanceGeometry());
            }
        }
        var geometry = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false });
        if (geometry == null) throw new InvalidOperationException("所选宿主没有可验证实体几何。");
        Gather(geometry);
        if (solids.Count != 1) throw new InvalidOperationException("第一版仅接受单一完整圆柱实体宿主。");
        var solidBody = solids[0];
        var cylinders = solidBody.Faces.Cast<Face>().OfType<CylindricalFace>().ToArray();
        if (cylinders.Length == 0) throw new InvalidOperationException("所选宿主不是圆截面桩：未找到圆柱面。");
        var cylinder = cylinders[0];
        if (Math.Abs(cylinder.Axis.Normalize().DotProduct(XYZ.BasisZ)) < 1 - 1e-8) throw new InvalidOperationException("第一版仅支持竖向直轴桩宿主。");
        double cylinderRadius = cylinder.get_Radius(0).GetLength();
        if (cylinders.Any(c => Math.Abs(c.get_Radius(0).GetLength() - cylinderRadius) > Ft(0.01) || new XYZ(c.Origin.X - cylinder.Origin.X, c.Origin.Y - cylinder.Origin.Y, 0).GetLength() > Ft(0.01)))
            throw new InvalidOperationException("所选宿主包含不同轴或不同半径的圆柱面。");
        var zValues = solidBody.Edges.Cast<Edge>().SelectMany(edge => edge.Tessellate()).Select(p => p.Z).ToArray();
        double low = zValues.Min(), high = zValues.Max();
        if (high <= low) throw new InvalidOperationException("The host has a nonpositive axial extent.");
        HostCylinderTopology.Validate(solidBody, cylinder.Origin, cylinderRadius, low, high);
        var hasConcrete = element.GetMaterialIds(false).Select(id => element.Document.GetElement(id)).OfType<Material>().Any(m =>
        {
            if (m.StructuralAssetId == ElementId.InvalidElementId) return false;
            return (element.Document.GetElement(m.StructuralAssetId) as PropertySetElement)?.GetStructuralAsset()?.StructuralAssetClass == StructuralAssetClass.Concrete;
        });
        if (!hasConcrete) throw new InvalidOperationException("宿主未读到混凝土物理资产，不能按混凝土桩接受。");
        return new NativeHostInfo(element.Id.Value, element.UniqueId, Mm(2 * cylinderRadius), Mm(high - low), Mm(cylinder.Origin.X), Mm(cylinder.Origin.Y), Mm(low));
    }
    public static string DescribeHost(UIApplication ui, long hostId, CageInput expected)
    {
        var document = Current(ui);
        var host = document.GetElement(new ElementId(hostId)) ?? throw new InvalidOperationException("所选宿主不存在。");
        VerifyHost(host, expected);
        return $"宿主 {hostId} 已核验：竖向完整圆柱、混凝土物理资产、D={expected.Diameter:0.###} mm、H={expected.Height:0.###} mm。";
    }
    private static void VerifyHost(Element host, CageInput input)
    {
        var info = InspectHost(host);
        if (Math.Abs(info.Diameter - input.Diameter) > 0.1 || Math.Abs(info.Height - input.Height) > 0.1)
            throw new InvalidOperationException($"宿主尺寸不匹配：实际 D={info.Diameter:0.###}、H={info.Height:0.###} mm；输入 D={input.Diameter:0.###}、H={input.Height:0.###} mm。");
        if (Math.Sqrt(Math.Pow(info.X - input.X, 2) + Math.Pow(info.Y - input.Y, 2) + Math.Pow(info.Z - input.Z, 2)) > 0.1)
            throw new InvalidOperationException("宿主轴底原点与输入放置位置不匹配，请重新选择宿主并采用其核验位置。");
    }
    private static bool HostMatches(NativeHostInfo info, CageInput input) =>
        Math.Abs(info.Diameter - input.Diameter) <= 0.1 && Math.Abs(info.Height - input.Height) <= 0.1 &&
        Math.Sqrt(Math.Pow(info.X - input.X, 2) + Math.Pow(info.Y - input.Y, 2) + Math.Pow(info.Z - input.Z, 2)) <= 0.1;
    private static JsonArray DependentInfo(Element element)
    {
        return new JsonArray(element.GetDependentElements(null).Select(id =>
        {
            var dependent = element.Document.GetElement(id);
            return (JsonNode?)new JsonObject { ["element_id"] = id.Value, ["class"] = dependent?.GetType().FullName, ["category"] = dependent?.Category?.Name, ["cage_guid"] = dependent == null ? "" : S(ReadTag(dependent), "cage_guid") };
        }).ToArray());
    }
    private static JsonObject HostJson(NativeHostInfo info) => new()
    {
        ["element_id"] = info.ElementId, ["unique_id"] = info.UniqueId, ["diameter_mm"] = info.Diameter,
        ["height_mm"] = info.Height, ["origin_mm"] = new JsonArray(info.X, info.Y, info.Z),
        ["verified_vertical_complete_cylinder"] = true, ["verified_concrete_asset"] = true, ["valid_rebar_host"] = true
    };

    private static RebarBarType EnsureBarType(Document document, double diameter, long requestedId)
    {
        if (requestedId > 0)
        {
            var selected = document.GetElement(new ElementId(requestedId)) as RebarBarType ?? throw new InvalidOperationException("指定钢筋类型不存在。");
            if (Math.Abs(Mm(selected.BarNominalDiameter) - diameter) > 0.001 || Math.Abs(Mm(selected.BarModelDiameter) - diameter) > 0.001)
                throw new InvalidOperationException("所选钢筋类型的公称直径或模型直径与输入不匹配：" + selected.Name);
            return selected;
        }
        var name = "PC1 d" + diameter.ToString("0.###", CultureInfo.InvariantCulture);
        var type = new FilteredElementCollector(document).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().FirstOrDefault(t => t.Name == name);
        if (type != null)
        {
            if (Math.Abs(Mm(type.BarNominalDiameter) - diameter) > 0.001 || Math.Abs(Mm(type.BarModelDiameter) - diameter) > 0.001) throw new InvalidOperationException("插件钢筋类型已被外部修改，直径不再匹配：" + name);
            return type;
        }
        type = RebarBarType.Create(document); type.Name = name; type.BarNominalDiameter = Ft(diameter); type.BarModelDiameter = Ft(diameter);
        type.StandardBendDiameter = Ft(4 * diameter); type.StirrupTieBendDiameter = Ft(4 * diameter); return type;
    }

    private static IList<Curve> MakeCurves(BarPlan bar, CageInput input, RebarBarType type, double shortCurveTolerance, int transitionNeighborParts, out JsonObject method)
    {
        if (transitionNeighborParts != 1 && transitionNeighborParts != 4)
            throw new ArgumentOutOfRangeException(nameof(transitionNeighborParts));
        method = new JsonObject { ["method"] = bar.Kind == "helix" ? "native_spatial_three_point_arcs_with_tolerance_bounded_C1_pitch_junctions" : bar.Kind, ["max_regular_arc_angle_degrees"] = 15.0,
            ["transition_neighbor_subdivisions"] = transitionNeighborParts,
            ["solid_fallback_refined"] = transitionNeighborParts > 1 };
        if (bar.Kind == "longitudinal") return new List<Curve> { Line.CreateBound(XYZmm(bar.Points[0]), XYZmm(bar.Points[^1])) };
        if (bar.Kind == "ring")
        {
            var center = new XYZ(Ft(input.X), Ft(input.Y), Ft(input.Z + bar.Start)); var radius = Ft(bar.Radius);
            return new List<Curve> { Arc.Create(center, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY), Arc.Create(center, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY) };
        }
        if (bar.Kind != "helix" || bar.Pieces.Count == 0) throw new InvalidOperationException("未知钢筋几何或缺少连续螺旋分区。");
        var curves = new List<Curve>();
        var transitions = new JsonArray(); method["pitch_transitions"] = transitions;
        var halfWidths = new double[Math.Max(0, bar.Pieces.Count - 1)];
        double centerlineBendRadius = Mm(Math.Max(type.StandardBendDiameter, type.StirrupTieBendDiameter) + type.BarModelDiameter) / 2;
        for (int j = 0; j < halfWidths.Length; j++)
        {
            var left = bar.Pieces[j]; var right = bar.Pieces[j + 1];
            double a = left.Pitch / (2 * Math.PI), b = right.Pitch / (2 * Math.PI), delta = b - a;
            if (Math.Abs(delta) < 1e-12) continue;
            // z(theta) is a quadratic transition matching both endpoint values
            // and slopes. The conservative phase-equivalent error bound is
            // R*abs(b-a)*alpha/(4*min(a,b)), using 60% of the fixed path gate.
            double alpha = Math.Min(0.01, input.Tolerance * 0.6 * 4 * Math.Min(a, b) / (bar.Radius * Math.Abs(delta)));
            alpha = Math.Min(alpha, Math.Min((left.End - left.Start) / a, (right.End - right.Start) / b) / 8);
            if (2 * alpha * Math.Sqrt(bar.Radius * bar.Radius + Math.Pow((a + b) / 2, 2)) <= Mm(shortCurveTolerance) * 1.1)
                throw new InvalidOperationException("螺距交界在当前容差内所需连接弧短于 Revit 可建最短曲线；此组螺距差和容差暂不支持，未改变输入螺距。");
            halfWidths[j] = alpha;
        }
        XYZ At(double angle, double z) => new(Ft(input.X + bar.Radius * Math.Cos(angle)), Ft(input.Y + bar.Radius * Math.Sin(angle)), Ft(input.Z + z));
        for (int index = 0; index < bar.Pieces.Count; index++)
        {
            var piece = bar.Pieces[index]; double slope = piece.Pitch / (2 * Math.PI);
            double thetaEnd = piece.StartAngle + (piece.End - piece.Start) / slope;
            double low = piece.StartAngle + (index > 0 ? halfWidths[index - 1] : 0);
            double high = thetaEnd - (index < halfWidths.Length ? halfWidths[index] : 0);
            int segments = Math.Max(1, (int)Math.Ceiling((high - low) / (Math.PI / 12)));
            XYZ OnSource(double theta) => At(theta, piece.Start + (theta - piece.StartAngle) * slope);
            for (int j = 0; j < segments; j++)
            {
                double start = low + (high - low) * j / segments, end = low + (high - low) * (j + 1) / segments;
                bool besideTransition = (j == 0 && index > 0 && halfWidths[index - 1] > 0) || (j == segments - 1 && index < halfWidths.Length && halfWidths[index] > 0);
                int localParts = besideTransition ? transitionNeighborParts : 1;
                if (localParts == 1)
                {
                    // Preserve the original arithmetic and Arc.Create call
                    // exactly; do not reconstruct end as start+(end-start).
                    curves.Add(Arc.Create(OnSource(start), OnSource(end), OnSource((start + end) / 2)));
                    continue;
                }
                for (int part = 0; part < localParts; part++)
                {
                    double p0 = start + (end - start) * part / localParts, p1 = start + (end - start) * (part + 1) / localParts;
                    curves.Add(Arc.Create(OnSource(p0), OnSource(p1), OnSource((p0 + p1) / 2)));
                }
            }
            if (index < halfWidths.Length && halfWidths[index] > 0)
            {
                double alpha = halfWidths[index], a = slope, b = bar.Pieces[index + 1].Pitch / (2 * Math.PI), z0 = piece.End;
                double startZ = z0 - a * alpha, endZ = z0 + b * alpha, middleZ = z0 + (b - a) * alpha / 4;
                var connector = Arc.Create(At(thetaEnd - alpha, startZ), At(thetaEnd + alpha, endZ), At(thetaEnd, middleZ));
                if (Mm(connector.Radius) < centerlineBendRadius)
                    throw new InvalidOperationException($"螺距交界所需连接弧半径 {Mm(connector.Radius):0.###} mm 小于实际钢筋类型的中心线最小弯曲半径 {centerlineBendRadius:0.###} mm；此组参数暂不支持。");
                curves.Add(connector);
                transitions.Add(new JsonObject
                {
                    ["source_boundary_z_mm"] = z0, ["source_boundary_cumulative_angle_rad"] = thetaEnd,
                    ["start_z_mm"] = startZ, ["end_z_mm"] = endZ, ["half_width_angle_rad"] = alpha,
                    ["nominal_connector_length_mm"] = Mm(connector.Length), ["connector_radius_mm"] = Mm(connector.Radius),
                    ["type_min_centerline_bend_radius_mm"] = centerlineBendRadius,
                    ["quadratic_reference_phase_bound_mm"] = bar.Radius * Math.Abs(b - a) * alpha / (4 * Math.Min(a, b)),
                    ["convention"] = "C1 quadratic z(theta) reference approximated by one native three-point arc; complete actual centerline still checked against original piecewise pitch law"
                });
            }
        }
        return curves;
    }

    private static JsonArray ReadBars(Document document, string guid)
    {
        var result = new JsonArray();
        foreach (var pair in ManagedBars(document, guid).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var rebar = pair.Value; var type = (RebarBarType)document.GetElement(rebar.GetTypeId());
            var paths = new JsonArray();
            foreach (var curve in rebar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0))
            {
                int count = Math.Clamp((int)Math.Ceiling(Mm(curve.Length) / 5), 32, 20000);
                var points = new JsonArray();
                for (int i = 0; i <= count; i++) points.Add(Point(curve.Evaluate((double)i / count, true)));
                paths.Add(new JsonObject { ["curve_type"] = curve.GetType().FullName, ["length_mm"] = Mm(curve.Length), ["points_mm"] = points });
            }
            result.Add(new JsonObject
            {
                ["bar_id"] = pair.Key, ["element_id"] = rebar.Id.Value, ["unique_id"] = rebar.UniqueId,
                ["class_name"] = rebar.GetType().FullName, ["is_freeform"] = rebar.IsRebarFreeForm(),
                ["host_id"] = rebar.GetHostId().Value, ["quantity"] = rebar.Quantity, ["type_id"] = type.Id.Value,
                ["type_name"] = type.Name, ["diameter_mm"] = Mm(type.BarNominalDiameter), ["model_diameter_mm"] = Mm(type.BarModelDiameter),
                ["length_mm"] = Mm(rebar.TotalLength), ["kind"] = S(ReadTag(rebar), "kind"), ["curves"] = paths
            });
        }
        return result;
    }

    private static JsonObject VerifyReadback(CagePlan plan, JsonArray actual, CageInput input, long hostId)
    {
        if (plan.Bars.Count != actual.Count) throw new InvalidOperationException("原生钢筋对象总数与物理钢筋计划不一致。");
        var byId = actual.ToDictionary(n => S(n, "bar_id")); var checks = new JsonArray();
        double[] Arr(JsonNode? p) => p!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        double Distance(double[] a, double[] b) => Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
        foreach (var bar in plan.Bars)
        {
            if (!byId.TryGetValue(bar.Id, out var row)) throw new InvalidOperationException("缺少原生钢筋：" + bar.Id);
            if (S(row, "class_name") != typeof(Rebar).FullName || N(row, "quantity") != 1 || row!["host_id"]!.GetValue<long>() != hostId)
                throw new InvalidOperationException(bar.Id + "：原生对象类型、物理根数或宿主关系不符。");
            if (Math.Abs(N(row, "diameter_mm") - bar.Diameter) > 0.001 || Math.Abs(N(row, "model_diameter_mm") - bar.Diameter) > 0.001)
                throw new InvalidOperationException(bar.Id + "：回读直径不符。");
            double lengthError = Math.Abs(N(row, "length_mm") - bar.Length), lengthLimit = Math.Max(0.1, bar.Length * 0.001);
            if (lengthError > lengthLimit) throw new InvalidOperationException(bar.Id + "：实际中心线长度超出 0.1%/0.1 mm 验收限值。");
            var curveRows = row["curves"]!.AsArray();
            var points = curveRows.SelectMany(c => c!["points_mm"]!.AsArray()).Select(Arr).ToArray();
            if (points.Length < 2) throw new InvalidOperationException(bar.Id + "：原生中心线回读为空。");
            double endpointError;
            if (bar.Kind == "ring") endpointError = Distance(points[0], points[^1]);
            else
            {
                var first = bar.Points[0]; var last = bar.Points[^1];
                endpointError = Math.Max(Distance(points[0], new[] { first.X, first.Y, first.Z }), Distance(points[^1], new[] { last.X, last.Y, last.Z }));
            }
            double adjacency = 0;
            for (int j = 1; j < curveRows.Count; j++) adjacency = Math.Max(adjacency, Distance(Arr(curveRows[j - 1]!["points_mm"]!.AsArray()[^1]), Arr(curveRows[j]!["points_mm"]!.AsArray()[0])));
            if (Math.Max(endpointError, adjacency) > 0.05) throw new InvalidOperationException(bar.Id + "：中心线端点或分段连接超出 0.05 mm。");
            double radial = 0, phase = 0, lineOrPlane = 0;
            foreach (var p in points)
            {
                if (bar.Kind == "longitudinal") lineOrPlane = Math.Max(lineOrPlane, Math.Sqrt(Math.Pow(p[0] - bar.Points[0].X, 2) + Math.Pow(p[1] - bar.Points[0].Y, 2)));
                else
                {
                    var dx = p[0] - input.X; var dy = p[1] - input.Y;
                    radial = Math.Max(radial, Math.Abs(Math.Sqrt(dx * dx + dy * dy) - bar.Radius));
                    if (bar.Kind == "ring") lineOrPlane = Math.Max(lineOrPlane, Math.Abs(p[2] - (input.Z + bar.Start)));
                    else
                    {
                        double z = p[2] - input.Z, theta = bar.Pieces[0].StartAngle;
                        foreach (var piece in bar.Pieces) theta += 2 * Math.PI * Math.Clamp(z - piece.Start, 0, piece.End - piece.Start) / piece.Pitch;
                        double error = Math.Atan2(Math.Sin(Math.Atan2(dy, dx) - theta), Math.Cos(Math.Atan2(dy, dx) - theta));
                        phase = Math.Max(phase, Math.Abs(error) * bar.Radius);
                    }
                }
            }
            if (Math.Max(radial, Math.Max(phase, lineOrPlane)) > input.Tolerance)
                throw new InvalidOperationException($"{bar.Id}：实际中心线超出 {input.Tolerance:0.###} mm 容差；径向 {radial:0.####}，等效相位 {phase:0.####}，直线/平面 {lineOrPlane:0.####} mm。");
            checks.Add(new JsonObject
            {
                ["bar_id"] = bar.Id, ["length_error_mm"] = lengthError, ["length_tolerance_mm"] = lengthLimit,
                ["endpoint_error_mm"] = endpointError, ["adjacent_endpoint_gap_mm"] = adjacency,
                ["max_radial_error_mm"] = radial, ["max_phase_error_equivalent_mm"] = phase, ["max_line_or_plane_error_mm"] = lineOrPlane,
                ["centerline_curve_count"] = curveRows.Count, ["actual_sample_count"] = points.Length
            });
        }
        return new JsonObject
        {
            ["status"] = "PASS", ["path_tolerance_mm"] = input.Tolerance, ["endpoint_tolerance_mm"] = 0.05,
            ["sampling_method"] = "actual Rebar.GetCenterlineCurves and Evaluate at <=5 mm nominal arc spacing, minimum 32 per API curve; finite sampling is not an exact global error bound",
            ["phase_reference"] = "independent cumulative integral 2*pi*sum(clamp(z-start,0,length)/pitch)", ["bars"] = checks
        };
    }

    private static JsonObject VerifySolids(Document document, string guid, View3D view)
    {
        var rows = new JsonArray();
        foreach (var pair in ManagedBars(document, guid))
        {
            var rebar = pair.Value;
            int count = 0; double volume = 0;
            void Count(GeometryElement? geometry)
            {
                if (geometry == null) return;
                foreach (var obj in geometry)
                {
                    if (obj is Solid solid && solid.Volume > 1e-12) { count++; volume += solid.Volume; }
                    else if (obj is GeometryInstance instance) Count(instance.GetInstanceGeometry());
                }
            }
            Count(rebar.GetFullGeometryForView(view));
            if (count == 0) throw new NoFinePositiveSolidException(pair.Key);
            var type = (RebarBarType)document.GetElement(rebar.GetTypeId());
            double expectedVolume = Math.PI * type.BarModelDiameter * type.BarModelDiameter / 4 * rebar.TotalLength;
            double volumeError = Math.Abs(volume / expectedVolume - 1);
            if (!double.IsFinite(volumeError) || volumeError > 0.001)
                throw new InvalidOperationException($"{pair.Key}：精细实体体积与钢筋直径及长度不符，差值 {volumeError:P4} 超过 0.1% 核查限值。");
            rows.Add(new JsonObject { ["bar_id"] = pair.Key, ["element_id"] = rebar.Id.Value, ["positive_volume_solid_count"] = count, ["solid_volume_mm3"] = volume * Math.Pow(304.8, 3), ["expected_volume_mm3"] = expectedVolume * Math.Pow(304.8, 3), ["relative_volume_error"] = volumeError });
        }
        return new JsonObject { ["status"] = "PASS", ["view_id"] = view.Id.Value, ["relative_volume_tolerance"] = 0.001, ["method"] = "Rebar.GetFullGeometryForView in Fine view; positive native solids and volume versus pi*d*d*TotalLength/4 required for every bar", ["bars"] = rows };
    }

    private static View3D EnsureView(Document document, Element host, string guid)
    {
        const string name = "灌注桩钢筋笼 · 模型核查";
        var view = new FilteredElementCollector(document).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate && v.Name == name);
        if (view == null)
        {
            var type = new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            view = View3D.CreateIsometric(document, type.Id); view.Name = name; view.DetailLevel = ViewDetailLevel.Fine; view.DisplayStyle = DisplayStyle.Shading;
            view.SetOrientation(new ViewOrientation3D(new XYZ(15, -15, 12), new XYZ(-1, 1, 2).Normalize(), new XYZ(-1, 1, -1).Normalize()));
        }
        view.DetailLevel = ViewDetailLevel.Fine;
        view.DisplayStyle = DisplayStyle.Shading;
        view.SetElementOverrides(host.Id, new OverrideGraphicSettings().SetSurfaceTransparency(85));
        foreach (var rebar in ManagedBars(document, guid).Values) rebar.SetUnobscuredInView(view, true);
        return view;
    }
    public static void DisplayCage(UIApplication ui, string guid, JsonObject report) => ShowCage(ui, Current(ui), guid, report);

    private static void ShowCage(UIApplication ui, Document document, string guid, JsonObject report)
    {
        try
        {
            var id = new ElementId(report["view_id"]!.GetValue<long>());
            if (document.GetElement(id) is View3D view) ui.ActiveUIDocument.ActiveView = view;
            var ids = ManagedBars(document, guid).Values.Select(r => r.Id).ToList(); ui.ActiveUIDocument.ShowElements(ids);
            report["display_succeeded"] = true;
        }
        catch (Exception ex) { report["display_succeeded"] = false; report["display_error"] = ex.Message; }
    }
    private sealed class LoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues) { source = FamilySource.Family; overwriteParameterValues = false; return true; }
    }
    private sealed class FailureCollector : IFailuresPreprocessor
    {
        public JsonArray Messages { get; } = new();
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool error = false;
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                Messages.Add(new JsonObject { ["severity"] = failure.GetSeverity().ToString(), ["description"] = failure.GetDescriptionText(), ["definition_guid"] = failure.GetFailureDefinitionId().Guid.ToString() });
                if (failure.GetSeverity() == FailureSeverity.Warning) failuresAccessor.DeleteWarning(failure); else error = true;
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
