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

| Metric | Question it answers | Relation to TG-132 | Drives the verdict? | Status |
|---|---|---|---|---|
| **NCC** | Does the anatomy line up, same modality? | §4.C.3, admitted for assessment | **No** — no tolerance exists | Measured |
| **NMI** | Does it line up when intensities are not linearly comparable (CT-MR, CT-PET)? | §4.C.3, admitted for assessment | **No** — no tolerance exists | Measured |
| **SSD** | Are there local intensity differences beyond the overall alignment? | §4.C.3, admitted for assessment | **No** — no tolerance exists | Measured |
| **Jacobian < 0** | Has the deformation folded tissue onto itself? | Table III, no negative values | Yes, at 0 % in every profile | Exact for rigid · N/A for DIR |
| **Max Displacement** | How far has the registration moved the anatomy? | Not tabulated; plausibility check | Only within one frame of reference | Exact for rigid · N/A for DIR |
| **Smoothness** | Is the deformation physically plausible? | Not tabulated; related to §4.C.3 | **No** — limits were invented | Exact for rigid · N/A for DIR |
| **DSC** | Do the same organs end up in the same place? | Table III, ~0.80–0.90 | Yes | Measured |
| **MDA** | On average, how far apart are the two organ surfaces? | Table III, ~2–3 mm | Yes | Measured |
| **HD95** | How far apart are the surfaces where they disagree most? | Not tabulated; report specifies MDA | Yes | Measured |
| **TRE (mean)** | How large is the spatial error, in millimetres? | Table III, primary metric | Yes | Measured |
| **TRE (max)** | Is any single landmark badly placed? | Table III; mean/max split is ours | Yes | Measured |
| **Inverse consistency** | Is the algorithm behaving stably? | §4.C.4 and Table III | Yes | Measured |

Two points the report makes that shape how the first three should be read. TG-132 §4.C.3
admits SSD, CC and MI for assessment **provided the metric was not the one the registration
algorithm optimised** — otherwise the assessment is circular — and notes they are **difficult
to convert into a measure of spatial accuracy**. A compliant NCC does not establish
millimetric accuracy; TRE is the metric that does. The plugin raises an advisory saying so on
every case where those metrics are available.

## Which metrics can fail a registration

Measuring a quantity and being entitled to fail a registration on it are different claims, and
the tool used to treat them as one.

**TG-132 gives no tolerance for NCC, NMI or SSD.** They appear in Table I, which catalogues the
metrics that *drive* a registration. As assessment tools they appear once, in §4.C.3, and in
passing. Table III has five rows and none of them is an intensity metric: TRE, MDA, DSC,
Jacobian determinant and consistency. The limits this project used to apply to the three
intensity metrics were its own invention, and they were producing NOT COMPLIANT verdicts on a
class of metric the report says cannot be converted into spatial accuracy. They are now
reported as **INFO**: value shown, no colour, no effect on the verdict, still written to the
CSV — which is where they earn their keep, as a local baseline distribution.

**Maximum displacement is classified only when both series share a DICOM frame of reference.**
Within one frame the identity is the "no correction" state, so the displacement is the
correction the registration applies. Across two frames — two scanners, or a CT and an MR — the
matrix must also span the offset between the two coordinate systems, and a correct registration
can exceed any limit for that reason alone. When the frames differ, or when either UID cannot
be read, the value is reported without a classification and an advisory says why.

**Smoothness follows the intensity metrics, for the same reason.** TG-132 does not name it and
its limits were invented too. It stays on screen because 1.0 for a rigid transform is a true
statement worth recording — the field gradient is constant, so no local irregularity is
possible — but a statement true by definition cannot fail anything.

**The Jacobian limit is now 0 % in all four profiles.** Table III admits no negative values;
the profiles used to admit between 1 % and 4 %, so the tool was more permissive than the
standard it cites, on its own authority. The limit is deliberately not varied by anatomical
site: the report ties this tolerance to the physics, and a folded voxel is as unphysical in a
lung as in a brain. Where the folding is confined to a region that does not affect the intended
use, TG-132 asks for that influence to be evaluated — that judgement is the physicist's, and
the tool does not pre-empt it by relaxing the limit.

**A value that is true by definition does not count as verification.** On a rigid transform the
Jacobian is 0 % and the smoothness 1.0 — not because this registration is good, but because
every rigid transform in existence gives those numbers. They are still shown, since a signed
report should record what the transform guarantees, but they are excluded from the verdict's
tally of evaluated metrics. Left in, they produced a `PARTIALLY COMPLIANT` on a case where
neither image would load at all, on the strength of "the 1 evaluated metric meets the profile".

Each metric declares its position in `Models/MetricCatalog.cs`, in a `GatingBasis` field the
constructor refuses to leave blank, and the same text is printed in the report appendix.

Where the tool departs from the report, deliberately:

- **HD95 alongside MDA.** Only MDA is in Table III. HD95 is kept as well because it is what
  the segmentation literature reports, which keeps local results comparable with published
  series. Read together they separate a uniform offset from a local failure: on two
  concentric surfaces the two agree, and they diverge as the disagreement becomes uneven.
- **Site-specific threshold profiles.** TG-132 ties its tolerances to voxel dimension and
  contouring uncertainty rather than to anatomical site. The DSC range comes from Table III;
  the rest are inherited values pending calibration against real data. See
  [VALIDATION.md](VALIDATION.md).

## What it actually measures

The plugin produces a report intended to be signed by a medical physicist, so it **never
substitutes a metric it could not measure with a plausible-looking value**.

There are two ways a metric can have no number, and they are treated differently:

- **It does not apply to this case** — a deformation metric on a rigid registration, TRE with
  no landmarks placed. The row is **not shown at all**: it would say the same thing on every
  case, and a table full of N/A in a signed document suggests missing data when nothing is
  missing. The omission is accounted for in the diagnostics tab and in a dedicated section of
  the report, with what would be needed to obtain it.
- **It was attempted and failed** — the volume would not load, the joint histogram could not
  be built. This one **stays visible as N/A** with its reason, because it points at something
  to fix and hiding it would bury the fault.

| Metric | Status | How it is obtained |
|---|---|---|
| **NCC** | ✅ Measured | Pearson correlation over voxel pairs matched by applying the registration transform. Signed, range [-1, 1]. |
| **NMI** | ✅ Measured | Studholme NMI, `(H(A)+H(B))/H(A,B)`, over the actual joint histogram. Bin count adapts to sample size. |
| **SSD** | ✅ Measured | Mean squared difference normalised by the square of the robust range (P1–P99) of the reference image. |
| **Translations and Euler angles** | ✅ Measured | From the registration matrix, with automatic convention detection, orthonormality verification and explicit gimbal-lock handling. If the matrix cannot be read there is no substitute: everything derived from it is reported as unavailable. |
| **TRE (mean and max)** | ✅ Measured | Matched point landmarks pushed through the registration. Needs MARKER or ISOCENTER structures with the same identifier on both series. |
| **Inverse consistency** | ✅ Measured | Forward then reverse mapping over a grid across the field of view. Needs the reverse registration to exist in the workspace. |
| **Max displacement** | ✅ Measured (rigid) | Exact maximum over the eight FOV corners. |
| **Jacobian < 0** | ✅ Exact by definition (rigid) | 0% — a rigid transform has \|J\| = 1 everywhere. |
| **Smoothness** | ✅ Exact by definition (rigid) | 1.0 — the field gradient is constant. |
| **Jacobian, displacement and smoothness** | ❌ **N/A for deformable** | Require traversing the deformation vector field, which the Varian scripting API does not expose. |
| **DSC, MDA and HD95** | ✅ Measured | Both structures rasterised onto one grid — the registered one carried through the registration first — then compared through a single distance transform. Needs contours with the same identifier on both series. |

For **deformable** registrations everything depends on finding a point-by-point mapping method
on the registration object: the intensity metrics need it to pair voxels, DSC/MDA/HD95 to carry
a contour across, TRE for the landmarks, consistency twice. If none is found, all of them are
marked N/A — applying only the linear component would describe a different transform from the
one under audit — and the diagnostics tab lists what the object does expose.

A method is accepted only after being probed with two real points, so that a stub returning its
input unchanged is rejected rather than silently reported as a perfect registration. Which
direction it maps in cannot be verified through the API; the method that answered is named in
the report so the assumption stays visible.

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

What has been verified: the pure mathematics, through 64 analytic checks in
`tools/verify_math.py` — Euler extraction, matrix convention detection, voxel↔patient
round-trips, the similarity metrics against their theoretical values, transform composition,
TRE against known landmark displacements, the distance transform against brute force, and DSC
against the analytic intersection volume of two spheres.

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

Two paths drive the build, and both have defaults that work on a standard installation.

| Property | Default | What it is |
|---|---|---|
| `VarianScriptingPath` | Probes the usual Varian install locations | Where `VMS.IRS.Scripting.dll` lives |
| `EclipsePluginsPath` | `%USERPROFILE%\Documents\Contouring Scripting API\Projects\plugins` | Where the built assembly is written |

**The build output goes straight into the Eclipse plugins folder**, not into `bin\`. Eclipse
only lists scripts it finds there, so building anywhere else produces an assembly that
compiles cleanly and cannot be invoked. The destination is printed at the end of every build
so there is never any doubt about where the DLL ended up.

Override either property in any of three ways:

```powershell
# environment variable
$env:EclipsePluginsPath = "D:\Scripts\plugins"
$env:VarianScriptingPath = "D:\Program Files (x86)\Varian\...\VMS.IRS.Workspace"

# or on the command line
msbuild ESAPI_RegistrationQA.csproj /p:EclipsePluginsPath="D:\Scripts\plugins"
```

Or a `Directory.Build.props` next to the solution (best left unversioned):

```xml
<Project>
  <PropertyGroup>
    <VarianScriptingPath>D:\Program Files (x86)\Varian\...\VMS.IRS.Workspace</VarianScriptingPath>
    <EclipsePluginsPath>D:\Scripts\plugins</EclipsePluginsPath>
  </PropertyGroup>
</Project>
```

The project builds as **x64**, which is what Eclipse 15.6 and later require.

## Usage

1. Build in Release. The assembly is written to `EclipsePluginsPath` automatically.
2. Launch from **Contouring / Registration → Tools → Scripts**.

If the script does not appear in the list, check the destination printed by the build against
the folder shown in the Eclipse script dialog.

## The window

One table. Every metric that was measured, grouped by section, with four columns: **metric,
value, status, and one line saying either the tolerance it was held to or why there is none**.

Advisories, diagnostics and the rigid transform used to be tabs of their own. All three are
reference material — needed when a number has to be explained, in the way the rest of the time.
The transform is now a line under the table; the other two are buttons that carry their own
counts, because a tab labelled "Diagnostics" says nothing about whether it is worth opening
while "Diagnostics (3 failures)" does.

The long-form reasoning did not disappear: it is in the tooltip of every row and, in full, in
the exported HTML report, which is the document that gets read once and signed.

## Reading the verdict

The overall status distinguishes five situations, and never declares a registration verified
while metrics remain unevaluated:

| Verdict | Meaning |
|---|---|
| **COMPLIANT** | Every metric was measured and every one meets the profile. |
| **PARTIALLY COMPLIANT** | What was measured passes, but some metrics could not be measured. Verification is not complete. |
| **REVIEW REQUIRED** | A metric fell into the attention zone (yellow). |
| **NOT COMPLIANT** | A metric breaches the profile criterion (red). |
| **NOT VERIFIED** | Metrics were measured, but none of them carries a tolerance that can establish compliance. Typically a case with intensity metrics only: no landmarks, no matched structures. |
| **NO EVIDENCE** | No metric could be evaluated at all. See the diagnostics tab. |

A grey **INFO** badge is not a failure and not a missing value: it is a measurement the tool
declines to grade. **N/A** is the missing value.

## Known limitations

* **Everything rests on reading the registration matrix from the API, and the property that
  holds it varies between Eclipse versions.** A dozen property paths are probed, each
  accepted in any of seven container shapes (2-D array, jagged, flat 16- or 12-element
  row-major, `[r,c]` or `[i]` indexer, `m00..m33` or `M11..M44` members), followed by a
  reflection sweep of the registration object. When all of that fails, the object's type and
  member list are written to the diagnostics tab — send that and the Eclipse version, and the
  right property can be probed by name.
* Topological metrics for deformable registrations depend on the DVF, which is not
  accessible from the scripting API.
* Computation runs synchronously on the UI thread. Sampling is capped at ~2·10⁶ voxel pairs
  to keep the interface responsive, and the resulting effective resolution is reported
  alongside the metrics.
* Similarity is computed on subsampled volumes; the report states the effective resolution
  used.

## Licence

[MIT](LICENSE).
