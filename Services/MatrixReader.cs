using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Recovers the 4x4 homogeneous registration matrix from whatever the Varian API happens
    /// to expose in this Eclipse version.
    ///
    /// The previous implementation probed four property names and then indexed the result as
    /// <c>matrix[r, c]</c>. Both halves of that are fragile. The property name differs between
    /// the classic ESAPI <c>Registration</c> and the VMS.IRS <c>MIRSRigidRegistration</c>
    /// wrapper, and the container is not necessarily a two-dimensional array: a DICOM spatial
    /// registration matrix is naturally a flat 16-element row-major vector, and several
    /// wrappers expose it as such, or as a jagged array, or as named M11..M44 members. When
    /// none of that matched, the analyzer silently fell back to the difference between the two
    /// image frames — a different quantity entirely — and reported it as if it were the
    /// registration.
    ///
    /// So this class does two things the old code did not:
    ///
    /// 1. It separates *finding* the object from *interpreting* it. Any of a dozen property
    ///    paths may hold the matrix, and each may hold it in any of seven shapes.
    /// 2. When nothing matches, it writes the registration object's actual member surface —
    ///    property names and types — into the diagnostic log. The API differences between
    ///    Eclipse versions cannot be reproduced outside a clinic, so the only way to close
    ///    this class of bug is to have the plugin report what it found instead of what it
    ///    hoped for.
    /// </summary>
    public static class MatrixReader
    {
        /// <summary>Upper bound on members listed when the matrix is not found.</summary>
        private const int MaxMembersReported = 60;

        /// <summary>
        /// Returns the raw 4x4 matrix, or null. <paramref name="source"/> names the property
        /// path and container shape that answered, for the report's provenance line.
        /// </summary>
        public static double[,] TryRead(dynamic registration, DiagnosticLog log, out string source)
        {
            source = null;
            if (registration == null) return null;

            // Everything past the named-property probes is plain reflection, so the object is
            // pinned to a static type here. Passing a dynamic into those helpers would make
            // every call site dynamically bound — including the ones with out parameters —
            // for no benefit.
            object registrationObject = registration;

            foreach (Tuple<string, Func<object>> candidate in NamedCandidates(registration))
            {
                dynamic held;
                if (!Dyn.TryGet("transform: locate matrix (" + candidate.Item1 + ")",
                        candidate.Item2, null, out held))
                {
                    continue;
                }

                double[,] raw;
                string shape;
                if (TryCoerce((object)held, out raw, out shape))
                {
                    source = candidate.Item1 + " as " + shape;
                    if (log != null) log.Info("transform: matrix", "read from " + source);
                    return raw;
                }

                if (log != null)
                {
                    log.Info("transform: matrix",
                        candidate.Item1 + " exists (" + TypeName((object)held) +
                        ") but does not hold a readable 4x4 matrix");
                }
            }

            double[,] swept = SweepByReflection(registrationObject, log, out source);
            if (swept != null) return swept;

            ReportMemberSurface(registrationObject, log);
            return null;
        }

        // ------------------------------------------------------------------ locating

        /// <summary>
        /// Property paths known or plausible across ESAPI and VMS.IRS versions, most specific
        /// first. <c>MIRSRigidRegistration</c> wraps an inner rigid registration, so the
        /// nested paths are tried before the flat ones.
        /// </summary>
        private static IEnumerable<Tuple<string, Func<object>>> NamedCandidates(dynamic r)
        {
            yield return Dyn.Alt("RigidRegistration.TransformationMatrix", () => r.RigidRegistration.TransformationMatrix);
            yield return Dyn.Alt("RigidRegistration.Matrix", () => r.RigidRegistration.Matrix);
            yield return Dyn.Alt("RigidRegistration.Transform", () => r.RigidRegistration.Transform);
            yield return Dyn.Alt("TransformationMatrix", () => r.TransformationMatrix);
            yield return Dyn.Alt("Matrix", () => r.Matrix);
            yield return Dyn.Alt("TransformMatrix", () => r.TransformMatrix);
            yield return Dyn.Alt("RigidTransformMatrix", () => r.RigidTransformMatrix);
            yield return Dyn.Alt("Transform.Matrix", () => r.Transform.Matrix);
            yield return Dyn.Alt("Transform.TransformationMatrix", () => r.Transform.TransformationMatrix);
            yield return Dyn.Alt("Transform", () => r.Transform);
            yield return Dyn.Alt("SpatialRegistration.TransformationMatrix", () => r.SpatialRegistration.TransformationMatrix);
            yield return Dyn.Alt("Registration.TransformationMatrix", () => r.Registration.TransformationMatrix);
            yield return Dyn.Alt("FrameOfReferenceTransformationMatrix", () => r.FrameOfReferenceTransformationMatrix);
        }

        /// <summary>
        /// Last resort before giving up: walk the object's own public properties, and one
        /// level into any property whose name suggests it wraps a registration or a transform,
        /// accepting the first value that coerces to a 4x4.
        ///
        /// Reading arbitrary properties has a cost — an ESAPI property getter can hit the
        /// database — so the sweep is restricted to parameterless getters and to a single
        /// level of nesting.
        /// </summary>
        private static double[,] SweepByReflection(object registration, DiagnosticLog log, out string source)
        {
            source = null;

            foreach (PropertyInfo property in ReadableProperties(registration))
            {
                object value;
                if (!TryReadProperty(registration, property, out value)) continue;
                if (value == null) continue;

                double[,] raw;
                string shape;
                if (TryCoerce(value, out raw, out shape))
                {
                    source = "reflection: " + property.Name + " as " + shape;
                    if (log != null)
                        log.Warning("transform: matrix",
                            "found by scanning the registration object: " + source +
                            ". No known property name matched, so please report the Eclipse " +
                            "version and this line so the name can be probed directly.");
                    return raw;
                }

                if (!LooksLikeWrapper(property.Name)) continue;

                foreach (PropertyInfo inner in ReadableProperties(value))
                {
                    object innerValue;
                    if (!TryReadProperty(value, inner, out innerValue)) continue;
                    if (innerValue == null) continue;

                    if (TryCoerce(innerValue, out raw, out shape))
                    {
                        source = "reflection: " + property.Name + "." + inner.Name + " as " + shape;
                        if (log != null)
                            log.Warning("transform: matrix",
                                "found by scanning the registration object: " + source +
                                ". No known property name matched, so please report the Eclipse " +
                                "version and this line so the name can be probed directly.");
                        return raw;
                    }
                }
            }

            return null;
        }

        private static bool LooksLikeWrapper(string name)
        {
            string lower = (name ?? string.Empty).ToLowerInvariant();
            return lower.Contains("registration") || lower.Contains("transform") || lower.Contains("matrix");
        }

        private static IEnumerable<PropertyInfo> ReadableProperties(object instance)
        {
            // Named differently from PropertiesOfType on purpose rather than overloaded:
            // Type is itself an object, so an overload pair would silently accept a Type here
            // and describe the members of System.Type instead of the type in hand.
            if (instance == null) return Enumerable.Empty<PropertyInfo>();

            try { return PropertiesOfType(instance.GetType()); }
            catch { return Enumerable.Empty<PropertyInfo>(); }
        }

        private static IEnumerable<PropertyInfo> PropertiesOfType(Type type)
        {
            if (type == null) return Enumerable.Empty<PropertyInfo>();

            try
            {
                return type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    .ToList();
            }
            catch
            {
                return Enumerable.Empty<PropertyInfo>();
            }
        }

        private static bool TryReadProperty(object instance, PropertyInfo property, out object value)
        {
            value = null;
            try
            {
                value = property.GetValue(instance, null);
                return true;
            }
            catch
            {
                // A property that throws is not a fault worth reporting: ESAPI raises on
                // properties that are meaningless for the current object state, and the sweep
                // deliberately touches properties it knows nothing about.
                return false;
            }
        }

        /// <summary>
        /// Writes the registration object's member surface to the log so that the next run in
        /// a clinic answers the question this code could not: what the matrix is actually
        /// called in that Eclipse version.
        /// </summary>
        private static void ReportMemberSurface(object registration, DiagnosticLog log)
        {
            if (log == null) return;

            log.Failure("transform: matrix",
                "no 4x4 matrix could be read from the registration. " + DescribeMemberSurface(registration));
        }

        /// <summary>
        /// The type name and public member surface of an API object, as a single line for the
        /// diagnostics tab.
        ///
        /// Shared with the deformable mapping search, which fails for the same underlying
        /// reason and needs the same evidence: differences between Eclipse versions cannot be
        /// reproduced outside a clinic, so the plugin has to report what it found rather than
        /// what it expected.
        /// </summary>
        public static string DescribeMemberSurface(object instance)
        {
            if (instance == null) return "The object is null.";

            try { return DescribeType(instance.GetType()); }
            catch { return "The object's type could not be determined."; }
        }

        /// <summary>
        /// Same, for a type known statically rather than through an instance.
        ///
        /// Kept separate because passing a <see cref="Type"/> to the overload above would
        /// describe the members of <c>System.Type</c> itself, which is a great deal of text
        /// about the wrong object.
        /// </summary>
        public static string DescribeType(Type type)
        {
            if (type == null) return "The type is null.";

            var members = new List<string>();

            foreach (PropertyInfo property in PropertiesOfType(type))
            {
                members.Add(property.Name + " : " + FriendlyTypeName(property.PropertyType));
                if (members.Count >= MaxMembersReported) break;
            }

            try
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (members.Count >= MaxMembersReported) break;
                    members.Add(field.Name + " : " + FriendlyTypeName(field.FieldType) + " (field)");
                }
            }
            catch
            {
                // Diagnostic only.
            }

            try
            {
                foreach (MethodInfo method in type
                             .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                             .Where(m => !m.IsSpecialName && m.GetParameters().Length <= 2))
                {
                    if (members.Count >= MaxMembersReported) break;
                    members.Add(method.Name + "(" +
                                string.Join(", ", method.GetParameters()
                                    .Select(p => FriendlyTypeName(p.ParameterType)).ToArray()) +
                                ") : " + FriendlyTypeName(method.ReturnType));
                }
            }
            catch
            {
                // Enumerating methods is diagnostic only; a failure here must not mask the
                // message this text is embedded in.
            }

            var text = new StringBuilder();
            text.Append("The object is of type '").Append(type.FullName).Append("' and exposes: ");
            text.Append(members.Count > 0 ? string.Join(" | ", members.ToArray()) : "(no readable members)");
            if (members.Count >= MaxMembersReported) text.Append(" | …(truncated)");

            return text.ToString();
        }

        // ------------------------------------------------------------------ interpreting

        /// <summary>
        /// Turns a candidate object into a 4x4 array of doubles, whatever shape it arrived in.
        ///
        /// A 3x4 or 12-element form is accepted and completed with the homogeneous row
        /// (0,0,0,1): that is the affine part alone, which is the whole of a rigid transform,
        /// and DICOM's own frame-of-reference transformation matrix is often handled that way.
        /// </summary>
        public static bool TryCoerce(object candidate, out double[,] raw, out string shape)
        {
            raw = null;
            shape = null;
            if (candidate == null) return false;

            if (TryFromArray(candidate as Array, out raw, out shape)) return true;
            if (TryFromJagged(candidate, out raw, out shape)) return true;
            if (TryFromFlatList(candidate, out raw, out shape)) return true;
            if (TryFromTwoIndexIndexer(candidate, out raw, out shape)) return true;
            if (TryFromOneIndexIndexer(candidate, out raw, out shape)) return true;
            if (TryFromNamedElements(candidate, out raw, out shape)) return true;

            return false;
        }

        private static bool TryFromArray(Array array, out double[,] raw, out string shape)
        {
            raw = null;
            shape = null;
            if (array == null) return false;

            if (array.Rank == 2)
            {
                int rows = array.GetLength(0);
                int columns = array.GetLength(1);

                // Non-zero lower bounds are legal in the CLR and would silently offset every
                // read, so the indices come from GetLowerBound rather than from 0.
                int r0 = array.GetLowerBound(0);
                int c0 = array.GetLowerBound(1);

                if ((rows == 4 || rows == 3) && columns == 4)
                {
                    var m = Homogeneous();
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < 4; c++)
                            if (!TrySet(m, r, c, array.GetValue(r0 + r, c0 + c))) return false;

                    raw = m;
                    shape = rows + "x4 array";
                    return true;
                }

                return false;
            }

            if (array.Rank == 1)
                return TryFromFlatSequence(array.Cast<object>().ToList(), out raw, out shape, "flat array");

            return false;
        }

        private static bool TryFromJagged(object candidate, out double[,] raw, out string shape)
        {
            raw = null;
            shape = null;

            var outer = candidate as IList;
            if (outer == null) return false;
            if (outer.Count != 4 && outer.Count != 3) return false;

            var m = Homogeneous();

            for (int r = 0; r < outer.Count; r++)
            {
                var row = outer[r] as IList;
                if (row == null || row.Count != 4) return false;

                for (int c = 0; c < 4; c++)
                    if (!TrySet(m, r, c, row[c])) return false;
            }

            raw = m;
            shape = outer.Count + "x4 jagged list";
            return true;
        }

        private static bool TryFromFlatList(object candidate, out double[,] raw, out string shape)
        {
            raw = null;
            shape = null;

            // Arrays are handled by TryFromArray; this covers List<double> and friends.
            if (candidate is Array) return false;

            var list = candidate as IList;
            if (list == null) return false;

            return TryFromFlatSequence(list.Cast<object>().ToList(), out raw, out shape, "flat list");
        }

        private static bool TryFromFlatSequence(
            IList<object> values, out double[,] raw, out string shape, string label)
        {
            raw = null;
            shape = null;
            if (values == null) return false;
            if (values.Count != 16 && values.Count != 12) return false;

            int rows = values.Count / 4;
            var m = Homogeneous();

            for (int r = 0; r < rows; r++)
                for (int c = 0; c < 4; c++)
                    if (!TrySet(m, r, c, values[r * 4 + c])) return false;

            raw = m;
            shape = values.Count + "-element row-major " + label;
            return true;
        }

        private static bool TryFromTwoIndexIndexer(object candidate, out double[,] raw, out string shape)
        {
            raw = null;
            shape = null;

            PropertyInfo indexer = FindIndexer(candidate, 2);
            if (indexer == null) return false;

            var m = Homogeneous();

            try
            {
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                        if (!TrySet(m, r, c, indexer.GetValue(candidate, new object[] { r, c }))) return false;
            }
            catch
            {
                return false;
            }

            raw = m;
            shape = "[row, column] indexer";
            return true;
        }

        private static bool TryFromOneIndexIndexer(object candidate, out double[,] raw, out string shape)
        {
            raw = null;
            shape = null;

            PropertyInfo indexer = FindIndexer(candidate, 1);
            if (indexer == null) return false;

            var values = new List<object>();

            try
            {
                for (int i = 0; i < 16; i++)
                    values.Add(indexer.GetValue(candidate, new object[] { i }));
            }
            catch
            {
                return false;
            }

            return TryFromFlatSequence(values, out raw, out shape, "row-major [i] indexer");
        }

        private static PropertyInfo FindIndexer(object candidate, int parameterCount)
        {
            try
            {
                return candidate.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.CanRead &&
                                         p.GetIndexParameters().Length == parameterCount &&
                                         p.GetIndexParameters().All(q => q.ParameterType == typeof(int)));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Named cells: <c>m00..m33</c> (0-based) or <c>M11..M44</c> (1-based, the WPF
        /// <c>Matrix3D</c> convention). Matrix3D names its translation row OffsetX/Y/Z rather
        /// than M41..M43, so those are accepted as substitutes.
        /// </summary>
        private static bool TryFromNamedElements(object candidate, out double[,] raw, out string shape)
        {
            raw = null;
            shape = null;

            var m = Homogeneous();
            bool zeroBased = true;

            for (int r = 0; r < 4 && zeroBased; r++)
                for (int c = 0; c < 4 && zeroBased; c++)
                {
                    object value;
                    if (!TryReadMember(candidate, "m" + r + c, out value)) zeroBased = false;
                    else if (!TrySet(m, r, c, value)) zeroBased = false;
                }

            if (zeroBased)
            {
                raw = m;
                shape = "m00..m33 members";
                return true;
            }

            m = Homogeneous();

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    object value;
                    if (TryReadMember(candidate, "M" + (r + 1) + (c + 1), out value))
                    {
                        if (!TrySet(m, r, c, value)) return false;
                        continue;
                    }

                    // Matrix3D: the translation row is exposed as OffsetX/OffsetY/OffsetZ.
                    if (r == 3 && c < 3)
                    {
                        string offset = c == 0 ? "OffsetX" : c == 1 ? "OffsetY" : "OffsetZ";
                        if (TryReadMember(candidate, offset, out value) && TrySet(m, r, c, value))
                            continue;
                    }

                    return false;
                }
            }

            raw = m;
            shape = "M11..M44 members";
            return true;
        }

        private static bool TryReadMember(object instance, string name, out object value)
        {
            value = null;
            if (instance == null) return false;

            try
            {
                Type type = instance.GetType();

                PropertyInfo property = type.GetProperty(
                    name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(instance, null);
                    return value != null;
                }

                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                {
                    value = field.GetValue(instance);
                    return value != null;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A 4x4 seeded with the homogeneous last row, so a 3x4 form completes itself.</summary>
        private static double[,] Homogeneous()
        {
            var m = new double[4, 4];
            m[3, 3] = 1.0;
            return m;
        }

        private static bool TrySet(double[,] m, int row, int column, object value)
        {
            if (value == null) return false;
            if (!(value is IConvertible)) return false;

            try
            {
                double converted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(converted) || double.IsInfinity(converted)) return false;
                m[row, column] = converted;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string TypeName(object instance)
        {
            try { return instance.GetType().Name; }
            catch { return "unknown type"; }
        }

        private static string FriendlyTypeName(Type type)
        {
            if (type == null) return "?";
            if (type == typeof(void)) return "void";
            if (type == typeof(double)) return "double";
            if (type == typeof(int)) return "int";
            if (type == typeof(string)) return "string";
            if (type == typeof(bool)) return "bool";
            return type.Name;
        }
    }
}
