using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.App.Viewport;

/// <summary>
/// Material brushes: seamless generated wood and stone, image files, and door-front styles.
/// Bitmaps and brushes are cached and frozen.
/// </summary>
internal static class TextureFactory
{
    private const int Size = 512;
    private static readonly Dictionary<string, BitmapSource?> Bitmaps = [];

    /// <summary>Tiled brush for texture coordinates measured in tiles (1 = one TextureSize).</summary>
    public static Brush? SurfaceBrush(BoardMaterial material)
    {
        if (Bitmap(material) is not { } bitmap)
            return null;
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
            Opacity = material.Opacity,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Swatch for lists and the catalog.</summary>
    public static Brush Swatch(BoardMaterial material)
    {
        Brush brush = Bitmap(material) is { } bitmap
            ? new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill, Viewbox = new Rect(0, 0, 0.5, 0.5) }
            : new SolidColorBrush(ParseColor(material.Color));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Brush for the outside face of a door or drawer front, drawn in millimetres:
    /// the material plus frame and panel shading. Texture coordinates run 0..1 over the face.
    /// </summary>
    public static Brush FrontBrush(BoardMaterial material, FrontStyle style, double width, double height)
    {
        var group = new DrawingGroup();
        var face = new Rect(0, 0, width, height);
        Brush surface = Bitmap(material) is { } bitmap
            ? new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, material.TextureSize, material.TextureSize),
            }
            : new SolidColorBrush(ParseColor(material.Color));
        group.Children.Add(new GeometryDrawing(surface, null, new RectangleGeometry(face)));

        double frame = Math.Clamp(Math.Min(width, height) * 0.16, 25, 75);
        var panel = new Rect(frame, frame, Math.Max(1, width - 2 * frame), Math.Max(1, height - 2 * frame));
        var dark = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
        var darkSoft = new SolidColorBrush(Color.FromArgb(55, 0, 0, 0));
        var light = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));

        switch (style)
        {
            case FrontStyle.Shaker:
                // Recessed panel: shadow along the top/left inner edge, highlight bottom/right.
                group.Children.Add(new GeometryDrawing(darkSoft, null, new RectangleGeometry(panel)));
                AddBevel(group, panel, 6, dark, light);
                break;
            case FrontStyle.RaisedPanel:
                double bevel = Math.Clamp(Math.Min(panel.Width, panel.Height) * 0.12, 12, 40);
                AddBevel(group, panel, 5, dark, light);
                var raised = new Rect(panel.X + bevel, panel.Y + bevel, Math.Max(1, panel.Width - 2 * bevel), Math.Max(1, panel.Height - 2 * bevel));
                AddSlopes(group, panel, raised, light, dark);
                break;
            case FrontStyle.Glass:
                AddBevel(group, panel, 6, dark, light);
                var glass = new LinearGradientBrush(Color.FromArgb(235, 150, 185, 200), Color.FromArgb(235, 205, 228, 236), 45);
                group.Children.Add(new GeometryDrawing(glass, null, new RectangleGeometry(panel)));
                break;
        }

        var brush = new DrawingBrush(group) { Stretch = Stretch.Fill, Opacity = material.Opacity };
        brush.Freeze();
        return brush;
    }

    /// <summary>Thin shadow/highlight lines around a rectangle (light from top-left).</summary>
    private static void AddBevel(DrawingGroup group, Rect r, double w, Brush shadow, Brush highlight)
    {
        group.Children.Add(new GeometryDrawing(shadow, null, new RectangleGeometry(new Rect(r.X, r.Y, r.Width, w))));
        group.Children.Add(new GeometryDrawing(shadow, null, new RectangleGeometry(new Rect(r.X, r.Y, w, r.Height))));
        group.Children.Add(new GeometryDrawing(highlight, null, new RectangleGeometry(new Rect(r.X, r.Bottom - w, r.Width, w))));
        group.Children.Add(new GeometryDrawing(highlight, null, new RectangleGeometry(new Rect(r.Right - w, r.Y, w, r.Height))));
    }

    /// <summary>The four sloped bevels of a raised panel between the outer and inner rectangle.</summary>
    private static void AddSlopes(DrawingGroup group, Rect outer, Rect inner, Brush lit, Brush shaded)
    {
        Geometry Quad(Point a, Point b, Point c, Point d) => new PathGeometry([new PathFigure(a, [new PolyLineSegment([b, c, d], true)], true)]);
        group.Children.Add(new GeometryDrawing(lit, null, Quad(outer.TopLeft, outer.TopRight, inner.TopRight, inner.TopLeft)));
        group.Children.Add(new GeometryDrawing(lit, null, Quad(outer.TopLeft, inner.TopLeft, inner.BottomLeft, outer.BottomLeft)));
        group.Children.Add(new GeometryDrawing(shaded, null, Quad(outer.BottomLeft, inner.BottomLeft, inner.BottomRight, outer.BottomRight)));
        group.Children.Add(new GeometryDrawing(shaded, null, Quad(outer.TopRight, outer.BottomRight, inner.BottomRight, inner.TopRight)));
    }

    public static Color ParseColor(string? value, Color? fallback = null)
    {
        try
        {
            return value is null ? fallback ?? Colors.LightGray : (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            return fallback ?? Colors.LightGray;
        }
    }

    private static BitmapSource? Bitmap(BoardMaterial material)
    {
        string key = $"{material.Texture}|{material.Color}|{material.ImagePath}";
        if (Bitmaps.TryGetValue(key, out var cached))
            return cached;

        var color = ParseColor(material.Color);
        BitmapSource? bitmap = material.Texture switch
        {
            MaterialTexture.Wood => Generate(color, Wood, Seed(material.Id)),
            MaterialTexture.Stone => Generate(color, Stone, Seed(material.Id)),
            MaterialTexture.Image => LoadImage(material.ImagePath),
            _ => null,
        };
        Bitmaps[key] = bitmap;
        return bitmap;
    }

    private static BitmapSource? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1024;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    // ---- procedural textures (seamless: noise is periodic over the image) -----------------

    private static int Seed(string id) => id.Aggregate(17, (h, c) => h * 31 + c);

    private static BitmapSource Generate(Color baseColor, Func<double, double, int, double> shade, int seed)
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double f = shade(x, y, seed);
                int i = (y * Size + x) * 4;
                pixels[i] = (byte)Math.Clamp(baseColor.B * f, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(baseColor.G * f, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(baseColor.R * f, 0, 255);
                pixels[i + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Grain runs along X: wavy growth rings plus fine pores.</summary>
    private static double Wood(double x, double y, int seed)
    {
        double u = x / Size, v = y / Size;
        double warp = Fbm(u, v, 3, 3, 6, seed);
        double rings = Math.Sin((v * 18 + warp * 3.2) * Math.PI * 2); // 18 whole rings: seamless in v
        double pores = Fbm(u, v, 2, 64, 256, seed + 7);
        double streaks = Fbm(u, v, 4, 4, 48, seed + 3);
        return 0.86 + 0.07 * rings + 0.10 * (streaks - 0.5) - 0.08 * Math.Max(0, pores - 0.62);
    }

    private static double Stone(double x, double y, int seed)
    {
        double u = x / Size, v = y / Size;
        double cloud = Fbm(u, v, 4, 4, 4, seed);
        double grain = Fbm(u, v, 1, 96, 96, seed + 5);
        double speck = grain > 0.80 ? -0.45 : grain < 0.12 ? 0.25 : 0;
        return 0.88 + 0.22 * (cloud - 0.5) + speck;
    }

    /// <summary>
    /// Fractal value noise over the unit square (u, v in 0..1), with <paramref name="cellsX"/> by
    /// <paramref name="cellsY"/> lattice cells in the first octave; periodic, so the image tiles seamlessly.
    /// </summary>
    private static double Fbm(double u, double v, int octaves, int cellsX, int cellsY, int seed)
    {
        double sum = 0, amplitude = 0.5, total = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += amplitude * ValueNoise(u * cellsX, v * cellsY, cellsX, cellsY, seed + o * 101);
            total += amplitude;
            cellsX *= 2;
            cellsY *= 2;
            amplitude *= 0.5;
        }
        return sum / total;
    }

    private static double ValueNoise(double x, double y, int periodX, int periodY, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        double a = Hash(x0, y0, periodX, periodY, seed), b = Hash(x0 + 1, y0, periodX, periodY, seed);
        double c = Hash(x0, y0 + 1, periodX, periodY, seed), d = Hash(x0 + 1, y0 + 1, periodX, periodY, seed);
        return (a + (b - a) * sx) * (1 - sy) + (c + (d - c) * sx) * sy;
    }

    private static double Hash(int x, int y, int periodX, int periodY, int seed)
    {
        x = ((x % periodX) + periodX) % periodX;
        y = ((y % periodY) + periodY) % periodY;
        uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0;
    }
}
