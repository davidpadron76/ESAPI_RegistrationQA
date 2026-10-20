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
        /// true when both modalities are known and different. This determines which
        /// intensity metric is the primary one: NMI for multimodal (intensities are not
        /// linearly comparable), NCC for monomodal.
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

        /// <summary>Key of the reference intensity metric for this modality pair.</summary>
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
