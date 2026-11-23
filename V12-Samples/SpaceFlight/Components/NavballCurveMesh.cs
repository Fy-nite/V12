using System;
using System.Collections.Generic;
using System.Numerics;
using Assimp;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace SpaceFlight.Components;

/// <summary>
/// A thin ribbon on the navball's visible disk. It samples the great circle
/// whose plane is <see cref="PlaneNormal"/>, keeps only the forward half (the
/// hemisphere around the nose), and projects that half onto the disk. This is
/// what makes the horizon behave like a real navball line instead of a
/// physical ring seen from outside the ball.
/// </summary>
public sealed class NavballCurveMesh : ComponentBase, IMeshRenderable
{
    public const float DiskRadius = 0.5f;

    public int Segments { get; set; } = 128;
    public float HalfWidth { get; set; } = 0.010f;

    // Start at zero so the first real normal always builds. UnitY would look
    // like "no change" for a level horizon and leave the mesh empty.
    private Vector3 _planeNormal = Vector3.Zero;
    private double[] _points = Array.Empty<double>();
    private uint[] _indices = Array.Empty<uint>();
    private readonly Material _material = new();

    public NavballCurveMesh() { Name = "NavballCurve"; }
    public NavballCurveMesh(string name) { Name = name; }

    /// <summary>
    /// Plane normal in the navball projection frame: ship-local axes with the
    /// nose at -Z and the disk spanning X/Y.
    /// </summary>
    public Vector3 PlaneNormal
    {
        get => _planeNormal;
        set
        {
            if (Vector3.DistanceSquared(_planeNormal, value) < 1e-5f)
                return;

            _planeNormal = value;
            Rebuild();
        }
    }

    public RenderType RenderType => RenderType.Mesh;
    public double[] MeshPoints => _points;
    public uint[] Indices => _indices;
    public Material Material => _material;
    public bool IsWorldLocked => true;
    public TRS LocalTransform => throw new NotImplementedException();
    public Matrix4x4 WorldTransform => Owner?.WorldTransform ?? Matrix4x4.Identity;

    public override IWorldElement BuildUI() => new Element();

    private void Rebuild()
    {
        var normal = _planeNormal;
        if (normal.LengthSquared() < 1e-8f)
        {
            Clear();
            return;
        }

        normal = Vector3.Normalize(normal);
        var helper = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var basisA = Vector3.Normalize(Vector3.Cross(helper, normal));
        var basisB = Vector3.Cross(normal, basisA);

        var directions = new Vector3[Segments];
        var visible = new bool[Segments];
        for (int i = 0; i < Segments; i++)
        {
            float angle = 2f * MathF.PI * i / Segments;
            var direction = basisA * MathF.Cos(angle) + basisB * MathF.Sin(angle);
            directions[i] = direction;
            visible[i] = direction.Z < -0.0005f;
        }

        // The visible half can wrap around the end of the sample array. Find an
        // invisible point first, then walk forward from it so the run is never
        // cut in half by the array boundary.
        int gap = -1;
        for (int i = 0; i < Segments; i++)
        {
            if (!visible[i])
            {
                gap = i;
                break;
            }
        }

        var run = new List<Vector3>(Segments / 2 + 1);
        if (gap < 0)
        {
            for (int i = 0; i < Segments; i++)
                run.Add(directions[i]);
        }
        else
        {
            int start = -1;
            for (int k = 0; k < Segments; k++)
            {
                int index = (gap + k) % Segments;
                if (visible[index])
                {
                    start = index;
                    break;
                }
            }

            if (start >= 0)
            {
                for (int k = 0; k < Segments; k++)
                {
                    int index = (start + k) % Segments;
                    if (!visible[index])
                        break;
                    run.Add(directions[index]);
                }
            }
        }

        if (run.Count < 2)
        {
            Clear();
            return;
        }

        var points = new List<double>(run.Count * 6);
        var indices = new List<uint>((run.Count - 1) * 6);

        for (int i = 0; i < run.Count; i++)
        {
            var direction = run[i];
            var flat = new Vector2(direction.X, direction.Y) * DiskRadius;

            var previous = run[Math.Max(0, i - 1)];
            var next = run[Math.Min(run.Count - 1, i + 1)];
            var tangent = new Vector2(next.X - previous.X, next.Y - previous.Y);
            var side = tangent.LengthSquared() > 1e-8f
                ? Vector2.Normalize(new Vector2(-tangent.Y, tangent.X))
                : Vector2.UnitX;

            var left = flat + side * HalfWidth;
            var right = flat - side * HalfWidth;

            points.Add(left.X);
            points.Add(left.Y);
            points.Add(0);
            points.Add(right.X);
            points.Add(right.Y);
            points.Add(0);

            if (i > 0)
            {
                uint baseIndex = (uint)((i - 1) * 2);
                indices.Add(baseIndex);
                indices.Add(baseIndex + 1);
                indices.Add(baseIndex + 2);
                indices.Add(baseIndex + 1);
                indices.Add(baseIndex + 3);
                indices.Add(baseIndex + 2);
            }
        }

        _points = points.ToArray();
        _indices = indices.ToArray();
        MarkDirty();
    }

    private void Clear()
    {
        if (_points.Length == 0 && _indices.Length == 0)
            return;

        _points = Array.Empty<double>();
        _indices = Array.Empty<uint>();
        MarkDirty();
    }
}
