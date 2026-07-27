# ESAPI Registration Quantitative Audit (ESAPI_RegistrationQA)

A C# / WPF plugin for the **Varian Eclipse Treatment Planning System** (ESAPI and VMS.IRS
architecture) that automates the quantitative audit of image registrations.

## Reference

Brock KK, Mutic S, McNutt TR, Li H, Kessler ML. *Use of image registration and fusion
algorithms and techniques in radiotherapy: Report of the AAPM Radiation Therapy Committee
Task Group No. 132.* Med Phys. 2017;44(7):e43–e76. [doi:10.1002/mp.12256](https://doi.org/10.1002/mp.12256)

Earlier versions of this project cited AAPM TG-233. That was an error: TG-233 covers
performance evaluation of CT systems and is unrelated to image registration.

## Why each metric is here

A metric earns its place by the clinical decision it supports. Appearing in TG-132 Table III
is evidence for that, not the criterion itself — and the converse holds too: several metrics
the report does not tabulate are useful for interpreting a co-registration, and TG-132 §4.C.3
explicitly admits some of them for assessment.

Each definition carries its justification in the code (`Models/MetricCatalog.cs`), and the
constructor refuses a metric that leaves it blank. The same table is printed as an appendix
to every exported report, so whoever countersigns the document can see the basis for each
number.

| Metric | Question it answers | Relation to TG-132 | Status |
|---|---|---|---|
| **NCC** | Does the anatomy line up, same modality? | §4.C.3, admitted for assessment | Measured |
| **NMI** | Does it line up when intensities are not linearly comparable (CT-MR, CT-PET)? | §4.C.3, admitted for assessment | Measured |
| **SSD** | Are there local intensity differences beyond the overall alignment? | §4.C.3, admitted for assessment | Measured |
| **Jacobian < 0** | Has the deformation folded tissue onto itself? | Table III | Exact for rigid · N/A for DIR |
| **Max Displacement** | How far has the registration moved the anatomy? | Not tabulated; plausibility check | Exact for rigid · N/A for DIR |
| **Smoothness** | Is the deformation physically plausible? | Not tabulated; related to §4.C.3 | Exact for rigid · N/A for DIR |
| **DSC** | Do the same organs end up in the same place? | Table III, ~0.80–0.90 | Not implemented |
| **HD95** | How far apart are the surfaces where they disagree most? | Not tabulated; report specifies MDA | Not implemented |
| **TRE (mean)** | How large is the spatial error, in millimetres? | Table III, primary metric | Measured |
| **TRE (max)** | Is any single landmark badly placed? | Table III; mean/max split is ours | Measured |
| **Inverse consistency** | Is the algorithm behaving stably? | §4.C.4 and Table III | Measured |

Two points the report makes that shape how the first three should be read. TG-132 §4.C.3
admits SSD, CC and MI for assessment **provided the metric was not the one the registration
algorithm optimised** — otherwise the assessment is circular — and notes they are **difficult
to convert into a measure of spatial accuracy**. A compliant NCC does not establish
millimetric accuracy; TRE is the metric that does. The plugin raises an advisory saying so on
every case where those metrics are available.

Where the tool departs from the report, deliberately:

- **HD95 instead of MDA.** Table III specifies Mean Distance to Agreement. HD95 is what the
  segmentation literature reports, which keeps local results comparable with published
  series. MDA is worth adding alongside it rather than in its place.
- **Site-specific threshold profiles.** TG-132 ties its tolerances to voxel dimension and
  contouring uncertainty rather than to anatomical site. The DSC range comes from Table III;
  the rest are inherited values pending calibration against real data. See
  [VALIDATION.md](VALIDATION.md).

## What it actually measures

The plugin produces a report intended to be signed by a medical physicist, so it **never
substitutes a metric it could not measure with a plausible-looking value**: it marks it *N/A*
with the specific reason, visible in the interface and in the report.

| Metric | Status | How it is obtained |
|---|---|---|
| **NCC** | ✅ Measured | Pearson correlation over voxel pairs matched by applying the registration transform. Signed, range [-1, 1]. |
| **NMI** | ✅ Measured | Studholme NMI, `(H(A)+H(B))/H(A,B)`, over the actual joint histogram. Bin count adapts to sample size. |
| **SSD** | ✅ Measured | Mean squared difference normalised by the square of the robust range (P1–P99) of the reference image. |
| **Translations and Euler angles** | ✅ Measured | From the registration matrix, with automatic convention detection, orthonormality verification and explicit gimbal-lock handling. |
| **TRE (mean and max)** | ✅ Measured | Matched point landmarks pushed through the registration. Needs MARKER or ISOCENTER structures with the same identifier on both series. |
| **Inverse consistency** | ✅ Measured | Forward then reverse mapping over a grid across the field of view. Needs the reverse registration to exist in the workspace. |
| **Max displacement** | ✅ Measured (rigid) | Exact maximum over the eight FOV corners. |
| **Jacobian < 0** | ✅ Exact by definition (rigid) | 0% — a rigid transform has \|J\| = 1 everywhere. |
| **Smoothness** | ✅ Exact by definition (rigid) | 1.0 — the field gradient is constant. |
| **Jacobian, displacement and smoothness** | ❌ **N/A for deformable** | Require traversing the deformation vector field, which the Varian scripting API does not expose. |
| **DSC / HD95** | ❌ **N/A** | Require rasterising a structure pair matched by identifier onto a common grid. Not implemented. |

For **deformable** registrations, if the API exposes a point-by-point mapping method, the
intensity metrics and the TRE are computed by traversing the deformation field. If it does
not, they are marked N/A: applying only the linear component would describe a different
transform from the one under audit.

## Features

* **Correct spatial matching:** voxels are compared after conversion to patient coordinates
  and application of the transform, with trilinear interpolation. Origin, spacing and
  direction cosines are honoured, so a planning CT and a CBCT with different FOV are compared
  meaningfully.
* **HU scaling:** the voxel→display ramp is determined by probing the API and verifying its
  linearity, rather than assuming a fixed range.
* **Anatomical profiles:** ART Head & Neck, Brain/SRS, Pelvis/Prostate and Thorax/Lung.
  Changing profile only reclassifies values already measured; it does not re-read the image.
* **Profile-driven advisory engine:** every advisory threshold comes from the active profile,
  so the table and the recommendations cannot contradict each other.
* **Visible diagnostics:** every API property that could not be read is recorded with the
  specific operation and exception, in a dedicated tab and in the report.
* **A4 HTML report:** with HTML escaping, invariant-culture number formatting, a data
  provenance section, and the assembly version that generated it.
* **Cumulative CSV dataset:** one row per audited registration, appended to a file of your
  choosing, carrying the measurements plus their provenance (voxel pairs, overlap, effective
  sampling). No patient identifier is written. Intended for building a local baseline
  distribution and, pooled across centres, for deriving tolerance limits from practice
  instead of from inherited values.

## Validation status

The numbers are measurements; the semaphore colours are provisional.

What has been verified: the pure mathematics, through 52 analytic checks in
`tools/verify_math.py` — Euler extraction, matrix convention detection, voxel↔patient
round-trips, the similarity metrics against their theoretical values, transform composition,
and TRE against known landmark displacements.

What has not: anything touching the Varian API beyond a single Eclipse installation. TRE and
inverse consistency in particular have never run against real data. And the tolerance limits
were inherited before the metrics were reimplemented, so they have not been recalibrated
against the current definitions — except the DSC range, which comes from Table III.

If you are evaluating the plugin, [VALIDATION.md](VALIDATION.md) has a test protocol that
closes the open questions in an afternoon, plus a method for building a local baseline that is
useful today despite the uncalibrated thresholds.

## Requirements

* Varian Eclipse TPS (v15.5 / v16.1 / v18.0)
* .NET Framework 4.8
* ESAPI scripting licence (research or clinical)

## Building

The Varian assemblies are located through the `VarianScriptingPath` property, which defaults
to `C:\Program Files (x86)\Varian\ProductLine\Workspaces\VMS.IRS.Workspace`.

For a different path, use any of these three options:

```powershell
# environment variable
$env:VarianScriptingPath = "D:\Program Files (x86)\Varian\...\VMS.IRS.Workspace"

# or on the command line
msbuild ESAPI_RegistrationQA.csproj /p:VarianScriptingPath="D:\..."
```

Or a `Directory.Build.props` next to the solution (best left unversioned):

```xml
<Project>
  <PropertyGroup>
    <VarianScriptingPath>D:\Program Files (x86)\Varian\...\VMS.IRS.Workspace</VarianScriptingPath>
  </PropertyGroup>
</Project>
```

The project builds as **x64**, which is what Eclipse 15.6 and later require.

## Usage

1. Build in Release.
2. Copy the assembly to the application's scripts directory (or to System Scripts).
3. Launch from **Contouring / Registration → Tools → Scripts**.

## Reading the verdict

The overall status distinguishes five situations, and never declares a registration verified
while metrics remain unevaluated:

| Verdict | Meaning |
|---|---|
| **COMPLIANT** | Every metric was measured and every one meets the profile. |
| **PARTIALLY COMPLIANT** | What was measured passes, but some metrics could not be measured. Verification is not complete. |
| **REVIEW REQUIRED** | A metric fell into the attention zone (yellow). |
| **NOT COMPLIANT** | A metric breaches the profile criterion (red). |
| **NO EVIDENCE** | No metric could be evaluated. See the diagnostics tab. |

## Known limitations

* DSC and HD95 are not implemented (see the scope table).
* Topological metrics for deformable registrations depend on the DVF, which is not
  accessible from the scripting API.
* Computation runs synchronously on the UI thread. Sampling is capped at ~2·10⁶ voxel pairs
  to keep the interface responsive, and the resulting effective resolution is reported
  alongside the metrics.
* Similarity is computed on subsampled volumes; the report states the effective resolution
  used.

## Licence

[MIT](LICENSE).
