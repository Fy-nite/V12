using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using V12.Components;

namespace V12.Physics.Extensions
{
    /// <summary>
    /// Inertia helpers for Bepu shapes. V12 previously created every dynamic body
    /// with <c>BodyInertia { InverseMass = 1/m }</c>, which leaves the inverse
    /// inertia tensor at zero (infinite rotational inertia) and makes torque a
    /// no-op. These helpers derive a real tensor from the shape and mass.
    /// </summary>
    public static class BepuInertia
    {
        public static BodyInertia ForShape(MeshShape shape, Vector3 size, float mass)
        {
            mass = mass > 0f ? mass : 1f;

            return shape switch
            {
                MeshShape.Sphere => new Sphere(Half(size.X)).ComputeInertia(mass),
                MeshShape.Capsule => new Capsule(Half(size.X), size.Y).ComputeInertia(mass),
                MeshShape.Cylinder => new Cylinder(Half(size.X), size.Y).ComputeInertia(mass),
                MeshShape.Plane => BoxInertia(new Vector3(size.X, 0.1f, size.Z), mass),
                MeshShape.Box => BoxInertia(size, mass),
                // A custom mesh might be open, concave, or non-manifold. A box
                // approximation from the collider bounds is predictable and keeps
                // the body stable; callers that need exact mass properties can
                // replace the inertia through the body API.
                _ => BoxInertia(size, mass),
            };
        }

        public static BodyInertia ForMesh(Mesh mesh, float mass)
        {
            mass = mass > 0f ? mass : 1f;
            try
            {
                return mesh.ComputeClosedInertia(mass);
            }
            catch
            {
                // Open or degenerate meshes have no closed-form mass properties.
                return BoxInertia(Vector3.One, mass);
            }
        }

        public static BodyInertia BoxInertia(Vector3 size, float mass)
        {
            mass = mass > 0f ? mass : 1f;
            var safe = new Vector3(
                size.X > 0f ? size.X : 0.01f,
                size.Y > 0f ? size.Y : 0.01f,
                size.Z > 0f ? size.Z : 0.01f);
            return new Box(safe.X, safe.Y, safe.Z).ComputeInertia(mass);
        }

        /// <summary>
        /// Override the inverse mass on an inertia value. Useful when a shape's
        /// computed tensor is kept but the gameplay mass is authoritative.
        /// </summary>
        public static BodyInertia WithMass(this BodyInertia inertia, float mass)
        {
            inertia.InverseMass = mass > 0f ? 1f / mass : 0f;
            return inertia;
        }

        private static float Half(float diameter) => MathF.Max(diameter * 0.5f, 0.001f);
    }
}
