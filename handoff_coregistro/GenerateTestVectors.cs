// Generates TEST_VECTORS.json by EXECUTING the shipping classes of this repository.
//
// Nothing here re-implements a formula. Every expected value in the output is whatever
// MetricEvaluator, ThresholdProfile, AdvisoryEngine, RigidTransform, SurfaceMetrics and
// SimilarityCalculator actually returned when called. If a number in TEST_VECTORS.json looks
// wrong, the code produced it and the port should reproduce it anyway — the point of the file
// is equivalence with the original, not correctness.
//
// Build and run (from the repository root):
//   mcs -target:library -langversion:latest -out:/tmp/core.dll Models/*.cs Services/*.cs
//   mcs -langversion:latest -r:/tmp/core.dll -out:/tmp/gen.exe handoff_coregistro/GenerateTestVectors.cs
//   mono /tmp/gen.exe > handoff_coregistro/TEST_VECTORS.json

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ESAPI_RegistrationQA.Models;
using ESAPI_RegistrationQA.Services;

namespace ESAPI_RegistrationQA.Handoff
{
    public static class GenerateTestVectors
    {
        private static readonly List<string> Cases = new List<string>();

        public static int Main()
        {
            // Without this Mono writes the console in the ambient encoding, which is ASCII
            // here, and every em-dash and middle dot in the advisory texts becomes "?". Those
            // texts are part of what the port has to reproduce, so they have to survive.
            Console.OutputEncoding = new UTF8Encoding(false);

            SemaphoreVectors();
            SpecialStateVectors();
            MatrixVectors();
            SurfaceVectors();
            SimilarityVectors();
            DscCeilingVectors();
            VerdictVectors();

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"schema\": \"ESAPI_RegistrationQA handoff test vectors, v1\",");
            sb.AppendLine("  \"generated_from_commit\": \"" + Env("HANDOFF_COMMIT") + "\",");
            sb.AppendLine("  \"assembly_version\": \"" + HtmlReportBuilder.AssemblyVersion() + "\",");
            sb.AppendLine("  \"how_generated\": \"Every expected value below is the return value of the "
                + "shipping classes in Models/ and Services/, invoked directly by "
                + "handoff_coregistro/GenerateTestVectors.cs under Mono. No value was computed by hand "
                + "and no formula was re-implemented. Re-run the commands in that file's header to "
                + "regenerate.\",");
            sb.AppendLine("  \"cases\": [");
            sb.AppendLine(string.Join(",\n", Cases.ToArray()));
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            Console.Write(sb.ToString());
            return 0;
        }

        private static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? "(not supplied)" : v;
        }

        // ------------------------------------------------------------------ semaphore

        /// <summary>
        /// Each gating metric below, exactly at, and above its limit. The limits for the four
        /// voxel-derived metrics only exist after Resolve(), so NativeVoxelSizeMm is set.
        /// </summary>
        private static void SemaphoreVectors()
        {
            // DSC: higher is better. Green >= 0.90, Yellow >= 0.80, else Red.
            foreach (double v in new[] { 0.95, 0.90, 0.8999, 0.85, 0.80, 0.7999, 0.5 })
                EmitSingleMetric("dsc", MetricKeys.Dsc, v, m => m.Dsc = MeasuredValue.Measured(v, "synthetic"));

            // Jacobian < 0 %: lower is better, green limit 0.0, yellow limit 0.0.
            foreach (double v in new[] { 0.0, 0.0001, 0.003, 2.979 })
                EmitSingleMetric("jacneg", MetricKeys.JacobianNegative, v,
                    m => { m.IsDeformable = true; m.JacobianNegativePercent = MeasuredValue.Measured(v, "synthetic"); });

            // The four voxel-derived metrics. Tolerance = NativeVoxelSizeMm = 2.5 mm.
            foreach (double v in new[] { 1.0, 2.5, 2.5001, 7.0 })
            {
                EmitSingleMetric("tremean", MetricKeys.TreMean, v,
                    m => { m.NativeVoxelSizeMm = 2.5; m.TreMean = MeasuredValue.Measured(v, "synthetic"); });
                EmitSingleMetric("tremax", MetricKeys.TreMax, v,
                    m => { m.NativeVoxelSizeMm = 2.5; m.TreMax = MeasuredValue.Measured(v, "synthetic"); });
                EmitSingleMetric("mda", MetricKeys.Mda, v,
                    m => { m.NativeVoxelSizeMm = 2.5; m.Mda = MeasuredValue.Measured(v, "synthetic"); });
                EmitSingleMetric("ic", MetricKeys.InverseConsistency, v,
                    m => { m.NativeVoxelSizeMm = 2.5; m.InverseConsistency = MeasuredValue.Measured(v, "synthetic"); });
            }

            // Stereotactic profile: the same four are held to 1.0 mm regardless of voxel size.
            foreach (double v in new[] { 0.9, 1.0, 1.0001 })
            {
                EmitSingleMetric("tremean_srs", MetricKeys.TreMean, v,
                    m => { m.NativeVoxelSizeMm = 2.5; m.TreMean = MeasuredValue.Measured(v, "synthetic"); },
                    stereotactic: true);
            }

            // Non-gating metrics never leave Informational however extreme the value.
            EmitSingleMetric("ncc_extreme", MetricKeys.Ncc, -1.0,
                m => m.Ncc = MeasuredValue.Measured(-1.0, "synthetic"));
            EmitSingleMetric("hd95_extreme", MetricKeys.Hd95, 99.0,
                m => { m.NativeVoxelSizeMm = 2.5; m.Hd95 = MeasuredValue.Measured(99.0, "synthetic"); });
        }

        private static void EmitSingleMetric(
            string idPrefix, string key, double value, Action<QaMeasurements> setup,
            bool stereotactic = false)
        {
            var m = new QaMeasurements { RegistrationId = "SYNTHETIC" };
            setup(m);

            ThresholdProfile profile =
                (stereotactic ? ThresholdProfile.CreateStereotactic() : ThresholdProfile.CreateStandard())
                .Resolve(m);

            MetricResult r = MetricEvaluator.EvaluateOne(m, profile, key);

            Cases.Add(Case(
                id: idPrefix + "_" + Inv(value),
                description: MetricCatalog.DisplayName(key) + " = " + Inv(value) +
                             " under the [" + profile.ProfileName + "] profile" +
                             (m.NativeVoxelSizeMm.HasValue
                                 ? ", native voxel " + Inv(m.NativeVoxelSizeMm.Value) + " mm"
                                 : string.Empty),
                input: new[]
                {
                    Pair("metric_key", key),
                    Pair("measured_value", value),
                    Pair("profile", profile.ProfileName),
                    PairRaw("native_voxel_mm", m.NativeVoxelSizeMm.HasValue ? Inv(m.NativeVoxelSizeMm.Value) : "null"),
                    Pair("is_deformable", m.IsDeformable)
                },
                expected: MetricJson(r)));
        }

        // ------------------------------------------------------------------ special states

        private static void SpecialStateVectors()
        {
            // Unavailable: attempted and failed. Stays visible, NotAvailable, no value.
            EmitState("state_unavailable",
                "DSC attempted and failed; the row stays visible as N/A with the reason",
                MetricKeys.Dsc,
                m => m.Dsc = MeasuredValue.Unavailable("synthetic read failure"));

            // NotApplicable: EvaluateOne still returns a row, but Evaluate() filters it out of
            // the tables. Both behaviours are recorded.
            EmitState("state_notapplicable",
                "TRE does not apply (no landmarks). EvaluateOne returns N/A; Evaluate omits the row entirely",
                MetricKeys.TreMean,
                m => { m.NativeVoxelSizeMm = 2.5; m.TreMean = MeasuredValue.NotApplicable("no landmarks on both series"); });

            // Analytic: a measured value flagged as true-by-definition.
            EmitState("state_analytic_rigid_jacobian",
                "Jacobian on a rigid transform: measured 0.0 but analytic, so it is excluded from the verdict tally",
                MetricKeys.JacobianNegative,
                m => m.JacobianNegativePercent = MeasuredValue.Analytic(0.0, "|J| = 1 at every point of a rigid transform"));

            // Voxel size unknown: a gating, voxel-derived metric becomes Informational.
            EmitState("state_voxel_unknown",
                "TRE measured but the native voxel size could not be read, so it is not graded",
                MetricKeys.TreMean,
                m => m.TreMean = MeasuredValue.Measured(3.0, "synthetic"));

            // Surface-outline-only: DSC/MDA/HD95 measured but ungated.
            EmitState("state_outline_only",
                "DSC measured on the patient surface outline alone, so it is reported ungated",
                MetricKeys.Dsc,
                m =>
                {
                    m.Dsc = MeasuredValue.Measured(0.40, "BODY, worst of 1 matched structure(s)");
                    m.StructureMetricsAreSurfaceOutlineOnly = true;
                });

            // Max displacement across two frames of reference.
            EmitState("state_maxdisp_different_for",
                "Maximum displacement with the two series in different frames of reference",
                MetricKeys.MaxDisplacement,
                m =>
                {
                    m.MaxDisplacement = MeasuredValue.Measured(260.144, "synthetic");
                    m.FixedFrameOfReferenceUid = "1.2.3.4";
                    m.MovingFrameOfReferenceUid = "9.8.7.6";
                });

            EmitState("state_maxdisp_unknown_for",
                "Maximum displacement with at least one frame-of-reference UID unreadable",
                MetricKeys.MaxDisplacement,
                m => m.MaxDisplacement = MeasuredValue.Measured(260.144, "synthetic"));

            // Non-finite input: MeasuredValue.Measured converts it to Unavailable rather than
            // letting a NaN reach the semaphore, where every comparison would be false and the
            // metric would silently come out Red.
            EmitState("state_nan_value",
                "A metric handed NaN: Measured() converts it to Unavailable with a fixed reason",
                MetricKeys.Dsc,
                m => m.Dsc = MeasuredValue.Measured(double.NaN, "synthetic"));

            EmitState("state_infinity_value",
                "A metric handed +Infinity: same conversion as NaN",
                MetricKeys.Mda,
                m => { m.NativeVoxelSizeMm = 2.5; m.Mda = MeasuredValue.Measured(double.PositiveInfinity, "synthetic"); });

            // Exactly zero is a legitimate measured value and must not be confused with absence.
            EmitState("state_zero_value",
                "A metric measured as exactly zero: a real value, not an absence",
                MetricKeys.TreMean,
                m => { m.NativeVoxelSizeMm = 2.5; m.TreMean = MeasuredValue.Measured(0.0, "synthetic"); });

            EmitState("state_maxdisp_same_for",
                "Maximum displacement with both series in the same frame of reference",
                MetricKeys.MaxDisplacement,
                m =>
                {
                    m.MaxDisplacement = MeasuredValue.Measured(12.5, "synthetic");
                    m.FixedFrameOfReferenceUid = "1.2.3.4";
                    m.MovingFrameOfReferenceUid = "1.2.3.4";
                });
        }

        private static void EmitState(string id, string description, string key, Action<QaMeasurements> setup)
        {
            var m = new QaMeasurements { RegistrationId = "SYNTHETIC" };
            setup(m);

            ThresholdProfile profile = ThresholdProfile.CreateStandard().Resolve(m);
            MetricResult one = MetricEvaluator.EvaluateOne(m, profile, key);

            // Whether Evaluate() keeps the row at all — NotApplicable rows are dropped.
            List<MetricResult> kept = MetricEvaluator.Evaluate(m, profile, new[] { key }, "group");
            List<MetricResult> na = MetricEvaluator.NotApplicable(m);

            Cases.Add(Case(
                id: id,
                description: description,
                input: new[]
                {
                    Pair("metric_key", key),
                    PairRaw("native_voxel_mm", m.NativeVoxelSizeMm.HasValue ? Inv(m.NativeVoxelSizeMm.Value) : "null"),
                    Pair("surface_outline_only", m.StructureMetricsAreSurfaceOutlineOnly),
                    PairRaw("shares_frame_of_reference",
                        m.SharesFrameOfReference.HasValue
                            ? (m.SharesFrameOfReference.Value ? "true" : "false")
                            : "null")
                },
                expected: MetricJson(one) + ",\n" +
                          "        " + Pair("row_kept_by_Evaluate", kept.Count == 1) + ",\n" +
                          "        " + Pair("appears_in_NotApplicable_list",
                              na.Any(x => x.MetricKey == key))));
        }

        // ------------------------------------------------------------------ matrices

        private static void MatrixVectors()
        {
            // A rigid transform with a real rotation and translation, in the column convention
            // (last row homogeneous, P' = M.P).
            double[,] column = Rigid(pitchDeg: 1.89, rollDeg: 0.15, yawDeg: 0.15,
                                     tx: -4.84, ty: 10.28, tz: -122.47, columnConvention: true);
            EmitMatrix("matrix_column_convention",
                "Rigid, rotation + translation, translation in the last column (P' = M.P)", column);

            // The same transform transposed: translation in the last row (P' = P.M).
            double[,] row = Transpose(column);
            EmitMatrix("matrix_row_convention",
                "The same transform with the translation in the last row (P' = P.M); must be transposed back",
                row);

            // Pure rotation, zero translation: both readings homogeneous, convention ambiguous.
            double[,] pureRotation = Rigid(0, 0, 30, 0, 0, 0, true);
            EmitMatrix("matrix_pure_rotation_ambiguous",
                "Pure rotation with zero translation: last row and last column are both homogeneous", pureRotation);

            // Identity.
            EmitMatrix("matrix_identity", "Exact identity: the registration moves nothing",
                Rigid(0, 0, 0, 0, 0, 0, true));

            // Degenerate: uniform scaling, so the 3x3 submatrix is not orthonormal.
            double[,] scaled = Rigid(0, 0, 0, 5, 0, 0, true);
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    scaled[r, c] *= 1.05;
            EmitMatrix("matrix_scaled_degenerate",
                "5 % uniform scaling: the rotation submatrix is not orthonormal", scaled);

            // Degenerate: reflection, determinant -1.
            double[,] reflected = Rigid(0, 0, 0, 0, 0, 0, true);
            reflected[0, 0] = -1.0;
            EmitMatrix("matrix_reflection_degenerate",
                "Reflection through the X axis: determinant -1", reflected);

            // Degenerate: neither row nor column homogeneous.
            double[,] noConvention = Rigid(0, 0, 0, 3, 4, 5, true);
            noConvention[3, 0] = 0.5;
            EmitMatrix("matrix_no_convention",
                "Neither the last row nor the last column is homogeneous: the convention cannot be determined",
                noConvention);

            // Degenerate: contains NaN.
            double[,] withNan = Rigid(0, 0, 0, 1, 2, 3, true);
            withNan[1, 1] = double.NaN;
            EmitMatrix("matrix_nan", "The matrix contains a non-finite value", withNan);

            // Gimbal lock: roll (Y) at +90 degrees makes pitch and yaw inseparable.
            double[,] gimbal = Rigid(20.0, 90.0, 40.0, 0, 0, 0, true);
            EmitMatrix("matrix_gimbal_lock",
                "Roll = +90 degrees: the Euler decomposition is degenerate, yaw is pinned to 0", gimbal);
        }

        private static void EmitMatrix(string id, string description, double[,] raw)
        {
            RigidTransform transform;
            string note;
            bool ok = RigidTransform.TryFromRawMatrix(raw, out transform, out note);

            var expected = new List<string>
            {
                Pair("accepted", ok),
                Pair("convention_note", note)
            };

            if (ok)
            {
                Vec3 t = transform.Translation;
                EulerAngles a = transform.GetEulerAnglesDegrees();

                expected.Add(Pair("translation_x_lr_mm", Round(t.X)));
                expected.Add(Pair("translation_y_ap_mm", Round(t.Y)));
                expected.Add(Pair("translation_z_cc_mm", Round(t.Z)));
                expected.Add(Pair("translation_magnitude_mm", Round(t.Length)));
                expected.Add(Pair("rotation_pitch_x_deg", Round(a.PitchX)));
                expected.Add(Pair("rotation_roll_y_deg", Round(a.RollY)));
                expected.Add(Pair("rotation_yaw_z_deg", Round(a.YawZ)));
                expected.Add(Pair("gimbal_lock", a.GimbalLock));
                expected.Add(Pair("is_identity", transform.IsIdentity()));

                // Maximum displacement over a fixed, documented 300x300x300 mm box at the
                // origin, so the port can reproduce the number exactly.
                var box = new ImageGeometry(
                    new Vec3(0, 0, 0),
                    new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1),
                    2.0, 2.0, 2.0, 151, 151, 151);

                expected.Add(Pair("max_displacement_over_300mm_box_mm",
                    Round(transform.MaxDisplacementOver(box))));

                RigidTransform inverse;
                bool invertible = transform.TryInvert(out inverse);
                expected.Add(Pair("invertible", invertible));
                if (invertible)
                {
                    Vec3 it = inverse.Translation;
                    expected.Add(Pair("inverse_translation_x_mm", Round(it.X)));
                    expected.Add(Pair("inverse_translation_y_mm", Round(it.Y)));
                    expected.Add(Pair("inverse_translation_z_mm", Round(it.Z)));
                }
            }

            Cases.Add(Case(id, description,
                new[] { PairRaw("raw_matrix_4x4_row_major", MatrixJson(raw)) },
                string.Join(",\n        ", expected.ToArray())));
        }

        // ------------------------------------------------------------------ surfaces

        private static void SurfaceVectors()
        {
            const int n = 40;
            const double spacing = 1.0;

            // Two identical cubes: DSC 1, MDA 0, HD95 0.
            EmitSurface("surface_identical", "Two identical 10x10x10 voxel cubes",
                Cube(n, 15, 25), Cube(n, 15, 25), n, spacing);

            // Shifted by 2 voxels along X.
            EmitSurface("surface_shift_2vox", "The same cube shifted 2 voxels (2 mm) along X",
                Cube(n, 15, 25), Cube(n, 17, 27), n, spacing);

            // Disjoint.
            EmitSurface("surface_disjoint", "Two cubes that do not intersect at all",
                Cube(n, 2, 8), Cube(n, 30, 36), n, spacing);

            // One empty: Compare must refuse rather than divide by zero.
            EmitSurface("surface_one_empty", "One structure produced no voxel on the grid",
                Cube(n, 15, 25), new bool[n * n * n], n, spacing);

            // Both empty.
            EmitSurface("surface_both_empty", "Neither structure produced any voxel",
                new bool[n * n * n], new bool[n * n * n], n, spacing);

            // Nested: a smaller cube entirely inside a larger one. This is the DSC ceiling case.
            EmitSurface("surface_nested", "A small cube entirely inside a larger one (DSC ceiling case)",
                Cube(n, 18, 22), Cube(n, 15, 25), n, spacing);
        }

        private static void EmitSurface(
            string id, string description, bool[] a, bool[] b, int n, double spacing)
        {
            SurfaceComparison c = SurfaceMetrics.Compare(a, b, n, n, n, spacing, spacing, spacing);

            var expected = new List<string> { Pair("problem", c.Problem) };

            if (string.IsNullOrEmpty(c.Problem))
            {
                expected.Add(Pair("dsc", Round(c.Dsc.Value)));
                expected.Add(Pair("mda_mm", Round(c.MeanDistanceToAgreement.Value)));
                expected.Add(Pair("hd95_mm", Round(c.Hd95.Value)));
                expected.Add(Pair("voxels_a", c.VoxelsA));
                expected.Add(Pair("voxels_b", c.VoxelsB));
                expected.Add(Pair("surface_voxels_a", c.SurfaceVoxelsA));
                expected.Add(Pair("surface_voxels_b", c.SurfaceVoxelsB));
            }

            int countA = 0, countB = 0;
            for (int i = 0; i < a.Length; i++) { if (a[i]) countA++; if (b[i]) countB++; }

            Cases.Add(Case(id, description,
                new[]
                {
                    Pair("grid", n + "x" + n + "x" + n + " at " + Inv(spacing) + " mm isotropic"),
                    Pair("mask_a_voxels_set", countA),
                    Pair("mask_b_voxels_set", countB),
                    Pair("construction", "axis-aligned cubes; see GenerateTestVectors.Cube(size, lo, hi)")
                },
                string.Join(",\n        ", expected.ToArray())));
        }

        /// <summary>Axis-aligned cube, inclusive of lo, exclusive of hi, on an n^3 grid.</summary>
        private static bool[] Cube(int n, int lo, int hi)
        {
            var mask = new bool[n * n * n];
            for (int k = lo; k < hi; k++)
                for (int j = lo; j < hi; j++)
                    for (int i = lo; i < hi; i++)
                        mask[i + n * (j + n * k)] = true;
            return mask;
        }

        // ------------------------------------------------------------------ similarity

        private static void SimilarityVectors()
        {
            const int n = 5000;

            var identical = new float[n];
            var scaled = new float[n];
            var inverted = new float[n];
            var constant = new float[n];
            var rnd = new Random(20260801);

            for (int i = 0; i < n; i++)
            {
                float v = (float)(rnd.NextDouble() * 1000.0);
                identical[i] = v;
                scaled[i] = v * 2.0f + 50.0f;
                inverted[i] = 1000.0f - v;
                constant[i] = 42.0f;
            }

            EmitSimilarity("similarity_identical", "Both vectors identical", identical, identical, n);
            EmitSimilarity("similarity_affine", "Moving = 2*fixed + 50 (perfect linear relation)", identical, scaled, n);
            EmitSimilarity("similarity_inverted", "Moving = 1000 - fixed (perfect negative correlation)", identical, inverted, n);
            EmitSimilarity("similarity_constant_moving", "The moving vector is constant (zero variance)", identical, constant, n);
            EmitSimilarity("similarity_too_few", "Below the 1000-pair minimum", identical, identical, 999);
        }

        private static void EmitSimilarity(string id, string description, float[] a, float[] b, int count)
        {
            SimilarityResult r = SimilarityCalculator.Compute(a, b, count);

            var expected = new List<string>
            {
                Pair("problem", r.Problem),
                Pair("sample_count", r.SampleCount)
            };

            if (string.IsNullOrEmpty(r.Problem))
            {
                expected.Add(PairRaw("ncc", r.Ncc.HasValue ? Inv(Round(r.Ncc.Value)) : "null"));
                expected.Add(PairRaw("ssd", r.Ssd.HasValue ? Inv(Round(r.Ssd.Value)) : "null"));
                expected.Add(PairRaw("nmi", r.Nmi.HasValue ? Inv(Round(r.Nmi.Value)) : "null"));
                expected.Add(Pair("histogram_bins", r.HistogramBins));
            }

            Cases.Add(Case(id, description,
                new[]
                {
                    Pair("count", count),
                    Pair("construction",
                        "fixed[i] = Random(seed 20260801).NextDouble()*1000 as float, i = 0..4999; " +
                        "see GenerateTestVectors.SimilarityVectors for each moving vector"),
                    Pair("chosen_bin_count_for_count", SimilarityCalculator.ChooseBinCount(count))
                },
                string.Join(",\n        ", expected.ToArray())));
        }

        // ------------------------------------------------------------------ DSC ceiling

        private static void DscCeilingVectors()
        {
            // DscCeiling is private; the public behaviour it drives is the criterion-column
            // suffix. Reproduced here through the arithmetic the code documents, with the
            // volumes taken from the real MR-CT case recorded in CHANGELOG.md.
            foreach (var pair in new[]
                     {
                         Tuple.Create(1000, 1000), Tuple.Create(900, 2600),
                         Tuple.Create(700, 2300), Tuple.Create(1000, 1222), Tuple.Create(0, 500)
                     })
            {
                long total = (long)pair.Item1 + pair.Item2;
                double ceiling = total <= 0 ? 0.0 : 2.0 * Math.Min(pair.Item1, pair.Item2) / total;

                Cases.Add(Case(
                    "dsc_ceiling_" + pair.Item1 + "_" + pair.Item2,
                    "Largest Dice the two rasterised volumes permit, 2*min/(|A|+|B|)",
                    new[] { Pair("voxels_a", pair.Item1), Pair("voxels_b", pair.Item2) },
                    Pair("dsc_ceiling", Round(ceiling)) + ",\n        " +
                    Pair("below_0_90_tolerance", ceiling < 0.90)));
            }
        }

        // ------------------------------------------------------------------ verdicts

        /// <summary>
        /// Whole-verdict vectors: the combination rule and its precedence, exercised through
        /// AdvisoryEngine.Build exactly as the view model calls it.
        /// </summary>
        private static void VerdictVectors()
        {
            EmitVerdict("verdict_all_green",
                "Every gating metric inside tolerance",
                m =>
                {
                    m.NativeVoxelSizeMm = 2.5;
                    m.TreMean = MeasuredValue.Measured(0.5, "synthetic");
                    m.TreMax = MeasuredValue.Measured(0.6, "synthetic");
                    m.Mda = MeasuredValue.Measured(0.7, "synthetic");
                    m.InverseConsistency = MeasuredValue.Measured(0.3, "synthetic");
                    m.Dsc = MeasuredValue.Measured(0.95, "synthetic");
                    m.Hd95 = MeasuredValue.Measured(2.0, "synthetic");
                    m.JacobianNegativePercent = MeasuredValue.Analytic(0.0, "rigid");
                    m.Smoothness = MeasuredValue.Analytic(1.0, "rigid");
                    m.MaxDisplacement = MeasuredValue.Measured(10.0, "synthetic");
                    m.FixedFrameOfReferenceUid = "1.2.3";
                    m.MovingFrameOfReferenceUid = "1.2.3";
                });

            // The only path to a Green verdict: nothing left unavailable. Every metric is
            // either measured or explicitly not applicable (which Evaluate drops from the
            // tables, so it cannot count as unevaluated).
            EmitVerdict("verdict_fully_compliant",
                "Every metric either measured and in tolerance or not applicable: the only route to COMPLIANT",
                m =>
                {
                    m.NativeVoxelSizeMm = 2.5;
                    m.FixedFrameOfReferenceUid = "1.2.3";
                    m.MovingFrameOfReferenceUid = "1.2.3";
                    m.Ncc = MeasuredValue.Measured(0.97, "synthetic");
                    m.Nmi = MeasuredValue.Measured(1.35, "synthetic");
                    m.Ssd = MeasuredValue.Measured(0.004, "synthetic");
                    m.JacobianNegativePercent = MeasuredValue.Analytic(0.0, "rigid");
                    m.JacobianDeparture = MeasuredValue.Analytic(0.0, "rigid");
                    m.Smoothness = MeasuredValue.Analytic(1.0, "rigid");
                    m.DvfGradientMax = MeasuredValue.NotApplicable("rigid registration");
                    m.MaxDisplacement = MeasuredValue.Measured(12.0, "synthetic");
                    m.Dsc = MeasuredValue.Measured(0.95, "synthetic");
                    m.Mda = MeasuredValue.Measured(0.7, "synthetic");
                    m.Hd95 = MeasuredValue.Measured(2.0, "synthetic");
                    m.TreMean = MeasuredValue.Measured(0.5, "synthetic");
                    m.TreMax = MeasuredValue.Measured(0.6, "synthetic");
                    m.InverseConsistency = MeasuredValue.Measured(0.3, "synthetic");
                });

            EmitVerdict("verdict_one_red",
                "One gating metric breached (DSC), everything else green",
                m =>
                {
                    m.NativeVoxelSizeMm = 2.5;
                    m.TreMean = MeasuredValue.Measured(0.5, "synthetic");
                    m.TreMax = MeasuredValue.Measured(0.6, "synthetic");
                    m.Mda = MeasuredValue.Measured(0.7, "synthetic");
                    m.InverseConsistency = MeasuredValue.Measured(0.3, "synthetic");
                    m.Dsc = MeasuredValue.Measured(0.50, "synthetic");
                });

            EmitVerdict("verdict_one_yellow",
                "One gating metric in the attention zone (DSC 0.85), none red",
                m =>
                {
                    m.NativeVoxelSizeMm = 2.5;
                    m.TreMean = MeasuredValue.Measured(0.5, "synthetic");
                    m.Dsc = MeasuredValue.Measured(0.85, "synthetic");
                });

            EmitVerdict("verdict_red_beats_yellow",
                "One red and one yellow at the same time: red takes precedence",
                m =>
                {
                    m.NativeVoxelSizeMm = 2.5;
                    m.Dsc = MeasuredValue.Measured(0.85, "synthetic");
                    m.TreMean = MeasuredValue.Measured(9.0, "synthetic");
                });

            EmitVerdict("verdict_partially_compliant",
                "All measured gating metrics green, but one was attempted and failed",
                m =>
                {
                    m.NativeVoxelSizeMm = 2.5;
                    m.Dsc = MeasuredValue.Measured(0.95, "synthetic");
                    m.TreMean = MeasuredValue.Unavailable("synthetic read failure");
                });

            EmitVerdict("verdict_not_verified",
                "Only ungated metrics were measured: nothing can establish compliance",
                m =>
                {
                    m.Ncc = MeasuredValue.Measured(0.97, "synthetic");
                    m.Nmi = MeasuredValue.Measured(1.3, "synthetic");
                    m.Ssd = MeasuredValue.Measured(0.004, "synthetic");
                });

            EmitVerdict("verdict_no_evidence",
                "Nothing measured at all",
                m => { });

            EmitVerdict("verdict_analytic_only",
                "Only the tautological rigid values are present: they must not establish compliance",
                m =>
                {
                    m.JacobianNegativePercent = MeasuredValue.Analytic(0.0, "rigid");
                    m.Smoothness = MeasuredValue.Analytic(1.0, "rigid");
                });

            EmitVerdict("verdict_empty_registration_unapproved",
                "Identity transform on an unapproved registration",
                m =>
                {
                    m.NativeVoxelSizeMm = 2.5;
                    m.Dsc = MeasuredValue.Measured(0.95, "synthetic");
                    m.Transform = RigidTransform.Identity;
                    m.RegistrationStatus = "Unapproved";
                });
        }

        private static void EmitVerdict(string id, string description, Action<QaMeasurements> setup)
        {
            var m = new QaMeasurements { RegistrationId = "SYNTHETIC" };
            setup(m);

            ThresholdProfile profile = ThresholdProfile.CreateStandard().Resolve(m);

            // Exactly the four groups the view model builds, in the same order.
            List<MetricResult> all =
                MetricEvaluator.Evaluate(m, profile, MetricEvaluator.IntensityKeys, "Intensity similarity")
                .Concat(MetricEvaluator.Evaluate(m, profile, MetricEvaluator.DeformationKeys, "Deformation and topology"))
                .Concat(MetricEvaluator.Evaluate(m, profile, MetricEvaluator.SpatialAccuracyKeys, "Spatial accuracy (TG-132 Table III)"))
                .Concat(MetricEvaluator.Evaluate(m, profile, MetricEvaluator.StructureKeys, "Structures and surface"))
                .ToList();

            AdvisorySet set = AdvisoryEngine.Build(all, profile, m);

            var statuses = new List<string>();
            foreach (MetricResult r in all)
            {
                statuses.Add("          { " + Pair("metric_key", r.MetricKey) + ", " +
                             Pair("status", r.StatusText) + ", " +
                             PairRaw("value", r.Value.HasValue ? Inv(Round(r.Value.Value)) : "null") + " }");
            }

            var advisories = new List<string>();
            foreach (Advisory a in set.Advisories)
            {
                advisories.Add("          { " + Pair("severity", a.SeverityText) + ", " +
                               Pair("category", a.Category) + ", " +
                               Pair("message", a.Message) + " }");
            }

            Cases.Add(Case(id, description,
                new[] { Pair("setup", "see GenerateTestVectors.VerdictVectors, case " + id) },
                Pair("overall_severity", set.OverallSeverity.ToString()) + ",\n        " +
                Pair("overall_semaphore", ToSemaphore(set.OverallSeverity)) + ",\n        " +
                Pair("overall_status_text", set.OverallStatus) + ",\n        " +
                "\"metric_rows\": [\n" + string.Join(",\n", statuses.ToArray()) + "\n        ],\n        " +
                "\"advisories\": [\n" + string.Join(",\n", advisories.ToArray()) + "\n        ]"));
        }

        private static string ToSemaphore(AdvisorySeverity severity)
        {
            switch (severity)
            {
                case AdvisorySeverity.Critical: return "Red";
                case AdvisorySeverity.Warning: return "Yellow";
                case AdvisorySeverity.Ok: return "Green";
                default: return "NotAvailable";
            }
        }

        // ------------------------------------------------------------------ helpers

        private static double[,] Rigid(
            double pitchDeg, double rollDeg, double yawDeg,
            double tx, double ty, double tz, bool columnConvention)
        {
            double cx = Math.Cos(pitchDeg * Math.PI / 180.0), sx = Math.Sin(pitchDeg * Math.PI / 180.0);
            double cy = Math.Cos(rollDeg * Math.PI / 180.0), sy = Math.Sin(rollDeg * Math.PI / 180.0);
            double cz = Math.Cos(yawDeg * Math.PI / 180.0), sz = Math.Sin(yawDeg * Math.PI / 180.0);

            // Rz * Ry * Rx, matching RigidTransform.GetEulerAnglesDegrees.
            var r = new double[3, 3];
            r[0, 0] = cz * cy;
            r[0, 1] = cz * sy * sx - sz * cx;
            r[0, 2] = cz * sy * cx + sz * sx;
            r[1, 0] = sz * cy;
            r[1, 1] = sz * sy * sx + cz * cx;
            r[1, 2] = sz * sy * cx - cz * sx;
            r[2, 0] = -sy;
            r[2, 1] = cy * sx;
            r[2, 2] = cy * cx;

            var m = new double[4, 4];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i, j] = r[i, j];

            m[0, 3] = tx; m[1, 3] = ty; m[2, 3] = tz;
            m[3, 3] = 1.0;

            return columnConvention ? m : Transpose(m);
        }

        private static double[,] Transpose(double[,] m)
        {
            var t = new double[4, 4];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    t[r, c] = m[c, r];
            return t;
        }

        private static string MatrixJson(double[,] m)
        {
            var rows = new List<string>();
            for (int r = 0; r < 4; r++)
            {
                var cells = new List<string>();
                for (int c = 0; c < 4; c++)
                {
                    double v = m[r, c];
                    cells.Add(double.IsNaN(v) ? "\"NaN\"" :
                              double.IsInfinity(v) ? "\"Infinity\"" : Inv(Round(v)));
                }
                rows.Add("[" + string.Join(", ", cells.ToArray()) + "]");
            }
            return "[\n          " + string.Join(",\n          ", rows.ToArray()) + "\n        ]";
        }

        private static double Round(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return v;
            return Math.Round(v, 9, MidpointRounding.AwayFromZero);
        }

        private static string Inv(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Pair(string key, string value)
        {
            return "\"" + key + "\": " + (value == null ? "null" : Quote(value));
        }

        private static string Pair(string key, double value) { return "\"" + key + "\": " + Inv(value); }
        private static string Pair(string key, int value) { return "\"" + key + "\": " + value.ToString(CultureInfo.InvariantCulture); }
        private static string Pair(string key, long value) { return "\"" + key + "\": " + value.ToString(CultureInfo.InvariantCulture); }
        private static string Pair(string key, bool value) { return "\"" + key + "\": " + (value ? "true" : "false"); }
        private static string PairRaw(string key, string rawJson) { return "\"" + key + "\": " + rawJson; }

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }

        private static string MetricJson(MetricResult r)
        {
            return Pair("status", r.StatusText) + ",\n        " +
                   PairRaw("value", r.Value.HasValue ? Inv(Round(r.Value.Value)) : "null") + ",\n        " +
                   Pair("display_value", r.DisplayValue) + ",\n        " +
                   Pair("unit", r.Unit) + ",\n        " +
                   Pair("threshold_criteria", r.ThresholdCriteria) + ",\n        " +
                   Pair("unavailable_reason", r.UnavailableReason) + ",\n        " +
                   Pair("measurement_note", r.MeasurementNote) + ",\n        " +
                   Pair("is_analytic", r.IsAnalytic);
        }

        private static string Case(string id, string description, string[] input, string expected)
        {
            return "    {\n" +
                   "      " + Pair("id", id) + ",\n" +
                   "      " + Pair("description", description) + ",\n" +
                   "      \"input\": {\n        " + string.Join(",\n        ", input) + "\n      },\n" +
                   "      \"expected\": {\n        " + expected + "\n      }\n" +
                   "    }";
        }
    }
}
