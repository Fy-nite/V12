using System;
using System.Numerics;
using V12.Components;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace SpaceFlight.Components;

/// <summary>
/// Scales the exhaust cone between a tiny "off" size and full size. Using the
/// transform instead of Active keeps the change dirty-tracked, so the renderer
/// updates immediately even though the cone is a plain mesh element.
/// </summary>
public sealed class ThrustPlumeComponent : ScriptableComponent
{
    public bool Thrusting { get; set; }
    public float OffScale { get; set; } = 0.001f;
    public float OnScale { get; set; } = 1f;

    public override void Update(float deltaTime)
    {
        if (Owner == null)
            return;

        float scale = Thrusting ? OnScale : OffScale;
        var transform = Owner.LocalTransform;
        if (MathF.Abs(transform.Scale.X - scale) < 1e-4f)
            return;

        Owner.LocalTransform = new TRS
        {
            Position = transform.Position,
            Rotation = transform.Rotation,
            Scale = new Vector3(scale),
        };
    }
}
