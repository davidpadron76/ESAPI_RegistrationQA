using System;
using System.Globalization;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Traduce un objeto de imagen de la API de Varian a un <see cref="SampledVolume"/>
    /// autónomo: geometría explícita y vóxeles ya convertidos a valores de display.
    ///
    /// Todo el acceso pasa por <see cref="Dyn"/>, de modo que cada propiedad que no exista
    /// en la versión de Eclipse en uso queda anotada en la bitácora en lugar de perderse.
    /// </summary>
    public static class EsapiImageReader
    {
        /// <summary>
        /// Máximo de muestras por eje tras submuestrear. 128³ ≈ 2·10⁶ vóxeles (8 MB en float),
        /// suficiente para un histograma conjunto de 64x64 con dos órdenes de magnitud de
        /// margen, y con un coste de lectura acotado.
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
                result.Problem = label + ": el objeto de imagen es nulo";
                return result;
            }

            // El portador de la geometría y los vóxeles es .Frame en VMS.IRS y el propio
            // objeto en ESAPI clásico. Se prueban ambos y se anota cuál respondió.
            dynamic frame;
            string frameSource;
            if (!Dyn.TryGetFirst(
                    label + ": localizar portador de vóxeles", log, out frame, out frameSource,
                    Dyn.Alt("Frame", () => imageLike.Frame),
                    Dyn.Alt("Image", () => imageLike.Image),
                    Dyn.Alt("objeto directo", () => imageLike)))
            {
                result.Problem = label + ": no se encontró un objeto con geometría de imagen";
                return result;
            }

            log.Info(label + ": portador de vóxeles", "resuelto vía " + frameSource);

            ImageGeometry geometry = ReadGeometry(frame, label, log);
            if (geometry == null)
            {
                result.Problem = label + ": no se pudo reconstruir la geometría de la imagen";
                return result;
            }

            string geometryProblem;
            if (!geometry.IsUsable(out geometryProblem))
            {
                result.Problem = label + ": geometría inconsistente (" + geometryProblem + ")";
                log.Failure(label + ": validación de geometría", geometryProblem);
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

        // ------------------------------------------------------------------ geometría

        private static ImageGeometry ReadGeometry(dynamic frame, string label, DiagnosticLog log)
        {
            int xSize, ySize, zSize;
            if (!Dyn.TryGetInt(label + ": XSize", () => frame.XSize, log, out xSize)) return null;
            if (!Dyn.TryGetInt(label + ": YSize", () => frame.YSize, log, out ySize)) return null;
            if (!Dyn.TryGetInt(label + ": ZSize", () => frame.ZSize, log, out zSize))
            {
                // Un portador que sólo expone un plano se trata como volumen de un corte.
                zSize = 1;
                log.Warning(label + ": ZSize", "no disponible; se asume un único plano");
            }

            double xRes, yRes, zRes;
            if (!Dyn.TryGetDouble(label + ": XRes", () => frame.XRes, log, out xRes)) return null;
            if (!Dyn.TryGetDouble(label + ": YRes", () => frame.YRes, log, out yRes)) return null;
            if (!Dyn.TryGetDouble(label + ": ZRes", () => frame.ZRes, log, out zRes))
            {
                zRes = 1.0;
                log.Warning(label + ": ZRes", "no disponible; se asume 1 mm entre cortes");
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
                // Orientación axial canónica. Es la correcta para la inmensa mayoría de
                // series de planificación, pero se deja constancia porque un estudio
                // adquirido con gantry inclinado quedaría mal interpretado.
                xDirection = new Vec3(1, 0, 0);
                yDirection = new Vec3(0, 1, 0);
                zDirection = new Vec3(0, 0, 1);
                log.Warning(
                    label + ": cosenos directores",
                    "no expuestos por la API; se asume orientación axial canónica (X=LR, Y=AP, Z=CC)");
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

            // VVector expone x/y/z en minúscula; otras APIs usan X/Y/Z.
            // Se inicializan porque la evaluación en cortocircuito puede dejar sin invocar
            // alguna de las lecturas, y entonces su parámetro de salida no llega a asignarse.
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
                    log.Failure(label + ": " + propertyName, "el vector no expone componentes x/y/z ni X/Y/Z");
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
                    label + ": modalidad", log, out value, out source,
                    Dyn.Alt("Image.Series.Modality", () => imageLike.Image.Series.Modality),
                    Dyn.Alt("Series.Modality", () => imageLike.Series.Modality),
                    Dyn.Alt("Modality", () => imageLike.Modality),
                    Dyn.Alt("Frame.Modality", () => frame.Modality)))
            {
                modalityText = Convert.ToString(value, CultureInfo.InvariantCulture);
                ImageModality parsed = RegistrationContext.ParseModality(modalityText);
                log.Info(label + ": modalidad", modalityText + " (vía " + source + ") → " + parsed);
                return parsed;
            }

            log.Warning(label + ": modalidad", "no expuesta por la API");
            return ImageModality.Unknown;
        }

        // ------------------------------------------------------------------ intensidad

        /// <summary>Relación lineal vóxel crudo → valor de display (HU en CT).</summary>
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
        /// Determina la rampa vóxel→HU sondeando <c>VoxelToDisplayValue</c> en tres puntos y
        /// verificando la linealidad.
        ///
        /// Se resuelve así, y no llamando al método por cada vóxel, porque cada invocación
        /// dinámica cuesta órdenes de magnitud más que una multiplicación: para dos millones
        /// de vóxeles la diferencia es entre milisegundos y minutos.
        /// </summary>
        private static IntensityScale ProbeIntensityScale(dynamic frame, string label, DiagnosticLog log)
        {
            var scale = new IntensityScale();

            // Inicializados por la misma razón que en TryReadVector: el cortocircuito de &&
            // puede dejar sin invocar alguna de las sondas.
            double v0 = 0.0, v1000 = 0.0, v500 = 0.0;
            bool probed =
                Dyn.TryGetDouble(label + ": VoxelToDisplayValue(0)", () => frame.VoxelToDisplayValue(0), log, out v0) &&
                Dyn.TryGetDouble(label + ": VoxelToDisplayValue(1000)", () => frame.VoxelToDisplayValue(1000), null, out v1000) &&
                Dyn.TryGetDouble(label + ": VoxelToDisplayValue(500)", () => frame.VoxelToDisplayValue(500), null, out v500);

            if (!probed)
            {
                log.Warning(
                    label + ": escalado de intensidad",
                    "VoxelToDisplayValue no disponible; se usan los valores crudos del vóxel. " +
                    "Las métricas de intensidad siguen siendo válidas (NCC y NMI son invariantes " +
                    "frente a una transformación afín común), pero la SSD no es comparable en HU.");
                return scale;
            }

            double slope = (v1000 - v0) / 1000.0;
            double intercept = v0;
            double predicted = slope * 500.0 + intercept;

            if (Math.Abs(predicted - v500) > 1e-6 * Math.Max(1.0, Math.Abs(v500)))
            {
                log.Warning(
                    label + ": escalado de intensidad",
                    "la rampa vóxel→display no es lineal; se usan los valores crudos");
                return scale;
            }

            scale.Slope = slope;
            scale.Intercept = intercept;
            scale.IsIdentity = Math.Abs(slope - 1.0) < 1e-12 && Math.Abs(intercept) < 1e-12;

            log.Info(
                label + ": escalado de intensidad",
                string.Format(CultureInfo.InvariantCulture,
                    "display = {0:0.######}·vóxel + {1:0.######}", slope, intercept));

            return scale;
        }

        // ------------------------------------------------------------------ vóxeles

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
                label + ": submuestreo",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}x{1}x{2} → {3}x{4}x{5} (paso {6}/{7}/{8}); resolución efectiva {9:F2}x{10:F2}x{11:F2} mm",
                    geometry.XSize, geometry.YSize, geometry.ZSize,
                    newX, newY, newZ, stepX, stepY, stepZ,
                    reduced.XRes, reduced.YRes, reduced.ZRes));

            var data = new float[(long)newX * newY * newZ];

            // GetVoxels espera un búfer del tamaño completo del plano. El tipo del búfer
            // varía entre versiones de la API (int[,] en ESAPI, ushort[,] en algunas
            // compilaciones de VMS.IRS), así que se prueban ambos una sola vez.
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
                        log.Info(label + ": GetVoxels", "el búfer aceptado es int[,]");
                    }
                    else if (Dyn.TryInvoke(label + ": GetVoxels(ushort[,])", () => frame.GetVoxels(plane, ushortBuffer), log))
                    {
                        useIntBuffer = false;
                        bufferKindResolved = true;
                        log.Info(label + ": GetVoxels", "el búfer aceptado es ushort[,]");
                    }
                    else
                    {
                        problem = "GetVoxels no aceptó ni int[,] ni ushort[,]; no se pueden leer los vóxeles";
                        return null;
                    }
                }
                else
                {
                    bool ok = useIntBuffer
                        ? Dyn.TryInvoke(label + ": GetVoxels plano " + plane, () => frame.GetVoxels(plane, intBuffer), log)
                        : Dyn.TryInvoke(label + ": GetVoxels plano " + plane, () => frame.GetVoxels(plane, ushortBuffer), log);

                    if (!ok)
                    {
                        problem = "fallo al leer el plano " + plane;
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
