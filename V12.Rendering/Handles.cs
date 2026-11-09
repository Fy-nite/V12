namespace V12.Rendering;

/// <summary>
/// Device generation. A renderer replacement or GPU-device recovery bumps this;
/// handles issued under an older epoch are invalid.
/// </summary>
public readonly record struct DeviceEpoch(ulong Value)
{
    public static readonly DeviceEpoch Initial = new(1);

    public override string ToString() => $"epoch {Value}";
}

/// <summary>
/// Common resource identity. A backend must validate the type, device epoch,
/// id and generation before touching a resource.
/// </summary>
public readonly record struct ResourceHandle(uint Id, uint Generation, DeviceEpoch Epoch)
{
    public bool IsValid => Id != 0;

    public override string ToString() => $"#{Id}:{Generation}@{Epoch.Value}";
}

public readonly record struct MeshHandle(ResourceHandle Value)
{
    public bool IsValid => Value.IsValid;
}

public readonly record struct TextureHandle(ResourceHandle Value)
{
    public bool IsValid => Value.IsValid;
}

public readonly record struct ShaderHandle(ResourceHandle Value)
{
    public bool IsValid => Value.IsValid;
}

public readonly record struct MaterialHandle(ResourceHandle Value)
{
    public bool IsValid => Value.IsValid;
}

public readonly record struct RenderTargetHandle(ResourceHandle Value)
{
    public bool IsValid => Value.IsValid;
}

public enum ResourceKind
{
    Mesh,
    Texture,
    Shader,
    Material,
    RenderTarget,
}

/// <summary>
/// Raised when a renderer observes a contract violation: a stale handle, a
/// broken resource dependency, a frame from another epoch, or a lifecycle
/// violation. Renderers must reject the operation deterministically.
/// </summary>
public sealed class RendererContractException : Exception
{
    public RendererContractException(string message) : base(message) { }
    public RendererContractException(string message, Exception inner) : base(message, inner) { }
}
