using System;
using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace V12.Skeletal.Components;

/// <summary>
/// Keeps this element on a bone of a skeletal manager. Useful for weapons,
/// hats, camera mounts and other props that should follow the pose.
///
/// Place the element under the manager's element (or anywhere); the component
/// converts the bone's model transform into the element's parent space.
/// </summary>
[ComponentEntry("GAMEPLAY", "Skeletal Bone Attachment", SingleInstance = true, SortOrder = 8, Icon = "link")]
public class SkeletalBoneAttachmentComponent : ComponentBase
{
    private SkeletalManagerComponent? _manager;
    private int _boneIndex = -1;

    public override string Name => "Skeletal Bone Attachment";
    public override string Description => "Follows a named bone of a skeletal manager";

    public SkeletalManagerComponent? Manager { get; set; }

    /// <summary>Bone name to follow. Case-insensitive.</summary>
    public string? Bone { get; set; }

    /// <summary>Offset in bone-local space.</summary>
    public Vector3 Offset { get; set; }

    public override void Update(float deltaTime)
    {
        var owner = Owner;
        if (owner is null)
            return;

        Resolve();
        if (_manager is null || _boneIndex < 0)
            return;

        // Manager model space -> world.
        Matrix4x4 boneWorld = _manager.GetBoneModelMatrix(_boneIndex);
        if (_manager.Owner is { } managerOwner)
            boneWorld *= managerOwner.WorldTransform;

        boneWorld *= Matrix4x4.CreateTranslation(Offset);

        Matrix4x4 parentWorld = owner.Parent?.WorldTransform ?? Matrix4x4.Identity;
        Matrix4x4 local = Matrix4x4.Invert(parentWorld, out var inverse)
            ? boneWorld * inverse
            : boneWorld;

        if (Matrix4x4.Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 position))
            owner.LocalTransform = new TRS(position, rotation, scale);
    }

    private void Resolve()
    {
        if (_manager is null)
        {
            var owner = Owner;
            _manager = Manager
                       ?? owner?.GetComponent<SkeletalManagerComponent>()
                       ?? owner?.Parent?.GetComponent<SkeletalManagerComponent>()
                       ?? GameRoot.Instance?.SelectedWorld
                           ?.FindElementWithComponentRecursive<SkeletalManagerComponent>()
                           ?.GetComponent<SkeletalManagerComponent>();
        }

        if (_manager is not null && _boneIndex < 0 && !string.IsNullOrWhiteSpace(Bone))
            _boneIndex = _manager.IndexOfBone(Bone);
    }

    public override IWorldElement BuildUI() => new Element();
}

