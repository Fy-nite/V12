using System;
using System.Numerics;

namespace V12.Gravity
{
    /// <summary>
    /// A two-body orbital state in double precision. The state is relative to the
    /// primary body, so <see cref="Position"/> and <see cref="Velocity"/> are the
    /// vessel's vectors around that body. Use this for on-rails propagation at
    /// time warp, and keep the local physics body for the active vessel.
    /// </summary>
    public readonly struct OrbitalState
    {
        public readonly Double3 Position;
        public readonly Double3 Velocity;
        public readonly double Mu;

        public OrbitalState(Double3 position, Double3 velocity, double mu)
        {
            if (mu <= 0) throw new ArgumentOutOfRangeException(nameof(mu), "Gravitational parameter must be positive.");
            Position = position;
            Velocity = velocity;
            Mu = mu;
        }

        public double Radius => Position.Length;
        public double Speed => Velocity.Length;

        /// <summary>Specific orbital energy (J/kg). Negative for a bound orbit.</summary>
        public double SpecificEnergy => Velocity.LengthSquared * 0.5 - Mu / Radius;

        /// <summary>Specific angular momentum vector.</summary>
        public Double3 AngularMomentum => Double3.Cross(Position, Velocity);

        /// <summary>Unit normal of the orbital plane, following the right-hand rule.</summary>
        public Double3 OrbitNormal => AngularMomentum.Normalized();

        /// <summary>Eccentricity vector, pointing at periapsis.</summary>
        public Double3 EccentricityVector
        {
            get
            {
                var h = AngularMomentum;
                return Double3.Cross(Velocity, h) / Mu - Position.Normalized();
            }
        }

        public double Eccentricity => EccentricityVector.Length;
        public bool IsBound => SpecificEnergy < 0;

        /// <summary>Semi-major axis. Positive for ellipses, negative for hyperbolas,
        /// and infinity for a parabola.</summary>
        public double SemiMajorAxis
        {
            get
            {
                double energy = SpecificEnergy;
                if (Math.Abs(energy) < 1e-12) return double.PositiveInfinity;
                return -Mu / (2 * energy);
            }
        }

        /// <summary>Orbital period in seconds for a bound orbit, otherwise infinity.</summary>
        public double Period
        {
            get
            {
                double a = SemiMajorAxis;
                if (!IsBound || a <= 0) return double.PositiveInfinity;
                return 2 * Math.PI * Math.Sqrt(a * a * a / Mu);
            }
        }

        public double Apoapsis
        {
            get
            {
                double a = SemiMajorAxis;
                if (a <= 0) return double.PositiveInfinity;
                return a * (1 + Eccentricity);
            }
        }

        public double Periapsis
        {
            get
            {
                double a = SemiMajorAxis;
                if (double.IsInfinity(a)) return Radius;
                return a * (1 - Eccentricity);
            }
        }

        /// <summary>Propagate this two-body state by <paramref name="deltaTime"/> seconds.</summary>
        public OrbitalState Propagate(double deltaTime) => OrbitalMechanics.Propagate(this, deltaTime);

        public static OrbitalState FromVectors(double mu, Vector3 position, Vector3 velocity)
            => new(Double3.FromVector3(position), Double3.FromVector3(velocity), mu);

        public override string ToString()
            => $"r={Position} v={Velocity} e={Eccentricity:F4} a={SemiMajorAxis:G6} T={Period:G6}s";
    }

    public static class OrbitalMechanics
    {
        public static double CircularSpeed(double mu, double radius)
        {
            if (mu <= 0 || radius <= 0) return 0;
            return Math.Sqrt(mu / radius);
        }

        public static double EscapeSpeed(double mu, double radius)
        {
            if (mu <= 0 || radius <= 0) return 0;
            return Math.Sqrt(2 * mu / radius);
        }

        public static double PeriodForSemiMajorAxis(double mu, double semiMajorAxis)
        {
            if (mu <= 0 || semiMajorAxis <= 0) return double.PositiveInfinity;
            return 2 * Math.PI * Math.Sqrt(semiMajorAxis * semiMajorAxis * semiMajorAxis / mu);
        }

        /// <summary>
        /// Radius of the smaller body's sphere of influence around the primary.
        /// Used by patched-conic transitions: only the dominant body acts inside
        /// the SOI. Standard approximation, <c>a * (m / M)^(2/5)</c>.
        /// </summary>
        public static double SphereOfInfluence(double primaryMu, double secondaryMu, double separation)
        {
            if (primaryMu <= 0 || secondaryMu <= 0 || separation <= 0)
                return 0;
            return separation * Math.Pow(secondaryMu / primaryMu, 0.4);
        }

        /// <summary>
        /// Universal-variable Kepler propagation. Handles elliptic, parabolic and
        /// hyperbolic states without patching conics.
        /// </summary>
        public static OrbitalState Propagate(OrbitalState state, double deltaTime)
        {
            if (deltaTime == 0) return state;

            var r0Vector = state.Position;
            var v0Vector = state.Velocity;
            double mu = state.Mu;
            double r0 = r0Vector.Length;
            if (r0 <= 1e-12) return state;

            double sqrtMu = Math.Sqrt(mu);
            double r0DotV0 = Double3.Dot(r0Vector, v0Vector);
            double alpha = 2.0 / r0 - v0Vector.LengthSquared / mu;
            double chi = InitialUniversalAnomaly(alpha, r0, r0DotV0, mu, sqrtMu, deltaTime);

            double f = 0, g = 0, fDot = 0, gDot = 0;
            bool converged = false;

            for (int iteration = 0; iteration < 100; iteration++)
            {
                double z = alpha * chi * chi;
                var (c, s) = Stumpff(z);

                // Stumpff overflows for very large hyperbolic z. Treat that as
                // a failed Newton step and let the numeric fallback answer,
                // rather than returning a NaN state to gameplay.
                if (!double.IsFinite(c) || !double.IsFinite(s))
                    break;

                double chi2 = chi * chi;
                double chi3 = chi2 * chi;
                double r0Term = r0DotV0 / sqrtMu;

                double residual = r0Term * chi2 * c
                                + (1 - alpha * r0) * chi3 * s
                                + r0 * chi
                                - sqrtMu * deltaTime;

                double derivative = r0Term * chi * (1 - z * s)
                                  + (1 - alpha * r0) * chi2 * c
                                  + r0;

                if (!double.IsFinite(residual) || !double.IsFinite(derivative))
                    break;

                if (Math.Abs(derivative) < 1e-15)
                    break;

                double correction = residual / derivative;
                chi -= correction;

                if (!double.IsFinite(chi))
                    break;

                if (Math.Abs(correction) <= 1e-12 * Math.Max(1.0, Math.Abs(chi)))
                {
                    converged = true;
                    break;
                }
            }

            if (converged)
            {
                double z = alpha * chi * chi;
                var (c, s) = Stumpff(z);
                double chi2 = chi * chi;
                double chi3 = chi2 * chi;

                f = 1 - chi2 / r0 * c;
                g = deltaTime - chi3 / sqrtMu * s;

                var position = r0Vector * f + v0Vector * g;
                double radius = position.Length;
                if (radius > 1e-12)
                {
                    fDot = sqrtMu / (radius * r0) * (alpha * chi3 * s - chi);
                    gDot = 1 - chi2 / radius * c;
                    var velocity = r0Vector * fDot + v0Vector * gDot;
                    return new OrbitalState(position, velocity, mu);
                }
            }

            // Extremely eccentric or near-parabolic states can defeat Newton from
            // the cheap initial guess. Fall back to a small velocity-Verlet march
            // so gameplay still gets a finite answer.
            return PropagateNumerically(state, deltaTime);
        }

        private static double InitialUniversalAnomaly(double alpha, double r0, double r0DotV0,
            double mu, double sqrtMu, double deltaTime)
        {
            if (alpha > 1e-8)
                return sqrtMu * deltaTime * alpha;

            if (alpha < -1e-8)
            {
                double sign = Math.Sign(deltaTime);
                double numerator = -2 * mu * alpha * deltaTime;
                double denominator = r0DotV0 + sign * Math.Sqrt(-mu / alpha) * (1 - r0 * alpha);
                if (denominator > 1e-12 && numerator > 0)
                {
                    double guess = sign * Math.Sqrt(-1 / alpha) * Math.Log(numerator / denominator);
                    if (!double.IsNaN(guess) && !double.IsInfinity(guess))
                        return guess;
                }
            }

            return sqrtMu * deltaTime / Math.Max(r0, 1e-9);
        }

        private static (double C, double S) Stumpff(double z)
        {
            if (z > 1e-8)
            {
                double sqrtZ = Math.Sqrt(z);
                return ((1 - Math.Cos(sqrtZ)) / z,
                        (sqrtZ - Math.Sin(sqrtZ)) / (sqrtZ * sqrtZ * sqrtZ));
            }

            if (z < -1e-8)
            {
                double sqrtZ = Math.Sqrt(-z);
                return ((Math.Cosh(sqrtZ) - 1) / -z,
                        (Math.Sinh(sqrtZ) - sqrtZ) / (sqrtZ * sqrtZ * sqrtZ));
            }

            double z2 = z * z;
            return (0.5 - z / 24 + z2 / 720,
                    1.0 / 6 - z / 120 + z2 / 5040);
        }

        private static OrbitalState PropagateNumerically(OrbitalState state, double deltaTime)
        {
            double period = state.Period;
            double reference = double.IsInfinity(period) ? Math.Abs(deltaTime) : period;
            int steps = (int)Math.Clamp(Math.Ceiling(Math.Abs(deltaTime) / Math.Max(reference / 1024, 1e-3)), 1, 4096);
            double h = deltaTime / steps;

            var position = state.Position;
            var velocity = state.Velocity;
            double mu = state.Mu;

            for (int i = 0; i < steps; i++)
            {
                var acceleration = Acceleration(position, mu);
                var nextPosition = position + velocity * h + acceleration * (0.5 * h * h);
                var nextAcceleration = Acceleration(nextPosition, mu);
                velocity += (acceleration + nextAcceleration) * (0.5 * h);
                position = nextPosition;
            }

            return new OrbitalState(position, velocity, mu);
        }

        private static Double3 Acceleration(Double3 position, double mu)
        {
            double r2 = position.LengthSquared;
            if (r2 <= 1e-12) return Double3.Zero;
            double r = Math.Sqrt(r2);
            return position * (-mu / (r2 * r));
        }
    }
}
