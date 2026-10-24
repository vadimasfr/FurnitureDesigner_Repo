using System.Globalization;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Import;

public readonly record struct Vec3(double X, double Y, double Z);

/// <summary>A polygon read from a file, with the name of its layer / material / object.</summary>
public sealed record MeshFace(IReadOnlyList<Vec3> Points, string Layer, string? Color = null);

public enum UpAxis { Y, Z }

/// <summary>Raw faces read from an exported file, before they are turned into boards.</summary>
public sealed class MeshData
{
    public List<MeshFace> Faces { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>Millimetres per file unit when the file declares its units, otherwise null.</summary>
    public double? MillimetresPerUnit { get; set; }
    public UpAxis Up { get; set; }
}

public sealed record ImportOptions
{
    /// <summary>Millimetres per file unit; null detects it from the file or the board thicknesses.</summary>
    public double? MillimetresPerUnit { get; init; }
    /// <summary>Null uses the file format's convention (DXF: Z up, OBJ: Y up).</summary>
    public UpAxis? Up { get; init; }
}

public sealed record ImportResult(
    IReadOnlyList<Board> Boards,
    IReadOnlyList<BoardMaterial> Materials,
    IReadOnlyList<string> Warnings,
    double MillimetresPerUnit);

/// <summary>
/// Imports furniture exported from PRO100 (or any CAD program) as 3D DXF or OBJ.
/// The native PRO100 .pro format is closed, so the 3D export is used instead: every closed
/// box in the export becomes one board.
/// </summary>
public static class FurnitureImporter
{
    public const string FileFilter = "PRO100 3D export (*.dxf;*.obj)|*.dxf;*.obj|DXF (*.dxf)|*.dxf|OBJ (*.obj)|*.obj";

    public static ImportResult ImportFile(string path, ImportOptions? options = null)
    {
        string mtl = Path.ChangeExtension(path, ".mtl");
        bool isObj = Path.GetExtension(path).Equals(".obj", StringComparison.OrdinalIgnoreCase);
        return Import(Path.GetFileName(path), () => File.ReadAllText(path),
            isObj && File.Exists(mtl) ? File.ReadAllText(mtl) : null, options);
    }

    /// <summary>
    /// Imports file contents (e.g. an upload). <paramref name="fileName"/> picks the format;
    /// <paramref name="mtlText"/> is the .mtl material library that goes with an .obj, if any.
    /// </summary>
    public static ImportResult Import(string fileName, string text, string? mtlText = null, ImportOptions? options = null) =>
        Import(fileName, () => text, mtlText, options);

    private static ImportResult Import(string fileName, Func<string> readText, string? mtlText, ImportOptions? options)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        MeshData mesh = extension switch
        {
            ".dxf" => DxfReader.Read(readText()),
            ".obj" => ObjReader.Read(readText(), MaterialLibrary(mtlText)),
            ".pro" or ".prox" => throw new NotSupportedException(
                "PRO100 project files (.pro) use a closed, undocumented format. " +
                "In PRO100 export the project as 3D DXF (or OBJ) and import that file."),
            _ => throw new NotSupportedException($"Unsupported file type '{extension}'."),
        };
        return BoardReconstructor.Build(mesh, options ?? new ImportOptions());
    }

    /// <summary>Reads Kd colours from the .mtl material library of an .obj, if any.</summary>
    private static IReadOnlyDictionary<string, string> MaterialLibrary(string? mtlText)
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mtlText is null)
            return colors;
        string? current = null;
        foreach (var raw in mtlText.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "newmtl")
                current = string.Join(' ', parts[1..]);
            else if (parts.Length >= 4 && parts[0] == "Kd" && current is not null)
            {
                static int C(string s) => (int)Math.Clamp(Math.Round(double.Parse(s, CultureInfo.InvariantCulture) * 255), 0, 255);
                colors[current] = $"#{C(parts[1]):X2}{C(parts[2]):X2}{C(parts[3]):X2}";
            }
        }
        return colors;
    }
}
