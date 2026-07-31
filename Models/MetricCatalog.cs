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

        /// <summary>
        /// Deliberately not folded into <see cref="Smoothness"/>. That metric is on a
        /// higher-is-better scale where 1.0 means a perfectly regular field; this one is a
        /// gradient magnitude where 0 means the same thing. Putting both in one column would
        /// print two incompatible scales under one heading.
        /// </summary>
        public const string DvfGradientMax = "DVF_Gradient_Max";

        /// <summary>
        /// The second clause of the Table III Jacobian row, which
        /// <see cref="JacobianNegative"/> does not cover. That one implements "no negative
        /// values"; this one measures the departure from 1 the same sentence goes on to
        /// constrain.
        /// </summary>
        public const string JacobianDeparture = "Jacobian_Departure";
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

        /// <summary>
        /// true when a value outside the profile limits may drive the overall verdict.
        ///
        /// Measuring a quantity and being entitled to fail a registration on it are different
        /// claims, and the tool used to treat them as one. Whether a metric can gate is a
        /// property of the metric and of the evidence behind its tolerance, not of the
        /// anatomical profile, which is why it lives here alongside <see cref="HigherIsBetter"/>
        /// rather than in <c>ThresholdProfile</c>: there it could differ between profiles for
        /// the same metric.
        /// </summary>
        public bool Gating { get; private set; }

        /// <summary>Why the metric does or does not gate. Mandatory, like the other rationale fields.</summary>
        public string GatingBasis { get; private set; }

        public MetricDefinition(
            string key,
            string displayName,
            string unit,
            bool higherIsBetter,
            string description,
            string clinicalQuestion,
            string decisionSupported,
            string standardBasis,
            bool gating,
            string gatingBasis)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", "key");
            if (string.IsNullOrWhiteSpace(clinicalQuestion))
                throw new ArgumentException("A metric must state the clinical question it answers.", "clinicalQuestion");
            if (string.IsNullOrWhiteSpace(decisionSupported))
                throw new ArgumentException("A metric must state what decision it supports.", "decisionSupported");
            if (string.IsNullOrWhiteSpace(standardBasis))
                throw new ArgumentException("A metric must state its position relative to TG-132.", "standardBasis");
            if (string.IsNullOrWhiteSpace(gatingBasis))
                throw new ArgumentException("A metric must state why it does or does not drive the verdict.", "gatingBasis");

            Key = key;
            DisplayName = displayName;
            Unit = unit ?? string.Empty;
            HigherIsBetter = higherIsBetter;
            Description = description;
            ClinicalQuestion = clinicalQuestion;
            DecisionSupported = decisionSupported;
            StandardBasis = standardBasis;
            Gating = gating;
            GatingBasis = gatingBasis;
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
                    "into a measure of spatial accuracy.",
                gating: false,
                gatingBasis:
                    "TG-132 defines no tolerance for CC anywhere, and states that the metric does not " +
                    "convert into a measure of spatial accuracy. Any numeric limit here would be one this " +
                    "project invented, so a red badge would assert a geometric failure the metric cannot " +
                    "establish. The value is reported and exported; interpret it against your own baseline " +
                    "distribution (VALIDATION.md section 5)."));

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
                    "as CC and SSD.",
                gating: false,
                gatingBasis:
                    "Same as CC: no tolerance in TG-132 and no route from the value to a distance in " +
                    "millimetres. Reported for information and for the local baseline."));

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
                    "Not in TG-132 Table III. Admitted by section 4.C.3 under the same conditions.",
                gating: false,
                gatingBasis:
                    "Same as CC and MI: no tolerance in TG-132. It is also the most scale-sensitive of " +
                    "the three, which makes an absolute limit even harder to defend across sites."));

            // ---------------------------------------------------------- deformation / topology

            Register(new MetricDefinition(
                key: MetricKeys.JacobianNegative,
                displayName: "Jacobian < 0",
                unit: "%",
                higherIsBetter: false,
                description:
                    "Percentage of deformation field voxels with a non-positive Jacobian determinant, " +
                    "that is, unphysical topological folding. Evaluated inside the patient outline " +
                    "when one can be placed on the field's grid, over the whole field otherwise; the " +
                    "criterion column names which. For a rigid transform this is 0 by definition, " +
                    "since |J| = 1 everywhere.",
                clinicalQuestion:
                    "Has the deformation folded tissue onto itself?",
                decisionSupported:
                    "A single negative value disqualifies the registration for dose accumulation and " +
                    "for direct contour propagation. This is the clearest go/no-go in the whole set.",
                standardBasis:
                    "TG-132 Table III. Tolerance: no negative values, with deviations from 1 as " +
                    "clinically expected.",
                gating: true,
                gatingBasis:
                    "Table III sets an explicit tolerance — no negative values — and the report calls a " +
                    "negative determinant an erroneous physical model of the patient. The profiles apply " +
                    "it literally: the limit is 0 % in all four, so any folding at all is a breach. It is " +
                    "deliberately not varied by anatomical site, because the report ties this tolerance to " +
                    "the physics and not to the site.\n\n" +
                    "The tolerance is not relaxed; the domain it is applied over is stated. Table III " +
                    "states this criterion per structure, and a deformation field's grid is a box that " +
                    "extends well past the patient into air, where the algorithm has no image to " +
                    "constrain it and folds freely. On the commissioning phantom, 99.95 % of the folded " +
                    "points were outside the patient outline: 2.979 % over the whole box against 0.003 % " +
                    "inside it. Grading the box therefore fails almost every deformable registration on " +
                    "a property of air, and a gate that always fails stops being read. So the value " +
                    "graded is the one inside the patient outline where one exists, the whole-field " +
                    "figure travels beside it in the criterion column and in the dataset, and the " +
                    "diagnostics report the Jacobian per structure. Where folding does fall inside the " +
                    "patient but in a region that does not affect the intended use, the report asks for " +
                    "that influence to be evaluated; that judgement is the physicist's and the tool does " +
                    "not pre-empt it."));

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
                    "tolerance.",
                gating: false,
                gatingBasis:
                    "Not in TG-132, and the per-site limits it used to carry were invented here. Two " +
                    "things made them indefensible. They had no source; and across two frames of " +
                    "reference — two scanners, or a CT and an MR — the matrix also carries the offset " +
                    "between the coordinate systems, so a correct registration can exceed any limit for " +
                    "that reason alone. It stays on screen as a plausibility check, since a displacement " +
                    "far larger than the case can justify usually means the wrong image pair was " +
                    "registered, but that judgement is the reader\u0027s and not a threshold\u0027s."));

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
                    "large local changes in the Jacobian determinant can indicate a registration error.",
                gating: false,
                gatingBasis:
                    "TG-132 does not name this metric and its limits here were invented, exactly like " +
                    "those of the intensity metrics. It is kept because for a rigid transform the value " +
                    "1.0 is a true statement worth recording in a signed report — the field gradient is " +
                    "constant, so no local irregularity is possible — but a statement that is true by " +
                    "definition cannot fail anything. For a deformable registration the real quantity is " +
                    "reported separately as the DVF gradient, which is measured rather than asserted."));

            Register(new MetricDefinition(
                key: MetricKeys.JacobianDeparture,
                displayName: "Jacobian departure from 1",
                unit: "",
                higherIsBetter: false,
                description:
                    "How far the Jacobian determinant departs from 1 over the central 98 % of the " +
                    "graded region — the patient outline where one exists, the whole field otherwise, " +
                    "the same domain as the folding metric above, since both are clauses of one " +
                    "Table III row. It is the larger of |p99 - 1| and |1 - p1|. A determinant of 1 " +
                    "means the deformation preserves volume at that point; below 1 it compresses, " +
                    "above 1 it expands. Percentiles rather than the extremes, because a single " +
                    "voxel at the edge of the field's support controls min and max while saying " +
                    "nothing about the deformation as a whole.",
                clinicalQuestion:
                    "Is the volume change this deformation applies the volume change the case led " +
                    "you to expect?",
                decisionSupported:
                    "A departure larger than the anatomy can justify — a structure expanding where " +
                    "it was expected to shrink, or either happening far more than the interval " +
                    "between scans allows — points at the registration rather than at the patient. " +
                    "It is the check that catches a deformation which folds nowhere and is still " +
                    "physically wrong.",
                standardBasis:
                    "TG-132 Table III, second clause of the Jacobian row: \u201Cno negative values, " +
                    "nor values departing from 1 relative to what is expected for the clinical " +
                    "scenario (0-1 for structures where volume reduction is expected; above 1 for " +
                    "structures where volume expansion is expected)\u201D.",
                gating: false,
                gatingBasis:
                    "Measured but not graded, and the reason is in the criterion itself: Table III " +
                    "ties the acceptable departure to \u201Cwhat is expected for the clinical " +
                    "scenario\u201D and states it per structure — 0-1 where reduction is expected, " +
                    "above 1 where expansion is. Neither the expectation nor the structure it " +
                    "applies to is available to this tool, and choosing a fixed band here would be " +
                    "inventing the very number the report declined to give. What the tool can do " +
                    "honestly is measure the departure and put it in front of the physicist, who " +
                    "knows which structures were expected to change and by how much. The first " +
                    "clause of the same row is gated, at 0 %, because \u201Cno negative values\u201D " +
                    "is absolute and needs no clinical context."));

            Register(new MetricDefinition(
                key: MetricKeys.DvfGradientMax,
                displayName: "DVF Gradient (max)",
                unit: "",
                higherIsBetter: false,
                description:
                    "Largest displacement-gradient magnitude in the deformation vector field: the " +
                    "Frobenius norm of grad u, evaluated by central differences over the field's own " +
                    "grid. Dimensionless — millimetres of displacement change per millimetre travelled. " +
                    "Zero would mean a pure translation; a large value means the field changes abruptly " +
                    "from one grid point to the next.",
                clinicalQuestion:
                    "Does the deformation vary abruptly between neighbouring points?",
                decisionSupported:
                    "An irregular field points to the algorithm overfitting in low-contrast regions, " +
                    "where there is no anatomical information to drive the registration. Those are " +
                    "exactly the regions where a propagated contour is least trustworthy, so a high " +
                    "value tells the physicist which part of the deformation to inspect rather than " +
                    "trust.",
                standardBasis:
                    "Not tabulated in TG-132. Related to the report's observation in section 4.C.3 that " +
                    "large local changes in the Jacobian determinant can indicate a registration error.",
                gating: false,
                gatingBasis:
                    "Measured, but not graded: TG-132 sets no limit for field regularity, and any number " +
                    "chosen here would be invented — the same reason NCC, NMI and SSD are ungraded. What " +
                    "makes it worth reporting anyway is that it is now a real measurement of this " +
                    "registration rather than a statement true of every rigid transform. A local baseline " +
                    "over cases the department already accepts is how it becomes actionable."));

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
                    "approximately 0.80-0.90, and volume dependent.",
                gating: true,
                gatingBasis:
                    "Table III gives a numeric range, which is the strongest tolerance basis in the set " +
                    "after TRE. The report's own footnote warns that the expected value depends on the " +
                    "volume of the structure, so read the classification together with which structure " +
                    "produced it. One exception: measured on the patient surface outline alone — no " +
                    "organ or target matched — it stops gating. The outline is not the kind of " +
                    "structure this row describes, and its ends cannot agree between two scans of " +
                    "different length regardless of registration quality."));

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
                    "or the maximum voxel dimension, roughly 2-3 mm.",
                gating: true,
                gatingBasis:
                    "Table III sets a tolerance in millimetres, and the metric is expressed in the same " +
                    "units as the quantity being bounded. One exception, shared with DSC: measured on " +
                    "the patient surface outline alone it stops gating, because a scan-length mismatch " +
                    "at the outline's edges reads as a distance error that has nothing to do with the " +
                    "registration."));

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
                    "published series.",
                gating: false,
                gatingBasis:
                    "TG-132 does not name this metric, and the limits it used to carry came from this " +
                    "project's first version rather than from any source that was ever cited. It follows " +
                    "the intensity metrics: shown, exported, unable to fail a registration. Its job is to " +
                    "qualify the MDA, which does gate — a mean distance inside tolerance beside a large " +
                    "HD95 means the disagreement is local rather than uniform, and that is what sends a " +
                    "physicist to look at the overlay."));

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
                    "voxel dimension, roughly 2-3 mm.",
                gating: true,
                gatingBasis:
                    "The strongest basis of any metric here: an explicit tolerance in Table III, tied to " +
                    "a physical quantity of the image pair rather than to a convention, and expressed in " +
                    "the units of the error itself. TG-132 also gives 1 mm for stereotactic radiosurgery."));

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
                    "landmarks. Reporting mean and maximum separately is a practical extension.",
                gating: true,
                gatingBasis:
                    "Same tolerance basis as the mean; only the summary statistic is ours. Applying the " +
                    "Table III limit to the worst landmark is the conservative reading, which is the right " +
                    "one for a small target."));

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
                    "than direct verification.",
                gating: true,
                gatingBasis:
                    "Table III sets a tolerance in millimetres. It gates in one direction only in " +
                    "substance: a large residual invalidates the other metrics, while a small one proves " +
                    "nothing on its own, since a registration can be consistently wrong."));
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

        /// <summary>
        /// Whether a value outside the profile limits may drive the verdict. An unknown key
        /// returns false: a metric nobody has justified must not be able to fail a
        /// registration.
        /// </summary>
        public static bool IsGating(string key)
        {
            MetricDefinition definition;
            return TryGet(key, out definition) && definition.Gating;
        }
    }
}
