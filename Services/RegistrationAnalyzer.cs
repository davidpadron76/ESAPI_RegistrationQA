using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        /// <summary>
        /// Region spanned by the matched contours, used wherever a region is needed and no
        /// volume could be loaded. Maximum displacement and inverse consistency both need
        /// somewhere to evaluate the transform over, and neither has any reason to insist that
        /// somewhere be an image.
        /// </summary>
        private ImageGeometry _structureGeometry;

        /// <summary>Forward mapping source → registered, shared by the similarity and TRE paths.</summary>
        private IPointMapper _mapper;

        /// <summary>
        /// Where stage changes go. Never null — a null sink is wrapped, so the reporting calls
        /// below need no guard.
        /// </summary>
        private readonly MeasurementProgress _progress;

        public RegistrationAnalyzer(DiagnosticLog log)
            : this(log, null)
        {
        }

        public RegistrationAnalyzer(DiagnosticLog log, IMeasurementProgressSink progressSink)
        {
            if (log == null) throw new ArgumentNullException("log");
            _log = log;
            _progress = new MeasurementProgress(progressSink);
        }

        public QaMeasurements Analyze(dynamic scriptContext)
        {
            var measurements = new QaMeasurements();

            _progress.Report(MeasurementStage.Starting);

            dynamic registration;
            if (!Dyn.TryGet("context: active registration", () => scriptContext.Registration, _log, out registration))
            {
                MarkAllUnavailable(measurements,
                    "there is no active registration in the Eclipse workspace");
                measurements.RegistrationId = "(no active registration)";
                _progress.Report(MeasurementStage.Finished);
                return measurements;
            }

            _progress.Report(MeasurementStage.IdentifyingRegistration);
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

            // Reported separately: these are the two slowest API reads in the pass, and when a
            // case stops moving it is almost always inside one of them.
            _progress.Report(MeasurementStage.LoadingSourceVolume);
            if (haveSource) source = EsapiImageReader.Load(sourceImage, "source image", _log);

            _progress.Report(MeasurementStage.LoadingRegisteredVolume);
            if (haveRegistered) target = EsapiImageReader.Load(registeredImage, "registered image", _log);

            if (source != null) measurements.FixedModality = source.Modality;
            if (target != null) measurements.MovingModality = target.Modality;

            if (source != null && source.Success) _fixedGeometry = source.Volume.Geometry;

            ReadFrameOfReference(sourceImage, registeredImage, measurements);

            // --- Rigid transform --------------------------------------------------------
            _progress.Report(MeasurementStage.ReadingTransform);
            ExtractRigidTransform(registration, measurements, source, target);

            // Built once and shared: the similarity sampling and the TRE both need to push
            // points through the same registration, and for a deformable case that mapping
            // is expensive to construct.
            _progress.Report(MeasurementStage.BuildingPointMapping);
            _mapper = BuildPointMapper(registration, measurements);
            if (_mapper != null) _log.Info("mapping", _mapper.Description);

            // --- Intensity similarity ---------------------------------------------------
            _progress.Report(MeasurementStage.IntensitySimilarity);
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

            // --- TG-132 Table III primary metrics ---------------------------------------
            // Structures come first because they establish the region that the deformation and
            // consistency metrics fall back on when no volume loads.
            RecordNativeVoxelSize(source, target, measurements);

            _progress.Report(MeasurementStage.StructureMetrics);
            ComputeStructureMetrics(sourceImage, registeredImage, measurements);

            // --- Deformation / topology -------------------------------------------------
            _progress.Report(MeasurementStage.DeformationField);
            ComputeDeformationMetrics(measurements, registration, sourceImage, registeredImage);

            _progress.Report(MeasurementStage.TargetRegistrationError);
            ComputeTargetRegistrationError(registration, sourceImage, registeredImage, measurements);

            _progress.Report(MeasurementStage.InverseConsistency);
            ComputeInverseConsistency(scriptContext, registration, sourceImage, registeredImage, measurements);

            foreach (DiagnosticEntry entry in _log.Entries)
                measurements.Diagnostics.Add(entry.ToString());

            _progress.Report(MeasurementStage.Finished);
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
            ReportRegistrationStatus(registration, measurements);

            dynamic value;
            string source;

            // The log is deliberately null here. On VMS.IRS neither property exists — the
            // registration object's member list confirms it — so their absence is the normal
            // case and not something to report as a failure. The class name is the real source
            // of this information on that API, and it is reliable: MIRSRigidRegistration and
            // MIRSNonRigidRegistration are distinct types.
            if (Dyn.TryGetFirst("registration: declared type", null, out value, out source,
                    Dyn.Alt("RegistrationType", () => registration.RegistrationType),
                    Dyn.Alt("Type", () => registration.Type)))
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                _log.Info("registration: type", text + " (via " + source + ")");
                ApplyTypeText(text, measurements);
                return;
            }

            string clrTypeName = "(unknown)";
            Dyn.TryInvoke("registration: CLR type name",
                () => { clrTypeName = ((object)registration).GetType().Name; }, _log);

            _log.Info("registration: type",
                "taken from the class name '" + clrTypeName + "'. This API exposes no " +
                "RegistrationType or Type property, so the class name is the only source; it " +
                "distinguishes rigid from non-rigid reliably, but a future version could rename " +
                "the class without notice.");

            ApplyTypeText(clrTypeName, measurements);
        }

        /// <summary>
        /// Records the registration's approval status where the API offers it.
        ///
        /// Purely contextual, and not turned into a metric: whether a registration has been
        /// approved in Eclipse says who signed it off, not whether it is geometrically
        /// accurate. But it belongs in a report that will be countersigned, because auditing a
        /// registration that was never approved for clinical use means something different
        /// from auditing one that was.
        /// </summary>
        private void ReportRegistrationStatus(dynamic registration, QaMeasurements measurements)
        {
            dynamic value;
            string source;

            if (!Dyn.TryGetFirst("registration: status", null, out value, out source,
                    Dyn.Alt("Status", () => registration.Status),
                    Dyn.Alt("ApprovalStatus", () => registration.ApprovalStatus)))
            {
                return;
            }

            string status = Convert.ToString(value, CultureInfo.InvariantCulture);
            measurements.RegistrationStatus = status;
            _log.Info("registration: status", status + " (via " + source + ")");
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

        // ---------------------------------------------------------------- frame of reference

        /// <summary>
        /// Reads the DICOM Frame of Reference UID of both series.
        ///
        /// It is read for one specific decision: whether the maximum displacement is a
        /// statement about the registration or about the two coordinate systems. Within a
        /// single frame of reference the identity is the "no correction" state, so the
        /// displacement is the correction being applied. Across two frames the matrix must
        /// also span the offset between them, which is a property of how the scanners were
        /// set up and can be arbitrarily large on a perfectly good registration.
        /// </summary>
        private void ReadFrameOfReference(
            dynamic sourceImage, dynamic registeredImage, QaMeasurements measurements)
        {
            measurements.FixedFrameOfReferenceUid = ReadFrameOfReferenceUid(sourceImage, "source image");
            measurements.MovingFrameOfReferenceUid = ReadFrameOfReferenceUid(registeredImage, "registered image");

            bool? shares = measurements.SharesFrameOfReference;

            if (!shares.HasValue)
            {
                _log.Warning("frame of reference",
                    "at least one series does not expose its Frame of Reference UID, so it cannot be " +
                    "established whether the two share a coordinate system. Maximum displacement is " +
                    "therefore reported without a classification: across two frames of reference its " +
                    "magnitude includes the offset between them and says nothing about the registration.");
                return;
            }

            _log.Info("frame of reference", shares.Value
                ? "both series share a Frame of Reference, so the maximum displacement is the correction " +
                  "the registration applies and is classified against the profile."
                : "the two series are in different Frames of Reference. The transform therefore spans two " +
                  "coordinate systems, and its magnitude is not a registration correction. Maximum " +
                  "displacement is reported without a classification.");
        }

        private string ReadFrameOfReferenceUid(dynamic imageLike, string label)
        {
            if (imageLike == null) return null;

            dynamic value;
            string source;
            if (Dyn.TryGetFirst("frame of reference of " + label, _log, out value, out source,
                    Dyn.Alt("Image.FOR", () => imageLike.Image.FOR),
                    Dyn.Alt("FOR", () => imageLike.FOR),
                    Dyn.Alt("Image.FrameOfReferenceUid", () => imageLike.Image.FrameOfReferenceUid),
                    Dyn.Alt("FrameOfReferenceUid", () => imageLike.FrameOfReferenceUid),
                    Dyn.Alt("Series.FOR", () => imageLike.Series.FOR)))
            {
                string uid = Convert.ToString(value, CultureInfo.InvariantCulture);
                _log.Info("frame of reference of " + label, uid + " (via " + source + ")");
                return uid;
            }

            return null;
        }

        // ---------------------------------------------------------------- transform

        private void ExtractRigidTransform(
            dynamic registration,
            QaMeasurements measurements,
            EsapiImageReader.LoadResult source,
            EsapiImageReader.LoadResult target,
            DiagnosticLog logOverride = null)
        {
            DiagnosticLog log = logOverride ?? _log;

            string matrixSource;
            // Cast so the call binds statically: with a dynamic argument the whole invocation
            // is deferred to the runtime binder, out parameter included, for no benefit.
            double[,] raw = MatrixReader.TryRead((object)registration, log, out matrixSource);

            if (raw != null)
            {
                RigidTransform transform;
                string note;
                if (RigidTransform.TryFromRawMatrix(raw, out transform, out note))
                {
                    measurements.Transform = transform;
                    measurements.TransformSource = "API matrix (" + matrixSource + ") — " + note;
                    measurements.RigidEulerAngles = transform.GetEulerAnglesDegrees();

                    log.Info("transform: matrix", note);

                    if (measurements.RigidEulerAngles.Value.GimbalLock)
                    {
                        log.Warning("transform: Euler angles",
                            "gimbal lock (pitch ≈ ±90°): only the combination of pitch and yaw is " +
                            "observable; yaw is reported as 0");
                    }
                    return;
                }

                log.Failure("transform: matrix", note);
            }

            // No transform. Everything downstream that needs one is left unavailable.
            //
            // Earlier versions fell back to the relative transform between the two image
            // frames. That fallback has been removed, because it is not an approximation of
            // the registration — it is a different quantity, and a misleading one:
            //
            //  * Patient coordinates are already common to both series through the frame of
            //    reference, so the difference between the two frames is the difference in
            //    where each scan started, not a correction applied by the registration. On a
            //    real pair it produced a "maximum displacement" of 171 mm and turned the
            //    verdict red on the strength of a number nobody had measured.
            //  * Fed to the point mapper it maps voxel (i,j,k) of one series onto voxel
            //    (i,j,k) of the other, which is exactly the index-wise comparison this tool
            //    was rewritten to eliminate. The overlap then comes out at 100 % by
            //    construction, so even the provenance line stops being informative.
            //
            // The frame offset is still worth reporting, so it is logged — as an observation
            // about the two series, clearly not as the registration.
            measurements.TransformSource =
                "not available — the API exposed no registration matrix";

            ReportFrameOffset(source, target);

            log.Failure("transform",
                "no registration matrix could be read, so every metric that requires mapping a point " +
                "through the registration is reported as unavailable rather than estimated. The " +
                "member list of the registration object is in this tab: sending it with the Eclipse " +
                "version is what allows the correct property to be probed directly.");
        }

        /// <summary>
        /// Logs how far apart the two series frames are. Purely descriptive: it says something
        /// about how the images were acquired, nothing about the registration.
        /// </summary>
        private void ReportFrameOffset(
            EsapiImageReader.LoadResult source, EsapiImageReader.LoadResult target)
        {
            if (source == null || target == null || !source.Success || !target.Success) return;

            Vec3 offset = target.Volume.Geometry.Origin - source.Volume.Geometry.Origin;

            _log.Info("image frames", string.Format(CultureInfo.InvariantCulture,
                "the two series origins differ by ({0:F1}, {1:F1}, {2:F1}) mm, magnitude {3:F1} mm. " +
                "This is where each scan started, not a registration displacement, and it is not " +
                "used as a transform.",
                offset.X, offset.Y, offset.Z, offset.Length));
        }

        // ---------------------------------------------------------------- intensity

        private void ComputeIntensitySimilarity(
            dynamic registration,
            EsapiImageReader.LoadResult source,
            EsapiImageReader.LoadResult target,
            QaMeasurements measurements)
        {
            IPointMapper mapper = _mapper;
            if (mapper == null)
            {
                measurements.Nmi = MeasuredValue.Unavailable(NoMappingReason(measurements));
                measurements.Ncc = MeasuredValue.Unavailable(NoMappingReason(measurements));
                measurements.Ssd = MeasuredValue.Unavailable(NoMappingReason(measurements));
                return;
            }

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
        private IPointMapper BuildPointMapper(
            dynamic registration, QaMeasurements measurements, DiagnosticLog logOverride = null)
        {
            DiagnosticLog log = logOverride ?? _log;

            // The API's own method comes first, for every kind of registration. The probe found
            // it on the nested RigidRegistration object as TransformPoint(VVector), and it is
            // strictly better than the matrix: no convention to detect, no orthonormality to
            // verify, and a direction that is right by construction rather than by inference.
            // Restricting this search to deformable registrations is what hid it.
            DynamicPointMapper apiMapper = PointMapperReader.TryBuild((object)registration, log);
            if (apiMapper != null) return apiMapper;

            // For a deformable registration there is no second option: applying the linear
            // component would describe a different transform from the one under audit.
            if (measurements.IsDeformable) return null;

            if (measurements.Transform == null) return null;

            return new RigidPointMapper(
                measurements.Transform,
                "rigid matrix — " + measurements.TransformSource);
        }

        // ---------------------------------------------------------------- deformation

        /// <summary>
        /// Topological metrics.
        ///
        /// For a rigid transform the Jacobian is 1 everywhere and smoothness is perfect:
        /// these are not estimates but consequences of the definition, and are labelled as
        /// such. Maximum displacement is computed exactly over the volume corners.
        ///
        /// For a deformable registration all three come from the vector field itself, read by
        /// <see cref="DeformationFieldReader"/>. They used to be reported as permanently
        /// unobtainable on the belief that the scripting API exposed no field; it does, and a
        /// probe against a real case read non-uniform displacements out of it. When the field
        /// cannot be read on a given Eclipse version they fall back to unavailable with the
        /// specific reason, which is a fault to report rather than a limitation to accept.
        /// </summary>
        private void ComputeDeformationMetrics(
            QaMeasurements measurements, dynamic registration,
            dynamic sourceImage, dynamic registeredImage)
        {
            if (measurements.IsDeformable)
            {
                ComputeDeformationMetricsFromField(
                    measurements, registration, sourceImage, registeredImage);
                return;
            }

            const string analyticNote = "exact by definition for a rigid transform, not estimated";

            // Analytic, not measured: these two follow from the transform being rigid and come
            // out identical on every rigid registration. They are shown, because they are true
            // statements a signed report should carry, but they cannot count as evidence that
            // this particular registration was verified.
            measurements.JacobianNegativePercent = MeasuredValue.Analytic(0.0,
                "|J| = 1 at every point of a rigid transform: no folding is possible. " + analyticNote);

            measurements.Smoothness = MeasuredValue.Analytic(1.0,
                "the gradient of the displacement field is constant. " + analyticNote);

            // The measured counterpart only exists for a deformable field. On a rigid transform
            // it would be |R - I|, a restatement of the rotation already in the transform table.
            measurements.DvfGradientMax = MeasuredValue.NotApplicable(
                "this is a rigid registration: the displacement gradient is constant, so there is no " +
                "local irregularity to measure. Smoothness carries that statement instead.");

            // The second clause of the Table III Jacobian row. A rigid transform preserves volume
            // everywhere, so the departure from 1 is exactly zero — true by definition, like the
            // 0 % above, and marked analytic for the same reason.
            measurements.JacobianDeparture = MeasuredValue.Analytic(0.0,
                "|J| = 1 at every point of a rigid transform, so no volume change is applied. " +
                analyticNote);

            if (measurements.Transform == null)
            {
                measurements.MaxDisplacement = MeasuredValue.Unavailable(
                    "the registration matrix could not be read from the API, so there is no transform " +
                    "whose displacement could be evaluated. The difference between the two series " +
                    "origins is not a substitute: it describes how the scans were framed, not what the " +
                    "registration does.");
                return;
            }

            // The image extent when there is one, the contoured region otherwise. Maximum
            // displacement depends on the size of the region evaluated, not on the transform
            // alone, so the region used is named in the note.
            ImageGeometry geometry = _fixedGeometry ?? _structureGeometry;
            if (geometry == null)
            {
                measurements.MaxDisplacement = MeasuredValue.Unavailable(
                    "no region could be established over which to evaluate displacement: neither " +
                    "image volume loaded and no matched structures were found");
                return;
            }

            double maxDisplacement = measurements.Transform.MaxDisplacementOver(geometry);
            measurements.MaxDisplacement = MeasuredValue.Measured(maxDisplacement,
                "maximum over the eight FOV corners; exact because the displacement of an affine map " +
                "is convex and attains its maximum at a vertex");
        }

        /// <summary>
        /// Why Smoothness is hidden on a deformable case: its 1.0 is a statement about rigid
        /// transforms, and the measured quantity lives in DvfGradientMax on the opposite scale.
        /// </summary>
        private const string SmoothnessIsRigidOnly =
            "this metric states that a rigid transform has a constant field gradient, which says " +
            "nothing about a deformable one. The measured equivalent is reported as the DVF gradient, " +
            "on the opposite scale: 0 there means what 1.0 means here.";

        /// <summary>
        /// The deformable case: Jacobian, DVF gradient and maximum displacement from the vector
        /// field, or all three unavailable with the same single reason when it cannot be read.
        ///
        /// One read failure is one fault, not three — the three share the field, so splitting
        /// the reason across them would make one API difference look like three separate
        /// problems.
        /// </summary>
        private void ComputeDeformationMetricsFromField(
            QaMeasurements measurements, dynamic registration,
            dynamic sourceImage, dynamic registeredImage)
        {
            // Hidden regardless of whether the field reads: it is a rigid-transform statement,
            // so on a deformable case it is inapplicable rather than merely missing.
            measurements.Smoothness = MeasuredValue.NotApplicable(SmoothnessIsRigidOnly);

            DeformationFieldReader.Result field =
                DeformationFieldReader.TryRead((object)registration, _log);

            if (field == null)
            {
                const string reason =
                    "the deformation vector field could not be read from this registration. The " +
                    "diagnostics tab lists what the registration object exposes in this Eclipse " +
                    "version — the field is read via DeformationField.GetVectors, confirmed present on " +
                    "VMS.IRS. The linear component is not used as a substitute: it describes a " +
                    "different transform from the one under audit.";

                measurements.JacobianNegativePercent = MeasuredValue.Unavailable(reason);
                measurements.JacobianDeparture = MeasuredValue.Unavailable(reason);
                measurements.DvfGradientMax = MeasuredValue.Unavailable(reason);
                measurements.MaxDisplacement = MeasuredValue.Unavailable(reason);
                return;
            }

            string problem;
            DeformationFieldMetrics.Result metrics = DeformationFieldMetrics.Compute(field, out problem);

            // Logged once rather than repeated in every metric's note. It was appended to all
            // four, which pushed the criterion column past what fits on screen even maximised —
            // and the geometry is the same sentence every time, so the table was carrying four
            // copies of a fact that belongs in the diagnostics.
            _log.Info("deformation field: grid", string.Format(CultureInfo.InvariantCulture,
                "{0}x{1}x{2} at {3:F2}/{4:F2}/{5:F2} mm — the field's own spacing, coarser than the " +
                "image grid, and what every derivative below uses",
                field.XSize, field.YSize, field.ZSize, field.XResMm, field.YResMm, field.ZResMm));

            string gridNote = string.Format(CultureInfo.InvariantCulture,
                "field grid {0}x{1}x{2}", field.XSize, field.YSize, field.ZSize);

            if (metrics == null)
            {
                string reason = "the deformation field was read, but " +
                                (problem ?? "no metric could be computed from it") + ".";

                measurements.JacobianNegativePercent = MeasuredValue.Unavailable(reason);
                measurements.JacobianDeparture = MeasuredValue.Unavailable(reason);
                measurements.DvfGradientMax = MeasuredValue.Unavailable(reason);

                // Maximum displacement needs no derivative, so a grid too thin for a central
                // difference does not stop it. Recomputed here rather than reaching into the
                // failed Result.
                double maxDisplacement = 0.0;
                for (int z = 0; z < field.ZSize; z++)
                    for (int y = 0; y < field.YSize; y++)
                        for (int x = 0; x < field.XSize; x++)
                        {
                            double length = field.Vectors[x, y, z].Length;
                            if (length > maxDisplacement) maxDisplacement = length;
                        }

                measurements.MaxDisplacement = MeasuredValue.Measured(maxDisplacement,
                    "max over every field point; " + gridNote);
                _log.Warning("deformation metrics", reason);
                return;
            }

            measurements.JacobianNegativePercent = MeasuredValue.Measured(
                metrics.NegativeJacobianPercent,
                string.Format(CultureInfo.InvariantCulture,
                    "min |J| {0:F4} over {1:N0} pts{2}",
                    metrics.MinJacobian, metrics.JacobianSampleCount,
                    DescribeFoldingLocation(metrics)));

            measurements.JacobianDeparture = MeasuredValue.Measured(
                metrics.MaxDepartureFromOne,
                string.Format(CultureInfo.InvariantCulture,
                    "|J| p1 {0:F3}, median {1:F3}, p99 {2:F3}",
                    metrics.JacobianP1, metrics.JacobianMedian, metrics.JacobianP99));

            // Diagnostics, not a table row: divergence is not a TG-132 metric and adding it to the
            // graded surface would be adding a criterion the report does not have. Its worth is as
            // a second quantity to hold against Eclipse's own display of the same field.
            _log.Info("deformation field: cross-checks", string.Format(CultureInfo.InvariantCulture,
                "Hold these against Eclipse's own views of the same field. " +
                "Jacobian {0:F2} to {1:F2} · divergence {2:F2} to {3:F2} · distance 0 to {4:F1} mm · " +
                "max |curl u| {5:F2}. None is a TG-132 metric except the Jacobian; they are here " +
                "because each is derived from the same field read, so a disagreement in any of them " +
                "points at the read rather than at the registration.",
                metrics.MinJacobian, metrics.MaxJacobian,
                metrics.MinDivergence, metrics.MaxDivergence,
                metrics.MaxDisplacementMm, metrics.MaxCurlMagnitude));

            // Curl is the one quantity that did not match Eclipse on the phantom case (2.15
            // against 1.99, everything else exact). This line is here to distinguish the leading
            // explanation — that the true maximum lies in the outermost shell, which a central
            // difference cannot evaluate — from any other.
            if (metrics.MaxCurlAt != null)
            {
                _log.Info("deformation field: curl location", string.Format(CultureInfo.InvariantCulture,
                    "max |curl u| {0:F2} at grid ({1}, {2}, {3}) of {4}x{5}x{6}, {7} step(s) from the " +
                    "nearest face of the evaluated region. Zero would mean it sits on the first " +
                    "evaluated layer with the excluded outer shell just beyond it, which would explain " +
                    "a larger value from an implementation that evaluates the full grid.",
                    metrics.MaxCurlMagnitude,
                    metrics.MaxCurlAt[0], metrics.MaxCurlAt[1], metrics.MaxCurlAt[2],
                    field.XSize, field.YSize, field.ZSize, metrics.MaxCurlStepsFromEdge));
            }

            // Separate line because it answers a different question: not whether the field was read
            // correctly, but whether this code's X/Y/Z are Eclipse's. VALIDATION.md test 2 calls the
            // axis convention the largest open risk in the project, and an earlier version did have
            // Y and Z transposed.
            _log.Info("deformation field: displacement per axis", string.Format(CultureInfo.InvariantCulture,
                "X (LR) {0:F2} to {1:F2} mm · Y (AP) {2:F2} to {3:F2} mm · Z (CC) {4:F2} to {5:F2} mm. " +
                "If Eclipse displays the field per component, these three ranges settle whether the " +
                "axis labelling matches without needing a phantom shifted by a known amount.",
                metrics.MinDisplacementPerAxis[0], metrics.MaxDisplacementPerAxis[0],
                metrics.MinDisplacementPerAxis[1], metrics.MaxDisplacementPerAxis[1],
                metrics.MinDisplacementPerAxis[2], metrics.MaxDisplacementPerAxis[2]));

            LogFoldingEvidence(field, metrics);
            LogPerStructureJacobian(field, metrics, sourceImage, registeredImage);

            measurements.DvfGradientMax = MeasuredValue.Measured(
                metrics.MaxGradientMagnitude,
                "largest \u2016grad u\u2016 over the interior points");

            measurements.MaxDisplacement = MeasuredValue.Measured(
                metrics.MaxDisplacementMm,
                "max over every field point, not an eight-corner bound; " + gridNote);
        }

        /// <summary>
        /// A short clause naming where the folding sits, for the criterion column. Empty when
        /// nothing folded.
        /// </summary>
        private static string DescribeFoldingLocation(DeformationFieldMetrics.Result metrics)
        {
            if (metrics.NegativeJacobianPercent <= 0.0) return string.Empty;

            double fraction = metrics.NegativeBoundaryFraction;

            // Terse: this lands in a table cell beside the criterion. The full reading — what
            // edge-confined folding usually means, and what TG-132 asks be evaluated — goes to
            // the diagnostics in LogFoldingEvidence.
            if (fraction >= 0.95)
                return ", all at the field edge";
            if (fraction >= 0.5)
                return string.Format(CultureInfo.InvariantCulture,
                    ", {0:P0} at the field edge", fraction);

            return string.Format(CultureInfo.InvariantCulture,
                ", {0:P0} inside the field", 1.0 - fraction);
        }

        /// <summary>
        /// The evidence a physicist needs to act on a folding result, which a percentage alone
        /// does not carry: where it is, and whether the field's Jacobian is the registration's.
        ///
        /// TG-132 asks for the influence of folding on the intended use to be evaluated rather
        /// than treating any non-zero value as disqualifying outright. That judgement needs to
        /// know whether the folded region is in the anatomy or hard against the boundary of the
        /// field's support, where the algorithm had no image to work from.
        /// </summary>
        private void LogFoldingEvidence(
            DeformationFieldReader.Result field, DeformationFieldMetrics.Result metrics)
        {
            if (metrics.NegativeJacobianPercent <= 0.0)
            {
                _log.Info("jacobian", "no folding: det(I + grad u) is positive at every interior grid point.");
                return;
            }

            int negative = (int)Math.Round(
                metrics.NegativeJacobianPercent * metrics.JacobianSampleCount / 100.0);

            var text = new System.Text.StringBuilder();
            text.AppendFormat(CultureInfo.InvariantCulture,
                "{0:N0} of {1:N0} interior points fold (minimum determinant {2:F4}). ",
                negative, metrics.JacobianSampleCount, metrics.MinJacobian);

            if (metrics.NegativeBoundsMin != null)
            {
                text.AppendFormat(CultureInfo.InvariantCulture,
                    "They span grid indices x {0}-{1}, y {2}-{3}, z {4}-{5} of a {6}x{7}x{8} field; ",
                    metrics.NegativeBoundsMin[0], metrics.NegativeBoundsMax[0],
                    metrics.NegativeBoundsMin[1], metrics.NegativeBoundsMax[1],
                    metrics.NegativeBoundsMin[2], metrics.NegativeBoundsMax[2],
                    field.XSize, field.YSize, field.ZSize);
            }

            text.AppendFormat(CultureInfo.InvariantCulture,
                "{0:N0} of them ({1:P0}) lie within two grid steps of the edge of the field. ",
                metrics.NegativeNearBoundary, metrics.NegativeBoundaryFraction);

            text.Append(metrics.NegativeBoundaryFraction >= 0.95
                ? "Folding confined to the edge of the field's support is usually the algorithm " +
                  "running out of image to drive it rather than a statement about the anatomy — " +
                  "TG-132 asks for the influence on the intended use to be evaluated, and a region " +
                  "outside the patient may have none."
                : "Folding away from the edge is inside the field's support, which is the case " +
                  "TG-132 is concerned with: evaluate its influence on the intended use before " +
                  "relying on this registration for dose accumulation or contour propagation.");

            _log.Warning("jacobian", text.ToString());
        }

        /// <summary>
        /// The Jacobian inside each contoured structure, which is how TG-132 Table III states the
        /// criterion — "structures where volume reduction is expected", "where expansion is
        /// expected" — rather than over the whole grid.
        ///
        /// The distinction is not academic. The field's grid is a box around the patient, and on a
        /// head case most of it is air. A deformable algorithm has no image to constrain it out
        /// there and folds freely, so a whole-field folding percentage can be dominated by voxels
        /// that say nothing about the anatomy. The existing "away from the edge" figure does not
        /// separate these: it measures distance from the grid boundary, not from the patient, so a
        /// point in air in the middle of the box counts as interior.
        ///
        /// Reported per structure and left to the physicist. TG-132 ties the acceptable departure
        /// from 1 to what was expected of that structure, which this tool cannot know.
        /// </summary>
        private void LogPerStructureJacobian(
            DeformationFieldReader.Result field, DeformationFieldMetrics.Result whole,
            dynamic sourceImage, dynamic registeredImage)
        {
            if (field.Geometry == null)
            {
                _log.Info("jacobian per structure",
                    "the deformation field did not expose its origin and direction cosines, so a " +
                    "structure cannot be placed on its grid. The Jacobian above covers the whole " +
                    "field, including whatever air the grid encloses.");
                return;
            }

            // Which image's structures share the field's frame is not something the API states,
            // and getting it wrong is silent: the mask lands in the wrong place and the Jacobian
            // is computed over the wrong voxels while still looking like a number. On the phantom
            // case the field's box sat 123 mm from BODY in Z — almost exactly the registration's
            // own Z translation — because the grid is built on the registered image's geometry
            // while the structures read were the source's.
            //
            // So both are read and the one whose structures actually line up with the field is
            // used, rather than assuming either. Coverage is the test: a frame mismatch shows up
            // as a collapse in how much of the structure the field's box contains.
            List<StructureRasterizer.NamedStructure> structures =
                ChooseStructuresMatchingField(field, sourceImage, registeredImage);

            if (structures == null || structures.Count == 0)
            {
                _log.Info("jacobian per structure",
                    "no contour structures could be placed on the deformation field's grid, so the " +
                    "Jacobian could only be evaluated over the whole field.");
                return;
            }

            foreach (StructureRasterizer.NamedStructure structure in structures)
            {
                bool[] mask;
                try
                {
                    // No mapper: the structure is drawn on the source image and the field's grid is
                    // already in patient coordinates, so the two share a frame.
                    mask = StructureRasterizer.Rasterise(structure.Contours, field.Geometry, null);
                }
                catch (Exception ex)
                {
                    _log.Warning("jacobian per structure: " + structure.Id,
                        "could not be rasterised onto the field's grid — " + DiagnosticLog.Describe(ex));
                    continue;
                }

                int inside = 0;
                for (int i = 0; i < mask.Length; i++) if (mask[i]) inside++;

                if (inside == 0)
                {
                    // Say which of the possible causes it is rather than listing them. A structure
                    // that sits outside the field's extent is a property of the case; one that
                    // sits inside it and still catches nothing is a fault worth chasing.
                    _log.Warning("jacobian per structure: " + structure.Id,
                        "no grid point of the deformation field falls inside this structure. " +
                        DescribeStructureAgainstGrid(structure, field.Geometry));
                    continue;
                }

                string problem;
                DeformationFieldMetrics.Result within =
                    DeformationFieldMetrics.Compute(field, mask, out problem);

                if (within == null)
                {
                    _log.Warning("jacobian per structure: " + structure.Id,
                        problem ?? "no Jacobian could be computed inside this structure");
                    continue;
                }

                double voxelMm3 = field.XResMm * field.YResMm * field.ZResMm;

                _log.Info("jacobian per structure: " + structure.Id, string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F3} % folding over {1:N0} interior points inside the structure " +
                    "(whole field: {2:F3} % over {3:N0}). |J| min {4:F4}, p1 {5:F3}, median {6:F3}, " +
                    "p99 {7:F3}, max {8:F4}. The mask holds {9:N0} grid points, {10:P1} of the grid, " +
                    "about {11:N0} cm3 at this spacing. {12}{13}",
                    within.NegativeJacobianPercent, within.JacobianSampleCount,
                    whole.NegativeJacobianPercent, whole.JacobianSampleCount,
                    within.MinJacobian, within.JacobianP1, within.JacobianMedian,
                    within.JacobianP99, within.MaxJacobian,
                    inside, (double)inside / mask.Length, inside * voxelMm3 / 1000.0,
                    DescribeCoverage(structure, field.Geometry),
                    structure.IsSurfaceOutline
                        ? " This is the patient outline (" + structure.SurfaceOutlineReason +
                          "), so it is the closest thing here to \u201Cinside the patient\u201D: folding " +
                          "well below the whole-field figure means most of it lies in air the grid " +
                          "encloses but the patient does not occupy."
                        : string.Empty));
            }
        }

        /// <summary>
        /// Picks whichever image's structures share the deformation field's frame of reference.
        ///
        /// The field's grid is its own geometry and the API does not say which image it belongs
        /// to. On the phantom case it was the registered image's — same 5.000 mm slice spacing,
        /// in-plane resolution exactly twice the registered image's — so rasterising the source
        /// image's contours onto it displaced every mask by the registration's own translation,
        /// 123 mm in Z. The Jacobian was then evaluated over air while reporting a structure's
        /// name, which is worse than not reporting it.
        ///
        /// Decided by measurement rather than by rule: the candidate whose bounding boxes overlap
        /// the field's by the greater fraction wins. A frame mismatch collapses that overlap, so
        /// the two are easy to tell apart, and the choice is logged.
        /// </summary>
        private List<StructureRasterizer.NamedStructure> ChooseStructuresMatchingField(
            DeformationFieldReader.Result field, dynamic sourceImage, dynamic registeredImage)
        {
            List<StructureRasterizer.NamedStructure> fromSource = sourceImage == null
                ? new List<StructureRasterizer.NamedStructure>()
                : StructureRasterizer.ReadContourStructures(sourceImage, "source image", _log);

            List<StructureRasterizer.NamedStructure> fromRegistered = registeredImage == null
                ? new List<StructureRasterizer.NamedStructure>()
                : StructureRasterizer.ReadContourStructures(registeredImage, "registered image", _log);

            double sourceOverlap = MeanBoxOverlapWithField(fromSource, field.Geometry);
            double registeredOverlap = MeanBoxOverlapWithField(fromRegistered, field.Geometry);

            bool useRegistered = registeredOverlap > sourceOverlap;

            _log.Info("jacobian per structure: frame", string.Format(CultureInfo.InvariantCulture,
                "the field's grid encloses {0:P0} of the source image's structures and {1:P0} of " +
                "the registered image's, so the {2} image's are the ones sharing its frame and are " +
                "the ones used. The API does not state which image a deformation field belongs to, " +
                "and rasterising the wrong one displaces every mask by the registration's own " +
                "translation while still producing a number.",
                sourceOverlap, registeredOverlap, useRegistered ? "registered" : "source"));

            if (Math.Max(sourceOverlap, registeredOverlap) < 0.05)
            {
                _log.Warning("jacobian per structure: frame",
                    "neither image's structures fall meaningfully inside the deformation field's " +
                    "grid. A per-structure Jacobian is not reported, because a mask that does not " +
                    "overlap the field would evaluate the metric over the wrong voxels.");
                return null;
            }

            return useRegistered ? fromRegistered : fromSource;
        }

        /// <summary>
        /// Mean fraction of each structure's bounding box that falls inside the field's grid box.
        /// A bounding-box test, not a rasterisation: it only has to separate "same frame" from
        /// "displaced by a registration", and that difference is large.
        /// </summary>
        private static double MeanBoxOverlapWithField(
            List<StructureRasterizer.NamedStructure> structures, ImageGeometry grid)
        {
            if (structures == null || structures.Count == 0) return 0.0;

            Vec3 boxLo, boxHi;
            GridBounds(grid, out boxLo, out boxHi);

            double total = 0.0;
            int counted = 0;

            foreach (StructureRasterizer.NamedStructure structure in structures)
            {
                Vec3 lo, hi;
                if (structure.Contours == null || !structure.Contours.TryGetBounds(out lo, out hi))
                    continue;

                double own = Span(lo.X, hi.X) * Span(lo.Y, hi.Y) * Span(lo.Z, hi.Z);
                if (own <= 0.0) continue;

                double shared =
                    Span(Math.Max(lo.X, boxLo.X), Math.Min(hi.X, boxHi.X)) *
                    Span(Math.Max(lo.Y, boxLo.Y), Math.Min(hi.Y, boxHi.Y)) *
                    Span(Math.Max(lo.Z, boxLo.Z), Math.Min(hi.Z, boxHi.Z));

                total += shared / own;
                counted++;
            }

            return counted == 0 ? 0.0 : total / counted;
        }

        private static double Span(double low, double high)
        {
            return high > low ? high - low : 0.0;
        }

        /// <summary>
        /// How much of a structure the deformation field actually spans, for the line that
        /// reports its Jacobian.
        ///
        /// The field's grid is its own box and need not cover the patient. On the phantom case it
        /// spanned 184x200x190 mm against a BODY of 236x264x385 mm, so the mask was BODY
        /// intersected with the field rather than BODY — and a folding percentage computed there
        /// is a statement about that intersection, not about the whole structure. Saying so is
        /// the difference between a bounded claim and an overreaching one.
        /// </summary>
        private static string DescribeCoverage(
            StructureRasterizer.NamedStructure structure, ImageGeometry grid)
        {
            Vec3 lo, hi;
            if (structure.Contours == null || !structure.Contours.TryGetBounds(out lo, out hi))
                return string.Empty;

            Vec3 boxLo, boxHi;
            GridBounds(grid, out boxLo, out boxHi);

            bool contained =
                lo.X >= boxLo.X && hi.X <= boxHi.X &&
                lo.Y >= boxLo.Y && hi.Y <= boxHi.Y &&
                lo.Z >= boxLo.Z && hi.Z <= boxHi.Z;

            if (contained)
                return "The field spans the whole structure, so this covers all of it.";

            return string.Format(CultureInfo.InvariantCulture,
                "The field does NOT span the whole structure — structure X {0:F0}..{1:F0}, " +
                "Y {2:F0}..{3:F0}, Z {4:F0}..{5:F0} mm against field X {6:F0}..{7:F0}, " +
                "Y {8:F0}..{9:F0}, Z {10:F0}..{11:F0} mm — so this figure describes only the part " +
                "of it the field covers, not the structure as a whole.",
                lo.X, hi.X, lo.Y, hi.Y, lo.Z, hi.Z,
                boxLo.X, boxHi.X, boxLo.Y, boxHi.Y, boxLo.Z, boxHi.Z);
        }

        private static void GridBounds(ImageGeometry grid, out Vec3 low, out Vec3 high)
        {
            Vec3 a = grid.VoxelToPatient(0, 0, 0);
            Vec3 b = grid.VoxelToPatient(grid.XSize - 1, grid.YSize - 1, grid.ZSize - 1);

            // The grid's axes need not run low-to-high in patient coordinates.
            low = new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
            high = new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        }

        /// <summary>
        /// Where a structure sits relative to the deformation field's grid, for the case where
        /// the mask came back empty. Distinguishes a structure outside the field's extent — a
        /// property of the case — from one inside it that still caught nothing, which is a fault.
        /// </summary>
        private static string DescribeStructureAgainstGrid(
            StructureRasterizer.NamedStructure structure, ImageGeometry grid)
        {
            Vec3 lo, hi;
            if (structure.Contours == null || !structure.Contours.TryGetBounds(out lo, out hi))
                return "Its contours expose no bounds, so it cannot be placed against the grid.";

            Vec3 boxLo, boxHi;
            GridBounds(grid, out boxLo, out boxHi);

            bool overlaps =
                lo.X <= boxHi.X && hi.X >= boxLo.X &&
                lo.Y <= boxHi.Y && hi.Y >= boxLo.Y &&
                lo.Z <= boxHi.Z && hi.Z >= boxLo.Z;

            string extents = string.Format(CultureInfo.InvariantCulture,
                "Structure spans X {0:F1}..{1:F1}, Y {2:F1}..{3:F1}, Z {4:F1}..{5:F1} mm; the field's " +
                "grid spans X {6:F1}..{7:F1}, Y {8:F1}..{9:F1}, Z {10:F1}..{11:F1} mm. ",
                lo.X, hi.X, lo.Y, hi.Y, lo.Z, hi.Z,
                boxLo.X, boxHi.X, boxLo.Y, boxHi.Y, boxLo.Z, boxHi.Z);

            if (!overlaps)
                return extents + "They do not overlap, so the deformation field simply does not " +
                       "cover this structure — nothing to fix, but nothing to report either.";

            return extents + "They do overlap, so the structure is not outside the field. Either it " +
                   "is thinner than the grid's spacing along some axis — " +
                   string.Format(CultureInfo.InvariantCulture, "{0:F2}/{1:F2}/{2:F2} mm here — ",
                       grid.XRes, grid.YRes, grid.ZRes) +
                   "or the rasterisation is not finding it, which would be a fault worth reporting.";
        }

        // ---------------------------------------------------------------- structures

        /// <summary>
        /// DSC, MDA and HD95 over the contour structures present on both series.
        ///
        /// Both structures are rasterised onto the same sampling grid — the one belonging to
        /// the source image — with the registered structure carried through the registration
        /// mapping first. From there the three metrics share a single distance transform.
        ///
        /// When several structures match, the worst case is reported rather than an average.
        /// A mean across organs of very different size says nothing: a Dice of 0.85 is
        /// excellent for a parotid and poor for a whole lung, so averaging them produces a
        /// number that describes neither.
        ///
        /// The previous version walked <c>StructureSets[0]</c>, summed volumes and hashes of
        /// the identifiers, and turned that sum into a DSC through modular arithmetic. The
        /// result bore no relation to any overlap.
        /// </summary>
        private void ComputeStructureMetrics(
            dynamic sourceImage, dynamic registeredImage, QaMeasurements measurements)
        {
            if (sourceImage == null || registeredImage == null)
            {
                const string reason = "the registration does not expose both images, so structures cannot be paired";
                measurements.Dsc = MeasuredValue.Unavailable(reason);
                measurements.Mda = MeasuredValue.Unavailable(reason);
                measurements.Hd95 = MeasuredValue.Unavailable(reason);
                return;
            }

            // Only the mapping is required. The image geometry used to be required as well,
            // because the comparison grid was derived from the loaded volume — a dependency
            // that was never real. Contours carry their own patient coordinates, so these three
            // metrics work on an installation that will not hand over a single voxel, which is
            // exactly the situation this tool is in. They are also the TG-132 Table III metrics,
            // so what survives is the part that matters most.
            if (_mapper == null)
            {
                measurements.Dsc = MeasuredValue.Unavailable(NoMappingReason(measurements));
                measurements.Mda = MeasuredValue.Unavailable(NoMappingReason(measurements));
                measurements.Hd95 = MeasuredValue.Unavailable(NoMappingReason(measurements));
                return;
            }

            List<StructureRasterizer.NamedStructure> sourceStructures =
                StructureRasterizer.ReadContourStructures(sourceImage, "source image", _log);
            List<StructureRasterizer.NamedStructure> targetStructures =
                StructureRasterizer.ReadContourStructures(registeredImage, "registered image", _log);

            var targetById = new Dictionary<string, StructureRasterizer.NamedStructure>(
                StringComparer.OrdinalIgnoreCase);
            foreach (StructureRasterizer.NamedStructure structure in targetStructures)
                targetById[structure.Id] = structure;

            var pairs = new List<Tuple<StructureRasterizer.NamedStructure, StructureRasterizer.NamedStructure>>();
            foreach (StructureRasterizer.NamedStructure structure in sourceStructures)
            {
                StructureRasterizer.NamedStructure counterpart;
                if (targetById.TryGetValue(structure.Id, out counterpart))
                    pairs.Add(Tuple.Create(structure, counterpart));
            }

            measurements.StructurePairCount = pairs.Count;

            if (pairs.Count == 0)
            {
                // Two different situations, and conflating them cost a session. Structures on
                // both sides with no shared identifier is a property of the case: the row is
                // hidden, because it would say the same thing for ever. Zero structures read at
                // all, on a series the user has just drawn on, is a read fault: the row stays
                // visible as N/A, because hiding it leaves no trace of anything having gone
                // wrong.
                bool readNothing = sourceStructures.Count == 0 || targetStructures.Count == 0;

                string reason = string.Format(CultureInfo.InvariantCulture,
                    "no contour structure carries the same identifier on both series (source: {0}, " +
                    "registered: {1}). {2}",
                    sourceStructures.Count, targetStructures.Count,
                    readNothing
                        ? "One of the series yielded no contour structure at all. If you have drawn one " +
                          "there, this is a reading failure rather than a missing contour — the " +
                          "diagnostics tab names the call that did not answer."
                        : "These metrics compare a reference contour with the same contour after " +
                          "propagation, so a matching pair is required. Copying the structure to the " +
                          "second series under the same name enables them.");

                if (readNothing)
                {
                    measurements.Dsc = MeasuredValue.Unavailable(reason);
                    measurements.Mda = MeasuredValue.Unavailable(reason);
                    measurements.Hd95 = MeasuredValue.Unavailable(reason);
                    _log.Failure("structures", reason);
                    return;
                }

                measurements.Dsc = MeasuredValue.NotApplicable(reason);
                measurements.Mda = MeasuredValue.NotApplicable(reason);
                measurements.Hd95 = MeasuredValue.NotApplicable(reason);
                _log.Info("structures", reason);
                return;
            }

            // The comparison grid is built from the contours themselves, at roughly 2 mm: a
            // surface does not need sub-millimetre sampling to be characterised, and the
            // distance transform runs over the whole grid once per structure pair.
            var allContours = new List<ContourSet>();
            foreach (var pair in pairs)
            {
                allContours.Add(pair.Item1.Contours);
                allContours.Add(pair.Item2.Contours);
            }

            // The union of every matched contour, kept only as the region that maximum
            // displacement and inverse consistency fall back on when no volume loaded.
            _structureGeometry = BuildContourGrid(allContours, null);

            if (_structureGeometry == null)
            {
                const string reason = "the matched structures carry no contour points, so there is no " +
                                      "region over which to compare them";
                measurements.Dsc = MeasuredValue.Unavailable(reason);
                measurements.Mda = MeasuredValue.Unavailable(reason);
                measurements.Hd95 = MeasuredValue.Unavailable(reason);
                _log.Failure("structures", reason);
                return;
            }

            // One structure identifier per metric, not one for all three. The worst DSC, the
            // worst MDA and the worst HD95 need not come from the same structure, and on a real
            // case they did not: a duplicated PTV sphere reported DSC 0.910 alongside HD95
            // 38.5 mm, which is impossible for one surface and obvious once the BODY contour is
            // in the set — two scans covering different lengths of patient disagree wildly where
            // one field of view ends. Naming the worst-DSC structure next to all three values
            // told the reader those numbers described it.
            double worstDsc = double.MaxValue, worstMda = 0.0, worstHd95 = 0.0;
            string worstDscId = null, worstMdaId = null, worstHd95Id = null;
            int evaluated = 0;

            // A parallel accumulator for the surface outline, kept only as a fallback for when
            // it is literally the only thing that matched — see below.
            double worstOutlineDsc = double.MaxValue, worstOutlineMda = 0.0, worstOutlineHd95 = 0.0;
            string worstOutlineDscId = null, worstOutlineMdaId = null, worstOutlineHd95Id = null;
            int evaluatedOutline = 0;

            var gridByStructure = new Dictionary<string, ImageGeometry>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in pairs)
            {
                // The patient surface outline (DICOM type EXTERNAL, or a near-universal name
                // like "BODY" when that field is not populated) is still rasterised and its own
                // DSC/MDA/HD95 logged below — that is useful evidence, and hiding it would look
                // like the structure was never read. What it must not do is enter the
                // organ-level worst-case comparison: TG-132's DSC and MDA rows are about "the
                // same organ", and where two series cover different lengths of patient the
                // outline cannot agree at the ends no matter how good the registration is. Left
                // in, it dominated: DSC 0.910 and MDA 6.74 mm from BODY buried a PTV that
                // actually measured DSC 0.952 and MDA 0.65 mm.
                string outlineReason = pair.Item1.SurfaceOutlineReason ?? pair.Item2.SurfaceOutlineReason;
                bool isOutline = outlineReason != null;

                // One grid per structure rather than one shared by all. A shared grid spans the
                // union of everything matched, so a BODY outline 385 mm long forced the sample
                // cap to coarsen it to 2.41 mm — and a PTV sphere measured on that grid returned
                // an HD95 of exactly 2.41 mm, its resolution floor rather than its real
                // disagreement. Each structure now gets the finest grid its own extent allows.
                ImageGeometry grid = BuildContourGrid(
                    new[] { pair.Item1.Contours, pair.Item2.Contours }, pair.Item1.Id);

                if (grid == null)
                {
                    _log.Warning("structures: " + pair.Item1.Id, "no contour points to compare");
                    continue;
                }

                gridByStructure[pair.Item1.Id] = grid;

                bool[] maskSource = StructureRasterizer.Rasterise(pair.Item1.Contours, grid, null);
                bool[] maskTarget = StructureRasterizer.Rasterise(pair.Item2.Contours, grid, _mapper);

                SurfaceComparison comparison = SurfaceMetrics.Compare(
                    maskSource, maskTarget,
                    grid.XSize, grid.YSize, grid.ZSize,
                    grid.XRes, grid.YRes, grid.ZRes);

                if (!string.IsNullOrEmpty(comparison.Problem))
                {
                    _log.Warning("structures: " + pair.Item1.Id, comparison.Problem);
                    continue;
                }

                // The grid resolution belongs on this line: a surface distance equal to the
                // grid spacing means "at or below what this grid can resolve", not a measured
                // disagreement, and the reader cannot tell the difference without both numbers.
                _log.Info("structures: " + pair.Item1.Id, string.Format(CultureInfo.InvariantCulture,
                    "DSC {0:F3}, MDA {1:F2} mm, HD95 {2:F2} mm (grid {3:F2} mm){4}",
                    comparison.Dsc.Value, comparison.MeanDistanceToAgreement.Value,
                    comparison.Hd95.Value, grid.CoarsestResolution,
                    isOutline
                        ? " — patient surface outline (" + outlineReason + "): kept out of the organ-level " +
                          "worst case, reported separately below"
                        : string.Empty));

                if (isOutline)
                {
                    evaluatedOutline++;

                    if (comparison.Dsc.Value < worstOutlineDsc)
                    {
                        worstOutlineDsc = comparison.Dsc.Value;
                        worstOutlineDscId = pair.Item1.Id;
                    }
                    if (comparison.MeanDistanceToAgreement.Value > worstOutlineMda)
                    {
                        worstOutlineMda = comparison.MeanDistanceToAgreement.Value;
                        worstOutlineMdaId = pair.Item1.Id;
                    }
                    if (comparison.Hd95.Value > worstOutlineHd95)
                    {
                        worstOutlineHd95 = comparison.Hd95.Value;
                        worstOutlineHd95Id = pair.Item1.Id;
                    }
                    continue;
                }

                evaluated++;

                if (comparison.Dsc.Value < worstDsc)
                {
                    worstDsc = comparison.Dsc.Value;
                    worstDscId = pair.Item1.Id;
                }
                if (comparison.MeanDistanceToAgreement.Value > worstMda)
                {
                    worstMda = comparison.MeanDistanceToAgreement.Value;
                    worstMdaId = pair.Item1.Id;
                }
                if (comparison.Hd95.Value > worstHd95)
                {
                    worstHd95 = comparison.Hd95.Value;
                    worstHd95Id = pair.Item1.Id;
                }
            }

            if (evaluated == 0)
            {
                if (evaluatedOutline > 0)
                {
                    // No organ or target matched, but the surface outline did, and rasterised
                    // cleanly. That is still a real measurement — useful as a coarse
                    // pre-propagation check, the kind done before any structure exists on
                    // either series — so it is reported rather than withheld. What it must not
                    // do is masquerade as the TG-132 comparison: MetricEvaluator reads this
                    // flag and reports DSC/MDA/HD95 as ungated INFO instead of a graded value,
                    // because a bad number here is as likely to be a mismatch in scan length as
                    // in registration.
                    measurements.StructureMetricsAreSurfaceOutlineOnly = true;
                    measurements.WorstStructureId = worstOutlineDscId;

                    measurements.Dsc = MeasuredValue.Measured(
                        worstOutlineDsc, StructureNote(worstOutlineDscId, evaluatedOutline, gridByStructure));
                    measurements.Mda = MeasuredValue.Measured(
                        worstOutlineMda, StructureNote(worstOutlineMdaId, evaluatedOutline, gridByStructure));
                    measurements.Hd95 = MeasuredValue.Measured(
                        worstOutlineHd95, StructureNote(worstOutlineHd95Id, evaluatedOutline, gridByStructure));

                    _log.Info("structures",
                        "DSC, MDA and HD95 come from the patient surface outline only — no organ or " +
                        "target contour matched by identifier on both series. Reported as a gross " +
                        "overlap check, not a TG-132 accuracy metric: shown as INFO and excluded from " +
                        "the verdict.");
                    return;
                }

                const string reason = "no matched structure could be rasterised onto the comparison grid";
                measurements.Dsc = MeasuredValue.Unavailable(reason);
                measurements.Mda = MeasuredValue.Unavailable(reason);
                measurements.Hd95 = MeasuredValue.Unavailable(reason);
                _log.Info("structures", reason);
                return;
            }

            measurements.WorstStructureId = worstDscId;

            measurements.Dsc = MeasuredValue.Measured(
                worstDsc, StructureNote(worstDscId, evaluated, gridByStructure));
            measurements.Mda = MeasuredValue.Measured(
                worstMda, StructureNote(worstMdaId, evaluated, gridByStructure));
            measurements.Hd95 = MeasuredValue.Measured(
                worstHd95, StructureNote(worstHd95Id, evaluated, gridByStructure));

            if (evaluated > 1)
            {
                var distinct = new List<string>();
                foreach (string id in new[] { worstDscId, worstMdaId, worstHd95Id })
                    if (id != null && !distinct.Contains(id)) distinct.Add(id);

                if (distinct.Count > 1)
                {
                    // The patient surface outline no longer competes for worst case — it is
                    // excluded above — so a spread across different structures now means real
                    // organs disagree by different amounts, which is worth a look but is not
                    // the field-of-view artefact this warning used to be about.
                    _log.Warning("structures", string.Format(CultureInfo.InvariantCulture,
                        "the three worst cases come from different structures ({0}). Read each value " +
                        "against the structure named beside it, and the per-structure lines above for " +
                        "the rest.",
                        string.Join(", ", distinct.ToArray())));
                }
            }
        }

        /// <summary>
        /// Provenance for one surface metric: which structure produced this particular worst
        /// case, out of how many, on what grid.
        /// </summary>
        private static string StructureNote(
            string structureId, int evaluated, Dictionary<string, ImageGeometry> gridByStructure)
        {
            ImageGeometry grid;
            string resolution =
                structureId != null && gridByStructure.TryGetValue(structureId, out grid)
                    ? string.Format(CultureInfo.InvariantCulture, "; grid {0:F1} mm", grid.CoarsestResolution)
                    : string.Empty;

            // Leads with the structure, because that is the part the reader needs first and
            // the note is appended to a criterion in a column that ellipsises.
            return string.Format(CultureInfo.InvariantCulture,
                "{0}worst of {1} matched structure(s){2}",
                structureId != null ? structureId + ", " : string.Empty,
                evaluated,
                resolution);
        }

        /// <summary>
        /// The grid the two structures are rasterised onto, derived from the contours rather
        /// than from the image.
        ///
        /// It spans every matched contour on both series, padded so the surface distances near
        /// the edge are not clipped, at roughly 2 mm and capped at 160 samples per axis. Finer
        /// buys nothing for a contour surface, and the distance transform runs over the whole
        /// grid once per structure pair.
        ///
        /// Building it from the image volume was the mistake that took DSC, MDA and HD95 down
        /// with the voxels. Nothing here needs a voxel: contours are points in patient
        /// coordinates and always were.
        /// </summary>
        private ImageGeometry BuildContourGrid(IEnumerable<ContourSet> contourSets, string label)
        {
            const double targetSpacingMm = 2.0;
            const int maxSamplesPerAxis = 160;

            // Enough room for the propagated structure to sit outside the reference one and
            // still have its distances measured rather than clipped at the boundary.
            const double marginMm = 20.0;

            var minimum = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var maximum = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            bool any = false;

            foreach (ContourSet contours in contourSets)
            {
                Vec3 low, high;
                if (contours == null || !contours.TryGetBounds(out low, out high)) continue;

                minimum = new Vec3(
                    Math.Min(minimum.X, low.X), Math.Min(minimum.Y, low.Y), Math.Min(minimum.Z, low.Z));
                maximum = new Vec3(
                    Math.Max(maximum.X, high.X), Math.Max(maximum.Y, high.Y), Math.Max(maximum.Z, high.Z));
                any = true;
            }

            if (!any) return null;

            var origin = new Vec3(minimum.X - marginMm, minimum.Y - marginMm, minimum.Z - marginMm);
            var extent = new Vec3(
                (maximum.X - minimum.X) + 2 * marginMm,
                (maximum.Y - minimum.Y) + 2 * marginMm,
                (maximum.Z - minimum.Z) + 2 * marginMm);

            double spacing = targetSpacingMm;
            double longest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
            if (longest / spacing > maxSamplesPerAxis) spacing = longest / maxSamplesPerAxis;

            int xSize = (int)Math.Ceiling(extent.X / spacing) + 1;
            int ySize = (int)Math.Ceiling(extent.Y / spacing) + 1;
            int zSize = (int)Math.Ceiling(extent.Z / spacing) + 1;

            if (label != null)
            {
                _log.Info("structures: grid for " + label, string.Format(CultureInfo.InvariantCulture,
                    "{0}x{1}x{2} at {3:F2} mm, spanning {4:F0}x{5:F0}x{6:F0} mm",
                    xSize, ySize, zSize, spacing, extent.X, extent.Y, extent.Z));
            }

            // Canonical axes: the grid is ours, not the scanner's, so there is no orientation to
            // inherit. The rasteriser assumes contours lie on constant-z planes, which is the
            // same assumption an axial acquisition already satisfies.
            return new ImageGeometry(
                origin,
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1),
                spacing, spacing, spacing,
                xSize, ySize, zSize);
        }

        // ---------------------------------------------------------------- TG-132 Table III

        private void RecordNativeVoxelSize(
            EsapiImageReader.LoadResult source,
            EsapiImageReader.LoadResult target,
            QaMeasurements measurements)
        {
            double? a = source != null ? source.NativeVoxelSizeMm : null;
            double? b = target != null ? target.NativeVoxelSizeMm : null;

            if (a.HasValue && b.HasValue) measurements.NativeVoxelSizeMm = Math.Max(a.Value, b.Value);
            else if (a.HasValue) measurements.NativeVoxelSizeMm = a;
            else if (b.HasValue) measurements.NativeVoxelSizeMm = b;
        }

        /// <summary>
        /// Target Registration Error over matched point landmarks — the primary accuracy
        /// metric of TG-132 Table III, and the only one here expressed directly in
        /// millimetres of spatial error.
        ///
        /// Each landmark is taken from the source image, pushed through the registration
        /// mapping and compared with the same landmark on the registered image. The same
        /// mapper as the intensity metrics is reused, so this works for a deformable
        /// registration whenever the API exposes point-to-point mapping.
        /// </summary>
        private void ComputeTargetRegistrationError(
            dynamic registration, dynamic sourceImage, dynamic registeredImage, QaMeasurements measurements)
        {
            if (sourceImage == null || registeredImage == null)
            {
                const string reason = "the registration does not expose both images, so landmarks cannot be paired";
                measurements.TreMean = MeasuredValue.Unavailable(reason);
                measurements.TreMax = MeasuredValue.Unavailable(reason);
                return;
            }

            if (_mapper == null)
            {
                measurements.TreMean = MeasuredValue.Unavailable(NoMappingReason(measurements));
                measurements.TreMax = MeasuredValue.Unavailable(NoMappingReason(measurements));
                return;
            }

            List<Landmark> sourceLandmarks = LandmarkExtractor.Read(sourceImage, "source image", _log);
            List<Landmark> targetLandmarks = LandmarkExtractor.Read(registeredImage, "registered image", _log);
            List<Tuple<Landmark, Landmark>> pairs =
                LandmarkExtractor.Match(sourceLandmarks, targetLandmarks, _log);

            measurements.TreLandmarkCount = pairs.Count;

            if (pairs.Count == 0)
            {
                string reason = string.Format(CultureInfo.InvariantCulture,
                    "no point landmark is present on both series under the same identifier " +
                    "(source: {0}, registered: {1}). TRE needs markers — DICOM type MARKER or ISOCENTER — " +
                    "placed on the same anatomical feature in both image sets. Contour structures are not " +
                    "used: their centre of mass shifts when the contour is edited.",
                    sourceLandmarks.Count, targetLandmarks.Count);

                measurements.TreMean = MeasuredValue.NotApplicable(reason);
                measurements.TreMax = MeasuredValue.NotApplicable(reason);
                _log.Info("TRE", reason);
                return;
            }

            var errors = new List<double>();
            var unmappable = new List<string>();

            foreach (Tuple<Landmark, Landmark> pair in pairs)
            {
                Vec3 mapped;
                if (!_mapper.TryMap(pair.Item1.Position, out mapped))
                {
                    unmappable.Add(pair.Item1.Id);
                    continue;
                }
                errors.Add((mapped - pair.Item2.Position).Length);
            }

            if (unmappable.Count > 0)
            {
                _log.Warning("TRE",
                    "could not map through the registration: " + string.Join(", ", unmappable.ToArray()));
            }

            if (errors.Count == 0)
            {
                const string reason = "no matched landmark could be mapped through the registration";
                measurements.TreMean = MeasuredValue.Unavailable(reason);
                measurements.TreMax = MeasuredValue.Unavailable(reason);
                return;
            }

            double mean = errors.Sum() / errors.Count;
            double max = errors.Max();

            string note = string.Format(CultureInfo.InvariantCulture,
                "{0} matched landmark(s)", errors.Count);

            if (errors.Count < 3)
            {
                // The full caveat goes to the diagnostics, not the cell: it is the same sentence
                // on every under-landmarked case and it crowded out the criterion it sat beside.
                _log.Warning("TRE", note + "; fewer than three landmarks, so this is an indication " +
                    "rather than a characterisation of the registration accuracy");
                note += " — indicative only";
            }

            measurements.TreMean = MeasuredValue.Measured(mean, note);
            measurements.TreMax = MeasuredValue.Measured(max, note);

            _log.Info("TRE", string.Format(CultureInfo.InvariantCulture,
                "mean {0:F2} mm, max {1:F2} mm over {2} landmark(s)", mean, max, errors.Count));
        }

        /// <summary>
        /// Inverse consistency per TG-132 §4.C.4: registering A to B and then B to A should
        /// return every point to itself. Whatever displacement remains is the residual.
        ///
        /// It requires the reverse registration to exist in the workspace, which is why the
        /// unavailability message says so explicitly — it is a check the user can enable by
        /// creating one, not a permanent limitation.
        ///
        /// The residual is evaluated over a coarse grid rather than only the volume corners.
        /// For a rigid pair the maximum would indeed fall on a corner, but a deformable
        /// composition is not affine and its worst point can lie anywhere inside.
        /// </summary>
        private void ComputeInverseConsistency(
            dynamic scriptContext, dynamic registration,
            dynamic sourceImage, dynamic registeredImage, QaMeasurements measurements)
        {
            ImageGeometry region = _fixedGeometry ?? _structureGeometry;
            if (_mapper == null || region == null)
            {
                measurements.InverseConsistency = MeasuredValue.Unavailable(
                    "no forward mapping, or no region to evaluate the round trip over: neither image " +
                    "volume loaded and no matched structures were found");
                return;
            }

            string sourceId = ReadImageId(sourceImage, "source image");
            string targetId = ReadImageId(registeredImage, "registered image");

            if (sourceId == null || targetId == null)
            {
                measurements.InverseConsistency = MeasuredValue.Unavailable(
                    "the images do not expose identifiers, so the reverse registration cannot be located");
                return;
            }

            dynamic reverse = FindReverseRegistration(scriptContext, registration, sourceId, targetId);

            if (reverse == null)
            {
                string missingReverse = string.Format(
                    CultureInfo.InvariantCulture,
                    "no reverse registration ({0} → {1}) was found in the workspace. Create it and re-run " +
                    "to enable this check: TG-132 §4.C.4 evaluates consistency by registering in both " +
                    "directions and composing the result.",
                    targetId, sourceId);

                measurements.InverseConsistency = MeasuredValue.NotApplicable(missingReverse);
                _log.Info("inverse consistency", missingReverse);
                return;
            }

            // Scoped, not the plain instance log: ExtractRigidTransform and BuildPointMapper log
            // under the same operation names ("transform: matrix", "point mapping") regardless
            // of which registration they were called with, so without a prefix the forward and
            // reverse entries are indistinguishable in Diagnostics — which is exactly the
            // evidence needed when this check's residual looks wrong.
            DiagnosticLog reverseLog = _log.Scoped("reverse: ");

            var reverseMeasurements = new QaMeasurements { IsDeformable = measurements.IsDeformable };
            ExtractRigidTransform(reverse, reverseMeasurements, null, null, reverseLog);
            IPointMapper reverseMapper = BuildPointMapper(reverse, reverseMeasurements, reverseLog);

            // The active registration's translation is already in the report's Rigid Transform
            // table. This is the number to hold up against it: a genuine inverse should show an
            // approximately opposite translation of similar magnitude. One that instead looks
            // like the active registration's own is the round trip applying the same direction
            // twice, and would explain a residual roughly double the active transform's
            // displacement rather than the near-zero a true inverse would leave.
            if (reverseMeasurements.Transform != null)
            {
                Vec3 t = reverseMeasurements.Transform.Translation;
                EulerAngles? angles = reverseMeasurements.Transform.GetEulerAnglesDegrees();

                reverseLog.Info("transform: translation", string.Format(CultureInfo.InvariantCulture,
                    "({0:F2}, {1:F2}, {2:F2}) mm, magnitude {3:F2} mm" +
                    (angles.HasValue
                        ? string.Format(CultureInfo.InvariantCulture, "; rotation {0:F2}/{1:F2}/{2:F2}° (pitch/roll/yaw)",
                            angles.Value.PitchX, angles.Value.RollY, angles.Value.YawZ)
                        : string.Empty),
                    t.X, t.Y, t.Z, t.Length));
            }

            if (reverseMapper == null)
            {
                measurements.InverseConsistency = MeasuredValue.Unavailable(
                    "the reverse registration was found in the workspace, but its matrix could not be " +
                    "read from the API either — the same reason the forward transform is missing. This " +
                    "is one fault, not two: the check works as soon as the matrix can be read.");
                return;
            }

            double residual;
            int evaluated;
            if (!EvaluateRoundTripResidual(_mapper, reverseMapper, region, out residual, out evaluated))
            {
                measurements.InverseConsistency = MeasuredValue.Unavailable(
                    "the round trip could not be evaluated at any sampled point");
                return;
            }

            measurements.InverseConsistency = MeasuredValue.Measured(residual, string.Format(
                CultureInfo.InvariantCulture,
                "worst residual over {0} points across the field of view, forward then reverse", evaluated));

            _log.Info("inverse consistency", string.Format(
                CultureInfo.InvariantCulture, "{0:F3} mm over {1} points", residual, evaluated));
        }

        private static bool EvaluateRoundTripResidual(
            IPointMapper forward, IPointMapper reverse, ImageGeometry geometry,
            out double worstResidual, out int evaluated)
        {
            worstResidual = 0.0;
            evaluated = 0;

            const int steps = 5;   // 5^3 = 125 points, cheap even with per-point API calls

            for (int a = 0; a < steps; a++)
            {
                for (int b = 0; b < steps; b++)
                {
                    for (int c = 0; c < steps; c++)
                    {
                        double i = (geometry.XSize - 1) * a / (double)(steps - 1);
                        double j = (geometry.YSize - 1) * b / (double)(steps - 1);
                        double k = (geometry.ZSize - 1) * c / (double)(steps - 1);

                        Vec3 origin = geometry.VoxelToPatient(i, j, k);

                        Vec3 forwardPoint, roundTrip;
                        if (!forward.TryMap(origin, out forwardPoint)) continue;
                        if (!reverse.TryMap(forwardPoint, out roundTrip)) continue;

                        double residual = (roundTrip - origin).Length;
                        if (residual > worstResidual) worstResidual = residual;
                        evaluated++;
                    }
                }
            }

            return evaluated > 0;
        }

        private string ReadImageId(dynamic imageLike, string label)
        {
            if (imageLike == null) return null;

            dynamic value;
            string source;
            if (Dyn.TryGetFirst("identifier of " + label, _log, out value, out source,
                    Dyn.Alt("Image.Id", () => imageLike.Image.Id),
                    Dyn.Alt("Id", () => imageLike.Id)))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            return null;
        }

        private dynamic FindReverseRegistration(
            dynamic scriptContext, dynamic activeRegistration, string sourceId, string targetId)
        {
            dynamic registrations;
            string collection;

            if (!Dyn.TryGetFirst("workspace: registration collection", _log, out registrations, out collection,
                    Dyn.Alt("Patient.Registrations", () => scriptContext.Patient.Registrations),
                    Dyn.Alt("Registrations", () => scriptContext.Registrations),
                    Dyn.Alt("Patient.MIRSRegistrations", () => scriptContext.Patient.MIRSRegistrations)))
            {
                _log.Info("inverse consistency",
                    "the API exposes no collection of registrations, so the reverse one cannot be located");
                return null;
            }

            dynamic found = null;

            Dyn.TryInvoke("workspace: scan registrations (" + collection + ")", () =>
            {
                foreach (dynamic candidate in registrations)
                {
                    if (candidate == null) continue;
                    if (ReferenceEquals((object)candidate, (object)activeRegistration)) continue;

                    dynamic candidateSource, candidateTarget;
                    if (!Dyn.TryGet("candidate SourceImage", () => candidate.SourceImage, null, out candidateSource)) continue;
                    if (!Dyn.TryGet("candidate RegisteredImage", () => candidate.RegisteredImage, null, out candidateTarget)) continue;

                    string candidateSourceId = ReadImageId(candidateSource, "candidate source");
                    string candidateTargetId = ReadImageId(candidateTarget, "candidate target");

                    bool isReverse =
                        string.Equals(candidateSourceId, targetId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidateTargetId, sourceId, StringComparison.OrdinalIgnoreCase);

                    if (isReverse)
                    {
                        found = candidate;

                        // Named explicitly so it can be checked against Eclipse: is this really
                        // a second, independent registration in the opposite direction, or
                        // another entry that happens to report the same image pair?
                        string reverseId = ReadRegistrationId(candidate);
                        _log.Info("inverse consistency: reverse registration", string.Format(
                            CultureInfo.InvariantCulture,
                            "found '{0}' ({1} → {2}); its matrix and point mapping are logged below " +
                            "with the \"reverse: \" prefix, separately from the active registration's",
                            reverseId ?? "(no id)", candidateSourceId, candidateTargetId));

                        return;
                    }
                }
            }, _log);

            return found;
        }

        private static string NoMappingReason(QaMeasurements measurements)
        {
            return measurements.IsDeformable
                ? "this is a deformable registration and the API exposes neither the deformation vector " +
                  "field nor a point-by-point mapping method. Using only the linear component would " +
                  "describe a different transform from the one under audit."
                : "the registration matrix could not be read from the API, so points cannot be mapped " +
                  "through the registration. The diagnostics tab lists what the registration object " +
                  "does expose in this Eclipse version.";
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
            measurements.DvfGradientMax = MeasuredValue.Unavailable(reason);
            measurements.JacobianDeparture = MeasuredValue.Unavailable(reason);
            measurements.Dsc = MeasuredValue.Unavailable(reason);
            measurements.Hd95 = MeasuredValue.Unavailable(reason);
            measurements.TreMean = MeasuredValue.Unavailable(reason);
            measurements.TreMax = MeasuredValue.Unavailable(reason);
            measurements.InverseConsistency = MeasuredValue.Unavailable(reason);
        }
    }
}
