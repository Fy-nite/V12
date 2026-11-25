using System;
using System.Numerics;
using V12.Components;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace PhysicsPlayground;

/// <summary>
/// Slow back-and-forth kinematic mover. The physics body is a kinematic body
/// with <c>ContinuousKinematicMotion</c> set, so the backend publishes the step
/// velocity and a rider is carried by contact instead of being teleported away.
/// </summary>
[ComponentEntry("GAMEPLAY", "Moving Platform", SingleInstance = true)]
public sealed class MovingPlatformComponent : ComponentBase
{
    public Vector3 Axis { get; set; } = Vector3.UnitX;
    public float Travel { get; set; } = 2f;
    public float Speed { get; set; } = 0.6f;
    public float Phase { get; set; }

    /// <summary>Last step's world velocity. A rider controller can add this to
    /// its own velocity so a dynamic body is carried instead of slipped.</summary>
    public Vector3 Velocity { get; private set; }

    private Vector3 _home;
    private Quaternion _rotation = Quaternion.Identity;
    private Vector3 _scale = Vector3.One;
    private float _time;
    private bool _seeded;

    public override string Name => "Moving Platform";
    public override string Description => "Back-and-forth kinematic platform";

    public override void Update(float deltaTime)
    {
        if (Owner == null)
            return;

        if (!_seeded)
        {
            _seeded = true;
            _home = Owner.LocalTransform.Position;
            _rotation = Owner.LocalTransform.Rotation;
            _scale = Owner.LocalTransform.Scale;
        }

        _time += deltaTime;
        float offset = MathF.Sin(_time * Speed + Phase) * Travel;

        var previous = Owner.LocalTransform.Position;
        var next = _home + Axis * offset;
        Velocity = (next - previous) / MathF.Max(deltaTime, 1e-4f);
        Owner.LocalTransform = new TRS(next, _rotation, _scale);
    }

    public void ResetMotion()
    {
        if (Owner == null)
            return;

        if (!_seeded)
        {
            _seeded = true;
            _home = Owner.LocalTransform.Position;
            _rotation = Owner.LocalTransform.Rotation;
            _scale = Owner.LocalTransform.Scale;
        }

        _time = 0f;
        Velocity = Vector3.Zero;
        Owner.LocalTransform = new TRS(_home, _rotation, _scale);
    }
}
