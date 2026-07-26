using System;
using System.Globalization;

namespace ESAPI_RegistrationQA.Models
{
    public enum QASemaphore { Green, Yellow, Red, NotAvailable }

    /// <summary>
    /// Resultado evaluado de una métrica: el valor medido más su clasificación frente al
    /// perfil de tolerancia activo.
    ///
    /// Una métrica que no se pudo medir se representa con <see cref="Value"/> nulo,
    /// <see cref="Status"/> igual a <see cref="QASemaphore.NotAvailable"/> y un
    /// <see cref="UnavailableReason"/> que explica por qué. Nunca debe sustituirse por un
    /// valor plausible inventado: un reporte de QA firmado no puede contener números que
    /// no procedan de una medición.
    /// </summary>
    public sealed class MetricResult
    {
        /// <summary>Clave canónica (ver <see cref="MetricKeys"/>). Estable frente a cambios de UI.</summary>
        public string MetricKey { get; set; }

        /// <summary>Nombre mostrado en pantalla. Puramente cosmético.</summary>
        public string MetricName { get; set; }

        public double? Value { get; set; }
        public string Unit { get; set; }
        public QASemaphore Status { get; set; }
        public string ThresholdCriteria { get; set; }

        /// <summary>Motivo por el que la métrica no está disponible. Nulo si sí lo está.</summary>
        public string UnavailableReason { get; set; }

        /// <summary>Aclaración sobre cómo se obtuvo el valor (p. ej. "exacto por definición").</summary>
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
        /// Texto del valor para UI y reporte. Cultura invariante: el punto decimal no debe
        /// depender de la configuración regional de la estación de planificación.
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

        /// <summary>Tooltip combinado: definición de la métrica, motivo de indisponibilidad y notas.</summary>
        public string Tooltip
        {
            get
            {
                MetricDefinition definition;
                string text = MetricCatalog.TryGet(MetricKey, out definition)
                    ? definition.Description
                    : MetricName;

                if (!string.IsNullOrEmpty(MeasurementNote))
                    text += Environment.NewLine + Environment.NewLine + "Nota: " + MeasurementNote;

                if (!IsAvailable && !string.IsNullOrEmpty(UnavailableReason))
                    text += Environment.NewLine + Environment.NewLine + "No disponible: " + UnavailableReason;

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
