# ESAPI Registration Quantitative Audit (ESAPI_RegistrationQA)

Plugin C# / WPF para el **Varian Eclipse Treatment Planning System** (arquitectura ESAPI y
VMS.IRS) que automatiza la auditoría cuantitativa de registros de imagen.

## Qué mide realmente

Esta sección es deliberadamente explícita sobre el alcance. El plugin genera un reporte
destinado a ser firmado por un físico médico, y por tanto **nunca sustituye una métrica que
no pudo medir por un valor plausible**: la marca como *N/A* con el motivo concreto, visible
tanto en la interfaz como en el reporte.

| Métrica | Estado | Cómo se obtiene |
|---|---|---|
| **NCC** | ✅ Medida | Correlación de Pearson sobre pares de vóxeles emparejados aplicando la transformación del registro. Con signo, rango [-1, 1]. |
| **NMI** | ✅ Medida | NMI de Studholme, `(H(A)+H(B))/H(A,B)`, sobre el histograma conjunto real. El número de bins se adapta al tamaño de la muestra. |
| **SSD** | ✅ Medida | Diferencia cuadrática media normalizada por el cuadrado del rango robusto (P1–P99) de la imagen de referencia. Adimensional y comparable entre modalidades. |
| **Traslaciones y ángulos de Euler** | ✅ Medidos | De la matriz del registro, con detección automática de la convención (traslación en fila o en columna), verificación de ortonormalidad y tratamiento explícito del bloqueo de cardán. |
| **Desplazamiento máximo** | ✅ Medido (registro rígido) | Máximo exacto sobre los ocho vértices del FOV. |
| **Jacobiano < 0** | ✅ Exacto por definición (registro rígido) | 0 % — una transformación rígida tiene \|J\| = 1 en todo punto. |
| **Suavidad** | ✅ Exacta por definición (registro rígido) | 1.0 — el gradiente del campo es constante. |
| **Jacobiano, desplazamiento y suavidad** | ❌ **N/A en registros deformables** | Requieren recorrer el campo de vectores de deformación (DVF), que la API de scripting de Varian no expone. |
| **DSC** | ❌ **N/A** | Requiere rasterizar un par de contornos emparejado por identificador sobre una rejilla común. No implementado. |
| **HD95** | ❌ **N/A** | Ídem. |

En registros **deformables**, si la API expone un método de mapeo punto a punto
(`TransformPoint` o equivalente), las métricas de intensidad se calculan atravesando el
campo de deformación. Si no lo expone, se marcan como N/A: aplicar sólo la componente
lineal describiría una transformación distinta de la que se está auditando.

## Características

* **Emparejamiento espacial correcto:** los vóxeles se comparan tras llevarlos a coordenadas
  de paciente y aplicar la transformación, con interpolación trilineal. Origen, espaciado y
  cosenos directores se respetan, de modo que un CT de planificación y un CBCT con distinto
  FOV se comparan de forma válida.
* **Escalado a HU:** la rampa vóxel→display se determina sondeando la API y verificando su
  linealidad, en lugar de asumir un rango fijo.
* **Perfiles anatómicos:** ART Head & Neck, Brain/SRS, Pelvis/Prostate y Thorax/Lung.
  Cambiar de perfil sólo reclasifica los valores ya medidos; no vuelve a leer la imagen.
* **Motor de avisos ligado al perfil:** todos los umbrales de los avisos salen del perfil
  activo, de modo que la tabla y las recomendaciones no pueden contradecirse.
* **Diagnóstico visible:** cada propiedad de la API que no se pudo leer queda registrada con
  la operación y la excepción concretas, en una pestaña propia y en el reporte.
* **Reporte HTML A4:** con escape HTML, formato numérico en cultura invariante, sección de
  procedencia del dato y versión del ensamblado que lo generó.

## Requisitos

* Varian Eclipse TPS (v15.5 / v16.1 / v18.0)
* .NET Framework 4.8
* Licencia de scripting ESAPI (investigación o clínica)

## Compilación

Los ensamblados de Varian se localizan mediante la propiedad `VarianScriptingPath`, cuyo
valor por defecto es
`C:\Program Files (x86)\Varian\ProductLine\Workspaces\VMS.IRS.Workspace`.

Para una ruta distinta, cualquiera de estas tres opciones:

```powershell
# variable de entorno
$env:VarianScriptingPath = "D:\Program Files (x86)\Varian\...\VMS.IRS.Workspace"

# o por línea de comandos
msbuild ESAPI_RegistrationQA.csproj /p:VarianScriptingPath="D:\..."
```

O bien un `Directory.Build.props` junto a la solución (conviene no versionarlo):

```xml
<Project>
  <PropertyGroup>
    <VarianScriptingPath>D:\Program Files (x86)\Varian\...\VMS.IRS.Workspace</VarianScriptingPath>
  </PropertyGroup>
</Project>
```

El proyecto compila como **x64**, que es lo que requiere Eclipse 15.6 y posteriores.

## Uso

1. Compilar en Release.
2. Copiar el ensamblado al directorio de scripts de la aplicación (o a System Scripts).
3. Lanzar desde **Contouring / Registration → Tools → Scripts**.

## Interpretación del veredicto

El estado global distingue cinco situaciones, y nunca declara un registro verificado si
quedaron métricas sin evaluar:

| Veredicto | Significado |
|---|---|
| **CONFORME** | Todas las métricas se midieron y todas cumplen el perfil. |
| **CONFORME PARCIAL** | Lo medido cumple, pero hubo métricas que no se pudieron medir. La verificación no es completa. |
| **REVISIÓN REQUERIDA** | Alguna métrica cayó en zona de atención (amarillo). |
| **NO CONFORME** | Alguna métrica incumple el criterio del perfil (rojo). |
| **SIN EVIDENCIA** | No se pudo evaluar ninguna métrica. Consulte la pestaña de diagnóstico. |

## Limitaciones conocidas

* DSC y HD95 no están implementados (ver tabla de alcance).
* Las métricas topológicas de registros deformables dependen del DVF, no accesible desde la
  API de scripting.
* El cálculo se ejecuta de forma síncrona en el hilo de interfaz. El muestreo está acotado a
  ~2·10⁶ pares de vóxeles para mantener el tiempo de respuesta, y la resolución efectiva
  resultante se reporta junto a las métricas.
* La similitud se calcula sobre volúmenes submuestreados; el reporte indica la resolución
  efectiva empleada.

## Licencia

[MIT](LICENSE).
