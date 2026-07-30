using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Reads the deformation vector field itself, not just a point-by-point mapping through it.
    ///
    /// <see cref="PointMapperReader"/> answers "can a point be pushed through this
    /// registration" and stops there — it never asks whether the field behind that mapping can
    /// be read wholesale. A probe against a real deformable case (VMS.IRS.Scripting) found
    /// <c>NonRigidRegistration.DeformationField</c>, a <c>VectorField</c> object exposing its
    /// own grid (<c>XSize</c>/<c>YSize</c>/<c>ZSize</c>, <c>XRes</c>/<c>YRes</c>/<c>ZRes</c> in
    /// millimetres) and <c>GetVectors(VectorFloat[,,] preallocatedBuffer)</c>, which the probe
    /// called on real data: non-uniform displacement vectors, 0.02-9.53 mm in the sampled
    /// plane, not an untouched buffer.
    ///
    /// That unlocks the three deformation metrics TG-132 Table III lists that
    /// <see cref="RegistrationAnalyzer"/> has always reported as unobtainable for a deformable
    /// case: Jacobian, DVF smoothness and maximum displacement all become computable once the
    /// field itself — not just the linear component, which describes a different transform —
    /// can be read voxel by voxel. See <see cref="DeformationFieldMetrics"/> for what is
    /// computed from the grid this class returns.
    ///
    /// The grid's own spacing does not match the image's: on the case the probe ran against it
    /// was 190x206x39 at ~0.975x0.975x5 mm, against a 512x512x458 image at ~0.45x0.45x0.4 mm.
    /// Every computation here must use the field's own resolution, never the image's.
    /// </summary>
    public static class DeformationFieldReader
    {
        public sealed class Result
        {
            public int XSize;
            public int YSize;
            public int ZSize;

            /// <summary>Grid spacing in millimetres, native to the field — not the image.</summary>
            public double XResMm;
            public double YResMm;
            public double ZResMm;

            /// <summary>Displacement in millimetres, indexed [x, y, z] on the field's own grid.</summary>
            public Vec3[,,] Vectors;

            /// <summary>
            /// Determinants of the linear part of the affine transforms the field is composed
            /// with, when the API exposes them. Null when the property was absent or unreadable.
            ///
            /// These decide whether the Jacobian computed from the field alone is the Jacobian of
            /// the registration. The deformable transform is the composition of
            /// <c>PreTransformationMatrix</c>, the field, and <c>PostTransformationMatrix</c>, so
            /// det J_total = det(Pre) · det(I + grad u) · det(Post). When both are rigid — det
            /// exactly +1, no scaling and no reflection — the field's own determinant *is* the
            /// registration's, and the folding percentage stands. A negative determinant on
            /// either would flip the sign of every point, turning folding into no folding and
            /// back; a scaling factor would rescale all of them.
            /// </summary>
            public double? PreDeterminant;
            public double? PostDeterminant;

            /// <summary>
            /// True when both affine parts were read and both are orientation-preserving with
            /// unit volume, so the field's Jacobian can be reported as the registration's without
            /// qualification.
            /// </summary>
            public bool AffinePartsAreRigid
            {
                get
                {
                    if (!PreDeterminant.HasValue || !PostDeterminant.HasValue) return false;
                    return Math.Abs(PreDeterminant.Value - 1.0) < 1e-6
                        && Math.Abs(PostDeterminant.Value - 1.0) < 1e-6;
                }
            }

            /// <summary>
            /// What the field's determinant must be multiplied by to become the registration's,
            /// or null when the affine parts could not be read.
            /// </summary>
            public double? AffineDeterminantProduct
            {
                get
                {
                    if (!PreDeterminant.HasValue || !PostDeterminant.HasValue) return null;
                    return PreDeterminant.Value * PostDeterminant.Value;
                }
            }
        }

        /// <summary>
        /// Same order <see cref="PointMapperReader"/> tries its wrapper properties, minus the
        /// two names that would themselves be the field rather than a holder of one. Confirmed
        /// on VMS.IRS.Scripting: <c>registration.NonRigidRegistration.DeformationField</c>.
        /// </summary>
        private static readonly string[] CandidateHolders =
        {
            null, // the registration object may expose DeformationField directly
            "NonRigidRegistration",
            "RigidRegistration",
            "DeformableRegistration",
            "DeformableRegistrationField",
            "Registration",
            "SpatialRegistration"
        };

        public static Result TryRead(object registration, DiagnosticLog log)
        {
            if (registration == null) return null;

            foreach (string holderName in CandidateHolders)
            {
                object holder = registration;

                if (holderName != null)
                {
                    object candidate;
                    if (!TryReadProperty(registration, holderName, out candidate) || candidate == null)
                        continue;
                    holder = candidate;
                }

                object field;
                if (!TryReadProperty(holder, "DeformationField", out field) || field == null) continue;

                string problem;
                Result result = TryBuild(field, out problem);

                string label = (holderName ?? "registration") + ".DeformationField";

                if (result != null)
                {
                    // Read from the holder, not the field: the affine parts sit alongside
                    // DeformationField on NonRigidRegistration, not inside it.
                    result.PreDeterminant = TryReadLinearDeterminant(holder, "PreTransformationMatrix");
                    result.PostDeterminant = TryReadLinearDeterminant(holder, "PostTransformationMatrix");

                    if (log != null)
                    {
                        log.Info("deformation field",
                            "read from " + label + " (" + result.XSize + "x" + result.YSize + "x" +
                            result.ZSize + " grid, " +
                            FormatMm(result.XResMm) + "x" + FormatMm(result.YResMm) + "x" +
                            FormatMm(result.ZResMm) + " mm spacing, native to the field, not the image)");

                        ReportAffineParts(result, log);
                    }
                    return result;
                }

                if (log != null)
                    log.Warning("deformation field: " + label, problem ?? "could not be read");
            }

            if (log != null)
            {
                log.Info("deformation field",
                    "no DeformationField property answered on the registration or its wrapper objects. " +
                    MatrixReader.DescribeMemberSurface(registration));
            }

            return null;
        }

        private static Result TryBuild(object field, out string problem)
        {
            problem = null;
            Type type = field.GetType();

            int xSize, ySize, zSize;
            double xRes, yRes, zRes;

            if (!TryReadInt(field, "XSize", out xSize) || !TryReadInt(field, "YSize", out ySize) ||
                !TryReadInt(field, "ZSize", out zSize) ||
                !TryReadDouble(field, "XRes", out xRes) || !TryReadDouble(field, "YRes", out yRes) ||
                !TryReadDouble(field, "ZRes", out zRes))
            {
                problem = "XSize/YSize/ZSize/XRes/YRes/ZRes did not all answer with usable values";
                return null;
            }

            if (xSize <= 0 || ySize <= 0 || zSize <= 0 || xRes <= 0 || yRes <= 0 || zRes <= 0)
            {
                problem = "the grid size or spacing was not positive";
                return null;
            }

            MethodInfo method;
            try
            {
                method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.ReturnType == typeof(void) &&
                                          m.GetParameters().Length == 1 &&
                                          m.GetParameters()[0].ParameterType.IsArray &&
                                          m.GetParameters()[0].ParameterType.GetArrayRank() == 3);
            }
            catch (Exception ex)
            {
                problem = "method search failed — " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            if (method == null)
            {
                problem = "no method taking a single 3D-array buffer (the GetVectors shape) was found";
                return null;
            }

            Type elementType = method.GetParameters()[0].ParameterType.GetElementType();

            Array buffer;
            try
            {
                buffer = Array.CreateInstance(elementType, xSize, ySize, zSize);
            }
            catch (Exception ex)
            {
                problem = "could not allocate the buffer — " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            try
            {
                method.Invoke(field, new object[] { buffer });
            }
            catch (Exception ex)
            {
                Exception real = ex.InnerException ?? ex;
                problem = method.Name + " → " + real.GetType().Name + ": " + real.Message;
                return null;
            }

            Vec3[,,] vectors = ConvertBuffer(buffer, elementType, xSize, ySize, zSize, out problem);
            if (vectors == null) return null;

            return new Result
            {
                XSize = xSize,
                YSize = ySize,
                ZSize = zSize,
                XResMm = xRes,
                YResMm = yRes,
                ZResMm = zRes,
                Vectors = vectors
            };
        }

        private static string FormatMm(double value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Says whether the Jacobian computed from the field alone can be reported as the
        /// registration's, and if not, by what factor it differs.
        /// </summary>
        private static void ReportAffineParts(Result result, DiagnosticLog log)
        {
            if (!result.PreDeterminant.HasValue || !result.PostDeterminant.HasValue)
            {
                log.Warning("deformation field: affine parts",
                    "Pre/PostTransformationMatrix could not both be read, so it cannot be confirmed " +
                    "that the field's Jacobian is the registration's. The deformable transform is " +
                    "the composition of the two affine parts with the field, and a scaling or " +
                    "reflection in either would rescale or invert every determinant.");
                return;
            }

            string values = string.Format(CultureInfo.InvariantCulture,
                "det(Pre) = {0:F6}, det(Post) = {1:F6}",
                result.PreDeterminant.Value, result.PostDeterminant.Value);

            if (result.AffinePartsAreRigid)
            {
                log.Info("deformation field: affine parts",
                    values + " — both are orientation-preserving with unit volume, so the field's " +
                    "own Jacobian is the registration's and the folding percentage needs no " +
                    "correction.");
                return;
            }

            log.Warning("deformation field: affine parts",
                values + " — at least one is not a unit-determinant rigid transform, so the " +
                "registration's Jacobian is the field's multiplied by " +
                result.AffineDeterminantProduct.Value.ToString("F6", CultureInfo.InvariantCulture) +
                ". A negative product would invert the sign of every point, so the reported " +
                "percentage of folding is not trustworthy until this is accounted for.");
        }

        /// <summary>
        /// Determinant of the top-left 3x3 of a 4x4 affine matrix — its linear part.
        ///
        /// The row-major/column-major question that <see cref="MatrixReader"/> has to settle does
        /// not arise here: transposing a matrix leaves its determinant unchanged, and the linear
        /// block is the top-left 3x3 under either convention.
        /// </summary>
        private static double? TryReadLinearDeterminant(object holder, string propertyName)
        {
            object raw;
            if (!TryReadProperty(holder, propertyName, out raw) || raw == null) return null;

            var matrix = raw as Array;
            if (matrix == null || matrix.Rank != 2) return null;
            if (matrix.GetLength(0) < 3 || matrix.GetLength(1) < 3) return null;

            var m = new double[3, 3];
            try
            {
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                        m[r, c] = Convert.ToDouble(matrix.GetValue(r, c), CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }

            double determinant =
                m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
              - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
              + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

            return double.IsNaN(determinant) || double.IsInfinity(determinant) ? (double?)null : determinant;
        }

        // ------------------------------------------------------------------ buffer conversion
        //
        // The naive version of this — Array.GetValue(x, y, z) followed by three
        // PropertyInfo.GetValue calls — took 5.0 s on the 190x206x39 grid the probe reported,
        // twelve times longer than computing every metric from the result. All of it was
        // reflection overhead and boxing, once per element, 1.5 million times over: GetValue on
        // a multidimensional array boxes the struct it returns, and each PropertyInfo.GetValue
        // boxes the float it reads.
        //
        // Both disappear if the loop runs against a strongly-typed array. The element type is
        // only known at runtime, so the typing is recovered by invoking a generic method through
        // reflection exactly once, and the component reads are compiled to delegates once as
        // well. Everything inside the loop is then ordinary typed code.
        //
        // Measured on the same grid: 5043 ms -> see tools/run_checks.sh, which pins it.

        private static readonly MethodInfo ConvertTypedMethod = typeof(DeformationFieldReader)
            .GetMethod("ConvertTyped", BindingFlags.NonPublic | BindingFlags.Static);

        private static Vec3[,,] ConvertBuffer(
            Array buffer, Type elementType, int xSize, int ySize, int zSize, out string problem)
        {
            problem = null;

            Delegate rx = BuildComponentAccessor(elementType, "X", "x");
            Delegate ry = BuildComponentAccessor(elementType, "Y", "y");
            Delegate rz = BuildComponentAccessor(elementType, "Z", "z");

            if (rx == null || ry == null || rz == null)
            {
                problem = elementType.Name + " exposes no recognisable X/Y/Z members";
                return null;
            }

            if (ConvertTypedMethod == null)
            {
                problem = "the internal buffer converter could not be located";
                return null;
            }

            try
            {
                return (Vec3[,,])ConvertTypedMethod
                    .MakeGenericMethod(elementType)
                    .Invoke(null, new object[] { buffer, xSize, ySize, zSize, rx, ry, rz });
            }
            catch (Exception ex)
            {
                Exception real = ex.InnerException ?? ex;
                problem = "the buffer could not be read back as X/Y/Z — " +
                          real.GetType().Name + ": " + real.Message;
                return null;
            }
        }

        /// <summary>
        /// Invoked through reflection once, so that <typeparamref name="T"/> is a real type for
        /// the duration of the loop and neither the array access nor the component reads box.
        /// </summary>
        private static Vec3[,,] ConvertTyped<T>(
            Array buffer, int xSize, int ySize, int zSize,
            Func<T, double> readX, Func<T, double> readY, Func<T, double> readZ)
        {
            var typed = (T[,,])buffer;
            var vectors = new Vec3[xSize, ySize, zSize];

            for (int z = 0; z < zSize; z++)
            {
                for (int y = 0; y < ySize; y++)
                {
                    for (int x = 0; x < xSize; x++)
                    {
                        T element = typed[x, y, z];
                        vectors[x, y, z] = new Vec3(readX(element), readY(element), readZ(element));
                    }
                }
            }

            return vectors;
        }

        /// <summary>
        /// Compiles a <c>Func&lt;TElement, double&gt;</c> reading one component, as a property or
        /// a field, in either casing. The conversion to double is part of the compiled
        /// expression: the API's component type is float, and converting per element through
        /// <see cref="Convert.ToDouble(object)"/> would box it again.
        /// </summary>
        private static Delegate BuildComponentAccessor(Type elementType, params string[] names)
        {
            foreach (string name in names)
            {
                MemberInfo member = SafeGetProperty(elementType, name);
                if (member == null) member = SafeGetField(elementType, name);
                if (member == null) continue;

                try
                {
                    ParameterExpression parameter = Expression.Parameter(elementType, "v");
                    Expression access = Expression.MakeMemberAccess(parameter, member);

                    // A component that is not numeric cannot be converted, and Expression.Convert
                    // would throw at build time rather than produce a wrong number.
                    Expression asDouble = Expression.Convert(access, typeof(double));

                    Type delegateType = typeof(Func<,>).MakeGenericType(elementType, typeof(double));
                    return Expression.Lambda(delegateType, asDouble, parameter).Compile();
                }
                catch
                {
                    // Try the next candidate name rather than failing outright: a member of the
                    // right name but the wrong shape is not the one being looked for.
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ reflection plumbing
        //
        // Duplicated rather than shared with PointMapperReader/MatrixReader: each reader in this
        // project probes its own object graph independently, and the copies have already drifted
        // (the element accessors here are compiled expressions rather than PropertyInfo lookups,
        // because they run 1.5 million times rather than twice).

        private static PropertyInfo SafeGetProperty(Type type, string name)
        {
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property != null && property.GetIndexParameters().Length == 0 ? property : null;
            }
            catch
            {
                return null;
            }
        }

        private static FieldInfo SafeGetField(Type type, string name)
        {
            try { return type.GetField(name, BindingFlags.Public | BindingFlags.Instance); }
            catch { return null; }
        }

        private static bool TryConvert(object raw, out double value)
        {
            value = 0.0;
            if (raw == null) return false;

            try
            {
                value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadProperty(object instance, string name, out object value)
        {
            value = null;
            if (instance == null) return false;

            try
            {
                PropertyInfo property = instance.GetType().GetProperty(
                    name, BindingFlags.Public | BindingFlags.Instance);

                if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
                    return false;

                value = property.GetValue(instance, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadInt(object instance, string name, out int value)
        {
            value = 0;
            object raw;
            if (!TryReadProperty(instance, name, out raw) || raw == null) return false;

            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadDouble(object instance, string name, out double value)
        {
            value = 0.0;
            object raw;
            if (!TryReadProperty(instance, name, out raw) || raw == null) return false;

            return TryConvert(raw, out value);
        }
    }
}
