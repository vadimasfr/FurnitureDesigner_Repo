using System.Windows.Media.Media3D;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.App.Viewport;

internal static class MeshFactory
{
    /// <summary>Adds a quad (corners in loop order) whose front face points along <paramref name="normal"/>.</summary>
    public static void AddQuad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d, Vector3D normal)
    {
        // WPF front faces are counter-clockwise; flip the loop if it winds the wrong way.
        if (Vector3D.DotProduct(Vector3D.CrossProduct(b - a, c - a), normal) < 0)
            (b, d) = (d, b);

        int i = mesh.Positions.Count;
        mesh.Positions.Add(a);
        mesh.Positions.Add(b);
        mesh.Positions.Add(c);
        mesh.Positions.Add(d);
        for (int n = 0; n < 4; n++)
            mesh.Normals.Add(normal);
        mesh.TriangleIndices.Add(i);
        mesh.TriangleIndices.Add(i + 1);
        mesh.TriangleIndices.Add(i + 2);
        mesh.TriangleIndices.Add(i);
        mesh.TriangleIndices.Add(i + 2);
        mesh.TriangleIndices.Add(i + 3);
    }

    public static void AddBox(MeshGeometry3D mesh, double x0, double y0, double z0, double x1, double y1, double z1)
    {
        AddQuad(mesh, new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), new(0, 0, 1));
        AddQuad(mesh, new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(x1, y0, z0), new(0, 0, -1));
        AddQuad(mesh, new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z1), new(1, 0, 0));
        AddQuad(mesh, new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), new(-1, 0, 0));
        AddQuad(mesh, new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(0, 1, 0));
        AddQuad(mesh, new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), new(0, -1, 0));
    }

    /// <summary>
    /// Adds one face of a box with texture coordinates. Axis 0/1/2 = X/Y/Z; <paramref name="sign"/> picks the min or max side.
    /// Tiled mode: coordinates in tiles of <paramref name="tile"/> mm with u along <paramref name="grainAxis"/> where it lies in the face.
    /// Relative mode (<paramref name="tile"/> = 0): 0..1 across the face, u horizontal and v = 0 at the top.
    /// </summary>
    public static void AddFace(MeshGeometry3D mesh, Box3 box, int axis, int sign, int grainAxis, double tile, double offset)
    {
        double[] min = [box.MinX, box.MinY, box.MinZ];
        double[] max = [box.MaxX, box.MaxY, box.MaxZ];
        int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
        int uAxis, vAxis;
        if (tile <= 0 && (a1 == 1 || a2 == 1))
            (uAxis, vAxis) = a1 == 1 ? (a2, a1) : (a1, a2);
        else if (grainAxis == a1 || grainAxis == a2)
            (uAxis, vAxis) = grainAxis == a1 ? (a1, a2) : (a2, a1);
        else
            (uAxis, vAxis) = max[a1] - min[a1] >= max[a2] - min[a2] ? (a1, a2) : (a2, a1);

        var points = new Point3D[4];
        var uvs = new System.Windows.Point[4];
        (int U, int V)[] corners = [(0, 0), (1, 0), (1, 1), (0, 1)];
        for (int k = 0; k < 4; k++)
        {
            var p = new double[3];
            p[axis] = sign > 0 ? max[axis] : min[axis];
            p[uAxis] = corners[k].U == 0 ? min[uAxis] : max[uAxis];
            p[vAxis] = corners[k].V == 0 ? min[vAxis] : max[vAxis];
            points[k] = new Point3D(p[0], p[1], p[2]);
            uvs[k] = tile > 0
                ? new System.Windows.Point((p[uAxis] + offset) / tile, (p[vAxis] + offset * 0.37) / tile)
                : new System.Windows.Point(
                    (p[uAxis] - min[uAxis]) / Math.Max(1e-9, max[uAxis] - min[uAxis]),
                    vAxis == 1 ? (max[1] - p[1]) / Math.Max(1e-9, max[1] - min[1]) : (p[vAxis] - min[vAxis]) / Math.Max(1e-9, max[vAxis] - min[vAxis]));
        }

        var normal = new Vector3D(axis == 0 ? sign : 0, axis == 1 ? sign : 0, axis == 2 ? sign : 0);
        if (Vector3D.DotProduct(Vector3D.CrossProduct(points[1] - points[0], points[2] - points[0]), normal) < 0)
        {
            (points[1], points[3]) = (points[3], points[1]);
            (uvs[1], uvs[3]) = (uvs[3], uvs[1]);
        }

        int i = mesh.Positions.Count;
        for (int k = 0; k < 4; k++)
        {
            mesh.Positions.Add(points[k]);
            mesh.Normals.Add(normal);
            mesh.TextureCoordinates.Add(uvs[k]);
        }
        foreach (int t in (int[])[0, 1, 2, 0, 2, 3])
            mesh.TriangleIndices.Add(i + t);
    }

    public static MeshGeometry3D Box(Box3 box)
    {
        var mesh = new MeshGeometry3D();
        AddBox(mesh, box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ);
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Adds the 12 edges of a box as thin bars of the given size (WPF 3D has no line primitive).</summary>
    public static void AddEdges(MeshGeometry3D mesh, Box3 b, double size)
    {
        double h = size / 2;
        foreach (var y in new[] { b.MinY, b.MaxY })
        foreach (var z in new[] { b.MinZ, b.MaxZ })
            AddBox(mesh, b.MinX - h, y - h, z - h, b.MaxX + h, y + h, z + h);
        foreach (var x in new[] { b.MinX, b.MaxX })
        foreach (var z in new[] { b.MinZ, b.MaxZ })
            AddBox(mesh, x - h, b.MinY - h, z - h, x + h, b.MaxY + h, z + h);
        foreach (var x in new[] { b.MinX, b.MaxX })
        foreach (var y in new[] { b.MinY, b.MaxY })
            AddBox(mesh, x - h, y - h, b.MinZ - h, x + h, y + h, b.MaxZ + h);
    }
}
