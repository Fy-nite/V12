using System;
using V12.Basic.Components;
using V12.Components;
using V12.Core;
using V12.Gravity;

namespace SpaceFlight.Components;

/// <summary>
/// Prints the live two-body state once per second, relative to the planet
/// currently dominating the ship's local gravity. Switching planets switches
/// the reported centre, mu and altitude.
/// </summary>
public sealed class OrbitTelemetryComponent : ScriptableComponent
{
    public GravityBodySet? Bodies { get; set; }

    private float _timer;

    public override void Update(float deltaTime)
    {
        var body = Owner?.GetComponent<PhysicsBodyComponent>()?.Body;
        if (body == null) return;

        _timer += deltaTime;
        if (_timer < 1f) return;
        _timer = 0f;

        var localBody = Bodies?.FindDominant(body.Position);
        if (localBody == null)
            return;

        var center = localBody.WorldPosition;
        var position = body.Position - center;
        var velocity = body.LinearVelocity - (Bodies?.VelocityOf(localBody) ?? System.Numerics.Vector3.Zero);
        var state = OrbitalState.FromVectors(localBody.Mu, position, velocity);
        float altitude = position.Length() - localBody.SurfaceRadius;

        // The field actually used by the physics step. It should match the
        // local body's pull inside an authored frame; if it does not, the
        // patched-conic field and the telemetry disagree.
        var fieldSystem = GameRoot.Instance?.Registry
            .Get<V12.Gravity.Systems.GravityFieldSystem>("GravityFieldSystem");
        var fieldAccel = fieldSystem?.Field.SampleAcceleration(body.Position)
                         ?? System.Numerics.Vector3.Zero;
        var localAccel = localBody.ToSource().AccelerationAt(body.Position);
        var sumAccel = System.Numerics.Vector3.Zero;
        var ancestorAccel = System.Numerics.Vector3.Zero;
        float fieldDistance = -1f;
        float worstAncestor = 0f;
        float worstSoi = 0f;
        if (fieldSystem != null)
        {
            foreach (var source in fieldSystem.Field.Sources)
            {
                sumAccel += source.AccelerationAt(body.Position);
                if (MathF.Abs(source.SphereOfInfluence - localBody.SphereOfInfluence) < 1f)
                    fieldDistance = (source.Position - body.Position).Length();

                if (source.SphereOfInfluence > 0f
                    && source.SphereOfInfluence <= localBody.SphereOfInfluence)
                    continue;

                var tidal = source.AccelerationAt(body.Position)
                          - source.AccelerationAt(localBody.WorldPosition);
                ancestorAccel += tidal;
                if (tidal.Length() > worstAncestor)
                {
                    worstAncestor = tidal.Length();
                    worstSoi = source.SphereOfInfluence;
                }
            }
        }

        var world = GameRoot.Instance?.GetWorldForElement(Owner!);
        var global = world?.GlobalPositionOf(Owner!) ?? WorldOffset.Zero;
        Console.WriteLine(
            $"[SpaceFlight] body={localBody.Owner?.Name ?? "?"} " +
            $"alt={altitude,7:F1} speed={velocity.Length(),6:F2} " +
            $"e={state.Eccentricity:F4} a={state.SemiMajorAxis,7:F1} " +
            $"apo={state.Apoapsis,7:F1} peri={state.Periapsis,7:F1} " +
            $"T={state.Period,6:F1}s g={fieldAccel.Length():F2} " +
            $"localg={localAccel.Length():F2} sum={sumAccel.Length():F2} " +
            $"anc={ancestorAccel.Length():F2} wa={worstAncestor:F2}/{worstSoi:F0} " +
            $"fd={fieldDistance:F0} cr={position.Length():F0} " +
            $"gv=({fieldAccel.X:F2},{fieldAccel.Y:F2},{fieldAccel.Z:F2}) " +
            $"lgv=({localAccel.X:F2},{localAccel.Y:F2},{localAccel.Z:F2}) " +
            $"soi={localBody.SphereOfInfluence:F0} " +
            $"global=({global.X:F0},{global.Y:F0},{global.Z:F0})");
    }
}
