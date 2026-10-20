# Traspaso: auditoría cuantitativa de co-registros (ESAPI 18.1 → ESAPI v11)

**Repositorio origen:** `davidpadron76/ESAPI_RegistrationQA`
**Commit descrito:** `324d78f3d809` (tag `v3.0.1`, `AssemblyInformationalVersion = 3.0.1`)
**Destino del port:** Eclipse 11 / ESAPI v11, plugin binario `.esapi.dll`, .NET Framework 4.0, C# 5
**Origen:** .NET Framework 4.8, C# 7.3, WPF, `VMS.CA.Scripting` + `VMS.IRS.Scripting`

Este documento describe **lo que el código hace hoy**. Donde algo me parece un error, no lo corregí
y no lo escondí: está en la sección H, aparte. Donde no pude verificar algo, lo digo.

Las tres fuentes de verdad para el port, en orden:

1. El código del repositorio.
2. `TEST_VECTORS.json`, cuyos valores esperados salieron de **ejecutar** ese código.
3. Este documento.

Si los tres se contradicen, gana el código.

---

## A. Propósito y alcance

### Qué es

Un plugin de Eclipse que audita **un** registro de imagen contra las recomendaciones del
**AAPM TG-132** (Brock KK et al., *Med Phys.* 2017;44(7):e43–e76), calcula las métricas de su
Tabla III y emite un veredicto de semáforo más un informe HTML firmable.

### Es de solo lectura — confirmado

Revisé todas las rutas de acceso a la API. **No hay una sola escritura al modelo de datos de
Eclipse.** Concretamente:

- No se invoca `Patient.BeginModifications()`.
- No existe ninguna llamada a `AddStructure`, `SegmentVolume`, `CreateRegistration`, `Save`,
  setters sobre objetos ESAPI, ni nada equivalente.
- Todo el acceso a la API pasa por `Services/Dyn.cs`, que solo lee propiedades e invoca métodos
  de consulta (`GetVoxels`, `GetContoursOnImagePlane`, `TransformPoint`, `GetVectors`).

Las **únicas** escrituras del programa son al sistema de archivos, y siempre a una ruta que el
usuario elige en un `SaveFileDialog`: el informe HTML y el CSV acumulativo.

### Tipos de registro que cubre

| | Soportado | Cómo |
|---|---|---|
| Rígido | Sí | Matriz 4×4 de la API, o el método `TransformPoint` de la API |
| Deformable (DIR) | Sí | Campo vectorial de deformación + mapeo punto a punto |
| Identidad | Sí, detectado y avisado | `RegistrationType.Identity` o matriz identidad exacta |
| Desconocido | Se trata como rígido | Si el nombre de clase no contiene ninguna palabra clave |

La clasificación está en `RegistrationAnalyzer.ApplyTypeText` (línea 236). Busca subcadenas en
mayúsculas sobre el texto del tipo o el nombre de la clase CLR:

- `NONRIGID`, `NON-RIGID`, `DEFORMABLE`, `DIR` → `NonRigid` y `IsDeformable = true`
- `IDENTITY` → `Identity`
- `RIGID` → `Rigid`
- cualquier otra cosa → `Unknown`

**El orden importa:** `MIRSNonRigidRegistration` contiene tanto `NONRIGID` como `RIGID`, y la
rama de no-rígido se evalúa primero, así que clasifica bien.

### Modalidades

No hay restricción. `FixedModality` y `MovingModality` se leen de la serie y se usan únicamente
para elegir el texto del aviso de modalidad (NCC para monomodal, NMI para multimodal) y para la
columna del CSV. **Ninguna métrica cambia de umbral según la modalidad.** Probado en CT–CT y
CT–MR; nada más.

### Qué NO hace

- No crea, modifica ni aprueba registros.
- No propaga estructuras ni acumula dosis.
- No evalúa más de un registro por ejecución: audita `ScriptContext.Registration`, el registro
  activo, y nada más. El resto del espacio de trabajo solo se recorre para localizar el registro
  inverso.
- No lee el plan de tratamiento. **`ScriptContext.PlanSetup` no se usa en ninguna parte.** Lo
  verifiqué con `grep`: la única entrada que el script toma del contexto es `Patient.Id`,
  `Registration` y, para la consistencia inversa, la colección de registros del paciente.
- No decide clínicamente. El veredicto es una clasificación contra tolerancias, no una
  liberación.

---

## B. Mapa del código

### Flujo desde `Execute`

```
VMS.IRS.Scripting.Script.Execute(ScriptContext)        ESAPI_RegistrationQA.cs
  │  valida context != null y context.Patient != null  (MessageBox y return si falla)
  ├─ new MainViewModel(context)                        ViewModels/MainViewModel.cs
  │     NO mide en el constructor: solo prepara colecciones y pone IsMeasuring = true
  ├─ new MainControl(viewModel)                        UI/MainControl.xaml(.cs)
  └─ new Window{...}.ShowDialog()                      modal, 1020×700, mínimo 820×560
        │
        └─ MainControl.Loaded  ──►  viewModel.StartMeasurement()      UI/MainControl.xaml.cs
              └─ Dispatcher.BeginInvoke(RunMeasurement, Background)
                    │   (deferido para que la ventana pinte antes de bloquear el hilo)
                    └─ MainViewModel.RunMeasurement()
                          ├─ ReadPatientId()
                          ├─ new RegistrationAnalyzer(log, this).Analyze(context)   ← todo el trabajo
                          ├─ copia _log.Entries a Diagnostics
                          └─ ReevaluateThresholds()
```

`RunMeasurement` corre **en el hilo de UI de Eclipse**, deliberadamente. El comentario en
`StartMeasurement` (línea 296) explica por qué: no se asume que la API de Varian sea segura desde
otro hilo. El progreso se refresca con un truco: `Report()` invoca un delegado vacío a
`DispatcherPriority.Render`, lo que fuerza el repintado sin bombear entrada.

### `RegistrationAnalyzer.Analyze` — orden exacto de etapas

| # | Etapa (`MeasurementStage`) | Qué hace |
|---|---|---|
| 0 | `Starting` | — |
| 1 | `IdentifyingRegistration` | `ReadRegistrationId`, `ClassifyRegistration`, estado de aprobación |
| 2 | `LoadingSourceVolume` | `EsapiImageReader.Load(SourceImage)` |
| 3 | `LoadingRegisteredVolume` | `EsapiImageReader.Load(RegisteredImage)` |
| — | — | `ReadFrameOfReference` (ambas series) |
| 4 | `ReadingTransform` | `ExtractRigidTransform` → `MatrixReader` → `RigidTransform.TryFromRawMatrix` |
| 5 | `BuildingPointMapping` | `BuildPointMapper` → `PointMapperReader`, o matriz rígida de reserva |
| 6 | `IntensitySimilarity` | `VoxelPairSampler` + `SimilarityCalculator` |
| — | — | `RecordNativeVoxelSize` (de aquí sale la tolerancia espacial) |
| 7 | `StructureMetrics` | `ComputeStructureMetrics` → DSC / MDA / HD95 |
| 8 | `DeformationField` | Jacobiano, gradiente DVF, desplazamiento máximo |
| 9 | `TargetRegistrationError` | TRE media y máxima sobre marcadores |
| 10 | `InverseConsistency` | Busca el registro inverso y compone ida y vuelta |
| — | `Finished` | — |

**Dependencias de orden que el port debe respetar:**

- `ComputeStructureMetrics` va **antes** que la deformación y la consistencia inversa, porque
  fija `_structureGeometry`, la región de reserva cuando no hay volumen cargado.
- `RecordNativeVoxelSize` va antes de cualquier clasificación: sin él, cuatro de las cinco
  tolerancias de la Tabla III no existen.
- `_mapper` se construye una vez y lo comparten similitud, estructuras, TRE y consistencia.

### Archivos y responsabilidades

**`Models/`** — sin dependencia de Varian, compila en cualquier parte.

| Archivo | Clase | Papel |
|---|---|---|
| `Vec3.cs` | `Vec3` | Vector 3D inmutable: `+ - *`, `Dot`, `Cross`, `Length`, `IsFinite` |
| `ImageGeometry.cs` | `ImageGeometry` | Origen, tres cosenos directores, resoluciones, tamaños. `VoxelToPatient`, `PatientToVoxel`, `BoundingBoxCorners`, `CoarsestResolution`, `Subsampled` |
| `RigidTransform.cs` | `RigidTransform`, `EulerAngles` | Matriz 4×4 homogénea, detección de convención, Euler, inversa, composición, desplazamiento máximo |
| `MetricCatalog.cs` | `MetricKeys`, `MetricDefinition`, `MetricCatalog` | Claves canónicas y definición de cada métrica, incluido si gradúa |
| `MetricResult.cs` | `QASemaphore`, `MetricResult` | Resultado evaluado: valor + color + criterio |
| `QaMeasurements.cs` | `MeasurementState`, `MeasuredValue`, `QaMeasurements` | Las medidas crudas y su ausencia justificada |
| `ThresholdProfile.cs` | `ThresholdLimits`, `ThresholdProfile` | Umbrales y su resolución contra el caso |
| `RegistrationContext.cs` | `RegistrationType`, `ImageModality`, `AnatomicRegion` | Enumeraciones |
| `RigidTransformItem.cs` | `RigidTransformItem` | Fila de la tabla de transformación |

**`Services/`** — aquí vive todo el contacto con la API.

| Archivo | Papel | Toca la API |
|---|---|---|
| `Dyn.cs` | Envoltorio de acceso dinámico con captura de excepción y registro | Sí (el único camino) |
| `MatrixReader.cs` | Localiza la matriz 4×4: 13 rutas, 7 formas de contenedor, barrido por reflexión | Sí |
| `PointMapperReader.cs` | Localiza un método de mapeo punto a punto: 10 nombres × 9 portadores | Sí |
| `EsapiImageReader.cs` | Carga un volumen submuestreado, resuelve la rampa HU | Sí |
| `VoxelPlaneReader.cs` | Resuelve qué combinación portador/búfer de `GetVoxels` funciona | Sí |
| `StructureRasterizer.cs` | Lee contornos y los rasteriza a máscara booleana | Sí |
| `LandmarkExtractor.cs` | Lee estructuras de tipo MARKER / ISOCENTER | Sí |
| `DeformationFieldReader.cs` | Lee el campo vectorial de deformación | Sí |
| `SampledVolume.cs` | Volumen en memoria + su geometría | No |
| `VoxelPairSampler.cs` | Empareja vóxeles entre volúmenes a través del mapeo | No |
| `SimilarityCalculator.cs` | NCC, SSD, NMI | No |
| `SurfaceMetrics.cs` | DSC, MDA, HD95 | No |
| `DistanceTransform.cs` | Transformada de distancia euclídea exacta (Felzenszwalb) | No |
| `DeformationFieldMetrics.cs` | Jacobiano, divergencia, rotacional, gradiente | No |
| `PointMapper.cs` | `IPointMapper`, `RigidPointMapper`, `DynamicPointMapper` | No |
| `MetricEvaluator.cs` | Medida + perfil → `MetricResult` | No |
| `AdvisoryEngine.cs` | Métricas → avisos + veredicto global | No |
| `HtmlReportBuilder.cs` | Informe HTML | No |
| `CsvDatasetExporter.cs` | Fila CSV acumulativa | No |
| `DiagnosticLog.cs` | Registro de diagnóstico con niveles | No |
| `MeasurementProgress.cs` | Etapas de progreso | No |

**`ViewModels/`, `UI/`** — WPF. `MainViewModel` (608 líneas), `RelayCommand`, `MainControl.xaml`
(371 líneas), `DetailWindow` (ventana genérica de tabla para avisos y diagnóstico).

---

## C. Datos de entrada

### De dónde sale cada dato

| Dato | Ruta | Archivo:línea | Si falla |
|---|---|---|---|
| Identificador de paciente | `context.Patient.Id` | `MainViewModel.cs:495` | `"(unidentified patient)"` |
| Registro a auditar | `context.Registration` | `RegistrationAnalyzer.cs:63` | Todo N/A, se aborta |
| Id del registro | `.Id` → `.Name` | `:161-162` | `"(unidentified registration)"` |
| Tipo | `.RegistrationType` → `.Type` → nombre de clase CLR | `:188-189, 199` | `Unknown` |
| Estado de aprobación | `.Status` → `.ApprovalStatus` | `:225-226` | Se omite, no es error |
| Imagen origen | `registration.SourceImage` | `:83` | Sin métricas de intensidad ni estructura |
| Imagen registrada | `registration.RegisteredImage` | `:84` | Igual |
| Frame of Reference | `Image.FOR` → `FOR` → `Image.FrameOfReferenceUid` → `FrameOfReferenceUid` → `Series.FOR` | `:305-309` | Desplazamiento máximo sin graduar |
| Modalidad | `Image.Series.Modality` → `Series.Modality` → `Modality` → `Frame.Modality` | `EsapiImageReader.cs:288-291` | `Unknown` |
| Geometría | `Frame.XSize/YSize/ZSize`, `XRes/YRes/ZRes`, `Origin`, `XDirection/YDirection/ZDirection` | `EsapiImageReader.cs:56-58` y siguientes | No carga el volumen |
| Vóxeles | `GetVoxels(int, buffer)` sobre `Frame`/`Image`/objeto directo | `VoxelPlaneReader.cs` | No carga el volumen |
| Matriz | 13 rutas, ver `MatrixReader.cs:97-109` | — | Sin transformación: casi todo N/A |
| Mapeo de puntos | 10 nombres de método × 9 portadores | `PointMapperReader.cs:43-74` | Matriz rígida de reserva, o nada si es deformable |
| Estructuras | `Image.StructureSets` → `StructureSets`, luego `structureSet.Structures` | `StructureRasterizer.cs:412-413` | Sin DSC/MDA/HD95 |
| Contornos | `structure.GetContoursOnImagePlane(i)` para `i` en `[0, ZSize)` | `StructureRasterizer.cs:~500` | Estructura vacía |
| Marcadores | mismas colecciones, filtradas por `DicomType`/`StructureType` ∈ {MARKER, ISOCENTER} | `LandmarkExtractor.cs:31, 42-43` | Sin TRE |
| Posición del marcador | `CenterPoint` → `Position` → `Point` → `Points[0]` | `LandmarkExtractor.cs:131-134` | Ese marcador se descarta |
| Campo de deformación | `<portador>.DeformationField` con `XSize…ZRes` y `GetVectors(buffer)` | `DeformationFieldReader.cs:112-119` | Métricas de deformación N/A |
| Registros del espacio de trabajo | `Patient.Registrations` → `Registrations` → `Patient.MIRSRegistrations` | `RegistrationAnalyzer.cs:2224-2226` | Sin consistencia inversa |

### Cómo elige el registro

**No elige.** Audita `ScriptContext.Registration`, que es el registro que el usuario tiene activo
en Eclipse. No hay interfaz de selección, no hay heurística, no hay lista.

- **Si hay varios registros abiertos:** se audita el activo y ya. Los demás solo se recorren
  dentro de `FindReverseRegistration`.
- **Si no hay ninguno:** `Dyn.TryGet` sobre `scriptContext.Registration` falla,
  `MarkAllUnavailable` marca todo como no disponible con el motivo *"there is no active
  registration in the Eclipse workspace"*, el identificador pasa a `"(no active registration)"`
  y se devuelve de inmediato. La ventana sale con veredicto NO EVIDENCE.

### Cómo elige el registro inverso

`FindReverseRegistration` (`:2217`) recorre la colección de registros y acepta el primer candidato
que cumple **las tres** condiciones:

1. No es el mismo objeto que el activo (`ReferenceEquals`).
2. `candidate.SourceImage.Id` es igual, **ignorando mayúsculas**, al `RegisteredImage.Id` del activo.
3. `candidate.RegisteredImage.Id` es igual, ignorando mayúsculas, al `SourceImage.Id` del activo.

El emparejamiento es **por identificador de imagen**, no por UID ni por frame of reference. Si hay
más de un candidato válido se queda con el primero que encuentre y el orden lo decide la API.

### Submuestreo de los volúmenes

`EsapiImageReader.MaxSamplesPerAxis = 128`. Cada eje se recorre con paso
`ceil(tamaño / 128)`, así que ningún volumen cargado supera 128³ ≈ 2.1 M vóxeles. La resolución
efectiva resultante se reporta y entra en el CSV.

**Importante:** `NativeVoxelSizeMm` se toma de la geometría **nativa**, antes de submuestrear
(`EsapiImageReader.cs:82`), porque es la que TG-132 usa como tolerancia. La resolución efectiva es
otra cosa y solo documenta la calidad del muestreo.

---

## D. Métricas y fórmulas

### Convenciones comunes

**Sistema de coordenadas.** Todo se calcula en **coordenadas de paciente en milímetros**, tal
como las entrega la API de Varian. El código las etiqueta según la convención DICOM del paciente:

```
X = izquierda-derecha        (LR)
Y = anterior-posterior       (AP)
Z = cráneo-caudal            (CC)
```

Esas etiquetas están en `MetricEvaluator.BuildRigidTransformRows` (`:229-231`) y en el informe.
**Verificado contra Eclipse** el 2026-08-01: `Properties → Tech (Reg) → Rigid Registration` mostró
X −4.8 / Y 10.3 / Z −122.5 mm donde el plugin reportó −4.84 / 10.28 / −122.47. Los tres ejes
coinciden en orden y signo.

El código **no** verifica que el origen y los cosenos directores sean LPS. Toma lo que la API da.
Si ESAPI v11 entrega otra convención, nada lo detectaría.

**Dirección de la matriz.** Convención interna: `P' = M · P` con `P` columna, traslación en la
**última columna** `m[0..2, 3]`, última fila `(0,0,0,1)`.

`RigidTransform.TryFromRawMatrix` (`:245`) detecta cuál de las dos convenciones entregó la API
mirando **dónde está la parte homogénea**, con tolerancia 1e-6:

| Última fila homogénea | Última columna homogénea | Acción | Nota emitida |
|---|---|---|---|
| sí | no | se usa tal cual | `translation in the last column (P' = M·P)` |
| no | sí | **se transpone** | `translation in the last row (P' = P·M); matrix transposed to the internal convention` |
| sí | sí | se usa tal cual | `zero translation; assuming the P' = M·P convention (pure rotation, sense not verifiable)` |
| no | no | **se rechaza** | `could not determine the matrix convention…` |

Después se exige que la submatriz 3×3 sea ortonormal con tolerancia **1e-3** (columnas unitarias,
mutuamente ortogonales, determinante +1). Si no lo es, se rechaza con el determinante en el texto.

**Qué FOR es origen y cuál destino.** El mapeo va **source → registered**. `SourceImage` es el
origen, `RegisteredImage` el destino. En `ComputeStructureMetrics` la estructura del `SourceImage`
se rasteriza **sin** mapeo y la del `RegisteredImage` **con** mapeo (`:1480-1481`), lo que implica
que `_mapper` lleva de las coordenadas del registrado a las del origen, o que ambos comparten
frame de paciente. **El código no puede verificar la dirección** y lo dice explícitamente en el
diagnóstico de `PointMapperReader`: *"The direction of this method is not verifiable from the
API"*.

**Orden de composición.** `RigidTransform.ComposeAfter(first)` devuelve `this ∘ first`, es decir
`resultado(p) = this.Apply(first.Apply(p))`. Se usa conceptualmente en la consistencia inversa,
aunque el cálculo real se hace punto a punto con los dos `IPointMapper`, no componiendo matrices.

**Cultura.** Todo el formato numérico usa `CultureInfo.InvariantCulture`. Es deliberado: el
separador decimal no debe depender de la configuración regional de la estación.

---

### D.1 NCC — correlación cruzada normalizada

**Archivo:** `SimilarityCalculator.cs:119`
**Unidad:** adimensional, rango teórico [−1, 1]
**Gradúa:** no

Fórmula implementada, en una sola pasada sobre `n` pares:

```
cov   = n·Σ(f·m) − Σf·Σm
varF  = n·Σf²    − (Σf)²
varM  = n·Σm²    − (Σm)²
NCC   = cov / sqrt(varF · varM)
```

Si `varF` o `varM` no son estrictamente positivos se aborta. La comprobación está escrita como
`!(v > 0)` y no como `v <= 0` **a propósito**, para que un NaN también la active: toda comparación
contra NaN es falsa, así que la forma natural dejaría pasar el valor envenenado.

**Dónde se evalúa:** sobre los pares de vóxeles que `VoxelPairSampler` consiguió emparejar, no
sobre toda la imagen.

---

### D.2 SSD — diferencia cuadrática media normalizada

**Archivo:** `SimilarityCalculator.cs:128`
**Unidad:** adimensional
**Gradúa:** no

```
SSD = (Σ(f − m)² / n) / (p99(f) − p1(f))²
```

El denominador es el **rango robusto** de la imagen fija: percentil 99 menos percentil 1, no
min-max. Si ese rango es 0, SSD queda no disponible.

Los percentiles se calculan ordenando una copia del vector y tomando
`copy[(int)(0.01·(n−1))]` y `copy[(int)(0.99·(n−1))]` — **índice truncado, sin interpolación**
(`:247-248`).

---

### D.3 NMI — información mutua normalizada (Studholme)

**Archivo:** `SimilarityCalculator.cs:154`
**Unidad:** adimensional, 1 = independientes, 2 = idénticas
**Gradúa:** no

```
NMI = (H(A) + H(B)) / H(A,B)
```

con entropías en **base 2** (`Math.Log(p, 2.0)`).

Histograma conjunto sobre el rango robusto de cada imagen. Número de bins:

```
bins = floor( sqrt( n / 20 ) ),  acotado a [16, 64]
```

El 20 es `MinSamplesPerJointCell`: ocupación media mínima por celda. La razón está documentada en
`:43-50` — la entropía conjunta se sesga a la baja cuando muchas celdas quedan vacías, lo que
inflaría el NMI artificialmente.

Asignación de bin: `index = (int)((valor − min) · bins / (max − min))`, recortado a `[0, bins−1]`.
Si la entropía conjunta ≤ 1e-12 devuelve null.

**Mínimo de muestras para las tres métricas de intensidad:** `MinimumSamples = 1000`. Por debajo,
las tres quedan no disponibles con el conteo exacto en el mensaje.

---

### D.4 Desplazamiento máximo

**Archivo rígido:** `RigidTransform.MaxDisplacementOver` (`:224`)
**Archivo deformable:** máximo de `‖u‖` sobre todos los puntos del campo
**Unidad:** mm
**Gradúa:** no (no está en TG-132)

Caso rígido: se evalúan **las ocho esquinas** de la caja de la geometría y se toma el máximo de
`‖M·p − p‖`. Es exacto, no una estimación: el desplazamiento de una aplicación afín es convexo y
alcanza su máximo en un vértice.

La región es `_fixedGeometry ?? _structureGeometry` — la imagen origen si cargó, el contorno si no.
**El valor depende del tamaño de la región**, lo que el texto del criterio dice.

Caso deformable: máximo sobre **todos** los puntos de la rejilla del campo, no solo las esquinas.

---

### D.5 Jacobiano negativo (%)

**Archivo:** `DeformationFieldMetrics.cs:263-281`
**Unidad:** % de puntos
**Gradúa:** **sí**, y es el único con tolerancia literal de la Tabla III

Derivadas por **diferencias centradas, solo en el interior** de la rejilla del campo (se excluye
la capa exterior de cada eje, porque una diferencia centrada necesita vecino a ambos lados):

```
∂u/∂x = (u[x+1,y,z] − u[x−1,y,z]) / (2·XRes)
∂u/∂y = (u[x,y+1,z] − u[x,y−1,z]) / (2·YRes)
∂u/∂z = (u[x,y,z+1] − u[x,y,z−1]) / (2·ZRes)
```

El determinante es de **`I + ∇u`**, no de `∇u`:

```
        | 1 + ∂ux/∂x     ∂ux/∂y       ∂ux/∂z   |
J = det |   ∂uy/∂x     1 + ∂uy/∂y     ∂uy/∂z   |
        |   ∂uz/∂x       ∂uz/∂y     1 + ∂uz/∂z |
```

Se cuenta el porcentaje de puntos evaluados con `J ≤ 0`. Puntos con `J` no finito se saltan y
**no cuentan en el denominador**.

**Región de evaluación:** desde la versión 3.0.0, **dentro del contorno externo del paciente**
cuando se puede colocar uno sobre la rejilla del campo; todo el campo si no. La selección está en
`RegistrationAnalyzer.SelectJacobianDomain`. El contorno externo se identifica por tipo DICOM
`EXTERNAL` o por nombre en {BODY, EXTERNAL, SKIN, EXTERIOR, CUERPO, PIEL}.

Caso rígido: valor **analítico** 0.0, marcado `IsAnalytic = true`.

---

### D.6 Separación del jacobiano respecto a 1

**Archivo:** `DeformationFieldMetrics.cs:139-141`
**Gradúa:** no

```
separación = max( |p99(J) − 1| , |1 − p1(J)| )
```

Percentiles sobre el conjunto de jacobianos del mismo dominio que D.5. Se usan percentiles y no
los extremos porque un solo vóxel en el borde del soporte controla min y max.

---

### D.7 Gradiente máximo del DVF

**Archivo:** `DeformationFieldMetrics.cs:325-327`
**Unidad:** adimensional
**Gradúa:** no

Norma de Frobenius del gradiente del desplazamiento, máximo sobre el interior:

```
‖∇u‖_F = sqrt( Σ_{i,j} (∂u_i/∂x_j)² )     con los nueve términos
```

Caso rígido: no aplicable (el gradiente es constante).

---

### D.8 DSC — coeficiente de Dice

**Archivo:** `SurfaceMetrics.cs:83`
**Unidad:** adimensional [0, 1]
**Gradúa:** **sí**

```
DSC = 2 · |A ∩ B| / (|A| + |B|)
```

Conteos de **vóxeles de la máscara rasterizada**, no volúmenes analíticos.

**Rejilla de comparación:** una por par de estructuras, construida en `BuildContourGrid`
(`:1825`) a partir de los contornos, no de la imagen:

- espaciado objetivo **2.0 mm isótropo**
- margen de **20 mm** por cada lado
- tope de **160 muestras por eje**; si se excede, el espaciado se engruesa a `extensión_mayor / 160`
- ejes canónicos (1,0,0),(0,1,0),(0,0,1): la rejilla es propia, no hereda orientación

**La máscara origen se rasteriza sin mapeo; la registrada, a través de `_mapper`.**

**Si hay varias estructuras emparejadas se reporta la peor, no la media.** Y el peor DSC, la peor
MDA y la peor HD95 pueden venir de estructuras distintas: cada una lleva su propio identificador.

**El contorno externo del paciente se excluye** de la comparación a nivel de órgano. Si resulta
ser lo único que emparejó, se reporta pero con `StructureMetricsAreSurfaceOutlineOnly = true`, lo
que lo degrada a informativo.

**Techo aritmético.** Desde la 3.0.1 se calcula `2·min(|A|,|B|)/(|A|+|B|)`, el mayor Dice que los
dos volúmenes permiten. Si queda por debajo de 0.90 se añade `; volumes cap DSC at X.XX` a la
columna de criterio y se emite un aviso. **No cambia la graduación.**

---

### D.9 MDA y HD95

**Archivo:** `SurfaceMetrics.cs:86-116`
**Unidad:** mm
**Gradúa:** MDA sí, HD95 no

Ambas salen del **mismo conjunto de distancias**, lo que garantiza `MDA ≤ HD95`:

1. Se extrae la superficie de cada máscara con **conectividad 6**. Un vóxel en el borde de la
   rejilla cuenta como superficie.
2. Transformada de distancia euclídea exacta (`DistanceTransform.SquaredDistanceMm`) de cada
   superficie, con el espaciado anisótropo de la rejilla.
3. Se construye la lista **simétrica**: distancia de cada vóxel de superficie de A a B, y de cada
   vóxel de superficie de B a A, en la misma lista.
4. `MDA = media` de esa lista. `HD95 = percentil 95` **con interpolación lineal**:

```
posición = 0.95 · (count − 1)
HD95 = sorted[floor] · (1 − w) + sorted[floor+1] · w,   w = posición − floor
```

Nótese que D.2 usa percentiles truncados y D.9 interpolados. Es una inconsistencia real; está en H.

---

### D.10 TRE — error de registro sobre blanco

**Archivo:** `RegistrationAnalyzer.cs:1908`
**Unidad:** mm
**Gradúa:** **sí**, media y máxima por separado

Para cada marcador emparejado por identificador entre las dos series:

```
error_i = ‖ mapper(posición_origen_i) − posición_destino_i ‖
TRE_media = Σ error_i / N
TRE_máxima = max error_i
```

Solo se usan estructuras de tipo DICOM **MARKER o ISOCENTER**. Las estructuras de contorno están
excluidas explícitamente porque su centro de masa se mueve al editar el contorno.

Con **menos de 3** marcadores se añade `" — indicative only"` a la nota y se emite un aviso. **La
graduación no cambia:** un solo marcador se gradúa igual que diez.

---

### D.11 Consistencia inversa

**Archivo:** `RegistrationAnalyzer.cs:2010`, residuo en `EvaluateRoundTripResidual` (`:2166`)
**Unidad:** mm
**Gradúa:** **sí**

Sobre una rejilla de **5×5×5 = 125 puntos** repartidos uniformemente por la región
`_fixedGeometry ?? _structureGeometry`:

```
para cada punto p:
    q = mapeo_directo(p)
    r = mapeo_inverso(q)
    residuo = ‖r − p‖
consistencia = max residuo
```

Es el **máximo**, no la media. Se usa rejilla y no solo las esquinas porque una composición
deformable no es afín y su peor punto puede estar en cualquier sitio.

Los índices se calculan como `i = (XSize−1)·a/4` para `a` en `0..4`, lo que incluye ambos extremos.
Los puntos que no se pueden mapear se saltan sin contar.

---

## E. Semáforo

**Esta es la parte que el port tiene que reproducir exactamente.**

### E.1 Tabla completa de umbrales

Solo **dos perfiles**: `Standard treatment` y `Stereotactic (SRS / SBRT)`. Los perfiles anatómicos
se eliminaron porque sus límites espaciales no tenían base en TG-132.

| Métrica | Clave | Gradúa | Límite verde | Límite amarillo | Inclusivo | Origen |
|---|---|---|---|---|---|---|
| Jacobiano < 0 | `Jacobian_Neg_%` | **Sí** | `0.0 %` | `0.0 %` | **sí, `≤`** | Tabla III, 1.ª cláusula: *"no negative values"* |
| DSC | `DSC` | **Sí** | `0.90` | `0.80` | **sí, `≥`** | Tabla III, rango `~0.80–0.90` |
| TRE media | `TRE_Mean` | **Sí** | voxel | voxel | **sí, `≤`** | Tabla III: *"maximum voxel dimension"* |
| TRE máxima | `TRE_Max` | **Sí** | voxel | voxel | **sí, `≤`** | Tabla III (la división media/máxima es del proyecto) |
| MDA | `MDA` | **Sí** | voxel | voxel | **sí, `≤`** | Tabla III: incertidumbre de contorneo o dimensión de vóxel |
| Consistencia inversa | `Inverse_Consistency` | **Sí** | voxel | voxel | **sí, `≤`** | Tabla III y §4.C.4 |
| NCC | `NCC` | No | — | — | — | §4.C.3 la admite, **sin tolerancia** |
| NMI | `NMI` | No | — | — | — | §4.C.3, sin tolerancia |
| SSD | `SSD` | No | — | — | — | §4.C.3, sin tolerancia |
| Desplazamiento máximo | `Max_Displacement` | No | — | — | — | No tabulado en TG-132 |
| Suavidad | `Smoothness` | No | — | — | — | No tabulado |
| Separación jacobiano de 1 | `Jacobian_Departure` | No | — | — | — | Tabla III 2.ª cláusula, atada a la estructura |
| Gradiente DVF | `DVF_Gradient_Max` | No | — | — | — | No tabulado |
| HD95 | `HD95` | No | — | — | — | No tabulado; TG-132 especifica MDA |

**`voxel`** = `measurements.NativeVoxelSizeMm`, que es `max(dimensión mayor de vóxel de las dos
series)`. En el perfil estereotáctico es **siempre 1.0 mm**, independientemente del vóxel.

**Verde y amarillo son iguales** en las cuatro métricas derivadas del vóxel, a propósito: la
Tabla III da **una** tolerancia, no dos bandas, así que una zona de atención habría que
inventarla.

### E.2 Reglas de comparación — exactas

`ThresholdProfile.Evaluate` (`:50`):

```csharp
if (!TryGetLimits(key, out limits)) return NotAvailable;   // el perfil no define la métrica

if (HigherIsBetter(key)) {
    if (value >= GreenLimit)  return Green;
    if (value >= YellowLimit) return Yellow;
    return Red;
}

if (value <= GreenLimit)  return Green;
if (value <= YellowLimit) return Yellow;
return Red;
```

`HigherIsBetter` vive en `MetricCatalog`, no en el perfil, para que no pueda volverse
inconsistente entre perfiles. Es **cierto** en cuatro métricas — `NCC`, `NMI`, `Smoothness` y
`DSC` — pero de esas cuatro **solo `DSC` gradúa**, así que en la práctica la rama `>=` solo se
recorre para DSC. Las otras tres usan el valor únicamente para el operador que `DescribeCriteria`
imprime. Las diez restantes son «menor es mejor».

**Todos los límites son inclusivos.** Verificado en `TEST_VECTORS.json`:

- `dsc_0.9` → Green, `dsc_0.8999` → Yellow
- `dsc_0.8` → Yellow, `dsc_0.7999` → Red
- `tremean_2.5` → Green, `tremean_2.5001` → Red (no hay amarillo: los dos límites son 2.5)
- `jacneg_0` → Green, `jacneg_0.0001` → Red

### E.3 Por qué una métrica medida puede no graduarse

`MetricEvaluator.WhyNotGated` (`:172`). **Se evalúa antes que el umbral** y devuelve `Informational`
con ese texto en la columna de criterio. En orden:

1. **La métrica no gradúa nunca** (`IsGating == false`): texto `"no TG-132 tolerance"`.
   Excepción para el desplazamiento máximo, que lleva texto más específico:
   - frames distintos → `"not in TG-132; different frames of reference"`
   - frame desconocido → `"not in TG-132; frame of reference unknown"`
2. **Métrica derivada del vóxel sin tamaño de vóxel legible**:
   `"not graded — voxel size unknown"`.
3. **DSC / MDA / HD95 medidas solo sobre el contorno externo**:
   `"not graded — measured on the patient surface outline only, not an organ"`.

### E.4 Regla de combinación — veredicto global

`AdvisoryEngine.BuildVerdict` (`:449`). **No hay ponderación. Es peor-caso con precedencia
estricta**, y la primera rama que se cumple gana:

```
red      = nº de métricas en Red
yellow   = nº de métricas en Yellow
green    = nº en Green  QUE NO SEAN ANALÍTICAS
analytic = nº analíticas que no estén en NotAvailable
informational = nº en Informational QUE NO SEAN ANALÍTICAS
unavailableCount = nº en NotAvailable
```

| # | Condición | Severidad | Color | Texto |
|---|---|---|---|---|
| 1 | `red > 0` | Critical | **Rojo** | `NOT COMPLIANT — Registration '{id}' breaches {red} criterion(s) of the [{perfil}] profile. {yellow} metric(s) in the attention zone, {unavailable} unevaluated, {informational} for information only.` |
| 2 | `yellow > 0` | Warning | **Amarillo** | `REVIEW REQUIRED — Registration '{id}' has {yellow} metric(s) in the attention zone of the [{perfil}] profile. {unavailable} unevaluated, {informational} for information only.` |
| 3 | `green == 0 && informational > 0` | Warning | **Amarillo** | `NOT VERIFIED — {informational} metric(s) of registration '{id}' were measured, but none of them carries a tolerance that can establish compliance, and {unavailable} could not be measured. The metrics that can verify a registration are TRE, MDA, DSC and consistency: placing matched landmarks, or contouring the same structure on both series, is what enables them.` |
| 4 | `green == 0` | Warning | **Amarillo** | `NO EVIDENCE — No metric of registration '{id}' could be evaluated. See the diagnostics tab for what failed.` + si `analytic > 0`: ` The {analytic} metric(s) shown with a value are exact by definition for this kind of transform and would read the same on any registration, so they verify nothing.` |
| 5 | `unavailableCount > 0` | Warning | **Amarillo** | `PARTIALLY COMPLIANT — The {green} evaluated metric(s) of registration '{id}' meet the [{perfil}] profile, but {unavailable} could not be measured. Verification is not complete.` |
| 6 | resto | Ok | **Verde** | `COMPLIANT — All {green} evaluated metrics of registration '{id}' satisfy the tolerances of the [{perfil}] profile.` + si `informational > 0`: ` A further {informational} metric(s) were measured for information only.` |

**Consecuencias que el port debe reproducir y que no son obvias:**

- **El color global nunca es "no disponible".** `NO EVIDENCE` sale amarillo, no gris.
  `AdvisorySeverity.Info` mapearía a `NotAvailable`, pero ninguna rama del veredicto la usa.
- **Verde exige cero métricas no disponibles.** En la práctica eso obliga a que toda métrica esté
  medida o marcada no aplicable. Ver `verdict_fully_compliant` en los vectores: es **el único caso
  verde de los diez** de veredicto, y hubo que asignar las catorce métricas para conseguirlo.
- **Las métricas analíticas no cuentan como verdes.** Un registro rígido cuyo Jacobiano analítico
  es 0 % y cuya suavidad es 1.0 no suma nada al conteo de `green`.
- **Las métricas no aplicables se filtran antes** (`MetricEvaluator.Evaluate:64`), así que no
  entran en `unavailableCount`.

### E.5 Mapeo severidad → color

`MainViewModel.ToSemaphore` (`:476`):

| `AdvisorySeverity` | `QASemaphore` |
|---|---|
| `Critical` | `Red` |
| `Warning` | `Yellow` |
| `Ok` | `Green` |
| `Info` *(y cualquier otra)* | `NotAvailable` |

### E.6 Estados especiales de una métrica individual

| Estado | `QASemaphore` | Texto en pantalla | Valor | ¿Visible? |
|---|---|---|---|---|
| Medida y clasificada | Green/Yellow/Red | `Green`/`Yellow`/`Red` | sí | sí |
| Medida sin tolerancia | `Informational` | `INFO` | sí | sí |
| Intentada y fallida | `NotAvailable` | `N/A` | no | sí, con motivo |
| No aplica al caso | `NotAvailable` | `N/A` | no | **no** — se filtra de las tablas y va a la lista de no aplicables |
| Analítica | Green/Yellow/Red | igual | sí | sí, pero no cuenta para el veredicto |
| Valor no finito | `NotAvailable` | `N/A` | no | sí, motivo `the computation produced a non-finite value` |

`MeasuredValue.Measured` rechaza NaN e infinito y los convierte en `Unavailable`. Esto es
importante: evita que un NaN llegue al semáforo, donde toda comparación sería falsa y la métrica
saldría **Red** en silencio.

### E.7 Textos exactos de motivos y recomendaciones

El código está **íntegramente en inglés**. El usuario pidió los textos en español; los traduzco
abajo, pero **el port debe emitir los literales en inglés** si quiere equivalencia con el
original. Los literales exactos están en `TEST_VECTORS.json`.

**Interpretación clínica por métrica incumplida** (`AdvisoryEngine.InterpretBreach:173`):

| Métrica | Traducción del texto |
|---|---|
| `Jacobian_Neg_%` | «Plegamiento de la rejilla: una inversión topológica no física en el campo de deformación. La Tabla III del TG-132 no admite valores negativos del jacobiano, por eso cualquier porcentaje distinto de cero incumple. Esto se mide dentro del contorno del paciente allí donde se pudo colocar uno sobre la rejilla del campo —la columna de criterio nombra la región—, así que no es plegamiento en el aire alrededor del paciente. El reporte pide evaluar la influencia de las regiones afectadas sobre el uso previsto; hasta que eso se haga, el registro no es apto para acumulación de dosis ni para propagación directa de contornos.» |
| `Max_Displacement` | «Desplazamiento vectorial alto. Confirme que corresponde a un cambio anatómico real y no a un error de correlación en los bordes del campo de visión. Este aviso solo aparece cuando ambas series comparten frame of reference; si no, la métrica no se clasifica.» |
| `DSC` / `MDA` / `HD95` | «El solape anatómico está fuera de tolerancia. Se recomienda verificación corte a corte y ajuste manual de los contornos propagados.» |
| `TRE_Mean` | «El residuo sobre marcadores excede la tolerancia. La Tabla III del TG-132 la fija en la dimensión máxima de vóxel. Es una medida directa del error espacial, así que pesa más que cualquiera de las métricas de intensidad.» |
| `TRE_Max` | «Al menos un marcador está desplazado más allá de la tolerancia aunque la media sea aceptable. Identifique cuál: un único residuo grande suele indicar una desalineación local más que un fallo global del registro.» |
| `Inverse_Consistency` | «Registrar en ambas direcciones no devuelve los puntos a su origen. Según el §4.C.4 del TG-132 esto evidencia un algoritmo inestable. Nótese que por sí solo no prueba inexactitud —un registro puede ser consistentemente erróneo— pero un residuo grande invalida cualquier afirmación de exactitud.» |
| cualquier otra | «Revise el valor contra el criterio del perfil.» |

**Formato del aviso de incumplimiento** (`BuildBreachAdvisory:149`):

```
{nombre} = {valor} against the {límite_verde} criterion of the [{perfil}] profile ({criterio}). {interpretación}
```

Categoría: `OUT OF TOLERANCE` si es Critical, `IN ATTENTION ZONE` si es Warning.

**Avisos que siempre se evalúan**, en este orden:

1. Uno por cada métrica en Red (Critical).
2. Uno por cada métrica en Yellow (Warning).
3. Uno por cada métrica en NotAvailable (Info, categoría `METRIC NOT EVALUATED`).
4. `MODALITY` (Info) — monomodal o multimodal.
5. `SAMPLING` (Info, o Warning si el solape < **0.35**).
6. `SCOPE OF SIMILARITY METRICS` (Info) — solo si alguna de NCC/NMI/SSD está disponible.
7. `TOLERANCE BASIS` (Info, o Warning si no hay tamaño de vóxel).
8. `TRANSFORM` (Warning) — si no hay matriz, o si hay gimbal lock.
9. `FRAME OF REFERENCE` (Info) — solo si hay desplazamiento máximo y los frames **no** coinciden.
10. `EMPTY REGISTRATION` (Info, o Warning si el estado contiene «unapproved») — si la
    transformación es exactamente la identidad.
11. El veredicto global.

---

## F. Casos borde y decisiones

| Situación | Qué hace hoy | Dónde |
|---|---|---|
| **Matriz no ortogonal** (escalado, cizalla) | Se **rechaza** la transformación completa. Nota: `the rotation submatrix is not orthonormal (det = X.XXXX); the transform includes scaling, shear or reflection`. Tolerancia 1e-3. Todo lo que dependa del mapeo queda N/A | `RigidTransform.cs:305` |
| **Reflexión** (det = −1) | Mismo rechazo, mismo texto | `:305` |
| **Matriz con NaN o infinito** | Rechazo previo: `the matrix contains non-finite values` | `:260` |
| **Matriz no 4×4** | `the retrieved matrix is not 4x4` | `:250` |
| **Convención indeterminable** | `could not determine the matrix convention: neither the last row nor the last column is homogeneous` | `:299` |
| **Rotación pura, traslación cero** | Se acepta asumiendo `P' = M·P`, y se registra que el sentido no es verificable | `:289` |
| **Gimbal lock** (pitch ≈ ±90°) | Se detecta con `cosPitch < 1e-6`. Yaw se fija en 0, el resto se pliega en pitch, se marca `GimbalLock = true` y se emite aviso | `RigidTransform.cs:167` |
| **FOR iguales** | El desplazamiento máximo **sí** se clasifica | `MetricEvaluator.cs:184` |
| **FOR distintos** | El desplazamiento máximo **no** se clasifica: `not in TG-132; different frames of reference` | `:184` |
| **FOR ilegible** | Tampoco se clasifica: `not in TG-132; frame of reference unknown` | `:185` |
| **Registro sin imagen asociada** | `SourceImage` o `RegisteredImage` nulos → sin intensidad, sin estructuras, sin TRE. El resto sigue | `RegistrationAnalyzer.cs:114, 1318, 1911` |
| **Modalidad desconocida** | Se omite el aviso de modalidad por completo. Nada más cambia | `AdvisoryEngine.cs:231` |
| **NaN en una métrica** | `MeasuredValue.Measured` lo convierte en `Unavailable("the computation produced a non-finite value")` | `QaMeasurements.cs:76` |
| **Valor exactamente 0** | Es un valor legítimo. `state_zero_value` en los vectores: TRE = 0.0 → Green | — |
| **Varianza cero en una imagen** | Las tres métricas de intensidad quedan N/A: `one of the images is constant over the overlapping region (zero variance)` | `SimilarityCalculator.cs:115` |
| **Menos de 1000 pares** | N/A con el conteo exacto | `:73` |
| **Solape < 10 %** | `VoxelPairSampler` emite problema; las tres intensidades quedan N/A. **Se registra como Warning, no Failure** | `VoxelPairSampler.cs:132`, `RegistrationAnalyzer.cs:441` |
| **Estructura emparejada que no rasteriza** | Se salta con Warning; si no queda ninguna, N/A | `:1472, 1550` |
| **Solo emparejó el contorno externo** | Se reporta pero degradado a informativo | `:1552` |
| **Cero estructuras leídas en una serie** | Se trata como **fallo de lectura**: N/A + `Failure` en el log | `:1383` |
| **Estructuras en ambas sin nombre común** | Se trata como **propiedad del caso**: NotApplicable + `Info` | `:1392` |
| **Contornos oblicuos** | Desde 3.0.1 el plano se identifica por **índice de corte**, no por z, y la z del polígono es la media de sus vértices | `StructureRasterizer.cs` |
| **Menos de 3 marcadores** | Se calcula igual y se gradúa igual; solo se añade `" — indicative only"` | `RegistrationAnalyzer.cs:1982` |
| **Ningún registro inverso** | NotApplicable con instrucción de cómo habilitarlo | `:2044` |
| **Registro inverso sin matriz legible** | N/A, con el texto «esto es un fallo, no dos» | `:2090` |
| **Transformación identidad** | Aviso `EMPTY REGISTRATION`. Warning si el estado contiene «unapproved», Info si no | `AdvisoryEngine.cs:367` |

### Supuestos implícitos

Estos no están comprobados en ningún sitio del código. Si ESAPI v11 los rompe, nada avisará:

1. **Las coordenadas de la API son coordenadas de paciente en milímetros.** No se verifica.
2. **`Frame.Origin` + cosenos directores describen la geometría completa.** No se comprueba que
   los cosenos sean ortonormales.
3. **`SourceImage` es el origen del mapeo y `RegisteredImage` el destino.** No verificable desde
   la API; el código lo dice explícitamente.
4. **`GetContoursOnImagePlane(i)` recorre `[0, Image.ZSize)`.** El conteo de planos se lee del
   `structureSet.Image`, y se asume que el índice de contorno es el índice de corte de esa imagen.
5. **La rejilla del campo de deformación está en coordenadas de paciente**, y pertenece a la
   imagen **registrada**. Lo segundo se decide por solape medido, no se asume.
6. **`VoxelToDisplayValue` es una rampa lineal.** Se comprueba con tres puntos (0, 500, 1000) y
   se avisa si no lo es, pero se usa igual.
7. **Los identificadores de estructura coinciden entre series** cuando se trata de la misma
   estructura. El emparejamiento es por `Id` ignorando mayúsculas.
8. **El contorno externo se reconoce por tipo DICOM `EXTERNAL` o por uno de seis nombres.** Un
   contorno llamado «Paciente» o «Contorno externo» no se reconocería.
9. **La rejilla de comparación de estructuras asume contornos en planos de z constante** para la
   rasterización, aunque la z del plano ya se calcula como media de vértices.

---

## G. Interfaz y salida

### La ventana

Modal, 1020×700, mínimo 820×560, centrada. Título:
`ESAPI Registration QA — Technical Audit v{AssemblyInformationalVersion}`.

De arriba abajo:

1. **Cabecera azul.** `REGISTRATION QUANTITATIVE AUDIT (AAPM TG-132)`, y debajo
   `Patient: {id} · Modalities: {par} · Type: {tipo}`. A la derecha, el desplegable de perfil
   anatómico.
2. **Banda de veredicto.** `GlobalStatusMessage` sobre fondo del color del semáforo.
3. **Una sola `DataGrid`** agrupada por `MetricResult.Group`, en este orden:
   `Intensity similarity`, `Deformation and topology`, `Spatial accuracy (TG-132 Table III)`,
   `Structures and surface`. Columnas: `Metric` (220 px, mín. 150), `Value` (110/90),
   `Status` (80/70), `Criterion / reason` (`*`, mín. 200). `HorizontalScrollBarVisibility="Disabled"`.
4. **Panel de progreso** superpuesto mientras `IsMeasuring`: rótulo, etapa, barra determinada
   (`Minimum=0`, `Maximum={StageTotal}`, `Value={StageCompleted}`, **ambos `Mode=OneWay`**) y el
   aviso de que la ventana no responderá a clics.
5. **Pie.** Resumen de transformación en una línea, y cuatro botones:
   `Advisories (N)`, `Diagnostics (N)` o `Diagnostics (N failures)`,
   `Add to dataset (CSV)`, `Export technical report (HTML)`.

Avisos y diagnóstico se abren en `DetailWindow`, una ventana genérica de tabla.

### El informe HTML

`HtmlReportBuilder.Build` (`:41`). UTF-8 sin BOM, escrito con `File.WriteAllText`.

Nombre por defecto: `RegistrationQA_{PatientId}_{yyyyMMdd_HHmm}.html`, pasado por
`SanitizeFileName`, que sustituye cada carácter de `Path.GetInvalidFileNameChars()` por `_`.
**La ruta la elige el usuario en un `SaveFileDialog`; no hay destino fijo.**

Esqueleto literal:

```html
<!DOCTYPE html><html lang='en'><head><meta charset='utf-8'/>
<title>Registration QA Report</title>
<style>…</style>
</head><body><div class='container'>
  <!-- secciones -->
</div></body></html>
```

Secciones, en orden exacto de `Build`:

| # | Método | Contenido |
|---|---|---|
| 1 | `AppendHeader` | `<div class='header'><h1>Registration Quantitative Audit Report</h1>` + metadatos |
| 2 | `AppendVerdict` | `<div class='verdict {clase}'>` con el texto del veredicto |
| 3 | `AppendAdvisories` | `<h2>Clinical advisories (AAPM TG-132)</h2>` + tabla `Level / Scope / Observation` |
| 4 | `AppendMetricsTable` | `<h2>1. Intensity similarity and topological metrics</h2>` + tabla `Metric / Value / Acceptance criterion / Status` |
| 5 | `AppendSpatialAccuracyTable` | `<h2>2. …</h2>` + nota + misma tabla |
| 6 | `AppendStructureTable` | `<h2>3. …</h2>` + tabla con cabecera `Anatomical index` |
| 7 | `AppendRigidTable` | `<h2>4. …</h2>` + nota de convención DICOM + tabla de transformación |
| 8 | `AppendProvenance` | Procedencia de la medición |
| 9 | `AppendNotApplicable` | Métricas no aplicables con su motivo |
| 10 | `AppendDiagnostics` | Entradas del log |
| 11 | `AppendMetricRationale` | `<h2>Appendix — basis for each metric</h2>`, generado desde `MetricCatalog` |
| 12 | `AppendSignatures` | Bloque de firmas |
| 13 | `AppendFooter` | `Generated by ESAPI_RegistrationQA v{versión}`, perfil activo, `Environment.UserName` |

Una fila de métrica:

```html
<tr[ class='na-row']>
  <td><b>{nombre}</b></td>
  <td>{valor}</td>
  <td>{criterio}</td>
  <td><span class='badge {clase}'>{estado}</span></td>
</tr>
<tr class='annotation'><td colspan='4'>{nota}</td></tr>   <!-- solo si hay nota -->
```

**Todo texto procedente de datos pasa por `WebUtility.HtmlEncode`**, en el único punto `E()`
(`:390`). Todos los números, por `CultureInfo.InvariantCulture`. El valor se formatea con `"F3"`.

**CSS literal** (`HtmlReportBuilder.Styles`, `:460`). Es cosmético y no afecta a ningún
resultado, pero va aquí entero porque el informe es el entregable firmable y debe salir igual:

```css
@page { size: A4 portrait; margin: 12mm; }
body { font-family: 'Segoe UI', Arial, sans-serif; margin: 0; color: #333; background: #fff;
       -webkit-print-color-adjust: exact; print-color-adjust: exact; font-size: 11px; }
.container { width: 100%; max-width: 190mm; margin: 0 auto; box-sizing: border-box; }
.header { background: #005a9c !important; color: #fff !important; padding: 12px 18px;
          border-radius: 5px; margin-bottom: 12px; }
.header h1 { margin: 0 0 6px 0; font-size: 15px; text-transform: uppercase; letter-spacing: 0.5px; }
.meta { display: flex; flex-wrap: wrap; gap: 14px; font-size: 11px; opacity: 0.95; }
.verdict { padding: 10px 14px; margin-bottom: 12px; font-weight: 600; font-size: 12px;
           border-left: 4px solid #999; background: #f4f6f9 !important; border-radius: 3px; }
.verdict.badge-red { border-left-color: #c62828; background: #ffebee !important; color: #b71c1c; }
.verdict.badge-yellow { border-left-color: #f9a825; background: #fffde7 !important; color: #7f6000; }
.verdict.badge-green { border-left-color: #2e7d32; background: #e8f5e9 !important; color: #1b5e20; }
.verdict.badge-na { border-left-color: #757575; color: #424242; }
h2 { color: #005a9c; border-bottom: 1.5px solid #005a9c; padding-bottom: 3px; font-size: 12px;
     margin: 16px 0 6px 0; text-transform: uppercase; }
p.note { font-size: 10px; color: #666; margin: 4px 0 6px 0; }
table { width: 100%; border-collapse: collapse; margin: 6px 0 10px 0; font-size: 10.5px;
        page-break-inside: avoid; }
th, td { border: 1px solid #d0d0d0; padding: 5px 8px; text-align: left; vertical-align: top; }
th { background: #f0f4f8 !important; color: #005a9c; font-weight: bold; }
tr.na-row td { color: #777; background: #fafafa !important; }
tr.annotation td { font-size: 9.5px; color: #666; background: #fbfbfb !important;
                   border-top: none; font-style: italic; }
table.provenance td.label { width: 40%; font-weight: 600; color: #005a9c; }
table.rationale { font-size: 9.5px; }
table.rationale td { line-height: 1.35; }
.badge { padding: 2px 7px; border-radius: 3px; font-weight: bold; text-align: center;
         display: inline-block; min-width: 46px; font-size: 9.5px; }
.badge-green { background: #c8e6c9 !important; color: #2e7d32 !important; }
.badge-yellow { background: #fff9c4 !important; color: #f57f17 !important; }
.badge-red { background: #ffcdd2 !important; color: #c62828 !important; }
.badge-na { background: #eceff1 !important; color: #546e7a !important; }
.signatures { margin-top: 28px; display: flex; justify-content: space-between;
              page-break-inside: avoid; }
.sig { width: 200px; border-top: 1px solid #333; text-align: center; padding-top: 4px;
       font-size: 10.5px; font-weight: 600; color: #444; }
.footer { margin-top: 15px; padding-top: 8px; border-top: 1px solid #e0e0e0; display: flex;
          justify-content: space-between; font-size: 9.5px; color: #777; page-break-inside: avoid; }
</style>
```

### El CSV acumulativo

`CsvDatasetExporter`. **Schema 7, 48 columnas**, UTF-8 **con** BOM (para que Excel lea bien los
acentos en Windows). Se **añade** al archivo elegido; si no existe, escribe la cabecera primero.

Columnas, en orden:

```
 1 SchemaVersion              17 MaxDisplacement_mm          33 TranslationZ_CC_mm
 2 ToolVersion                18 SameFrameOfReference        34 TranslationMagnitude_mm
 3 TimestampUtc               19 Smoothness                  35 RotationPitchX_deg
 4 Centre                     20 DVFGradientMax              36 RotationRollY_deg
 5 CaseHash                   21 DSC                         37 RotationYawZ_deg
 6 RegistrationType           22 MDA_mm                      38 GimbalLock
 7 FixedModality              23 HD95_mm                     39 TransformSource
 8 MovingModality             24 StructurePairs              40 VoxelPairs
 9 Profile                    25 WorstStructure              41 OverlapFraction
10 NCC                        26 TRE_Mean_mm                 42 EffectiveSampling_mm
11 NMI                        27 TRE_Max_mm                  43 ToolVerdict
12 SSD                        28 TRE_Landmarks               44 DiagnosticFailures
13 JacobianNegPercent         29 InverseConsistency_mm       45 DiagnosticWarnings
14 JacobianDomain             30 NativeVoxel_mm              46 NotApplicable
15 JacobianNegPercent_WholeField  31 TranslationX_LR_mm      47 PhysicistVerdict
16 JacobianDepartureFrom1     32 TranslationY_AP_mm          48 Notes
```

**No se escribe ningún identificador de paciente.** La columna `CaseHash` son los primeros
**12 caracteres hexadecimales de SHA-256** sobre paciente + identificador de registro
(`CsvDatasetExporter.cs:294`). Es estable entre ejecuciones, así que re-auditar el mismo caso no
duplica la fila.

---

## H. Posibles errores e inconsistencias

**No corregí ninguno.** Van ordenados por lo que me parece su importancia para el port.

### H.1 `MarkAllUnavailable` no asigna `Mda`

`RegistrationAnalyzer.cs:2288`. El método asigna las otras trece métricas pero **omite
`measurements.Mda`**. En la práctica no se nota porque el constructor de `QaMeasurements` ya la
deja en `Unavailable("not measured")`, así que el estado final es el mismo pero **el motivo es
distinto**: las demás dirán «there is no active registration in the Eclipse workspace» y MDA dirá
«not measured». Es una omisión, no una decisión.

### H.2 Tres definiciones distintas de percentil en el mismo programa

Las tres conviven y ninguna referencia a las otras:

| Dónde | Fórmula | Convención |
|---|---|---|
| `SimilarityCalculator.RobustRange:247` | `copy[(int)(f·(n−1))]` | índice truncado sobre `n−1` |
| `SurfaceMetrics.Percentile:161` | interpola entre `floor` y `floor+1` de `f·(n−1)` | **interpolación lineal** |
| `DeformationFieldMetrics.Percentile:382` | `sorted[ceil(f·n) − 1]` | **rango más cercano**, sobre `n` |

Para `n = 100` y `f = 0.95` dan índices 94, 94.05 interpolado y 94 respectivamente — casi lo
mismo. Para muestras pequeñas divergen. Ninguna es incorrecta por sí misma; lo discutible es que
sean tres sin que nada lo declare, y que el port podría unificarlas sin darse cuenta de que
cambia valores. **El port debe copiar las tres por separado.**

### H.3 La ventana no se puede cerrar mientras mide

`IsMeasuring` deshabilita la interacción y el trabajo corre en el hilo de UI. Si una llamada a la
API se cuelga, Eclipse queda bloqueado sin forma de cancelar. No hay token de cancelación en
ninguna parte. Es una decisión consciente (está documentada), pero el riesgo es real.

### H.4 `AdvisorySeverity.Info` nunca llega al veredicto

`MainViewModel.ToSemaphore` mapea `Info` a `NotAvailable`, pero `BuildVerdict` solo asigna
`Critical`, `Warning` u `Ok`. La rama existe y no puede ejecutarse. No hace daño; es código
muerto que sugiere un estado que no ocurre.

### H.5 TRE con un marcador gradúa igual que con diez

Un único marcador produce una TRE que se clasifica Verde o Roja con la misma autoridad que una
sobre veinte. Lo único que cambia es el sufijo `" — indicative only"` en la nota. Sobre un
registro MR→CT real esto significa que la fila de la Tabla III más importante puede estar
decidiéndose sobre un punto.

### H.6 La rejilla de comparación se dimensiona con contornos **sin mapear**

`BuildContourGrid` recibe los contornos tal cual, antes de aplicar el registro, así que abarca la
separación entre las dos estructuras además de la anatomía. En el fantoma: 207 mm en Z donde cada
estructura mide 40 mm. No cambia DSC/MDA/HD95 —se calculan de las máscaras y el espacio extra está
vacío— **pero** la rejilla está topada en 160 muestras por eje, así que en estructuras grandes ese
span inflado alcanza el tope y engruesa el espaciado. `BODY` recibió 3.42 mm donde le tocaban 2.66.
Ya está registrado en `CHANGELOG.md` como conocido y sin arreglar.

### H.7 El rasterizador extruye en vez de interpolar

Cada contorno recibe el grosor completo de su corte, así que una estructura de cuatro planos
recibe tapas cuadradas donde la anatomía se afila. Medido contra las estadísticas de Eclipse:
**+33 % y +36 %** en estructuras de 4 planos contra +4 % y +10 % en las de 17. Tiene un efecto de
segundo orden: **enmascara el techo del DSC**. Con los volúmenes de Eclipse el Dice máximo
alcanzable en ese caso era 0.837 y 0.800, bajo la tolerancia de 0.90, así que el aviso debería
dispararse; con los volúmenes inflados calcula 0.960 y 0.905 y se queda callado. Registrado en
`CHANGELOG.md`.

### H.8 `DSC` incumple aunque su techo sea inalcanzable

Si los dos volúmenes difieren más de 1.22×, el 0.90 es aritméticamente inalcanzable y la fila sale
Roja igual. El código lo **dice** (columna de criterio y aviso) pero sigue graduando. Es una
decisión declarada, no un descuido, pero produce NOT COMPLIANT sobre registros cuya alineación
está bien.

### H.9 El curl lee ~8 % bajo respecto a Eclipse

Las derivadas se evalúan solo en el interior y el máximo rotacional de ese campo cae en su
frontera. Eclipse mostró 2.15, el plugin 1.99. Es deliberado y está documentado; el curl no
gradúa nada.

### H.10 `ReadPlaneCount` usa `structureSet.Image.ZSize` para recorrer contornos

Si el structure set pertenece a una imagen distinta de la que se está auditando, el rango de
índices sería el equivocado. No se comprueba que coincidan.

### H.11 El veredicto se recalcula dos veces por exportación

`BuildReportData` llama a `AdvisoryEngine.Build` de nuevo en vez de reutilizar el resultado que
`ReevaluateThresholds` ya calculó. Da lo mismo porque es determinista, pero es trabajo duplicado y
una oportunidad de divergencia si alguna vez dejara de serlo.

---

## I. Limitaciones conocidas y qué está validado de verdad

### Validado contra una respuesta independiente

Todo esto es el propio Eclipse mostrando el mismo dato, sobre **una sola instalación**:

| Qué | Eclipse | Plugin | Fecha | Resultado |
|---|---|---|---|---|
| Rango del jacobiano | −0.72 / +3.04 | −0.7213 / +3.0431 | 2026-07-30 | Coincide |
| Rango de divergencia | −2.87 / +1.46 | exacto | 2026-07-30 | Coincide |
| Distancia (desplazamiento) | 58.4 mm | 58.399 mm | 2026-07-30 | Coincide |
| Componentes X, Y, Z del campo | los tres | los tres exactos | 2026-07-30 | Coincide |
| Traslación y rotación rígidas | −4.8 / 10.3 / −122.5 mm | −4.84 / 10.28 / −122.47 | 2026-08-01 | Coincide |
| Curl | 2.15 | 1.99 | 2026-07-30 | **8 % bajo**, causa establecida |
| Volúmenes de estructura | 2.5 / 1.8 / 2.1 / 1.4 cm³ | +4 % / +33 % / +10 % / +36 % | 2026-08-02 | Sesgo sistemático, ver H.7 |

**La convención de ejes quedó cerrada** por los dos caminos: componentes del campo deformable y
traslación del rígido, con código distinto cada uno.

### Validado contra un control

Las mismas dos series registradas de forma rígida y deformable, auditadas con los mismos ajustes:
consistencia inversa **0.259 mm** contra **7.042 mm** desde el mismo código, el caso rígido
COMPLIANT y el deformable NOT COMPLIANT. Una herramienta que reprobara todo habría reprobado
también el rígido.

### Validado por construcción

- **92 comprobaciones analíticas** en `tools/verify_math.py` (Python, reimplementa la aritmética).
- **102 comprobaciones de contrato** en `tools/DvfContractTests.cs` (ejecutan el C# real contra
  objetos con la forma de la API).
- `tools/run_checks.sh` corre ambas más una compilación con *warnings as errors*.
- Ambas suites están **verificadas capaces de fallar**: sustituir `det(I + ∇u)` por `det(∇u)`
  tumba cinco comprobaciones; borrar un `row.Add` del exportador CSV tumba la de alineación de
  columnas.

### NO validado — solo asumido

- **TRE**: ningún valor contrastado contra una respuesta independiente. Nunca ha corrido con más
  de un marcador emparejado.
- **Gradiente del DVF**: sin respuesta independiente.
- **DSC contra Eclipse**: discrepancia abierta (0.90 contra 0.953 en el fantoma). Eliminadas la
  resolución de rejilla y la dirección del mapeo; queda la interpolación entre planos de contorno.
- **Una sola instalación de Eclipse.** Todo lo que toca la API se ha ejercitado en una.
- **Solo CT–CT y CT–MR.** Ningún otro par de modalidades.
- **Las tolerancias no están calibradas contra ninguna distribución multicéntrica.** Son la
  Tabla III aplicada literalmente. El CSV existe precisamente para construir esa base.
- **El perfil estereotáctico nunca se ha ejercitado sobre un caso real**, solo en los vectores de
  prueba.

---

## Resumen para el port a v11

### Replicable con casi total seguridad

Todo `Models/` y la mitad de `Services/` que no toca la API: `SimilarityCalculator`,
`SurfaceMetrics`, `DistanceTransform`, `DeformationFieldMetrics`, `VoxelPairSampler`,
`MetricEvaluator`, `AdvisoryEngine`, `ThresholdProfile`, `MetricCatalog`, `HtmlReportBuilder`,
`CsvDatasetExporter`. Son aritmética y lógica pura, sin dependencia de Varian. `TEST_VECTORS.json`
los cubre entero.

Las barreras aquí son de **lenguaje**, no de API, y son mecánicas. No lo estimé: compilé el
repositorio con `-langversion:5`. `Models/` y `Services/` dan **un solo error** en 11 800 líneas,
el descarte `out _` de `MetricEvaluator.cs:152`. `ViewModels/` da 34, más `nameof` que `mcs` no
marca y el compilador de Microsoft sí, más dos tipos de .NET 4.5 (`IReadOnlyList<T>` y
`[CallerMemberName]`). El desglose completo está en `API_USADA.md` §11.

### Dependen de API dudosa en v11

Por orden de riesgo. El detalle completo está en `API_USADA.md`.

1. **Campo de deformación.** `DeformationField.GetVectors` es `VMS.IRS`, no ESAPI clásico. Si v11
   no lo expone, se pierden Jacobiano, separación de 1 y gradiente DVF — tres filas, una de ellas
   la que más gradúa.
2. **Mapeo punto a punto en deformables.** Sin `TransformPoint` o equivalente, un registro
   deformable se queda sin intensidad, sin estructuras, sin TRE y sin consistencia.
3. **La matriz de registro.** Trece rutas probadas, ninguna garantizada en v11.
4. **`GetVoxels`.** La combinación portador/forma de búfer varía entre versiones, y ya falló una
   vez aceptando la llamada sin escribir nada.
5. **`Frame` y sus cosenos directores.** v11 podría exponer la geometría de otra forma.

### Lo que yo probaría primero en Eclipse 11, con una sonda de solo lectura

En este orden, porque cada paso decide si el siguiente tiene sentido:

1. **¿Existe `ScriptContext.Registration`?** Si v11 no lo tiene, el punto de entrada entero cambia.
2. **Volcar el surtido de miembros del objeto de registro.** Nombre de clase CLR, propiedades y
   métodos. Eso responde de golpe por la matriz, por el tipo y por el mapeo de puntos.
3. **¿Responde alguna de las trece rutas de matriz?** Y si responde, ¿con qué forma de contenedor?
4. **¿Hay `TransformPoint` o similar, y en qué portador?** Probarlo con dos puntos reales y
   comprobar que **no** devuelve la entrada sin cambios.
5. **¿`GetVoxels` escribe algo?** Probar en un plano a un tercio del volumen, nunca en el plano 0,
   y verificar que los datos **no son constantes**.
6. **¿`GetContoursOnImagePlane` existe y devuelve polígonos con z?**
7. **¿Hay algo parecido a `DeformationField`?** Si no, decidir pronto si el port cubre solo
   registros rígidos.

El `probe/` del repositorio original hace exactamente esto y es de solo lectura; portarlo primero
es más barato que portar el plugin a ciegas.

---

## Cómo se generaron los vectores de prueba

`TEST_VECTORS.json` **no contiene ni un solo número calculado a mano**. Los 78 casos salen de
invocar las clases del repositorio y volcar lo que devolvieron.

El programa que lo hace es `GenerateTestVectors.cs`, en esta misma carpeta. Es el cuarto archivo,
además de los tres que pediste: sin él los vectores no se pueden regenerar ni volver a correr
contra el port, que es justamente para lo que existen. Bórralo si sobra.

Comandos exactos, desde la raíz del repositorio:

```bash
MONO_API=/usr/lib/mono/4.8-api
REFS="-r:$MONO_API/mscorlib.dll -r:$MONO_API/System.dll \
      -r:$MONO_API/System.Core.dll -r:$MONO_API/Microsoft.CSharp.dll"

mcs -target:library -langversion:latest -nostdlib $REFS -out:/tmp/core.dll \
    Models/*.cs Services/*.cs Properties/AssemblyInfo.cs

mcs -langversion:latest -r:/tmp/core.dll -out:/tmp/gen.exe \
    handoff_coregistro/GenerateTestVectors.cs

HANDOFF_COMMIT=$(git rev-parse HEAD) mono /tmp/gen.exe > handoff_coregistro/TEST_VECTORS.json
```

`Properties/AssemblyInfo.cs` entra en la compilación a propósito: sin él,
`HtmlReportBuilder.AssemblyVersion()` lee un ensamblado sin atributos de versión y el JSON saldría
marcado `0.0.0.0` en vez de `3.0.1`.

Qué clase produjo cada familia de casos:

| Prefijo de id | Clase ejecutada |
|---|---|
| `dsc_`, `jacneg_`, `tremean_`, `tremax_`, `mda_`, `ic_`, `ncc_`, `hd95_` | `ThresholdProfile.Resolve` + `MetricEvaluator.EvaluateOne` |
| `state_` | `MetricEvaluator.EvaluateOne`, `.Evaluate`, `.NotApplicable` |
| `matrix_` | `RigidTransform.TryFromRawMatrix`, `.GetEulerAnglesDegrees`, `.MaxDisplacementOver`, `.TryInvert`, `.IsIdentity` |
| `surface_` | `SurfaceMetrics.Compare` (que a su vez usa `DistanceTransform`) |
| `similarity_` | `SimilarityCalculator.Compute` y `.ChooseBinCount` |
| `dsc_ceiling_` | La aritmética `2·min/(|A|+|B|)` que documenta `RegistrationAnalyzer.DscCeiling`, que es privado |
| `verdict_` | Las cuatro llamadas a `MetricEvaluator.Evaluate` en el mismo orden que el view model, más `AdvisoryEngine.Build` |

**Única excepción, y la declaro:** `DscCeiling` es `private static`, así que los casos
`dsc_ceiling_*` reproducen su fórmula en el generador en lugar de llamarla. Es una línea
(`2.0 * Math.Min(a,b) / (a+b)`, con guarda de total ≤ 0) y está copiada literal de
`RegistrationAnalyzer.cs:1782`. Todo lo demás es ejecución directa.

**Anonimización:** ningún caso contiene datos de paciente. Los identificadores son `SYNTHETIC`,
las máscaras son cubos generados por `Cube(n, lo, hi)` y los vectores de intensidad salen de
`new Random(20260801)`, semilla fija para que sean reproducibles. Los únicos números tomados de
un caso real son volúmenes de estructura en cm³ y una matriz de traslación/rotación, que ya
aparecen publicados en `CHANGELOG.md` y no identifican a nadie.
