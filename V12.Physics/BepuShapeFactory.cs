using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using V12.Components;
using V12.Core.Interfaces.Renderer;

namespace V12.Physics;

/// <summary>Builds Bepu shape indices from V12's generic collider description.</summary>
internal static class BepuShapeFactory
{
    public static TypedIndex Create(Simulation simulation, BufferPool pool, MeshShape shape, Vector3 size,
        IMeshRenderable? mesh = null)
    {
        switch (shape)
        {
            case MeshShape.Sphere:
                return simulation.Shapes.Add(new Sphere(Half(size.X)));
            case MeshShape.Capsule:
                return simulation.Shapes.Add(new Capsule(Half(size.X), size.Y));
            case MeshShape.Cylinder:
                return simulation.Shapes.Add(new Cylinder(Half(size.X), size.Y));
            case MeshShape.Plane:
                return simulation.Shapes.Add(new Box(size.X, 0.1f, size.Z));
            case MeshShape.Custom when mesh != null:
                return CreateMesh(simulation, pool, mesh);
            case MeshShape.Box:
            default:
                return simulation.Shapes.Add(new Box(size.X, size.Y, size.Z));
        }
    }

    private static TypedIndex CreateMesh(Simulation simulation, BufferPool pool, IMeshRenderable mesh)
    {
        var points = mesh.MeshPoints;
        var indices = mesh.Indices;

        if (points == null || indices == null || indices.Length < 3)
            return simulation.Shapes.Add(new Box(1f, 1f, 1f));

        int triangleCount = indices.Length / 3;
        pool.Take<BepuPhysics.Collidables.Triangle>(triangleCount, out var triangles);

        for (int i = 0; i < triangleCount; i++)
        {
            int i0 = (int)indices[i * 3];
            int i1 = (int)indices[i * 3 + 1];
            int i2 = (int)indices[i * 3 + 2];

            triangles[i] = new BepuPhysics.Collidables.Triangle(
                new Vector3((float)points[i0 * 3], (float)points[i0 * 3 + 1], (float)points[i0 * 3 + 2]),
                new Vector3((float)points[i1 * 3], (float)points[i1 * 3 + 1], (float)points[i1 * 3 + 2]),
                new Vector3((float)points[i2 * 3], (float)points[i2 * 3 + 1], (float)points[i2 * 3 + 2]));
        }

        var bepuMesh = new BepuPhysics.Collidables.Mesh(triangles, Vector3.One, pool);
        return simulation.Shapes.Add(bepuMesh);
    }

    private static float Half(float diameter) => MathF.Max(diameter * 0.5f, 0.001f);
}
