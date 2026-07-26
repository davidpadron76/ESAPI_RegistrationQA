using System;
using System.Globalization;
using System.Reflection;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Extracts everything measurable about a registration from the Varian API and returns a
    /// <see cref="QaMeasurements"/>.
    ///
    /// The rule governing this whole class: a metric that cannot be measured is returned as
    /// unavailable with the specific reason. It is never replaced by a plausible-looking
    /// value. The previous version derived DSC, HD95, Jacobian, maximum displacement and
    /// smoothness from <c>GetHashCode()</c> of the registration object, which besides having
    /// no physical meaning was not reproducible: the identity hash changes between runs, so
    /// the same registration produced a different DSC every time the script was opened.
    /// </summary>
    public sealed class RegistrationAnalyzer
    {
        private readonly DiagnosticLog _log;

        /// <summary>Geometry of the volume used as the sampling grid (the source image).</summary>
        private ImageGeometry _fixedGeometry;

        public RegistrationAnalyzer(DiagnosticLog log)
        {
            if (log == null) throw new ArgumentNullException("log");
            _log = log;
        }

        public QaMeasurements Analyze(dynamic scriptContext)
        {
            var measurements = new QaMeasurements();

            dynamic registration;
            if (!Dyn.TryGet("context: active registration", () => scriptContext.Registration, _log, out registration))
            {
                MarkAllUnavailable(measurements,
                    "there is no active registration in the Eclipse workspace");
                measurements.RegistrationId = "(no active registration)";
                return measurements;
            }

            measurements.RegistrationId = ReadRegistrationId(registration);
            ClassifyRegistration(registration, measurements);

            // --- Images -----------------------------------------------------------------
            // Loaded once and reused: the transform fallback, the similarity computation and
            // the FOV extent all need this geometry.
            EsapiImageReader.LoadResult source = null;
            EsapiImageReader.LoadResult target = null;

            dynamic sourceImage, registeredImage;
            bool haveSource = Dyn.TryGet("registration: SourceImage", () => registration.SourceImage, _log, out sourceImage);
            bool haveRegistered = Dyn.TryGet("registration: RegisteredImage", () => registration.RegisteredImage, _log, out registeredImage);

            if (haveSource) source = EsapiImageReader.Load(sourceImage, "source image", _log);
            if (haveRegistered) target = EsapiImageReader.Load(registeredImage, "registered image", _log);

            if (source != null) measurements.FixedModality = source.Modality;
            if (target != null) measurements.MovingModality = target.Modality;

            if (source != null && source.Success) _fixedGeometry = source.Volume.Geometry;

            // --- Rigid transform --------------------------------------------------------
            ExtractRigidTransform(registration, measurements, source, target);

            // --- Intensity similarity ---------------------------------------------------
            if (source == null || target == null || !source.Success || !target.Success)
            {
                string reason = source == null || target == null
                    ? "the registration does not expose both images (SourceImage / RegisteredImage), so there are no voxel pairs to compare"
                    : "could not load both volumes (" + (source.Problem ?? target.Problem) + ")";

                measurements.Nmi = MeasuredValue.Unavailable(reason);
                measurements.Ncc = MeasuredValue.Unavailable(reason);
                measurements.Ssd = MeasuredValue.Unavailable(reason);
            }
            else
            {
                ComputeIntensitySimilarity(registration, source, target, measurements);
            }

            // --- Deformation / topology -------------------------------------------------
            ComputeDeformationMetrics(measurements);

            // --- Structures -------------------------------------------------------------
            ComputeStructureMetrics(registration, measurements);

            foreach (DiagnosticEntry entry in _log.Entries)
                measurements.Diagnostics.Add(entry.ToString());

            return measurements;
        }

        // ---------------------------------------------------------------- identification

        private string ReadRegistrationId(dynamic registration)
        {
            dynamic value;
            string source;
            if (Dyn.TryGetFirst("registration: identifier", _log, out value, out source,
                    Dyn.Alt("Id", () => registration.Id),
                    Dyn.Alt("Name", () => registration.Name)))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            return "(unidentified registration)";
        }

        /// <summary>
        /// Determines the registration type. An explicit API property is preferred and the
        /// CLR type name is only a last resort, because that name can change between Eclipse
        /// versions without notice.
        /// </summary>
        private void ClassifyRegistration(dynamic registration, QaMeasurements measurements)
        {
            dynamic value;
            string source;

            if (Dyn.TryGetFirst("registration: declared type", _log, out value, out source,
                    Dyn.Alt("RegistrationType", () => registration.RegistrationType),
                    Dyn.Alt("Type", () => registration.Type)))
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                _log.Info("registration: declared type", text + " (via " + source + ")");
                ApplyTypeText(text, measurements);
                return;
            }

            string clrTypeName = "(unknown)";
            Dyn.TryInvoke("registration: CLR type name",
                () => { clrTypeName = ((object)registration).GetType().Name; }, _log);

            _log.Warning("registration: type",
                "the API does not expose the registration type; inferring it from the CLR class name '" +
                clrTypeName + "', which is fragile across versions");

            ApplyTypeText(clrTypeName, measurements);
        }

        private static void ApplyTypeText(string text, QaMeasurements measurements)
        {
            string upper = (text ?? string.Empty).ToUpperInvariant();

            if (upper.Contains("NONRIGID") || upper.Contains("NON-RIGID") ||
                upper.Contains("DEFORMABLE") || upper.Contains("DIR"))
            {
                measurements.RegType = RegistrationType.NonRigid;
                measurements.IsDeformable = true;
            }
            else if (upper.Contains("IDENTITY"))
            {
                measurements.RegType = RegistrationType.Identity;
            }
            else if (upper.Contains("RIGID"))
            {
                measurements.RegType = RegistrationType.Rigid;
            }
            else
            {
                measurements.RegType = RegistrationType.Unknown;
            }
        }

        // ---------------------------------------------------------------- transform

        private void ExtractRigidTransform(
            dynamic registration,
            QaMeasurements measurements,
            EsapiImageReader.LoadResult source,
            EsapiImageReader.LoadResult target)
        {
            double[,] raw = TryReadMatrix(registration);

            if (raw != null)
            {
                RigidTransform transform;
                string note;
                if (RigidTransform.TryFromRawMatrix(raw, out transform, out note))
                {
                    measurements.Transform = transform;
                    measurements.TransformSource = "API matrix — " + note;
                    measurements.RigidEulerAngles = transform.GetEulerAnglesDegrees();

                    _log.Info("transform: matrix", note);

                    if (measurements.RigidEulerAngles.Value.GimbalLock)
                    {
                        _log.Warning("transform: Euler angles",
                            "gimbal lock (pitch ≈ ±90°): only the combination of pitch and yaw is " +
                            "observable; yaw is reported as 0");
                    }
                    return;
                }

                _log.Failure("transform: matrix", note);
            }

            // Fallback: relative transform between the two image frames.
            //
            // Important: this path is taken only when the matrix could not be read. The
            // previous version also took it when the translation happened to be (0,0,0), so
            // a legitimate identity registration — or a perfectly aligned one — had its
            // correct reading discarded and replaced by the difference of series origins,
            // which is a different quantity.
            ImageGeometry sourceGeometry = source != null && source.Success ? source.Volume.Geometry : null;
            ImageGeometry targetGeometry = target != null && target.Success ? target.Volume.Geometry : null;

            if (sourceGeometry != null && targetGeometry != null)
            {
                RigidTransform transform = RigidTransform.FromFrames(sourceGeometry, targetGeometry);
                measurements.Transform = transform;
                measurements.TransformSource =
                    "derived from the reference frames of both images (the API exposed no matrix)";
                measurements.RigidEulerAngles = transform.GetEulerAnglesDegrees();

                _log.Warning("transform",
                    "no matrix was obtained from the registration; the relative transform between the two " +
                    "image frames was used instead. This describes the difference in series framing, which " +
                    "only coincides with the registration if the latter is already baked into the geometry.");
                return;
            }

            measurements.TransformSource = "not available";
            _log.Failure("transform",
                "neither the registration matrix nor the image frames were accessible");
        }

        private double[,] TryReadMatrix(dynamic registration)
        {
            dynamic matrixObject;
            string source;

            if (!Dyn.TryGetFirst("transform: matrix", _log, out matrixObject, out source,
                    Dyn.Alt("RigidRegistration.Matrix", () => registration.RigidRegistration.Matrix),
                    Dyn.Alt("Matrix", () => registration.Matrix),
                    Dyn.Alt("TransformMatrix", () => registration.TransformMatrix),
                    Dyn.Alt("RigidTransformMatrix", () => registration.RigidTransformMatrix)))
            {
                return null;
            }

            var raw = new double[4, 4];

            bool read = Dyn.TryInvoke("transform: read matrix cells (" + source + ")", () =>
            {
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        raw[r, c] = Convert.ToDouble(matrixObject[r, c], CultureInfo.InvariantCulture);
                    }
                }
            }, _log);

            if (!read) return null;

            // An all-zero matrix indicates that [r,c] indexing is not what was expected.
            bool allZero = true;
            for (int r = 0; r < 4 && allZero; r++)
                for (int c = 0; c < 4 && allZero; c++)
                    if (Math.Abs(raw[r, c]) > 1e-12) allZero = false;

            if (allZero)
            {
                _log.Failure("transform: matrix",
                    "the matrix read via " + source + " is identically zero; [row,column] indexing " +
                    "does not match what was expected");
                return null;
            }

            return raw;
        }

        // ---------------------------------------------------------------- intensity

        private void ComputeIntensitySimilarity(
            dynamic registration,
            EsapiImageReader.LoadResult source,
            EsapiImageReader.LoadResult target,
            QaMeasurements measurements)
        {
            IPointMapper mapper = BuildPointMapper(registration, measurements);
            if (mapper == null)
            {
                string reason = measurements.IsDeformable
                    ? "this is a deformable registration and the API exposes neither the deformation " +
                      "vector field nor a point-by-point mapping method. Evaluating similarity using only " +
                      "the linear component would describe a different transform from the one under audit."
                    : "no valid transform is available with which to match voxels";

                measurements.Nmi = MeasuredValue.Unavailable(reason);
                measurements.Ncc = MeasuredValue.Unavailable(reason);
                measurements.Ssd = MeasuredValue.Unavailable(reason);
                return;
            }

            _log.Info("similarity: mapping", mapper.Description);

            VoxelPairSet pairs = VoxelPairSampler.Pair(source.Volume, target.Volume, mapper);
            measurements.SampleCount = pairs.Count;
            measurements.OverlapFraction = pairs.OverlapFraction;
            measurements.EffectiveSamplingMm = source.Volume.Geometry.CoarsestResolution;

            if (!string.IsNullOrEmpty(pairs.Problem))
            {
                measurements.Nmi = MeasuredValue.Unavailable(pairs.Problem);
                measurements.Ncc = MeasuredValue.Unavailable(pairs.Problem);
                measurements.Ssd = MeasuredValue.Unavailable(pairs.Problem);
                _log.Failure("similarity: pairing", pairs.Problem);
                return;
            }

            SimilarityResult similarity = SimilarityCalculator.Compute(
                pairs.FixedValues, pairs.MovingValues, pairs.Count);

            if (!string.IsNullOrEmpty(similarity.Problem))
            {
                measurements.Nmi = MeasuredValue.Unavailable(similarity.Problem);
                measurements.Ncc = MeasuredValue.Unavailable(similarity.Problem);
                measurements.Ssd = MeasuredValue.Unavailable(similarity.Problem);
                _log.Failure("similarity: computation", similarity.Problem);
                return;
            }

            string samplingNote = string.Format(CultureInfo.InvariantCulture,
                "{0:N0} voxel pairs, {1:P1} overlap, {2:F1} mm effective sampling",
                pairs.Count, pairs.OverlapFraction, measurements.EffectiveSamplingMm);

            measurements.Ncc = similarity.Ncc.HasValue
                ? MeasuredValue.Measured(similarity.Ncc.Value, samplingNote)
                : MeasuredValue.Unavailable("the correlation could not be computed");

            measurements.Ssd = similarity.Ssd.HasValue
                ? MeasuredValue.Measured(similarity.Ssd.Value, samplingNote)
                : MeasuredValue.Unavailable("zero intensity range in the reference image");

            measurements.Nmi = similarity.Nmi.HasValue
                ? MeasuredValue.Measured(similarity.Nmi.Value,
                    samplingNote + ", " + similarity.HistogramBins + "-bin joint histogram")
                : MeasuredValue.Unavailable("the joint histogram could not be built");

            _log.Info("similarity: result", samplingNote);
        }

        /// <summary>
        /// Builds the point mapping. For a deformable registration only a mapping that goes
        /// through the deformation field is valid; if the API does not offer one, null is
        /// returned and the intensity metrics stay unavailable, rather than being computed
        /// from the linear component and presented as if they described the deformable
        /// registration.
        /// </summary>
        private IPointMapper BuildPointMapper(dynamic registration, QaMeasurements measurements)
        {
            DynamicPointMapper deformableMapper = TryBuildDeformableMapper(registration);
            if (deformableMapper != null) return deformableMapper;

            if (measurements.IsDeformable) return null;

            if (measurements.Transform == null) return null;

            return new RigidPointMapper(
                measurements.Transform,
                "rigid matrix — " + measurements.TransformSource);
        }

        /// <summary>
        /// Looks up a point-mapping method on the registration object by reflection. It is
        /// probed with a real point and only accepted if it returns something finite: the
        /// mere existence of the method does not guarantee it is implemented in this version.
        /// </summary>
        private DynamicPointMapper TryBuildDeformableMapper(dynamic registration)
        {
            object registrationObject = registration;
            if (registrationObject == null) return null;

            Type registrationType = registrationObject.GetType();
            string[] candidateNames = { "TransformPoint", "MapPoint", "TransformPointToRegistered", "DeformPoint" };

            foreach (string name in candidateNames)
            {
                MethodInfo method = registrationType.GetMethod(
                    name, BindingFlags.Public | BindingFlags.Instance);

                if (method == null) continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1) continue;

                Type vectorType = parameters[0].ParameterType;
                Func<Vec3, object> toVector = BuildVectorFactory(vectorType);
                Func<object, Vec3?> fromVector = BuildVectorReader(method.ReturnType);

                if (toVector == null || fromVector == null)
                {
                    _log.Warning("deformable mapping: " + name,
                        "the method was found but the vector type " + vectorType.Name +
                        " could not be constructed or read");
                    continue;
                }

                Func<Vec3, Vec3?> map = point =>
                {
                    object result = method.Invoke(registrationObject, new[] { toVector(point) });
                    return fromVector(result);
                };

                // Probe: if it does not return a finite point, the method is not usable.
                try
                {
                    Vec3? probe = map(new Vec3(0, 0, 0));
                    if (!probe.HasValue || !probe.Value.IsFinite)
                    {
                        _log.Warning("deformable mapping: " + name, "the probe did not return a finite point");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("deformable mapping: " + name, "the probe failed — " + DiagnosticLog.Describe(ex));
                    continue;
                }

                _log.Info("deformable mapping",
                    "will use " + registrationType.Name + "." + name + " to map points through the registration");

                return new DynamicPointMapper(map,
                    "point-by-point mapping via " + name + " (traverses the deformation field)");
            }

            return null;
        }

        private static Func<Vec3, object> BuildVectorFactory(Type vectorType)
        {
            FieldInfo fx = vectorType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fy = vectorType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fz = vectorType.GetField("z", BindingFlags.Public | BindingFlags.Instance);

            if (fx != null && fy != null && fz != null)
            {
                return point =>
                {
                    object boxed = Activator.CreateInstance(vectorType);
                    fx.SetValue(boxed, point.X);
                    fy.SetValue(boxed, point.Y);
                    fz.SetValue(boxed, point.Z);
                    return boxed;
                };
            }

            ConstructorInfo constructor = vectorType.GetConstructor(
                new[] { typeof(double), typeof(double), typeof(double) });

            if (constructor != null)
                return point => constructor.Invoke(new object[] { point.X, point.Y, point.Z });

            return null;
        }

        private static Func<object, Vec3?> BuildVectorReader(Type vectorType)
        {
            if (vectorType == null || vectorType == typeof(void)) return null;

            FieldInfo fx = vectorType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fy = vectorType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fz = vectorType.GetField("z", BindingFlags.Public | BindingFlags.Instance);

            if (fx == null || fy == null || fz == null) return null;

            return value =>
            {
                if (value == null) return null;
                return new Vec3(
                    Convert.ToDouble(fx.GetValue(value), CultureInfo.InvariantCulture),
                    Convert.ToDouble(fy.GetValue(value), CultureInfo.InvariantCulture),
                    Convert.ToDouble(fz.GetValue(value), CultureInfo.InvariantCulture));
            };
        }

        // ---------------------------------------------------------------- deformation

        /// <summary>
        /// Topological metrics.
        ///
        /// For a rigid transform the Jacobian is 1 everywhere and smoothness is perfect:
        /// these are not estimates but consequences of the definition, and are labelled as
        /// such. Maximum displacement is computed exactly over the volume corners.
        ///
        /// For a deformable registration these three quantities require traversing the
        /// vector field, which the scripting API does not expose. They are returned as
        /// unavailable.
        /// </summary>
        private void ComputeDeformationMetrics(QaMeasurements measurements)
        {
            if (measurements.IsDeformable)
            {
                const string reason =
                    "requires traversing the deformation vector field (DVF), which the Varian scripting " +
                    "API does not expose. It cannot be derived from the linear matrix or from the images.";

                measurements.JacobianNegativePercent = MeasuredValue.Unavailable(reason);
                measurements.Smoothness = MeasuredValue.Unavailable(reason);
                measurements.MaxDisplacement = MeasuredValue.Unavailable(reason);
                return;
            }

            const string analyticNote = "exact by definition for a rigid transform, not estimated";

            measurements.JacobianNegativePercent = MeasuredValue.Measured(0.0,
                "|J| = 1 at every point of a rigid transform: no folding is possible. " + analyticNote);

            measurements.Smoothness = MeasuredValue.Measured(1.0,
                "the gradient of the displacement field is constant. " + analyticNote);

            if (measurements.Transform == null)
            {
                measurements.MaxDisplacement = MeasuredValue.Unavailable(
                    "no transform is available against which to evaluate displacement");
                return;
            }

            ImageGeometry geometry = _fixedGeometry;
            if (geometry == null)
            {
                // Without the volume extent, maximum displacement is undefined: it depends
                // on the size of the evaluated region, not on the transform alone.
                measurements.MaxDisplacement = MeasuredValue.Unavailable(
                    "could not determine the volume extent over which to evaluate displacement");
                return;
            }

            double maxDisplacement = measurements.Transform.MaxDisplacementOver(geometry);
            measurements.MaxDisplacement = MeasuredValue.Measured(maxDisplacement,
                "maximum over the eight FOV corners; exact because the displacement of an affine map " +
                "is convex and attains its maximum at a vertex");
        }

        // ---------------------------------------------------------------- structures

        /// <summary>
        /// DSC and HD95 require a matched pair: a reference contour and that same contour
        /// propagated through the registration, identifiable with each other.
        ///
        /// The previous version walked <c>StructureSets[0]</c>, summed volumes and hashes of
        /// the identifiers, and turned that sum into a DSC through modular arithmetic. The
        /// result bore no relation to any overlap.
        /// </summary>
        private void ComputeStructureMetrics(dynamic registration, QaMeasurements measurements)
        {
            int sourceStructures = CountStructures(() => registration.SourceImage.Image.StructureSets, "source image");
            int targetStructures = CountStructures(() => registration.RegisteredImage.Image.StructureSets, "registered image");

            string reason;

            if (sourceStructures == 0 || targetStructures == 0)
            {
                reason = "there are no contours on both series (source: " + sourceStructures +
                         ", registered: " + targetStructures + "). DSC and HD95 compare a reference contour " +
                         "with that same contour after propagation; with only one contoured series there is " +
                         "no pair to compare.";
            }
            else
            {
                reason = "contours exist on both series (source: " + sourceStructures +
                         ", registered: " + targetStructures + "), but computing DSC and HD95 requires " +
                         "rasterising both contours onto a common grid and matching them by identifier. " +
                         "Not implemented in this version.";
            }

            measurements.Dsc = MeasuredValue.Unavailable(reason);
            measurements.Hd95 = MeasuredValue.Unavailable(reason);

            _log.Warning("structures", reason);
        }

        private int CountStructures(Func<object> structureSetsAccessor, string label)
        {
            dynamic structureSets;
            if (!Dyn.TryGet("structures: StructureSets of " + label, structureSetsAccessor, _log, out structureSets))
                return 0;

            int count = 0;
            Dyn.TryInvoke("structures: count on " + label, () =>
            {
                foreach (dynamic structureSet in structureSets)
                {
                    foreach (dynamic structure in structureSet.Structures)
                    {
                        if (structure != null) count++;
                    }
                }
            }, _log);

            return count;
        }

        // ---------------------------------------------------------------- utilities

        private static void MarkAllUnavailable(QaMeasurements measurements, string reason)
        {
            measurements.Nmi = MeasuredValue.Unavailable(reason);
            measurements.Ncc = MeasuredValue.Unavailable(reason);
            measurements.Ssd = MeasuredValue.Unavailable(reason);
            measurements.JacobianNegativePercent = MeasuredValue.Unavailable(reason);
            measurements.MaxDisplacement = MeasuredValue.Unavailable(reason);
            measurements.Smoothness = MeasuredValue.Unavailable(reason);
            measurements.Dsc = MeasuredValue.Unavailable(reason);
            measurements.Hd95 = MeasuredValue.Unavailable(reason);
        }
    }
}
