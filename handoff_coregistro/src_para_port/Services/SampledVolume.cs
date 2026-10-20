using System;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// A subsampled image volume, already converted to display values (HU for CT), held in
    /// memory together with its matching geometry.
    ///
    /// Subsampling happens at load time for two reasons: to bound memory (a 512x512x200 CT
    /// takes 100 MB as ushort) and to bound computation time so the UI does not block. The
    /// resulting effective resolution is reported explicitly alongside the metrics, because
    /// it conditions their interpretation.
    /// </summary>
    public sealed class SampledVolume
    {
        private readonly float[] _data;

        public ImageGeometry Geometry { get; private set; }

        public SampledVolume(ImageGeometry geometry, float[] data)
        {
            if (geometry == null) throw new ArgumentNullException("geometry");
            if (data == null) throw new ArgumentNullException("data");

            long expected = (long)geometry.XSize * geometry.YSize * geometry.ZSize;
            if (data.LongLength != expected)
                throw new ArgumentException("Buffer size does not match the geometry.", "data");

            Geometry = geometry;
            _data = data;
        }

        public float this[int i, int j, int k]
        {
            get { return _data[Index(i, j, k)]; }
        }

        private int Index(int i, int j, int k)
        {
            // Load-time subsampling keeps the dimensions well below int.MaxValue, and the
            // constructor has already verified that the buffer matches the geometry.
            return i + Geometry.XSize * (j + Geometry.YSize * k);
        }

        public bool IsInside(double i, double j, double k)
        {
            return i >= 0 && i <= Geometry.XSize - 1
                && j >= 0 && j <= Geometry.YSize - 1
                && k >= 0 && k <= Geometry.ZSize - 1;
        }

        /// <summary>
        /// Trilinear interpolation at continuous voxel coordinates. Returns false when the
        /// point falls outside the volume, which signals that this fixed-image voxel has no
        /// correspondence and must be excluded from the computation rather than contribute
        /// a zero.
        /// </summary>
        public bool TrySample(double i, double j, double k, out float value)
        {
            value = 0f;
            if (!IsInside(i, j, k)) return false;

            int i0 = (int)Math.Floor(i);
            int j0 = (int)Math.Floor(j);
            int k0 = (int)Math.Floor(k);

            int i1 = Math.Min(i0 + 1, Geometry.XSize - 1);
            int j1 = Math.Min(j0 + 1, Geometry.YSize - 1);
            int k1 = Math.Min(k0 + 1, Geometry.ZSize - 1);

            double fi = i - i0;
            double fj = j - j0;
            double fk = k - k0;

            double c000 = this[i0, j0, k0];
            double c100 = this[i1, j0, k0];
            double c010 = this[i0, j1, k0];
            double c110 = this[i1, j1, k0];
            double c001 = this[i0, j0, k1];
            double c101 = this[i1, j0, k1];
            double c011 = this[i0, j1, k1];
            double c111 = this[i1, j1, k1];

            double c00 = c000 * (1 - fi) + c100 * fi;
            double c10 = c010 * (1 - fi) + c110 * fi;
            double c01 = c001 * (1 - fi) + c101 * fi;
            double c11 = c011 * (1 - fi) + c111 * fi;

            double c0 = c00 * (1 - fj) + c10 * fj;
            double c1 = c01 * (1 - fj) + c11 * fj;

            value = (float)(c0 * (1 - fk) + c1 * fk);
            return true;
        }
    }
}
