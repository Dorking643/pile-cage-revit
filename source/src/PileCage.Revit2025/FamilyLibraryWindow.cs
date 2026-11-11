using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PileCage;

/// <summary>A selection-only dialog. Revit loading happens after confirmation.</summary>
public sealed class FamilyLibraryWindow : Window
{
    public static FamilyLibraryWindow? Instance { get; private set; }
    public bool Confirmed { get; private set; }
    public IReadOnlyList<string> SelectedIds => _choices.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToArray();
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private readonly Button _load;
    private readonly TextBlock _selection = Ui.Text("Selected: 0", 11, Ui.Muted);

    private FamilyLibraryWindow(Window owner, IReadOnlyList<FamilyLibraryItem> catalog)
    {
        if (catalog.Any(x => string.IsNullOrWhiteSpace(x.Id)) || catalog.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Count)
            throw new InvalidOperationException("Invalid or duplicate family IDs.");
        Owner = owner; Instance = this;
        Title = "Load Families"; Width = 500; MinWidth = 420; MinHeight = 360;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32); Height = Math.Min(630, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Ui.Back; Foreground = Ui.Ink;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12;
        AutomationProperties.SetAutomationId(this, "FamilyLibrary_Window");
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var filterLabel = Ui.Text("Category", 11); filterLabel.Width = 72; DockPanel.SetDock(filterLabel, Dock.Left); filterRow.Children.Add(filterLabel);
        // Keep the one component-family group easy to find without scrolling
        // through every steel diameter in the catalog.
        var groupNames = catalog.Select(GroupName).Distinct().OrderBy(name => name.StartsWith("Rebar", StringComparison.Ordinal) ? 1 : 0).ToArray();
        var filter = new ComboBox { ItemsSource = new[] { "All" }.Concat(groupNames).ToArray(), SelectedIndex = 0, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(filter, "FamilyLibrary_Filter"); filterRow.Children.Add(filter); header.Children.Add(filterRow); root.Children.Add(header);
        var list = new StackPanel();
        var sections = new List<(string Name, FrameworkElement Element)>();
        foreach (var group in catalog.GroupBy(GroupName))
        {
            var content = new StackPanel();
            foreach (var item in group)
            {
                var details = new StackPanel();
                details.Children.Add(Ui.Text(ItemName(item), 12, Ui.Ink, true));
                var choice = new CheckBox { Content = details, IsChecked = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(5, 1, 0, 1), Margin = new Thickness(0, 0, 0, 9) };
                AutomationProperties.SetAutomationId(choice, "FamilyLibrary_Item_" + item.Id);
                AutomationProperties.SetName(choice, ItemName(item));
                _choices.Add(item.Id, choice); content.Children.Add(choice);
                choice.Checked += (_, _) => RefreshSelection(); choice.Unchecked += (_, _) => RefreshSelection();
            }
            var heading = Ui.Text(group.Key, 13, Ui.Ink, true); heading.Margin = new Thickness(0, 0, 0, 10);
            var section = new StackPanel(); section.Children.Add(heading); section.Children.Add(content);
            var card = Ui.Card(section, new Thickness(10)); card.Margin = new Thickness(0, 0, 0, 8); list.Children.Add(card); sections.Add((group.Key, card));
        }
        if (catalog.Count == 0) list.Children.Add(Ui.Text("No families available.", 12, Ui.Muted));
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 5, 0) };
        AutomationProperties.SetAutomationId(scroll, "FamilyLibrary_Items"); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        filter.SelectionChanged += (_, _) =>
        {
            var selected = filter.SelectedItem as string;
            foreach (var section in sections) section.Element.Visibility = selected == "All" || selected == section.Name ? Visibility.Visible : Visibility.Collapsed;
            scroll.ScrollToTop();
        };
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(_selection, "FamilyLibrary_SelectionSummary"); _selection.Margin = new Thickness(0, 0, 0, 9); footer.Children.Add(_selection);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _load = Ui.Button("Load Selected", Confirm, true); _load.IsDefault = true; AutomationProperties.SetAutomationId(_load, "FamilyLibrary_LoadSelected"); buttons.Children.Add(_load);
        var cancel = Ui.Button("Cancel", Cancel); cancel.IsCancel = true; cancel.Margin = new Thickness(0); AutomationProperties.SetAutomationId(cancel, "FamilyLibrary_Cancel"); buttons.Children.Add(cancel);
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        Closed += (_, _) => { if (ReferenceEquals(Instance, this)) Instance = null; };
        RefreshSelection();
    }

    private static string GroupName(FamilyLibraryItem item)
    {
        if (item.Kind != "钢筋类型") return "Positioning Plate";
        // Match the seismic-grade suffix before its ordinary-grade prefix.
        foreach (string grade in new[] { "HPB300", "HRB400E", "HRB400", "HRB500E", "HRB500", "HRB335" })
            if (item.Name.Contains(grade, StringComparison.OrdinalIgnoreCase)) return "Rebar · " + grade;
        return "Rebar";
    }

    private static string ItemName(FamilyLibraryItem item) => item.Kind == "钢筋类型"
        ? $"{item.Grade} · Ø{Ui.N(item.DiameterMm)} mm"
        : "Positioning Plate";

    public static FamilyLibraryWindow CreateChooser(Window owner, IReadOnlyList<FamilyLibraryItem> catalog) => new(owner, catalog);

    public static IReadOnlyList<string>? Choose(Window owner, IReadOnlyList<FamilyLibraryItem> catalog)
    {
        var window = CreateChooser(owner, catalog); window.ShowDialog();
        return window.Confirmed ? window.SelectedIds : null;
    }

    /// <summary>UI-only selection helper; uses the same checkboxes and change handlers as clicks.</summary>
    public void SetSelectedIds(IEnumerable<string> selectedIds)
    {
        ArgumentNullException.ThrowIfNull(selectedIds);
        var ids = selectedIds.ToHashSet(StringComparer.Ordinal);
        Dispatcher.Invoke(() =>
        {
            var unknown = ids.Where(id => !_choices.ContainsKey(id)).ToArray();
            if (unknown.Length > 0) throw new ArgumentException("Unknown library IDs: " + string.Join(", ", unknown), nameof(selectedIds));
            foreach (var choice in _choices) choice.Value.IsChecked = ids.Contains(choice.Key);
        });
    }

    /// <summary>Uses the normal confirmation checks; does not load or alter a Revit model.</summary>
    public void ConfirmSelection() => Dispatcher.Invoke(Confirm);

    /// <summary>Uses the same cancellation path as the visible Cancel button.</summary>
    public void CancelSelection() => Dispatcher.Invoke(Cancel);

    private void RefreshSelection()
    {
        int count = SelectedIds.Count; _selection.Text = $"Selected: {count}";
        _load.IsEnabled = count > 0;
    }

    private void Confirm()
    {
        if (SelectedIds.Count == 0) return;
        Confirmed = true; DialogResult = true;
    }

    private void Cancel()
    {
        Confirmed = false; Close();
    }

    public void CaptureScreenshot(string path)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateLayout(); var content = Content as FrameworkElement ?? throw new InvalidOperationException("Library window is not initialized.");
            var dpi = VisualTreeHelper.GetDpi(content);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual(); var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(Background ?? Brushes.White, null, bounds);
                dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect(0, 0, 1, 1), Stretch = Stretch.Fill }, null, bounds);
            }
            bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = File.Create(path); encoder.Save(stream);
        });
    }
}
