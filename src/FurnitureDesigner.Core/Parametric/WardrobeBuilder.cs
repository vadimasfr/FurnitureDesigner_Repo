using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Parametric;

/// <summary>Boards of a wardrobe plus anything the builder had to adjust to make the design fit.</summary>
public sealed record WardrobeResult(IReadOnlyList<Board> Boards, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a <see cref="WardrobeDesign"/> into boards, with the niche's back-left-bottom corner at the origin.
/// Construction: full-height sides standing on the floor, bottom above the plinth, top under the ceiling clearance,
/// back panel between the sides, dividers between sections, fixed shelves between zones, full-overlay fronts
/// (each front covers half of a neighbouring divider or shelf). Board thickness comes from the materials.
/// </summary>
public static class WardrobeBuilder
{
    public const double FrontGap = 3;
    public const string RailMaterialId = "chrome";
    private const double ShelfSetBack = 20;
    private const double MinPart = 50;
    private const double RailSize = 25;
    private const double RailBelowTop = 60;
    private const double RunnerGap = 13;
    private const double DrawerSide = 16;
    private const double DefaultThickness = 18;

    public static WardrobeResult Build(WardrobeDesign design, IReadOnlyList<BoardMaterial> materials)
    {
        var context = new Context(design, materials);
        context.Build();
        return new WardrobeResult(context.Boards, context.Warnings);
    }

    /// <summary>Doors a section gets: its setting, or automatic by front width.</summary>
    public static int DoorCount(WardrobeSection section, double frontWidth) =>
        Math.Max(0, section.Doors ?? (frontWidth > 650 ? 2 : 1));

    /// <summary>
    /// Splits <paramref name="space"/> between parts with fixed sizes (non-null) and parts sharing the rest (null).
    /// Fixed sizes are scaled down when they do not fit; reports that through <paramref name="warn"/>.
    /// </summary>
    public static double[] Distribute(IReadOnlyList<double?> sizes, double space, Action<string> warn, string what)
    {
        var result = new double[sizes.Count];
        if (sizes.Count == 0)
            return result;
        double fixedSum = sizes.Where(s => s is not null).Sum(s => Math.Max(0, s!.Value));
        int auto = sizes.Count(s => s is null);
        double share = auto > 0 ? (space - fixedSum) / auto : 0;

        if (fixedSum > space || (auto > 0 && share < MinPart) || (auto == 0 && Math.Abs(fixedSum - space) > 0.5))
        {
            // Does not add up: keep the proportions and fill the space exactly.
            double total = fixedSum + auto * Math.Max(MinPart, share);
            for (int i = 0; i < sizes.Count; i++)
                result[i] = (sizes[i] is { } s ? Math.Max(0, s) : Math.Max(MinPart, share)) / Math.Max(1, total) * space;
            warn($"The {what} did not add up to the available {space:0} mm; they were scaled to fit.");
            return result;
        }
        for (int i = 0; i < sizes.Count; i++)
            result[i] = sizes[i] is { } s ? Math.Max(0, s) : share;
        return result;
    }

    private sealed class Context(WardrobeDesign d, IReadOnlyList<BoardMaterial> materials)
    {
        public List<Board> Boards { get; } = [];
        public List<string> Warnings { get; } = [];

        private readonly double t = Thickness(materials, d.CarcassMaterialId, DefaultThickness);
        private readonly double tb = Thickness(materials, d.BackMaterialId, 3);
        private readonly double tf = Thickness(materials, d.FrontMaterialId, DefaultThickness);

        // Carcass outline inside the niche.
        private double x0, width, height, depth, plinth, mainTop;
        // Section interiors (x and width) and the front columns (full overlay edges).
        private double[] sectionX = [], sectionWidth = [], columnLeft = [], columnRight = [];

        public void Build()
        {
            x0 = Math.Max(0, d.Clearance);
            width = d.Width - 2 * x0;
            height = d.Height - x0;
            depth = d.Depth - tf;
            plinth = Math.Max(0, d.Plinth);
            if (width < 4 * t || height < 4 * t || depth < 4 * t)
            {
                Warnings.Add("The niche is too small for a wardrobe.");
                return;
            }

            double top = d.TopStorage;
            if (top > 0 && top < 2 * t + MinPart)
            {
                Warnings.Add($"Top storage must be at least {2 * t + MinPart:0} mm high; it was left out.");
                top = 0;
            }
            if (height - top - plinth < 4 * t + MinPart)
            {
                Warnings.Add("Top storage and plinth leave no room for the main wardrobe; top storage was left out.");
                top = 0;
            }
            mainTop = height - top;

            var sections = d.Sections.Count > 0 ? d.Sections : [new WardrobeSection()];
            LayoutSections(sections);
            BuildBox("", 0, mainTop, plinth, sections.Count);
            for (int k = 0; k < sections.Count; k++)
                BuildSection(sections[k], k);
            if (top > 0)
                BuildTopStorage(sections, top);
        }

        private void LayoutSections(IReadOnlyList<WardrobeSection> sections)
        {
            int n = sections.Count;
            double inner = width - 2 * t - (n - 1) * t;
            var widths = Distribute([.. sections.Select(s => s.Width)], inner, Warnings.Add, "section widths");
            sectionX = new double[n];
            sectionWidth = widths;
            columnLeft = new double[n];
            columnRight = new double[n];
            double x = x0 + t;
            for (int k = 0; k < n; k++)
            {
                sectionX[k] = x;
                columnLeft[k] = k == 0 ? x0 : x - t / 2;
                columnRight[k] = k == n - 1 ? x0 + width : x + widths[k] + t / 2;
                x += widths[k] + t;
            }
        }

        /// <summary>Sides, bottom, top, back and dividers of a box from <paramref name="y0"/> to <paramref name="y1"/>.</summary>
        private void BuildBox(string prefix, double y0, double y1, double bottomY, int sectionCount)
        {
            string c = d.CarcassMaterialId;
            double h = y1 - y0;
            Add($"{prefix}Left side", BoardKind.Side, c, x0, y0, 0, t, h, depth);
            Add($"{prefix}Right side", BoardKind.Side, c, x0 + width - t, y0, 0, t, h, depth);
            Add($"{prefix}Bottom", BoardKind.Bottom, c, x0 + t, bottomY, 0, width - 2 * t, t, depth);
            Add($"{prefix}Top", BoardKind.Top, c, x0 + t, y1 - t, 0, width - 2 * t, t, depth);
            Add($"{prefix}Back", BoardKind.Back, d.BackMaterialId, x0 + t, bottomY + t, 0, width - 2 * t, y1 - bottomY - 2 * t, tb);
            for (int k = 0; k < sectionCount - 1; k++)
            {
                Add($"{prefix}Divider {k + 1}", BoardKind.Side, c,
                    sectionX[k] + sectionWidth[k], bottomY + t, tb, t, y1 - bottomY - 2 * t, depth - tb);
            }
            if (prefix.Length == 0 && plinth > 0)
                Add("Plinth", BoardKind.Plinth, c, x0 + t, 0, depth - 50 - t, width - 2 * t, plinth, t);
        }

        private void BuildSection(WardrobeSection section, int k)
        {
            string name = $"Section {k + 1}";
            double sx = sectionX[k], sw = sectionWidth[k];
            double innerBottom = plinth + t, innerTop = mainTop - t;
            var zones = section.Zones.Count > 0 ? section.Zones : [new WardrobeZone { Kind = ZoneKind.Open }];
            int n = zones.Count;
            var heights = Distribute([.. zones.Select(z => z.Height)], innerTop - innerBottom - (n - 1) * t, Warnings.Add, $"zone heights of {name.ToLowerInvariant()}");

            // Zone interiors and the front edges between them (the middle of the fixed shelves).
            var zoneY = new double[n];
            var edges = new double[n + 1];
            edges[0] = plinth;
            edges[n] = mainTop;
            double y = innerBottom;
            for (int i = 0; i < n; i++)
            {
                zoneY[i] = y;
                y += heights[i];
                if (i < n - 1)
                {
                    Add($"{name} fixed shelf {i + 1}", BoardKind.Shelf, d.CarcassMaterialId, sx, y, tb, sw, t, depth - tb);
                    edges[i + 1] = y + t / 2;
                    y += t;
                }
            }

            // Part names are unique within the wardrobe: they give the boards stable ids across rebuilds.
            int shelfNumber = 0, railNumber = 0, drawerNumber = 0;
            for (int i = 0; i < n; i++)
            {
                var zone = zones[i];
                switch (zone.Kind)
                {
                    case ZoneKind.Shelves:
                        int count = Math.Max(0, zone.Count);
                        double pitch = heights[i] / (count + 1);
                        for (int s = 1; s <= count; s++)
                        {
                            Add($"{name} shelf {++shelfNumber}", BoardKind.Shelf, d.CarcassMaterialId,
                                sx, zoneY[i] + pitch * s - t / 2, tb, sw, t, depth - tb - ShelfSetBack);
                        }
                        break;
                    case ZoneKind.HangingRail:
                        if (heights[i] > RailBelowTop + RailSize)
                        {
                            Add(++railNumber == 1 ? $"{name} hanging rail" : $"{name} hanging rail {railNumber}", BoardKind.Rail, RailMaterialId,
                                sx, zoneY[i] + heights[i] - RailBelowTop - RailSize, (depth - RailSize) / 2, sw, RailSize, RailSize);
                        }
                        break;
                    case ZoneKind.Drawers:
                        AddDrawers(name, k, zone, zoneY[i], heights[i], edges[i], edges[i + 1], ref drawerNumber);
                        break;
                }
            }

            // Doors cover each run of non-drawer zones.
            double frontWidth = columnRight[k] - columnLeft[k];
            int doors = DoorCount(section, frontWidth);
            int runStart = -1;
            int runNumber = 0;
            for (int i = 0; i <= n; i++)
            {
                bool door = i < n && zones[i].Kind != ZoneKind.Drawers;
                if (door && runStart < 0)
                    runStart = i;
                if (!door && runStart >= 0)
                {
                    string label = ++runNumber switch
                    {
                        1 => $"{name} door",
                        2 => $"{name} upper door",
                        _ => $"{name} door {runNumber}",
                    };
                    AddFronts(label, BoardKind.Door, doors, columnLeft[k], columnRight[k], edges[runStart], edges[i]);
                    runStart = -1;
                }
            }
        }

        private void AddDrawers(string name, int k, WardrobeZone zone, double innerY, double innerHeight, double bottomEdge, double topEdge, ref int drawerNumber)
        {
            int count = Math.Max(1, zone.Count);
            double frontHeight = (topEdge - bottomEdge - count * FrontGap) / count;
            double boxWidth = sectionWidth[k] - 2 * RunnerGap;
            double boxDepth = Math.Max(250, Math.Floor((depth - tb - 30) / 50) * 50);
            double boxHeight = Math.Max(60, Math.Min(frontHeight - 70, innerHeight / count - 40));
            double boxX = sectionX[k] + RunnerGap;
            double boxZ = depth - boxDepth;
            if (frontHeight < MinPart || boxWidth < 4 * DrawerSide)
            {
                Warnings.Add($"{name}: no room for {count} drawers.");
                return;
            }

            for (int i = 0; i < count; i++)
            {
                double fy = bottomEdge + FrontGap / 2 + i * (frontHeight + FrontGap);
                double by = Math.Max(innerY, fy + 20);
                string label = $"{name} drawer {++drawerNumber}";
                Add($"{label} front", BoardKind.DrawerFront, d.FrontMaterialId,
                    columnLeft[k] + FrontGap / 2, fy, depth, columnRight[k] - columnLeft[k] - FrontGap, frontHeight, tf)
                    .FrontStyle = d.FrontStyle;
                Add($"{label} bottom", BoardKind.DrawerPart, d.BackMaterialId, boxX, by, boxZ, boxWidth, tb, boxDepth);
                Add($"{label} left", BoardKind.DrawerPart, d.CarcassMaterialId, boxX, by + tb, boxZ, DrawerSide, boxHeight, boxDepth);
                Add($"{label} right", BoardKind.DrawerPart, d.CarcassMaterialId, boxX + boxWidth - DrawerSide, by + tb, boxZ, DrawerSide, boxHeight, boxDepth);
                Add($"{label} back", BoardKind.DrawerPart, d.CarcassMaterialId, boxX + DrawerSide, by + tb, boxZ, boxWidth - 2 * DrawerSide, boxHeight, DrawerSide);
                Add($"{label} inner front", BoardKind.DrawerPart, d.CarcassMaterialId, boxX + DrawerSide, by + tb, boxZ + boxDepth - DrawerSide, boxWidth - 2 * DrawerSide, boxHeight, DrawerSide);
            }
        }

        private void BuildTopStorage(IReadOnlyList<WardrobeSection> sections, double top)
        {
            BuildBox("Top storage ", mainTop, height, mainTop, sections.Count);
            for (int k = 0; k < sections.Count; k++)
            {
                int doors = DoorCount(sections[k], columnRight[k] - columnLeft[k]);
                AddFronts($"Top storage section {k + 1} door", BoardKind.Door, doors, columnLeft[k], columnRight[k], mainTop, height);
            }
        }

        /// <summary>Fronts side by side over a rectangle, with <see cref="FrontGap"/> between and around them.</summary>
        private void AddFronts(string label, BoardKind kind, int count, double left, double right, double bottom, double top)
        {
            if (count <= 0)
                return;
            double w = (right - left - count * FrontGap) / count;
            for (int i = 0; i < count; i++)
            {
                string suffix = count == 1 ? "" : count == 2 ? (i == 0 ? " L" : " R") : $" {i + 1}";
                var front = Add(label + suffix, kind, d.FrontMaterialId,
                    left + FrontGap / 2 + i * (w + FrontGap), bottom + FrontGap / 2, depth, w, top - bottom - FrontGap, tf);
                front.FrontStyle = d.FrontStyle;
            }
        }

        private Board Add(string name, BoardKind kind, string material, double x, double y, double z, double sx, double sy, double sz)
        {
            var board = new Board
            {
                Name = name, Kind = kind, MaterialId = material,
                X = x, Y = y, Z = z,
                SizeX = Math.Max(1, sx), SizeY = Math.Max(1, sy), SizeZ = Math.Max(1, sz),
            };
            Boards.Add(board);
            return board;
        }

        private static double Thickness(IReadOnlyList<BoardMaterial> materials, string id, double fallback) =>
            materials.FirstOrDefault(m => m.Id == id)?.Thickness is { } value && value > 0 ? value : fallback;
    }
}
