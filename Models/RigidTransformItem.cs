using System;
using System.Globalization;

namespace ESAPI_RegistrationQA.Models
{
    /// <summary>
    /// One row of the rigid parameter table.
    ///
    /// The value is nullable because the transform may not have been readable, and in that
    /// case the row must read "N/A" rather than show a zero that would be mistaken for a
    /// genuine null translation.
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
