using System;
using System.Globalization;
using System.Reflection;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Extrae de la API de Varian todo lo medible sobre un registro y devuelve un
    /// <see cref="QaMeasurements"/>.
    ///
    /// Regla que gobierna toda la clase: una métrica que no se puede medir se devuelve como
    /// no disponible con el motivo concreto. Nunca se sustituye por un valor plausible. La
    /// versión anterior derivaba DSC, HD95, jacobiano, desplazamiento máximo y suavidad de
    /// <c>GetHashCode()</c> del objeto de registro, lo que además de carecer de significado
    /// físico no era reproducible: el hash de identidad cambia entre ejecuciones, de modo
    /// que el mismo registro producía un DSC distinto cada vez que se abría el script.
    /// </summary>
    public sealed class RegistrationAnalyzer
    {
        private readonly DiagnosticLog _log;

        /// <summary>Geometría del volumen usado como rejilla de muestreo (la imagen origen).</summary>
        private ImageGeometry _fixedGeometry;

        public RegistrationAnalyzer(DiagnosticLog log)
        {
            if (log == null) throw new ArgumentNullException("log");
            _log = log;
        }

        public QaMeasurements Analyze(dynamic scriptContext)
        {
            var measurements = new QaMeasurements();

            dynamic registration;
            if (!Dyn.TryGet("contexto: registro activo", () => scriptContext.Registration, _log, out registration))
            {
                MarkAllUnavailable(measurements,
                    "no hay ningún registro activo en el espacio de trabajo de Eclipse");
                measurements.RegistrationId = "(sin registro activo)";
                return measurements;
            }

            measurements.RegistrationId = ReadRegistrationId(registration);
            ClassifyRegistration(registration, measurements);

            // --- Imágenes ---------------------------------------------------------------
            // Se cargan una sola vez y se reutilizan: tanto el respaldo de la transformación
            // como el cálculo de similitud y la extensión del FOV necesitan esta geometría.
            EsapiImageReader.LoadResult source = null;
            EsapiImageReader.LoadResult target = null;

            dynamic sourceImage, registeredImage;
            bool haveSource = Dyn.TryGet("registro: SourceImage", () => registration.SourceImage, _log, out sourceImage);
            bool haveRegistered = Dyn.TryGet("registro: RegisteredImage", () => registration.RegisteredImage, _log, out registeredImage);

            if (haveSource) source = EsapiImageReader.Load(sourceImage, "imagen origen", _log);
            if (haveRegistered) target = EsapiImageReader.Load(registeredImage, "imagen registrada", _log);

            if (source != null) measurements.FixedModality = source.Modality;
            if (target != null) measurements.MovingModality = target.Modality;

            if (source != null && source.Success) _fixedGeometry = source.Volume.Geometry;

            // --- Transformación rígida -------------------------------------------------
            ExtractRigidTransform(registration, measurements, source, target);

            // --- Similitud de intensidad ------------------------------------------------
            if (source == null || target == null || !source.Success || !target.Success)
            {
                string reason = source == null || target == null
                    ? "el registro no expone ambas imágenes (SourceImage / RegisteredImage), por lo que no hay pares de vóxeles que comparar"
                    : "no se pudieron cargar ambos volúmenes (" + (source.Problem ?? target.Problem) + ")";

                measurements.Nmi = MeasuredValue.Unavailable(reason);
                measurements.Ncc = MeasuredValue.Unavailable(reason);
                measurements.Ssd = MeasuredValue.Unavailable(reason);
            }
            else
            {
                ComputeIntensitySimilarity(registration, source, target, measurements);
            }

            // --- Deformación / topología ------------------------------------------------
            ComputeDeformationMetrics(measurements);

            // --- Estructuras ------------------------------------------------------------
            ComputeStructureMetrics(registration, measurements);

            foreach (DiagnosticEntry entry in _log.Entries)
                measurements.Diagnostics.Add(entry.ToString());

            return measurements;
        }

        // ---------------------------------------------------------------- identificación

        private string ReadRegistrationId(dynamic registration)
        {
            dynamic value;
            string source;
            if (Dyn.TryGetFirst("registro: identificador", _log, out value, out source,
                    Dyn.Alt("Id", () => registration.Id),
                    Dyn.Alt("Name", () => registration.Name)))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            return "(registro sin identificador)";
        }

        /// <summary>
        /// Determina el tipo de registro. Se prefiere una propiedad explícita de la API y
        /// sólo se recurre al nombre del tipo CLR como último recurso, porque ese nombre
        /// puede cambiar entre versiones de Eclipse sin previo aviso.
        /// </summary>
        private void ClassifyRegistration(dynamic registration, QaMeasurements measurements)
        {
            dynamic value;
            string source;

            if (Dyn.TryGetFirst("registro: tipo declarado", _log, out value, out source,
                    Dyn.Alt("RegistrationType", () => registration.RegistrationType),
                    Dyn.Alt("Type", () => registration.Type)))
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                _log.Info("registro: tipo declarado", text + " (vía " + source + ")");
                ApplyTypeText(text, measurements);
                return;
            }

            string clrTypeName = "(desconocido)";
            Dyn.TryInvoke("registro: nombre de tipo CLR",
                () => { clrTypeName = ((object)registration).GetType().Name; }, _log);

            _log.Warning("registro: tipo",
                "la API no expone el tipo de registro; se deduce del nombre de la clase CLR '" +
                clrTypeName + "', lo que es frágil ante cambios de versión");

            ApplyTypeText(clrTypeName, measurements);
        }

        private static void ApplyTypeText(string text, QaMeasurements measurements)
        {
            string upper = (text ?? string.Empty).ToUpperInvariant();

            if (upper.Contains("NONRIGID") || upper.Contains("NON-RIGID") ||
                upper.Contains("DEFORMABLE") || upper.Contains("DIR"))
            {
                measurements.RegType = RegistrationType.NonRigid;
                measurements.IsDeformable = true;
            }
            else if (upper.Contains("IDENTITY"))
            {
                measurements.RegType = RegistrationType.Identity;
            }
            else if (upper.Contains("RIGID"))
            {
                measurements.RegType = RegistrationType.Rigid;
            }
            else
            {
                measurements.RegType = RegistrationType.Unknown;
            }
        }

        // ---------------------------------------------------------------- transformación

        private void ExtractRigidTransform(
            dynamic registration,
            QaMeasurements measurements,
            EsapiImageReader.LoadResult source,
            EsapiImageReader.LoadResult target)
        {
            double[,] raw = TryReadMatrix(registration);

            if (raw != null)
            {
                RigidTransform transform;
                string note;
                if (RigidTransform.TryFromRawMatrix(raw, out transform, out note))
                {
                    measurements.Transform = transform;
                    measurements.TransformSource = "matriz de la API — " + note;
                    measurements.RigidEulerAngles = transform.GetEulerAnglesDegrees();

                    _log.Info("transformación: matriz", note);

                    if (measurements.RigidEulerAngles.Value.GimbalLock)
                    {
                        _log.Warning("transformación: ángulos de Euler",
                            "bloqueo de cardán (cabeceo ≈ ±90°): sólo la combinación de cabeceo y guiñada " +
                            "es observable; se reporta la guiñada como 0");
                    }
                    return;
                }

                _log.Failure("transformación: matriz", note);
            }

            // Respaldo: transformación relativa entre los marcos de las dos imágenes.
            //
            // Nota importante: sólo se recurre a este camino cuando la matriz no se pudo
            // leer. La versión anterior lo tomaba también cuando la traslación resultaba
            // ser (0,0,0), de modo que un registro identidad legítimo —o uno perfectamente
            // alineado— veía descartada su lectura correcta y sustituida por la diferencia
            // de orígenes de las series, que es otra magnitud.
            ImageGeometry sourceGeometry = source != null && source.Success ? source.Volume.Geometry : null;
            ImageGeometry targetGeometry = target != null && target.Success ? target.Volume.Geometry : null;

            if (sourceGeometry != null && targetGeometry != null)
            {
                RigidTransform transform = RigidTransform.FromFrames(sourceGeometry, targetGeometry);
                measurements.Transform = transform;
                measurements.TransformSource =
                    "deducida de los marcos de referencia de ambas imágenes (la API no expuso una matriz)";
                measurements.RigidEulerAngles = transform.GetEulerAnglesDegrees();

                _log.Warning("transformación",
                    "no se obtuvo matriz del registro; se usó la transformación relativa entre los marcos " +
                    "de las dos imágenes. Esto describe la diferencia de encuadre de las series, que sólo " +
                    "coincide con el registro si éste ya está aplicado a la geometría.");
                return;
            }

            measurements.TransformSource = "no disponible";
            _log.Failure("transformación",
                "ni la matriz del registro ni los marcos de las imágenes fueron accesibles");
        }

        private double[,] TryReadMatrix(dynamic registration)
        {
            dynamic matrixObject;
            string source;

            if (!Dyn.TryGetFirst("transformación: matriz", _log, out matrixObject, out source,
                    Dyn.Alt("RigidRegistration.Matrix", () => registration.RigidRegistration.Matrix),
                    Dyn.Alt("Matrix", () => registration.Matrix),
                    Dyn.Alt("TransformMatrix", () => registration.TransformMatrix),
                    Dyn.Alt("RigidTransformMatrix", () => registration.RigidTransformMatrix)))
            {
                return null;
            }

            var raw = new double[4, 4];

            bool read = Dyn.TryInvoke("transformación: lectura de celdas de la matriz (" + source + ")", () =>
            {
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        raw[r, c] = Convert.ToDouble(matrixObject[r, c], CultureInfo.InvariantCulture);
                    }
                }
            }, _log);

            if (!read) return null;

            // Una matriz toda a cero indica que la indexación [r,c] no es la esperada.
            bool allZero = true;
            for (int r = 0; r < 4 && allZero; r++)
                for (int c = 0; c < 4 && allZero; c++)
                    if (Math.Abs(raw[r, c]) > 1e-12) allZero = false;

            if (allZero)
            {
                _log.Failure("transformación: matriz",
                    "la matriz leída vía " + source + " es idénticamente nula; la indexación [fila,columna] " +
                    "no corresponde con la esperada");
                return null;
            }

            return raw;
        }

        // ---------------------------------------------------------------- intensidad

        private void ComputeIntensitySimilarity(
            dynamic registration,
            EsapiImageReader.LoadResult source,
            EsapiImageReader.LoadResult target,
            QaMeasurements measurements)
        {
            IPointMapper mapper = BuildPointMapper(registration, measurements);
            if (mapper == null)
            {
                string reason = measurements.IsDeformable
                    ? "es un registro deformable y la API no expone ni el campo de vectores de deformación " +
                      "ni un método de mapeo punto a punto. Evaluar la similitud aplicando sólo la componente " +
                      "lineal describiría una transformación distinta de la que se está auditando."
                    : "no se dispone de una transformación válida con la que emparejar los vóxeles";

                measurements.Nmi = MeasuredValue.Unavailable(reason);
                measurements.Ncc = MeasuredValue.Unavailable(reason);
                measurements.Ssd = MeasuredValue.Unavailable(reason);
                return;
            }

            _log.Info("similitud: mapeo", mapper.Description);

            VoxelPairSet pairs = VoxelPairSampler.Pair(source.Volume, target.Volume, mapper);
            measurements.SampleCount = pairs.Count;
            measurements.OverlapFraction = pairs.OverlapFraction;
            measurements.EffectiveSamplingMm = source.Volume.Geometry.CoarsestResolution;

            if (!string.IsNullOrEmpty(pairs.Problem))
            {
                measurements.Nmi = MeasuredValue.Unavailable(pairs.Problem);
                measurements.Ncc = MeasuredValue.Unavailable(pairs.Problem);
                measurements.Ssd = MeasuredValue.Unavailable(pairs.Problem);
                _log.Failure("similitud: emparejamiento", pairs.Problem);
                return;
            }

            SimilarityResult similarity = SimilarityCalculator.Compute(
                pairs.FixedValues, pairs.MovingValues, pairs.Count);

            if (!string.IsNullOrEmpty(similarity.Problem))
            {
                measurements.Nmi = MeasuredValue.Unavailable(similarity.Problem);
                measurements.Ncc = MeasuredValue.Unavailable(similarity.Problem);
                measurements.Ssd = MeasuredValue.Unavailable(similarity.Problem);
                _log.Failure("similitud: cálculo", similarity.Problem);
                return;
            }

            string samplingNote = string.Format(CultureInfo.InvariantCulture,
                "{0:N0} pares de vóxeles, solapamiento {1:P1}, muestreo efectivo {2:F1} mm",
                pairs.Count, pairs.OverlapFraction, measurements.EffectiveSamplingMm);

            measurements.Ncc = similarity.Ncc.HasValue
                ? MeasuredValue.Measured(similarity.Ncc.Value, samplingNote)
                : MeasuredValue.Unavailable("no se pudo calcular la correlación");

            measurements.Ssd = similarity.Ssd.HasValue
                ? MeasuredValue.Measured(similarity.Ssd.Value, samplingNote)
                : MeasuredValue.Unavailable("rango de intensidad nulo en la imagen de referencia");

            measurements.Nmi = similarity.Nmi.HasValue
                ? MeasuredValue.Measured(similarity.Nmi.Value,
                    samplingNote + ", histograma conjunto de " + similarity.HistogramBins + " bins")
                : MeasuredValue.Unavailable("no se pudo construir el histograma conjunto");

            _log.Info("similitud: resultado", samplingNote);
        }

        /// <summary>
        /// Construye el mapeo de puntos. Para un registro deformable sólo es válido un mapeo
        /// que atraviese el campo de deformación; si la API no lo ofrece, se devuelve null y
        /// las métricas de intensidad quedan como no disponibles, en vez de calcularse con
        /// la componente lineal y presentarse como si describieran el registro deformable.
        /// </summary>
        private IPointMapper BuildPointMapper(dynamic registration, QaMeasurements measurements)
        {
            DynamicPointMapper deformableMapper = TryBuildDeformableMapper(registration);
            if (deformableMapper != null) return deformableMapper;

            if (measurements.IsDeformable) return null;

            if (measurements.Transform == null) return null;

            return new RigidPointMapper(
                measurements.Transform,
                "matriz rígida — " + measurements.TransformSource);
        }

        /// <summary>
        /// Busca por reflexión un método de mapeo punto a punto en el objeto de registro.
        /// Se sonda con un punto real y sólo se acepta si devuelve algo finito: la mera
        /// existencia del método no garantiza que esté implementado en esta versión.
        /// </summary>
        private DynamicPointMapper TryBuildDeformableMapper(dynamic registration)
        {
            object registrationObject = registration;
            if (registrationObject == null) return null;

            Type registrationType = registrationObject.GetType();
            string[] candidateNames = { "TransformPoint", "MapPoint", "TransformPointToRegistered", "DeformPoint" };

            foreach (string name in candidateNames)
            {
                MethodInfo method = registrationType.GetMethod(
                    name, BindingFlags.Public | BindingFlags.Instance);

                if (method == null) continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1) continue;

                Type vectorType = parameters[0].ParameterType;
                Func<Vec3, object> toVector = BuildVectorFactory(vectorType);
                Func<object, Vec3?> fromVector = BuildVectorReader(method.ReturnType);

                if (toVector == null || fromVector == null)
                {
                    _log.Warning("mapeo deformable: " + name,
                        "se encontró el método pero no se pudo construir/leer el tipo de vector " +
                        vectorType.Name);
                    continue;
                }

                Func<Vec3, Vec3?> map = point =>
                {
                    object result = method.Invoke(registrationObject, new[] { toVector(point) });
                    return fromVector(result);
                };

                // Sonda: si no devuelve un punto finito, el método no es utilizable.
                try
                {
                    Vec3? probe = map(new Vec3(0, 0, 0));
                    if (!probe.HasValue || !probe.Value.IsFinite)
                    {
                        _log.Warning("mapeo deformable: " + name, "la sonda no devolvió un punto finito");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("mapeo deformable: " + name, "la sonda falló — " + DiagnosticLog.Describe(ex));
                    continue;
                }

                _log.Info("mapeo deformable",
                    "se usará " + registrationType.Name + "." + name + " para mapear puntos a través del registro");

                return new DynamicPointMapper(map,
                    "mapeo punto a punto vía " + name + " (atraviesa el campo de deformación)");
            }

            return null;
        }

        private static Func<Vec3, object> BuildVectorFactory(Type vectorType)
        {
            FieldInfo fx = vectorType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fy = vectorType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fz = vectorType.GetField("z", BindingFlags.Public | BindingFlags.Instance);

            if (fx != null && fy != null && fz != null)
            {
                return point =>
                {
                    object boxed = Activator.CreateInstance(vectorType);
                    fx.SetValue(boxed, point.X);
                    fy.SetValue(boxed, point.Y);
                    fz.SetValue(boxed, point.Z);
                    return boxed;
                };
            }

            ConstructorInfo constructor = vectorType.GetConstructor(
                new[] { typeof(double), typeof(double), typeof(double) });

            if (constructor != null)
                return point => constructor.Invoke(new object[] { point.X, point.Y, point.Z });

            return null;
        }

        private static Func<object, Vec3?> BuildVectorReader(Type vectorType)
        {
            if (vectorType == null || vectorType == typeof(void)) return null;

            FieldInfo fx = vectorType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fy = vectorType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo fz = vectorType.GetField("z", BindingFlags.Public | BindingFlags.Instance);

            if (fx == null || fy == null || fz == null) return null;

            return value =>
            {
                if (value == null) return null;
                return new Vec3(
                    Convert.ToDouble(fx.GetValue(value), CultureInfo.InvariantCulture),
                    Convert.ToDouble(fy.GetValue(value), CultureInfo.InvariantCulture),
                    Convert.ToDouble(fz.GetValue(value), CultureInfo.InvariantCulture));
            };
        }

        // ---------------------------------------------------------------- deformación

        /// <summary>
        /// Métricas topológicas.
        ///
        /// Para una transformación rígida el jacobiano vale 1 en todo punto y la suavidad es
        /// perfecta: no son estimaciones sino consecuencias de la definición, y se marcan
        /// como tales. El desplazamiento máximo sí se calcula, de forma exacta, sobre los
        /// vértices del volumen.
        ///
        /// Para un registro deformable estas tres magnitudes exigen recorrer el campo de
        /// vectores, que la API de scripting no expone. Se devuelven como no disponibles.
        /// </summary>
        private void ComputeDeformationMetrics(QaMeasurements measurements)
        {
            if (measurements.IsDeformable)
            {
                const string reason =
                    "requiere recorrer el campo de vectores de deformación (DVF), que la API de scripting " +
                    "de Varian no expone. No es derivable de la matriz lineal ni de las imágenes.";

                measurements.JacobianNegativePercent = MeasuredValue.Unavailable(reason);
                measurements.Smoothness = MeasuredValue.Unavailable(reason);
                measurements.MaxDisplacement = MeasuredValue.Unavailable(reason);
                return;
            }

            const string analyticNote = "exacto por definición para una transformación rígida, no estimado";

            measurements.JacobianNegativePercent = MeasuredValue.Measured(0.0,
                "|J| = 1 en todo punto de una transformación rígida: no hay plegamiento posible. " + analyticNote);

            measurements.Smoothness = MeasuredValue.Measured(1.0,
                "el gradiente del campo de desplazamiento es constante. " + analyticNote);

            if (measurements.Transform == null)
            {
                measurements.MaxDisplacement = MeasuredValue.Unavailable(
                    "no se dispone de la transformación con la que evaluar el desplazamiento");
                return;
            }

            ImageGeometry geometry = _fixedGeometry;
            if (geometry == null)
            {
                // Sin extensión del volumen, el desplazamiento máximo no está definido:
                // depende del tamaño de la región evaluada, no sólo de la transformación.
                measurements.MaxDisplacement = MeasuredValue.Unavailable(
                    "no se pudo determinar la extensión del volumen sobre la que evaluar el desplazamiento");
                return;
            }

            double maxDisplacement = measurements.Transform.MaxDisplacementOver(geometry);
            measurements.MaxDisplacement = MeasuredValue.Measured(maxDisplacement,
                "máximo sobre los ocho vértices del FOV; exacto porque el desplazamiento de una " +
                "aplicación afín es convexo y alcanza su máximo en un vértice");
        }

        // ---------------------------------------------------------------- estructuras

        /// <summary>
        /// DSC y HD95 exigen un par emparejado: un contorno de referencia y ese mismo
        /// contorno propagado por el registro, identificables entre sí.
        ///
        /// La versión anterior recorría <c>StructureSets[0]</c>, sumaba volúmenes y hashes de
        /// los identificadores y convertía esa suma en un DSC mediante aritmética modular.
        /// El resultado no tenía relación con solapamiento alguno.
        /// </summary>
        private void ComputeStructureMetrics(dynamic registration, QaMeasurements measurements)
        {
            int sourceStructures = CountStructures(() => registration.SourceImage.Image.StructureSets, "imagen origen");
            int targetStructures = CountStructures(() => registration.RegisteredImage.Image.StructureSets, "imagen registrada");

            string reason;

            if (sourceStructures == 0 || targetStructures == 0)
            {
                reason = "no hay contornos en ambas series (origen: " + sourceStructures +
                         ", registrada: " + targetStructures + "). El DSC y la HD95 comparan un contorno " +
                         "de referencia con ese mismo contorno propagado; con una sola serie contorneada " +
                         "no existe el par a comparar.";
            }
            else
            {
                reason = "hay contornos en ambas series (origen: " + sourceStructures +
                         ", registrada: " + targetStructures + "), pero el cálculo de DSC y HD95 requiere " +
                         "rasterizar ambos contornos sobre una rejilla común y emparejarlos por " +
                         "identificador. No está implementado en esta versión.";
            }

            measurements.Dsc = MeasuredValue.Unavailable(reason);
            measurements.Hd95 = MeasuredValue.Unavailable(reason);

            _log.Warning("estructuras", reason);
        }

        private int CountStructures(Func<object> structureSetsAccessor, string label)
        {
            dynamic structureSets;
            if (!Dyn.TryGet("estructuras: StructureSets de " + label, structureSetsAccessor, _log, out structureSets))
                return 0;

            int count = 0;
            Dyn.TryInvoke("estructuras: recuento en " + label, () =>
            {
                foreach (dynamic structureSet in structureSets)
                {
                    foreach (dynamic structure in structureSet.Structures)
                    {
                        if (structure != null) count++;
                    }
                }
            }, _log);

            return count;
        }

        // ---------------------------------------------------------------- utilidades

        private static void MarkAllUnavailable(QaMeasurements measurements, string reason)
        {
            measurements.Nmi = MeasuredValue.Unavailable(reason);
            measurements.Ncc = MeasuredValue.Unavailable(reason);
            measurements.Ssd = MeasuredValue.Unavailable(reason);
            measurements.JacobianNegativePercent = MeasuredValue.Unavailable(reason);
            measurements.MaxDisplacement = MeasuredValue.Unavailable(reason);
            measurements.Smoothness = MeasuredValue.Unavailable(reason);
            measurements.Dsc = MeasuredValue.Unavailable(reason);
            measurements.Hd95 = MeasuredValue.Unavailable(reason);
        }
    }
}
