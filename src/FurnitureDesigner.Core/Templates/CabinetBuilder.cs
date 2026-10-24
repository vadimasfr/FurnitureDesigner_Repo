using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Templates;

/// <summary>
/// Builds a standard carcass: two full sides, bottom and top between the sides, HDF back in the rear,
/// optional plinth, evenly spaced shelves and overlay doors or drawers on the front.
/// The back of the cabinet is at Z = 0, the front of the carcass at Z = Depth, fronts sit in front of it.
/// </summary>
public static class CabinetBuilder
{
    public const double BackThickness = 3;
    public const double FrontGap = 2;
    private const double ShelfSetBack = 20;
    private const double DrawerSide = 16;
    private const double DrawerRunnerGap = 13;

    public static IReadOnlyList<Board> Cabinet(TemplateParameters p)
    {
        double t = p.Thickness, w = p.Width, h = p.Height, d = p.Depth, plinth = p.Plinth;
        string carcass = p.CarcassMaterialId;
        var boards = new List<Board>
        {
            Make("Left side", BoardKind.Side, carcass, 0, 0, 0, t, h, d),
            Make("Right side", BoardKind.Side, carcass, w - t, 0, 0, t, h, d),
            Make("Bottom", BoardKind.Bottom, carcass, t, plinth, 0, w - 2 * t, t, d),
            Make("Top", BoardKind.Top, carcass, t, h - t, 0, w - 2 * t, t, d),
            Make("Back", BoardKind.Back, p.BackMaterialId, t, plinth + t, 0, w - 2 * t, h - plinth - 2 * t, BackThickness),
        };
        if (plinth > 0)
            boards.Add(Make("Plinth", BoardKind.Plinth, carcass, t, 0, d - 50 - t, w - 2 * t, plinth, t));

        double innerBottom = plinth + t, innerTop = h - t;
        double pitch = (innerTop - innerBottom) / (p.Shelves + 1);
        for (int i = 1; i <= p.Shelves; i++)
        {
            boards.Add(Make($"Shelf {i}", BoardKind.Shelf, carcass,
                t, innerBottom + pitch * i - t / 2, BackThickness,
                w - 2 * t, t, d - BackThickness - ShelfSetBack));
        }

        if (p.Drawers > 0)
            AddDrawers(boards, p);
        else if (p.Doors > 0)
            AddDoors(boards, p);

        foreach (var front in boards.Where(b => b.Kind is BoardKind.Door or BoardKind.DrawerFront))
            front.FrontStyle = p.FrontStyle;
        return boards;
    }

    public static IReadOnlyList<Board> SingleBoard(string name, BoardKind kind, TemplateParameters p)
    {
        var board = Make(name, kind, p.CarcassMaterialId, 0, 0, 0, p.Width, p.Height, p.Depth);
        if (kind == BoardKind.Door)
            board.FrontStyle = p.FrontStyle;
        return [board];
    }

    private static void AddDoors(List<Board> boards, TemplateParameters p)
    {
        int n = p.Doors;
        double doorWidth = (p.Width - FrontGap * (n + 1)) / n;
        double doorHeight = p.Height - p.Plinth - 2 * FrontGap;
        for (int i = 0; i < n; i++)
        {
            boards.Add(Make($"Door {i + 1}", BoardKind.Door, p.FrontMaterialId,
                FrontGap + i * (doorWidth + FrontGap), p.Plinth + FrontGap, p.Depth,
                doorWidth, doorHeight, p.Thickness));
        }
    }

    private static void AddDrawers(List<Board> boards, TemplateParameters p)
    {
        int n = p.Drawers;
        double t = p.Thickness;
        double frontHeight = (p.Height - p.Plinth - FrontGap * (n + 1)) / n;
        double boxWidth = p.Width - 2 * t - 2 * DrawerRunnerGap;
        double boxDepth = p.Depth - BackThickness - 30;
        double boxX = t + DrawerRunnerGap;
        double boxZ = p.Depth - boxDepth;
        double boxHeight = Math.Max(60, frontHeight - 70);

        for (int i = 0; i < n; i++)
        {
            double y = p.Plinth + FrontGap + i * (frontHeight + FrontGap);
            double boxY = y + 25;
            string label = $"Drawer {i + 1}";
            boards.Add(Make($"{label} front", BoardKind.DrawerFront, p.FrontMaterialId,
                FrontGap, y, p.Depth, p.Width - 2 * FrontGap, frontHeight, t));
            boards.Add(Make($"{label} bottom", BoardKind.DrawerPart, p.BackMaterialId,
                boxX, boxY, boxZ, boxWidth, BackThickness, boxDepth));
            boards.Add(Make($"{label} left", BoardKind.DrawerPart, p.CarcassMaterialId,
                boxX, boxY + BackThickness, boxZ, DrawerSide, boxHeight, boxDepth));
            boards.Add(Make($"{label} right", BoardKind.DrawerPart, p.CarcassMaterialId,
                boxX + boxWidth - DrawerSide, boxY + BackThickness, boxZ, DrawerSide, boxHeight, boxDepth));
            boards.Add(Make($"{label} back", BoardKind.DrawerPart, p.CarcassMaterialId,
                boxX + DrawerSide, boxY + BackThickness, boxZ, boxWidth - 2 * DrawerSide, boxHeight, DrawerSide));
            boards.Add(Make($"{label} inner front", BoardKind.DrawerPart, p.CarcassMaterialId,
                boxX + DrawerSide, boxY + BackThickness, boxZ + boxDepth - DrawerSide, boxWidth - 2 * DrawerSide, boxHeight, DrawerSide));
        }
    }

    private static Board Make(string name, BoardKind kind, string material,
        double x, double y, double z, double sizeX, double sizeY, double sizeZ) => new()
        {
            Name = name,
            Kind = kind,
            MaterialId = material,
            X = x,
            Y = y,
            Z = z,
            SizeX = sizeX,
            SizeY = sizeY,
            SizeZ = sizeZ,
        };
}
