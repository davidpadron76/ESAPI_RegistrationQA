using System;
using System.Globalization;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Translates an image object from the Varian API into a self-contained
    /// <see cref="SampledVolume"/>: explicit geometry and voxels already converted to
    /// display values.
    ///
    /// All access goes through <see cref="Dyn"/>, so any property missing from the Eclipse
    /// version in use is recorded in the log rather than lost.
    /// </summary>
    public static class EsapiImageReader
    {
        /// <summary>
        /// Maximum samples per axis after subsampling. 128³ ≈ 2·10⁶ voxels (8 MB as float),
        /// enough for a 64x64 joint histogram with two orders of magnitude of headroom, at a
        /// bounded read cost.
        /// </summary>
        public const int MaxSamplesPerAxis = 128;

        public sealed class LoadResult
        {
            public SampledVolume Volume { get; set; }
            public ImageModality Modality { get; set; }
            public string Problem { get; set; }
            public bool Success { get { return Volume != null; } }
        }

        public static LoadResult Load(dynamic imageLike, string label, DiagnosticLog log)
        {
            var result = new LoadResult { Modality = ImageModality.Unknown };

            if (imageLike == null)
            {
                result.Problem = label + ": the image object is null";
                return result;
            }

            // The geometry and voxel carrier is .Frame in VMS.IRS and the object itself in
            // classic ESAPI. Both are tried and whichever answered is recorded.
            dynamic frame;
            string frameSource;
            if (!Dyn.TryGetFirst(
                    label + ": locate voxel carrier", log, out frame, out frameSource,
                    Dyn.Alt("Frame", () => imageLike.Frame),
                    Dyn.Alt("Image", () => imageLike.Image),
                    Dyn.Alt("direct object", () => imageLike)))
            {
                result.Problem = label + ": no object with image geometry was found";
                return result;
            }

            log.Info(label + ": voxel carrier", "resolved via " + frameSource);

            ImageGeometry geometry = ReadGeometry(frame, label, log);
            if (geometry == null)
            {
                result.Problem = label + ": could not reconstruct the image geometry";
                return result;
            }

            string geometryProblem;
            if (!geometry.IsUsable(out geometryProblem))
            {
                result.Problem = label + ": inconsistent geometry (" + geometryProblem + ")";
                log.Failure(label + ": geometry validation", geometryProblem);
                return result;
            }

            result.Modality = ReadModality(imageLike, frame, label, log);

            IntensityScale scale = ProbeIntensityScale(frame, label, log);

            string voxelProblem;
            SampledVolume volume = ReadVoxels(frame, geometry, scale, label, log, out voxelProblem);
            if (volume == null)
            {
                result.Problem = label + ": " + voxelProblem;
                return result;
            }

            result.Volume = volume;
            return result;
        }

        // ------------------------------------------------------------------ geometry

        private static ImageGeometry ReadGeometry(dynamic frame, string label, DiagnosticLog log)
        {
            int xSize, ySize, zSize;
            if (!Dyn.TryGetInt(label + ": XSize", () => frame.XSize, log, out xSize)) return null;
            if (!Dyn.TryGetInt(label + ": YSize", () => frame.YSize, log, out ySize)) return null;
            if (!Dyn.TryGetInt(label + ": ZSize", () => frame.ZSize, log, out zSize))
            {
                // A carrier exposing a single plane is treated as a one-slice volume.
                zSize = 1;
                log.Warning(label + ": ZSize", "not available; assuming a single plane");
            }

            double xRes, yRes, zRes;
            if (!Dyn.TryGetDouble(label + ": XRes", () => frame.XRes, log, out xRes)) return null;
            if (!Dyn.TryGetDouble(label + ": YRes", () => frame.YRes, log, out yRes)) return null;
            if (!Dyn.TryGetDouble(label + ": ZRes", () => frame.ZRes, log, out zRes))
            {
                zRes = 1.0;
                log.Warning(label + ": ZRes", "not available; assuming 1 mm slice spacing");
            }

            Vec3 origin;
            if (!TryReadVector("Origin", () => frame.Origin, label, log, out origin)) return null;

            Vec3 xDirection, yDirection, zDirection;
            bool haveDirections =
                TryReadVector("XDirection", () => frame.XDirection, label, log, out xDirection) &&
                TryReadVector("YDirection", () => frame.YDirection, label, log, out yDirection) &&
                TryReadVector("ZDirection", () => frame.ZDirection, label, log, out zDirection);

            if (!haveDirections)
            {
                // Canonical axial orientation. This is correct for the vast majority of
                // planning series, but it is recorded because a study acquired with a tilted
                // gantry would be misinterpreted.
                xDirection = new Vec3(1, 0, 0);
                yDirection = new Vec3(0, 1, 0);
                zDirection = new Vec3(0, 0, 1);
                log.Warning(
                    label + ": direction cosines",
                    "not exposed by the API; assuming canonical axial orientation (X=LR, Y=AP, Z=CC)");
            }

            return new ImageGeometry(
                origin, xDirection, yDirection, zDirection,
                xRes, yRes, zRes, xSize, ySize, zSize);
        }

        private static bool TryReadVector(
            string propertyName, Func<object> accessor,
            string label, DiagnosticLog log, out Vec3 vector)
        {
            vector = Vec3.Zero;

            dynamic raw;
            if (!Dyn.TryGet(label + ": " + propertyName, accessor, log, out raw)) return false;

            // VVector exposes lowercase x/y/z; other APIs use X/Y/Z.
            // These are initialised because short-circuit evaluation may skip one of the
            // reads, leaving its out parameter unassigned.
            double x = 0.0, y = 0.0, z = 0.0;
            bool lower =
                Dyn.TryGetDouble(label + ": " + propertyName + ".x", () => raw.x, null, out x) &&
                Dyn.TryGetDouble(label + ": " + propertyName + ".y", () => raw.y, null, out y) &&
                Dyn.TryGetDouble(label + ": " + propertyName + ".z", () => raw.z, null, out z);

            if (!lower)
            {
                bool upper =
                    Dyn.TryGetDouble(label + ": " + propertyName + ".X", () => raw.X, log, out x) &&
                    Dyn.TryGetDouble(label + ": " + propertyName + ".Y", () => raw.Y, log, out y) &&
                    Dyn.TryGetDouble(label + ": " + propertyName + ".Z", () => raw.Z, log, out z);

                if (!upper)
                {
                    log.Failure(label + ": " + propertyName, "the vector exposes neither x/y/z nor X/Y/Z components");
                    return false;
                }
            }

            vector = new Vec3(x, y, z);
            return true;
        }

        private static ImageModality ReadModality(dynamic imageLike, dynamic frame, string label, DiagnosticLog log)
        {
            string modalityText;
            dynamic value;
            string source;

            if (Dyn.TryGetFirst(
                    label + ": modality", log, out value, out source,
                    Dyn.Alt("Image.Series.Modality", () => imageLike.Image.Series.Modality),
                    Dyn.Alt("Series.Modality", () => imageLike.Series.Modality),
                    Dyn.Alt("Modality", () => imageLike.Modality),
                    Dyn.Alt("Frame.Modality", () => frame.Modality)))
            {
                modalityText = Convert.ToString(value, CultureInfo.InvariantCulture);
                ImageModality parsed = RegistrationContext.ParseModality(modalityText);
                log.Info(label + ": modality", modalityText + " (via " + source + ") → " + parsed);
                return parsed;
            }

            log.Warning(label + ": modality", "not exposed by the API");
            return ImageModality.Unknown;
        }

        // ------------------------------------------------------------------ intensity

        /// <summary>Linear raw-voxel → display-value ramp (HU for CT).</summary>
        private sealed class IntensityScale
        {
            public double Slope = 1.0;
            public double Intercept = 0.0;
            public bool IsIdentity = true;

            public float Apply(int rawVoxel)
            {
                return IsIdentity ? rawVoxel : (float)(rawVoxel * Slope + Intercept);
            }
        }

        /// <summary>
        /// Determines the voxel→HU ramp by probing <c>VoxelToDisplayValue</c> at three
        /// points and verifying linearity.
        ///
        /// It is resolved this way, rather than calling the method per voxel, because each
        /// dynamic invocation costs orders of magnitude more than a multiplication: for two
        /// million voxels that is the difference between milliseconds and minutes.
        /// </summary>
        private static IntensityScale ProbeIntensityScale(dynamic frame, string label, DiagnosticLog log)
        {
            var scale = new IntensityScale();

            // Initialised for the same reason as in TryReadVector: && short-circuiting may
            // skip one of the probes.
            double v0 = 0.0, v1000 = 0.0, v500 = 0.0;
            bool probed =
                Dyn.TryGetDouble(label + ": VoxelToDisplayValue(0)", () => frame.VoxelToDisplayValue(0), log, out v0) &&
                Dyn.TryGetDouble(label + ": VoxelToDisplayValue(1000)", () => frame.VoxelToDisplayValue(1000), null, out v1000) &&
                Dyn.TryGetDouble(label + ": VoxelToDisplayValue(500)", () => frame.VoxelToDisplayValue(500), null, out v500);

            if (!probed)
            {
                log.Warning(
                    label + ": intensity scaling",
                    "VoxelToDisplayValue not available; raw voxel values are used. The intensity " +
                    "metrics remain valid (NCC and NMI are invariant under a common affine " +
                    "transform), but the SSD is not comparable in HU.");
                return scale;
            }

            double slope = (v1000 - v0) / 1000.0;
            double intercept = v0;
            double predicted = slope * 500.0 + intercept;

            if (Math.Abs(predicted - v500) > 1e-6 * Math.Max(1.0, Math.Abs(v500)))
            {
                log.Warning(
                    label + ": intensity scaling",
                    "the voxel→display ramp is not linear; raw values are used");
                return scale;
            }

            scale.Slope = slope;
            scale.Intercept = intercept;
            scale.IsIdentity = Math.Abs(slope - 1.0) < 1e-12 && Math.Abs(intercept) < 1e-12;

            log.Info(
                label + ": intensity scaling",
                string.Format(CultureInfo.InvariantCulture,
                    "display = {0:0.######}·voxel + {1:0.######}", slope, intercept));

            return scale;
        }

        // ------------------------------------------------------------------ voxels

        private static SampledVolume ReadVoxels(
            dynamic frame, ImageGeometry geometry, IntensityScale scale,
            string label, DiagnosticLog log, out string problem)
        {
            problem = null;

            int stepX = Step(geometry.XSize);
            int stepY = Step(geometry.YSize);
            int stepZ = Step(geometry.ZSize);

            int newX = (geometry.XSize + stepX - 1) / stepX;
            int newY = (geometry.YSize + stepY - 1) / stepY;
            int newZ = (geometry.ZSize + stepZ - 1) / stepZ;

            ImageGeometry reduced = geometry.Subsampled(stepX, stepY, stepZ, newX, newY, newZ);

            log.Info(
                label + ": subsampling",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}x{1}x{2} → {3}x{4}x{5} (step {6}/{7}/{8}); effective resolution {9:F2}x{10:F2}x{11:F2} mm",
                    geometry.XSize, geometry.YSize, geometry.ZSize,
                    newX, newY, newZ, stepX, stepY, stepZ,
                    reduced.XRes, reduced.YRes, reduced.ZRes));

            var data = new float[(long)newX * newY * newZ];

            // GetVoxels expects a full-size plane buffer. The buffer type varies between API
            // versions (int[,] in ESAPI, ushort[,] in some VMS.IRS builds), so both are
            // tried once.
            var intBuffer = new int[geometry.XSize, geometry.YSize];
            var ushortBuffer = new ushort[geometry.XSize, geometry.YSize];

            bool useIntBuffer = true;
            bool bufferKindResolved = false;

            int outK = 0;
            for (int k = 0; k < geometry.ZSize; k += stepZ, outK++)
            {
                int plane = k;

                if (!bufferKindResolved)
                {
                    if (Dyn.TryInvoke(label + ": GetVoxels(int[,])", () => frame.GetVoxels(plane, intBuffer), log))
                    {
                        useIntBuffer = true;
                        bufferKindResolved = true;
                        log.Info(label + ": GetVoxels", "the accepted buffer type is int[,]");
                    }
                    else if (Dyn.TryInvoke(label + ": GetVoxels(ushort[,])", () => frame.GetVoxels(plane, ushortBuffer), log))
                    {
                        useIntBuffer = false;
                        bufferKindResolved = true;
                        log.Info(label + ": GetVoxels", "the accepted buffer type is ushort[,]");
                    }
                    else
                    {
                        problem = "GetVoxels accepted neither int[,] nor ushort[,]; voxels cannot be read";
                        return null;
                    }
                }
                else
                {
                    bool ok = useIntBuffer
                        ? Dyn.TryInvoke(label + ": GetVoxels plane " + plane, () => frame.GetVoxels(plane, intBuffer), log)
                        : Dyn.TryInvoke(label + ": GetVoxels plane " + plane, () => frame.GetVoxels(plane, ushortBuffer), log);

                    if (!ok)
                    {
                        problem = "failed to read plane " + plane;
                        return null;
                    }
                }

                int outJ = 0;
                for (int j = 0; j < geometry.YSize; j += stepY, outJ++)
                {
                    int outI = 0;
                    for (int i = 0; i < geometry.XSize; i += stepX, outI++)
                    {
                        int raw = useIntBuffer ? intBuffer[i, j] : ushortBuffer[i, j];
                        data[outI + newX * (outJ + newY * outK)] = scale.Apply(raw);
                    }
                }
            }

            return new SampledVolume(reduced, data);
        }

        private static int Step(int size)
        {
            if (size <= MaxSamplesPerAxis) return 1;
            return (int)Math.Ceiling((double)size / MaxSamplesPerAxis);
        }
    }
}
