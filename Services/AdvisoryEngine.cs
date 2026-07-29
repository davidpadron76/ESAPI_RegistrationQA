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

            // --- Metrics that were attempted and failed ----------------------------------
            //
            // Only these reach the advisory list. Metrics that did not apply to the case have
            // already been filtered out upstream and are accounted for in the diagnostics.
            // Counting them here would have made every rigid registration "partially
            // compliant" for failing to measure deformation metrics that never applied to it.
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

            // --- What the similarity metrics do and do not establish ---------------------
            AddSimilarityScopeAdvisory(set, metrics);

            // --- TG-132 ties its spatial tolerances to the voxel size --------------------
            AddVoxelToleranceAdvisory(set, metrics, measurements);

            // --- Transform provenance ----------------------------------------------------
            if (measurements != null && measurements.Transform == null)
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Warning,
                    "TRANSFORM",
                    "The registration matrix could not be read from the API, so no translation, rotation " +
                    "or displacement is reported, and every metric that requires mapping a point through " +
                    "the registration is unavailable. Nothing has been estimated in their place. The " +
                    "property holding the matrix varies between Eclipse versions: the diagnostics tab " +
                    "lists what this registration object does expose."));
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

            // --- Frame of reference ------------------------------------------------------
            AddFrameOfReferenceAdvisory(set, measurements);

            // --- A registration that does not register anything --------------------------
            AddEmptyRegistrationAdvisory(set, measurements);

            // --- Overall verdict ---------------------------------------------------------
            BuildVerdict(set, metrics, unavailable.Count, profileName, measurements);

            return set;
        }

        private static Advisory BuildBreachAdvisory(
            MetricResult metric, ThresholdProfile profile, string profileName, AdvisorySeverity severity)
        {
            string limit = profile != null ? profile.DescribeGreenLimit(metric.MetricKey) : "—";
            string measured = metric.DisplayValue;

            string interpretation = InterpretBreach(metric.MetricKey);

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
        ///
        /// The severity no longer varies the text. The only metric that used to distinguish
        /// red from yellow was the Jacobian, and it now has no yellow band.
        /// </summary>
        private static string InterpretBreach(string metricKey)
        {
            switch (metricKey)
            {
                // There is no yellow band for this one: TG-132 Table III admits no negative
                // values, so the profiles set both limits at 0 and any folding at all is a
                // breach.
                case MetricKeys.JacobianNegative:
                    return "Grid folding: an unphysical topological inversion in the deformation field. " +
                           "TG-132 Table III admits no negative Jacobian values, which is why any " +
                           "non-zero percentage breaches. The report asks for the influence of the " +
                           "affected regions on the intended use to be evaluated; until that is done the " +
                           "registration is not suitable for dose accumulation or for direct contour " +
                           "propagation.";

                case MetricKeys.MaxDisplacement:
                    return "High vector displacement. Confirm that it corresponds to a real anatomical " +
                           "change and not to a correlation error at the FOV boundaries. This advisory " +
                           "only appears when both series share a frame of reference; otherwise the " +
                           "metric is not classified at all.";

                // NCC, NMI, SSD and Smoothness are deliberately absent from this switch. They
                // no longer gate, so they cannot reach a red or yellow state and no breach
                // advisory can be raised for them. Arms that can never execute would suggest
                // otherwise.

                case MetricKeys.Dsc:
                case MetricKeys.Mda:
                case MetricKeys.Hd95:
                    return "Anatomical overlap is out of tolerance. Slice-by-slice verification and manual " +
                           "adjustment of the propagated contours are recommended.";

                case MetricKeys.TreMean:
                    return "Landmark residual exceeds tolerance. TG-132 Table III sets this at the maximum " +
                           "voxel dimension. This is a direct measure of spatial error, so it carries more " +
                           "weight than any of the intensity metrics.";

                case MetricKeys.TreMax:
                    return "At least one landmark is displaced beyond tolerance even if the mean is " +
                           "acceptable. Identify which one: a single large residual usually points to a " +
                           "local misalignment rather than a global registration failure.";

                case MetricKeys.InverseConsistency:
                    return "Registering in both directions does not return points to their origin. Per " +
                           "TG-132 §4.C.4 this evidences an unstable algorithm. Note that it does not by " +
                           "itself prove inaccuracy — a registration can be consistently wrong — but a " +
                           "large residual invalidates any accuracy claim.";

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

        /// <summary>
        /// States the two limits TG-132 places on intensity similarity metrics.
        ///
        /// Section 4.C.3 allows SSD, CC and MI to be used for assessment, but only when the
        /// metric was not the one the registration algorithm optimised — otherwise the
        /// assessment is circular — and it notes that these metrics are difficult to convert
        /// into a quantitative measure of spatial accuracy.
        ///
        /// This matters because those three are the metrics this plugin computes best, and a
        /// green NCC could easily be read as "geometrically accurate". It does not establish
        /// that. The quantitative metrics TG-132 lists for spatial accuracy are in Table III:
        /// TRE, MDA, DSC, Jacobian determinant and consistency.
        /// </summary>
        private static void AddSimilarityScopeAdvisory(AdvisorySet set, List<MetricResult> metrics)
        {
            bool anySimilarityMeasured = metrics.Any(m =>
                m.IsAvailable &&
                (m.MetricKey == MetricKeys.Ncc || m.MetricKey == MetricKeys.Nmi || m.MetricKey == MetricKeys.Ssd));

            if (!anySimilarityMeasured) return;

            set.Advisories.Add(new Advisory(
                AdvisorySeverity.Info,
                "SCOPE OF SIMILARITY METRICS",
                "TG-132 §4.C.3 admits SSD, CC and MI for assessing a registration only if the metric was " +
                "not the one optimised by the registration algorithm itself; otherwise the assessment is " +
                "circular. Confirm which metric drives your TPS registration. TG-132 also notes these " +
                "metrics are difficult to convert into a measure of spatial accuracy: a compliant value " +
                "does not establish millimetric accuracy. For that, Table III lists TRE, MDA, DSC, the " +
                "Jacobian determinant and consistency."));
        }

        /// <summary>
        /// TG-132 does not express the tolerance for TRE and consistency as a fixed number:
        /// Table III says "maximum voxel dimension (~2–3 mm)". The profiles in this tool use
        /// fixed values, so where the two disagree the reader needs to know the actual voxel
        /// size in order to apply the report's rule rather than the profile's approximation.
        /// </summary>
        private static void AddVoxelToleranceAdvisory(
            AdvisorySet set, List<MetricResult> metrics, QaMeasurements measurements)
        {
            if (measurements == null || !measurements.NativeVoxelSizeMm.HasValue) return;

            bool anySpatialMeasured = metrics.Any(m =>
                m.IsAvailable &&
                (m.MetricKey == MetricKeys.TreMean ||
                 m.MetricKey == MetricKeys.TreMax ||
                 m.MetricKey == MetricKeys.InverseConsistency));

            if (!anySpatialMeasured) return;

            double voxel = measurements.NativeVoxelSizeMm.Value;

            string detail = string.Format(CultureInfo.InvariantCulture,
                "TG-132 Table III sets the tolerance for TRE and consistency at the maximum voxel " +
                "dimension, which for this image pair is {0:F2} mm. The profile thresholds shown in the " +
                "tables are fixed values; where they differ from {0:F2} mm, the report's rule is the one " +
                "to apply.", voxel);

            if (measurements.TreLandmarkCount > 0)
            {
                detail += string.Format(CultureInfo.InvariantCulture,
                    " TRE was computed over {0} matched landmark(s).", measurements.TreLandmarkCount);
            }

            set.Advisories.Add(new Advisory(AdvisorySeverity.Info, "TOLERANCE BASIS", detail));
        }

        /// <summary>
        /// States whether the two series share a DICOM frame of reference, but only when
        /// there is a maximum displacement on screen for the answer to bear on. It is the one
        /// metric whose interpretation the answer changes.
        /// </summary>
        /// <summary>
        /// Says so when the registration does not move anything.
        ///
        /// Eclipse hands out a perfectly valid identity matrix for a registration that was
        /// created and never executed, and nothing else in the report distinguishes that from a
        /// registration whose answer genuinely is "no shift". Three sessions were spent chasing
        /// metrics that read as empty when the registration under audit was itself empty, and
        /// nothing said so. Approval status alone is not enough — a real registration can sit
        /// unapproved — so both conditions have to hold.
        /// </summary>
        private static void AddEmptyRegistrationAdvisory(AdvisorySet set, QaMeasurements measurements)
        {
            if (measurements == null || measurements.Transform == null) return;
            if (!measurements.Transform.IsIdentity()) return;

            bool unapproved = !string.IsNullOrEmpty(measurements.RegistrationStatus) &&
                              measurements.RegistrationStatus.IndexOf(
                                  "unapproved", StringComparison.OrdinalIgnoreCase) >= 0;

            string status = string.IsNullOrEmpty(measurements.RegistrationStatus)
                ? string.Empty
                : " Its status is " + measurements.RegistrationStatus + ".";

            set.Advisories.Add(new Advisory(
                unapproved ? AdvisorySeverity.Warning : AdvisorySeverity.Info,
                "EMPTY REGISTRATION",
                "The transform is exactly the identity: this registration moves nothing." + status +
                (unapproved
                    ? " An unapproved registration with an identity transform is almost always one that " +
                      "was created and never executed. Run the registration, then audit it — the metrics " +
                      "below describe the alignment of the two series as acquired, not the work of any " +
                      "registration."
                    : " If that is the intended answer the series were already aligned; otherwise check " +
                      "that the registration was actually executed.")));
        }

        private static void AddFrameOfReferenceAdvisory(AdvisorySet set, QaMeasurements measurements)
        {
            if (measurements == null) return;
            if (!measurements.MaxDisplacement.IsAvailable) return;

            bool? shares = measurements.SharesFrameOfReference;

            if (shares.HasValue && shares.Value) return;

            string detail = shares.HasValue
                ? "The two series are in different DICOM Frames of Reference. The registration matrix " +
                  "therefore spans two coordinate systems, and the maximum displacement includes the " +
                  "offset between them as well as the correction being applied. A correct registration " +
                  "between images from two different scanners, or between a CT and an MR, can exceed any " +
                  "profile limit for that reason alone, so the metric is reported without a " +
                  "classification."
                : "At least one series does not expose its Frame of Reference UID, so it cannot be " +
                  "established whether the two share a coordinate system. Maximum displacement is " +
                  "reported without a classification: if the frames differ, its magnitude is not a " +
                  "registration correction.";

            set.Advisories.Add(new Advisory(AdvisorySeverity.Info, "FRAME OF REFERENCE", detail));
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

            // Analytic values are excluded from the green count on purpose. A rigid transform
            // has |J| = 1 and a constant field gradient by definition, so those two metrics
            // pass on every rigid registration ever audited and say nothing about this one.
            // Counting them made a case whose images would not load at all come out as
            // PARTIALLY COMPLIANT, on the strength of "the 1 evaluated metric meets the
            // profile" — a tautology standing in for verification.
            int green = metrics.Count(m => m.Status == QASemaphore.Green && !m.IsAnalytic);
            int analytic = metrics.Count(m => m.IsAnalytic && m.Status != QASemaphore.NotAvailable);

            // Measured, but with no tolerance behind them, so they cannot count towards
            // compliance or against it. They are counted separately because the difference
            // between "nothing was measured" and "things were measured that cannot decide"
            // is exactly what the reader needs in order to know what to do next.
            // Analytic values are excluded here too. Smoothness on a rigid transform is both
            // informational and a tautology, and counting it would let the verdict announce
            // that one metric was measured when nothing about this registration was.
            int informational = metrics.Count(m => m.Status == QASemaphore.Informational && !m.IsAnalytic);

            if (red > 0)
            {
                set.OverallSeverity = AdvisorySeverity.Critical;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "NOT COMPLIANT — Registration '{0}' breaches {1} criterion(s) of the [{2}] profile. " +
                    "{3} metric(s) in the attention zone, {4} unevaluated, {5} for information only.",
                    registrationId, red, profileName, yellow, unavailableCount, informational);
                return;
            }

            if (yellow > 0)
            {
                set.OverallSeverity = AdvisorySeverity.Warning;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "REVIEW REQUIRED — Registration '{0}' has {1} metric(s) in the attention zone of the " +
                    "[{2}] profile. {3} unevaluated, {4} for information only.",
                    registrationId, yellow, profileName, unavailableCount, informational);
                return;
            }

            if (green == 0 && informational > 0)
            {
                set.OverallSeverity = AdvisorySeverity.Warning;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "NOT VERIFIED — {0} metric(s) of registration '{1}' were measured, but none of them " +
                    "carries a tolerance that can establish compliance, and {2} could not be measured. " +
                    "The metrics that can verify a registration are TRE, MDA, DSC and consistency: " +
                    "placing matched landmarks, or contouring the same structure on both series, is what " +
                    "enables them.",
                    informational, registrationId, unavailableCount);
                return;
            }

            if (green == 0)
            {
                set.OverallSeverity = AdvisorySeverity.Warning;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "NO EVIDENCE — No metric of registration '{0}' could be evaluated. " +
                    "See the diagnostics tab for what failed.{1}",
                    registrationId,
                    analytic > 0
                        ? string.Format(CultureInfo.InvariantCulture,
                            " The {0} metric(s) shown with a value are exact by definition for this " +
                            "kind of transform and would read the same on any registration, so they " +
                            "verify nothing.", analytic)
                        : string.Empty);
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
                "[{2}] profile.{3}",
                green, registrationId, profileName,
                informational > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        " A further {0} metric(s) were measured for information only.", informational)
                    : string.Empty);

            set.Advisories.Add(new Advisory(
                AdvisorySeverity.Ok,
                "STATUS",
                "All measured metrics fall within the quality thresholds of the active profile."));
        }
    }
}
