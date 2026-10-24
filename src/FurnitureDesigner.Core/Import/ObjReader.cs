using System.Globalization;

namespace FurnitureDesigner.Core.Import;

/// <summary>Wavefront OBJ reader: vertices and faces; the layer is the material (usemtl) or else the object/group name.</summary>
public static class ObjReader
{
    public static MeshData Read(string text, IReadOnlyDictionary<string, string>? materialColors = null)
    {
        var mesh = new MeshData { Up = UpAxis.Y };
        var vertices = new List<Vec3>();
        string group = "default";
        string? material = null;

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;
            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    vertices.Add(new Vec3(Number(parts[1]), Number(parts[2]), Number(parts[3])));
                    break;
                case "o" or "g" when parts.Length >= 2:
                    group = string.Join(' ', parts[1..]);
                    break;
                case "usemtl" when parts.Length >= 2:
                    material = string.Join(' ', parts[1..]);
                    break;
                case "f" when parts.Length >= 4:
                    var points = parts[1..]
                        .Select(p => int.Parse(p.Split('/')[0], CultureInfo.InvariantCulture))
                        .Select(k => k < 0 ? vertices.Count + k : k - 1)
                        .Where(k => k >= 0 && k < vertices.Count)
                        .Select(k => vertices[k])
                        .Distinct()
                        .ToList();
                    if (points.Count >= 3)
                    {
                        string layer = material ?? group;
                        mesh.Faces.Add(new MeshFace(points, layer, materialColors?.GetValueOrDefault(layer)));
                    }
                    break;
            }
        }
        return mesh;
    }

    private static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}
