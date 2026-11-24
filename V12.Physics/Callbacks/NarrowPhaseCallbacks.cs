using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;

namespace V12.Physics;

internal struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    private readonly CollisionContext _collisions;

    public NarrowPhaseCallbacks(CollisionContext collisions)
    {
        _collisions = collisions;
    }

    public void Initialize(Simulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        => !_collisions.IsTrigger(a) && !_collisions.IsTrigger(b);

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        => !_collisions.IsTrigger(pair.A) && !_collisions.IsTrigger(pair.B);

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial = new PairMaterialProperties(1f, 0.1f, new SpringSettings(30, 1));
        return true;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold) => true;

    public void Dispose() { }
}
