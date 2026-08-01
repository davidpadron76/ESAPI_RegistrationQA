# ESAPI probe

A throwaway diagnostic. It measures nothing, decides nothing, and writes nothing back to
Eclipse. Its job is to answer questions about this API's real surface that cannot be settled
from outside a clinic — questions worth resolving before writing code against a guess.

**Original question, already answered:** does this API hand out image and transform data at
all in the Contouring workspace? Three builds of the audit plugin came back with the
registration matrix reading as the identity and every voxel reading as zero, on an Eclipse
whose fusion displays correctly. Two independent reads through two independent code paths both
returning empty was not two bugs — it was one question, and it turned out to be a fault in how
voxels were being requested, now fixed.

**Current question:** can a batch commissioning workflow be built on this API — locate phantom
images by name without a registration existing yet, enumerate every registration already in the
workspace to audit them in one pass, and is there any method to create or run a registration
from a script at all? See "Batch commissioning feasibility" below.

## Use

1. Open `probe/Probe.csproj` in Visual Studio (it is a separate project; it shares no code with
   the audit plugin and does not need it).
2. Build in **Release**. It lands in the same Eclipse plugins folder — the build prints the path.
3. In Eclipse, open the patient and select the registration you have been testing with.
4. Run **ESAPI_Probe** from the scripts dialog.
5. A window appears with the full dump, and the same text is saved to a `.txt` on your Desktop.

Send the file.

## What it reports

- Every object's real .NET type and its complete public surface: `ScriptContext`, the
  registration, `RigidRegistration`, both images, their `Frame` and `Image`.
- The value of every simple property, which is what shows whether an object carries data or
  only structure.
- The **raw matrix**, exactly as it arrives: its type, its shape and all sixteen cells, from
  four candidate properties and through a `[r,c]` indexer.
- **Ten different ways of asking for one plane of voxels** — three carriers × buffer shapes,
  plus the return-value forms — each with either the exception it raised or the actual minimum,
  maximum and first values it produced.

The probe reads the **middle** plane, never plane 0: on a head CT the first slice is uniform air
and an unwritten buffer looks exactly like a correctly written one. Any attempt whose buffer
comes back uniform is flagged `*** ALL VOXELS EQUAL — the call wrote nothing ***`.

## Reading the result

- **Some attempt reports `REAL DATA`** — the data is there, the audit plugin is asking for it
  the wrong way, and the fix is to copy whatever worked. One line of code.
- **Every attempt reports `ALL VOXELS EQUAL` or an exception** — this API does not expose pixel
  data in this context, and no amount of rewriting the plugin will change that. That is worth
  knowing before spending another evening on it.

Same logic for the matrix: sixteen cells that are not the identity mean the transform is
readable and we are reading the wrong property; sixteen cells that are the identity, on a pair
of series in different frames of reference, mean the object is not carrying the registration.

## Batch commissioning feasibility

Prompted by a question about automating TG-132 commissioning testing: given a phantom dataset
with known offsets, can the plugin find the right image pairs by name, and can it run the
registrations themselves rather than requiring each one to be created by hand in Eclipse first?

The probe now also dumps, regardless of whether a registration is active:

- **`Patient` image library.** Tries `Patient.Studies`, `.Courses`, `.Images`, `.ImageSets`,
  walking one level into any `Series`/`Images` property it finds, printing every `Id` it can
  read. This is what decides whether a naming convention like "basic phantom 1", "basic
  phantom 2"... can be matched into pairs before any registration exists.
- **The registration collection**, wherever it resolves (`Patient.Registrations`,
  `Registrations`, `Patient.MIRSRegistrations`) — every registration already sitting in the
  workspace, with its source and target image `Id`. This is the raw material for auditing all
  of them in one pass instead of one at a time, which needs no new API capability — the plugin
  already reads one registration; this is the same read, repeated.
- **A reflection sweep for any method with "Regist" in its name** on `ScriptContext`,
  `Patient`, and the registration collection itself — the three objects most likely to expose
  a way to create or run a registration, if one exists. Finding nothing here does not prove no
  such method exists anywhere in the API; it proves it is not on these three.

Read the result: if the sweep finds nothing beyond what the audit plugin already uses
(`TransformPoint`, `InverseTransformPoint`), the working assumption — that image registration is
a UI-only workflow in Eclipse's Registration workspace, not something a script can trigger —
holds, and a commissioning workflow can automate the pairing and the batch audit, but a
physicist still has to click "Register" for each pair. If it finds something else, that changes
what is worth building next.

## Afterwards

Delete this folder. It has no place in a QA tool.
