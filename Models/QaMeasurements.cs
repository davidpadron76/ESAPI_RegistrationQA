using System;
using System.Collections.Generic;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Valor medido de una métrica, o la ausencia justificada del mismo.
    ///
    /// Es deliberadamente imposible construir un valor sin decidir si existe o no: no hay
    /// constructor público que acepte un double "por si acaso". Esto es lo que evita que
    /// vuelvan a colarse números de relleno en un reporte firmado.
    /// </summary>
    public sealed class MeasuredValue
    {
        public double? Value { get; private set; }
        public string UnavailableReason { get; private set; }

        /// <summary>Aclaración sobre la procedencia del valor cuando sí existe.</summary>
        public string Note { get; private set; }

        private MeasuredValue(double? value, string unavailableReason, string note)
        {
            Value = value;
            UnavailableReason = unavailableReason;
            Note = note;
        }

        public bool IsAvailable { get { return Value.HasValue; } }

        public static MeasuredValue Measured(double value, string note = null)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return Unavailable("el cálculo produjo un valor no finito");

            return new MeasuredValue(value, null, note);
        }

        public static MeasuredValue Unavailable(string reason)
        {
            return new MeasuredValue(null, reason ?? "motivo no especificado", null);
        }
    }

    /// <summary>
    /// Resultado completo de una pasada de medición sobre un registro. Es independiente del
    /// perfil de tolerancia activo: contiene sólo lo medido, nunca lo evaluado.
    ///
    /// Cambiar de perfil anatómico reevalúa umbrales sobre este mismo objeto y no vuelve a
    /// tocar la imagen ni las estructuras.
    /// </summary>
    public sealed class QaMeasurements
    {
        public string RegistrationId { get; set; }
        public RegistrationType RegType { get; set; }
        public bool IsDeformable { get; set; }

        public ImageModality FixedModality { get; set; }
        public ImageModality MovingModality { get; set; }

        // Similitud de intensidad
        public MeasuredValue Nmi { get; set; }
        public MeasuredValue Ncc { get; set; }
        public MeasuredValue Ssd { get; set; }

        // Deformación / topología
        public MeasuredValue JacobianNegativePercent { get; set; }
        public MeasuredValue MaxDisplacement { get; set; }
        public MeasuredValue Smoothness { get; set; }

        // Estructuras
        public MeasuredValue Dsc { get; set; }
        public MeasuredValue Hd95 { get; set; }

        // Transformación rígida
        public RigidTransform Transform { get; set; }
        public string TransformSource { get; set; }
        public EulerAngles? RigidEulerAngles { get; set; }

        // Trazabilidad del muestreo
        public long SampleCount { get; set; }
        public double? OverlapFraction { get; set; }
        public double? EffectiveSamplingMm { get; set; }

        public List<string> Diagnostics { get; private set; }

        public QaMeasurements()
        {
            Diagnostics = new List<string>();
            RegType = RegistrationType.Unknown;
            FixedModality = ImageModality.Unknown;
            MovingModality = ImageModality.Unknown;

            const string notMeasured = "no medido";
            Nmi = MeasuredValue.Unavailable(notMeasured);
            Ncc = MeasuredValue.Unavailable(notMeasured);
            Ssd = MeasuredValue.Unavailable(notMeasured);
            JacobianNegativePercent = MeasuredValue.Unavailable(notMeasured);
            MaxDisplacement = MeasuredValue.Unavailable(notMeasured);
            Smoothness = MeasuredValue.Unavailable(notMeasured);
            Dsc = MeasuredValue.Unavailable(notMeasured);
            Hd95 = MeasuredValue.Unavailable(notMeasured);
        }

        public MeasuredValue ForKey(string metricKey)
        {
            switch (metricKey)
            {
                case MetricKeys.Nmi: return Nmi;
                case MetricKeys.Ncc: return Ncc;
                case MetricKeys.Ssd: return Ssd;
                case MetricKeys.JacobianNegative: return JacobianNegativePercent;
                case MetricKeys.MaxDisplacement: return MaxDisplacement;
                case MetricKeys.Smoothness: return Smoothness;
                case MetricKeys.Dsc: return Dsc;
                case MetricKeys.Hd95: return Hd95;
                default: return MeasuredValue.Unavailable("métrica desconocida: " + metricKey);
            }
        }
    }
}
