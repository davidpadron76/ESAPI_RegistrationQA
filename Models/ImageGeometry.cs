using System;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Geometry of an image volume: origin, direction cosines, voxel size and dimensions.
    /// Allows conversion between voxel indices and patient coordinates, which is exactly
    /// what the previous version lacked: it compared voxels by index, ignoring origin,
    /// spacing and orientation.
    ///
    /// Axis convention in DICOM patient coordinates (HFS orientation):
    ///   X → left-right        (LR)
    ///   Y → anterior-posterior (AP)
    ///   Z → cranio-caudal      (CC)
    /// </summary>
    public sealed class ImageGeometry
    {
        public Vec3 Origin { get; private set; }
        public Vec3 XDirection { get; private set; }
        public Vec3 YDirection { get; private set; }
        public Vec3 ZDirection { get; private set; }

        /// <summary>Voxel size in mm along each volume axis.</summary>
        public double XRes { get; private set; }
        public double YRes { get; private set; }
        public double ZRes { get; private set; }

        public int XSize { get; private set; }
        public int YSize { get; private set; }
        public int ZSize { get; private set; }

        public ImageGeometry(
            Vec3 origin,
            Vec3 xDirection, Vec3 yDirection, Vec3 zDirection,
            double xRes, double yRes, double zRes,
            int xSize, int ySize, int zSize)
        {
            Origin = origin;
            XDirection = xDirection.Normalized();
            YDirection = yDirection.Normalized();
            ZDirection = zDirection.Normalized();
            XRes = xRes;
            YRes = yRes;
            ZRes = zRes;
            XSize = xSize;
            YSize = ySize;
            ZSize = zSize;
        }

        public long VoxelCount
        {
            get { return (long)XSize * YSize * ZSize; }
        }

        /// <summary>Equivalent isotropic resolution, used to report the effective sampling.</summary>
        public double CoarsestResolution
        {
            get { return Math.Max(XRes, Math.Max(YRes, ZRes)); }
        }

        /// <summary>
        /// Checks that the geometry is usable: positive dimensions, positive and finite
        /// resolutions, and mutually orthogonal direction cosines.
        /// </summary>
        public bool IsUsable(out string problem)
        {
            if (XSize <= 0 || YSize <= 0 || ZSize <= 0)
            {
                problem = "non-positive volume dimensions";
                return false;
            }

            if (!(XRes > 0) || !(YRes > 0) || !(ZRes > 0) ||
                double.IsNaN(XRes) || double.IsNaN(YRes) || double.IsNaN(ZRes) ||
                double.IsInfinity(XRes) || double.IsInfinity(YRes) || double.IsInfinity(ZRes))
            {
                problem = "non-positive or non-finite voxel size";
                return false;
            }

            if (!Origin.IsFinite)
            {
                problem = "non-finite origin";
                return false;
            }

            const double tolerance = 1e-3;
            if (Math.Abs(XDirection.Dot(YDirection)) > tolerance ||
                Math.Abs(XDirection.Dot(ZDirection)) > tolerance ||
                Math.Abs(YDirection.Dot(ZDirection)) > tolerance)
            {
                problem = "direction cosines are not mutually orthogonal";
                return false;
            }

            if (Math.Abs(XDirection.Length - 1.0) > tolerance ||
                Math.Abs(YDirection.Length - 1.0) > tolerance ||
                Math.Abs(ZDirection.Length - 1.0) > tolerance)
            {
                problem = "direction cosines are not unit vectors";
                return false;
            }

            problem = null;
            return true;
        }

        /// <summary>Continuous voxel index → patient coordinate in mm.</summary>
        public Vec3 VoxelToPatient(double i, double j, double k)
        {
            return Origin
                 + XDirection * (i * XRes)
                 + YDirection * (j * YRes)
                 + ZDirection * (k * ZRes);
        }

        /// <summary>
        /// Patient coordinate → continuous voxel index. Relies on the direction cosines
        /// being orthonormal, so that the inverse of the orientation matrix is its transpose
        /// and projecting onto each axis suffices.
        /// </summary>
        public void PatientToVoxel(Vec3 point, out double i, out double j, out double k)
        {
            Vec3 delta = point - Origin;
            i = delta.Dot(XDirection) / XRes;
            j = delta.Dot(YDirection) / YRes;
            k = delta.Dot(ZDirection) / ZRes;
        }

        /// <summary>The eight corners of the volume in patient coordinates.</summary>
        public Vec3[] BoundingBoxCorners()
        {
            double maxI = XSize - 1;
            double maxJ = YSize - 1;
            double maxK = ZSize - 1;

            return new[]
            {
                VoxelToPatient(0,    0,    0),
                VoxelToPatient(maxI, 0,    0),
                VoxelToPatient(0,    maxJ, 0),
                VoxelToPatient(maxI, maxJ, 0),
                VoxelToPatient(0,    0,    maxK),
                VoxelToPatient(maxI, 0,    maxK),
                VoxelToPatient(0,    maxJ, maxK),
                VoxelToPatient(maxI, maxJ, maxK)
            };
        }

        /// <summary>
        /// Equivalent geometry after subsampling by an integer step on each axis. The origin
        /// is unchanged (voxel 0,0,0 is preserved) and the voxel size is scaled.
        /// </summary>
        public ImageGeometry Subsampled(int stepX, int stepY, int stepZ, int newXSize, int newYSize, int newZSize)
        {
            return new ImageGeometry(
                Origin,
                XDirection, YDirection, ZDirection,
                XRes * stepX, YRes * stepY, ZRes * stepZ,
                newXSize, newYSize, newZSize);
        }
    }
}
