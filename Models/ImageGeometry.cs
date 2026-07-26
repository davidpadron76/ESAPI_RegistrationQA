using System;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Geometría de un volumen de imagen: origen, cosenos directores, tamaño de vóxel y
    /// dimensiones. Permite convertir entre índices de vóxel y coordenadas de paciente,
    /// que es exactamente lo que faltaba en la versión anterior: allí se comparaban
    /// vóxeles por índice, ignorando origen, espaciado y orientación.
    ///
    /// Convención de ejes en coordenadas de paciente DICOM (orientación HFS):
    ///   X → izquierda-derecha  (LR)
    ///   Y → anterior-posterior (AP)
    ///   Z → cráneo-caudal      (CC)
    /// </summary>
    public sealed class ImageGeometry
    {
        public Vec3 Origin { get; private set; }
        public Vec3 XDirection { get; private set; }
        public Vec3 YDirection { get; private set; }
        public Vec3 ZDirection { get; private set; }

        /// <summary>Tamaño de vóxel en mm a lo largo de cada eje del volumen.</summary>
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

        /// <summary>Resolución isotrópica equivalente, usada para informar del muestreo efectivo.</summary>
        public double CoarsestResolution
        {
            get { return Math.Max(XRes, Math.Max(YRes, ZRes)); }
        }

        /// <summary>
        /// Comprueba que la geometría es utilizable: dimensiones positivas, resoluciones
        /// positivas y finitas, y cosenos directores mutuamente ortogonales.
        /// </summary>
        public bool IsUsable(out string problem)
        {
            if (XSize <= 0 || YSize <= 0 || ZSize <= 0)
            {
                problem = "dimensiones de volumen no positivas";
                return false;
            }

            if (!(XRes > 0) || !(YRes > 0) || !(ZRes > 0) ||
                double.IsNaN(XRes) || double.IsNaN(YRes) || double.IsNaN(ZRes) ||
                double.IsInfinity(XRes) || double.IsInfinity(YRes) || double.IsInfinity(ZRes))
            {
                problem = "tamaño de vóxel no positivo o no finito";
                return false;
            }

            if (!Origin.IsFinite)
            {
                problem = "origen no finito";
                return false;
            }

            const double tolerance = 1e-3;
            if (Math.Abs(XDirection.Dot(YDirection)) > tolerance ||
                Math.Abs(XDirection.Dot(ZDirection)) > tolerance ||
                Math.Abs(YDirection.Dot(ZDirection)) > tolerance)
            {
                problem = "los cosenos directores no son mutuamente ortogonales";
                return false;
            }

            if (Math.Abs(XDirection.Length - 1.0) > tolerance ||
                Math.Abs(YDirection.Length - 1.0) > tolerance ||
                Math.Abs(ZDirection.Length - 1.0) > tolerance)
            {
                problem = "los cosenos directores no son unitarios";
                return false;
            }

            problem = null;
            return true;
        }

        /// <summary>Índice de vóxel (continuo) → coordenada de paciente en mm.</summary>
        public Vec3 VoxelToPatient(double i, double j, double k)
        {
            return Origin
                 + XDirection * (i * XRes)
                 + YDirection * (j * YRes)
                 + ZDirection * (k * ZRes);
        }

        /// <summary>
        /// Coordenada de paciente → índice de vóxel (continuo). Se apoya en que los cosenos
        /// directores son ortonormales, por lo que la inversa de la matriz de orientación es
        /// su traspuesta y basta con proyectar sobre cada eje.
        /// </summary>
        public void PatientToVoxel(Vec3 point, out double i, out double j, out double k)
        {
            Vec3 delta = point - Origin;
            i = delta.Dot(XDirection) / XRes;
            j = delta.Dot(YDirection) / YRes;
            k = delta.Dot(ZDirection) / ZRes;
        }

        /// <summary>Los ocho vértices del volumen en coordenadas de paciente.</summary>
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
        /// Geometría equivalente tras submuestrear por un paso entero en cada eje. El origen
        /// no cambia (el vóxel 0,0,0 se conserva) y el tamaño de vóxel se escala.
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
