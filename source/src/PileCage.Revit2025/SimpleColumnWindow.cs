using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
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
using Binding = System.Windows.Data.Binding;

namespace PileCage;

[Transaction(TransactionMode.Manual)]
public sealed class SimpleColumnCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        SimpleColumnWindow.ShowFor(data.Application);
        return Result.Succeeded;
    }
}

/// <summary>A small form for generating all three reinforcement components in a selected column.</summary>
public sealed class SimpleColumnWindow : Window
{
    public static SimpleColumnWindow? Instance { get; private set; }
    public CageInput Input => _input.Clone();
    public NativeResult? LastResult { get; private set; }
    public JsonObject? LastFamilyLoadResult { get; private set; }
    public string FamilyLoadSummary => LastFamilyLoadResult?["message"]?.ToString() ?? "";
    public bool IsBusy => _busy;
    public string LastValidationMessage { get; private set; } = "";
    public PileHeadUiInput? AnchorageSettings => _anchorage?.Clone();
    public string AnchorageSummary => _anchorageState.Text;
    public PositionPlateInput? PositionPlateSettings => _positionPlates?.Clone();
    public string PositionPlateSummary => _plateState.Text;
    private CageInput _input = new();
    private PileHeadUiInput? _anchorage;
    private PositionPlateInput? _positionPlates;
    private readonly ObservableCollection<ZoneRow> _zones = [];
    private readonly Dictionary<string, TextBox> _numbers = [];
    private readonly Dictionary<string, ComboBox> _typeBoxes = [];
    private readonly HashSet<string> _invalid = [];
    private List<BarTypeChoice> _types = [];
    private readonly StackPanel _parameters = new();
    private readonly TextBlock _host = Ui.Text("Pile Diameter (mm): —    Pile Height (mm): —", 12);
    private readonly TextBlock _derived = Ui.Text("", 11, Ui.Muted);
    private readonly TextBlock _state = Ui.Text("", 12);
    private readonly TextBlock _anchorageState = Ui.Text("Anchorage: Off", 11, Ui.Muted);
    private readonly TextBlock _plateState = Ui.Text("Positioning Plates: Off", 11, Ui.Muted);
    private Button _select = null!, _generate = null!, _loadFamilyButton = null!, _plateButton = null!;
    private DataGrid _zoneGrid = null!;
    private bool _loading, _busy;

    private SimpleColumnWindow()
    {
        Instance = this;
        Title = "Parametric Rebar Design System";
        Width = 680; MinWidth = 620; MinHeight = 600;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24);
        Height = Math.Min(700, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 12; Background = Ui.Back; Foreground = Ui.Ink;
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        var selectRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _select = Button("Select Pile", "SelectColumn", PickColumn);
        _select.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(_select, Dock.Left); selectRow.Children.Add(_select); selectRow.Children.Add(_host);
        header.Children.Add(selectRow);
        var settingRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };
        _loadFamilyButton = Button("Load Families", "LoadFamily", OpenFamilyLibrary); settingRow.Children.Add(_loadFamilyButton);
        _plateButton = Button("Positioning Plates", "PositionPlateSettings", OpenPositionPlateSettings); settingRow.Children.Add(_plateButton); header.Children.Add(settingRow);
        AutomationProperties.SetAutomationId(_plateState, "Simple_PositionPlateSummary");
        _plateState.Visibility = System.Windows.Visibility.Collapsed; header.Children.Add(_plateState);
        AutomationProperties.SetAutomationId(_host, "Simple_HostGeometry");
        header.Margin = new Thickness(0, 0, 0, 10); root.Children.Add(header);
        var scroll = new ScrollViewer { Content = _parameters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 5, 0) };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _derived.Visibility = System.Windows.Visibility.Collapsed; footer.Children.Add(_derived);
        _state.Margin = new Thickness(0, 0, 0, 9); _state.MaxHeight = 58;
        AutomationProperties.SetAutomationId(_state, "Simple_Result"); footer.Children.Add(_state);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _generate = Button("Generate", "Generate", Generate, true);
        buttons.Children.Add(_generate); var close = Button("Close", "Close", Close); close.Margin = new Thickness(0); buttons.Children.Add(close);
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        BuildFields(); SetBusy(false);
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => { if (ReferenceEquals(Instance, this)) Instance = null; };
    }

    public static void ShowFor(UIApplication ui)
    {
        App.EnsureDispatcher();
        if (Instance != null) { Instance._anchorage = null; Instance.UpdateAnchorageSummary(); Instance.Activate(); return; }
        var window = new SimpleColumnWindow();
        new WindowInteropHelper(window).Owner = ui.MainWindowHandle;
        window.ReadCurrentSelection(ui);
        window.Show();
    }

    public void CaptureScreenshot(string path)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateLayout(); var content = Content as FrameworkElement ?? throw new InvalidOperationException("Parameter window is not initialized.");
            var dpi = VisualTreeHelper.GetDpi(content);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual(); var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(Background ?? Brushes.White, null, bounds);
                // Relative bounds include the whole root visual even when its Margin offsets its drawing.
                dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill }, null, bounds);
            }
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = File.Create(path); encoder.Save(stream);
        });
    }

    private static Button Button(string text, string id, Action action, bool primary = false)
    {
        var button = Ui.Button(text, action, primary);
        AutomationProperties.SetAutomationId(button, "Simple_" + id);
        return button;
    }

    private StackPanel Group(string title)
    {
        var panel = new StackPanel(); var caption = Ui.Text(title, 13, Ui.Ink, true);
        caption.Margin = new Thickness(0, 0, 0, 5); panel.Children.Add(caption);
        var card = Ui.Card(panel, new Thickness(8)); card.Margin = new Thickness(0, 0, 0, 6);
        _parameters.Children.Add(card); return panel;
    }

    private static Grid Row(string label, UIElement editor)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(115) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Ui.Text(label)); Grid.SetColumn(editor, 1); row.Children.Add(editor); return row;
    }

    private TextBox Numeric(string name, double value, Action<double> assign, bool integer = false)
    {
        var editor = Ui.Box(Ui.N(value)); editor.Padding = new Thickness(6, 3, 6, 3); editor.MinHeight = 28;
        AutomationProperties.SetAutomationId(editor, "Simple_" + name); _numbers[name] = editor;
        editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            if (!Ui.Number(editor.Text, out var number) || integer && (number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue))
            {
                _invalid.Add(name); editor.BorderBrush = Ui.Brush("#BB3445");
            }
            else { _invalid.Remove(name); editor.BorderBrush = Ui.Line; assign(number); }
            RefreshDerived();
        };
        return editor;
    }

    private void Pair(StackPanel group, string labelA, TextBox editorA, string labelB, TextBox editorB)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(14) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var a = Row(labelA, editorA); a.ColumnDefinitions[0].Width = new GridLength(112);
        var b = Row(labelB, editorB); b.ColumnDefinitions[0].Width = new GridLength(112);
        row.Children.Add(a); Grid.SetColumn(b, 2); row.Children.Add(b); group.Children.Add(row);
    }

    private ComboBox TypeBox(string name, long selectedId, Action<long, double> assign)
    {
        var box = new ComboBox { ItemsSource = _types, SelectedValuePath = nameof(BarTypeChoice.Id), SelectedValue = selectedId, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding { Converter = new BarTypeLabelConverter() });
        box.ItemTemplate = new DataTemplate { VisualTree = label };
        AutomationProperties.SetAutomationId(box, "Simple_" + name); _typeBoxes[name] = box;
        box.SelectionChanged += (_, _) => { if (!_loading && box.SelectedItem is BarTypeChoice choice) { assign(choice.Id, choice.Diameter); RefreshDerived(); } };
        return box;
    }

    private static void TypeAndNumber(StackPanel group, ComboBox type, string numberLabel, TextBox number)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(62) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(105) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(75) });
        row.Children.Add(Ui.Text("Bar Type")); Grid.SetColumn(type, 1); row.Children.Add(type);
        var label = Ui.Text(numberLabel); Grid.SetColumn(label, 3); row.Children.Add(label);
        Grid.SetColumn(number, 4); row.Children.Add(number); group.Children.Add(row);
    }

    private void BuildFields()
    {
        _loading = true; _parameters.Children.Clear(); _numbers.Clear(); _typeBoxes.Clear(); _invalid.Clear(); _zones.Clear();
        var common = new StackPanel();
        // Chapter 3 defines c at the outer spiral surface. Convert a legacy
        // main-bar-surface value without changing the cage radii.
        if (_input.CoverBasis == CoverBasis.LongitudinalSurface) _input.Cover -= _input.SpiralDiameter;
        _input.CoverBasis = CoverBasis.OutermostSurface;
        var coverRow = Row("Concrete Cover (mm)", Numeric("Cover", _input.Cover, v => _input.Cover = v));
        coverRow.ColumnDefinitions[0].Width = new GridLength(170);
        common.Children.Add(coverRow); _parameters.Children.Add(common);

        var main = Group("Longitudinal Bars");
        TypeAndNumber(main, TypeBox("MainType", _input.MainTypeId, (id, diameter) => { _input.MainTypeId = id; _input.MainDiameter = diameter; }), "Count", Numeric("MainCount", _input.MainCount, v => _input.MainCount = (int)v, true));
        Pair(main, "Start Height (mm)", Numeric("MainStart", _input.MainStart, v => _input.MainStart = v), "End Height (mm)", Numeric("MainEnd", _input.MainEnd, v => _input.MainEnd = v));

        var spiral = Group("Spiral Reinforcement");
        TypeAndNumber(spiral, TypeBox("SpiralType", _input.SpiralTypeId, (id, diameter) => { _input.SpiralTypeId = id; _input.SpiralDiameter = diameter; }), "Start Height (mm)", Numeric("SpiralStart", _input.SpiralStart, v => _input.SpiralStart = v));
        foreach (var zone in _input.Zones)
        {
            var row = new ZoneRow { Id = zone.Id, Length = Ui.N(zone.Length), Pitch = Ui.N(zone.Pitch) };
            row.PropertyChanged += (_, _) => { if (!_loading) { SyncZones(); RefreshDerived(); } }; _zones.Add(row);
        }
        _zoneGrid = new DataGrid { ItemsSource = _zones, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Height = 88, RowHeight = 26, ColumnHeaderHeight = 26, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(0, 0, 0, 5) };
        AutomationProperties.SetAutomationId(_zoneGrid, "Simple_Zones");
        _zoneGrid.Columns.Add(new DataGridTextColumn { Header = "Segment (Bottom–Top)", Binding = new Binding(nameof(ZoneRow.Id)), IsReadOnly = true, Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
        _zoneGrid.Columns.Add(new DataGridTextColumn { Header = "Length (mm)", Binding = new Binding(nameof(ZoneRow.Length)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _zoneGrid.Columns.Add(new DataGridTextColumn { Header = "Pitch (mm)", Binding = new Binding(nameof(ZoneRow.Pitch)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        spiral.Children.Add(_zoneGrid);
        var zoneButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var add = Button("Add Segment", "AddZone", AddZone); var remove = Button("Remove Segment", "RemoveZone", RemoveZone);
        add.Padding = remove.Padding = new Thickness(10, 3, 10, 3); add.MinHeight = remove.MinHeight = 26;
        zoneButtons.Children.Add(add); zoneButtons.Children.Add(remove); spiral.Children.Add(zoneButtons);

        var rings = Group("Internal Stiffening Rings");
        TypeAndNumber(rings, TypeBox("RingType", _input.RingTypeId, (id, diameter) => { _input.RingTypeId = id; _input.RingDiameter = diameter; }), "Spacing (mm)", Numeric("RingSpacing", _input.RingSpacing, v => _input.RingSpacing = v));
        Pair(rings, "First Height (mm)", Numeric("RingStart", _input.RingStart, v => _input.RingStart = v), "End Limit (mm)", Numeric("RingEnd", _input.RingEnd, v => _input.RingEnd = v));
        _loading = false; RefreshDerived();
    }

    private void AddZone()
    {
        CommitZones(); if (_zones.Count >= 100) { _state.Text = "Maximum: 100 segments."; return; }
        var row = new ZoneRow { Id = $"Z{_zones.Count + 1}", Length = "500", Pitch = "200" };
        row.PropertyChanged += (_, _) => { if (!_loading) { SyncZones(); RefreshDerived(); } };
        _zones.Add(row); _zoneGrid.SelectedItem = row; _zoneGrid.ScrollIntoView(row); SyncZones(); RefreshDerived();
    }

    private void RemoveZone()
    {
        CommitZones(); if (_zones.Count <= 1) { _state.Text = "At least one segment is required."; return; }
        var selected = _zoneGrid.SelectedItem as ZoneRow ?? _zones[^1]; _zones.Remove(selected);
        _loading = true; for (int i = 0; i < _zones.Count; i++) _zones[i].Id = $"Z{i + 1}"; _loading = false;
        SyncZones(); RefreshDerived();
    }

    private void CommitZones() { _zoneGrid.CommitEdit(DataGridEditingUnit.Cell, true); _zoneGrid.CommitEdit(DataGridEditingUnit.Row, true); SyncZones(); }

    private void SyncZones()
    {
        var zones = new List<ZoneInput>();
        foreach (var row in _zones)
        {
            if (!Ui.Number(row.Length, out double length) || !Ui.Number(row.Pitch, out double pitch)) { _invalid.Add("Zones"); return; }
            zones.Add(new() { Id = row.Id, Length = length, Pitch = pitch });
        }
        _input.Zones = zones; _invalid.Remove("Zones");
    }

    private void RefreshDerived()
    {
        if (_loading) return;
        if (LastResult != null) LastResult = null;
        _state.Text = "";
        if (_input.HostId <= 0) { _derived.Text = ""; return; }
        if (_invalid.Count > 0) { _derived.Text = ""; return; }
        var radii = CagePlanner.Radii(_input);
        _derived.Text = $"Radii (mm): {Ui.N(radii.Main)}, {Ui.N(radii.Spiral)}, {Ui.N(radii.Ring)}; Spiral End (mm): {Ui.N(_input.SpiralStart + _input.Zones.Sum(z => z.Length))}; Rings: {CagePlanner.RingCount(_input)}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; _select.IsEnabled = !busy; _parameters.IsEnabled = !busy && _input.HostId > 0;
        _loadFamilyButton.IsEnabled = !busy;
        _plateButton.IsEnabled = !busy && _input.HostId > 0;
        _generate.IsEnabled = !busy && _input.HostId > 0 && _types.Count > 0;
    }

    private void ReadCurrentSelection(UIApplication ui)
    {
        try
        {
            if (ui.ActiveUIDocument == null) { _state.Text = "Open a Revit project."; return; }
            var selected = ui.ActiveUIDocument.Selection.GetElementIds();
            if (selected.Count == 1) LoadColumn(ui, selected.Single());
            else _state.Text = "";
        }
        catch (Exception ex) { ReportError("Invalid pile selection.", ex.Message); }
    }

    /// <summary>Choose library content without requiring a selected column.</summary>
    public void OpenFamilyLibrary()
    {
        if (_busy) return;
        IReadOnlyList<string>? selected;
        try
        {
            var catalog = FamilyLibraryService.GetCatalog();
            SetBusy(true);
            selected = FamilyLibraryWindow.Choose(this, catalog);
        }
        catch (Exception ex) { SetBusy(false); ReportError("Cannot open the family library.", ex.Message); return; }
        if (selected == null) { SetBusy(false); return; }
        var ids = selected.ToArray(); LastFamilyLoadResult = null; _state.Text = "Loading…";
        try
        {
            App.Enqueue(ui =>
            {
                try
                {
                    if (_input.HostId > 0 && ui.ActiveUIDocument?.Document.ProjectInformation.UniqueId != _input.HostDocumentUniqueId)
                        throw new InvalidOperationException("Return to the selected pile's project before loading families.");
                    var result = FamilyLibraryService.Load(ui, ids);
                    LastFamilyLoadResult = (JsonObject)result.DeepClone();
                    if (string.Equals(result["status"]?.ToString(), "PASS", StringComparison.OrdinalIgnoreCase))
                    {
                        try { RefreshBarTypeChoices(ui); }
                        catch (Exception ex)
                        {
                            LastFamilyLoadResult["ui_refresh_error"] = ex.Message;
                            ReportError("Families loaded; type list refresh failed.", ex.Message);
                            return;
                        }
                        _state.Text = "";
                    }
                    else ReportError("Family loading failed.", result["message"]?.ToString() ?? "");
                }
                catch (Exception ex)
                {
                    LastFamilyLoadResult = new JsonObject { ["status"] = "FAIL", ["message"] = ex.Message };
                    ReportError("Family loading failed.", ex.Message);
                }
                finally { SetBusy(false); }
            });
        }
        catch (Exception ex)
        {
            LastFamilyLoadResult = new JsonObject { ["status"] = "FAIL", ["message"] = ex.Message };
            SetBusy(false); ReportError("Cannot start family loading.", ex.Message);
        }
    }

    private void RefreshBarTypeChoices(UIApplication ui)
    {
        // Refresh only the lists. Rebuilding fields would discard text edits,
        // reset invalid inputs and overwrite the current column's parameters.
        _types = NativeEngine.GetBarTypes(ui).Select(t => new BarTypeChoice(t.Id, t.Name, t.Diameter)).ToList();
        bool wasLoading = _loading; _loading = true;
        try
        {
            foreach (var (name, selectedId) in new[] { ("MainType", _input.MainTypeId), ("SpiralType", _input.SpiralTypeId), ("RingType", _input.RingTypeId) })
            {
                var box = _typeBoxes[name]; box.ItemsSource = _types; box.SelectedValue = selectedId;
            }
        }
        finally { _loading = wasLoading; }
    }

    private void LoadColumn(UIApplication ui, ElementId id)
    {
        var host = SimpleColumnWorkflow.ReadColumn(ui, id);
        var input = SimpleColumnWorkflow.DefaultInput(ui, host);
        var types = NativeEngine.GetBarTypes(ui).Select(t => new BarTypeChoice(t.Id, t.Name, t.Diameter)).ToList();
        _input = input; _types = types; _anchorage = null; _positionPlates = null; UpdateAnchorageSummary(); UpdatePositionPlateSummary(); LastResult = null; BuildFields();
        _host.Text = $"Pile Diameter (mm): {Ui.N(host.Diameter)}    Pile Height (mm): {Ui.N(host.Height)}\nBase Center (mm): ({Ui.N(host.X)}, {Ui.N(host.Y)}, {Ui.N(host.Z)})";
        _state.Text = types.Count == 0 ? "Load rebar types." : "";
        SetBusy(false);
    }

    public void LoadAnchorageSettings(PileHeadUiInput? settings)
    {
        if (_busy) throw new InvalidOperationException("Generation is in progress.");
        if (settings != null && (_input.HostId <= 0 || settings.HostId != _input.HostId || settings.HostDocumentUniqueId != _input.HostDocumentUniqueId))
            throw new InvalidOperationException("Anchorage settings do not match the selected pile.");
        _anchorage = settings?.Clone(); LastResult = null; UpdateAnchorageSummary();
        _state.Text = "";
    }

    private static string AnchorageName(PileHeadForm form) => form switch
    {
        PileHeadForm.Straight => "Straight", PileHeadForm.Bent90 => "90° Bend", PileHeadForm.Splayed => "Splayed", PileHeadForm.Waterproof => "Straight with Waterproofing", _ => "Unknown"
    };

    public void LoadPositionPlateSettings(PositionPlateInput? settings)
    {
        if (_busy) throw new InvalidOperationException("Generation is in progress.");
        if (_input.HostId <= 0 && settings != null) throw new InvalidOperationException("Select a pile.");
        _positionPlates = settings?.Clone();
        if (_positionPlates != null) _positionPlates.LastLevelLimitFromBaseMm = null;
        LastResult = null; UpdatePositionPlateSummary();
        _state.Text = "";
    }

    private void UpdatePositionPlateSummary()
    {
        _plateState.Text = _positionPlates == null ? "Positioning Plates: Off" : "Positioning Plates: On";
        _plateState.ToolTip = null;
    }

    private void OpenPositionPlateSettings()
    {
        if (_busy) return; CommitZones();
        if (_invalid.Count > 0) { _state.Text = "Check the highlighted inputs."; return; }
        var errors = CagePlanner.Validate(_input);
        if (errors.Count > 0) { ReportError("Invalid reinforcement parameters.", string.Join("\n", errors)); return; }
        var cage = _input.Clone(); var committed = _positionPlates?.Clone(); SetBusy(true);
        try
        {
            App.Enqueue(ui =>
            {
                try
                {
                    if (ui.ActiveUIDocument?.Document.ProjectInformation.UniqueId != cage.HostDocumentUniqueId)
                        throw new InvalidOperationException("The active project does not match the selected pile.");
                    var choices = PositionPlateTypeService.ReadChoices(ui);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SetBusy(false);
                        try
                        {
                            var result = PositionPlateWindow.ConfigureWithChoices(this, cage, committed, choices);
                            if (result.Confirmed) LoadPositionPlateSettings(result.Settings);
                        }
                        catch (Exception ex) { ReportError("Cannot open positioning plate settings.", ex.Message); }
                    }));
                }
                catch (Exception ex) { SetBusy(false); ReportError("Cannot read positioning plate types.", ex.Message); }
            });
        }
        catch (Exception ex) { SetBusy(false); ReportError("Cannot open positioning plate settings.", ex.Message); }
    }

    private void UpdateAnchorageSummary()
    {
        _anchorageState.Text = _anchorage == null ? "Anchorage: Off" : "Anchorage: " + AnchorageName(_anchorage.Form);
        _anchorageState.ToolTip = null;
    }

    private void OpenAnchorageSettings()
    {
        if (_busy) return; CommitZones();
        if (_invalid.Count > 0) { _state.Text = "Check the highlighted inputs."; return; }
        var errors = CagePlanner.Validate(_input);
        if (errors.Count > 0) { ReportError("Invalid reinforcement parameters.", string.Join("\n", errors)); return; }
        var cage = _input.Clone(); var committed = _anchorage?.Clone(); SetBusy(true);
        try
        {
            App.Enqueue(ui =>
            {
                try
                {
                    var planned = SimpleColumnWorkflow.BuildPlannedSnapshot(ui, cage);
                    // Finish the Revit API event before opening the modal parameter editor.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SetBusy(false);
                        try
                        {
                            var result = PileHeadWindow.Configure(this, cage, committed, planned);
                            if (result.Confirmed) LoadAnchorageSettings(result.Settings);
                        }
                        catch (Exception ex) { ReportError("Cannot open anchorage settings.", ex.Message); }
                    }));
                }
                catch (Exception ex) { SetBusy(false); ReportError("Cannot preview anchorage settings.", ex.Message); }
            });
        }
        catch (Exception ex) { SetBusy(false); ReportError("Cannot read anchorage settings.", ex.Message); }
    }

    private void PickColumn()
    {
        if (_busy) return; SetBusy(true); Hide();
        try
        {
            App.Enqueue(ui =>
            {
                try
                {
                    var reference = ui.ActiveUIDocument.Selection.PickObject(ObjectType.Element, new ColumnFilter(), "Select a vertical circular concrete pile.");
                    LoadColumn(ui, reference.ElementId);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { _state.Text = ""; }
                catch (Exception ex) { ReportError("Cannot select the pile.", ex.Message); }
                finally { SetBusy(false); Show(); Activate(); }
            });
        }
        catch (Exception ex) { SetBusy(false); Show(); ReportError("Cannot start pile selection.", ex.Message); }
    }

    private void Generate()
    {
        if (_busy) return; CommitZones();
        if (_invalid.Count > 0) { _state.Text = "Check the highlighted inputs."; return; }
        if (_typeBoxes.Values.Any(box => box.SelectedItem is not BarTypeChoice)) { _state.Text = "Select all three rebar types."; return; }
        var errors = CagePlanner.Validate(_input);
        if (errors.Count > 0) { ReportError("Invalid reinforcement parameters.", string.Join("\n", errors)); return; }
        // Anchorage remains available through its independent workflow, but
        // this simplified window must not apply an invisible retained setting.
        _anchorage = null; UpdateAnchorageSummary();
        var request = _input.Clone(); PileHeadUiInput? anchorage = null; var plates = _positionPlates?.Clone(); LastResult = null; SetBusy(true); _state.Text = "Generating…";
        try
        {
            App.Enqueue(ui =>
            {
                try
                {
                    var result = SimpleColumnWorkflow.Generate(ui, request, anchorage, plates); LastResult = result;
                    if (result.Success && result.Details.TryGetPropertyValue("display_error", out var displayError))
                        ReportError("Reinforcement created; view display failed.", displayError?.ToString() ?? "");
                    else
                        if (result.Success) _state.Text = "";
                        else ReportError("Generation failed.", result.Message);
                }
                catch (Exception ex) { ReportError("Generation failed.", ex.Message); }
                finally { SetBusy(false); }
            });
        }
        catch (Exception ex) { SetBusy(false); ReportError("Cannot start generation.", ex.Message); }
    }

    private void ReportError(string summary, string detail)
    {
        LastValidationMessage = detail;
        _state.Text = summary;
    }

    private sealed class BarTypeLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not BarTypeChoice choice) return "";
            var grade = System.Text.RegularExpressions.Regex.Match(choice.Name, @"(?:HRB|HPB)\d{3}E?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return $"{(grade.Success ? grade.Value.ToUpperInvariant() : $"Type {choice.Id}")} · Ø{Ui.N(choice.Diameter)} mm";
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class ColumnFilter : ISelectionFilter
    {
        public bool AllowElement(Element element) => element.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
