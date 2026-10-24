using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FurnitureDesigner.Core.Editing;
using FurnitureDesigner.Core.Models;
using FurnitureDesigner.Core.Templates;

namespace FurnitureDesigner.Core.Assistant;

/// <summary>A tool the assistant may call: name, description and a strict JSON schema for its input.</summary>
public sealed record DesignToolDefinition(string Name, string Description, JsonObject InputSchema);

/// <summary>Thrown for invalid tool input; the message is returned to the model as an error result.</summary>
public sealed class DesignToolException(string message) : Exception(message);

/// <summary>
/// The operations an AI assistant can perform on the open project, independent of any AI SDK.
/// Items and boards are addressed by short ids (the first 8 hex digits of their Guid).
/// </summary>
public sealed class DesignTools(ProjectEditor editor)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    // Declared before Definitions: static initializers run in textual order.
    private static readonly string[] FrontStyleNames = System.Enum.GetNames<FrontStyle>().Select(n => n.ToLowerInvariant()).ToArray();

    public static IReadOnlyList<DesignToolDefinition> Definitions { get; } =
    [
        new("get_project",
            "Returns the room, materials and all furniture items with their bounding boxes (mm). " +
            "Set include_boards to also list every board of every item.",
            Schema(("include_boards", Bool("Also list the individual boards.")))),

        new("insert_item",
            "Inserts a new item from the template library. Position is the back-left-bottom corner; " +
            "x null places it right of the existing furniture at the same height, z null puts it against the back wall. " +
            "Size and construction fields left null use the template defaults.",
            Schema(
                ("template_key", Enum("Template to insert.", TemplateLibrary.All.Select(t => t.Key))),
                ("name", Str("Item name, or null for an automatic name.", nullable: true)),
                ("x", Num("Left edge, mm.", nullable: true)),
                ("z", Num("Back edge, mm (0 = back wall).", nullable: true)),
                ("width", Num("Width (X), mm.", nullable: true)),
                ("height", Num("Height (Y), mm.", nullable: true)),
                ("depth", Num("Depth (Z), mm.", nullable: true)),
                ("elevation", Num("Height of the bottom above the floor, mm.", nullable: true)),
                ("thickness", Num("Carcass board thickness, mm.", nullable: true)),
                ("plinth", Num("Plinth height, mm.", nullable: true)),
                ("shelves", Int("Number of shelves.", nullable: true)),
                ("doors", Int("Number of doors.", nullable: true)),
                ("drawers", Int("Number of drawers (replace the doors).", nullable: true)),
                ("carcass_material", Str("Material id for the carcass (or the board for single boards).", nullable: true)),
                ("front_material", Str("Material id for doors and drawer fronts.", nullable: true)),
                ("front_style", NullableEnum("Door/drawer front style.", FrontStyleNames)))),

        new("rebuild_item",
            "Changes the size or construction of an item that was made from a template and rebuilds it in place " +
            "(back-left corner kept). Fields left null keep their current value.",
            Schema(
                ("item_id", Str("Item id.")),
                ("width", Num("Width (X), mm.", nullable: true)),
                ("height", Num("Height (Y), mm.", nullable: true)),
                ("depth", Num("Depth (Z), mm.", nullable: true)),
                ("elevation", Num("Height of the bottom above the floor, mm.", nullable: true)),
                ("plinth", Num("Plinth height, mm.", nullable: true)),
                ("shelves", Int("Number of shelves.", nullable: true)),
                ("doors", Int("Number of doors.", nullable: true)),
                ("drawers", Int("Number of drawers.", nullable: true)),
                ("carcass_material", Str("Material id for the carcass.", nullable: true)),
                ("front_material", Str("Material id for fronts.", nullable: true)),
                ("front_style", NullableEnum("Door/drawer front style.", FrontStyleNames)))),

        new("set_front_style",
            "Sets the look of the doors and drawer fronts of items (flat, shaker frame, raised panel, glass insert).",
            Schema(("ids", Ids()), ("style", Enum("Front style.", FrontStyleNames)))),

        new("align_items",
            "Aligns the given items to each other (left/right/bottom/top/back/front edges of their common bounding box), " +
            "or pushes each one against the back wall or down onto the floor.",
            Schema(("ids", Ids()), ("mode", Enum("Alignment.", System.Enum.GetNames<AlignMode>().Select(n => n.ToLowerInvariant()))))),

        new("move_items",
            "Moves items and/or boards by an offset in mm. X = right, Y = up, Z = towards the room front.",
            Schema(("ids", Ids()), ("dx", Num("Offset X, mm.")), ("dy", Num("Offset Y, mm.")), ("dz", Num("Offset Z, mm.")))),

        new("place_items",
            "Moves items and/or boards together so that the minimum corner of their bounding box is at the given position. " +
            "Null keeps that coordinate.",
            Schema(("ids", Ids()), ("x", Num("Left edge, mm.", nullable: true)), ("y", Num("Bottom, mm.", nullable: true)), ("z", Num("Back edge, mm.", nullable: true)))),

        new("rotate_items",
            "Rotates items and/or boards around the centre of their footprint by 90° steps (clockwise seen from above).",
            Schema(("ids", Ids()), ("quarter_turns", Int("1, 2 or 3 quarter turns.")))),

        new("duplicate_items",
            "Copies items and/or boards. The copy is moved by the offset (mm) and its new ids are returned.",
            Schema(("ids", Ids()), ("dx", Num("Offset X, mm.")), ("dy", Num("Offset Y, mm.")), ("dz", Num("Offset Z, mm.")))),

        new("delete_items", "Deletes items and/or boards.", Schema(("ids", Ids()))),

        new("set_material",
            "Sets the material of items and/or boards. part limits it to the carcass or the fronts (doors, drawer fronts).",
            Schema(("ids", Ids()), ("material_id", Str("Material id.")), ("part", Enum("Which boards to change.", ["all", "carcass", "fronts"])))),

        new("update_board",
            "Changes one board: name, material, position or size (mm). Null keeps the value.",
            Schema(
                ("board_id", Str("Board id.")),
                ("name", Str("Board name.", nullable: true)),
                ("material_id", Str("Material id.", nullable: true)),
                ("x", Num("X, mm.", nullable: true)),
                ("y", Num("Y, mm.", nullable: true)),
                ("z", Num("Z, mm.", nullable: true)),
                ("width", Num("Size X, mm.", nullable: true)),
                ("height", Num("Size Y, mm.", nullable: true)),
                ("depth", Num("Size Z, mm.", nullable: true)))),

        new("rename_item", "Renames an item.", Schema(("item_id", Str("Item id.")), ("name", Str("New name.")))),

        new("set_room", "Changes the room size in mm. Null keeps the value.",
            Schema(("width", Num("Width, mm.", nullable: true)), ("depth", Num("Depth, mm.", nullable: true)), ("height", Num("Height, mm.", nullable: true)))),

        new("add_material", "Adds a new material, or updates it if the id exists.",
            Schema(
                ("id", Str("Short lowercase id, e.g. 'birch'.")),
                ("name", Str("Display name.")),
                ("color", Str("Colour as #RRGGBB.")),
                ("thickness", Num("Board thickness, mm.")),
                ("price_per_m2", Num("Price per square metre.")),
                ("opacity", Num("1 = opaque, below 1 = transparent like glass.")))),
    ];

    /// <summary>Boards touched by the last call, so the UI can select them.</summary>
    public IReadOnlyList<Guid> LastAffected { get; private set; } = [];

    private FurnitureProject Project => editor.Project;

    public string Execute(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        LastAffected = [];
        var args = new Args(input);
        return name switch
        {
            "get_project" => GetProject(args.Bool("include_boards")),
            "insert_item" => InsertItem(args),
            "rebuild_item" => RebuildItem(args),
            "move_items" => MoveItems(args),
            "place_items" => PlaceItems(args),
            "rotate_items" => RotateItems(args),
            "duplicate_items" => DuplicateItems(args),
            "delete_items" => DeleteItems(args),
            "set_material" => SetMaterial(args),
            "update_board" => UpdateBoard(args),
            "rename_item" => RenameItem(args),
            "set_front_style" => SetFrontStyle(args),
            "align_items" => AlignItems(args),
            "set_room" => SetRoom(args),
            "add_material" => AddMaterial(args),
            _ => throw new DesignToolException($"Unknown tool '{name}'."),
        };
    }

    /// <summary>Compact JSON description of the project, also sent with every user request.</summary>
    public string GetProject(bool includeBoards)
    {
        var project = Project;
        var root = new JsonObject
        {
            ["room"] = new JsonObject { ["width"] = project.Room.Width, ["depth"] = project.Room.Depth, ["height"] = project.Room.Height },
            ["materials"] = new JsonArray(project.Materials.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id, ["name"] = m.Name, ["color"] = m.Color, ["thickness"] = m.Thickness,
            }).ToArray()),
        };

        var items = new JsonArray();
        foreach (var group in project.Groups)
        {
            var boards = project.Boards.Where(b => b.GroupId == group.Id).ToList();
            var item = new JsonObject
            {
                ["id"] = ShortId(group.Id),
                ["name"] = group.Name,
                ["template"] = group.TemplateKey,
                ["bounds"] = BoundsJson(Box3.Of(boards)),
            };
            if (group.Parameters is { } p)
            {
                item["parameters"] = new JsonObject
                {
                    ["shelves"] = p.Shelves, ["doors"] = p.Doors, ["drawers"] = p.Drawers, ["plinth"] = p.Plinth,
                    ["carcass_material"] = p.CarcassMaterialId, ["front_material"] = p.FrontMaterialId,
                };
            }
            if (includeBoards)
                item["boards"] = new JsonArray(boards.Select(b => (JsonNode)BoardJson(b)).ToArray());
            else
                item["board_count"] = boards.Count;
            items.Add(item);
        }
        root["items"] = items;

        var loose = project.Boards.Where(b => project.FindGroup(b.GroupId) is null).ToList();
        if (loose.Count > 0)
            root["loose_boards"] = new JsonArray(loose.Select(b => (JsonNode)BoardJson(b)).ToArray());

        if (editor.Selection.Count > 0)
            root["selected_ids"] = new JsonArray(SelectionIds().Select(id => (JsonNode)id!).ToArray());

        return root.ToJsonString(JsonOptions);
    }

    // ---- operations ----------------------------------------------------------------------

    private string InsertItem(Args a)
    {
        string key = a.String("template_key");
        var template = TemplateLibrary.All.FirstOrDefault(t => t.Key == key)
            ?? throw new DesignToolException($"Unknown template '{key}'.");
        var d = template.Defaults;
        var p = d with
        {
            Width = Positive(a.Number("width") ?? d.Width, "width"),
            Height = Positive(a.Number("height") ?? d.Height, "height"),
            Depth = Positive(a.Number("depth") ?? d.Depth, "depth"),
            Elevation = a.Number("elevation") ?? d.Elevation,
            Thickness = Positive(a.Number("thickness") ?? d.Thickness, "thickness"),
            Plinth = Math.Max(0, a.Number("plinth") ?? d.Plinth),
            Shelves = Math.Max(0, a.Int("shelves") ?? d.Shelves),
            Doors = Math.Max(0, a.Int("doors") ?? d.Doors),
            Drawers = Math.Max(0, a.Int("drawers") ?? d.Drawers),
            CarcassMaterialId = Material(a.OptionalString("carcass_material")) ?? d.CarcassMaterialId,
            FrontMaterialId = Material(a.OptionalString("front_material")) ?? d.FrontMaterialId,
            FrontStyle = ParseFrontStyle(a.OptionalString("front_style")) ?? d.FrontStyle,
        };
        ValidateCabinet(template, p);

        double x = a.Number("x") ?? editor.NextFreeX(p.Elevation, p.Height, p.Width);
        double z = a.Number("z") ?? 0;
        var group = editor.Insert(template, p, x, z);
        if (a.OptionalString("name") is { Length: > 0 } name)
            editor.Execute(_ => group.Name = name);

        LastAffected = editor.Selection.ToList();
        return $"Inserted '{group.Name}' id={ShortId(group.Id)} bounds={BoundsJson(Box3.Of(editor.SelectedBoards))!.ToJsonString()}";
    }

    private string RebuildItem(Args a)
    {
        var group = FindGroup(a.String("item_id"));
        var current = group.Parameters
            ?? throw new DesignToolException($"'{group.Name}' was not made from a template; change its boards instead.");
        var template = TemplateLibrary.All.First(t => t.Key == group.TemplateKey);
        var p = current with
        {
            Width = Positive(a.Number("width") ?? current.Width, "width"),
            Height = Positive(a.Number("height") ?? current.Height, "height"),
            Depth = Positive(a.Number("depth") ?? current.Depth, "depth"),
            Elevation = a.Number("elevation") ?? current.Elevation,
            Plinth = Math.Max(0, a.Number("plinth") ?? current.Plinth),
            Shelves = Math.Max(0, a.Int("shelves") ?? current.Shelves),
            Doors = Math.Max(0, a.Int("doors") ?? current.Doors),
            Drawers = Math.Max(0, a.Int("drawers") ?? current.Drawers),
            CarcassMaterialId = Material(a.OptionalString("carcass_material")) ?? current.CarcassMaterialId,
            FrontMaterialId = Material(a.OptionalString("front_material")) ?? current.FrontMaterialId,
            FrontStyle = ParseFrontStyle(a.OptionalString("front_style")) ?? current.FrontStyle,
        };
        ValidateCabinet(template, p);
        editor.RebuildGroup(group.Id, p);
        LastAffected = editor.Selection.ToList();
        return $"Rebuilt '{group.Name}' bounds={BoundsJson(Box3.Of(editor.SelectedBoards))!.ToJsonString()}";
    }

    private string MoveItems(Args a)
    {
        SelectTargets(a);
        editor.MoveSelection(a.Number("dx") ?? 0, a.Number("dy") ?? 0, a.Number("dz") ?? 0);
        return Moved();
    }

    private string PlaceItems(Args a)
    {
        SelectTargets(a);
        var box = Box3.Of(editor.SelectedBoards)!.Value;
        double dx = a.Number("x") is { } x ? x - box.MinX : 0;
        double dy = a.Number("y") is { } y ? y - box.MinY : 0;
        double dz = a.Number("z") is { } z ? z - box.MinZ : 0;
        editor.MoveSelection(dx, dy, dz);
        return Moved();
    }

    private string RotateItems(Args a)
    {
        SelectTargets(a);
        int turns = ((a.Int("quarter_turns") ?? 1) % 4 + 4) % 4;
        for (int i = 0; i < turns; i++)
            editor.RotateSelection();
        return Moved();
    }

    private string DuplicateItems(Args a)
    {
        SelectTargets(a);
        var box = Box3.Of(editor.SelectedBoards)!.Value;
        editor.DuplicateSelection(); // copies are placed right of the originals and selected
        editor.MoveSelection((a.Number("dx") ?? 0) - box.SizeX, a.Number("dy") ?? 0, a.Number("dz") ?? 0);
        LastAffected = editor.Selection.ToList();
        return $"Copied. New ids: {string.Join(", ", SelectionIds())}; bounds={BoundsJson(Box3.Of(editor.SelectedBoards))!.ToJsonString()}";
    }

    private string DeleteItems(Args a)
    {
        int count = SelectTargets(a);
        editor.DeleteSelection();
        return $"Deleted {count} boards.";
    }

    private string SetMaterial(Args a)
    {
        var material = Material(a.String("material_id"))!;
        var part = a.String("part");
        SelectTargets(a);
        var boards = editor.SelectedBoards.Where(b => part switch
        {
            "fronts" => IsFront(b),
            "carcass" => !IsFront(b),
            _ => true,
        }).Select(b => b.Id).ToHashSet();
        editor.Execute(p =>
        {
            foreach (var board in p.Boards.Where(b => boards.Contains(b.Id)))
                board.MaterialId = material;
            foreach (var group in p.Groups.Where(g => g.Parameters is not null && p.Boards.Any(b => b.GroupId == g.Id && boards.Contains(b.Id))))
            {
                group.Parameters = part switch
                {
                    "fronts" => group.Parameters! with { FrontMaterialId = material },
                    "carcass" => group.Parameters! with { CarcassMaterialId = material },
                    _ => group.Parameters! with { CarcassMaterialId = material, FrontMaterialId = material },
                };
            }
        });
        LastAffected = boards.ToList();
        return $"Material '{material}' set on {boards.Count} boards.";
    }

    private string UpdateBoard(Args a)
    {
        var board = FindBoard(a.String("board_id"));
        var material = Material(a.OptionalString("material_id"));
        double? width = a.Number("width"), height = a.Number("height"), depth = a.Number("depth");
        foreach (var (value, field) in new[] { (width, "width"), (height, "height"), (depth, "depth") })
        {
            if (value is not null)
                Positive(value.Value, field);
        }

        editor.Execute(_ =>
        {
            if (a.OptionalString("name") is { Length: > 0 } name) board.Name = name;
            if (material is not null) board.MaterialId = material;
            board.X = a.Number("x") ?? board.X;
            board.Y = a.Number("y") ?? board.Y;
            board.Z = a.Number("z") ?? board.Z;
            board.SizeX = width ?? board.SizeX;
            board.SizeY = height ?? board.SizeY;
            board.SizeZ = depth ?? board.SizeZ;
        });
        LastAffected = [board.Id];
        return $"Updated {BoardJson(board).ToJsonString()}";
    }

    private string SetFrontStyle(Args a)
    {
        var style = ParseFrontStyle(a.String("style"))!.Value;
        SelectTargets(a);
        var fronts = editor.SelectedBoards.Where(IsFront).Select(b => b.Id).ToHashSet();
        if (fronts.Count == 0)
            throw new DesignToolException("The selection has no doors or drawer fronts.");
        editor.Execute(p =>
        {
            foreach (var board in p.Boards.Where(b => fronts.Contains(b.Id)))
                board.FrontStyle = style;
            foreach (var group in p.Groups.Where(g => g.Parameters is not null && p.Boards.Any(b => b.GroupId == g.Id && fronts.Contains(b.Id))))
                group.Parameters = group.Parameters! with { FrontStyle = style };
        });
        LastAffected = fronts.ToList();
        return $"Front style {style} set on {fronts.Count} fronts.";
    }

    private string AlignItems(Args a)
    {
        var mode = System.Enum.Parse<AlignMode>(a.String("mode"), ignoreCase: true);
        SelectTargets(a);
        editor.AlignSelection(mode);
        return Moved();
    }

    private static FrontStyle? ParseFrontStyle(string? value) =>
        value is null ? null
        : System.Enum.TryParse<FrontStyle>(value, ignoreCase: true, out var style) ? style
        : throw new DesignToolException($"Unknown front style '{value}'.");

    private string RenameItem(Args a)
    {
        var group = FindGroup(a.String("item_id"));
        var name = a.String("name");
        editor.Execute(_ => group.Name = name);
        return $"Renamed to '{name}'.";
    }

    private string SetRoom(Args a)
    {
        var room = Project.Room;
        double width = Positive(a.Number("width") ?? room.Width, "width");
        double depth = Positive(a.Number("depth") ?? room.Depth, "depth");
        double height = Positive(a.Number("height") ?? room.Height, "height");
        editor.Execute(p =>
        {
            p.Room.Width = width;
            p.Room.Depth = depth;
            p.Room.Height = height;
        });
        return $"Room is now {width} × {depth} × {height} mm (W × D × H).";
    }

    private string AddMaterial(Args a)
    {
        string id = a.String("id").Trim().ToLowerInvariant();
        string color = a.String("color");
        if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            throw new DesignToolException("color must be #RRGGBB.");
        var material = new BoardMaterial
        {
            Id = id,
            Name = a.String("name"),
            Color = color,
            Thickness = Positive(a.Number("thickness") ?? 18, "thickness"),
            PricePerM2 = Math.Max(0, a.Number("price_per_m2") ?? 0),
            Opacity = Math.Clamp(a.Number("opacity") ?? 1, 0.05, 1),
        };
        editor.Execute(p =>
        {
            p.Materials.RemoveAll(m => m.Id == id);
            p.Materials.Add(material);
        });
        return $"Material '{id}' saved.";
    }

    // ---- helpers -------------------------------------------------------------------------

    public static string ShortId(Guid id) => id.ToString("N")[..8];

    private int SelectTargets(Args a)
    {
        var ids = a.StringArray("ids");
        if (ids.Count == 0)
            throw new DesignToolException("ids is empty.");
        var boards = new HashSet<Guid>();
        foreach (var id in ids)
            boards.UnionWith(ResolveBoards(id));
        editor.Select(boards);
        LastAffected = boards.ToList();
        return boards.Count;
    }

    private IEnumerable<Guid> ResolveBoards(string shortId)
    {
        var groups = Project.Groups.Where(g => Matches(g.Id, shortId)).ToList();
        var boards = Project.Boards.Where(b => Matches(b.Id, shortId)).ToList();
        return (groups.Count + boards.Count) switch
        {
            0 => throw new DesignToolException($"No item or board with id '{shortId}'. Call get_project for current ids."),
            1 when groups.Count == 1 => Project.Boards.Where(b => b.GroupId == groups[0].Id).Select(b => b.Id),
            1 => [boards[0].Id],
            _ => throw new DesignToolException($"Id '{shortId}' is ambiguous."),
        };
    }

    private FurnitureGroup FindGroup(string shortId) =>
        Project.Groups.SingleOrDefault(g => Matches(g.Id, shortId))
        ?? throw new DesignToolException($"No item with id '{shortId}'. Call get_project for current ids.");

    private Board FindBoard(string shortId) =>
        Project.Boards.SingleOrDefault(b => Matches(b.Id, shortId))
        ?? throw new DesignToolException($"No board with id '{shortId}'. Call get_project with include_boards.");

    private static bool Matches(Guid id, string shortId) =>
        shortId.Length >= 4 && id.ToString("N").StartsWith(shortId.Trim().ToLowerInvariant(), StringComparison.Ordinal);

    private IEnumerable<string> SelectionIds()
    {
        var selected = editor.Selection;
        foreach (var group in Project.Groups)
        {
            var members = Project.Boards.Where(b => b.GroupId == group.Id).Select(b => b.Id).ToList();
            if (members.Count > 0 && members.All(selected.Contains))
                yield return ShortId(group.Id);
        }
        foreach (var board in editor.SelectedBoards)
        {
            var group = Project.FindGroup(board.GroupId);
            if (group is null || !Project.Boards.Where(b => b.GroupId == group.Id).All(b => selected.Contains(b.Id)))
                yield return ShortId(board.Id);
        }
    }

    private string Moved()
    {
        LastAffected = editor.Selection.ToList();
        return $"Done. Bounds now {BoundsJson(Box3.Of(editor.SelectedBoards))!.ToJsonString()}";
    }

    private string? Material(string? id)
    {
        if (id is null)
            return null;
        return Project.FindMaterial(id)?.Id
            ?? throw new DesignToolException($"Unknown material '{id}'. Known: {string.Join(", ", Project.Materials.Select(m => m.Id))}. Use add_material first.");
    }

    private static bool IsFront(Board b) => b.Kind is BoardKind.Door or BoardKind.DrawerFront;

    private static double Positive(double value, string field) =>
        value > 0 ? value : throw new DesignToolException($"{field} must be greater than zero.");

    private static void ValidateCabinet(FurnitureTemplate template, TemplateParameters p)
    {
        if (template.IsCabinet && (2 * p.Thickness >= p.Width || p.Plinth + 2 * p.Thickness >= p.Height))
            throw new DesignToolException("The cabinet is too small for its board thickness and plinth.");
    }

    private static JsonObject? BoundsJson(Box3? box) => box is { } b
        ? new JsonObject
        {
            ["x"] = Round(b.MinX), ["y"] = Round(b.MinY), ["z"] = Round(b.MinZ),
            ["width"] = Round(b.SizeX), ["height"] = Round(b.SizeY), ["depth"] = Round(b.SizeZ),
        }
        : null;

    private static JsonObject BoardJson(Board b) => new()
    {
        ["id"] = ShortId(b.Id), ["name"] = b.Name, ["kind"] = b.Kind.ToString(), ["material"] = b.MaterialId,
        ["x"] = Round(b.X), ["y"] = Round(b.Y), ["z"] = Round(b.Z),
        ["width"] = Round(b.SizeX), ["height"] = Round(b.SizeY), ["depth"] = Round(b.SizeZ),
    };

    private static double Round(double v) => Math.Round(v, 1);

    // ---- schema builders (strict: every property required, nullable via a type array) ----

    private static JsonObject Schema(params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
            props[name] = schema;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode)p.Name).ToArray()),
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject Typed(string type, string description, bool nullable) => new()
    {
        ["type"] = nullable ? new JsonArray(type, "null") : type,
        ["description"] = description,
    };

    private static JsonObject Num(string description, bool nullable = false) => Typed("number", description, nullable);
    private static JsonObject Int(string description, bool nullable = false) => Typed("integer", description, nullable);
    private static JsonObject Str(string description, bool nullable = false) => Typed("string", description, nullable);
    private static JsonObject Bool(string description) => Typed("boolean", description, false);

    private static JsonObject Enum(string description, IEnumerable<string> values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()),
        ["description"] = description,
    };

    private static JsonObject NullableEnum(string description, IEnumerable<string> values) => new()
    {
        ["type"] = new JsonArray("string", "null"),
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).Append(null).ToArray()),
        ["description"] = description,
    };

    private static JsonObject Ids() => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = "Item ids and/or board ids from get_project.",
    };

    /// <summary>Typed access to the model's tool input; missing or null values read as null.</summary>
    private sealed class Args(IReadOnlyDictionary<string, JsonElement> input)
    {
        private JsonElement? Get(string name) =>
            input.TryGetValue(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

        public double? Number(string name) => Get(name) is { } v
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
              : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
              : throw new DesignToolException($"{name} must be a number.")
            : null;

        public int? Int(string name) => Number(name) is { } d ? (int)Math.Round(d) : null;
        public bool Bool(string name) => Get(name) is { ValueKind: JsonValueKind.True };
        public string? OptionalString(string name) => Get(name)?.GetString();
        public string String(string name) => OptionalString(name) ?? throw new DesignToolException($"{name} is required.");

        public IReadOnlyList<string> StringArray(string name) => Get(name) is { ValueKind: JsonValueKind.Array } v
            ? v.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];
    }
}
