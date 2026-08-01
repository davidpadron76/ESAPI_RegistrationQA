#!/usr/bin/env bash
#
# Everything that can be verified without an Eclipse in front of you:
#
#   1. the analytic maths        (tools/verify_math.py, pure Python)
#   2. that Models/ and Services/ actually compile
#   3. the DVF reader and metrics against API-shaped stubs (tools/DvfContractTests.cs)
#
# Step 2 matters more than it looks. This project has lost three field-test round trips to
# compile errors that a build here would have caught (CS0246 on a ViewModel dependency, CS0165
# twice on definite assignment through a short-circuit chain). A physicist's Eclipse session is
# the scarcest resource in the loop, and it should not be spent finding out that the branch does
# not build.
#
# Only the WPF-free core is compiled. Models/ and Services/ reference no VMS type and no
# System.Windows type in code — every mention is in a comment — so Mono can build them without
# the Varian assemblies. UI/ and ViewModels/ genuinely need WPF and are left to Visual Studio.
#
# Requirements: python3, and mono-devel for steps 2 and 3 (apt-get install mono-devel).
# Steps 2 and 3 are skipped with a notice when mcs is absent, so the script stays useful on a
# machine that only has Python.

set -u
# pipefail is load-bearing, not decoration. Without it, "mono tests.exe | tail -1" reports the
# exit status of tail — which always succeeds — so a failing test suite came back green. Found by
# deliberately breaking DeformationFieldMetrics to confirm the harness could still see it.
set -o pipefail

cd "$(dirname "$0")/.." || exit 1

OUT="${TMPDIR:-/tmp}/esapi-regqa-checks"
mkdir -p "$OUT"

status=0

echo "=== 1/3  analytic maths (verify_math.py) ==="
# Full output only on failure: 76 passing lines are noise, a failure needs all of them.
if maths_output=$(python3 tools/verify_math.py 2>&1); then
    echo "$maths_output" | tail -1
else
    echo "$maths_output"
    status=1
fi
echo

if ! command -v mcs >/dev/null 2>&1; then
    echo "=== 2/3, 3/3  SKIPPED: mcs not found (apt-get install mono-devel) ==="
    exit "$status"
fi

MONO_API=/usr/lib/mono/4.8-api
REFS="-r:$MONO_API/mscorlib.dll -r:$MONO_API/System.dll -r:$MONO_API/System.Core.dll -r:$MONO_API/Microsoft.CSharp.dll"

echo "=== 2/3  compiling Models/ and Services/ (warnings as errors) ==="
# shellcheck disable=SC2086
if mcs -target:library -langversion:latest -nostdlib -warn:4 -warnaserror \
       $REFS -out:"$OUT/core.dll" Models/*.cs Services/*.cs; then
    echo "  OK     0 errors, 0 warnings at level 4"
else
    echo "  FAILED core does not compile"
    exit 1
fi
echo

echo "=== 3/3  DVF reader and metrics against API-shaped stubs ==="
if mcs -langversion:latest -r:"$OUT/core.dll" -out:"$OUT/dvftests.exe" tools/DvfContractTests.cs; then
    if tests_output=$(mono "$OUT/dvftests.exe" 2>&1); then
        echo "$tests_output" | tail -1
    else
        echo "$tests_output"
        status=1
    fi
else
    echo "  FAILED the contract tests do not compile"
    status=1
fi

echo
if [ "$status" -eq 0 ]; then
    echo "All checks passed."
else
    echo "Some checks FAILED."
fi
exit "$status"
