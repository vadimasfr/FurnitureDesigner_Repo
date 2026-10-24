using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.App.Viewport;

/// <summary>Renders boards off-screen into a small image (catalog thumbnails).</summary>
internal static class ThumbnailRenderer
{
    public static BitmapSource Render(IReadOnlyList<Board> boards, IReadOnlyList<BoardMaterial> materials, int width = 120, int height = 104)
    {
        var project = new FurnitureProject { Materials = [.. materials], Boards = [.. boards] };
        var model = new SceneBuilder().Build(project, [], showEdges: true, showRoom: false);

        var box = Box3.Of(boards) ?? new Box3(0, 0, 0, 1, 1, 1);
        var target = new Point3D((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2, (box.MinZ + box.MaxZ) / 2);
        double size = Math.Max(box.SizeX, Math.Max(box.SizeY, box.SizeZ));
        var direction = new Vector3D(0.55, 0.38, 1);
        direction.Normalize();

        var viewport = new Viewport3D
        {
            Width = width,
            Height = height,
            Camera = new PerspectiveCamera(target + direction * size * 2.3, -direction, new Vector3D(0, 1, 0), 32),
        };
        viewport.Children.Add(new ModelVisual3D { Content = model });
        viewport.Measure(new Size(width, height));
        viewport.Arrange(new Rect(0, 0, width, height));
        viewport.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(viewport);
        bitmap.Freeze();
        return bitmap;
    }
}
