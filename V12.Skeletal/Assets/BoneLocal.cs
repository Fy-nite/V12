using System.Numerics;

namespace V12.Skeletal;

/// <summary>
/// A parent-space bone transform in its decomposed form. Clips store these
/// directly so a backend never has to decompose a matrix per frame.
/// </summary>
public readonly record struct BoneLocal(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static BoneLocal Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateFromQuaternion(Rotation)
        * Matrix4x4.CreateTranslation(Position);

    public static BoneLocal FromMatrix(Matrix4x4 matrix)
    {
        if (Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 position))
            return new BoneLocal(position, rotation, scale);

        return new BoneLocal(
            new Vector3(matrix.M41, matrix.M42, matrix.M43),
            Quaternion.Identity,
            Vector3.One);
    }
}
