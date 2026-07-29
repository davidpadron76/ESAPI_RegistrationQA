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
