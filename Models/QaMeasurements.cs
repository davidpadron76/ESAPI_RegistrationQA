using System;
using System.Collections.Generic;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Why a metric has no value.
    ///
    /// The distinction drives what the user sees. A metric that could not apply to this case
    /// is noise on screen: it would say the same thing on every rigid registration for ever.
    /// A metric that was attempted and failed is the opposite — it points at something to
    /// fix, and hiding it would bury the fault.
    /// </summary>
    public enum MeasurementState
    {
        /// <summary>A value was obtained.</summary>
        Measured,

        /// <summary>Attempted and failed. Shown as N/A with the reason.</summary>
        Unavailable,

        /// <summary>
        /// Cannot apply to this case at all — a deformation metric on a rigid registration,
        /// a landmark metric with no landmarks. Hidden from the tables, recorded in the
        /// diagnostics so the omission stays traceable.
        /// </summary>
        NotApplicable
    }

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
        public MeasurementState State { get; private set; }

        /// <summary>Why there is no value. Null when there is one.</summary>
        public string UnavailableReason { get; private set; }

        /// <summary>How the value was obtained, when it does exist.</summary>
        public string Note { get; private set; }

        private MeasuredValue(double? value, MeasurementState state, string reason, string note)
        {
            Value = value;
            State = state;
            UnavailableReason = reason;
            Note = note;
        }

        public bool IsAvailable { get { return State == MeasurementState.Measured; } }

        /// <summary>true when the metric should not be shown at all for this case.</summary>
        public bool IsNotApplicable { get { return State == MeasurementState.NotApplicable; } }

        public static MeasuredValue Measured(double value, string note = null)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return Unavailable("the computation produced a non-finite value");

            return new MeasuredValue(value, MeasurementState.Measured, null, note);
        }

        /// <summary>The metric was attempted and failed. It stays visible as N/A.</summary>
        public static MeasuredValue Unavailable(string reason)
        {
            return new MeasuredValue(null, MeasurementState.Unavailable,
                reason ?? "reason not specified", null);
        }

        /// <summary>
        /// The metric does not apply to this case. It is hidden from the tables and recorded
        /// in the diagnostics instead.
        ///
        /// The reason should say what would be needed to obtain it, where that is within the
        /// user's control — placing markers, creating the reverse registration. For a
        /// genuine limitation such as the deformation field not being exposed, it should say
        /// so plainly.
        /// </summary>
        public static MeasuredValue NotApplicable(string reason)
        {
            return new MeasuredValue(null, MeasurementState.NotApplicable,
                reason ?? "does not apply to this case", null);
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
        public MeasuredValue Mda { get; set; }
        public MeasuredValue Hd95 { get; set; }

        /// <summary>Number of contour structures matched by identifier between the two series.</summary>
        public int StructurePairCount { get; set; }

        /// <summary>Identifier of the structure that produced the worst surface agreement.</summary>
        public string WorstStructureId { get; set; }

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
            Mda = MeasuredValue.Unavailable(notMeasured);
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
                case MetricKeys.Mda: return Mda;
                case MetricKeys.Hd95: return Hd95;
                case MetricKeys.TreMean: return TreMean;
                case MetricKeys.TreMax: return TreMax;
                case MetricKeys.InverseConsistency: return InverseConsistency;
                default: return MeasuredValue.Unavailable("unknown metric: " + metricKey);
            }
        }
    }
}
