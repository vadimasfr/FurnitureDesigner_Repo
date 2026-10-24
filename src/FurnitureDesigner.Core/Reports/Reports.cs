using System.Globalization;
using System.Text;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Reports;

public sealed record CutListRow(string Material, double Length, double Width, double Thickness, int Quantity, string Parts)
{
    public double AreaM2 => Math.Round(Length * Width * Quantity / 1_000_000, 3);
}

public sealed record CostRow(string Material, double AreaM2, double PricePerM2)
{
    public double Cost => Math.Round(AreaM2 * PricePerM2, 2);
}

public static class ProjectReports
{
    /// <summary>Identical boards (same material and cut size) are merged into one row with a quantity.</summary>
    public static IReadOnlyList<CutListRow> CutList(FurnitureProject project) =>
        project.Boards
            .Select(b => (Board: b, Dim: b.CutDimensions(), Material: project.FindMaterial(b.MaterialId)?.Name ?? b.MaterialId))
            .GroupBy(x => (x.Material, L: Math.Round(x.Dim.Length, 1), W: Math.Round(x.Dim.Width, 1), T: Math.Round(x.Dim.Thickness, 1)))
            .Select(g => new CutListRow(g.Key.Material, g.Key.L, g.Key.W, g.Key.T, g.Count(),
                string.Join(", ", g.Select(x => x.Board.Name).Distinct())))
            .OrderBy(r => r.Material).ThenByDescending(r => r.Thickness).ThenByDescending(r => r.Length).ThenByDescending(r => r.Width)
            .ToList();

    public static IReadOnlyList<CostRow> Costs(FurnitureProject project) =>
        project.Boards
            .GroupBy(b => b.MaterialId)
            .Select(g =>
            {
                var material = project.FindMaterial(g.Key);
                double area = g.Sum(b => { var d = b.CutDimensions(); return d.Length * d.Width; }) / 1_000_000;
                return new CostRow(material?.Name ?? g.Key, Math.Round(area, 3), material?.PricePerM2 ?? 0);
            })
            .OrderBy(r => r.Material)
            .ToList();

    public static string CutListCsv(IEnumerable<CutListRow> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("Material;Length;Width;Thickness;Quantity;AreaM2;Parts\n");
        foreach (var r in rows)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{Escape(r.Material)};{r.Length.ToString(inv)};{r.Width.ToString(inv)};{r.Thickness.ToString(inv)};{r.Quantity};{r.AreaM2.ToString(inv)};{Escape(r.Parts)}\n");
        }
        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.Contains(';') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
