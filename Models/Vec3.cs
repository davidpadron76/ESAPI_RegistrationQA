using System;
using System.Globalization;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Vector/punto 3D en coordenadas de paciente DICOM (mm).
    ///
    /// Se define un tipo propio en lugar de reutilizar System.Windows.Media.Media3D para
    /// que toda la capa de cálculo quede libre de dependencias de WPF y sea verificable
    /// de forma aislada.
    /// </summary>
    public struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator *(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        public static Vec3 operator *(double s, Vec3 a) { return a * s; }

        public double Dot(Vec3 other) { return X * other.X + Y * other.Y + Z * other.Z; }

        public Vec3 Cross(Vec3 o)
        {
            return new Vec3(
                Y * o.Z - Z * o.Y,
                Z * o.X - X * o.Z,
                X * o.Y - Y * o.X);
        }

        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }

        public Vec3 Normalized()
        {
            double length = Length;
            if (length <= double.Epsilon) return Zero;
            return new Vec3(X / length, Y / length, Z / length);
        }

        public bool IsFinite
        {
            get
            {
                return !double.IsNaN(X) && !double.IsInfinity(X)
                    && !double.IsNaN(Y) && !double.IsInfinity(Y)
                    && !double.IsNaN(Z) && !double.IsInfinity(Z);
            }
        }

        public bool Equals(Vec3 other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        }

        public override bool Equals(object obj)
        {
            return obj is Vec3 && Equals((Vec3)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", X, Y, Z);
        }
    }
}
