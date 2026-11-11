using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PileCage;

/// <summary>Detached values only; safe to pass from an API event to the WPF window.</summary>
public sealed record PositionPlateFamilyChoice(
    long Id,
    string UniqueId,
    string DocumentUniqueId,
    string FamilyName,
    string TypeName,
    string Label,
    PositionPlateInput Dimensions);

/// <summary>
/// Reads the loaded face-based plate family without opening transactions, placing
/// instances, activating symbols, changing the active family type, or saving files.
/// The dimensions in this family are instance parameters. Their type defaults must
/// be read from FamilyType, rather than assumed when Symbol.LookupParameter is null.
/// Existing customized family instances are intentionally not used as defaults.
/// </summary>
public static class PositionPlateTypeService
{
    private const double SnapshotToleranceMm = .000001;
    public const string LoadFamiliesMessage = "当前项目尚未加载可用定位钢片族，请先通过 Load Families 加载面基定位钢片。";

    /// <summary>
    /// Call in a Revit API context, outside any project transaction. Only the
    /// supported single plate family is offered: the existing generator cannot
    /// represent an arbitrary face-based family's different profile.
    /// </summary>
    public static IReadOnlyList<PositionPlateFamilyChoice> ReadChoices(UIApplication ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        var doc = ui.ActiveUIDocument?.Document ?? throw new InvalidOperationException("请先打开Revit项目。");
        if (doc.IsFamilyDocument) throw new InvalidOperationException("请在项目文档中选择已载入的定位钢片族类型。");
        if (doc.IsModifiable || doc.IsReadOnly)
            throw new InvalidOperationException("定位钢片族类型须在项目事务外读取，且项目不能处于只读状态。");

        var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => string.Equals(f.Name, PositionPlateFamilyBuilder.FamilyName, StringComparison.Ordinal)).ToArray();
        if (families.Length == 0) return Array.Empty<PositionPlateFamilyChoice>();
        if (families.Length != 1) throw new InvalidOperationException("项目中存在不唯一的定位钢片族，请检查同名族。");
        var family = families[0];
        if (!FamilyLibraryService.IsPlateFamily(family))
            throw new InvalidOperationException("已载入的定位钢片族未通过面基属性、尺寸公式及真实轮廓校验，请检查族或重新通过 Load Families 加载。");

        var symbols = family.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol)
            .Select(s => s ?? throw new InvalidOperationException("定位钢片族类型已失效。"))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
        if (symbols.Length == 0) throw new InvalidOperationException("已载入的定位钢片族不包含可选类型。");

        // EditFamily creates an independent copy. Never load it back or save it.
        var definition = doc.EditFamily(family);
        try
        {
            var manager = definition.FamilyManager;
            var parameters = manager.Parameters.Cast<FamilyParameter>()
                .ToDictionary(p => p.Definition.Name, StringComparer.Ordinal);
            var types = manager.Types.Cast<FamilyType>().ToArray();
            var result = new List<PositionPlateFamilyChoice>();
            foreach (var symbol in symbols)
            {
                var matches = types.Where(t => string.Equals(t.Name, symbol.Name, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException($"不能唯一读取定位钢片类型“{symbol.Name}”的实际默认尺寸。");
                var type = matches[0];
                double Value(string property)
                {
                    string name = PositionPlateFamilyBuilder.ParameterNames[property];
                    if (!parameters.TryGetValue(name, out var parameter) || !parameter.IsInstance ||
                        parameter.StorageType != StorageType.Double || parameter.Definition.GetDataType() != SpecTypeId.Length)
                        throw new InvalidOperationException($"类型“{symbol.Name}”缺少有效的实例长度参数“{name}”。");
                    double value = (type.AsDouble(parameter) ?? throw new InvalidOperationException($"类型“{symbol.Name}”的“{name}”没有默认值。")) * 304.8;
                    if (!double.IsFinite(value) || value <= 0)
                        throw new InvalidOperationException($"类型“{symbol.Name}”的“{name}”必须为有限正尺寸。");
                    return value;
                }
                var dimensions = new PositionPlateInput
                {
                    FamilyTypeId = symbol.Id.Value, FamilyTypeUniqueId = symbol.UniqueId,
                    FamilyDocumentUniqueId = doc.ProjectInformation.UniqueId, FamilyName = family.Name, FamilyTypeName = symbol.Name,
                    WidthMm = Value(nameof(PositionPlateInput.WidthMm)),
                    ThicknessMm = Value(nameof(PositionPlateInput.ThicknessMm)),
                    BottomLegMm = Value(nameof(PositionPlateInput.BottomLegMm)),
                    LowerRiseMm = Value(nameof(PositionPlateInput.LowerRiseMm)),
                    MiddleLegMm = Value(nameof(PositionPlateInput.MiddleLegMm)),
                    TopLegMm = Value(nameof(PositionPlateInput.TopLegMm)),
                    OutermostProjectionMm = Value(nameof(PositionPlateInput.OutermostProjectionMm)),
                    NominalBlankLengthMm = Value(nameof(PositionPlateInput.NominalBlankLengthMm))
                };
                if (dimensions.OutermostProjectionMm <= dimensions.ThicknessMm)
                    throw new InvalidOperationException($"类型“{symbol.Name}”的外凸尺寸必须大于板厚。");
                result.Add(new(symbol.Id.Value, symbol.UniqueId, doc.ProjectInformation.UniqueId, family.Name, symbol.Name,
                    family.Name + " : " + symbol.Name, dimensions));
            }
            return result;
        }
        finally { definition.Close(false); }
    }

    /// <summary>Pure DTO mapping. Preserve the user start height; derive the end limit from the bar range.</summary>
    public static PositionPlateInput Apply(PositionPlateFamilyChoice choice, PositionPlateInput current)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(current);
        var value = current.Clone();
        CopyDimensions(choice.Dimensions, value);
        value.FamilyTypeId = choice.Id; value.FamilyTypeUniqueId = choice.UniqueId;
        value.FamilyDocumentUniqueId = choice.DocumentUniqueId; value.FamilyName = choice.FamilyName; value.FamilyTypeName = choice.TypeName;
        value.LastLevelLimitFromBaseMm = null;
        return value;
    }

    /// <summary>
    /// Read-only generation preflight. A selected type must still belong to this
    /// document and retain its actual default dimensions. Legacy explicit inputs
    /// without FamilyTypeId remain supported. No silent refresh or fallback occurs.
    /// </summary>
    public static void ValidateSource(UIApplication ui, CageInput cage, PositionPlateInput input)
    {
        ArgumentNullException.ThrowIfNull(ui); ArgumentNullException.ThrowIfNull(cage); ArgumentNullException.ThrowIfNull(input);
        if (!input.FamilyTypeId.HasValue) return;
        var doc = ui.ActiveUIDocument?.Document ?? throw new InvalidOperationException("请先打开Revit项目。");
        if (input.FamilyTypeId.Value <= 0 || string.IsNullOrWhiteSpace(input.FamilyTypeUniqueId) ||
            input.FamilyDocumentUniqueId != doc.ProjectInformation.UniqueId ||
            (!string.IsNullOrWhiteSpace(cage.HostDocumentUniqueId) && cage.HostDocumentUniqueId != doc.ProjectInformation.UniqueId))
            throw new InvalidOperationException("定位钢片族类型所属项目或身份已变化，请重新打开定位钢片设置并选择类型。");
        if (doc.GetElement(new ElementId(input.FamilyTypeId.Value)) is not FamilySymbol symbol ||
            symbol.UniqueId != input.FamilyTypeUniqueId || symbol.Family.Name != input.FamilyName || symbol.Name != input.FamilyTypeName)
            throw new InvalidOperationException("所选定位钢片族类型已删除、替换或重命名，请重新选择类型。");

        var choice = ReadChoices(ui).SingleOrDefault(c => c.Id == input.FamilyTypeId.Value && c.UniqueId == input.FamilyTypeUniqueId)
            ?? throw new InvalidOperationException("所选定位钢片族类型不再可用，请重新选择类型。");
        var actual = Dimensions(choice.Dimensions); var snapshot = Dimensions(input);
        var changed = actual.Where(pair => !double.IsFinite(snapshot[pair.Key]) || Math.Abs(pair.Value - snapshot[pair.Key]) > SnapshotToleranceMm)
            .Select(pair => PositionPlateFamilyBuilder.ParameterNames[pair.Key]).ToArray();
        if (changed.Length > 0)
            throw new InvalidOperationException("定位钢片族类型默认尺寸已变化（" + string.Join("、", changed) + "），请重新打开设置确认后生成。");
    }

    private static void CopyDimensions(PositionPlateInput source, PositionPlateInput target)
    {
        target.WidthMm = source.WidthMm; target.ThicknessMm = source.ThicknessMm;
        target.BottomLegMm = source.BottomLegMm; target.LowerRiseMm = source.LowerRiseMm;
        target.MiddleLegMm = source.MiddleLegMm; target.TopLegMm = source.TopLegMm;
        target.OutermostProjectionMm = source.OutermostProjectionMm; target.NominalBlankLengthMm = source.NominalBlankLengthMm;
    }

    private static IReadOnlyDictionary<string, double> Dimensions(PositionPlateInput input) => new Dictionary<string, double>
    {
        [nameof(input.WidthMm)] = input.WidthMm, [nameof(input.ThicknessMm)] = input.ThicknessMm,
        [nameof(input.BottomLegMm)] = input.BottomLegMm, [nameof(input.LowerRiseMm)] = input.LowerRiseMm,
        [nameof(input.MiddleLegMm)] = input.MiddleLegMm, [nameof(input.TopLegMm)] = input.TopLegMm,
        [nameof(input.OutermostProjectionMm)] = input.OutermostProjectionMm, [nameof(input.NominalBlankLengthMm)] = input.NominalBlankLengthMm
    };
}
