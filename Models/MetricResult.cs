using System;
using System.Globalization;

namespace ESAPI_RegistrationQA.Models
{
    public enum QASemaphore { Green, Yellow, Red, NotAvailable }

    /// <summary>
    /// The evaluated result of a metric: the measured value plus its classification against
    /// the active tolerance profile.
    ///
    /// A metric that could not be measured is represented with a null <see cref="Value"/>,
    /// a <see cref="Status"/> of <see cref="QASemaphore.NotAvailable"/> and an
    /// <see cref="UnavailableReason"/> explaining why. It must never be substituted with a
    /// plausible-looking value: a signed QA report cannot contain numbers that did not come
    /// from a measurement.
    /// </summary>
    public sealed class MetricResult
    {
        /// <summary>Canonical key (see <see cref="MetricKeys"/>). Stable across UI changes.</summary>
        public string MetricKey { get; set; }

        /// <summary>Name shown on screen. Purely cosmetic.</summary>
        public string MetricName { get; set; }

        public double? Value { get; set; }
        public string Unit { get; set; }
        public QASemaphore Status { get; set; }
        public string ThresholdCriteria { get; set; }

        /// <summary>Why the metric is unavailable. Null when it is available.</summary>
        public string UnavailableReason { get; set; }

        /// <summary>How the value was obtained (e.g. "exact by definition").</summary>
        public string MeasurementNote { get; set; }

        public DateTime Timestamp { get; set; }

        public MetricResult()
        {
            Timestamp = DateTime.Now;
            Status = QASemaphore.NotAvailable;
            Unit = string.Empty;
        }

        public bool IsAvailable
        {
            get { return Value.HasValue && Status != QASemaphore.NotAvailable; }
        }

        /// <summary>
        /// Value text for the UI and the report. Invariant culture: the decimal separator
        /// must not depend on the regional settings of the planning workstation.
        /// </summary>
        public string DisplayValue
        {
            get
            {
                if (!Value.HasValue) return "N/A";
                string text = Value.Value.ToString("F3", CultureInfo.InvariantCulture);
                return string.IsNullOrEmpty(Unit) ? text : text + " " + Unit;
            }
        }

        public string StatusText
        {
            get { return Status == QASemaphore.NotAvailable ? "N/A" : Status.ToString(); }
        }

        /// <summary>
        /// Combined tooltip. Leads with the clinical question rather than the formula: the
        /// reader needs to know what they are being asked to decide before the arithmetic
        /// is of any use to them.
        /// </summary>
        public string Tooltip
        {
            get
            {
                MetricDefinition definition;
                if (!MetricCatalog.TryGet(MetricKey, out definition))
                    return MetricName;

                string text =
                    definition.ClinicalQuestion + Environment.NewLine + Environment.NewLine +
                    definition.Description + Environment.NewLine + Environment.NewLine +
                    "What it supports: " + definition.DecisionSupported + Environment.NewLine + Environment.NewLine +
                    "TG-132: " + definition.StandardBasis;

                if (!string.IsNullOrEmpty(MeasurementNote))
                    text += Environment.NewLine + Environment.NewLine + "Note: " + MeasurementNote;

                if (!IsAvailable && !string.IsNullOrEmpty(UnavailableReason))
                    text += Environment.NewLine + Environment.NewLine + "Not available: " + UnavailableReason;

                return text;
            }
        }

        public static MetricResult Unavailable(string metricKey, string reason)
        {
            MetricDefinition definition;
            MetricCatalog.TryGet(metricKey, out definition);

            return new MetricResult
            {
                MetricKey = metricKey,
                MetricName = definition != null ? definition.DisplayName : metricKey,
                Unit = definition != null ? definition.Unit : string.Empty,
                Value = null,
                Status = QASemaphore.NotAvailable,
                ThresholdCriteria = "—",
                UnavailableReason = reason
            };
        }
    }
}
