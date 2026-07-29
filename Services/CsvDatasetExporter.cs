using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Appends one row per audited registration to a CSV dataset.
    ///
    /// The HTML report is meant to be read and signed for a single case. This is the other
    /// half: a machine-readable record that can be accumulated over many cases and pooled
    /// across centres, which is what makes it possible to derive tolerance limits from real
    /// data instead of inheriting them from a textbook.
    ///
    /// Two design rules follow from the pooling intent.
    ///
    /// First, no direct identifiers. The patient identifier never reaches the file; cases
    /// are keyed by a hash of patient plus registration identifier. That key is stable, so
    /// re-running the same case does not duplicate a row, but it is a deduplication aid and
    /// not a substitute for whatever anonymisation review the institution requires before
    /// data leaves it.
    ///
    /// Second, every row carries its own provenance — voxel pairs, overlap, effective
    /// sampling resolution, tool version. A similarity value measured over 8% overlap at
    /// 4 mm sampling is not comparable with one measured over 80% at 1 mm, and pooled data
    /// without that context is not analysable.
    /// </summary>
    public static class CsvDatasetExporter
    {
        /// <summary>
        /// Bumped whenever columns are added, removed or redefined. Pooled files with
        /// different schema versions must not be concatenated blindly.
        /// </summary>
        // 5 adds DVFGradientMax. Bumped rather than appended silently: a pooled dataset mixing
        // rows with and without the column cannot be read by column position.
        public const string SchemaVersion = "5";

        private static readonly string[] Columns =
        {
            "SchemaVersion",
            "ToolVersion",
            "TimestampUtc",
            "Centre",
            "CaseHash",
            "RegistrationType",
            "FixedModality",
            "MovingModality",
            "Profile",
            "NCC",
            "NMI",
            "SSD",
            "JacobianNegPercent",
            "MaxDisplacement_mm",
            // Without it MaxDisplacement_mm is not analysable across a pooled dataset: within
            // one frame of reference it is the correction the registration applies, across two
            // it also spans the offset between the coordinate systems.
            "SameFrameOfReference",
            "Smoothness",
            "DVFGradientMax",
            "DSC",
            "MDA_mm",
            "HD95_mm",
            "StructurePairs",
            "WorstStructure",
            "TRE_Mean_mm",
            "TRE_Max_mm",
            "TRE_Landmarks",
            "InverseConsistency_mm",
            "NativeVoxel_mm",
            "TranslationX_LR_mm",
            "TranslationY_AP_mm",
            "TranslationZ_CC_mm",
            "TranslationMagnitude_mm",
            "RotationPitchX_deg",
            "RotationRollY_deg",
            "RotationYawZ_deg",
            "GimbalLock",
            "TransformSource",
            "VoxelPairs",
            "OverlapFraction",
            "EffectiveSampling_mm",
            "ToolVerdict",
            "DiagnosticFailures",
            "DiagnosticWarnings",
            "NotApplicable",
            "PhysicistVerdict",
            "Notes"
        };

        public sealed class AppendResult
        {
            public bool FileCreated { get; set; }
            public int TotalRows { get; set; }
        }

        /// <summary>
        /// Appends a row, writing the header first if the file is new. Appending rather than
        /// overwriting is deliberate: the intended workflow is to point every audit at the
        /// same file and let the dataset accumulate over weeks.
        /// </summary>
        public static AppendResult Append(ReportData data, string path)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Empty path.", "path");

            bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

            var sb = new StringBuilder();

            if (isNew)
                sb.AppendLine(string.Join(",", Columns.Select(Escape)));

            sb.AppendLine(string.Join(",", BuildRow(data).Select(Escape)));

            // UTF-8 with BOM: without it Excel misreads accented characters in the free-text
            // columns on a Windows workstation.
            if (isNew)
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            else
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));

            return new AppendResult
            {
                FileCreated = isNew,
                TotalRows = CountDataRows(path)
            };
        }

        private static List<string> BuildRow(ReportData data)
        {
            QaMeasurements m = data.Measurements;
            var row = new List<string>();

            row.Add(SchemaVersion);
            row.Add(HtmlReportBuilder.AssemblyVersion());
            row.Add(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

            // Left blank on purpose: filled once per site with a find-and-replace before the
            // file is shared, so no site code has to be configured in the plugin.
            row.Add(string.Empty);

            row.Add(CaseHash(data.PatientId, m != null ? m.RegistrationId : null));
            row.Add(m != null ? m.RegType.ToString() : string.Empty);
            row.Add(m != null ? m.FixedModality.ToString() : string.Empty);
            row.Add(m != null ? m.MovingModality.ToString() : string.Empty);
            row.Add(data.ProfileName ?? string.Empty);

            row.Add(Metric(m, MetricKeys.Ncc));
            row.Add(Metric(m, MetricKeys.Nmi));
            row.Add(Metric(m, MetricKeys.Ssd));
            row.Add(Metric(m, MetricKeys.JacobianNegative));
            row.Add(Metric(m, MetricKeys.MaxDisplacement));
            row.Add(m != null && m.SharesFrameOfReference.HasValue
                ? (m.SharesFrameOfReference.Value ? "TRUE" : "FALSE")
                : string.Empty);
            row.Add(Metric(m, MetricKeys.Smoothness));
            row.Add(Metric(m, MetricKeys.DvfGradientMax));
            row.Add(Metric(m, MetricKeys.Dsc));
            row.Add(Metric(m, MetricKeys.Mda));
            row.Add(Metric(m, MetricKeys.Hd95));
            row.Add(m != null && m.StructurePairCount > 0
                ? m.StructurePairCount.ToString(CultureInfo.InvariantCulture)
                : string.Empty);
            row.Add(m != null ? (m.WorstStructureId ?? string.Empty) : string.Empty);
            row.Add(Metric(m, MetricKeys.TreMean));
            row.Add(Metric(m, MetricKeys.TreMax));
            row.Add(m != null && m.TreLandmarkCount > 0
                ? m.TreLandmarkCount.ToString(CultureInfo.InvariantCulture)
                : string.Empty);
            row.Add(Metric(m, MetricKeys.InverseConsistency));
            row.Add(Number(m != null ? m.NativeVoxelSizeMm : null, "F2"));

            Vec3? translation = m != null && m.Transform != null ? m.Transform.Translation : (Vec3?)null;
            row.Add(Number(translation.HasValue ? translation.Value.X : (double?)null));
            row.Add(Number(translation.HasValue ? translation.Value.Y : (double?)null));
            row.Add(Number(translation.HasValue ? translation.Value.Z : (double?)null));
            row.Add(Number(translation.HasValue ? translation.Value.Length : (double?)null));

            EulerAngles? angles = m != null ? m.RigidEulerAngles : null;
            row.Add(Number(angles.HasValue ? angles.Value.PitchX : (double?)null));
            row.Add(Number(angles.HasValue ? angles.Value.RollY : (double?)null));
            row.Add(Number(angles.HasValue ? angles.Value.YawZ : (double?)null));
            row.Add(angles.HasValue ? (angles.Value.GimbalLock ? "TRUE" : "FALSE") : string.Empty);

            row.Add(m != null ? (m.TransformSource ?? string.Empty) : string.Empty);

            row.Add(m != null && m.SampleCount > 0
                ? m.SampleCount.ToString(CultureInfo.InvariantCulture)
                : string.Empty);
            row.Add(Number(m != null ? m.OverlapFraction : null, "F4"));
            row.Add(Number(m != null ? m.EffectiveSamplingMm : null, "F2"));

            row.Add(data.Advisories != null ? VerdictCode(data.Advisories.OverallSeverity) : string.Empty);

            int failures = 0, warnings = 0;
            if (data.Diagnostics != null)
            {
                failures = data.Diagnostics.Count(d => d.Level == DiagnosticLevel.Failure);
                warnings = data.Diagnostics.Count(d => d.Level == DiagnosticLevel.Warning);
            }
            row.Add(failures.ToString(CultureInfo.InvariantCulture));
            row.Add(warnings.ToString(CultureInfo.InvariantCulture));

            // Which metrics did not apply to this case. Empty cells alone cannot distinguish
            // a metric that failed from one that never applied, and pooled analysis needs
            // that difference: the first is a data quality problem, the second is not.
            row.Add(NotApplicableKeys(m));

            // The two columns the tool cannot fill. PhysicistVerdict is the ground-truth
            // label: without a human judgement next to each measurement there is nothing to
            // calibrate a threshold against.
            row.Add(string.Empty);
            row.Add(string.Empty);

            return row;
        }

        private static string NotApplicableKeys(QaMeasurements measurements)
        {
            if (measurements == null) return string.Empty;

            var keys = new List<string>();
            foreach (MetricDefinition definition in MetricCatalog.All)
            {
                if (measurements.ForKey(definition.Key).IsNotApplicable)
                    keys.Add(definition.Key);
            }

            return string.Join(";", keys.ToArray());
        }

        private static string Metric(QaMeasurements measurements, string key)
        {
            if (measurements == null) return string.Empty;

            MeasuredValue value = measurements.ForKey(key);
            // An unavailable metric is left as an empty cell, not as a zero or a sentinel.
            // Empty is what every analysis tool reads as missing data.
            return value.IsAvailable ? Number(value.Value) : string.Empty;
        }

        private static string Number(double? value, string format = "F6")
        {
            if (!value.HasValue) return string.Empty;
            return value.Value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string VerdictCode(AdvisorySeverity severity)
        {
            switch (severity)
            {
                case AdvisorySeverity.Critical: return "NOT_COMPLIANT";
                case AdvisorySeverity.Warning: return "REVIEW";
                case AdvisorySeverity.Ok: return "COMPLIANT";
                default: return "NO_EVIDENCE";
            }
        }

        /// <summary>
        /// Stable pseudonym for a case: first 12 hex characters of SHA-256 over the patient
        /// and registration identifiers.
        ///
        /// Its purpose is deduplication — re-auditing the same registration yields the same
        /// key, so repeated rows can be collapsed. It is not an anonymisation guarantee: a
        /// short identifier drawn from a small, known space can be attacked by enumeration.
        /// Treat the file as institutional data and apply the usual review before it leaves.
        /// </summary>
        public static string CaseHash(string patientId, string registrationId)
        {
            string material = (patientId ?? string.Empty) + "|" + (registrationId ?? string.Empty);

            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static string Escape(string field)
        {
            if (field == null) return string.Empty;

            bool needsQuotes = field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuotes) return field;

            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        private static int CountDataRows(string path)
        {
            try
            {
                int lines = File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l));
                return Math.Max(0, lines - 1);
            }
            catch
            {
                // The row count is only shown for reassurance in the confirmation dialog;
                // failing to read it back must not turn a successful append into an error.
                return -1;
            }
        }
    }
}
