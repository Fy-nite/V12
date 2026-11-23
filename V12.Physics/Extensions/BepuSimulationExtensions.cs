using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using V12.Components;
using V12.Core.Interfaces.Renderer;

namespace V12.Physics.Extensions
{
    /// <summary>
    /// One-call spawn helpers for code that drives the raw Bepu simulation
    /// directly (prototypes, tools, tests) instead of going through elements.
    /// </summary>
    public static class BepuSimulationExtensions
    {
        public static BodyHandle AddDynamicBody(this Simulation simulation, RigidPose pose,
            MeshShape shape, Vector3 size, float mass, IMeshRenderable? mesh = null)
        {
            var shapeIndex = simulation.AddShape(shape, size, mesh);
            var inertia = BepuInertia.ForShape(shape, size, mass);
            return simulation.Bodies.Add(BodyDescription.CreateDynamic(
                pose, inertia, new CollidableDescription(shapeIndex, 0.1f), new BodyActivityDescription(0.01f)));
        }

        public static StaticHandle AddStaticShape(this Simulation simulation, RigidPose pose,
            MeshShape shape, Vector3 size, IMeshRenderable? mesh = null)
        {
            var shapeIndex = simulation.AddShape(shape, size, mesh);
            return simulation.Statics.Add(new StaticDescription(pose, shapeIndex));
        }
    }
}
