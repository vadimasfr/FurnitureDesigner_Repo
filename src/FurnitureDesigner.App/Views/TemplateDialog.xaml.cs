using System.Windows;
using System.Windows.Input;
using FurnitureDesigner.Core.Models;
using FurnitureDesigner.Core.Templates;

namespace FurnitureDesigner.App.Views;

public partial class TemplateDialog : Window
{
    private readonly EditModel _model;

    /// <param name="current">Parameters of an existing item to edit, or null to insert a new one.</param>
    public TemplateDialog(FurnitureTemplate template, IReadOnlyList<BoardMaterial> materials, TemplateParameters? current = null)
    {
        InitializeComponent();
        _model = new EditModel(template, materials, current ?? template.Defaults);
        DataContext = _model;
        if (current is not null)
        {
            Title = "Edit item";
            OkButton.Content = "Apply";
        }
    }

    public TemplateParameters Parameters => _model.ToParameters();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // Commit the focused TextBox before validating.
        FocusManager.SetFocusedElement(this, null);
        if (_model.Width <= 0 || _model.Height <= 0 || _model.Depth <= 0)
        {
            MessageBox.Show(this, "Width, height and depth must be greater than zero.", Title);
            return;
        }
        if (_model.IsCabinet && (_model.Thickness <= 0 || 2 * _model.Thickness >= _model.Width || _model.Plinth + 2 * _model.Thickness >= _model.Height))
        {
            MessageBox.Show(this, "The cabinet is too small for this board thickness and plinth.", Title);
            return;
        }
        DialogResult = true;
    }

    /// <summary>Mutable copy of the template defaults for the dialog fields.</summary>
    private sealed class EditModel(FurnitureTemplate template, IReadOnlyList<BoardMaterial> materials, TemplateParameters start)
    {
        private readonly TemplateParameters _defaults = start;

        public string TemplateName => template.Name;
        public bool IsCabinet => template.IsCabinet;
        public string MaterialLabel => IsCabinet ? "Carcass material" : "Material";
        public IReadOnlyList<BoardMaterial> Materials => materials;

        public double Width { get; set; } = start.Width;
        public double Height { get; set; } = start.Height;
        public double Depth { get; set; } = start.Depth;
        public double Elevation { get; set; } = start.Elevation;
        public double Thickness { get; set; } = start.Thickness;
        public double Plinth { get; set; } = start.Plinth;
        public int Shelves { get; set; } = start.Shelves;
        public int Doors { get; set; } = start.Doors;
        public int Drawers { get; set; } = start.Drawers;
        public string CarcassMaterialId { get; set; } = start.CarcassMaterialId;
        public string FrontMaterialId { get; set; } = start.FrontMaterialId;
        public string BackMaterialId { get; set; } = start.BackMaterialId;
        public FrontStyle FrontStyle { get; set; } = start.FrontStyle;
        public IReadOnlyList<FrontStyle> FrontStyles { get; } = Enum.GetValues<FrontStyle>();

        public TemplateParameters ToParameters() => _defaults with
        {
            Width = Width,
            Height = Height,
            Depth = Depth,
            Elevation = Elevation,
            Thickness = Thickness,
            Plinth = Math.Max(0, Plinth),
            Shelves = Math.Max(0, Shelves),
            Doors = Math.Max(0, Doors),
            Drawers = Math.Max(0, Drawers),
            CarcassMaterialId = CarcassMaterialId,
            FrontMaterialId = FrontMaterialId,
            BackMaterialId = BackMaterialId,
            FrontStyle = FrontStyle,
        };
    }
}
