using System;

namespace ESAPI_RegistrationQA.Services
{
    public sealed class SimilarityResult
    {
        /// <summary>Correlación de Pearson con signo, rango [-1,1]. Negativa = contraste invertido.</summary>
        public double? Ncc { get; set; }

        /// <summary>Diferencia cuadrática media normalizada por el cuadrado del rango robusto.</summary>
        public double? Ssd { get; set; }

        /// <summary>NMI de Studholme: (H(A)+H(B))/H(A,B), rango [1,2].</summary>
        public double? Nmi { get; set; }

        public int SampleCount { get; set; }

        /// <summary>Bins usados en el histograma conjunto del NMI (0 si no se calculó).</summary>
        public int HistogramBins { get; set; }

        public string Problem { get; set; }
    }

    /// <summary>
    /// Métricas de similitud sobre pares de intensidades ya emparejados espacialmente.
    ///
    /// Esta clase no sabe nada de ESAPI, de transformaciones ni de geometría: recibe dos
    /// vectores de intensidades correspondientes al mismo punto físico y calcula. Es
    /// deliberadamente pura para poder verificarse sin un entorno Eclipse.
    /// </summary>
    public static class SimilarityCalculator
    {
        /// <summary>Número mínimo de pares para que las métricas tengan sentido estadístico.</summary>
        public const int MinimumSamples = 1000;

        /// <summary>Cotas del número de bins del histograma conjunto.</summary>
        public const int MaxHistogramBins = 64;
        public const int MinHistogramBins = 16;

        /// <summary>Ocupación media mínima por celda del histograma conjunto.</summary>
        private const int MinSamplesPerJointCell = 20;

        /// <summary>
        /// Elige el número de bins en función del tamaño de la muestra.
        ///
        /// La entropía conjunta se sesga a la baja cuando muchas celdas quedan vacías, lo
        /// que inflaría artificialmente el NMI. Fijar 64 bins con pocas muestras —el caso de
        /// un registro deformable, donde el mapeo punto a punto obliga a un presupuesto
        /// reducido— produciría un valor optimista y no comparable entre estudios.
        /// </summary>
        public static int ChooseBinCount(int sampleCount)
        {
            int bins = (int)Math.Floor(Math.Sqrt((double)sampleCount / MinSamplesPerJointCell));
            if (bins > MaxHistogramBins) bins = MaxHistogramBins;
            if (bins < MinHistogramBins) bins = MinHistogramBins;
            return bins;
        }

        public static SimilarityResult Compute(float[] fixedValues, float[] movingValues, int count)
        {
            var result = new SimilarityResult { SampleCount = count };

            if (fixedValues == null || movingValues == null)
            {
                result.Problem = "vectores de muestras nulos";
                return result;
            }

            if (count < MinimumSamples)
            {
                result.Problem = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "sólo {0} pares de vóxeles solapados; se requieren al menos {1} para una estimación estable",
                    count, MinimumSamples);
                return result;
            }

            if (count > fixedValues.Length || count > movingValues.Length)
            {
                result.Problem = "recuento de muestras mayor que los vectores suministrados";
                return result;
            }

            // --- Estadísticos de primer y segundo orden en una sola pasada ---
            double sumF = 0, sumM = 0, sumFM = 0, sumF2 = 0, sumM2 = 0, sumDiff2 = 0;

            for (int i = 0; i < count; i++)
            {
                double f = fixedValues[i];
                double m = movingValues[i];

                sumF += f;
                sumM += m;
                sumFM += f * m;
                sumF2 += f * f;
                sumM2 += m * m;

                double d = f - m;
                sumDiff2 += d * d;
            }

            double n = count;
            double covariance = (n * sumFM) - (sumF * sumM);
            double varianceF = (n * sumF2) - (sumF * sumF);
            double varianceM = (n * sumM2) - (sumM * sumM);

            if (varianceF <= 0 || varianceM <= 0)
            {
                result.Problem = "una de las imágenes es constante en la región solapada (varianza nula)";
                return result;
            }

            result.Ncc = covariance / Math.Sqrt(varianceF * varianceM);

            // --- SSD normalizada por el rango robusto de la imagen fija ---
            double p1, p99;
            RobustRange(fixedValues, count, out p1, out p99);
            double range = p99 - p1;

            if (range > 0)
            {
                result.Ssd = (sumDiff2 / n) / (range * range);
            }

            // --- NMI sobre el histograma conjunto ---
            double mp1, mp99;
            RobustRange(movingValues, count, out mp1, out mp99);

            if (range > 0 && (mp99 - mp1) > 0)
            {
                result.HistogramBins = ChooseBinCount(count);
                result.Nmi = ComputeNmi(
                    fixedValues, movingValues, count,
                    p1, p99, mp1, mp99, result.HistogramBins);
            }

            return result;
        }

        /// <summary>
        /// NMI de Studholme: (H(A) + H(B)) / H(A,B).
        ///
        /// Se calcula sobre el histograma conjunto real. La versión anterior devolvía
        /// 1.20 + 0.45·NCC, que es una función lineal del NCC y no aporta información
        /// independiente alguna: para un par multimodal, donde precisamente el NCC deja de
        /// ser válido, ese "NMI" heredaba su misma degradación.
        /// </summary>
        public static double? ComputeNmi(
            float[] a, float[] b, int count,
            double aMin, double aMax,
            double bMin, double bMax,
            int bins)
        {
            if (bins < 2) return null;
            if (!(aMax > aMin) || !(bMax > bMin)) return null;

            var joint = new double[bins, bins];
            double aScale = bins / (aMax - aMin);
            double bScale = bins / (bMax - bMin);

            for (int i = 0; i < count; i++)
            {
                int ai = Bin(a[i], aMin, aScale, bins);
                int bi = Bin(b[i], bMin, bScale, bins);
                joint[ai, bi] += 1.0;
            }

            var marginalA = new double[bins];
            var marginalB = new double[bins];

            for (int x = 0; x < bins; x++)
            {
                for (int y = 0; y < bins; y++)
                {
                    double v = joint[x, y];
                    marginalA[x] += v;
                    marginalB[y] += v;
                }
            }

            double total = count;
            double entropyA = Entropy(marginalA, total);
            double entropyB = Entropy(marginalB, total);
            double entropyJoint = EntropyJoint(joint, total, bins);

            if (entropyJoint <= 1e-12) return null;

            return (entropyA + entropyB) / entropyJoint;
        }

        private static int Bin(double value, double min, double scale, int bins)
        {
            int index = (int)((value - min) * scale);
            if (index < 0) return 0;
            if (index >= bins) return bins - 1;
            return index;
        }

        private static double Entropy(double[] counts, double total)
        {
            double entropy = 0.0;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] <= 0) continue;
                double p = counts[i] / total;
                entropy -= p * Math.Log(p, 2.0);
            }
            return entropy;
        }

        private static double EntropyJoint(double[,] joint, double total, int bins)
        {
            double entropy = 0.0;
            for (int x = 0; x < bins; x++)
            {
                for (int y = 0; y < bins; y++)
                {
                    double c = joint[x, y];
                    if (c <= 0) continue;
                    double p = c / total;
                    entropy -= p * Math.Log(p, 2.0);
                }
            }
            return entropy;
        }

        /// <summary>
        /// Percentiles 1 y 99. Se usa el rango robusto en lugar del mínimo/máximo absolutos
        /// para que un único vóxel con artefacto metálico (o el valor centinela de aire
        /// fuera del FOV) no comprima todo el histograma.
        ///
        /// Sustituye al divisor fijo 4096.0 de la versión anterior, que asumía un rango de
        /// CT de 12 bits y carecía de sentido para MR o PET.
        /// </summary>
        public static void RobustRange(float[] values, int count, out double p1, out double p99)
        {
            var copy = new float[count];
            Array.Copy(values, copy, count);
            Array.Sort(copy);

            p1 = copy[(int)(0.01 * (count - 1))];
            p99 = copy[(int)(0.99 * (count - 1))];
        }
    }
}
