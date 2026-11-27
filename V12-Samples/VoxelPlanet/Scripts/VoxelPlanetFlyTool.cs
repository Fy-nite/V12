using System;
using System.Numerics;
using V12.Components;
using V12.Components.UI;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;
using V12.Core.Interfaces.Renderer;
using V12.Voxel;
using V12.Voxel.Components;

namespace VoxelPlanet;

/// <summary>
/// Desktop free-fly camera and terrain tool for the voxel sample. The planet
/// itself is a <see cref="VoxelPlanetComponent"/> in the world file; this
/// controller only aims and edits, so the sample doubles as the smallest
/// example of driving the package from game code.
/// </summary>
[ComponentEntry("GAMEPLAY", "Voxel Planet Fly Tool", SingleInstance = true)]
public sealed class VoxelPlanetFlyTool : ComponentBase
{
    private const float LookSensitivity = 0.0026f;
    private const float MoveSpeed = 13f;
    private const float SprintMultiplier = 3f;
    private const float BrushInterval = 0f;

    private GameRoot? _root;
    private InputService? _input;
    private InputActionMap? _actions;
    private IWorldElement? _owner;
    private IWorldElement? _camera;
    private VoxelPlanetComponent? _planet;

    private Vector3 _position;
    private float _yaw;
    private float _pitch;
    private bool _placed;

    private float _radius = 2.4f;
    private float _brushCooldown;
    private float _hudCooldown;
    private float _missingPlanetSeconds;

    private LabelComponent? _modeLabel;
    private LabelComponent? _statsLabel;

    /// <summary>
    /// Carves a shallow crater on the camera-facing side once the planet has
    /// built. It makes the sample show its strata and prove the edit path on
    /// the first frame instead of asking the user to trust a pristine sphere.
    /// </summary>
    public bool CarveStartupCrater { get; set; } = true;

    private bool _craterCarved;

    public override string Description => "Fly camera and voxel terrain tool";

    public override void OnAttach(IWorldElement worldElement)
    {
        base.OnAttach(worldElement);

        _owner = worldElement;
        _root = GameRoot.Instance;
        _input = _root?.Registry.Get<InputService>();

        BindInput();
    }

    public override void OnDetach(IWorldElement worldElement)
    {
        if (_actions != null && _input != null)
            _actions.Detach(_input);

        _actions = null;
        _owner = null;
        _camera = null;
        _planet = null;
        base.OnDetach(worldElement);
    }

    private void BindInput()
    {
        if (_input == null)
        {
            GameRoot.Log?.Warning("[VoxelPlanet] No InputService; tool input disabled.");
            return;
        }

        var actions = new InputActionMap { Name = "VoxelPlanet" };

        actions.BindAxis("MoveX", InputKey.D, InputKey.A);
        actions.BindAxis("MoveZ", InputKey.W, InputKey.S);
        actions.BindAxis("MoveY", InputKey.Space, InputKey.LeftControl);

        actions.BindDeltaAxis("MouseX", "mouse_dx", "");
        actions.BindDeltaAxis("MouseY", "mouse_dy", "");

        actions.BindButton("Capture", "mouse_right");
        actions.BindButton("Dig", "mouse_left");
        actions.BindButton("Fill", InputKey.E);
        actions.BindButton("Smooth", InputKey.Q);
        actions.BindButton("Sprint", InputKey.LeftShift);
        actions.BindAxis("BrushRadius", "ui_wheel", "");

        actions.Attach(_input);
        _actions = actions;
    }

    public override void Update(float deltaTime)
    {
        if (_actions == null)
            return;

        _actions.Update(deltaTime);

        _planet ??= _root?
            .FindElementWithComponent<VoxelPlanetComponent>()?
            .GetComponent<VoxelPlanetComponent>();
        _camera ??= _owner?.FindChildByNameRecursive("DesktopCamera");

        if (_actions.GetButtonDown("Capture") && _root?.MouseCapture is { } capture)
            capture.SetCaptured(!capture.IsCaptured);

        bool captured = _root?.MouseCapture?.IsCaptured ?? false;

        if (!_placed && _planet != null)
            PlaceAtStart(_planet);

        if (!_placed)
        {
            // A missing planet usually means V12.Voxel did not resolve before
            // WorldML parsed (a stale host output, or a build interrupted mid-copy).
            // Put the camera somewhere sane and say why instead of showing black.
            _missingPlanetSeconds += deltaTime;
            if (_missingPlanetSeconds >= 1.5f)
            {
                GameRoot.Log?.Error(
                    "[VoxelPlanet] No VoxelPlanetComponent in the scene. " +
                    "V12.Voxel probably did not load; rebuild NovaV2.Host and the sample, then relaunch.");
                PlaceOrbit(28f);
            }

            return;
        }

        if (!_craterCarved && _planet?.Terrain != null)
        {
            CarveStartupCraterNow(_planet);
            _craterCarved = true;
        }

        if (captured)
            UpdateLook();

        UpdateMove(deltaTime);
        ApplyCamera();

        if (captured)
            UpdateBrush(deltaTime);

        UpdateHud(deltaTime, captured);
    }

    private void PlaceAtStart(VoxelPlanetComponent planet) =>
        PlaceOrbit(MathF.Max(6f, planet.Radius));

    private void PlaceOrbit(float radius)
    {
        _position = new Vector3(radius * 1.55f, radius * 0.9f, radius * 1.55f);
        Vector3 direction = Vector3.Normalize(-_position);

        _yaw = MathF.Atan2(-direction.X, -direction.Z);
        _pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        _placed = true;

        ApplyCamera();
    }

    private void UpdateLook()
    {
        _yaw -= _actions!.GetAxis("MouseX") * LookSensitivity;
        _pitch = Math.Clamp(
            _pitch - _actions.GetAxis("MouseY") * LookSensitivity,
            -1.5f,
            1.5f);
    }

    private void UpdateMove(float deltaTime)
    {
        Quaternion yawOnly = Quaternion.CreateFromYawPitchRoll(_yaw, 0f, 0f);

        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, yawOnly);
        Vector3 right = Vector3.Transform(Vector3.UnitX, yawOnly);

        Vector3 move =
            forward * _actions!.GetAxis("MoveZ") +
            right * _actions.GetAxis("MoveX") +
            Vector3.UnitY * _actions.GetAxis("MoveY");

        if (move.LengthSquared() < 1e-6f)
            return;

        float speed = MoveSpeed * (_actions.GetButton("Sprint") ? SprintMultiplier : 1f);
        _position += Vector3.Normalize(move) * (speed * deltaTime);
    }

    private void ApplyCamera()
    {
        if (_owner == null || _camera == null)
            return;

        // The rig element carries world position, the camera child carries only
        // orientation, so the published camera matrix stays easy to reason about.
        _owner.LocalTransform = new TRS(_position, Quaternion.Identity, Vector3.One);
        _camera.LocalTransform = new TRS(
            Vector3.Zero,
            Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f),
            Vector3.One);
    }

    private void CarveStartupCraterNow(VoxelPlanetComponent planet)
    {
        Vector3 outward = Vector3.Normalize(_position);
        Vector3 right = Vector3.Cross(Vector3.UnitY, outward);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;

        Vector3 surface = outward * (planet.Radius + 0.5f);

        planet.ApplyWorldBrush(surface - outward * 1.5f, 5.2f, 3.6f, VoxelBrushMode.Dig);
        planet.ApplyWorldBrush(surface + right * 4.2f - outward * 0.8f, 2.9f, 2.8f, VoxelBrushMode.Dig);
        planet.ApplyWorldBrush(surface - right * 3.6f - outward * 1.2f, 2.2f, 2.4f, VoxelBrushMode.Dig);
    }

    private void UpdateBrush(float deltaTime)
    {
        _radius = Math.Clamp(_radius + _actions!.GetAxis("BrushRadius") * 0.6f, 1f, 8f);

        _brushCooldown -= deltaTime;
        if (_brushCooldown > 0f || _planet?.Terrain == null || _camera == null)
            return;

        VoxelBrushMode mode = CurrentMode;
        bool wantsEdit = _actions.GetButton("Dig") || mode != VoxelBrushMode.Dig;
        if (!wantsEdit)
            return;

        _brushCooldown = BrushInterval;

        Matrix4x4 world = ElementPlacement.RenderWorldMatrix(_camera);
        var origin = new Vector3(world.M41, world.M42, world.M43);
        var direction = new Vector3(-world.M31, -world.M32, -world.M33);

        float strength = _planet.Terrain.Settings.VoxelSize * 0.14f;
        _planet.TryUseTool(origin, direction, _radius, mode, strength, out _);
    }

    private void UpdateHud(float deltaTime, bool captured)
    {
        _hudCooldown -= deltaTime;
        if (_hudCooldown > 0f)
            return;

        _hudCooldown = 0.2f;

        _modeLabel ??= FindLabel("HudMode");
        _statsLabel ??= FindLabel("HudStats");

        if (_modeLabel != null)
        {
            _modeLabel.Text = captured
                ? $"{CurrentMode.ToString().ToUpperInvariant()}  {_radius:0.0} m"
                : "RIGHT CLICK TO CAPTURE";
        }

        if (_statsLabel != null)
        {
            int chunks = _planet?.ChunkCount ?? 0;
            int edits = _planet?.EditedVoxelCount ?? 0;
            double remesh = _planet?.LastRemeshMilliseconds ?? 0.0;
            _statsLabel.Text = $"chunks {chunks}   edits {edits}   remesh {remesh:0.0} ms";
        }
    }

    private VoxelBrushMode CurrentMode
    {
        get
        {
            if (_actions?.GetButton("Fill") == true)
                return VoxelBrushMode.Fill;

            if (_actions?.GetButton("Smooth") == true)
                return VoxelBrushMode.Smooth;

            return VoxelBrushMode.Dig;
        }
    }

    private LabelComponent? FindLabel(string name) =>
        _owner?.GetRoot().FindChildByNameRecursive(name)?.GetComponent<LabelComponent>();
}
