using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    public enum AdvisorySeverity { Ok, Info, Warning, Critical }

    public sealed class Advisory
    {
        public AdvisorySeverity Severity { get; private set; }
        public string Category { get; private set; }
        public string Message { get; private set; }

        public Advisory(AdvisorySeverity severity, string category, string message)
        {
            Severity = severity;
            Category = category;
            Message = message;
        }

        public string SeverityText
        {
            get
            {
                switch (Severity)
                {
                    case AdvisorySeverity.Critical: return "CRITICAL";
                    case AdvisorySeverity.Warning: return "REVIEW";
                    case AdvisorySeverity.Info: return "INFO";
                    default: return "OK";
                }
            }
        }

        public override string ToString()
        {
            return "[" + SeverityText + "] " + Category + ": " + Message;
        }
    }

    public sealed class AdvisorySet
    {
        public List<Advisory> Advisories { get; private set; }
        public string OverallStatus { get; set; }
        public AdvisorySeverity OverallSeverity { get; set; }

        public AdvisorySet()
        {
            Advisories = new List<Advisory>();
        }
    }

    /// <summary>
    /// Turns evaluated metrics into clinical recommendations.
    ///
    /// Every threshold comes from the active profile. The previous version compared against
    /// constants (1.0% Jacobian, 15 mm displacement) while the tables used the profile
    /// limits, so under the Brain/SRS profile a 0.5% Jacobian was painted red but raised no
    /// advisory, and under Thorax a 1.5% Jacobian raised a critical advisory even though the
    /// profile accepted it.
    /// </summary>
    public static class AdvisoryEngine
    {
        public static AdvisorySet Build(
            IEnumerable<MetricResult> allMetrics,
            ThresholdProfile profile,
            QaMeasurements measurements)
        {
            var set = new AdvisorySet();
            List<MetricResult> metrics = (allMetrics ?? Enumerable.Empty<MetricResult>()).ToList();
            string profileName = profile != null ? profile.ProfileName : "(no profile)";

            // --- Metrics out of tolerance ----------------------------------------------
            foreach (MetricResult metric in metrics.Where(m => m.Status == QASemaphore.Red))
                set.Advisories.Add(BuildBreachAdvisory(metric, profile, profileName, AdvisorySeverity.Critical));

            foreach (MetricResult metric in metrics.Where(m => m.Status == QASemaphore.Yellow))
                set.Advisories.Add(BuildBreachAdvisory(metric, profile, profileName, AdvisorySeverity.Warning));

            // --- Metrics that were not evaluated -----------------------------------------
            List<MetricResult> unavailable = metrics
                .Where(m => m.Status == QASemaphore.NotAvailable)
                .ToList();

            foreach (MetricResult metric in unavailable)
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Info,
                    "METRIC NOT EVALUATED",
                    metric.MetricName + " could not be measured: " + metric.UnavailableReason +
                    " This metric neither supports nor contradicts acceptance of the registration."));
            }

            // --- Modality context --------------------------------------------------------
            AddModalityAdvisory(set, measurements);

            // --- Sampling quality --------------------------------------------------------
            AddSamplingAdvisory(set, measurements);

            // --- Transform provenance ----------------------------------------------------
            if (measurements != null && measurements.Transform == null)
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Warning,
                    "TRANSFORM",
                    "The registration transform could not be obtained. The rigid parameters shown do " +
                    "not come from a valid reading."));
            }
            else if (measurements != null && measurements.RigidEulerAngles.HasValue &&
                     measurements.RigidEulerAngles.Value.GimbalLock)
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Warning,
                    "TRANSFORM",
                    "The Euler decomposition hit gimbal lock (pitch ≈ ±90°). Pitch and yaw are not " +
                    "separable at this orientation; read the three angles as a set, not individually."));
            }

            // --- Overall verdict ---------------------------------------------------------
            BuildVerdict(set, metrics, unavailable.Count, profileName, measurements);

            return set;
        }

        private static Advisory BuildBreachAdvisory(
            MetricResult metric, ThresholdProfile profile, string profileName, AdvisorySeverity severity)
        {
            string limit = profile != null ? profile.DescribeGreenLimit(metric.MetricKey) : "—";
            string measured = metric.DisplayValue;

            string interpretation = InterpretBreach(metric.MetricKey, severity);

            return new Advisory(
                severity,
                severity == AdvisorySeverity.Critical ? "OUT OF TOLERANCE" : "IN ATTENTION ZONE",
                string.Format(CultureInfo.InvariantCulture,
                    "{0} = {1} against the {2} criterion of the [{3}] profile ({4}). {5}",
                    metric.MetricName, measured, limit, profileName, metric.ThresholdCriteria, interpretation));
        }

        /// <summary>
        /// Clinical interpretation attached to each metric. Selected by canonical key, not
        /// by substring of the display name: renaming a UI label must not silently stop an
        /// advisory from firing.
        /// </summary>
        private static string InterpretBreach(string metricKey, AdvisorySeverity severity)
        {
            switch (metricKey)
            {
                case MetricKeys.JacobianNegative:
                    return severity == AdvisorySeverity.Critical
                        ? "Indicates grid folding: unphysical topological inversion in the deformation " +
                          "field. The registration is NOT suitable for dose accumulation or for direct " +
                          "contour propagation."
                        : "Local folding is present in the deformation field. Verify the affected regions " +
                          "before propagating contours.";

                case MetricKeys.MaxDisplacement:
                    return "High vector displacement. Confirm that it corresponds to a real anatomical " +
                           "change and not to a correlation error at the FOV boundaries.";

                case MetricKeys.Smoothness:
                    return "Irregular deformation field. May indicate algorithm overfitting in low-contrast " +
                           "regions.";

                case MetricKeys.Nmi:
                case MetricKeys.Ncc:
                case MetricKeys.Ssd:
                    return "Possible contrast differences, metal artefacts, FOV truncation, or a genuine " +
                           "misalignment. Review the overlay slice by slice.";

                case MetricKeys.Dsc:
                case MetricKeys.Hd95:
                    return "Anatomical overlap is out of tolerance. Slice-by-slice verification and manual " +
                           "adjustment of the propagated contours are recommended.";

                default:
                    return "Review the value against the profile criterion.";
            }
        }

        private static void AddModalityAdvisory(AdvisorySet set, QaMeasurements measurements)
        {
            if (measurements == null) return;
            if (measurements.FixedModality == ImageModality.Unknown ||
                measurements.MovingModality == ImageModality.Unknown)
            {
                return;
            }

            bool multimodal = measurements.FixedModality != measurements.MovingModality;
            string primaryKey = multimodal ? MetricKeys.Nmi : MetricKeys.Ncc;
            string primaryName = MetricCatalog.DisplayName(primaryKey);

            string pair = measurements.FixedModality + " → " + measurements.MovingModality;

            if (multimodal)
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Info,
                    "MODALITY",
                    "Multimodal registration (" + pair + "). The reference metric is " + primaryName +
                    ": the NCC assumes a linear relationship between intensities that does not hold " +
                    "across different modalities, so its value here is indicative only."));
            }
            else
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Info,
                    "MODALITY",
                    "Monomodal registration (" + pair + "). The reference metric is " + primaryName + "."));
            }
        }

        private static void AddSamplingAdvisory(AdvisorySet set, QaMeasurements measurements)
        {
            if (measurements == null || measurements.SampleCount <= 0) return;

            string detail = string.Format(CultureInfo.InvariantCulture,
                "Similarity computed over {0:N0} voxel pairs at {1:F1} mm effective sampling.",
                measurements.SampleCount,
                measurements.EffectiveSamplingMm.HasValue ? measurements.EffectiveSamplingMm.Value : 0.0);

            if (measurements.OverlapFraction.HasValue)
            {
                detail += string.Format(CultureInfo.InvariantCulture,
                    " Overlap between volumes: {0:P1}.", measurements.OverlapFraction.Value);
            }

            AdvisorySeverity severity = AdvisorySeverity.Info;
            if (measurements.OverlapFraction.HasValue && measurements.OverlapFraction.Value < 0.35)
            {
                severity = AdvisorySeverity.Warning;
                detail += " Low overlap concentrates the metric on a small fraction of the anatomy; " +
                          "its representativeness is limited.";
            }

            set.Advisories.Add(new Advisory(severity, "SAMPLING", detail));
        }

        /// <summary>
        /// Overall verdict. It never declares a registration verified while metrics remain
        /// unevaluated: absence of evidence is not evidence of conformity, and that is
        /// precisely what the previous version asserted by filling the gaps with generated
        /// values.
        /// </summary>
        private static void BuildVerdict(
            AdvisorySet set, List<MetricResult> metrics, int unavailableCount,
            string profileName, QaMeasurements measurements)
        {
            string registrationId = measurements != null ? measurements.RegistrationId : "(unknown)";
            int red = metrics.Count(m => m.Status == QASemaphore.Red);
            int yellow = metrics.Count(m => m.Status == QASemaphore.Yellow);
            int green = metrics.Count(m => m.Status == QASemaphore.Green);

            if (red > 0)
            {
                set.OverallSeverity = AdvisorySeverity.Critical;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "NOT COMPLIANT — Registration '{0}' breaches {1} criterion(s) of the [{2}] profile. " +
                    "{3} metric(s) in the attention zone, {4} unevaluated.",
                    registrationId, red, profileName, yellow, unavailableCount);
                return;
            }

            if (yellow > 0)
            {
                set.OverallSeverity = AdvisorySeverity.Warning;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "REVIEW REQUIRED — Registration '{0}' has {1} metric(s) in the attention zone of the " +
                    "[{2}] profile. {3} unevaluated.",
                    registrationId, yellow, profileName, unavailableCount);
                return;
            }

            if (green == 0)
            {
                set.OverallSeverity = AdvisorySeverity.Warning;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "NO EVIDENCE — No metric of registration '{0}' could be evaluated. " +
                    "See the diagnostics tab for what failed.",
                    registrationId);
                return;
            }

            if (unavailableCount > 0)
            {
                set.OverallSeverity = AdvisorySeverity.Warning;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "PARTIALLY COMPLIANT — The {0} evaluated metric(s) of registration '{1}' meet the [{2}] " +
                    "profile, but {3} could not be measured. Verification is not complete.",
                    green, registrationId, profileName, unavailableCount);
                return;
            }

            set.OverallSeverity = AdvisorySeverity.Ok;
            set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                "COMPLIANT — All {0} evaluated metrics of registration '{1}' satisfy the tolerances of the " +
                "[{2}] profile.",
                green, registrationId, profileName);

            set.Advisories.Add(new Advisory(
                AdvisorySeverity.Ok,
                "STATUS",
                "All measured metrics fall within the quality thresholds of the active profile."));
        }
    }
}
