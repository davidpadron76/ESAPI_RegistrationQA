using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    public sealed class ReportData
    {
        public string PatientId { get; set; }
        public string ProfileName { get; set; }
        public QaMeasurements Measurements { get; set; }
        public AdvisorySet Advisories { get; set; }
        public IList<MetricResult> IntensityMetrics { get; set; }
        public IList<MetricResult> DeformationMetrics { get; set; }
        public IList<MetricResult> StructureMetrics { get; set; }
        public IList<RigidTransformItem> RigidTransform { get; set; }
        public IList<DiagnosticEntry> Diagnostics { get; set; }
    }

    /// <summary>
    /// Generates the HTML audit report.
    ///
    /// Two invariable rules:
    ///  - all text coming from data (patient identifier, registration identifier,
    ///    unavailability reasons) is escaped before insertion. It used to be interpolated
    ///    raw, so an identifier containing &lt;, &amp; or quotes broke the document;
    ///  - all numbers are formatted in invariant culture. They used to inherit the
    ///    workstation culture, so on a Spanish configuration decimals came out with a comma,
    ///    which prevents consistent archiving or reprocessing of the report.
    /// </summary>
    public static class HtmlReportBuilder
    {
        public static string Build(ReportData data)
        {
            if (data == null) throw new ArgumentNullException("data");

            var sb = new StringBuilder(32768);

            sb.Append("<!DOCTYPE html><html lang='en'><head><meta charset='utf-8'/>");
            sb.Append("<title>Registration QA Report</title>");
            sb.Append(Styles);
            sb.Append("</head><body><div class='container'>");

            AppendHeader(sb, data);
            AppendVerdict(sb, data);
            AppendAdvisories(sb, data);
            AppendMetricsTable(sb, "1. Intensity similarity and topological metrics",
                data.IntensityMetrics, data.DeformationMetrics);
            AppendStructureTable(sb, data);
            AppendRigidTable(sb, data);
            AppendProvenance(sb, data);
            AppendDiagnostics(sb, data);
            AppendSignatures(sb);
            AppendFooter(sb, data);

            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        public static void Save(ReportData data, string path)
        {
            // Explicit UTF-8, consistent with the charset declared in the header.
            File.WriteAllText(path, Build(data), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------ sections

        private static void AppendHeader(StringBuilder sb, ReportData data)
        {
            QaMeasurements m = data.Measurements;

            sb.Append("<div class='header'><h1>Registration Quantitative Audit Report</h1>");
            sb.Append("<div class='meta'>");
            AppendMeta(sb, "Patient", data.PatientId);
            AppendMeta(sb, "Registration", m != null ? m.RegistrationId : null);
            AppendMeta(sb, "Type", m != null ? m.RegType.ToString() : null);
            AppendMeta(sb, "Profile", data.ProfileName);
            AppendMeta(sb, "Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            sb.Append("</div></div>");
        }

        private static void AppendMeta(StringBuilder sb, string label, string value)
        {
            sb.Append("<span><b>").Append(E(label)).Append(":</b> ")
              .Append(E(string.IsNullOrEmpty(value) ? "—" : value)).Append("</span>");
        }

        private static void AppendVerdict(StringBuilder sb, ReportData data)
        {
            if (data.Advisories == null) return;

            string cssClass = SeverityClass(data.Advisories.OverallSeverity);
            sb.Append("<div class='verdict ").Append(cssClass).Append("'>")
              .Append(E(data.Advisories.OverallStatus))
              .Append("</div>");
        }

        private static void AppendAdvisories(StringBuilder sb, ReportData data)
        {
            if (data.Advisories == null || data.Advisories.Advisories.Count == 0) return;

            sb.Append("<h2>Clinical advisories (AAPM TG-132 / TG-233)</h2>");
            sb.Append("<table class='advisories'><tr><th>Level</th><th>Scope</th><th>Observation</th></tr>");

            foreach (Advisory advisory in data.Advisories.Advisories
                         .OrderByDescending(a => a.Severity))
            {
                sb.Append("<tr><td><span class='badge ").Append(SeverityClass(advisory.Severity)).Append("'>")
                  .Append(E(advisory.SeverityText)).Append("</span></td><td>")
                  .Append(E(advisory.Category)).Append("</td><td>")
                  .Append(E(advisory.Message)).Append("</td></tr>");
            }

            sb.Append("</table>");
        }

        private static void AppendMetricsTable(
            StringBuilder sb, string title,
            IList<MetricResult> first, IList<MetricResult> second)
        {
            sb.Append("<h2>").Append(E(title)).Append("</h2>");
            sb.Append("<table><tr><th>Metric</th><th>Value</th><th>Acceptance criterion</th><th>Status</th></tr>");

            foreach (MetricResult metric in Concat(first, second))
                AppendMetricRow(sb, metric);

            sb.Append("</table>");
        }

        private static void AppendStructureTable(StringBuilder sb, ReportData data)
        {
            sb.Append("<h2>2. Structure and surface alignment</h2>");
            sb.Append("<table><tr><th>Anatomical index</th><th>Value</th><th>Acceptance criterion</th><th>Status</th></tr>");

            foreach (MetricResult metric in data.StructureMetrics ?? new List<MetricResult>())
                AppendMetricRow(sb, metric);

            sb.Append("</table>");
        }

        private static void AppendMetricRow(StringBuilder sb, MetricResult metric)
        {
            string statusClass = StatusClass(metric.Status);

            sb.Append("<tr").Append(metric.IsAvailable ? string.Empty : " class='na-row'").Append(">");
            sb.Append("<td><b>").Append(E(metric.MetricName)).Append("</b></td>");
            sb.Append("<td>").Append(E(metric.DisplayValue)).Append("</td>");
            sb.Append("<td>").Append(E(metric.ThresholdCriteria)).Append("</td>");
            sb.Append("<td><span class='badge ").Append(statusClass).Append("'>")
              .Append(E(metric.StatusText)).Append("</span></td>");
            sb.Append("</tr>");

            string annotation = metric.IsAvailable ? metric.MeasurementNote : metric.UnavailableReason;
            if (!string.IsNullOrEmpty(annotation))
            {
                sb.Append("<tr class='annotation'><td colspan='4'>")
                  .Append(metric.IsAvailable ? "Note: " : "Reason: ")
                  .Append(E(annotation))
                  .Append("</td></tr>");
            }
        }

        private static void AppendRigidTable(StringBuilder sb, ReportData data)
        {
            sb.Append("<h2>3. Rigid transformation parameters</h2>");
            sb.Append("<p class='note'>DICOM patient coordinates: X = left-right (LR), ");
            sb.Append("Y = anterior-posterior (AP), Z = cranio-caudal (CC). ");
            sb.Append("Euler angles in the intrinsic Rz·Ry·Rx convention.</p>");

            sb.Append("<table><tr><th>Spatial parameter</th><th>Value</th><th>Unit</th></tr>");

            foreach (RigidTransformItem item in data.RigidTransform ?? new List<RigidTransformItem>())
            {
                sb.Append("<tr").Append(item.Value.HasValue ? string.Empty : " class='na-row'").Append(">");
                sb.Append("<td><b>").Append(E(item.Parameter)).Append("</b></td>");
                sb.Append("<td>").Append(E(item.DisplayValue)).Append("</td>");
                sb.Append("<td>").Append(E(item.Unit)).Append("</td>");
                sb.Append("</tr>");
            }

            sb.Append("</table>");
        }

        private static void AppendProvenance(StringBuilder sb, ReportData data)
        {
            QaMeasurements m = data.Measurements;
            if (m == null) return;

            sb.Append("<h2>4. Measurement provenance and traceability</h2>");
            sb.Append("<table class='provenance'>");

            AppendProvenanceRow(sb, "Transform source", m.TransformSource);
            AppendProvenanceRow(sb, "Modalities", m.FixedModality + " → " + m.MovingModality);

            if (m.SampleCount > 0)
            {
                AppendProvenanceRow(sb, "Voxel pairs evaluated",
                    m.SampleCount.ToString("N0", CultureInfo.InvariantCulture));
            }

            if (m.OverlapFraction.HasValue)
            {
                AppendProvenanceRow(sb, "Overlap between volumes",
                    m.OverlapFraction.Value.ToString("P1", CultureInfo.InvariantCulture));
            }

            if (m.EffectiveSamplingMm.HasValue)
            {
                AppendProvenanceRow(sb, "Effective sampling resolution",
                    m.EffectiveSamplingMm.Value.ToString("F2", CultureInfo.InvariantCulture) + " mm");
            }

            sb.Append("</table>");
        }

        private static void AppendProvenanceRow(StringBuilder sb, string label, string value)
        {
            sb.Append("<tr><td class='label'>").Append(E(label)).Append("</td><td>")
              .Append(E(string.IsNullOrEmpty(value) ? "—" : value)).Append("</td></tr>");
        }

        private static void AppendDiagnostics(StringBuilder sb, ReportData data)
        {
            if (data.Diagnostics == null || data.Diagnostics.Count == 0) return;

            List<DiagnosticEntry> relevant = data.Diagnostics
                .Where(d => d.Level != DiagnosticLevel.Info)
                .ToList();

            if (relevant.Count == 0) return;

            sb.Append("<h2>5. API access diagnostics</h2>");
            sb.Append("<p class='note'>Issues recorded while reading the data. ");
            sb.Append("They are included so that every unevaluated metric has a traceable cause.</p>");
            sb.Append("<table class='diagnostics'><tr><th>Level</th><th>Operation</th><th>Detail</th></tr>");

            foreach (DiagnosticEntry entry in relevant)
            {
                string cssClass = entry.Level == DiagnosticLevel.Failure ? "badge-red" : "badge-yellow";
                sb.Append("<tr><td><span class='badge ").Append(cssClass).Append("'>")
                  .Append(E(entry.LevelText)).Append("</span></td><td>")
                  .Append(E(entry.Operation)).Append("</td><td>")
                  .Append(E(entry.Detail)).Append("</td></tr>");
            }

            sb.Append("</table>");
        }

        private static void AppendSignatures(StringBuilder sb)
        {
            sb.Append("<div class='signatures'>");
            sb.Append("<div class='sig'>Medical physicist</div>");
            sb.Append("<div class='sig'>Radiation oncologist</div>");
            sb.Append("</div>");
        }

        private static void AppendFooter(StringBuilder sb, ReportData data)
        {
            sb.Append("<div class='footer'>");
            sb.Append("<span>Generated by ESAPI_RegistrationQA v").Append(E(AssemblyVersion())).Append("</span>");
            sb.Append("<span>Active profile: ").Append(E(data.ProfileName ?? "—")).Append("</span>");
            sb.Append("<span>").Append(E(Environment.UserName)).Append("</span>");
            sb.Append("</div>");
        }

        // ------------------------------------------------------------------ utilities

        /// <summary>HTML escaping. The single point through which all inserted text passes.</summary>
        private static string E(string text)
        {
            return WebUtility.HtmlEncode(text ?? string.Empty);
        }

        private static IEnumerable<MetricResult> Concat(IList<MetricResult> a, IList<MetricResult> b)
        {
            if (a != null) foreach (MetricResult m in a) yield return m;
            if (b != null) foreach (MetricResult m in b) yield return m;
        }

        private static string StatusClass(QASemaphore status)
        {
            switch (status)
            {
                case QASemaphore.Green: return "badge-green";
                case QASemaphore.Yellow: return "badge-yellow";
                case QASemaphore.Red: return "badge-red";
                default: return "badge-na";
            }
        }

        private static string SeverityClass(AdvisorySeverity severity)
        {
            switch (severity)
            {
                case AdvisorySeverity.Critical: return "badge-red";
                case AdvisorySeverity.Warning: return "badge-yellow";
                case AdvisorySeverity.Ok: return "badge-green";
                default: return "badge-na";
            }
        }

        /// <summary>
        /// Assembly version. A signed QA report must be traceable to the exact version of the
        /// code that produced it.
        /// </summary>
        public static string AssemblyVersion()
        {
            try
            {
                Assembly assembly = typeof(HtmlReportBuilder).Assembly;
                var info = (AssemblyInformationalVersionAttribute[])assembly
                    .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);

                if (info.Length > 0 && !string.IsNullOrEmpty(info[0].InformationalVersion))
                    return info[0].InformationalVersion;

                return assembly.GetName().Version.ToString();
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>Strips characters that are not allowed in a file name.</summary>
        public static string SanitizeFileName(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return "no_id";

            var sb = new StringBuilder(candidate.Length);
            char[] invalid = Path.GetInvalidFileNameChars();

            foreach (char c in candidate)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            return sb.ToString();
        }

        private const string Styles = @"<style>
@page { size: A4 portrait; margin: 12mm; }
body { font-family: 'Segoe UI', Arial, sans-serif; margin: 0; color: #333; background: #fff;
       -webkit-print-color-adjust: exact; print-color-adjust: exact; font-size: 11px; }
.container { width: 100%; max-width: 190mm; margin: 0 auto; box-sizing: border-box; }
.header { background: #005a9c !important; color: #fff !important; padding: 12px 18px;
          border-radius: 5px; margin-bottom: 12px; }
.header h1 { margin: 0 0 6px 0; font-size: 15px; text-transform: uppercase; letter-spacing: 0.5px; }
.meta { display: flex; flex-wrap: wrap; gap: 14px; font-size: 11px; opacity: 0.95; }
.verdict { padding: 10px 14px; margin-bottom: 12px; font-weight: 600; font-size: 12px;
           border-left: 4px solid #999; background: #f4f6f9 !important; border-radius: 3px; }
.verdict.badge-red { border-left-color: #c62828; background: #ffebee !important; color: #b71c1c; }
.verdict.badge-yellow { border-left-color: #f9a825; background: #fffde7 !important; color: #7f6000; }
.verdict.badge-green { border-left-color: #2e7d32; background: #e8f5e9 !important; color: #1b5e20; }
.verdict.badge-na { border-left-color: #757575; color: #424242; }
h2 { color: #005a9c; border-bottom: 1.5px solid #005a9c; padding-bottom: 3px; font-size: 12px;
     margin: 16px 0 6px 0; text-transform: uppercase; }
p.note { font-size: 10px; color: #666; margin: 4px 0 6px 0; }
table { width: 100%; border-collapse: collapse; margin: 6px 0 10px 0; font-size: 10.5px;
        page-break-inside: avoid; }
th, td { border: 1px solid #d0d0d0; padding: 5px 8px; text-align: left; vertical-align: top; }
th { background: #f0f4f8 !important; color: #005a9c; font-weight: bold; }
tr.na-row td { color: #777; background: #fafafa !important; }
tr.annotation td { font-size: 9.5px; color: #666; background: #fbfbfb !important;
                   border-top: none; font-style: italic; }
table.provenance td.label { width: 40%; font-weight: 600; color: #005a9c; }
.badge { padding: 2px 7px; border-radius: 3px; font-weight: bold; text-align: center;
         display: inline-block; min-width: 46px; font-size: 9.5px; }
.badge-green { background: #c8e6c9 !important; color: #2e7d32 !important; }
.badge-yellow { background: #fff9c4 !important; color: #f57f17 !important; }
.badge-red { background: #ffcdd2 !important; color: #c62828 !important; }
.badge-na { background: #eceff1 !important; color: #546e7a !important; }
.signatures { margin-top: 28px; display: flex; justify-content: space-between;
              page-break-inside: avoid; }
.sig { width: 200px; border-top: 1px solid #333; text-align: center; padding-top: 4px;
       font-size: 10.5px; font-weight: 600; color: #444; }
.footer { margin-top: 15px; padding-top: 8px; border-top: 1px solid #e0e0e0; display: flex;
          justify-content: space-between; font-size: 9.5px; color: #777; page-break-inside: avoid; }
</style>";
    }
}
