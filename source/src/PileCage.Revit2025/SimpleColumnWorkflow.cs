using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PileCage;

/// <summary>The simple workflow uses the selected native column as its sole geometry source.</summary>
public static class SimpleColumnWorkflow
{
    private const string ColumnTemplate = @"__REVIT_METRIC_FAMILY_TEMPLATES__\公制结构柱.rft";
    private static readonly Guid PreparedSchemaId = new("5450F3B6-097E-4C90-924D-D751348E98BE");
    private static readonly Guid CageSchemaId = new("D71C2A12-2A7C-4DD3-9965-65B5025D7F31");
    private static double Ft(double mm) => mm / 304.8;
    private static string S(JsonObject r, string key, string fallback = "") => r[key]?.GetValue<string>() ?? fallback;
    private static Document Current(UIApplication ui) => ui.ActiveUIDocument?.Document is Document d && !d.IsFamilyDocument && !d.IsReadOnly
        ? d : throw new InvalidOperationException("请先打开可编辑的 Revit 项目。");

    public static NativeHostInfo ReadColumn(UIApplication ui, ElementId id)
    {
        var element = Current(ui).GetElement(id) ?? throw new InvalidOperationException("所选圆形柱已不存在，请重新选择。");
        if (element is not FamilyInstance || element.Category?.Id.Value != (long)BuiltInCategory.OST_StructuralColumns)
            throw new InvalidOperationException("请选择结构柱类别中的竖向圆形混凝土柱。");
        return NativeEngine.InspectHost(element);
    }

    public static CageInput DefaultInput(UIApplication ui, NativeHostInfo host)
    {
        var types = NativeEngine.GetBarTypes(ui);
        if (types.Count == 0) throw new InvalidOperationException("项目没有钢筋类型，请先准备钢筋类型。");
        NativeBarType Type(double d) => types.OrderBy(t => Math.Abs(t.Diameter - d)).ThenBy(t => t.Id).First();
        var main = Type(20); var spiral = Type(8); var ring = Type(12);
        double edge = Math.Min(100, host.Height / 10), mainEnd = host.Height - edge;
        double spiralLength = Math.Max(1, host.Height - 3 * edge);
        double firstZone = spiralLength * 4 / 9;
        return new CageInput
        {
            CageId = "圆柱配筋", CageGuid = "", SourceKind = "USER_PARAMETERS", EngineeringConfirmed = true,
            Diameter = host.Diameter, Height = host.Height, X = host.X, Y = host.Y, Z = host.Z,
            HostId = host.ElementId, HostDocumentUniqueId = Current(ui).ProjectInformation.UniqueId,
            CreateHost = false, CoverBasis = CoverBasis.OutermostSurface, Cover = 50,
            MainCount = 16, MainDiameter = main.Diameter, MainTypeId = main.Id, MainStart = edge, MainEnd = mainEnd,
            SpiralDiameter = spiral.Diameter, SpiralTypeId = spiral.Id, SpiralStart = edge,
            Zones = [new() { Id = "Z1", Length = firstZone, Pitch = 100 }, new() { Id = "Z2", Length = spiralLength - firstZone, Pitch = 200 }],
            RingsEnabled = true, RingDiameter = ring.Diameter, RingTypeId = ring.Id,
            RingStart = 2 * edge, RingEnd = host.Height - 2 * edge, RingSpacing = 1000, Tolerance = 0.5
        };
    }

    private static CageInput SelectedInput(UIApplication ui, CageInput input, bool forAnchorage)
    {
        ArgumentNullException.ThrowIfNull(input);
        var doc = Current(ui);
        if (input.HostDocumentUniqueId != doc.ProjectInformation.UniqueId)
            throw new InvalidOperationException("项目已切换，请在当前项目重新选择圆形柱。");
        var host = ReadColumn(ui, new ElementId(input.HostId));
        if (new[] { input.Diameter, input.Height, input.X, input.Y, input.Z }.Any(v => !double.IsFinite(v)) ||
            Math.Abs(input.Diameter - host.Diameter) > 0.1 || Math.Abs(input.Height - host.Height) > 0.1 ||
            Math.Abs(input.X - host.X) > 0.1 || Math.Abs(input.Y - host.Y) > 0.1 || Math.Abs(input.Z - host.Z) > 0.1)
            throw new InvalidOperationException("所选柱的几何尺寸或位置已改变，请重新选择柱以刷新参数。");
        var snapshot = input.Clone();
        snapshot.Diameter = host.Diameter; snapshot.Height = host.Height;
        snapshot.X = host.X; snapshot.Y = host.Y; snapshot.Z = host.Z;
        snapshot.HostId = host.ElementId; snapshot.HostDocumentUniqueId = doc.ProjectInformation.UniqueId;
        snapshot.CreateHost = false; snapshot.RingsEnabled = true;
        if (snapshot.MainTypeId <= 0 || snapshot.SpiralTypeId <= 0 || snapshot.RingTypeId <= 0)
            throw new InvalidOperationException("请为三类钢筋分别选择项目中的钢筋类型。");
        if (forAnchorage)
        {
            SelectedBarType(doc, snapshot.MainTypeId, snapshot.MainDiameter);
            SelectedBarType(doc, snapshot.SpiralTypeId, snapshot.SpiralDiameter);
            SelectedBarType(doc, snapshot.RingTypeId, snapshot.RingDiameter);
            // This is only the intermediate straight cage endpoint. The final anchored path is
            // derived from column top, while the user's lower endpoint is kept exactly.
            snapshot.MainEnd = snapshot.Height - snapshot.MainDiameter / 2;
        }
        return snapshot;
    }

    private static RebarBarType SelectedBarType(Document doc, long id, double expectedDiameter)
    {
        var type = doc.GetElement(new ElementId(id)) as RebarBarType ?? throw new InvalidOperationException("所选钢筋类型已不存在，请重新选择。");
        if (!double.IsFinite(expectedDiameter) || Math.Abs(type.BarNominalDiameter * 304.8 - expectedDiameter) > 0.01)
            throw new InvalidOperationException("钢筋类型的实际直径已改变，请重新选择类型以刷新参数。");
        return type;
    }

    /// <summary>API-thread read only: plans the new main bars, without requiring existing reinforcement.</summary>
    public static PileHeadUiSnapshot BuildPlannedSnapshot(UIApplication ui, CageInput input)
    {
        var snapshot = SelectedInput(ui, input, true);
        var type = SelectedBarType(Current(ui), snapshot.MainTypeId, snapshot.MainDiameter);
        if (Math.Abs((type.BarModelDiameter - type.BarNominalDiameter) * 304.8) > 0.01)
            throw new InvalidOperationException("当前锚固模块要求主筋模型直径与公称直径一致，请核对钢筋类型。");
        var plan = CagePlanner.Build(snapshot);
        var bars = plan.Bars.Where(b => b.Kind == "longitudinal").Select(b => new PileHeadBarSource(
            b.Id, new PileHeadPoint(b.Points[0].X, b.Points[0].Y, b.Points[0].Z),
            new PileHeadPoint(snapshot.X, snapshot.Y, snapshot.Z), snapshot.Z + snapshot.Height,
            snapshot.MainDiameter, type.StandardBendDiameter * 304.8)).ToList();
        return new PileHeadUiSnapshot
        {
            HostId = snapshot.HostId, HostDocumentUniqueId = snapshot.HostDocumentUniqueId,
            Diameter = snapshot.Diameter, Height = snapshot.Height, MainCount = bars.Count,
            MainDiameter = snapshot.MainDiameter, Bars = bars, SteelGrade = "",
            SourceSummary = "由当前圆柱、配筋输入与实际主筋类型计算拟建纵筋；锚固保留下端，上端按柱顶构造确定。"
        };
    }

    public static NativeResult Generate(UIApplication ui, CageInput input, PileHeadUiInput? anchorage = null, PositionPlateInput? plates = null) => GenerateInternal(ui, input, anchorage, plates, false, false);

    private static NativeResult GenerateInternal(UIApplication ui, CageInput input, PileHeadUiInput? anchorage, PositionPlateInput? plates, bool injectAfterCage, bool injectAfterPlates)
    {
        NativeResult result = new();
        JsonObject? integrated = null;
        try
        {
            var doc = Current(ui); var snapshot = SelectedInput(ui, input, anchorage != null);
            snapshot.CageGuid = Guid.NewGuid().ToString("D"); snapshot.CageId = "圆柱配筋-" + snapshot.CageGuid[..8];
            snapshot.AdditionalData.Remove("position_plates");
            PositionPlateInput? resolvedPlates = null;
            if (plates != null)
            {
                PositionPlateTypeService.ValidateSource(ui, snapshot, plates);
                // Resolve the user's installation range before anchorage extends the main bars.
                var plateBase = SelectedInput(ui, input, false);
                var mainType = SelectedBarType(doc, plateBase.MainTypeId, plateBase.MainDiameter);
                if (Math.Abs((mainType.BarModelDiameter - mainType.BarNominalDiameter) * 304.8) > 0.01)
                    throw new InvalidOperationException("定位钢片贴合计算要求主筋模型直径与公称直径一致，请核对钢筋类型。");
                resolvedPlates = PositionPlatePlanner.Resolve(plateBase, plates.Clone());
                var platePlan = PositionPlatePlanner.Build(plateBase, resolvedPlates);
                if (Ft(resolvedPlates.WidthMm) <= ui.Application.ShortCurveTolerance ||
                    platePlan.Profile.Select((a, i) => (A: a, B: platePlan.Profile[(i + 1) % platePlan.Profile.Count]))
                        .Any(e => Ft(Math.Sqrt(Math.Pow(e.A.Radial - e.B.Radial, 2) + Math.Pow(e.A.Elevation - e.B.Elevation, 2))) <= ui.Application.ShortCurveTolerance))
                    throw new InvalidOperationException("定位钢片边长或板宽短于Revit最小曲线容差，请调整折形尺寸。");
                snapshot.AdditionalData["position_plates"] = JsonSerializer.SerializeToElement(resolvedPlates, NativeEngine.JsonOptions);
            }
            if (anchorage == null && resolvedPlates == null)
            {
                result = NativeEngine.Apply(ui, snapshot);
                if (result.Success) input.CageGuid = result.CageGuid;
                return result;
            }

            integrated = new JsonObject
            {
                ["status"] = "FAIL", ["operation"] = "simple_generate_with_optional_details", ["transaction_committed"] = false,
                ["cage_guid"] = snapshot.CageGuid, ["host_id"] = snapshot.HostId, ["document_unique_id"] = snapshot.HostDocumentUniqueId,
                ["input"] = JsonSerializer.SerializeToNode(input, NativeEngine.JsonOptions),
                ["intermediate_cage_input"] = JsonSerializer.SerializeToNode(snapshot, NativeEngine.JsonOptions),
                ["main_endpoint_convention"] = anchorage == null ? "The user's lower and upper main bar endpoints are preserved." : "MainStart is preserved. MainEnd is replaced by the column-top anchorage path; the straight cage endpoint is intermediate only."
            };
            result.CageGuid = snapshot.CageGuid; result.HostId = snapshot.HostId; result.Details = integrated;
            PileHeadUiInput? head = anchorage?.Clone();
            if (head != null)
            {
                if ((head.HostId != 0 && head.HostId != snapshot.HostId) ||
                    (!string.IsNullOrWhiteSpace(head.HostDocumentUniqueId) && head.HostDocumentUniqueId != snapshot.HostDocumentUniqueId))
                    throw new InvalidOperationException("锚固设置属于其他圆柱或项目，请重新打开锚固设置。");
                head.HostId = snapshot.HostId; head.HostDocumentUniqueId = snapshot.HostDocumentUniqueId;
                var planned = BuildPlannedSnapshot(ui, snapshot);
                var calculation = PileHeadCalculation.Calculate(planned, head);
                if (calculation.Plan.Bars.Any(b => b.Segments.Any(s => Ft(s.Length) <= ui.Application.ShortCurveTolerance)))
                    throw new ArgumentException("锚固曲线短于Revit最小曲线容差，请调整过度接近90°的外斜角，或采用直锚。");
                integrated["anchorage"] = JsonSerializer.SerializeToNode(head, NativeEngine.JsonOptions);
                integrated["preflight"] = calculation.Preview.DeepClone();
            }
            var findings = RuleChecker.Check(snapshot, RuleStore.Load());
            if (findings.Any(f => f.Severity is "Block" or "Warn"))
                throw new ArgumentException(string.Join("\n", findings.Where(f => f.Severity is "Block" or "Warn").Select(f => f.Message)));
            if (resolvedPlates != null)
            {
                integrated["position_plates"] = JsonSerializer.SerializeToNode(resolvedPlates, NativeEngine.JsonOptions);
                integrated["position_plate_preflight"] = PositionPlatePlanner.Preview(snapshot, resolvedPlates);
            }
            var beforeIds = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(b => b.GetHostId().Value == snapshot.HostId).Select(b => b.UniqueId).Order().ToArray();
            integrated["prior_host_rebar_unique_ids"] = JsonSerializer.SerializeToNode(beforeIds);
            using var group = new TransactionGroup(doc, "圆形柱配筋与附加构造统一生成"); group.Start();
            try
            {
                var cageResult = NativeEngine.Apply(ui, snapshot, deferDisplay: true, deferReport: true);
                integrated["cage_creation"] = cageResult.Details.DeepClone();
                if (!cageResult.Success) throw new InvalidOperationException(cageResult.Message);
                if (injectAfterCage)
                {
                    integrated["failure_injection"] = "after_successful_cage_before_optional_details";
                    throw new InvalidOperationException("INJECTED_FAILURE_AFTER_CAGE：受控验收故障，用于核查三类钢筋及其事务已整体回滚。");
                }
                var created = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(b => S(CageTag(b), "cage_guid") == snapshot.CageGuid).ToArray();
                var mains = created.Where(b => S(CageTag(b), "kind") == "longitudinal").OrderBy(b => b.Id.Value).ToArray();
                if (mains.Length != snapshot.MainCount || created.Any(b => b.GetHostId().Value != snapshot.HostId))
                    throw new InvalidOperationException("本次新建钢筋的数量或宿主不一致，已停止并回滚。");
                integrated["new_main_element_ids"] = JsonSerializer.SerializeToNode(mains.Select(b => b.Id.Value).ToArray());
                long viewId = cageResult.Details["view_id"]?.GetValue<long>() ?? 0;
                if (head != null)
                {
                    var headResult = PileHeadWorkflow.Generate(ui, head, mains.Select(b => b.Id.Value).ToArray(), snapshot.CageGuid, deferDisplay: true);
                    integrated["anchorage_creation"] = headResult;
                    if (S(headResult, "status") != "PASS") throw new InvalidOperationException(S(headResult, "message", "锚固生成失败。"));
                    viewId = headResult["readback"]?["view_id"]?.GetValue<long>() ?? viewId;
                }
                if (resolvedPlates != null)
                {
                    var plateResult = PositionPlateWorkflow.Generate(ui, snapshot, resolvedPlates, viewId);
                    integrated["position_plate_creation"] = plateResult;
                    if (S(plateResult, "status") != "PASS") throw new InvalidOperationException(S(plateResult, "message", "定位钢片生成失败。"));
                    integrated["plate_count"] = plateResult["plate_count"]?.DeepClone();
                    if (injectAfterPlates)
                    {
                        integrated["failure_injection"] = "after_successful_position_plates_before_outer_commit";
                        throw new InvalidOperationException("INJECTED_FAILURE_AFTER_PLATES：受控验收故障，用于核查钢片、钢筋、附加构造及其记录已整体回滚。");
                    }
                }
                if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("钢筋与附加构造统一事务组未提交。");
                result.Success = true; result.MainCount = cageResult.MainCount; result.SpiralCount = cageResult.SpiralCount; result.RingCount = cageResult.RingCount;
                result.Message = $"已同时生成纵筋{result.MainCount}根、螺旋筋{result.SpiralCount}个、加劲箍{result.RingCount}道" + (head != null ? "、所选锚固构造" : "") + (resolvedPlates != null ? $"、定位钢片{integrated["plate_count"]}片" : "") + "。";
                integrated["status"] = "PASS"; integrated["transaction_committed"] = true;
                integrated["view_id"] = viewId;
                input.CageGuid = snapshot.CageGuid;
                try { if (head != null) PileHeadWorkflow.Show(ui, snapshot.HostId, snapshot.CageGuid); else NativeEngine.DisplayCage(ui, snapshot.CageGuid, integrated); }
                catch (Exception ex) { integrated["display_error"] = ex.Message; }
            }
            catch
            {
                if (group.GetStatus() == TransactionStatus.Started) integrated["rollback_status"] = group.RollBack().ToString();
                integrated["transaction_committed"] = group.GetStatus() == TransactionStatus.Committed;
                var after = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(b => b.GetHostId().Value == snapshot.HostId).ToArray();
                integrated["rollback_original_rebar_ids_preserved"] = beforeIds.SequenceEqual(after.Select(b => b.UniqueId).Order());
                integrated["rollback_new_cage_bar_count"] = after.Count(b => S(CageTag(b), "cage_guid") == snapshot.CageGuid);
                integrated["rollback_head_record_exists"] = PileHeadWorkflow.HasScope(ui, snapshot.HostId, snapshot.CageGuid);
                integrated["rollback_plate_record_exists"] = PositionPlateWorkflow.HasExpectedRecord(ui, snapshot.CageGuid);
                foreach (string stage in new[] { "cage_creation", "anchorage_creation", "position_plate_creation" })
                    if (integrated[stage] is JsonObject detail) { detail["outer_transaction_committed"] = false; detail["outer_transaction_rolled_back"] = group.GetStatus() == TransactionStatus.RolledBack; }
                throw;
            }
        }
        catch (Exception ex)
        {
            result.Success = false; result.Message = ex.Message;
            result.Details = integrated ?? new JsonObject { ["transaction_committed"] = false };
            result.Details["status"] = "FAIL"; result.Details["error"] = ex.ToString(); result.Details["message"] = ex.Message;
        }
        if (integrated != null)
        {
            integrated["utc"] = DateTime.UtcNow.ToString("o"); integrated["message"] = result.Message;
            integrated["assembly_path"] = typeof(SimpleColumnWorkflow).Assembly.Location;
            integrated["assembly_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SimpleColumnWorkflow).Assembly.Location)));
            try
            {
                var directory = Path.Combine(App.Runtime, "reports"); Directory.CreateDirectory(directory);
                result.ReportPath = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_INTEGRATED_" + (result.Success ? "PASS" : "FAIL") + ".json");
                integrated["report_path"] = result.ReportPath;
                File.WriteAllText(result.ReportPath, integrated.ToJsonString(NativeEngine.JsonOptions));
            }
            catch (Exception ex) { result.ReportPath = ""; integrated["report_write_error"] = ex.Message; }
        }
        return result;
    }

    public static JsonObject Run(UIApplication ui, JsonObject request)
    {
        var operation = S(request, "operation");
        try
        {
            JsonObject response;
            if (operation == "simple_prepare") response = Prepare(ui, request);
            else if (operation == "simple_reopen") response = Reopen(ui, request);
            else if (operation == "simple_inspect") response = Inspect(ui, request);
            else if (operation == "simple_generate")
            {
                ControlledDocument(ui);
                var p = request["input"]?.Deserialize<CageInput>(NativeEngine.JsonOptions);
                if (p == null) p = DefaultInput(ui, ReadColumn(ui, new ElementId(HostId(ui, request))));
                var anchorage = request["anchorage"]?.Deserialize<PileHeadUiInput>(NativeEngine.JsonOptions);
                var plates = request["position_plates"]?.Deserialize<PositionPlateInput>(NativeEngine.JsonOptions);
                bool injectAfterCage = request["inject_after_cage"]?.GetValue<bool>() == true;
                bool injectAfterPlates = request["inject_after_plates"]?.GetValue<bool>() == true;
                if ((injectAfterCage || injectAfterPlates) && (!File.Exists(Path.Combine(App.Runtime, "enable-test-queue.flag")) || (anchorage == null && plates == null) || (injectAfterPlates && plates == null)))
                    throw new InvalidOperationException("生成后故障注入只用于显式启用测试队列、配置相应附加构造的受控验证项目。");
                response = Result(GenerateInternal(ui, p, anchorage, plates, injectAfterCage, injectAfterPlates));
            }
            else if (operation == "simple_plate_missing_test")
            {
                ControlledDocument(ui);
                if (!File.Exists(Path.Combine(App.Runtime, "enable-test-queue.flag"))) throw new InvalidOperationException("钢片缺失验收仅用于显式启用测试队列的受控项目。");
                response = PositionPlateWorkflow.MissingTest(ui, S(request, "cage_guid"), request["remove_record"]?.GetValue<bool>() == true);
            }
            else if (operation == "simple_read") response = Result(NativeEngine.Read(ui, S(request, "cage_guid")));
            else if (operation == "simple_delete_generated") response = DeleteGenerated(ui, request);
            else if (operation == "simple_view") response = PrepareView(ui, request);
            else if (operation == "simple_save") response = Save(ui, request);
            else throw new ArgumentException("未知圆柱配筋操作：" + operation);
            response["operation"] = operation;
            response["utc"] = DateTime.UtcNow.ToString("o");
            var assembly = typeof(SimpleColumnWorkflow).Assembly;
            response["assembly_path"] = assembly.Location;
            response["assembly_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
            return response;
        }
        catch (Exception ex) { return new JsonObject { ["status"] = "FAIL", ["operation"] = operation, ["error"] = ex.ToString(), ["message"] = ex.Message }; }
    }

    private static JsonObject Result(NativeResult r)
    {
        var response = (JsonObject)r.Details.DeepClone(); response["status"] = r.Success ? "PASS" : "FAIL";
        response["message"] = r.Message; response["cage_guid"] = r.CageGuid; response["report_path"] = r.ReportPath;
        response["main_count"] = r.MainCount; response["spiral_count"] = r.SpiralCount; response["ring_count"] = r.RingCount;
        response["host_id"] = r.HostId;
        return response;
    }

    private static string WorkspacePath(string path)
    {
        var result = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(App.Root, path));
        if (!result.StartsWith(App.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("测试文件必须保存在当前工作区内。");
        return result;
    }

    private static Schema PreparedSchema()
    {
        var s = Schema.Lookup(PreparedSchemaId); if (s != null) return s;
        var b = new SchemaBuilder(PreparedSchemaId); b.SetSchemaName("SimpleColumnPreparedModelV1");
        b.SetReadAccessLevel(AccessLevel.Public); b.SetWriteAccessLevel(AccessLevel.Public); b.AddSimpleField("record", typeof(string)); return b.Finish();
    }

    private static JsonObject PreparedTag(Document doc)
    {
        var schema = Schema.Lookup(PreparedSchemaId); if (schema == null) return new();
        var entity = doc.ProjectInformation.GetEntity(schema);
        return entity.IsValid() ? JsonNode.Parse(entity.Get<string>("record"))!.AsObject() : new();
    }

    private static Document ControlledDocument(UIApplication ui)
    {
        var doc = Current(ui); WorkspacePath(doc.PathName);
        if (string.IsNullOrWhiteSpace(doc.PathName) || PreparedTag(doc)["purpose"]?.GetValue<string>() != "simple_column_validation")
            throw new InvalidOperationException("自动验证操作仅适用于本次创建并保存的圆柱验证项目。");
        return doc;
    }

    private static long HostId(UIApplication ui, JsonObject request)
    {
        if (request["host_id"] != null) return request["host_id"]!.GetValue<long>();
        var selected = ui.ActiveUIDocument.Selection.GetElementIds().Where(id => Current(ui).GetElement(id)?.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns).ToArray();
        if (selected.Length == 1) return selected[0].Value;
        return PreparedTag(Current(ui))["host_id"]?.GetValue<long>() ?? throw new InvalidOperationException("请指定或选择圆形柱。");
    }

    private static JsonObject Inspect(UIApplication ui, JsonObject request)
    {
        var host = ReadColumn(ui, new ElementId(HostId(ui, request)));
        if (request["select"]?.GetValue<bool>() == true) ui.ActiveUIDocument.Selection.SetElementIds([new ElementId(host.ElementId)]);
        var p = DefaultInput(ui, host);
        return new JsonObject
        {
            ["status"] = "PASS", ["document_path"] = Current(ui).PathName, ["document_unique_id"] = Current(ui).ProjectInformation.UniqueId,
            ["host"] = JsonSerializer.SerializeToNode(host, NativeEngine.JsonOptions), ["host_category"] = "OST_StructuralColumns",
            ["bar_types"] = JsonSerializer.SerializeToNode(NativeEngine.GetBarTypes(ui), NativeEngine.JsonOptions),
            ["default_input"] = JsonSerializer.SerializeToNode(p, NativeEngine.JsonOptions),
            ["native_rebar_count"] = new FilteredElementCollector(Current(ui)).OfClass(typeof(Rebar)).GetElementCount()
        };
    }

    private static JsonObject Prepare(UIApplication ui, JsonObject request)
    {
        var path = WorkspacePath(S(request, "save_as", "runtime/models/CircularColumn-Ready-Revit2025.rvt"));
        if (File.Exists(path)) throw new InvalidOperationException("准备文件已存在，拒绝覆盖：" + path);
        double d = request["diameter"]?.GetValue<double>() ?? 1000, h = request["height"]?.GetValue<double>() ?? 3000;
        double x = request["x"]?.GetValue<double>() ?? 5000, y = request["y"]?.GetValue<double>() ?? 4000, z = request["z"]?.GetValue<double>() ?? 0;
        if (new[] { d, h, x, y, z }.Any(v => !double.IsFinite(v)) || d <= 0 || h <= 0) throw new ArgumentException("准备模型的尺寸和位置无效。");
        var barDiameters = new List<double> { 8, 10, 12, 16, 20, 22, 25 };
        double? requestedMainDiameter = request["main_diameter"]?.GetValue<double>();
        if (requestedMainDiameter.HasValue) barDiameters.Add(requestedMainDiameter.Value);
        if (request["bar_diameters"] is JsonArray requestedDiameters)
            barDiameters.AddRange(requestedDiameters.Select(n => n?.GetValue<double>() ?? throw new ArgumentException("钢筋直径数组不能包含空值。")));
        if (barDiameters.Any(value => !double.IsFinite(value) || value <= 0)) throw new ArgumentException("准备的钢筋直径必须是有限正数。");
        barDiameters = barDiameters.Distinct().OrderBy(value => value).ToList();
        if (!File.Exists(ColumnTemplate)) throw new FileNotFoundException("找不到结构柱族模板。", ColumnTemplate);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var doc = ui.Application.NewProjectDocument(UnitSystem.Metric);
        bool closed = false;
        try
        {
            var symbol = BuildColumnFamily(ui, doc, d, h);
            long hostId; long viewId;
            using (var t = new Transaction(doc, "准备圆形结构柱及钢筋类型"))
            {
                t.Start();
                var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => Math.Abs(l.Elevation - Ft(z)) < 1e-7) ?? Level.Create(doc, Ft(z));
                if (!symbol.IsActive) symbol.Activate();
                var instance = doc.Create.NewFamilyInstance(new XYZ(Ft(x), Ft(y), Ft(z)), symbol, level, StructuralType.Column);
                var offset = instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                if (offset != null && !offset.IsReadOnly) offset.Set(0.0);
                var topLevel = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                if (topLevel != null && !topLevel.IsReadOnly) topLevel.Set(level.Id);
                var topOffset = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                if (topOffset != null && !topOffset.IsReadOnly) topOffset.Set(Ft(h));
                var coverType = RebarCoverType.Create(doc, "圆柱保护层 50 mm", Ft(50));
                foreach (var parameter in new[] { BuiltInParameter.CLEAR_COVER_OTHER, BuiltInParameter.CLEAR_COVER_TOP, BuiltInParameter.CLEAR_COVER_BOTTOM })
                {
                    var cover = instance.get_Parameter(parameter);
                    if (cover != null && !cover.IsReadOnly) cover.Set(coverType.Id);
                }
                doc.Regenerate();
                var host = NativeEngine.InspectHost(instance);
                if (instance.Category?.Id.Value != (long)BuiltInCategory.OST_StructuralColumns || Math.Abs(host.Diameter - d) > 0.1 || Math.Abs(host.Height - h) > 0.1 || Math.Abs(host.Z - z) > 0.1)
                    throw new InvalidOperationException("准备的结构柱实体与指定几何尺寸不一致。");
                hostId = instance.Id.Value;
                foreach (double diameter in barDiameters)
                {
                    var type = RebarBarType.Create(doc); type.Name = $"圆柱钢筋 d{diameter:0.###}";
                    type.BarNominalDiameter = Ft(diameter); type.BarModelDiameter = Ft(diameter);
                    type.StandardBendDiameter = Ft(4 * diameter); type.StirrupTieBendDiameter = Ft(4 * diameter);
                }
                var vt = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                var view = View3D.CreateIsometric(doc, vt.Id); view.Name = "圆形柱 · 选柱配筋"; view.DetailLevel = ViewDetailLevel.Fine; view.DisplayStyle = DisplayStyle.Shading;
                view.SetOrientation(new ViewOrientation3D(new XYZ(25, -15, 15), new XYZ(-1, 1, 2).Normalize(), new XYZ(-1, 1, -1).Normalize()));
                viewId = view.Id.Value;
                var entity = new Entity(PreparedSchema());
                entity.Set("record", new JsonObject { ["purpose"] = "simple_column_validation", ["host_id"] = hostId, ["created_utc"] = DateTime.UtcNow.ToString("o") }.ToJsonString());
                doc.ProjectInformation.SetEntity(entity);
                t.Commit();
            }
            doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false });
            doc.Close(false); closed = true;
            var uidoc = ui.OpenAndActivateDocument(path);
            uidoc.ActiveView = (View)uidoc.Document.GetElement(new ElementId(viewId));
            uidoc.Selection.SetElementIds([new ElementId(hostId)]); uidoc.ShowElements(new ElementId(hostId));
            uidoc.Document.Save();
            var response = Inspect(ui, new JsonObject { ["host_id"] = hostId });
            if (requestedMainDiameter.HasValue)
            {
                var main = NativeEngine.GetBarTypes(ui).First(type => Math.Abs(type.Diameter - requestedMainDiameter.Value) < 0.001);
                var defaults = response["default_input"]!.AsObject();
                defaults["MainDiameter"] = main.Diameter; defaults["MainTypeId"] = main.Id;
                response["requested_main_diameter_mm"] = requestedMainDiameter.Value;
            }
            response["saved_path"] = path; response["save_succeeded"] = true; response["prepared_empty_rebar_model"] = true;
            return response;
        }
        catch { if (!closed && doc.IsValidObject) doc.Close(false); throw; }
    }

    private static FamilySymbol BuildColumnFamily(UIApplication ui, Document project, double diameter, double height)
    {
        var familyDoc = ui.Application.NewFamilyDocument(ColumnTemplate);
        try
        {
            using (var t = new Transaction(familyDoc, "创建圆形混凝土结构柱族"))
            {
                t.Start(); familyDoc.FamilyManager.NewType($"D{diameter:0.###} H{height:0.###}");
                var materialKind = familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_STRUCT_MATERIAL_TYPE);
                if (materialKind != null && !materialKind.IsReadOnly) materialKind.Set((int)StructuralMaterialType.Concrete);
                var canHost = familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_CAN_HOST_REBAR);
                if (canHost != null && !canHost.IsReadOnly) canHost.Set(1);
                var matId = Material.Create(familyDoc, "圆柱验证混凝土"); var mat = (Material)familyDoc.GetElement(matId);
                mat.MaterialClass = "Concrete"; mat.Color = new Color(180, 185, 190);
                mat.StructuralAssetId = PropertySetElement.Create(familyDoc, new StructuralAsset("圆柱验证混凝土物理资产", StructuralAssetClass.Concrete)).Id;
                var sketch = SketchPlane.Create(familyDoc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                var circle = new CurveArray(); circle.Append(Arc.Create(XYZ.Zero, Ft(diameter / 2), 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                circle.Append(Arc.Create(XYZ.Zero, Ft(diameter / 2), Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                var profile = new CurveArrArray(); profile.Append(circle);
                var body = familyDoc.FamilyCreate.NewExtrusion(true, profile, sketch, Ft(height));
                body.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(matId);
                var structuralMaterial = familyDoc.FamilyManager.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                if (structuralMaterial != null && !structuralMaterial.IsReadOnly) familyDoc.FamilyManager.Set(structuralMaterial, matId);
                t.Commit();
            }
            var familyPath = WorkspacePath("runtime/families/SimpleCircularColumn_" + Guid.NewGuid().ToString("N")[..10] + ".rfa");
            Directory.CreateDirectory(Path.GetDirectoryName(familyPath)!);
            familyDoc.SaveAs(familyPath, new SaveAsOptions { OverwriteExistingFile = false });
            var family = familyDoc.LoadFamily(project, new LoadOptions());
            return (FamilySymbol)project.GetElement(family.GetFamilySymbolIds().First());
        }
        finally { familyDoc.Close(false); }
    }

    private static JsonObject CageTag(Element e)
    {
        var schema = Schema.Lookup(CageSchemaId); if (schema == null) return new();
        var entity = e.GetEntity(schema); return entity.IsValid() ? JsonNode.Parse(entity.Get<string>("record"))!.AsObject() : new();
    }

    private static JsonObject DeleteGenerated(UIApplication ui, JsonObject request)
    {
        var doc = ControlledDocument(ui); string guid = S(request, "cage_guid");
        if (!Guid.TryParse(guid, out _)) throw new ArgumentException("必须指定本次验证钢筋的 cage_guid。");
        var bars = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(r => CageTag(r)["cage_guid"]?.GetValue<string>() == guid).ToArray();
        long hostId = PreparedTag(doc)["host_id"]!.GetValue<long>();
        if (bars.Any(r => r.GetHostId().Value != hostId)) throw new InvalidOperationException("钢筋不属于本次准备的柱，拒绝删除。");
        var allowed = bars.Select(b => b.Id.Value).ToHashSet();
        long[] deletedIds;
        using (var t = new Transaction(doc, "验证：手动删除本次生成的钢筋"))
        {
            t.Start(); deletedIds = bars.Length == 0 ? [] : doc.Delete(bars.Select(b => b.Id).ToList()).Select(id => id.Value).ToArray();
            if (deletedIds.Any(id => !allowed.Contains(id))) { t.RollBack(); throw new InvalidOperationException("删除会影响钢筋以外的依赖对象，已回滚。"); }
            t.Commit();
        }
        return new JsonObject { ["status"] = "PASS", ["cage_guid"] = guid, ["deleted_count"] = deletedIds.Length, ["deleted_ids"] = JsonSerializer.SerializeToNode(deletedIds), ["host_preserved"] = doc.GetElement(new ElementId(hostId)) != null, ["remaining_rebar_count"] = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount(), ["metadata_left_as_after_manual_deletion"] = true };
    }

    private static JsonObject Save(UIApplication ui, JsonObject request)
    {
        var doc = ControlledDocument(ui); string saveAs = S(request, "save_as");
        if (!string.IsNullOrWhiteSpace(saveAs))
        {
            string path = WorkspacePath(saveAs); if (File.Exists(path)) throw new InvalidOperationException("另存目标已存在，拒绝覆盖：" + path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false });
        }
        else doc.Save();
        return new JsonObject { ["status"] = "PASS", ["saved_path"] = doc.PathName, ["save_succeeded"] = true, ["native_rebar_count"] = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount() };
    }

    private static JsonObject PrepareView(UIApplication ui, JsonObject request)
    {
        var doc = ControlledDocument(ui); var host = ReadColumn(ui, new ElementId(HostId(ui, request)));
        string export = S(request, "export_path"); string? prefix = null;
        if (!string.IsNullOrWhiteSpace(export))
        {
            var resolved = WorkspacePath(export);
            prefix = resolved.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? resolved[..^4] : resolved;
            Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
            if (Directory.GetFiles(Path.GetDirectoryName(prefix)!, Path.GetFileName(prefix) + "*.png").Length > 0)
                throw new InvalidOperationException("图像目标已存在，拒绝覆盖：" + prefix);
        }
        var bars = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Where(r => r.GetHostId().Value == host.ElementId).ToArray();
        View3D view;
        double margin = host.Diameter / 2;
        var box = new BoundingBoxXYZ
        {
            Min = new XYZ(Ft(host.X - host.Diameter / 2 - margin), Ft(host.Y - host.Diameter / 2 - margin), Ft(host.Z - margin)),
            Max = new XYZ(Ft(host.X + host.Diameter / 2 + margin), Ft(host.Y + host.Diameter / 2 + margin), Ft(host.Z + host.Height + margin))
        };
        using (var t = new Transaction(doc, "设置圆柱钢筋核查视图"))
        {
            t.Start();
            view = ui.ActiveUIDocument.ActiveView as View3D ?? new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective)!;
            if (view == null)
            {
                var vt = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                view = View3D.CreateIsometric(doc, vt.Id); view.Name = "圆柱钢筋 · 核查视图";
            }
            if (view.IsPerspective) throw new InvalidOperationException("核查视图需使用正投影三维视图。");
            view.DetailLevel = ViewDetailLevel.Fine; view.DisplayStyle = DisplayStyle.Shading;
            view.SetOrientation(new ViewOrientation3D(new XYZ(Ft(host.X + 3 * host.Diameter), Ft(host.Y - 3 * host.Diameter), Ft(host.Z + host.Height)), new XYZ(-1, 1, 2).Normalize(), new XYZ(-1, 1, -1).Normalize()));
            view.SetSectionBox(box); view.IsSectionBoxActive = true;
            foreach (var category in new[] { BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids })
            {
                var id = new ElementId(category); if (view.CanCategoryBeHidden(id)) view.SetCategoryHidden(id, true);
            }
            view.SetElementOverrides(new ElementId(host.ElementId), new OverrideGraphicSettings().SetSurfaceTransparency(85));
            var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
            foreach (var bar in bars)
            {
                var kind = CageTag(bar)["kind"]?.GetValue<string>();
                var color = kind == "helix" ? new Color(215, 113, 25) : kind == "ring" ? new Color(37, 143, 85) : new Color(43, 92, 164);
                var graphics = new OverrideGraphicSettings().SetProjectionLineColor(color).SetSurfaceTransparency(0);
                if (solid != null) graphics.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(color);
                view.SetElementOverrides(bar.Id, graphics); bar.SetUnobscuredInView(view, true);
            }
            t.Commit();
        }
        ui.ActiveUIDocument.ActiveView = view;
        ui.ActiveUIDocument.Selection.SetElementIds(new List<ElementId>());
        ui.ActiveUIDocument.RefreshActiveView();
        var uiView = ui.ActiveUIDocument.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
        if (uiView == null) throw new InvalidOperationException("无法找到当前三维视图窗口。");
        // Compute the view-plane rectangle from all eight section-box corners; model XYZ minima
        // alone can project onto the same horizontal point in an isometric view.
        var center = (box.Min + box.Max) / 2; var right = view.RightDirection; var up = view.UpDirection;
        var corners = new List<XYZ>();
        foreach (double x in new[] { box.Min.X, box.Max.X }) foreach (double y in new[] { box.Min.Y, box.Max.Y }) foreach (double z in new[] { box.Min.Z, box.Max.Z }) corners.Add(new XYZ(x, y, z));
        var horizontal = corners.Select(p => (p - center).DotProduct(right)).ToArray();
        var vertical = corners.Select(p => (p - center).DotProduct(up)).ToArray();
        uiView.ZoomAndCenterRectangle(center + right * horizontal.Min() + up * vertical.Min(), center + right * horizontal.Max() + up * vertical.Max());
        ui.ActiveUIDocument.RefreshActiveView();
        var response = new JsonObject { ["status"] = "PASS", ["view_id"] = view.Id.Value, ["host_id"] = host.ElementId, ["styled_rebar_count"] = bars.Length, ["host_transparency"] = 85, ["section_box_active"] = view.IsSectionBoxActive, ["colors"] = new JsonObject { ["longitudinal"] = "blue", ["helix"] = "orange", ["ring"] = "green" } };
        if (prefix != null)
        {
            using var options = new ImageExportOptions
            {
                FilePath = prefix, ExportRange = ExportRange.VisibleRegionOfCurrentView,
                ZoomType = ZoomFitType.FitToPage, PixelSize = 1600, FitDirection = FitDirectionType.Vertical,
                HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150
            };
            doc.ExportImage(options);
            var files = Directory.GetFiles(Path.GetDirectoryName(prefix)!, Path.GetFileName(prefix) + "*.png");
            if (files.Length == 0) throw new InvalidOperationException("Revit 图像导出未产生 PNG 文件。");
            response["exported_files"] = JsonSerializer.SerializeToNode(files); response["export_source"] = "Revit native 3D view";
        }
        return response;
    }

    private static JsonObject Reopen(UIApplication ui, JsonObject request)
    {
        string path = WorkspacePath(S(request, "path"));
        if (!File.Exists(path)) throw new FileNotFoundException("待重开的模型不存在。", path);
        var active = ui.ActiveUIDocument?.Document;
        var loaded = ui.Application.Documents.Cast<Document>().FirstOrDefault(d => !d.IsFamilyDocument && string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));
        if (loaded != null && PreparedTag(loaded)["purpose"]?.GetValue<string>() != "simple_column_validation")
            throw new InvalidOperationException("重开验证只能关闭本次准备的圆柱验证模型。");
        if (loaded?.IsModified == true) throw new InvalidOperationException("请先保存当前模型，再执行关闭重开验证。");
        // Revit may return different managed Document wrappers for the same native
        // document. Match its saved path, never object reference identity.
        if (loaded != null && active != null && string.Equals(active.PathName, path, StringComparison.OrdinalIgnoreCase))
        {
            ControlledDocument(ui);
            // Revit cannot close its active document: switch to a separately saved scratch document first.
            var staging = WorkspacePath("runtime/models/SimpleColumn-ReopenStage-" + Guid.NewGuid().ToString("N")[..8] + ".rvt");
            var empty = ui.Application.NewProjectDocument(UnitSystem.Metric); empty.SaveAs(staging, new SaveAsOptions { OverwriteExistingFile = false }); empty.Close(false);
            var switchDoc = ui.OpenAndActivateDocument(staging).Document;
            if (!string.Equals(ui.ActiveUIDocument?.Document.PathName, staging, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("临时项目未成为活动文档，已停止关闭模型。");
            if (!loaded.Close(false)) throw new InvalidOperationException("验证模型未成功关闭，不能判定为磁盘重开。");
            ui.OpenAndActivateDocument(path);
            if (!string.Equals(ui.ActiveUIDocument?.Document.PathName, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("验证模型未成为活动文档，已停止关闭临时项目。");
            switchDoc.Close(false);
        }
        else
        {
            if (loaded != null && !loaded.Close(false)) throw new InvalidOperationException("验证模型未成功关闭，不能判定为磁盘重开。");
            ui.OpenAndActivateDocument(path);
        }
        ControlledDocument(ui);
        var response = Inspect(ui, request); response["reopened_path"] = path; response["disk_reopened"] = true;
        return response;
    }

    private sealed class LoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues) { source = FamilySource.Family; overwriteParameterValues = false; return true; }
    }
}
