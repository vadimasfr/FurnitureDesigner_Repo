using System.Globalization;
using System.Text;
using System.Text.Json;
using FurnitureDesigner.Core.Assistant;
using FurnitureDesigner.Core.Editing;
using FurnitureDesigner.Core.Import;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.Core.Tests;

public class DesignToolsTests
{
    private static IReadOnlyDictionary<string, JsonElement> Input(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static string ItemId(ProjectEditor editor, int index = 0) => DesignTools.ShortId(editor.Project.Groups[index].Id);

    [Fact]
    public void Definitions_AreStrictObjectSchemas()
    {
        foreach (var tool in DesignTools.Definitions)
        {
            Assert.Equal("object", (string?)tool.InputSchema["type"]);
            Assert.False((bool)tool.InputSchema["additionalProperties"]!);
            var required = tool.InputSchema["required"]!.AsArray().Select(n => (string?)n).ToHashSet();
            Assert.All(tool.InputSchema["properties"]!.AsObject(), p => Assert.Contains(p.Key, required));
        }
    }

    [Fact]
    public void InsertMoveAndRebuild_InOneTransaction_UndoesAsOneStep()
    {
        var editor = new ProjectEditor();
        var tools = new DesignTools(editor);

        using (editor.BeginTransaction())
        {
            tools.Execute("insert_item", Input(new
            {
                template_key = "base-2door", name = "Sink", x = 100, z = (double?)null, width = 900, height = (double?)null,
                depth = (double?)null, elevation = (double?)null, thickness = (double?)null, plinth = (double?)null,
                shelves = 2, doors = (int?)null, drawers = (int?)null, carcass_material = (string?)null, front_material = "walnut",
            }));
            string id = ItemId(editor);
            tools.Execute("move_items", Input(new { ids = new[] { id }, dx = 50, dy = 0, dz = 0 }));
            tools.Execute("rebuild_item", Input(new
            {
                item_id = id, width = 1000, height = (double?)null, depth = (double?)null, elevation = (double?)null,
                plinth = (double?)null, shelves = (int?)null, doors = (int?)null, drawers = (int?)null,
                carcass_material = (string?)null, front_material = (string?)null,
            }));
        }

        var group = Assert.Single(editor.Project.Groups);
        Assert.Equal("Sink", group.Name);
        var box = Box3.Of(editor.Project.Boards)!.Value;
        Assert.Equal(150, box.MinX, 3);
        Assert.Equal(1000, box.SizeX, 3);
        Assert.Equal(2, editor.Project.Boards.Count(b => b.Kind == BoardKind.Shelf));
        Assert.All(editor.Project.Boards.Where(b => b.Kind == BoardKind.Door), b => Assert.Equal("walnut", b.MaterialId));

        Assert.True(editor.Undo());
        Assert.Empty(editor.Project.Boards);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void TransactionWithoutChanges_LeavesUndoAndRedoAlone()
    {
        var editor = new ProjectEditor();
        var template = Templates.TemplateLibrary.All.Single(t => t.Key == "bookcase");
        editor.Insert(template, template.Defaults, 0, 0);
        editor.Undo();
        Assert.True(editor.CanRedo);

        using (editor.BeginTransaction())
        {
            // e.g. the AI request failed before any tool ran
        }

        Assert.False(editor.CanUndo);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public void FallbackTrigger_SerializesInWireFormat()
    {
        var trigger = Anthropic.Models.Beta.Messages.BetaFallbackRefusalTrigger.FromRawUnchecked(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"type":"refusal","category":"cyber"}""")!);

        var json = JsonSerializer.SerializeToElement(trigger);

        Assert.Equal("cyber", json.GetProperty("category").GetString());
        Assert.Equal("refusal", json.GetProperty("type").GetString());
    }

    [Fact]
    public void SetMaterial_FrontsOnly()
    {
        var editor = new ProjectEditor();
        var tools = new DesignTools(editor);
        tools.Execute("insert_item", Input(new
        {
            template_key = "wardrobe", name = (string?)null, x = (double?)null, z = (double?)null, width = (double?)null,
            height = (double?)null, depth = (double?)null, elevation = (double?)null, thickness = (double?)null,
            plinth = (double?)null, shelves = (int?)null, doors = (int?)null, drawers = (int?)null,
            carcass_material = (string?)null, front_material = (string?)null,
        }));

        tools.Execute("set_material", Input(new { ids = new[] { ItemId(editor) }, material_id = "graphite", part = "fronts" }));

        Assert.All(editor.Project.Boards, b =>
            Assert.Equal(b.Kind == BoardKind.Door ? "graphite" : b.Kind == BoardKind.Back ? "hdf" : "white", b.MaterialId));
        Assert.Equal("graphite", editor.Project.Groups[0].Parameters!.FrontMaterialId);
    }

    [Fact]
    public void FrontStyleAndAlign_Tools()
    {
        var editor = new ProjectEditor();
        var tools = new DesignTools(editor);
        var wall = Templates.TemplateLibrary.All.Single(t => t.Key == "wall-2door");
        var base1 = Templates.TemplateLibrary.All.Single(t => t.Key == "base-1door");
        editor.Insert(wall, wall.Defaults, 0, 200);
        editor.Insert(base1, base1.Defaults, 1000, 0);
        string wallId = ItemId(editor, 0), baseId = ItemId(editor, 1);

        tools.Execute("set_front_style", Input(new { ids = new[] { wallId }, style = "glass" }));
        tools.Execute("align_items", Input(new { ids = new[] { wallId }, mode = "backwall" }));
        tools.Execute("align_items", Input(new { ids = new[] { wallId, baseId }, mode = "left" }));

        var wallBoards = editor.Project.Boards.Where(b => b.GroupId == editor.Project.Groups[0].Id).ToList();
        Assert.All(wallBoards.Where(b => b.Kind == BoardKind.Door), b => Assert.Equal(FrontStyle.Glass, b.FrontStyle));
        Assert.Equal(0, Box3.Of(wallBoards)!.Value.MinZ, 3);
        Assert.Equal(0, Box3.Of(editor.Project.Boards)!.Value.MinX, 3);
        Assert.All(editor.Project.Groups, g => Assert.Equal(0, Box3.Of(editor.Project.Boards.Where(b => b.GroupId == g.Id))!.Value.MinX, 3));
    }

    [Fact]
    public void UnknownIdsAndMaterials_AreReportedAsToolErrors()
    {
        var tools = new DesignTools(new ProjectEditor());
        Assert.Throws<DesignToolException>(() => tools.Execute("delete_items", Input(new { ids = new[] { "deadbeef" } })));
        Assert.Throws<DesignToolException>(() => tools.Execute("no_such_tool", Input(new { })));
    }

    [Fact]
    public void GetProject_ListsItemsWithShortIds()
    {
        var editor = new ProjectEditor();
        var tools = new DesignTools(editor);
        var template = Templates.TemplateLibrary.All.Single(t => t.Key == "bookcase");
        editor.Insert(template, template.Defaults, 0, 0);

        using var doc = JsonDocument.Parse(tools.GetProject(includeBoards: true));
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(ItemId(editor), item.GetProperty("id").GetString());
        Assert.Equal(800, item.GetProperty("bounds").GetProperty("width").GetDouble());
        Assert.Equal(editor.Project.Boards.Count, item.GetProperty("boards").GetArrayLength());
    }
}

public class ImportTests
{
    /// <summary>A DXF like PRO100 writes: Z up, centimetres, each board as six 3DFACEs.</summary>
    private static string Dxf(params (string Layer, double X, double Y, double Z, double Dx, double Dy, double Dz)[] boxes)
    {
        var sb = new StringBuilder("0\nSECTION\n2\nENTITIES\n");
        foreach (var (layer, x, y, z, dx, dy, dz) in boxes)
        {
            double x1 = x + dx, y1 = y + dy, z1 = z + dz;
            double[][][] faces =
            [
                [[x, y, z], [x1, y, z], [x1, y1, z], [x, y1, z]],
                [[x, y, z1], [x1, y, z1], [x1, y1, z1], [x, y1, z1]],
                [[x, y, z], [x1, y, z], [x1, y, z1], [x, y, z1]],
                [[x, y1, z], [x1, y1, z], [x1, y1, z1], [x, y1, z1]],
                [[x, y, z], [x, y1, z], [x, y1, z1], [x, y, z1]],
                [[x1, y, z], [x1, y1, z], [x1, y1, z1], [x1, y, z1]],
            ];
            foreach (var face in faces)
            {
                sb.Append($"0\n3DFACE\n8\n{layer}\n");
                for (int k = 0; k < 4; k++)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{10 + k}\n{face[k][0]}\n{20 + k}\n{face[k][1]}\n{30 + k}\n{face[k][2]}\n");
                }
            }
        }
        return sb.Append("0\nENDSEC\n0\nEOF\n").ToString();
    }

    [Fact]
    public void Dxf_BoxesInCentimetres_BecomeBoardsInMillimetres()
    {
        // Two 1.8 cm sides of a 60 x 56 x 72 cm cabinet. In the plan the back wall is at the top
        // (high DXF Y), so the front panel has the lowest Y.
        var mesh = DxfReader.Read(Dxf(
            ("Korpus", 0, 0, 0, 1.8, 56, 72),
            ("Korpus", 58.2, 0, 0, 1.8, 56, 72),
            ("Front", 0, -1.8, 0, 60, 1.8, 72)));

        var result = BoardReconstructor.Build(mesh, new ImportOptions());

        Assert.Equal(10, result.MillimetresPerUnit);
        Assert.Equal(3, result.Boards.Count);
        Assert.Equal(2, result.Materials.Count);
        var left = result.Boards.OrderBy(b => b.X).ThenBy(b => b.SizeX).First();
        Assert.Equal((18d, 720d, 560d), (left.SizeX, left.SizeY, left.SizeZ));
        var front = result.Boards.Single(b => b.Name.StartsWith("Front"));
        Assert.Equal(560, front.Z);             // in front of the 560 mm deep sides (Z grows towards the viewer)
        Assert.Equal(18, front.SizeZ);
        Assert.Equal(0, left.Z);                // the back of the cabinet is at the wall
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Obj_UsesMaterialsAsLayers()
    {
        var obj = """
            v 0 0 0
            v 18 0 0
            v 18 720 0
            v 0 720 0
            v 0 0 560
            v 18 0 560
            v 18 720 560
            v 0 720 560
            usemtl Oak
            f 1 2 3 4
            f 5 6 7 8
            f 1 2 6 5
            f 4 3 7 8
            f 1 4 8 5
            f 2 3 7 6
            """;

        var result = BoardReconstructor.Build(ObjReader.Read(obj), new ImportOptions());

        var board = Assert.Single(result.Boards);
        Assert.Equal(1, result.MillimetresPerUnit);
        Assert.Equal((18d, 720d, 560d), (board.SizeX, board.SizeY, board.SizeZ));
        Assert.Equal("Oak", Assert.Single(result.Materials).Name);
    }

    [Fact]
    public void FlatDrawing_IsReportedNotImported()
    {
        var obj = "v 0 0 0\nv 1 0 0\nv 1 1 0\nf 1 2 3\n";
        var result = BoardReconstructor.Build(ObjReader.Read(obj), new ImportOptions());
        Assert.Empty(result.Boards);
        Assert.Contains(result.Warnings, w => w.Contains("flat"));
    }

    [Fact]
    public void ProjectFile_GivesAClearMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pro");
        File.WriteAllText(path, "x");
        try
        {
            var ex = Assert.Throws<NotSupportedException>(() => FurnitureImporter.ImportFile(path));
            Assert.Contains("export", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
