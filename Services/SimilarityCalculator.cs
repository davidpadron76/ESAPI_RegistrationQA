using System;

namespace ESAPI_RegistrationQA.Services
{
    public sealed class SimilarityResult
    {
        /// <summary>Signed Pearson correlation, range [-1,1]. Negative = inverted contrast.</summary>
        public double? Ncc { get; set; }

        /// <summary>Mean squared difference normalised by the square of the robust range.</summary>
        public double? Ssd { get; set; }

        /// <summary>Studholme NMI: (H(A)+H(B))/H(A,B), range [1,2].</summary>
        public double? Nmi { get; set; }

        public int SampleCount { get; set; }

        /// <summary>Bins used in the NMI joint histogram (0 if it was not computed).</summary>
        public int HistogramBins { get; set; }

        public string Problem { get; set; }
    }

    /// <summary>
    /// Similarity metrics over intensity pairs that have already been matched spatially.
    ///
    /// This class knows nothing about ESAPI, transforms or geometry: it receives two vectors
    /// of intensities corresponding to the same physical point and computes. It is
    /// deliberately pure so it can be verified without an Eclipse environment.
    /// </summary>
    public static class SimilarityCalculator
    {
        /// <summary>Minimum number of pairs for the metrics to be statistically meaningful.</summary>
        public const int MinimumSamples = 1000;

        /// <summary>Bounds on the number of joint-histogram bins.</summary>
        public const int MaxHistogramBins = 64;
        public const int MinHistogramBins = 16;

        /// <summary>Minimum mean occupancy per joint-histogram cell.</summary>
        private const int MinSamplesPerJointCell = 20;

        /// <summary>
        /// Chooses the bin count from the sample size.
        ///
        /// Joint entropy is biased downwards when many cells stay empty, which would inflate
        /// the NMI artificially. Fixing 64 bins with few samples — the deformable case,
        /// where point-by-point mapping forces a reduced budget — would produce an
        /// optimistic value that is not comparable across studies.
        /// </summary>
        public static int ChooseBinCount(int sampleCount)
        {
            int bins = (int)Math.Floor(Math.Sqrt((double)sampleCount / MinSamplesPerJointCell));
            if (bins > MaxHistogramBins) bins = MaxHistogramBins;
            if (bins < MinHistogramBins) bins = MinHistogramBins;
            return bins;
        }

        public static SimilarityResult Compute(float[] fixedValues, float[] movingValues, int count)
        {
            var result = new SimilarityResult { SampleCount = count };

            if (fixedValues == null || movingValues == null)
            {
                result.Problem = "null sample vectors";
                return result;
            }

            if (count < MinimumSamples)
            {
                result.Problem = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "only {0} overlapping voxel pairs; at least {1} are required for a stable estimate",
                    count, MinimumSamples);
                return result;
            }

            if (count > fixedValues.Length || count > movingValues.Length)
            {
                result.Problem = "sample count larger than the supplied vectors";
                return result;
            }

            // --- First- and second-order statistics in a single pass ---
            double sumF = 0, sumM = 0, sumFM = 0, sumF2 = 0, sumM2 = 0, sumDiff2 = 0;

            for (int i = 0; i < count; i++)
            {
                double f = fixedValues[i];
                double m = movingValues[i];

                sumF += f;
                sumM += m;
                sumFM += f * m;
                sumF2 += f * f;
                sumM2 += m * m;

                double d = f - m;
                sumDiff2 += d * d;
            }

            double n = count;
            double covariance = (n * sumFM) - (sumF * sumM);
            double varianceF = (n * sumF2) - (sumF * sumF);
            double varianceM = (n * sumM2) - (sumM * sumM);

            if (varianceF <= 0 || varianceM <= 0)
            {
                result.Problem = "one of the images is constant over the overlapping region (zero variance)";
                return result;
            }

            result.Ncc = covariance / Math.Sqrt(varianceF * varianceM);

            // --- SSD normalised by the robust range of the fixed image ---
            double p1, p99;
            RobustRange(fixedValues, count, out p1, out p99);
            double range = p99 - p1;

            if (range > 0)
            {
                result.Ssd = (sumDiff2 / n) / (range * range);
            }

            // --- NMI over the joint histogram ---
            double mp1, mp99;
            RobustRange(movingValues, count, out mp1, out mp99);

            if (range > 0 && (mp99 - mp1) > 0)
            {
                result.HistogramBins = ChooseBinCount(count);
                result.Nmi = ComputeNmi(
                    fixedValues, movingValues, count,
                    p1, p99, mp1, mp99, result.HistogramBins);
            }

            return result;
        }

        /// <summary>
        /// Studholme NMI: (H(A) + H(B)) / H(A,B).
        ///
        /// Computed over the actual joint histogram. The previous version returned
        /// 1.20 + 0.45·NCC, a linear function of the NCC that carries no independent
        /// information: for a multimodal pair, precisely where the NCC stops being valid,
        /// that "NMI" inherited the very same degradation.
        /// </summary>
        public static double? ComputeNmi(
            float[] a, float[] b, int count,
            double aMin, double aMax,
            double bMin, double bMax,
            int bins)
        {
            if (bins < 2) return null;
            if (!(aMax > aMin) || !(bMax > bMin)) return null;

            var joint = new double[bins, bins];
            double aScale = bins / (aMax - aMin);
            double bScale = bins / (bMax - bMin);

            for (int i = 0; i < count; i++)
            {
                int ai = Bin(a[i], aMin, aScale, bins);
                int bi = Bin(b[i], bMin, bScale, bins);
                joint[ai, bi] += 1.0;
            }

            var marginalA = new double[bins];
            var marginalB = new double[bins];

            for (int x = 0; x < bins; x++)
            {
                for (int y = 0; y < bins; y++)
                {
                    double v = joint[x, y];
                    marginalA[x] += v;
                    marginalB[y] += v;
                }
            }

            double total = count;
            double entropyA = Entropy(marginalA, total);
            double entropyB = Entropy(marginalB, total);
            double entropyJoint = EntropyJoint(joint, total, bins);

            if (entropyJoint <= 1e-12) return null;

            return (entropyA + entropyB) / entropyJoint;
        }

        private static int Bin(double value, double min, double scale, int bins)
        {
            int index = (int)((value - min) * scale);
            if (index < 0) return 0;
            if (index >= bins) return bins - 1;
            return index;
        }

        private static double Entropy(double[] counts, double total)
        {
            double entropy = 0.0;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] <= 0) continue;
                double p = counts[i] / total;
                entropy -= p * Math.Log(p, 2.0);
            }
            return entropy;
        }

        private static double EntropyJoint(double[,] joint, double total, int bins)
        {
            double entropy = 0.0;
            for (int x = 0; x < bins; x++)
            {
                for (int y = 0; y < bins; y++)
                {
                    double c = joint[x, y];
                    if (c <= 0) continue;
                    double p = c / total;
                    entropy -= p * Math.Log(p, 2.0);
                }
            }
            return entropy;
        }

        /// <summary>
        /// 1st and 99th percentiles. The robust range is used instead of the absolute
        /// min/max so that a single voxel with a metal artefact (or the sentinel air value
        /// outside the FOV) does not compress the whole histogram.
        ///
        /// This replaces the fixed 4096.0 divisor of the previous version, which assumed a
        /// 12-bit CT range and was meaningless for MR or PET.
        /// </summary>
        public static void RobustRange(float[] values, int count, out double p1, out double p99)
        {
            var copy = new float[count];
            Array.Copy(values, copy, count);
            Array.Sort(copy);

            p1 = copy[(int)(0.01 * (count - 1))];
            p99 = copy[(int)(0.99 * (count - 1))];
        }
    }
}
