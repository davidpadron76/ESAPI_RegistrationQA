#!/usr/bin/env python3
"""
Ports the numerical algorithms of RigidTransform, ImageGeometry and SimilarityCalculator
to Python so they can be verified, since the C# project only builds on Windows with the
Varian DLLs installed.
"""
import math, random

random.seed(20260726)
FAILS = []

def check(name, cond, detail=""):
    print(("  OK   " if cond else "  FAIL ") + f"  {name}" + (f"  [{detail}]" if detail and not cond else ""))
    if not cond:
        FAILS.append(name)

# ---------------------------------------------------------------- RigidTransform

def rot_x(a): return [[1,0,0],[0,math.cos(a),-math.sin(a)],[0,math.sin(a),math.cos(a)]]
def rot_y(a): return [[math.cos(a),0,math.sin(a)],[0,1,0],[-math.sin(a),0,math.cos(a)]]
def rot_z(a): return [[math.cos(a),-math.sin(a),0],[math.sin(a),math.cos(a),0],[0,0,1]]

def matmul(a,b):
    return [[sum(a[i][k]*b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]

def euler_from_matrix(m):
    """Exact replica of RigidTransform.GetEulerAnglesDegrees()."""
    r2d = 180.0/math.pi
    m00, m10, m20 = m[0][0], m[1][0], m[2][0]
    m11, m12 = m[1][1], m[1][2]
    m21, m22 = m[2][1], m[2][2]
    cos_pitch = math.sqrt(m21*m21 + m22*m22)
    gimbal = cos_pitch < 1e-6
    roll_y = math.atan2(-m20, cos_pitch)
    if not gimbal:
        pitch_x = math.atan2(m21, m22)
        yaw_z = math.atan2(m10, m00)
    else:
        pitch_x = math.atan2(-m12, m11)
        yaw_z = 0.0
    return pitch_x*r2d, roll_y*r2d, yaw_z*r2d, gimbal

print("\n=== Euler angle extraction (Rz·Ry·Rx convention) ===")
ok = True
worst = 0.0
for _ in range(2000):
    ax = random.uniform(-math.pi/2+0.05, math.pi/2-0.05)
    ay = random.uniform(-math.pi/2+0.15, math.pi/2-0.15)
    az = random.uniform(-math.pi+0.05, math.pi-0.05)
    R = matmul(rot_z(az), matmul(rot_y(ay), rot_x(ax)))
    px, ry, yz, g = euler_from_matrix(R)
    err = max(abs(px-math.degrees(ax)), abs(ry-math.degrees(ay)), abs(yz-math.degrees(az)))
    worst = max(worst, err)
    if err > 1e-6 or g:
        ok = False
check("recovers all three angles across 2000 random rotations", ok, f"max error {worst:.2e}deg")

# Gimbal lock: pitch = +90deg  ->  m20 = -1
R = matmul(rot_z(0.7), matmul(rot_y(math.pi/2), rot_x(0.3)))
px, ry, yz, g = euler_from_matrix(R)
check("detects gimbal lock at ry = +90deg", g, f"gimbal={g}")
check("produces no NaN under gimbal lock",
      all(not math.isnan(v) for v in (px, ry, yz)))
check("reports yaw = 0 under gimbal lock", abs(yz) < 1e-12)

# Under gimbal lock only the ax-az combination is observable; how it is split between
# pitch and yaw is arbitrary. The property that must hold is that the reported angles
# reconstruct exactly the same rotation matrix.
recon = matmul(rot_z(math.radians(yz)), matmul(rot_y(math.radians(ry)), rot_x(math.radians(px))))
check("angles under gimbal lock reconstruct the same rotation",
      all(abs(recon[i][j]-R[i][j]) < 1e-9 for i in range(3) for j in range(3)),
      f"px={px:.4f} ry={ry:.4f} yz={yz:.4f}")
check("pitch captures the observable combination (ax - az)",
      abs(px - math.degrees(0.3 - 0.7)) < 1e-6, f"px={px:.6f}")

# Comparison with the previous formula, which applied atan2(m21, m22) and atan2(m10, m00)
# with no guard at all.
def old_euler(m):
    return (math.degrees(math.atan2(m[2][1], m[2][2])),
            math.degrees(math.atan2(-m[2][0], math.sqrt(m[2][1]**2 + m[2][2]**2))),
            math.degrees(math.atan2(m[1][0], m[0][0])))

# On the in-memory composed matrix the degenerate elements are ~1e-17 rather than 0 and
# their errors are correlated, so the old formula yields three angles that happen to
# reconstruct the matrix - but individually they mean nothing: it reports pitch 17.19deg and
# yaw 40.11deg when neither is separately determined.
old = old_euler(R)
check("the previous formula reports spurious individual angles under gimbal lock",
      abs(old[0] - px) > 1.0 and abs(old[2] - yz) > 1.0,
      f"old=({old[0]:.2f},{old[1]:.2f},{old[2]:.2f}) vs new=({px:.2f},{ry:.2f},{yz:.2f})")

# With an exactly degenerate matrix - as the API would hand over, already rounded - the
# previous formula stops reconstructing even the rotation.
Rexact = [[0.0, math.sin(0.3-0.7), math.cos(0.3-0.7)],
          [0.0, math.cos(0.3-0.7), -math.sin(0.3-0.7)],
          [-1.0, 0.0, 0.0]]
old = old_euler(Rexact)
old_recon = matmul(rot_z(math.radians(old[2])),
                   matmul(rot_y(math.radians(old[1])), rot_x(math.radians(old[0]))))
old_err = max(abs(old_recon[i][j]-Rexact[i][j]) for i in range(3) for j in range(3))

npx, nry, nyz, ng = euler_from_matrix(Rexact)
new_recon = matmul(rot_z(math.radians(nyz)),
                   matmul(rot_y(math.radians(nry)), rot_x(math.radians(npx))))
new_err = max(abs(new_recon[i][j]-Rexact[i][j]) for i in range(3) for j in range(3))

check("with an exactly degenerate matrix the previous formula fails to reconstruct the rotation",
      old_err > 0.1, f"old error={old_err:.4f}")
check("the new one does reconstruct the degenerate rotation", new_err < 1e-12, f"new error={new_err:.2e}")
check("and it also flags gimbal lock to the user", ng)

print("\n=== Orthonormality and determinant ===")
def is_orthonormal(m, tol=1e-3):
    c = [[m[r][k] for r in range(3)] for k in range(3)]  # columns
    def dot(a,b): return sum(a[i]*b[i] for i in range(3))
    def cross(a,b): return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
    det = dot(c[0], cross(c[1], c[2]))
    for v in c:
        if abs(math.sqrt(dot(v,v)) - 1.0) > tol: return False, det
    if abs(dot(c[0],c[1])) > tol or abs(dot(c[0],c[2])) > tol or abs(dot(c[1],c[2])) > tol:
        return False, det
    return abs(det - 1.0) <= tol, det

R = matmul(rot_z(0.4), rot_x(0.2))
good, det = is_orthonormal(R)
check("accepts a pure rotation", good, f"det={det}")

scaled = [[R[i][j]*1.05 for j in range(3)] for i in range(3)]
good, det = is_orthonormal(scaled)
check("rejects a matrix with 5% scaling", not good, f"det={det}")

mirrored = [row[:] for row in R]
for i in range(3): mirrored[i][0] = -mirrored[i][0]
good, det = is_orthonormal(mirrored)
check("rejects a reflection (det = -1)", not good, f"det={det}")

print("\n=== 4x4 matrix convention detection ===")
def detect(raw, tol=1e-6):
    """Replica of RigidTransform.TryFromRawMatrix (detection part)."""
    last_row_h = (abs(raw[3][0])<tol and abs(raw[3][1])<tol and
                  abs(raw[3][2])<tol and abs(raw[3][3]-1)<tol)
    last_col_h = (abs(raw[0][3])<tol and abs(raw[1][3])<tol and
                  abs(raw[2][3])<tol and abs(raw[3][3]-1)<tol)
    if last_row_h and not last_col_h: return "column", raw
    if last_col_h and not last_row_h:
        return "row", [[raw[c][r] for c in range(4)] for r in range(4)]
    if last_row_h and last_col_h: return "both(pure rot)", raw
    return "undetermined", None

R = matmul(rot_z(0.3), rot_x(0.15))
t = (12.5, -3.25, 7.0)

col = [[R[0][0],R[0][1],R[0][2],t[0]],
       [R[1][0],R[1][1],R[1][2],t[1]],
       [R[2][0],R[2][1],R[2][2],t[2]],
       [0,0,0,1]]
kind, norm = detect(col)
check("identifies the column convention", kind == "column", kind)
check("reads the translation correctly in column convention",
      norm and all(abs(norm[i][3]-t[i]) < 1e-12 for i in range(3)))

row = [[col[c][r] for c in range(4)] for r in range(4)]   # transposed
kind, norm = detect(row)
check("identifies the row convention", kind == "row", kind)
check("recovers the translation after transposing",
      norm and all(abs(norm[i][3]-t[i]) < 1e-12 for i in range(3)))
check("recovers the rotation after transposing (not its inverse)",
      norm and all(abs(norm[i][j]-R[i][j]) < 1e-12 for i in range(3) for j in range(3)))

# The key point justifying the detection: the 3x3 submatrix is orthonormal under BOTH
# readings (the transpose of an orthonormal matrix is also orthonormal), so orthonormality
# cannot discriminate; only the position of the translation can. And getting it wrong yields
# the angles of the inverse rotation, with nothing failing.
mis_read = [[row[i][j] for j in range(3)] for i in range(3)]
good_mis, _ = is_orthonormal(mis_read)
check("the wrong reading also looks orthonormal (hence validating it is not enough)",
      good_mis)

px_ok, ry_ok, yz_ok, _ = euler_from_matrix(R)
px_bad, ry_bad, yz_bad, _ = euler_from_matrix(mis_read)
check("confusing the convention yields different angles (reason for the detection)",
      max(abs(px_ok-px_bad), abs(ry_ok-ry_bad), abs(yz_ok-yz_bad)) > 1.0,
      f"correct=({px_ok:.3f},{ry_ok:.3f},{yz_ok:.3f}) wrong=({px_bad:.3f},{ry_bad:.3f},{yz_bad:.3f})")

# The wrong reading corresponds exactly to the inverse rotation.
inv_check = matmul(mis_read, R)
check("the wrong reading is the inverse rotation",
      all(abs(inv_check[i][j] - (1.0 if i==j else 0.0)) < 1e-12 for i in range(3) for j in range(3)))

print("\n=== Maximum displacement over the FOV ===")
def apply(R, t, p):
    return tuple(sum(R[i][j]*p[j] for j in range(3)) + t[i] for i in range(3))

# Pure 2deg rotation about Z over a 500x500x300 box centred on the origin.
ang = math.radians(2.0)
Rz = rot_z(ang)
corners = [(x,y,z) for x in (-250,250) for y in (-250,250) for z in (-150,150)]
displacements = [math.dist(apply(Rz,(0,0,0),c), c) for c in corners]
max_corner = max(displacements)
# Theoretical maximum: 2*r*sin(theta/2) with r = XY-plane radius = sqrt(250^2+250^2)
r = math.hypot(250,250)
theoretical = 2*r*math.sin(ang/2)
check("the corner maximum matches the theoretical value",
      abs(max_corner - theoretical) < 1e-9, f"{max_corner:.6f} vs {theoretical:.6f}")

# Checks convexity: no interior point exceeds the corner maximum.
interior_max = 0.0
for _ in range(20000):
    p = (random.uniform(-250,250), random.uniform(-250,250), random.uniform(-150,150))
    interior_max = max(interior_max, math.dist(apply(Rz,(0,0,0),p), p))
check("no interior point exceeds the corner maximum",
      interior_max <= max_corner + 1e-9, f"interior={interior_max:.6f} > corners={max_corner:.6f}")

print("\n=== ImageGeometry: voxel <-> patient round-trip ===")
class Geom:
    def __init__(self, origin, xd, yd, zd, xr, yr, zr, xs, ys, zs):
        self.o, self.xd, self.yd, self.zd = origin, xd, yd, zd
        self.xr, self.yr, self.zr = xr, yr, zr
        self.xs, self.ys, self.zs = xs, ys, zs
    def to_patient(self, i, j, k):
        return tuple(self.o[a] + self.xd[a]*i*self.xr + self.yd[a]*j*self.yr + self.zd[a]*k*self.zr
                     for a in range(3))
    def to_voxel(self, p):
        d = [p[a]-self.o[a] for a in range(3)]
        dot = lambda u,v: sum(u[a]*v[a] for a in range(3))
        return dot(d,self.xd)/self.xr, dot(d,self.yd)/self.yr, dot(d,self.zd)/self.zr

# Oblique geometry (tilted gantry) to avoid the trivial axial case.
a = math.radians(12)
g = Geom((-250.0, -190.5, -400.0),
         (1,0,0), (0,math.cos(a),-math.sin(a)), (0,math.sin(a),math.cos(a)),
         0.9765625, 0.9765625, 2.5, 512, 512, 160)
worst = 0.0
for _ in range(5000):
    i,j,k = random.uniform(0,511), random.uniform(0,511), random.uniform(0,159)
    p = g.to_patient(i,j,k)
    bi,bj,bk = g.to_voxel(p)
    worst = max(worst, abs(bi-i), abs(bj-j), abs(bk-k))
check("exact round-trip on oblique geometry", worst < 1e-9, f"max error {worst:.2e} voxels")

print("\n=== FromFrames: relative transform between frames ===")
def from_frames(fr, to):
    """Replica of RigidTransform.FromFrames: R = R_to * R_from^T, t = O_to - R*O_from."""
    fa = [fr.xd, fr.yd, fr.zd]
    ta = [to.xd, to.yd, to.zd]
    R = [[sum(ta[x][r]*fa[x][c] for x in range(3)) for c in range(3)] for r in range(3)]
    rot_o = [sum(R[r][c]*fr.o[c] for c in range(3)) for r in range(3)]
    t = [to.o[r] - rot_o[r] for r in range(3)]
    return R, t

# One frame rotated 12deg relative to the other and shifted: the relative transform must
# map the origin of the first exactly onto the origin of the second.
g2 = Geom((-240.0, -180.0, -395.0), (1,0,0), (0,1,0), (0,0,1),
          0.9765625, 0.9765625, 2.5, 512, 512, 160)
R, t = from_frames(g, g2)
mapped = apply(R, t, g.o)
check("maps the source frame origin onto the target one",
      all(abs(mapped[i]-g2.o[i]) < 1e-9 for i in range(3)),
      f"{mapped} vs {g2.o}")
good, det = is_orthonormal(R)
check("the relative rotation is orthonormal", good, f"det={det}")

# The bug this fixes: subtracting components of the direction vectors.
old_rx = (g2.yd[1] - g.yd[1]) * 57.2958     # XDirection.y in the original
true_angle = math.degrees(a)
check("the previous formula (component subtraction) gave a wrong angle",
      abs(old_rx - true_angle) > 1.0,
      f"old={old_rx:.3f}deg vs true={true_angle:.3f}deg")

# ---------------------------------------------------------------- Similitud

print("\n=== SimilarityCalculator ===")
def ncc(a, b):
    n = len(a)
    sa, sb = sum(a), sum(b)
    sab = sum(x*y for x,y in zip(a,b))
    sa2, sb2 = sum(x*x for x in a), sum(y*y for y in b)
    cov = n*sab - sa*sb
    va, vb = n*sa2 - sa*sa, n*sb2 - sb*sb
    if va <= 0 or vb <= 0: return None
    return cov / math.sqrt(va*vb)

def robust_range(v):
    s = sorted(v); n = len(s)
    return s[int(0.01*(n-1))], s[int(0.99*(n-1))]

def nmi(a, b, bins):
    amin, amax = robust_range(a)
    bmin, bmax = robust_range(b)
    if amax <= amin or bmax <= bmin: return None
    asc, bsc = bins/(amax-amin), bins/(bmax-bmin)
    joint = [[0.0]*bins for _ in range(bins)]
    for x,y in zip(a,b):
        ai = min(bins-1, max(0, int((x-amin)*asc)))
        bi = min(bins-1, max(0, int((y-bmin)*bsc)))
        joint[ai][bi] += 1
    total = float(len(a))
    ma = [sum(r) for r in joint]
    mb = [sum(joint[x][y] for x in range(bins)) for y in range(bins)]
    ent = lambda c: -sum((v/total)*math.log(v/total,2) for v in c if v > 0)
    hj = -sum((joint[x][y]/total)*math.log(joint[x][y]/total,2)
              for x in range(bins) for y in range(bins) if joint[x][y] > 0)
    if hj <= 1e-12: return None
    return (ent(ma) + ent(mb)) / hj

base = [random.gauss(0, 300) for _ in range(60000)]

check("NCC = 1 for identical signals", abs(ncc(base, base) - 1.0) < 1e-9)
inv = [-x for x in base]
check("NCC = -1 with inverted contrast (sign preserved)",
      abs(ncc(base, inv) + 1.0) < 1e-9, f"{ncc(base,inv):.9f}")
affine = [2.5*x + 140 for x in base]
check("NCC invariant under an affine intensity transform",
      abs(ncc(base, affine) - 1.0) < 1e-9)
noisy = [x + random.gauss(0, 300) for x in base]
r = ncc(base, noisy)
check("NCC ~ 0.707 with equal-variance noise", abs(r - 0.7071) < 0.02, f"{r:.4f}")
indep = [random.gauss(0, 300) for _ in range(60000)]
check("NCC ~ 0 for independent signals", abs(ncc(base, indep)) < 0.02,
      f"{ncc(base,indep):.4f}")

v_same = nmi(base, base, 64)
check("NMI = 2 for identical signals (theoretical maximum)", abs(v_same - 2.0) < 1e-9, f"{v_same:.9f}")
v_indep = nmi(base, indep, 64)
check("NMI ~ 1 for independent signals (theoretical minimum)", 1.0 <= v_indep < 1.05, f"{v_indep:.4f}")
v_noisy = nmi(base, noisy, 64)
check("NMI orders correctly: independent < noisy < identical",
      v_indep < v_noisy < v_same, f"{v_indep:.4f} < {v_noisy:.4f} < {v_same:.4f}")

# Contrast with theory: for a bivariate Gaussian of correlation rho,
# I(A;B) = -0.5*log2(1-rho^2). With rho^2 = 0.5 -> 0.5 bits, and NMI = 2H/(2H - I).
rho2 = ncc(base, noisy) ** 2
mi_theory = -0.5 * math.log(1 - rho2, 2)
h = (2 * math.log(64, 2) * 0)  # placeholder: the discretised entropy is estimated below
# H(A) discretised over the same 64 bins:
amin, amax = robust_range(base)
hist = [0]*64
for x in base:
    hist[min(63, max(0, int((x-amin)*64/(amax-amin))))] += 1
ha = -sum((c/len(base))*math.log(c/len(base), 2) for c in hist if c > 0)
nmi_theory = (2*ha) / (2*ha - mi_theory)
check("noisy NMI agrees with the Gaussian theoretical value",
      abs(v_noisy - nmi_theory) < 0.02, f"measured={v_noisy:.4f} theoretical={nmi_theory:.4f}")

# The essential point: real NMI distinguishes cases the previous formula could not.
# The old version was nmi = 1.20 + 0.45*ncc, a linear function of the NCC.
nonlinear = [x*x/300.0 for x in base]      # strong dependence, low NCC
r_nl = ncc(base, nonlinear)
v_nl = nmi(base, nonlinear, 64)
check("NMI detects non-linear dependence where NCC cannot",
      abs(r_nl) < 0.15 and v_nl > 1.25, f"NCC={r_nl:.3f}, NMI={v_nl:.3f}")
old_formula = 1.20 + 0.45*r_nl
check("the previous formula would have given an NMI far below the real one",
      abs(old_formula - v_nl) > 0.15, f"old={old_formula:.3f} vs real={v_nl:.3f}")

# Normalised SSD
def ssd(a, b):
    lo, hi = robust_range(a)
    rng = hi - lo
    if rng <= 0: return None
    return (sum((x-y)**2 for x,y in zip(a,b))/len(a)) / (rng*rng)

check("SSD = 0 for identical signals", abs(ssd(base, base)) < 1e-15)
check("SSD > 0 and dimensionless with noise", 0 < ssd(base, noisy) < 1, f"{ssd(base,noisy):.5f}")
# Scale invariance: multiplying both signals must not change the normalised SSD.
s1 = ssd(base, noisy)
s2 = ssd([10*x for x in base], [10*x for x in noisy])
check("normalised SSD invariant under intensity scale",
      abs(s1 - s2) < 1e-9, f"{s1:.8f} vs {s2:.8f}")

print("\n=== Adaptive bin selection ===")
def choose_bins(n):
    b = int(math.floor(math.sqrt(n/20.0)))
    return max(16, min(64, b))
check("64 bins for a large sample", choose_bins(2000000) == 64, str(choose_bins(2000000)))
check("54 bins at the deformable budget (60k)", choose_bins(60000) == 54, str(choose_bins(60000)))
check("floor of 16 bins for a small sample", choose_bins(1000) == 16, str(choose_bins(1000)))

print()
if FAILS:
    print(f"{len(FAILS)} check(s) FAILED:")
    for f in FAILS: print("  - " + f)
    raise SystemExit(1)
print("All numerical checks passed.")
