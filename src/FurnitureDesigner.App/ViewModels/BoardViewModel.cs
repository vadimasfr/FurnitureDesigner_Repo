using FurnitureDesigner.Core.Editing;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.App.ViewModels;

/// <summary>
/// Editable view of one board. Looks the board up by id on every access, because undo/redo
/// replaces the project (and with it all board instances).
/// </summary>
public sealed class BoardViewModel(ProjectEditor editor, Guid boardId) : ObservableObject
{
    public Guid BoardId => boardId;

    private Board? Board => editor.Project.Boards.FirstOrDefault(b => b.Id == boardId);

    public string Name
    {
        get => Board?.Name ?? "";
        set => Edit(b => b.Name = value);
    }

    public string Kind => Board?.Kind.ToString() ?? "";

    public string GroupName
    {
        get => editor.Project.FindGroup(Board?.GroupId)?.Name ?? "(none)";
        set
        {
            if (Board?.GroupId is { } id)
                editor.Execute(p => p.FindGroup(id)!.Name = value);
        }
    }

    public string MaterialId
    {
        get => Board?.MaterialId ?? "";
        set => Edit(b => b.MaterialId = value);
    }

    public double X { get => Board?.X ?? 0; set => Edit(b => b.X = value); }
    public double Y { get => Board?.Y ?? 0; set => Edit(b => b.Y = value); }
    public double Z { get => Board?.Z ?? 0; set => Edit(b => b.Z = value); }

    public double Width { get => Board?.SizeX ?? 0; set => EditSize(value, b => b.SizeX = value); }
    public double Height { get => Board?.SizeY ?? 0; set => EditSize(value, b => b.SizeY = value); }
    public double Depth { get => Board?.SizeZ ?? 0; set => EditSize(value, b => b.SizeZ = value); }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void EditSize(double value, Action<Board> change)
    {
        if (value > 0)
            Edit(change);
        else
            Refresh();
    }

    private void Edit(Action<Board> change)
    {
        if (Board is null)
            return;
        editor.Execute(p => change(p.Boards.First(b => b.Id == boardId)));
    }
}
