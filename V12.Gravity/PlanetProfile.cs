using System;

namespace V12.Gravity
{
    /// <summary>
    /// A planet's gravity profile: radius, surface gravity, and the derived
    /// standard gravitational parameter. The numbers for the EcoVR working
    /// planet come from <c>Docs/EcoVR-Planet-Design-Doc.docx</c>.
    /// </summary>
    public readonly struct PlanetProfile
    {
        /// <summary>Newtonian gravitational constant (m^3 / (kg s^2)).</summary>
        public const double G = 6.67430e-11;

        public readonly string Name;
        /// <summary>Mean radius in metres.</summary>
        public readonly double Radius;
        /// <summary>Surface gravity in m/s^2.</summary>
        public readonly double SurfaceGravity;
        /// <summary>Optional sidereal day length in seconds; 0 means undecided.</summary>
        public readonly double RotationPeriod;

        public PlanetProfile(string name, double radius, double surfaceGravity, double rotationPeriod = 0)
        {
            if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
            if (surfaceGravity <= 0) throw new ArgumentOutOfRangeException(nameof(surfaceGravity));
            Name = name;
            Radius = radius;
            SurfaceGravity = surfaceGravity;
            RotationPeriod = Math.Max(0, rotationPeriod);
        }

        /// <summary>Standard gravitational parameter, mu = g * R^2.</summary>
        public double Mu => SurfaceGravity * Radius * Radius;

        public double Mass => Mu / G;

        /// <summary>Circular orbital speed at an altitude above the surface.</summary>
        public double CircularSpeed(double altitude = 0)
        {
            double r = Radius + altitude;
            return r > 0 ? Math.Sqrt(Mu / r) : 0;
        }

        /// <summary>Escape speed at an altitude above the surface.</summary>
        public double EscapeSpeed(double altitude = 0)
        {
            double r = Radius + altitude;
            return r > 0 ? Math.Sqrt(2 * Mu / r) : 0;
        }

        /// <summary>Period of a circular orbit at an altitude above the surface.</summary>
        public double OrbitalPeriod(double altitude = 0)
        {
            double r = Radius + altitude;
            return r > 0 ? 2 * Math.PI * Math.Sqrt(r * r * r / Mu) : 0;
        }

        /// <summary>Radius of a geostationary orbit, or 0 when the day length is unset.</summary>
        public double GeostationaryRadius
        {
            get
            {
                if (RotationPeriod <= 0) return 0;
                double r3 = Mu * RotationPeriod * RotationPeriod / (4 * Math.PI * Math.PI);
                return Math.Cbrt(r3);
            }
        }

        /// <summary>The EcoVR working planet: 2,400 km radius, 1g.</summary>
        public static PlanetProfile EcoVRWorking => new("EcoVR working planet", 2_400_000, 9.81);

        public static PlanetProfile Earth => new("Earth", 6_371_000, 9.80665, 86_164.09);

        public static PlanetProfile Moon => new("Moon", 1_737_400, 1.62, 2_360_591.5);

        public override string ToString()
            => $"{Name}(R:{Radius / 1000:F0} km, g:{SurfaceGravity:F2}, mu:{Mu:G6})";
    }
}
