// Exercises the real DeformationFieldReader and DeformationFieldMetrics against stub objects
// shaped like the Varian API's, so the reflection layer is tested without an Eclipse.
//
// verify_math.py checks the arithmetic by replicating it in Python. That leaves the part most
// likely to break untested: the reflection plumbing between the API and the arithmetic — locating
// DeformationField on a wrapper, allocating a buffer of a struct type unknown at compile time,
// invoking GetVectors, and reading each element's components back. Every bug this project has hit
// in Eclipse lived in exactly that layer (GetVoxels writing nothing, VVector overload resolution,
// markers exposing Points instead of CenterPoint, DicomType missing entirely), never in the
// formulas.
//
// The stubs mirror what the probe observed on VMS.IRS.Scripting:
//   MIRSNonRigidRegistration.NonRigidRegistration.DeformationField -> VectorField
//   VectorField { XSize, YSize, ZSize, XRes, YRes, ZRes, GetVectors(VectorFloat[,,]) }
//   VectorFloat { X, Y, Z }   (float components, read as properties)
//
// Build and run (Mono):
//   mcs -target:library -langversion:latest -out:core.dll Models/*.cs Services/*.cs
//   mcs -langversion:latest -r:core.dll -out:dvftests.exe tools/DvfContractTests.cs
//   mono dvftests.exe

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ESAPI_RegistrationQA.Models;
using ESAPI_RegistrationQA.Services;

namespace ESAPI_RegistrationQA.Tools
{
    // ---------------------------------------------------------------- API-shaped stubs

    /// <summary>
    /// Stands in for VMS.CA.Scripting.VectorFloat. Components are float properties, which is the
    /// case the reader has to handle: reading them as double would work, reading them only as
    /// fields would not — the mistake PointMapperReader originally made with the vector type.
    /// </summary>
    public struct StubVectorFloat
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    /// <summary>
    /// Stands in for VMS.CA.Scripting.VectorField. Fills the caller's buffer rather than
    /// returning one, exactly like GetVectors.
    /// </summary>
    public sealed class StubVectorField
    {
        private readonly Func<int, int, int, StubVectorFloat> _generator;

        public StubVectorField(int nx, int ny, int nz, double rx, double ry, double rz,
            Func<int, int, int, StubVectorFloat> generator)
        {
            XSize = nx; YSize = ny; ZSize = nz;
            XRes = rx; YRes = ry; ZRes = rz;
            _generator = generator;
        }

        public int XSize { get; private set; }
        public int YSize { get; private set; }
        public int ZSize { get; private set; }
        public double XRes { get; private set; }
        public double YRes { get; private set; }
        public double ZRes { get; private set; }

        public void GetVectors(StubVectorFloat[,,] preallocatedBuffer)
        {
            for (int z = 0; z < ZSize; z++)
                for (int y = 0; y < YSize; y++)
                    for (int x = 0; x < XSize; x++)
                        preallocatedBuffer[x, y, z] = _generator(x, y, z);
        }
    }

    /// <summary>Stands in for VMS.CA.Scripting.NonRigidRegistration.</summary>
    public sealed class StubNonRigidRegistration
    {
        public StubVectorField DeformationField { get; set; }

        /// <summary>
        /// Default to the identity, which is what a registration with no affine component
        /// carries. Tests that care set them explicitly.
        /// </summary>
        public double[,] PreTransformationMatrix { get; set; } = Identity();
        public double[,] PostTransformationMatrix { get; set; } = Identity();

        public static double[,] Identity()
        {
            return new double[,]
            {
                { 1, 0, 0, 0 },
                { 0, 1, 0, 0 },
                { 0, 0, 1, 0 },
                { 0, 0, 0, 1 }
            };
        }

        /// <summary>A uniform scaling by <paramref name="s"/>: determinant s^3.</summary>
        public static double[,] Scaling(double s)
        {
            return new double[,]
            {
                { s, 0, 0, 0 },
                { 0, s, 0, 0 },
                { 0, 0, s, 0 },
                { 0, 0, 0, 1 }
            };
        }

        /// <summary>A reflection through the x axis: determinant -1.</summary>
        public static double[,] Reflection()
        {
            return new double[,]
            {
                { -1, 0, 0, 0 },
                {  0, 1, 0, 0 },
                {  0, 0, 1, 0 },
                {  0, 0, 0, 1 }
            };
        }
    }

    /// <summary>
    /// Stands in for VMS.IRS.Scripting.MIRSNonRigidRegistration: a wrapper whose field lives one
    /// level down. Reaching it is the reader's holder-traversal path.
    /// </summary>
    public sealed class StubMirsNonRigidRegistration
    {
        public StubNonRigidRegistration NonRigidRegistration { get; set; }
    }

    /// <summary>A registration exposing no field at all — the unreadable case.</summary>
    public sealed class StubOpaqueRegistration
    {
        public string Id { get { return "OPAQUE"; } }
    }

    // ---------------------------------------------------------------- the tests

    public static class DvfContractTests
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _passed;

        public static int Main()
        {
            FieldIsReadThroughTheWrapper();
            UniformTranslation();
            UniformExpansion();
            Float32StoragePrecisionIsAsExpected();
            FoldingField();
            AnisotropicSpacingIsHonoured();
            GridTooThinForACentralDifference();
            UnreadableFieldIsReportedNotGuessed();
            RigidCaseDoesNotClaimAGradient();
            RealSizedGridDoesNotTakeSeconds();
            ProgressStagesAreOrderedAndComplete();
            AFailingProgressSinkDoesNotAbortTheMeasurement();
            AffineDeterminantsAreReadAndJudged();
            FoldingIsLocatedNotJustCounted();
            JacobianDepartureFollowsTableIIISecondClause();
            DivergenceIsTheTraceOfTheGradient();
            CurlAndPerAxisRangesAreCorrect();

            Console.WriteLine();
            if (Failures.Count > 0)
            {
                Console.WriteLine(Failures.Count + " check(s) FAILED:");
                foreach (string f in Failures) Console.WriteLine("  - " + f);
                return 1;
            }

            Console.WriteLine("All " + _passed + " DVF contract checks passed.");
            return 0;
        }

        // --- the cases ------------------------------------------------------------------

        /// <summary>
        /// The reader must reach a field nested one level down and report the grid it found,
        /// using the field's own spacing rather than anything else.
        /// </summary>
        private static void FieldIsReadThroughTheWrapper()
        {
            var registration = Wrap(Field(5, 6, 7, 0.98, 0.98, 5.0, (x, y, z) => Vec(1, 0, 0)));

            var log = new DiagnosticLog();
            DeformationFieldReader.Result field = DeformationFieldReader.TryRead(registration, log);

            Check("field is located through the wrapper property", field != null);
            if (field == null) return;

            Check("grid size is read from the field", field.XSize == 5 && field.YSize == 6 && field.ZSize == 7,
                field.XSize + "x" + field.YSize + "x" + field.ZSize);
            Check("grid spacing is the field's own, not the image's",
                Near(field.XResMm, 0.98) && Near(field.YResMm, 0.98) && Near(field.ZResMm, 5.0),
                field.XResMm + "/" + field.YResMm + "/" + field.ZResMm);
            Check("every vector was read back out of the buffer",
                Near(field.Vectors[2, 3, 4].X, 1.0) && Near(field.Vectors[2, 3, 4].Y, 0.0),
                field.Vectors[2, 3, 4].ToString());
        }

        /// <summary>
        /// A pure translation. The transform is x + u with u constant, so det(I + grad u) is
        /// exactly 1 and the gradient exactly 0. This is the case that fails loudly if the
        /// implementation ever computes det(grad u) instead: that would give 0, not 1.
        /// </summary>
        private static void UniformTranslation()
        {
            var registration = Wrap(Field(7, 7, 7, 2.0, 2.0, 2.0, (x, y, z) => Vec(3.0, -1.0, 2.0)));

            DeformationFieldMetrics.Result m = Measure(registration, "uniform translation");
            if (m == null) return;

            Check("translation: no folding", m.NegativeJacobianPercent == 0.0,
                m.NegativeJacobianPercent + "%");
            Check("translation: det(I + grad u) is 1", Near(m.MinJacobian, 1.0, 1e-9),
                m.MinJacobian.ToString("F12", CultureInfo.InvariantCulture));
            Check("translation: gradient is 0", m.MaxGradientMagnitude < 1e-9,
                m.MaxGradientMagnitude.ToString("E3", CultureInfo.InvariantCulture));
            Check("translation: max displacement is the shift magnitude",
                Near(m.MaxDisplacementMm, Math.Sqrt(14.0), 1e-6),
                m.MaxDisplacementMm.ToString("F6", CultureInfo.InvariantCulture));
            Check("Jacobian is evaluated over the interior only", m.JacobianSampleCount == 125,
                m.JacobianSampleCount + " samples");
        }

        /// <summary>
        /// u = k*x, so the transform is (1+k)x and det J = (1+k)^3 exactly.
        ///
        /// Checked at 1e-6 rather than the 1e-9 the other cases use, and the reason is worth
        /// recording: the API stores displacements as <c>VectorFloat</c>, whose components are
        /// 32-bit floats carrying about seven significant decimal digits. k = 0.10 has no exact
        /// binary representation, so storing k*x*spacing loses precision that the central
        /// difference then divides by the spacing — this case comes out around 2e-8 off, while
        /// the translation and fold cases match to 1e-9 because their values (3, -1, 2, and
        /// multiples of 2.0) are exactly representable.
        ///
        /// verify_math.py cannot see this at all: Python computes in float64 throughout, so it
        /// reports exact agreement for a field the real API could not store exactly. The limit
        /// is physically irrelevant — a Jacobian good to seven digits is far beyond what any
        /// tolerance in TG-132 asks — but a test that demanded more than the storage can carry
        /// would fail for a reason that has nothing to do with the registration.
        /// </summary>
        private static void UniformExpansion()
        {
            const double k = 0.10, sp = 2.0;
            const double float32Tolerance = 1e-6;

            var registration = Wrap(Field(7, 7, 7, sp, sp, sp,
                (x, y, z) => Vec(k * x * sp, k * y * sp, k * z * sp)));

            DeformationFieldMetrics.Result m = Measure(registration, "uniform expansion");
            if (m == null) return;

            Check("expansion: det J = (1+k)^3",
                Near(m.MinJacobian, Math.Pow(1.0 + k, 3), float32Tolerance),
                m.MinJacobian.ToString("F12", CultureInfo.InvariantCulture) + " vs " +
                Math.Pow(1.0 + k, 3).ToString("F12", CultureInfo.InvariantCulture));
            Check("expansion: no folding", m.NegativeJacobianPercent == 0.0);
            Check("expansion: gradient is k*sqrt(3)",
                Near(m.MaxGradientMagnitude, k * Math.Sqrt(3.0), float32Tolerance),
                m.MaxGradientMagnitude.ToString("F12", CultureInfo.InvariantCulture) + " vs " +
                (k * Math.Sqrt(3.0)).ToString("F12", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Pins the precision the float32 storage actually delivers, so a future change that
        /// quietly costs an order of magnitude is visible rather than absorbed by a loose
        /// tolerance. The expansion case above is the one that exercises inexact values.
        /// </summary>
        private static void Float32StoragePrecisionIsAsExpected()
        {
            const double k = 0.10, sp = 2.0;
            var registration = Wrap(Field(7, 7, 7, sp, sp, sp,
                (x, y, z) => Vec(k * x * sp, k * y * sp, k * z * sp)));

            DeformationFieldMetrics.Result m = Measure(registration, "float32 precision");
            if (m == null) return;

            double error = Math.Abs(m.MinJacobian - Math.Pow(1.0 + k, 3));

            Check("float32 storage costs no more than 1e-7 on the Jacobian", error < 1e-7,
                "error=" + error.ToString("E3", CultureInfo.InvariantCulture));
            Check("float32 storage costs at least something (the stub really is float32)",
                error > 0.0,
                "error=" + error.ToString("E3", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// u_x = -2x makes the transform x - 2x = -x, an inversion: det J = -1 everywhere. The
        /// go/no-go metric of TG-132 Table III has to catch this.
        /// </summary>
        private static void FoldingField()
        {
            const double sp = 2.0;
            var registration = Wrap(Field(7, 7, 7, sp, sp, sp,
                (x, y, z) => Vec(-2.0 * x * sp, 0.0, 0.0)));

            DeformationFieldMetrics.Result m = Measure(registration, "folding field");
            if (m == null) return;

            Check("fold is detected as negative Jacobian", m.NegativeJacobianPercent == 100.0,
                m.NegativeJacobianPercent + "% negative");
            Check("fold: det J = -1", Near(m.MinJacobian, -1.0, 1e-9),
                m.MinJacobian.ToString("F12", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The same displacement pattern read at two spacings must give gradients in the inverse
        /// ratio. If the image resolution were ever substituted for the field's, this is the
        /// check that would fail — and it is the failure that would otherwise look like a
        /// plausible Jacobian.
        /// </summary>
        private static void AnisotropicSpacingIsHonoured()
        {
            Func<int, int, int, StubVectorFloat> pattern = (x, y, z) => Vec(0.0, 0.0, 0.5 * z);

            DeformationFieldMetrics.Result fine =
                Measure(Wrap(Field(7, 7, 7, 1.0, 1.0, 1.0, pattern)), "fine spacing");
            DeformationFieldMetrics.Result coarse =
                Measure(Wrap(Field(7, 7, 7, 1.0, 1.0, 5.0, pattern)), "coarse spacing");

            if (fine == null || coarse == null) return;

            Check("gradient scales with the axis spacing actually used",
                Near(fine.MaxGradientMagnitude, 5.0 * coarse.MaxGradientMagnitude, 1e-9),
                "fine=" + fine.MaxGradientMagnitude.ToString("F6", CultureInfo.InvariantCulture) +
                " coarse=" + coarse.MaxGradientMagnitude.ToString("F6", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Two samples on an axis leave no interior, so no central difference exists. Compute
        /// must decline rather than invent a one-sided derivative — but the caller still has a
        /// maximum displacement, which needs no derivative at all.
        /// </summary>
        private static void GridTooThinForACentralDifference()
        {
            var registration = Wrap(Field(7, 7, 2, 1.0, 1.0, 1.0, (x, y, z) => Vec(0.0, 0.0, 4.0)));

            var log = new DiagnosticLog();
            DeformationFieldReader.Result field = DeformationFieldReader.TryRead(registration, log);
            Check("a two-deep field is still read", field != null);
            if (field == null) return;

            string problem;
            DeformationFieldMetrics.Result m = DeformationFieldMetrics.Compute(field, out problem);

            Check("no Jacobian is invented on a grid with no interior", m == null);
            Check("the reason names the grid", problem != null && problem.Contains("7x7x2"), problem);

            double maxDisplacement = 0.0;
            for (int z = 0; z < field.ZSize; z++)
                for (int y = 0; y < field.YSize; y++)
                    for (int x = 0; x < field.XSize; x++)
                        maxDisplacement = Math.Max(maxDisplacement, field.Vectors[x, y, z].Length);

            Check("max displacement survives a grid too thin for a derivative",
                Near(maxDisplacement, 4.0, 1e-9),
                maxDisplacement.ToString("F6", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A registration with no field must return null and log why, rather than falling back to
        /// anything. Substituting the linear component here would describe a different transform
        /// from the one under audit.
        /// </summary>
        private static void UnreadableFieldIsReportedNotGuessed()
        {
            var log = new DiagnosticLog();
            DeformationFieldReader.Result field =
                DeformationFieldReader.TryRead(new StubOpaqueRegistration(), log);

            Check("a registration with no field returns null", field == null);

            bool mentioned = false;
            foreach (DiagnosticEntry entry in log.Entries)
            {
                string text = entry.ToString();
                if (text.IndexOf("deformation field", StringComparison.OrdinalIgnoreCase) >= 0)
                    mentioned = true;
            }

            Check("the failure is written to the diagnostics", mentioned);
        }

        /// <summary>
        /// A null field object must not be mistaken for a readable one. This is the shape the API
        /// takes for a registration that has a DeformationField property that simply is not
        /// populated.
        /// </summary>
        private static void RigidCaseDoesNotClaimAGradient()
        {
            var registration = new StubMirsNonRigidRegistration
            {
                NonRigidRegistration = new StubNonRigidRegistration { DeformationField = null }
            };

            var log = new DiagnosticLog();
            DeformationFieldReader.Result field = DeformationFieldReader.TryRead(registration, log);

            Check("a present-but-null field is not treated as readable", field == null);
        }

        /// <summary>
        /// A regression tripwire, not a benchmark.
        ///
        /// The first version of the reader took 5.0 s on this grid — the real 190x206x39 the probe
        /// reported — because it read every element through Array.GetValue plus three
        /// PropertyInfo.GetValue calls, boxing four times per element across 1.5 million elements.
        /// Typed access through a generic method with compiled component accessors brought it to
        /// 0.28 s. The plugin runs this on the UI thread, so five seconds is the difference between
        /// a pause and an apparent hang.
        ///
        /// The limit is deliberately loose: it has to hold on a busy planning workstation, not
        /// measure anything. It is set to catch a return to the boxing version, which would blow
        /// through it by a factor of two even on slow hardware.
        /// </summary>
        private static void RealSizedGridDoesNotTakeSeconds()
        {
            var registration = Wrap(Field(190, 206, 39, 0.975, 0.975, 5.0,
                (x, y, z) => Vec(0.01 * x, 0.01 * y, 0.01 * z)));

            var log = new DiagnosticLog();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            DeformationFieldReader.Result field = DeformationFieldReader.TryRead(registration, log);
            watch.Stop();

            Check("the real-sized grid is read", field != null);
            if (field == null) return;

            Check("reading a 190x206x39 field stays under 2.5 s",
                watch.ElapsedMilliseconds < 2500,
                watch.ElapsedMilliseconds + " ms");
            Check("all 1,526,460 vectors are present",
                field.XSize * field.YSize * field.ZSize == 1526460);
        }

        /// <summary>
        /// The bar must not go backwards, must not exceed its total, and must reach it. A stage
        /// left out of MeasurementProgress's ordered list would silently report position 0, so a
        /// bar that stalls at the start is the symptom of a missing entry rather than a slow read.
        /// </summary>
        private static void ProgressStagesAreOrderedAndComplete()
        {
            var sink = new RecordingProgressSink();
            var progress = new MeasurementProgress(sink);

            progress.Report(MeasurementStage.Starting);
            foreach (MeasurementStage stage in MeasurementProgress.AllStages)
                progress.Report(stage);
            progress.Report(MeasurementStage.Finished);

            Check("every stage reports", sink.Reports.Count == MeasurementProgress.StageCount + 2,
                sink.Reports.Count + " reports");

            bool monotonic = true;
            for (int i = 1; i < sink.Reports.Count; i++)
                if (sink.Reports[i].Completed < sink.Reports[i - 1].Completed) monotonic = false;

            Check("progress never goes backwards", monotonic);

            bool withinBounds = true;
            foreach (RecordedReport r in sink.Reports)
                if (r.Completed < 0 || r.Completed > r.Total) withinBounds = false;

            Check("progress never exceeds its total", withinBounds);

            Check("progress starts at zero", sink.Reports[0].Completed == 0);
            Check("progress reaches its total",
                sink.Reports[sink.Reports.Count - 1].Completed == MeasurementProgress.StageCount,
                sink.Reports[sink.Reports.Count - 1].Completed + "/" + MeasurementProgress.StageCount);

            // A stage missing from the ordered list falls through to 0. Two stages reporting the
            // same position is the signature.
            var seen = new HashSet<int>();
            bool distinct = true;
            foreach (MeasurementStage stage in MeasurementProgress.AllStages)
            {
                var probe = new RecordingProgressSink();
                new MeasurementProgress(probe).Report(stage);
                if (!seen.Add(probe.Reports[0].Completed)) distinct = false;
            }

            Check("each stage maps to its own position", distinct);

            bool described = true;
            foreach (RecordedReport r in sink.Reports)
                if (string.IsNullOrEmpty(r.Description)) described = false;

            Check("every stage carries a caption", described);
        }

        /// <summary>
        /// The audit is the deliverable and the progress numbers are a courtesy. A sink that
        /// throws — a UI already torn down, a dispatcher shutting down — must not take the
        /// measurement with it.
        /// </summary>
        private static void AFailingProgressSinkDoesNotAbortTheMeasurement()
        {
            var progress = new MeasurementProgress(new ThrowingProgressSink());

            bool survived = true;
            try
            {
                progress.Report(MeasurementStage.LoadingSourceVolume);
            }
            catch
            {
                survived = false;
            }

            Check("a throwing progress sink is swallowed", survived);
        }

        /// <summary>
        /// The deformable transform is Pre ∘ field ∘ Post, so det J_total = det(Pre) ·
        /// det(I + grad u) · det(Post). Reporting the field's determinant as the registration's
        /// is only honest when both affine parts are rigid, and a reflection in either would
        /// invert the sign of every point — turning folding into no folding.
        /// </summary>
        private static void AffineDeterminantsAreReadAndJudged()
        {
            StubVectorField field = Field(5, 5, 5, 1.0, 1.0, 1.0, (x, y, z) => Vec(0, 0, 0));

            var identity = new StubMirsNonRigidRegistration
            {
                NonRigidRegistration = new StubNonRigidRegistration { DeformationField = field }
            };

            DeformationFieldReader.Result read =
                DeformationFieldReader.TryRead(identity, new DiagnosticLog());

            Check("affine determinants are read", read != null &&
                read.PreDeterminant.HasValue && read.PostDeterminant.HasValue);
            if (read == null || !read.PreDeterminant.HasValue) return;

            Check("identity affine parts read as determinant 1",
                Near(read.PreDeterminant.Value, 1.0) && Near(read.PostDeterminant.Value, 1.0),
                read.PreDeterminant.Value + " / " + read.PostDeterminant.Value);
            Check("identity affine parts are judged rigid", read.AffinePartsAreRigid);

            // A 2x scaling has determinant 8, so the registration's Jacobian is eight times the
            // field's — the folding percentage would be unchanged in sign but the magnitude wrong.
            var scaled = new StubMirsNonRigidRegistration
            {
                NonRigidRegistration = new StubNonRigidRegistration
                {
                    DeformationField = field,
                    PreTransformationMatrix = StubNonRigidRegistration.Scaling(2.0)
                }
            };

            DeformationFieldReader.Result scaledRead =
                DeformationFieldReader.TryRead(scaled, new DiagnosticLog());

            Check("a scaling affine part is detected", scaledRead != null &&
                Near(scaledRead.PreDeterminant.Value, 8.0),
                scaledRead == null ? "null" : scaledRead.PreDeterminant.Value.ToString());
            Check("a scaling affine part is not judged rigid",
                scaledRead != null && !scaledRead.AffinePartsAreRigid);

            // The dangerous one: a reflection flips the sign of every determinant, so folding and
            // no folding swap places.
            var reflected = new StubMirsNonRigidRegistration
            {
                NonRigidRegistration = new StubNonRigidRegistration
                {
                    DeformationField = field,
                    PostTransformationMatrix = StubNonRigidRegistration.Reflection()
                }
            };

            DeformationFieldReader.Result reflectedRead =
                DeformationFieldReader.TryRead(reflected, new DiagnosticLog());

            Check("a reflecting affine part is detected as negative",
                reflectedRead != null && reflectedRead.PostDeterminant.Value < 0,
                reflectedRead == null ? "null" : reflectedRead.PostDeterminant.Value.ToString());
            Check("a reflecting affine part is not judged rigid",
                reflectedRead != null && !reflectedRead.AffinePartsAreRigid);
            Check("the correction factor is reported",
                reflectedRead != null && Near(reflectedRead.AffineDeterminantProduct.Value, -1.0),
                reflectedRead == null ? "null" : reflectedRead.AffineDeterminantProduct.ToString());
        }

        /// <summary>
        /// TG-132 asks for the influence of folding on the intended use to be evaluated, not for
        /// any non-zero value to disqualify outright. That judgement needs to know where the
        /// folding is: against the edge of the field's support, where the algorithm had no image
        /// to work from, or in the middle of the anatomy.
        /// </summary>
        private static void FoldingIsLocatedNotJustCounted()
        {
            const int n = 21;
            const double sp = 1.0;

            // Folding confined to a slab at the low-x edge: u_x = -2x there, identity elsewhere.
            var atEdge = Wrap(Field(n, n, n, sp, sp, sp,
                (x, y, z) => x <= 2 ? Vec(-2.0 * x * sp, 0, 0) : Vec(0, 0, 0)));

            DeformationFieldMetrics.Result edge = Measure(atEdge, "folding at the edge");
            if (edge == null) return;

            Check("edge folding is detected at all", edge.NegativeJacobianPercent > 0.0,
                edge.NegativeJacobianPercent + "%");
            Check("edge folding is reported as being at the edge",
                edge.NegativeBoundaryFraction >= 0.95,
                edge.NegativeBoundaryFraction.ToString("P0"));

            // Folding in a slab through the centre instead.
            int mid = n / 2;
            var atCentre = Wrap(Field(n, n, n, sp, sp, sp,
                (x, y, z) => Math.Abs(x - mid) <= 1 ? Vec(-2.0 * x * sp, 0, 0) : Vec(0, 0, 0)));

            DeformationFieldMetrics.Result centre = Measure(atCentre, "folding at the centre");
            if (centre == null) return;

            Check("central folding is detected at all", centre.NegativeJacobianPercent > 0.0,
                centre.NegativeJacobianPercent + "%");
            Check("central folding is not attributed to the edge",
                centre.NegativeBoundaryFraction < 0.5,
                centre.NegativeBoundaryFraction.ToString("P0"));
            Check("the folded region is bounded away from the field edges in x",
                centre.NegativeBoundsMin != null && centre.NegativeBoundsMin[0] > 2 &&
                centre.NegativeBoundsMax[0] < n - 3,
                centre.NegativeBoundsMin == null
                    ? "null"
                    : "x " + centre.NegativeBoundsMin[0] + "-" + centre.NegativeBoundsMax[0]);

            // A field with no folding must report no location rather than an empty box.
            DeformationFieldMetrics.Result clean = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(1, 0, 0))), "clean field");
            if (clean == null) return;

            Check("a field with no folding reports no folded region",
                clean.NegativeBoundsMin == null && clean.NegativeNearBoundary == 0);
            Check("a field with no folding reports a boundary fraction of zero",
                clean.NegativeBoundaryFraction == 0.0);
        }

        /// <summary>
        /// TG-132 Table III's Jacobian row has two clauses. The first — no negative values — is
        /// gated at 0 %. The second constrains how far the determinant departs from 1 relative to
        /// the volume change expected for the structure, and this measures it.
        ///
        /// The percentile basis is the point of the last case here: min and max are controlled by
        /// whichever single voxel is worst, usually at the edge of the field's support, so a
        /// metric built on them would report a wild departure for a deformation that is uniform
        /// everywhere it matters.
        /// </summary>
        private static void JacobianDepartureFollowsTableIIISecondClause()
        {
            const int n = 21;
            const double sp = 1.0;

            // A pure translation preserves volume everywhere: J = 1, so the departure is 0.
            DeformationFieldMetrics.Result rigidLike = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(3, -1, 2))), "volume-preserving");
            if (rigidLike == null) return;

            Check("a volume-preserving field departs from 1 by 0",
                Near(rigidLike.MaxDepartureFromOne, 0.0, 1e-9),
                rigidLike.MaxDepartureFromOne.ToString("E3", CultureInfo.InvariantCulture));
            Check("a volume-preserving field has median Jacobian 1",
                Near(rigidLike.JacobianMedian, 1.0, 1e-9),
                rigidLike.JacobianMedian.ToString("F9", CultureInfo.InvariantCulture));

            // Uniform expansion by k: J = (1+k)^3 everywhere, so every percentile is that value
            // and the departure is (1+k)^3 - 1. Expansion, so above 1 — the sign Table III cares
            // about for a structure expected to grow.
            const double k = 0.10;
            DeformationFieldMetrics.Result expand = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(k * x * sp, k * y * sp, k * z * sp))),
                "uniform expansion");
            if (expand == null) return;

            double expected = Math.Pow(1.0 + k, 3);
            Check("uniform expansion reports its determinant at every percentile",
                Near(expand.JacobianP1, expected, 1e-6) &&
                Near(expand.JacobianMedian, expected, 1e-6) &&
                Near(expand.JacobianP99, expected, 1e-6),
                expand.JacobianP1.ToString("F6", CultureInfo.InvariantCulture) + " / " +
                expand.JacobianMedian.ToString("F6", CultureInfo.InvariantCulture) + " / " +
                expand.JacobianP99.ToString("F6", CultureInfo.InvariantCulture));
            Check("uniform expansion departs from 1 by (1+k)^3 - 1",
                Near(expand.MaxDepartureFromOne, expected - 1.0, 1e-6),
                expand.MaxDepartureFromOne.ToString("F6", CultureInfo.InvariantCulture));
            Check("expansion is above 1, the side Table III ties to expected growth",
                expand.JacobianMedian > 1.0);

            // Compression: below 1, the side Table III ties to expected volume reduction.
            DeformationFieldMetrics.Result shrink = Measure(
                Wrap(Field(n, n, n, sp, sp, sp,
                    (x, y, z) => Vec(-k * x * sp, -k * y * sp, -k * z * sp))), "uniform compression");
            if (shrink == null) return;

            Check("compression is below 1, the side Table III ties to expected reduction",
                shrink.JacobianMedian < 1.0,
                shrink.JacobianMedian.ToString("F6", CultureInfo.InvariantCulture));

            // The case that justifies percentiles. One small corner distorts violently; the rest
            // of the field is a plain translation. min/max would report the corner; p1/p99 should
            // report the field.
            var spike = Wrap(Field(n, n, n, sp, sp, sp,
                (x, y, z) => (x <= 1 && y <= 1 && z <= 1) ? Vec(-4.0 * x * sp, 0, 0) : Vec(3, -1, 2)));

            DeformationFieldMetrics.Result spiked = Measure(spike, "localised spike");
            if (spiked == null) return;

            Check("a localised spike still moves the full range",
                spiked.MinJacobian < 0.5 || spiked.MaxJacobian > 1.5,
                "min " + spiked.MinJacobian.ToString("F3", CultureInfo.InvariantCulture) +
                ", max " + spiked.MaxJacobian.ToString("F3", CultureInfo.InvariantCulture));
            Check("a localised spike does not dominate the percentile departure",
                spiked.MaxDepartureFromOne < 0.05,
                spiked.MaxDepartureFromOne.ToString("F6", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// div u = trace(grad u), the quantity Eclipse displays beside the Jacobian. Verified
        /// against fields whose divergence is known in closed form, so that the comparison
        /// against Eclipse's own view tests the field read rather than this arithmetic.
        /// </summary>
        private static void DivergenceIsTheTraceOfTheGradient()
        {
            const int n = 21;
            const double sp = 2.0;

            // A pure translation has no gradient at all, so no divergence.
            DeformationFieldMetrics.Result shift = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(3, -1, 2))), "translation divergence");
            if (shift == null) return;

            Check("a pure translation has zero divergence",
                Math.Abs(shift.MinDivergence) < 1e-9 && Math.Abs(shift.MaxDivergence) < 1e-9,
                shift.MinDivergence.ToString("E3", CultureInfo.InvariantCulture) + " to " +
                shift.MaxDivergence.ToString("E3", CultureInfo.InvariantCulture));

            // u = k*x isotropically: each diagonal term is k, so div u = 3k exactly.
            const double k = 0.10;
            DeformationFieldMetrics.Result expand = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(k * x * sp, k * y * sp, k * z * sp))),
                "expansion divergence");
            if (expand == null) return;

            Check("uniform expansion has divergence 3k",
                Near(expand.MinDivergence, 3.0 * k, 1e-6) && Near(expand.MaxDivergence, 3.0 * k, 1e-6),
                expand.MinDivergence.ToString("F6", CultureInfo.InvariantCulture));

            // u_x = -2x and nothing else: div u = -2.
            DeformationFieldMetrics.Result fold = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(-2.0 * x * sp, 0, 0))),
                "fold divergence");
            if (fold == null) return;

            Check("a single-axis inversion has divergence -2",
                Near(fold.MinDivergence, -2.0, 1e-6) && Near(fold.MaxDivergence, -2.0, 1e-6),
                fold.MinDivergence.ToString("F6", CultureInfo.InvariantCulture));

            // Compression and expansion must land on opposite sides of zero, which is the sign
            // convention the Eclipse comparison depends on.
            DeformationFieldMetrics.Result shrink = Measure(
                Wrap(Field(n, n, n, sp, sp, sp,
                    (x, y, z) => Vec(-k * x * sp, -k * y * sp, -k * z * sp))), "compression divergence");
            if (shrink == null) return;

            Check("compression is negative divergence, expansion positive",
                shrink.MaxDivergence < 0 && expand.MinDivergence > 0,
                shrink.MaxDivergence.ToString("F4", CultureInfo.InvariantCulture) + " vs " +
                expand.MinDivergence.ToString("F4", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Curl is the rotational part of the gradient the divergence takes the trace of, and the
        /// per-axis ranges are what would settle whether this code's X/Y/Z are Eclipse's.
        /// </summary>
        private static void CurlAndPerAxisRangesAreCorrect()
        {
            const int n = 21;
            const double sp = 2.0;

            // A pure translation and an isotropic expansion are both irrotational: curl is zero.
            DeformationFieldMetrics.Result shift = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(3, -1, 2))), "translation curl");
            if (shift == null) return;

            Check("a pure translation has zero curl", shift.MaxCurlMagnitude < 1e-9,
                shift.MaxCurlMagnitude.ToString("E3", CultureInfo.InvariantCulture));

            DeformationFieldMetrics.Result expand = Measure(
                Wrap(Field(n, n, n, sp, sp, sp,
                    (x, y, z) => Vec(0.1 * x * sp, 0.1 * y * sp, 0.1 * z * sp))), "expansion curl");
            if (expand == null) return;

            Check("an isotropic expansion has zero curl", expand.MaxCurlMagnitude < 1e-6,
                expand.MaxCurlMagnitude.ToString("E3", CultureInfo.InvariantCulture));

            // A shear u = (a*y, 0, 0) has curl_z = d(u_y)/dx - d(u_x)/dy = -a, so |curl| = |a|,
            // while its divergence is 0 — the case that separates the two quantities.
            const double a = 0.25;
            DeformationFieldMetrics.Result shear = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(a * y * sp, 0, 0))), "shear curl");
            if (shear == null) return;

            Check("a shear has curl equal to the shear rate",
                Near(shear.MaxCurlMagnitude, a, 1e-6),
                shear.MaxCurlMagnitude.ToString("F6", CultureInfo.InvariantCulture));
            Check("a shear has zero divergence, unlike its curl",
                Math.Abs(shear.MinDivergence) < 1e-9 && Math.Abs(shear.MaxDivergence) < 1e-9,
                shear.MaxDivergence.ToString("E3", CultureInfo.InvariantCulture));

            // Per-axis ranges: a field that moves only along Z must show a range on Z and none on
            // X or Y. This is the check that would catch a transposed component ordering.
            DeformationFieldMetrics.Result zOnly = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(0, 0, 5.0))), "z-only displacement");
            if (zOnly == null) return;

            Check("a Z-only field reports its range on Z",
                Near(zOnly.MinDisplacementPerAxis[2], 5.0, 1e-6) &&
                Near(zOnly.MaxDisplacementPerAxis[2], 5.0, 1e-6),
                zOnly.MinDisplacementPerAxis[2].ToString("F3", CultureInfo.InvariantCulture));
            Check("a Z-only field reports nothing on X or Y",
                Near(zOnly.MaxDisplacementPerAxis[0], 0.0, 1e-9) &&
                Near(zOnly.MaxDisplacementPerAxis[1], 0.0, 1e-9));

            // And the components must not be transposed among themselves.
            DeformationFieldMetrics.Result distinct = Measure(
                Wrap(Field(n, n, n, sp, sp, sp, (x, y, z) => Vec(1.0, 2.0, 3.0))), "distinct axes");
            if (distinct == null) return;

            Check("each axis reports its own component, untransposed",
                Near(distinct.MaxDisplacementPerAxis[0], 1.0, 1e-6) &&
                Near(distinct.MaxDisplacementPerAxis[1], 2.0, 1e-6) &&
                Near(distinct.MaxDisplacementPerAxis[2], 3.0, 1e-6),
                string.Join(" / ", distinct.MaxDisplacementPerAxis
                    .Select(v => v.ToString("F2", CultureInfo.InvariantCulture)).ToArray()));
        }

        private struct RecordedReport
        {
            public int Completed;
            public int Total;
            public string Description;
        }

        private sealed class RecordingProgressSink : IMeasurementProgressSink
        {
            public readonly List<RecordedReport> Reports = new List<RecordedReport>();

            public void Report(MeasurementStage stage, int completed, int total, string description)
            {
                Reports.Add(new RecordedReport
                {
                    Completed = completed,
                    Total = total,
                    Description = description
                });
            }
        }

        private sealed class ThrowingProgressSink : IMeasurementProgressSink
        {
            public void Report(MeasurementStage stage, int completed, int total, string description)
            {
                throw new InvalidOperationException("the window is gone");
            }
        }

        // --- plumbing -------------------------------------------------------------------

        private static DeformationFieldMetrics.Result Measure(object registration, string label)
        {
            var log = new DiagnosticLog();
            DeformationFieldReader.Result field = DeformationFieldReader.TryRead(registration, log);

            if (field == null)
            {
                Check(label + ": field was read", false, "TryRead returned null");
                return null;
            }

            string problem;
            DeformationFieldMetrics.Result metrics = DeformationFieldMetrics.Compute(field, out problem);

            if (metrics == null)
            {
                Check(label + ": metrics were computed", false, problem);
                return null;
            }

            return metrics;
        }

        private static StubVectorField Field(int nx, int ny, int nz, double rx, double ry, double rz,
            Func<int, int, int, StubVectorFloat> generator)
        {
            return new StubVectorField(nx, ny, nz, rx, ry, rz, generator);
        }

        private static StubMirsNonRigidRegistration Wrap(StubVectorField field)
        {
            return new StubMirsNonRigidRegistration
            {
                NonRigidRegistration = new StubNonRigidRegistration { DeformationField = field }
            };
        }

        private static StubVectorFloat Vec(double x, double y, double z)
        {
            return new StubVectorFloat { X = (float)x, Y = (float)y, Z = (float)z };
        }

        private static bool Near(double a, double b, double tolerance = 1e-6)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        private static void Check(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  OK     " + name);
                return;
            }

            Failures.Add(name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            Console.WriteLine("  FAILED " + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
        }
    }
}
