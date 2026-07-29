# tools/

## `verify_math.py`

Ports the numerical algorithms of `RigidTransform`, `ImageGeometry` and
`SimilarityCalculator` to Python and checks them against known analytic results.

It exists because the project only builds on Windows with the Varian DLLs installed, so the
computation logic cannot run in an ordinary CI environment. These algorithms are pure — they
depend on neither ESAPI nor WPF — so their correctness can be verified independently.

```bash
python3 tools/verify_math.py
```

Among other things it covers:

* recovery of Euler angles across 2000 random rotations;
* behaviour under gimbal lock, including reconstruction of the rotation;
* detection of the row/column convention of the 4x4 matrix, and why orthonormality is not
  enough to discriminate between them;
* exactness of the maximum displacement evaluated at the FOV corners, and its convexity;
* voxel ↔ patient round-trip on an oblique geometry;
* NCC, NMI and SSD against their theoretical values (including the NMI of a bivariate
  Gaussian).

**If any of those algorithms change in C#, mirror the change here and re-run it.** The script
is not compiled into the plugin and is not distributed with it.

Being a re-implementation is also its limit. It computes in float64 while the API stores
displacements as 32-bit floats, so it reports exact agreement for fields the real code cannot
represent exactly — see `DvfContractTests.cs`, which runs the shipping C# instead.

## `run_checks.sh`

Runs everything verifiable without an Eclipse: the Python maths, a real compile of `Models/` and
`Services/`, and the DVF contract tests.

```bash
./tools/run_checks.sh          # needs python3; mono-devel for steps 2 and 3
```

**Run this before handing a branch to a physicist.** Three field-test round trips have been lost
to compile errors alone — CS0246 on a ViewModel dependency, CS0165 twice on definite assignment
through a short-circuit chain. An Eclipse session is the scarcest resource in this project and
should not be spent discovering that the branch does not build.

Only the WPF-free core is compiled. Nothing in `Models/` or `Services/` references a VMS or
`System.Windows` type in code — every mention is in a comment — so Mono builds them without the
Varian assemblies. `UI/` and `ViewModels/` genuinely need WPF and are left to Visual Studio.

## `DvfContractTests.cs`

Exercises the **real** `DeformationFieldReader` and `DeformationFieldMetrics` against stub objects
shaped like the API's: a `VectorField` with its own grid and a
`GetVectors(VectorFloat[,,])` that fills the caller's buffer, nested inside a wrapper the way
`MIRSNonRigidRegistration.NonRigidRegistration.DeformationField` is.

This reaches the layer `verify_math.py` cannot. That script re-implements the arithmetic; these
tests run the shipping code, including the part most likely to break — locating the field on a
wrapper, allocating a buffer of a struct type unknown at compile time, invoking the method, and
reading each element's components back. Every bug this project has hit in a clinic lived in that
layer (`GetVoxels` writing nothing, `VVector` overload resolution, markers exposing `Points`
instead of `CenterPoint`, `DicomType` missing entirely), never in the formulas.

It also pins what the float32 storage costs: about 2e-8 on the Jacobian for a field whose values
are not exactly representable in binary. Physically irrelevant — TG-132 asks for nothing near
seven digits — but a test demanding more than the storage can carry would fail for a reason that
has nothing to do with the registration.

The suite is verified to be capable of failing: replacing `det(I + grad u)` with `det(grad u)` —
the one mistake that would make a near-rigid field read 0 instead of 1 — trips five checks.
