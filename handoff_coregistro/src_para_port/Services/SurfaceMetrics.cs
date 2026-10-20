using System;
using System.Collections.Generic;

namespace ESAPI_RegistrationQA.Services
{
    public sealed class SurfaceComparison
    {
        /// <summary>Dice similarity coefficient, 2|A∩B| / (|A|+|B|).</summary>
        public double? Dsc { get; set; }

        /// <summary>Mean distance to agreement in mm, symmetric.</summary>
        public double? MeanDistanceToAgreement { get; set; }

        /// <summary>95th-percentile Hausdorff distance in mm, symmetric.</summary>
        public double? Hd95 { get; set; }

        public int VoxelsA { get; set; }
        public int VoxelsB { get; set; }
        public int SurfaceVoxelsA { get; set; }
        public int SurfaceVoxelsB { get; set; }

        public string Problem { get; set; }
    }

    /// <summary>
    /// Overlap and surface agreement between two structures already rasterised onto the same
    /// grid.
    ///
    /// All three metrics come out of one pass because they share the distance transform:
    /// having computed the distance from every voxel to the nearest surface voxel of the
    /// other structure, MDA is the mean of those distances and HD95 the 95th percentile of
    /// the same set. Computing them separately would be doing the expensive part twice.
    ///
    /// Both distance metrics are symmetric — evaluated over the union of the two directed
    /// sets — which is the usual definition and has a useful side effect: MDA and HD95 are
    /// then drawn from exactly the same numbers, so MDA can never exceed HD95. That
    /// relationship is worth checking after any change to this file.
    /// </summary>
    public static class SurfaceMetrics
    {
        public static SurfaceComparison Compare(
            bool[] maskA, bool[] maskB,
            int sizeX, int sizeY, int sizeZ,
            double spacingX, double spacingY, double spacingZ)
        {
            var result = new SurfaceComparison();

            if (maskA == null || maskB == null)
            {
                result.Problem = "one of the structures could not be rasterised";
                return result;
            }

            long total = (long)sizeX * sizeY * sizeZ;
            if (maskA.LongLength != total || maskB.LongLength != total)
            {
                result.Problem = "the two structures were rasterised onto different grids";
                return result;
            }

            // ---- Dice, from volume counts -------------------------------------------------
            int countA = 0, countB = 0, countIntersection = 0;

            for (long i = 0; i < total; i++)
            {
                if (maskA[i]) countA++;
                if (maskB[i]) countB++;
                if (maskA[i] && maskB[i]) countIntersection++;
            }

            result.VoxelsA = countA;
            result.VoxelsB = countB;

            if (countA == 0 || countB == 0)
            {
                result.Problem = countA == 0 && countB == 0
                    ? "neither structure produced any voxel on the sampling grid"
                    : "one of the structures produced no voxel on the sampling grid; it may be "
                      + "smaller than the grid spacing or lie outside the shared field of view";
                return result;
            }

            result.Dsc = 2.0 * countIntersection / (double)(countA + countB);

            // ---- Surface distances, from the distance transform ---------------------------
            bool[] surfaceA = ExtractSurface(maskA, sizeX, sizeY, sizeZ);
            bool[] surfaceB = ExtractSurface(maskB, sizeX, sizeY, sizeZ);

            double[] squaredToB = DistanceTransform.SquaredDistanceMm(
                surfaceB, sizeX, sizeY, sizeZ, spacingX, spacingY, spacingZ);
            double[] squaredToA = DistanceTransform.SquaredDistanceMm(
                surfaceA, sizeX, sizeY, sizeZ, spacingX, spacingY, spacingZ);

            var distances = new List<double>();

            for (long i = 0; i < total; i++)
            {
                if (surfaceA[i]) distances.Add(Math.Sqrt(squaredToB[i]));
                if (surfaceB[i]) distances.Add(Math.Sqrt(squaredToA[i]));
                if (surfaceA[i]) result.SurfaceVoxelsA++;
                if (surfaceB[i]) result.SurfaceVoxelsB++;
            }

            if (distances.Count == 0)
            {
                result.Problem = "no surface voxels were found for either structure";
                return result;
            }

            distances.Sort();

            double sum = 0.0;
            for (int i = 0; i < distances.Count; i++) sum += distances[i];

            result.MeanDistanceToAgreement = sum / distances.Count;
            result.Hd95 = Percentile(distances, 0.95);

            return result;
        }

        /// <summary>
        /// Voxels of the mask that touch the outside, using 6-connectivity.
        ///
        /// A voxel on the edge of the grid counts as surface: the structure is then clipped by
        /// the field of view rather than closed, and treating it as interior would silently
        /// remove that boundary from the comparison.
        /// </summary>
        public static bool[] ExtractSurface(bool[] mask, int sizeX, int sizeY, int sizeZ)
        {
            var surface = new bool[mask.Length];

            for (int k = 0; k < sizeZ; k++)
            {
                for (int j = 0; j < sizeY; j++)
                {
                    for (int i = 0; i < sizeX; i++)
                    {
                        int index = i + sizeX * (j + sizeY * k);
                        if (!mask[index]) continue;

                        bool isBoundary =
                            i == 0 || i == sizeX - 1 ||
                            j == 0 || j == sizeY - 1 ||
                            k == 0 || k == sizeZ - 1 ||
                            !mask[index - 1] ||
                            !mask[index + 1] ||
                            !mask[index - sizeX] ||
                            !mask[index + sizeX] ||
                            !mask[index - sizeX * sizeY] ||
                            !mask[index + sizeX * sizeY];

                        surface[index] = isBoundary;
                    }
                }
            }

            return surface;
        }

        /// <summary>Percentile of an already sorted list, by linear interpolation.</summary>
        public static double Percentile(List<double> sorted, double fraction)
        {
            if (sorted == null || sorted.Count == 0) return 0.0;
            if (sorted.Count == 1) return sorted[0];

            double position = fraction * (sorted.Count - 1);
            int lower = (int)Math.Floor(position);
            int upper = Math.Min(lower + 1, sorted.Count - 1);
            double weight = position - lower;

            return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
        }
    }
}
