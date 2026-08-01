# Changelog

Versions that changed what a number *means* are marked **breaking**. In a QA tool that is the
change that matters: a value you cannot compare against last month's is worse than a missing one,
because it still looks comparable.

The CSV dataset carries its own `SchemaVersion` column, bumped independently. **Files with
different schema versions must not be concatenated.**

---

## Unreleased

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
- **DSC does not agree: Eclipse 0.90 against 0.953.** Open. The grid and the mapping direction
  are eliminated and the transform is verified; what remains is interpolation between contour
  planes, which on this case is severe — Eclipse's structure properties show the same target
  stored at 0.4 mm in Z on one series and 5.0 mm on the other. A new
  `structures: <id>: rasterisation` diagnostic reports each mask's volume and plane spacing so
  it can be compared against the TPS's own structure statistics.

---

## 2.15.0

The baseline this changelog starts from — the version in clinical field testing when the
deformation field turned out to be readable. Earlier history is in the commit log.
