using FurnitureDesigner.Core.Editing;
using FurnitureDesigner.Core.Models;
using FurnitureDesigner.Core.Persistence;
using FurnitureDesigner.Core.Reports;
using FurnitureDesigner.Core.Templates;

namespace FurnitureDesigner.Core.Tests;

public class CabinetBuilderTests
{
    [Fact]
    public void TwoDoorCabinet_HasCarcassShelfAndDoors()
    {
        var boards = CabinetBuilder.Cabinet(new TemplateParameters
        {
            Width = 800, Height = 820, Depth = 560, Plinth = 100, Shelves = 1, Doors = 2,
        });

        Assert.Equal(2, boards.Count(b => b.Kind == BoardKind.Side));
        Assert.Single(boards, b => b.Kind == BoardKind.Shelf);
        Assert.Single(boards, b => b.Kind == BoardKind.Plinth);
        var doors = boards.Where(b => b.Kind == BoardKind.Door).ToList();
        Assert.Equal(2, doors.Count);
        Assert.All(doors, d => Assert.Equal(397, d.SizeX, 3)); // (800 - 3 * 2) / 2
        Assert.All(doors, d => Assert.Equal(716, d.SizeY, 3)); // 820 - 100 plinth - 2 * 2 gap

        var bottom = boards.Single(b => b.Kind == BoardKind.Bottom);
        Assert.Equal(764, bottom.SizeX); // between the sides
        Assert.Equal(100, bottom.Y);     // on the plinth
    }

    [Fact]
    public void DrawerCabinet_HasOneFrontPerDrawer()
    {
        var boards = CabinetBuilder.Cabinet(new TemplateParameters { Drawers = 3, Plinth = 100, Height = 820 });
        Assert.Equal(3, boards.Count(b => b.Kind == BoardKind.DrawerFront));
        Assert.DoesNotContain(boards, b => b.Kind == BoardKind.Door);
    }
}

public class ProjectEditorTests
{
    private static ProjectEditor EditorWithCabinet(out FurnitureGroup group)
    {
        var editor = new ProjectEditor();
        var template = TemplateLibrary.All.Single(t => t.Key == "base-2door");
        group = editor.Insert(template, template.Defaults, 0, 0);
        return editor;
    }

    [Fact]
    public void Insert_SelectsAllBoardsOfTheGroup()
    {
        var editor = EditorWithCabinet(out var group);
        Assert.Equal(editor.Project.Boards.Count, editor.Selection.Count);
        Assert.All(editor.Project.Boards, b => Assert.Equal(group.Id, b.GroupId));
    }

    [Fact]
    public void Move_ThenUndo_RestoresPositions()
    {
        var editor = EditorWithCabinet(out _);
        double before = editor.Project.Boards.Sum(b => b.X);

        editor.MoveSelection(100, 0, 0);
        Assert.Equal(before + 100 * editor.Project.Boards.Count, editor.Project.Boards.Sum(b => b.X), 6);

        Assert.True(editor.Undo());
        Assert.Equal(before, editor.Project.Boards.Sum(b => b.X), 6);
        Assert.True(editor.Redo());
        Assert.Equal(before + 100 * editor.Project.Boards.Count, editor.Project.Boards.Sum(b => b.X), 6);
    }

    [Fact]
    public void Rotate_SwapsFootprintAroundCentre()
    {
        var editor = EditorWithCabinet(out _);
        var before = Box3.Of(editor.Project.Boards)!.Value;

        editor.RotateSelection();
        var after = Box3.Of(editor.Project.Boards)!.Value;

        Assert.Equal(before.SizeX, after.SizeZ, 6);
        Assert.Equal(before.SizeZ, after.SizeX, 6);
        Assert.Equal(before.CenterX, after.CenterX, 6);
        Assert.Equal(before.CenterZ, after.CenterZ, 6);
    }

    [Fact]
    public void Duplicate_CreatesNewGroupNextToOriginal()
    {
        var editor = EditorWithCabinet(out _);
        int count = editor.Project.Boards.Count;

        editor.DuplicateSelection();

        Assert.Equal(2 * count, editor.Project.Boards.Count);
        Assert.Equal(2, editor.Project.Groups.Count);
        Assert.True(editor.SelectedBoards.Min(b => b.X) >= 800);
    }

    [Fact]
    public void Delete_RemovesEmptyGroups()
    {
        var editor = EditorWithCabinet(out _);
        editor.DeleteSelection();
        Assert.Empty(editor.Project.Boards);
        Assert.Empty(editor.Project.Groups);
    }
}

public class ReportsAndPersistenceTests
{
    [Fact]
    public void CutList_MergesIdenticalBoards()
    {
        var editor = new ProjectEditor();
        var template = TemplateLibrary.All.Single(t => t.Key == "base-2door");
        editor.Insert(template, template.Defaults, 0, 0);

        var rows = ProjectReports.CutList(editor.Project);

        var sides = rows.Single(r => r.Parts.Contains("Left side"));
        Assert.Equal(2, sides.Quantity);
        Assert.Equal((820d, 560d, 18d), (sides.Length, sides.Width, sides.Thickness));
        Assert.Equal(editor.Project.Boards.Count, rows.Sum(r => r.Quantity));
    }

    [Fact]
    public void Serializer_RoundTrips()
    {
        var editor = new ProjectEditor();
        var template = TemplateLibrary.All.Single(t => t.Key == "wardrobe");
        editor.Insert(template, template.Defaults, 100, 0);

        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(editor.Project));

        Assert.Equal(editor.Project.Boards.Count, copy.Boards.Count);
        Assert.Equal(editor.Project.Boards[0].Kind, copy.Boards[0].Kind);
        Assert.Equal(editor.Project.Materials.Count, copy.Materials.Count);
    }
}
