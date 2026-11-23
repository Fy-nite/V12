using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using V12.Components;
using V12.Core.Interfaces.Renderer;

namespace V12.Physics.Extensions
{
    /// <summary>
    /// Public shape helpers for code that talks to the raw Bepu simulation.
    /// </summary>
    public static class BepuShapeExtensions
    {
        /// <summary>
        /// Adds a Bepu shape from V12's generic collider description. Uses the
        /// simulation's own buffer pool, so no extra pool needs to be threaded
        /// through game code.
        /// </summary>
        public static TypedIndex AddShape(this Simulation simulation, MeshShape shape, Vector3 size,
            IMeshRenderable? mesh = null)
        {
            return BepuShapeFactory.Create(simulation, simulation.BufferPool, shape, size, mesh);
        }

        /// <summary>Shape-derived inertia for a V12 collider description.</summary>
        public static BodyInertia ComputeInertia(this MeshShape shape, Vector3 size, float mass)
        {
            return BepuInertia.ForShape(shape, size, mass);
        }
    }
}
