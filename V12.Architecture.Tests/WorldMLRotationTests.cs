using System;
using System.Numerics;
using V12.Core;
using V12.WorldML;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class WorldMLRotationTests
{
    [Fact]
    public void LocalTransform_Rotation_AcceptsXyzDegrees()
    {
        var xml = """
            <World name="Rot">
              <Element name="Box">
                <LocalTransform>
                  <Position>1,2,3</Position>
                  <Rotation>10,20,30</Rotation>
                  <Scale>1,1,1</Scale>
                </LocalTransform>
              </Element>
            </World>
            """;

        var world = (Element)new WorldMLParser().Parse(xml);
        var box = world.FindChildByName("Box");

        Assert.NotNull(box);
        AssertQuaternionApproximately(
            Quaternion.CreateFromYawPitchRoll(
                yaw: 20f * MathF.PI / 180f,
                pitch: 10f * MathF.PI / 180f,
                roll: 30f * MathF.PI / 180f),
            box!.LocalTransform.Rotation);
    }

    [Fact]
    public void LocalTransform_Rotation_WritesXyzDegreesAndRoundTrips()
    {
        var xml = """
            <World name="Rot">
              <Element name="Box">
                <LocalTransform>
                  <Rotation>10,20,30</Rotation>
                </LocalTransform>
              </Element>
            </World>
            """;

        var world = (Element)new WorldMLParser().Parse(xml);
        var box = world.FindChildByName("Box")!;

        var saved = WorldMLSerializer.Serialize(new[] { box }, "Rot");
        Assert.True(saved.Contains("<Rotation>10,20,30</Rotation>", StringComparison.Ordinal), saved);

        var reparsed = (Element)new WorldMLParser().Parse(saved);
        var boxAgain = reparsed.FindChildByName("Box");

        Assert.NotNull(boxAgain);
        AssertQuaternionApproximately(box.LocalTransform.Rotation, boxAgain!.LocalTransform.Rotation);
    }

    [Fact]
    public void LocalTransform_Rotation_StillAcceptsLegacyQuaternion()
    {
        var xml = """
            <World name="Rot">
              <Element name="Box">
                <LocalTransform>
                  <Rotation>0,0.3827,0,0.9239</Rotation>
                </LocalTransform>
              </Element>
            </World>
            """;

        var world = (Element)new WorldMLParser().Parse(xml);
        var box = world.FindChildByName("Box");

        Assert.NotNull(box);
        AssertQuaternionApproximately(new Quaternion(0f, 0.3827f, 0f, 0.9239f), box!.LocalTransform.Rotation);
    }

    private static void AssertQuaternionApproximately(Quaternion expected, Quaternion actual)
    {
        Assert.True(
            MathF.Abs(Quaternion.Dot(expected, actual)) > 0.9999f,
            $"Expected {expected}, got {actual}");
    }
}
