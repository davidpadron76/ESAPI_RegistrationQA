using System;
using System.Collections.Generic;
using System.Globalization;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Convierte mediciones crudas en resultados evaluados frente a un perfil de tolerancia.
    ///
    /// Es una función pura de (mediciones, perfil) → resultados: no toca la API, no lee
    /// vóxeles y no recorre estructuras. Ésa es exactamente la separación que faltaba: antes,
    /// cambiar el perfil anatómico en el desplegable volvía a ejecutar todo el pipeline de
    /// medición para obtener los mismos números y clasificarlos de otra forma.
    /// </summary>
    public static class MetricEvaluator
    {
        public static readonly string[] IntensityKeys =
        {
            MetricKeys.Nmi, MetricKeys.Ncc, MetricKeys.Ssd
        };

        public static readonly string[] DeformationKeys =
        {
            MetricKeys.JacobianNegative, MetricKeys.MaxDisplacement, MetricKeys.Smoothness
        };

        public static readonly string[] StructureKeys =
        {
            MetricKeys.Dsc, MetricKeys.Hd95
        };

        public static List<MetricResult> Evaluate(
            QaMeasurements measurements, ThresholdProfile profile, IEnumerable<string> keys)
        {
            var results = new List<MetricResult>();
            if (keys == null) return results;

            foreach (string key in keys)
                results.Add(EvaluateOne(measurements, profile, key));

            return results;
        }

        public static MetricResult EvaluateOne(
            QaMeasurements measurements, ThresholdProfile profile, string key)
        {
            MetricDefinition definition;
            MetricCatalog.TryGet(key, out definition);

            string displayName = definition != null ? definition.DisplayName : key;
            string unit = definition != null ? definition.Unit : string.Empty;

            if (measurements == null)
                return MetricResult.Unavailable(key, "no se ejecutó ninguna medición");

            MeasuredValue measured = measurements.ForKey(key);

            if (!measured.IsAvailable)
            {
                MetricResult unavailable = MetricResult.Unavailable(key, measured.UnavailableReason);
                // El criterio del perfil se sigue mostrando: informa de qué se habría exigido.
                unavailable.ThresholdCriteria = profile != null ? profile.DescribeCriteria(key) : "—";
                return unavailable;
            }

            double value = measured.Value.Value;

            return new MetricResult
            {
                MetricKey = key,
                MetricName = displayName,
                Unit = unit,
                Value = value,
                Status = profile != null ? profile.Evaluate(key, value) : QASemaphore.NotAvailable,
                ThresholdCriteria = profile != null ? profile.DescribeCriteria(key) : "—",
                MeasurementNote = measured.Note,
                UnavailableReason = profile != null && profile.TryGetLimits(key, out _)
                    ? null
                    : "el perfil activo no define un umbral para esta métrica"
            };
        }

        /// <summary>
        /// Filas de la transformación rígida.
        ///
        /// Las etiquetas de eje siguen la convención de coordenadas de paciente DICOM: la
        /// versión anterior rotulaba Y como cráneo-caudal y Z como anteroposterior, que es
        /// justo al revés.
        /// </summary>
        public static List<RigidTransformItem> BuildRigidTransformRows(QaMeasurements measurements)
        {
            var rows = new List<RigidTransformItem>();

            RigidTransform transform = measurements != null ? measurements.Transform : null;
            EulerAngles? angles = measurements != null ? measurements.RigidEulerAngles : null;

            Vec3? translation = transform != null ? transform.Translation : (Vec3?)null;
            string source = measurements != null ? measurements.TransformSource : null;

            rows.Add(Row("Traslación X (LR)", translation.HasValue ? translation.Value.X : (double?)null, "mm", source));
            rows.Add(Row("Traslación Y (AP)", translation.HasValue ? translation.Value.Y : (double?)null, "mm", source));
            rows.Add(Row("Traslación Z (CC)", translation.HasValue ? translation.Value.Z : (double?)null, "mm", source));

            string angleNote = source;
            if (angles.HasValue && angles.Value.GimbalLock)
            {
                angleNote = "Bloqueo de cardán: cabeceo ≈ ±90°, la guiñada no es separable y se reporta como 0. " +
                            (source ?? string.Empty);
            }

            rows.Add(Row("Rotación cabeceo (X)", angles.HasValue ? angles.Value.PitchX : (double?)null, "°", angleNote));
            rows.Add(Row("Rotación alabeo (Y)", angles.HasValue ? angles.Value.RollY : (double?)null, "°", angleNote));
            rows.Add(Row("Rotación guiñada (Z)", angles.HasValue ? angles.Value.YawZ : (double?)null, "°", angleNote));

            if (translation.HasValue)
            {
                rows.Add(Row("Magnitud de traslación", translation.Value.Length, "mm",
                    "Norma euclídea del vector de traslación."));
            }

            return rows;
        }

        private static RigidTransformItem Row(string parameter, double? value, string unit, string note)
        {
            return new RigidTransformItem
            {
                Parameter = parameter,
                Value = value,
                Unit = unit,
                Note = value.HasValue ? note : "No se pudo obtener la transformación del registro."
            };
        }
    }
}
