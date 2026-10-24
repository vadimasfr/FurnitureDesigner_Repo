using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using FurnitureDesigner.Ai;
using FurnitureDesigner.App.Views;
using FurnitureDesigner.Core.Editing;
using FurnitureDesigner.Core.Import;
using FurnitureDesigner.Core.Models;
using FurnitureDesigner.Core.Persistence;
using FurnitureDesigner.Core.Reports;
using FurnitureDesigner.Core.Templates;
using Microsoft.Win32;

namespace FurnitureDesigner.App.ViewModels;

/// <summary>A node of the Structure tree: an item with its boards, or a loose board.</summary>
public sealed record StructureNode(string Name, string Info, IReadOnlyList<Guid> BoardIds, IReadOnlyList<StructureNode> Children);

/// <summary>A library template with its rendered thumbnail.</summary>
public sealed record CatalogItem(FurnitureTemplate Template, System.Windows.Media.ImageSource Thumbnail)
{
    public string Name => Template.Name;
    public string Category => Template.Category;
}

/// <summary>What a left click in the 3D view does (PRO100's tool palette).</summary>
public enum DesignTool { Select, Paint, Eyedropper, Measure }

/// <summary>One line in the AI chat: Who is "You", "AI" or "tool".</summary>
public sealed record ChatEntry(string Who, string Text)
{
    public bool IsTool => Who == "tool";
}

public sealed class MainViewModel : ObservableObject
{
    private const string FileFilter = "Furniture project (*.fdp)|*.fdp|All files (*.*)|*.*";

    private FurnitureTemplate? _selectedTemplate;
    private BoardViewModel? _selectedBoard;
    private bool _selectWholeGroup = true;
    private double _snap = 10;
    private double _step = 10;
    private string _assistantInput = "";
    private bool _isAssistantBusy;
    private CancellationTokenSource? _assistantCancellation;

    public MainViewModel()
    {
        Assistant = new DesignAssistant(Editor);
        Assistant.ToolCalled += line => ChatEntries.Add(new ChatEntry("tool", line));
        SendToAssistantCommand = new RelayCommand(() => _ = SendToAssistantAsync(),
            () => !IsAssistantBusy && !string.IsNullOrWhiteSpace(AssistantInput));
        CancelAssistantCommand = new RelayCommand(() => _assistantCancellation?.Cancel(), () => IsAssistantBusy);
        NewChatCommand = new RelayCommand(() =>
        {
            Assistant.Reset();
            ChatEntries.Clear();
        }, () => !IsAssistantBusy);
        ImportCommand = new RelayCommand(ImportFile, () => !IsAssistantBusy);
        EditItemCommand = new RelayCommand(EditSelectedItem, () => SelectedGroup?.Parameters is not null && !IsAssistantBusy);

        var catalog = CollectionViewSource.GetDefaultView(TemplateLibrary.All
            .Select(t => new CatalogItem(t, Viewport.ThumbnailRenderer.Render(t.Build(t.Defaults), Editor.Project.Materials)))
            .ToList());
        catalog.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogItem.Category)));
        Catalog = catalog;
        _currentMaterial = Editor.Project.FindMaterial("oak");

        AlignCommand = new ParameterCommand(mode => Editor.AlignSelection(Enum.Parse<AlignMode>(mode)), HasSelection);
        ApplyMaterialCommand = new RelayCommand(
            () => Editor.SetMaterial(Editor.Selection, CurrentMaterial!.Id), () => HasSelection() && CurrentMaterial is not null);
        NewMaterialCommand = new RelayCommand(NewMaterial);
        EditMaterialCommand = new RelayCommand(EditMaterial, () => CurrentMaterial is not null);

        NewCommand = new RelayCommand(NewProject);
        OpenCommand = new RelayCommand(OpenProject);
        SaveCommand = new RelayCommand(() => SaveProject(saveAs: false));
        SaveAsCommand = new RelayCommand(() => SaveProject(saveAs: true));
        ExportCutListCommand = new RelayCommand(ExportCutList);
        UndoCommand = new RelayCommand(() => Editor.Undo(), () => Editor.CanUndo && !IsAssistantBusy);
        RedoCommand = new RelayCommand(() => Editor.Redo(), () => Editor.CanRedo && !IsAssistantBusy);
        DuplicateCommand = new RelayCommand(Editor.DuplicateSelection, HasSelection);
        RotateCommand = new RelayCommand(Editor.RotateSelection, HasSelection);
        DeleteCommand = new RelayCommand(Editor.DeleteSelection, HasSelection);
        SelectAllCommand = new RelayCommand(Editor.SelectAll);
        InsertCommand = new RelayCommand(() => InsertTemplate(SelectedTemplate), () => SelectedTemplate is not null);

        Editor.ProjectChanged += (_, _) => RefreshProject();
        Editor.SelectionChanged += (_, _) => RefreshSelection();
        RefreshProject();
        RefreshSelection();
    }

    public ProjectEditor Editor { get; } = new();
    public ICollectionView Catalog { get; }
    public ObservableCollection<StructureNode> Structure { get; } = [];

    public ParameterCommand AlignCommand { get; }
    public RelayCommand ApplyMaterialCommand { get; }
    public RelayCommand NewMaterialCommand { get; }
    public RelayCommand EditMaterialCommand { get; }

    private DesignTool _activeTool;
    private BoardMaterial? _currentMaterial;

    public DesignTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (SetField(ref _activeTool, value))
                OnPropertyChanged(nameof(ToolHint));
        }
    }

    public string ToolHint => ActiveTool switch
    {
        DesignTool.Paint => "Paint: click a board to give it the current material (Shift: the whole item).",
        DesignTool.Eyedropper => "Eyedropper: click a board to pick up its material.",
        DesignTool.Measure => "Measure: click two points (snaps to board corners). Esc clears.",
        _ => "Select: click to select, drag to move (Shift: up/down). Drag items from the catalog into the room.",
    };

    /// <summary>Material used by the paint tool and "apply to selection" (the Textures palette selection).</summary>
    public BoardMaterial? CurrentMaterial
    {
        get => _currentMaterial;
        set => SetField(ref _currentMaterial, value);
    }

    /// <summary>Inserts a template with its default sizes centred on a floor point (drag and drop from the catalog).</summary>
    public void InsertAt(FurnitureTemplate template, double centerX, double centerZ)
    {
        var p = template.Defaults;
        var room = Editor.Project.Room;
        double x = Math.Clamp(Math.Round((centerX - p.Width / 2) / 10) * 10, 0, Math.Max(0, room.Width - p.Width));
        double z = Math.Clamp(Math.Round((centerZ - p.Depth / 2) / 10) * 10, 0, Math.Max(0, room.Depth - p.Depth));
        Editor.Insert(template, p, x, z);
    }

    /// <summary>Paint tool: gives the boards the current material.</summary>
    public void PaintBoards(IEnumerable<Guid> boardIds)
    {
        if (CurrentMaterial is { } material)
            Editor.SetMaterial(boardIds, material.Id);
    }

    public void PickMaterial(Guid boardId)
    {
        var board = Editor.Project.Boards.FirstOrDefault(b => b.Id == boardId);
        CurrentMaterial = board is null ? CurrentMaterial : Editor.Project.FindMaterial(board.MaterialId);
        ActiveTool = DesignTool.Paint;
    }

    public void SelectStructureNode(StructureNode node) => Editor.Select(node.BoardIds);

    private void NewMaterial()
    {
        var template = CurrentMaterial ?? new BoardMaterial();
        int n = 1;
        while (Editor.Project.FindMaterial($"custom{n}") is not null)
            n++;
        var draft = new BoardMaterial
        {
            Id = $"custom{n}", Name = $"New material {n}", Color = template.Color, Texture = template.Texture,
            Thickness = template.Thickness, PricePerM2 = template.PricePerM2, TextureSize = template.TextureSize,
        };
        var dialog = new MaterialDialog(draft, "New material") { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
            return;
        Editor.Execute(p => p.Materials.Add(dialog.Result!));
        CurrentMaterial = Editor.Project.FindMaterial(dialog.Result!.Id);
    }

    private void EditMaterial()
    {
        if (CurrentMaterial is not { } material)
            return;
        var dialog = new MaterialDialog(material, "Edit material") { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
            return;
        Editor.Execute(p =>
        {
            int index = p.Materials.FindIndex(m => m.Id == material.Id);
            if (index >= 0)
                p.Materials[index] = dialog.Result!;
        });
        CurrentMaterial = Editor.Project.FindMaterial(material.Id);
    }

    public RelayCommand NewCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand ExportCutListCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand RotateCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand InsertCommand { get; }
    public RelayCommand SendToAssistantCommand { get; }
    public RelayCommand CancelAssistantCommand { get; }
    public RelayCommand NewChatCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand EditItemCommand { get; }

    public DesignAssistant Assistant { get; }
    public ObservableCollection<ChatEntry> ChatEntries { get; } = [];

    public string AssistantInput
    {
        get => _assistantInput;
        set => SetField(ref _assistantInput, value);
    }

    public bool IsAssistantBusy
    {
        get => _isAssistantBusy;
        private set
        {
            if (SetField(ref _isAssistantBusy, value))
            {
                OnPropertyChanged(nameof(IsEditingEnabled));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>False while the AI is changing the project, so the user does not edit at the same time.</summary>
    public bool IsEditingEnabled => !IsAssistantBusy;

    private FurnitureGroup? SelectedGroup =>
        SelectedBoard is null ? null : Editor.Project.FindGroup(Editor.Project.Boards.FirstOrDefault(b => b.Id == SelectedBoard.BoardId)?.GroupId);

    public ObservableCollection<CutListRow> CutList { get; } = [];
    public ObservableCollection<CostRow> Costs { get; } = [];
    /// <summary>A fresh copy on every change, so swatches refresh when a material is edited.</summary>
    public IReadOnlyList<BoardMaterial> MaterialPalette => [.. Editor.Project.Materials];
    public List<BoardMaterial> Materials => Editor.Project.Materials;

    public string Title =>
        $"Furniture Designer - {(Editor.FilePath is null ? "Untitled" : Path.GetFileName(Editor.FilePath))}{(Editor.IsDirty ? " *" : "")}";

    public double TotalCost => Costs.Sum(c => c.Cost);
    public string Summary => $"{Editor.Project.Boards.Count} boards, {Editor.Project.Groups.Count} items";

    public FurnitureTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set => SetField(ref _selectedTemplate, value);
    }

    /// <summary>Set when exactly one board is selected (or the whole selection is one group: then its first board).</summary>
    public BoardViewModel? SelectedBoard
    {
        get => _selectedBoard;
        private set
        {
            if (SetField(ref _selectedBoard, value))
                OnPropertyChanged(nameof(HasSelectedBoard));
        }
    }

    public bool HasSelectedBoard => SelectedBoard is not null;
    public string SelectionText { get; private set; } = "";

    public bool SelectWholeGroup
    {
        get => _selectWholeGroup;
        set => SetField(ref _selectWholeGroup, value);
    }

    public double Snap
    {
        get => _snap;
        set => SetField(ref _snap, Math.Max(0, value));
    }

    public double Step
    {
        get => _step;
        set => SetField(ref _step, Math.Max(1, value));
    }

    public double RoomWidth
    {
        get => Editor.Project.Room.Width;
        set { if (value > 0) Editor.Execute(p => p.Room.Width = value); }
    }

    public double RoomDepth
    {
        get => Editor.Project.Room.Depth;
        set { if (value > 0) Editor.Execute(p => p.Room.Depth = value); }
    }

    public double RoomHeight
    {
        get => Editor.Project.Room.Height;
        set { if (value > 0) Editor.Execute(p => p.Room.Height = value); }
    }


    /// <summary>Replaces the project with a small kitchen and a wardrobe, as a starting point to play with.</summary>
    public void LoadSample()
    {
        if (ConfirmDiscardChanges())
            Editor.LoadSample();
    }

    public void InsertTemplate(FurnitureTemplate? template)
    {
        if (template is null)
            return;
        var dialog = new TemplateDialog(template, Materials) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
            return;

        var parameters = dialog.Parameters;
        double x = Editor.NextFreeX(parameters.Elevation, parameters.Height, parameters.Width);
        Editor.Insert(template, parameters, x, 0);
    }

    private async Task SendToAssistantAsync()
    {
        string request = AssistantInput.Trim();
        AssistantInput = "";
        ChatEntries.Add(new ChatEntry("You", request));
        IsAssistantBusy = true;
        _assistantCancellation = new CancellationTokenSource();
        try
        {
            string reply = await Assistant.SendAsync(request, _assistantCancellation.Token);
            ChatEntries.Add(new ChatEntry("AI", reply.Length > 0 ? reply : "(done)"));
            var affected = Assistant.Affected.Where(id => Editor.Project.Boards.Any(b => b.Id == id)).ToList();
            if (affected.Count > 0)
                Editor.Select(affected);
        }
        catch (Exception ex)
        {
            ChatEntries.Add(new ChatEntry("AI", DesignAssistant.DescribeError(ex)));
        }
        finally
        {
            _assistantCancellation.Dispose();
            _assistantCancellation = null;
            IsAssistantBusy = false;
        }
    }

    private void ImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = FurnitureImporter.FileFilter + "|PRO100 project (*.pro)|*.pro|All files (*.*)|*.*",
            Title = "Import from PRO100",
        };
        if (dialog.ShowDialog() != true)
            return;

        ImportResult result;
        try
        {
            result = FurnitureImporter.ImportFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FormatException or InvalidDataException)
        {
            MessageBox.Show(ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string warnings = result.Warnings.Count == 0 ? "" : "\n\n" + string.Join("\n", result.Warnings.Select(w => "• " + w));
        if (result.Boards.Count == 0)
        {
            MessageBox.Show("Nothing to import." + warnings, "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Editor.AddBoards(Path.GetFileNameWithoutExtension(dialog.FileName), result.Boards, result.Materials);
        var box = Box3.Of(result.Boards)!.Value;
        var room = Editor.Project.Room;
        if (box.MaxX > room.Width || box.MaxZ > room.Depth || box.MaxY > room.Height)
        {
            Editor.Execute(p =>
            {
                p.Room.Width = Math.Max(room.Width, Math.Ceiling(box.MaxX / 100) * 100);
                p.Room.Depth = Math.Max(room.Depth, Math.Ceiling(box.MaxZ / 100) * 100);
                p.Room.Height = Math.Max(room.Height, Math.Ceiling(box.MaxY / 100) * 100);
            });
            warnings += "\n• The room was enlarged to fit the import.";
        }

        MessageBox.Show(
            string.Create(CultureInfo.CurrentCulture,
                $"Imported {result.Boards.Count} boards in {result.Materials.Count} materials ({box.SizeX:0} × {box.SizeY:0} × {box.SizeZ:0} mm, file unit = {result.MillimetresPerUnit} mm).{warnings}"),
            "Import", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EditSelectedItem()
    {
        if (SelectedGroup is not { Parameters: { } parameters } group)
            return;
        var template = TemplateLibrary.All.FirstOrDefault(t => t.Key == group.TemplateKey);
        if (template is null)
            return;
        var dialog = new TemplateDialog(template, Materials, parameters) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
            Editor.RebuildGroup(group.Id, dialog.Parameters);
    }

    /// <summary>Asks to save unsaved changes. Returns false when the user cancels.</summary>
    public bool ConfirmDiscardChanges()
    {
        if (!Editor.IsDirty)
            return true;
        var answer = MessageBox.Show("Save changes to the current project?", "Furniture Designer",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveProject(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private bool HasSelection() => Editor.Selection.Count > 0;

    private void NewProject()
    {
        if (ConfirmDiscardChanges())
            Editor.Load(FurnitureProject.CreateDefault(), null);
    }

    private void OpenProject()
    {
        if (!ConfirmDiscardChanges())
            return;
        var dialog = new OpenFileDialog { Filter = FileFilter };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            Editor.Load(ProjectSerializer.Load(dialog.FileName), dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the project:\n{ex.Message}", "Open", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool SaveProject(bool saveAs)
    {
        string? path = Editor.FilePath;
        if (saveAs || path is null)
        {
            var dialog = new SaveFileDialog { Filter = FileFilter, DefaultExt = ProjectSerializer.FileExtension };
            if (dialog.ShowDialog() != true)
                return false;
            path = dialog.FileName;
        }
        try
        {
            Editor.Save(path);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save the project:\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ExportCutList()
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "cut-list.csv" };
        if (dialog.ShowDialog() == true)
            File.WriteAllText(dialog.FileName, ProjectReports.CutListCsv(CutList));
    }

    private void RefreshProject()
    {
        Replace(CutList, ProjectReports.CutList(Editor.Project));
        Replace(Costs, ProjectReports.Costs(Editor.Project));

        var project = Editor.Project;
        StructureNode Leaf(Board b) => new(b.Name, $"{project.FindMaterial(b.MaterialId)?.Name}, {BoxInfo([b])}", [b.Id], []);
        var nodes = project.Groups
            .Select(g => (Group: g, Boards: project.Boards.Where(b => b.GroupId == g.Id).ToList()))
            .Select(x => new StructureNode(x.Group.Name, BoxInfo(x.Boards), x.Boards.Select(b => b.Id).ToList(), x.Boards.Select(Leaf).ToList()))
            .Concat(project.Boards.Where(b => project.FindGroup(b.GroupId) is null).Select(Leaf));
        Replace(Structure, nodes);

        // Undo/redo replaces the material objects: keep the palette selection by id.
        if (CurrentMaterial is { } current)
            CurrentMaterial = project.FindMaterial(current.Id) ?? project.Materials.FirstOrDefault();

        SelectedBoard?.Refresh();
        OnPropertyChanged(nameof(Materials));
        OnPropertyChanged(nameof(MaterialPalette));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TotalCost));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RoomWidth));
        OnPropertyChanged(nameof(RoomDepth));
        OnPropertyChanged(nameof(RoomHeight));
        UpdateSelectionText();
    }

    private void RefreshSelection()
    {
        var selected = Editor.SelectedBoards.ToList();
        var single = selected.Count == 1 ? selected[0] : null;
        if (single is null && selected.Count > 1 && selected.Select(b => b.GroupId).Distinct().Count() == 1 && selected[0].GroupId is not null)
            single = selected[0];

        if (single is null)
            SelectedBoard = null;
        else if (SelectedBoard?.BoardId != single.Id)
            SelectedBoard = new BoardViewModel(Editor, single.Id);
        else
            SelectedBoard.Refresh();

        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        var selected = Editor.SelectedBoards.ToList();
        SelectionText = selected.Count switch
        {
            0 => "Nothing selected",
            1 => $"{selected[0].Name}: {BoxInfo(selected)}",
            _ => $"{selected.Count} boards, {BoxInfo(selected)}",
        };
        OnPropertyChanged(nameof(SelectionText));
    }

    private static string BoxInfo(IEnumerable<Board> boards) =>
        Box3.Of(boards) is { } b
            ? string.Create(CultureInfo.CurrentCulture, $"{b.SizeX:0.#} × {b.SizeY:0.#} × {b.SizeZ:0.#} mm at ({b.MinX:0.#}; {b.MinY:0.#}; {b.MinZ:0.#})")
            : "";

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}
