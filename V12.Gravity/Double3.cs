using System;
using System.Numerics;

namespace V12.Gravity
{
    /// <summary>
    /// Minimal double-precision 3D vector for orbital math. V12's scene transforms
    /// are float32, but orbits at planet scale need more headroom, so propagation
    /// and element solving stay in doubles and only the final local state is
    /// converted back to <see cref="Vector3"/>.
    /// </summary>
    public readonly struct Double3 : IEquatable<Double3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Double3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Double3 Zero = new(0, 0, 0);
        public static readonly Double3 UnitX = new(1, 0, 0);
        public static readonly Double3 UnitY = new(0, 1, 0);
        public static readonly Double3 UnitZ = new(0, 0, 1);

        public double LengthSquared => X * X + Y * Y + Z * Z;
        public double Length => Math.Sqrt(LengthSquared);

        public Double3 Normalized()
        {
            double length = Length;
            return length > 1e-15 ? this / length : Zero;
        }

        public static double Dot(Double3 a, Double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Double3 Cross(Double3 a, Double3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Double3 operator -(Double3 a) => new(-a.X, -a.Y, -a.Z);
        public static Double3 operator *(Double3 a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        public static Double3 operator *(double scale, Double3 a) => new(a.X * scale, a.Y * scale, a.Z * scale);
        public static Double3 operator /(Double3 a, double divisor) => new(a.X / divisor, a.Y / divisor, a.Z / divisor);

        public static Double3 FromVector3(Vector3 value) => new(value.X, value.Y, value.Z);
        public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);

        public bool Equals(Double3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object? obj) => obj is Double3 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
    }
}
