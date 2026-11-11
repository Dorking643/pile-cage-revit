using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Grid = System.Windows.Controls.Grid;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using WpfPoint = System.Windows.Point;

namespace PileCage;

public sealed class PileHeadUiInput
{
    public long HostId { get; set; }
    public string HostDocumentUniqueId { get; set; } = "";
    public PileHeadForm Form { get; set; } = PileHeadForm.Straight;
    public string ConcreteGrade { get; set; } = "";
    public string SteelGrade { get; set; } = "";
    public string SeismicGrade { get; set; } = "";
    public bool EpoxyCoated { get; set; }
    public bool EasilyDisturbed { get; set; }
    public double CapWidth { get; set; } = 4000;
    public double CapDepth { get; set; } = 4000;
    public double CapHeight { get; set; } = 2200;
    public double PileEmbedment { get; set; } = 100;
    public double Cover { get; set; } = 50;
    public double SplayAngleDegrees { get; set; } = 75;
    public double WaterproofThickness { get; set; } = 4;
    public double RigidWaterproofThickness { get; set; } = 20;
    public double WaterproofExtension { get; set; } = 300;
    public PileHeadUiInput Clone() => JsonSerializer.Deserialize<PileHeadUiInput>(JsonSerializer.Serialize(this, Files.Json), Files.Json)!;
}

public sealed class PileHeadUiSnapshot
{
    public long HostId { get; set; }
    public string HostDocumentUniqueId { get; set; } = "";
    public double Diameter { get; set; }
    public double Height { get; set; }
    public int MainCount { get; set; }
    public double MainDiameter { get; set; }
    public string SteelGrade { get; set; } = "";
    public string SourceSummary { get; set; } = "";
    public List<PileHeadBarSource> Bars { get; set; } = [];
    public PileHeadForm? ExistingForm { get; set; }
}

public sealed record PileHeadConfigurationResult(bool Confirmed, PileHeadUiInput? Settings);

[Transaction(TransactionMode.Manual)]
public sealed class PileHeadCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        PileHeadWindow.ShowFor(data.Application); return Result.Succeeded;
    }
}

public sealed class PileHeadWindow : Window
{
    public static PileHeadWindow? Instance { get; private set; }
    public static PileHeadWindow? ConfigurationInstance { get; private set; }
    public PileHeadUiInput Input => _input.Clone();
    public PileHeadUiSnapshot? Snapshot => _snapshot;
    public JsonObject? LastResult { get; private set; }
    public bool IsConfigurationMode => _configurationMode;
    public bool AnchorageEnabled => !_configurationMode || _anchorageEnabled;
    public PileHeadConfigurationResult ConfigurationResult => new(_configurationConfirmed, _savedConfiguration?.Clone());
    private readonly bool _configurationMode;
    private bool _anchorageEnabled, _configurationConfirmed;
    private PileHeadUiInput? _savedConfiguration;
    private PileHeadUiInput _input = new();
    private PileHeadUiSnapshot? _snapshot;
    private readonly StackPanel _body = new();
    private readonly HashSet<string> _invalid = [];
    private readonly TextBlock _host = Ui.Text("请选择已有主筋的圆形混凝土柱。", 12);
    private readonly TextBlock _anchor = Ui.Text("锚固长度由材料和抗震条件自动计算。", 12, Ui.Ink, true);
    private readonly TextBlock _source = Ui.Text("", 10, Ui.Muted);
    private readonly TextBlock _state = Ui.Text("", 12);
    private Button _select = null!, _apply = null!;
    private TextBox _angle = null!;
    private FrameworkElement _waterproof = null!;
    private bool _loading, _busy, _previewOk;

    private PileHeadWindow(bool configurationMode = false)
    {
        _configurationMode = configurationMode;
        if (configurationMode) ConfigurationInstance = this; else Instance = this;
        Title = configurationMode ? "锚固设置" : "桩顶构造"; Width = 720; MinWidth = 660; MinHeight = 660;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24); Height = Math.Min(850, MaxHeight);
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); FontSize = 12;
        Background = Ui.Back; Foreground = Ui.Ink; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        _select = Button("选择圆形柱", "SelectColumn", PickColumn); _select.Margin = new Thickness(0, 0, 12, 0);
        if (configurationMode) _select.Visibility = System.Windows.Visibility.Collapsed;
        DockPanel.SetDock(_select, Dock.Left); header.Children.Add(_select); header.Children.Add(_host); root.Children.Add(header);
        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _state.MaxHeight = 58; _state.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetAutomationId(_state, "PileHead_Result"); footer.Children.Add(_state);
        var buttons = new DockPanel(); var note = Ui.Text(configurationMode ? "保存后随“生成全部钢筋”一起创建。" : "应用至原主筋，保留原有箍筋。", 11, Ui.Muted);
        buttons.Children.Add(note); var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _apply = Button(configurationMode ? "保存设置" : "应用桩顶构造", configurationMode ? "ConfirmSettings" : "Generate", Apply, true); _apply.IsDefault = configurationMode; actions.Children.Add(_apply);
        var close = Button(configurationMode ? "取消" : "关闭", configurationMode ? "CancelSettings" : "Close", Close); close.IsCancel = configurationMode; close.Margin = new Thickness(0); actions.Children.Add(close); buttons.Children.Add(actions);
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        BuildFields(); SetBusy(false);
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) =>
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (ReferenceEquals(ConfigurationInstance, this)) ConfigurationInstance = null;
        };
    }

    public static void ShowFor(UIApplication ui)
    {
        App.EnsureDispatcher();
        if (Instance != null) { Instance.Activate(); return; }
        var window = new PileHeadWindow(); new WindowInteropHelper(window).Owner = ui.MainWindowHandle;
        try
        {
            var ids = ui.ActiveUIDocument?.Selection.GetElementIds();
            if (ids?.Count == 1) window.LoadColumn(ui, ids.Single());
            else window._state.Text = "点击“选择圆形柱”，读取原主筋及柱顶位置。";
        }
        catch (Exception ex) { window._state.Text = "当前选择不可用：" + ex.Message; }
        window.Show();
    }

    public static PileHeadWindow CreateConfiguration(Window owner, CageInput cage, PileHeadUiInput? current, PileHeadUiSnapshot plannedSnapshot)
    {
        if (plannedSnapshot.HostId != cage.HostId || plannedSnapshot.HostDocumentUniqueId != cage.HostDocumentUniqueId)
            throw new InvalidOperationException("拟生成主筋与当前柱不一致，请重新选择柱。");
        var window = new PileHeadWindow(true) { Owner = owner };
        window._snapshot = plannedSnapshot;
        window._input = current?.Clone() ?? new PileHeadUiInput();
        window._input.HostId = cage.HostId; window._input.HostDocumentUniqueId = cage.HostDocumentUniqueId;
        window._anchorageEnabled = current != null;
        window._host.Text = $"柱 ID {cage.HostId} · D{Ui.N(cage.Diameter)} × H{Ui.N(cage.Height)} mm\n拟生成主筋 {cage.MainCount} 根 · Φ{Ui.N(cage.MainDiameter)} mm（按当前参数预览）";
        window.BuildFields();
        window._state.Text = "启用后保留主筋下端，上端按锚固延伸至柱顶以上；原终点高度不作为锚固终点。";
        return window;
    }

    public static PileHeadConfigurationResult Configure(Window owner, CageInput cage, PileHeadUiInput? current, PileHeadUiSnapshot plannedSnapshot)
    {
        var window = CreateConfiguration(owner, cage, current, plannedSnapshot);
        window.ShowDialog(); return window.ConfigurationResult;
    }

    public void LoadInput(PileHeadUiInput input)
    {
        if (_busy) throw new InvalidOperationException("桩顶构造正在应用，暂不能替换参数。");
        if (_snapshot == null || input.HostId != _snapshot.HostId || input.HostDocumentUniqueId != _snapshot.HostDocumentUniqueId)
            throw new InvalidOperationException("参数与当前选中柱不一致，请先重新选择柱。");
        _input = input.Clone(); LastResult = null; BuildFields(); SetBusy(false);
    }

    public void CaptureScreenshot(string path)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateLayout(); var content = (FrameworkElement)Content; var dpi = VisualTreeHelper.GetDpi(content);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual(); var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(Background, null, bounds);
                // Relative bounds include the whole root visual even when its Margin offsets its drawing.
                dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill }, null, bounds);
            }
            bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); using var stream = File.Create(path); encoder.Save(stream);
        });
    }

    private static Button Button(string text, string id, Action action, bool primary = false)
    {
        var button = Ui.Button(text, action, primary); AutomationProperties.SetAutomationId(button, "PileHead_" + id); return button;
    }

    private StackPanel Group(string title)
    {
        var panel = new StackPanel(); var caption = Ui.Text(title, 13, Ui.Ink, true); caption.Margin = new Thickness(0, 0, 0, 7); panel.Children.Add(caption);
        var card = Ui.Card(panel, new Thickness(10)); card.Tag = "AnchorageInput"; card.Margin = new Thickness(0, 0, 0, 8); _body.Children.Add(card); return panel;
    }

    private static void Fields(StackPanel group, params (string Label, FrameworkElement Editor)[] fields)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        for (int i = 0; i < fields.Length; i++)
        {
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 6, 0, i == fields.Length - 1 ? 0 : 6, 0) };
            var label = Ui.Text(fields[i].Label, 11); label.Margin = new Thickness(0, 0, 0, 4); cell.Children.Add(label); cell.Children.Add(fields[i].Editor);
            Grid.SetColumn(cell, i); row.Children.Add(cell);
        }
        group.Children.Add(row);
    }

    private TextBox Number(string key, double value, Action<double> assign)
    {
        var editor = Ui.Box(Ui.N(value)); editor.MinHeight = 28; editor.Padding = new Thickness(6, 3, 6, 3);
        AutomationProperties.SetAutomationId(editor, "PileHead_" + key);
        editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (Ui.Number(editor.Text, out double number)) { _invalid.Remove(key); editor.BorderBrush = Ui.Line; assign(number); }
            else { _invalid.Add(key); editor.BorderBrush = Ui.Brush("#BB3445"); }
            RefreshPreview();
        };
        return editor;
    }

    private ComboBox Choice(string key, string value, IEnumerable<(string Value, string Label)> choices, Action<string> assign)
    {
        var items = choices.Select(x => new ChoiceItem(x.Value, x.Label)).Prepend(new ChoiceItem("", "请选择")).ToList();
        var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = nameof(ChoiceItem.Label), SelectedValuePath = nameof(ChoiceItem.Value), SelectedValue = value, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(combo, "PileHead_" + key); combo.ToolTip = "请选择与设计一致的条件。";
        combo.SelectionChanged += (_, _) => { if (!_loading && combo.SelectedItem is ChoiceItem selected) { assign(selected.Value); RefreshPreview(); } };
        return combo;
    }

    private void BuildFields()
    {
        if (_anchor.Parent is Panel previousAnchorParent) previousAnchorParent.Children.Remove(_anchor);
        if (_source.Parent is ContentControl previousSourceParent) previousSourceParent.Content = null;
        _loading = true; _body.Children.Clear(); _invalid.Clear();
        if (_configurationMode)
        {
            var enabled = new CheckBox { Content = "启用锚固（未勾选＝无锚固）", IsChecked = _anchorageEnabled, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10) };
            AutomationProperties.SetAutomationId(enabled, "PileHead_EnableAnchorage");
            enabled.Checked += (_, _) => { if (!_loading) { _anchorageEnabled = true; RefreshPreview(); } };
            enabled.Unchecked += (_, _) => { if (!_loading) { _anchorageEnabled = false; RefreshPreview(); } };
            _body.Children.Add(enabled);
        }
        var forms = Group("构造形式 · 示意，非比例"); var options = new UniformGrid { Columns = 2, Rows = 2 };
        AddForm(options, PileHeadForm.Straight, "1  直锚", "主筋竖向伸入承台");
        AddForm(options, PileHeadForm.Bent90, "2  向外弯折 90°", "竖向段与外伸水平段");
        AddForm(options, PileHeadForm.Splayed, "3  径向外斜", "与水平面夹角不小于75°");
        AddForm(options, PileHeadForm.Waterproof, "4  直锚 + 桩顶防水", "直锚钢筋与防水示意层");
        forms.Children.Add(options);

        var materials = Group("锚固计算条件 · 请按设计选择");
        var concrete = Choice("ConcreteGrade", _input.ConcreteGrade, new[] { "C25", "C30", "C35", "C40", "C45", "C50", "C55", "C60" }.Select(x => (x, x)), v => _input.ConcreteGrade = v);
        var steel = Choice("SteelGrade", _input.SteelGrade, new[] { "HRB400", "HRB500" }.Select(x => (x, x)), v => _input.SteelGrade = v);
        var seismic = Choice("SeismicGrade", _input.SeismicGrade, new[] { ("NonSeismic", "非抗震"), ("I", "一级"), ("II", "二级"), ("III", "三级"), ("IV", "四级") }, v => _input.SeismicGrade = v);
        Fields(materials, ("承台混凝土", concrete), ("主筋牌号", steel), ("抗震等级", seismic));
        var conditions = new StackPanel { Orientation = Orientation.Horizontal };
        var epoxy = new CheckBox { Content = "环氧树脂涂层", IsChecked = _input.EpoxyCoated, Margin = new Thickness(0, 0, 22, 0) };
        AutomationProperties.SetAutomationId(epoxy, "PileHead_EpoxyCoated");
        epoxy.Checked += (_, _) => { if (!_loading) { _input.EpoxyCoated = true; RefreshPreview(); } };
        epoxy.Unchecked += (_, _) => { if (!_loading) { _input.EpoxyCoated = false; RefreshPreview(); } };
        var disturbed = new CheckBox { Content = "施工中易受扰动", IsChecked = _input.EasilyDisturbed };
        AutomationProperties.SetAutomationId(disturbed, "PileHead_EasilyDisturbed");
        disturbed.Checked += (_, _) => { if (!_loading) { _input.EasilyDisturbed = true; RefreshPreview(); } };
        disturbed.Unchecked += (_, _) => { if (!_loading) { _input.EasilyDisturbed = false; RefreshPreview(); } };
        conditions.Children.Add(epoxy); conditions.Children.Add(disturbed); materials.Children.Add(conditions);

        var geometry = Group("承台及构造尺寸 · mm");
        Fields(geometry, ("承台宽度", Number("CapWidth", _input.CapWidth, v => _input.CapWidth = v)), ("承台长度", Number("CapDepth", _input.CapDepth, v => _input.CapDepth = v)), ("承台高度", Number("CapHeight", _input.CapHeight, v => _input.CapHeight = v)));
        _angle = Number("SplayAngleDegrees", _input.SplayAngleDegrees, v => _input.SplayAngleDegrees = v);
        Fields(geometry, ("桩嵌入承台高度", Number("PileEmbedment", _input.PileEmbedment, v => _input.PileEmbedment = v)), ("承台钢筋保护层", Number("Cover", _input.Cover, v => _input.Cover = v)), ("斜筋与水平面夹角（°）", _angle));
        var waterproof = new StackPanel();
        Fields(waterproof, ("柔性防水示意层厚度", Number("WaterproofThickness", _input.WaterproofThickness, v => _input.WaterproofThickness = v)), ("刚性防水示意层厚度", Number("RigidWaterproofThickness", _input.RigidWaterproofThickness, v => _input.RigidWaterproofThickness = v)), ("防水层沿承台边外延", Number("WaterproofExtension", _input.WaterproofExtension, v => _input.WaterproofExtension = v)));
        _waterproof = waterproof; geometry.Children.Add(waterproof);
        geometry.Children.Add(Ui.Text("尺寸初值仅用于几何示意，请按设计调整；防水层不代表完整防水节点。", 10, Ui.Muted));

        var calculated = Group("自动锚固结果 · 只读"); _anchor.Margin = new Thickness(0, 0, 0, 5); calculated.Children.Add(_anchor);
        var sources = new ScrollViewer { Content = _source, MaxHeight = 65, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; calculated.Children.Add(sources);
        AutomationProperties.SetAutomationId(_anchor, "PileHead_AnchorageResult");
        _loading = false; RefreshPreview();
    }

    private void AddForm(UniformGrid grid, PileHeadForm form, string title, string description)
    {
        var content = new DockPanel(); var drawing = new PileHeadMiniDiagram { Form = form, Width = 84, Height = 70, Margin = new Thickness(0, 0, 7, 0) };
        DockPanel.SetDock(drawing, Dock.Left); content.Children.Add(drawing);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(title, 12, Ui.Ink, true)); var caption = Ui.Text(description, 10, Ui.Muted); caption.Margin = new Thickness(0, 4, 0, 0); text.Children.Add(caption); content.Children.Add(text);
        var radio = new RadioButton { Content = content, GroupName = "PileHeadForms", IsChecked = _input.Form == form, Padding = new Thickness(5), Margin = new Thickness(0, 0, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(radio, "PileHead_Form_" + form);
        radio.Checked += (_, _) => { if (!_loading) { _input.Form = form; RefreshPreview(); } }; grid.Children.Add(radio);
    }

    private void RefreshPreview()
    {
        if (_loading) return;
        _angle.IsEnabled = _input.Form == PileHeadForm.Splayed;
        _waterproof.IsEnabled = _input.Form == PileHeadForm.Waterproof;
        if (LastResult != null) { LastResult = null; _state.Text = "参数已修改，请重新应用桩顶构造。"; }
        _previewOk = false;
        if (_configurationMode && !_anchorageEnabled)
        {
            _anchor.Text = "无锚固：按主窗口原有参数生成三类钢筋。"; _source.Text = "无需选择材料或填写锚固条件。";
            _previewOk = true; SetBusy(_busy); return;
        }
        if (_snapshot == null) { _anchor.Text = "选择柱后读取原主筋直径，再自动计算锚固长度。"; _source.Text = ""; SetBusy(_busy); return; }
        if (_invalid.Count > 0) { _anchor.Text = "请修正无效尺寸。"; _source.Text = ""; SetBusy(_busy); return; }
        try
        {
            var preview = PileHeadWorkflow.Preview(_snapshot, _input);
            _previewOk = preview["status"]?.ToString() == "PASS";
            if (_previewOk)
            {
                _anchor.Text = _input.Form == PileHeadForm.Bent90
                    ? $"自动计算：竖向投影 {preview["vertical_projection_mm"]} mm · 外伸水平段 {preview["horizontal_length_mm"]} mm"
                    : $"自动计算：柱顶以上锚固竖向投影 {preview["anchor_length_mm"]} mm";
                _source.Text = preview["source_summary"]?.ToString() ?? "";
            }
            else { _anchor.Text = preview["message"]?.ToString() ?? "当前条件尚不能计算。"; _source.Text = preview["source_summary"]?.ToString() ?? ""; }
        }
        catch (Exception ex) { _anchor.Text = ex.Message; _source.Text = ""; }
        SetBusy(_busy);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; _select.IsEnabled = !busy; _body.IsEnabled = !busy;
        foreach (var item in _body.Children.OfType<FrameworkElement>().Where(item => Equals(item.Tag, "AnchorageInput")))
            item.IsEnabled = !_configurationMode || _anchorageEnabled;
        _apply.IsEnabled = !busy && (_configurationMode && !_anchorageEnabled || _snapshot != null && _previewOk && _invalid.Count == 0);
    }

    private void LoadColumn(UIApplication ui, ElementId id)
    {
        var snapshot = PileHeadWorkflow.Inspect(ui, id); _snapshot = snapshot;
        _input.HostId = snapshot.HostId; _input.HostDocumentUniqueId = snapshot.HostDocumentUniqueId;
        if (snapshot.ExistingForm.HasValue) _input.Form = snapshot.ExistingForm.Value;
        LastResult = null;
        _host.Text = $"柱 ID {snapshot.HostId} · D{Ui.N(snapshot.Diameter)} × H{Ui.N(snapshot.Height)} mm\n原主筋 {snapshot.MainCount} 根 · Φ{Ui.N(snapshot.MainDiameter)} mm";
        BuildFields(); _state.Text = "已读取原主筋。选择构造形式及计算条件后应用。"; SetBusy(false);
    }

    private void PickColumn()
    {
        if (_configurationMode) return;
        if (_busy) return; SetBusy(true); Hide();
        try
        {
            App.Enqueue(ui =>
            {
                try { var reference = ui.ActiveUIDocument.Selection.PickObject(ObjectType.Element, new ColumnFilter(), "选择已有纵向主筋的圆形混凝土柱。"); LoadColumn(ui, reference.ElementId); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { _state.Text = "已取消选择。"; }
                catch (Exception ex) { _state.Text = "无法读取：" + ex.Message; }
                finally { SetBusy(false); Show(); Activate(); }
            });
        }
        catch (Exception ex) { SetBusy(false); Show(); _state.Text = "无法开始选择：" + ex.Message; }
    }

    private void Apply()
    {
        if (_busy) return; RefreshPreview(); if (!_previewOk) return;
        if (_configurationMode)
        {
            _savedConfiguration = _anchorageEnabled ? _input.Clone() : null;
            _configurationConfirmed = true; DialogResult = true; return;
        }
        var request = _input.Clone(); LastResult = null; SetBusy(true); _state.Text = "正在应用桩顶构造并核对原生钢筋几何…";
        try
        {
            App.Enqueue(ui =>
            {
                try
                {
                    var result = PileHeadWorkflow.Generate(ui, request); LastResult = result;
                    _state.Text = result["message"]?.ToString() ?? (result["status"]?.ToString() == "PASS" ? "桩顶构造已应用，原有箍筋保留。" : "桩顶构造未能完成，请检查计算条件。");
                    if (result.ContainsKey("display_error")) _state.Text = "构造已应用，但视图显示失败：" + result["display_error"];
                }
                catch (Exception ex) { _state.Text = "应用失败：" + ex.Message; }
                finally { SetBusy(false); }
            });
        }
        catch (Exception ex) { SetBusy(false); _state.Text = "无法开始应用：" + ex.Message; }
    }

    private sealed record ChoiceItem(string Value, string Label);
    private sealed class ColumnFilter : ISelectionFilter
    {
        public bool AllowElement(Element element) => element.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}

internal sealed class PileHeadMiniDiagram : FrameworkElement
{
    public PileHeadForm Form { get; init; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.PushTransform(new ScaleTransform(ActualWidth / 84, ActualHeight / 70));
        var outline = new Pen(Ui.Brush("#9AAAB6"), 1); var steel = new Pen(Ui.Accent, 2.2);
        dc.DrawRectangle(Ui.Brush("#EDF2F5"), outline, new Rect(3, 3, 78, 39));
        dc.DrawRectangle(Ui.Brush("#F8FAFB"), outline, new Rect(27, 39, 30, 29));
        if (Form == PileHeadForm.Waterproof) dc.DrawRectangle(Ui.Brush("#80BDDC"), null, new Rect(15, 39, 54, 5));
        foreach (bool right in new[] { false, true })
        {
            double x = right ? 49 : 35;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new WpfPoint(x, 65), false, false);
                path.LineTo(new WpfPoint(x, 40), true, false);
                if (Form == PileHeadForm.Bent90)
                {
                    path.LineTo(new WpfPoint(x, 21), true, false);
                    path.QuadraticBezierTo(new WpfPoint(x, 16), new WpfPoint(x + (right ? 5 : -5), 16), true, false);
                    path.LineTo(new WpfPoint(right ? 72 : 12, 16), true, false);
                }
                else if (Form == PileHeadForm.Splayed) path.LineTo(new WpfPoint(x + (right ? 8 : -8), 10), true, false);
                else path.LineTo(new WpfPoint(x, 10), true, false);
            }
            dc.DrawGeometry(null, steel, geometry);
        }
        dc.Pop();
    }
}
