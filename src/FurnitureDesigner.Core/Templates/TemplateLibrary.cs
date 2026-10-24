using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Templates;

public static class TemplateLibrary
{
    public static IReadOnlyList<FurnitureTemplate> All { get; } =
    [
        new("base-1door", "Base cabinet, 1 door", "Kitchen", true,
            new() { Width = 450, Height = 820, Depth = 560, Plinth = 100, Shelves = 1, Doors = 1 },
            CabinetBuilder.Cabinet),
        new("base-2door", "Base cabinet, 2 doors", "Kitchen", true,
            new() { Width = 800, Height = 820, Depth = 560, Plinth = 100, Shelves = 1, Doors = 2 },
            CabinetBuilder.Cabinet),
        new("base-drawers", "Drawer cabinet", "Kitchen", true,
            new() { Width = 600, Height = 820, Depth = 560, Plinth = 100, Drawers = 3 },
            CabinetBuilder.Cabinet),
        new("wall-2door", "Wall cabinet, 2 doors", "Kitchen", true,
            new() { Width = 800, Height = 720, Depth = 320, Elevation = 1450, Shelves = 2, Doors = 2 },
            CabinetBuilder.Cabinet),
        new("countertop", "Countertop", "Kitchen", false,
            new() { Width = 2400, Height = 38, Depth = 600, Elevation = 820, CarcassMaterialId = "worktop" },
            p => CabinetBuilder.SingleBoard("Countertop", BoardKind.Countertop, p)),

        new("wardrobe", "Wardrobe, 2 doors", "Bedroom", true,
            new() { Width = 1000, Height = 2200, Depth = 600, Plinth = 80, Shelves = 4, Doors = 2 },
            CabinetBuilder.Cabinet),
        new("bookcase", "Bookcase", "Living room", true,
            new() { Width = 800, Height = 1900, Depth = 300, Plinth = 60, Shelves = 4, CarcassMaterialId = "walnut" },
            CabinetBuilder.Cabinet),

        new("board-horizontal", "Horizontal board", "Boards", false,
            new() { Width = 800, Height = 18, Depth = 300 },
            p => CabinetBuilder.SingleBoard("Board", BoardKind.Board, p)),
        new("board-vertical", "Vertical board", "Boards", false,
            new() { Width = 18, Height = 720, Depth = 560 },
            p => CabinetBuilder.SingleBoard("Board", BoardKind.Board, p)),
        new("board-front", "Front board", "Boards", false,
            new() { Width = 600, Height = 720, Depth = 18, CarcassMaterialId = "oak" },
            p => CabinetBuilder.SingleBoard("Front", BoardKind.Door, p)),
    ];
}
