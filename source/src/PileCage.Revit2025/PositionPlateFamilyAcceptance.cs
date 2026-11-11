using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PileCage;

/// <summary>Controlled-project native placement/flex tests; no RFA generation.</summary>
public static class PositionPlateFamilyAcceptance
{
    private static readonly Guid SchemaId = new("60BCD88B-5A62-4595-B289-C0E13655E94B");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Default) { WriteIndented = true };
    private static readonly string[] Names = ["板宽", "板厚", "下直段", "下斜升高", "中直段", "上直段", "外凸尺寸"];
    private static readonly double[] Defaults = [60, 4, 100, 40, 60, 100, 40];
    private static readonly double[] Modified = [75, 6, 120, 50, 80, 130, 50];
    private const double ToleranceMm = .01;

    public static JsonObject Exercise(UIApplication ui)
    {
        Document doc = ui.ActiveUIDocument?.Document ?? throw new InvalidOperationException("请打开圆柱测试项目。");
        string workspace = Path.GetFullPath(App.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (doc.IsFamilyDocument || string.IsNullOrWhiteSpace(doc.PathName) || !Path.GetFullPath(doc.PathName).StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此验收入口仅用于已保存至当前工作区的受控测试项目。");
        if (doc.IsModifiable) throw new InvalidOperationException("面基族验收须在外层事务之外调用。");
        var selection = ui.ActiveUIDocument.Selection.GetElementIds().Select(doc.GetElement).OfType<FamilyInstance>()
            .Where(e => e.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns).ToArray();
        if (selection.Length != 1) throw new InvalidOperationException("请选择且只选择一根实际圆形结构柱。");
        FamilyInstance host = selection[0];
        _ = NativeEngine.InspectHost(host); // Throws unless an actual complete concrete cylinder.
        FamilySymbol symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .SingleOrDefault(s => s.Family.Name == PositionPlateFamilyBuilder.FamilyName)
            ?? throw new InvalidOperationException("请先加载唯一的PC_PositionPlate_FaceBased面基族及其默认类型。");
        if (symbol.Family.FamilyPlacementType != FamilyPlacementType.WorkPlaneBased
            || symbol.Family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsInteger() != 5)
            throw new InvalidOperationException("加载的族不是实际面基族。");
        var hostFaces = ReferencedFaces(host).ToArray();
        var cylinderRecord = hostFaces.Where(r => r.Face is CylindricalFace f && Math.Abs(r.ToWorld.OfVector(f.Axis).Normalize().Z) > .999999)
            .OrderByDescending(r => r.Face.Area).FirstOrDefault() ?? throw new InvalidOperationException("无法获得带Reference的圆柱侧面。");
        var planeRecord = hostFaces.Where(r => r.Face is PlanarFace f && r.ToWorld.OfVector(f.FaceNormal).Z > .999999)
            .OrderByDescending(r => r.ToWorld.OfPoint(((PlanarFace)r.Face).Origin).Z).FirstOrDefault() ?? throw new InvalidOperationException("无法获得带Reference的圆柱顶面。");
        var cylindrical = (CylindricalFace)cylinderRecord.Face; var planar = (PlanarFace)planeRecord.Face;
        UV sideUV = InteriorUV(cylindrical); XYZ sidePoint = cylinderRecord.ToWorld.OfPoint(cylindrical.Evaluate(sideUV)), normal = cylinderRecord.ToWorld.OfVector(cylindrical.ComputeNormal(sideUV)).Normalize();
        XYZ axial = cylinderRecord.ToWorld.OfVector(cylindrical.Axis).Normalize(); if (axial.Z < 0) axial = -axial;
        XYZ sideX = axial.CrossProduct(normal).Normalize();
        UV topUV = InteriorUV(planar); XYZ topPoint = planeRecord.ToWorld.OfPoint(planar.Evaluate(topUV)), topX = planeRecord.ToWorld.OfVector(planar.XVector).Normalize();
        var baseline = AllUids(doc); var stages = new JsonArray();
        using var group = new TransactionGroup(doc, "面基定位钢片真实实例验收");
        group.Start();
        try
        {
            FamilyInstance first = null!, second = null!;
            Commit(doc, "在真实圆柱面和顶平面放置定位钢片", () =>
            {
                if (!symbol.IsActive) symbol.Activate(); doc.Regenerate();
                first = doc.Create.NewFamilyInstance(cylindrical.Reference, sidePoint, sideX, symbol);
                second = doc.Create.NewFamilyInstance(planar.Reference, topPoint, topX, symbol);
                SetValues(first, Values()); SetValues(second, Values());
            });
            var secondBaseline = Inspect(doc, second, Values(), host.UniqueId, "PlanarFace");
            stages.Add(new JsonObject { ["case"] = "默认双实例", ["first"] = Inspect(doc, first, Values(), host.UniqueId, "CylindricalFace"), ["second"] = secondBaseline.DeepClone() });
            for (int i = 0; i < Names.Length; ++i)
            {
                var values = Values(); values[Names[i]] = Modified[i];
                Commit(doc, "独立修改钢片实例参数：" + Names[i], () => SetValues(first, values));
                var afterFirst = Inspect(doc, first, values, host.UniqueId, "CylindricalFace");
                var afterSecond = Inspect(doc, second, Values(), host.UniqueId, "PlanarFace");
                double isolation = PointSetError(ReadLocalVertices(first), ExpectedVertices(values));
                double secondDifference = PointSetError(ParsePoints(secondBaseline["local_vertices_mm"]!), ParsePoints(afterSecond["local_vertices_mm"]!));
                if (secondDifference > ToleranceMm || Math.Abs(secondBaseline["volume_mm3"]!.GetValue<double>() - afterSecond["volume_mm3"]!.GetValue<double>()) > .001)
                    throw new InvalidOperationException("修改第一实例影响了第二实例的实际几何，实例参数隔离失败。");
                stages.Add(new JsonObject { ["case"] = Names[i], ["first"] = afterFirst, ["second"] = afterSecond,
                    ["second_geometry_change_mm"] = secondDifference, ["first_expected_geometry_error_mm"] = isolation });
            }
            var finalFirst = Values(); finalFirst[Names[^1]] = Modified[^1];
            Commit(doc, "保存面基实例验收基准", () =>
            {
                Store(first, host.UniqueId, "CylindricalFace", finalFirst, "侧面改参实例");
                Store(second, host.UniqueId, "PlanarFace", Values(), "顶面独立对照实例");
            });
            JsonObject read = Read(doc);
            if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("面基实例验收事务组未提交。");
            read["exercise_stages"] = stages; read["transaction_group_committed"] = true;
            read["new_instance_ids"] = J(new[] { first.Id.Value, second.Id.Value });
            read["new_instance_unique_ids"] = J(new[] { first.UniqueId, second.UniqueId });
            read["baseline_elements_retained"] = baseline.IsSubsetOf(AllUids(doc));
            read["save_or_reopen_performed"] = false;
            return read;
        }
        catch (Exception ex)
        {
            bool rolledBack = group.GetStatus() == TransactionStatus.Started && group.RollBack() == TransactionStatus.RolledBack;
            return new JsonObject { ["status"] = "FAIL", ["message"] = ex.Message, ["trace"] = ex.ToString(),
                ["rolled_back"] = rolledBack, ["baseline_element_uids_restored"] = baseline.SetEquals(AllUids(doc)), ["exercise_stages"] = stages };
        }
    }

    /// <summary>Read-only, usable after reopening. Expectations are per-instance.</summary>
    public static JsonObject Read(Document doc)
    {
        bool modified = doc.IsModified; var schema = Schema.Lookup(SchemaId); var items = new JsonArray();
        if (schema == null) throw new InvalidOperationException("此项目没有面基定位钢片验收记录。");
        var candidates = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(f => f.GetEntity(schema).IsValid()).ToArray();
        if (candidates.Length != 2) throw new InvalidOperationException($"面基族验收应保留两件测试实例，实际{candidates.Length}件。");
        foreach (var instance in candidates)
        {
            JsonObject record = JsonNode.Parse(instance.GetEntity(schema).Get<string>("record"))!.AsObject();
            var expected = JsonSerializer.Deserialize<Dictionary<string, double>>(record["expected_parameters_mm"]!.ToJsonString(), Json)!;
            var row = Inspect(doc, instance, expected, record["host_unique_id"]!.GetValue<string>(), record["host_face_type"]!.GetValue<string>());
            row["persisted_expectation"] = record; items.Add(row);
        }
        return new JsonObject { ["status"] = "PASS", ["document_path"] = doc.PathName, ["document_modified_before"] = modified,
            ["document_modified_after"] = doc.IsModified, ["read_only"] = true, ["family_name"] = PositionPlateFamilyBuilder.FamilyName,
            ["instance_count"] = items.Count, ["instances"] = items, ["tolerance_mm"] = ToleranceMm,
            ["boundary"] = "Actual FamilyInstance placement, instance parameter isolation, host-face reference and transformed native geometry only; no structural, fabrication or design approval is inferred." };
    }

    private static JsonObject Inspect(Document doc, FamilyInstance instance, IReadOnlyDictionary<string, double> expected, string hostUid, string faceType)
    {
        if (instance.Symbol.Family.Name != PositionPlateFamilyBuilder.FamilyName) throw new InvalidOperationException("实际实例族名不符。");
        Reference reference = instance.HostFace ?? throw new InvalidOperationException("实际实例HostFace为空，不能视为面宿主实例。");
        Element host = doc.GetElement(reference.ElementId) ?? throw new InvalidOperationException("实际宿主面Element不存在。");
        if (host.UniqueId != hostUid) throw new InvalidOperationException("面宿主身份发生改变。");
        string stable = reference.ConvertToStableRepresentation(doc);
        var hostFace = ReferencedFaces(host).FirstOrDefault(r => r.Face.Reference.ConvertToStableRepresentation(doc) == stable)
            ?? throw new InvalidOperationException("HostFace无法解析为实际宿主几何引用及其坐标变换。");
        Face face = hostFace.Face;
        if (face.GetType().Name != faceType) throw new InvalidOperationException("实际宿主面类型不符。");
        var actual = new Dictionary<string, double>();
        foreach (string name in Names)
        {
            Parameter p = instance.LookupParameter(name) ?? throw new InvalidOperationException("实例缺少参数：" + name);
            if (p.IsReadOnly || p.StorageType != StorageType.Double) throw new InvalidOperationException("不是可编辑的实例长度参数：" + name);
            double value = Mm(p.AsDouble()); actual[name] = value;
            if (Math.Abs(value - expected[name]) > .001) throw new InvalidOperationException("实际实例参数值不符：" + name);
        }
        var solids = WorldSolids(instance).ToArray(); if (solids.Length != 1) throw new InvalidOperationException("实例必须包含一个原生正体积钢片实体。");
        XYZ[] points = ReadLocalVertices(instance); XYZ[] target = ExpectedVertices(expected);
        double pointError = PointSetError(points, target);
        if (points.Length != 24 || pointError > ToleranceMm) throw new InvalidOperationException($"实际实例局部顶点不符：{points.Length}/24，误差{pointError:G8}mm。");
        double volume = solids.Sum(s => s.Volume) * Math.Pow(304.8, 3), targetVolume = ExpectedVolume(expected);
        if (Math.Abs(volume / targetVolume - 1) > 1e-7) throw new InvalidOperationException("实例实际体积不符，参数未正确驱动几何。");
        Transform transform = instance.GetTransform(); XYZ location = (instance.Location as LocationPoint)?.Point ?? transform.Origin;
        IntersectionResult projection = face.Project(hostFace.ToWorld.Inverse.OfPoint(location)) ?? throw new InvalidOperationException("插入点不能投影到真实宿主面。");
        double hostDistance = Mm(projection.Distance);
        if (hostDistance > ToleranceMm) throw new InvalidOperationException($"实际实例插入点离宿主面{hostDistance:G8}mm。");
        double normalDot = transform.BasisZ.Normalize().DotProduct(hostFace.ToWorld.OfVector(face.ComputeNormal(projection.UVPoint)).Normalize());
        if (normalDot < .999999) throw new InvalidOperationException("实际族Z轴未指向宿主面外法向。");
        var materials = instance.GetMaterialIds(false).Select(id => doc.GetElement(id) as Material).Where(m => m != null).ToArray();
        if (materials.Length == 0 || materials.Any(m => !string.Equals(m!.MaterialClass, "Steel", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("实际钢片实例未赋Steel类别材质。");
        return new JsonObject { ["status"] = "PASS", ["element_id"] = instance.Id.Value, ["unique_id"] = instance.UniqueId,
            ["class"] = instance.GetType().FullName, ["host_id"] = host.Id.Value, ["host_unique_id"] = host.UniqueId,
            ["host_property_id"] = instance.Host?.Id.Value, ["host_face_type"] = face.GetType().Name,
            ["host_face_stable_reference"] = reference.ConvertToStableRepresentation(doc), ["host_face_distance_mm"] = hostDistance,
            ["face_normal_dot_family_z"] = normalDot, ["parameters_mm"] = J(actual), ["positive_solid_count"] = solids.Length,
            ["volume_mm3"] = volume, ["expected_volume_mm3"] = targetVolume, ["native_vertex_count"] = points.Length,
            ["maximum_vertex_error_mm"] = pointError, ["local_vertices_mm"] = J(points.Select(P).ToArray()),
            ["transform"] = new JsonObject { ["origin_mm"] = J(P(transform.Origin)), ["basis_x"] = J(V(transform.BasisX)), ["basis_y"] = J(V(transform.BasisY)), ["basis_z"] = J(V(transform.BasisZ)) },
            ["materials"] = J(materials.Select(m => new { id = m!.Id.Value, name = m.Name, material_class = m.MaterialClass }).ToArray()) };
    }

    private sealed record ReferencedFace(Face Face, Transform ToWorld);
    private static IEnumerable<ReferencedFace> ReferencedFaces(Element host)
    {
        var geometry = host.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
        return Traverse(geometry, Transform.Identity);
        static IEnumerable<ReferencedFace> Traverse(GeometryElement geometry, Transform transform)
        {
            foreach (GeometryObject item in geometry)
            {
                if (item is Solid solid) foreach (Face f in solid.Faces) if (f.Reference != null) yield return new(f, transform);
                if (item is GeometryInstance gi)
                    foreach (var f in Traverse(gi.GetSymbolGeometry(), transform.Multiply(gi.Transform))) yield return f;
            }
        }
    }
    private static UV InteriorUV(Face face)
    {
        BoundingBoxUV box = face.GetBoundingBox();
        foreach (double u in new[] { .5, .25, .75 }) foreach (double v in new[] { .5, .25, .75 })
        {
            var q = new UV(box.Min.U + (box.Max.U - box.Min.U) * u, box.Min.V + (box.Max.V - box.Min.V) * v);
            if (face.IsInside(q)) return q;
        }
        throw new InvalidOperationException("无法找到实际宿主面的内部点。");
    }
    private static IEnumerable<Solid> WorldSolids(FamilyInstance instance)
    {
        return Traverse(instance.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false }), Transform.Identity);
        static IEnumerable<Solid> Traverse(GeometryElement geometry, Transform transform)
        {
            foreach (GeometryObject item in geometry)
            {
                if (item is Solid s && s.Volume > 0) yield return transform.IsIdentity ? s : SolidUtils.CreateTransformed(s, transform);
                else if (item is GeometryInstance gi) foreach (Solid child in Traverse(gi.GetSymbolGeometry(), transform.Multiply(gi.Transform))) yield return child;
            }
        }
    }
    private static XYZ[] ReadLocalVertices(FamilyInstance instance)
    {
        Transform inverse = instance.GetTransform().Inverse; var points = new List<XYZ>();
        foreach (Solid solid in WorldSolids(instance)) foreach (Edge edge in solid.Edges)
            foreach (XYZ point in new[] { edge.AsCurve().GetEndPoint(0), edge.AsCurve().GetEndPoint(1) })
            {
                XYZ local = inverse.OfPoint(point);
                if (points.All(p => p.DistanceTo(local) > 1e-7)) points.Add(local);
            }
        return points.ToArray();
    }
    // Independent normal-offset intersection of the five centerline segments,
    // not the builder's closed-form vertex expressions.
    private static (double X, double Y)[] Section(IReadOnlyDictionary<string, double> p)
    {
        double t = p["板厚"], half = t / 2, outer = p["外凸尺寸"] - half;
        double a = p["下直段"], b = a + p["下斜升高"], c = b + p["中直段"];
        (double X, double Y)[] line = [(half, 0), (half, a), (outer, b), (outer, c), (half, c), (half, c + p["上直段"])];
        (double X, double Y)[] Offset(double distance)
        {
            var directions = line.Zip(line.Skip(1), (u, v) => { double x = v.X - u.X, y = v.Y - u.Y, l = Math.Sqrt(x * x + y * y); return (X: x / l, Y: y / l); }).ToArray();
            var output = new (double X, double Y)[line.Length];
            for (int i = 0; i < line.Length; i++)
            {
                var u = directions[Math.Max(0, i - 1)]; var v = directions[Math.Min(i, directions.Length - 1)]; var q = line[i];
                double x1 = q.X - u.Y * distance, y1 = q.Y + u.X * distance, x2 = q.X - v.Y * distance, y2 = q.Y + v.X * distance;
                double cross = u.X * v.Y - u.Y * v.X, s = Math.Abs(cross) < 1e-12 ? 0 : ((x2 - x1) * v.Y - (y2 - y1) * v.X) / cross;
                output[i] = (x1 + s * u.X, y1 + s * u.Y);
            }
            return output;
        }
        return Offset(half).Concat(Offset(-half).Reverse()).ToArray();
    }
    private static XYZ[] ExpectedVertices(IReadOnlyDictionary<string, double> values) =>
        new[] { -values["板宽"] / 2, values["板宽"] / 2 }.SelectMany(x => Section(values).Select(p => new XYZ(Ft(x), Ft(p.Y), Ft(p.X)))).ToArray();
    private static double ExpectedVolume(IReadOnlyDictionary<string, double> values)
    {
        var p = Section(values); double twiceArea = 0;
        for (int i = 0; i < p.Length; i++) { var q = p[(i + 1) % p.Length]; twiceArea += p[i].X * q.Y - q.X * p[i].Y; }
        return Math.Abs(twiceArea) / 2 * values["板宽"];
    }
    private static double PointSetError(XYZ[] actual, XYZ[] expected) => Math.Max(actual.Max(p => expected.Min(q => Mm(p.DistanceTo(q)))), expected.Max(p => actual.Min(q => Mm(p.DistanceTo(q)))));
    private static XYZ[] ParsePoints(JsonNode points) => points.AsArray().Select(n => new XYZ(Ft(n![0]!.GetValue<double>()), Ft(n[1]!.GetValue<double>()), Ft(n[2]!.GetValue<double>()))).ToArray();
    private static Dictionary<string, double> Values() => Names.Select((n, i) => new KeyValuePair<string, double>(n, Defaults[i])).ToDictionary(x => x.Key, x => x.Value);
    private static void SetValues(FamilyInstance instance, IReadOnlyDictionary<string, double> values)
    {
        foreach (var pair in values)
        {
            Parameter p = instance.LookupParameter(pair.Key) ?? throw new InvalidOperationException("实例没有参数：" + pair.Key);
            if (p.IsReadOnly || !p.Set(Ft(pair.Value))) throw new InvalidOperationException("实例参数设置失败：" + pair.Key);
        }
    }
    private static void Store(FamilyInstance instance, string hostUid, string faceType, IReadOnlyDictionary<string, double> values, string role)
    {
        Schema? schema = Schema.Lookup(SchemaId);
        if (schema == null)
        {
            var b = new SchemaBuilder(SchemaId); b.SetSchemaName("PositionPlateFamilyAcceptanceV1"); b.SetReadAccessLevel(AccessLevel.Public); b.SetWriteAccessLevel(AccessLevel.Public); b.AddSimpleField("record", typeof(string)); schema = b.Finish();
        }
        var record = new JsonObject { ["host_unique_id"] = hostUid, ["host_face_type"] = faceType, ["role"] = role, ["expected_parameters_mm"] = J(values), ["created_utc"] = DateTime.UtcNow.ToString("o") };
        var entity = new Entity(schema); entity.Set("record", record.ToJsonString(Json)); instance.SetEntity(entity);
    }
    private static HashSet<string> AllUids(Document doc) => new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.UniqueId).ToHashSet();
    private static void Commit(Document doc, string name, Action work)
    {
        using var tx = new Transaction(doc, name); tx.Start(); var failures = new TestFailures();
        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
        work(); doc.Regenerate();
        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(name + "：" + string.Join("；", failures.Errors));
    }
    private sealed class TestFailures : IFailuresPreprocessor
    {
        public List<string> Errors { get; } = [];
        public FailureProcessingResult PreprocessFailures(FailuresAccessor f)
        {
            Errors.AddRange(f.GetFailureMessages().Where(m => m.GetSeverity() == FailureSeverity.Error).Select(m => m.GetDescriptionText()));
            return Errors.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
    private static JsonNode? J<T>(T value) => JsonSerializer.SerializeToNode(value, Json);
    private static double Ft(double mm) => mm / 304.8;
    private static double Mm(double feet) => feet * 304.8;
    private static double[] P(XYZ point) => [Mm(point.X), Mm(point.Y), Mm(point.Z)];
    private static double[] V(XYZ point) => [point.X, point.Y, point.Z];
}
