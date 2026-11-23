using System;
using System.Numerics;
using V12.Components;
using V12.Core.Core.Interfaces;

namespace SpaceFlight.Components;

/// <summary>
/// Moves a planet or moon on a circular track. The kinematic physics body
/// follows the element transform, so the gravity source moves with it. When
/// <see cref="CenterElement"/> is set the track follows that element, which is
/// how the Moon stays with Earth while the Earth orbits the Sun.
/// </summary>
public sealed class PlanetOrbitComponent : ScriptableComponent
{
    public Vector3 Center { get; set; }
    public IWorldElement? CenterElement { get; set; }
    public float Radius { get; set; } = 1000f;
    public float Height { get; set; }
    public float AngularSpeed { get; set; } = 0.01f;
    public float Phase { get; set; }

    /// <summary>World velocity of the track at the last update, for patched
    /// conic state relative to this body.</summary>
    public Vector3 LinearVelocity { get; private set; }

    private float _angle;

    public override void Update(float deltaTime)
    {
        if (Owner == null)
            return;

        _angle += AngularSpeed * deltaTime;
        float angle = _angle + Phase;
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);

        var center = Center;
        var centerVelocity = Vector3.Zero;
        if (CenterElement != null)
        {
            var matrix = CenterElement.WorldTransform;
            center = new Vector3(matrix.M41, matrix.M42, matrix.M43);
            centerVelocity = CenterElement.GetComponent<PlanetOrbitComponent>()?.LinearVelocity
                             ?? Vector3.Zero;
        }

        var transform = Owner.LocalTransform;
        transform.Position = center + new Vector3(cos * Radius, Height, sin * Radius);
        Owner.LocalTransform = transform;

        LinearVelocity = centerVelocity
                       + new Vector3(-sin * AngularSpeed * Radius, 0f, cos * AngularSpeed * Radius);
    }
}
