# Validation protocol

## Why this document exists

The tool has never been checked against a known answer. What has been verified is the pure
mathematics — 38 analytic checks in `tools/verify_math.py`, covering Euler extraction, matrix
convention detection, voxel↔patient round-trips and the similarity metrics against their
theoretical values — plus the fact that it builds and runs.

Everything that touches the Varian API has been exercised on exactly one Eclipse
installation. And the tolerance limits in the four anatomical profiles were inherited from
the literature before the metrics were reimplemented; they have not been recalibrated against
the current definitions.

So: **the numbers are measurements, the colours are provisional.** Read the values, treat the
semaphore as a placeholder until the thresholds are derived from real data.

That is what this protocol is for. Tests 1 to 4 take an afternoon and close the open
questions that cannot be answered without an Eclipse in front of you. Section 5 is the part
that turns a group of testers into a dataset.

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
| DSC / HD95 | Not implemented. Reported as N/A with the reason stated. Requires contour rasterisation onto a common grid with structures matched by identifier. |
| Jacobian, DVF smoothness, max displacement for DIR | Not obtainable. They need the deformation vector field, which the scripting API does not expose. Reported as N/A rather than approximated from the linear component. |
| Deformable intensity metrics | Computed only when the API exposes a point-to-point mapping. Whether it does appears to depend on the Eclipse version — this is one of the things the testing should establish. |
| Threshold profiles | Inherited values, not recalibrated for the current metric definitions. See section 5. |
| Direction cosines | If the API does not expose them, canonical axial orientation is assumed and a warning is logged. A tilted-gantry acquisition would be misread; the Diagnostics tab will say so. |
| Performance | Runs synchronously on the UI thread, capped at ~2·10⁶ voxel pairs. The interface stops responding while it computes. |

---

## Collection sheet

| # | Test | Expected | Observed | Pass | Notes |
|---|---|---|---|---|---|
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
| 5 | Baseline — cases collected | ≥ 20 | | | |
| — | Diagnostics tab | failures listed | | | |
| — | Eclipse version | | | | |
