# tools/

## `verify_math.py`

Porta a Python los algoritmos numéricos de `RigidTransform`, `ImageGeometry` y
`SimilarityCalculator` y los contrasta contra resultados analíticos conocidos.

Existe porque el proyecto sólo compila en Windows con las DLL de Varian instaladas, de modo
que la lógica de cálculo no puede ejecutarse en un entorno de integración continua ordinario.
Estos algoritmos son puros —no dependen de ESAPI ni de WPF—, así que su corrección sí puede
verificarse de forma independiente.

```bash
python3 tools/verify_math.py
```

Cubre, entre otras cosas:

* recuperación de los ángulos de Euler en 2000 rotaciones aleatorias;
* comportamiento bajo bloqueo de cardán, incluida la reconstrucción de la rotación;
* detección de la convención fila/columna de la matriz 4x4, y por qué la ortonormalidad
  no basta para discriminarla;
* exactitud del máximo desplazamiento evaluado en los vértices del FOV, y su convexidad;
* ida y vuelta vóxel ↔ paciente en geometría oblicua;
* NCC, NMI y SSD contra sus valores teóricos (incluido el NMI de una gaussiana bivariante).

**Si se modifica alguno de esos algoritmos en C#, hay que reflejar el cambio aquí y volver
a ejecutarlo.** El script no se compila con el plugin ni se distribuye con él.
