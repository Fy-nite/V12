namespace V12.Rendering;

/// <summary>
/// Backend-independent renderer sink. V12 owns the simulation and submits one
/// immutable <see cref="RenderFrameLease"/> per frame; the renderer owns GPU
/// realization and presentation.
/// </summary>
public interface IRenderer : IDisposable
{
    /// <summary>
    /// Accepts a completed frame. Ownership transfers only on a successful return;
    /// on a contract failure the caller retains the lease and must release it.
    /// </summary>
    void Submit(RenderFrameLease frame);

    RendererCapabilities Capabilities { get; }

    // Resource calls are valid only for the current device epoch.
    MeshHandle CreateMesh(in MeshData data);
    void UpdateMesh(MeshHandle mesh, in MeshData data);
    void DestroyMesh(MeshHandle mesh);

    TextureHandle CreateTexture(in TextureData data);
    void UpdateTexture(TextureHandle texture, in TextureData data);
    void DestroyTexture(TextureHandle texture);

    ShaderHandle CreateShader(in ShaderProgram program);
    void DestroyShader(ShaderHandle shader);

    MaterialHandle CreateMaterial(ShaderHandle shader, in MaterialDesc desc);
    void UpdateMaterial(MaterialHandle material, in MaterialDesc desc);
    void DestroyMaterial(MaterialHandle material);

    RenderTargetHandle CreateRenderTarget(in RenderTargetDesc desc);
    void ResizeRenderTarget(RenderTargetHandle target, int width, int height);
    void DestroyRenderTarget(RenderTargetHandle target);
    TextureHandle GetRenderTargetTexture(RenderTargetHandle target);

    ValueTask<ReadbackResult> RequestReadbackAsync(
        RenderTargetHandle? target,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Device lifecycle is a control-plane contract, separate from the frame feed.
/// On <see cref="RendererDeviceEventKind.Lost"/> all handles are invalid; on
/// <see cref="RendererDeviceEventKind.Ready"/> V12 recreates resources under the new epoch.
/// </summary>
public interface IRendererLifecycle
{
    event Action<RendererDeviceEvent>? DeviceEvent;
    DeviceEpoch CurrentEpoch { get; }
}

public enum RendererDeviceEventKind
{
    Lost,
    Recovering,
    Ready,
    PermanentFailure,
}

public readonly record struct RendererDeviceEvent(
    RendererDeviceEventKind Kind,
    DeviceEpoch Epoch,
    string? Detail = null);

/// <summary>
/// Optional presentation/windowing contract. It owns surfaces and reports viewport
/// and window events; it is deliberately not part of <see cref="IRenderer"/>.
/// </summary>
public interface IPresentationHost : IDisposable
{
    ViewportInfo GetViewport();
    IReadOnlyList<ViewportInfo> GetViewports();
    event Action<WindowEvent>? WindowEvent;
}

[Flags]
public enum XrPresentationFeatures
{
    None = 0,
    LateLatch = 1 << 0,
    DepthReprojection = 1 << 1,
    CachedFramePresentation = 1 << 2,
}

/// <summary>
/// Optional XR presentation capability surface. The renderer/platform owns
/// session/tracking/compositor state; this only advertises what it can do.
/// </summary>
public interface IXrPresentation
{
    XrPresentationFeatures Features { get; }
}

/// <summary>
/// Host-owned mouse capture. Games use this to lock the pointer for relative
/// look and release it for menus. The host decides what capture means on its
/// platform (OS mouse mode, cursor visibility, focus), and gameplay never talks
/// to a windowing API directly.
///
/// Resolve it from the service registry (<c>GameRoot.MouseCapture</c> in the
/// engine, <c>Registry.Resolve&lt;IMouseCapture&gt;()</c> in a gamepak). It is
/// optional: a headless or web host can leave it unregistered, so always
/// null-check.
/// </summary>
public interface IMouseCapture
{
    /// <summary>True while the host has locked the pointer.</summary>
    bool IsCaptured { get; }

    /// <summary>False when the platform cannot capture (headless, test host).</summary>
    bool IsSupported { get; }

    /// <summary>Raised after the capture state changes, with the new value.</summary>
    event Action<bool>? CaptureChanged;

    /// <summary>Lock the pointer. No-op when already captured.</summary>
    void Capture();

    /// <summary>Release the pointer. No-op when already released.</summary>
    void Release();

    /// <summary>Set the state explicitly; equivalent to Capture/Release.</summary>
    void SetCaptured(bool captured);
}
