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
                    case AdvisorySeverity.Critical: return "CRÍTICO";
                    case AdvisorySeverity.Warning: return "REVISAR";
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
    /// Traduce métricas evaluadas en recomendaciones clínicas.
    ///
    /// Todos los umbrales salen del perfil activo. La versión anterior comparaba contra
    /// constantes (1.0 % de jacobiano, 15 mm de desplazamiento) mientras las tablas usaban
    /// los límites del perfil, de modo que con el perfil Brain/SRS un jacobiano del 0,5 %
    /// se pintaba en rojo pero no generaba aviso, y con el de Tórax un 1,5 % generaba un
    /// aviso crítico aunque el perfil lo aceptase.
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
            string profileName = profile != null ? profile.ProfileName : "(sin perfil)";

            // --- Métricas fuera de tolerancia ------------------------------------------
            foreach (MetricResult metric in metrics.Where(m => m.Status == QASemaphore.Red))
                set.Advisories.Add(BuildBreachAdvisory(metric, profile, profileName, AdvisorySeverity.Critical));

            foreach (MetricResult metric in metrics.Where(m => m.Status == QASemaphore.Yellow))
                set.Advisories.Add(BuildBreachAdvisory(metric, profile, profileName, AdvisorySeverity.Warning));

            // --- Métricas no evaluadas ---------------------------------------------------
            List<MetricResult> unavailable = metrics
                .Where(m => m.Status == QASemaphore.NotAvailable)
                .ToList();

            foreach (MetricResult metric in unavailable)
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Info,
                    "MÉTRICA NO EVALUADA",
                    metric.MetricName + " no se pudo medir: " + metric.UnavailableReason +
                    " Esta métrica no respalda ni contradice la aceptación del registro."));
            }

            // --- Contexto de modalidad ---------------------------------------------------
            AddModalityAdvisory(set, measurements);

            // --- Calidad del muestreo ----------------------------------------------------
            AddSamplingAdvisory(set, measurements);

            // --- Procedencia de la transformación ----------------------------------------
            if (measurements != null && measurements.Transform == null)
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Warning,
                    "TRANSFORMACIÓN",
                    "No se pudo obtener la transformación del registro. Los parámetros rígidos " +
                    "mostrados no proceden de una lectura válida."));
            }
            else if (measurements != null && measurements.RigidEulerAngles.HasValue &&
                     measurements.RigidEulerAngles.Value.GimbalLock)
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Warning,
                    "TRANSFORMACIÓN",
                    "La descomposición en ángulos de Euler cayó en bloqueo de cardán (cabeceo ≈ ±90°). " +
                    "Cabeceo y guiñada no son separables en esta orientación; interprete los tres " +
                    "ángulos como un conjunto, no de forma individual."));
            }

            // --- Veredicto global --------------------------------------------------------
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
                severity == AdvisorySeverity.Critical ? "FUERA DE TOLERANCIA" : "EN ZONA DE ATENCIÓN",
                string.Format(CultureInfo.InvariantCulture,
                    "{0} = {1} frente al criterio {2} del perfil [{3}] ({4}). {5}",
                    metric.MetricName, measured, limit, profileName, metric.ThresholdCriteria, interpretation));
        }

        /// <summary>
        /// Interpretación clínica asociada a cada métrica. Se selecciona por clave canónica,
        /// no por subcadena del nombre visible: renombrar una etiqueta en la UI no puede
        /// hacer que un aviso deje de dispararse en silencio.
        /// </summary>
        private static string InterpretBreach(string metricKey, AdvisorySeverity severity)
        {
            switch (metricKey)
            {
                case MetricKeys.JacobianNegative:
                    return severity == AdvisorySeverity.Critical
                        ? "Indica plegamiento de la rejilla: inversión topológica no física en el campo de " +
                          "deformación. El registro NO es apto para acumulación de dosis ni para propagación " +
                          "directa de contornos."
                        : "Presencia de plegamiento local en el campo de deformación. Verifique las regiones " +
                          "afectadas antes de propagar contornos.";

                case MetricKeys.MaxDisplacement:
                    return "Desplazamiento vectorial elevado. Confirme que corresponde a un cambio anatómico " +
                           "real y no a un error de correlación en los bordes del FOV.";

                case MetricKeys.Smoothness:
                    return "Campo de deformación irregular. Puede indicar sobreajuste del algoritmo en zonas " +
                           "de bajo contraste.";

                case MetricKeys.Nmi:
                case MetricKeys.Ncc:
                case MetricKeys.Ssd:
                    return "Posibles diferencias de contraste, artefactos metálicos, truncamiento del FOV o " +
                           "un desalineamiento real. Revise la superposición corte a corte.";

                case MetricKeys.Dsc:
                case MetricKeys.Hd95:
                    return "El solapamiento anatómico está fuera de tolerancia. Se recomienda verificación " +
                           "corte a corte y ajuste manual de los contornos propagados.";

                default:
                    return "Revise el valor frente al criterio del perfil.";
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
                    "MODALIDAD",
                    "Registro multimodal (" + pair + "). La métrica de referencia es " + primaryName +
                    ": el NCC asume una relación lineal entre intensidades que no se cumple entre " +
                    "modalidades distintas, por lo que su valor aquí es orientativo."));
            }
            else
            {
                set.Advisories.Add(new Advisory(
                    AdvisorySeverity.Info,
                    "MODALIDAD",
                    "Registro monomodal (" + pair + "). La métrica de referencia es " + primaryName + "."));
            }
        }

        private static void AddSamplingAdvisory(AdvisorySet set, QaMeasurements measurements)
        {
            if (measurements == null || measurements.SampleCount <= 0) return;

            string detail = string.Format(CultureInfo.InvariantCulture,
                "Similitud calculada sobre {0:N0} pares de vóxeles con muestreo efectivo de {1:F1} mm.",
                measurements.SampleCount,
                measurements.EffectiveSamplingMm.HasValue ? measurements.EffectiveSamplingMm.Value : 0.0);

            if (measurements.OverlapFraction.HasValue)
            {
                detail += string.Format(CultureInfo.InvariantCulture,
                    " Solapamiento entre volúmenes: {0:P1}.", measurements.OverlapFraction.Value);
            }

            AdvisorySeverity severity = AdvisorySeverity.Info;
            if (measurements.OverlapFraction.HasValue && measurements.OverlapFraction.Value < 0.35)
            {
                severity = AdvisorySeverity.Warning;
                detail += " Un solapamiento bajo concentra la métrica en una fracción reducida de la " +
                          "anatomía; su representatividad es limitada.";
            }

            set.Advisories.Add(new Advisory(severity, "MUESTREO", detail));
        }

        /// <summary>
        /// Veredicto global. Nunca declara un registro verificado si quedaron métricas sin
        /// evaluar: la ausencia de evidencia no es evidencia de conformidad, y es
        /// precisamente lo que la versión anterior afirmaba al rellenar los huecos con
        /// valores generados.
        /// </summary>
        private static void BuildVerdict(
            AdvisorySet set, List<MetricResult> metrics, int unavailableCount,
            string profileName, QaMeasurements measurements)
        {
            string registrationId = measurements != null ? measurements.RegistrationId : "(desconocido)";
            int red = metrics.Count(m => m.Status == QASemaphore.Red);
            int yellow = metrics.Count(m => m.Status == QASemaphore.Yellow);
            int green = metrics.Count(m => m.Status == QASemaphore.Green);

            if (red > 0)
            {
                set.OverallSeverity = AdvisorySeverity.Critical;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "NO CONFORME — El registro '{0}' incumple {1} criterio(s) del perfil [{2}]. " +
                    "{3} métrica(s) en zona de atención, {4} sin evaluar.",
                    registrationId, red, profileName, yellow, unavailableCount);
                return;
            }

            if (yellow > 0)
            {
                set.OverallSeverity = AdvisorySeverity.Warning;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "REVISIÓN REQUERIDA — El registro '{0}' tiene {1} métrica(s) en zona de atención " +
                    "del perfil [{2}]. {3} sin evaluar.",
                    registrationId, yellow, profileName, unavailableCount);
                return;
            }

            if (green == 0)
            {
                set.OverallSeverity = AdvisorySeverity.Warning;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "SIN EVIDENCIA — No se pudo evaluar ninguna métrica del registro '{0}'. " +
                    "Consulte la pestaña de diagnóstico para ver qué falló.",
                    registrationId);
                return;
            }

            if (unavailableCount > 0)
            {
                set.OverallSeverity = AdvisorySeverity.Warning;
                set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                    "CONFORME PARCIAL — Las {0} métrica(s) evaluadas del registro '{1}' cumplen el perfil " +
                    "[{2}], pero {3} no se pudieron medir. La verificación no es completa.",
                    green, registrationId, profileName, unavailableCount);
                return;
            }

            set.OverallSeverity = AdvisorySeverity.Ok;
            set.OverallStatus = string.Format(CultureInfo.InvariantCulture,
                "CONFORME — Las {0} métricas evaluadas del registro '{1}' satisfacen las tolerancias " +
                "del perfil [{2}].",
                green, registrationId, profileName);

            set.Advisories.Add(new Advisory(
                AdvisorySeverity.Ok,
                "ESTADO",
                "Todas las métricas medidas se encuentran dentro de los umbrales de calidad del perfil activo."));
        }
    }
}
