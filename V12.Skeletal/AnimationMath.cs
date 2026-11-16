using System.Numerics;

namespace V12.Skeletal;

/// <summary>
/// Conversions between Prowl's column-vector math and V12's row-vector
/// <see cref="Matrix4x4"/>.
/// </summary>
public static class AnimationMath
{
    /// <summary>Builds a local TRS matrix from position/rotation/scale.</summary>
    public static Matrix4x4 ToMatrix(Vector3 position, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateFromQuaternion(rotation)
        * Matrix4x4.CreateTranslation(position);

    /// <summary>Builds a local TRS matrix from a Prowl transform.</summary>
    public static Matrix4x4 ToMatrix(in Prowl.Vector.Spatial.Transform3D transform) =>
        ToMatrix(
            new Vector3(transform.position.X, transform.position.Y, transform.position.Z),
            new Quaternion(
                transform.rotation.X,
                transform.rotation.Y,
                transform.rotation.Z,
                transform.rotation.W),
            new Vector3(transform.scale.X, transform.scale.Y, transform.scale.Z));

    /// <summary>
    /// Converts a Prowl matrix (column-vector, columns in <c>c0..c3</c>) into
    /// V12's row-vector <see cref="Matrix4x4"/>. Each Prowl column becomes a row.
    /// </summary>
    public static Matrix4x4 ToMatrix(in Prowl.Vector.Float4x4 matrix) => new(
        matrix.c0.X, matrix.c0.Y, matrix.c0.Z, matrix.c0.W,
        matrix.c1.X, matrix.c1.Y, matrix.c1.Z, matrix.c1.W,
        matrix.c2.X, matrix.c2.Y, matrix.c2.Z, matrix.c2.W,
        matrix.c3.X, matrix.c3.Y, matrix.c3.Z, matrix.c3.W);
}
