# API usada — inventario exhaustivo para el port a ESAPI v11

**Commit:** `324d78f3d809` · **Origen:** ESAPI 18.1 / VMS.IRS.Scripting, .NET 4.8, C# 7.3
**Destino:** ESAPI v11, `.esapi.dll`, .NET 4.0, C# 5

Las líneas son del commit indicado. Todo acceso a la API pasa por `Services/Dyn.cs`, que captura
la excepción y la registra: **nada de esto revienta el script si falta**, cada ausencia degrada
una métrica concreta.

Columna **Riesgo**:

- 🔴 **Alto** — sé que esto cambia entre versiones, o es `VMS.IRS` y no ESAPI clásico.
- 🟠 **Medio** — existe en ESAPI clásico pero con firma o portador que varía.
- 🟢 **Bajo** — estable desde v11 hasta hoy, hasta donde puedo afirmar.
- ⚪ **No es Varian** — BCL o WPF.

---

## 1. Punto de entrada y contexto

| Miembro | Para qué | Archivo:línea | Riesgo |
|---|---|---|---|
| `VMS.IRS.Scripting.ScriptContext` | Parámetro de `Execute` | `ESAPI_RegistrationQA.cs:13` | 🔴 **El namespace es `VMS.IRS.Scripting`, no `VMS.TPS.Common.Model.API`.** En ESAPI v11 clásico la firma es `Execute(ScriptContext, Window)` o `Execute(ScriptContext)` sobre `VMS.TPS.Common.Model.API.ScriptContext`. **Esto cambia seguro.** |
| `ScriptContext.Patient` | Comprobación de nulo | `ESAPI_RegistrationQA.cs:23` | 🟢 |
| `Patient.Id` | Identificador, cabecera e informe | `MainViewModel.cs:495` | 🟢 |
| `ScriptContext.Registration` | **El registro que se audita** | `RegistrationAnalyzer.cs:63` | 🔴 No tengo constancia de que ESAPI v11 exponga `Registration` en el contexto. **Verificar primero.** |
| `Patient.Registrations` | Buscar el registro inverso | `RegistrationAnalyzer.cs:2224` | 🔴 Igual |
| `ScriptContext.Registrations` | Alternativa | `:2225` | 🔴 |
| `Patient.MIRSRegistrations` | Alternativa VMS.IRS | `:2226` | 🔴 Específico de VMS.IRS |

**No se usan:** `PlanSetup`, `Course`, `PlansInScope`, `StructureSet` del contexto, `ExternalBeam`,
dosis. Verificado con `grep`: no aparecen en ninguna parte del código del plugin.

---

## 2. Objeto de registro

| Miembro | Para qué | Archivo:línea | Riesgo |
|---|---|---|---|
| `.Id` | Identificador | `RegistrationAnalyzer.cs:161` | 🟢 |
| `.Name` | Alternativa | `:162` | 🟢 |
| `.RegistrationType` | Tipo declarado | `:188` | 🟠 En VMS.IRS **no existe**; el código cae al nombre de clase |
| `.Type` | Alternativa | `:189` | 🟠 Idem |
| *nombre de clase CLR* | Último recurso para el tipo | `:199` | 🔴 `MIRSRigidRegistration` / `MIRSNonRigidRegistration` son nombres de VMS.IRS. En v11 serán otros y **la clasificación rígido/deformable fallaría en silencio** |
| `.Status` | Estado de aprobación | `:225` | 🟠 Solo contextual |
| `.ApprovalStatus` | Alternativa | `:226` | 🟠 |
| `.SourceImage` | Imagen origen | `:83` | 🟠 En ESAPI clásico el objeto `Registration` no expone imágenes así |
| `.RegisteredImage` | Imagen destino | `:84` | 🟠 Idem |

---

## 3. La matriz de transformación

`Services/MatrixReader.cs:97-109`. Trece rutas, en este orden exacto:

| # | Ruta | Riesgo |
|---|---|---|
| 1 | `RigidRegistration.TransformationMatrix` | 🔴 La que funciona en VMS.IRS |
| 2 | `RigidRegistration.Matrix` | 🔴 |
| 3 | `RigidRegistration.Transform` | 🔴 |
| 4 | `TransformationMatrix` | 🟠 |
| 5 | `Matrix` | 🟠 |
| 6 | `TransformMatrix` | 🟠 |
| 7 | `RigidTransformMatrix` | 🟠 |
| 8 | `Transform.Matrix` | 🟠 |
| 9 | `Transform.TransformationMatrix` | 🟠 |
| 10 | `Transform` | 🟠 |
| 11 | `SpatialRegistration.TransformationMatrix` | 🟠 |
| 12 | `Registration.TransformationMatrix` | 🟠 |
| 13 | `FrameOfReferenceTransformationMatrix` | 🟠 Nombre DICOM; el más probable en v11 |

Si ninguna responde, barre el objeto **por reflexión** buscando cualquier miembro cuyo valor se
pueda interpretar como matriz 4×4.

**Siete formas de contenedor aceptadas** (`MatrixReader.cs`): `double[,]`, jagged `double[][]`,
plano de 16 elementos *row-major*, plano de 12, indexador `[r,c]`, indexador `[i]`, y miembros
nombrados `m00..m33` o `M11..M44`.

Esa flexibilidad es la mejor baza del port: **probablemente v11 encaje en alguna de las siete sin
tocar nada**, siempre que una de las trece rutas (o el barrido) encuentre el objeto.

---

## 4. Mapeo punto a punto

`Services/PointMapperReader.cs`. **10 nombres de método × 9 portadores**, se prueba todo contra
todo.

Métodos, en orden de preferencia (`:43-57`):

```
TransformPointToRegistered · TransformPointFromSourceToRegistered · MapPointToRegistered
TransformPoint ← la que responde en VMS.IRS · MapPoint · DeformPoint
TransformPointToSource · MapPointToSource · InverseTransformPoint ← último recurso, mapea al revés
```

Portadores (`:62-74`): el propio registro, y luego
`RigidRegistration`, `NonRigidRegistration`, `DeformableRegistration`, `DeformableRegistrationField`,
`Registration`, `SpatialRegistration`, `DeformationField`, `VectorField`.

Tipo del argumento: lo que la API espere. Se construye un `VVector`-like por reflexión y se aceptan
componentes como campo o propiedad, en minúscula (`x`) o mayúscula (`X`).

**Un candidato no se acepta por no lanzar excepción.** Se prueba con dos puntos reales —
`(0,0,0)` y `(37.5, −21.25, 64.0)` (`:192`) — y se **rechaza si devuelve la entrada sin cambios**,
porque un método que devuelve lo que recibe se reportaría como un registro perfecto.

| Riesgo | Consecuencia si falta en v11 |
|---|---|
| 🔴 Alto | Para un registro **rígido**: se cae a la matriz, no se pierde nada. Para uno **deformable**: se pierden intensidad, estructuras, TRE y consistencia inversa — es decir, casi todo. |

---

## 5. Imagen y geometría

| Miembro | Para qué | Archivo:línea | Riesgo |
|---|---|---|---|
| `imageLike.Frame` | Portador de geometría, 1.ª opción | `EsapiImageReader.cs:56` | 🔴 **`Frame` es VMS.IRS.** ESAPI clásico pone la geometría en `Image` directamente |
| `imageLike.Image` | 2.ª opción | `:57` | 🟢 |
| *el objeto mismo* | 3.ª opción | `:58` | — |
| `frame.XSize` / `YSize` / `ZSize` | Dimensiones | `EsapiImageReader.cs` | 🟠 En ESAPI clásico son `Image.XSize` etc. — la 2.ª opción debería cubrirlo |
| `frame.XRes` / `YRes` / `ZRes` | Resolución en mm | idem | 🟠 Idem |
| `frame.Origin` | Origen en coordenadas de paciente | idem | 🟠 |
| `frame.XDirection` / `YDirection` / `ZDirection` | Cosenos directores | idem | 🟠 |
| `.x/.y/.z` o `.X/.Y/.Z` sobre el vector | Componentes | idem | 🟢 Se aceptan ambas grafías |
| `frame.VoxelToDisplayValue(int)` | Rampa vóxel → HU | `EsapiImageReader.cs:334-336` | 🟠 En ESAPI clásico el equivalente es `Image.VoxelToDisplayValue`. Si falta, se usan valores crudos y se avisa |
| `Image.Series.Modality` | Modalidad | `:288` | 🟢 |
| `Series.Modality` / `Modality` / `Frame.Modality` | Alternativas | `:289-291` | 🟢 |
| `Image.FOR` | Frame of Reference UID | `RegistrationAnalyzer.cs:305` | 🟠 |
| `FOR` / `Image.FrameOfReferenceUid` / `FrameOfReferenceUid` / `Series.FOR` | Alternativas | `:306-309` | 🟠 En ESAPI clásico suele ser `Image.FOR` |
| `Image.Id` / `Id` | Identificador de imagen, para el registro inverso | `:2208-2209` | 🟢 |

---

## 6. Lectura de vóxeles

`Services/VoxelPlaneReader.cs`. Se prueba **cada portador contra cada forma de búfer**:

- **Portadores:** `Frame`, `Image`, el objeto directo.
- **Firma:** `GetVoxels(int plane, buffer)`.
- **Formas de búfer:** `int[,]` y `ushort[,]`.

**Un búfer que no lanza no es un búfer que funciona.** El código prueba en planos a **1/3, 1/2 y
2/3** del volumen — nunca el plano 0, que en un TC de cabeza es aire uniforme — y acepta la
combinación solo cuando **ha escrito datos no constantes**.

> Esto no es paranoia: en un Eclipse real `Frame.GetVoxels(int, ushort[,])` aceptó la llamada y no
> escribió nada. Todos los vóxeles volvieron como 0, que tras la rampa HU se lee como −1000 HU
> uniformes, y la auditoría entera se derrumbó cuatro pasos más allá.

| Riesgo | Consecuencia si falta |
|---|---|
| 🔴 Alto | Sin vóxeles: no hay NCC, NMI ni SSD. **DSC, MDA, HD95 y TRE siguen funcionando**, porque los contornos llevan sus propias coordenadas de paciente y no necesitan un solo vóxel. |

**Submuestreo:** `MaxSamplesPerAxis = 128`, paso `ceil(tamaño/128)` por eje. Máximo ≈ 2.1 M vóxeles
por volumen.

---

## 7. Estructuras y contornos

| Miembro | Para qué | Archivo:línea | Riesgo |
|---|---|---|---|
| `Image.StructureSets` | Colección de structure sets | `StructureRasterizer.cs:412` | 🟢 |
| `StructureSets` | Alternativa | `:413` | 🟢 |
| `structureSet.Structures` | Estructuras | `:428`, `:548` | 🟢 |
| `structure.Id` | Identificador, usado para emparejar | `:433` | 🟢 |
| `structure.DicomType` | Tipo DICOM RT | `:442` | 🟠 |
| `structure.StructureType` | Alternativa (enum, no string) | `:443` | 🔴 `VMS.CA.Scripting.VolumetricStructure` **no tiene `DicomType`**; tiene `StructureType`. En ESAPI clásico es al revés. **El código ya prueba las dos** |
| `structureSet.Image.ZSize` | Nº de planos a recorrer | `:566` | 🟠 |
| `structureSet.Image.Frame.ZSize` | Alternativa | `:574` | 🔴 VMS.IRS |
| `structureSet.Image.Frames` → `frame.ZSize` | Alternativa | `:582` | 🔴 VMS.IRS |
| `structure.GetContoursOnImagePlane(int)` | **Polígonos de un corte** | `:625` | 🟠 Existe en ESAPI clásico. Se recorre `i` en `[0, ZSize)` |
| `vertex.x/.y/.z` o `.X/.Y/.Z` | Vértices | `:689` | 🟢 Ambas grafías |

**Reconocimiento del contorno externo** (`StructureRasterizer.cs:343` y `:354`): tipo DICOM `EXTERNAL`, o
`Id` exactamente igual (ignorando mayúsculas) a uno de
`{BODY, EXTERNAL, SKIN, EXTERIOR, CUERPO, PIEL}`.

---

## 8. Marcadores (para la TRE)

| Miembro | Para qué | Archivo:línea | Riesgo |
|---|---|---|---|
| `Image.StructureSets` / `StructureSets` | Misma colección que arriba | `LandmarkExtractor.cs:42-43` | 🟢 |
| `DicomType` / `StructureType` | Filtrar por `MARKER` o `ISOCENTER` | `:73-74` | 🔴 Igual que en §7 |
| `structure.CenterPoint` | Posición del marcador, 1.ª opción | `:131` | 🟠 Propiedad de `VolumetricStructure` |
| `structure.Position` | 2.ª | `:132` | 🟠 |
| `structure.Point` | 3.ª | `:133` | 🟠 |
| `structure.Points[0]` | 4.ª | `:134` | 🔴 Añadida porque un caso real devolvió `VMS.CA.Scripting.PointsStructure`, que **no tiene ninguna de las otras tres**, solo una colección `Points` con un punto |

---

## 9. Campo de deformación

`Services/DeformationFieldReader.cs`. **Lo más específico de VMS.IRS de todo el plugin.**

Ruta observada en la sonda:

```
MIRSNonRigidRegistration
  └─ .NonRigidRegistration
       └─ .DeformationField            → VectorField
            ├─ XSize, YSize, ZSize             (int)
            ├─ XRes, YRes, ZRes                (double, mm)
            ├─ Origin, XDirection, YDirection, ZDirection   (opcional, para la geometría)
            └─ GetVectors(VectorFloat[,,] preallocatedBuffer)
                                       VectorFloat { float X, Y, Z }
```

Portadores probados (`:112-119`): `NonRigidRegistration`, `RigidRegistration`,
`DeformableRegistration`, `DeformableRegistrationField`, `Registration`.

También lee, si existen: `PreTransformationMatrix` y `PostTransformationMatrix`, para confirmar
que las partes afines son rígidas (`det = 1`) y que el Jacobiano del campo es por tanto el
Jacobiano total.

**Detalles de implementación que el port debe conservar:**

- El búfer se asigna por reflexión con el tipo de elemento que la API pida: `Array.CreateInstance`
  sobre el tipo deducido de la firma de `GetVectors`.
- La lectura usa un **genérico tipado más accesores compilados** con `System.Linq.Expressions`
  (`:495-503`). No es decoración: la versión con `Array.GetValue` + `PropertyInfo.GetValue`
  tardaba **5043 ms**; esta tarda **283 ms** sobre el mismo campo de 1.5 M vectores.
  `System.Linq.Expressions` existe desde .NET 3.5, así que **sirve en .NET 4.0**.
- `VectorFloat` almacena en **float32**, lo que cuesta ~2e-8 en el Jacobiano. Está fijado por un
  test de contrato; no intentes exigir más precisión.

| Riesgo | Consecuencia si falta en v11 |
|---|---|
| 🔴 **El más alto de todos** | Se pierden Jacobiano negativo, separación de 1 y gradiente DVF. El Jacobiano es **la métrica con la tolerancia más literal de la Tabla III**. Un registro deformable quedaría sin ninguna métrica de deformación. |

---

## 10. Reflexión y BCL

| Miembro | Para qué | Riesgo |
|---|---|---|
| `object.GetType()`, `Type.GetMembers/GetMethods/GetProperties/GetFields` | Barridos de `MatrixReader`, `PointMapperReader`, `DeformationFieldReader` | ⚪ |
| `MethodInfo.Invoke`, `MakeGenericMethod` | Invocación dinámica | ⚪ |
| `Array.CreateInstance` | Búfer del campo de deformación | ⚪ |
| `System.Linq.Expressions` | Accesores compilados | ⚪ .NET 3.5+, sirve en 4.0 |
| `dynamic` / `Microsoft.CSharp` | Todo `Dyn.cs` | ⚪ .NET 4.0+, **sirve**. Requiere referencia a `Microsoft.CSharp` |
| `System.Security.Cryptography.SHA256` | `CaseHash` del CSV | ⚪ |
| `System.Net.WebUtility.HtmlEncode` | Escapado del informe | ⚪ .NET 4.0+ |

---

## 11. WPF y lo que no compila en .NET 4.0 / C# 5

| Elemento | Dónde | Problema |
|---|---|---|
| `IReadOnlyList<T>` | `MainViewModel.cs:125` | **.NET 4.5+.** Sustituir por `IList<T>` o `ReadOnlyCollection<T>` |
| `[CallerMemberName]` | `MainViewModel.cs:605` | **.NET 4.5+.** Pasar el nombre de propiedad como literal en cada llamada |
| Interpolación `$"…"` | `MainViewModel.cs`, 11 usos | C# 6. Sustituir por `string.Format` |
| `nameof(…)` | `MainViewModel.cs` ×11, `RelayCommand.cs` ×1 | C# 6. Sustituir por literales |
| `?.` | `MainViewModel.cs` ×2 | C# 6. Expandir a comprobación de nulo |
| `?? throw …` | `MainViewModel.cs` ×1, `RelayCommand.cs` ×1 | C# 7. Expandir a `if (x == null) throw …` |
| Miembros con cuerpo de expresión `=>` | `RelayCommand.cs` ×3, accesores de `MainViewModel` | C# 6. Expandir a bloques |
| `out string id` en línea | `MainViewModel.cs:495` | C# 7. Declarar la variable antes |
| `out _` (descarte) | **`MetricEvaluator.cs:152`** | C# 7. **Es el único de `Services/`** — declarar una variable y descartarla |
| `ObservableCollection`, `INotifyPropertyChanged`, `Dispatcher`, `DispatcherPriority` | `MainViewModel`, UI | ⚪ WPF, disponible en .NET 4.0 |
| `Microsoft.Win32.SaveFileDialog` | Exportación | ⚪ |
| `CommandManager.InvalidateRequerySuggested` | `RelayCommand` | ⚪ |

### Esto no es una estimación: lo compilé

Compilando el repositorio con `-langversion:5` bajo Mono:

```
$ mcs -target:library -langversion:5 -nostdlib $REFS -out:core5.dll Models/*.cs Services/*.cs
Services/MetricEvaluator.cs(152,86): error CS1644: Feature `discards' cannot be used
                                     because it is not part of the C# 5.0 language specification
```

**Un solo error en 11 800 líneas.** `Models/` y `Services/` ya son C# 5 salvo ese descarte. Los
`out` que aparecen al buscar con grep en esos directorios son **declaraciones de parámetro**, no
variables en línea, y compilan sin tocar nada.

Sobre `ViewModels/`, el mismo compilador da **34 errores CS1644**:

| Nº | Característica |
|---|---|
| 11 | cadenas interpoladas |
| 11 | miembros con cuerpo de expresión |
| 5 | accesores de propiedad con cuerpo de expresión |
| 2 | expresiones `throw` |
| 2 | operador de propagación de nulo `?.` |
| 2 | accesores de evento con cuerpo de expresión |
| 1 | declaración de variable `out` en línea |

`nameof` **no** aparece en esa lista porque `mcs` no lo marca, pero es C# 6 por especificación y
el compilador de Microsoft sí lo rechazará. Hay 12 usos (11 en `MainViewModel`, 1 en
`RelayCommand`).

A esto hay que sumar los dos tipos de .NET 4.5 (`IReadOnlyList<T>` y `[CallerMemberName]`), que
son errores de biblioteca y no de lenguaje, así que no salen en ese recuento.

Todo el trabajo de modernización está concentrado en `ViewModels/MainViewModel.cs` y
`RelayCommand.cs`: **638 líneas de las 12 455 del plugin.**

---

## 12. Resumen de riesgo por métrica

Qué sobrevive en v11 según qué pieza de API falte.

| Métrica | Necesita | Riesgo de pérdida |
|---|---|---|
| NCC, NMI, SSD | `GetVoxels` **+** mapeo | 🔴 Alto |
| Desplazamiento máximo (rígido) | Matriz | 🟠 Medio |
| Desplazamiento máximo (deformable) | Campo de deformación | 🔴 Alto |
| Jacobiano negativo (rígido) | Nada — es analítico | 🟢 **Ninguno** |
| Jacobiano negativo (deformable) | Campo de deformación | 🔴 Alto |
| Separación del jacobiano de 1 | Campo de deformación | 🔴 Alto |
| Gradiente DVF | Campo de deformación | 🔴 Alto |
| Suavidad | Nada — es analítico | 🟢 **Ninguno** |
| **DSC** | Contornos **+** mapeo | 🟠 Medio — **no necesita vóxeles** |
| **MDA** | Contornos **+** mapeo | 🟠 Medio |
| **HD95** | Contornos **+** mapeo | 🟠 Medio |
| **TRE media y máxima** | Marcadores **+** mapeo | 🟠 Medio |
| **Consistencia inversa** | Mapeo **+** colección de registros | 🔴 Alto |
| Traslación y rotación | Matriz | 🟠 Medio |

**La observación que más importa para planificar el port:** DSC, MDA, HD95 y TRE —cuatro de las
cinco filas de la Tabla III— **no dependen de `GetVoxels`**. Los contornos y los marcadores llevan
sus propias coordenadas de paciente. Si en v11 no se consiguen leer los vóxeles, lo que se pierde
son las tres métricas de intensidad, que son precisamente las que **TG-132 no gradúa**.

Lo que de verdad decide el alcance del port es si v11 expone un **mapeo punto a punto** y un
**campo de deformación**. Sin el primero no hay nada. Sin el segundo hay una herramienta completa
para registros rígidos y ninguna métrica de deformación para los deformables.
