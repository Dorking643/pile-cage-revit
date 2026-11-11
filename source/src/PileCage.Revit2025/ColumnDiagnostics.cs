using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PileCage;

/// <summary>Read-only evidence. Does not start transactions, regenerate, modify views, select, save or delete.</summary>
public static class ColumnDiagnostics
{
    private static readonly Guid CageSchemaId = new("D71C2A12-2A7C-4DD3-9965-65B5025D7F31");
    private static double Mm(double feet) => feet * 304.8;
    private static JsonArray Point(XYZ p) => new(Mm(p.X), Mm(p.Y), Mm(p.Z));
    private static JsonArray Vector(XYZ p) => new(p.X, p.Y, p.Z);

    // Individual API failures are evidence, and must not suppress the remaining diagnostics.
    private static void Capture(JsonObject target, string key, Func<object?> read)
    {
        try { target[key] = JsonSerializer.SerializeToNode(read(), NativeEngine.JsonOptions); }
        catch (Exception ex) { target[key] = null; target[key + "_error"] = ex.GetType().Name + ": " + ex.Message; }
    }

    public static JsonObject Run(UIApplication ui, JsonObject request)
    {
        var result = new JsonObject { ["status"] = "PASS", ["operation"] = "column_diagnostics", ["read_only"] = true, ["utc"] = DateTime.UtcNow.ToString("o") };
        try
        {
            var uidoc = ui.ActiveUIDocument ?? throw new InvalidOperationException("没有活动项目。");
            var doc = uidoc.Document;
            string filterGuid = request["cage_guid"]?.GetValue<string>() ?? "";
            bool detailedGeometry = request["include_geometry"]?.GetValue<bool>() ?? !string.IsNullOrWhiteSpace(filterGuid);
            result["geometry_cage_guid_filter"] = filterGuid;
            result["include_geometry"] = detailedGeometry;
            Capture(result, "document_path", () => doc.PathName);
            Capture(result, "document_unique_id", () => doc.ProjectInformation.UniqueId);
            Capture(result, "document_is_modified_before", () => doc.IsModified);
            Capture(result, "document_is_read_only", () => doc.IsReadOnly);
            Capture(result, "active_view_id", () => uidoc.ActiveView.Id.Value);
            Capture(result, "selection_ids", () => uidoc.Selection.GetElementIds().Select(id => id.Value).ToArray());
            var allBars = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().OrderBy(r => r.Id.Value).ToArray();
            result["document_rebar_count"] = allBars.Length;
            result["document_rebar_ids"] = JsonSerializer.SerializeToNode(allBars.Select(r => r.Id.Value).ToArray());

            var views = new List<View> { uidoc.ActiveView };
            views.AddRange(new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate && (v.Name.Contains("模型核查") || v.Name.Contains("圆柱钢筋") || v.Name.Contains("圆形柱"))));
            views = views.GroupBy(v => v.Id.Value).Select(g => g.First()).ToList();
            var viewRows = new JsonArray(); result["views"] = viewRows;
            var collectorSets = new Dictionary<long, HashSet<long>?>();
            foreach (var view in views)
            {
                var row = ViewInfo(doc, view, out var ids); viewRows.Add(row); collectorSets[view.Id.Value] = ids;
            }

            var rows = new JsonArray(); result["bars"] = rows;
            foreach (var bar in allBars)
            {
                var row = new JsonObject(); rows.Add(row);
                Capture(row, "element_id", () => bar.Id.Value);
                Capture(row, "unique_id", () => bar.UniqueId);
                Capture(row, "is_valid_object", () => bar.IsValidObject);
                Capture(row, "mark", () => bar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString());
                Capture(row, "comments", () => bar.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString());
                Capture(row, "host_id", () => bar.GetHostId().Value);
                Capture(row, "is_freeform", () => bar.IsRebarFreeForm());
                Capture(row, "type_id", () => bar.GetTypeId().Value);
                Capture(row, "type_name", () => doc.GetElement(bar.GetTypeId())?.Name);
                Capture(row, "nominal_diameter_mm", () => doc.GetElement(bar.GetTypeId()) is RebarBarType t ? Mm(t.BarNominalDiameter) : null);
                Capture(row, "model_diameter_mm", () => doc.GetElement(bar.GetTypeId()) is RebarBarType t ? Mm(t.BarModelDiameter) : null);
                Capture(row, "quantity", () => bar.Quantity);
                Capture(row, "number_of_bar_positions", () => bar.NumberOfBarPositions);
                Capture(row, "does_bar_exist_at_position_zero", () => bar.DoesBarExistAtPosition(0));
                Capture(row, "length_mm", () => Mm(bar.TotalLength));
                Capture(row, "model_bbox", () => Box(bar.get_BoundingBox(null)));
                JsonObject tag = new();
                Capture(row, "tag", () => { tag = Tag(bar); return tag; });
                string guid = tag["cage_guid"]?.GetValue<string>() ?? "";
                string kind = tag["kind"]?.GetValue<string>() ?? "";
                row["cage_guid"] = guid; row["kind"] = kind;
                Capture(row, "centerline", () => Centerline(bar));
                if (row["centerline"] is JsonObject centerline) row["centerline_curve_count"] = centerline["curve_count"]?.DeepClone();
                var barViews = new JsonArray(); row["views"] = barViews;
                foreach (var view in views)
                {
                    var viewRow = new JsonObject { ["view_id"] = view.Id.Value };
                    barViews.Add(viewRow);
                    var ids = collectorSets[view.Id.Value]; viewRow["in_view_collector"] = ids == null ? null : JsonValue.Create(ids.Contains(bar.Id.Value));
                    Capture(viewRow, "element_hidden", () => bar.IsHidden(view));
                    Capture(viewRow, "bar_zero_hidden", () => bar.IsBarHidden(view, 0));
                    Capture(viewRow, "unobscured", () => bar.IsUnobscuredInView(view));
                    Capture(viewRow, "visible_in_temporary_hide_isolate", () => view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, bar.Id));
                    Capture(viewRow, "view_bbox", () => Box(bar.get_BoundingBox(view)));
                    Capture(viewRow, "element_graphic_overrides", () => GraphicOverrides(view.GetElementOverrides(bar.Id)));
                }
                bool detailThisBar = detailedGeometry && (string.IsNullOrEmpty(filterGuid) || guid == filterGuid);
                if (detailThisBar)
                {
                    Capture(row, "freeform_server_guid", () => bar.IsRebarFreeForm() ? bar.GetFreeFormAccessor().GetServerGUID().ToString("D") : null);
                    Capture(row, "fine_model_geometry", () => GeometrySummary(bar, null, true));
                    if (kind == "helix") Capture(row, "fine_visible_model_geometry", () => GeometrySummary(bar, null, false));
                    Capture(row, "active_view_geometry", () => GeometrySummary(bar, uidoc.ActiveView, true));
                }
            }
            Capture(result, "hosts", () => HostRows(doc, allBars));
            Capture(result, "document_is_modified_after", () => doc.IsModified);
            result["interpretation"] = "View collectors and bounding boxes describe API visibility candidates; they are not proof that pixels rendered. Centerline existence does not alone prove solid geometry rendered. No diagnostic transaction or regeneration was run.";
        }
        catch (Exception ex) { result["status"] = "FAIL"; result["error"] = ex.ToString(); }
        return result;
    }

    private static JsonObject ViewInfo(Document doc, View view, out HashSet<long>? collectorIds)
    {
        var row = new JsonObject(); collectorIds = null;
        Capture(row, "view_id", () => view.Id.Value); Capture(row, "name", () => view.Name);
        Capture(row, "view_type", () => view.ViewType.ToString()); Capture(row, "detail_level", () => view.DetailLevel.ToString());
        Capture(row, "display_style", () => view.DisplayStyle.ToString()); Capture(row, "template_id", () => view.ViewTemplateId.Value);
        Capture(row, "discipline", () => view.Discipline.ToString());
        Capture(row, "rebar_category_hidden", () => view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Rebar)));
        Capture(row, "temporary_hide_isolate_active", () => view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate));
        Capture(row, "right_direction", () => Vector(view.RightDirection)); Capture(row, "up_direction", () => Vector(view.UpDirection));
        if (view is View3D v3)
        {
            Capture(row, "is_perspective", () => v3.IsPerspective);
            Capture(row, "section_box_active", () => v3.IsSectionBoxActive);
            Capture(row, "section_box", () => Box(v3.GetSectionBox()));
        }
        Capture(row, "filters", () =>
        {
            var filters = new JsonArray();
            foreach (var id in view.GetFilters())
            {
                var filter = new JsonObject { ["filter_id"] = id.Value }; filters.Add(filter);
                Capture(filter, "name", () => doc.GetElement(id)?.Name);
                Capture(filter, "visible", () => view.GetFilterVisibility(id)); Capture(filter, "enabled", () => view.GetIsFilterEnabled(id));
                if (doc.GetElement(id) is ParameterFilterElement parameterFilter)
                    Capture(filter, "category_ids", () => parameterFilter.GetCategories().Select(c => c.Value).ToArray());
            }
            return filters;
        });
        try
        {
            collectorIds = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Rebar)).ToElementIds().Select(id => id.Value).ToHashSet();
            row["view_collector_rebar_count"] = collectorIds.Count;
            row["view_collector_rebar_ids"] = JsonSerializer.SerializeToNode(collectorIds.OrderBy(id => id).ToArray());
        }
        catch (Exception ex) { row["view_collector_error"] = ex.GetType().Name + ": " + ex.Message; }
        return row;
    }

    private static JsonObject Centerline(Rebar bar)
    {
        var curves = bar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0);
        var row = new JsonObject { ["curve_count"] = curves.Count };
        Capture(row, "summed_length_mm", () => curves.Sum(c => Mm(c.Length)));
        Capture(row, "curve_type_counts", () => curves.GroupBy(c => c.GetType().Name).ToDictionary(g => g.Key, g => g.Count()));
        if (curves.Count > 0)
        {
            Capture(row, "first_endpoint_mm", () => Point(curves[0].GetEndPoint(0)));
            Capture(row, "last_endpoint_mm", () => Point(curves[^1].GetEndPoint(1)));
        }
        return row;
    }

    private static JsonObject GeometrySummary(Rebar bar, View? view, bool includeNonVisible)
    {
        var options = new Options { IncludeNonVisibleObjects = includeNonVisible, ComputeReferences = false };
        if (view == null) options.DetailLevel = ViewDetailLevel.Fine; else options.View = view;
        var geometry = bar.get_Geometry(options);
        int solids = 0, positiveSolids = 0, meshes = 0, curves = 0, instances = 0, triangles = 0;
        double volume = 0;
        var errors = new JsonArray(); var types = new Dictionary<string, int>();
        void ReadGeometry(GeometryElement elements)
        {
            foreach (var element in elements)
            {
                var type = element.GetType().Name; types[type] = types.GetValueOrDefault(type) + 1;
                try
                {
                    if (element is Solid s) { solids++; double v = s.Volume; if (v > 0) positiveSolids++; volume += v; }
                    else if (element is Mesh mesh) { meshes++; triangles += mesh.NumTriangles; }
                    else if (element is Curve) curves++;
                    else if (element is GeometryInstance instance) { instances++; ReadGeometry(instance.GetInstanceGeometry()); }
                }
                catch (Exception ex) { if (errors.Count < 32) errors.Add(type + ": " + ex.Message); }
            }
        }
        if (geometry != null) ReadGeometry(geometry);
        var summary = new JsonObject
        {
            ["include_non_visible_objects"] = includeNonVisible, ["geometry_returned"] = geometry != null, ["solid_count"] = solids, ["positive_volume_solid_count"] = positiveSolids,
            ["solid_volume_mm3"] = volume * 304.8 * 304.8 * 304.8, ["mesh_count"] = meshes, ["mesh_triangle_count"] = triangles,
            ["curve_count"] = curves, ["instance_count"] = instances, ["object_type_counts"] = JsonSerializer.SerializeToNode(types), ["errors"] = errors
        };
        if (geometry != null) Capture(summary, "geometry_bbox", () => Box(geometry.GetBoundingBox()));
        return summary;
    }

    private static JsonObject GraphicOverrides(OverrideGraphicSettings graphics)
    {
        var row = new JsonObject();
        Capture(row, "surface_transparency", () => graphics.Transparency);
        Capture(row, "projection_line_weight", () => graphics.ProjectionLineWeight);
        Capture(row, "projection_line_color", () => ColorInfo(graphics.ProjectionLineColor));
        Capture(row, "surface_foreground_color", () => ColorInfo(graphics.SurfaceForegroundPatternColor));
        Capture(row, "surface_foreground_pattern_id", () => graphics.SurfaceForegroundPatternId.Value);
        return row;
    }

    private static JsonObject ColorInfo(Color color) => color.IsValid
        ? new JsonObject { ["valid"] = true, ["red"] = color.Red, ["green"] = color.Green, ["blue"] = color.Blue }
        : new JsonObject { ["valid"] = false };

    private static JsonNode? Box(BoundingBoxXYZ? box)
    {
        if (box == null) return null;
        var transform = box.Transform; var corners = new List<XYZ>();
        foreach (double x in new[] { box.Min.X, box.Max.X }) foreach (double y in new[] { box.Min.Y, box.Max.Y }) foreach (double z in new[] { box.Min.Z, box.Max.Z })
            corners.Add(transform.OfPoint(new XYZ(x, y, z)));
        return new JsonObject
        {
            ["enabled"] = box.Enabled, ["local_min_mm"] = Point(box.Min), ["local_max_mm"] = Point(box.Max),
            ["transform_origin_mm"] = Point(transform.Origin), ["transform_basis_x"] = Vector(transform.BasisX),
            ["transform_basis_y"] = Vector(transform.BasisY), ["transform_basis_z"] = Vector(transform.BasisZ),
            ["world_aabb_min_mm"] = Point(new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z))),
            ["world_aabb_max_mm"] = Point(new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z)))
        };
    }

    private static JsonObject Tag(Element element)
    {
        var schema = Schema.Lookup(CageSchemaId); if (schema == null) return new();
        var entity = element.GetEntity(schema); return entity.IsValid() ? JsonNode.Parse(entity.Get<string>("record"))!.AsObject() : new();
    }

    private static JsonArray HostRows(Document doc, IEnumerable<Rebar> bars)
    {
        var rows = new JsonArray();
        var ids = new HashSet<long>();
        foreach (var bar in bars) { try { ids.Add(bar.GetHostId().Value); } catch { } }
        foreach (long id in ids.OrderBy(id => id))
        {
            var row = new JsonObject { ["host_id"] = id }; rows.Add(row);
            var host = doc.GetElement(new ElementId(id)); row["exists"] = host != null; if (host == null) continue;
            Capture(row, "category", () => host.Category?.Name); Capture(row, "bbox", () => Box(host.get_BoundingBox(null)));
            Capture(row, "verified_circular_geometry", () => NativeEngine.InspectHost(host));
        }
        return rows;
    }
}
