using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Templates;

/// <summary>Parameters of a parametric furniture template. All sizes in millimetres.</summary>
public sealed record TemplateParameters
{
    public double Width { get; init; } = 600;
    public double Height { get; init; } = 720;
    public double Depth { get; init; } = 560;
    public double Thickness { get; init; } = 18;
    public double Plinth { get; init; }
    /// <summary>Height of the bottom of the item above the floor (wall cabinets, countertops).</summary>
    public double Elevation { get; init; }
    public int Shelves { get; init; }
    public int Doors { get; init; }
    public int Drawers { get; init; }
    public string CarcassMaterialId { get; init; } = "white";
    public string FrontMaterialId { get; init; } = "oak";
    public string BackMaterialId { get; init; } = "hdf";
    public FrontStyle FrontStyle { get; init; } = FrontStyle.Shaker;
}

/// <param name="IsCabinet">True for carcass templates; false for single boards (only W/H/D and one material apply).</param>
/// <param name="Build">Creates the boards with the item's minimum corner at the origin.</param>
public sealed record FurnitureTemplate(
    string Key,
    string Name,
    string Category,
    bool IsCabinet,
    TemplateParameters Defaults,
    Func<TemplateParameters, IReadOnlyList<Board>> Build);
