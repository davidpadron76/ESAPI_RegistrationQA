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
        public const string Mda = "MDA";
        public const string Hd95 = "HD95";
        public const string TreMean = "TRE_Mean";
        public const string TreMax = "TRE_Max";
        public const string InverseConsistency = "Inverse_Consistency";
    }

    /// <summary>
    /// Definition of a metric: what it is, and why it is in this tool.
    ///
    /// The three rationale fields are mandatory and the constructor rejects a definition
    /// that leaves any of them blank. That is deliberate. A metric earns its place by the
    /// clinical decision it supports, not by appearing in a table in a task group report,
    /// and the reverse is equally true: a metric named in TG-132 is not automatically worth
    /// computing if nobody acts on it. Forcing the answer to live here, next to the code,
    /// stops that reasoning from drifting into a README that can be rewritten without
    /// anyone noticing.
    ///
    /// When adding or removing a metric, fill these in first. If they cannot be filled in
    /// honestly, that is the answer.
    /// </summary>
    public sealed class MetricDefinition
    {
        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public string Unit { get; private set; }

        /// <summary>true when a higher value is better.</summary>
        public bool HigherIsBetter { get; private set; }

        /// <summary>What the metric computes and from which data.</summary>
        public string Description { get; private set; }

        /// <summary>The question a physicist is asking when they look at this number.</summary>
        public string ClinicalQuestion { get; private set; }

        /// <summary>What can actually be decided or acted upon from this value.</summary>
        public string DecisionSupported { get; private set; }

        /// <summary>
        /// Where the metric stands relative to AAPM TG-132, stated either way. Metrics the
        /// report does not name are marked as such together with the reason for keeping
        /// them.
        /// </summary>
        public string StandardBasis { get; private set; }

        public MetricDefinition(
            string key,
            string displayName,
            string unit,
            bool higherIsBetter,
            string description,
            string clinicalQuestion,
            string decisionSupported,
            string standardBasis)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", "key");
            if (string.IsNullOrWhiteSpace(clinicalQuestion))
                throw new ArgumentException("A metric must state the clinical question it answers.", "clinicalQuestion");
            if (string.IsNullOrWhiteSpace(decisionSupported))
                throw new ArgumentException("A metric must state what decision it supports.", "decisionSupported");
            if (string.IsNullOrWhiteSpace(standardBasis))
                throw new ArgumentException("A metric must state its position relative to TG-132.", "standardBasis");

            Key = key;
            DisplayName = displayName;
            Unit = unit ?? string.Empty;
            HigherIsBetter = higherIsBetter;
            Description = description;
            ClinicalQuestion = clinicalQuestion;
            DecisionSupported = decisionSupported;
            StandardBasis = standardBasis;
        }
    }

    public static class MetricCatalog
    {
        private static readonly Dictionary<string, MetricDefinition> _byKey =
            new Dictionary<string, MetricDefinition>(StringComparer.Ordinal);

        private static readonly List<MetricDefinition> _all = new List<MetricDefinition>();

        static MetricCatalog()
        {
            // ---------------------------------------------------------- intensity similarity

            Register(new MetricDefinition(
                key: MetricKeys.Ncc,
                displayName: "NCC",
                unit: "",
                higherIsBetter: true,
                description:
                    "Normalized Cross Correlation (Pearson) between paired intensities, computed over " +
                    "voxel pairs matched by applying the registration transform. Range [-1,1]; negative " +
                    "values indicate inverted contrast.",
                clinicalQuestion:
                    "Does the anatomy line up, when both images come from the same modality?",
                decisionSupported:
                    "Catches gross misalignment without any calibration: a badly displaced registration " +
                    "collapses the correlation. Against a local baseline distribution it also flags the " +
                    "case that sits well below what this department normally achieves, which is " +
                    "actionable even before absolute limits are agreed.",
                standardBasis:
                    "Not in TG-132 Table III. Section 4.C.3 admits CC for assessment provided the metric " +
                    "was not the one the registration algorithm optimised, and warns it does not convert " +
                    "into a measure of spatial accuracy."));

            Register(new MetricDefinition(
                key: MetricKeys.Nmi,
                displayName: "NMI",
                unit: "",
                higherIsBetter: true,
                description:
                    "Normalized Mutual Information (Studholme): (H(A)+H(B))/H(A,B) over the joint " +
                    "histogram of the matched intensity pairs. Range [1,2]; 1 means statistical " +
                    "independence.",
                clinicalQuestion:
                    "Does the anatomy line up when the two images are not linearly comparable in " +
                    "intensity, as in CT-MR or CT-PET?",
                decisionSupported:
                    "The usable intensity metric for multimodal pairs, where the linearity assumption " +
                    "behind NCC does not hold. Without it there is no intensity-based check at all on a " +
                    "CT-MR fusion.",
                standardBasis:
                    "Not in TG-132 Table III. Admitted by section 4.C.3 under the same two conditions " +
                    "as CC and SSD."));

            Register(new MetricDefinition(
                key: MetricKeys.Ssd,
                displayName: "SSD",
                unit: "",
                higherIsBetter: false,
                description:
                    "Normalized sum of squared differences: mean((a-b)^2) divided by the square of the " +
                    "robust range (P1-P99) of the fixed image. Dimensionless and comparable across " +
                    "modalities.",
                clinicalQuestion:
                    "Beyond overall alignment, are there local intensity differences between the two " +
                    "series?",
                decisionSupported:
                    "Sensitive to things a correlation coefficient smooths over: field-of-view " +
                    "truncation, metal artefact, a contrast phase mismatch, or a local geometric error " +
                    "confined to part of the volume. Useful as the reason to go and look at the overlay.",
                standardBasis:
                    "Not in TG-132 Table III. Admitted by section 4.C.3 under the same conditions."));

            // ---------------------------------------------------------- deformation / topology

            Register(new MetricDefinition(
                key: MetricKeys.JacobianNegative,
                displayName: "Jacobian < 0",
                unit: "%",
                higherIsBetter: false,
                description:
                    "Percentage of deformation field voxels with a non-positive Jacobian determinant, " +
                    "that is, unphysical topological folding. For a rigid transform this is 0 by " +
                    "definition, since |J| = 1 everywhere.",
                clinicalQuestion:
                    "Has the deformation folded tissue onto itself?",
                decisionSupported:
                    "A single negative value disqualifies the registration for dose accumulation and " +
                    "for direct contour propagation. This is the clearest go/no-go in the whole set.",
                standardBasis:
                    "TG-132 Table III. Tolerance: no negative values, with deviations from 1 as " +
                    "clinically expected."));

            Register(new MetricDefinition(
                key: MetricKeys.MaxDisplacement,
                displayName: "Max Displacement",
                unit: "mm",
                higherIsBetter: false,
                description:
                    "Largest spatial displacement applied to any point within the field of view. For a " +
                    "rigid transform it is evaluated exactly over the eight corners, since the " +
                    "displacement magnitude of an affine map is convex and attains its maximum at a " +
                    "vertex.",
                clinicalQuestion:
                    "How far has this registration actually moved the anatomy?",
                decisionSupported:
                    "A plausibility check. A displacement far larger than the anatomical change the " +
                    "case can justify usually means the wrong image pair was registered, or the " +
                    "registration failed and landed in a local minimum. Cheap to read, and it catches " +
                    "an error class the similarity metrics can miss when both images are largely air.",
                standardBasis:
                    "Not in TG-132 Table III. Included as a magnitude sanity check; the report discusses " +
                    "expected volume change in section 4.C.3 but does not tabulate a displacement " +
                    "tolerance."));

            Register(new MetricDefinition(
                key: MetricKeys.Smoothness,
                displayName: "Smoothness",
                unit: "",
                higherIsBetter: true,
                description:
                    "Regularity of the deformation vector field. For a rigid transform it is 1.0 by " +
                    "definition: the gradient of the field is constant.",
                clinicalQuestion:
                    "Is the deformation physically plausible, or does it vary abruptly from voxel to " +
                    "voxel?",
                decisionSupported:
                    "An irregular field points to the algorithm overfitting in low-contrast regions, " +
                    "where there is no anatomical information to drive the registration. Those are the " +
                    "regions where a propagated contour is least trustworthy.",
                standardBasis:
                    "Not in TG-132 Table III. Related to the report's observation in section 4.C.3 that " +
                    "large local changes in the Jacobian determinant can indicate a registration error."));

            // ---------------------------------------------------------- structures

            Register(new MetricDefinition(
                key: MetricKeys.Dsc,
                displayName: "DSC (Dice Overlap)",
                unit: "",
                higherIsBetter: true,
                description:
                    "Dice Similarity Coefficient: 2|A∩B| / (|A|+|B|) between a reference contour and " +
                    "the same contour after propagation. Requires a structure set pair matched by " +
                    "structure identifier.",
                clinicalQuestion:
                    "Do the same organs end up in the same place after registration?",
                decisionSupported:
                    "The anatomical check most physicists trust before propagating contours, because it " +
                    "is expressed in terms of the structures they will actually use rather than in " +
                    "image intensities.",
                standardBasis:
                    "TG-132 Table III. Tolerance: within the contouring uncertainty of the structure, " +
                    "approximately 0.80-0.90, and volume dependent."));

            Register(new MetricDefinition(
                key: MetricKeys.Mda,
                displayName: "MDA (mean surface distance)",
                unit: "mm",
                higherIsBetter: false,
                description:
                    "Mean Distance to Agreement: the average distance between the reference and " +
                    "propagated contour surfaces, taken symmetrically in both directions.",
                clinicalQuestion:
                    "On average, how far apart are the two versions of the same organ surface?",
                decisionSupported:
                    "Expressed in millimetres, so it can be set against a PTV margin or the contouring " +
                    "uncertainty of the structure in a way the DSC cannot: a Dice value depends on the " +
                    "volume of the organ, and 0.85 means something very different for a parotid and for " +
                    "a whole lung. MDA is comparable across structures of different sizes.",
                standardBasis:
                    "TG-132 Table III. Tolerance: within the contouring uncertainty of the structure, " +
                    "or the maximum voxel dimension, roughly 2-3 mm."));

            Register(new MetricDefinition(
                key: MetricKeys.Hd95,
                displayName: "HD95 (Hausdorff)",
                unit: "mm",
                higherIsBetter: false,
                description:
                    "95th-percentile Hausdorff distance between the reference and propagated surfaces, " +
                    "excluding the 5% most deviant points. Computed from the same distance transform as " +
                    "the MDA; the two differ only in taking the 95th percentile instead of the mean.",
                clinicalQuestion:
                    "How far apart are the contour surfaces where they disagree most?",
                decisionSupported:
                    "Complements the MDA and the DSC, both of which average over the whole surface and " +
                    "can look acceptable while one boundary sits several millimetres off. For an organ " +
                    "at risk adjacent to the target, that worst boundary is the part that matters. " +
                    "Reading MDA and HD95 together separates a uniform offset from a local failure.",
                standardBasis:
                    "Not in TG-132 Table III, which specifies MDA. Retained alongside it because HD95 " +
                    "is what the segmentation literature reports, so local results stay comparable with " +
                    "published series."));

            // ---------------------------------------------------------- spatial accuracy

            Register(new MetricDefinition(
                key: MetricKeys.TreMean,
                displayName: "TRE (mean)",
                unit: "mm",
                higherIsBetter: false,
                description:
                    "Target Registration Error averaged over matched landmarks. Each landmark is taken " +
                    "from the source image, mapped through the registration, and compared with the same " +
                    "landmark on the registered image.",
                clinicalQuestion:
                    "How large is the spatial error, in millimetres?",
                decisionSupported:
                    "The only metric here that converts directly into a distance, which makes it the " +
                    "only one that can be set against a PTV margin or a couch shift tolerance. Everything " +
                    "else says whether the registration looks right; this one says by how much it is " +
                    "wrong.",
                standardBasis:
                    "TG-132 Table III, the report's primary accuracy metric. Tolerance: the maximum " +
                    "voxel dimension, roughly 2-3 mm."));

            Register(new MetricDefinition(
                key: MetricKeys.TreMax,
                displayName: "TRE (max)",
                unit: "mm",
                higherIsBetter: false,
                description:
                    "Largest single-landmark Target Registration Error.",
                clinicalQuestion:
                    "Is any individual landmark badly placed, even if the average looks fine?",
                decisionSupported:
                    "The mean dilutes one bad landmark across the set. For a boost volume or a small " +
                    "target, the single worst point is the one that determines whether the registration " +
                    "can be used. Reading mean and max together also separates a global offset from a " +
                    "local failure.",
                standardBasis:
                    "TG-132 Table III defines TRE without prescribing how to summarise it across " +
                    "landmarks. Reporting mean and maximum separately is a practical extension."));

            Register(new MetricDefinition(
                key: MetricKeys.InverseConsistency,
                displayName: "Inverse consistency",
                unit: "mm",
                higherIsBetter: false,
                description:
                    "Residual displacement after mapping a point forward through the registration and " +
                    "back through the reverse registration, evaluated over a grid spanning the field of " +
                    "view.",
                clinicalQuestion:
                    "Is the registration algorithm behaving stably on this pair of images?",
                decisionSupported:
                    "It does not establish accuracy on its own: a registration can be consistently " +
                    "wrong. What it does is invalidate the other metrics when the residual is large, " +
                    "because an algorithm that does not return a point to itself is not producing a " +
                    "result worth interpreting.",
                standardBasis:
                    "TG-132 section 4.C.4 and Table III. Tolerance: the maximum voxel dimension. The " +
                    "report is explicit that consistency provides evidence of a stable system rather " +
                    "than direct verification."));
        }

        private static void Register(MetricDefinition definition)
        {
            _byKey[definition.Key] = definition;
            _all.Add(definition);
        }

        /// <summary>Every registered metric, in the order they are defined.</summary>
        public static IEnumerable<MetricDefinition> All
        {
            get { return _all.AsReadOnly(); }
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
