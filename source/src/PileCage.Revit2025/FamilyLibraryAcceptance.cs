using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ComboBox = System.Windows.Controls.ComboBox;

namespace PileCage;

public static class FamilyLibraryAcceptance
{
    public static JsonObject Run(UIApplication ui, JsonObject request)
    {
        string operation = request["operation"]!.GetValue<string>();
        JsonObject result;
        try
        {
            result = operation switch
            {
                "library_build_assets" => FamilyLibraryService.BuildAssets(ui),
                "library_load" => FamilyLibraryService.Load(ui, request["selected_ids"]!.AsArray().Select(n => n!.GetValue<string>())),
                "library_inventory" => Inventory(ui.ActiveUIDocument.Document),
                "library_exercise_plate" => PositionPlateFamilyAcceptance.Exercise(ui),
                "library_read_plate" => PositionPlateFamilyAcceptance.Read(ui.ActiveUIDocument.Document),
                "library_ui_choose" => ChooseThroughUi(ui, request),
                "library_ui_state" => UiState(ui, request),
                "library_conflict_test" => ConflictTest(ui),
                "library_fake_family_test" => FakeFamilyTest(ui),
                "library_close_ui" => CloseUi(),
                "library_export_plate" => ExportPlate(ui, request),
                "library_capture_current" => CaptureCurrent(ui, request),
                _ => throw new ArgumentException("未知族库操作：" + operation)
            };
        }
        catch (Exception ex) { result = new() { ["status"] = "FAIL", ["message"] = ex.Message, ["error"] = ex.ToString() }; }
        var assembly = typeof(FamilyLibraryAcceptance).Assembly;
        result["assembly_path"] = assembly.Location;
        result["assembly_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        result["operation"] = operation; result["utc"] = DateTime.UtcNow.ToString("o");
        return result;
    }
    private static JsonObject CloseUi()
    {
        int count = 0;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("PileCage.") == true))
            if (assembly.GetType("PileCage.SimpleColumnWindow")?.GetProperty("Instance")?.GetValue(null) is Window window) { window.Close(); count++; }
        return new() { ["status"] = "PASS", ["closed"] = count };
    }
    private static JsonObject ExportPlate(UIApplication ui, JsonObject request)
    {
        var doc = ui.ActiveUIDocument.Document;
        var instances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(f => f.Symbol.Family.Name == PositionPlateFamilyBuilder.FamilyName).ToArray();
        if (instances.Length == 0) throw new InvalidOperationException("测试模型没有定位钢片实例。");
        View3D view;
        using (var tx = new Transaction(doc, "定位钢片参数族验收视图"))
        {
            tx.Start();
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            view = View3D.CreateIsometric(doc, type.Id); view.Name = "面基定位钢片 · 改参验证 " + DateTime.Now.ToString("HHmmss");
            view.DetailLevel = ViewDetailLevel.Fine; view.DisplayStyle = DisplayStyle.Shading;
            var one = instances[0]; var transform = one.GetTransform();
            view.SetOrientation(new ViewOrientation3D(transform.OfPoint(new XYZ(2, -2, 2)), transform.OfVector(new XYZ(1, 5, -2)).Normalize(), transform.OfVector(new XYZ(1, -1, -2)).Normalize()));
            view.IsolateElementsTemporary(new[] { one.Id, one.HostFace.ElementId }); view.ConvertTemporaryHideIsolateToPermanent();
            view.SetElementOverrides(one.HostFace.ElementId, new OverrideGraphicSettings().SetSurfaceTransparency(100));
            var box = one.get_BoundingBox(null);
            view.SetSectionBox(new BoundingBoxXYZ { Min = box.Min - new XYZ(.4, .4, .4), Max = box.Max + new XYZ(.4, .4, .4) }); view.IsSectionBoxActive = true;
            view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_SectionBox), true);
            tx.Commit();
        }
        ui.ActiveUIDocument.ActiveView = view; ui.ActiveUIDocument.Selection.SetElementIds(Array.Empty<ElementId>()); ui.ActiveUIDocument.RefreshActiveView();
        var bounds = instances[0].get_BoundingBox(null); var center = (bounds.Min + bounds.Max) / 2;
        var corners = new List<XYZ>();
        foreach (double x in new[] { bounds.Min.X, bounds.Max.X }) foreach (double y in new[] { bounds.Min.Y, bounds.Max.Y }) foreach (double z in new[] { bounds.Min.Z, bounds.Max.Z }) corners.Add(new XYZ(x, y, z));
        var horizontal = corners.Select(p => (p - center).DotProduct(view.RightDirection)).ToArray(); var vertical = corners.Select(p => (p - center).DotProduct(view.UpDirection)).ToArray();
        var viewport = ui.ActiveUIDocument.GetOpenUIViews().Single(v => v.ViewId == view.Id);
        viewport.ZoomAndCenterRectangle(center + view.RightDirection * (horizontal.Min() - .05) + view.UpDirection * (vertical.Min() - .05), center + view.RightDirection * (horizontal.Max() + .05) + view.UpDirection * (vertical.Max() + .05));
        ui.ActiveUIDocument.RefreshActiveView();
        string prefix = Path.Combine(App.Root, request["path"]?.GetValue<string>() ?? "evidence/family-library/plate-model"); Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        return new() { ["status"] = "PASS", ["view_id"] = view.Id.Value, ["capture_required_after_idle"] = true, ["zoom_corners"] = JsonSerializer.SerializeToNode(viewport.GetZoomCorners().Select(p => new[] { p.X, p.Y, p.Z })) };
    }
    private static JsonObject CaptureCurrent(UIApplication ui, JsonObject request)
    {
        string prefix = Path.Combine(App.Root, request["path"]?.GetValue<string>() ?? "evidence/family-library/final-plate-model"); Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        ui.ActiveUIDocument.Document.ExportImage(new ImageExportOptions { FilePath = prefix, ExportRange = ExportRange.VisibleRegionOfCurrentView, ZoomType = ZoomFitType.FitToPage, PixelSize = 1600, FitDirection = FitDirectionType.Horizontal, HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG, ImageResolution = ImageResolution.DPI_150 });
        return new() { ["status"] = "PASS", ["view_id"] = ui.ActiveUIDocument.ActiveView.Id.Value, ["files"] = JsonSerializer.SerializeToNode(Directory.GetFiles(Path.GetDirectoryName(prefix)!, Path.GetFileName(prefix) + "*.png")) };
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static T Control<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static JsonObject ChooseThroughUi(UIApplication ui, JsonObject request)
    {
        App.EnsureDispatcher(); SimpleColumnWindow.ShowFor(ui);
        var window = SimpleColumnWindow.Instance!;
        string[] ids = request["selected_ids"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
        bool cancel = request["cancel"]?.GetValue<bool>() == true;
        string? capture = request["capture_path"]?.GetValue<string>();
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var chooser = FamilyLibraryWindow.Instance ?? throw new InvalidOperationException("点击加载族后未打开选择界面。");
                foreach (var id in ids) Control<CheckBox>(chooser, "FamilyLibrary_Item_" + id).IsChecked = true;
                if (capture != null) chooser.CaptureScreenshot(Path.Combine(App.Root, capture));
                Control<Button>(chooser, cancel ? "FamilyLibrary_Cancel" : "FamilyLibrary_LoadSelected").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Control<Button>(window, "Simple_LoadFamily").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        return new() { ["status"] = "QUEUED", ["selected_ids"] = JsonSerializer.SerializeToNode(ids), ["cancel"] = cancel, ["before"] = Inventory(ui.ActiveUIDocument.Document) };
    }
    private static JsonObject UiState(UIApplication ui, JsonObject request)
    {
        var window = SimpleColumnWindow.Instance ?? throw new InvalidOperationException("配筋窗口未打开。");
        if (request["capture_path"] is JsonNode path) window.CaptureScreenshot(Path.Combine(App.Root, path.GetValue<string>()));
        return new()
        {
            ["status"] = "PASS", ["busy"] = window.IsBusy,
            ["last_load_result"] = window.LastFamilyLoadResult?.DeepClone(),
            ["input"] = JsonSerializer.SerializeToNode(window.Input, NativeEngine.JsonOptions),
            ["visible_buttons"] = JsonSerializer.SerializeToNode(Descendants(window).OfType<Button>().Where(b => b.IsVisible).Select(b => b.Content?.ToString())),
            ["bar_choices"] = JsonSerializer.SerializeToNode(Descendants(window).OfType<ComboBox>().Where(c => AutomationProperties.GetAutomationId(c).EndsWith("Type")).Select(c => new { id = AutomationProperties.GetAutomationId(c), count = c.Items.Count, selected = c.SelectedValue })),
            ["inventory"] = Inventory(ui.ActiveUIDocument.Document)
        };
    }
    private static JsonObject ConflictTest(UIApplication ui)
    {
        var doc = ui.ActiveUIDocument.Document;
        if (!doc.PathName.Contains("FamilyLibrary-Validation")) throw new InvalidOperationException("负例只能在族库验收模型中执行。");
        using var group = new TransactionGroup(doc, "族库冲突回滚测试"); group.Start();
        var existing = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().Single(t => t.Name == "HRB400 Φ20");
        using (var tx = new Transaction(doc, "注入同名直径冲突")) { tx.Start(); existing.BarModelDiameter = 21 / 304.8; tx.Commit(); }
        var before = new FilteredElementCollector(doc).GetElementCount();
        var response = FamilyLibraryService.Load(ui, ["HRB400_20", "HRB500_36"]);
        int after = new FilteredElementCollector(doc).GetElementCount(); group.RollBack();
        bool pass = response["status"]?.ToString() == "FAIL" && before == after && Math.Abs(existing.BarModelDiameter * 304.8 - 20) < .001;
        return new() { ["status"] = pass ? "PASS" : "FAIL", ["rejected_result"] = response, ["before_count"] = before, ["after_count"] = after, ["restored_model_mm"] = existing.BarModelDiameter * 304.8 };
    }
    private static JsonObject FakeFamilyTest(UIApplication ui)
    {
        var doc = ui.ActiveUIDocument.Document;
        if (!doc.PathName.Contains("FamilyLibrary-Validation")) throw new InvalidOperationException("负例只能在族库验收模型中执行。");
        string folder = Path.Combine(App.Runtime, "family-library", "fake-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FamilyLibraryService.PlateFile);
        var fake = ui.Application.NewFamilyDocument(@"__REVIT_METRIC_FAMILY_TEMPLATES__\基于面的公制常规模型.rft");
        try
        {
            using (var tx = new Transaction(fake, "构造同名面基负例")) { tx.Start(); fake.FamilyManager.NewType("缺少参数的同名族"); tx.Commit(); }
            fake.SaveAs(path);
        }
        finally { fake.Close(false); }
        var family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Single(f => f.Name == PositionPlateFamilyBuilder.FamilyName);
        var uidBefore = AllUniqueIds(doc);
        using var group = new TransactionGroup(doc, "同名面基族冲突验收"); group.Start();
        using (var tx = new Transaction(doc, "注入伪同名面基族"))
        {
            tx.Start(); family.Name = "PC_验收临时原族";
            if (!doc.LoadFamily(path, out var ignored)) throw new InvalidOperationException("未载入负例族。");
            tx.Commit();
        }
        int before = new FilteredElementCollector(doc).GetElementCount();
        var response = FamilyLibraryService.Load(ui, [FamilyLibraryService.PlateId]);
        int after = new FilteredElementCollector(doc).GetElementCount();
        group.RollBack();
        var uidAfter = AllUniqueIds(doc);
        bool pass = response["status"]?.ToString() == "FAIL" && before == after && uidBefore.SetEquals(uidAfter) && family.Name == PositionPlateFamilyBuilder.FamilyName;
        return new() { ["status"] = pass ? "PASS" : "FAIL", ["rejected_result"] = response, ["before_count"] = before, ["after_count"] = after, ["restored_all_unique_ids"] = uidBefore.SetEquals(uidAfter) };
    }
    private static HashSet<string> AllUniqueIds(Document doc) => new FilteredElementCollector(doc)
        .WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(), new ElementIsElementTypeFilter(true))).Select(e => e.UniqueId).ToHashSet();
    public static JsonObject Inventory(Document doc) => new()
    {
        ["status"] = "PASS", ["document_path"] = doc.PathName,
        ["bar_types"] = JsonSerializer.SerializeToNode(new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
            .Select(t => new { id = t.Id.Value, name = t.Name, nominal_mm = t.BarNominalDiameter * 304.8, model_mm = t.BarModelDiameter * 304.8, deformation = t.DeformationType.ToString(),
                parameters = t.Parameters.Cast<Parameter>().Select(p => new { name = p.Definition.Name, id = p.Id.Value, storage = p.StorageType.ToString(), value = p.AsValueString() }) })),
        ["families"] = JsonSerializer.SerializeToNode(new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Where(f => f.Name == PositionPlateFamilyBuilder.FamilyName)
            .Select(f => new { id = f.Id.Value, name = f.Name, placement = f.FamilyPlacementType.ToString(), hosting = f.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsInteger() })),
        ["element_count"] = new FilteredElementCollector(doc).GetElementCount()
    };
}
