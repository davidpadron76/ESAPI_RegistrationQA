using System;
using System.Collections.Generic;

namespace ESAPI_RegistrationQA.Models
{
    public enum RegistrationType { Rigid, NonRigid, Identity }
    public enum ImageModality { CT, MR, PT, Unknown }
    public enum AnatomicRegion { WholeBody, HN, Thorax, AbdoPelvis, Brain }

    public class RegistrationContext
    {
        public RegistrationType RegType { get; set; }
        public ImageModality ModalityFixed { get; set; }
        public ImageModality ModalityMoving { get; set; }
        public AnatomicRegion Region { get; set; }

        public string ModalityPair => $"{ModalityFixed}_{ModalityMoving}";
    }
}