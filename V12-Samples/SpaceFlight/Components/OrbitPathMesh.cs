using System;
using System.Numerics;
using Assimp;
using V12.Basic.Components;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Gravity;
using V12.Gravity.Components;

namespace SpaceFlight.Components;

/// <summary>
/// Draws the ship's current two-body orbit as a thin ribbon around whichever
/// planet currently dominates its local gravity. Switching planets changes the
/// conic's centre, radius and mu, so the visible ring follows the body the ship
/// is actually orbiting. Unbound states hide the ribbon.
///
/// Geometry is local to the element; the transform carries the body centre, so
/// a moving planet does not force a vertex rebuild.
/// </summary>
public sealed class OrbitPathMesh : ComponentBase, IMeshRenderable
{
    public float Mu { get; set; }
    public IWorldElement? Ship { get; set; }
    public GravityBodySet? Bodies { get; set; }
    public int Segments { get; set; } = 2048;
    public float HalfWidth { get; set; } = 1.4f;
    public float RefreshInterval { get; set; } = 0.02f;

    private double[] _points = Array.Empty<double>();
    private uint[] _indices = Array.Empty<uint>();
    private readonly Material _material = new();

    private float _timer;
    private bool _hasState;
    private double _lastSemiMajorAxis;
    private double _lastEccentricity;
    private GravitySourceComponent? _lastBody;

    public OrbitPathMesh()
    {
        Name = "OrbitPath";
    }

    public RenderType RenderType => RenderType.Mesh;
    public double[] MeshPoints => _points;
    public uint[] Indices => _indices;
    public double[]? MeshUvs => null;
    public double[]? MeshNormals => null;
    public Material Material => _material;
    public bool IsWorldLocked => true;
    public TRS LocalTransform => throw new NotImplementedException();
    public Matrix4x4 WorldTransform => Owner?.WorldTransform ?? Matrix4x4.Identity;

    public override IWorldElement BuildUI() => new Element();

    /// <summary>Seed the path from a known state so the ring exists immediately.</summary>
    public void SetState(OrbitalState state, Vector3 center = default)
    {
        if (state.IsBound && state.Eccentricity < 1 && state.SemiMajorAxis > 0)
        {
            ApplyCenter(center);
            BuildFor(state);
        }
    }

    public override void Update(float deltaTime)
    {
        _timer += deltaTime;
        if (_timer < RefreshInterval)
            return;
        _timer = 0;

        var body = Ship?.GetComponent<PhysicsBodyComponent>()?.Body;
        if (body == null)
            return;

        var localBody = Bodies?.FindDominant(body.Position);
        Vector3 center = localBody?.WorldPosition ?? Vector3.Zero;
        float mu = localBody?.Mu ?? Mu;
        if (mu <= 0f)
        {
            Hide();
            return;
        }

        var relativePosition = body.Position - center;
        var relativeVelocity = body.LinearVelocity - (Bodies?.VelocityOf(localBody) ?? Vector3.Zero);
        var state = OrbitalState.FromVectors(mu, relativePosition, relativeVelocity);
        if (!state.IsBound || state.Eccentricity >= 1 || state.SemiMajorAxis <= 0)
        {
            Hide();
            return;
        }

        // Geometry is built around the origin; the element is moved to the
        // body's centre. Rebuilding on centre drift rebuilt the whole tube on
        // every refresh, because the planets are always moving.
        ApplyCenter(center);

        bool changed = !_hasState
            || !ReferenceEquals(localBody, _lastBody)
            || Math.Abs(state.SemiMajorAxis - _lastSemiMajorAxis) > Math.Abs(_lastSemiMajorAxis) * 1e-3
            || Math.Abs(state.Eccentricity - _lastEccentricity) > 1e-4;

        if (changed)
            BuildFor(state);

        _lastBody = localBody;
    }

    private void ApplyCenter(Vector3 center)
    {
        if (Owner == null)
            return;

        var transform = Owner.LocalTransform;
        transform.Position = center;
        Owner.LocalTransform = transform;
    }

    private void BuildFor(OrbitalState state)
    {
        int segments = Math.Max(32, Segments);
        var normal = state.OrbitNormal;
        var periapsis = state.EccentricityVector;
        var periapsisDir = periapsis.Length > 1e-6 ? periapsis.Normalized() : state.Position.Normalized();
        var sideDir = Double3.Cross(normal, periapsisDir).Normalized();

        if (periapsisDir.LengthSquared < 0.25 || sideDir.LengthSquared < 0.25)
            return;

        double semiMajor = state.SemiMajorAxis;
        double eccentricity = Math.Clamp(state.Eccentricity, 0, 0.999999);
        double oneMinusESquared = 1 - eccentricity * eccentricity;
        // Constant world-space thickness. Scaling this with semi-major axis
        // made the line visibly fatten every time a burn raised the orbit.
        double tubeRadius = HalfWidth;

        var centers = new Double3[segments];
        for (int i = 0; i < segments; i++)
        {
            double trueAnomaly = 2 * Math.PI * i / segments;
            double denominator = 1 + eccentricity * Math.Cos(trueAnomaly);
            if (denominator < 1e-6)
                denominator = 1e-6;

            double radius = semiMajor * oneMinusESquared / denominator;
            centers[i] = periapsisDir * (radius * Math.Cos(trueAnomaly))
                       + sideDir * (radius * Math.Sin(trueAnomaly));
        }

        const int radialSegments = 6;
        int ringCount = radialSegments + 1;
        var points = new double[segments * ringCount * 3];
        var indices = new uint[segments * radialSegments * 6];
        int p = 0;

        for (int i = 0; i < segments; i++)
        {
            var center = centers[i];
            var next = centers[(i + 1) % segments];
            var previous = centers[(i + segments - 1) % segments];
            var tangent = (next - previous).Normalized();
            if (tangent.LengthSquared < 0.25)
                tangent = sideDir;

            var binormal = Double3.Cross(tangent, normal).Normalized();
            if (binormal.LengthSquared < 0.25)
                binormal = sideDir;

            for (int j = 0; j <= radialSegments; j++)
            {
                double angle = 2 * Math.PI * j / radialSegments;
                var offset = normal * (tubeRadius * Math.Cos(angle))
                           + binormal * (tubeRadius * Math.Sin(angle));
                var point = center + offset;

                points[p++] = point.X;
                points[p++] = point.Y;
                points[p++] = point.Z;
            }
        }

        int k = 0;
        for (int i = 0; i < segments; i++)
        {
            uint r0 = (uint)(i * ringCount);
            uint r1 = (uint)(((i + 1) % segments) * ringCount);

            for (int j = 0; j < radialSegments; j++)
            {
                indices[k++] = r0 + (uint)j;
                indices[k++] = r1 + (uint)(j + 1);
                indices[k++] = r1 + (uint)j;
                indices[k++] = r0 + (uint)j;
                indices[k++] = r0 + (uint)(j + 1);
                indices[k++] = r1 + (uint)(j + 1);
            }
        }

        _points = points;
        _indices = indices;
        _lastSemiMajorAxis = semiMajor;
        _lastEccentricity = state.Eccentricity;
        _hasState = true;

        if (Owner != null)
            Owner.Active = true;

        MarkDirty();
    }

    private void Hide()
    {
        if (_points.Length != 0)
        {
            _points = Array.Empty<double>();
            _indices = Array.Empty<uint>();
            _hasState = false;
            MarkDirty();
        }

        if (Owner != null)
            Owner.Active = false;
    }
}
