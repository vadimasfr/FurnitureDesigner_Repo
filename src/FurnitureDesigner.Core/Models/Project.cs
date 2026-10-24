namespace FurnitureDesigner.Core.Models;

public sealed class BoardMaterial
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Colour as #RRGGBB.</summary>
    public string Color { get; set; } = "#CCCCCC";
    /// <summary>0..1, below 1 is rendered transparent (glass).</summary>
    public double Opacity { get; set; } = 1;
    public double Thickness { get; set; } = 18;
    public double PricePerM2 { get; set; }
    /// <summary>How the surface is drawn: solid colour, generated wood grain or stone, or an image file.</summary>
    public MaterialTexture Texture { get; set; }
    /// <summary>Image file for <see cref="MaterialTexture.Image"/>.</summary>
    public string? ImagePath { get; set; }
    /// <summary>Size of one texture tile on the board, mm.</summary>
    public double TextureSize { get; set; } = 1000;
}

public enum MaterialTexture { Solid, Wood, Stone, Image }

/// <summary>Look of a door or drawer front (drawn on its face).</summary>
public enum FrontStyle { Flat, Shaker, RaisedPanel, Glass }

/// <summary>A piece of furniture: a named set of boards that are selected and moved together.</summary>
public sealed class FurnitureGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string TemplateKey { get; set; } = "";
    /// <summary>Parameters the item was built with; lets it be rebuilt with new sizes. Null for imported items.</summary>
    public Templates.TemplateParameters? Parameters { get; set; }
    /// <summary>Design of a parametric built-in wardrobe; its boards are regenerated from it. Null for other items.</summary>
    public Parametric.WardrobeDesign? Wardrobe { get; set; }
}

public sealed class Room
{
    public double Width { get; set; } = 4000;
    public double Depth { get; set; } = 3000;
    public double Height { get; set; } = 2600;
    public string FloorColor { get; set; } = "#D8D2C8";
    public string WallColor { get; set; } = "#F4F1EA";
}

public sealed class FurnitureProject
{
    public string Name { get; set; } = "Untitled";
    public Room Room { get; set; } = new();
    public List<BoardMaterial> Materials { get; set; } = [];
    public List<FurnitureGroup> Groups { get; set; } = [];
    public List<Board> Boards { get; set; } = [];

    public FurnitureGroup? FindGroup(Guid? id) => id is null ? null : Groups.FirstOrDefault(g => g.Id == id);
    public BoardMaterial? FindMaterial(string id) => Materials.FirstOrDefault(m => m.Id == id);

    public static FurnitureProject CreateDefault() => new()
    {
        Materials =
        [
            new() { Id = "white", Name = "Melamine white", Color = "#F2F0EB", Thickness = 18, PricePerM2 = 25 },
            new() { Id = "oak", Name = "Oak Sonoma", Color = "#C9A271", Thickness = 18, PricePerM2 = 32, Texture = MaterialTexture.Wood },
            new() { Id = "cherry", Name = "Cherry", Color = "#A0522D", Thickness = 18, PricePerM2 = 45, Texture = MaterialTexture.Wood },
            new() { Id = "walnut", Name = "Walnut", Color = "#6B4A32", Thickness = 18, PricePerM2 = 38, Texture = MaterialTexture.Wood },
            new() { Id = "graphite", Name = "Graphite", Color = "#4A4D52", Thickness = 18, PricePerM2 = 30 },
            new() { Id = "hdf", Name = "HDF white 3 mm", Color = "#E6E6E0", Thickness = 3, PricePerM2 = 8 },
            new() { Id = "worktop", Name = "Granite worktop", Color = "#B8B2A6", Thickness = 38, PricePerM2 = 70, Texture = MaterialTexture.Stone, TextureSize = 800 },
            new() { Id = "glass", Name = "Glass", Color = "#BFD9E0", Opacity = 0.35, Thickness = 4, PricePerM2 = 60 },
            new() { Id = "chrome", Name = "Chrome (rails)", Color = "#C9CDD2", Thickness = 25 },
        ],
    };
}
