using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;

namespace PileCage;

public sealed record PositionPlateConfigurationResult(bool Confirmed, PositionPlateInput? Settings);

/// <summary>Optional positioning plates for the next complete cage generation.</summary>
public sealed class PositionPlateWindow : Window
{
    public static PositionPlateWindow? Instance { get; private set; }
    public PositionPlateInput Input => _input.Clone();
    public bool PlatesEnabled => _enabled;
    public JsonObject? LastPreview { get; private set; }
    public string LastValidationMessage { get; private set; } = "";
    public PositionPlateConfigurationResult ConfigurationResult => new(_confirmed, _saved?.Clone());
    private readonly CageInput _cage;
    private PositionPlateInput _input;
    private readonly ComboBox _familyType;
    private PositionPlateInput? _saved;
    private bool _enabled, _confirmed;
    private readonly HashSet<string> _invalid = [];
    private readonly StackPanel _fields = new();
    private readonly TextBlock _preview = Ui.Text("", 12, Ui.Ink, true);
    private readonly TextBlock _state = Ui.Text("", 11, Ui.Muted);
    private readonly Button _save;

    private PositionPlateWindow(Window owner, CageInput cage, PositionPlateInput? current, IReadOnlyList<PositionPlateFamilyChoice> choices)
    {
        Instance = this; Owner = owner; _cage = cage.Clone(); _input = current?.Clone() ?? PositionPlatePlanner.Defaults(_cage); _enabled = current != null;
        if (current == null) _input.FirstLevelFromBaseMm = null;
        _input.LastLevelLimitFromBaseMm = null;
        Title = "Positioning Plates"; Width = 490; MinWidth = 450; MinHeight = 365;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24); Height = Math.Min(390, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Ui.Back; Foreground = Ui.Ink;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12;
        AutomationProperties.SetAutomationId(this, "PositionPlate_Window");
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var enable = new CheckBox { Content = "Enable", IsChecked = _enabled, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(enable, "PositionPlate_Enable"); header.Children.Add(enable); root.Children.Add(header);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _fields, Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var arrangement = new StackPanel(); _fields.Children.Add(Ui.Card(arrangement, new Thickness(10)));
        var familyLabel = Ui.Text("Family Type", 11); familyLabel.Margin = new Thickness(0, 0, 0, 3); arrangement.Children.Add(familyLabel);
        _familyType = new ComboBox { ItemsSource = choices, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        var typeText = new FrameworkElementFactory(typeof(TextBlock));
        typeText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new PlateTypeLabelConverter() });
        _familyType.ItemTemplate = new DataTemplate { VisualTree = typeText };
        AutomationProperties.SetAutomationId(_familyType, "PositionPlate_FamilyType"); arrangement.Children.Add(_familyType);
        var selected = choices.FirstOrDefault(x => x.UniqueId == _input.FamilyTypeUniqueId && x.DocumentUniqueId == _input.FamilyDocumentUniqueId);
        if (selected == null && string.IsNullOrWhiteSpace(_input.FamilyTypeUniqueId)) selected = choices.FirstOrDefault();
        _familyType.SelectedItem = selected;
        if (selected != null)
        {
            var start = _input.FirstLevelFromBaseMm;
            _input = PositionPlateTypeService.Apply(selected, _input);
            _input.FirstLevelFromBaseMm = start;
        }
        _input.FirstLevelFromBaseMm ??= PositionPlatePlanner.Resolve(_cage, _input).FirstLevelFromBaseMm;
        Pair(arrangement, "Level Spacing (mm)", Number("AxialSpacingMm", _input.AxialSpacingMm, x => _input.AxialSpacingMm = x), "Plates per Level", Number("PerLevelCount", _input.PerLevelCount, x => _input.PerLevelCount = (int)x, true));
        var startLabel = Ui.Text("Start Height (mm)", 11); startLabel.Margin = new Thickness(0, 0, 0, 3); arrangement.Children.Add(startLabel);
        arrangement.Children.Add(Number("FirstLevelFromBaseMm", _input.FirstLevelFromBaseMm ?? double.NaN, x => _input.FirstLevelFromBaseMm = x));

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _preview.Visibility = System.Windows.Visibility.Collapsed; AutomationProperties.SetAutomationId(_preview, "PositionPlate_Preview"); footer.Children.Add(_preview);
        _state.Margin = new Thickness(0, 0, 0, 9); _state.MaxHeight = 90; AutomationProperties.SetAutomationId(_state, "PositionPlate_Result"); footer.Children.Add(_state);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _save = Ui.Button("Apply", Save, true); _save.IsDefault = true; AutomationProperties.SetAutomationId(_save, "PositionPlate_ConfirmSettings"); buttons.Children.Add(_save);
        var cancel = Ui.Button("Cancel", Close); cancel.IsCancel = true; cancel.Margin = new Thickness(0); AutomationProperties.SetAutomationId(cancel, "PositionPlate_CancelSettings"); buttons.Children.Add(cancel); footer.Children.Add(buttons);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        enable.Checked += (_, _) => { _enabled = true; RefreshPreview(); };
        enable.Unchecked += (_, _) => { _enabled = false; RefreshPreview(); };
        _familyType.SelectionChanged += (_, _) =>
        {
            if (_familyType.SelectedItem is PositionPlateFamilyChoice choice)
            {
                var start = _input.FirstLevelFromBaseMm;
                _input = PositionPlateTypeService.Apply(choice, _input);
                _input.FirstLevelFromBaseMm = start;
            }
            RefreshPreview();
        };
        Closed += (_, _) => { if (ReferenceEquals(Instance, this)) Instance = null; };
        RefreshPreview();
    }

    public static PositionPlateWindow CreateConfiguration(Window owner, CageInput cage, PositionPlateInput? current) => new(owner, cage, current, []);
    public static PositionPlateWindow CreateConfigurationWithChoices(Window owner, CageInput cage, PositionPlateInput? current, IReadOnlyList<PositionPlateFamilyChoice> choices) => new(owner, cage, current, choices);
    public static PositionPlateConfigurationResult Configure(Window owner, CageInput cage, PositionPlateInput? current)
    {
        var window = CreateConfiguration(owner, cage, current); window.ShowDialog(); return window.ConfigurationResult;
    }
    public static PositionPlateConfigurationResult ConfigureWithChoices(Window owner, CageInput cage, PositionPlateInput? current, IReadOnlyList<PositionPlateFamilyChoice> choices)
    {
        var window = CreateConfigurationWithChoices(owner, cage, current, choices); window.ShowDialog(); return window.ConfigurationResult;
    }

    private TextBox Number(string name, double value, Action<double> assign, bool integer = false)
    {
        var box = Ui.Box(double.IsFinite(value) ? Ui.N(value) : ""); box.MinHeight = 28; box.Padding = new Thickness(6, 3, 6, 3);
        AutomationProperties.SetAutomationId(box, "PositionPlate_" + name);
        if (!double.IsFinite(value)) _invalid.Add(name);
        box.TextChanged += (_, _) =>
        {
            if (!Ui.Number(box.Text, out var number) || integer && (number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue))
            { _invalid.Add(name); box.BorderBrush = Ui.Brush("#BB3445"); }
            else { _invalid.Remove(name); box.BorderBrush = Ui.Line; assign(number); }
            RefreshPreview();
        };
        return box;
    }

    private static void Pair(StackPanel parent, string labelA, UIElement editorA, string labelB, UIElement editorB)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new());
        var left = new StackPanel { Margin = new Thickness(0, 0, 6, 0) }; var right = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        var a = Ui.Text(labelA, 11); a.Margin = new Thickness(0, 0, 0, 3); left.Children.Add(a); left.Children.Add(editorA);
        var b = Ui.Text(labelB, 11); b.Margin = new Thickness(0, 0, 0, 3); right.Children.Add(b); right.Children.Add(editorB);
        row.Children.Add(left); Grid.SetColumn(right, 1); row.Children.Add(right); parent.Children.Add(row);
    }

    private void RefreshPreview()
    {
        LastPreview = null; _fields.IsEnabled = _enabled; _preview.Text = ""; _state.Text = "";
        if (!_enabled)
        {
            _save.IsEnabled = true; return;
        }
        if (_familyType.SelectedItem is not PositionPlateFamilyChoice) { _state.Text = "Select a loaded positioning plate type."; _save.IsEnabled = false; return; }
        if (_invalid.Count > 0) { _state.Text = "Check the highlighted inputs."; _save.IsEnabled = false; return; }
        try
        {
            var errors = PositionPlatePlanner.Validate(_cage, _input);
            if (errors.Count > 0) { LastValidationMessage = string.Join("\n", errors); _state.Text = ValidationSummary(errors[0]); _save.IsEnabled = false; return; }
            LastPreview = PositionPlatePlanner.Preview(_cage, _input); _save.IsEnabled = true;
        }
        catch (Exception ex) { LastValidationMessage = ex.Message; _state.Text = "Cannot validate positioning plates."; _save.IsEnabled = false; }
    }

    private static string ValidationSummary(string detail)
    {
        if (detail.Contains("整除", StringComparison.Ordinal)) return "Plates per level must be ≥ 3 and divide the bar count.";
        if (detail.Contains("至少", StringComparison.Ordinal) && detail.Contains("2", StringComparison.Ordinal)) return "At least two plate levels are required.";
        if (detail.Contains("穿出", StringComparison.Ordinal)) return "The selected plate type exceeds the pile boundary.";
        if (detail.Contains("范围", StringComparison.Ordinal)) return "The plate type does not fit the bar range.";
        return "Invalid plate type or arrangement.";
    }

    private sealed class PlateTypeLabelConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not PositionPlateFamilyChoice choice) return "";
            string type = string.IsNullOrWhiteSpace(choice.TypeName) || choice.TypeName.Any(c => c >= '\u4e00' && c <= '\u9fff') ? $"Type {choice.Id}" : choice.TypeName;
            return "Positioning Plate · " + type;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
    }

    private void Save()
    {
        if (_enabled)
        {
            RefreshPreview(); if (!_save.IsEnabled) return;
            _saved = _input.Clone();
        }
        else _saved = null;
        _confirmed = true; DialogResult = true;
    }

    public void CaptureScreenshot(string path)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateLayout(); var content = (FrameworkElement)Content; var dpi = VisualTreeHelper.GetDpi(content);
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight); var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(Background, null, bounds);
                dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill }, null, bounds);
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * dpi.DpiScaleX), (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); using var stream = File.Create(path); encoder.Save(stream);
        });
    }
}
