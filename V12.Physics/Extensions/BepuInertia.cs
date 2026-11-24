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
                MeshShape.Capsule => CapsuleInertia(size, mass),
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
        /// Rescale an inertia value to a new mass. The inverse inertia tensor
        /// scales with <c>1 / mass</c>, so changing only
        /// <see cref="BodyInertia.InverseMass"/> would leave the angular response
        /// at the old mass. A non-positive mass makes the body immovable.
        /// </summary>
        public static BodyInertia WithMass(this BodyInertia inertia, float mass)
        {
            if (mass <= 0f)
            {
                inertia.InverseMass = 0f;
                inertia.InverseInertiaTensor = default;
                return inertia;
            }

            float oldInverseMass = inertia.InverseMass;
            if (oldInverseMass > 0f)
            {
                float scale = (1f / oldInverseMass) / mass;
                var tensor = inertia.InverseInertiaTensor;
                tensor.XX *= scale;
                tensor.YX *= scale;
                tensor.YY *= scale;
                tensor.ZX *= scale;
                tensor.ZY *= scale;
                tensor.ZZ *= scale;
                inertia.InverseInertiaTensor = tensor;
            }

            inertia.InverseMass = 1f / mass;
            return inertia;
        }

        private static BodyInertia CapsuleInertia(Vector3 size, float mass)
        {
            var (radius, segment) = BepuShapeFactory.CapsuleDimensions(size);
            return new Capsule(radius, segment).ComputeInertia(mass);
        }

        private static float Half(float diameter) => MathF.Max(diameter * 0.5f, 0.001f);
    }
}
