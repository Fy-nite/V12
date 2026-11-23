using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.Networking;

namespace MultiplayerDemo;

/// <summary>
/// Host-authoritative shared cube. The host writes <see cref="Angle"/> every
/// fixed step; a remote client only applies the scoped SyncValue.
/// </summary>
public sealed class MultiplayerCubeComponent : ComponentBase
{
    public SyncValue<float> Angle { get; set; } = new("sharedCube/angle", 0f, markDirtyInitially: true);

    private float _elapsed;

    public override string Name => "Multiplayer Cube";
    public override string Description => "Host-authoritative shared cube";

    public override void OnAttach(IWorldElement worldElement)
    {
        base.OnAttach(worldElement);
        Angle.OnRemoteApplied += OnRemoteAngle;
    }

    public override void OnDetach(IWorldElement worldElement)
    {
        Angle.OnRemoteApplied -= OnRemoteAngle;
        base.OnDetach(worldElement);
    }

    public override void Update(float deltaTime)
    {
        _elapsed += deltaTime;
        float angle = MathF.Sin(_elapsed * 1.2f) * 0.6f;
        Angle.Set(angle);
        ApplyAngle(angle);
    }

    private void OnRemoteAngle(float value, ulong version) => ApplyAngle(value);

    private void ApplyAngle(float angle)
    {
        if (Owner is null) return;

        var transform = Owner.LocalTransform;
        transform.Rotation = Quaternion.CreateFromYawPitchRoll(angle, 0f, 0f);
        Owner.LocalTransform = transform;
    }
}
