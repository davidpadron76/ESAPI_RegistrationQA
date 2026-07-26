using System;
using System.Globalization;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// Una fila de la tabla de parámetros rígidos.
    ///
    /// El valor es anulable porque la transformación puede no haberse podido leer, y en ese
    /// caso la fila debe decir "N/A" en lugar de mostrar un cero que se confundiría con una
    /// traslación nula real.
    /// </summary>
    public sealed class RigidTransformItem
    {
        public string Parameter { get; set; }
        public double? Value { get; set; }
        public string Unit { get; set; }
        public string Note { get; set; }

        public string DisplayValue
        {
            get
            {
                return Value.HasValue
                    ? Value.Value.ToString("F3", CultureInfo.InvariantCulture)
                    : "N/A";
            }
        }

        public string Tooltip
        {
            get { return string.IsNullOrEmpty(Note) ? Parameter : Parameter + Environment.NewLine + Note; }
        }
    }
}
