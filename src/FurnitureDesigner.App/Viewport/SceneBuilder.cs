using System.Windows.Media;
using System.Windows.Media.Media3D;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.App.Viewport;

/// <summary>Turns a project into a WPF 3D model and remembers which model belongs to which board (for picking).</summary>
internal sealed class SceneBuilder
{
    private const double EdgeSize = 1.5;
    private const double GridStep = 500;
    private static readonly Color SelectionColor = Color.FromRgb(255, 140, 0);

    public Dictionary<GeometryModel3D, Guid> BoardByModel { get; } = [];

    public Model3DGroup Build(FurnitureProject project, IReadOnlyCollection<Guid> selection, bool showEdges, bool showRoom = true)
    {
        BoardByModel.Clear();
        var root = new Model3DGroup();
        AddLights(root);
        if (showRoom)
            AddRoom(root, project.Room);

        var materials = project.Materials.ToDictionary(m => m.Id);
        var groupCenters = project.Boards
            .Where(b => b.GroupId is not null)
            .GroupBy(b => b.GroupId!.Value)
            .ToDictionary(g => g.Key, g => Box3.Of(g)!.Value);
        var edges = new MeshGeometry3D();
        var selectedEdges = new MeshGeometry3D();

        // Transparent boards last, so opaque ones behind them are already in the depth buffer.
        foreach (var board in project.Boards.OrderBy(b => materials.GetValueOrDefault(b.MaterialId)?.Opacity < 1 ? 1 : 0))
        {
            bool selected = selection.Contains(board.Id);
            var material = materials.GetValueOrDefault(board.MaterialId) ?? new BoardMaterial { Id = board.MaterialId };
            var groupBox = board.GroupId is { } gid ? groupCenters.GetValueOrDefault(gid) : (Box3?)null;
            foreach (var model in BoardModels(board, material, selected, groupBox))
            {
                root.Children.Add(model);
                BoardByModel[model] = board.Id;
            }

            if (selected)
                MeshFactory.AddEdges(selectedEdges, board.Bounds, EdgeSize * 2);
            else if (showEdges)
                MeshFactory.AddEdges(edges, board.Bounds, EdgeSize);
        }

        root.Children.Add(Solid(edges, Color.FromRgb(45, 40, 35)));
        root.Children.Add(Solid(selectedEdges, SelectionColor));
        return root;
    }

    /// <summary>
    /// One model for the board's faces, plus a second one for the outside face of a styled door
    /// or drawer front (drawn with frame/panel shading).
    /// </summary>
    private static IEnumerable<GeometryModel3D> BoardModels(Board board, BoardMaterial material, bool selected, Box3? groupBox)
    {
        var b = board.Bounds;
        double[] sizes = [b.SizeX, b.SizeY, b.SizeZ];
        int grain = Array.IndexOf(sizes, sizes.Max());
        int thin = Array.IndexOf(sizes, sizes.Min());
        double offset = (uint)board.Id.GetHashCode() % 997 / 997.0 * material.TextureSize;

        bool styled = board.Kind is BoardKind.Door or BoardKind.DrawerFront && board.FrontStyle != FrontStyle.Flat;
        // The outside of a front is the side away from the centre of its cabinet.
        int outerSign = 1;
        if (styled && groupBox is { } g)
        {
            double[] boardCenter = [(b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2, (b.MinZ + b.MaxZ) / 2];
            double[] groupCenter = [(g.MinX + g.MaxX) / 2, (g.MinY + g.MaxY) / 2, (g.MinZ + g.MaxZ) / 2];
            outerSign = boardCenter[thin] >= groupCenter[thin] ? 1 : -1;
        }

        var surface = TextureFactory.SurfaceBrush(material);
        var mesh = new MeshGeometry3D();
        for (int axis = 0; axis < 3; axis++)
        {
            foreach (int sign in (int[])[-1, 1])
            {
                if (styled && axis == thin && sign == outerSign)
                    continue;
                MeshFactory.AddFace(mesh, b, axis, sign, grain, material.TextureSize, offset);
            }
        }
        mesh.Freeze();
        yield return Model(mesh, surface ?? new SolidColorBrush(TextureFactory.ParseColor(material.Color)) { Opacity = material.Opacity }, selected);

        if (styled)
        {
            var face = new MeshGeometry3D();
            MeshFactory.AddFace(face, b, thin, outerSign, grain, 0, 0);
            face.Freeze();
            // Face size in mm as drawn: width along the horizontal axis, height along Y
            // (a front lying flat is drawn with its longer side as width).
            (double width, double height) = thin switch
            {
                0 => (b.SizeZ, b.SizeY),
                2 => (b.SizeX, b.SizeY),
                _ => (Math.Max(b.SizeX, b.SizeZ), Math.Min(b.SizeX, b.SizeZ)),
            };
            yield return Model(face, TextureFactory.FrontBrush(material, board.FrontStyle, width, height), selected);
        }
    }

    private static GeometryModel3D Model(MeshGeometry3D mesh, Brush brush, bool selected)
    {
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(brush));
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 40));
        if (selected)
            group.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(70, 35, 0)))); // tint, texture stays visible
        group.Freeze();
        return new GeometryModel3D(mesh, group);
    }

    private static void AddLights(Model3DGroup root)
    {
        root.Children.Add(new AmbientLight(Color.FromRgb(100, 100, 100)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(195, 190, 180), new Vector3D(-0.4, -1, -0.6)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(85, 85, 95), new Vector3D(0.6, -0.3, 0.8)));
    }

    private static void AddRoom(Model3DGroup root, Room room)
    {
        double w = room.Width, d = room.Depth, h = room.Height;
        var floorColor = TextureFactory.ParseColor(room.FloorColor, Colors.Tan);
        var wallColor = TextureFactory.ParseColor(room.WallColor, Colors.WhiteSmoke);

        var floor = new MeshGeometry3D();
        MeshFactory.AddQuad(floor, new(0, 0, 0), new(0, 0, d), new(w, 0, d), new(w, 0, 0), new(0, 1, 0));
        root.Children.Add(Solid(floor, floorColor));

        // Walls face inwards and have no back material, so a wall between the camera and the room is culled ("dollhouse" view).
        var walls = new MeshGeometry3D();
        MeshFactory.AddQuad(walls, new(0, 0, 0), new(w, 0, 0), new(w, h, 0), new(0, h, 0), new(0, 0, 1));
        MeshFactory.AddQuad(walls, new(0, 0, d), new(w, 0, d), new(w, h, d), new(0, h, d), new(0, 0, -1));
        MeshFactory.AddQuad(walls, new(0, 0, 0), new(0, 0, d), new(0, h, d), new(0, h, 0), new(1, 0, 0));
        MeshFactory.AddQuad(walls, new(w, 0, 0), new(w, 0, d), new(w, h, d), new(w, h, 0), new(-1, 0, 0));
        root.Children.Add(Solid(walls, wallColor));

        // 500 mm grid on the floor and walls, as in PRO100.
        const double line = 4, lift = 1;
        var floorGrid = new MeshGeometry3D();
        for (double x = GridStep; x < w; x += GridStep)
            MeshFactory.AddQuad(floorGrid, new(x - line / 2, lift, 0), new(x - line / 2, lift, d), new(x + line / 2, lift, d), new(x + line / 2, lift, 0), new(0, 1, 0));
        for (double z = GridStep; z < d; z += GridStep)
            MeshFactory.AddQuad(floorGrid, new(0, lift, z - line / 2), new(w, lift, z - line / 2), new(w, lift, z + line / 2), new(0, lift, z + line / 2), new(0, 1, 0));
        root.Children.Add(Solid(floorGrid, Darken(floorColor, 0.85)));

        var wallGrid = new MeshGeometry3D();
        for (double y = GridStep; y < h; y += GridStep)
        {
            MeshFactory.AddQuad(wallGrid, new(0, y - line / 2, lift), new(w, y - line / 2, lift), new(w, y + line / 2, lift), new(0, y + line / 2, lift), new(0, 0, 1));
            MeshFactory.AddQuad(wallGrid, new(lift, y - line / 2, 0), new(lift, y - line / 2, d), new(lift, y + line / 2, d), new(lift, y + line / 2, 0), new(1, 0, 0));
            MeshFactory.AddQuad(wallGrid, new(w - lift, y - line / 2, 0), new(w - lift, y - line / 2, d), new(w - lift, y + line / 2, d), new(w - lift, y + line / 2, 0), new(-1, 0, 0));
        }
        for (double x = GridStep; x < w; x += GridStep)
            MeshFactory.AddQuad(wallGrid, new(x - line / 2, 0, lift), new(x + line / 2, 0, lift), new(x + line / 2, h, lift), new(x - line / 2, h, lift), new(0, 0, 1));
        for (double z = GridStep; z < d; z += GridStep)
        {
            MeshFactory.AddQuad(wallGrid, new(lift, 0, z - line / 2), new(lift, 0, z + line / 2), new(lift, h, z + line / 2), new(lift, h, z - line / 2), new(1, 0, 0));
            MeshFactory.AddQuad(wallGrid, new(w - lift, 0, z - line / 2), new(w - lift, 0, z + line / 2), new(w - lift, h, z + line / 2), new(w - lift, h, z - line / 2), new(-1, 0, 0));
        }
        root.Children.Add(Solid(wallGrid, Darken(wallColor, 0.9)));
    }

    private static GeometryModel3D Solid(MeshGeometry3D mesh, Color color)
    {
        mesh.Freeze();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var model = new GeometryModel3D(mesh, new DiffuseMaterial(brush));
        model.Freeze();
        return model;
    }

    private static Color Darken(Color c, double factor) =>
        Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
}
