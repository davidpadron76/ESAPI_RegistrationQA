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

            /// <summary>
            /// Range of the divergence of the displacement field, div u = trace(grad u).
            ///
            /// Not a TG-132 metric and not reported as one — it exists because Eclipse displays it
            /// alongside the Jacobian, which makes it a second quantity the plugin's reading of
            /// the field can be held against an independent implementation. It comes free: the
            /// trace is the diagonal of the gradient the Jacobian already needs.
            ///
            /// Dimensionless, and related to the determinant by det(I + grad u) = 1 + div u +
            /// higher-order terms, so the two agree closely only where the deformation is small.
            /// </summary>
            public double MinDivergence;
            public double MaxDivergence;

            /// <summary>
            /// Largest curl magnitude, |curl u| = |grad x u|. The rotational part of the field,
            /// where divergence is the volumetric part. Free from the same gradient.
            ///
            /// Like divergence, not a TG-132 metric and not reported as one — it is here because
            /// Eclipse may display it, and every additional quantity derived from the same field
            /// read is another chance to catch a misread before it reaches a report.
            /// </summary>
            public double MaxCurlMagnitude;

            /// <summary>
            /// Grid indices where <see cref="MaxCurlMagnitude"/> was found, and how many grid
            /// steps that sits from the nearest face of the evaluated region.
            ///
            /// Recorded to settle a specific disagreement. On the phantom case Eclipse reported a
            /// maximum curl of 2.15 against this code's 1.99, while the Jacobian, divergence,
            /// distance and all three displacement components matched exactly. The formula is
            /// pinned by contract tests, so the likeliest remaining difference is the domain: this
            /// code evaluates the interior only, since a central difference needs a neighbour on
            /// both sides, and if Eclipse evaluates the full grid with one-sided differences at
            /// the edges then a curl peaking in that outermost shell would explain the gap. A
            /// maximum found hard against the interior boundary supports that; one found well
            /// inside rules it out and the cause is elsewhere.
            /// </summary>
            public int[] MaxCurlAt;
            public int MaxCurlStepsFromEdge;

            /// <summary>
            /// Per-component displacement range, in millimetres.
            ///
            /// These settle a question the extremes of |u| cannot: whether the component this code
            /// calls X is the component Eclipse calls X. The audit labels axes by the DICOM patient
            /// convention (X left-right, Y anterior-posterior, Z cranio-caudal), and an earlier
            /// version of this project had Y and Z transposed. If Eclipse displays the field
            /// per component, comparing these three ranges against it settles the ordering without
            /// needing a phantom shifted by a known amount.
            /// </summary>
            public double[] MinDisplacementPerAxis;
            public double[] MaxDisplacementPerAxis;

            /// <summary>
            /// How many of the folded points lie within two grid steps of the edge of the field.
            ///
            /// TG-132 does not ask only whether a registration folds, but what the folding
            /// affects: "where the folding is confined to a region that does not affect the
            /// intended use, the influence should be evaluated". A physicist cannot make that
            /// judgement from a percentage alone. Folding hard against the boundary of the
            /// field's support is usually the algorithm running out of image to drive it —
            /// commonly air, or beyond the patient — while folding in the middle of the volume is
            /// inside the anatomy and is the case that matters.
            /// </summary>
            public int NegativeNearBoundary;

            /// <summary>
            /// Extent of the folded region in grid indices, or null when nothing folded. Reported
            /// in the field's own indices rather than millimetres: converting would need
            /// GridToDicom, which is on the API object and not available to this pure computation.
            /// </summary>
            public int[] NegativeBoundsMin;
            public int[] NegativeBoundsMax;

            /// <summary>
            /// Distribution of the Jacobian determinant, for the second clause of TG-132
            /// Table III: "no values departing from 1 relative to what is expected for the
            /// clinical scenario (0-1 where volume reduction is expected, above 1 where
            /// expansion is expected)".
            ///
            /// Percentiles rather than the extremes alone: a single voxel at the edge of the
            /// field's support says nothing about whether the deformation as a whole is
            /// plausible, and min/max are exactly the values that voxel controls.
            /// </summary>
            public double JacobianP1;
            public double JacobianMedian;
            public double JacobianP99;
            public double MaxJacobian;

            /// <summary>
            /// How far the Jacobian departs from 1 over the central 98 % of the field — the
            /// larger of |p99 - 1| and |1 - p1|.
            ///
            /// A measurement, not a threshold. Whether a given departure is acceptable depends on
            /// the structure and on whether volume change was expected there, which is what
            /// Table III ties the criterion to and what this tool cannot know.
            /// </summary>
            public double MaxDepartureFromOne
            {
                get { return Math.Max(Math.Abs(JacobianP99 - 1.0), Math.Abs(1.0 - JacobianP1)); }
            }

            /// <summary>Fraction of folded points that sit against the field's edge, 0 to 1.</summary>
            public double NegativeBoundaryFraction
            {
                get
                {
                    int negative = (int)Math.Round(NegativeJacobianPercent * JacobianSampleCount / 100.0);
                    return negative <= 0 ? 0.0 : (double)NegativeNearBoundary / negative;
                }
            }
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

            // Filled by the same pass as maxDisplacement: these walk every point, not just the
            // interior, because a displacement needs no derivative and the field's extremes are
            // as meaningful at the boundary as anywhere else.
            var minAxis = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            var maxAxis = new[] { double.MinValue, double.MinValue, double.MinValue };

            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        Vec3 u = field.Vectors[x, y, z];

                        double length = u.Length;
                        if (length > maxDisplacement) maxDisplacement = length;

                        if (u.X < minAxis[0]) minAxis[0] = u.X;
                        if (u.Y < minAxis[1]) minAxis[1] = u.Y;
                        if (u.Z < minAxis[2]) minAxis[2] = u.Z;
                        if (u.X > maxAxis[0]) maxAxis[0] = u.X;
                        if (u.Y > maxAxis[1]) maxAxis[1] = u.Y;
                        if (u.Z > maxAxis[2]) maxAxis[2] = u.Z;
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

            // Where the folding is, not just how much. Two grid steps from the edge: one is the
            // shell the central difference already excludes, so the first evaluated layer would
            // otherwise always count as "interior" by a hair.
            const int BoundaryMargin = 2;
            int negativeNearBoundary = 0;
            int minNx = int.MaxValue, minNy = int.MaxValue, minNz = int.MaxValue;
            int maxNx = int.MinValue, maxNy = int.MinValue, maxNz = int.MinValue;

            // Kept so the distribution can be reported, not only the extremes. Sorting 1.4 million
            // doubles costs about a tenth of the field read it follows, and an exact percentile is
            // worth more than a histogram's approximation on a number a physicist will compare
            // against a clinical expectation.
            var jacobians = new double[(nx - 2) * (ny - 2) * (nz - 2)];
            double maxJacobian = double.MinValue;
            double minDivergence = double.MaxValue, maxDivergence = double.MinValue;
            double maxCurl = 0.0;
            int curlX0 = -1, curlY0 = -1, curlZ0 = -1;

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

                        // The trace of grad u. Free here, and the quantity Eclipse's divergence
                        // view shows.
                        double divergence = dudx.X + dudy.Y + dudz.Z;
                        if (divergence < minDivergence) minDivergence = divergence;
                        if (divergence > maxDivergence) maxDivergence = divergence;

                        // curl u = (dz/dy - dy/dz, dx/dz - dz/dx, dy/dx - dx/dy): the antisymmetric
                        // part of the same gradient, where the divergence is the trace.
                        double curlX = dudy.Z - dudz.Y;
                        double curlY = dudz.X - dudx.Z;
                        double curlZ = dudx.Y - dudy.X;
                        double curl = Math.Sqrt(curlX * curlX + curlY * curlY + curlZ * curlZ);
                        if (curl > maxCurl)
                        {
                            maxCurl = curl;
                            curlX0 = x; curlY0 = y; curlZ0 = z;
                        }

                        jacobians[samples] = j;
                        samples++;
                        if (j < minJacobian) minJacobian = j;
                        if (j > maxJacobian) maxJacobian = j;

                        if (j <= 0.0)
                        {
                            negative++;

                            if (x < BoundaryMargin || x >= nx - BoundaryMargin ||
                                y < BoundaryMargin || y >= ny - BoundaryMargin ||
                                z < BoundaryMargin || z >= nz - BoundaryMargin)
                            {
                                negativeNearBoundary++;
                            }

                            if (x < minNx) minNx = x;
                            if (y < minNy) minNy = y;
                            if (z < minNz) minNz = z;
                            if (x > maxNx) maxNx = x;
                            if (y > maxNy) maxNy = y;
                            if (z > maxNz) maxNz = z;
                        }

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

            // Only the entries actually written: a point whose derivatives came back non-finite is
            // skipped above, so samples can be short of the array's length.
            Array.Sort(jacobians, 0, samples);

            return new Result
            {
                NegativeJacobianPercent = 100.0 * negative / samples,
                MinJacobian = minJacobian,
                MaxJacobian = maxJacobian,
                JacobianP1 = Percentile(jacobians, samples, 0.01),
                JacobianMedian = Percentile(jacobians, samples, 0.50),
                JacobianP99 = Percentile(jacobians, samples, 0.99),
                JacobianSampleCount = samples,
                MaxGradientMagnitude = maxGradient,
                MaxDisplacementMm = maxDisplacement,
                MinDivergence = minDivergence,
                MaxDivergence = maxDivergence,
                MaxCurlMagnitude = maxCurl,
                MaxCurlAt = curlX0 < 0 ? null : new[] { curlX0, curlY0, curlZ0 },
                // Distance to the nearest face of the evaluated region. The interior runs from
                // index 1 to n-2, so 0 means the maximum sits on the first evaluated layer, with
                // the excluded shell immediately beyond it.
                MaxCurlStepsFromEdge = curlX0 < 0 ? -1 : Math.Min(
                    Math.Min(Math.Min(curlX0 - 1, nx - 2 - curlX0), Math.Min(curlY0 - 1, ny - 2 - curlY0)),
                    Math.Min(curlZ0 - 1, nz - 2 - curlZ0)),
                MinDisplacementPerAxis = minAxis,
                MaxDisplacementPerAxis = maxAxis,
                NegativeNearBoundary = negativeNearBoundary,
                NegativeBoundsMin = negative > 0 ? new[] { minNx, minNy, minNz } : null,
                NegativeBoundsMax = negative > 0 ? new[] { maxNx, maxNy, maxNz } : null
            };
        }

        /// <summary>
        /// Nearest-rank percentile over the already-sorted prefix. No interpolation: the value
        /// reported is one the field actually attains, which is the honest thing for a number a
        /// physicist will hold against a clinical expectation.
        /// </summary>
        private static double Percentile(double[] sorted, int count, double fraction)
        {
            if (count <= 0) return double.NaN;

            int index = (int)Math.Ceiling(fraction * count) - 1;
            if (index < 0) index = 0;
            if (index >= count) index = count - 1;

            return sorted[index];
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
