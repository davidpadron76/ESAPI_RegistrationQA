# ESAPI probe

A throwaway diagnostic. It measures nothing, decides nothing, and writes nothing back to
Eclipse. Its only job is to answer one question that cannot be answered from outside a clinic:

**does this API hand out image and transform data at all in the Contouring workspace?**

Three builds of the audit plugin came back with the registration matrix reading as the identity
and every voxel reading as zero, on an Eclipse whose fusion displays correctly. Two independent
reads through two independent code paths both returning empty is not two bugs — it is one
question. Guessing at it has cost three round trips through the clinic; this ends the guessing
in one.

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

## Afterwards

Delete this folder. It has no place in a QA tool.
