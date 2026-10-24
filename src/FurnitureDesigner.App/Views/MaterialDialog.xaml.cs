using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FurnitureDesigner.App.Viewport;
using FurnitureDesigner.Core.Models;
using Microsoft.Win32;

namespace FurnitureDesigner.App.Views;

/// <summary>Edits a copy of a material; <see cref="Result"/> holds the edited copy after OK.</summary>
public partial class MaterialDialog : Window
{
    private readonly BoardMaterial _source;

    public MaterialDialog(BoardMaterial source, string title)
    {
        InitializeComponent();
        _source = source;
        Title = title;
        var c = CultureInfo.CurrentCulture;
        NameBox.Text = source.Name;
        ColorBox.Text = source.Color;
        TextureBox.ItemsSource = Enum.GetValues<MaterialTexture>();
        TextureBox.SelectedItem = source.Texture;
        ImageBox.Text = source.ImagePath ?? "";
        TileBox.Text = source.TextureSize.ToString(c);
        ThicknessBox.Text = source.Thickness.ToString(c);
        PriceBox.Text = source.PricePerM2.ToString(c);
        OpacityBox.Text = source.Opacity.ToString(c);
    }

    public BoardMaterial? Result { get; private set; }

    private void OnColorChanged(object sender, TextChangedEventArgs e) =>
        ColorPreview.Background = new SolidColorBrush(TextureFactory.ParseColor(ColorBox.Text, Colors.Transparent));

    private void OnTextureChanged(object sender, SelectionChangedEventArgs e) =>
        ImageRow.Visibility = TextureBox.SelectedItem is MaterialTexture.Image ? Visibility.Visible : Visibility.Collapsed;

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp" };
        if (dialog.ShowDialog(this) == true)
        {
            ImageBox.Text = dialog.FileName;
            TextureBox.SelectedItem = MaterialTexture.Image;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var c = CultureInfo.CurrentCulture;
        if (string.IsNullOrWhiteSpace(NameBox.Text)
            || !System.Text.RegularExpressions.Regex.IsMatch(ColorBox.Text.Trim(), "^#[0-9A-Fa-f]{6}$")
            || !double.TryParse(TileBox.Text, c, out double tile) || tile <= 0
            || !double.TryParse(ThicknessBox.Text, c, out double thickness) || thickness <= 0
            || !double.TryParse(PriceBox.Text, c, out double price) || price < 0
            || !double.TryParse(OpacityBox.Text, c, out double opacity) || opacity is < 0.1 or > 1)
        {
            MessageBox.Show(this, "Please check the values: a name, a colour like #C9A271, and positive numbers.", Title);
            return;
        }

        Result = new BoardMaterial
        {
            Id = _source.Id,
            Name = NameBox.Text.Trim(),
            Color = ColorBox.Text.Trim().ToUpperInvariant(),
            Texture = (MaterialTexture)TextureBox.SelectedItem,
            ImagePath = string.IsNullOrWhiteSpace(ImageBox.Text) ? null : ImageBox.Text.Trim(),
            TextureSize = tile,
            Thickness = thickness,
            PricePerM2 = price,
            Opacity = opacity,
        };
        DialogResult = true;
    }
}
