using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PileCage;

public sealed class SettingsWindow : Window
{
    public CageInput Result { get; private set; }
    private readonly List<RuleRecord> _rules;
    private readonly ListBox _list = new() { MinWidth = 235, BorderBrush = Ui.Line, Padding = new Thickness(4), Background = Brushes.White };
    private readonly StackPanel _details = new();
    private readonly TextBlock _hint = Ui.Text("规则记录与几何检查分别保存。未核实的条文不作为强制依据。", 11, Ui.Muted);
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly List<BarTypeChoice> _types;
    private RuleRecord? _selected;
    private ComboBox _parameter = null!, _operator = null!, _action = null!, _cover = null!;
    private CheckBox _enabled = null!, _verified = null!, _applicable = null!, _host = null!;
    private TextBox _tolerance = null!, _directory = null!;
    private readonly Dictionary<string, TextBox> _defaults = [];
    private readonly Dictionary<string, ComboBox> _maps = [];
    private bool _selecting;
    public SettingsWindow(CageInput input, List<BarTypeChoice> types)
    {
        Result = input.Clone(); _types = types; _rules = RuleStore.Load(); Title = "设置 · 灌注桩钢筋笼"; Width = 950; Height = 735; MinWidth = 880; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Ui.Back; FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); FontSize = 12;
        var root = new Grid { Margin = new Thickness(18) }; root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = Ui.Text("设置", 23, Ui.Ink, true); title.Margin = new Thickness(2, 0, 0, 14); root.Children.Add(title); var tabs = new TabControl { Background = Brushes.White, BorderBrush = Ui.Line, Padding = new Thickness(12) }; tabs.Items.Add(new TabItem { Header = "  规范与规则  ", Content = BuildRules() }); tabs.Items.Add(new TabItem { Header = "  建模选项  ", Content = BuildOptions() }); Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var bottom = new Grid { Margin = new Thickness(0, 14, 0, 0) }; bottom.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); bottom.Children.Add(_hint); var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(Ui.Button("保存设置", Save, true)); actions.Children.Add(Ui.Button("取消", () => DialogResult = false)); Grid.SetColumn(actions, 1); bottom.Children.Add(actions); Grid.SetRow(bottom, 2); root.Children.Add(bottom); Content = root;
        RefreshRuleList(); _list.SelectedIndex = 0;
    }
    public void CaptureScreenshot(string path) { UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(this); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(this); var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var s = File.Create(path); e.Save(s); }
    public void SelectTab(int index) { if (Content is Grid root && root.Children[1] is TabControl tabs) tabs.SelectedIndex = index; }
    private UIElement BuildRules()
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(270) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var left = new Grid(); left.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); _list.SelectionChanged += (_, _) => { if (_selecting) return; var next = _list.SelectedItem as RuleRecord; if (_selected != null && !CommitRule(false)) { _selecting = true; _list.SelectedItem = _selected; _selecting = false; return; } _selected = next; ShowRule(); }; left.Children.Add(_list); var add = Ui.Button("＋ 项目自定义规则", AddCustom); add.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(add, 1); left.Children.Add(add); grid.Children.Add(left);
        var scroller = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(scroller, 2); grid.Children.Add(scroller); return grid;
    }
    private void RefreshRuleList() { _list.ItemsSource = null; _list.ItemsSource = _rules; }
    private TextBox DetailField(string key, string label, string value, bool readOnly, bool multiline = false)
    {
        var title = Ui.Text(label, 11, Ui.Muted); title.Margin = new Thickness(0, 8, 0, 4); _details.Children.Add(title); var box = Ui.Box(value); box.IsReadOnly = readOnly; box.Background = readOnly ? Ui.Back : Brushes.White; if (multiline) { box.TextWrapping = TextWrapping.Wrap; box.AcceptsReturn = true; box.MinHeight = 52; } _fields[key] = box; _details.Children.Add(box); return box;
    }
    private void ShowRule()
    {
        _details.Children.Clear(); _fields.Clear(); if (_selected is not { } r) return; bool custom = r.SourceType == "Custom";
        _details.Children.Add(Ui.Text(r.Name, 17, Ui.Ink, true)); _details.Children.Add(Ui.Text(custom ? "项目自定义规则 · 独立于原标准记录" : "来源记录只读。更改要求请另建项目自定义规则。", 11, custom ? Ui.Accent : Ui.Muted));
        DetailField("Name", "规则名称", r.Name, !custom); DetailField("Source", "来源：" + (r.SourceType == "Standard" ? "规范要求" : r.SourceType == "Drawing" ? "项目图纸要求" : "项目自定义要求"), r.Source, !custom); DetailField("Clause", "条文号／图纸说明位置", r.Clause, !custom); DetailField("Summary", "条文要点", r.Summary, !custom, true); DetailField("Applicability", "适用条件", r.Applicability, !custom, true);
        _details.Children.Add(Ui.Text("检查参数、关系、限值与单位", 11, Ui.Muted)); var pg = new Grid { Margin = new Thickness(0, 5, 0, 8) }; foreach (double w in new[] { 2.5, .65, .9, .7 }) pg.ColumnDefinitions.Add(new() { Width = new GridLength(w, GridUnitType.Star) });
        var parameterNames = new Dictionary<string, string> { ["Diameter"] = "桩径 D", ["Height"] = "桩长 H", ["Cover"] = "当前基准保护层 c", ["MainSurfaceCover"] = "主筋表面保护层", ["MainCount"] = "主筋数量 n", ["MainDiameter"] = "主筋直径 d₁", ["MainClearSpacing"] = "主筋净距", ["SpiralDiameter"] = "螺旋直径 d₂", ["SpiralMinPitch"] = "最小螺距", ["SpiralMaxPitch"] = "最大螺距", ["RingDiameter"] = "内环直径 d₃", ["RingSpacing"] = "内环间距 s" };
        _parameter = new ComboBox { ItemsSource = parameterNames, DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = r.Parameter, IsEnabled = custom, MinHeight = 30, Margin = new Thickness(0, 0, 5, 0), ToolTip = "当前基准保护层 c：输入值，其测量终点由建模选项确定。\n主筋表面保护层：桩外表面至纵向主筋外表面的实际距离，按 D/2 − 主筋中心线半径 − d₁/2 计算；不因输入保护层基准切换而改变测量终点。" }; pg.Children.Add(_parameter);
        _operator = new ComboBox { ItemsSource = new[] { ">=", "<=", "==" }, SelectedItem = r.Operator, IsEnabled = custom, Margin = new Thickness(0, 0, 5, 0) }; Grid.SetColumn(_operator, 1); pg.Children.Add(_operator); var limit = Ui.Box(r.Limit.HasValue ? Ui.N(r.Limit.Value) : ""); limit.IsReadOnly = !custom; limit.Margin = new Thickness(0, 0, 5, 0); _fields["Limit"] = limit; Grid.SetColumn(limit, 2); pg.Children.Add(limit); var unit = Ui.Box(r.Unit); unit.IsReadOnly = !custom; _fields["Unit"] = unit; Grid.SetColumn(unit, 3); pg.Children.Add(unit); _details.Children.Add(pg);
        _verified = new CheckBox { Content = custom ? "项目要求来源已确认" : "原文已核实（只读）", IsChecked = r.OriginalVerified, IsEnabled = custom, Margin = new Thickness(0, 3, 0, 7) }; _details.Children.Add(_verified); _applicable = new CheckBox { Content = "已确认适用于当前项目", IsChecked = r.ProjectApplicable, IsEnabled = custom || r.OriginalVerified, Margin = new Thickness(0, 0, 0, 7) }; _details.Children.Add(_applicable);
        _enabled = new CheckBox { Content = "启用该规则", IsChecked = r.Enabled, IsEnabled = r.OriginalVerified && r.ProjectApplicable, Margin = new Thickness(0, 0, 0, 8) }; _details.Children.Add(_enabled); void RefreshEnable() { _enabled.IsEnabled = _verified.IsChecked == true && _applicable.IsChecked == true; if (!_enabled.IsEnabled) _enabled.IsChecked = false; } _verified.Checked += (_, _) => RefreshEnable(); _verified.Unchecked += (_, _) => RefreshEnable(); _applicable.Checked += (_, _) => RefreshEnable(); _applicable.Unchecked += (_, _) => RefreshEnable();
        _action = new ComboBox { ItemsSource = new Dictionary<string, string> { ["Block"] = "阻止生成", ["Warn"] = "提示并明确确认", ["Reference"] = "仅供查阅" }, DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = r.Action, IsEnabled = custom, MinHeight = 30 }; _details.Children.Add(Ui.Text("处理方式", 11, Ui.Muted)); _details.Children.Add(_action); _details.Children.Add(Ui.Text(!r.OriginalVerified || !r.ProjectApplicable ? "状态：未完成该项核查。不得据此声明规范合格。" : "已具备核查前提；是否执行以启用状态和处理方式为准。", 11, Ui.Brush("#A95E1B")));
    }
    private bool CommitRule(bool showError)
    {
        if (_selected is not { } r) return true; if (r.SourceType == "Custom")
        {
            if (string.IsNullOrWhiteSpace(_fields["Name"].Text) || !Ui.Number(_fields["Limit"].Text, out var limit)) { _hint.Text = "请填写自定义规则名称和有限数值限值，再切换规则或保存。"; if (showError) MessageBox.Show(this, _hint.Text, Title, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            r.Name = _fields["Name"].Text; r.Source = _fields["Source"].Text; r.Clause = _fields["Clause"].Text; r.Summary = _fields["Summary"].Text; r.Applicability = _fields["Applicability"].Text; r.Parameter = _parameter.SelectedValue?.ToString() ?? "Cover"; r.Operator = _operator.SelectedItem?.ToString() ?? ">="; r.Limit = limit; r.Unit = _fields["Unit"].Text; r.OriginalVerified = _verified.IsChecked == true; r.Action = _action.SelectedValue?.ToString() ?? "Reference";
            if (r.OriginalVerified && (string.IsNullOrWhiteSpace(r.Source) || string.IsNullOrWhiteSpace(r.Applicability) || r.Source.StartsWith("待填写") || r.Applicability.StartsWith("待填写"))) { _hint.Text = "确认来源前，请填写真实来源和项目适用条件。"; if (showError) MessageBox.Show(this, _hint.Text, Title); return false; }
        }
        r.ProjectApplicable = _applicable.IsChecked == true; r.Enabled = _enabled.IsChecked == true && r.OriginalVerified && r.ProjectApplicable; return true;
    }
    private void AddCustom()
    {
        if (!CommitRule(true)) return; var r = new RuleRecord { Id = "PROJECT-" + Guid.NewGuid().ToString("N")[..8], Name = "项目自定义保护层要求", SourceType = "Custom", Source = "待填写项目要求来源", Clause = "项目约定", Summary = "按项目确认要求检查，非规范原文。", Applicability = "待填写本项目适用条件", Parameter = "Cover", Operator = ">=", Limit = Result.Cover, Unit = "mm", Action = "Warn" }; _rules.Add(r); _selected = null; _selecting = true; RefreshRuleList(); _list.SelectedItem = r; _selecting = false; _selected = r; ShowRule();
    }
    private UIElement BuildOptions()
    {
        var panel = new StackPanel { Margin = new Thickness(8) }; panel.Children.Add(Ui.Text("保护层测量基准", 16, Ui.Ink, true)); _cover = new ComboBox { ItemsSource = new[] { "最外层钢筋表面（外螺旋筋）", "纵向主筋表面" }, SelectedIndex = Result.CoverBasis == CoverBasis.OutermostSurface ? 0 : 1, Margin = new Thickness(0, 10, 0, 8), MinHeight = 32 }; panel.Children.Add(_cover); panel.Children.Add(Ui.Text("切换基准会重新计算三类钢筋中心线半径，并同步主界面与横截面标注。", 11, Ui.Muted));
        var mt = Ui.Text("钢筋类型映射", 16, Ui.Ink, true); mt.Margin = new Thickness(0, 20, 0, 8); panel.Children.Add(mt); Map(panel, "Main", "纵向主筋 d₁", Result.MainTypeId); Map(panel, "Spiral", "外螺旋筋 d₂", Result.SpiralTypeId); Map(panel, "Ring", "内加劲环 d₃", Result.RingTypeId); panel.Children.Add(Ui.Text("实际类型使用 Revit 公称直径。按直径创建的受管类型不代表已确认钢筋强度等级。", 11, Ui.Muted));
        var start = Ui.Text("端部与定位默认参数（本次参数）", 16, Ui.Ink, true); start.Margin = new Thickness(0, 20, 0, 8); panel.Children.Add(start); Default(panel, "MainStart", "主筋起点 z₁（mm）", Result.MainStart); Default(panel, "MainEnd", "主筋终点 z₂（mm）", Result.MainEnd); Default(panel, "SpiralStart", "螺旋筋起点 zₛ（mm）", Result.SpiralStart); Default(panel, "RingStart", "内环首道位置（mm）", Result.RingStart); Default(panel, "RingEnd", "内环末端范围（mm）", Result.RingEnd);
        _tolerance = Ui.Box(Ui.N(Result.Tolerance)); panel.Children.Add(OptionRow("几何计算容差（mm，0 < t ≤ 0.5）", _tolerance)); _host = new CheckBox { Content = "创建圆形混凝土宿主（已有模式请在主界面选取）", IsChecked = Result.CreateHost, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(_host); panel.Children.Add(Ui.Text("参数文件保存位置", 12, Ui.Ink, true)); _directory = Ui.Box(Files.ParameterDirectory); _directory.Margin = new Thickness(0, 7, 0, 10); panel.Children.Add(_directory); panel.Children.Add(Ui.Text("桩底 z=0，向上为正；直段及闭合圆环按明确几何约定生成，不自动添加加工构造。", 11, Ui.Muted)); return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private static Grid OptionRow(string label, UIElement value) { var r = new Grid { Margin = new Thickness(0, 0, 0, 8) }; r.ColumnDefinitions.Add(new() { Width = new GridLength(260) }); r.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); r.Children.Add(Ui.Text(label)); Grid.SetColumn(value, 1); r.Children.Add(value); return r; }
    private void Default(StackPanel panel, string key, string label, double value) { var b = Ui.Box(Ui.N(value)); _defaults[key] = b; panel.Children.Add(OptionRow(label, b)); }
    private void Map(StackPanel panel, string key, string label, long value) { var b = new ComboBox { ItemsSource = _types, SelectedValuePath = nameof(BarTypeChoice.Id), SelectedValue = value, MinHeight = 31 }; if (b.SelectedIndex < 0) b.SelectedIndex = 0; _maps[key] = b; panel.Children.Add(OptionRow(label, b)); }
    private void Save()
    {
        if (!CommitRule(true)) return; try
        {
            if (!Ui.Number(_tolerance.Text, out var tolerance) || tolerance <= 0 || tolerance > .5) throw new InvalidDataException("容差须为 (0, 0.5] mm。"); var values = new Dictionary<string, double>(); foreach (var item in _defaults) { if (!Ui.Number(item.Value.Text, out var n)) throw new InvalidDataException("端部参数必须为有限数值。"); values[item.Key] = n; }
            string directory = Path.GetFullPath(_directory.Text); Directory.CreateDirectory(directory); Result.Tolerance = tolerance; Result.CoverBasis = _cover.SelectedIndex == 0 ? CoverBasis.OutermostSurface : CoverBasis.LongitudinalSurface; Result.CreateHost = _host.IsChecked == true; if (Result.CreateHost) { Result.HostId = 0; Result.HostDocumentUniqueId = ""; } Result.MainStart = values["MainStart"]; Result.MainEnd = values["MainEnd"]; Result.SpiralStart = values["SpiralStart"]; Result.RingStart = values["RingStart"]; Result.RingEnd = values["RingEnd"];
            foreach (var item in _maps) { if (item.Value.SelectedItem is not BarTypeChoice t) continue; switch (item.Key) { case "Main": Result.MainTypeId = t.Id; if (t.Id != 0) Result.MainDiameter = t.Diameter; break; case "Spiral": Result.SpiralTypeId = t.Id; if (t.Id != 0) Result.SpiralDiameter = t.Diameter; break; case "Ring": Result.RingTypeId = t.Id; if (t.Id != 0) Result.RingDiameter = t.Diameter; break; } }
            RuleStore.Save(_rules); Files.ParameterDirectory = directory; Directory.CreateDirectory(Path.Combine(Files.Root, "config")); File.WriteAllText(Path.Combine(Files.Root, "config", "ui-options.json"), JsonSerializer.Serialize(new { ParameterDirectory = directory }, Files.Json)); DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, "设置未保存：\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
