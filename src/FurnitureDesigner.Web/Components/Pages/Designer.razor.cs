using System.Globalization;
using System.Text.Json;
using FurnitureDesigner.Ai;
using FurnitureDesigner.Core.Editing;
using FurnitureDesigner.Core.Import;
using FurnitureDesigner.Core.Models;
using FurnitureDesigner.Core.Parametric;
using FurnitureDesigner.Core.Persistence;
using FurnitureDesigner.Core.Reports;
using FurnitureDesigner.Core.Templates;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace FurnitureDesigner.Web.Components.Pages;

/// <summary>What a left click in the 3D view does (PRO100's tool palette).</summary>
public enum DesignTool { Select, Paint, Eyedropper, Measure }

/// <summary>One line in the AI chat: Who is "You", "AI" or "tool".</summary>
public sealed record ChatEntry(string Who, string Text)
{
    public bool IsTool => Who == "tool";
}

/// <summary>A node of the Structure tree: an item with its boards, or a loose board.</summary>
public sealed record StructureNode(string Name, string Info, IReadOnlyList<Guid> BoardIds, IReadOnlyList<StructureNode> Children);

/// <summary>
/// The designer page: the web counterpart of the desktop MainViewModel and MainWindow.
/// Every browser tab (Blazor circuit) gets its own editor, undo history and AI chat.
/// </summary>
public partial class Designer : IAsyncDisposable
{
    private const long MaxUploadBytes = 64 * 1024 * 1024;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    private readonly ProjectEditor _editor = new();
    private DesignAssistant _assistant = null!;
    private IJSObjectReference? _viewport;
    private DotNetObjectReference<Designer>? _self;
    private ElementReference _viewportHost;

    private int _projectVersion;
    private bool _sceneDirty = true;

    private DesignTool _tool;
    private string? _currentMaterialId = "oak";
    private bool _selectWholeGroup = true;
    private double _snap = 10;
    private double _step = 10;
    private bool _showEdges = true;
    private bool _showDimensions = true;
    private bool _orthographic;

    private string _rightTab = "catalog";
    private string _catalogTab = "cabinets";
    private string _bottomTab = "cutlist";
    private FurnitureTemplate? _selectedTemplate;

    private readonly List<ChatEntry> _chat = [];
    private string _assistantInput = "";
    private bool _busy;
    private CancellationTokenSource? _assistantCancellation;

    // Dialogs.
    private (FurnitureTemplate Template, TemplateParameters Start, Guid? GroupId)? _templateDialog;
    private (BoardMaterial Material, string Title, bool IsNew)? _materialDialog;
    private (string Title, string Text)? _message;
    private TaskCompletionSource<bool?>? _confirm;
    private string _confirmText = "";
    private bool _showHelp;

    protected override void OnInitialized()
    {
        _assistant = new DesignAssistant(_editor);
        _assistant.ToolCalled += line =>
        {
            _chat.Add(new ChatEntry("tool", line));
            StateHasChanged();
        };
        _editor.ProjectChanged += (_, _) =>
        {
            _projectVersion++;
            _sceneDirty = true;
            // Undo/redo replaces the material objects: keep the palette selection by id.
            if (_currentMaterialId is { } id && _editor.Project.FindMaterial(id) is null)
                _currentMaterialId = _editor.Project.Materials.FirstOrDefault()?.Id;
            StateHasChanged();
        };
        _editor.SelectionChanged += (_, _) =>
        {
            _sceneDirty = true;
            StateHasChanged();
        };
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (firstRender)
            {
                _self = DotNetObjectReference.Create(this);
                _viewport = await JS.InvokeAsync<IJSObjectReference>("import", "./js/viewport.js");
                await _viewport.InvokeVoidAsync("init", _viewportHost, _self, CatalogData());
                if (new Uri(Navigation.Uri).Query.Contains("sample", StringComparison.OrdinalIgnoreCase))
                    _editor.LoadSample();
            }
            if (_viewport is null)
                return;

            object? scene = null;
            if (_sceneDirty)
            {
                _sceneDirty = false;
                scene = SceneData();
            }
            await _viewport.InvokeVoidAsync("sync", new
            {
                scene,
                options = new { tool = _tool.ToString(), snap = _snap, showEdges = _showEdges, showDimensions = _showDimensions, busy = _busy },
                title = Title,
                dirty = _editor.IsDirty,
            });
        }
        catch (JSDisconnectedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _assistantCancellation?.Cancel();
        try
        {
            if (_viewport is not null)
                await _viewport.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
        _self?.Dispose();
    }

    // ---- data for the 3D view --------------------------------------------------------------

    private static object BoardData(Board b) => new
    {
        id = b.Id.ToString(),
        kind = b.Kind.ToString(),
        groupId = b.GroupId?.ToString(),
        materialId = b.MaterialId,
        frontStyle = b.FrontStyle.ToString(),
        x = b.X, y = b.Y, z = b.Z,
        sizeX = b.SizeX, sizeY = b.SizeY, sizeZ = b.SizeZ,
    };

    private static object MaterialData(BoardMaterial m) => new
    {
        id = m.Id, name = m.Name, color = m.Color, opacity = m.Opacity, texture = m.Texture.ToString(),
        imagePath = m.ImagePath, textureSize = m.TextureSize,
    };

    private object SceneData()
    {
        var p = _editor.Project;
        return new
        {
            version = _projectVersion,
            room = new { width = p.Room.Width, depth = p.Room.Depth, height = p.Room.Height, floorColor = p.Room.FloorColor, wallColor = p.Room.WallColor },
            materials = p.Materials.Select(MaterialData),
            boards = p.Boards.Select(BoardData),
            selection = _editor.Selection.Select(id => id.ToString()),
        };
    }

    private static object CatalogData()
    {
        var materials = FurnitureProject.CreateDefault().Materials;
        var wardrobe = WardrobeBuilder.Build(WardrobeDesign.ForNiche(2400, 2500, 600), materials).Boards;
        return new
        {
            templates = TemplateLibrary.All
                .Select(t => new { key = t.Key, boards = t.Build(t.Defaults).Select(BoardData) })
                .Append(new { key = ProjectEditor.WardrobeTemplateKey, boards = wardrobe.Select(BoardData) }),
            materials = materials.Select(MaterialData),
        };
    }

    // ---- calls from the 3D view ------------------------------------------------------------

    /// <summary>Select tool click on a board (or empty space). Returns the boards to drag, if the click starts a drag.</summary>
    [JSInvokable]
    public string[] ViewportClick(string? boardId, bool ctrl, bool alt, bool shift)
    {
        if (_busy)
            return [];
        try
        {
            if (boardId is null || !Guid.TryParse(boardId, out var id))
            {
                if (!ctrl)
                    _editor.ClearSelection();
                return [];
            }

            bool singleBoard = !_selectWholeGroup || alt;
            List<Guid> ids = singleBoard ? [id] : [.. _editor.GroupMembers(id)];
            if (ctrl)
            {
                _editor.ToggleSelection(ids);
                return [];
            }
            if (!_editor.Selection.Contains(id) || singleBoard)
                _editor.Select(ids);
            return [.. _editor.Selection.Select(g => g.ToString())];
        }
        finally
        {
            StateHasChanged();
        }
    }

    /// <summary>End of a mouse drag: moves the selection as one undo step. Returns the project version that includes the move.</summary>
    [JSInvokable]
    public int ViewportMove(double dx, double dy, double dz)
    {
        if (!_busy)
            _editor.MoveSelection(dx, dy, dz);
        StateHasChanged();
        return _projectVersion;
    }

    /// <summary>Paint tool: gives the board (Shift: the whole item) the current material.</summary>
    [JSInvokable]
    public void ViewportPaint(string boardId, bool shift)
    {
        if (_busy || !Guid.TryParse(boardId, out var id) || CurrentMaterial is not { } material)
            return;
        _editor.SetMaterial(shift ? _editor.GroupMembers(id) : [id], material.Id);
        StateHasChanged();
    }

    [JSInvokable]
    public void ViewportPick(string boardId)
    {
        if (_busy || !Guid.TryParse(boardId, out var id))
            return;
        var board = _editor.Project.Boards.FirstOrDefault(b => b.Id == id);
        if (board is not null && _editor.Project.FindMaterial(board.MaterialId) is not null)
            _currentMaterialId = board.MaterialId;
        _tool = DesignTool.Paint;
        StateHasChanged();
    }

    /// <summary>Inserts a template with its default sizes centred on a floor point (drag and drop from the catalog).</summary>
    [JSInvokable]
    public void ViewportDrop(string templateKey, double centerX, double centerZ)
    {
        if (templateKey == ProjectEditor.WardrobeTemplateKey)
        {
            InsertWardrobe();
            StateHasChanged();
            return;
        }
        var template = TemplateLibrary.All.FirstOrDefault(t => t.Key == templateKey);
        if (_busy || template is null)
            return;
        var p = template.Defaults;
        var room = _editor.Project.Room;
        double x = Math.Clamp(Math.Round((centerX - p.Width / 2) / 10) * 10, 0, Math.Max(0, room.Width - p.Width));
        double z = Math.Clamp(Math.Round((centerZ - p.Depth / 2) / 10) * 10, 0, Math.Max(0, room.Depth - p.Depth));
        _editor.Insert(template, p, x, z);
        StateHasChanged();
    }

    [JSInvokable]
    public void ViewportOrthoChanged(bool orthographic)
    {
        _orthographic = orthographic;
        StateHasChanged();
    }

    [JSInvokable]
    public async Task ViewportKey(string key, bool ctrl, bool shift)
    {
        if (key == "F1")
        {
            _showHelp = true;
            StateHasChanged();
            return;
        }
        if (ctrl)
        {
            switch (key)
            {
                case "z": Undo(); break;
                case "y": Redo(); break;
                case "d": if (CanEdit && HasSelection) _editor.DuplicateSelection(); break;
                case "a": if (CanEdit) _editor.SelectAll(); break;
                case "s": await SaveProject(); break;
            }
            StateHasChanged();
            return;
        }
        if (!CanEdit)
            return;

        double step = _step * (shift ? 10 : 1);
        switch (key)
        {
            case "Delete": _editor.DeleteSelection(); break;
            case "r": _editor.RotateSelection(); break;
            case "s": _tool = DesignTool.Select; break;
            case "p": _tool = DesignTool.Paint; break;
            case "i": _tool = DesignTool.Eyedropper; break;
            case "m": _tool = DesignTool.Measure; break;
            case "Escape": _editor.ClearSelection(); break;
            case "ArrowLeft": _editor.MoveSelection(-step, 0, 0); break;
            case "ArrowRight": _editor.MoveSelection(step, 0, 0); break;
            case "ArrowUp": _editor.MoveSelection(0, 0, -step); break;
            case "ArrowDown": _editor.MoveSelection(0, 0, step); break;
            case "PageUp": _editor.MoveSelection(0, step, 0); break;
            case "PageDown": _editor.MoveSelection(0, -step, 0); break;
        }
        StateHasChanged();
    }

    private async Task ViewCommand(string name)
    {
        if (_viewport is not null)
            _orthographic = await _viewport.InvokeAsync<bool>("command", name);
    }

    // ---- state shown on the page -----------------------------------------------------------

    private bool CanEdit => !_busy;
    private bool HasSelection => _editor.Selection.Count > 0;
    private BoardMaterial? CurrentMaterial => _currentMaterialId is null ? null : _editor.Project.FindMaterial(_currentMaterialId);

    private string FileName => _editor.FilePath ?? "Untitled";
    private string Title => $"Furniture Designer - {FileName}{(_editor.IsDirty ? " *" : "")}";
    private string Summary => $"{_editor.Project.Boards.Count} boards, {_editor.Project.Groups.Count} items";

    private string ToolHint => _tool switch
    {
        DesignTool.Paint => "Paint: click a board to give it the current material (Shift: the whole item).",
        DesignTool.Eyedropper => "Eyedropper: click a board to pick up its material.",
        DesignTool.Measure => "Measure: click two points (snaps to board corners). Esc clears.",
        _ => "Select: click to select, drag to move (Shift: up/down). Drag items from the catalog into the room.",
    };

    /// <summary>Set when exactly one board is selected (or the whole selection is one group: then its first board).</summary>
    private Board? SelectedBoard
    {
        get
        {
            var selected = _editor.SelectedBoards.ToList();
            if (selected.Count == 1)
                return selected[0];
            if (selected.Count > 1 && selected[0].GroupId is not null && selected.All(b => b.GroupId == selected[0].GroupId))
                return selected[0];
            return null;
        }
    }

    private FurnitureGroup? SelectedGroup => SelectedBoard is { } board ? _editor.Project.FindGroup(board.GroupId) : null;

    private string SelectionText
    {
        get
        {
            var selected = _editor.SelectedBoards.ToList();
            return selected.Count switch
            {
                0 => "Nothing selected",
                1 => $"{selected[0].Name}: {BoxInfo(selected)}",
                _ => $"{selected.Count} boards, {BoxInfo(selected)}",
            };
        }
    }

    private IEnumerable<StructureNode> Structure()
    {
        var project = _editor.Project;
        StructureNode Leaf(Board b) => new(b.Name, $"{project.FindMaterial(b.MaterialId)?.Name}, {BoxInfo([b])}", [b.Id], []);
        return project.Groups
            .Select(g => (Group: g, Boards: project.Boards.Where(b => b.GroupId == g.Id).ToList()))
            .Select(x => new StructureNode(x.Group.Name, BoxInfo(x.Boards), x.Boards.Select(b => b.Id).ToList(), x.Boards.Select(Leaf).ToList()))
            .Concat(project.Boards.Where(b => project.FindGroup(b.GroupId) is null).Select(Leaf));
    }

    private static string BoxInfo(IEnumerable<Board> boards) =>
        Box3.Of(boards) is { } b
            ? string.Create(Inv, $"{b.SizeX:0.#} × {b.SizeY:0.#} × {b.SizeZ:0.#} mm at ({b.MinX:0.#}; {b.MinY:0.#}; {b.MinZ:0.#})")
            : "";

    private static string F(double value) => value.ToString("0.#", Inv);

    private static bool TryNumber(ChangeEventArgs e, out double value) =>
        double.TryParse(e.Value?.ToString(), NumberStyles.Float, Inv, out value) && double.IsFinite(value);

    // ---- commands --------------------------------------------------------------------------

    private void Undo()
    {
        if (CanEdit)
            _editor.Undo();
    }

    private void Redo()
    {
        if (CanEdit)
            _editor.Redo();
    }

    private void Align(AlignMode mode)
    {
        if (CanEdit)
            _editor.AlignSelection(mode);
    }

    private void ApplyMaterial()
    {
        if (CanEdit && HasSelection && CurrentMaterial is { } material)
            _editor.SetMaterial(_editor.Selection.ToList(), material.Id);
    }

    private async Task NewProject()
    {
        if (await ConfirmDiscardChanges())
            _editor.Load(FurnitureProject.CreateDefault(), null);
    }

    private async Task LoadSample()
    {
        if (await ConfirmDiscardChanges())
            _editor.LoadSample();
    }

    private async Task OpenProject(InputFileChangeEventArgs e)
    {
        var file = e.File;
        string json;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream(MaxUploadBytes));
            json = await reader.ReadToEndAsync();
        }
        catch (IOException ex)
        {
            ShowMessage("Open", $"Could not read the file:\n{ex.Message}");
            return;
        }
        if (!await ConfirmDiscardChanges())
            return;
        try
        {
            _editor.Load(ProjectSerializer.Deserialize(json), file.Name);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            ShowMessage("Open", $"Could not open the project:\n{ex.Message}");
        }
    }

    /// <summary>"Saving" downloads the project file; the browser decides where it goes.</summary>
    private async Task SaveProject()
    {
        if (_viewport is null)
            return;
        string name = Path.GetFileNameWithoutExtension(_editor.FilePath ?? _editor.Project.Name) is { Length: > 0 } n ? n : "project";
        string fileName = name + ProjectSerializer.FileExtension;
        await _viewport.InvokeVoidAsync("download", fileName, ProjectSerializer.Serialize(_editor.Project), "application/json");
        _editor.MarkSaved(fileName);
    }

    private async Task ExportCutList()
    {
        if (_viewport is not null)
            await _viewport.InvokeVoidAsync("download", "cut-list.csv", ProjectReports.CutListCsv(ProjectReports.CutList(_editor.Project)), "text/csv");
    }

    private async Task ImportFile(InputFileChangeEventArgs e)
    {
        var files = e.GetMultipleFiles(4);
        var main = files.FirstOrDefault(f => !f.Name.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase));
        if (main is null)
        {
            ShowMessage("Import", "Choose a .dxf or .obj file (with its .mtl file, if any).");
            return;
        }

        ImportResult result;
        try
        {
            string text = await ReadText(main);
            var mtl = files.FirstOrDefault(f => f.Name.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase));
            result = FurnitureImporter.Import(main.Name, text, mtl is null ? null : await ReadText(mtl));
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FormatException or InvalidDataException)
        {
            ShowMessage("Import", ex.Message);
            return;
        }

        string warnings = result.Warnings.Count == 0 ? "" : "\n\n" + string.Join("\n", result.Warnings.Select(w => "• " + w));
        if (result.Boards.Count == 0)
        {
            ShowMessage("Import", "Nothing to import." + warnings);
            return;
        }

        _editor.AddBoards(Path.GetFileNameWithoutExtension(main.Name), result.Boards, result.Materials);
        var box = Box3.Of(result.Boards)!.Value;
        var room = _editor.Project.Room;
        if (box.MaxX > room.Width || box.MaxZ > room.Depth || box.MaxY > room.Height)
        {
            _editor.Execute(p =>
            {
                p.Room.Width = Math.Max(room.Width, Math.Ceiling(box.MaxX / 100) * 100);
                p.Room.Depth = Math.Max(room.Depth, Math.Ceiling(box.MaxZ / 100) * 100);
                p.Room.Height = Math.Max(room.Height, Math.Ceiling(box.MaxY / 100) * 100);
            });
            warnings += "\n• The room was enlarged to fit the import.";
        }
        ShowMessage("Import", string.Create(Inv,
            $"Imported {result.Boards.Count} boards in {result.Materials.Count} materials ({box.SizeX:0} × {box.SizeY:0} × {box.SizeZ:0} mm, file unit = {result.MillimetresPerUnit} mm).{warnings}"));
    }

    private static async Task<string> ReadText(IBrowserFile file)
    {
        using var reader = new StreamReader(file.OpenReadStream(MaxUploadBytes));
        return await reader.ReadToEndAsync();
    }

    // ---- catalog and item dialogs ----------------------------------------------------------

    private void OpenInsertDialog(FurnitureTemplate? template)
    {
        if (CanEdit && template is not null)
            _templateDialog = (template, template.Defaults, null);
    }

    private void OpenEditItemDialog()
    {
        if (!CanEdit || SelectedGroup is not { Parameters: { } parameters } group)
            return;
        if (TemplateLibrary.All.FirstOrDefault(t => t.Key == group.TemplateKey) is { } template)
            _templateDialog = (template, parameters, group.Id);
    }

    private void OnTemplateDialogOk(TemplateParameters parameters)
    {
        if (_templateDialog is not { } dialog)
            return;
        _templateDialog = null;
        if (dialog.GroupId is { } groupId)
        {
            _editor.RebuildGroup(groupId, parameters);
            return;
        }
        double x = _editor.NextFreeX(parameters.Elevation, parameters.Height, parameters.Width);
        _editor.Insert(dialog.Template, parameters, x, 0);
    }

    // ---- parametric wardrobe ---------------------------------------------------------------

    /// <summary>Adds a wardrobe that fills the room's back wall and opens its designer.</summary>
    private void InsertWardrobe()
    {
        if (!CanEdit)
            return;
        var room = _editor.Project.Room;
        _editor.InsertWardrobe(WardrobeDesign.ForNiche(room.Width, room.Height, Math.Min(600, room.Depth)), 0, 0);
        _rightTab = "properties";
    }

    private void EditWardrobe(Guid groupId, Action<WardrobeDesign> change)
    {
        if (!CanEdit || _editor.Project.FindGroup(groupId)?.Wardrobe is null)
            return;
        try
        {
            _editor.EditWardrobe(groupId, change);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The design changed under a stale control (e.g. undo while a field was focused): ignore the edit.
        }
    }

    private void FitWardrobeToRoom(Guid groupId)
    {
        var room = _editor.Project.Room;
        if (CanEdit)
            _editor.EditWardrobe(groupId, d => (d.Width, d.Height) = (room.Width, room.Height), nicheOrigin: (0, 0));
    }

    /// <summary>Selects one section's boards (carcass parts, interior and fronts, including its top storage doors).</summary>
    private void SelectWardrobeSection(Guid groupId, int section)
    {
        string inner = $"Section {section + 1} ", top = $"Top storage section {section + 1} ";
        _editor.Select(_editor.Project.Boards
            .Where(b => b.GroupId == groupId && (b.Name.StartsWith(inner, StringComparison.Ordinal) || b.Name.StartsWith(top, StringComparison.Ordinal)))
            .Select(b => b.Id));
    }

    private void OpenNewMaterialDialog()
    {
        var template = CurrentMaterial ?? new BoardMaterial();
        int n = 1;
        while (_editor.Project.FindMaterial($"custom{n}") is not null)
            n++;
        var draft = new BoardMaterial
        {
            Id = $"custom{n}", Name = $"New material {n}", Color = template.Color, Texture = template.Texture,
            Thickness = template.Thickness, PricePerM2 = template.PricePerM2, TextureSize = template.TextureSize,
            ImagePath = template.ImagePath,
        };
        _materialDialog = (draft, "New material", true);
    }

    private void OpenEditMaterialDialog()
    {
        if (CurrentMaterial is { } material)
            _materialDialog = (material, "Edit material", false);
    }

    private void OnMaterialDialogOk(BoardMaterial result)
    {
        if (_materialDialog is not { } dialog)
            return;
        _materialDialog = null;
        _editor.Execute(p =>
        {
            int index = p.Materials.FindIndex(m => m.Id == result.Id);
            if (dialog.IsNew || index < 0)
                p.Materials.Add(result);
            else
                p.Materials[index] = result;
        });
        _currentMaterialId = result.Id;
    }

    // ---- property edits --------------------------------------------------------------------

    private void EditBoard(Guid boardId, Action<Board> change)
    {
        if (CanEdit && _editor.Project.Boards.Any(b => b.Id == boardId))
            _editor.Execute(p => change(p.Boards.First(b => b.Id == boardId)));
    }

    private void EditBoardNumber(Guid boardId, ChangeEventArgs e, Action<Board, double> change, bool positive = false)
    {
        if (TryNumber(e, out double value) && (!positive || value > 0))
            EditBoard(boardId, b => change(b, value));
        else
            StateHasChanged();
    }

    private void EditGroupName(Guid groupId, ChangeEventArgs e)
    {
        if (CanEdit && _editor.Project.FindGroup(groupId) is not null)
            _editor.Execute(p => p.FindGroup(groupId)!.Name = e.Value?.ToString() ?? "");
    }

    private void EditRoom(ChangeEventArgs e, Action<Room, double> change)
    {
        if (CanEdit && TryNumber(e, out double value) && value > 0)
            _editor.Execute(p => change(p.Room, value));
    }

    private void EditMaterial(string materialId, Action<BoardMaterial> change)
    {
        if (CanEdit && _editor.Project.FindMaterial(materialId) is not null)
            _editor.Execute(p => change(p.FindMaterial(materialId)!));
    }

    private void EditMaterialNumber(string materialId, ChangeEventArgs e, Action<BoardMaterial, double> change, double min, double max = double.MaxValue)
    {
        if (TryNumber(e, out double value) && value >= min && value <= max)
            EditMaterial(materialId, m => change(m, value));
    }

    // ---- AI assistant ----------------------------------------------------------------------

    private async Task SendToAssistant()
    {
        string request = _assistantInput.Trim();
        if (_busy || request.Length == 0)
            return;
        _assistantInput = "";
        _chat.Add(new ChatEntry("You", request));
        _busy = true;
        _assistantCancellation = new CancellationTokenSource();
        StateHasChanged();
        try
        {
            string reply = await _assistant.SendAsync(request, _assistantCancellation.Token);
            _chat.Add(new ChatEntry("AI", reply.Length > 0 ? reply : "(done)"));
            var affected = _assistant.Affected.Where(id => _editor.Project.Boards.Any(b => b.Id == id)).ToList();
            if (affected.Count > 0)
                _editor.Select(affected);
        }
        catch (Exception ex)
        {
            _chat.Add(new ChatEntry("AI", DesignAssistant.DescribeError(ex)));
        }
        finally
        {
            _assistantCancellation.Dispose();
            _assistantCancellation = null;
            _busy = false;
        }
    }

    private void CancelAssistant() => _assistantCancellation?.Cancel();

    private void NewChat()
    {
        if (_busy)
            return;
        _assistant.Reset();
        _chat.Clear();
    }

    // ---- message and confirmation dialogs --------------------------------------------------

    private void ShowMessage(string title, string text) => _message = (title, text);

    /// <summary>Asks to save unsaved changes. Returns false when the user cancels.</summary>
    private async Task<bool> ConfirmDiscardChanges()
    {
        if (!_editor.IsDirty)
            return true;
        _confirmText = "Save changes to the current project?";
        _confirm = new TaskCompletionSource<bool?>();
        StateHasChanged();
        bool? answer = await _confirm.Task;
        _confirm = null;
        if (answer == true)
            await SaveProject();
        return answer is not null;
    }

    private void AnswerConfirm(bool? answer) => _confirm?.TrySetResult(answer);
}
