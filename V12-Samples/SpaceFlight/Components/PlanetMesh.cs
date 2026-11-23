using System;
using System.Numerics;
using Assimp;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace SpaceFlight.Components;

/// <summary>
/// Higher-tessellation UV sphere for the visual demo. The engine's built-in
/// sphere mesh is 16x8, which is fine at gameplay scale but visibly faceted on a
/// planet-sized object.
/// </summary>
public sealed class PlanetMesh : ComponentBase, IMeshRenderable
{
    private float _radius = 1f;
    private int _stacks = 48;
    private int _slices = 96;

    private double[]? _points;
    private double[]? _uvs;
    private double[]? _normals;
    private uint[]? _indices;
    private readonly Material _material = new();

    public PlanetMesh()
    {
        Name = "PlanetMesh";
    }

    public float Radius
    {
        get => _radius;
        set { _radius = MathF.Max(0.001f, value); Invalidate(); }
    }

    public int Stacks
    {
        get => _stacks;
        set { _stacks = Math.Max(4, value); Invalidate(); }
    }

    public int Slices
    {
        get => _slices;
        set { _slices = Math.Max(4, value); Invalidate(); }
    }

    public RenderType RenderType => RenderType.Mesh;
    public double[] MeshPoints => _points ??= BuildPoints();
    public uint[] Indices => _indices ??= BuildIndices();
    public double[]? MeshUvs => _uvs ??= BuildUvs();
    public double[]? MeshNormals => _normals ??= BuildNormals();
    public Material Material => _material;
    public bool IsWorldLocked => true;
    public TRS LocalTransform => throw new NotImplementedException();
    public Matrix4x4 WorldTransform => Owner?.WorldTransform ?? Matrix4x4.Identity;

    public override IWorldElement BuildUI() => new Element();

    private void Invalidate()
    {
        _points = null;
        _uvs = null;
        _normals = null;
        _indices = null;
    }

    private double[] BuildPoints()
    {
        var points = new double[(_stacks + 1) * (_slices + 1) * 3];
        int k = 0;
        for (int i = 0; i <= _stacks; i++)
        {
            double phi = Math.PI * i / _stacks;
            double sinPhi = Math.Sin(phi);
            double cosPhi = Math.Cos(phi);
            for (int j = 0; j <= _slices; j++)
            {
                double theta = 2 * Math.PI * j / _slices;
                points[k++] = _radius * sinPhi * Math.Cos(theta);
                points[k++] = _radius * cosPhi;
                points[k++] = _radius * sinPhi * Math.Sin(theta);
            }
        }
        return points;
    }

    private double[] BuildNormals()
    {
        var normals = new double[(_stacks + 1) * (_slices + 1) * 3];
        int k = 0;
        for (int i = 0; i <= _stacks; i++)
        {
            double phi = Math.PI * i / _stacks;
            double sinPhi = Math.Sin(phi);
            double cosPhi = Math.Cos(phi);
            for (int j = 0; j <= _slices; j++)
            {
                double theta = 2 * Math.PI * j / _slices;
                normals[k++] = sinPhi * Math.Cos(theta);
                normals[k++] = cosPhi;
                normals[k++] = sinPhi * Math.Sin(theta);
            }
        }
        return normals;
    }

    private double[] BuildUvs()
    {
        var uvs = new double[(_stacks + 1) * (_slices + 1) * 2];
        int k = 0;
        for (int i = 0; i <= _stacks; i++)
        {
            double v = (double)i / _stacks;
            for (int j = 0; j <= _slices; j++)
            {
                uvs[k++] = (double)j / _slices;
                uvs[k++] = v;
            }
        }
        return uvs;
    }

    private uint[] BuildIndices()
    {
        var indices = new uint[_stacks * _slices * 6];
        int k = 0;
        for (int i = 0; i < _stacks; i++)
        {
            for (int j = 0; j < _slices; j++)
            {
                uint a = (uint)(i * (_slices + 1) + j);
                uint b = a + (uint)(_slices + 1);

                // Outward winding for the engine's right-handed, back-face
                // culled mesh pipeline.
                indices[k++] = a;
                indices[k++] = a + 1;
                indices[k++] = b;
                indices[k++] = a + 1;
                indices[k++] = b + 1;
                indices[k++] = b;
            }
        }
        return indices;
    }
}
