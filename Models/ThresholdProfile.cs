using System;
using System.Collections.Generic;
using System.Globalization;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Green/yellow limits for a metric. The direction of goodness (whether higher is
    /// better) is not stored here: it is an intrinsic property of the metric and lives in
    /// <see cref="MetricCatalog"/>, so it cannot become inconsistent between profiles.
    /// </summary>
    public sealed class ThresholdLimits
    {
        public double GreenLimit { get; set; }
        public double YellowLimit { get; set; }

        public ThresholdLimits() { }

        public ThresholdLimits(double greenLimit, double yellowLimit)
        {
            GreenLimit = greenLimit;
            YellowLimit = yellowLimit;
        }
    }

    public sealed class ThresholdProfile
    {
        public string ProfileName { get; set; }
        public Dictionary<string, ThresholdLimits> Limits { get; private set; }

        public ThresholdProfile()
        {
            Limits = new Dictionary<string, ThresholdLimits>(StringComparer.Ordinal);
        }

        public bool TryGetLimits(string metricKey, out ThresholdLimits limits)
        {
            if (metricKey == null)
            {
                limits = null;
                return false;
            }
            return Limits.TryGetValue(metricKey, out limits);
        }

        /// <summary>
        /// Classifies a measured value. Returns <see cref="QASemaphore.NotAvailable"/> when
        /// the profile does not define the metric: no default classification is invented.
        /// </summary>
        public QASemaphore Evaluate(string metricKey, double value)
        {
            ThresholdLimits limits;
            if (!TryGetLimits(metricKey, out limits)) return QASemaphore.NotAvailable;

            if (MetricCatalog.HigherIsBetter(metricKey))
            {
                if (value >= limits.GreenLimit) return QASemaphore.Green;
                if (value >= limits.YellowLimit) return QASemaphore.Yellow;
                return QASemaphore.Red;
            }

            if (value <= limits.GreenLimit) return QASemaphore.Green;
            if (value <= limits.YellowLimit) return QASemaphore.Yellow;
            return QASemaphore.Red;
        }

        /// <summary>
        /// Acceptance criterion text, showing both thresholds. Previously only the green
        /// limit was shown, which made it impossible to understand why a metric came out
        /// yellow.
        /// </summary>
        public string DescribeCriteria(string metricKey)
        {
            // A metric that cannot gate has no criterion to describe, and saying "—" would
            // read as a gap in the profile rather than as a deliberate position.
            if (!MetricCatalog.IsGating(metricKey))
                return "no TG-132 tolerance";

            ThresholdLimits limits;
            if (!TryGetLimits(metricKey, out limits)) return "—";

            string op = MetricCatalog.HigherIsBetter(metricKey) ? "≥" : "≤";
            string unit = MetricCatalog.Unit(metricKey);
            string suffix = string.IsNullOrEmpty(unit) ? string.Empty : " " + unit;

            // One tolerance, one band. Printing "≤ 5 mm (yellow ≤ 5 mm)" for the metrics whose
            // green and yellow limits are deliberately equal reads as a mistake.
            if (Math.Abs(limits.GreenLimit - limits.YellowLimit) < 1e-9)
            {
                string basis = IsVoxelDerived(metricKey)
                    ? (IsStereotactic ? " — TG-132, stereotactic" : " — TG-132, max voxel dimension")
                    : string.Empty;

                return string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:0.###}{2}{3}", op, limits.GreenLimit, suffix, basis);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1:0.###}{2} (yellow {0} {3:0.###}{2})",
                op, limits.GreenLimit, suffix, limits.YellowLimit);
        }

        /// <summary>Green threshold as text, for embedding in advisory messages.</summary>
        public string DescribeGreenLimit(string metricKey)
        {
            ThresholdLimits limits;
            if (!TryGetLimits(metricKey, out limits)) return "—";

            string unit = MetricCatalog.Unit(metricKey);
            string suffix = string.IsNullOrEmpty(unit) ? string.Empty : " " + unit;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###}{1}", limits.GreenLimit, suffix);
        }

        /// <summary>
        /// True when this profile is for stereotactic treatment, the only distinction TG-132
        /// draws between one kind of case and another.
        /// </summary>
        public bool IsStereotactic { get; set; }

        /// <summary>
        /// The metrics whose tolerance TG-132 ties to the images rather than to a fixed number.
        /// Table III gives "maximum voxel dimension (~2-3 mm)" for TRE and consistency, and
        /// "within the contouring uncertainty of the structure or maximum voxel dimension" for
        /// MDA.
        /// </summary>
        private static readonly string[] VoxelDerivedKeys =
        {
            MetricKeys.TreMean, MetricKeys.TreMax, MetricKeys.Mda, MetricKeys.InverseConsistency
        };

        public static bool IsVoxelDerived(string metricKey)
        {
            return Array.IndexOf(VoxelDerivedKeys, metricKey) >= 0;
        }

        /// <summary>
        /// Turns this profile into one with concrete numbers for a particular case.
        ///
        /// Four of the five Table III tolerances are not constants. The report states them as
        /// the maximum voxel dimension of the images being registered, which the plugin already
        /// measures, and it names one exception: "stereotactic radiosurgery tolerances are
        /// 1 mm, corresponding to the smaller voxel dimension".
        ///
        /// This replaces four anatomical profiles whose spatial limits were a fiction. TG-132
        /// has no tolerance that varies by anatomical site — where it does allow variation, in
        /// the MDA and DSC rows, it attributes it to the contouring uncertainty and the volume
        /// of the individual structure. And the two interobserver studies the report actually
        /// cites in section 4.C.2 run the other way from the profiles this project had:
        /// 2.6 mm for peripheral lung (Persson) against 3.9 mm for glottic larynx (Brouwer),
        /// while the profiles allowed head and neck 2.0 mm and thorax 3.0 mm.
        /// </summary>
        public ThresholdProfile Resolve(QaMeasurements measurements)
        {
            var resolved = new ThresholdProfile
            {
                ProfileName = ProfileName,
                IsStereotactic = IsStereotactic
            };

            foreach (var entry in Limits)
                resolved.Limits[entry.Key] = entry.Value;

            double? tolerance = SpatialToleranceMm(measurements);
            if (!tolerance.HasValue) return resolved;

            // Green equals yellow on purpose. Table III gives one tolerance, not two bands, so
            // an attention zone would have to be invented — the same reasoning that put the
            // Jacobian limit at 0 %.
            foreach (string key in VoxelDerivedKeys)
                resolved.Limits[key] = new ThresholdLimits(tolerance.Value, tolerance.Value);

            return resolved;
        }

        /// <summary>
        /// The spatial tolerance for this case in mm, or null when the voxel size could not be
        /// read — in which case the metrics that depend on it must not be classified at all,
        /// since the report ties the limit to the image.
        /// </summary>
        public double? SpatialToleranceMm(QaMeasurements measurements)
        {
            if (IsStereotactic) return 1.0;

            if (measurements == null || !measurements.NativeVoxelSizeMm.HasValue) return null;
            return measurements.NativeVoxelSizeMm.Value;
        }

        // Two profiles, because two is what TG-132 distinguishes. The anatomical ones are gone:
        // their spatial limits had no basis in the report, and every other metric they used to
        // carry a limit for — NMI, NCC, SSD, smoothness, HD95, maximum displacement — no longer
        // decides anything, so there was nothing left for a site to vary.

        /// <summary>
        /// Standard fractionated treatment. The spatial tolerances resolve to the maximum voxel
        /// dimension of the two series.
        /// </summary>
        public static ThresholdProfile CreateStandard()
        {
            var profile = new ThresholdProfile { ProfileName = "Standard treatment" };
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(0.0, 0.0));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.90, 0.80));
            return profile;
        }

        /// <summary>
        /// Stereotactic treatment. TG-132: "stereotactic radiosurgery tolerances are 1 mm."
        /// </summary>
        public static ThresholdProfile CreateStereotactic()
        {
            var profile = new ThresholdProfile
            {
                ProfileName = "Stereotactic (SRS / SBRT)",
                IsStereotactic = true
            };
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(0.0, 0.0));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.90, 0.80));
            return profile;
        }

        public static List<ThresholdProfile> GetAllProfiles()
        {
            return new List<ThresholdProfile>
            {
                CreateStandard(),
                CreateStereotactic()
            };
        }
    }
}
