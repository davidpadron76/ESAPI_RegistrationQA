using System;
using System.Collections.Generic;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Claves estables de métrica. Son el identificador canónico usado por los perfiles de
    /// tolerancia, el motor de avisos y el reporte. NUNCA deben derivarse del texto mostrado
    /// en pantalla: renombrar una etiqueta de la UI no debe alterar la lógica de evaluación.
    /// </summary>
    public static class MetricKeys
    {
        public const string Nmi = "NMI";
        public const string Ncc = "NCC";
        public const string Ssd = "SSD";
        public const string JacobianNegative = "Jacobian_Neg_%";
        public const string MaxDisplacement = "Max_Displacement";
        public const string Smoothness = "Smoothness";
        public const string Dsc = "DSC";
        public const string Hd95 = "HD95";
    }

    /// <summary>
    /// Metadatos de presentación y semántica de una métrica. Centraliza la unidad y la
    /// dirección de bondad, que antes se infería con comparaciones de subcadena sobre el
    /// nombre visible.
    /// </summary>
    public sealed class MetricDefinition
    {
        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public string Unit { get; private set; }

        /// <summary>true si un valor mayor es mejor (NMI, NCC, DSC, Smoothness).</summary>
        public bool HigherIsBetter { get; private set; }

        public string Description { get; private set; }

        public MetricDefinition(string key, string displayName, string unit, bool higherIsBetter, string description)
        {
            Key = key;
            DisplayName = displayName;
            Unit = unit ?? string.Empty;
            HigherIsBetter = higherIsBetter;
            Description = description;
        }
    }

    public static class MetricCatalog
    {
        private static readonly Dictionary<string, MetricDefinition> _byKey =
            new Dictionary<string, MetricDefinition>(StringComparer.Ordinal);

        static MetricCatalog()
        {
            Register(new MetricDefinition(
                MetricKeys.Nmi, "NMI", string.Empty, true,
                "Normalized Mutual Information (Studholme): (H(A)+H(B))/H(A,B) calculada sobre el " +
                "histograma conjunto de las intensidades emparejadas tras aplicar la transformación. " +
                "Rango [1,2]; 1 indica independencia estadística. Es la métrica de referencia para " +
                "registros multimodales (CT-MR, CT-PET)."));

            Register(new MetricDefinition(
                MetricKeys.Ncc, "NCC", string.Empty, true,
                "Normalized Cross Correlation (Pearson) entre las intensidades emparejadas. " +
                "Rango [-1,1]. Es la métrica de referencia para registros monomodales (CT-CT, CT-CBCT). " +
                "Valores negativos indican contraste invertido."));

            Register(new MetricDefinition(
                MetricKeys.Ssd, "SSD", string.Empty, false,
                "Suma de diferencias cuadráticas normalizada: media((a-b)^2) dividida por el cuadrado " +
                "del rango robusto (P1-P99) de la imagen fija. Adimensional y comparable entre " +
                "modalidades. Sensible a diferencias de contraste y a errores geométricos locales."));

            Register(new MetricDefinition(
                MetricKeys.JacobianNegative, "Jacobian < 0", "%", false,
                "Porcentaje de vóxeles del campo de deformación con determinante jacobiano no positivo, " +
                "es decir, plegamiento topológico no físico. Para una transformación rígida es 0 por " +
                "definición (|J| = 1 en todo punto)."));

            Register(new MetricDefinition(
                MetricKeys.MaxDisplacement, "Max Displacement", "mm", false,
                "Máximo desplazamiento espacial aplicado a un punto dentro del campo de visión. " +
                "Para una transformación rígida se evalúa de forma exacta sobre los ocho vértices del " +
                "volumen, ya que el módulo del desplazamiento de una aplicación afín es convexo y " +
                "alcanza su máximo en un vértice."));

            Register(new MetricDefinition(
                MetricKeys.Smoothness, "Smoothness", string.Empty, true,
                "Regularidad del campo de vectores de deformación. Para una transformación rígida es " +
                "1.0 por definición: el gradiente del campo es constante."));

            Register(new MetricDefinition(
                MetricKeys.Dsc, "DSC (Dice Overlap)", string.Empty, true,
                "Coeficiente de similitud de Dice: 2|A∩B| / (|A|+|B|) entre un contorno de referencia y " +
                "el mismo contorno propagado. Requiere un par de structure sets emparejado por " +
                "identificador de estructura."));

            Register(new MetricDefinition(
                MetricKeys.Hd95, "HD95 (Hausdorff)", "mm", false,
                "Distancia de Hausdorff al percentil 95 entre las superficies de referencia y propagada. " +
                "Excluye el 5 % de puntos más desviados. Requiere el mismo par emparejado que el DSC."));
        }

        private static void Register(MetricDefinition definition)
        {
            _byKey[definition.Key] = definition;
        }

        public static bool TryGet(string key, out MetricDefinition definition)
        {
            if (key == null)
            {
                definition = null;
                return false;
            }
            return _byKey.TryGetValue(key, out definition);
        }

        public static MetricDefinition Get(string key)
        {
            MetricDefinition definition;
            if (TryGet(key, out definition)) return definition;
            throw new ArgumentException("Métrica desconocida: " + key, "key");
        }

        public static string DisplayName(string key)
        {
            MetricDefinition definition;
            return TryGet(key, out definition) ? definition.DisplayName : key;
        }

        public static string Unit(string key)
        {
            MetricDefinition definition;
            return TryGet(key, out definition) ? definition.Unit : string.Empty;
        }

        public static bool HigherIsBetter(string key)
        {
            MetricDefinition definition;
            return TryGet(key, out definition) && definition.HigherIsBetter;
        }
    }
}
