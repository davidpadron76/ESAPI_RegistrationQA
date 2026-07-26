using System;
using System.Collections.Generic;
using System.Globalization;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Límites verde/amarillo de una métrica. La dirección de bondad (si mayor es mejor)
    /// no se almacena aquí: es una propiedad intrínseca de la métrica y vive en
    /// <see cref="MetricCatalog"/>, de modo que no puede quedar inconsistente entre perfiles.
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
        /// Clasifica un valor medido. Devuelve <see cref="QASemaphore.NotAvailable"/> si el
        /// perfil no define la métrica: no se inventa una clasificación por defecto.
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
        /// Texto del criterio de aceptación, mostrando ambos umbrales. Antes sólo se mostraba
        /// el límite verde, lo que hacía imposible entender por qué una métrica salía en amarillo.
        /// </summary>
        public string DescribeCriteria(string metricKey)
        {
            ThresholdLimits limits;
            if (!TryGetLimits(metricKey, out limits)) return "—";

            string op = MetricCatalog.HigherIsBetter(metricKey) ? "≥" : "≤";
            string unit = MetricCatalog.Unit(metricKey);
            string suffix = string.IsNullOrEmpty(unit) ? string.Empty : " " + unit;

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1:0.###}{2} (amarillo {0} {3:0.###}{2})",
                op, limits.GreenLimit, suffix, limits.YellowLimit);
        }

        /// <summary>Umbral verde en texto, para insertarlo en los mensajes de aviso.</summary>
        public string DescribeGreenLimit(string metricKey)
        {
            ThresholdLimits limits;
            if (!TryGetLimits(metricKey, out limits)) return "—";

            string unit = MetricCatalog.Unit(metricKey);
            string suffix = string.IsNullOrEmpty(unit) ? string.Empty : " " + unit;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###}{1}", limits.GreenLimit, suffix);
        }

        // 1. Cabeza y Cuello (ART H&N)
        public static ThresholdProfile CreateDefaultHN()
        {
            var profile = new ThresholdProfile { ProfileName = "ART Head & Neck" };
            profile.Limits.Add(MetricKeys.Nmi, new ThresholdLimits(1.50, 1.35));
            profile.Limits.Add(MetricKeys.Ncc, new ThresholdLimits(0.94, 0.88));
            profile.Limits.Add(MetricKeys.Ssd, new ThresholdLimits(0.025, 0.040));
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(1.0, 2.5));
            profile.Limits.Add(MetricKeys.MaxDisplacement, new ThresholdLimits(15.0, 22.0));
            profile.Limits.Add(MetricKeys.Smoothness, new ThresholdLimits(0.90, 0.80));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.85, 0.75));
            profile.Limits.Add(MetricKeys.Hd95, new ThresholdLimits(3.0, 5.0));
            return profile;
        }

        // 2. Cerebro / Radiocirugía (Brain / SRS) - Tolerancias estrictas
        public static ThresholdProfile CreateBrainSRS()
        {
            var profile = new ThresholdProfile { ProfileName = "Brain / SRS" };
            profile.Limits.Add(MetricKeys.Nmi, new ThresholdLimits(1.60, 1.45));
            profile.Limits.Add(MetricKeys.Ncc, new ThresholdLimits(0.96, 0.91));
            profile.Limits.Add(MetricKeys.Ssd, new ThresholdLimits(0.015, 0.030));
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(0.2, 1.0));
            profile.Limits.Add(MetricKeys.MaxDisplacement, new ThresholdLimits(5.0, 10.0));
            profile.Limits.Add(MetricKeys.Smoothness, new ThresholdLimits(0.95, 0.88));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.90, 0.82));
            profile.Limits.Add(MetricKeys.Hd95, new ThresholdLimits(2.0, 3.5));
            return profile;
        }

        // 3. Pelvis / Próstata
        public static ThresholdProfile CreatePelvis()
        {
            var profile = new ThresholdProfile { ProfileName = "Pelvis / Prostate" };
            profile.Limits.Add(MetricKeys.Nmi, new ThresholdLimits(1.40, 1.25));
            profile.Limits.Add(MetricKeys.Ncc, new ThresholdLimits(0.91, 0.85));
            profile.Limits.Add(MetricKeys.Ssd, new ThresholdLimits(0.030, 0.050));
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(1.5, 3.0));
            profile.Limits.Add(MetricKeys.MaxDisplacement, new ThresholdLimits(20.0, 30.0));
            profile.Limits.Add(MetricKeys.Smoothness, new ThresholdLimits(0.88, 0.78));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.80, 0.70));
            profile.Limits.Add(MetricKeys.Hd95, new ThresholdLimits(4.0, 6.0));
            return profile;
        }

        // 4. Tórax / Pulmón (Thorax / Lung)
        public static ThresholdProfile CreateThorax()
        {
            var profile = new ThresholdProfile { ProfileName = "Thorax / Lung" };
            profile.Limits.Add(MetricKeys.Nmi, new ThresholdLimits(1.35, 1.20));
            profile.Limits.Add(MetricKeys.Ncc, new ThresholdLimits(0.89, 0.82));
            profile.Limits.Add(MetricKeys.Ssd, new ThresholdLimits(0.035, 0.055));
            profile.Limits.Add(MetricKeys.JacobianNegative, new ThresholdLimits(2.0, 4.0));
            profile.Limits.Add(MetricKeys.MaxDisplacement, new ThresholdLimits(25.0, 35.0));
            profile.Limits.Add(MetricKeys.Smoothness, new ThresholdLimits(0.85, 0.75));
            profile.Limits.Add(MetricKeys.Dsc, new ThresholdLimits(0.80, 0.70));
            profile.Limits.Add(MetricKeys.Hd95, new ThresholdLimits(4.5, 6.5));
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
