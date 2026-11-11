using Autodesk.Revit.UI;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PileCage;

/// <summary>Shared startup and live-update ribbon definition.</summary>
public static class RibbonConfiguration
{
    public const string TabName = "Rebar Modeling";
    public const string PanelName = "Parametric Rebar Design System";
    public const string ButtonName = "PileCage.CreateRebar";

    public static void Register(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        Configure(app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName)
            ?? app.CreateRibbonPanel(TabName, PanelName));
        HideOldPanel(app.GetRibbonPanels());
    }

    public static JsonObject Register(UIApplication app)
    {
        try { app.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        Configure(app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName)
            ?? app.CreateRibbonPanel(TabName, PanelName));
        HideOldPanel(app.GetRibbonPanels());
        // These old tunnel entry points are disabled in their own backed-up manifests.
        // Hide the already-loaded tab too, without touching unrelated add-in tabs.
        foreach (object tab in Items(Ribbon(), "Tabs"))
        {
            string title = Value(tab, "Title")?.ToString() ?? "";
            if (title == "盾构建模") Set(tab, "IsVisible", false);
            if (title == TabName) { Set(tab, "IsVisible", true); Set(tab, "IsActive", true); }
        }
        return Snapshot(app);
    }

    private static void Configure(RibbonPanel panel)
    {
        panel.Visible = true;
        var button = panel.GetItems().OfType<PushButton>().SingleOrDefault(b => b.Name == ButtonName)
            ?? (PushButton)panel.AddItem(new PushButtonData(ButtonName, "Create Rebar", typeof(App).Assembly.Location, "PileCage.SimpleColumnCommand"));
        button.AssemblyName = typeof(App).Assembly.Location;
        button.ClassName = "PileCage.SimpleColumnCommand";
        button.ItemText = "Create Rebar";
        button.ToolTip = "Create Rebar";
        button.LargeImage = Icon("rebar-32.png"); button.Image = Icon("rebar-16.png");
        button.Visible = true;
    }

    private static BitmapImage Icon(string name)
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("PileCage.Icons." + name)
            ?? throw new FileNotFoundException("Embedded rebar icon is missing.", name);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    private static void HideOldPanel(IEnumerable<RibbonPanel> panels)
    {
        foreach (var panel in panels.Where(p => p.Name == "圆形柱配筋")) panel.Visible = false;
    }

    internal static object? Value(object? instance, string property) => instance?.GetType().GetProperty(property)?.GetValue(instance);
    private static void Set(object instance, string property, object value) => instance.GetType().GetProperty(property)?.SetValue(instance, value);
    internal static IEnumerable<object> Items(object? parent, string property) => Value(parent, property) is IEnumerable list ? list.Cast<object>() : [];
    private static object? Ribbon() => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Autodesk.Windows.ComponentManager")).FirstOrDefault(t => t != null)?.GetProperty("Ribbon", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);

    public static JsonObject Snapshot(UIApplication app)
    {
        var tabs = new JsonArray();
        foreach (object tab in Items(Ribbon(), "Tabs"))
        {
            var panels = new JsonArray();
            foreach (object panel in Items(tab, "Panels"))
            {
                object? source = Value(panel, "Source"); var items = new JsonArray();
                foreach (object item in Items(source, "Items")) items.Add(new JsonObject
                { ["id"] = Value(item, "Id")?.ToString(), ["text"] = Value(item, "Text")?.ToString(), ["visible"] = Value(item, "IsVisible")?.ToString(), ["large_image"] = Value(item, "LargeImage") != null });
                panels.Add(new JsonObject { ["title"] = Value(source, "Title")?.ToString(), ["visible"] = Value(panel, "IsVisible")?.ToString(), ["items"] = items });
            }
            tabs.Add(new JsonObject { ["title"] = Value(tab, "Title")?.ToString(), ["visible"] = Value(tab, "IsVisible")?.ToString(), ["active"] = Value(tab, "IsActive")?.ToString(), ["panels"] = panels });
        }
        var ours = app.GetRibbonPanels(TabName).Single(p => p.Name == PanelName);
        var button = ours.GetItems().OfType<PushButton>().Single(b => b.Name == ButtonName);
        return new JsonObject { ["status"] = "PASS", ["tab"] = TabName, ["panel"] = ours.Name, ["button"] = button.ItemText,
            ["assembly"] = button.AssemblyName, ["class"] = button.ClassName, ["visible"] = button.Visible,
            ["icon_width"] = (button.LargeImage as BitmapSource)?.PixelWidth, ["tabs"] = tabs };
    }

    public static void Capture(string path)
    {
        if (Ribbon() is not FrameworkElement element || element.ActualWidth <= 0) throw new InvalidOperationException("Revit ribbon is not ready.");
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var stream = File.Create(path); encoder.Save(stream);
    }
}
