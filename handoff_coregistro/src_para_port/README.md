# `src_para_port/` — código fuente sin modificar

26 archivos copiados **byte a byte** desde el commit `324d78f3d809` (tag `v3.0.1`). Verificado
con `cmp` contra los originales: ninguno difiere.

```
Models/        9 archivos   (los 9 del repositorio, completo)
Services/     16 archivos
Properties/    1 archivo    AssemblyInfo.cs
```

**Services incluidos:** `SimilarityCalculator`, `SurfaceMetrics`, `DistanceTransform`,
`DeformationFieldMetrics`, `VoxelPairSampler`, `SampledVolume`, `PointMapper`, `MetricEvaluator`,
`AdvisoryEngine`, `HtmlReportBuilder`, `CsvDatasetExporter`, `DiagnosticLog`,
`MeasurementProgress`, `StructureRasterizer`, `LandmarkExtractor`, `RegistrationAnalyzer`.

**Excluidos a propósito:** `ViewModels/`, `UI/`, `Dyn.cs`, `MatrixReader.cs`,
`PointMapperReader.cs`, `EsapiImageReader.cs`, `VoxelPlaneReader.cs`, `DeformationFieldReader.cs`.

---

## Sí, falta código del que dependen los copiados

**Esto no compila tal cual.** Faltan **seis** archivos, no cinco: `VoxelPlaneReader.cs` no lo
mencionaste en la lista de exclusiones, pero `EsapiImageReader` lo necesita.

### Qué copiado necesita qué excluido

| Archivo copiado | Depende de | Usos |
|---|---|---|
| `DeformationFieldMetrics.cs` | `DeformationFieldReader` | 2 |
| `LandmarkExtractor.cs` | `Dyn`, `MatrixReader` | 19, 2 |
| `StructureRasterizer.cs` | `Dyn`, `MatrixReader` | 22, 4 |
| `RegistrationAnalyzer.cs` | `Dyn`, `MatrixReader`, `PointMapperReader`, `EsapiImageReader`, `DeformationFieldReader` | 29, 1, 1, 12, 6 |

Los otros 22 archivos copiados no dependen de nada excluido.

### Y los excluidos dependen entre sí

| Excluido | Necesita a su vez |
|---|---|
| `Dyn.cs` | nada — es la hoja del árbol |
| `MatrixReader.cs` | `Dyn` |
| `PointMapperReader.cs` | `MatrixReader` → `Dyn` |
| `VoxelPlaneReader.cs` | `Dyn`, `MatrixReader` |
| `EsapiImageReader.cs` | `Dyn`, `VoxelPlaneReader` → `MatrixReader` |
| `DeformationFieldReader.cs` | `MatrixReader` → `Dyn` |

Cierre completo: **los seis**. Verificado compilando los 26 copiados más esos seis:

```
32 archivos → compila, 0 errores
```

### Un detalle que ahorra trabajo

`StructureRasterizer` y `LandmarkExtractor` usan de `MatrixReader` **una sola cosa**:
`MatrixReader.DescribeMemberSurface(object)`, seis veces entre los dos. Es un ayudante de
diagnóstico que vuelca el surtido de miembros de un objeto cuando una lectura falla; no tiene
nada que ver con leer matrices.

Si el port extrae ese método a su propia clase, esos dos archivos dejan de depender de las
629 líneas de `MatrixReader` y solo necesitan `Dyn`.

---

## El subconjunto que sí es autosuficiente

Quitando los cuatro que tocan la API —`RegistrationAnalyzer`, `StructureRasterizer`,
`LandmarkExtractor`, `DeformationFieldMetrics`— **los 22 restantes compilan solos**, sin ningún
archivo excluido:

```
Models/*.cs (9) + Properties/AssemblyInfo.cs + Services/ (12):
  SimilarityCalculator, SurfaceMetrics, DistanceTransform, VoxelPairSampler,
  SampledVolume, PointMapper, MetricEvaluator, AdvisoryEngine,
  HtmlReportBuilder, CsvDatasetExporter, DiagnosticLog, MeasurementProgress
                                                        → compila, 0 errores
```

Ese subconjunto es **toda la aritmética, todo el semáforo, todo el veredicto y las dos
exportaciones**. Es lo que `TEST_VECTORS.json` cubre entero, y es donde el port puede empezar sin
saber todavía nada de ESAPI v11.

En **C# 5** ese mismo subconjunto da **un único error**:

```
Services/MetricEvaluator.cs(152,86): error CS1644: Feature `discards' cannot be used
                                     because it is not part of the C# 5.0 language specification
```

Un descarte `out _`. Se arregla declarando una variable y tirándola.

---

## Nota sobre cómo se verificó

Compilar el conjunto copiado y leer los errores **engaña**. `mcs` resuelve primero las
declaraciones y aborta antes de ligar los cuerpos de método, así que el primer intento reportó
solo 14 errores sobre dos tipos (`EsapiImageReader` y `DeformationFieldReader`), que son los que
aparecen en firmas y declaraciones de variable.

`Dyn` (70 usos), `MatrixReader` (7) y `PointMapperReader` (1) **no salieron**, porque solo
aparecen dentro de cuerpos de método que el compilador nunca llegó a procesar. Quien tome esta
carpeta y compile obtendrá esos mismos 14 errores y creerá que faltan dos archivos.

**Faltan seis.**
