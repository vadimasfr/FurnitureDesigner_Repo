using System.Windows;
using System.Windows.Media.Media3D;

namespace FurnitureDesigner.App.Viewport;

internal readonly record struct Ray3(Point3D Origin, Vector3D Direction)
{
    public Point3D? IntersectPlane(Point3D planePoint, Vector3D planeNormal)
    {
        double denominator = Vector3D.DotProduct(Direction, planeNormal);
        if (Math.Abs(denominator) < 1e-9)
            return null;
        double t = Vector3D.DotProduct(planePoint - Origin, planeNormal) / denominator;
        return t < 0 ? null : Origin + Direction * t;
    }
}

/// <summary>
/// Orbit camera around a target point. Yaw 0 / pitch 0 looks at the room from the front (from +Z);
/// orthographic mode gives the 2D-like projections (front, top, sides).
/// </summary>
internal sealed class CameraController
{
    private const double FieldOfView = 45;
    private const double OrthographicStandOff = 30_000;

    private readonly PerspectiveCamera _perspective = new() { FieldOfView = FieldOfView, NearPlaneDistance = 50, FarPlaneDistance = 200_000 };
    private readonly OrthographicCamera _orthographic = new() { NearPlaneDistance = 10, FarPlaneDistance = 200_000 };

    public Point3D Target { get; set; }
    public double Yaw { get; set; } = 30;
    public double Pitch { get; set; } = 25;
    public double Distance { get; set; } = 7000;
    public bool Orthographic { get; set; }

    public ProjectionCamera Camera => Orthographic ? _orthographic : _perspective;

    /// <summary>Width of the visible area at the target distance, in millimetres.</summary>
    public double VisibleWidth => 2 * Distance * Math.Tan(FieldOfView * Math.PI / 360);

    public string Description => $"{(Orthographic ? "Orthographic" : "Perspective")}  yaw {Yaw:0}°  pitch {Pitch:0}°";

    private Vector3D OffsetDirection
    {
        get
        {
            double yaw = Yaw * Math.PI / 180, pitch = Pitch * Math.PI / 180;
            return new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw));
        }
    }

    public Vector3D Forward => -OffsetDirection;

    public void Update()
    {
        Pitch = Math.Clamp(Pitch, -89.5, 89.5);
        Distance = Math.Clamp(Distance, 300, 60_000);
        var offset = OffsetDirection;
        var up = new Vector3D(0, 1, 0);

        _perspective.Position = Target + offset * Distance;
        _perspective.LookDirection = -offset;
        _perspective.UpDirection = up;

        _orthographic.Position = Target + offset * OrthographicStandOff;
        _orthographic.LookDirection = -offset;
        _orthographic.UpDirection = up;
        _orthographic.Width = VisibleWidth;
    }

    public void SetView(double yaw, double pitch, bool orthographic)
    {
        Yaw = yaw;
        Pitch = pitch;
        Orthographic = orthographic;
        Update();
    }

    /// <summary>Centres the camera on the room.</summary>
    public void Frame(double width, double depth, double height)
    {
        Target = new Point3D(width / 2, height * 0.35, depth / 2);
        Distance = Math.Max(width, Math.Max(depth, height)) * 1.7;
        Update();
    }

    public void Orbit(double dx, double dy)
    {
        Yaw -= dx * 0.4;
        Pitch += dy * 0.4;
        Update();
    }

    public void Pan(double dx, double dy, double viewportWidth)
    {
        var (right, up) = Basis();
        double scale = VisibleWidth / Math.Max(1, viewportWidth);
        Target += -right * dx * scale + up * dy * scale;
        Update();
    }

    public void Zoom(int wheelDelta)
    {
        Distance *= wheelDelta > 0 ? 0.88 : 1 / 0.88;
        Update();
    }

    /// <summary>Ray through a point of the viewport, in world coordinates.</summary>
    public Ray3 GetRay(Point point, Size viewport)
    {
        var (right, up) = Basis();
        double nx = 2 * point.X / viewport.Width - 1;
        double ny = 1 - 2 * point.Y / viewport.Height;
        double aspect = viewport.Width / viewport.Height;

        if (Orthographic)
        {
            double half = _orthographic.Width / 2;
            var origin = _orthographic.Position + right * nx * half + up * ny * half / aspect;
            return new Ray3(origin, Forward);
        }

        double tan = Math.Tan(FieldOfView * Math.PI / 360); // WPF field of view is horizontal
        var direction = Forward + right * nx * tan + up * ny * tan / aspect;
        direction.Normalize();
        return new Ray3(_perspective.Position, direction);
    }

    private (Vector3D Right, Vector3D Up) Basis()
    {
        var forward = Forward;
        var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0));
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        up.Normalize();
        return (right, up);
    }
}
