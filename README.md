# ESAPI Registration Quantitative Audit (ESAPI_RegistrationQA)

A C# / WPF plugin for the **Varian Eclipse Treatment Planning System** (ESAPI and VMS.IRS
architecture) that automates the quantitative audit of image registrations.

## Intended use, and what this tool is not

**This is a measurement instrument for a qualified medical physicist. It is not a medical
device, it is not certified by any regulatory body, and it does not decide whether a
registration may be used clinically.** That decision is the physicist's, and nothing here
substitutes for it.

Read that as a statement about what the tool does, not as boilerplate:

* **It measures, and it grades only where TG-132 gives a number.** Five metrics can fail a
  registration and they are exactly Table III. Everything else is reported without a colour and
  cannot affect the verdict. Where the report declines to give a limit, so does this tool.
* **A green verdict is not a released registration.** It says the metrics that carry a TG-132
  tolerance met it on the data available. It says nothing about the metrics that could not be
  measured, about anatomy outside the sampled region, or about whether the registration is fit
  for the use you have in mind.
* **A red verdict is not a rejected registration either.** TG-132 asks for the influence of a
  breach on the intended use to be evaluated. The tool gives you the evidence — where the
  folding is, how many landmarks the TRE rests on, what fraction of the volume overlapped — and
  stops there.
* **Every number carries its provenance, and you should read it.** Voxel pairs, overlap
  fraction, effective sampling, grid spacing, which structure produced a worst case, which
  region a metric was computed over. A DSC over 8 % overlap at 4 mm sampling is not the same
  measurement as one over 80 % at 1 mm, and the criterion column says which you are looking at.
* **It has been exercised on one Eclipse installation.** See [Validation
  status](#validation-status) for exactly what has been checked against an independent answer
  and what has not. Commission it against your own data before you rely on it, the same way you
  would commission any other measuring device.

Licensed under [MIT](LICENSE), which includes its warranty disclaimer.

## Reference

Brock KK, Mutic S, McNutt TR, Li H, Kessler ML. *Use of image registration and fusion
algorithms and techniques in radiotherapy: Report of the AAPM Radiation Therapy Committee
Task Group No. 132.* Med Phys. 2017;44(7):e43–e76. [doi:10.1002/mp.12256](https://doi.org/10.1002/mp.12256)

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
| **Jacobian < 0** | Has the deformation folded tissue onto itself? | Table III, first clause: no negative values | Yes, at 0 %, inside the patient outline | Exact for rigid · measured from the field for DIR |
| **Jacobian departure from 1** | Is the volume change the one the case led you to expect? | Table III, second clause: no departure from 1 beyond what the clinical scenario expects | **No** — the report ties it to the structure and the expectation, neither of which the tool knows | Exact for rigid · measured from the field for DIR |
| **Max Displacement** | How far has the registration moved the anatomy? | Not tabulated; plausibility check | **No** — limits were invented | Exact for rigid · measured over the field for DIR |
| **Smoothness** | Is the deformation physically plausible? | Not tabulated; related to §4.C.3 | **No** — limits were invented | Exact for rigid · hidden for DIR, see DVF gradient |
| **DVF Gradient (max)** | Does the deformation vary abruptly between neighbouring points? | Not tabulated; related to §4.C.3 | **No** — no tolerance exists | Measured for DIR only |
| **DSC** | Do the same organs end up in the same place? | Table III, ~0.80–0.90 | Yes, from that range | Measured |
| **MDA** | On average, how far apart are the two organ surfaces? | Table III, ~2–3 mm | Yes, at the voxel dimension | Measured |
| **HD95** | How far apart are the surfaces where they disagree most? | Not tabulated; report specifies MDA | **No** — limits were inherited | Measured |
| **TRE (mean)** | How large is the spatial error, in millimetres? | Table III, primary metric | Yes, at the voxel dimension | Measured |
| **TRE (max)** | Is any single landmark badly placed? | Table III; mean/max split is ours | Yes, at the voxel dimension | Measured |
| **Inverse consistency** | Is the algorithm behaving stably? | §4.C.4 and Table III | Yes, at the voxel dimension | Measured |

Two points the report makes that shape how the first three should be read. TG-132 §4.C.3
admits SSD, CC and MI for assessment **provided the metric was not the one the registration
algorithm optimised** — otherwise the assessment is circular — and notes they are **difficult
to convert into a measure of spatial accuracy**. A compliant NCC does not establish
millimetric accuracy; TRE is the metric that does. The plugin raises an advisory saying so on
every case where those metrics are available.

## Where every tolerance comes from

Short answer: **five metrics can fail a registration, and they are exactly TG-132 Table III.**
Four of the five limits are not constants — the report states them as a property of the images,
and the plugin measures it.

| Metric | Limit | Source |
|---|---|---|
| **TRE (mean and max)** | maximum voxel dimension of the two series | Table III, verbatim |
| **MDA** | same | Table III, verbatim |
| **Inverse consistency** | same | Table III, verbatim |
| **DSC** | ≥ 0.90 green, ≥ 0.80 yellow | Table III's `~0.80–0.90` range mapped onto the two bands |
| **Jacobian < 0** | 0 % inside the patient outline | Table III, first clause: "no negative values", stated per structure |
| **Jacobian departure from 1** | *ungraded* | Table III, second clause — tied to the structure and the expected volume change, so no fixed band exists to apply |

The one exception the report makes: *"stereotactic radiosurgery tolerances are 1 mm."* That is
the whole of the profile selector — **Standard treatment** or **Stereotactic (SRS/SBRT)**.

**There used to be four anatomical profiles, and their spatial limits were a fiction.** TG-132
has no tolerance that varies by anatomical site. Where it does allow variation, in the MDA and
DSC rows, it attributes it to the contouring uncertainty and the volume of the *individual
structure*, not to the region. And the two interobserver studies the report actually cites in
§4.C.2 run the other way from the profiles this project shipped: 2.6 mm for peripheral lung
(Persson) against 3.9 mm for glottic larynx (Brouwer), while the profiles allowed head and neck
2.0 mm and thorax 3.0 mm. The ordering was inverted with respect to the evidence.

For the record, since the question is a fair one to ask of any QA tool: **DSC and HD95 carried
their per-site numbers from this project's first version.** MDA, TRE and inverse consistency did
not exist then; their per-site numbers were invented when those metrics were implemented. None
of them had a source.

When the voxel size cannot be read, TRE, MDA and consistency are not classified at all — a limit
the report ties to the image cannot be applied without the image.

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

**Maximum displacement does not classify at all.** It is not in TG-132 and its per-site limits
were invented here, but there was a second reason before that one was faced: within one frame of
reference the identity is the "no correction" state, so the displacement is the correction the
registration applies — while across two frames, two scanners or a CT and an MR, the matrix must
also span the offset between the coordinate systems, and a correct registration can exceed any
limit for that reason alone. The value is shown as a plausibility check, with an advisory naming
which of the two situations this case is in.

**Smoothness follows the intensity metrics, for the same reason.** TG-132 does not name it and
its limits were invented too. It stays on screen for a rigid transform because 1.0 is a true
statement worth recording — the field gradient is constant, so no local irregularity is
possible — but a statement true by definition cannot fail anything. On a deformable case it is
hidden and the **DVF gradient** carries the real measurement, on the opposite scale: 0 there
means what 1.0 means here. It is ungraded for the same reason — the report sets no limit for
field regularity, so any number chosen would be invented. Section 5 of `VALIDATION.md` is how a
local baseline makes it actionable.

**The Jacobian row of Table III has two clauses, and only one of them can be gated.** The row
reads: no negative values, *nor values departing from 1 relative to what is expected for the
clinical scenario (0–1 for structures where volume reduction is expected; above 1 where expansion
is expected)*. The first clause is absolute and is gated at 0 %. The second is explicitly relative
to the structure and to what the physicist expected of it, so it is measured — as the departure of
the p1/p99 Jacobian from 1 — and shown ungraded. Picking a fixed band there would invent the very
number the report declined to give.

**The Jacobian limit is 0 %.** Table III admits no negative values; the profiles used to admit
between 1 % and 4 %, so the tool was more permissive than the standard it cites, on its own
authority. It does not vary with anything: the report ties this tolerance to the physics, and a
folded voxel is as unphysical in a lung as in a brain.

**It is applied inside the patient, not over the whole grid.** Table III states this criterion
per structure, and a deformation field's grid is a box that extends well past the anatomy into
air, where the algorithm has no image to constrain it and folds freely. On a head phantom, 2.979 %
of the whole box folded against 0.003 % inside `BODY` — 99.95 % of the folded points were in air.
Grading the box is not the conservative choice it looks like: it fails almost every deformable
registration on a property of air, and a gate that always fails stops being read. So the graded
value is the one inside the patient outline (DICOM type `EXTERNAL`, or a `BODY`/`SKIN`-style name)
wherever one can be placed on the field's grid, with the whole-field figure carried beside it in
the criterion column and in the dataset, and a `jacobian: grading domain` diagnostic naming the
region — including which fallback applied when no outline could be placed. Both clauses of the row
move together, since grading one on the anatomy and the other on the air would be incoherent.
Where folding does fall inside the patient but in a region that does not affect the intended use,
TG-132 asks for that influence to be evaluated — that judgement is the physicist's, and the tool
does not pre-empt it by relaxing the limit.

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
- **Nothing else.** The site-specific threshold profiles are gone; see the section above for
  why. What the tool still cannot do is account for the structure-by-structure variation the
  report attributes MDA and DSC to — it names the structure behind each value instead, and
  leaves that judgement where it belongs. See [VALIDATION.md](VALIDATION.md).

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
| **Jacobian, max displacement and DVF gradient (deformable)** | ✅ Measured | From the deformation vector field itself: `NonRigidRegistration.DeformationField` exposes its own grid and `GetVectors`, so the Jacobian is `det(I + grad u)` by central differences, the gradient is the largest `‖grad u‖_F`, and the displacement maximum is taken over every field point rather than the eight corners. Derivatives use the field's own spacing, which is coarser than the image's. **Cross-checked against Eclipse:** on a head phantom CT→CT DIR, Eclipse's own Jacobian view showed a range of −0.72 to +3.04 and the plugin computed −0.7213 to +3.0431 — agreement on both extremes to the precision Eclipse displays. |
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
  instead of from inherited values. The header carries a schema version — currently **7** —
  which is bumped whenever a column is added, removed or redefined, so files that must not be
  concatenated can be told apart before they are.

## Validation status

Everything below is recorded case by case, with dates and numbers, in
[VALIDATION.md](VALIDATION.md). Read this section as a summary of that, not as a substitute.

**Checked against an independent answer — Eclipse's own displays, on one installation:**

| what | result |
|---|---|
| Jacobian determinant range | −0.72 / +3.04 against −0.7213 / +3.0431 ✅ |
| Divergence range | −2.87 / +1.46, exact ✅ |
| Displacement (distance view) | 58.4 mm, exact ✅ |
| Field components X, Y, Z | all three exact ✅ |
| Rigid translation and rotation | −4.8 / 10.3 / −122.5 mm against −4.84 / 10.28 / −122.47 ✅ |
| Curl | 2.15 against 1.99 — 8 % low, cause established, see Known limitations |
| **DSC** | **0.90 against 0.953 — unresolved, see Known limitations** ❌ |

The two axis questions that were the project's largest open risk — the deformation field's
components and the rigid transform's translation — are both closed by that table, on different
code paths.

**Checked against a control, not against Eclipse.** The same two series were registered rigidly
and deformably and audited with identical settings. Inverse consistency came out at 0.259 mm and
7.042 mm from the same code, the rigid case passing every graded metric and the deformable
breaching two. A tool that failed everything would have failed the rigid one.

**Checked by construction:** 80 analytic checks in `tools/verify_math.py` — Euler extraction,
matrix convention detection, voxel↔patient round-trips, the similarity metrics against their
theoretical values, transform composition, rigid inversion, TRE against known landmark
displacements, the distance transform against brute force, DSC against the analytic intersection
of two spheres, and the deformation-field Jacobian, divergence, curl and gradient against fields
whose answer is known exactly. Plus 81 contract checks in `tools/DvfContractTests.cs`, which run
the shipping C# against API-shaped stubs rather than re-implementing it. `tools/run_checks.sh`
runs both and a warnings-as-errors compile.

**Not checked:** TRE and the DVF gradient have no independent answer. Nothing has run on more
than one Eclipse installation. A clinical MR→CT rigid registration has now been audited — the
intensity metrics behaved as §4.C.3 predicts, NCC dropping to 0.631 where NMI stayed usable —
but nothing on that pair has been held against an independent answer. The threshold profiles
are TG-132 Table III applied literally, but no metric has been calibrated against a multi-centre
distribution — which is what the CSV dataset exists to build.

If you are evaluating the plugin, [VALIDATION.md](VALIDATION.md) has the full protocol with a
checklist you can fill in, and section 5 describes how to contribute to that baseline.

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
* **DSC does not yet agree with Eclipse's own.** On the head phantom's `PTV_High`, Eclipse's
  DICOM statistics report a Dice of 0.90 where this plugin reports 0.953 — both applying the
  registration, both comparing the same structure. Two candidate causes are eliminated: the
  rasterisation grid (0.5 mm against 2.0 mm moves DSC by under 0.002 on a structure this size)
  and the mapping direction (a reversed mapping would put the contours 245 mm apart and give a
  DSC of 0, not 0.953). The transform itself is verified against Eclipse.

  The `structures: <id>: rasterisation` diagnostic then narrowed it further. The rasterised
  volumes agree with Eclipse's own to 0.6 % and 2.5 % (33.3 against 33.5 cm³, 33.4 against
  32.6 cm³), while the intersections implied by the two Dice values differ by 6.1 % — so the
  disagreement is in the overlap, not in either mask. Grid resolution is eliminated a second
  time and in the opposite direction to the obvious guess: comparing a structure contoured at
  5 mm against the same anatomy contoured at 0.4 mm gives DSC 0.951 on a 0.5 mm grid and 0.940
  on a 2 mm one, so a coarser grid reads *lower*, and this plugin reads higher.

  That same simulation puts a ceiling on what any DSC across this pair can be: **the Z
  contouring mismatch alone — 99 planes at 0.4 mm against 8 planes at 5.0 mm for the same
  target — accounts for a DSC of about 0.95 with no registration error at all.** The plugin's
  0.953 sits exactly there. It is Eclipse's 0.90 that now needs explaining, which is the
  reverse of where this investigation started.

  **DSC gates at ≥ 0.90, so if the plugin is the one that is wrong, it is wrong in the
  permissive direction.** Until a case with matched slice spacing settles it, treat the DSC row
  as provisional, and treat any DSC across a series pair with very different slice spacing as a
  statement about contouring resolution as much as about registration.
* **The rasterised volume of a coarsely-contoured structure is wrong, so DSC, MDA and HD95 are
  unreliable on one.** Held against Eclipse's own structure statistics on a clinical MR↔CT pair,
  a structure contoured at 1.00 mm came out within 4–10 %, while the same structure copied onto
  the much coarser MR planes came out at **exactly half** its true volume in one run and 1.22×
  in another. The volume scales with an estimate of the contour plane spacing, and that estimate
  is unstable on an unevenly spaced plane set. A `structures: <id>: source planes` diagnostic now
  prints the plane positions and gaps and warns when they are uneven. **Until this is fixed,
  treat those three rows as unreliable whenever a structure's contours are sparse relative to its
  image planes** — the normal state of a structure propagated onto a coarser-sliced series.
* **TRE rests on however many landmarks you place, and one is not a measurement.** On a case
  with a single matched marker the criterion column says `1 matched landmark(s) — indicative
  only`. Place several, spread out, or read the row as an anecdote.
* **Curl reads about 8 % below Eclipse's**, because derivatives are evaluated on the interior
  only and the field's rotational maximum sits at its boundary. Documented in
  [VALIDATION.md](VALIDATION.md) test 3d and deliberately not engineered away — curl gates
  nothing, and mixing one-sided boundary derivatives into a graded statistic would be the wrong
  trade.
* Computation runs synchronously on the UI thread. It is visible rather than instantaneous: the
  window appears immediately and reports ten named stages on a progress bar. Sampling is capped
  at ~2·10⁶ voxel pairs to keep the interface responsive, and the resulting effective resolution
  is reported alongside the metrics.
* Similarity is computed on subsampled volumes; the report states the effective resolution
  used.

## Licence

[MIT](LICENSE).
