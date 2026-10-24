namespace FurnitureDesigner.Core.Models;

/// <summary>Axis-aligned bounding box in millimetres.</summary>
public readonly record struct Box3(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public double SizeX => MaxX - MinX;
    public double SizeY => MaxY - MinY;
    public double SizeZ => MaxZ - MinZ;
    public double CenterX => (MinX + MaxX) / 2;
    public double CenterZ => (MinZ + MaxZ) / 2;

    public Box3 Union(Box3 other) => new(
        Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Min(MinZ, other.MinZ),
        Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY), Math.Max(MaxZ, other.MaxZ));

    public static Box3? Of(IEnumerable<Board> boards)
    {
        Box3? result = null;
        foreach (var board in boards)
            result = result is { } box ? box.Union(board.Bounds) : board.Bounds;
        return result;
    }
}
