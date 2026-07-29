using System;
using System.Collections.Generic;
using System.Globalization;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>A point landmark in patient coordinates, identified by its structure id.</summary>
    public sealed class Landmark
    {
        public string Id { get; set; }
        public Vec3 Position { get; set; }
        public string DicomType { get; set; }
    }

    /// <summary>
    /// Reads point landmarks from the structure sets attached to an image.
    ///
    /// TG-132 Table III defines Target Registration Error over "implanted or naturally
    /// occurring landmarks visualized on a pair of images". In an RT structure set those are
    /// the point-type structures: DICOM type MARKER, and ISOCENTER where it has been placed
    /// on an identifiable feature.
    ///
    /// Contour-based structures are deliberately excluded. Their centre of mass moves when
    /// the contour is edited, so it is not a landmark in the sense the report means, and
    /// using it would produce a TRE that looks valid and measures something else.
    /// </summary>
    public static class LandmarkExtractor
    {
        private static readonly HashSet<string> PointTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MARKER", "ISOCENTER" };

        public static List<Landmark> Read(dynamic imageLike, string label, DiagnosticLog log)
        {
            var landmarks = new List<Landmark>();
            if (imageLike == null) return landmarks;

            dynamic structureSets;
            string source;

            if (!Dyn.TryGetFirst("landmarks: structure sets of " + label, log, out structureSets, out source,
                    Dyn.Alt("Image.StructureSets", () => imageLike.Image.StructureSets),
                    Dyn.Alt("StructureSets", () => imageLike.StructureSets)))
            {
                return landmarks;
            }

            int rejectedForType = 0;
            int rejectedForPosition = 0;
            bool describedMemberSurfaceOnFailure = false;

            Dyn.TryInvoke("landmarks: walk structures of " + label, () =>
            {
                foreach (dynamic structureSet in structureSets)
                {
                    foreach (dynamic structure in structureSet.Structures)
                    {
                        if (structure == null) continue;

                        string id;
                        if (!Dyn.TryGetString("landmarks: structure id", () => structure.Id, null, out id))
                            continue;

                        string dicomType;
                        bool readOk = Dyn.TryGetString("landmarks: DicomType of " + id, () => structure.DicomType, log, out dicomType);
                        if (!readOk)
                        {
                            dicomType = string.Empty;

                            // On an API where VolumetricStructure has no DicomType property, this
                            // is why: MARKER/ISOCENTER can never be recognised, so TRE stays
                            // unreachable regardless of whether markers exist in the case. See
                            // the matching note in StructureRasterizer.ReadContourStructures.
                            if (!describedMemberSurfaceOnFailure)
                            {
                                describedMemberSurfaceOnFailure = true;
                                log.Warning("landmarks: DicomType", "DicomType is not a member of this API's " +
                                    "structure type, so no structure can be recognised as MARKER or ISOCENTER " +
                                    "here. " + MatrixReader.DescribeMemberSurface((object)structure));
                            }
                        }

                        if (!PointTypes.Contains(dicomType))
                        {
                            rejectedForType++;
                            continue;
                        }

                        Vec3 position;
                        if (!TryReadCentre(structure, id, log, out position))
                        {
                            rejectedForPosition++;
                            continue;
                        }

                        landmarks.Add(new Landmark
                        {
                            Id = id.Trim(),
                            Position = position,
                            DicomType = dicomType
                        });
                    }
                }
            }, log);

            log.Info("landmarks: " + label, string.Format(
                CultureInfo.InvariantCulture,
                "{0} point landmark(s) found via {1}; {2} structure(s) skipped as non-point types, {3} with unreadable centre",
                landmarks.Count, source, rejectedForType, rejectedForPosition));

            return landmarks;
        }

        private static bool TryReadCentre(dynamic structure, string id, DiagnosticLog log, out Vec3 position)
        {
            position = Vec3.Zero;

            dynamic centre;
            if (!Dyn.TryGet("landmarks: CenterPoint of " + id, () => structure.CenterPoint, log, out centre))
                return false;

            double x = 0.0, y = 0.0, z = 0.0;
            bool lower =
                Dyn.TryGetDouble("landmarks: centre.x", () => centre.x, null, out x) &&
                Dyn.TryGetDouble("landmarks: centre.y", () => centre.y, null, out y) &&
                Dyn.TryGetDouble("landmarks: centre.z", () => centre.z, null, out z);

            if (!lower)
            {
                bool upper =
                    Dyn.TryGetDouble("landmarks: centre.X", () => centre.X, log, out x) &&
                    Dyn.TryGetDouble("landmarks: centre.Y", () => centre.Y, log, out y) &&
                    Dyn.TryGetDouble("landmarks: centre.Z", () => centre.Z, log, out z);

                if (!upper) return false;
            }

            position = new Vec3(x, y, z);
            return position.IsFinite;
        }

        /// <summary>
        /// Pairs landmarks present on both image sets, matching on the structure identifier.
        ///
        /// Matching is case-insensitive and trims surrounding whitespace, because the same
        /// marker copied between series routinely differs in casing. Anything unmatched is
        /// reported rather than silently dropped: a marker that exists on one side only is
        /// usually a naming slip the user wants to know about.
        /// </summary>
        public static List<Tuple<Landmark, Landmark>> Match(
            List<Landmark> source, List<Landmark> target, DiagnosticLog log)
        {
            var pairs = new List<Tuple<Landmark, Landmark>>();
            if (source == null || target == null) return pairs;

            var targetById = new Dictionary<string, Landmark>(StringComparer.OrdinalIgnoreCase);
            foreach (Landmark landmark in target)
            {
                if (landmark.Id == null) continue;
                targetById[landmark.Id.Trim()] = landmark;
            }

            var unmatched = new List<string>();

            foreach (Landmark landmark in source)
            {
                Landmark counterpart;
                if (landmark.Id != null && targetById.TryGetValue(landmark.Id.Trim(), out counterpart))
                    pairs.Add(Tuple.Create(landmark, counterpart));
                else
                    unmatched.Add(landmark.Id ?? "(unnamed)");
            }

            if (unmatched.Count > 0)
            {
                log.Warning("landmarks: matching",
                    "present on the source image but not on the registered image: " +
                    string.Join(", ", unmatched.ToArray()));
            }

            return pairs;
        }
    }
}
