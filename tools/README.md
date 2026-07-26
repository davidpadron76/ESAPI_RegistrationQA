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
