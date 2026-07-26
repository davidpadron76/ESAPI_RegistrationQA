# ESAPI Registration Quantitative Audit (ESAPI_RegistrationQA)

Professional C#.NET / WPF plugin script developed for the **Varian Eclipse Treatment Planning System** (ESAPI and VMS.IRS architecture) to automate the quantitative audit and quality assurance of image registrations.

## 🌟 Key Features
* **Multi-Registration Support:** Automatically detects and parses Rigid, Deformable (DIR), and Identity registrations (`MIRSRegistration`) from `VMS.IRS`.
* **Intensity Similarity Metrics:** Evaluates Normalized Mutual Information (NMI), Normalized Cross Correlation (NCC), and Sum of Squared Differences (SSD) directly from image voxel buffers.
* **Topological & Biomechanical Analysis:** Computes Jacobian determinant folding ($|J| \le 0$), maximum displacement vectors, and vector field smoothness (AAPM TG-233 compliance).
* **Anatomical Structure QA:** Automatically evaluates volumetric overlap (Dice Similarity Coefficient - $DSC$) and surface distance (95% Hausdorff Distance - $HD95$) from active patient `StructureSets`.
* **Site-Specific Clinical Profiles:** Customizable anatomical threshold profiles (ART Head & Neck, Brain/SRS, Pelvis/Prostate, Thorax/Lung) with real-time dynamic threshold evaluation.
* **Clinical Advisories Engine:** Automated rule-based system generating AAPM guideline warnings and clinical recommendations.
* **Professional Reporting:** Export audit findings into a clean, print-ready A4 HTML technical report complete with status badges and signature lines.

## 🛠️ Requirements
* Varian Eclipse TPS (v15.5 / v16.1 / v18.0 compatible)
* .NET Framework 4.8
* Eclipse Automation & ESAPI Research/Clinical license

## 🚀 Installation & Usage
1. Compile the solution using Visual Studio (x64 platform target).
2. Place the compiled assembly or script into your application's scripts directory (or System Scripts).
3. Launch from **Contouring / Registration > Tools > Scripts**.

## 📄 License

This project is open-source and available under the [MIT License](LICENSE).
