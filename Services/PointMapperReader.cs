using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Finds a way to push a point through a registration, using the API's own method rather
    /// than a matrix.
    ///
    /// This is now the primary route for every registration, rigid or deformable. The probe
    /// against a real Eclipse found <c>TransformPoint(VVector)</c> and
    /// <c>InverseTransformPoint(VVector)</c> on the nested
    /// <c>VMS.CA.Scripting.RigidRegistration</c> object — Varian's own mapping, whose direction
    /// is correct by construction, and which sidesteps the entire business of guessing whether
    /// a matrix is row- or column-major. Two earlier mistakes kept it hidden: this search ran
    /// only for deformable registrations, and <c>RigidRegistration</c> was not among the
    /// wrapper properties it looked inside.
    ///
    /// Everything measurable about a registration depends on this one thing. The intensity
    /// metrics need it to pair voxels, DSC/MDA/HD95 to carry a contour across, TRE for the
    /// landmarks and consistency twice over. Without it every metric is unavailable and the
    /// verdict is NO EVIDENCE — which is what a deformable case produced on every metric, every
    /// time, because the method was being located correctly and then thrown away: the returned
    /// vector's components were read with <c>GetField</c> alone, and the ESAPI vector type
    /// exposes x, y and z as properties.
    ///
    /// So a component is accepted as a field or a property, in either casing, and when nothing
    /// matches the object's member surface goes to the diagnostics — the same evidence trail as
    /// <see cref="MatrixReader"/>, for the same reason.
    /// </summary>
    public static class PointMapperReader
    {
        /// <summary>
        /// Method names that might map a point through the registration, most specific first.
        /// Direction matters and cannot be verified from here, so the name that answered is
        /// recorded in the mapper description and printed in the report.
        /// </summary>
        private static readonly string[] CandidateMethods =
        {
            "TransformPointToRegistered",
            "TransformPointFromSourceToRegistered",
            "MapPointToRegistered",
            "TransformPoint",
            "MapPoint",
            "DeformPoint",
            "TransformPointToSource",
            "MapPointToSource",
            // Last resort, and deliberately below every forward candidate: it maps the other
            // way, so accepting it when a forward method exists would silently invert every
            // measurement.
            "InverseTransformPoint"
        };

        /// <summary>
        /// Properties that may wrap the object actually carrying the mapping method. A
        /// MIRSNonRigidRegistration is a wrapper, so the method can live one level down.
        /// </summary>
        private static readonly string[] CandidateHolders =
        {
            // First, because it is the one the probe actually found. MIRSRigidRegistration is a
            // wrapper and the mapping lives on the RigidRegistration it holds.
            "RigidRegistration",
            "NonRigidRegistration",
            "DeformableRegistration",
            "DeformableRegistrationField",
            "Registration",
            "SpatialRegistration",
            "DeformationField",
            "VectorField"
        };

        public static DynamicPointMapper TryBuild(object registration, DiagnosticLog log)
        {
            if (registration == null) return null;

            DynamicPointMapper mapper = TryBuildOn(registration, null, log);
            if (mapper != null) return mapper;

            foreach (string holderName in CandidateHolders)
            {
                object holder;
                if (!TryReadProperty(registration, holderName, out holder)) continue;
                if (holder == null) continue;

                mapper = TryBuildOn(holder, holderName, log);
                if (mapper != null) return mapper;
            }

            if (log != null)
            {
                log.Info("point mapping",
                    "the registration exposes no method for mapping a point. For a rigid registration " +
                    "the matrix is used instead; for a deformable one nothing else is valid, since the " +
                    "linear component describes a different transform from the one under audit. " +
                    MatrixReader.DescribeMemberSurface(registration) +
                    " Send this line with your Eclipse version: naming the right method is a one-line fix.");
            }

            return null;
        }

        private static DynamicPointMapper TryBuildOn(object target, string holderName, DiagnosticLog log)
        {
            Type targetType;
            try { targetType = target.GetType(); }
            catch { return null; }

            string prefix = holderName != null ? holderName + "." : string.Empty;

            foreach (string name in CandidateMethods)
            {
                MethodInfo method = FindSinglePointMethod(targetType, name);
                if (method == null) continue;

                Type vectorType = method.GetParameters()[0].ParameterType;

                Func<Vec3, object> toVector = BuildVectorFactory(vectorType);
                Func<object, Vec3?> fromVector = BuildVectorReader(method.ReturnType);

                if (toVector == null || fromVector == null)
                {
                    if (log != null)
                    {
                        log.Warning("point mapping: " + prefix + name,
                            "the method exists but its vector type could not be " +
                            (toVector == null ? "constructed" : "read") + ". Parameter type '" +
                            vectorType.Name + "', return type '" + method.ReturnType.Name + "'. " +
                            MatrixReader.DescribeType(toVector == null ? vectorType : method.ReturnType));
                    }
                    continue;
                }

                object instance = target;
                Func<Vec3, Vec3?> map = point =>
                {
                    object result = method.Invoke(instance, new[] { toVector(point) });
                    return fromVector(result);
                };

                // The method existing does not mean it is implemented in this version, so it
                // is probed with real points before being trusted. Two rather than one: a stub
                // returning its input unchanged would pass a single probe at the origin.
                string probeProblem;
                if (!Probe(map, out probeProblem))
                {
                    if (log != null)
                        log.Warning("point mapping: " + prefix + name, probeProblem);
                    continue;
                }

                if (log != null)
                {
                    log.Info("point mapping",
                        "using " + targetType.Name + "." + name + " to map points through the registration. " +
                        "The direction of this method is not verifiable from the API: if TRE comes " +
                        "out systematically large while the fusion looks correct, it may be mapping the " +
                        "other way.");
                }

                return new DynamicPointMapper(map,
                    "point-by-point mapping via " + prefix + name + " (the API's own, no matrix involved)");
            }

            return null;
        }

        private static MethodInfo FindSinglePointMethod(Type type, string name)
        {
            try
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, name, StringComparison.Ordinal) &&
                        m.GetParameters().Length == 1 &&
                        m.ReturnType != typeof(void) &&
                        !m.GetParameters()[0].ParameterType.IsPrimitive);
            }
            catch
            {
                return null;
            }
        }

        private static bool Probe(Func<Vec3, Vec3?> map, out string problem)
        {
            problem = null;

            var probes = new[] { new Vec3(0, 0, 0), new Vec3(37.5, -21.25, 64.0) };
            var results = new List<Vec3>();

            foreach (Vec3 point in probes)
            {
                try
                {
                    Vec3? mapped = map(point);
                    if (!mapped.HasValue || !mapped.Value.IsFinite)
                    {
                        problem = "the probe at (" + Describe(point) + ") did not return a finite point";
                        return false;
                    }
                    results.Add(mapped.Value);
                }
                catch (Exception ex)
                {
                    problem = "the probe at (" + Describe(point) + ") threw — " + DiagnosticLog.Describe(ex);
                    return false;
                }
            }

            // Distinct inputs mapping to the same output means the method is not a spatial
            // mapping at all.
            if ((results[0] - results[1]).Length < 1e-9)
            {
                problem = "two different points mapped to the same location, so this method is not a " +
                          "spatial mapping";
                return false;
            }

            return true;
        }

        private static string Describe(Vec3 v)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1}, {1:F1}, {2:F1}", v.X, v.Y, v.Z);
        }

        // ------------------------------------------------------------------ vector plumbing

        /// <summary>
        /// Builds a vector of the API's own type from a <see cref="Vec3"/>.
        ///
        /// The three-double constructor is tried first: it is the only route that works for an
        /// immutable struct, and the ESAPI vector type has one.
        /// </summary>
        private static Func<Vec3, object> BuildVectorFactory(Type vectorType)
        {
            if (vectorType == null) return null;

            ConstructorInfo constructor = null;
            try
            {
                constructor = vectorType.GetConstructor(
                    new[] { typeof(double), typeof(double), typeof(double) });
            }
            catch
            {
                // Fall through to the member-assignment route below.
            }

            if (constructor != null)
                return point => constructor.Invoke(new object[] { point.X, point.Y, point.Z });

            MemberWriter wx = FindWriter(vectorType, "x", "X");
            MemberWriter wy = FindWriter(vectorType, "y", "Y");
            MemberWriter wz = FindWriter(vectorType, "z", "Z");

            if (wx == null || wy == null || wz == null) return null;

            return point =>
            {
                object boxed = Activator.CreateInstance(vectorType);
                wx(boxed, point.X);
                wy(boxed, point.Y);
                wz(boxed, point.Z);
                return boxed;
            };
        }

        /// <summary>
        /// Reads x, y and z back off whatever the API returned.
        ///
        /// This is the method that used to reject a perfectly good mapping: it looked for
        /// fields alone, and the ESAPI vector type exposes its components as properties.
        /// </summary>
        private static Func<object, Vec3?> BuildVectorReader(Type vectorType)
        {
            if (vectorType == null || vectorType == typeof(void)) return null;

            MemberReader rx = FindReader(vectorType, "x", "X");
            MemberReader ry = FindReader(vectorType, "y", "Y");
            MemberReader rz = FindReader(vectorType, "z", "Z");

            if (rx == null || ry == null || rz == null) return null;

            return value =>
            {
                if (value == null) return null;

                double x, y, z;
                if (!rx(value, out x) || !ry(value, out y) || !rz(value, out z)) return null;

                return new Vec3(x, y, z);
            };
        }

        private delegate bool MemberReader(object instance, out double value);

        private delegate void MemberWriter(object instance, double value);

        private static MemberReader FindReader(Type type, params string[] names)
        {
            foreach (string name in names)
            {
                PropertyInfo property = SafeGetProperty(type, name);
                if (property != null && property.CanRead)
                {
                    PropertyInfo captured = property;
                    return (object instance, out double value) =>
                        TryConvert(captured.GetValue(instance, null), out value);
                }

                FieldInfo field = SafeGetField(type, name);
                if (field != null)
                {
                    FieldInfo captured = field;
                    return (object instance, out double value) =>
                        TryConvert(captured.GetValue(instance), out value);
                }
            }

            return null;
        }

        private static MemberWriter FindWriter(Type type, params string[] names)
        {
            foreach (string name in names)
            {
                PropertyInfo property = SafeGetProperty(type, name);
                if (property != null && property.CanWrite)
                {
                    PropertyInfo captured = property;
                    return (instance, value) => captured.SetValue(instance, value, null);
                }

                FieldInfo field = SafeGetField(type, name);
                if (field != null && !field.IsInitOnly)
                {
                    FieldInfo captured = field;
                    return (instance, value) => captured.SetValue(instance, value);
                }
            }

            return null;
        }

        private static PropertyInfo SafeGetProperty(Type type, string name)
        {
            try
            {
                PropertyInfo property = type.GetProperty(
                    name, BindingFlags.Public | BindingFlags.Instance);
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

                if (property == null || !property.CanRead) return false;
                if (property.GetIndexParameters().Length != 0) return false;

                value = property.GetValue(instance, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
