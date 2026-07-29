using System;
using System.Collections.Generic;
using System.Globalization;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Converts raw measurements into results evaluated against a tolerance profile.
    ///
    /// This is a pure function of (measurements, profile) → results: it does not touch the
    /// API, read voxels or walk structures. That is exactly the separation that was missing:
    /// previously, changing the anatomical profile in the dropdown re-ran the entire
    /// measurement pipeline to obtain the same numbers and classify them differently.
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

        /// <summary>
        /// The quantitative metrics of TG-132 Table III, grouped together because that is how
        /// the report presents them and because they are the ones expressed in millimetres of
        /// spatial error. DSC belongs to the same table.
        /// </summary>
        public static readonly string[] SpatialAccuracyKeys =
        {
            MetricKeys.TreMean, MetricKeys.TreMax, MetricKeys.InverseConsistency
        };

        public static readonly string[] StructureKeys =
        {
            MetricKeys.Dsc, MetricKeys.Mda, MetricKeys.Hd95
        };

        /// <summary>
        /// Evaluates a group of metrics, omitting those that do not apply to this case.
        ///
        /// A metric that could never be filled here — a deformation metric on a rigid
        /// registration, a landmark metric with no landmarks — would show the same N/A on
        /// every case for ever. It is dropped from the table and recorded in the diagnostics
        /// instead, so the omission stays traceable without occupying a row.
        ///
        /// A metric that was attempted and failed is kept. That one points at something to
        /// fix, and hiding it would bury the fault.
        /// </summary>
        public static List<MetricResult> Evaluate(
            QaMeasurements measurements, ThresholdProfile profile, IEnumerable<string> keys,
            string group = null)
        {
            var results = new List<MetricResult>();
            if (keys == null) return results;

            foreach (string key in keys)
            {
                if (measurements != null && measurements.ForKey(key).IsNotApplicable) continue;

                MetricResult result = EvaluateOne(measurements, profile, key);
                result.Group = group;
                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// The metrics that did not apply to this case, with the reason. Feeds the
        /// diagnostics view and the report, which is where the omissions are accounted for.
        /// </summary>
        public static List<MetricResult> NotApplicable(QaMeasurements measurements)
        {
            var results = new List<MetricResult>();
            if (measurements == null) return results;

            foreach (MetricDefinition definition in MetricCatalog.All)
            {
                MeasuredValue measured = measurements.ForKey(definition.Key);
                if (!measured.IsNotApplicable) continue;

                results.Add(MetricResult.Unavailable(definition.Key, measured.UnavailableReason));
            }

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
                return MetricResult.Unavailable(key, "no measurement was performed");

            MeasuredValue measured = measurements.ForKey(key);

            if (!measured.IsAvailable)
            {
                MetricResult unavailable = MetricResult.Unavailable(key, measured.UnavailableReason);
                // The profile criterion is still shown: it tells the reader what would have
                // been required.
                unavailable.ThresholdCriteria = profile != null ? profile.DescribeCriteria(key) : "—";
                return unavailable;
            }

            double value = measured.Value.Value;

            string ungatedReason = WhyNotGated(key, measurements);

            if (ungatedReason != null)
            {
                // Measured, shown, exported — but with no classification, so it cannot move
                // the verdict in either direction. The reason goes in the criterion column,
                // where the reader is looking for the tolerance it would otherwise have been
                // held to.
                return new MetricResult
                {
                    MetricKey = key,
                    MetricName = displayName,
                    Unit = unit,
                    Value = value,
                    Status = QASemaphore.Informational,
                    ThresholdCriteria = ungatedReason,
                    MeasurementNote = measured.Note,
                    // Smoothness arrives here and is also a tautology on a rigid transform.
                    // Without this the verdict would count it as a metric that was measured.
                    IsAnalytic = measured.IsAnalytic
                };
            }

            return new MetricResult
            {
                MetricKey = key,
                MetricName = displayName,
                Unit = unit,
                Value = value,
                Status = profile != null ? profile.Evaluate(key, value) : QASemaphore.NotAvailable,
                ThresholdCriteria = profile != null ? profile.DescribeCriteria(key) : "—",
                MeasurementNote = measured.Note,
                IsAnalytic = measured.IsAnalytic,
                UnavailableReason = profile != null && profile.TryGetLimits(key, out _)
                    ? null
                    : "the active profile defines no threshold for this metric"
            };
        }

        /// <summary>
        /// Why a measured metric is not classified, or null when it is.
        ///
        /// Two reasons, and they are different in kind. The first is permanent and belongs to
        /// the metric: NCC, NMI and SSD have no tolerance in TG-132 and no route from their
        /// value to a distance, so any limit would be invented. The second is a property of
        /// this particular image pair: the maximum displacement is only the correction the
        /// registration applies when both series share a frame of reference. Across two frames
        /// the transform must also span the offset between the coordinate systems, and a
        /// correct registration can exceed any profile limit with nothing wrong.
        /// </summary>
        private static string WhyNotGated(string key, QaMeasurements measurements)
        {
            // Kept short: this text lands in a narrow column. The full reasoning is in the
            // metric tooltip and in the report appendix, both fed from MetricCatalog.
            if (!MetricCatalog.IsGating(key))
            {
                // Maximum displacement keeps the more specific wording where it applies: that
                // the number spans two coordinate systems is the thing worth saying about it.
                if (key == MetricKeys.MaxDisplacement)
                {
                    bool? shares = measurements != null ? measurements.SharesFrameOfReference : null;

                    if (shares.HasValue && !shares.Value) return "not in TG-132; different frames of reference";
                    if (!shares.HasValue) return "not in TG-132; frame of reference unknown";
                }

                return "no TG-132 tolerance";
            }

            // A tolerance the report ties to the image cannot be applied without the image.
            if (ThresholdProfile.IsVoxelDerived(key) &&
                (measurements == null || !measurements.NativeVoxelSizeMm.HasValue))
            {
                return "not graded — voxel size unknown";
            }

            return null;
        }

        /// <summary>
        /// Rows for the rigid transform table.
        ///
        /// The axis labels follow the DICOM patient coordinate convention: the previous
        /// version labelled Y as cranio-caudal and Z as anterior-posterior, which is the
        /// other way round.
        /// </summary>
        public static List<RigidTransformItem> BuildRigidTransformRows(QaMeasurements measurements)
        {
            var rows = new List<RigidTransformItem>();

            RigidTransform transform = measurements != null ? measurements.Transform : null;
            EulerAngles? angles = measurements != null ? measurements.RigidEulerAngles : null;

            Vec3? translation = transform != null ? transform.Translation : (Vec3?)null;
            string source = measurements != null ? measurements.TransformSource : null;

            rows.Add(Row("Translation X (LR)", translation.HasValue ? translation.Value.X : (double?)null, "mm", source));
            rows.Add(Row("Translation Y (AP)", translation.HasValue ? translation.Value.Y : (double?)null, "mm", source));
            rows.Add(Row("Translation Z (CC)", translation.HasValue ? translation.Value.Z : (double?)null, "mm", source));

            string angleNote = source;
            if (angles.HasValue && angles.Value.GimbalLock)
            {
                angleNote = "Gimbal lock: pitch ≈ ±90°, yaw is not separable and is reported as 0. " +
                            (source ?? string.Empty);
            }

            rows.Add(Row("Rotation pitch (X)", angles.HasValue ? angles.Value.PitchX : (double?)null, "°", angleNote));
            rows.Add(Row("Rotation roll (Y)", angles.HasValue ? angles.Value.RollY : (double?)null, "°", angleNote));
            rows.Add(Row("Rotation yaw (Z)", angles.HasValue ? angles.Value.YawZ : (double?)null, "°", angleNote));

            if (translation.HasValue)
            {
                rows.Add(Row("Translation magnitude", translation.Value.Length, "mm",
                    "Euclidean norm of the translation vector."));
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
                Note = value.HasValue ? note : "The registration transform could not be obtained."
            };
        }
    }
}
