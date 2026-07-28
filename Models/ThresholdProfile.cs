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

        // What the four profiles no longer carry, and why.
        //
        // NMI, NCC, SSD and Smoothness have no limits here at all. The values they used to
        // have were this project's own invention. TG-132 gives no tolerance for the first
        // three and section 4.C.3 says they are difficult to convert into a measure of spatial
        // accuracy; Smoothness it does not mention. All four were able to produce a
        // NOT COMPLIANT verdict on that basis. They are still measured, shown and exported —
        // MetricCatalog states the position on each.
        //
        // The Jacobian limits are 0/0 in every profile, and identical on purpose. Table III
        // gives one tolerance, "no negative values", and ties it to the physics rather than to
        // the anatomical site: the report calls a negative determinant an erroneous physical
        // model of the patient, which is as true in a lung as in a brain. The previous values
        // admitted between 1 % and 4 % of folded voxels, so the tool was more permissive than
        // the standard it cites — on its own authority, and silently.

        // 1. Head and neck (ART H&N)
        public static ThresholdProfile CreateDefaultHN()
        {
            var profile = new ThresholdProfile { ProfileName = "ART Head & Neck" };
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(0.0, 0.0));
            profile.Limits.Add(MetricKeys.MaxDisplacement, new ThresholdLimits(15.0, 22.0));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.85, 0.75));
            profile.Limits.Add(MetricKeys.Mda, new ThresholdLimits(2.0, 3.0));
            profile.Limits.Add(MetricKeys.Hd95, new ThresholdLimits(3.0, 5.0));
            profile.Limits.Add(MetricKeys.TreMean, new ThresholdLimits(2.0, 3.0));
            profile.Limits.Add(MetricKeys.TreMax, new ThresholdLimits(3.0, 5.0));
            profile.Limits.Add(MetricKeys.InverseConsistency, new ThresholdLimits(2.0, 3.0));
            return profile;
        }

        // 2. Brain / radiosurgery (Brain / SRS) - tight tolerances
        public static ThresholdProfile CreateBrainSRS()
        {
            var profile = new ThresholdProfile { ProfileName = "Brain / SRS" };
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(0.0, 0.0));
            profile.Limits.Add(MetricKeys.MaxDisplacement, new ThresholdLimits(5.0, 10.0));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.90, 0.82));
            profile.Limits.Add(MetricKeys.Mda, new ThresholdLimits(1.0, 2.0));
            profile.Limits.Add(MetricKeys.Hd95, new ThresholdLimits(2.0, 3.5));
            profile.Limits.Add(MetricKeys.TreMean, new ThresholdLimits(1.0, 2.0));
            profile.Limits.Add(MetricKeys.TreMax, new ThresholdLimits(1.5, 2.5));
            profile.Limits.Add(MetricKeys.InverseConsistency, new ThresholdLimits(1.0, 2.0));
            return profile;
        }

        // 3. Pelvis / prostate
        public static ThresholdProfile CreatePelvis()
        {
            var profile = new ThresholdProfile { ProfileName = "Pelvis / Prostate" };
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(0.0, 0.0));
            profile.Limits.Add(MetricKeys.MaxDisplacement, new ThresholdLimits(20.0, 30.0));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.80, 0.70));
            profile.Limits.Add(MetricKeys.Mda, new ThresholdLimits(2.5, 3.5));
            profile.Limits.Add(MetricKeys.Hd95, new ThresholdLimits(4.0, 6.0));
            profile.Limits.Add(MetricKeys.TreMean, new ThresholdLimits(2.5, 3.5));
            profile.Limits.Add(MetricKeys.TreMax, new ThresholdLimits(4.0, 6.0));
            profile.Limits.Add(MetricKeys.InverseConsistency, new ThresholdLimits(2.5, 3.5));
            return profile;
        }

        // 4. Thorax / lung
        public static ThresholdProfile CreateThorax()
        {
            var profile = new ThresholdProfile { ProfileName = "Thorax / Lung" };
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(0.0, 0.0));
            profile.Limits.Add(MetricKeys.MaxDisplacement, new ThresholdLimits(25.0, 35.0));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.80, 0.70));
            profile.Limits.Add(MetricKeys.Mda, new ThresholdLimits(3.0, 4.5));
            profile.Limits.Add(MetricKeys.Hd95, new ThresholdLimits(4.5, 6.5));
            profile.Limits.Add(MetricKeys.TreMean, new ThresholdLimits(3.0, 5.0));
            profile.Limits.Add(MetricKeys.TreMax, new ThresholdLimits(5.0, 8.0));
            profile.Limits.Add(MetricKeys.InverseConsistency, new ThresholdLimits(3.0, 5.0));
            return profile;
        }

        public static List<ThresholdProfile> GetAllProfiles()
        {
            return new List<ThresholdProfile>
            {
                CreateDefaultHN(),
                CreateBrainSRS(),
                CreatePelvis(),
                CreateThorax()
            };
        }
    }
}
