using System;
using System.Numerics;
using Assimp;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace SpaceFlight.Components;

/// <summary>
/// Flat annulus in the XZ plane, used for Saturn-style rings. The element
/// carrying it is scaled to the planet's radius, so the mesh radii are
/// expressed as multiples of that radius.
/// </summary>
public sealed class PlanetRingMesh : ComponentBase, IMeshRenderable
{
    public float InnerRadius { get; set; } = 1.3f;
    public float OuterRadius { get; set; } = 2.15f;
    public int Segments { get; set; } = 128;

    private double[]? _points;
    private uint[]? _indices;
    private readonly Material _material = new();

    public PlanetRingMesh() { Name = "PlanetRing"; }
    public PlanetRingMesh(string name) { Name = name; }

    public RenderType RenderType => RenderType.Mesh;
    public double[] MeshPoints => _points ??= BuildPoints();
    public uint[] Indices => _indices ??= BuildIndices();
    public Material Material => _material;
    public bool IsWorldLocked => true;
    public TRS LocalTransform => throw new NotImplementedException();
    public Matrix4x4 WorldTransform => Owner?.WorldTransform ?? Matrix4x4.Identity;

    public override IWorldElement BuildUI() => new Element();

    private double[] BuildPoints()
    {
        int segments = Math.Max(8, Segments);
        var points = new double[(segments + 1) * 2 * 3];
        int p = 0;
        for (int i = 0; i <= segments; i++)
        {
            float angle = 2f * MathF.PI * i / segments;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            points[p++] = cos * InnerRadius;
            points[p++] = 0;
            points[p++] = sin * InnerRadius;

            points[p++] = cos * OuterRadius;
            points[p++] = 0;
            points[p++] = sin * OuterRadius;
        }
        return points;
    }

    private uint[] BuildIndices()
    {
        int segments = Math.Max(8, Segments);
        var indices = new uint[segments * 6];
        int k = 0;
        for (int i = 0; i < segments; i++)
        {
            uint inner = (uint)(i * 2);
            uint outer = inner + 1;
            uint nextInner = inner + 2;
            uint nextOuter = inner + 3;

            indices[k++] = inner;
            indices[k++] = outer;
            indices[k++] = nextInner;
            indices[k++] = outer;
            indices[k++] = nextOuter;
            indices[k++] = nextInner;
        }
        return indices;
    }
}
