# Validation protocol

## Why this document exists

One part of the tool has now been checked against an independent answer: the deformation-field
Jacobian agrees with Eclipse's own to the precision Eclipse displays (see test 3d). Everything
else rests on the pure mathematics — 76 analytic checks in `tools/verify_math.py`, covering Euler extraction, matrix
convention detection, voxel↔patient round-trips, the similarity metrics against their
theoretical values, transform composition, TRE against known landmark displacements, and the
deformation-field Jacobian and gradient against fields whose answer is known exactly (a pure
translation, a uniform expansion, a deliberate fold) — plus the fact that it builds and runs.

Everything that touches the Varian API has been exercised on exactly one Eclipse installation.

The tolerances are no longer provisional in the way they were. Five metrics can fail a
registration, they are exactly TG-132 Table III, and four of the five take their limit from the
maximum voxel dimension of the images — the report's own rule, applied to the images in front of
it rather than to a table of invented numbers. Everything else is reported without a colour.

That is what this protocol is for. Tests 1 to 4 take an afternoon and close the open
questions that cannot be answered without an Eclipse in front of you. Tests 3b and 3d cover the
newest code — the TG-132 Table III spatial metrics, and the deformation-field metrics that
became possible once the vector field turned out to be readable. Of those, only the Jacobian has
been held against an independent answer; TRE, inverse consistency and the DVF gradient have not.
Section 5 is the part that turns a group of testers into a dataset.

---

## 0. The registration matrix — run this before anything else

**Setup.** Open any rigid registration and read the provenance line under the verdict.

**Expected.** It begins `Transform: API matrix (…)`, naming the property and the container
shape the matrix was read from.

**If it says `not available — the API exposed no registration matrix`,** stop: nothing that
requires mapping a point through the registration can be measured, so NCC, NMI, SSD, TRE,
DSC, MDA, HD95, inverse consistency and maximum displacement will all read N/A. That is the
correct behaviour, not a second fault.

This happens because the property holding the matrix differs between Eclipse versions. The
plugin probes a dozen paths, accepts seven container shapes, then sweeps the object by
reflection; when all of that fails it writes the registration object's type and full member
list into the **Diagnostics** tab. **Send that entry with your Eclipse version.** It is a
one-line fix once the name is known, and it is the single most useful thing early testers can
contribute.

Earlier versions did not fail here — they silently substituted the relative transform between
the two image frames. That is the difference in where each scan started, not the registration,
and it produced a "maximum displacement" of 171 mm on a perfectly ordinary CT–CT pair, turning
the verdict red on a number nobody had measured. The substitution has been removed.

---

## 0a. Are the voxels actually being read?

**Setup.** Any registration. Diagnostics tab.

**Expected.** Two lines per series:

```
GetVoxels        reading voxels through Frame.GetVoxels(int, ushort[x,y])
intensity range  min -1000.0, max 1200.0, mean -350.0 over 2,015,232 voxels
```

The carrier and buffer shape named will depend on your Eclipse version. What matters is the
second line: **min and max must differ.** If they are equal, the API returned without writing
anything and the audit has nothing to work with — no intensity metrics, and no structure metrics
either, since the sampling grid is only kept when a volume loads.

This is the failure the first field test hit. `Frame.GetVoxels(int, ushort[*,*])` accepted the
call, threw nothing, and left the buffer at zero, which after the HU ramp reads as a uniform
−1000 HU across the whole volume. The plugin now tries every combination of carrier
(`Frame`, `Image`, the image object) and buffer shape, and accepts one only once it has written
non-constant data to a probe plane — a third, a half and two thirds of the way through the
volume, never plane 0, which on a head CT is uniform air and would look identical to a buffer
that was never touched.

If every combination fails, the diagnostics list each attempt with why, plus the full member
surface of every candidate object. **Send that.**

---

## 0b. Deformable registration — the point mapping

**Setup.** Open a deformable registration. Any one will do.

**Expected.** The Diagnostics tab contains a line beginning `deformable mapping: using …`, naming
the method being used to push points through the deformation field.

**If instead every metric reads N/A and the verdict is NO EVIDENCE,** no such method was found.
That is one fault, not twelve: the intensity metrics need the mapping to pair voxels, DSC, MDA
and HD95 need it to carry a contour across, TRE needs it for the landmarks, and consistency
needs it twice. The linear component is not used as a substitute, because it describes a
different transform from the one being audited.

Eight method names are probed on the registration and on seven possible wrapper properties, and
a candidate is accepted only after being probed with two real points — a stub that returns its
input unchanged is rejected rather than reported as a perfect registration. When none answers,
the object's type and member list go to the Diagnostics tab. **Send that line with your Eclipse
version.**

One thing worth checking if the mapping does work: the direction. The API gives no way to tell
whether the method maps source→registered or the reverse. If TRE comes out systematically large
while the fusion looks correct on screen, that is the likely explanation — report it, as the
method name is recorded in the report.

---

## 0c. Progress feedback, and whether a background thread is viable

**Setup.** Open any registration and run the plugin. Watch the window as it appears.

**Expected.** The window appears **immediately**, showing "Measuring registration" over a
progress bar and the name of the stage running — "Reading the source volume…", "Reading the
deformation field…", and so on through ten stages.

Earlier versions computed everything before creating the window, so Eclipse sat with no window at
all for several seconds. That is what this changes: nothing is faster except the deformation-field
read, which went from 5.0 s to 0.28 s, but the wait is now visible and attributable.

**What to report.**

- **Which stage is slowest on your data.** The stage caption names the API read, so a case that
  dwells on one of them tells us where to look. This is the useful measurement.
- **If the bar reaches the end but the window stays on the progress panel**, the pass threw
  somewhere the diagnostics should still record — send the Diagnostics tab.
- **If the window never appears at all**, the deferred start did not fire. That would mean
  `Loaded` never raised, and it is worth knowing.

The window deliberately does not accept clicks while measuring, and says so. The work stays on
the UI thread because the Varian API cannot be assumed safe to touch from another one — an API
with thread affinity might not throw, it might hand back an empty buffer, which is precisely the
failure mode `GetVoxels` produced for three sessions.

**The probe has now settled that question, and the answer was the permissive one.** Its "Thread
affinity" section read `Image.Id`, `Frame.XSize` and `Frame.GetVoxels` on the calling thread
(managed id 1, STA) and on an MTA worker, and all three matched — `GetVoxels` included, returning
the same `min 7127, max 10060` from both. No exception, and no empty buffer, which was the failure
mode being guarded against.

**What that does and does not establish.** It shows those three reads work from a worker on this
Eclipse version. It does not cover the whole surface a measurement touches: the structure-set
enumeration, `VectorField.GetVectors` and `TransformPoint` were not tested off-thread, and they
are different objects. Nor does it establish safety under *concurrent* access — but that is not
what a background measurement would do, since the whole pass would run on one worker with the UI
thread idle, which is exactly the pattern that was tested.

So the remaining risk is confined to the three untested reads. Extending the probe to cover them
is the cheap way to close it before committing to the refactor.

---

## 1. Identity registration

**Setup.** Register a CT to itself, or select an identity registration if the workspace has
one.

**Expected.**

| Metric | Expected | Meaning if it differs |
|---|---|---|
| NCC | 1.000 | Voxel pairing is broken |
| NMI | 2.000 | Joint histogram is wrong |
| SSD | 0.000 | Same |
| Translations | 0.000 mm | Transform extraction is wrong |
| Rotations | 0.000° | Same |
| Overlap | ~100% | Geometry conversion is wrong |

This single test exercises the entire measurement chain end to end. If NCC does not come out
at 1.000, stop and report it before running anything else — nothing downstream is meaningful.

---

## 2. Known translation — the important one

**Setup.** Take a rigid registration where you know the applied shift. Easiest route: apply a
deliberate offset of, say, **5 mm in the cranial direction** and nothing else.

**Expected.** The reported translation matches the applied one **and appears on the right
axis**.

The plugin labels the axes following the DICOM patient convention:

```
X = left-right         (LR)
Y = anterior-posterior (AP)
Z = cranio-caudal      (CC)
```

A 5 mm cranial shift must therefore appear as **Translation Z (CC) = 5.000** with X and Y at
zero.

**Why this test matters more than the others.** These labels were corrected during the
rewrite: an earlier version had Y and Z swapped. The correction follows the DICOM standard,
but it has not been confirmed against the convention VMS.IRS actually uses. If your 5 mm
cranial shift lands in `Translation Y (AP)`, the labelling is wrong and needs reverting —
and a QA report that names the wrong axis is worse than no report.

**Please report the result of this test even if it passes.** It is the largest open risk in
the project.

---

## 3. Known rotation

**Setup.** A small rotation, 2° to 5°, about a single axis.

**Expected.** The angle matches on the corresponding axis, the other two read zero.

Angles use the intrinsic Rz·Ry·Rx convention. Near a pitch of ±90° the decomposition becomes
degenerate; the tool detects this, reports yaw as zero and raises a gimbal-lock advisory. If
you can produce such a case, it is worth checking that the advisory appears.

---

## 3b. TRE and inverse consistency

These are the TG-132 Table III metrics and the most valuable ones to exercise, because they
are new and untested.

**TRE.** Place two or more markers on identifiable features — implanted fiducials, or bony
landmarks — with the same structure identifier on both series. Run the plugin. Under a
registration you trust, the mean TRE should sit at or below the maximum voxel dimension, which
the report states for you in the provenance section.

Combine this with test 2: apply a known 5 mm shift and the mean TRE should come out at 5 mm.
That validates the landmark path and the axis convention at the same time.

A marker's position is read from `CenterPoint` first, then `Position`, `Point` and the first
element of `Points` as alternatives — a real case (v2.13.0–v2.14.0) had markers come back as
`VMS.CA.Scripting.PointsStructure`, which has none of `CenterPoint`, `Position` or `Point` (the
property volumetric structures expose), only a `Points` collection holding the one point. If none
of the four answer, Diagnostics logs the object's member surface so the real property name can be
added the same way `StructureType` was found for the DICOM type.

**Inverse consistency.** Create the reverse registration (B→A) alongside the forward one and
re-run. The residual should be small; TG-132 sets the tolerance at the maximum voxel dimension.
A rotational inconsistency vanishes at the centre of the volume and grows towards the
periphery, which is why the plugin samples a grid across the whole field of view rather than
the centre alone.

If the reverse registration exists but the plugin reports the metric as unavailable, that
means it could not enumerate the registrations in your Eclipse version. Please send the
Diagnostics tab: that is exactly the kind of API difference this testing is meant to surface.

If the residual comes out large instead — a real case (v2.13.0) read 266 mm, almost exactly
twice the registration's own maximum displacement — that number is the signature of the round
trip applying the same direction twice instead of forward-then-back. Diagnostics now separates
the two registrations involved: entries for the active one are unprefixed, entries for the one
found as its reverse carry a `"reverse: "` prefix on the same operation names (`transform:
matrix`, `point mapping`), plus a line naming which registration was picked and which images
matched it. Compare the two before assuming the check itself is broken — it may instead be that
the "reverse" registration found in the workspace is not a genuine inverse of the active one.

Diagnostics also logs `reverse: transform: translation`, the reverse registration's own
translation and rotation in the same form the report's Rigid Transform table uses for the active
one. Hold the two up against each other: a genuine inverse should read approximately opposite and
of similar magnitude. One that looks like the active registration's own translation, same sign
and similar size, is the round trip applying the same direction twice — which is what the
identical operation names above cannot show by themselves, since both registrations resolving
through the same method name is expected and not itself a fault.

## 3c. Structures and hidden metrics

**Structures.** Copy a contour to the second series under the same identifier and re-run. DSC,
MDA and HD95 should appear. Two checks worth making:

- **MDA must come out below HD95** on the same structure. They are drawn from the same set of
  surface distances, so MDA above HD95 would mean one of the two is wrong.
- Contrast the DSC against whatever Eclipse or another tool reports for the same pair.

When several structures match, the worst case is reported, not an average — a Dice of 0.85 is
excellent for a parotid and poor for a whole lung, so averaging across organs produces a number
that describes neither. The note beside the metric names which structure was worst.

**Three badges, three meanings.** Before reporting anything as a fault, check which one you are
looking at:

| Badge | Meaning |
|---|---|
| Green / Yellow / Red | Measured and classified against the profile. |
| **INFO** (grey) | Measured, but the tool declines to grade it — no defensible tolerance. NCC, NMI and SSD always; maximum displacement when the two series are in different frames of reference; DSC/MDA/HD95 when the only matched structure is the patient surface outline. Never affects the verdict. |
| **N/A** (grey) | Not measured. This one is a fault, and the reason is beside it. |
| *(absent)* | Does not apply to this case. Accounted for in the diagnostics tab. |

**Which structure the number belongs to.** DSC, MDA and HD95 each report the worst case across
the matched structures, and the worst case for one need not be the worst case for another. Each
value now names its own structure in the criterion column, and a warning appears when the three
come from different ones. The Diagnostics window lists every structure with its three values.

**The patient surface outline is excluded from the organ-level comparison automatically, on
either of two criteria.** A structure is treated as the outline if its DICOM type is `EXTERNAL`
— the normative type for a body outline — **or** if its identifier exactly matches (trimmed,
case-insensitive, not a substring) one of `BODY`, `EXTERNAL`, `SKIN`, `EXTERIOR`, `CUERPO`,
`PIEL`. The name check exists because the DICOM type is often not the reliable signal it should
be: a real case (v2.11.0) had a structure literally named `BODY` whose DicomType was not
`EXTERNAL`, so the type-only check missed it and BODY still won the worst case. Either criterion
is still read and rasterised, and its own DSC/MDA/HD95 are logged in Diagnostics, but it never
enters the worst case reported against an organ or target. TG-132's DSC and MDA rows describe
"the same organ", not the skin, and where two series cover different lengths of patient the
outline's ends cannot agree no matter how good the registration is: on a real case it reported
DSC 0.910 and MDA 6.74 mm and buried a PTV that actually measured DSC 0.952 and MDA 0.65 mm.
There is no checkbox to bring it back — if a structure named BODY genuinely needs comparing as
an organ for some case, it has to be renamed in Eclipse first.

**Diagnostics now shows the DicomType actually read for every structure**, logged as it comes
back from the API (including empty), plus which criterion — DICOM type or name match —
triggered the outline exclusion when it fires. That visibility is what let the v2.11.0 failure
be diagnosed instead of guessed at: previously the value was read but never logged, so there was
no way to see why the exclusion had not fired.

**The field is read under two possible property names.** A real Eclipse install (v2.12.0, the
`VMS.CA.Scripting` API) turned out to have no `DicomType` property on its structure type at all
— the read failed with `RuntimeBinderException`, not an empty value. On that API the same DICOM
field is exposed as `StructureType`, an enum rather than a string, carrying the same vocabulary
(`EXTERNAL`, `MARKER`, `ISOCENTER`...) under a different name. Both are tried, `DicomType` first;
if neither answers, Diagnostics logs the object's full member surface once so a third variant can
be identified the same way this one was, without a separate probe session. This also fixes TRE:
without a working DICOM type field, MARKER and ISOCENTER structures could never be recognised, so
TRE stayed unreachable on that API regardless of whether markers existed in the case.

**If the only structure pair that matches by identifier is the surface outline**, DSC, MDA and
HD95 are still measured from it — useful as a coarse pre-propagation check, the kind done
before any organ or target has been contoured on either series — but reported as **INFO**, not
graded: grey badge, no colour, excluded from the verdict, with the criterion column saying
`not graded — measured on the patient surface outline only, not an organ`. A registration can be
flawless and still show a large "disagreement" here if the two scans differ in length, so a bad
number in this state means nothing about registration quality by itself. Once a real organ or
target is contoured under a shared identifier, the three revert to measuring — and grading —
that structure instead.

**Do not use BODY, EXTERNAL, or any of the other outline names for this.** Where the two scans
cover different lengths of patient, the outline surfaces cannot agree at the ends, and the
disagreement measures the field of view rather than the registration. A duplicated PTV sphere
reported DSC 0.910 next to HD95 38.5 mm on a real case for exactly that reason: the Dice came
from the sphere and the Hausdorff from BODY.

**Hidden metrics.** A metric that cannot apply is no longer shown as N/A; it is omitted and
accounted for in the diagnostics tab instead. So:

- On a **rigid** registration the deformation metrics should be absent, and the verdict must
  not read "partially compliant" on their account.
- On a case **without markers**, TRE should be absent, with the diagnostics tab saying that
  MARKER structures sharing an identifier are what would enable it.
- If you force a **genuine failure** — a registration whose image will not load — NCC, NMI and
  SSD must still appear as N/A with their reason. That distinction is the point of the change:
  a fault stays visible, a context mismatch disappears. If a real failure gets hidden, report
  it.

## 3d. Deformation field metrics — new and unvalidated against known deformation

**Setup.** Open a deformable registration. The Diagnostics tab should carry a line beginning
`deformation field: read from ...DeformationField`, naming the grid size and its spacing.

**Expected.** Four rows appear that a rigid case does not have: `Jacobian < 0`,
`Jacobian departure from 1`,
`DVF Gradient (max)` and a `Max Displacement` measured over every field point rather than the
eight FOV corners.

**What to check, in order of value.**

- **Jacobian on a registration you trust should be 0 %.** Any folding at all is a breach of
  Table III, and on a clinically acceptable deformation there should be none. A non-zero
  percentage on a registration that looks correct on screen is the single most important thing
  to report from this test.
- **Max displacement should be plausible for the anatomy.** It is now the true maximum over the
  field, so it will generally read *higher* than the old rigid-style corner bound on the same
  case. That is expected, not a regression.
- **The grid is not the image's.** The Diagnostics line names both; on the case this was built
  against the field was 190×206×39 at ~0.98×0.98×5 mm against a 512×512×458 image at
  ~0.45×0.45×0.4 mm. Every derivative uses the field's own spacing. If the reported spacing
  matches the *image* instead, the wrong geometry is being read and every gradient is scaled by
  the ratio between the two.

**The mathematics is checked analytically** — `tools/verify_math.py` covers a pure translation
(det J exactly 1, gradient exactly 0), a uniform expansion (det J = (1+k)³), a deliberate fold
(det J = −1, flagged 100 % negative), and that the gradient scales with the axis spacing
actually used.

**It has now also been checked against Eclipse's own Jacobian.** On a head phantom CT→CT
deformable registration (2026-07-30), Eclipse's Jacobian determinant view showed a colour-bar
range of **−0.72 to +3.04**. The plugin computed **−0.7213 to +3.0431** on the same registration:
agreement on both extremes to the two decimals Eclipse displays.

That is the strongest external check available without a phantom of known deformation, and it
exercises the whole chain at once. Reading the field wrongly, using the image's spacing instead
of the field's, or computing det(grad u) instead of det(I + grad u) would each move those numbers,
and none of them is a small perturbation. It also implies the affine parts are rigid on this
case — a scaling or reflection in `PreTransformationMatrix` or `PostTransformationMatrix` would
have put the plugin's determinants out by that factor against Eclipse's.

**There is a second view to compare, and the plugin now reports it.** Eclipse also displays the
*divergence* of the same field, div u = trace(grad u). It is not a TG-132 metric and is
deliberately not a table row — adding it would be adding a criterion the report does not have —
but it is a second independent quantity computed from the same field read, so it goes to the
Diagnostics tab as `deformation field: divergence`.

The two views constrain each other. Where the deformation is small,
det(I + grad u) = 1 + div u + higher-order terms, so a large negative divergence should accompany
volume inversion and a large positive one large expansion. On the phantom case Eclipse showed
divergence spanning **−2.87 to +1.46** against a determinant of −0.72 to +3.04 — the signs agree
at both ends, and the gap is the higher-order terms, which is what a deformation this far from
small should show.

**The distance view matched too, exactly.** Eclipse showed 0 to **58.4 mm**; the plugin reported a
maximum displacement of **58.399 mm**. That one also resolved an ambiguity worth recording: the
two series sit in different frames of reference with a 122.75 mm offset between them, and the
agreement at 58.4 mm confirms that Eclipse's distance view — like the plugin's max displacement —
measures the deformation field alone and not the total transform including that offset.

**Repeat every comparison you can.** The Diagnostics tab now carries a
`deformation field: cross-checks` line holding the Jacobian range, the divergence range, the
maximum distance and the maximum curl together, precisely so they can be read against Eclipse's
views one after another. Any single disagreement points at the field read rather than at the
registration, because all four come from the same read.

### Eclipse reference values, phantom CT→CT deformable, 2026-07-30

Captured from Eclipse's own views of the field, for the plugin to be held against. Two are
already confirmed; the rest await a build carrying the Diagnostics cross-check lines.

| Eclipse view | Eclipse | Plugin | Status |
|---|---|---|---|
| Jacobian determinant | −0.72 to +3.04 | −0.7213 to +3.0431 | **match** |
| Divergence | −2.87 to +1.46 | −2.87 to +1.46 | **match** |
| Distance (‖u‖) | 0 to 58.4 mm | 58.399 mm | **match** |
| X-Component | −34.1 to 40.1 mm | −34.07 to 40.10 mm | **match** |
| Y-Component | −24.8 to 6.9 mm | −24.80 to 6.89 mm | **match** |
| Z-Component | −44.5 to 25.3 mm | −44.54 to 25.29 mm | **match** |
| Curl | 0 to 2.15 | 0 to 1.99 | **differs — explained** |

**The axis convention is settled, and it is correct.** All three components match Eclipse to the
precision it displays, so the plugin's X is Eclipse's X, its Y is Eclipse's Y and its Z is
Eclipse's Z. Test 2 has called this the largest open risk in the project since an earlier version
was found to have Y and Z transposed; the correction followed the DICOM standard but had never
been confirmed against the convention VMS.IRS actually uses. It now has been, on the deformation
field, without needing a phantom shifted by a known amount.

That confirmation covers the deformation field's components. The rigid transform's translation
labelling is read from a different property by different code, so **test 2 is still worth running
on a rigid case** — but the evidence that the two now disagree is gone.

**Curl is the one that does not match: Eclipse 2.15, plugin 1.99, a difference of 8 %** — and the
cause is now established. It is the evaluation domain, not the formula.

The plugin evaluates every derivative on the interior only, skipping the outermost shell in each
axis, because a central difference needs a neighbour on both sides. The Diagnostics line reports
where the maximum was found, and on this case it reads:

```
max |curl u| 1.99 at grid (1, 151, 37) of 190x206x39, 0 step(s) from the nearest face
```

On a 190×206×39 field the evaluated interior runs x 1–188, y 1–204, z 1–37. The maximum sits at
x = 1, the **first** evaluated layer, and simultaneously at z = 37, the **last** — against two
faces at once, with the excluded shell directly adjacent in both directions. An implementation
evaluating the full grid with one-sided differences there would find a larger value, which is
what Eclipse reports. Nothing else needs to be invoked: the ratio 1.0804 matches no alternative
definition of curl, and the formula is pinned by contract tests including a shear whose curl
equals its shear rate exactly.

That a rotational measure peaks at the boundary is expected rather than surprising — the field's
support ends there, so the displacement falls away abruptly and the local shear is at its
largest.

**Why the Jacobian still matched, if Eclipse evaluates the full grid.** Because its extremes are
genuinely interior, and the plugin's own folding analysis says so independently: of 42,271 folded
points, only 1,673 — 4 % — lie within two grid steps of the edge. The most extreme determinant
being inside is consistent with that, so the agreement is not luck.

**The interior-only choice stands, and the difference is left as documented rather than
engineered away.** Mixing one-sided derivatives at the boundary with central differences inside
would put estimates of two different error orders into the same graded statistic, which is the
reason the shell is excluded. Changing a metric that gates a QA verdict so it matches a display
would be the wrong trade. Curl gates nothing and no metric derives from it, so what it needs is
to be understood, which it now is: **expect the plugin's curl to read slightly below Eclipse's
whenever the peak sits at the field edge.**

**The three components are internally consistent with the distance view**, which is worth
checking before comparing anything against them. The largest single component is 44.5 mm, so
‖u‖ can be no smaller than that; the norm of the three per-axis extremes is 64.8 mm, so it can be
no larger. Eclipse's 58.4 mm sits inside [44.5, 64.8]. Had it fallen outside, one of the views
would have been misread before the plugin was ever involved.

**What these numbers say about the registration itself:** the field displaces the phantom by up
to 58 mm, with per-axis excursions of 74 mm in X and 70 mm in Z. This is a rigid skull phantom
imaged twice — there is no anatomy to deform. Together with 2.979 % folding, 96 % of it inside
the field rather than at its edge, and a determinant reaching 3.04, the deformation is not
describing the patient. The intensity metrics saw none of this: NCC came out at 0.968 and the
fusion looks correct on screen, which is exactly the case TG-132 §4.C.3 warns cannot be converted
into spatial accuracy.

**One comparison is worth more than the rest, and it is not on this list.** A second Diagnostics
line, `deformation field: displacement per axis`, gives the field's range on X, Y and Z
separately. If Eclipse displays the field per component, checking those three against it settles
the axis convention — the question test 2 calls the largest open risk in the project, and which
has so far needed a phantom shifted by a known amount to answer. The same goes for the rigid
translation: Eclipse displays the registration's own translation, and holding it against the
plugin's `Translation … (LR / AP / CC)` line answers test 2 directly, without shifting anything.

What it does *not* establish: the percentage of folded voxels (2.979 % on that case) and the
percentiles are not visible on the colour bar, so they remain checked only against the analytic
cases. And it is one registration on one Eclipse version.

**Only one thing here is graded, and it is half of one row.** TG-132 Table III's Jacobian row
reads: no negative values, *nor values departing from 1 relative to what is expected for the
clinical scenario (0–1 for structures where volume reduction is expected; above 1 where expansion
is expected)*. The first clause is absolute and is gated at 0 %. The second is measured — as the
departure of the p1/p99 Jacobian from 1 — and shown ungraded, because the report ties it to the
structure and to what you expected of it. **That second number is yours to judge:** compare it
against the volume change the interval between the two scans can justify. The DVF gradient has no
tolerance in the report either, so it is INFO as well; section 5 is how both become actionable.

## 4. Multimodal pair

**Setup.** A CT–MR registration of the same patient.

**Expected.** NCC drops noticeably relative to a CT–CT pair; NMI stays interpretable. The
advisory panel should state that the registration is multimodal and that NMI is the reference
metric.

This is a behavioural check rather than a numerical one: NCC assumes a linear relationship
between intensities, which does not hold across modalities. Seeing the two metrics diverge is
the expected outcome, not a fault.

---

## 5. Local baseline — the part that produces something useful

Tests 1 to 4 verify the tool. This is the part that gives you something back.

**Method.** Run the plugin on 20–30 registrations you already consider acceptable, of a single
type (CT–CBCT for IGRT verification is the most productive starting point). Use the **Add to
dataset (CSV)** button and point every case at the same file.

**What you get.** The distribution of NCC, NMI and SSD for *your* protocols, *your* scanners
and *your* patient mix. From that point on, a new registration falling three standard
deviations below your own distribution is flagged without anyone having agreed on a universal
threshold. It is the same statistical logic used for linac constancy checks.

This works today, with uncalibrated profiles, because the measurements are comparable to each
other even when the absolute limits are unsettled.

**Fill in the `PhysicistVerdict` column.** The tool cannot. Use:

| Value | Meaning |
|---|---|
| `ACCEPT` | You would use this registration clinically without further review |
| `REVIEW` | Usable but you would look at it more closely |
| `REJECT` | You would redo it |

That column is the ground truth. A measurement without a human judgement beside it cannot
calibrate anything; with it, the pooled data gives thresholds derived from practice instead of
inherited from a paper.

Also fill the `Centre` column once, with a find-and-replace, before sharing the file.

**On patient data.** No patient identifier is written to the CSV. Cases are keyed by a
truncated SHA-256 of the patient and registration identifiers, which lets repeated audits of
the same case be collapsed. That is a deduplication aid, not an anonymisation guarantee — a
short identifier from a small known space can be enumerated. Treat the file as institutional
data and apply your usual review before it leaves the department.

---

## What to send back

For anything that fails, or behaves unexpectedly:

1. **The Diagnostics tab.** This is the single most useful thing you can send. Every property
   the plugin failed to read from the API is listed there with the operation and the exact
   exception. It exists precisely because the API surface varies between Eclipse versions and
   those differences cannot be reproduced elsewhere.
2. **Eclipse version** (15.5 / 16.1 / 18.0 / other).
3. **Registration type** and the modalities involved.
4. **The HTML report**, if the case can be shared.

Open an issue at
<https://github.com/davidpadron76/ESAPI_RegistrationQA/issues>.

---

## Known limitations, so nobody spends time rediscovering them

| Area | Status |
|---|---|
| DSC / MDA / HD95 | Measured, but only when contour structures share an identifier across both series. Otherwise hidden, with the reason in the diagnostics tab. |
| TRE | Measured, but only when point landmarks (DICOM type MARKER or ISOCENTER) exist on both series under the same identifier. Otherwise N/A with the counts found on each side. |
| Inverse consistency | Measured, but only when the reverse registration exists in the workspace. Otherwise N/A saying so — it is a check you can enable, not a permanent limitation. |
| Jacobian, DVF gradient, max displacement for DIR | **Measured.** The scripting API does expose the field: `NonRigidRegistration.DeformationField` is a `VectorField` carrying its own grid and `GetVectors(VectorFloat[,,])`, which a probe called on a real deformable case and read non-uniform displacements from (0.02–9.53 mm in the sampled plane). All three are computed from it by central differences over the field's own spacing. Never run against a case with known deformation — see test 3d. |
| Smoothness on a deformable case | Hidden. Its 1.0 is a statement about rigid transforms; the measured equivalent is the DVF gradient, on the opposite scale (0 there means what 1.0 means here). |
| Deformable registrations | Everything depends on finding a point-by-point mapping method on the registration object. Without it every metric is N/A and the verdict is NO EVIDENCE. See test 0b. |
| HD95 and max displacement tolerances | None exist either. Both carried numbers with no source — HD95 from this project's first version, max displacement invented — and both are now **INFO**. |
| NCC / NMI / SSD tolerances | None exist. TG-132 gives no limit for any of the three and §4.C.3 says they do not convert into spatial accuracy. Shown as **INFO** — value, no colour, no effect on the verdict. Section 5 is how you make them actionable. |
| Smoothness tolerance | Same: not named in TG-132, limits were invented. Shown as **INFO**. For a rigid transform the value is 1.0 by definition. |
| Max displacement tolerance | Applied only when both series share a DICOM frame of reference. Otherwise the magnitude spans two coordinate systems and is shown as **INFO**. |
| Jacobian tolerance | 0 % in every profile, matching the first clause of Table III's Jacobian row, "no negative values". Not varied by anatomical site: the report ties this clause to the physics. |
| Jacobian departure from 1 | **Measured, not graded.** The second clause of the same row constrains departure from 1 "relative to what is expected for the clinical scenario", per structure. Neither the expectation nor the structure it applies to is available to the tool, so a fixed band here would invent the number the report declined to give. Reported as the departure of the p1/p99 Jacobian from 1, for the physicist to judge. |
| Threshold profiles | Replaced. The four anatomical profiles had no basis in TG-132: the report gives no site-dependent tolerance, and the two interobserver studies it cites ran the other way from what they encoded. TRE, MDA and consistency now take the maximum voxel dimension of the images, DSC the 0.80–0.90 range of Table III, and the selector offers only what the report distinguishes — standard treatment or stereotactic, where it sets 1 mm. |
| Registration matrix | The property holding it varies between Eclipse versions. A dozen paths and seven container shapes are tried, then a reflection sweep. If none answers, everything downstream is N/A and the object's member list goes to the Diagnostics tab. See test 0. |
| Direction cosines | If the API does not expose them, canonical axial orientation is assumed and a warning is logged. A tilted-gantry acquisition would be misread; the Diagnostics tab will say so. |
| Performance | Runs on the UI thread, capped at ~2·10⁶ voxel pairs. The window appears first and shows which stage is running, repainting between stages, but it does not accept clicks until the pass finishes. **The probe has since shown this is more conservative than it needs to be:** `Image.Id`, `Frame.XSize` and `Frame.GetVoxels` all returned identical values from an MTA worker thread (Eclipse 15.6, 2026-07-30), so a background thread is viable. Moving the pass there is not done yet — see the caveat under test 0c. |

---

## Collection sheet

| # | Test | Expected | Observed | Pass | Notes |
|---|---|---|---|---|---|
| 0 | **Matrix read from the API** | `API matrix (…)` | | | property path and shape |
| 0 | Frame of Reference read | same / different | | | affects whether Max Displacement is graded |
| 0a | **Voxels read: min ≠ max** | real HU range | | | if equal, nothing downstream works |
| 0b | **Deformable: point mapping found** | method named in Diagnostics | | | if not, everything is N/A |
| 0c | Window appears before measuring | immediately, with progress | | | slowest stage: |
| 0c | **Probe: thread affinity** | MATCH or DIFFERS | | | decides whether a background thread is viable |
| 1 | Identity — NCC | 1.000 | | | |
| 1 | Identity — NMI | 2.000 | | | |
| 1 | Identity — SSD | 0.000 | | | |
| 1 | Identity — translations | 0.000 mm | | | |
| 1 | Identity — overlap | ~100% | | | |
| 2 | **Known translation — magnitude** | applied value | | | |
| 2 | **Known translation — axis** | correct axis label | | | |
| 3 | Known rotation — angle | applied value | | | |
| 3 | Known rotation — other axes | 0.000° | | | |
| 4 | CT–MR — NCC vs CT–CT | lower | | | |
| 4 | CT–MR — multimodal advisory | present | | | |
| 3b | TRE — landmarks matched | ≥ 2 | | | |
| 3b | TRE mean under known shift | applied value | | | |
| 3b | Inverse consistency residual | ≤ max voxel dim | | | |
| 3d | **Deformation field read** | grid + spacing in Diagnostics | | | must be the field's, not the image's |
| 3d | **Jacobian on a trusted DIR** | 0 % | | | any folding is a Table III breach |
| 3d | **Jacobian min/max vs Eclipse's colour bar** | agree to 2 dp | | | matched −0.72/+3.04 on 2026-07-30 |
| 3d | **Divergence vs Eclipse's divergence view** | agree | | | Diagnostics line; Eclipse showed −2.87/+1.46 |
| 3d | **Distance vs Eclipse's distance view** | agree | | | matched 58.4 mm on 2026-07-30 |
| 3d | **Curl vs Eclipse's curl view** | agree | | | Eclipse showed 0–2.15 on 2026-07-30; plugin value not yet compared — needs a rebuild after b9976ec |
| 2 | **Per-axis displacement vs Eclipse** | X=LR, Y=AP, Z=CC | | | see the reference table under test 3d; plugin value pending rebuild |
| 3d | Jacobian departure from 1 | plausible, INFO | | | judge against expected volume change |
| 3d | DVF gradient (max) | plausible, INFO | | | no TG-132 tolerance |
| 3d | Max displacement over the field | ≥ the old corner bound | | | true maximum now |
| 3c | DSC on a matched structure | plausible | | | |
| 3c | MDA < HD95 on the same structure | always | | | |
| 3c | Rigid case: deformation metrics absent | absent | | | |
| 3c | Real failure: NCC still shown as N/A | shown | | | |
| 5 | Baseline — cases collected | ≥ 20 | | | |
| — | Diagnostics tab | failures listed | | | |
| — | Eclipse version | | | | |
