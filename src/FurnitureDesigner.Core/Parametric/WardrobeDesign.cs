using System.Text.Json;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Parametric;

/// <summary>What fills a zone of a wardrobe section.</summary>
public enum ZoneKind { Shelves, HangingRail, Drawers, Open }

/// <summary>A horizontal band of a section. Zones are listed from the floor up.</summary>
public sealed class WardrobeZone
{
    public ZoneKind Kind { get; set; } = ZoneKind.Shelves;
    /// <summary>Number of shelves or drawers (ignored for a rail or an open zone).</summary>
    public int Count { get; set; } = 3;
    /// <summary>Inner height in mm, or null to share the space left by the fixed zones.</summary>
    public double? Height { get; set; }
}

/// <summary>A vertical column of a wardrobe, between two sides or dividers.</summary>
public sealed class WardrobeSection
{
    /// <summary>Inner width in mm, or null to share the space left by the fixed sections.</summary>
    public double? Width { get; set; }
    /// <summary>Doors in front of the section; null = automatic (1 up to 650 mm, else 2), 0 = open.</summary>
    public int? Doors { get; set; }
    public List<WardrobeZone> Zones { get; set; } = [];
}

/// <summary>
/// A built-in wardrobe described by its niche and its sections; <see cref="WardrobeBuilder"/> turns it into boards.
/// Changing any value regenerates every board, so the design stays consistent (space-first design).
/// All sizes in millimetres.
/// </summary>
public sealed class WardrobeDesign
{
    /// <summary>Niche size: wall to wall, floor to ceiling, back wall to the front.</summary>
    public double Width { get; set; } = 2400;
    public double Height { get; set; } = 2500;
    public double Depth { get; set; } = 600;
    /// <summary>Gap left to the side walls and the ceiling, so the carcass fits into a real (not quite square) niche.</summary>
    public double Clearance { get; set; } = 5;
    public double Plinth { get; set; } = 80;
    /// <summary>Height of the separate storage box on top (with its own doors), or 0 for none.</summary>
    public double TopStorage { get; set; } = 450;

    public string CarcassMaterialId { get; set; } = "white";
    public string FrontMaterialId { get; set; } = "oak";
    public string BackMaterialId { get; set; } = "hdf";
    public FrontStyle FrontStyle { get; set; } = FrontStyle.Flat;

    public List<WardrobeSection> Sections { get; set; } = [];

    private static readonly JsonSerializerOptions CloneOptions = new();

    public WardrobeDesign Clone() =>
        JsonSerializer.Deserialize<WardrobeDesign>(JsonSerializer.Serialize(this, CloneOptions), CloneOptions)!;

    /// <summary>A sensible starting design for a niche: sections of about 800–1000 mm, alternating hanging and shelves.</summary>
    public static WardrobeDesign ForNiche(double width, double height, double depth)
    {
        var design = new WardrobeDesign { Width = width, Height = height, Depth = depth };
        int sections = Math.Max(1, (int)Math.Round(width / 900));
        for (int i = 0; i < sections; i++)
        {
            design.Sections.Add(i % 2 == 0
                ? Preset(SectionPreset.Hanging)
                : Preset(SectionPreset.Shelves));
        }
        if (sections >= 3)
            design.Sections[^1] = Preset(SectionPreset.DrawersAndHanging);
        return design;
    }

    public static WardrobeSection Preset(SectionPreset preset) => preset switch
    {
        SectionPreset.Hanging => new()
        {
            Zones = [new() { Kind = ZoneKind.HangingRail }, new() { Kind = ZoneKind.Shelves, Count = 1, Height = 350 }],
        },
        SectionPreset.DrawersAndHanging => new()
        {
            Zones =
            [
                new() { Kind = ZoneKind.Drawers, Count = 3, Height = 600 },
                new() { Kind = ZoneKind.HangingRail },
                new() { Kind = ZoneKind.Shelves, Count = 1, Height = 350 },
            ],
        },
        SectionPreset.LongHanging => new() { Zones = [new() { Kind = ZoneKind.HangingRail }] },
        _ => new() { Zones = [new() { Kind = ZoneKind.Shelves, Count = 5 }] },
    };
}

/// <summary>Ready-made section layouts for quick editing.</summary>
public enum SectionPreset { Shelves, Hanging, DrawersAndHanging, LongHanging }
