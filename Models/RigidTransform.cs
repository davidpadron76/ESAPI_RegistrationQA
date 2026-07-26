using System;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>Ángulos de Euler en grados, convención intrínseca Rz·Ry·Rx.</summary>
    public struct EulerAngles
    {
        public readonly double PitchX;
        public readonly double RollY;
        public readonly double YawZ;

        /// <summary>true si la extracción cayó en bloqueo de cardán (|pitch| ≈ 90°).</summary>
        public readonly bool GimbalLock;

        public EulerAngles(double pitchX, double rollY, double yawZ, bool gimbalLock)
        {
            PitchX = pitchX;
            RollY = rollY;
            YawZ = yawZ;
            GimbalLock = gimbalLock;
        }
    }

    /// <summary>
    /// Transformación rígida homogénea 4x4 en coordenadas de paciente.
    ///
    /// Convención interna: P' = M · P con P en columna, es decir, la traslación reside en
    /// la última COLUMNA (m[0..2,3]) y la última fila es (0,0,0,1). Las matrices leídas de
    /// la API se normalizan a esta convención en <see cref="TryFromRawMatrix"/>.
    /// </summary>
    public sealed class RigidTransform
    {
        private readonly double[,] _m;

        private RigidTransform(double[,] m)
        {
            _m = m;
        }

        public double this[int row, int column]
        {
            get { return _m[row, column]; }
        }

        public static RigidTransform Identity
        {
            get
            {
                var m = new double[4, 4];
                m[0, 0] = m[1, 1] = m[2, 2] = m[3, 3] = 1.0;
                return new RigidTransform(m);
            }
        }

        public static RigidTransform FromRotationAndTranslation(double[,] rotation3x3, Vec3 translation)
        {
            var m = new double[4, 4];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    m[r, c] = rotation3x3[r, c];

            m[0, 3] = translation.X;
            m[1, 3] = translation.Y;
            m[2, 3] = translation.Z;
            m[3, 3] = 1.0;
            return new RigidTransform(m);
        }

        public Vec3 Translation
        {
            get { return new Vec3(_m[0, 3], _m[1, 3], _m[2, 3]); }
        }

        public Vec3 Apply(Vec3 p)
        {
            return new Vec3(
                _m[0, 0] * p.X + _m[0, 1] * p.Y + _m[0, 2] * p.Z + _m[0, 3],
                _m[1, 0] * p.X + _m[1, 1] * p.Y + _m[1, 2] * p.Z + _m[1, 3],
                _m[2, 0] * p.X + _m[2, 1] * p.Y + _m[2, 2] * p.Z + _m[2, 3]);
        }

        /// <summary>
        /// Inversa analítica válida para transformaciones rígidas: R⁻¹ = Rᵀ y t⁻¹ = −Rᵀ·t.
        /// Devuelve false si la submatriz de rotación no es ortonormal, en cuyo caso esta
        /// fórmula no aplica y el resultado sería silenciosamente incorrecto.
        /// </summary>
        public bool TryInvert(out RigidTransform inverse)
        {
            double determinant;
            if (!IsRotationOrthonormal(1e-3, out determinant))
            {
                inverse = null;
                return false;
            }

            var m = new double[4, 4];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    m[r, c] = _m[c, r];

            Vec3 t = Translation;
            m[0, 3] = -(m[0, 0] * t.X + m[0, 1] * t.Y + m[0, 2] * t.Z);
            m[1, 3] = -(m[1, 0] * t.X + m[1, 1] * t.Y + m[1, 2] * t.Z);
            m[2, 3] = -(m[2, 0] * t.X + m[2, 1] * t.Y + m[2, 2] * t.Z);
            m[3, 3] = 1.0;

            inverse = new RigidTransform(m);
            return true;
        }

        /// <summary>
        /// Verifica que la submatriz 3x3 es una rotación pura: columnas unitarias, mutuamente
        /// ortogonales y determinante +1. Si hay escalado o reflexión, los ángulos de Euler
        /// extraídos carecen de sentido físico y hay que decirlo en vez de reportarlos.
        /// </summary>
        public bool IsRotationOrthonormal(double tolerance, out double determinant)
        {
            var c0 = new Vec3(_m[0, 0], _m[1, 0], _m[2, 0]);
            var c1 = new Vec3(_m[0, 1], _m[1, 1], _m[2, 1]);
            var c2 = new Vec3(_m[0, 2], _m[1, 2], _m[2, 2]);

            determinant = c0.Dot(c1.Cross(c2));

            if (Math.Abs(c0.Length - 1.0) > tolerance) return false;
            if (Math.Abs(c1.Length - 1.0) > tolerance) return false;
            if (Math.Abs(c2.Length - 1.0) > tolerance) return false;
            if (Math.Abs(c0.Dot(c1)) > tolerance) return false;
            if (Math.Abs(c0.Dot(c2)) > tolerance) return false;
            if (Math.Abs(c1.Dot(c2)) > tolerance) return false;

            return Math.Abs(determinant - 1.0) <= tolerance;
        }

        /// <summary>
        /// Extrae los ángulos de Euler (convención Rz·Ry·Rx) tratando explícitamente el
        /// bloqueo de cardán. La versión anterior usaba Atan2(m21, m22) sin protección: con
        /// pitch cercano a ±90° ambos argumentos tienden a cero, Atan2(0,0) devuelve 0 y los
        /// ángulos se degradaban en silencio.
        /// </summary>
        public EulerAngles GetEulerAnglesDegrees()
        {
            const double radiansToDegrees = 180.0 / Math.PI;

            double m00 = _m[0, 0], m10 = _m[1, 0], m20 = _m[2, 0];
            double m11 = _m[1, 1], m12 = _m[1, 2];
            double m21 = _m[2, 1], m22 = _m[2, 2];

            double cosPitch = Math.Sqrt(m21 * m21 + m22 * m22);
            bool gimbalLock = cosPitch < 1e-6;

            double rollY = Math.Atan2(-m20, cosPitch);
            double pitchX;
            double yawZ;

            if (!gimbalLock)
            {
                pitchX = Math.Atan2(m21, m22);
                yawZ = Math.Atan2(m10, m00);
            }
            else
            {
                // Degenerado: sólo la suma (o diferencia) pitch±yaw es observable.
                // Se fija yaw = 0 y se vuelca la rotación restante sobre pitch.
                pitchX = Math.Atan2(-m12, m11);
                yawZ = 0.0;
            }

            return new EulerAngles(
                pitchX * radiansToDegrees,
                rollY * radiansToDegrees,
                yawZ * radiansToDegrees,
                gimbalLock);
        }

        /// <summary>
        /// Máximo desplazamiento inducido dentro de un volumen. Para una aplicación afín el
        /// módulo del desplazamiento es una función convexa del punto, de modo que su máximo
        /// sobre una caja se alcanza siempre en un vértice: evaluar los ocho vértices da el
        /// valor exacto, no una estimación.
        /// </summary>
        public double MaxDisplacementOver(ImageGeometry geometry)
        {
            double max = 0.0;
            foreach (Vec3 corner in geometry.BoundingBoxCorners())
            {
                double displacement = (Apply(corner) - corner).Length;
                if (displacement > max) max = displacement;
            }
            return max;
        }

        /// <summary>
        /// Normaliza una matriz 4x4 cruda de la API a la convención interna.
        ///
        /// Existen dos convenciones habituales y son indistinguibles mirando sólo la
        /// submatriz 3x3 (la traspuesta de una matriz ortonormal también es ortonormal), por
        /// lo que confundirlas invierte el signo de todos los ángulos sin que nada falle. El
        /// discriminante fiable es dónde está la traslación:
        ///   - columna (P' = M·P): última fila = (0,0,0,1) → se usa tal cual
        ///   - fila    (P' = P·M): última columna = (0,0,0,1)ᵀ → se traspone
        /// </summary>
        public static bool TryFromRawMatrix(double[,] raw, out RigidTransform transform, out string conventionNote)
        {
            transform = null;
            conventionNote = null;

            if (raw == null || raw.GetLength(0) != 4 || raw.GetLength(1) != 4)
            {
                conventionNote = "la matriz recuperada no es 4x4";
                return false;
            }

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (double.IsNaN(raw[r, c]) || double.IsInfinity(raw[r, c]))
                    {
                        conventionNote = "la matriz contiene valores no finitos";
                        return false;
                    }
                }
            }

            const double tolerance = 1e-6;
            bool lastRowIsHomogeneous =
                Math.Abs(raw[3, 0]) < tolerance && Math.Abs(raw[3, 1]) < tolerance &&
                Math.Abs(raw[3, 2]) < tolerance && Math.Abs(raw[3, 3] - 1.0) < tolerance;

            bool lastColumnIsHomogeneous =
                Math.Abs(raw[0, 3]) < tolerance && Math.Abs(raw[1, 3]) < tolerance &&
                Math.Abs(raw[2, 3]) < tolerance && Math.Abs(raw[3, 3] - 1.0) < tolerance;

            double[,] normalized;

            if (lastRowIsHomogeneous && !lastColumnIsHomogeneous)
            {
                normalized = (double[,])raw.Clone();
                conventionNote = "traslación en la última columna (P' = M·P)";
            }
            else if (lastColumnIsHomogeneous && !lastRowIsHomogeneous)
            {
                normalized = Transpose(raw);
                conventionNote = "traslación en la última fila (P' = P·M); matriz traspuesta a la convención interna";
            }
            else if (lastRowIsHomogeneous && lastColumnIsHomogeneous)
            {
                // Traslación nula en ambas lecturas: sólo rotación, las dos son válidas para
                // la traslación pero difieren en el sentido de la rotación. Se asume la
                // convención estándar y se deja constancia.
                normalized = (double[,])raw.Clone();
                conventionNote = "traslación nula; se asume convención P' = M·P (rotación pura, sentido no verificable)";
            }
            else
            {
                conventionNote = "no se pudo determinar la convención de la matriz: ni la última fila ni la última columna son homogéneas";
                return false;
            }

            var candidate = new RigidTransform(normalized);
            double determinant;
            if (!candidate.IsRotationOrthonormal(1e-3, out determinant))
            {
                conventionNote = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "la submatriz de rotación no es ortonormal (det = {0:F4}); la transformación incluye escalado, cizalla o reflexión",
                    determinant);
                return false;
            }

            transform = candidate;
            return true;
        }

        /// <summary>
        /// Transformación rígida relativa entre dos marcos de imagen, deducida de sus
        /// cosenos directores y orígenes: R = R_hasta · R_desdeᵀ y t = O_hasta − R · O_desde.
        ///
        /// Sustituye al cálculo anterior, que restaba componentes sueltas de los vectores
        /// director y las multiplicaba por 180/π. Esa resta sólo se aproxima al ángulo real
        /// bajo la hipótesis de ángulo pequeño y para el eje que casualmente coincidiera.
        /// </summary>
        public static RigidTransform FromFrames(ImageGeometry from, ImageGeometry to)
        {
            // Columnas de R_desde y R_hasta son los cosenos directores de cada marco.
            var rotation = new double[3, 3];

            Vec3[] fromAxes = { from.XDirection, from.YDirection, from.ZDirection };
            Vec3[] toAxes = { to.XDirection, to.YDirection, to.ZDirection };

            // R = R_hasta · R_desdeᵀ  →  R[r,c] = Σ_a  toAxes[a][r] · fromAxes[a][c]
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double sum = 0.0;
                    for (int a = 0; a < 3; a++)
                        sum += Component(toAxes[a], r) * Component(fromAxes[a], c);
                    rotation[r, c] = sum;
                }
            }

            var rotated = new Vec3(
                rotation[0, 0] * from.Origin.X + rotation[0, 1] * from.Origin.Y + rotation[0, 2] * from.Origin.Z,
                rotation[1, 0] * from.Origin.X + rotation[1, 1] * from.Origin.Y + rotation[1, 2] * from.Origin.Z,
                rotation[2, 0] * from.Origin.X + rotation[2, 1] * from.Origin.Y + rotation[2, 2] * from.Origin.Z);

            return FromRotationAndTranslation(rotation, to.Origin - rotated);
        }

        private static double Component(Vec3 v, int index)
        {
            if (index == 0) return v.X;
            if (index == 1) return v.Y;
            return v.Z;
        }

        private static double[,] Transpose(double[,] m)
        {
            var t = new double[4, 4];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    t[r, c] = m[c, r];
            return t;
        }
    }
}
