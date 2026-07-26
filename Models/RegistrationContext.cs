using System;

namespace ESAPI_RegistrationQA.Models
{
    public enum RegistrationType { Rigid, NonRigid, Identity, Unknown }
    public enum ImageModality { CT, MR, PT, NM, US, CBCT, Unknown }
    public enum AnatomicRegion { WholeBody, HN, Thorax, AbdoPelvis, Brain }

    public sealed class RegistrationContext
    {
        public RegistrationType RegType { get; set; }
        public ImageModality ModalityFixed { get; set; }
        public ImageModality ModalityMoving { get; set; }
        public AnatomicRegion Region { get; set; }

        public RegistrationContext()
        {
            RegType = RegistrationType.Unknown;
            ModalityFixed = ImageModality.Unknown;
            ModalityMoving = ImageModality.Unknown;
        }

        public string ModalityPair
        {
            get { return ModalityFixed + " → " + ModalityMoving; }
        }

        /// <summary>
        /// true si ambas modalidades son conocidas y distintas. Determina qué métrica de
        /// intensidad es la primaria: NMI para multimodal (las intensidades no son
        /// linealmente comparables), NCC para monomodal.
        /// </summary>
        public bool IsMultimodal
        {
            get
            {
                return ModalityFixed != ImageModality.Unknown
                    && ModalityMoving != ImageModality.Unknown
                    && ModalityFixed != ModalityMoving;
            }
        }

        /// <summary>Clave de la métrica de intensidad de referencia para este par de modalidades.</summary>
        public string PrimaryIntensityMetricKey
        {
            get { return IsMultimodal ? MetricKeys.Nmi : MetricKeys.Ncc; }
        }

        public static ImageModality ParseModality(string dicomModality)
        {
            if (string.IsNullOrWhiteSpace(dicomModality)) return ImageModality.Unknown;

            switch (dicomModality.Trim().ToUpperInvariant())
            {
                case "CT": return ImageModality.CT;
                case "MR": return ImageModality.MR;
                case "PT": return ImageModality.PT;
                case "NM": return ImageModality.NM;
                case "US": return ImageModality.US;
                default: return ImageModality.Unknown;
            }
        }
    }
}
