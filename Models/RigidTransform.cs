using System;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>Euler angles in degrees, intrinsic Rz·Ry·Rx convention.</summary>
    public struct EulerAngles
    {
        public readonly double PitchX;
        public readonly double RollY;
        public readonly double YawZ;

        /// <summary>true when the extraction hit gimbal lock (|pitch| ≈ 90°).</summary>
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
    /// A 4x4 homogeneous rigid transform in patient coordinates.
    ///
    /// Internal convention: P' = M · P with P as a column, i.e. the translation lives in the
    /// last COLUMN (m[0..2,3]) and the last row is (0,0,0,1). Matrices read from the API are
    /// normalised to this convention in <see cref="TryFromRawMatrix"/>.
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
        /// Analytic inverse, valid for rigid transforms: R⁻¹ = Rᵀ and t⁻¹ = −Rᵀ·t.
        /// Returns false when the rotation submatrix is not orthonormal, in which case this
        /// formula does not apply and the result would be silently wrong.
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
        /// Verifies that the 3x3 submatrix is a pure rotation: unit columns, mutually
        /// orthogonal, determinant +1. If there is scaling or reflection, the extracted
        /// Euler angles carry no physical meaning and that must be reported rather than
        /// glossed over.
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
        /// Extracts the Euler angles (Rz·Ry·Rx convention) with explicit gimbal-lock
        /// handling. The previous version used Atan2(m21, m22) unguarded: with pitch near
        /// ±90° both arguments tend to zero, Atan2(0,0) returns 0 and the angles degraded
        /// silently.
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
                // Degenerate: only the sum (or difference) pitch±yaw is observable.
                // Yaw is pinned to 0 and the remaining rotation is folded into pitch.
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
        /// Largest displacement induced within a volume. For an affine map the displacement
        /// magnitude is a convex function of the point, so its maximum over a box is always
        /// attained at a vertex: evaluating the eight corners gives the exact value, not an
        /// estimate.
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
        /// Normalises a raw 4x4 matrix from the API to the internal convention.
        ///
        /// Two conventions are in common use and they are indistinguishable by looking at
        /// the 3x3 submatrix alone (the transpose of an orthonormal matrix is also
        /// orthonormal), so confusing them flips the sign of every angle without anything
        /// failing. The reliable discriminant is where the translation sits:
        ///   - column (P' = M·P): last row = (0,0,0,1) → used as is
        ///   - row    (P' = P·M): last column = (0,0,0,1)ᵀ → transposed
        /// </summary>
        public static bool TryFromRawMatrix(double[,] raw, out RigidTransform transform, out string conventionNote)
        {
            transform = null;
            conventionNote = null;

            if (raw == null || raw.GetLength(0) != 4 || raw.GetLength(1) != 4)
            {
                conventionNote = "the retrieved matrix is not 4x4";
                return false;
            }

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (double.IsNaN(raw[r, c]) || double.IsInfinity(raw[r, c]))
                    {
                        conventionNote = "the matrix contains non-finite values";
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
                conventionNote = "translation in the last column (P' = M·P)";
            }
            else if (lastColumnIsHomogeneous && !lastRowIsHomogeneous)
            {
                normalized = Transpose(raw);
                conventionNote = "translation in the last row (P' = P·M); matrix transposed to the internal convention";
            }
            else if (lastRowIsHomogeneous && lastColumnIsHomogeneous)
            {
                // Zero translation under both readings: rotation only. Both are valid for
                // the translation but differ in the sense of the rotation. The standard
                // convention is assumed and the ambiguity is recorded.
                normalized = (double[,])raw.Clone();
                conventionNote = "zero translation; assuming the P' = M·P convention (pure rotation, sense not verifiable)";
            }
            else
            {
                conventionNote = "could not determine the matrix convention: neither the last row nor the last column is homogeneous";
                return false;
            }

            var candidate = new RigidTransform(normalized);
            double determinant;
            if (!candidate.IsRotationOrthonormal(1e-3, out determinant))
            {
                conventionNote = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "the rotation submatrix is not orthonormal (det = {0:F4}); the transform includes scaling, shear or reflection",
                    determinant);
                return false;
            }

            transform = candidate;
            return true;
        }

        /// <summary>
        /// Relative rigid transform between two image frames, derived from their direction
        /// cosines and origins: R = R_to · R_fromᵀ and t = O_to − R · O_from.
        ///
        /// This replaces the previous calculation, which subtracted individual components of
        /// the direction vectors and multiplied them by 180/π. That subtraction only
        /// approximates the true angle under a small-angle assumption and for whichever axis
        /// happened to line up.
        /// </summary>
        public static RigidTransform FromFrames(ImageGeometry from, ImageGeometry to)
        {
            // The columns of R_from and R_to are the direction cosines of each frame.
            var rotation = new double[3, 3];

            Vec3[] fromAxes = { from.XDirection, from.YDirection, from.ZDirection };
            Vec3[] toAxes = { to.XDirection, to.YDirection, to.ZDirection };

            // R = R_to · R_fromᵀ  →  R[r,c] = Σ_a  toAxes[a][r] · fromAxes[a][c]
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
