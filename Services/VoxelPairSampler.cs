using System;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    public sealed class VoxelPairSet
    {
        public float[] FixedValues { get; set; }
        public float[] MovingValues { get; set; }
        public int Count { get; set; }

        /// <summary>Fracción de vóxeles visitados de la imagen fija que tienen correspondencia.</summary>
        public double OverlapFraction { get; set; }

        public string Problem { get; set; }
    }

    /// <summary>
    /// Empareja vóxeles físicamente correspondientes entre dos volúmenes aplicando el mapeo
    /// del registro.
    ///
    /// Esto es lo que la versión anterior no hacía: comparaba <c>bufferFijo[x,y]</c> con
    /// <c>bufferMovil[x,y]</c> por índice de vóxel, de modo que el resultado medía la
    /// coincidencia de los sistemas de índices y no la calidad del registro. Entre un CT de
    /// planificación y un CBCT, con distinto FOV, espaciado y origen, esa comparación carece
    /// de interpretación física.
    /// </summary>
    public static class VoxelPairSampler
    {
        /// <summary>Cota superior de pares retenidos, para acotar memoria y tiempo de ordenación.</summary>
        public const int MaxRetainedPairs = 2000000;

        /// <summary>Solapamiento mínimo por debajo del cual las métricas no son representativas.</summary>
        public const double MinimumOverlapFraction = 0.10;

        /// <summary>
        /// Recorre la rejilla de la imagen fija, lleva cada punto a coordenadas de paciente,
        /// le aplica el mapeo del registro y muestrea la imagen móvil por interpolación
        /// trilineal.
        /// </summary>
        public static VoxelPairSet Pair(
            SampledVolume fixedVolume,
            SampledVolume movingVolume,
            IPointMapper mapper)
        {
            var result = new VoxelPairSet();

            if (fixedVolume == null || movingVolume == null)
            {
                result.Problem = "falta uno de los volúmenes";
                return result;
            }

            if (mapper == null)
            {
                result.Problem = "no se dispone del mapeo entre las dos imágenes";
                return result;
            }

            ImageGeometry fixedGeometry = fixedVolume.Geometry;
            long totalCandidates = fixedGeometry.VoxelCount;
            int budget = Math.Min(MaxRetainedPairs, Math.Max(1, mapper.RecommendedSampleBudget));

            // Si la rejilla fija excede el presupuesto se recorre con paso uniforme en lugar
            // de truncar: truncar sesgaría el muestreo hacia los primeros cortes, que en un
            // volumen de planificación son mayoritariamente aire.
            int stride = 1;
            if (totalCandidates > budget)
                stride = (int)Math.Ceiling((double)totalCandidates / budget);

            int capacity = (int)Math.Min(budget, (totalCandidates + stride - 1) / stride);
            if (capacity <= 0)
            {
                result.Problem = "la rejilla de muestreo quedó vacía";
                return result;
            }

            var fixedValues = new float[capacity];
            var movingValues = new float[capacity];

            int paired = 0;
            long visited = 0;
            long linear = -1;

            for (int k = 0; k < fixedGeometry.ZSize && paired < capacity; k++)
            {
                for (int j = 0; j < fixedGeometry.YSize && paired < capacity; j++)
                {
                    for (int i = 0; i < fixedGeometry.XSize && paired < capacity; i++)
                    {
                        linear++;
                        if (linear % stride != 0) continue;

                        visited++;

                        Vec3 patientFixed = fixedGeometry.VoxelToPatient(i, j, k);

                        Vec3 patientMoving;
                        if (!mapper.TryMap(patientFixed, out patientMoving)) continue;

                        double mi, mj, mk;
                        movingVolume.Geometry.PatientToVoxel(patientMoving, out mi, out mj, out mk);

                        float movingValue;
                        if (!movingVolume.TrySample(mi, mj, mk, out movingValue)) continue;

                        fixedValues[paired] = fixedVolume[i, j, k];
                        movingValues[paired] = movingValue;
                        paired++;
                    }
                }
            }

            result.FixedValues = fixedValues;
            result.MovingValues = movingValues;
            result.Count = paired;
            result.OverlapFraction = visited > 0 ? (double)paired / visited : 0.0;

            if (paired == 0)
            {
                result.Problem = "las dos imágenes no se solapan tras aplicar el registro";
            }
            else if (result.OverlapFraction < MinimumOverlapFraction)
            {
                result.Problem = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "solapamiento insuficiente ({0:P1} de la imagen fija); las métricas de intensidad no serían representativas",
                    result.OverlapFraction);
            }

            return result;
        }
    }
}
