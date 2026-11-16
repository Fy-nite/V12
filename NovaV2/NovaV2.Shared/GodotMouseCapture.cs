using Godot;
using V12.Rendering;

namespace NovaV2.Hosting;

/// <summary>
/// Godot implementation of <see cref="IMouseCapture"/>. It owns the window's
/// mouse mode so gameplay never touches <see cref="Godot.Input"/> directly, and
/// it keeps the runtime's look input in step by raising
/// <see cref="IMouseCapture.CaptureChanged"/>.
/// </summary>
public sealed class GodotMouseCapture : IMouseCapture
{
    private bool _captured;
    private bool _disposed;

    public bool IsCaptured => _captured;

    public bool IsSupported => true;

    public event Action<bool>? CaptureChanged;

    public void Capture() => SetCaptured(true);

    public void Release() => SetCaptured(false);

    public void SetCaptured(bool captured)
    {
        if (_disposed || _captured == captured)
            return;

        _captured = captured;
        Godot.Input.MouseMode = captured
            ? Godot.Input.MouseModeEnum.Captured
            : Godot.Input.MouseModeEnum.Visible;

        CaptureChanged?.Invoke(captured);
    }

    /// <summary>Restores the cursor when the runtime shuts down.</summary>
    internal void Shutdown()
    {
        if (_disposed)
            return;

        SetCaptured(false);
        _disposed = true;
    }
}
