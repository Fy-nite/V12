using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuUtilities;

namespace V12.Physics;

/// <summary>
/// Integrates uniform gravity plus an optional point-gravity field, and honors
/// each body's gravity multiplier. The field is snapshotted by
/// <see cref="GravityContext.Prepare"/> before the step starts.
/// </summary>
internal struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    private readonly GravityContext _gravity;

    public PoseIntegratorCallbacks(GravityContext gravity)
    {
        _gravity = gravity;
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => (AngularIntegrationMode)0;
    public readonly bool AllowSubsteppingForFocusBodies => false;
    public bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(Simulation simulation) { }
    public void PrepareForMultithreadedExecution(int workerCount) { }

    public void PrepareForIntegration(float dt)
    {
        // PhysicsService.Step has already called GravityContext.Prepare with the
        // active body set and the body->gravity-scale map.
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
        ref BodyVelocityWide velocity)
    {
        int laneCount = Vector<float>.Count;
        Vector3Wide accelerationWide = default;

        for (int lane = 0; lane < laneCount; lane++)
        {
            if (integrationMask[lane] == 0)
                continue;

            int bodyIndex = bodyIndices[lane];
            float gravityScale = _gravity.GetGravityScale(bodyIndex);
            if (gravityScale == 0f)
                continue;

            Vector3Wide.ReadSlot(ref position, lane, out var bodyPosition);
            Vector3 acceleration = _gravity.AccelerationAt(bodyPosition, gravityScale);
            Vector3Wide.WriteSlot(acceleration * dt[lane], lane, ref accelerationWide);
        }

        Vector3Wide.Add(velocity.Linear, accelerationWide, out velocity.Linear);
    }
}
