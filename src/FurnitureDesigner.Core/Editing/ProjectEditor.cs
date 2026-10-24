using FurnitureDesigner.Core.Models;
using FurnitureDesigner.Core.Persistence;
using FurnitureDesigner.Core.Templates;

namespace FurnitureDesigner.Core.Editing;

public enum AlignMode { Left, Right, Bottom, Top, Back, Front, BackWall, Floor }

/// <summary>
/// Owns the open project, the selection and the undo history. Every change goes through
/// <see cref="Execute"/> (or <see cref="PushUndo"/> + <see cref="NotifyChanged"/> for interactive drags),
/// which snapshots the project as JSON before the change.
/// </summary>
public sealed class ProjectEditor
{
    public const int MaxUndo = 100;

    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private readonly HashSet<Guid> _selection = [];

    public ProjectEditor(FurnitureProject? project = null) => Project = project ?? FurnitureProject.CreateDefault();

    public FurnitureProject Project { get; private set; }
    public string? FilePath { get; private set; }
    public bool IsDirty { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public IReadOnlyCollection<Guid> Selection => _selection;
    public IEnumerable<Board> SelectedBoards => Project.Boards.Where(b => _selection.Contains(b.Id));

    public event EventHandler? ProjectChanged;
    public event EventHandler? SelectionChanged;

    // ---- history -------------------------------------------------------------------------

    public void Execute(Action<FurnitureProject> change)
    {
        PushUndo();
        change(Project);
        NotifyChanged();
    }

    /// <summary>
    /// Groups all changes until the returned scope is disposed into one undo step
    /// (used for AI requests, which run many operations).
    /// </summary>
    public IDisposable BeginTransaction()
    {
        if (_transactionDepth == 0)
        {
            _redoBeforeTransaction = [.. _redo];
            PushUndo();
            _transactionChanged = false;
        }
        _transactionDepth++;
        return new TransactionScope(this);
    }

    private int _transactionDepth;
    private bool _transactionChanged;
    private List<string> _redoBeforeTransaction = [];

    private void EndTransaction()
    {
        if (--_transactionDepth > 0 || _transactionChanged)
            return;
        // Nothing changed (e.g. the request failed): drop the snapshot and give back the redo history.
        _undo.RemoveAt(_undo.Count - 1);
        _redo.AddRange(_redoBeforeTransaction);
    }

    private sealed class TransactionScope(ProjectEditor editor) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                editor.EndTransaction();
            }
        }
    }

    public void PushUndo()
    {
        if (_transactionDepth > 0)
            return;
        _undo.Add(ProjectSerializer.Serialize(Project));
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void NotifyChanged()
    {
        if (_transactionDepth > 0)
            _transactionChanged = true;
        IsDirty = true;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);

    private bool Restore(List<string> from, List<string> to)
    {
        if (from.Count == 0)
            return false;
        to.Add(ProjectSerializer.Serialize(Project));
        Project = ProjectSerializer.Deserialize(from[^1]);
        from.RemoveAt(from.Count - 1);
        _selection.IntersectWith(Project.Boards.Select(b => b.Id));
        NotifyChanged();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Load(FurnitureProject project, string? path)
    {
        Project = project;
        FilePath = path;
        _undo.Clear();
        _redo.Clear();
        _selection.Clear();
        IsDirty = false;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string path)
    {
        ProjectSerializer.Save(Project, path);
        FilePath = path;
        IsDirty = false;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- selection -----------------------------------------------------------------------

    public void Select(IEnumerable<Guid> ids, bool additive = false)
    {
        if (!additive)
            _selection.Clear();
        _selection.UnionWith(ids);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleSelection(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        if (list.All(_selection.Contains))
            _selection.ExceptWith(list);
        else
            _selection.UnionWith(list);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0)
            return;
        _selection.Clear();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll() => Select(Project.Boards.Select(b => b.Id));

    /// <summary>All boards of the board's furniture group, or just the board if it is not grouped.</summary>
    public IEnumerable<Guid> GroupMembers(Guid boardId)
    {
        var groupId = Project.Boards.FirstOrDefault(b => b.Id == boardId)?.GroupId;
        return groupId is null
            ? [boardId]
            : Project.Boards.Where(b => b.GroupId == groupId).Select(b => b.Id);
    }

    // ---- operations ----------------------------------------------------------------------

    /// <summary>Inserts a template with its back-left-bottom corner at (x, elevation, z) and selects it.</summary>
    public FurnitureGroup Insert(FurnitureTemplate template, TemplateParameters parameters, double x, double z)
    {
        int number = Project.Groups.Count(g => g.TemplateKey == template.Key) + 1;
        var group = new FurnitureGroup { Name = $"{template.Name} #{number}", TemplateKey = template.Key, Parameters = parameters };
        var boards = template.Build(parameters);
        foreach (var board in boards)
        {
            board.GroupId = group.Id;
            board.X += x;
            board.Y += parameters.Elevation;
            board.Z += z;
        }

        Execute(p =>
        {
            p.Groups.Add(group);
            p.Boards.AddRange(boards);
        });
        Select(boards.Select(b => b.Id));
        return group;
    }

    /// <summary>
    /// Rebuilds a template item with new parameters, keeping its back-left corner in place.
    /// Boards edited by hand inside the item are replaced. Rotated items are rebuilt unrotated.
    /// </summary>
    public void RebuildGroup(Guid groupId, TemplateParameters parameters)
    {
        var group = Project.FindGroup(groupId) ?? throw new InvalidOperationException("Item not found.");
        var template = TemplateLibrary.All.FirstOrDefault(t => t.Key == group.TemplateKey)
            ?? throw new InvalidOperationException($"'{group.Name}' was not made from a library template and cannot be rebuilt.");
        var bounds = Box3.Of(Project.Boards.Where(b => b.GroupId == groupId));
        double x = bounds?.MinX ?? 0, z = bounds?.MinZ ?? 0;

        var boards = template.Build(parameters);
        foreach (var board in boards)
        {
            board.GroupId = groupId;
            board.X += x;
            board.Y += parameters.Elevation;
            board.Z += z;
        }

        Execute(p =>
        {
            p.Boards.RemoveAll(b => b.GroupId == groupId);
            p.Boards.AddRange(boards);
            p.FindGroup(groupId)!.Parameters = parameters;
        });
        Select(boards.Select(b => b.Id));
    }

    /// <summary>Adds already positioned boards (e.g. from an import) as one new item and selects them.</summary>
    public FurnitureGroup AddBoards(string name, IReadOnlyList<Board> boards, IEnumerable<BoardMaterial> newMaterials)
    {
        var group = new FurnitureGroup { Name = name, TemplateKey = "import" };
        foreach (var board in boards)
            board.GroupId = group.Id;
        Execute(p =>
        {
            foreach (var material in newMaterials)
            {
                if (p.FindMaterial(material.Id) is null)
                    p.Materials.Add(material);
            }
            p.Groups.Add(group);
            p.Boards.AddRange(boards);
        });
        Select(boards.Select(b => b.Id));
        return group;
    }

    /// <summary>X right of the last item standing in the same height band, or 0 when the wall is full.</summary>
    public double NextFreeX(double elevation, double height, double width)
    {
        var row = Project.Boards.Where(b => b.Y < elevation + height && b.Y + b.SizeY > elevation).ToList();
        double x = row.Count == 0 ? 0 : row.Max(b => b.X + b.SizeX);
        return x + width > Project.Room.Width ? 0 : x;
    }

    public void MoveSelection(double dx, double dy, double dz, bool recordUndo = true)
    {
        if (_selection.Count == 0 || (dx == 0 && dy == 0 && dz == 0))
            return;
        if (recordUndo)
            PushUndo();
        foreach (var board in SelectedBoards)
        {
            board.X += dx;
            board.Y += dy;
            board.Z += dz;
        }
        NotifyChanged();
    }

    /// <summary>Rotates the selection 90° (clockwise seen from above) around the centre of its bounding box.</summary>
    public void RotateSelection()
    {
        if (Box3.Of(SelectedBoards) is not { } box)
            return;
        double cx = box.CenterX, cz = box.CenterZ;
        Execute(_ =>
        {
            foreach (var b in SelectedBoards)
            {
                double x0 = b.X, x1 = b.X + b.SizeX, z0 = b.Z, z1 = b.Z + b.SizeZ;
                b.X = cx + (z0 - cz);
                b.Z = cz - (x1 - cx);
                (b.SizeX, b.SizeZ) = (z1 - z0, x1 - x0);
            }
        });
    }

    /// <summary>
    /// Aligns every selected item (a whole group, or a loose board) to the selection's bounding box,
    /// or pushes it against the back wall / onto the floor.
    /// </summary>
    public void AlignSelection(AlignMode mode)
    {
        if (Box3.Of(SelectedBoards) is not { } all)
            return;
        var units = SelectedBoards.GroupBy(b => (object?)b.GroupId ?? b.Id).Select(g => g.ToList()).ToList();
        Execute(_ =>
        {
            foreach (var unit in units)
            {
                var box = Box3.Of(unit)!.Value;
                (double dx, double dy, double dz) = mode switch
                {
                    AlignMode.Left => (all.MinX - box.MinX, 0d, 0d),
                    AlignMode.Right => (all.MaxX - box.MaxX, 0, 0),
                    AlignMode.Bottom => (0, all.MinY - box.MinY, 0),
                    AlignMode.Top => (0, all.MaxY - box.MaxY, 0),
                    AlignMode.Back => (0, 0, all.MinZ - box.MinZ),
                    AlignMode.Front => (0, 0, all.MaxZ - box.MaxZ),
                    AlignMode.BackWall => (0, 0, -box.MinZ),
                    AlignMode.Floor => (0, -box.MinY, 0),
                    _ => (0, 0, 0),
                };
                foreach (var b in unit)
                {
                    b.X += dx;
                    b.Y += dy;
                    b.Z += dz;
                }
            }
        });
    }

    public void SetMaterial(IEnumerable<Guid> boardIds, string materialId)
    {
        var ids = boardIds.ToHashSet();
        Execute(p =>
        {
            foreach (var b in p.Boards.Where(b => ids.Contains(b.Id)))
                b.MaterialId = materialId;
        });
    }

    public void DuplicateSelection()
    {
        if (Box3.Of(SelectedBoards) is not { } box)
            return;
        var groupMap = new Dictionary<Guid, FurnitureGroup>();
        var copies = new List<Board>();
        foreach (var board in SelectedBoards.ToList())
        {
            Guid? newGroupId = null;
            if (Project.FindGroup(board.GroupId) is { } group)
            {
                if (!groupMap.TryGetValue(group.Id, out var copy))
                {
                    copy = new FurnitureGroup { Name = group.Name + " (copy)", TemplateKey = group.TemplateKey };
                    groupMap[group.Id] = copy;
                }
                newGroupId = copy.Id;
            }
            var clone = board.Clone(newGroupId);
            clone.X += box.SizeX;
            copies.Add(clone);
        }

        Execute(p =>
        {
            p.Groups.AddRange(groupMap.Values);
            p.Boards.AddRange(copies);
        });
        Select(copies.Select(b => b.Id));
    }

    public void DeleteSelection()
    {
        if (_selection.Count == 0)
            return;
        Execute(p =>
        {
            p.Boards.RemoveAll(b => _selection.Contains(b.Id));
            var used = p.Boards.Select(b => b.GroupId).ToHashSet();
            p.Groups.RemoveAll(g => !used.Contains(g.Id));
        });
        ClearSelection();
    }
}
