using System;
using System.Collections.Generic;
using System.Text;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Resolves how to read one plane of voxels out of the API, and then reads them.
    ///
    /// The previous arrangement tried <c>Frame.GetVoxels</c> with an <c>int[,]</c> buffer and
    /// then with a <c>ushort[,]</c> one, and accepted whichever did not throw. On a real
    /// Eclipse the ushort call did not throw — and did not write anything either. Every voxel
    /// of both volumes came back as 0, which after the HU ramp reads as a uniform −1000 HU,
    /// and the whole audit collapsed four steps later: no intensity metrics, and no structure
    /// metrics either, because the sampling geometry is only kept when a volume loads.
    ///
    /// Two lessons are built in here.
    ///
    /// First, and it is the same one the deformable point mapper taught: <b>a call that does
    /// not throw is not a call that worked</b>. A candidate is accepted only after it has
    /// demonstrably written non-constant data.
    ///
    /// Second, the probe plane matters. Plane 0 of a head CT is uniform air, so probing there
    /// cannot distinguish "did not fill the buffer" from "filled it with air". The probes run
    /// at a third, a half and two thirds of the way through the volume, where a diagnostic
    /// image has anatomy by construction.
    ///
    /// The search is also wider than before. Geometry resolves through <c>Frame</c>, which is
    /// correct, but that does not mean the voxels live there: the parent image object answered
    /// for modality and frame of reference, and in classic ESAPI <c>Image.GetVoxels</c> is the
    /// documented method. Every carrier is tried against every buffer shape.
    /// </summary>
    public sealed class VoxelPlaneReader
    {
        private readonly PlaneBuffer _buffer;
        private readonly object _carrier;

        private VoxelPlaneReader(object carrier, PlaneBuffer buffer, string description)
        {
            _carrier = carrier;
            _buffer = buffer;
            Description = description;
        }

        /// <summary>Which carrier and buffer shape answered, for the diagnostics.</summary>
        public string Description { get; private set; }

        /// <summary>Fills the internal buffer with one plane. False if the call failed.</summary>
        public bool TryReadPlane(int plane, string operation, DiagnosticLog log)
        {
            return _buffer.TryFill(_carrier, plane, operation, log);
        }

        /// <summary>Raw voxel value at (i, j) of the plane last read.</summary>
        public int this[int i, int j]
        {
            get { return _buffer.Get(i, j); }
        }

        // ------------------------------------------------------------------ resolution

        /// <summary>
        /// Finds a working combination of carrier and buffer shape, or null.
        ///
        /// <paramref name="frame"/> is the object the geometry was read from and
        /// <paramref name="imageLike"/> the image object it came from; both are tried, along
        /// with the parent image, because which one carries the pixel data varies.
        /// </summary>
        public static VoxelPlaneReader Resolve(
            dynamic frame, dynamic imageLike, ImageGeometry geometry, string label, DiagnosticLog log)
        {
            List<Tuple<string, object>> carriers = BuildCarriers(frame, imageLike, log, label);
            int[] probePlanes = BuildProbePlanes(geometry.ZSize);

            var attempts = new List<string>();

            foreach (Tuple<string, object> carrier in carriers)
            {
                foreach (PlaneBuffer buffer in BuildBuffers(geometry))
                {
                    string candidate = carrier.Item1 + ".GetVoxels(int, " + buffer.Description + ")";

                    bool called = false;
                    bool filled = false;

                    foreach (int plane in probePlanes)
                    {
                        buffer.Clear();

                        // The probe is logged at Info, not Failure: trying a combination that
                        // this Eclipse version does not accept is how the search works, not a
                        // fault. Only exhausting every combination is a fault.
                        if (!buffer.TryFill(carrier.Item2, plane, label + ": probe " + candidate, null))
                            break;

                        called = true;

                        if (buffer.IsConstant(geometry.XSize, geometry.YSize)) continue;

                        filled = true;
                        break;
                    }

                    if (!called)
                    {
                        attempts.Add(candidate + " → rejected the arguments");
                        continue;
                    }

                    if (!filled)
                    {
                        attempts.Add(candidate + " → accepted the call but left the buffer unchanged");
                        continue;
                    }

                    log.Info(label + ": GetVoxels", "reading voxels through " + candidate);
                    return new VoxelPlaneReader(carrier.Item2, buffer, candidate);
                }
            }

            ReportFailure(carriers, attempts, label, log);
            return null;
        }

        private static List<Tuple<string, object>> BuildCarriers(
            dynamic frame, dynamic imageLike, DiagnosticLog log, string label)
        {
            var carriers = new List<Tuple<string, object>>();

            Action<string, object> add = (name, instance) =>
            {
                if (instance == null) return;

                // The same object often answers under two names — geometry may already have
                // resolved through .Image — and probing it twice would double the log for no
                // gain.
                foreach (Tuple<string, object> existing in carriers)
                    if (ReferenceEquals(existing.Item2, instance)) return;

                carriers.Add(Tuple.Create(name, instance));
            };

            add("Frame", (object)frame);

            dynamic parentImage;
            if (Dyn.TryGet(label + ": parent image for voxels", () => imageLike.Image, null, out parentImage))
                add("Image", (object)parentImage);

            add("image object", (object)imageLike);

            return carriers;
        }

        /// <summary>
        /// Planes to probe: a third, a half and two thirds of the way in. Never plane 0 — on a
        /// head CT that slice is uniform air, so it cannot tell an unwritten buffer from a
        /// correctly written one.
        /// </summary>
        private static int[] BuildProbePlanes(int zSize)
        {
            if (zSize <= 1) return new[] { 0 };

            var planes = new List<int>();
            foreach (double fraction in new[] { 0.5, 0.33, 0.66 })
            {
                int plane = (int)(zSize * fraction);
                if (plane < 0) plane = 0;
                if (plane > zSize - 1) plane = zSize - 1;
                if (!planes.Contains(plane)) planes.Add(plane);
            }

            return planes.ToArray();
        }

        private static IEnumerable<PlaneBuffer> BuildBuffers(ImageGeometry geometry)
        {
            yield return new UShortPlaneBuffer(geometry.XSize, geometry.YSize, false);
            yield return new IntPlaneBuffer(geometry.XSize, geometry.YSize, false);

            // A transposed buffer is only worth trying on a non-square image. When XSize
            // equals YSize the two shapes are the same allocation and the same call, so the
            // attempt would be indistinguishable from the one above.
            if (geometry.XSize != geometry.YSize)
            {
                yield return new UShortPlaneBuffer(geometry.XSize, geometry.YSize, true);
                yield return new IntPlaneBuffer(geometry.XSize, geometry.YSize, true);
            }
        }

        private static void ReportFailure(
            List<Tuple<string, object>> carriers, List<string> attempts, string label, DiagnosticLog log)
        {
            var text = new StringBuilder();
            text.Append("no way of reading voxels produced any data. Attempts: ");
            text.Append(attempts.Count > 0 ? string.Join("; ", attempts.ToArray()) : "(none)");
            text.Append(". ");

            foreach (Tuple<string, object> carrier in carriers)
            {
                text.Append(carrier.Item1).Append(" — ");
                text.Append(MatrixReader.DescribeMemberSurface(carrier.Item2));
                text.Append(' ');
            }

            text.Append("Send this line with your Eclipse version: it names every object that " +
                        "could carry the pixel data and everything each one exposes.");

            log.Failure(label + ": GetVoxels", text.ToString());
        }

        // ------------------------------------------------------------------ buffer shapes

        /// <summary>
        /// One plane's worth of storage, plus the knowledge of how to fill it and how to read
        /// it back.
        ///
        /// Each shape keeps its array at its concrete static type on purpose. Passing it as
        /// <c>object</c> through a dynamic call would make the runtime binder look for a
        /// <c>GetVoxels(int, object)</c> overload — dynamic dispatch uses the static type of
        /// arguments that are not themselves dynamic — and every candidate would fail for a
        /// reason that has nothing to do with the API.
        /// </summary>
        private abstract class PlaneBuffer
        {
            protected readonly bool Transposed;

            protected PlaneBuffer(bool transposed)
            {
                Transposed = transposed;
            }

            public abstract string Description { get; }

            public abstract bool TryFill(object carrier, int plane, string operation, DiagnosticLog log);

            public abstract int Get(int i, int j);

            public abstract void Clear();

            /// <summary>
            /// True when every voxel of the plane holds the same value — the signature of a
            /// call that returned without writing.
            /// </summary>
            public bool IsConstant(int xSize, int ySize)
            {
                int first = Get(0, 0);

                for (int j = 0; j < ySize; j++)
                    for (int i = 0; i < xSize; i++)
                        if (Get(i, j) != first) return false;

                return true;
            }
        }

        private sealed class UShortPlaneBuffer : PlaneBuffer
        {
            private readonly ushort[,] _data;

            public UShortPlaneBuffer(int xSize, int ySize, bool transposed)
                : base(transposed)
            {
                _data = transposed ? new ushort[ySize, xSize] : new ushort[xSize, ySize];
            }

            public override string Description
            {
                get { return Transposed ? "ushort[y,x]" : "ushort[x,y]"; }
            }

            public override bool TryFill(object carrier, int plane, string operation, DiagnosticLog log)
            {
                dynamic target = carrier;
                ushort[,] buffer = _data;
                return Dyn.TryInvoke(operation, () => target.GetVoxels(plane, buffer), log);
            }

            public override int Get(int i, int j)
            {
                return Transposed ? _data[j, i] : _data[i, j];
            }

            public override void Clear()
            {
                Array.Clear(_data, 0, _data.Length);
            }
        }

        private sealed class IntPlaneBuffer : PlaneBuffer
        {
            private readonly int[,] _data;

            public IntPlaneBuffer(int xSize, int ySize, bool transposed)
                : base(transposed)
            {
                _data = transposed ? new int[ySize, xSize] : new int[xSize, ySize];
            }

            public override string Description
            {
                get { return Transposed ? "int[y,x]" : "int[x,y]"; }
            }

            public override bool TryFill(object carrier, int plane, string operation, DiagnosticLog log)
            {
                dynamic target = carrier;
                int[,] buffer = _data;
                return Dyn.TryInvoke(operation, () => target.GetVoxels(plane, buffer), log);
            }

            public override int Get(int i, int j)
            {
                return Transposed ? _data[j, i] : _data[i, j];
            }

            public override void Clear()
            {
                Array.Clear(_data, 0, _data.Length);
            }
        }
    }
}
