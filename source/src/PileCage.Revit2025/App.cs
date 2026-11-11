using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PileCage;

public sealed class App : IExternalApplication
{
    private static readonly ConcurrentQueue<Action<UIApplication>> Actions = new();
    private static ExternalEvent? _event;
    private static bool _ownsTestQueue;
    private System.Threading.Timer? _timer;
    public static string Root { get; } = LocateRoot();
    public static string Runtime => Path.Combine(Root, "runtime");
    // Called only in a Revit API context, including a command loaded for validation.
    public static void EnsureDispatcher()
    {
        _event ??= ExternalEvent.Create(new Handler());
    }
    public static void Enqueue(Action<UIApplication> action)
    {
        if (_event == null) throw new InvalidOperationException("插件 ExternalEvent 尚未初始化，请重新启动 Revit。");
        Actions.Enqueue(action);
        _event.Raise();
    }
    public Result OnStartup(UIControlledApplication application)
    {
        Directory.CreateDirectory(Runtime);
        Directory.CreateDirectory(Path.Combine(Runtime, "inbox"));
        Directory.CreateDirectory(Path.Combine(Runtime, "outbox"));
        EnsureDispatcher();
        _ownsTestQueue = true;
        // Queue automation is opt-in. The marker must be present in this new workspace.
        // The timer only raises ExternalEvent; every document access is on Revit's API thread.
        _timer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (File.Exists(Path.Combine(Runtime, "enable-test-queue.flag")) &&
                    Directory.EnumerateFiles(Path.Combine(Runtime, "inbox"), "*.json").Any()) _event?.Raise();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }, null, 1500, 1000);
        RibbonConfiguration.Register(application);
        File.WriteAllText(Path.Combine(Runtime, "addin_loaded.json"), JsonSerializer.Serialize(new
        {
            utc = DateTime.UtcNow, assembly = Assembly.GetExecutingAssembly().Location,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(), revit = application.ControlledApplication.VersionNumber
        }, new JsonSerializerOptions { WriteIndented = true }));
        return Result.Succeeded;
    }
    public Result OnShutdown(UIControlledApplication application)
    {
        _timer?.Dispose(); _timer = null; _event?.Dispose(); _event = null;
        return Result.Succeeded;
    }
    private sealed class Handler : IExternalEventHandler
    {
        public void Execute(UIApplication application)
        {
            while (Actions.TryDequeue(out var action))
            {
                try { action(application); }
                catch (Exception ex) { TaskDialog.Show("灌注桩钢筋笼", ex.Message); }
            }
            if (_ownsTestQueue && File.Exists(Path.Combine(Runtime, "enable-test-queue.flag"))) ProcessQueue(application);
        }
        public string GetName() => "灌注桩钢筋笼 ExternalEvent";
    }
    private static void ProcessQueue(UIApplication application)
    {
        var file = Directory.EnumerateFiles(Path.Combine(Runtime, "inbox"), "*.json").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
        if (file == null) return;
        var active = file + ".running";
        JsonObject? result = null;
        try
        {
            File.Move(file, active);
            result = NativeEngine.Run(application, File.ReadAllText(active));
            var output = Path.Combine(Runtime, "outbox", Path.GetFileName(file));
            File.WriteAllText(output + ".tmp", result.ToJsonString(NativeEngine.JsonOptions));
            File.Move(output + ".tmp", output, true);
            File.Move(active, file + ".done", true);
        }
        catch (Exception ex)
        {
            result ??= new JsonObject { ["status"] = "FAIL", ["transaction_state"] = "not_started_or_report_unavailable" };
            result["queue_error"] = ex.ToString();
            File.WriteAllText(Path.Combine(Runtime, "outbox", Path.GetFileName(file)), result.ToJsonString(NativeEngine.JsonOptions));
            if (File.Exists(active)) File.Move(active, file + ".failed", true);
        }
    }
    private static string LocateRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pilecage.workspace"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("请保留插件 DLL 与 pilecage.workspace 的目录关系。");
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class ProbeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var result = NativeEngine.Probe(data.Application);
        TaskDialog.Show("原生钢筋技术小样", result.Message + "\n" + result.ReportPath);
        message = result.Message;
        return result.Success ? Result.Succeeded : Result.Failed;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class QueueCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        // This command provides a controlled manual execution entry point as well.
        App.Enqueue(_ => { });
        return Result.Succeeded;
    }
}
