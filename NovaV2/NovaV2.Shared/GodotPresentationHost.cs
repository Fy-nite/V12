using System.Numerics;
using Godot;
using V12.Rendering;
using RenderWindowEvent = V12.Rendering.WindowEvent;

namespace NovaV2.Rendering;

/// <summary>
/// Minimal Godot window/viewport bridge for <see cref="IPresentationHost"/>.
/// It exposes the main window size and forwards resize, focus and close events.
/// Input motion/buttons can be forwarded by the host through <see cref="ReportMouseMove"/>
/// and <see cref="ReportMouseButton"/>.
/// </summary>
public sealed class GodotPresentationHost : IPresentationHost
{
    private readonly Window _window;
    private bool _disposed;

    public GodotPresentationHost(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _window.SizeChanged += OnSizeChanged;
        _window.FocusEntered += OnFocusEntered;
        _window.FocusExited += OnFocusExited;
        _window.CloseRequested += OnCloseRequested;
    }

    public event Action<RenderWindowEvent>? WindowEvent;

    public ViewportInfo GetViewport()
    {
        if (_disposed || !GodotObject.IsInstanceValid(_window))
            return new ViewportInfo(1, 0f, 0f, 0f, 0f, false, Matrix4x4.Identity, Matrix4x4.Identity);

        var size = _window.Size;

        bool hasCamera = false;
        Matrix4x4 view = Matrix4x4.Identity;
        Matrix4x4 projection = Matrix4x4.Identity;

        var camera = _window.GetCamera3D();
        if (camera is not null && GodotObject.IsInstanceValid(camera))
        {
            hasCamera = true;
            view = ViewFromGodot(camera.GlobalTransform);
            projection = ProjectionFromGodot(camera.GetCameraProjection());
        }

        return new ViewportInfo(
            1,
            0f,
            0f,
            size.X,
            size.Y,
            hasCamera,
            view,
            projection);
    }

    public IReadOnlyList<ViewportInfo> GetViewports() => new[] { GetViewport() };

    public void ReportMouseMove(float x, float y) =>
        WindowEvent?.Invoke(new RenderWindowEvent(WindowEventKind.MouseMove, X: x, Y: y));

    public void ReportMouseButton(int button, bool pressed) =>
        WindowEvent?.Invoke(new RenderWindowEvent(
            WindowEventKind.MouseButton,
            Button: button,
            Pressed: pressed));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (GodotObject.IsInstanceValid(_window))
        {
            _window.SizeChanged -= OnSizeChanged;
            _window.FocusEntered -= OnFocusEntered;
            _window.FocusExited -= OnFocusExited;
            _window.CloseRequested -= OnCloseRequested;
        }
    }

    private void OnSizeChanged()
    {
        var size = _window.Size;
        WindowEvent?.Invoke(new RenderWindowEvent(WindowEventKind.Resize, size.X, size.Y));
    }

    private void OnFocusEntered() =>
        WindowEvent?.Invoke(new RenderWindowEvent(WindowEventKind.FocusGained));

    private void OnFocusExited() =>
        WindowEvent?.Invoke(new RenderWindowEvent(WindowEventKind.FocusLost));

    private void OnCloseRequested() =>
        WindowEvent?.Invoke(new RenderWindowEvent(WindowEventKind.Close));

    private static Matrix4x4 ViewFromGodot(Transform3D worldFromLocal)
    {
        var t = worldFromLocal.Inverse();
        return new Matrix4x4(
            t.Basis.X.X, t.Basis.X.Y, t.Basis.X.Z, 0f,
            t.Basis.Y.X, t.Basis.Y.Y, t.Basis.Y.Z, 0f,
            t.Basis.Z.X, t.Basis.Z.Y, t.Basis.Z.Z, 0f,
            t.Origin.X, t.Origin.Y, t.Origin.Z, 1f);
    }

    private static Matrix4x4 ProjectionFromGodot(Projection p) => new(
        p.X.X, p.X.Y, p.X.Z, p.X.W,
        p.Y.X, p.Y.Y, p.Y.Z, p.Y.W,
        p.Z.X, p.Z.Y, p.Z.Z, p.Z.W,
        p.W.X, p.W.Y, p.W.Z, p.W.W);
}
