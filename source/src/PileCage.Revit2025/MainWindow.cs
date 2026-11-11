using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Microsoft.Win32;
using WpfColor = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using FormattedText = System.Windows.Media.FormattedText;
using Binding = System.Windows.Data.Binding;

namespace PileCage;
internal static class Ui
{
    public static readonly SolidColorBrush Ink = Brush("#172E43"), Muted = Brush("#667887"), Accent = Brush("#087E8B"), Line = Brush("#DCE5EC"), Back = Brush("#F2F6F9");
    public static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public static TextBlock Text(string text, double size = 12, Brush? color = null, bool bold = false) => new() { Text = text, FontSize = size, Foreground = color ?? Ink, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    public static Button Button(string text, Action action, bool primary = false) { var b = new Button { Content = text, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0), MinHeight = 34, Background = primary ? Accent : Brushes.White, Foreground = primary ? Brushes.White : Ink, BorderBrush = primary ? Accent : Line, BorderThickness = new Thickness(1), Cursor = Cursors.Hand }; b.Click += (_, _) => action(); return b; }
    public static Border Card(UIElement child, Thickness? padding = null) => new() { Child = child, Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = padding ?? new Thickness(14), Margin = new Thickness(0, 0, 0, 12) };
    public static TextBox Box(string text = "") => new() { Text = text, Padding = new Thickness(7, 5, 7, 5), MinHeight = 30, BorderBrush = Line, BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center, Foreground = Ink };
    public static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    public static bool Number(string value, out double number) => (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number)) && double.IsFinite(number);
}
public sealed record BarTypeChoice(long Id, string Name, double Diameter) { public override string ToString() => Id == 0 ? "按公称直径创建／复用受管类型" : $"{Name} · Ø{Ui.N(Diameter)} mm"; }
internal sealed class ZoneRow : INotifyPropertyChanged
{
    private string _id = "", _length = "", _pitch = "", _range = "";
    public string Id { get => _id; set { _id = value; Changed(); } }
    public string Length { get => _length; set { _length = value; Changed(); } }
    public string Pitch { get => _pitch; set { _pitch = value; Changed(); } }
    public string Range { get => _range; set { if (_range == value) return; _range = value; PropertyChanged?.Invoke(this, new(nameof(Range))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
}

[Transaction(TransactionMode.Manual)]
public sealed class ShowWindowCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        App.EnsureDispatcher();
        MainWindow.ShowFor(data.Application);
        return Result.Succeeded;
    }
}
public sealed class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }
    private CageInput _input = new();
    private readonly HashSet<string> _invalid = [];
    private readonly ObservableCollection<ZoneRow> _zones = [];
    private List<BarTypeChoice> _types = [new(0, "受管公称直径", 0)];
    private readonly Dictionary<string, TextBox> _numeric = [];
    private readonly Dictionary<string, ComboBox> _typeBoxes = [];
    private readonly StackPanel _parameters = new();
    private readonly CageDiagram _diagram = new();
    private readonly TextBlock _state = Ui.Text("正在读取当前项目钢筋类型…", 12), _coverage = Ui.Text("", 11), _source = Ui.Text("演示参数 · 非工程设计", 11);
    private TextBlock _zoneTotal = Ui.Text(""), _ringTotal = Ui.Text(""), _coverLabel = Ui.Text(""), _locationLabel = Ui.Text("");
    private DataGrid? _zoneGrid;
    private Button _generate = null!, _update = null!;
    private bool _building, _busy;
    private string _catalogDocument = "";
    private string _mappedTypesDocument = "";
    public CageInput Input => _input.Clone();
    public string FocusKey { get; private set; } = "Diameter";
    public MainWindow()
    {
        Current = this; Title = "灌注桩钢筋笼参数化建模"; Width = 1100; Height = 760; MinWidth = 950; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { string options = Path.Combine(Files.Root, "config", "ui-options.json"); if (File.Exists(options)) { using var json = JsonDocument.Parse(File.ReadAllText(options)); if (json.RootElement.TryGetProperty("ParameterDirectory", out var dir) && !string.IsNullOrWhiteSpace(dir.GetString())) Files.ParameterDirectory = Path.GetFullPath(dir.GetString()!); } } catch { /* Bad optional path never prevents opening the modelling UI. */ }
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); FontSize = 12; Background = Ui.Back; Foreground = Ui.Ink;
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var head = new Grid { Margin = new Thickness(22, 17, 18, 15) }; head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var titles = new StackPanel(); titles.Children.Add(Ui.Text("灌注桩钢筋笼参数化建模", 23, Ui.Ink, true)); var sub = Ui.Text("Revit 2025   /   直轴 · 圆截面 · 竖向桩", 11, Ui.Muted); sub.Margin = new Thickness(0, 5, 0, 0); titles.Children.Add(sub); head.Children.Add(titles);
        var settings = Ui.Button("⚙  设置", OpenSettings); settings.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(settings, 1); head.Children.Add(settings); root.Children.Add(head);
        var body = new Grid { Margin = new Thickness(18, 0, 18, 0) }; body.ColumnDefinitions.Add(new() { Width = new GridLength(40, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(60, GridUnitType.Star) }); Grid.SetRow(body, 1);
        var scroll = new ScrollViewer { Content = _parameters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 5, 0) }; body.Children.Add(scroll);
        var preview = new Grid(); preview.RowDefinitions.Add(new() { Height = GridLength.Auto }); preview.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); preview.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var pHead = new StackPanel { Margin = new Thickness(0, 0, 0, 6) }; pHead.Children.Add(Ui.Text("参数位置示意", 16, Ui.Ink, true)); _source.Margin = new Thickness(0, 5, 0, 0); pHead.Children.Add(_source); preview.Children.Add(pHead); Grid.SetRow(_diagram, 1); preview.Children.Add(_diagram);
        var note = Ui.Text("聚焦输入框可高亮对应构件。示意，非等比例；实际尺寸与对象由生成后回读核对。", 11, Ui.Muted); note.Margin = new Thickness(0, 5, 0, 0); Grid.SetRow(note, 2); preview.Children.Add(note);
        var previewCard = Ui.Card(preview); previewCard.Margin = new Thickness(0, 0, 0, 12); Grid.SetColumn(previewCard, 2); body.Children.Add(previewCard); root.Children.Add(body);
        var foot = new StackPanel { Margin = new Thickness(18, 0, 18, 14) }; _state.MaxHeight = 50; _state.Margin = new Thickness(0, 0, 0, 4); foot.Children.Add(_state); _coverage.Margin = new Thickness(0, 0, 0, 10); foot.Children.Add(_coverage);
        var buttons = new Grid(); buttons.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); var left = new StackPanel { Orientation = Orientation.Horizontal }; left.Children.Add(Ui.Button("载入参数", LoadFile)); left.Children.Add(Ui.Button("保存参数", SaveFile)); buttons.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal }; _generate = Ui.Button("生成模型", () => Execute(false), true); _update = Ui.Button("更新本笼", () => Execute(true)); _update.IsEnabled = false; right.Children.Add(_generate); right.Children.Add(_update); right.Children.Add(Ui.Button("关闭", Close)); Grid.SetColumn(right, 1); buttons.Children.Add(right); foot.Children.Add(buttons); Grid.SetRow(foot, 2); root.Children.Add(foot); Content = root;
        RebuildParameters(); Loaded += (_, _) => RefreshCatalog(); Closing += (_, e) => { if (_busy) { e.Cancel = true; _state.Text = "建模事务正在执行，请等待回读结果后关闭。"; } }; Closed += (_, _) => { if (ReferenceEquals(Current, this)) Current = null; };
    }
    public static void ShowFor(UIApplication app) { if (Current != null) { Current.Activate(); return; } var w = new MainWindow(); new WindowInteropHelper(w).Owner = app.MainWindowHandle; w.Show(); }
    public void LoadInput(CageInput input)
    {
        if (_busy) throw new InvalidOperationException("建模执行中，暂不能载入另一组参数。");
        var candidate = input.Clone(); candidate.Pending ??= []; candidate.Zones ??= []; candidate.AdditionalData ??= []; candidate.SourceKind ??= "UNKNOWN"; candidate.CageId ??= ""; candidate.CageGuid ??= "";
        if (candidate.Zones.Any(z => z is null) || candidate.Pending.Any(s => s is null)) throw new InvalidDataException("区段与待确认列表不能包含 null 条目。");
        if (!Enum.IsDefined(candidate.CoverBasis)) throw new InvalidDataException("未知保护层测量基准。");
        if (candidate.Zones.Count > 100) throw new InvalidDataException("区段数超过 100 段容量限制。");
        _mappedTypesDocument = candidate.AdditionalData.TryGetValue("_TypeDocumentUniqueId", out var mapped) && mapped.ValueKind == JsonValueKind.String ? mapped.GetString() ?? "" : "";
        _input = candidate; RebuildParameters(); _state.Text = "已载入参数。预览不会写入 Revit；请检查来源与待确认项。";
    }
    public void CaptureScreenshot(string path) { Dispatcher.Invoke(() => { UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(this); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var stream = File.Create(path); encoder.Save(stream); }); }
    public void FocusParameter(string key) { if (_numeric.TryGetValue(key, out var box)) { box.Focus(); box.SelectAll(); } else { FocusKey = key; RefreshPreview(); } }
    private StackPanel Group(string number, string name) { var panel = new StackPanel(); panel.Children.Add(Ui.Text(number + "  " + name, 14, Ui.Ink, true)); panel.Children.Add(new Border { Background = Ui.Line, Height = 1, Margin = new Thickness(0, 9, 0, 9) }); _parameters.Children.Add(Ui.Card(panel)); return panel; }
    private Grid Row(string label, UIElement input, string unit = "") { var row = new Grid { Margin = new Thickness(0, 0, 0, 7) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(110) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(27) }); var name = Ui.Text(label); name.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(name); Grid.SetColumn(input, 1); row.Children.Add(input); var u = Ui.Text(unit, 10, Ui.Muted); u.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(u, 2); row.Children.Add(u); return row; }
    private TextBox Number(StackPanel group, string label, string key, double value, Action<double> set, bool integer = false)
    {
        var b = Ui.Box(Ui.N(value)); System.Windows.Automation.AutomationProperties.SetAutomationId(b, "Param_" + key); _numeric[key] = b; group.Children.Add(Row(label, b, integer ? "根" : "mm")); b.GotKeyboardFocus += (_, _) => { FocusKey = key; RefreshPreview(); };
        b.TextChanged += (_, _) => { if (_building) return; if (!Ui.Number(b.Text, out var n) || integer && (n != Math.Truncate(n) || n < int.MinValue || n > int.MaxValue)) { _invalid.Add(key); b.BorderBrush = Ui.Brush("#CC4854"); b.ToolTip = integer ? "请输入有限整数。" : "请输入有限数值，单位 mm。"; } else { _invalid.Remove(key); b.BorderBrush = Ui.Line; b.ToolTip = null; set(n); } RefreshPreview(); }; return b;
    }
    private Expander More(StackPanel group) { var e = new Expander { Header = "更多定位参数（相对桩底 z=0，向上为正）", Margin = new Thickness(0, 3, 0, 2), FontSize = 11 }; group.Children.Add(e); e.Content = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; return e; }
    private void TypeBox(StackPanel group, string key, long selected, Action<long, double> set)
    {
        var combo = new ComboBox { ItemsSource = _types, SelectedValuePath = nameof(BarTypeChoice.Id), SelectedValue = selected, MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) }; combo.ToolTip = "选择实际类型时同步公称直径。受管类型只声明直径，不推定钢筋强度等级。"; if (combo.SelectedIndex < 0) combo.SelectedIndex = 0; _typeBoxes[key] = combo; group.Children.Add(combo);
        combo.SelectionChanged += (_, _) => { if (_building || combo.SelectedItem is not BarTypeChoice t) return; _building = true; _mappedTypesDocument = _catalogDocument; set(t.Id, t.Diameter); if (t.Id != 0 && _numeric.TryGetValue(key, out var box)) { box.Text = Ui.N(t.Diameter); box.BorderBrush = Ui.Line; box.ToolTip = null; _invalid.Remove(key); } _building = false; RefreshPreview(); };
    }
    private void RebuildParameters()
    {
        _building = true; _parameters.Children.Clear(); _numeric.Clear(); _typeBoxes.Clear(); _invalid.Clear(); _zones.Clear();
        var a = Group("01", "基础与定位参数"); var id = Ui.Box(_input.CageId); id.TextChanged += (_, _) => { _input.CageId = id.Text; RefreshPreview(); }; a.Children.Add(Row("钢筋笼编号", id));
        Number(a, "桩径  D", "Diameter", _input.Diameter, v => _input.Diameter = v); Number(a, "桩体几何长度  H", "Height", _input.Height, v => _input.Height = v); Number(a, "保护层  c", "Cover", _input.Cover, v => _input.Cover = v); _coverLabel = Ui.Text("", 10, Ui.Muted); _coverLabel.Margin = new Thickness(0, 0, 0, 9); a.Children.Add(_coverLabel);
        var place = new StackPanel { Orientation = Orientation.Horizontal }; place.Children.Add(Ui.Button("选择已有宿主", PickHost)); place.Children.Add(Ui.Button("拾取新建位置", PickPoint)); a.Children.Add(place); _locationLabel = Ui.Text("", 10, Ui.Muted); _locationLabel.Margin = new Thickness(0, 7, 0, 0); a.Children.Add(_locationLabel);
        var pos = new Expander { Header = "坐标与来源", FontSize = 11, Margin = new Thickness(0, 8, 0, 0) }; var pp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; Number(pp, "内部坐标  X", "X", _input.X, v => _input.X = v); Number(pp, "内部坐标  Y", "Y", _input.Y, v => _input.Y = v); Number(pp, "桩底坐标  Z", "Z", _input.Z, v => _input.Z = v); pp.Children.Add(Ui.Text("坐标基于 Revit 内部原点；H 为建模几何长度。", 10, Ui.Muted)); if (_input.Pending.Count > 0) pp.Children.Add(Ui.Text("待确认：\n" + string.Join("\n", _input.Pending), 11, Ui.Brush("#AD5F16"))); if (_input.AdditionalData.TryGetValue("_SourceEvidence", out var evidence)) pp.Children.Add(Ui.Text("来源证据（保留随参数保存）：\\n".Replace("\\n", "\n") + evidence.ToString(), 10, Ui.Muted)); pos.Content = pp; a.Children.Add(pos);
        var b = Group("02", "纵向主筋"); Number(b, "主筋数量  n", "MainCount", _input.MainCount, v => _input.MainCount = (int)v, true); Number(b, "主筋直径  d₁", "MainDiameter", _input.MainDiameter, v => { _input.MainDiameter = v; _input.MainTypeId = 0; ResetType("MainDiameter"); }); TypeBox(b, "MainDiameter", _input.MainTypeId, (tid, d) => { _input.MainTypeId = tid; if (tid != 0) _input.MainDiameter = d; }); var bm = (StackPanel)More(b).Content; Number(bm, "主筋起点  z₁", "MainStart", _input.MainStart, v => _input.MainStart = v); Number(bm, "主筋终点  z₂", "MainEnd", _input.MainEnd, v => _input.MainEnd = v); bm.Children.Add(Ui.Text("直段模型；未添加搭接、弯钩或锚固构造。", 10, Ui.Muted));
        var c = Group("03", "外螺旋箍筋"); Number(c, "螺旋筋直径  d₂", "SpiralDiameter", _input.SpiralDiameter, v => { _input.SpiralDiameter = v; _input.SpiralTypeId = 0; ResetType("SpiralDiameter"); }); TypeBox(c, "SpiralDiameter", _input.SpiralTypeId, (tid, d) => { _input.SpiralTypeId = tid; if (tid != 0) _input.SpiralDiameter = d; }); Number(c, "螺旋起点  zₛ", "SpiralStart", _input.SpiralStart, v => _input.SpiralStart = v);
        foreach (var z in _input.Zones) { var zr = new ZoneRow { Id = z.Id, Length = Ui.N(z.Length), Pitch = Ui.N(z.Pitch) }; zr.PropertyChanged += ZoneChanged; _zones.Add(zr); }
        _zoneGrid = new DataGrid { ItemsSource = _zones, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 90, MaxHeight = 240, RowHeight = 29, ColumnHeaderHeight = 31, FontSize = 11, BorderBrush = Ui.Line, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Ui.Line, Background = Brushes.White, SelectionMode = DataGridSelectionMode.Single };
        _zoneGrid.Columns.Add(new DataGridTextColumn { Header = "区段", Width = new DataGridLength(0.7, DataGridLengthUnitType.Star), Binding = new Binding(nameof(ZoneRow.Id)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } }); _zoneGrid.Columns.Add(new DataGridTextColumn { Header = "Lᵢ (mm)", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding(nameof(ZoneRow.Length)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } }); _zoneGrid.Columns.Add(new DataGridTextColumn { Header = "pᵢ (mm)", Width = new DataGridLength(0.85, DataGridLengthUnitType.Star), Binding = new Binding(nameof(ZoneRow.Pitch)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } }); _zoneGrid.Columns.Add(new DataGridTextColumn { Header = "起 → 止", Width = new DataGridLength(1.45, DataGridLengthUnitType.Star), IsReadOnly = true, Binding = new Binding(nameof(ZoneRow.Range)) }); _zoneGrid.GotKeyboardFocus += (_, _) => { FocusKey = "Zones"; RefreshPreview(); }; c.Children.Add(_zoneGrid);
        var zb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) }; zb.Children.Add(Ui.Button("＋ 增加区段", AddZone)); zb.Children.Add(Ui.Button("－ 删除区段", DeleteZone)); c.Children.Add(zb); _zoneTotal = Ui.Text("", 10, Ui.Muted); c.Children.Add(_zoneTotal);
        var d = Group("04", "内加劲环"); var enabled = new CheckBox { Content = "生成内加劲环", IsChecked = _input.RingsEnabled, Margin = new Thickness(0, 0, 0, 10) }; enabled.Checked += (_, _) => { _input.RingsEnabled = true; RefreshPreview(); }; enabled.Unchecked += (_, _) => { _input.RingsEnabled = false; RefreshPreview(); }; d.Children.Add(enabled); Number(d, "内环钢筋直径  d₃", "RingDiameter", _input.RingDiameter, v => { _input.RingDiameter = v; _input.RingTypeId = 0; ResetType("RingDiameter"); }); TypeBox(d, "RingDiameter", _input.RingTypeId, (tid, v) => { _input.RingTypeId = tid; if (tid != 0) _input.RingDiameter = v; }); Number(d, "布置间距  s", "RingSpacing", _input.RingSpacing, v => _input.RingSpacing = v); var dm = (StackPanel)More(d).Content; Number(dm, "首道位置  zᵣ", "RingStart", _input.RingStart, v => _input.RingStart = v); Number(dm, "末端布置范围", "RingEnd", _input.RingEnd, v => _input.RingEnd = v); _ringTotal = Ui.Text("", 10, Ui.Muted); d.Children.Add(_ringTotal);
        _building = false; RefreshPreview();
    }
    private void ResetType(string key) { if (_typeBoxes.TryGetValue(key, out var combo) && combo.SelectedValue is long id && id != 0) { _building = true; combo.SelectedValue = 0L; _building = false; } }
    private void ZoneChanged(object? sender, PropertyChangedEventArgs e) { if (_building || e.PropertyName == nameof(ZoneRow.Range)) return; CommitZones(); }
    private void CommitZones()
    {
        var candidate = new List<ZoneInput>(); bool valid = true; double at = _input.SpiralStart;
        foreach (var z in _zones) { if (!Ui.Number(z.Length, out var len) || !Ui.Number(z.Pitch, out var pitch)) { valid = false; z.Range = "数值待修正"; continue; } candidate.Add(new ZoneInput { Id = z.Id, Length = len, Pitch = pitch }); z.Range = $"{Ui.N(at)} → {Ui.N(at + len)}"; at += len; }
        if (valid) { _input.Zones = candidate; _invalid.Remove("Zones"); } else _invalid.Add("Zones"); RefreshPreview();
    }
    private void AddZone() { if (_zones.Count >= 100) return; var i = 1; while (_zones.Any(z => z.Id == "Z" + i)) i++; var z = new ZoneRow { Id = "Z" + i, Length = "500", Pitch = "200" }; z.PropertyChanged += ZoneChanged; _zones.Add(z); CommitZones(); _zoneGrid?.ScrollIntoView(z); }
    private void DeleteZone() { if (_zones.Count <= 1) { _state.Text = "至少保留一个螺旋区段。"; return; } var z = _zoneGrid?.SelectedItem as ZoneRow ?? _zones.Last(); _zones.Remove(z); CommitZones(); }
    private void RefreshPreview()
    {
        if (_building) return; _diagram.Input = _input; _diagram.FocusKey = FocusKey; _diagram.InvalidateVisual(); _source.Text = _input.SourceKind == "DEMONSTRATION" ? "演示参数 · 非工程设计" : $"工程参数 · {(_input.EngineeringConfirmed && _input.Pending.Count == 0 ? "来源已确认" : "含待确认字段，禁止生成")}";
        _coverLabel.Text = "测量基准：" + (_input.CoverBasis == CoverBasis.OutermostSurface ? "最外层钢筋表面（外螺旋筋）" : "纵向主筋外表面"); _locationLabel.Text = (_input.CreateHost ? "新建圆形混凝土宿主" : "已有宿主 ID " + _input.HostId) + $"  ·  ({Ui.N(_input.X)}, {Ui.N(_input.Y)}, {Ui.N(_input.Z)}) mm";
        var end = _input.SpiralStart + _input.Zones.Sum(z => z.Length); _zoneTotal.Text = $"共 {_input.Zones.Count} 段 · 总布置长度 {Ui.N(end - _input.SpiralStart)} mm\n范围 {Ui.N(_input.SpiralStart)} → {Ui.N(end)} mm；边界继承累计转角。";
        double at = _input.SpiralStart; foreach (var z in _zones) if (Ui.Number(z.Length, out var len)) { z.Range = $"{Ui.N(at)} → {Ui.N(at + len)}"; at += len; }
        var count = Math.Max(0, CagePlanner.RingCount(_input)); var last = count > 0 ? Ui.N(_input.RingStart + (count - 1) * _input.RingSpacing) : "—"; _ringTotal.Text = $"自动数量：{count} 道 · 末道：{last} mm\n按固定间距布置；余长不足一间距时，不增补末道。";
        var errs = CagePlanner.Validate(_input); List<RuleFinding> rules; try { rules = RuleChecker.Check(_input, RuleStore.Load()); } catch (Exception ex) { rules = [new() { Severity = "Block", Message = "规则配置无法读取：" + ex.Message }]; }
        _coverage.Text = $"检查覆盖：{rules.Count(r => r.Severity == "Unchecked")} 项未完成核查 · {rules.Count(r => r.Severity == "Warn")} 项提示 · {rules.Count(r => r.Severity == "Block")} 项阻止" + (_invalid.Count > 0 ? $"  |  {_invalid.Count} 处数值输入待修正" : errs.Count > 0 ? "  |  输入检查：" + errs[0] : "  |  几何输入有效"); _coverage.Foreground = _invalid.Count > 0 || errs.Count > 0 ? Ui.Brush("#A95E1B") : Ui.Muted;
        _generate.Content = string.IsNullOrWhiteSpace(_input.CageGuid) ? "生成模型" : "生成模型（本笼已建）"; _generate.IsEnabled = !_busy; _update.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(_input.CageGuid); _parameters.IsEnabled = !_busy;
    }
    private void RefreshCatalog()
    {
        try { App.Enqueue(app => { try { var doc = app.ActiveUIDocument?.Document; var items = new List<BarTypeChoice> { new(0, "受管公称直径", 0) }; if (doc != null) items.AddRange(NativeEngine.GetBarTypes(app).Select(t => new BarTypeChoice(t.Id, t.Name, t.Diameter))); Dispatcher.Invoke(() => { _catalogDocument = doc?.ProjectInformation.UniqueId ?? ""; _types = items; RebuildParameters(); _state.Text = doc == null ? "请先打开 Revit 项目，然后生成。" : $"就绪 · 已读取 {items.Count - 1} 个实际钢筋类型。"; }); } catch (Exception ex) { Dispatcher.Invoke(() => _state.Text = "读取类型失败：" + ex.Message); } }); } catch (Exception ex) { _state.Text = ex.Message; }
    }
    private void OpenSettings() { if (_busy) return; var settings = new SettingsWindow(_input.Clone(), _types) { Owner = this }; if (settings.ShowDialog() == true) { _input = settings.Result; _mappedTypesDocument = _catalogDocument; RebuildParameters(); _state.Text = "设置已保存；基准和类型已同步到参数与示意。"; } }
    private void LoadFile()
    {
        if (_busy) return; try { Directory.CreateDirectory(Files.ParameterDirectory); var d = new OpenFileDialog { Filter = "钢筋笼参数 (*.json)|*.json", InitialDirectory = Files.ParameterDirectory }; if (d.ShowDialog(this) != true) return; var p = JsonSerializer.Deserialize<CageInput>(File.ReadAllText(d.FileName), Files.Json) ?? throw new InvalidDataException("参数文件为空。"); LoadInput(p); } catch (Exception ex) { MessageBox.Show(this, "无法载入参数：\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void SaveFile()
    {
        try { _zoneGrid?.CommitEdit(DataGridEditingUnit.Cell, true); if (_invalid.Count > 0) throw new InvalidDataException("仍有无效数值，请先修正后再保存。"); Directory.CreateDirectory(Files.ParameterDirectory); var d = new SaveFileDialog { Filter = "钢筋笼参数 (*.json)|*.json", InitialDirectory = Files.ParameterDirectory, FileName = _input.CageId + ".json" }; if (d.ShowDialog(this) != true) return; _input.AdditionalData["_TypeDocumentUniqueId"] = JsonSerializer.SerializeToElement(_mappedTypesDocument); File.WriteAllText(d.FileName, JsonSerializer.Serialize(_input, Files.Json)); _state.Text = "参数已保存：" + d.FileName; } catch (Exception ex) { MessageBox.Show(this, "无法保存：\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Execute(bool update)
    {
        if (_busy) return; _zoneGrid?.CommitEdit(DataGridEditingUnit.Cell, true); _zoneGrid?.CommitEdit(DataGridEditingUnit.Row, true);
        if (_invalid.Count > 0) { MessageBox.Show(this, "存在无效数值。请修正红框或分区表中的输入。", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        List<RuleFinding> findings; try { findings = RuleChecker.Check(_input, RuleStore.Load()); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "规则配置无效"); return; }
        var errors = CagePlanner.Validate(_input); errors.AddRange(findings.Where(f => f.Severity == "Block").Select(f => f.Message)); if (errors.Count > 0) { MessageBox.Show(this, string.Join("\n", errors), "无法生成", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var warnings = findings.Where(f => f.Severity == "Warn").Select(f => f.Message).ToList(); if (warnings.Count > 0 && MessageBox.Show(this, string.Join("\n", warnings) + "\n\n按当前参数继续建模？", "请确认推荐性检查提示", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (update && string.IsNullOrWhiteSpace(_input.CageGuid)) return; var request = _input.Clone(); _busy = true; RefreshPreview(); _state.Text = "正在执行事务并从 Revit 回读，请稍候…";
        try { App.Enqueue(app => { NativeResult? result = null; Exception? error = null; try { if ((request.MainTypeId != 0 || request.SpiralTypeId != 0 || request.RingTypeId != 0) && app.ActiveUIDocument.Document.ProjectInformation.UniqueId != _mappedTypesDocument) throw new InvalidOperationException("钢筋类型的文档归属未确认或当前项目已切换；请打开设置，重新确认当前项目的实际类型映射。"); result = NativeEngine.Apply(app, request, acceptWarnings: warnings.Count > 0); } catch (Exception ex) { error = ex; } Dispatcher.Invoke(() => { _busy = false; if (result?.Success == true) { _input.CageGuid = result.CageGuid; _input.HostId = request.CreateHost ? 0 : result.HostId; _input.HostDocumentUniqueId = app.ActiveUIDocument.Document.ProjectInformation.UniqueId; _state.Text = $"{_input.CageId} · 主筋 {result.MainCount} 根 · 螺旋筋 {result.SpiralCount} 个对象 · 内环 {result.RingCount} 道\n{result.Message}"; } else { _state.Text = "执行失败：" + (error?.Message ?? result?.Message ?? "未返回结果"); MessageBox.Show(this, _state.Text, Title, MessageBoxButton.OK, MessageBoxImage.Warning); } RefreshPreview(); }); }); } catch (Exception ex) { _busy = false; RefreshPreview(); _state.Text = ex.Message; }
    }
    private void PickPoint()
    {
        if (_busy) return; Hide(); try { App.Enqueue(app => { try { var point = app.ActiveUIDocument.Selection.PickPoint("拾取新桩桩底中心。坐标将采用 Revit 内部坐标。"); Dispatcher.Invoke(() => { _input.X = UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters); _input.Y = UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters); _input.Z = UnitUtils.ConvertFromInternalUnits(point.Z, UnitTypeId.Millimeters); _input.CreateHost = true; _input.HostId = 0; _input.HostDocumentUniqueId = ""; RebuildParameters(); _state.Text = "新建位置已拾取；生成时创建宿主。"; }); } catch (Autodesk.Revit.Exceptions.OperationCanceledException) { } catch (Exception ex) { Dispatcher.Invoke(() => _state.Text = "拾取位置失败：" + ex.Message); } finally { Dispatcher.Invoke(() => { Show(); Activate(); }); } }); } catch (Exception ex) { Show(); _state.Text = ex.Message; }
    }
    private void PickHost()
    {
        if (_busy) return; Hide(); try { App.Enqueue(app => { try { var reference = app.ActiveUIDocument.Selection.PickObject(ObjectType.Element, "选择圆形竖向混凝土桩；核验尺寸、方向和宿主资格。"); var host = NativeEngine.InspectHost(app.ActiveUIDocument.Document.GetElement(reference)); var candidate = _input.Clone(); candidate.HostId = host.ElementId; candidate.X = host.X; candidate.Y = host.Y; candidate.Z = host.Z; candidate.HostDocumentUniqueId = app.ActiveUIDocument.Document.ProjectInformation.UniqueId; candidate.CreateHost = false; var description = NativeEngine.DescribeHost(app, candidate.HostId, candidate); Dispatcher.Invoke(() => { _input = candidate; RebuildParameters(); _state.Text = description; }); } catch (Autodesk.Revit.Exceptions.OperationCanceledException) { } catch (Exception ex) { Dispatcher.Invoke(() => _state.Text = "宿主未通过核验：" + ex.Message); } finally { Dispatcher.Invoke(() => { Show(); Activate(); }); } }); } catch (Exception ex) { Show(); _state.Text = ex.Message; }
    }
}

internal sealed class CageDiagram : FrameworkElement
{
    public CageInput Input { get; set; } = new();
    public string FocusKey { get; set; } = "Diameter";
    private readonly Brush _main = Ui.Brush("#087F8C"), _spiral = Ui.Brush("#347AC4"), _ring = Ui.Brush("#9654A8"), _hi = Ui.Brush("#E69028");
    private DrawingContext _dc = null!;
    private Brush ComponentColor(string kind) => FocusKey.StartsWith(kind, StringComparison.Ordinal) || kind == "Spiral" && FocusKey == "Zones" ? _hi : kind == "Main" ? _main : kind == "Spiral" ? _spiral : _ring;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _dc = dc; if (ActualWidth < 100 || ActualHeight < 100) return; dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight))); dc.PushTransform(new ScaleTransform(ActualWidth / 570, ActualHeight / 510));
        try { Draw(Input); } catch { Label("参数待修正；保留最近有效输入。", 20, 30, 13, Ui.Muted); } finally { dc.Pop(); dc.Pop(); }
    }
    private void Label(string text, double x, double y, double size = 11, Brush? brush = null, bool bold = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, brush ?? Ui.Ink, 1); _dc.DrawText(ft, new System.Windows.Point(x, y));
    }
    private void Line(double x1, double y1, double x2, double y2, Brush brush, double width = 1, bool dash = false) { var pen = new Pen(brush, width); if (dash) pen.DashStyle = DashStyles.Dash; _dc.DrawLine(pen, new(x1, y1), new(x2, y2)); }
    private void VDim(double x, double y1, double y2, string text, Brush brush, double labelX = -1) { Line(x, y1, x, y2, brush); Line(x - 4, y1, x + 4, y1, brush); Line(x - 4, y2, x + 4, y2, brush); Label(text, labelX < 0 ? x + 5 : labelX, (y1 + y2) / 2 - 6, 10, brush); }
    private void Draw(CageInput p)
    {
        double h = Math.Max(1, Math.Abs(p.Height)), diam = Math.Max(1, Math.Abs(p.Diameter)), top = 45, bottom = 439, cx = 121, scaleX = 134 / diam, scaleZ = (bottom - top) / h; double Y(double z) => Math.Clamp(bottom - z * scaleZ, -10000, 10000);
        var radii = CagePlanner.Radii(p); double rm = Math.Clamp(radii.Main * scaleX, 1, 65), rs = Math.Clamp(radii.Spiral * scaleX, 1, 66), rr = Math.Clamp(radii.Ring * scaleX, 1, 64);
        Label("立面", 54, 7, 13, Ui.Ink, true); Label("局部横截面", 330, 7, 13, Ui.Ink, true);
        _dc.DrawRectangle(Ui.Brush("#F5F8FA"), new Pen(Ui.Brush("#A9BAC8"), 1.2), new Rect(cx - 67, top, 134, bottom - top)); Line(cx, top - 15, cx, bottom + 15, Ui.Brush("#AAB9C5"), 0.8, true);
        int count = Math.Clamp(p.MainCount, 1, 500); for (int i = 0; i < count; i++) { double x = cx + rm * Math.Cos(2 * Math.PI * i / count); Line(x, Y(p.MainStart), x, Y(p.MainEnd), ComponentColor("Main"), FocusKey.StartsWith("Main") ? 2 : 1.05); }
        double angle = 0, z0 = p.SpiralStart; var colors = new[] { Ui.Brush("#EAF2FD"), Ui.Brush("#F1EDF9"), Ui.Brush("#EAF7F3") };
        for (int i = 0; i < p.Zones.Count; i++)
        {
            var z = p.Zones[i]; if (z.Pitch <= 0 || z.Length <= 0 || !double.IsFinite(z.Length / z.Pitch)) continue; var da = 2 * Math.PI * z.Length / z.Pitch; int steps = Math.Clamp((int)Math.Min(da * 8 + 2, 3000), 2, 3000); var geom = new StreamGeometry(); using (var c = geom.Open()) { c.BeginFigure(new(cx + rs * Math.Cos(angle), Y(z0)), false, false); for (int j = 1; j <= steps; j++) c.LineTo(new(cx + rs * Math.Cos(angle + da * j / steps), Y(z0 + z.Length * j / steps)), true, false); } _dc.DrawGeometry(null, new Pen(ComponentColor("Spiral"), FocusKey == "Zones" || FocusKey.StartsWith("Spiral") ? 2.4 : 1.3), geom);
            double ya = Y(z0), yb = Y(z0 + z.Length); _dc.DrawRectangle(colors[i % 3], null, new Rect(201, Math.Min(ya, yb), 69, Math.Max(0.2, Math.Abs(ya - yb)))); Line(199, ya, 274, ya, Ui.Line, 1, true); if (Math.Abs(ya - yb) >= 25) { Label(z.Id, 208, yb + 6, 10, Ui.Muted, true); Label($"L{i + 1}={Ui.N(z.Length)}", 208, (ya + yb) / 2 - 3, 10, FocusKey == "Zones" ? _hi : Ui.Ink); Label($"p{i + 1}={Ui.N(z.Pitch)}", 208, (ya + yb) / 2 + 13, 10, ComponentColor("Spiral")); } z0 += z.Length; angle += da;
        }
        int rings = Math.Clamp(CagePlanner.RingCount(p), 0, 1000); for (int i = 0; i < rings; i++) { double y = Y(p.RingStart + i * p.RingSpacing); Line(cx - rr, y, cx + rr, y, ComponentColor("Ring"), FocusKey.StartsWith("Ring") ? 3 : 2.2); }
        VDim(27, top, bottom, "H", FocusKey == "Height" ? _hi : Ui.Muted, 9); Label(Ui.N(p.Height), 0, bottom + 5, 10, FocusKey == "Height" ? _hi : Ui.Muted); VDim(194, Y(p.MainEnd), Y(p.MainStart), "", ComponentColor("Main")); Label($"主筋 {Ui.N(p.MainEnd - p.MainStart)}", 77, 451, 10, ComponentColor("Main")); if (rings > 1) VDim(281, Y(p.RingStart), Y(p.RingStart + p.RingSpacing), "s=" + Ui.N(p.RingSpacing), ComponentColor("Ring"));
        Line(46, bottom, 46, bottom - 48, Ui.Ink); Line(46, bottom - 48, 42, bottom - 40, Ui.Ink); Line(46, bottom - 48, 50, bottom - 40, Ui.Ink); Label("+z", 31, bottom - 69, 10); Label("桩底 z=0", 57, bottom + 21, 10, Ui.Muted);
        double scx = 422, scy = 130, pr = 85, sx = pr / (diam / 2); double sm = Math.Clamp(radii.Main * sx, 1, 82), ss = Math.Clamp(radii.Spiral * sx, 1, 84), sr = Math.Clamp(radii.Ring * sx, 1, 80); _dc.DrawEllipse(Ui.Brush("#F5F8FA"), new Pen(Ui.Brush("#A9BAC8"), 1.2), new(scx, scy), pr, pr); Line(scx - 100, scy, scx + 101, scy, Ui.Line, 0.8, true); Line(scx, scy - 97, scx, scy + 98, Ui.Line, 0.8, true);
        if (p.RingsEnabled) _dc.DrawEllipse(null, new Pen(ComponentColor("Ring"), Math.Clamp(p.RingDiameter * sx, 1.5, 6)), new(scx, scy), sr, sr); _dc.DrawEllipse(null, new Pen(ComponentColor("Spiral"), Math.Clamp(p.SpiralDiameter * sx, 1.5, 6)), new(scx, scy), ss, ss);
        for (int i = 0; i < count; i++) { double a = 2 * Math.PI * i / count; _dc.DrawEllipse(ComponentColor("Main"), null, new(scx + sm * Math.Cos(a), scy + sm * Math.Sin(a)), Math.Clamp(p.MainDiameter * sx / 2, 1.5, 8), Math.Clamp(p.MainDiameter * sx / 2, 1.5, 8)); }
        Line(scx - pr, 235, scx + pr, 235, FocusKey == "Diameter" ? _hi : Ui.Muted); Line(scx - pr, 230, scx - pr, 240, Ui.Muted); Line(scx + pr, 230, scx + pr, 240, Ui.Muted); Label("D = " + Ui.N(p.Diameter) + " mm", scx - 49, 243, 11, FocusKey == "Diameter" ? _hi : Ui.Ink);
        double coverEnd = p.CoverBasis == CoverBasis.OutermostSurface ? ss + p.SpiralDiameter * sx / 2 : sm + p.MainDiameter * sx / 2; var cb = FocusKey == "Cover" ? _hi : Ui.Muted; Line(scx + coverEnd, scy, scx + coverEnd, scy - 42, cb); Line(scx + pr, scy, scx + pr, scy - 42, cb); Line(scx + coverEnd, scy - 39, scx + pr, scy - 39, cb, 1.5); Line(scx + pr - 3, scy - 39, 537, 66, cb); Label("c=" + Ui.N(p.Cover), 501, 47, 10, cb);
        Label($"n = {p.MainCount}  ·  等角布置", 358, 272, 11, ComponentColor("Main")); Line(327, 308, 348, 308, _main, 3); Label($"纵向主筋  d₁ = {Ui.N(p.MainDiameter)}", 355, 300, 11, ComponentColor("Main")); Line(327, 338, 348, 338, _spiral, 3); Label($"外螺旋筋  d₂ = {Ui.N(p.SpiralDiameter)}", 355, 330, 11, ComponentColor("Spiral")); Line(327, 368, 348, 368, _ring, 3); Label($"内加劲环  d₃ = {Ui.N(p.RingDiameter)}", 355, 360, 11, ComponentColor("Ring"));
        Label("保护层 c 的测量终点", 327, 400, 11, Ui.Ink, true); Label(p.CoverBasis == CoverBasis.OutermostSurface ? "最外层螺旋筋外表面" : "纵向主筋外表面", 327, 421, 11, cb); Label("单位 mm  ·  示意，非等比例", 327, 458, 10, Ui.Muted);
    }
}
