using System;
using System.Collections.Generic;

namespace ESAPI_RegistrationQA.Models
{
    public class ThresholdLimits
    {
        public double GreenLimit { get; set; }
        public double YellowLimit { get; set; }
        public bool IsLowerBoundBetter { get; set; } // true: >= es mejor; false: <= es mejor
    }

    public class ThresholdProfile
    {
        public string ProfileName { get; set; }
        public Dictionary<string, ThresholdLimits> Limits { get; set; }

        public ThresholdProfile()
        {
            Limits = new Dictionary<string, ThresholdLimits>();
        }

        // 1. Cabeza y Cuello (ART H&N)
        public static ThresholdProfile CreateDefaultHN()
        {
            var profile = new ThresholdProfile { ProfileName = "ART Head & Neck" };
            profile.Limits.Add("NMI", new ThresholdLimits { GreenLimit = 1.50, YellowLimit = 1.35, IsLowerBoundBetter = true });
            profile.Limits.Add("NCC", new ThresholdLimits { GreenLimit = 0.94, YellowLimit = 0.88, IsLowerBoundBetter = true });
            profile.Limits.Add("SSD", new ThresholdLimits { GreenLimit = 0.025, YellowLimit = 0.040, IsLowerBoundBetter = false });
            profile.Limits.Add("Jacobian_Neg_%", new ThresholdLimits { GreenLimit = 1.0, YellowLimit = 2.5, IsLowerBoundBetter = false });
            profile.Limits.Add("Max_Displacement", new ThresholdLimits { GreenLimit = 15.0, YellowLimit = 22.0, IsLowerBoundBetter = false });
            profile.Limits.Add("Smoothness", new ThresholdLimits { GreenLimit = 0.90, YellowLimit = 0.80, IsLowerBoundBetter = true });

            // Métricas Anatómicas
            profile.Limits.Add("DSC", new ThresholdLimits { GreenLimit = 0.85, YellowLimit = 0.75, IsLowerBoundBetter = true });
            profile.Limits.Add("HD95", new ThresholdLimits { GreenLimit = 3.0, YellowLimit = 5.0, IsLowerBoundBetter = false });
            return profile;
        }

        // 2. Cerebro / Radiocirugía (Brain / SRS) - Tolerancias estrictas
        public static ThresholdProfile CreateBrainSRS()
        {
            var profile = new ThresholdProfile { ProfileName = "Brain / SRS" };
            profile.Limits.Add("NMI", new ThresholdLimits { GreenLimit = 1.60, YellowLimit = 1.45, IsLowerBoundBetter = true });
            profile.Limits.Add("NCC", new ThresholdLimits { GreenLimit = 0.96, YellowLimit = 0.91, IsLowerBoundBetter = true });
            profile.Limits.Add("SSD", new ThresholdLimits { GreenLimit = 0.015, YellowLimit = 0.030, IsLowerBoundBetter = false });
            profile.Limits.Add("Jacobian_Neg_%", new ThresholdLimits { GreenLimit = 0.2, YellowLimit = 1.0, IsLowerBoundBetter = false });
            profile.Limits.Add("Max_Displacement", new ThresholdLimits { GreenLimit = 5.0, YellowLimit = 10.0, IsLowerBoundBetter = false });
            profile.Limits.Add("Smoothness", new ThresholdLimits { GreenLimit = 0.95, YellowLimit = 0.88, IsLowerBoundBetter = true });

            // Métricas Anatómicas (Exigencia milimétrica)
            profile.Limits.Add("DSC", new ThresholdLimits { GreenLimit = 0.90, YellowLimit = 0.82, IsLowerBoundBetter = true });
            profile.Limits.Add("HD95", new ThresholdLimits { GreenLimit = 2.0, YellowLimit = 3.5, IsLowerBoundBetter = false });
            return profile;
        }

        // 3. Pelvis / Próstata
        public static ThresholdProfile CreatePelvis()
        {
            var profile = new ThresholdProfile { ProfileName = "Pelvis / Prostate" };
            profile.Limits.Add("NMI", new ThresholdLimits { GreenLimit = 1.40, YellowLimit = 1.25, IsLowerBoundBetter = true });
            profile.Limits.Add("NCC", new ThresholdLimits { GreenLimit = 0.91, YellowLimit = 0.85, IsLowerBoundBetter = true });
            profile.Limits.Add("SSD", new ThresholdLimits { GreenLimit = 0.030, YellowLimit = 0.050, IsLowerBoundBetter = false });
            profile.Limits.Add("Jacobian_Neg_%", new ThresholdLimits { GreenLimit = 1.5, YellowLimit = 3.0, IsLowerBoundBetter = false });
            profile.Limits.Add("Max_Displacement", new ThresholdLimits { GreenLimit = 20.0, YellowLimit = 30.0, IsLowerBoundBetter = false });
            profile.Limits.Add("Smoothness", new ThresholdLimits { GreenLimit = 0.88, YellowLimit = 0.78, IsLowerBoundBetter = true });

            // Métricas Anatómicas
            profile.Limits.Add("DSC", new ThresholdLimits { GreenLimit = 0.80, YellowLimit = 0.70, IsLowerBoundBetter = true });
            profile.Limits.Add("HD95", new ThresholdLimits { GreenLimit = 4.0, YellowLimit = 6.0, IsLowerBoundBetter = false });
            return profile;
        }

        // 4. Tórax / Pulmón (Thorax / Lung)
        public static ThresholdProfile CreateThorax()
        {
            var profile = new ThresholdProfile { ProfileName = "Thorax / Lung" };
            profile.Limits.Add("NMI", new ThresholdLimits { GreenLimit = 1.35, YellowLimit = 1.20, IsLowerBoundBetter = true });
            profile.Limits.Add("NCC", new ThresholdLimits { GreenLimit = 0.89, YellowLimit = 0.82, IsLowerBoundBetter = true });
            profile.Limits.Add("SSD", new ThresholdLimits { GreenLimit = 0.035, YellowLimit = 0.055, IsLowerBoundBetter = false });
            profile.Limits.Add("Jacobian_Neg_%", new ThresholdLimits { GreenLimit = 2.0, YellowLimit = 4.0, IsLowerBoundBetter = false });
            profile.Limits.Add("Max_Displacement", new ThresholdLimits { GreenLimit = 25.0, YellowLimit = 35.0, IsLowerBoundBetter = false });
            profile.Limits.Add("Smoothness", new ThresholdLimits { GreenLimit = 0.85, YellowLimit = 0.75, IsLowerBoundBetter = true });

            // Métricas Anatómicas
            profile.Limits.Add("DSC", new ThresholdLimits { GreenLimit = 0.80, YellowLimit = 0.70, IsLowerBoundBetter = true });
            profile.Limits.Add("HD95", new ThresholdLimits { GreenLimit = 4.5, YellowLimit = 6.5, IsLowerBoundBetter = false });
            return profile;
        }

        public static List<ThresholdProfile> GetAllProfiles()
        {
            return new List<ThresholdProfile>
            {
                CreateDefaultHN(),
                CreateBrainSRS(),
                CreatePelvis(),
                CreateThorax()
            };
        }
    }
}