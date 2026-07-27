using System;
using System.Collections.Generic;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// A measured metric value, or its justified absence.
    ///
    /// It is deliberately impossible to construct a value without deciding whether it
    /// exists: there is no public constructor taking a "just in case" double. This is what
    /// stops filler numbers from creeping back into a signed report.
    /// </summary>
    public sealed class MeasuredValue
    {
        public double? Value { get; private set; }
        public string UnavailableReason { get; private set; }

        /// <summary>How the value was obtained, when it does exist.</summary>
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
                return Unavailable("the computation produced a non-finite value");

            return new MeasuredValue(value, null, note);
        }

        public static MeasuredValue Unavailable(string reason)
        {
            return new MeasuredValue(null, reason ?? "reason not specified", null);
        }
    }

    /// <summary>
    /// The complete result of one measurement pass over a registration. It is independent
    /// of the active tolerance profile: it holds only what was measured, never what was
    /// evaluated.
    ///
    /// Switching anatomical profile re-evaluates thresholds against this same object and
    /// does not touch the image or the structures again.
    /// </summary>
    public sealed class QaMeasurements
    {
        public string RegistrationId { get; set; }
        public RegistrationType RegType { get; set; }
        public bool IsDeformable { get; set; }

        public ImageModality FixedModality { get; set; }
        public ImageModality MovingModality { get; set; }

        // Intensity similarity
        public MeasuredValue Nmi { get; set; }
        public MeasuredValue Ncc { get; set; }
        public MeasuredValue Ssd { get; set; }

        // Deformation / topology
        public MeasuredValue JacobianNegativePercent { get; set; }
        public MeasuredValue MaxDisplacement { get; set; }
        public MeasuredValue Smoothness { get; set; }

        // Structures
        public MeasuredValue Dsc { get; set; }
        public MeasuredValue Hd95 { get; set; }

        // TG-132 Table III primary metrics
        public MeasuredValue TreMean { get; set; }
        public MeasuredValue TreMax { get; set; }
        public MeasuredValue InverseConsistency { get; set; }

        /// <summary>Number of landmarks matched between the two image sets.</summary>
        public int TreLandmarkCount { get; set; }

        /// <summary>
        /// Largest native voxel dimension across both images, in mm. TG-132 expresses the
        /// tolerance for TRE, MDA and consistency as "maximum voxel dimension", so this is
        /// the figure the reader needs in order to judge those metrics against the report.
        /// It is the native size, not the subsampled one used for intensity metrics.
        /// </summary>
        public double? NativeVoxelSizeMm { get; set; }

        // Rigid transform
        public RigidTransform Transform { get; set; }
        public string TransformSource { get; set; }
        public EulerAngles? RigidEulerAngles { get; set; }

        // Sampling traceability
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

            const string notMeasured = "not measured";
            Nmi = MeasuredValue.Unavailable(notMeasured);
            Ncc = MeasuredValue.Unavailable(notMeasured);
            Ssd = MeasuredValue.Unavailable(notMeasured);
            JacobianNegativePercent = MeasuredValue.Unavailable(notMeasured);
            MaxDisplacement = MeasuredValue.Unavailable(notMeasured);
            Smoothness = MeasuredValue.Unavailable(notMeasured);
            Dsc = MeasuredValue.Unavailable(notMeasured);
            Hd95 = MeasuredValue.Unavailable(notMeasured);
            TreMean = MeasuredValue.Unavailable(notMeasured);
            TreMax = MeasuredValue.Unavailable(notMeasured);
            InverseConsistency = MeasuredValue.Unavailable(notMeasured);
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
                case MetricKeys.TreMean: return TreMean;
                case MetricKeys.TreMax: return TreMax;
                case MetricKeys.InverseConsistency: return InverseConsistency;
                default: return MeasuredValue.Unavailable("unknown metric: " + metricKey);
            }
        }
    }
}
