using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// The contours of one structure, grouped by the plane they were drawn on, in patient
    /// coordinates.
    ///
    /// Membership is resolved with an even-odd ray cast across every polygon on the nearest
    /// plane. Counting crossings over all polygons together rather than per polygon is what
    /// makes holes come out right: a point inside a hole crosses the inner and the outer
    /// contour, an even number, and is correctly reported as outside.
    ///
    /// The plane lookup assumes contours lie on constant-z planes in patient coordinates,
    /// which holds for axial acquisitions. A study reconstructed on oblique planes would need
    /// the query point projected onto the plane normal instead; the analyzer logs a warning
    /// when the image geometry is not axial so that case does not pass silently.
    /// </summary>
    public sealed class ContourSet
    {
        private sealed class Plane
        {
            public double Z;
            public List<double[]> PolygonsX = new List<double[]>();
            public List<double[]> PolygonsY = new List<double[]>();
        }

        private readonly List<Plane> _planes = new List<Plane>();
        private double _halfSpacing = 1.0;

        public int PlaneCount { get { return _planes.Count; } }
        public int PolygonCount { get; private set; }

        /// <summary>
        /// Median gap between contour planes, in mm — the Z resolution the structure actually
        /// carries, as opposed to the grid it gets rasterised onto.
        ///
        /// Worth reporting because the two can differ by an order of magnitude and the
        /// difference is not visible in any of the surface metrics. A phantom pair audited here
        /// had the same target contoured at 0.4 mm on one series and 5.0 mm on the other: every
        /// DSC or MDA across that pair is dominated by how the space between 5 mm planes is
        /// filled, not by the registration.
        /// </summary>
        public double MedianPlaneSpacingMm { get { return _halfSpacing * 2.0; } }

        /// <summary>
        /// Where the contour planes actually sit, and the gaps between them — the raw evidence
        /// behind <see cref="MedianPlaneSpacingMm"/>, which every rasterised volume scales with.
        ///
        /// It exists because that estimate was caught being wrong by a factor of exactly two.
        /// On a clinical MR↔CT pair, the same structure on the same image read as 4 planes at
        /// 2.90 mm in one run and 4 planes at 5.78 mm in another, and the rasterised volumes came
        /// out at 0.9 cm3 and 2.2 cm3 against Eclipse's 1.8 cm3 — exactly half, and 1.22 times.
        /// A median is a summary, and summarising was how a doubled gap hid; the positions
        /// themselves cannot hide it.
        /// </summary>
        public string DescribePlanes()
        {
            if (_planes.Count == 0) return "no contour planes";

            var text = new System.Text.StringBuilder();
            text.AppendFormat(CultureInfo.InvariantCulture,
                "{0} plane(s), z {1:F2} to {2:F2} mm",
                _planes.Count, _planes[0].Z, _planes[_planes.Count - 1].Z);

            if (_planes.Count < 2) return text.ToString();

            var gaps = new List<double>();
            for (int i = 1; i < _planes.Count; i++)
                gaps.Add(Math.Abs(_planes[i].Z - _planes[i - 1].Z));

            double smallest = gaps[0], largest = gaps[0];
            foreach (double g in gaps)
            {
                if (g < smallest) smallest = g;
                if (g > largest) largest = g;
            }

            text.AppendFormat(CultureInfo.InvariantCulture,
                "; gaps {0:F2} to {1:F2} mm, median {2:F2} mm",
                smallest, largest, MedianPlaneSpacingMm);

            // The failure mode this was built to catch: an irregular plane set, where the median
            // lands on a doubled gap and every volume derived from it doubles with it.
            if (largest > 1.5 * smallest)
            {
                text.AppendFormat(CultureInfo.InvariantCulture,
                    ". The planes are NOT evenly spaced — the largest gap is {0:F1}x the smallest, " +
                    "so the median is a poor description of this structure and the volume derived " +
                    "from it is unreliable. Gaps: {1}",
                    largest / smallest, string.Join(", ", gaps.ConvertAll(
                        g => g.ToString("F2", CultureInfo.InvariantCulture)).ToArray()));
            }

            return text.ToString();
        }

        public void AddPolygon(double z, double[] xs, double[] ys)
        {
            if (xs == null || ys == null || xs.Length < 3) return;

            Plane plane = _planes.FirstOrDefault(p => Math.Abs(p.Z - z) < 1e-4);
            if (plane == null)
            {
                plane = new Plane { Z = z };
                _planes.Add(plane);
            }

            plane.PolygonsX.Add(xs);
            plane.PolygonsY.Add(ys);
            PolygonCount++;
        }

        /// <summary>
        /// Determines the half slice spacing once every polygon has been added. A point is
        /// considered to lie on a plane when it falls within this distance of it, so that the
        /// rasterised structure has thickness rather than being a stack of infinitely thin
        /// sheets that the sampling grid would mostly miss.
        /// </summary>
        public void Finalise()
        {
            _planes.Sort((a, b) => a.Z.CompareTo(b.Z));

            if (_planes.Count < 2)
            {
                _halfSpacing = 1.5;
                return;
            }

            var gaps = new List<double>();
            for (int i = 1; i < _planes.Count; i++)
                gaps.Add(Math.Abs(_planes[i].Z - _planes[i - 1].Z));

            gaps.Sort();
            double median = gaps[gaps.Count / 2];
            _halfSpacing = median > 0 ? median / 2.0 : 1.5;
        }

        /// <summary>
        /// Axis-aligned extent of every polygon, in patient coordinates. False when the
        /// structure holds no contour.
        ///
        /// This is what lets the surface metrics work on an Eclipse that will not hand over a
        /// single voxel. The comparison grid used to be derived from the loaded image volume,
        /// so DSC, MDA and HD95 died along with it — a dependency that was never real, since
        /// contours carry their own coordinates. The z extent is padded by the half slice
        /// spacing because <see cref="Contains"/> gives each plane that much thickness.
        /// </summary>
        public bool TryGetBounds(out Vec3 minimum, out Vec3 maximum)
        {
            minimum = Vec3.Zero;
            maximum = Vec3.Zero;

            if (_planes.Count == 0 || PolygonCount == 0) return false;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;

            foreach (Plane plane in _planes)
            {
                for (int p = 0; p < plane.PolygonsX.Count; p++)
                {
                    double[] xs = plane.PolygonsX[p];
                    double[] ys = plane.PolygonsY[p];

                    for (int i = 0; i < xs.Length; i++)
                    {
                        if (xs[i] < minX) minX = xs[i];
                        if (xs[i] > maxX) maxX = xs[i];
                        if (ys[i] < minY) minY = ys[i];
                        if (ys[i] > maxY) maxY = ys[i];
                        any = true;
                    }
                }

                if (plane.Z - _halfSpacing < minZ) minZ = plane.Z - _halfSpacing;
                if (plane.Z + _halfSpacing > maxZ) maxZ = plane.Z + _halfSpacing;
            }

            if (!any) return false;

            minimum = new Vec3(minX, minY, minZ);
            maximum = new Vec3(maxX, maxY, maxZ);
            return true;
        }

        public bool Contains(Vec3 point)
        {
            Plane nearest = null;
            double best = double.MaxValue;

            for (int i = 0; i < _planes.Count; i++)
            {
                double distance = Math.Abs(_planes[i].Z - point.Z);
                if (distance < best)
                {
                    best = distance;
                    nearest = _planes[i];
                }
            }

            if (nearest == null || best > _halfSpacing) return false;

            int crossings = 0;
            for (int p = 0; p < nearest.PolygonsX.Count; p++)
                crossings += RayCrossings(nearest.PolygonsX[p], nearest.PolygonsY[p], point.X, point.Y);

            return (crossings & 1) == 1;
        }

        /// <summary>
        /// Crossings of a ray cast in +X from the query point against one closed polygon.
        /// The half-open vertical rule (one endpoint strictly above, one at or below) counts
        /// each edge once and keeps a point level with a vertex from being counted twice.
        /// </summary>
        private static int RayCrossings(double[] xs, double[] ys, double x, double y)
        {
            int crossings = 0;
            int n = xs.Length;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                bool straddles = (ys[i] > y) != (ys[j] > y);
                if (!straddles) continue;

                double t = (y - ys[i]) / (ys[j] - ys[i]);
                double crossingX = xs[i] + t * (xs[j] - xs[i]);

                if (crossingX > x) crossings++;
            }

            return crossings;
        }
    }

    /// <summary>
    /// Reads structure contours from the Varian API and rasterises them onto a common grid.
    ///
    /// <c>Structure.IsPointInsideSegment</c> would answer membership directly, but it is a
    /// dynamic call per point: a 128³ grid means two million invocations across the API
    /// boundary for a single structure. Pulling the polygons once with
    /// <c>GetContoursOnImagePlane</c> and testing membership in managed code keeps the whole
    /// comparison in the order of seconds.
    /// </summary>
    public static class StructureRasterizer
    {
        /// <summary>Structure types that are landmarks rather than volumes; TRE consumes these.</summary>
        private static readonly HashSet<string> PointTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MARKER", "ISOCENTER" };

        /// <summary>
        /// The DICOM RTROIInterpretedType for a patient surface outline. This is the normative
        /// field, but it is only the primary criterion — see <see cref="SurfaceOutlineNames"/>
        /// for why it cannot be the only one.
        /// </summary>
        private const string SurfaceOutlineType = "EXTERNAL";

        /// <summary>
        /// Fallback identifiers for the patient surface outline, used when DicomType does not
        /// say EXTERNAL. Field evidence (v2.11.0) showed a real Eclipse instance carrying a
        /// structure literally named "BODY" whose DicomType was not "EXTERNAL" — the field is
        /// often left at whatever the contouring tool defaulted to rather than corrected by
        /// hand, so trusting it alone let the outline back into the organ-level worst case.
        /// Matched on the trimmed, case-insensitive identifier exactly, not as a substring, so
        /// something like "PTV_BODY_boost" is not caught by mistake.
        /// </summary>
        private static readonly HashSet<string> SurfaceOutlineNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "BODY", "EXTERNAL", "SKIN", "EXTERIOR", "CUERPO", "PIEL" };

        public sealed class NamedStructure
        {
            public string Id { get; set; }
            public string DicomType { get; set; }
            public ContourSet Contours { get; set; }

            /// <summary>
            /// True for the patient surface outline. It still gets read and rasterised like
            /// any other structure, but it is not the kind of thing TG-132's DSC and MDA rows
            /// are about — the report speaks of "the same organ", not of the skin. And where
            /// two series cover different lengths of patient, its surface cannot agree at the
            /// ends no matter how good the registration is, so including it in a worst-case
            /// comparison buries every organ result behind a number that measures
            /// field-of-view overlap instead.
            /// </summary>
            public bool IsSurfaceOutline
            {
                get { return SurfaceOutlineReason != null; }
            }

            /// <summary>
            /// Which criterion classified this structure as the patient surface outline, or
            /// null when neither did. Exposed so the exclusion is traceable in diagnostics
            /// rather than a silent boolean.
            /// </summary>
            public string SurfaceOutlineReason
            {
                get
                {
                    if (string.Equals(DicomType, SurfaceOutlineType, StringComparison.OrdinalIgnoreCase))
                        return "DICOM type EXTERNAL";

                    if (!string.IsNullOrEmpty(Id) && SurfaceOutlineNames.Contains(Id.Trim()))
                        return "name match (\"" + Id.Trim() + "\")";

                    return null;
                }
            }
        }

        /// <summary>
        /// Reads every contour structure attached to an image. Point structures are excluded:
        /// they have no volume to overlap and are the input to the TRE instead.
        /// </summary>
        public static List<NamedStructure> ReadContourStructures(
            dynamic imageLike, string label, DiagnosticLog log)
        {
            var structures = new List<NamedStructure>();
            if (imageLike == null) return structures;

            dynamic structureSets;
            string source;

            if (!Dyn.TryGetFirst("structures: sets of " + label, log, out structureSets, out source,
                    Dyn.Alt("Image.StructureSets", () => imageLike.Image.StructureSets),
                    Dyn.Alt("StructureSets", () => imageLike.StructureSets)))
            {
                return structures;
            }

            int skippedPoints = 0;
            int skippedEmpty = 0;
            bool describedMemberSurfaceOnFailure = false;

            Dyn.TryInvoke("structures: walk contours of " + label, () =>
            {
                foreach (dynamic structureSet in structureSets)
                {
                    int planeCount = ReadPlaneCount(structureSet, log);

                    foreach (dynamic structure in structureSet.Structures)
                    {
                        if (structure == null) continue;

                        string id;
                        if (!Dyn.TryGetString("structures: id", () => structure.Id, null, out id)) continue;

                        // VMS.CA.Scripting.VolumetricStructure (a real Eclipse install, v2.12.0)
                        // has no DicomType property at all — the classic ESAPI name. What it has
                        // instead is StructureType, an enum rather than a string, carrying the
                        // same DICOM RT ROI Interpreted Type vocabulary under a different name.
                        dynamic dicomTypeRaw;
                        string usedAlt;
                        bool readOk = Dyn.TryGetFirst("structures: DicomType of " + id, log, out dicomTypeRaw, out usedAlt,
                            Dyn.Alt("DicomType", () => structure.DicomType),
                            Dyn.Alt("StructureType", () => structure.StructureType.ToString()));

                        string dicomType = readOk
                            ? Convert.ToString(dicomTypeRaw, CultureInfo.InvariantCulture)
                            : string.Empty;

                        if (!readOk && !describedMemberSurfaceOnFailure)
                        {
                            // Neither known name answered — a third API shape. The member surface
                            // is logged once so the real property name can be found without a
                            // separate probe session.
                            describedMemberSurfaceOnFailure = true;
                            log.Warning("structures: DicomType", "neither DicomType nor StructureType is a " +
                                "member of this API's structure type; the surface-outline exclusion is relying " +
                                "on name matching only for this run. " + MatrixReader.DescribeMemberSurface((object)structure));
                        }

                        // Read with the real log, not null: this value decides whether a
                        // structure gets excluded as the patient surface outline, and the
                        // v2.11.0 field failure — BODY not excluded — was undiagnosable until
                        // this line existed, because nobody could see what the API actually
                        // returned.
                        log.Info("structures: DicomType of " + id,
                            string.IsNullOrEmpty(dicomType) ? "(empty)" : dicomType);

                        if (PointTypes.Contains(dicomType))
                        {
                            skippedPoints++;
                            continue;
                        }

                        ContourSet contours = ReadContours(structure, id, planeCount, log);
                        if (contours == null || contours.PolygonCount == 0)
                        {
                            skippedEmpty++;
                            continue;
                        }

                        var namedStructure = new NamedStructure
                        {
                            Id = id.Trim(),
                            DicomType = dicomType,
                            Contours = contours
                        };

                        if (namedStructure.IsSurfaceOutline)
                        {
                            log.Info("structures: " + id,
                                "classified as patient surface outline — " + namedStructure.SurfaceOutlineReason);
                        }

                        structures.Add(namedStructure);
                    }
                }
            }, log);

            log.Info("structures: " + label, string.Format(CultureInfo.InvariantCulture,
                "{0} contour structure(s) read via {1}; {2} point structure(s) skipped, {3} empty",
                structures.Count, source, skippedPoints, skippedEmpty));

            if (structures.Count == 0 && skippedEmpty > 0)
            {
                // Structures exist and every one came back without a single polygon. That is a
                // read fault, not an empty series, and the member surface is what identifies
                // which call is the wrong one.
                log.Failure("structures: " + label, string.Format(CultureInfo.InvariantCulture,
                    "{0} structure(s) were found and none yielded a contour. " +
                    "GetContoursOnImagePlane is the call being used; if this API names it " +
                    "differently, this is what says so. {1}",
                    skippedEmpty, DescribeFirstStructure(structureSets)));
            }

            return structures;
        }

        /// <summary>
        /// How many image planes to ask each structure for.
        ///
        /// This asked <c>structureSet.Image.ZSize</c> and nothing else, and on VMS.CA that
        /// property does not exist: <c>VolumeImage</c> exposes UserOrigin, StructureSets,
        /// ImageModality, FOR, UID, ImagingOrientation, ImageStatus, IsProcessed, Frames,
        /// Series and identifiers — the sizes live on <c>Frame</c>. The count came back as
        /// zero, every structure was then read as having no contours, no pair could be formed,
        /// and DSC, MDA and HD95 went to NotApplicable, which is the one state that hides the
        /// row entirely. A structure drawn on both series produced no metrics and no visible
        /// reason.
        ///
        /// Same shape of mistake as the matrix and the voxels before it: asking one object for
        /// something its neighbour exposes. So every plausible path is tried and the one that
        /// answered is recorded.
        /// </summary>
        /// <summary>
        /// Member surface of the first structure found, for the diagnostics. Reading contours
        /// is the one step of this class that has never been confirmed against a real API.
        /// </summary>
        private static string DescribeFirstStructure(dynamic structureSets)
        {
            string description = "(no structure could be inspected)";

            Dyn.TryInvoke("structures: inspect first structure", () =>
            {
                foreach (dynamic structureSet in structureSets)
                {
                    description = "StructureSet — " + MatrixReader.DescribeMemberSurface((object)structureSet);

                    foreach (dynamic structure in structureSet.Structures)
                    {
                        if (structure == null) continue;
                        description += " | Structure — " + MatrixReader.DescribeMemberSurface((object)structure);
                        return;
                    }
                }
            }, null);

            return description;
        }

        private static int ReadPlaneCount(dynamic structureSet, DiagnosticLog log)
        {
            int planes;

            // Classic ESAPI: the image carries its own dimensions.
            if (Dyn.TryGetInt("structures: plane count (Image.ZSize)",
                    () => structureSet.Image.ZSize, null, out planes) && planes > 0)
            {
                log.Info("structures: plane count", planes + " via Image.ZSize");
                return planes;
            }

            // VMS.CA: the dimensions live on the frame.
            if (Dyn.TryGetInt("structures: plane count (Image.Frame.ZSize)",
                    () => structureSet.Image.Frame.ZSize, null, out planes) && planes > 0)
            {
                log.Info("structures: plane count", planes + " via Image.Frame.ZSize");
                return planes;
            }

            // VMS.CA again: Frames is a collection, and the first frame holds the geometry.
            dynamic frames;
            if (Dyn.TryGet("structures: Image.Frames", () => structureSet.Image.Frames, null, out frames))
            {
                int found = 0;
                Dyn.TryInvoke("structures: first frame of Image.Frames", () =>
                {
                    foreach (dynamic frame in frames)
                    {
                        int size;
                        if (Dyn.TryGetInt("structures: frame ZSize", () => frame.ZSize, null, out size) && size > 0)
                        {
                            found = size;
                            return;
                        }
                    }
                }, null);

                if (found > 0)
                {
                    log.Info("structures: plane count", found + " via Image.Frames[0].ZSize");
                    return found;
                }
            }

            log.Failure("structures: plane count",
                "no plane count could be obtained, so no structure can be read. " +
                MatrixReader.DescribeMemberSurface((object)structureSet));

            return 0;
        }

        private static ContourSet ReadContours(dynamic structure, string id, int planeCount, DiagnosticLog log)
        {
            if (planeCount <= 0) return null;

            var contours = new ContourSet();
            bool anyFailure = false;

            for (int plane = 0; plane < planeCount; plane++)
            {
                int captured = plane;
                dynamic polygons;

                if (!Dyn.TryGet("structures: contours of " + id + " on plane " + captured,
                        () => structure.GetContoursOnImagePlane(captured), null, out polygons))
                {
                    anyFailure = true;
                    continue;
                }

                Dyn.TryInvoke("structures: read polygons of " + id, () =>
                {
                    foreach (dynamic polygon in polygons)
                    {
                        if (polygon == null) continue;

                        var xs = new List<double>();
                        var ys = new List<double>();
                        double z = 0.0;
                        bool first = true;

                        foreach (dynamic vertex in polygon)
                        {
                            double x, y, vz;
                            if (!TryReadVertex(vertex, out x, out y, out vz)) continue;

                            xs.Add(x);
                            ys.Add(y);
                            if (first) { z = vz; first = false; }
                        }

                        if (xs.Count >= 3)
                            contours.AddPolygon(z, xs.ToArray(), ys.ToArray());
                    }
                }, null);
            }

            if (anyFailure && contours.PolygonCount == 0)
                log.Warning("structures: " + id, "no contour plane could be read");

            contours.Finalise();
            return contours;
        }

        private static bool TryReadVertex(dynamic vertex, out double x, out double y, out double z)
        {
            x = y = z = 0.0;

            bool lower =
                Dyn.TryGetDouble("vertex.x", () => vertex.x, null, out x) &&
                Dyn.TryGetDouble("vertex.y", () => vertex.y, null, out y) &&
                Dyn.TryGetDouble("vertex.z", () => vertex.z, null, out z);

            if (lower) return true;

            return Dyn.TryGetDouble("vertex.X", () => vertex.X, null, out x) &&
                   Dyn.TryGetDouble("vertex.Y", () => vertex.Y, null, out y) &&
                   Dyn.TryGetDouble("vertex.Z", () => vertex.Z, null, out z);
        }

        /// <summary>
        /// Rasterises a contour set onto a sampling grid.
        ///
        /// When <paramref name="mapper"/> is supplied, every grid point is carried through
        /// the registration before membership is tested. That is what puts a structure drawn
        /// on the registered image into the frame of the source image, so both masks share
        /// one grid and can be compared voxel by voxel — and it works for a deformable
        /// registration wherever point-to-point mapping is available.
        /// </summary>
        public static bool[] Rasterise(ContourSet contours, ImageGeometry grid, IPointMapper mapper)
        {
            var mask = new bool[(long)grid.XSize * grid.YSize * grid.ZSize];
            if (contours == null) return mask;

            for (int k = 0; k < grid.ZSize; k++)
            {
                for (int j = 0; j < grid.YSize; j++)
                {
                    int rowBase = grid.XSize * (j + grid.YSize * k);

                    for (int i = 0; i < grid.XSize; i++)
                    {
                        Vec3 point = grid.VoxelToPatient(i, j, k);

                        if (mapper != null)
                        {
                            Vec3 mapped;
                            if (!mapper.TryMap(point, out mapped)) continue;
                            point = mapped;
                        }

                        if (contours.Contains(point)) mask[rowBase + i] = true;
                    }
                }
            }

            return mask;
        }
    }
}
