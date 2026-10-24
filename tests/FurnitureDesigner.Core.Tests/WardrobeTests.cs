using FurnitureDesigner.Core.Editing;
using FurnitureDesigner.Core.Models;
using FurnitureDesigner.Core.Parametric;
using FurnitureDesigner.Core.Persistence;
using FurnitureDesigner.Core.Reports;

namespace FurnitureDesigner.Core.Tests;

public class WardrobeTests
{
    private static readonly List<BoardMaterial> Materials = FurnitureProject.CreateDefault().Materials;

    private static WardrobeDesign TwoSections() => new()
    {
        Width = 2000, Height = 2500, Depth = 600, Clearance = 5, Plinth = 80, TopStorage = 450,
        Sections =
        [
            new() { Zones = [new() { Kind = ZoneKind.HangingRail }] },
            new() { Width = 600, Zones = [new() { Kind = ZoneKind.Drawers, Count = 3, Height = 600 }, new() { Kind = ZoneKind.Shelves, Count = 4 }] },
        ],
    };

    private static Board Part(WardrobeResult result, string name) => Assert.Single(result.Boards, b => b.Name == name);

    [Fact]
    public void Carcass_FitsTheNicheWithClearance()
    {
        var result = WardrobeBuilder.Build(TwoSections(), Materials);

        Assert.Empty(result.Warnings);
        var box = Box3.Of(result.Boards.Where(b => b.Kind != BoardKind.Door && b.Kind != BoardKind.DrawerFront))!.Value;
        Assert.Equal(5, box.MinX, 3);
        Assert.Equal(1995, box.MaxX, 3);
        Assert.Equal(2495, box.MaxY, 3); // ceiling clearance
        Assert.Equal(0, box.MinY, 3);
        var fronts = Box3.Of(result.Boards.Where(b => b.Kind is BoardKind.Door or BoardKind.DrawerFront))!.Value;
        Assert.Equal(600, fronts.MaxZ, 3); // fronts flush with the niche
    }

    [Fact]
    public void FixedSectionWidth_IsKept_AndTheRestIsShared()
    {
        var result = WardrobeBuilder.Build(TwoSections(), Materials);

        var divider = Part(result, "Divider 1");
        var right = Part(result, "Right side");
        Assert.Equal(600, right.X - (divider.X + divider.SizeX), 3);
        var left = Part(result, "Left side");
        Assert.Equal(2000 - 2 * 5 - 3 * 18 - 600, divider.X - (left.X + left.SizeX), 3);
    }

    [Fact]
    public void ThicknessComesFromTheMaterial()
    {
        var materials = FurnitureProject.CreateDefault().Materials;
        materials.Single(m => m.Id == "white").Thickness = 16;

        var result = WardrobeBuilder.Build(TwoSections(), materials);

        Assert.Equal(16, Part(result, "Left side").SizeX);
        Assert.Equal(16, Part(result, "Bottom").SizeY);
    }

    [Fact]
    public void Fronts_CoverTheFullWidthWithEvenGaps()
    {
        var result = WardrobeBuilder.Build(TwoSections(), Materials);

        // Section 1 (1328 mm front) gets two doors, section 2 (600 mm) one: three doors side by side.
        var doors = result.Boards.Where(b => b.Kind == BoardKind.Door && b.Name.StartsWith("Section")).OrderBy(b => b.X).ToList();
        Assert.Equal(["Section 1 door L", "Section 1 door R", "Section 2 door"], doors.Select(d => d.Name));
        Assert.Equal(5 + WardrobeBuilder.FrontGap / 2, doors[0].X, 3);
        Assert.Equal(1995 - WardrobeBuilder.FrontGap / 2, doors[^1].X + doors[^1].SizeX, 3);
        for (int i = 1; i < doors.Count; i++)
            Assert.Equal(WardrobeBuilder.FrontGap, doors[i].X - (doors[i - 1].X + doors[i - 1].SizeX), 3);
    }

    [Fact]
    public void Drawers_GetFronts_AndDoorsCoverTheZoneAbove()
    {
        var result = WardrobeBuilder.Build(TwoSections(), Materials);

        var drawerFronts = result.Boards.Where(b => b.Kind == BoardKind.DrawerFront).OrderBy(b => b.Y).ToList();
        Assert.Equal(3, drawerFronts.Count);
        var door = Part(result, "Section 2 door");
        Assert.True(door.Y > drawerFronts[^1].Y + drawerFronts[^1].SizeY);
        Assert.Equal(4, result.Boards.Count(b => b.Name.StartsWith("Section 2 shelf")));
        Assert.Single(result.Boards, b => b.Name == "Section 2 fixed shelf 1");
    }

    [Fact]
    public void Rails_AreDrawnButNotCut()
    {
        var project = FurnitureProject.CreateDefault();
        var editor = new ProjectEditor(project);
        editor.InsertWardrobe(TwoSections(), 0, 0);

        Assert.Contains(project.Boards, b => b.Kind == BoardKind.Rail);
        Assert.DoesNotContain(ProjectReports.CutList(project), r => r.Parts.Contains("rail"));
    }

    [Fact]
    public void ImpossibleSizes_AreScaledWithAWarning()
    {
        var design = TwoSections();
        design.Sections[1].Width = 5000;

        var result = WardrobeBuilder.Build(design, Materials);

        Assert.NotEmpty(result.Warnings);
        Assert.All(result.Boards, b => Assert.True(b.SizeX > 0 && b.SizeY > 0 && b.SizeZ > 0));
        Assert.True(Box3.Of(result.Boards)!.Value.MaxX <= 2000);
    }

    [Fact]
    public void EditWardrobe_RegeneratesWhereTheWardrobeWasMoved_AsOneUndoStep()
    {
        var editor = new ProjectEditor();
        var group = editor.InsertWardrobe(TwoSections(), 0, 0);
        editor.MoveSelection(300, 0, 100);
        var leftBefore = editor.Project.Boards.Single(b => b.Name == "Left side").X;

        editor.EditWardrobe(group.Id, d => d.Width = 2400);

        var boards = editor.Project.Boards.Where(b => b.GroupId == group.Id).ToList();
        Assert.Equal(leftBefore, boards.Single(b => b.Name == "Left side").X, 3);
        Assert.Equal(300 + 2400 - 5, boards.Single(b => b.Name == "Right side").X + 18, 3);
        Assert.Equal(2400, editor.Project.FindGroup(group.Id)!.Wardrobe!.Width);

        editor.Undo();
        Assert.Equal(2000, editor.Project.FindGroup(group.Id)!.Wardrobe!.Width);
        Assert.Equal(300 + 2000 - 5, editor.Project.Boards.Single(b => b.Name == "Right side").X + 18, 3);
    }

    [Fact]
    public void Design_IsSavedWithTheProject()
    {
        var editor = new ProjectEditor();
        var group = editor.InsertWardrobe(TwoSections(), 0, 0);

        var loaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(editor.Project));

        var design = loaded.FindGroup(group.Id)!.Wardrobe!;
        Assert.Equal(2, design.Sections.Count);
        Assert.Equal(ZoneKind.Drawers, design.Sections[1].Zones[0].Kind);
        Assert.Null(design.Sections[0].Width);
    }

    [Fact]
    public void ConvertToBoards_KeepsTheBoardsButDropsTheDesign()
    {
        var editor = new ProjectEditor();
        var group = editor.InsertWardrobe(TwoSections(), 0, 0);
        int count = editor.Project.Boards.Count;

        editor.ConvertToBoards(group.Id);

        Assert.Null(editor.Project.FindGroup(group.Id)!.Wardrobe);
        Assert.Equal(count, editor.Project.Boards.Count);
    }

    [Fact]
    public void PartNames_AreUnique_EvenWithRepeatedZones()
    {
        var design = TwoSections();
        design.Sections[1].Zones =
        [
            new() { Kind = ZoneKind.Drawers, Count = 2, Height = 400 },
            new() { Kind = ZoneKind.HangingRail },
            new() { Kind = ZoneKind.Drawers, Count = 2, Height = 400 },
            new() { Kind = ZoneKind.HangingRail },
        ];

        var names = WardrobeBuilder.Build(design, Materials).Boards.Select(b => b.Name).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Selection_SurvivesRebuildAndUndo()
    {
        var editor = new ProjectEditor();
        var group = editor.InsertWardrobe(TwoSections(), 0, 0);
        int selected = editor.Selection.Count;

        editor.EditWardrobe(group.Id, d => d.Depth = 550);
        Assert.Equal(selected, editor.Selection.Count);

        editor.Undo();
        Assert.Equal(selected, editor.Selection.Count);
    }

    [Fact]
    public void DuplicatingAWholeWardrobe_KeepsItParametric()
    {
        var editor = new ProjectEditor();
        editor.InsertWardrobe(TwoSections(), 0, 0);

        editor.DuplicateSelection();

        var copies = editor.Project.Groups.Where(g => g.Wardrobe is not null).ToList();
        Assert.Equal(2, copies.Count);
        editor.EditWardrobe(copies[1].Id, d => d.Sections.RemoveAt(1));
        Assert.Single(editor.Project.FindGroup(copies[1].Id)!.Wardrobe!.Sections);
        Assert.Equal(2, editor.Project.FindGroup(copies[0].Id)!.Wardrobe!.Sections.Count);
    }

    [Theory]
    [InlineData(900, 1)]
    [InlineData(2818, 3)]
    [InlineData(3600, 4)]
    public void ForNiche_PicksSectionsByWidth(double width, int sections)
    {
        var design = WardrobeDesign.ForNiche(width, 2500, 600);

        Assert.Equal(sections, design.Sections.Count);
        Assert.Empty(WardrobeBuilder.Build(design, Materials).Warnings);
    }
}
