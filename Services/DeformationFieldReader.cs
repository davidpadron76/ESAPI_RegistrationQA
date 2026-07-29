using System;
using System.Globalization;
using System.Linq;
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
                    if (log != null)
                    {
                        log.Info("deformation field",
                            "read from " + label + " (" + result.XSize + "x" + result.YSize + "x" +
                            result.ZSize + " grid, " +
                            FormatMm(result.XResMm) + "x" + FormatMm(result.YResMm) + "x" +
                            FormatMm(result.ZResMm) + " mm spacing, native to the field, not the image)");
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

            MemberReader rx = FindReader(elementType, "X", "x");
            MemberReader ry = FindReader(elementType, "Y", "y");
            MemberReader rz = FindReader(elementType, "Z", "z");

            if (rx == null || ry == null || rz == null)
            {
                problem = elementType.Name + " exposes no recognisable X/Y/Z members";
                return null;
            }

            var vectors = new Vec3[xSize, ySize, zSize];

            for (int z = 0; z < zSize; z++)
            {
                for (int y = 0; y < ySize; y++)
                {
                    for (int x = 0; x < xSize; x++)
                    {
                        object element = buffer.GetValue(x, y, z);

                        double vx, vy, vz;
                        if (!rx(element, out vx) || !ry(element, out vy) || !rz(element, out vz))
                        {
                            problem = "an element in the buffer could not be read back as X/Y/Z";
                            return null;
                        }

                        vectors[x, y, z] = new Vec3(vx, vy, vz);
                    }
                }
            }

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

        // ------------------------------------------------------------------ reflection plumbing
        //
        // Duplicated rather than shared with PointMapperReader/MatrixReader: each reader in this
        // project probes its own object graph independently, and the three copies have already
        // drifted in small ways (this one needs field element readers for a type that is never
        // known until GetVectors' parameter is inspected at runtime).

        private delegate bool MemberReader(object instance, out double value);

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
