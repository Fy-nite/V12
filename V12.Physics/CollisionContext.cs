using System.Collections.Generic;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace V12.Physics;

/// <summary>
/// Shared collision flags for the narrow-phase callback. Bepu copies the
/// callback struct into the simulation, so mutable per-body flags live here.
/// Trigger handles are written on the game thread between steps and only read
/// by worker threads during a step.
/// </summary>
internal sealed class CollisionContext
{
    private readonly HashSet<BodyHandle> _triggerBodies = new();
    private readonly HashSet<StaticHandle> _triggerStatics = new();

    public bool IsTrigger(CollidableReference collidable)
        => collidable.Mobility == CollidableMobility.Static
            ? _triggerStatics.Contains(collidable.StaticHandle)
            : _triggerBodies.Contains(collidable.BodyHandle);

    public void SetBody(BodyHandle handle, bool isTrigger)
    {
        if (isTrigger)
            _triggerBodies.Add(handle);
        else
            _triggerBodies.Remove(handle);
    }

    public void SetStatic(StaticHandle handle, bool isTrigger)
    {
        if (isTrigger)
            _triggerStatics.Add(handle);
        else
            _triggerStatics.Remove(handle);
    }

    public void RemoveBody(BodyHandle handle) => _triggerBodies.Remove(handle);
    public void RemoveStatic(StaticHandle handle) => _triggerStatics.Remove(handle);

    public void Clear()
    {
        _triggerBodies.Clear();
        _triggerStatics.Clear();
    }
}
