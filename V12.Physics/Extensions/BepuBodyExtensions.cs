using System.Numerics;
using BepuPhysics;

namespace V12.Physics.Extensions
{
    /// <summary>
    /// Convenience helpers over Bepu's <see cref="BodyReference"/>. Bepu's own
    /// impulse methods are instantaneous; these add the continuous force/torque
    /// form V12 exposes through <see cref="V12.Core.Interfaces.Physics.IPhysicsBody"/>.
    /// </summary>
    public static class BepuBodyExtensions
    {
        public static float GetMass(this BodyReference body)
        {
            float inverseMass = body.LocalInertia.InverseMass;
            return inverseMass > 0f ? 1f / inverseMass : 0f;
        }

        public static void SetMass(this BodyReference body, float mass, in BodyInertia inertia)
        {
            body.SetLocalInertia(inertia.WithMass(mass));
            body.Awake = true;
        }

        /// <summary>Apply a force for one step: impulse = force * deltaTime.</summary>
        public static void ApplyForce(this BodyReference body, Vector3 force, float deltaTime)
        {
            body.ApplyLinearImpulse(force * deltaTime);
            body.Awake = true;
        }

        /// <summary>Apply a torque for one step: angular impulse = torque * deltaTime.</summary>
        public static void ApplyTorque(this BodyReference body, Vector3 torque, float deltaTime)
        {
            body.ApplyAngularImpulse(torque * deltaTime);
            body.Awake = true;
        }

        /// <summary>Wake the body so a force or impulse written this tick is integrated.</summary>
        public static void Wake(this BodyReference body) => body.Awake = true;
    }
}
