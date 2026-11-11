using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace PileCage;

/// <summary>Controlled validation and recovery commands; not exposed on the user ribbon.</summary>
public static class UiRevisionAcceptance
{
    public static JsonObject Run(UIApplication ui, JsonObject request)
    {
        string operation = request["operation"]!.ToString();
        JsonObject result = operation switch
        {
            "revision_session" => Session(ui),
            "revision_open" => Open(ui, request),
            "revision_ribbon" => RibbonConfiguration.Snapshot(ui),
            "revision_ui" => InspectUi(request),
            "revision_ui_edit" => EditUi(request),
            "revision_plate_dialog" => PlateDialog(request),
            _ => throw new ArgumentException("Unknown UI revision operation.")
        };
        if (operation == "revision_ribbon" && request["capture_path"] is JsonNode capture) RibbonConfiguration.Capture(Workspace(capture.ToString()));
        result["operation"] = operation; result["utc"] = DateTime.UtcNow.ToString("o");
        result["assembly_path"] = typeof(UiRevisionAcceptance).Assembly.Location;
        return result;
    }

    private static JsonObject Session(UIApplication ui)
    {
        var docs = new JsonArray();
        foreach (Document doc in ui.Application.Documents)
        {
            if (doc.IsFamilyDocument) continue;
            var columns = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().ToArray();
            docs.Add(new JsonObject { ["path"] = doc.PathName, ["modified"] = doc.IsModified,
                ["rebar_count"] = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).GetElementCount(),
                ["columns"] = JsonSerializer.SerializeToNode(columns.Select(e => new { id = e.Id.Value, name = e.Name })),
                ["views"] = JsonSerializer.SerializeToNode(new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate).Select(v => new { id = v.Id.Value, name = v.Name })) });
        }
        return new JsonObject { ["status"] = "PASS", ["active_document"] = ui.ActiveUIDocument?.Document.PathName, ["active_view"] = ui.ActiveUIDocument?.ActiveView.Name, ["documents"] = docs };
    }

    private static JsonObject Open(UIApplication ui, JsonObject request)
    {
        string path = Workspace(request["path"]!.ToString());
        if (!File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() != ".rvt") throw new ArgumentException("Existing workspace RVT required.");
        // Open/activate only. Never close a document or discard unsaved work.
        var uidoc = ui.OpenAndActivateDocument(path);
        if (request["view_id"] is JsonNode viewId)
        {
            var view = uidoc.Document.GetElement(new ElementId(viewId.GetValue<long>())) as View3D ?? throw new ArgumentException("3D view not found.");
            uidoc.ActiveView = view;
            uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id)?.ZoomToFit();
        }
        if (request["select_host"] is JsonNode hostId) uidoc.Selection.SetElementIds([new ElementId(hostId.GetValue<long>())]);
        uidoc.RefreshActiveView(); return Session(ui);
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Control<T>(DependencyObject window, string id) where T : DependencyObject => Children(window).OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static JsonObject WindowSnapshot(Window window)
    {
        window.UpdateLayout(); var objects = Children(window).OfType<FrameworkElement>().Where(e => e.IsVisible).ToArray();
        return new JsonObject { ["status"] = "PASS", ["title"] = window.Title,
            ["labels"] = JsonSerializer.SerializeToNode(objects.OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct()),
            ["controls"] = JsonSerializer.SerializeToNode(objects.Where(e => !string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(e))).Select(e => new
            {
                id = AutomationProperties.GetAutomationId(e), type = e.GetType().Name, enabled = e.IsEnabled,
                value = e is TextBox t ? t.Text : e is ComboBox c ? c.Text : e is ContentControl cc ? cc.Content?.ToString() : null,
                read_only = e is TextBox tb && tb.IsReadOnly
            })) };
    }
    private static JsonObject InspectUi(JsonObject request)
    {
        var window = SimpleColumnWindow.Instance ?? throw new InvalidOperationException("Open Create Rebar first.");
        if (request["capture_path"] is JsonNode path) window.CaptureScreenshot(Workspace(path.ToString()));
        var result = WindowSnapshot(window); result["input"] = JsonSerializer.SerializeToNode(window.Input, NativeEngine.JsonOptions);
        result["last_result"] = JsonSerializer.SerializeToNode(window.LastResult, NativeEngine.JsonOptions);
        result["busy"] = window.IsBusy; return result;
    }
    private static JsonObject EditUi(JsonObject request)
    {
        var window = SimpleColumnWindow.Instance ?? throw new InvalidOperationException("Open Create Rebar first.");
        if (request["values"] is JsonObject values) foreach (var item in values) Control<TextBox>(window, "Simple_" + item.Key).Text = item.Value!.ToString();
        if (request["click"] is JsonNode button)
        {
            string id = "Simple_" + button.ToString();
            window.Dispatcher.BeginInvoke(new Action(() => Control<Button>(window, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        }
        return new JsonObject { ["status"] = "QUEUED", ["input"] = JsonSerializer.SerializeToNode(window.Input, NativeEngine.JsonOptions) };
    }
    private static JsonObject PlateDialog(JsonObject request)
    {
        var owner = SimpleColumnWindow.Instance ?? throw new InvalidOperationException("Open Create Rebar first.");
        string receipt = Workspace(request["receipt_path"]!.ToString());
        owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            var started = DateTime.UtcNow;
            var timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                if (PositionPlateWindow.Instance == null && (DateTime.UtcNow - started).TotalSeconds < 30) return;
                timer.Stop();
                try
                {
                    var dialog = PositionPlateWindow.Instance ?? throw new InvalidOperationException("Positioning Plate button did not open a dialog.");
                    if (request["enable"]?.GetValue<bool>() == true) Control<CheckBox>(dialog, "PositionPlate_Enable").IsChecked = true;
                    if (request["values"] is JsonObject values) foreach (var item in values) Control<TextBox>(dialog, "PositionPlate_" + item.Key).Text = item.Value!.ToString();
                    if (request["capture_path"] is JsonNode path) dialog.CaptureScreenshot(Workspace(path.ToString()));
                    var result = WindowSnapshot(dialog); result["input"] = JsonSerializer.SerializeToNode(dialog.Input, NativeEngine.JsonOptions);
                    result["preview"] = dialog.LastPreview?.DeepClone();
                    File.WriteAllText(receipt, result.ToJsonString(NativeEngine.JsonOptions));
                    var finish = Control<Button>(dialog, request["save"]?.GetValue<bool>() == true ? "PositionPlate_ConfirmSettings" : "PositionPlate_CancelSettings");
                    if (!finish.IsEnabled) throw new InvalidOperationException("Requested dialog action is disabled.");
                    finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { File.WriteAllText(receipt, new JsonObject { ["status"] = "FAIL", ["error"] = ex.ToString() }.ToJsonString(NativeEngine.JsonOptions)); PositionPlateWindow.Instance?.Close(); }
            };
            timer.Start();
            Control<Button>(owner, "Simple_PositionPlateSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        return new JsonObject { ["status"] = "QUEUED", ["receipt_path"] = receipt };
    }
    private static string Workspace(string path)
    {
        string root = Path.GetFullPath(App.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path must stay in this workspace.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!); return full;
    }
}
