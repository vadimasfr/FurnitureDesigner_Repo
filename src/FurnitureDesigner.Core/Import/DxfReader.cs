using System.Globalization;

namespace FurnitureDesigner.Core.Import;

/// <summary>
/// Minimal ASCII DXF reader for 3D exports: 3DFACE entities and polyface meshes (POLYLINE with flag 64).
/// Reads $INSUNITS from the header. Blocks (INSERT) are not expanded.
/// </summary>
public static class DxfReader
{
    public static MeshData Read(string text)
    {
        var mesh = new MeshData { Up = UpAxis.Z };
        var pairs = Pairs(text).ToList();
        int inserts = 0;
        string section = "";

        for (int i = 0; i < pairs.Count; i++)
        {
            var (code, value) = pairs[i];
            if (code != 0 && !(code == 9 && section == "HEADER"))
            {
                if (code == 2 && i > 0 && pairs[i - 1] is (0, "SECTION"))
                    section = value;
                continue;
            }

            if (code == 9 && value == "$INSUNITS" && i + 1 < pairs.Count)
            {
                mesh.MillimetresPerUnit = pairs[i + 1].Value.Trim() switch
                {
                    "1" => 25.4,   // inches
                    "4" => 1,      // mm
                    "5" => 10,     // cm
                    "6" => 1000,   // m
                    _ => null,
                };
                continue;
            }

            if (section != "ENTITIES")
                continue;

            switch (value)
            {
                case "3DFACE":
                    ReadFace(pairs, i, mesh);
                    break;
                case "POLYLINE":
                    i = ReadPolyline(pairs, i, mesh);
                    break;
                case "INSERT":
                    inserts++;
                    break;
            }
        }

        if (inserts > 0)
            mesh.Warnings.Add($"{inserts} block references (INSERT) were skipped; export without blocks if parts are missing.");
        return mesh;
    }

    private static void ReadFace(List<(int Code, string Value)> pairs, int start, MeshData mesh)
    {
        var x = new double[4];
        var y = new double[4];
        var z = new double[4];
        string layer = "0";
        string? color = null;
        for (int j = start + 1; j < pairs.Count && pairs[j].Code != 0; j++)
        {
            var (code, value) = pairs[j];
            switch (code)
            {
                case 8: layer = value; break;
                case 62: color = value; break;
                case >= 10 and <= 13: x[code - 10] = Number(value); break;
                case >= 20 and <= 23: y[code - 20] = Number(value); break;
                case >= 30 and <= 33: z[code - 30] = Number(value); break;
            }
        }
        var points = Enumerable.Range(0, 4).Select(k => new Vec3(x[k], y[k], z[k])).Distinct().ToList();
        if (points.Count >= 3)
            mesh.Faces.Add(new MeshFace(points, layer, AciColor(color)));
    }

    /// <summary>Reads a POLYLINE ... VERTEX ... SEQEND block; returns the index of SEQEND.</summary>
    private static int ReadPolyline(List<(int Code, string Value)> pairs, int start, MeshData mesh)
    {
        string layer = "0";
        string? color = null;
        int flags = 0;
        int j = start + 1;
        for (; j < pairs.Count && pairs[j].Code != 0; j++)
        {
            switch (pairs[j].Code)
            {
                case 8: layer = pairs[j].Value; break;
                case 62: color = pairs[j].Value; break;
                case 70: flags = (int)Number(pairs[j].Value); break;
            }
        }

        var vertices = new List<Vec3>();
        var faces = new List<int[]>();
        while (j < pairs.Count && pairs[j] is (0, "VERTEX"))
        {
            double vx = 0, vy = 0, vz = 0;
            int vflags = 0;
            var indices = new int[4];
            for (j++; j < pairs.Count && pairs[j].Code != 0; j++)
            {
                var (code, value) = pairs[j];
                switch (code)
                {
                    case 10: vx = Number(value); break;
                    case 20: vy = Number(value); break;
                    case 30: vz = Number(value); break;
                    case 70: vflags = (int)Number(value); break;
                    case >= 71 and <= 74: indices[code - 71] = (int)Number(value); break;
                }
            }
            if ((vflags & 128) != 0 && (vflags & 64) == 0)
                faces.Add(indices);
            else
                vertices.Add(new Vec3(vx, vy, vz));
        }

        if ((flags & 64) != 0)
        {
            foreach (var face in faces)
            {
                // Indices are 1-based; negative means an invisible edge.
                var points = face.Where(k => k != 0).Select(k => Math.Abs(k) - 1)
                    .Where(k => k < vertices.Count).Select(k => vertices[k]).Distinct().ToList();
                if (points.Count >= 3)
                    mesh.Faces.Add(new MeshFace(points, layer, AciColor(color)));
            }
        }
        return j; // at SEQEND
    }

    private static IEnumerable<(int Code, string Value)> Pairs(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } codeLine && reader.ReadLine() is { } valueLine)
        {
            if (int.TryParse(codeLine.Trim(), out int code))
                yield return (code, valueLine.Trim());
        }
    }

    private static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>The standard AutoCAD colours 1-9; others are left to the importer's palette.</summary>
    private static string? AciColor(string? aci) => aci?.Trim() switch
    {
        "1" => "#FF0000",
        "2" => "#FFFF00",
        "3" => "#00FF00",
        "4" => "#00FFFF",
        "5" => "#0000FF",
        "6" => "#FF00FF",
        "7" => "#F0F0F0",
        "8" => "#808080",
        "9" => "#C0C0C0",
        _ => null,
    };
}
