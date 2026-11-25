using System;
using System.Numerics;
using V12.Core.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// World canvases are placed with V12's row-vector transform convention. A
/// scaled and rotated canvas must keep an orthonormal basis; normalising the
/// matrix's columns instead of its rows shears the panel.
/// </summary>
public sealed class WorldCanvasTransformTests
{
    [Fact]
    public void ScaledRotatedCanvas_KeepsOrthonormalBasis()
    {
        var matrix = Matrix4x4.CreateScale(0.16f, 0.0838f, 0.16f)
                     * Matrix4x4.CreateFromYawPitchRoll(0.7f, 0.3f, 0.1f)
                     * Matrix4x4.CreateTranslation(1f, 2f, 3f);

        var canvas = WorldCanvasSnapshot.Create(
            1,
            Guid.Empty,
            420,
            220,
            matrix,
            priority: 0,
            interactive: false);

        Assert.Equal(0.16f, canvas.WidthMetres, 3);
        Assert.Equal(0.0838f, canvas.HeightMetres, 3);

        var x = new Vector3(canvas.World.M11, canvas.World.M12, canvas.World.M13);
        var y = new Vector3(canvas.World.M21, canvas.World.M22, canvas.World.M23);
        var z = new Vector3(canvas.World.M31, canvas.World.M32, canvas.World.M33);

        Assert.Equal(1f, x.Length(), 3);
        Assert.Equal(1f, y.Length(), 3);
        Assert.Equal(1f, z.Length(), 3);
        Assert.Equal(0f, Vector3.Dot(x, y), 3);
        Assert.Equal(0f, Vector3.Dot(x, z), 3);
        Assert.Equal(0f, Vector3.Dot(y, z), 3);
    }
}
