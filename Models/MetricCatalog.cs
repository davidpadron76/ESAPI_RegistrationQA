using System;
using System.Collections.Generic;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Stable metric keys. These are the canonical identifiers used by the tolerance
    /// profiles, the advisory engine and the report. They must NEVER be derived from the
    /// text shown on screen: renaming a UI label must not alter evaluation logic.
    /// </summary>
    public static class MetricKeys
    {
        public const string Nmi = "NMI";
        public const string Ncc = "NCC";
        public const string Ssd = "SSD";
        public const string JacobianNegative = "Jacobian_Neg_%";
        public const string MaxDisplacement = "Max_Displacement";
        public const string Smoothness = "Smoothness";
        public const string Dsc = "DSC";
        public const string Hd95 = "HD95";

        // TG-132 Table III primary metrics.
        public const string TreMean = "TRE_Mean";
        public const string TreMax = "TRE_Max";
        public const string InverseConsistency = "Inverse_Consistency";
    }

    /// <summary>
    /// Presentation and semantic metadata for a metric. Centralises the unit and the
    /// direction of goodness, which used to be inferred with substring comparisons against
    /// the display name.
    /// </summary>
    public sealed class MetricDefinition
    {
        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public string Unit { get; private set; }

        /// <summary>true when a higher value is better (NMI, NCC, DSC, Smoothness).</summary>
        public bool HigherIsBetter { get; private set; }

        public string Description { get; private set; }

        public MetricDefinition(string key, string displayName, string unit, bool higherIsBetter, string description)
        {
            Key = key;
            DisplayName = displayName;
            Unit = unit ?? string.Empty;
            HigherIsBetter = higherIsBetter;
            Description = description;
        }
    }

    public static class MetricCatalog
    {
        private static readonly Dictionary<string, MetricDefinition> _byKey =
            new Dictionary<string, MetricDefinition>(StringComparer.Ordinal);

        static MetricCatalog()
        {
            Register(new MetricDefinition(
                MetricKeys.Nmi, "NMI", string.Empty, true,
                "Normalized Mutual Information (Studholme): (H(A)+H(B))/H(A,B), computed over the " +
                "joint histogram of intensity pairs after applying the registration transform. " +
                "Range [1,2]; 1 means statistical independence. This is the reference metric for " +
                "multimodal registrations (CT-MR, CT-PET)."));

            Register(new MetricDefinition(
                MetricKeys.Ncc, "NCC", string.Empty, true,
                "Normalized Cross Correlation (Pearson) between paired intensities. Range [-1,1]. " +
                "This is the reference metric for monomodal registrations (CT-CT, CT-CBCT). " +
                "Negative values indicate inverted contrast."));

            Register(new MetricDefinition(
                MetricKeys.Ssd, "SSD", string.Empty, false,
                "Normalized sum of squared differences: mean((a-b)^2) divided by the square of the " +
                "robust range (P1-P99) of the fixed image. Dimensionless and comparable across " +
                "modalities. Sensitive to contrast differences and to local geometric errors."));

            Register(new MetricDefinition(
                MetricKeys.JacobianNegative, "Jacobian < 0", "%", false,
                "Percentage of deformation field voxels with a non-positive Jacobian determinant, " +
                "i.e. unphysical topological folding. For a rigid transform this is 0 by definition " +
                "(|J| = 1 everywhere)."));

            Register(new MetricDefinition(
                MetricKeys.MaxDisplacement, "Max Displacement", "mm", false,
                "Largest spatial displacement applied to any point within the field of view. For a " +
                "rigid transform it is evaluated exactly over the eight corners of the volume, since " +
                "the displacement magnitude of an affine map is convex and attains its maximum at a " +
                "vertex."));

            Register(new MetricDefinition(
                MetricKeys.Smoothness, "Smoothness", string.Empty, true,
                "Regularity of the deformation vector field. For a rigid transform it is 1.0 by " +
                "definition: the gradient of the field is constant."));

            Register(new MetricDefinition(
                MetricKeys.Dsc, "DSC (Dice Overlap)", string.Empty, true,
                "Dice Similarity Coefficient: 2|A∩B| / (|A|+|B|) between a reference contour and the " +
                "same contour after propagation. Requires a structure set pair matched by structure " +
                "identifier."));

            Register(new MetricDefinition(
                MetricKeys.Hd95, "HD95 (Hausdorff)", "mm", false,
                "95th-percentile Hausdorff distance between the reference and propagated surfaces. " +
                "Excludes the 5% most deviant points. Requires the same matched pair as the DSC. " +
                "Note that TG-132 Table III specifies Mean Distance to Agreement (MDA) rather than " +
                "HD95; HD95 is common in the segmentation literature but is not the report's metric."));

            Register(new MetricDefinition(
                MetricKeys.TreMean, "TRE (mean)", "mm", false,
                "Target Registration Error, averaged over matched landmarks. Each landmark is taken " +
                "from the source image, mapped through the registration, and compared with the same " +
                "landmark on the registered image. This is the primary accuracy metric of TG-132 " +
                "Table III and the only one here expressed directly in millimetres of spatial error. " +
                "Tolerance in Table III is the maximum voxel dimension (~2-3 mm)."));

            Register(new MetricDefinition(
                MetricKeys.TreMax, "TRE (max)", "mm", false,
                "Largest single-landmark Target Registration Error. The mean can conceal one badly " +
                "mismatched landmark, which is usually the clinically relevant case, so both are " +
                "reported."));

            Register(new MetricDefinition(
                MetricKeys.InverseConsistency, "Inverse consistency", "mm", false,
                "Residual displacement after mapping a point forward through the registration and " +
                "back through the reverse registration. TG-132 §4.C.4: a registration should be " +
                "consistent in magnitude and opposite in direction, so the composition should return " +
                "each point to itself. It does not verify accuracy directly — a registration can be " +
                "consistently wrong — but it evidences a stable, well-behaved algorithm. Tolerance in " +
                "Table III is the maximum voxel dimension (~2-3 mm)."));
        }

        private static void Register(MetricDefinition definition)
        {
            _byKey[definition.Key] = definition;
        }

        public static bool TryGet(string key, out MetricDefinition definition)
        {
            if (key == null)
            {
                definition = null;
                return false;
            }
            return _byKey.TryGetValue(key, out definition);
        }

        public static MetricDefinition Get(string key)
        {
            MetricDefinition definition;
            if (TryGet(key, out definition)) return definition;
            throw new ArgumentException("Unknown metric: " + key, "key");
        }

        public static string DisplayName(string key)
        {
            MetricDefinition definition;
            return TryGet(key, out definition) ? definition.DisplayName : key;
        }

        public static string Unit(string key)
        {
            MetricDefinition definition;
            return TryGet(key, out definition) ? definition.Unit : string.Empty;
        }

        public static bool HigherIsBetter(string key)
        {
            MetricDefinition definition;
            return TryGet(key, out definition) && definition.HigherIsBetter;
        }
    }
}
