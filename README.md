# ESAPI Registration Quantitative Audit (ESAPI_RegistrationQA)

A C# / WPF plugin for the **Varian Eclipse Treatment Planning System** (ESAPI and VMS.IRS
architecture) that automates the quantitative audit of image registrations.

## What it actually measures

This section is deliberately explicit about scope. The plugin produces a report intended to
be signed by a medical physicist, and therefore it **never substitutes a metric it could not
measure with a plausible-looking value**: it marks it *N/A* with the specific reason, visible
both in the interface and in the report.

| Metric | Status | How it is obtained |
|---|---|---|
| **NCC** | ✅ Measured | Pearson correlation over voxel pairs matched by applying the registration transform. Signed, range [-1, 1]. |
| **NMI** | ✅ Measured | Studholme NMI, `(H(A)+H(B))/H(A,B)`, over the actual joint histogram. Bin count adapts to sample size. |
| **SSD** | ✅ Measured | Mean squared difference normalised by the square of the robust range (P1–P99) of the reference image. Dimensionless and comparable across modalities. |
| **Translations and Euler angles** | ✅ Measured | From the registration matrix, with automatic convention detection (translation in row or column), orthonormality verification and explicit gimbal-lock handling. |
| **Max displacement** | ✅ Measured (rigid) | Exact maximum over the eight FOV corners. |
| **Jacobian < 0** | ✅ Exact by definition (rigid) | 0% — a rigid transform has \|J\| = 1 everywhere. |
| **Smoothness** | ✅ Exact by definition (rigid) | 1.0 — the field gradient is constant. |
| **Jacobian, displacement and smoothness** | ❌ **N/A for deformable** | Require traversing the deformation vector field (DVF), which the Varian scripting API does not expose. |
| **DSC** | ❌ **N/A** | Requires rasterising a structure pair matched by identifier onto a common grid. Not implemented. |
| **HD95** | ❌ **N/A** | Same. |

For **deformable** registrations, if the API exposes a point-by-point mapping method
(`TransformPoint` or equivalent), the intensity metrics are computed by traversing the
deformation field. If it does not, they are marked N/A: applying only the linear component
would describe a different transform from the one under audit.

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

What has been verified: the pure mathematics, through 38 analytic checks in
`tools/verify_math.py`. What has not: anything touching the Varian API beyond a single Eclipse
installation, and the tolerance limits, which were inherited before the metrics were
reimplemented and have not been recalibrated against the current definitions.

If you are evaluating the plugin, [VALIDATION.md](VALIDATION.md) has a four-test protocol that
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
