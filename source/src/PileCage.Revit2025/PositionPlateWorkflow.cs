using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PileCage;

public static class PositionPlateWorkflow
{
    private static readonly Guid PlateSchemaId = new("32E42C8B-4F67-4D86-BEDB-C9B06592BEB1");
    private static readonly Guid CageSchemaId = new("D71C2A12-2A7C-4DD3-9965-65B5025D7F31");
    private static double Ft(double mm) => mm / 304.8;
    private static double Mm(double ft) => ft * 304.8;
    private static XYZ X(Point3 p) => new(Ft(p.X), Ft(p.Y), Ft(p.Z));
    private static Point3 P(XYZ p) => new(Mm(p.X), Mm(p.Y), Mm(p.Z));
    private static JsonNode? J<T>(T value) => JsonSerializer.SerializeToNode(value, NativeEngine.JsonOptions);
    private static JsonObject Evidence(JsonObject report)
    {
        report["utc"] = DateTime.UtcNow.ToString("o");
        report["assembly_path"] = typeof(PositionPlateWorkflow).Assembly.Location;
        report["assembly_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PositionPlateWorkflow).Assembly.Location)));
        return report;
    }
    private static string S(JsonNode? value, string key) => value?[key]?.GetValue<string>() ?? "";
    private static Document Doc(UIApplication ui) => ui.ActiveUIDocument?.Document ?? throw new InvalidOperationException("请打开 Revit 项目。");
    private static JsonObject Tag(Element e, Guid id)
    {
        var schema = Schema.Lookup(id); if (schema == null) return new();
        var entity = e.GetEntity(schema); return entity.IsValid() ? JsonNode.Parse(entity.Get<string>("record"))!.AsObject() : new();
    }
    private static void SetTag(Element e, JsonObject data)
    {
        var schema = Schema.Lookup(PlateSchemaId);
        if (schema == null)
        {
            var b = new SchemaBuilder(PlateSchemaId); b.SetSchemaName("CircularColumnPositionPlateV1"); b.SetReadAccessLevel(AccessLevel.Public); b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField("record", typeof(string)); schema = b.Finish();
        }
        var entity = new Entity(schema); entity.Set("record", data.ToJsonString(NativeEngine.JsonOptions)); e.SetEntity(entity);
    }
    private static DataStorage[] Records(Document doc, string guid) => new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
        .Where(e => S(Tag(e, PlateSchemaId), "cage_guid") == guid && S(Tag(e, PlateSchemaId), "role") == "record").ToArray();
    private static DirectShape[] Plates(Document doc, string guid) => new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
        .Where(e => S(Tag(e, PlateSchemaId), "cage_guid") == guid && S(Tag(e, PlateSchemaId), "role") == "position_plate").ToArray();
    public static bool HasExpectedRecord(UIApplication ui, string cageGuid) => Records(Doc(ui), cageGuid).Length != 0;
    private static Rebar Main(Document doc, CageInput cage, int index)
    {
        string id = $"M{index + 1:D3}";
        return new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().Single(b => S(Tag(b, CageSchemaId), "cage_guid") == cage.CageGuid && S(Tag(b, CageSchemaId), "bar_id") == id);
    }
    public static JsonObject Generate(UIApplication ui, CageInput cage, PositionPlateInput input, long viewId)
    {
        var report = new JsonObject { ["status"] = "FAIL", ["transaction_committed"] = false, ["cage_guid"] = cage.CageGuid };
        try
        {
            var doc = Doc(ui); var plan = PositionPlatePlanner.Build(cage, input);
            if (Records(doc, cage.CageGuid).Length != 0 || Plates(doc, cage.CageGuid).Length != 0) throw new InvalidOperationException("本次钢筋笼已有定位钢片记录，不能重复创建。");
            using (var t = new Transaction(doc, "创建本次钢筋笼定位钢片"))
            {
                t.Start(); t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(new PlateFailures()).SetClearAfterRollback(true));
                var allMaterials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToArray();
                var material = allMaterials.FirstOrDefault(m => m.Name == "定位钢片_钢材" && m.MaterialClass == "Steel");
                if (material == null)
                {
                    string materialName = allMaterials.Any(m => m.Name == "定位钢片_钢材") ? "定位钢片_钢材_" + cage.CageGuid[..8] : "定位钢片_钢材";
                    material = (Material)doc.GetElement(Material.Create(doc, materialName));
                    material.MaterialClass = "Steel"; material.Color = new Color(125, 135, 147); material.Shininess = 35;
                }
                var stored = new JsonArray(); var view = doc.GetElement(new ElementId(viewId)) as View3D;
                foreach (var piece in plan.Plates)
                {
                    int count = plan.Profile.Count; var loop = new CurveLoop();
                    for (int i = 0; i < count; i++)
                    {
                        var a = X(piece.Vertices[i]); var b = X(piece.Vertices[(i + 1) % count]);
                        if (a.DistanceTo(b) <= ui.Application.ShortCurveTolerance) throw new InvalidOperationException("定位钢片截面边短于Revit最小曲线容差。");
                        loop.Append(Line.CreateBound(a, b));
                    }
                    var direction = new XYZ(-Math.Sin(piece.AngleRadians), Math.Cos(piece.AngleRadians), 0);
                    using var options = new SolidOptions(material.Id, ElementId.InvalidElementId);
                    var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, direction, Ft(input.WidthMm), options);
                    var main = Main(doc, cage, piece.MainBarIndex);
                    var element = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                    element.Name = "定位钢片 " + piece.Id; element.ApplicationId = "PileCage.PositionPlate"; element.ApplicationDataId = cage.CageGuid + ":" + piece.Id;
                    element.SetShape(new GeometryObject[] { solid });
                    SetTag(element, new() { ["role"] = "position_plate", ["cage_guid"] = cage.CageGuid, ["host_id"] = cage.HostId, ["piece_id"] = piece.Id, ["main_element_id"] = main.Id.Value });
                    stored.Add(new JsonObject { ["piece_id"] = piece.Id, ["unique_id"] = element.UniqueId, ["main_unique_id"] = main.UniqueId });
                    if (view != null)
                    {
                        if (view.CanCategoryBeHidden(element.Category.Id)) view.SetCategoryHidden(element.Category.Id, false);
                        view.SetElementOverrides(element.Id, new OverrideGraphicSettings().SetProjectionLineColor(new Color(104, 111, 125)).SetSurfaceTransparency(0));
                    }
                }
                var storage = DataStorage.Create(doc);
                SetTag(storage, new() { ["role"] = "record", ["cage_guid"] = cage.CageGuid, ["host_id"] = cage.HostId, ["document_unique_id"] = doc.ProjectInformation.UniqueId,
                    ["input"] = J(plan.Parameters), ["pieces"] = stored, ["material_id"] = material.Id.Value, ["geometry_convention"] = plan.GeometryConvention });
                doc.Regenerate(); if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("定位钢片事务未提交。");
            }
            report["readback"] = Read(ui, cage.CageGuid, cage);
            if (S(report["readback"], "status") != "PASS") throw new InvalidOperationException(S(report["readback"], "message"));
            report["status"] = "PASS"; report["transaction_committed"] = true; report["plate_count"] = plan.TotalCount; report["level_count"] = plan.LevelCount;
        }
        catch (Exception ex) { report["message"] = ex.Message; report["error"] = ex.ToString(); }
        return Evidence(report);
    }

    public static JsonObject Read(UIApplication ui, string cageGuid, CageInput expectedCage)
    {
        try
        {
            var doc = Doc(ui);
            if (expectedCage.CageGuid != cageGuid || expectedCage.HostDocumentUniqueId != doc.ProjectInformation.UniqueId) throw new InvalidOperationException("定位钢片预期参数与钢筋笼或当前项目不符。");
            if (!expectedCage.AdditionalData.TryGetValue("position_plates", out var marker)) throw new InvalidOperationException("钢筋笼缺少定位钢片预期配置标记。");
            var input = marker.Deserialize<PositionPlateInput>(NativeEngine.JsonOptions) ?? throw new InvalidOperationException("定位钢片预期配置无效。");
            var records = Records(doc, cageGuid); if (records.Length != 1) throw new InvalidOperationException("定位钢片持久记录缺失或重复。");
            var record = Tag(records[0], PlateSchemaId);
            if (record["host_id"]!.GetValue<long>() != expectedCage.HostId || S(record, "document_unique_id") != doc.ProjectInformation.UniqueId) throw new InvalidOperationException("定位钢片记录的宿主或项目不符。");
            var host = SimpleColumnWorkflow.ReadColumn(ui, new ElementId(expectedCage.HostId));
            if (Math.Abs(host.X - expectedCage.X) > 0.05 || Math.Abs(host.Y - expectedCage.Y) > 0.05 || Math.Abs(host.Z - expectedCage.Z) > 0.05 || Math.Abs(host.Diameter - expectedCage.Diameter) > 0.05 || Math.Abs(host.Height - expectedCage.Height) > 0.05)
                throw new InvalidOperationException("定位钢片宿主几何已改变。");
            var plan = PositionPlatePlanner.Build(expectedCage, input); var elements = Plates(doc, cageGuid);
            if (elements.Length != plan.TotalCount) throw new InvalidOperationException($"定位钢片实体数量不符：预期{plan.TotalCount}，实际{elements.Length}。");
            var recorded = record["pieces"]!.AsArray().ToDictionary(n => S(n, "piece_id"));
            if (recorded.Count != plan.TotalCount) throw new InvalidOperationException("定位钢片记录数量不符。");
            var actualById = elements.ToDictionary(e => S(Tag(e, PlateSchemaId), "piece_id")); var rows = new JsonArray();
            long materialId = record["material_id"]!.GetValue<long>();
            foreach (var piece in plan.Plates)
            {
                if (!actualById.TryGetValue(piece.Id, out var element) || !recorded.TryGetValue(piece.Id, out var saved) || element.UniqueId != S(saved, "unique_id")) throw new InvalidOperationException("定位钢片标识与保存记录不符。");
                var tag = Tag(element, PlateSchemaId); var main = Main(doc, expectedCage, piece.MainBarIndex);
                if (main.UniqueId != S(saved, "main_unique_id") || tag["main_element_id"]!.GetValue<long>() != main.Id.Value || main.GetHostId().Value != expectedCage.HostId || tag["host_id"]!.GetValue<long>() != expectedCage.HostId)
                    throw new InvalidOperationException("定位钢片的主筋或宿主关联已改变。");
                var bt = (RebarBarType)doc.GetElement(main.GetTypeId());
                if (Math.Abs(Mm(bt.BarModelDiameter) - expectedCage.MainDiameter) > 0.01) throw new InvalidOperationException("贴合主筋的实际模型直径已改变。");
                var curves = main.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                var lower = curves.SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) }).OrderBy(p => p.Z).First();
                double r = plan.MainSurfaceRadiusMm - expectedCage.MainDiameter / 2;
                var expectedLower = new XYZ(Ft(host.X + r * Math.Cos(piece.AngleRadians)), Ft(host.Y + r * Math.Sin(piece.AngleRadians)), Ft(host.Z + expectedCage.MainStart));
                if (Mm(lower.DistanceTo(expectedLower)) > 0.05) throw new InvalidOperationException("钢片对应主筋的实际柱内位置已改变。");
                var firstStart = curves[0].GetEndPoint(0); var firstEnd = curves[0].GetEndPoint(1);
                if (curves[0] is not Line || Mm(Math.Abs(firstStart.X - firstEnd.X)) > 0.05 || Mm(Math.Abs(firstStart.Y - firstEnd.Y)) > 0.05 || Mm(Math.Max(firstStart.Z, firstEnd.Z)) < piece.CenterElevationMm + plan.TotalHeightMm / 2 - 0.05)
                    throw new InvalidOperationException("定位钢片范围内的关联主筋不再保持柱内竖直。");
                var solids = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }).OfType<Solid>().Where(s => s.Volume > 1e-12).ToArray();
                if (solids.Length != 1) throw new InvalidOperationException("定位钢片应具有一个正体积原生实体。");
                var body = solids[0]; double volume = body.Volume * Math.Pow(304.8, 3), volumeError = Math.Abs(volume / plan.VolumePerPlateMm3 - 1);
                if (volumeError > 1e-6) throw new InvalidOperationException("定位钢片实际体积与折形截面计算不符。");
                var vertices = new List<XYZ>();
                foreach (var edge in body.Edges.Cast<Edge>())
                {
                    var c = edge.AsCurve(); if (c is not Line) throw new InvalidOperationException("理想尖角定位片出现非直边几何。");
                    foreach (var point in new[] { c.GetEndPoint(0), c.GetEndPoint(1) }) if (!vertices.Any(v => Mm(v.DistanceTo(point)) < 1e-5)) vertices.Add(point);
                }
                var expected = piece.Vertices.Select(X).ToArray();
                double error = Math.Max(vertices.Max(v => expected.Min(p => Mm(v.DistanceTo(p)))), expected.Max(p => vertices.Min(v => Mm(v.DistanceTo(p)))));
                if (vertices.Count != expected.Length || error > 0.05) throw new InvalidOperationException("定位钢片实体顶点或姿态与安装方案不符。");
                var centroid = body.ComputeCentroid(); if (Mm(centroid.DistanceTo(X(piece.Centroid))) > 0.05) throw new InvalidOperationException("定位钢片实体质心不符。");
                var faces = new JsonArray(); var materials = new HashSet<long>();
                foreach (var face in body.Faces.Cast<Face>())
                {
                    if (face is not PlanarFace plane) throw new InvalidOperationException("理想折板出现非平面表面。");
                    materials.Add(face.MaterialElementId.Value);
                    faces.Add(new JsonObject { ["origin_mm"] = J(P(plane.Origin)), ["normal"] = J(new Point3(plane.FaceNormal.X, plane.FaceNormal.Y, plane.FaceNormal.Z)), ["area_mm2"] = plane.Area * 304.8 * 304.8 });
                }
                if (materials.Count != 1 || !materials.Contains(materialId) || doc.GetElement(new ElementId(materialId)) is not Material plateMaterial || plateMaterial.MaterialClass != "Steel") throw new InvalidOperationException("定位钢片实体钢材材料关联或材料类别不符。");
                double actualGap = vertices.Min(v => host.Diameter / 2 - Math.Sqrt(Math.Pow(Mm(v.X) - host.X, 2) + Math.Pow(Mm(v.Y) - host.Y, 2)));
                if (actualGap < -0.05 || Math.Abs(actualGap - plan.MinimumWallClearanceMm) > 0.05) throw new InvalidOperationException("定位钢片实体孔壁净距不符或穿出柱面。");
                rows.Add(new JsonObject { ["element_id"] = element.Id.Value, ["unique_id"] = element.UniqueId, ["class_name"] = element.GetType().FullName, ["category"] = element.Category.Name, ["category_id"] = element.Category.Id.Value,
                    ["cage_guid"] = cageGuid, ["host_id"] = expectedCage.HostId, ["piece_id"] = piece.Id, ["main_element_id"] = main.Id.Value, ["main_unique_id"] = main.UniqueId,
                    ["main_model_diameter_mm"] = Mm(bt.BarModelDiameter), ["main_lower_mm"] = J(P(lower)), ["main_first_curve_endpoints_mm"] = new JsonArray(J(P(curves[0].GetEndPoint(0))), J(P(curves[0].GetEndPoint(1)))),
                    ["level_index"] = piece.LevelIndex, ["main_index"] = piece.MainBarIndex, ["installation_angle_radians"] = piece.AngleRadians, ["center_elevation_mm"] = piece.CenterElevationMm,
                    ["native_vertices_mm"] = J(vertices.Select(P).ToArray()), ["native_faces"] = faces, ["native_centroid_mm"] = J(P(centroid)), ["volume_mm3"] = volume,
                    ["relative_volume_error"] = volumeError, ["max_vertex_error_mm"] = error, ["actual_min_wall_clearance_mm"] = actualGap, ["material_ids"] = J(materials.ToArray()), ["material_names"] = J(materials.Select(id => doc.GetElement(new ElementId(id)).Name).ToArray()), ["material_classes"] = J(materials.Select(id => ((Material)doc.GetElement(new ElementId(id))).MaterialClass).ToArray()) });
            }
            return Evidence(new() { ["status"] = "PASS", ["cage_guid"] = cageGuid, ["host"] = J(host), ["record_id"] = records[0].Id.Value, ["record_unique_id"] = records[0].UniqueId,
                ["plate_count"] = rows.Count, ["level_count"] = plan.LevelCount, ["plan"] = J(plan), ["pieces"] = rows });
        }
        catch (Exception ex) { return Evidence(new() { ["status"] = "FAIL", ["message"] = ex.Message, ["error"] = ex.ToString() }); }
    }
    public static JsonObject MissingTest(UIApplication ui, string cageGuid, bool removeRecord)
    {
        var doc = Doc(ui); var initial = NativeEngine.Read(ui, cageGuid);
        if (!initial.Success) throw new InvalidOperationException("缺失测试要求原模型回读先通过：" + initial.Message);
        string[] Inventory() => new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.UniqueId).Order().ToArray();
        var before = Inventory(); var targets = removeRecord ? Records(doc, cageGuid).Select(e => e.Id).ToArray() : Plates(doc, cageGuid).Select(e => e.Id).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("缺失测试没有可临时删除的对象。");
        JsonObject failure; string rollback;
        using (var t = new Transaction(doc, "定位钢片缺失回滚验收"))
        {
            t.Start();
            try { foreach (var id in targets) doc.Delete(id); doc.Regenerate(); var read = NativeEngine.Read(ui, cageGuid); failure = read.Details.DeepClone().AsObject(); if (read.Success) throw new InvalidOperationException("删除预期定位钢片后回读仍PASS，缺失检查失效。"); }
            finally { rollback = t.RollBack().ToString(); }
        }
        var after = Inventory(); var restored = NativeEngine.Read(ui, cageGuid);
        return Evidence(new() { ["status"] = before.SequenceEqual(after) && restored.Success ? "PASS" : "FAIL", ["removed_kind"] = removeRecord ? "record" : "all_plate_elements",
            ["temporary_deleted_ids"] = J(targets.Select(id => id.Value).ToArray()), ["missing_readback"] = failure, ["rollback_status"] = rollback,
            ["before_all_unique_ids"] = J(before), ["after_all_unique_ids"] = J(after), ["all_unique_ids_restored"] = before.SequenceEqual(after), ["restored_readback"] = restored.Details.DeepClone() });
    }
    private sealed class PlateFailures : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            bool error = false; foreach (var failure in a.GetFailureMessages()) { if (failure.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(failure); else error = true; }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
