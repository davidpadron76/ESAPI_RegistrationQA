# Changelog

Versions that changed what a number *means* are marked **breaking**. In a QA tool that is the
change that matters: a value you cannot compare against last month's is worse than a missing one,
because it still looks comparable.

The CSV dataset carries its own `SchemaVersion` column, bumped independently. **Files with
different schema versions must not be concatenated.**

---

## 3.0.0 — 2026-08-01

A major version because two things stopped being comparable with 2.15, not because the
feature list is long. Anyone pooling data across the two needs to know before they do it.

### Breaking

- **The Jacobian metrics are graded inside the patient outline, not over the whole field.**
  TG-132 Table III states the criterion per structure, and a deformation field's grid is a box
  that extends well past the anatomy into air, where the algorithm has no image to constrain it
  and folds freely. On the head phantom, 2.979 % of the whole box folded against 0.003 % inside
  `BODY` — 99.95 % of the folded points were in air. The tolerance is unchanged at 0 %; the
  region it applies to is now the patient outline (DICOM type `EXTERNAL`, or a `BODY`/`SKIN`-style
  name) wherever one can be placed on the field's grid, with a stated fallback to the whole field
  otherwise. Both clauses of the Table III row move together. **A folding percentage from this
  version is not comparable with one from an earlier version.**
- **CSV dataset schema 6 → 7.** Adds `JacobianDomain` and `JacobianNegPercent_WholeField` so a
  pooled dataset can tell the two domains apart and so the previous definition is still present
  by column rather than by assumption.

### Added

- **Deformation vector field metrics.** The DVF turned out to be readable through
  `MIRSNonRigidRegistration.NonRigidRegistration.DeformationField`, which earlier versions of
  this README stated was impossible. That made three previously unobtainable TG-132 quantities
  measurable: the Jacobian determinant as `det(I + grad u)` by central differences on the field's
  own grid, the DVF gradient, and a maximum displacement taken over every field point rather than
  the eight FOV corners.
- **`Jacobian departure from 1`** — the second clause of the Table III Jacobian row, measured as
  the larger of |p99 − 1| and |1 − p1| and reported ungraded, because the report ties the
  acceptable departure to the volume change the clinical scenario leads you to expect.
- **Per-structure Jacobian in the diagnostics**, which is how Table III states the criterion. The
  image whose structures share the field's frame is decided by measured bounding-box overlap and
  logged, rather than assumed — assuming wrongly displaces every mask by the registration's own
  translation while still producing a number.
- **Cross-check diagnostics against Eclipse's own field views**: Jacobian, divergence, distance,
  curl and per-axis displacement ranges, each derived from the same field read, so a disagreement
  in any of them points at the read rather than at the registration.
- **`inverse consistency: inverse check`** — states the residual between the reverse registration
  and the active one's analytic inverse. Comparing the two translations by eye is wrong once a
  rotation is involved, and it cost this project a false alarm: negation is the inverse of a pure
  translation only, and a 1.89° pitch over a 122 mm translation moved 4 mm onto another axis.
- **Progress feedback.** The window now appears immediately and reports ten named stages on a
  determinate progress bar, instead of Eclipse sitting with no window for several seconds.
- **`tools/run_checks.sh`** — analytic maths, a warnings-as-errors compile of the WPF-free core,
  and the DVF contract tests, in one command. Run it before handing a branch to a physicist.
- **`tools/DvfContractTests.cs`** — 81 checks that run the shipping code against API-shaped stubs,
  reaching the reflection layer that `verify_math.py` cannot. Every bug this project has hit in a
  clinic lived in that layer, never in the formulas.

### Fixed

- **The deformation field read was 18× slower than it needed to be**: 5043 ms → 283 ms, by
  replacing `Array.GetValue` plus three `PropertyInfo.GetValue` calls per element — 1.5 million
  boxing operations — with a typed generic and compiled accessors.
- **`InvalidOperationException` on opening the window**, from a two-way binding onto the read-only
  `StageCompleted`. `RangeBase.Value` is `BindsTwoWayByDefault`.
- **The results grid arrived with its columns collapsed.** `HorizontalScrollBarVisibility="Auto"`
  measures against infinite width, so the star-sized column could never resolve.
- The criterion column carried four copies of the field geometry and a restatement of reasoning
  already present in the tooltips, which made it unreadable even maximised.

### Verified against Eclipse

- Jacobian range, divergence range, displacement, the three field components, and the rigid
  transform's translation and rotation all agree — closing the axis-convention question, which
  this project had recorded as its largest open risk, on both the deformable and the rigid path.
- Curl reads 8 % low for an established reason (interior-only derivatives, boundary maximum) and
  is deliberately left that way.
- **DSC does not agree: Eclipse 0.90 against 0.953.** Open, and narrowed. The rasterised volumes
  match Eclipse's own to 0.6 % and 2.5 %, while the intersections implied by the two Dice values
  differ by 6.1 %, so the disagreement is in the overlap rather than in either mask. Grid
  resolution is eliminated in the opposite direction to the obvious guess — a coarser grid reads
  *lower*, and this plugin reads higher. What the same simulation does show is a ceiling: the Z
  contouring mismatch alone (99 planes at 0.4 mm against 8 at 5.0 mm for the same target)
  accounts for DSC ≈ 0.95 with no registration error at all, which is where the plugin sits. A
  new `structures: <id>: rasterisation` diagnostic reports each mask's volume, plane count and
  plane spacing so this can be reproduced elsewhere.

### Added after 3.0.0 was tagged

- **The DSC ceiling set by the two volumes.** A Dice cannot exceed 2·min(|A|,|B|)/(|A|+|B|),
  because the intersection cannot exceed the smaller volume — so the 0.90 tolerance needs the two
  volumes to agree within about **1.22×**, before the registration is considered at all. On a
  clinical MR→CT case the same target was contoured to 0.9 cm³ on one series and 2.6 cm³ on the
  other: DSC read 0.433 and failed the registration while MDA read 1.62 mm and passed
  comfortably. The two only look contradictory until the ceiling is computed — 0.514, of which
  the registration achieved 84 %. That row was reporting a contouring difference between two
  readers on two modalities, not an alignment error.

  The criterion column now carries `volumes cap DSC at 0.51`, but **only when the ceiling falls
  below the tolerance** — on a normal case the volumes are comparable and saying so would be
  noise. A `structures: DSC ceiling` warning states it in full and points at MDA instead, which
  is in millimetres and does not depend on volume. Twelve analytic checks pin the arithmetic.

  **The verdict is unchanged.** Whether a row should gate when its tolerance is unreachable is a
  question about grading, and changing that silently would be the same mistake as inventing a
  tolerance.

### Fixed after 3.0.0 was tagged

- **Contours on an oblique series shattered into spurious planes, and every rasterised volume
  followed.** A brain MR is routinely acquired oblique, so its contours are not planar in patient
  z. Two things assumed they were: the z of a polygon was taken from its **first vertex**, and two
  polygons counted as the same contour plane only within **1e-4 mm** — a tenth of a micron.

  On a clinical MR↔CT pair that produced contour "planes" 0.22 mm apart on a structure sliced in
  millimetres, gaps of 6.44, 5.78 and 4.02 mm where the acquisition is uniform, a median plane
  spacing that meant nothing, and rasterised volumes of **exactly half** Eclipse's figure in one
  run and 1.22× it in another. The finely-sliced axial CT structure was accurate throughout,
  which is why this survived: it only bites when the series is tilted.

  A polygon's z is now the **mean of its own vertices**, which is exact whenever the old reading
  was right and is the best single z for a tilted contour. The merge tolerance is **0.2 mm** —
  below any clinical slice spacing, above the numerical noise it exists to absorb. The tilt is
  measured rather than absorbed: `ContourSet.WidestPolygonZSpreadMm` reports how far a single
  polygon spans in z, and the plane description says outright that the series is not axial.

  Sixteen contract checks cover it, including that taking z from the first vertex splits each
  tilted slice in two and corrupts the derived spacing, while the mean recovers it exactly. Both
  halves were confirmed to bite by reverting them.

  **This supersedes what the previous entry recorded as an unexplained instability.** The volumes
  were not unstable — they were wrong in a way that depended on which structure was read.

- **A deliberate refusal to compute a metric was logged as an API failure.** On a CT–MR pair with
  different fields of view, the 5.5 % overlap check correctly declined to report intensity
  metrics, and did so under the heading *"N failure(s) while reading data from the API"*. Nothing
  had failed and nothing was read wrongly — both volumes loaded and the mapping worked. It is now
  a warning that says so explicitly. A peer reading the diagnostics should be able to trust that
  a failure count means something broke.

### Known, not yet fixed

- **The structure comparison grid is sized from unmapped contour bounds.** It is built from the
  union of both structures' extents *before* the registration is applied, so on a case with a
  large translation it spans the separation between them as well as the anatomy: on the head
  phantom, 207 mm in Z where each structure is 40 mm deep and both land in the same place once
  mapped. DSC, MDA and HD95 are computed from the masks, so the empty space does not change
  them — but the grid is capped at 160 samples per axis, and on a large structure the inflated
  span reaches that cap and coarsens the spacing. `BODY` on that case gets 3.42 mm instead of
  the 2.66 mm it would get from mapped bounds. Since a coarser grid reads a lower DSC, this
  makes large structures look slightly worse than they are.

---

## 2.15.0

The baseline this changelog starts from — the version in clinical field testing when the
deformation field turned out to be readable. Earlier history is in the commit log.
