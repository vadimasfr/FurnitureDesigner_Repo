using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using FurnitureDesigner.App.ViewModels;
using FurnitureDesigner.App.Viewport;
using FurnitureDesigner.Core.Models;

namespace FurnitureDesigner.App;

public partial class MainWindow : Window
{
    private const double MeasureSnap = 40; // mm: measure points snap to a board corner this close

    private readonly MainViewModel _vm = new();
    private readonly CameraController _camera = new();
    private readonly SceneBuilder _scene = new();
    private readonly ModelVisual3D _sceneVisual = new();
    private bool _showEdges = true;
    private bool _showDimensions = true;
    private string _viewName = "3D";

    // Mouse interaction state.
    private enum MouseMode { None, Orbit, Pan, Drag }
    private MouseMode _mode;
    private Point _lastMouse;
    private Point3D _dragStart;
    private Vector3D _dragNormal;
    private bool _dragVertical;
    private Vector3D _dragApplied;
    private bool _dragUndoPushed;

    // Measure tool: first and second point.
    private Point3D? _measureA;
    private Point3D? _measureB;

    // Drag and drop from the catalog.
    private Point? _catalogDragStart;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Viewport.Children.Add(_sceneVisual);

        _vm.Editor.ProjectChanged += (_, _) => RebuildScene();
        _vm.Editor.SelectionChanged += (_, _) => RebuildScene();
        _vm.ChatEntries.CollectionChanged += (_, _) =>
        {
            if (_vm.ChatEntries.Count > 0)
                ChatList.ScrollIntoView(_vm.ChatEntries[^1]);
        };

        var room = _vm.Editor.Project.Room;
        _camera.Frame(room.Width, room.Depth, room.Height);
        ApplyCamera();
        RebuildScene();

        if (Environment.GetCommandLineArgs().Contains("--sample"))
            _vm.LoadSample();
    }

    private void OnLoadSample(object sender, RoutedEventArgs e) => _vm.LoadSample();

    // ---- scene ---------------------------------------------------------------------------

    private void RebuildScene()
    {
        _sceneVisual.Content = _scene.Build(_vm.Editor.Project, _vm.Editor.Selection, _showEdges);
        UpdateOverlay();
    }

    private void ApplyCamera()
    {
        Viewport.Camera = _camera.Camera;
        CameraStatus.Text = _camera.Description;
        ViewName.Text = _viewName;
        OrthoMenu.IsChecked = _camera.Orthographic;
        UpdateOverlay();
    }

    private void SetView(string name, double yaw, double pitch, bool orthographic)
    {
        _viewName = name;
        _camera.SetView(yaw, pitch, orthographic);
        ApplyCamera();
    }

    private void OnView3D(object sender, RoutedEventArgs e) => SetView("3D", 30, 25, false);
    private void OnViewFront(object sender, RoutedEventArgs e) => SetView("Front", 0, 0, true);
    private void OnViewTop(object sender, RoutedEventArgs e) => SetView("Top", 0, 89.5, true);
    private void OnViewLeft(object sender, RoutedEventArgs e) => SetView("Left", -90, 0, true);
    private void OnViewRight(object sender, RoutedEventArgs e) => SetView("Right", 90, 0, true);

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        _camera.Zoom(1);
        ApplyCamera();
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        _camera.Zoom(-1);
        ApplyCamera();
    }

    private void OnToggleOrtho(object sender, RoutedEventArgs e)
    {
        _camera.Orthographic = OrthoMenu.IsChecked;
        _camera.Update();
        ApplyCamera();
    }

    private void OnToggleEdges(object sender, RoutedEventArgs e)
    {
        _showEdges = EdgesMenu.IsChecked;
        RebuildScene();
    }

    private void OnToggleDimensions(object sender, RoutedEventArgs e)
    {
        _showDimensions = DimensionsMenu.IsChecked;
        UpdateOverlay();
    }

    private void OnZoomToRoom(object sender, RoutedEventArgs e)
    {
        var room = _vm.Editor.Project.Room;
        _camera.Frame(room.Width, room.Depth, room.Height);
        ApplyCamera();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverlay();

    // ---- 2D overlay: selection dimensions and measure line -------------------------------

    private void UpdateOverlay()
    {
        Overlay.Children.Clear();
        if (_showDimensions && Box3.Of(_vm.Editor.SelectedBoards) is { } b)
        {
            // Width along the bottom front edge, height up the front left edge, depth along the bottom left edge.
            AddDimension(new(b.MinX, b.MinY, b.MaxZ), new(b.MaxX, b.MinY, b.MaxZ), b.SizeX, Brushes.DarkOrange);
            AddDimension(new(b.MinX, b.MinY, b.MaxZ), new(b.MinX, b.MaxY, b.MaxZ), b.SizeY, Brushes.DarkOrange);
            AddDimension(new(b.MinX, b.MinY, b.MinZ), new(b.MinX, b.MinY, b.MaxZ), b.SizeZ, Brushes.DarkOrange);
        }
        if (_measureA is { } a)
        {
            var end = _measureB ?? a;
            AddDimension(a, end, (end - a).Length, Brushes.RoyalBlue, showDot: true);
        }
    }

    private void AddDimension(Point3D from, Point3D to, double millimetres, Brush brush, bool showDot = false)
    {
        if (Project(from) is not { } p1 || Project(to) is not { } p2)
            return;
        Overlay.Children.Add(new Line { X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y, Stroke = brush, StrokeThickness = 1.5 });
        if (showDot)
        {
            foreach (var p in new[] { p1, p2 })
            {
                var dot = new Ellipse { Width = 7, Height = 7, Fill = brush };
                Canvas.SetLeft(dot, p.X - 3.5);
                Canvas.SetTop(dot, p.Y - 3.5);
                Overlay.Children.Add(dot);
            }
        }
        if (millimetres < 1)
            return;

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 1),
            Child = new TextBlock
            {
                Text = millimetres.ToString("0.#", CultureInfo.CurrentCulture),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush,
            },
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, (p1.X + p2.X) / 2 - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, (p1.Y + p2.Y) / 2 - label.DesiredSize.Height / 2);
        Overlay.Children.Add(label);
    }

    /// <summary>World point to viewport pixels, or null when it is not in front of the camera.</summary>
    private Point? Project(Point3D point)
    {
        if (Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1
            || Vector3D.DotProduct(point - _camera.Camera.Position, _camera.Forward) <= 0)
            return null;
        var transform = _sceneVisual.TransformToAncestor(Viewport);
        return transform is not null && transform.TryTransform(point, out var result)
               && double.IsFinite(result.X) && double.IsFinite(result.Y)
            ? result
            : null;
    }

    // ---- mouse ---------------------------------------------------------------------------

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        ViewportHost.Focus();
        _lastMouse = e.GetPosition(ViewportHost);

        if (e.ChangedButton == MouseButton.Middle
            || (e.ChangedButton == MouseButton.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            _mode = MouseMode.Pan;
        else if (e.ChangedButton == MouseButton.Right)
            _mode = MouseMode.Orbit;
        else if (e.ChangedButton == MouseButton.Left)
            OnLeftClick(_lastMouse);

        if (_mode != MouseMode.None)
            ViewportHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnLeftClick(Point position)
    {
        switch (_vm.ActiveTool)
        {
            case DesignTool.Select:
                BeginSelectOrDrag(position);
                break;
            case DesignTool.Paint:
                if (HitTestBoard(position) is (Guid paintId, _))
                {
                    var ids = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? _vm.Editor.GroupMembers(paintId) : [paintId];
                    _vm.PaintBoards(ids);
                }
                break;
            case DesignTool.Eyedropper:
                if (HitTestBoard(position) is (Guid pickId, _))
                    _vm.PickMaterial(pickId);
                break;
            case DesignTool.Measure:
                if (HitTestSurface(position) is { } point)
                {
                    if (_measureA is null || _measureB is not null)
                        (_measureA, _measureB) = (point, null);
                    else
                        _measureB = point;
                    UpdateOverlay();
                }
                break;
        }
    }

    private void BeginSelectOrDrag(Point position)
    {
        var editor = _vm.Editor;
        var modifiers = Keyboard.Modifiers;
        if (HitTestBoard(position) is not (Guid boardId, Point3D hitPoint))
        {
            if (!modifiers.HasFlag(ModifierKeys.Control))
                editor.ClearSelection();
            return;
        }

        bool singleBoard = !_vm.SelectWholeGroup || modifiers.HasFlag(ModifierKeys.Alt);
        var ids = singleBoard ? [boardId] : editor.GroupMembers(boardId).ToList();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            editor.ToggleSelection(ids);
            return;
        }
        if (!editor.Selection.Contains(boardId) || singleBoard)
            editor.Select(ids);

        // Drag the selection on a horizontal plane through the clicked point, or vertically with Shift.
        _mode = MouseMode.Drag;
        _dragStart = hitPoint;
        _dragApplied = new Vector3D();
        _dragUndoPushed = false;
        _dragVertical = modifiers.HasFlag(ModifierKeys.Shift);
        if (_dragVertical)
        {
            var forward = _camera.Forward;
            _dragNormal = new Vector3D(forward.X, 0, forward.Z);
            if (_dragNormal.Length < 1e-6)
                _dragNormal = new Vector3D(0, 0, 1);
            _dragNormal.Normalize();
        }
        else
        {
            _dragNormal = new Vector3D(0, 1, 0);
        }
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(ViewportHost);
        var delta = position - _lastMouse;
        _lastMouse = position;

        switch (_mode)
        {
            case MouseMode.Orbit:
                _viewName = "3D";
                _camera.Orbit(delta.X, delta.Y);
                ApplyCamera();
                break;
            case MouseMode.Pan:
                _camera.Pan(delta.X, delta.Y, ViewportHost.ActualWidth);
                ApplyCamera();
                break;
            case MouseMode.Drag:
                DragSelection(position);
                break;
            case MouseMode.None when _vm.ActiveTool == DesignTool.Measure && _measureA is not null && _measureB is null:
                // Rubber band from the first point.
                if (HitTestSurface(position) is { } p)
                {
                    _measureB = p;
                    UpdateOverlay();
                    _measureB = null;
                }
                break;
        }
    }

    private void DragSelection(Point position)
    {
        var ray = _camera.GetRay(position, ViewportHost.RenderSize);
        if (ray.IntersectPlane(_dragStart, _dragNormal) is not { } current)
            return;

        var moved = current - _dragStart;
        var target = _dragVertical
            ? new Vector3D(0, SnapValue(moved.Y), 0)
            : new Vector3D(SnapValue(moved.X), 0, SnapValue(moved.Z));
        var step = target - _dragApplied;
        if (step.Length < 1e-6)
            return;

        if (!_dragUndoPushed)
        {
            _vm.Editor.PushUndo();
            _dragUndoPushed = true;
        }
        _vm.Editor.MoveSelection(step.X, step.Y, step.Z, recordUndo: false);
        _dragApplied = target;
    }

    private double SnapValue(double value) => _vm.Snap > 0 ? Math.Round(value / _vm.Snap) * _vm.Snap : value;

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode != MouseMode.None)
            ViewportHost.ReleaseMouseCapture();
        _mode = MouseMode.None;
    }

    private void OnViewportLostCapture(object sender, MouseEventArgs e) => _mode = MouseMode.None;

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _camera.Zoom(e.Delta);
        ApplyCamera();
    }

    private (Guid BoardId, Point3D Point)? HitTestBoard(Point position)
    {
        (Guid, Point3D)? found = null;
        VisualTreeHelper.HitTest(Viewport, null, result =>
        {
            // 3D hits arrive nearest first; walls, floor and edges are not boards and are skipped.
            if (result is RayMeshGeometry3DHitTestResult hit
                && hit.ModelHit is GeometryModel3D model
                && _scene.BoardByModel.TryGetValue(model, out var id))
            {
                found = (id, hit.PointHit);
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(position));
        return found;
    }

    /// <summary>
    /// Nearest surface facing the camera (board, wall or floor) for the measure tool;
    /// on a board the point snaps to a nearby corner.
    /// </summary>
    private Point3D? HitTestSurface(Point position)
    {
        var rayDirection = _camera.GetRay(position, ViewportHost.RenderSize).Direction;
        Point3D? found = null;
        VisualTreeHelper.HitTest(Viewport, null, result =>
        {
            if (result is not RayMeshGeometry3DHitTestResult hit || hit.ModelHit is not GeometryModel3D model)
                return HitTestResultBehavior.Continue;
            var positions = hit.MeshHit.Positions;
            var normal = Vector3D.CrossProduct(positions[hit.VertexIndex2] - positions[hit.VertexIndex1],
                                               positions[hit.VertexIndex3] - positions[hit.VertexIndex1]);
            if (Vector3D.DotProduct(normal, rayDirection) > 0)
                return HitTestResultBehavior.Continue; // back face, e.g. a wall seen from outside

            var point = hit.PointHit;
            if (_scene.BoardByModel.TryGetValue(model, out var id)
                && _vm.Editor.Project.Boards.FirstOrDefault(b => b.Id == id) is { } board)
            {
                var b = board.Bounds;
                var corner = (from x in new[] { b.MinX, b.MaxX }
                              from y in new[] { b.MinY, b.MaxY }
                              from z in new[] { b.MinZ, b.MaxZ }
                              select new Point3D(x, y, z)).MinBy(c => (c - point).Length);
                if ((corner - point).Length <= MeasureSnap)
                    point = corner;
            }
            found = point;
            return HitTestResultBehavior.Stop;
        }, new PointHitTestParameters(position));
        return found;
    }

    // ---- catalog drag and drop -----------------------------------------------------------

    private void OnCatalogMouseDown(object sender, MouseButtonEventArgs e) => _catalogDragStart = e.GetPosition(CatalogList);

    private void OnCatalogMouseMove(object sender, MouseEventArgs e)
    {
        if (_catalogDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            _catalogDragStart = null;
            return;
        }
        var delta = e.GetPosition(CatalogList) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _catalogDragStart = null;
        if ((e.OriginalSource as DependencyObject) is { } source
            && ItemsControl.ContainerFromElement(CatalogList, source) is ListBoxItem { DataContext: CatalogItem item })
        {
            DragDrop.DoDragDrop(CatalogList, new DataObject(typeof(CatalogItem), item), DragDropEffects.Copy);
        }
    }

    private void OnViewportDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(CatalogItem)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnViewportDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CatalogItem)) is not CatalogItem item)
            return;
        var ray = _camera.GetRay(e.GetPosition(ViewportHost), ViewportHost.RenderSize);
        if (ray.IntersectPlane(new Point3D(0, 0, 0), new Vector3D(0, 1, 0)) is { } floor)
            _vm.InsertAt(item.Template, floor.X, floor.Z);
        ViewportHost.Focus();
    }

    private void OnCatalogDoubleClick(object sender, MouseButtonEventArgs e) => _vm.InsertTemplate(_vm.SelectedTemplate);

    // ---- palette and structure -----------------------------------------------------------

    private void OnPaletteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: BoardMaterial material })
            _vm.CurrentMaterial = material;
    }

    private void OnPaletteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.ApplyMaterialCommand.CanExecute(null))
            _vm.ApplyMaterialCommand.Execute(null);
    }

    private void OnStructureSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is StructureNode node)
            _vm.SelectStructureNode(node);
    }

    // ---- keyboard ------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox)
            return;

        var editor = _vm.Editor;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (ctrl)
        {
            e.Handled = key switch
            {
                Key.Z => Run(_vm.UndoCommand),
                Key.Y => Run(_vm.RedoCommand),
                Key.D => Run(_vm.DuplicateCommand),
                Key.A => Run(_vm.SelectAllCommand),
                Key.S => Run(_vm.SaveCommand),
                Key.O => Run(_vm.OpenCommand),
                Key.N => Run(_vm.NewCommand),
                _ => false,
            };
            return;
        }

        switch (key)
        {
            case Key.F1: OnShowHelp(this, e); e.Handled = true; return;
            case Key.F5: OnView3D(this, e); e.Handled = true; return;
            case Key.F6: OnViewFront(this, e); e.Handled = true; return;
            case Key.F7: OnViewTop(this, e); e.Handled = true; return;
            case Key.F8: OnViewLeft(this, e); e.Handled = true; return;
            case Key.Home: OnZoomToRoom(this, e); e.Handled = true; return;
        }

        // Editing keys only while the 3D view has focus, so lists and grids keep their own keys.
        if (!ViewportHost.IsKeyboardFocusWithin)
            return;

        double step = _vm.Step * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
        e.Handled = true;
        switch (key)
        {
            case Key.Delete: Run(_vm.DeleteCommand); break;
            case Key.R: Run(_vm.RotateCommand); break;
            case Key.S: _vm.ActiveTool = DesignTool.Select; break;
            case Key.P: _vm.ActiveTool = DesignTool.Paint; break;
            case Key.I: _vm.ActiveTool = DesignTool.Eyedropper; break;
            case Key.M: _vm.ActiveTool = DesignTool.Measure; break;
            case Key.Escape:
                if (_measureA is not null)
                {
                    _measureA = _measureB = null;
                    UpdateOverlay();
                }
                else
                {
                    editor.ClearSelection();
                }
                break;
            case Key.Left: editor.MoveSelection(-step, 0, 0); break;
            case Key.Right: editor.MoveSelection(step, 0, 0); break;
            case Key.Up: editor.MoveSelection(0, 0, -step); break;
            case Key.Down: editor.MoveSelection(0, 0, step); break;
            case Key.PageUp: editor.MoveSelection(0, step, 0); break;
            case Key.PageDown: editor.MoveSelection(0, -step, 0); break;
            default: e.Handled = false; break;
        }
    }

    private static bool Run(ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
        return true;
    }

    // ---- other UI ------------------------------------------------------------------------

    private void OnMaterialBeginningEdit(object? sender, DataGridBeginningEditEventArgs e) => _vm.Editor.PushUndo();

    private void OnMaterialCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
            Dispatcher.BeginInvoke(_vm.Editor.NotifyChanged); // after the value is written to the material
    }

    private void OnShowHelp(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        """
        Tools (left palette, 3D view focused)
          S  Select / move         P  Paint with the current material (Shift: whole item)
          I  Eyedropper            M  Measure (click two points; Esc clears)

        Mouse (3D view)
          Left click            select an item (Alt: single board, Ctrl: add/remove)
          Left drag             move the selection on the floor plane
          Shift + left drag     move the selection up/down
          Right drag            orbit
          Middle / Shift+right  pan
          Wheel                 zoom
          Catalog → room        drag a cabinet into the room to place it

        Keyboard (3D view focused)
          Arrows                move along X / Z by Step (Shift ×10)
          PgUp / PgDn           move up / down
          R                     rotate 90°
          Del                   delete
          Ctrl+D                duplicate
          Ctrl+Z / Ctrl+Y       undo / redo
          F5 3D · F6 front · F7 top · F8 left · Home zoom to room
        """, "Controls");

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) =>
        e.Cancel = !_vm.ConfirmDiscardChanges();
}
