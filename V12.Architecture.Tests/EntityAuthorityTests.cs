using System;
using V12.Core;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>Ownership bookkeeping: epochs, release, and the delegated fast-path flag.</summary>
public sealed class EntityAuthorityTests
{
    [Fact]
    public void DelegatedFlag_TracksAssignAndRelease()
    {
        var world = new World("W");
        var element = new Element("E") { Uid = Guid.NewGuid() };
        world.AddElement(element);
        var store = new EntityAuthorityStore();

        Assert.False(store.HasDelegatedEntities);

        Assert.Equal(1, store.Assign(world, element, 7));
        Assert.True(store.HasDelegatedEntities);

        Assert.True(store.Release(world, element));
        Assert.False(store.HasDelegatedEntities);
        Assert.Equal(0, store.GetSimulatingPlayer(world, element));

        // A repeated release stays host-owned.
        Assert.False(store.Release(world, element));
        Assert.False(store.HasDelegatedEntities);
    }

    [Fact]
    public void Apply_RejectsStaleAndDuplicateEpochs()
    {
        var worldId = Guid.NewGuid();
        var elementUid = Guid.NewGuid();
        var store = new EntityAuthorityStore();

        store.Apply(new EntityAssignment(worldId, elementUid, 7, 2));
        Assert.Equal(7, store.GetSimulatingPlayer(new World("W") { Id = worldId }, Element(elementUid)));

        // Duplicate: no change.
        store.Apply(new EntityAssignment(worldId, elementUid, 9, 2));

        // Stale: no change. This is the late-packet case.
        store.Apply(new EntityAssignment(worldId, elementUid, 9, 1));

        var world = new World("W") { Id = worldId };
        var element = Element(elementUid);
        world.AddElement(element);
        Assert.Equal(7, store.GetSimulatingPlayer(world, element));
        Assert.True(store.HasDelegatedEntities);

        // Newer wins, including a release back to the host.
        store.Apply(new EntityAssignment(worldId, elementUid, 0, 3));
        Assert.Equal(0, store.GetSimulatingPlayer(world, element));
        Assert.False(store.HasDelegatedEntities);
    }

    [Fact]
    public void Apply_AcceptsTheFirstAssignmentOfAnUnknownElement()
    {
        var world = new World("W") { Id = Guid.NewGuid() };
        var element = Element(Guid.NewGuid());
        world.AddElement(element);
        var store = new EntityAuthorityStore();

        store.Apply(new EntityAssignment(world.Id, element.Uid, 7, 1));

        Assert.Equal(7, store.GetSimulatingPlayer(world, element));
        Assert.True(store.CanWrite(world, element, 7, isHost: false));
        Assert.False(store.CanWrite(world, element, 8, isHost: false));
    }

    private static Element Element(Guid uid) => new("E") { Uid = uid };
}
