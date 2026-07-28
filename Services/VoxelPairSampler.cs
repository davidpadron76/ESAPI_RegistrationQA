using System;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    public sealed class VoxelPairSet
    {
        public float[] FixedValues { get; set; }
        public float[] MovingValues { get; set; }
        public int Count { get; set; }

        /// <summary>Fraction of visited fixed-image voxels that have a correspondence.</summary>
        public double OverlapFraction { get; set; }

        public string Problem { get; set; }
    }

    /// <summary>
    /// Matches physically corresponding voxels between two volumes by applying the
    /// registration mapping.
    ///
    /// This is what the previous version did not do: it compared <c>fixedBuffer[x,y]</c>
    /// with <c>movingBuffer[x,y]</c> by voxel index, so the result measured how well the
    /// index systems coincided, not the quality of the registration. Between a planning CT
    /// and a CBCT, with different FOV, spacing and origin, that comparison has no physical
    /// interpretation.
    /// </summary>
    public static class VoxelPairSampler
    {
        /// <summary>Upper bound on retained pairs, to bound memory and sorting time.</summary>
        public const int MaxRetainedPairs = 2000000;

        /// <summary>Minimum overlap below which the metrics are not representative.</summary>
        public const double MinimumOverlapFraction = 0.10;

        /// <summary>
        /// Walks the fixed-image grid, converts each point to patient coordinates, applies
        /// the registration mapping and samples the moving image by trilinear interpolation.
        /// </summary>
        public static VoxelPairSet Pair(
            SampledVolume fixedVolume,
            SampledVolume movingVolume,
            IPointMapper mapper)
        {
            var result = new VoxelPairSet();

            if (fixedVolume == null || movingVolume == null)
            {
                result.Problem = "one of the volumes is missing";
                return result;
            }

            if (mapper == null)
            {
                result.Problem = "no mapping between the two images is available";
                return result;
            }

            ImageGeometry fixedGeometry = fixedVolume.Geometry;
            long totalCandidates = fixedGeometry.VoxelCount;
            int budget = Math.Min(MaxRetainedPairs, Math.Max(1, mapper.RecommendedSampleBudget));

            // If the fixed grid exceeds the budget it is walked with a uniform stride rather
            // than truncated: truncating would bias sampling towards the first slices, which
            // in a planning volume are mostly air.
            int stride = 1;
            if (totalCandidates > budget)
                stride = (int)Math.Ceiling((double)totalCandidates / budget);

            int capacity = (int)Math.Min(budget, (totalCandidates + stride - 1) / stride);
            if (capacity <= 0)
            {
                result.Problem = "the sampling grid came out empty";
                return result;
            }

            var fixedValues = new float[capacity];
            var movingValues = new float[capacity];

            int paired = 0;
            long visited = 0;
            long linear = -1;

            for (int k = 0; k < fixedGeometry.ZSize && paired < capacity; k++)
            {
                for (int j = 0; j < fixedGeometry.YSize && paired < capacity; j++)
                {
                    for (int i = 0; i < fixedGeometry.XSize && paired < capacity; i++)
                    {
                        linear++;
                        if (linear % stride != 0) continue;

                        visited++;

                        Vec3 patientFixed = fixedGeometry.VoxelToPatient(i, j, k);

                        Vec3 patientMoving;
                        if (!mapper.TryMap(patientFixed, out patientMoving)) continue;

                        double mi, mj, mk;
                        movingVolume.Geometry.PatientToVoxel(patientMoving, out mi, out mj, out mk);

                        float movingValue;
                        if (!movingVolume.TrySample(mi, mj, mk, out movingValue)) continue;

                        float fixedValue = fixedVolume[i, j, k];

                        // A single non-finite voxel poisons every accumulator downstream, and
                        // it does so silently: NaN fails every comparison, so the zero-variance
                        // guard in the similarity calculator lets it through and the metrics
                        // come out as NaN rather than as an error. Excluding the pair here is
                        // the only place where the two values are still separable.
                        if (float.IsNaN(fixedValue) || float.IsInfinity(fixedValue)) continue;
                        if (float.IsNaN(movingValue) || float.IsInfinity(movingValue)) continue;

                        fixedValues[paired] = fixedValue;
                        movingValues[paired] = movingValue;
                        paired++;
                    }
                }
            }

            result.FixedValues = fixedValues;
            result.MovingValues = movingValues;
            result.Count = paired;
            result.OverlapFraction = visited > 0 ? (double)paired / visited : 0.0;

            if (paired == 0)
            {
                result.Problem = "the two images do not overlap after applying the registration";
            }
            else if (result.OverlapFraction < MinimumOverlapFraction)
            {
                result.Problem = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "insufficient overlap ({0:P1} of the fixed image); the intensity metrics would not be representative",
                    result.OverlapFraction);
            }

            return result;
        }
    }
}
