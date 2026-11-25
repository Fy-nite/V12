using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Basic.Components;
using V12.Basic.Grabbing;
using V12.Basic.Player;
using V12.Components;
using V12.Components.UI;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Physics;
using V12.Core.Interfaces.Renderer;
using V12.Core.UI.Paper;

namespace PhysicsPlayground;

/// <summary>
/// Sample-specific yard behaviour: HUD, score, triggers, grabber wiring and the
/// wrist panel. Desktop/XR movement, the camera rig and the ADSR speed envelope
/// live in the reusable <see cref="PlayerMotorComponent"/>.
/// </summary>
[ComponentEntry("GAMEPLAY", "Physics Playground Controller", SingleInstance = true)]
public sealed class PhysicsPlaygroundController : ComponentBase
{
    private const float HudInterval = 0.1f;

    private GameRoot? _root;
    private IWorldElement? _owner;
    private PlayerMotorComponent? _motor;
    private GrabberComponent? _grabber;
    private PhysicsPlaygroundLevel? _level;
    private IPhysicsBackend? _physics;
    private IWorldElement? _hudElement;
    private IWorldElement? _wristHud;

    private bool _grabberHooked;
    private bool _triggerHooked;
    private bool _motorHooked;
    private bool _lastXr;

    private LabelComponent? _statusLabel;
    private LabelComponent? _promptLabel;
    private LabelComponent? _scoreLabel;
    private LabelComponent? _eventLabel;
    private LabelComponent? _wristStatusLabel;
    private LabelComponent? _wristPromptLabel;
    private LabelComponent? _wristScoreLabel;
    private LabelComponent? _wristEventLabel;
    private string _eventText = string.Empty;
    private float _eventTimer;

    private int _score;
    private readonly HashSet<long> _scored = new();
    private readonly HashSet<long> _binTargets = new();

    private MaterialComponent? _highlighted;
    private (float r, float g, float b, float energy)? _highlightSaved;
    private bool _paletteApplied;

    public override string Name => "Playground Controller";
    public override string Description => "HUD, score, triggers, grabber and wrist panel";

    /// <summary>Live motor readout for diagnostics and MCP probes.</summary>
    public string InputDebug => _motor?.DebugState ?? "no motor";

    // ── Lifecycle ───────────────────────────────────────────────────────────

    public override void OnAttach(IWorldElement worldElement)
    {
        base.OnAttach(worldElement);
        _owner = worldElement;
        _root = GameRoot.Instance;
        _physics = _root?.PhysicsBackend;

        ApplyPalette();
        EnsureMotor();
        EnsureGrabber();
        HookTriggers();
    }

    public override void OnDetach(IWorldElement worldElement)
    {
        UnhookGrabber();
        UnhookTriggers();
        UnhookMotor();
        RestoreHighlight();
        base.OnDetach(worldElement);
    }

    private void ApplyPalette()
    {
        if (_paletteApplied)
            return;
        _paletteApplied = true;

        PaperPalette.Set(
            canvas: PaperPalette.FromHex("#12100d"),
            panel: PaperPalette.FromHex("#1b1712"),
            raised: PaperPalette.FromHex("#252019"),
            hover: PaperPalette.FromHex("#30291f"),
            selected: PaperPalette.FromHex("#3a3124"),
            border: PaperPalette.FromHex("#3a3327"),
            accent: PaperPalette.FromHex("#d08a34"),
            text: PaperPalette.FromHex("#efe6d2"),
            muted: PaperPalette.FromHex("#a6957e"),
            disabled: PaperPalette.FromHex("#6e6558"),
            isLight: false,
            danger: PaperPalette.FromHex("#c86a52"),
            success: PaperPalette.FromHex("#8ba57a"));
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void Update(float deltaTime)
    {
        EnsureMotor();
        EnsureGrabber();
        EnsureLevel();
        HookTriggers();

        if (_grabber != null)
            _grabber.SetPlayerBody(_motor?.Body);

        if (_motor != null)
        {
            if (_motor.IsXr != _lastXr)
            {
                _lastXr = _motor.IsXr;
                SetHudMode(_lastXr);
                SetEvent(_lastXr ? "XR tracked" : "Desktop mode");
            }

            // Set the carrier velocity before the motor ticks. The motor folds it
            // into its own target so a moving platform carries rather than fires.
            _motor.ExternalVelocity = PlatformCarry();
        }

        UpdateWristHud();
        UpdateHud(deltaTime);
    }

    // ── Motor / grabber / level ─────────────────────────────────────────────

    private void EnsureMotor()
    {
        if (_motorHooked || _owner == null)
            return;

        _motor ??= _owner.GetComponent<PlayerMotorComponent>();
        if (_motor == null)
            return;

        _motorHooked = true;
        _lastXr = _motor.IsXr;
        _motor.ResetRequested += OnResetRequested;
        SetHudMode(_lastXr);
        _grabber?.SetPlayerBody(_motor.Body);
    }

    private void UnhookMotor()
    {
        if (!_motorHooked || _motor == null)
            return;

        _motor.ResetRequested -= OnResetRequested;
        _motorHooked = false;
    }

    private void OnResetRequested()
    {
        // PlayerMotorComponent already returned the player to spawn.
        _level?.Reset();
        _score = 0;
        _scored.Clear();
        SetEvent("Yard reset");
    }

    private void EnsureGrabber()
    {
        if (_grabberHooked || _owner == null)
            return;

        _grabber ??= _owner.GetComponent<GrabberComponent>();
        if (_grabber == null)
            return;

        _grabberHooked = true;
        _grabber.SetPlayerBody(_motor?.Body);
        _grabber.AimChanged += OnAimChanged;
        _grabber.Grabbed += OnGrabbed;
        _grabber.Released += OnReleased;
    }

    private void UnhookGrabber()
    {
        if (!_grabberHooked || _grabber == null)
            return;

        _grabber.AimChanged -= OnAimChanged;
        _grabber.Grabbed -= OnGrabbed;
        _grabber.Released -= OnReleased;
        _grabberHooked = false;
    }

    private void EnsureLevel()
    {
        if (_level != null)
            return;

        _level = _root?.FindElementWithComponent<PhysicsPlaygroundLevel>()
            ?.GetComponent<PhysicsPlaygroundLevel>();
        if (_level == null)
            return;

        _binTargets.Clear();
        foreach (var prop in _level.Props)
            if (prop.BinTarget)
                _binTargets.Add(prop.Element.Id);
    }

    private Vector3 PlatformCarry()
    {
        if (_level?.MovingPlatform is not { } motor ||
            _level.MovingPlatformElement == null ||
            _motor?.Body?.Body == null ||
            _motor.Locomotion == null ||
            !_motor.Locomotion.IsGrounded)
            return Vector3.Zero;

        var platformPosition = _level.MovingPlatformElement.WorldTransform.Translation;
        var bodyPosition = _motor.Body.Body.Position;
        var half = _level.MovingPlatformHalfExtents;

        if (MathF.Abs(bodyPosition.X - platformPosition.X) > half.X + 0.4f ||
            MathF.Abs(bodyPosition.Z - platformPosition.Z) > half.Z + 0.4f)
            return Vector3.Zero;

        float height = bodyPosition.Y - platformPosition.Y;
        if (height < 0.6f || height > 1.7f)
            return Vector3.Zero;

        return motor.Velocity;
    }

    // ── Wrist panel ─────────────────────────────────────────────────────────

    private void SetHudMode(bool xr)
    {
        var root = _owner?.GetRoot();
        _hudElement ??= root?.FindChildByNameRecursive("Hud");
        _wristHud ??= root?.FindChildByNameRecursive("WristHud");

        if (_hudElement != null)
            _hudElement.Active = !xr;
        if (_wristHud != null)
            _wristHud.Active = xr;
    }

    private void UpdateWristHud()
    {
        if (_motor is not { IsXr: true, LeftHand: { } hand } || _wristHud == null)
            return;

        var handPosition = PositionOf(hand.WorldTransform);
        var panelPosition = handPosition + Vector3.UnitY * 0.12f;

        var headPosition = _motor.Head != null
            ? PositionOf(_motor.Head.WorldTransform)
            : panelPosition + Vector3.UnitY * 0.4f;

        var forward = headPosition - panelPosition;
        if (forward.LengthSquared() < 1e-6f)
            forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);

        var right = Vector3.Cross(Vector3.UnitY, forward);
        if (right.LengthSquared() < 1e-6f)
            right = Vector3.UnitX;
        right = Vector3.Normalize(right);

        var up = Vector3.Cross(forward, right);
        var rotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f));

        _wristHud.LocalTransform = new TRS(
            panelPosition + forward * 0.025f,
            rotation,
            Vector3.One);
    }

    private static Vector3 PositionOf(Matrix4x4 matrix) =>
        new(matrix.M41, matrix.M42, matrix.M43);

    // ── Triggers ────────────────────────────────────────────────────────────

    private void HookTriggers()
    {
        _physics ??= _root?.PhysicsBackend;
        if (_triggerHooked || _physics is not IPhysicsTriggerEvents triggers)
            return;

        triggers.TriggerEnter += OnTriggerEnter;
        triggers.TriggerExit += OnTriggerExit;
        _triggerHooked = true;
    }

    private void UnhookTriggers()
    {
        if (!_triggerHooked || _physics is not IPhysicsTriggerEvents triggers)
            return;

        triggers.TriggerEnter -= OnTriggerEnter;
        triggers.TriggerExit -= OnTriggerExit;
        _triggerHooked = false;
    }

    private void OnTriggerEnter(IPhysicsBody trigger, IPhysicsBody other)
    {
        var triggerElement = FindElementForBody(trigger);
        if (triggerElement == null)
            return;

        if (triggerElement.Name == _level?.BinTriggerName)
        {
            var otherElement = FindElementForBody(other);
            if (otherElement == null || !_binTargets.Contains(otherElement.Id))
                return;

            if (_scored.Add(otherElement.Id))
            {
                _score++;
                SetEvent($"Bin +1  ({_score}/{_level?.BinTargetCount ?? 0})");
            }
        }
        else if (triggerElement.Name == _level?.GateTriggerName)
        {
            var otherElement = FindElementForBody(other);
            var bodyElement = _motor?.BodyElement;
            if (otherElement == null || bodyElement == null || otherElement.Id != bodyElement.Id)
                return;

            SetEvent("Gate crossed");
        }
    }

    private void OnTriggerExit(IPhysicsBody trigger, IPhysicsBody other)
    {
        // Score is a one-way tally for this yard run. Reset clears it.
    }

    private IWorldElement? FindElementForBody(IPhysicsBody body)
    {
        return _root?.FindElement(element =>
            element.GetComponent<PhysicsBodyComponent>() is { Body: { } candidate }
            && ReferenceEquals(candidate, body));
    }

    // ── Presentation ────────────────────────────────────────────────────────

    private void OnAimChanged(GrabberComponent grabber, GrabbableComponent aimed)
    {
        RestoreHighlight();

        if (aimed?.Owner == null)
            return;

        if (aimed.Owner.GetComponent<MaterialComponent>() is not { } material)
            return;

        _highlighted = material;
        _highlightSaved = (material.EmissionR, material.EmissionG, material.EmissionB, material.EmissionEnergy);
        material.EmissionR = 0.95f;
        material.EmissionG = 0.52f;
        material.EmissionB = 0.14f;
        material.EmissionEnergy = 0.65f;
    }

    private void OnGrabbed(GrabberComponent grabber, GrabbableComponent grabbable)
        => SetEvent($"Grabbed {grabbable.Owner?.Name ?? "prop"}");

    private void OnReleased(GrabberComponent grabber, GrabbableComponent grabbable)
        => SetEvent($"Released {grabbable.Owner?.Name ?? "prop"}");

    private void RestoreHighlight()
    {
        if (_highlighted == null || _highlightSaved == null)
        {
            _highlighted = null;
            _highlightSaved = null;
            return;
        }

        var (r, g, b, energy) = _highlightSaved.Value;
        _highlighted.EmissionR = r;
        _highlighted.EmissionG = g;
        _highlighted.EmissionB = b;
        _highlighted.EmissionEnergy = energy;
        _highlighted = null;
        _highlightSaved = null;
    }

    private void UpdateHud(float deltaTime)
    {
        _hudTimer -= deltaTime;
        if (_hudTimer > 0f)
            return;
        _hudTimer = HudInterval;

        _statusLabel ??= FindLabel("HudStatus");
        _promptLabel ??= FindLabel("HudPrompt");
        _scoreLabel ??= FindLabel("HudScore");
        _eventLabel ??= FindLabel("HudEvent");
        _wristStatusLabel ??= FindLabel("WristStatus");
        _wristPromptLabel ??= FindLabel("WristPrompt");
        _wristScoreLabel ??= FindLabel("WristScore");
        _wristEventLabel ??= FindLabel("WristEvent");

        if (_eventTimer > 0f)
        {
            _eventTimer -= HudInterval;
            if (_eventTimer <= 0f)
                _eventText = string.Empty;
        }

        int bodies = CountBodies();
        string status = $"{bodies} bodies  -  {(_motor?.IsXr == true ? "XR" : "desktop")}";
        string score = $"BIN {_score:00} / {_level?.BinTargetCount ?? 0:00}";

        SetLabel(_statusLabel, status);
        SetLabel(_scoreLabel, score);
        SetLabel(_eventLabel, _eventText);
        SetLabel(_wristStatusLabel, status);
        SetLabel(_wristScoreLabel, score);
        SetLabel(_wristEventLabel, _eventText);

        string prompt = string.Empty;
        if (_grabber != null)
        {
            if (_grabber.HeldElement != null)
                prompt = $"Holding {_grabber.HeldElement.Name}";
            else if (_grabber.Aimed?.Owner != null)
                prompt = $"Grab {_grabber.Aimed.Owner.Name}";
        }
        SetLabel(_promptLabel, prompt);
        SetLabel(_wristPromptLabel, prompt);
    }

    private float _hudTimer;

    private int CountBodies()
    {
        int count = 0;
        if (_root == null) return 0;
        foreach (var element in _root.FindElementsWithComponent<PhysicsBodyComponent>())
            if (element.GetComponent<PhysicsBodyComponent>()?.Body != null)
                count++;
        return count;
    }

    private LabelComponent? FindLabel(string name)
        => _owner?.GetRoot().FindChildByNameRecursive(name)?.GetComponent<LabelComponent>();

    private static void SetLabel(LabelComponent? label, string text)
    {
        if (label == null || label.Text == text)
            return;
        label.Text = text;
    }

    private void SetEvent(string text)
    {
        _eventText = text;
        _eventTimer = 2.5f;
    }
}
