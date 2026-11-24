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

        var world = GameRoot.Instance?.GetWorldForElement(Owner!);
        var global = world?.GlobalPositionOf(Owner!) ?? WorldOffset.Zero;
        Console.WriteLine(
            $"[SpaceFlight] body={localBody.Owner?.Name ?? "?"} " +
            $"alt={altitude,7:F1} speed={velocity.Length(),6:F2} " +
            $"e={state.Eccentricity:F4} a={state.SemiMajorAxis,7:F1} " +
            $"apo={state.Apoapsis,7:F1} peri={state.Periapsis,7:F1} " +
            $"T={state.Period,6:F1}s " +
            $"global=({global.X:F0},{global.Y:F0},{global.Z:F0})");
    }
}
