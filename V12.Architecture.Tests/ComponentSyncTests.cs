using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using V12.Components;
using V12.Core.Networking;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class ComponentSyncTests
{
    private sealed class SyncProbeComponent : ComponentBase
    {
        private float _value;

        public override string Name => "SyncProbe";

        public float Value
        {
            get => _value;
            set => SetSync(ref _value, value, 0.01f);
        }
    }

    [Fact]
    public void SetSync_OnlyRaisesDirty_WhenValueChangesOutsideEpsilon()
    {
        var component = new SyncProbeComponent();
        int dirty = 0;
        component.OnDirty += _ => dirty++;

        component.Value = 0f;
        Assert.Equal(0, dirty);

        component.Value = 0.005f;
        Assert.Equal(0, dirty);

        component.Value = 0.02f;
        Assert.Equal(1, dirty);

        component.Value = 0.025f;
        Assert.Equal(1, dirty);
    }

    [Fact]
    public void SyncValueMembers_AreIgnoredByTheBsonComponentPath_WithoutBsonIgnore()
    {
        BsonConfig.Initialize();

        var mesh = new MeshComponent
        {
            Name = "mesh",
            MeshPointsValue = new SyncValue<double[]>(
                "test/sync-value/mesh",
                new[] { 1.0, 2.0, 3.0 },
                markDirtyInitially: false)
        };

        var dto = AncientCompressor.CompressComponent(mesh);
        Assert.NotNull(dto);

        var document = BsonSerializer.Deserialize<BsonDocument>(dto!.Data);
        Assert.False(document.Contains("MeshPointsValue"));
        Assert.False(document.Contains("IndicesValue"));

        var restored = AncientCompressor.DecompressComponent(dto) as MeshComponent;
        Assert.NotNull(restored);
        Assert.Equal("mesh", restored!.Name);
    }
}
