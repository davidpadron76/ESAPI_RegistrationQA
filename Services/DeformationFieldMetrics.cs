using System;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Jacobian, smoothness and maximum displacement computed from the deformation vector field
    /// read by <see cref="DeformationFieldReader"/>.
    ///
    /// These are the three TG-132 Table III deformation quantities the analyzer has always
    /// reported as unobtainable for a deformable registration. They are unobtainable from the
    /// *linear component* — which describes a different transform — but the field itself turned
    /// out to be readable, so they are computed here from the real thing.
    ///
    /// Every derivative uses the field's own grid spacing. The field's grid is coarser than the
    /// image's and need not share its origin, so substituting the image resolution would scale
    /// every gradient by the ratio between the two and silently produce a plausible, wrong
    /// Jacobian.
    /// </summary>
    public static class DeformationFieldMetrics
    {
        public sealed class Result
        {
            /// <summary>Percentage of interior grid points where det(J) &lt;= 0 — folding.</summary>
            public double NegativeJacobianPercent;

            public double MinJacobian;

            /// <summary>Interior points the Jacobian was evaluated at (the border has no central difference).</summary>
            public int JacobianSampleCount;

            /// <summary>
            /// Largest displacement-gradient magnitude found, as a dimensionless ratio
            /// (mm of displacement change per mm of distance travelled).
            /// </summary>
            public double MaxGradientMagnitude;

            /// <summary>Maximum |displacement| over the whole field, in millimetres.</summary>
            public double MaxDisplacementMm;
        }

        /// <summary>
        /// Central differences over the interior of the grid.
        ///
        /// A central difference needs a neighbour on both sides, so the outermost shell in each
        /// axis is skipped rather than approximated with a one-sided difference: a one-sided
        /// derivative at the boundary has a different error order, and mixing the two would put
        /// a systematically noisier estimate into the same "% negative" statistic as the
        /// interior. A field with fewer than three samples on any axis has no interior at all
        /// and is rejected.
        /// </summary>
        public static Result Compute(DeformationFieldReader.Result field, out string problem)
        {
            problem = null;
            if (field == null || field.Vectors == null)
            {
                problem = "no deformation field was read";
                return null;
            }

            int nx = field.XSize, ny = field.YSize, nz = field.ZSize;

            double maxDisplacement = 0.0;
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        double length = field.Vectors[x, y, z].Length;
                        if (length > maxDisplacement) maxDisplacement = length;
                    }

            if (nx < 3 || ny < 3 || nz < 3)
            {
                problem = "the field grid is " + nx + "x" + ny + "x" + nz +
                          "; a central difference needs at least three samples on every axis, so no " +
                          "Jacobian or gradient can be evaluated";
                return null;
            }

            int negative = 0, samples = 0;
            double minJacobian = double.MaxValue;
            double maxGradient = 0.0;

            for (int z = 1; z < nz - 1; z++)
            {
                for (int y = 1; y < ny - 1; y++)
                {
                    for (int x = 1; x < nx - 1; x++)
                    {
                        // Partial derivatives of the displacement field u with respect to each
                        // grid axis, in mm of displacement per mm of distance.
                        Vec3 dudx = (field.Vectors[x + 1, y, z] - field.Vectors[x - 1, y, z]) *
                                    (1.0 / (2.0 * field.XResMm));
                        Vec3 dudy = (field.Vectors[x, y + 1, z] - field.Vectors[x, y - 1, z]) *
                                    (1.0 / (2.0 * field.YResMm));
                        Vec3 dudz = (field.Vectors[x, y, z + 1] - field.Vectors[x, y, z - 1]) *
                                    (1.0 / (2.0 * field.ZResMm));

                        if (!dudx.IsFinite || !dudy.IsFinite || !dudz.IsFinite) continue;

                        // The transform is x + u(x), so its Jacobian is I + grad(u). Taking
                        // det(grad u) alone would be a different quantity that is near zero for
                        // a near-rigid field rather than near one.
                        double j = Determinant(
                            1.0 + dudx.X, dudy.X, dudz.X,
                            dudx.Y, 1.0 + dudy.Y, dudz.Y,
                            dudx.Z, dudy.Z, 1.0 + dudz.Z);

                        if (double.IsNaN(j) || double.IsInfinity(j)) continue;

                        samples++;
                        if (j <= 0.0) negative++;
                        if (j < minJacobian) minJacobian = j;

                        double gradient = Math.Sqrt(
                            dudx.X * dudx.X + dudx.Y * dudx.Y + dudx.Z * dudx.Z +
                            dudy.X * dudy.X + dudy.Y * dudy.Y + dudy.Z * dudy.Z +
                            dudz.X * dudz.X + dudz.Y * dudz.Y + dudz.Z * dudz.Z);

                        if (gradient > maxGradient) maxGradient = gradient;
                    }
                }
            }

            if (samples == 0)
            {
                problem = "no interior grid point yielded a finite Jacobian";
                return null;
            }

            return new Result
            {
                NegativeJacobianPercent = 100.0 * negative / samples,
                MinJacobian = minJacobian,
                JacobianSampleCount = samples,
                MaxGradientMagnitude = maxGradient,
                MaxDisplacementMm = maxDisplacement
            };
        }

        private static double Determinant(
            double m00, double m01, double m02,
            double m10, double m11, double m12,
            double m20, double m21, double m22)
        {
            return m00 * (m11 * m22 - m12 * m21)
                 - m01 * (m10 * m22 - m12 * m20)
                 + m02 * (m10 * m21 - m11 * m20);
        }
    }
}
