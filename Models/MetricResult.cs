using System;
using System.Globalization;

namespace ESAPI_RegistrationQA.Models
{
    public enum QASemaphore
    {
        Green,
        Yellow,
        Red,

        /// <summary>
        /// Measured and reported, but deliberately not classified against a tolerance.
        ///
        /// It exists to separate two situations an empty semaphore used to conflate: a metric
        /// with no number, and a metric with a perfectly good number that no defensible
        /// tolerance exists for. NCC, NMI and SSD are the second kind — TG-132 gives no limit
        /// for any of them and §4.C.3 says outright that they are "difficult to convert into
        /// quantitative measures of spatial accuracy" — so a red badge on one of them would
        /// assert a spatial failure the metric cannot establish.
        ///
        /// A metric in this state never contributes to the verdict, in either direction.
        /// </summary>
        Informational,

        NotAvailable
    }

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

        /// <summary>
        /// Section this metric belongs to, used to group the single table the window now
        /// shows. The metrics used to live in four separate tabs, which meant four clicks to
        /// see what had been measured and no way to take it in at a glance.
        /// </summary>
        public string Group { get; set; }

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

        /// <summary>
        /// True when the value follows from the kind of transform rather than from this
        /// registration. Shown like any other value, but excluded from the count of metrics
        /// that can establish compliance. See <see cref="MeasuredValue.IsAnalytic"/>.
        /// </summary>
        public bool IsAnalytic { get; set; }

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
            get
            {
                switch (Status)
                {
                    case QASemaphore.NotAvailable: return "N/A";
                    case QASemaphore.Informational: return "INFO";
                    default: return Status.ToString();
                }
            }
        }

        /// <summary>
        /// One line explaining the number: the tolerance it was held to, why it was held to
        /// none, or why there is no number at all.
        ///
        /// This replaces two separate columns. Showing criterion and reason side by side left
        /// neither with enough width — the screenshot that prompted this had both clipped
        /// mid-word. Whichever of the two applies is the one the reader needs, and the full
        /// text is still a hover away in <see cref="Tooltip"/>.
        /// </summary>
        public string Summary
        {
            get
            {
                if (!IsAvailable)
                    return UnavailableReason ?? "not measured";

                if (string.IsNullOrEmpty(ThresholdCriteria) || ThresholdCriteria == "—")
                    return MeasurementNote ?? string.Empty;

                // Both, when both exist. The criterion says what the value was judged against;
                // the note says what it was measured on, and for the surface metrics that is
                // the structure name — which must not be buried in a tooltip when the three
                // worst cases can come from three different structures.
                return string.IsNullOrEmpty(MeasurementNote)
                    ? ThresholdCriteria
                    : ThresholdCriteria + "   ·   " + MeasurementNote;
            }
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

                // The criterion column is narrow, so a metric shown as INFO can only say so in
                // a few words there. The reasoning belongs somewhere the reader can reach it
                // without opening the source, and the tooltip is the nearest such place.
                if (Status == QASemaphore.Informational)
                {
                    text += Environment.NewLine + Environment.NewLine +
                            "Not graded: " + definition.GatingBasis;
                }

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
