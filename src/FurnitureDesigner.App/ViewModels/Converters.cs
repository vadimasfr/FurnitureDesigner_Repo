using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FurnitureDesigner.App.Viewport;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.App.ViewModels;

/// <summary>Binds a RadioButton to an enum value: ConverterParameter is the value's name.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>Material to a swatch brush (texture or colour).</summary>
public sealed class MaterialSwatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is BoardMaterial material ? TextureFactory.Swatch(material) : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
