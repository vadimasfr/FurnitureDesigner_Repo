using System.Text.Json.Serialization;

namespace FurnitureDesigner.Core.Models;

public enum BoardKind
{
    Board,
    Side,
    Top,
    Bottom,
    Shelf,
    Back,
    Door,
    DrawerFront,
    DrawerPart,
    Plinth,
    Countertop,
}

/// <summary>
/// A single axis-aligned board (the basic element, like a PRO100 "element").
/// X/Y/Z is the minimum corner; all values are millimetres. X = right, Y = up, Z = towards the viewer.
/// </summary>
public sealed class Board
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Board";
    public BoardKind Kind { get; set; }
    public Guid? GroupId { get; set; }
    public string MaterialId { get; set; } = "";
    /// <summary>Door/drawer-front look; ignored for other boards.</summary>
    public FrontStyle FrontStyle { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double SizeX { get; set; }
    public double SizeY { get; set; }
    public double SizeZ { get; set; }

    [JsonIgnore]
    public Box3 Bounds => new(X, Y, Z, X + SizeX, Y + SizeY, Z + SizeZ);

    /// <summary>Dimensions as used in a cut list: the smallest size is the thickness.</summary>
    public (double Length, double Width, double Thickness) CutDimensions()
    {
        double[] sizes = [SizeX, SizeY, SizeZ];
        Array.Sort(sizes);
        return (sizes[2], sizes[1], sizes[0]);
    }

    public Board Clone(Guid? groupId) => new()
    {
        Name = Name,
        Kind = Kind,
        GroupId = groupId,
        MaterialId = MaterialId,
        FrontStyle = FrontStyle,
        X = X,
        Y = Y,
        Z = Z,
        SizeX = SizeX,
        SizeY = SizeY,
        SizeZ = SizeZ,
    };
}
