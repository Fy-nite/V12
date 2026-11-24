using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using V12.Core.Interfaces.Physics;

namespace V12.Physics;

/// <summary>
/// Shared, mutable gravity state for the pose integrator. Bepu stores a copy of
/// the callback struct inside the simulation, so mutable data lives here and is
/// refreshed once per step on the game thread.
/// </summary>
internal sealed class GravityContext
{
    public Vector3 UniformGravity = new(0f, -9.81f, 0f);
    public IGravityField? Field;

    private GravitySource[] _sources = new GravitySource[32];
    private float[] _gravityScales = Array.Empty<float>();

    public int SourceCount { get; private set; }
    public int ActiveBodyCount { get; private set; }

    /// <summary>
    /// Snapshot the field and the per-body gravity multipliers for the step.
    /// Must run before <see cref="Simulation.Timestep"/> starts worker threads.
    /// </summary>
    public void Prepare(Simulation simulation, IReadOnlyDictionary<BodyHandle, BepuPhysicsBody> bodiesByHandle)
    {
        int sourceCount = Field?.SourceCount ?? 0;
        if (_sources.Length < sourceCount)
            Array.Resize(ref _sources, Math.Max(sourceCount, 32));

        // A custom field can report a count that does not match what it copied.
        // Clamp both ends so AccelerationAt never indexes past the snapshot.
        int copied = Field?.CopySources(_sources) ?? 0;
        SourceCount = Math.Clamp(copied, 0, _sources.Length);

        var activeSet = simulation.Bodies.ActiveSet;
        int count = activeSet.Count;
        ActiveBodyCount = count;

        if (_gravityScales.Length < count)
            Array.Resize(ref _gravityScales, Math.Max(count, 32));

        for (int i = 0; i < count; i++)
        {
            var handle = activeSet.IndexToHandle[i];
            _gravityScales[i] = bodiesByHandle.TryGetValue(handle, out var body)
                ? body.GravityScale
                : 1f;
        }
    }

    public float GetGravityScale(int activeBodyIndex)
        => (uint)activeBodyIndex < (uint)ActiveBodyCount ? _gravityScales[activeBodyIndex] : 1f;

    public Vector3 AccelerationAt(Vector3 position, float gravityScale)
    {
        var acceleration = UniformGravity;
        for (int i = 0; i < SourceCount; i++)
            acceleration += _sources[i].AccelerationAt(position);
        return acceleration * gravityScale;
    }
}
