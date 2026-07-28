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

        public sealed class NamedStructure
        {
            public string Id { get; set; }
            public string DicomType { get; set; }
            public ContourSet Contours { get; set; }
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

                        string dicomType;
                        if (!Dyn.TryGetString("structures: DicomType of " + id, () => structure.DicomType, null, out dicomType))
                            dicomType = string.Empty;

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

                        structures.Add(new NamedStructure
                        {
                            Id = id.Trim(),
                            DicomType = dicomType,
                            Contours = contours
                        });
                    }
                }
            }, log);

            log.Info("structures: " + label, string.Format(CultureInfo.InvariantCulture,
                "{0} contour structure(s) read via {1}; {2} point structure(s) skipped, {3} empty",
                structures.Count, source, skippedPoints, skippedEmpty));

            return structures;
        }

        private static int ReadPlaneCount(dynamic structureSet, DiagnosticLog log)
        {
            int planes;
            if (Dyn.TryGetInt("structures: plane count", () => structureSet.Image.ZSize, log, out planes))
                return planes;

            // Without a plane count there is nothing to iterate; the caller will report the
            // structure as having no contours, which is accurate.
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
