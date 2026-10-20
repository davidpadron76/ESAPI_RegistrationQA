using System;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Exact 3D Euclidean distance transform (Felzenszwalb and Huttenlocher, 2012).
    ///
    /// Given a binary mask, produces for every voxel the distance to the nearest set voxel.
    /// The algorithm is separable — a one-dimensional lower envelope computed along each axis
    /// in turn — which makes it linear in the number of voxels and exact, not an approximation
    /// by chamfer weights.
    ///
    /// It exists here because the surface metrics need nearest-surface distances for every
    /// boundary voxel of one structure against the whole surface of the other. Comparing the
    /// two point sets directly is O(n·m): for an organ rasterised at 2 mm that is of the order
    /// of 10^8 operations per structure pair, on the interface thread. With the transform it
    /// becomes a linear pass plus a lookup.
    ///
    /// Anisotropic voxels are handled by passing the spacing per axis; distances come out in
    /// millimetres.
    /// </summary>
    public static class DistanceTransform
    {
        /// <summary>Stands in for "no set voxel anywhere along this line".</summary>
        private const double Infinity = 1e20;

        /// <summary>
        /// Squared Euclidean distance in mm² from each voxel to the nearest set voxel of
        /// <paramref name="mask"/>. Returns squared values because that is what the algorithm
        /// works in; callers that need the distance take the square root only for the voxels
        /// they actually read.
        /// </summary>
        public static double[] SquaredDistanceMm(
            bool[] mask, int sizeX, int sizeY, int sizeZ,
            double spacingX, double spacingY, double spacingZ)
        {
            if (mask == null) throw new ArgumentNullException("mask");

            long expected = (long)sizeX * sizeY * sizeZ;
            if (mask.LongLength != expected)
                throw new ArgumentException("Mask size does not match the given dimensions.", "mask");

            var result = new double[expected];

            for (long i = 0; i < expected; i++)
                result[i] = mask[i] ? 0.0 : Infinity;

            // Each pass replaces the running squared distance with the lower envelope along
            // one axis. Applying the three axes in sequence yields the exact 3D transform.
            TransformAlongX(result, sizeX, sizeY, sizeZ, spacingX);
            TransformAlongY(result, sizeX, sizeY, sizeZ, spacingY);
            TransformAlongZ(result, sizeX, sizeY, sizeZ, spacingZ);

            return result;
        }

        private static void TransformAlongX(double[] data, int sizeX, int sizeY, int sizeZ, double spacing)
        {
            var line = new double[sizeX];
            var output = new double[sizeX];
            var v = new int[sizeX];
            var z = new double[sizeX + 1];

            for (int k = 0; k < sizeZ; k++)
            {
                for (int j = 0; j < sizeY; j++)
                {
                    int baseIndex = sizeX * (j + sizeY * k);

                    for (int i = 0; i < sizeX; i++) line[i] = data[baseIndex + i];
                    LowerEnvelope(line, output, v, z, sizeX, spacing);
                    for (int i = 0; i < sizeX; i++) data[baseIndex + i] = output[i];
                }
            }
        }

        private static void TransformAlongY(double[] data, int sizeX, int sizeY, int sizeZ, double spacing)
        {
            var line = new double[sizeY];
            var output = new double[sizeY];
            var v = new int[sizeY];
            var z = new double[sizeY + 1];

            for (int k = 0; k < sizeZ; k++)
            {
                for (int i = 0; i < sizeX; i++)
                {
                    for (int j = 0; j < sizeY; j++)
                        line[j] = data[i + sizeX * (j + sizeY * k)];

                    LowerEnvelope(line, output, v, z, sizeY, spacing);

                    for (int j = 0; j < sizeY; j++)
                        data[i + sizeX * (j + sizeY * k)] = output[j];
                }
            }
        }

        private static void TransformAlongZ(double[] data, int sizeX, int sizeY, int sizeZ, double spacing)
        {
            var line = new double[sizeZ];
            var output = new double[sizeZ];
            var v = new int[sizeZ];
            var z = new double[sizeZ + 1];

            for (int j = 0; j < sizeY; j++)
            {
                for (int i = 0; i < sizeX; i++)
                {
                    for (int k = 0; k < sizeZ; k++)
                        line[k] = data[i + sizeX * (j + sizeY * k)];

                    LowerEnvelope(line, output, v, z, sizeZ, spacing);

                    for (int k = 0; k < sizeZ; k++)
                        data[i + sizeX * (j + sizeY * k)] = output[k];
                }
            }
        }

        /// <summary>
        /// Lower envelope of the parabolas rooted at each sample, in one dimension.
        ///
        /// Each position q contributes the parabola f(q) + (x−q)²·spacing², and the transform
        /// at x is the minimum over all of them. The parabolas are scanned once to build the
        /// envelope and once to read it off, so the pass is linear rather than quadratic.
        /// </summary>
        private static void LowerEnvelope(
            double[] f, double[] output, int[] v, double[] z, int n, double spacing)
        {
            double spacingSquared = spacing * spacing;

            int k = 0;
            v[0] = 0;
            z[0] = -Infinity;
            z[1] = Infinity;

            for (int q = 1; q < n; q++)
            {
                // Intersection of the parabola at q with the one currently on top of the
                // envelope. While it lies left of that parabola's start, the latter is
                // entirely covered and gets popped.
                double s = Intersection(f, v[k], q, spacingSquared);

                while (s <= z[k])
                {
                    k--;
                    s = Intersection(f, v[k], q, spacingSquared);
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = Infinity;
            }

            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;

                double d = (q - v[k]) * spacing;
                output[q] = d * d + f[v[k]];
            }
        }

        private static double Intersection(double[] f, int p, int q, double spacingSquared)
        {
            // Abscissa where the parabolas rooted at p and q meet.
            return ((f[q] + q * q * spacingSquared) - (f[p] + p * p * spacingSquared))
                   / (2.0 * spacingSquared * (q - p));
        }
    }
}
