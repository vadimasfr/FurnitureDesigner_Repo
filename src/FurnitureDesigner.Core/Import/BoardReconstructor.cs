using System.Text.RegularExpressions;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Import;

/// <summary>
/// Turns imported faces into boards: faces of the same layer that share vertices form one solid,
/// and each solid becomes an axis-aligned board (its bounding box).
/// </summary>
public static partial class BoardReconstructor
{
    private static readonly string[] Palette = ["#E8DCC8", "#C9A271", "#9DB4C0", "#B5838D", "#A3B18A", "#E0B084", "#8D99AE", "#CDB4DB"];

    public static ImportResult Build(MeshData mesh, ImportOptions options)
    {
        var warnings = new List<string>(mesh.Warnings);
        if (mesh.Faces.Count == 0)
        {
            warnings.Add("No 3D faces found. Export from PRO100 in 3D (DXF 3D or OBJ), not as a 2D drawing.");
            return new ImportResult([], [], warnings, 1);
        }

        var up = options.Up ?? mesh.Up;
        var solids = FindSolids(mesh.Faces).Select(s => (s.Layer, s.Color, Points: s.Points.Select(p => ToWorld(p, up)).ToList())).ToList();

        double scale = options.MillimetresPerUnit ?? mesh.MillimetresPerUnit ?? GuessScale(solids.Select(s => Bounds(s.Points)));

        var materials = new Dictionary<string, BoardMaterial>();
        var boards = new List<Board>();
        int flat = 0, skewed = 0;
        foreach (var (layer, color, points) in solids)
        {
            var b = Bounds(points);
            double tolerance = Math.Max(1e-6, Math.Max(b.SizeX, Math.Max(b.SizeY, b.SizeZ)) * 1e-4);
            if (Math.Min(b.SizeX, Math.Min(b.SizeY, b.SizeZ)) <= tolerance)
            {
                flat++;
                continue;
            }
            if (!IsAxisAlignedBox(points, b, tolerance * 10))
                skewed++;

            if (!materials.TryGetValue(layer, out var material))
            {
                material = new BoardMaterial
                {
                    Id = "imp-" + Slug(layer),
                    Name = layer,
                    Color = color ?? Palette[materials.Count % Palette.Length],
                    Thickness = Math.Round(Math.Min(b.SizeX, Math.Min(b.SizeY, b.SizeZ)) * scale, 1),
                };
                materials[layer] = material;
            }

            boards.Add(new Board
            {
                Name = $"{layer} {boards.Count(x => x.MaterialId == material.Id) + 1}",
                MaterialId = material.Id,
                X = b.MinX * scale,
                Y = b.MinY * scale,
                Z = b.MinZ * scale,
                SizeX = b.SizeX * scale,
                SizeY = b.SizeY * scale,
                SizeZ = b.SizeZ * scale,
            });
        }

        // Place the import with its minimum corner at the origin of the room.
        if (Box3.Of(boards) is { } all)
        {
            foreach (var board in boards)
            {
                board.X = Math.Round(board.X - all.MinX, 1);
                board.Y = Math.Round(board.Y - all.MinY, 1);
                board.Z = Math.Round(board.Z - all.MinZ, 1);
                board.SizeX = Math.Round(board.SizeX, 1);
                board.SizeY = Math.Round(board.SizeY, 1);
                board.SizeZ = Math.Round(board.SizeZ, 1);
            }
        }

        if (flat > 0)
            warnings.Add($"{flat} flat shapes (no thickness) were skipped.");
        if (skewed > 0)
            warnings.Add($"{skewed} parts are rotated or not box-shaped; they were imported as their bounding box.");
        return new ImportResult(boards, materials.Values.ToList(), warnings, scale);
    }

    /// <summary>File coordinates to program coordinates (X right, Y up, Z towards the viewer).</summary>
    private static Vec3 ToWorld(Vec3 p, UpAxis up) => up == UpAxis.Z ? new Vec3(p.X, p.Z, -p.Y) : p;

    /// <summary>Groups faces into connected solids (shared vertices, same layer) with union-find.</summary>
    private static IEnumerable<(string Layer, string? Color, List<Vec3> Points)> FindSolids(List<MeshFace> faces)
    {
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        var owner = new Dictionary<(string, long, long, long), int>();
        for (int f = 0; f < faces.Count; f++)
        {
            foreach (var p in faces[f].Points)
            {
                var key = (faces[f].Layer, Key(p.X), Key(p.Y), Key(p.Z));
                if (owner.TryGetValue(key, out int other))
                    parent[Find(f)] = Find(other);
                else
                    owner[key] = f;
            }
        }

        return Enumerable.Range(0, faces.Count)
            .GroupBy(Find)
            .Select(g => (faces[g.Key].Layer, faces[g.Key].Color, g.SelectMany(f => faces[f].Points).Distinct().ToList()));
    }

    private static long Key(double value) => (long)Math.Round(value * 1000);

    private static Box3 Bounds(IReadOnlyList<Vec3> points) => new(
        points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z),
        points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));

    private static bool IsAxisAlignedBox(IEnumerable<Vec3> points, Box3 b, double tolerance)
    {
        static bool Near(double v, double a, double c, double t) => Math.Abs(v - a) <= t || Math.Abs(v - c) <= t;
        return points.All(p => Near(p.X, b.MinX, b.MaxX, tolerance) && Near(p.Y, b.MinY, b.MaxY, tolerance) && Near(p.Z, b.MinZ, b.MaxZ, tolerance));
    }

    /// <summary>
    /// Board thickness is the most reliable hint: furniture boards are 3-40 mm thick.
    /// The median smallest dimension tells whether the file is in mm, cm or m.
    /// </summary>
    private static double GuessScale(IEnumerable<Box3> boxes)
    {
        var thicknesses = boxes.Select(b => Math.Min(b.SizeX, Math.Min(b.SizeY, b.SizeZ))).Where(t => t > 0).Order().ToList();
        if (thicknesses.Count == 0)
            return 1;
        double median = thicknesses[thicknesses.Count / 2];
        return median switch
        {
            < 0.1 => 1000, // metres
            < 5 => 10,     // centimetres (PRO100's default unit)
            _ => 1,        // millimetres
        };
    }

    private static string Slug(string value)
    {
        var slug = NonWord().Replace(value.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "layer" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonWord();
}
