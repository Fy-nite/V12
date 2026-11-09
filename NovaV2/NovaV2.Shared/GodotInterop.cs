using System.Runtime.InteropServices;
using Godot;
using V12.Rendering;
using NMatrix4x4 = System.Numerics.Matrix4x4;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace NovaV2.Rendering;

internal static class GodotInterop
{
    public static Vector3 ToGodot(NVector3 v) => new(v.X, v.Y, v.Z);
    public static Vector2 ToGodot(NVector2 v) => new(v.X, v.Y);
    public static Color ToGodotColor(NVector3 v) => new(v.X, v.Y, v.Z, 1f);
    public static Color ToGodotColor(NVector4 v) => new(v.X, v.Y, v.Z, v.W);

    public static Transform3D ToTransform(NMatrix4x4 m) => new(
        new Basis(
            new Vector3(m.M11, m.M12, m.M13),
            new Vector3(m.M21, m.M22, m.M23),
            new Vector3(m.M31, m.M32, m.M33)),
        new Vector3(m.M41, m.M42, m.M43));

    public static Transform3D ViewToCameraTransform(NMatrix4x4 view)
    {
        if (!NMatrix4x4.Invert(view, out var world))
            world = NMatrix4x4.Identity;
        return ToTransform(world);
    }

    public static ArrayMesh ToArrayMesh(in MeshData data)
    {
        var mesh = new ArrayMesh();
        if (data.VertexStreams.Length == 0 || data.Layout.Length == 0)
            return mesh;

        var stream = data.VertexStreams.Span[0];
        if (stream.Stride <= 0)
            return mesh;

        int vertexCount = stream.Data.Length / stream.Stride;
        if (vertexCount <= 0)
            return mesh;

        var verticesBytes = stream.Data.Span;
        Vector3[]? positions = null;
        Vector3[]? normals = null;
        Vector2[]? uvs = null;
        float[]? tangents = null;
        Color[]? colors = null;

        foreach (var attribute in data.Layout.Span)
        {
            if (attribute.Stream != 0 || attribute.Offset < 0)
                continue;

            switch (attribute.Semantic)
            {
                case VertexSemantic.Position:
                    positions = ReadVector3Array(verticesBytes, vertexCount, stream.Stride, attribute);
                    break;
                case VertexSemantic.Normal:
                    normals = ReadVector3Array(verticesBytes, vertexCount, stream.Stride, attribute);
                    break;
                case VertexSemantic.Uv0:
                    uvs = ReadVector2Array(verticesBytes, vertexCount, stream.Stride, attribute);
                    break;
                case VertexSemantic.Tangent:
                    tangents = ReadFloatArray4(verticesBytes, vertexCount, stream.Stride, attribute);
                    break;
                case VertexSemantic.Color:
                    colors = ReadColorArray(verticesBytes, vertexCount, stream.Stride, attribute);
                    break;
            }
        }

        if (positions is null)
            return mesh;

        var arrays = new Variant[(int)Mesh.ArrayType.Max];
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        if (normals is not null) arrays[(int)Mesh.ArrayType.Normal] = normals;
        if (uvs is not null) arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        if (colors is not null) arrays[(int)Mesh.ArrayType.Color] = colors;

        // Godot expects ARRAY_TANGENT as a PackedFloat32Array of 4 floats per
        // vertex (xyz + binormal sign w). A Vector4[] would marshal to
        // PackedVector4Array, which ArrayMesh rejects, so it stays a flat
        // float array.
        if (tangents is not null && tangents.Length == vertexCount * 4)
            arrays[(int)Mesh.ArrayType.Tangent] = tangents;

        if (data.Indices.Length > 0)
        {
            int[] indices = ReadIndices(data.Indices.Span, data.IndexFormat);

            // V12 geometry uses counter-clockwise outward winding. Godot's
            // ArrayMesh front faces are clockwise, so flip each triangle at the
            // backend boundary without touching the CPU-side convention.
            for (int i = 0; i + 2 < indices.Length; i += 3)
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);

            if (indices.Length > 0)
                arrays[(int)Mesh.ArrayType.Index] = indices;
        }

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, new Godot.Collections.Array(arrays));
        return mesh;
    }

    public static Image? ToImage(in TextureData data)
    {
        if (data.Dimension != TextureDimension.Texture2D || data.Width <= 0 || data.Height <= 0)
            return null;

        Image.Format? format = MapFormat(data.Format);
        if (format is null)
            return null;

        var image = Image.CreateEmpty(data.Width, data.Height, false, format.Value);
        if (data.Subresources.Length == 0)
            return image;

        var subresource = data.Subresources.Span[0];
        int bytesPerPixel = BytesPerPixel(data.Format);
        if (bytesPerPixel <= 0)
            return image;

        int rowBytes = data.Width * bytesPerPixel;
        var source = subresource.Bytes.Span;
        if (subresource.RowPitch == 0 || subresource.RowPitch == rowBytes)
        {
            image.SetData(data.Width, data.Height, false, format.Value, source);
            return image;
        }

        int required = rowBytes * data.Height;
        if (source.Length < required || subresource.RowPitch < rowBytes)
            return image;

        var packed = new byte[required];
        for (int y = 0; y < data.Height; y++)
            source.Slice(y * subresource.RowPitch, rowBytes).CopyTo(packed.AsSpan(y * rowBytes));

        image.SetData(data.Width, data.Height, false, format.Value, packed);
        return image;
    }

    private static Vector3[] ReadVector3Array(
        ReadOnlySpan<byte> data,
        int count,
        int stride,
        in VertexAttribute attribute)
    {
        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * stride + attribute.Offset;
            result[i] = new Vector3(
                MemoryMarshal.Read<float>(data.Slice(offset, 4)),
                MemoryMarshal.Read<float>(data.Slice(offset + 4, 4)),
                MemoryMarshal.Read<float>(data.Slice(offset + 8, 4)));
        }
        return result;
    }

    private static Vector2[] ReadVector2Array(
        ReadOnlySpan<byte> data,
        int count,
        int stride,
        in VertexAttribute attribute)
    {
        var result = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * stride + attribute.Offset;
            result[i] = new Vector2(
                MemoryMarshal.Read<float>(data.Slice(offset, 4)),
                MemoryMarshal.Read<float>(data.Slice(offset + 4, 4)));
        }
        return result;
    }

    private static float[] ReadFloatArray4(
        ReadOnlySpan<byte> data,
        int count,
        int stride,
        in VertexAttribute attribute)
    {
        var result = new float[count * 4];
        for (int i = 0; i < count; i++)
        {
            int offset = i * stride + attribute.Offset;
            int o = i * 4;
            result[o + 0] = MemoryMarshal.Read<float>(data.Slice(offset, 4));
            result[o + 1] = MemoryMarshal.Read<float>(data.Slice(offset + 4, 4));
            result[o + 2] = MemoryMarshal.Read<float>(data.Slice(offset + 8, 4));
            result[o + 3] = MemoryMarshal.Read<float>(data.Slice(offset + 12, 4));
        }
        return result;
    }

    private static Color[] ReadColorArray(
        ReadOnlySpan<byte> data,
        int count,
        int stride,
        in VertexAttribute attribute)
    {
        var result = new Color[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * stride + attribute.Offset;
            if (attribute.Format is VertexFormat.UByte4 or VertexFormat.UByte4Normalized)
            {
                result[i] = new Color(
                    data[offset] / 255f,
                    data[offset + 1] / 255f,
                    data[offset + 2] / 255f,
                    data[offset + 3] / 255f);
            }
            else
            {
                result[i] = new Color(
                    MemoryMarshal.Read<float>(data.Slice(offset, 4)),
                    MemoryMarshal.Read<float>(data.Slice(offset + 4, 4)),
                    MemoryMarshal.Read<float>(data.Slice(offset + 8, 4)),
                    MemoryMarshal.Read<float>(data.Slice(offset + 12, 4)));
            }
        }
        return result;
    }

    private static int[] ReadIndices(ReadOnlySpan<byte> data, IndexFormat format)
    {
        int size = format == IndexFormat.UInt16 ? 2 : 4;
        int count = data.Length / size;
        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * size;
            result[i] = format == IndexFormat.UInt16
                ? MemoryMarshal.Read<ushort>(data.Slice(offset, 2))
                : (int)MemoryMarshal.Read<uint>(data.Slice(offset, 4));
        }
        return result;
    }

    private static Image.Format? MapFormat(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => Image.Format.L8,
        TextureFormat.Rgba8Unorm or TextureFormat.Rgba8Srgb => Image.Format.Rgba8,
        TextureFormat.Rgba16Float => Image.Format.Rgbah,
        TextureFormat.Rgba32Float => Image.Format.Rgbaf,
        _ => null,
    };

    private static int BytesPerPixel(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => 1,
        TextureFormat.Rgba8Unorm or TextureFormat.Rgba8Srgb => 4,
        TextureFormat.Rgba16Float => 8,
        TextureFormat.Rgba32Float => 16,
        _ => 0,
    };
}
