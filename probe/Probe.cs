using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace VMS.IRS.Scripting
{
    /// <summary>
    /// A throwaway diagnostic. It measures nothing and decides nothing.
    ///
    /// Three builds of the audit plugin have come back with the registration matrix reading as
    /// the identity and every voxel reading as zero, on an Eclipse whose fusion displays
    /// correctly. Two independent reads through two independent code paths both returning
    /// empty is not two bugs; it is one question, and the question is whether this API hands
    /// out payload data at all in the Contouring workspace.
    ///
    /// Guessing at that from outside the clinic has cost three round trips. This script exists
    /// to end the guessing in one: it dumps what the API actually returns — every object's
    /// real type and members, the raw matrix cells, and the result of every way of asking for
    /// voxels — into a window and a text file.
    ///
    /// Build it, run it once, send the file. Either the data is there and the audit plugin is
    /// asking for it wrongly, or it is not there and no amount of rewriting will help.
    /// </summary>
    public class Script
    {
        private readonly StringBuilder _out = new StringBuilder();

        public Script() { }

        public void Execute(ScriptContext context)
        {
            try
            {
                Dump(context);
            }
            catch (Exception ex)
            {
                Line("PROBE ITSELF FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Line(ex.StackTrace);
            }

            string text = _out.ToString();
            string path = Save(text);
            Show(text, path);
        }

        // ------------------------------------------------------------------ the dump

        private void Dump(dynamic context)
        {
            Line("ESAPI registration probe");
            Line("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            Line("");

            if (context == null) { Line("context is null."); return; }

            Section("ScriptContext");
            Members((object)context);

            // These two run regardless of whether a registration is active, because the
            // question behind them — can images be found by name and registrations be batched
            // — has nothing to do with what happens to be open right now.
            DumpPatientLibrary(context);
            DumpRegistrationCollection(context);
            DumpDeformableRegistration(context);
            DumpThreadAffinity(context);

            dynamic registration = Try(() => context.Registration, "context.Registration");
            if (registration == null)
            {
                Line("");
                Line("No active registration. Open one in the Registration workspace and re-run.");
                return;
            }

            Section("Registration");
            Members((object)registration);
            Scalars((object)registration);

            // --- the matrix ------------------------------------------------------------
            dynamic rigid = Try(() => registration.RigidRegistration, "registration.RigidRegistration");
            if (rigid != null)
            {
                Section("RigidRegistration (the object that holds the matrix)");
                Members((object)rigid);
                Scalars((object)rigid);

                DumpMatrixCandidate(() => rigid.TransformationMatrix, "RigidRegistration.TransformationMatrix");
                DumpMatrixCandidate(() => rigid.Matrix, "RigidRegistration.Matrix");
                DumpMatrixCandidate(() => rigid.Transform, "RigidRegistration.Transform");
            }

            DumpMatrixCandidate(() => registration.TransformationMatrix, "Registration.TransformationMatrix");

            // --- the images ------------------------------------------------------------
            DumpImage(Try(() => registration.SourceImage, "registration.SourceImage"), "SourceImage");
            DumpImage(Try(() => registration.RegisteredImage, "registration.RegisteredImage"), "RegisteredImage");
        }

        /// <summary>
        /// Can every image in the chart be found by name without a registration already
        /// existing? A commissioning workflow needs to locate "basic phantom 1", "basic
        /// phantom 2"... before pairing and registering them, so this has to work from the
        /// patient object alone, not from an active registration's SourceImage/RegisteredImage.
        ///
        /// The nesting is unknown in advance — classic ESAPI goes Patient.Studies ->
        /// Study.Series -> Series.Images, VMS.IRS may not — so several candidate collections
        /// are tried and each item is walked one level down.
        /// </summary>
        private void DumpPatientLibrary(dynamic context)
        {
            Section("Patient image library (can images be found by name before any registration exists?)");

            dynamic patient = Try(() => context.Patient, "context.Patient");
            if (patient == null) return;

            Members((object)patient);
            Scalars((object)patient);

            DumpImageCollection(() => patient.Studies, "Patient.Studies");
            DumpImageCollection(() => patient.Courses, "Patient.Courses");
            DumpImageCollection(() => patient.Images, "Patient.Images");
            DumpImageCollection(() => patient.ImageSets, "Patient.ImageSets");
            // Seen on ScriptContext.Patient's own member list (VMS.IRS.Scripting.Patient) but
            // never queried: the Ids in the registration collection (CT_1, COORDENADAS,
            // kVCBCT_01a01...) match MIRSImage.Id, so this is likely the flat list that answers
            // "can every image be found by name" without walking Studies -> Series.
            DumpImageCollection(() => patient.MIRSImages, "Patient.MIRSImages");
        }

        private void DumpImageCollection(Func<object> accessor, string name)
        {
            object collection;
            try { collection = accessor(); }
            catch (Exception ex) { Line(name + " → " + ex.GetType().Name + ": " + Shorten(ex.Message)); return; }

            if (collection == null) { Line(name + " → null"); return; }

            var items = collection as IEnumerable;
            if (items == null) { Line(name + " → not enumerable (" + collection.GetType().Name + ")"); return; }

            Line(name + " → type " + collection.GetType().Name);
            int count = 0;
            foreach (dynamic item in items)
            {
                count++;
                if (count > 30) { Line("  ... (truncated after 30)"); break; }

                string id = TryGetString(() => item.Id, "(no Id)");
                Line("  [" + count + "] " + id + "  (" + ((object)item).GetType().Name + ")");

                // One level down: a Study holds Series, a Series holds Images. Harmless to try
                // both on anything — a property that does not exist is simply skipped.
                DumpNested(item, "Series", "      ");
                DumpNested(item, "Images", "      ");
            }
            if (count == 0) Line("  (empty)");
        }

        private void DumpNested(dynamic parent, string propertyName, string indent)
        {
            object nested;
            try
            {
                PropertyInfo p = ((object)parent).GetType()
                    .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return;
                nested = p.GetValue(parent, null);
            }
            catch { return; }

            var items = nested as IEnumerable;
            if (items == null) return;

            int count = 0;
            foreach (dynamic item in items)
            {
                count++;
                if (count > 15) { Line(indent + "... (truncated after 15)"); break; }
                string id = TryGetString(() => item.Id, "(no Id)");
                Line(indent + propertyName + "[" + count + "] " + id);
            }
        }

        /// <summary>
        /// Every registration already sitting in the workspace — the raw material for auditing
        /// all of them in one pass instead of one at a time — plus a reflection sweep for any
        /// method that might create or run one. Nothing found by this sweep is not proof that
        /// no such method exists anywhere in the API, only that it is not on the three objects
        /// most likely to carry it.
        /// </summary>
        private void DumpRegistrationCollection(dynamic context)
        {
            Section("Registration collection (batch auditing needs this) and a search for a create/run method");

            dynamic collection = null;
            string source = null;

            if (TryAssign(() => context.Patient.Registrations, out collection)) source = "Patient.Registrations";
            else if (TryAssign(() => context.Registrations, out collection)) source = "Registrations";
            else if (TryAssign(() => context.Patient.MIRSRegistrations, out collection)) source = "Patient.MIRSRegistrations";

            if (collection == null)
            {
                Line("no registration collection answered any of the known paths.");
            }
            else
            {
                Line("resolved via " + source);

                int count = 0;
                foreach (dynamic reg in collection)
                {
                    count++;
                    string id = TryGetString(() => reg.Id, "(no Id)");
                    string sourceId = TryGetString(() => reg.SourceImage.Id, "?");
                    string targetId = TryGetString(() => reg.RegisteredImage.Id, "?");
                    Line(string.Format(CultureInfo.InvariantCulture,
                        "  [{0}] Id={1}  {2} -> {3}  type={4}",
                        count, id, sourceId, targetId, ((object)reg).GetType().Name));
                }
                if (count == 0) Line("  (empty — no registrations exist yet in this workspace)");
            }

            Line("");
            Line("Method sweep for anything with \"Regist\" in its name (create/perform a registration):");
            SweepForRegistrationMethods((object)context, "ScriptContext");

            dynamic patient = Try(() => context.Patient, "context.Patient (for method sweep)");
            if (patient != null) SweepForRegistrationMethods((object)patient, "Patient");

            if (collection != null) SweepForRegistrationMethods((object)collection, "registration collection");
        }

        /// <summary>
        /// Follow-up to the batch-commissioning question above: once a deformable case is
        /// found, what does the API actually expose on it, and is the point-by-point mapping
        /// the plugin already uses (<see cref="PointMapperReader"/> in the main project) the
        /// only thing reachable, or is there a real deformation vector field underneath?
        ///
        /// Reuses PointMapperReader's own candidate method names and wrapper properties, in the
        /// same order, so a match found here is known — not guessed — to be reachable from the
        /// plugin. The DVF sweep below is the part PointMapperReader does not attempt: it never
        /// looks for the field itself, only for a method that maps one point at a time.
        /// </summary>
        private void DumpDeformableRegistration(dynamic context)
        {
            Section("Deformable registration (what can be read from a non-rigid case)");

            dynamic collection = null;
            if (!TryAssign(() => context.Patient.Registrations, out collection))
                TryAssign(() => context.Registrations, out collection);
            if (collection == null) TryAssign(() => context.Patient.MIRSRegistrations, out collection);

            if (collection == null)
            {
                Line("no registration collection answered any of the known paths — see the batch " +
                     "section above.");
                return;
            }

            dynamic deformable = null;
            string deformableId = null, sourceId = null, targetId = null;

            foreach (dynamic reg in collection)
            {
                // Exact-name match, not Contains: "MIRSNonRigidRegistration" contains the
                // substring "Rigid" (Non-Rigid-Registration), so excluding anything whose name
                // contains "Rigid" excluded the deformable case too — confirmed against a real
                // workspace that had both MIRSRigidRegistration and MIRSNonRigidRegistration
                // entries and reported "no non-rigid registration found".
                string typeName = ((object)reg).GetType().Name;
                if (string.Equals(typeName, "MIRSIdentityRegistration", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(typeName, "MIRSRigidRegistration", StringComparison.OrdinalIgnoreCase))
                    continue;

                deformable = reg;
                deformableId = TryGetString(() => reg.Id, "?");
                sourceId = TryGetString(() => reg.SourceImage.Id, "?");
                targetId = TryGetString(() => reg.RegisteredImage.Id, "?");
                break;
            }

            if (deformable == null)
            {
                Line("no non-rigid registration found in this workspace — every entry above is " +
                     "MIRSIdentityRegistration or MIRSRigidRegistration. Re-run on a patient with " +
                     "an elastic/deformable registration to answer this question.");
                return;
            }

            Line("found " + deformableId + "  " + sourceId + " -> " + targetId +
                 "  type=" + ((object)deformable).GetType().Name);
            Line("");

            Members((object)deformable);
            Scalars((object)deformable);

            // Same order PointMapperReader.CandidateHolders / CandidateMethods use, so this is
            // directly comparable to what the plugin would find on this same registration.
            string[] holderNames =
            {
                "RigidRegistration", "NonRigidRegistration", "DeformableRegistration",
                "DeformableRegistrationField", "Registration", "SpatialRegistration",
                "DeformationField", "VectorField"
            };

            string[] methodNames =
            {
                "TransformPointToRegistered", "TransformPointFromSourceToRegistered",
                "MapPointToRegistered", "TransformPoint", "MapPoint", "DeformPoint",
                "TransformPointToSource", "MapPointToSource", "InverseTransformPoint"
            };

            Line("");
            Line("Point-mapping method search (same names/holders PointMapperReader already tries):");
            ProbeMappingCandidates((object)deformable, null, methodNames);

            var holders = new List<KeyValuePair<string, object>>();
            foreach (string holderName in holderNames)
            {
                object holder;
                if (!TryReadObjectProperty((object)deformable, holderName, out holder) || holder == null)
                    continue;

                holders.Add(new KeyValuePair<string, object>(holderName, holder));

                Line("");
                Line(holderName + " → " + holder.GetType().FullName);
                Members(holder, "  ");
                Scalars(holder, "  ");
                ProbeMappingCandidates(holder, holderName, methodNames);
            }

            // The net PointMapperReader does not cast: it only ever looks for a point-mapping
            // method, never for the field itself. If Eclipse exposes the DVF directly under some
            // other name, this is what would catch it. Unlike the method-name search above,
            // this opens whatever it finds and tries to read from it, because knowing the DVF
            // property exists (DeformationField : VectorField, confirmed on a real case) says
            // nothing about whether it can actually be queried per point or per voxel.
            Line("");
            Line("Reflection sweep for anything DVF-shaped (name contains " +
                 "Deform/Vector/Displacement/Field/DVF/Warp), opened and probed for read access:");
            ExploreDvfShapedMembers((object)deformable, deformableId ?? "registration");
            foreach (KeyValuePair<string, object> holder in holders)
                ExploreDvfShapedMembers(holder.Value, holder.Key);
        }

        /// <summary>
        /// Can the API be read from a thread other than the one Eclipse handed the script?
        ///
        /// This decides how the audit plugin can report progress. Its measurement takes seconds —
        /// long enough that a window with no feedback reads as a hang — and the fix depends on
        /// whether the work can move off the UI thread. Nothing in the Varian documentation says,
        /// and guessing the permissive answer is the expensive mistake: an API with thread
        /// affinity may not throw, it may quietly hand back an empty buffer, which is exactly the
        /// failure mode that cost three sessions with GetVoxels.
        ///
        /// So each read is done twice, on the calling thread and on a worker, and the two results
        /// are compared. A read that throws off-thread answers the question. A read that succeeds
        /// but disagrees with the on-thread value answers it more urgently.
        /// </summary>
        private void DumpThreadAffinity(dynamic context)
        {
            Section("Thread affinity (can the API be read off the UI thread?)");

            Line("calling thread: managed id " + Thread.CurrentThread.ManagedThreadId +
                 ", apartment " + Thread.CurrentThread.GetApartmentState());
            Line("");

            dynamic image = Try(() => context.Image, "context.Image");
            if (image == null)
            {
                dynamic registration = Try(() => context.Registration, "context.Registration (for thread test)");
                if (registration != null) image = Try(() => registration.SourceImage, "registration.SourceImage");
            }

            if (image == null)
            {
                Line("no image reachable, so the thread test cannot run. Open a registration and re-run.");
                return;
            }

            // A scalar property first: the cheapest possible read, and if even this fails
            // off-thread then nothing else needs testing.
            CompareAcrossThreads("Image.Id", () => Convert.ToString(image.Id, CultureInfo.InvariantCulture));

            dynamic frame = Try(() => image.Frame, "image.Frame (for thread test)");
            if (frame != null)
            {
                CompareAcrossThreads("Frame.XSize",
                    () => Convert.ToString(frame.XSize, CultureInfo.InvariantCulture));

                // The real question. GetVoxels is the call that took three sessions to get right
                // on-thread; whether it also works off-thread is what decides the design.
                int xSize = ReadInt(frame, null, "XSize");
                int ySize = ReadInt(frame, null, "YSize");
                int zSize = ReadInt(frame, null, "ZSize");

                if (xSize > 0 && ySize > 0)
                {
                    int plane = zSize > 1 ? zSize / 2 : 0;
                    CompareAcrossThreads("Frame.GetVoxels(mid plane) range", () =>
                    {
                        var buffer = new ushort[xSize, ySize];
                        frame.GetVoxels(plane, buffer);

                        ushort min = ushort.MaxValue, max = ushort.MinValue;
                        foreach (ushort v in buffer)
                        {
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }
                        return "min " + min + ", max " + max;
                    });
                }
            }

            Line("");
            Line("Reading the result: identical values on both threads means the measurement could");
            Line("move to a background thread and the window could stay fully responsive. An");
            Line("exception, or a different value — especially an empty range where the calling");
            Line("thread saw real data — means it must not, and the plugin is right to keep the work");
            Line("on the UI thread and yield between stages instead.");
        }

        /// <summary>
        /// Runs the same read on this thread and on a worker, printing both. Exceptions are
        /// reported rather than propagated: an exception off-thread is the answer, not a fault.
        /// </summary>
        private void CompareAcrossThreads(string label, Func<string> read)
        {
            string onThread;
            try { onThread = read(); }
            catch (Exception ex) { onThread = ex.GetType().Name + ": " + Shorten(ex.Message); }

            string offThread = null;
            var worker = new Thread(() =>
            {
                try { offThread = read(); }
                catch (Exception ex) { offThread = ex.GetType().Name + ": " + Shorten(ex.Message); }
            });

            // MTA deliberately: an STA worker would give the API the apartment it may be relying
            // on and hide the very affinity being tested. A background thread in a real plugin
            // would be a thread-pool thread, which is MTA.
            worker.SetApartmentState(ApartmentState.MTA);
            worker.IsBackground = true;
            worker.Start();

            if (!worker.Join(TimeSpan.FromSeconds(30)))
                offThread = "*** did not return within 30 s — the call appears to block off-thread ***";

            Line("  " + label);
            Line("    on the calling thread : " + onThread);
            Line("    on a worker thread    : " + offThread);
            Line("    " + (string.Equals(onThread, offThread, StringComparison.Ordinal)
                ? "MATCH — this read is thread-safe"
                : "*** DIFFERS — this read has thread affinity ***"));
        }

        private void ProbeMappingCandidates(object target, string holderName, string[] methodNames)
        {
            Type type;
            try { type = target.GetType(); }
            catch { return; }

            string prefix = holderName != null ? holderName + "." : string.Empty;

            foreach (string name in methodNames)
            {
                MethodInfo method;
                try
                {
                    method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal) &&
                                              m.GetParameters().Length == 1 &&
                                              m.ReturnType != typeof(void));
                }
                catch
                {
                    method = null;
                }

                if (method == null) continue;

                Line("  " + prefix + name + "(" + method.GetParameters()[0].ParameterType.Name +
                     ") : " + method.ReturnType.Name + "  <-- candidate found");
            }
        }

        private static bool TryReadObjectProperty(object instance, string name, out object value)
        {
            value = null;
            if (instance == null) return false;

            try
            {
                PropertyInfo property = instance.GetType().GetProperty(
                    name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
                    return false;

                value = property.GetValue(instance, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Finds properties whose name suggests a deformation vector field, reads each one
        /// (not just names it), and hands the result to <see cref="ProbeDvfField"/> to see
        /// whether it can actually be queried.
        /// </summary>
        private void ExploreDvfShapedMembers(object instance, string label)
        {
            if (instance == null) return;

            Type type;
            try { type = instance.GetType(); }
            catch { return; }

            string[] keywords = { "Deform", "Vector", "Displacement", "Field", "DVF", "Warp" };

            List<PropertyInfo> props;
            try
            {
                props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0 &&
                                keywords.Any(k => p.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();
            }
            catch (Exception ex)
            {
                Line("  " + label + ": sweep failed — " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (props.Count == 0)
            {
                Line("  " + label + ": nothing DVF-shaped in its properties");
                return;
            }

            foreach (PropertyInfo p in props)
            {
                object value;
                try
                {
                    value = p.GetValue(instance, null);
                }
                catch (Exception ex)
                {
                    Line("  " + label + "." + p.Name + " : " + p.PropertyType.Name + " → " +
                         ex.GetType().Name + ": " + Shorten(ex.Message));
                    continue;
                }

                ProbeDvfField(label + "." + p.Name, value);
            }
        }

        /// <summary>
        /// Opens a candidate DVF object: its own member surface, then every indexer and every
        /// read-like method that takes 1-3 simple numeric parameters, invoked with zeros and
        /// reported with whatever came back — a real value, or the exception, either one
        /// answers the question. Restricted to primitive numeric parameters because there is no
        /// way to construct an arbitrary VVector/Point argument by reflection without already
        /// knowing the vector type, which is exactly what this is trying to discover.
        /// </summary>
        private void ProbeDvfField(string label, object field)
        {
            if (field == null) { Line("  " + label + " → null"); return; }

            Line("");
            Line(label + " → " + field.GetType().FullName);
            Members(field, "  ");
            Scalars(field, "  ");

            Type type = field.GetType();

            try
            {
                var indexers = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length >= 1 && p.GetIndexParameters().Length <= 3 &&
                                p.GetIndexParameters().All(ip => IsProbeableType(ip.ParameterType)))
                    .ToList();

                if (indexers.Count == 0)
                {
                    Line("  no numeric-argument indexer found on " + type.Name);
                }

                foreach (PropertyInfo indexer in indexers)
                    InvokeAndReport(args => indexer.GetValue(field, args), indexer.GetIndexParameters(),
                        "  indexer");
            }
            catch (Exception ex)
            {
                Line("  indexer probe failed — " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName &&
                                m.ReturnType != typeof(void) &&
                                m.GetParameters().Length >= 1 && m.GetParameters().Length <= 3 &&
                                m.GetParameters().All(p => IsProbeableType(p.ParameterType)))
                    .ToList();

                foreach (MethodInfo m in candidates)
                    InvokeAndReport(args => m.Invoke(field, args), m.GetParameters(), "  " + m.Name);
            }
            catch (Exception ex)
            {
                Line("  method probe failed — " + ex.GetType().Name + ": " + ex.Message);
            }

            // GetVectors(VectorFloat[,,] preallocatedBuffer) doesn't fit the "1-3 simple
            // numeric parameters" shape above — it takes one 3D array of a struct type unknown
            // at compile time. Now that a real case has shown the exact method name and buffer
            // shape, probe it directly rather than waiting for the generic sweep to stumble on
            // it (it never would: an array parameter is not IsProbeableType).
            ProbeBulkVectorMethod(field, type);
        }

        /// <summary>
        /// Mirrors the GetVoxels probe that found the original intensity-reading bug: build the
        /// buffer via reflection from XSize/YSize/ZSize, call the method, then check whether the
        /// middle plane — never plane 0, which can look identical to an unwritten buffer — came
        /// back uniform or carries real per-voxel vectors.
        /// </summary>
        private void ProbeBulkVectorMethod(object field, Type fieldType)
        {
            List<MethodInfo> candidates;
            try
            {
                candidates = fieldType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.ReturnType == typeof(void) &&
                                m.GetParameters().Length == 1 &&
                                m.GetParameters()[0].ParameterType.IsArray &&
                                m.GetParameters()[0].ParameterType.GetArrayRank() == 3)
                    .ToList();
            }
            catch (Exception ex)
            {
                Line("  bulk-array method search failed — " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (candidates.Count == 0)
            {
                Line("  no method taking a single 3D-array buffer found on " + fieldType.Name);
                return;
            }

            int xSize = ReadIntProperty(field, "XSize");
            int ySize = ReadIntProperty(field, "YSize");
            int zSize = ReadIntProperty(field, "ZSize");

            if (xSize <= 0 || ySize <= 0 || zSize <= 0)
            {
                Line("  cannot size the buffer — XSize/YSize/ZSize did not answer with positive values");
                return;
            }

            foreach (MethodInfo m in candidates)
            {
                Type elementType = m.GetParameters()[0].ParameterType.GetElementType();
                Line("  trying " + m.Name + "(" + elementType.Name + "[" + xSize + "," + ySize + "," +
                     zSize + "])");

                Array buffer;
                try
                {
                    buffer = Array.CreateInstance(elementType, xSize, ySize, zSize);
                }
                catch (Exception ex)
                {
                    Line("    could not allocate buffer — " + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                try
                {
                    m.Invoke(field, new object[] { buffer });
                }
                catch (Exception ex)
                {
                    Exception real = ex.InnerException ?? ex;
                    Line("    " + m.Name + " → " + real.GetType().Name + ": " + Shorten(real.Message));
                    continue;
                }

                int midZ = zSize > 1 ? zSize / 2 : 0;
                DescribeVectorPlane(buffer, elementType, xSize, ySize, midZ, m.Name);
            }
        }

        private static int ReadIntProperty(object instance, string name)
        {
            try
            {
                PropertyInfo p = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return -1;
                return Convert.ToInt32(p.GetValue(instance, null), CultureInfo.InvariantCulture);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Reports whether the middle plane of the returned buffer carries real, non-uniform
        /// per-voxel vectors or came back untouched. Reads each element's x/y/z generically
        /// (property or field, either casing — the same lesson PointMapperReader already
        /// learned reading the point-mapping return value) rather than assuming a fixed layout
        /// for a struct type that isn't known until this line runs.
        /// </summary>
        private void DescribeVectorPlane(Array buffer, Type elementType, int xSize, int ySize, int z,
            string methodName)
        {
            object probe;
            try { probe = Activator.CreateInstance(elementType); }
            catch { probe = null; }

            if (probe != null && ReadXyz(probe) == null)
            {
                Line("    " + methodName + " ran without throwing, but " + elementType.Name +
                     " exposes no recognisable X/Y/Z members — its own surface:");
                Members(probe, "      ");
                return;
            }

            int sampled = 0;
            double minLen = double.MaxValue, maxLen = double.MinValue;
            var firstValues = new List<string>();

            for (int y = 0; y < ySize && sampled < 4096; y++)
            {
                for (int x = 0; x < xSize && sampled < 4096; x++)
                {
                    object element = buffer.GetValue(x, y, z);
                    double[] xyz = ReadXyz(element);
                    if (xyz == null) continue;

                    double len = Math.Sqrt(xyz[0] * xyz[0] + xyz[1] * xyz[1] + xyz[2] * xyz[2]);
                    if (len < minLen) minLen = len;
                    if (len > maxLen) maxLen = len;
                    sampled++;

                    if (firstValues.Count < 8)
                        firstValues.Add(string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})",
                            xyz[0], xyz[1], xyz[2]));
                }
            }

            if (sampled == 0)
            {
                Line("    " + methodName + " → could not read any element back as X/Y/Z");
                return;
            }

            string verdict = minLen == maxLen
                ? "*** ALL VECTORS EQUAL (length " + minLen.ToString("F6", CultureInfo.InvariantCulture) +
                  ") — looks like the call wrote nothing ***"
                : "REAL DATA";

            Line(string.Format(CultureInfo.InvariantCulture,
                "    {0} → plane z={1}, {2} samples, |v| min {3:F4} max {4:F4} → {5}",
                methodName, z, sampled, minLen, maxLen, verdict));
            Line("    first values: " + string.Join(", ", firstValues.ToArray()));
        }

        private static double[] ReadXyz(object element)
        {
            if (element == null) return null;
            Type t = element.GetType();

            double? x = ReadNumericMember(t, element, "X", "x");
            double? y = ReadNumericMember(t, element, "Y", "y");
            double? z = ReadNumericMember(t, element, "Z", "z");

            if (!x.HasValue || !y.HasValue || !z.HasValue) return null;
            return new[] { x.Value, y.Value, z.Value };
        }

        private static double? ReadNumericMember(Type t, object instance, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.CanRead)
                        return Convert.ToDouble(p.GetValue(instance, null), CultureInfo.InvariantCulture);

                    FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                    if (f != null)
                        return Convert.ToDouble(f.GetValue(instance), CultureInfo.InvariantCulture);
                }
                catch
                {
                    // try the next candidate name
                }
            }
            return null;
        }

        private void InvokeAndReport(Func<object[], object> invoke, ParameterInfo[] parameters, string label)
        {
            object[] args = parameters.Select(p => DefaultProbeValue(p.ParameterType)).ToArray();
            string argsText = string.Join(", ", args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)).ToArray());

            try
            {
                object result = invoke(args);
                Line(label + "(" + argsText + ") → " +
                     (result == null ? "null" : result.GetType().Name + " = " +
                         Convert.ToString(result, CultureInfo.InvariantCulture)));
            }
            catch (Exception ex)
            {
                Exception real = ex.InnerException ?? ex;
                Line(label + "(" + argsText + ") → " + real.GetType().Name + ": " + Shorten(real.Message));
            }
        }

        private static bool IsProbeableType(Type t)
        {
            return t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(float);
        }

        private static object DefaultProbeValue(Type t)
        {
            if (t == typeof(int)) return 0;
            if (t == typeof(long)) return 0L;
            if (t == typeof(double)) return 0.0;
            if (t == typeof(float)) return 0.0f;
            return null;
        }

        private static bool TryAssign(Func<object> accessor, out dynamic value)
        {
            try
            {
                object v = accessor();
                value = v;
                return v != null;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private void SweepForRegistrationMethods(object instance, string label)
        {
            if (instance == null) return;

            Type type;
            try { type = instance.GetType(); }
            catch { return; }

            try
            {
                var matches = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName &&
                                m.Name.IndexOf("Regist", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                if (matches.Count == 0)
                {
                    Line("  " + label + ": no method with \"Regist\" in its name");
                    return;
                }

                foreach (MethodInfo m in matches)
                {
                    Line("  " + label + ": " + m.Name + "(" +
                         string.Join(", ", m.GetParameters()
                             .Select(p => p.ParameterType.Name + " " + p.Name).ToArray()) +
                         ") : " + m.ReturnType.Name);
                }
            }
            catch (Exception ex)
            {
                Line("  " + label + ": method sweep failed — " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string TryGetString(Func<object> accessor, string fallback)
        {
            try
            {
                object v = accessor();
                return v == null ? fallback : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Everything about one image: which object carries the geometry, what it exposes, and
        /// the result of every way of asking it for a plane of voxels.
        /// </summary>
        private void DumpImage(dynamic imageLike, string label)
        {
            Section(label);

            if (imageLike == null) { Line("null."); return; }

            Members((object)imageLike);
            Scalars((object)imageLike);

            dynamic frame = Try(() => imageLike.Frame, label + ".Frame");
            dynamic image = Try(() => imageLike.Image, label + ".Image");

            if (frame != null)
            {
                Section(label + ".Frame");
                Members((object)frame);
                Scalars((object)frame);
            }

            if (image != null)
            {
                Section(label + ".Image");
                Members((object)image);
                Scalars((object)image);
            }

            // Sizes are read from whichever object answers, because the buffer has to match.
            int xSize = ReadInt(frame, image, "XSize");
            int ySize = ReadInt(frame, image, "YSize");
            int zSize = ReadInt(frame, image, "ZSize");

            Line("");
            Line(string.Format(CultureInfo.InvariantCulture,
                "Sizes used for the voxel probes: X={0} Y={1} Z={2}", xSize, ySize, zSize));

            if (xSize <= 0 || ySize <= 0)
            {
                Line("Cannot probe voxels without plane dimensions.");
                return;
            }

            // The middle plane, never plane 0: on a head CT the first slice is uniform air and
            // an unwritten buffer looks exactly the same as a correctly written one.
            int plane = zSize > 1 ? zSize / 2 : 0;
            Line("Probing plane " + plane + " (the middle one).");
            Line("");

            ProbeVoxels(frame, "Frame", xSize, ySize, plane);
            ProbeVoxels(image, "Image", xSize, ySize, plane);
            ProbeVoxels(imageLike, label, xSize, ySize, plane);

            DumpDisplayRamp(frame, "Frame");
            DumpDisplayRamp(image, "Image");

            ProbeImageProfile(frame, "Frame", xSize, ySize, plane);
        }

        /// <summary>
        /// The other door to the same data.
        ///
        /// <c>GetImageProfile</c> samples intensities along a line rather than filling a plane,
        /// and it is the one voxel-access route on this API that has never been tried. If it
        /// returns real numbers while GetVoxels returns an untouched buffer, then the pixel data
        /// is there and the whole intensity side of the audit can be rebuilt on line profiles
        /// across the volume.
        ///
        /// The line runs through the middle of the plane, left to right in patient coordinates,
        /// so it crosses anatomy rather than air.
        /// </summary>
        private void ProbeImageProfile(dynamic frame, string carrierName, int xSize, int ySize, int plane)
        {
            if (frame == null) return;

            Line("");
            Line("  --- GetImageProfile, the untried route ---");

            // Seeded for definite assignment: the calls are dynamically bound (frame is
            // dynamic), so the compiler cannot verify the out parameters through the ||
            // short-circuit the way it would for a statically-bound call.
            dynamic start = null, stop = null;
            if (!TryVoxelToDicom(frame, 0, ySize / 2, plane, out start) ||
                !TryVoxelToDicom(frame, xSize - 1, ySize / 2, plane, out stop))
            {
                Line("  could not convert the line endpoints to patient coordinates");
                return;
            }

            Line("  line from " + Convert.ToString((object)start, CultureInfo.InvariantCulture) +
                 " to " + Convert.ToString((object)stop, CultureInfo.InvariantCulture));

            var buffer = new double[xSize];

            try
            {
                object profile = frame.GetImageProfile(start, stop, buffer);

                int finite = 0;
                double min = double.MaxValue, max = double.MinValue;
                foreach (double v in buffer)
                {
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    finite++;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }

                string state = finite == 0 ? "*** every sample non-finite ***"
                    : min == max ? "*** ALL SAMPLES EQUAL — the call wrote nothing ***"
                    : "REAL DATA";

                Line(string.Format(CultureInfo.InvariantCulture,
                    "  {0}.GetImageProfile → OK, buffer min {1}, max {2} → {3}",
                    carrierName, min, max, state));

                var first = new List<string>();
                for (int i = 0; i < Math.Min(16, buffer.Length); i++)
                    first.Add(buffer[i].ToString("F1", CultureInfo.InvariantCulture));
                Line("  buffer first values: " + string.Join(", ", first.ToArray()));

                if (profile != null)
                {
                    Line("  returned " + profile.GetType().FullName);
                    Members(profile, "  ");
                    Scalars(profile, "  ");
                    DumpProfileSamples(profile);
                }
            }
            catch (Exception ex)
            {
                Line("  " + carrierName + ".GetImageProfile → " + ex.GetType().Name + ": " + Shorten(ex.Message));
            }
        }

        /// <summary>
        /// The returned ImageProfile may carry the samples itself rather than through the
        /// buffer, so whatever it enumerates is printed too.
        /// </summary>
        private void DumpProfileSamples(object profile)
        {
            var sequence = profile as IEnumerable;
            if (sequence == null) return;

            var values = new List<string>();
            int taken = 0;

            try
            {
                foreach (object item in sequence)
                {
                    values.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                    if (++taken >= 12) break;
                }
            }
            catch (Exception ex)
            {
                Line("  enumerating the profile → " + ex.GetType().Name + ": " + Shorten(ex.Message));
                return;
            }

            Line("  profile enumerates: " + string.Join(", ", values.ToArray()));
        }

        private bool TryVoxelToDicom(dynamic frame, int i, int j, int k, out dynamic patientPoint)
        {
            patientPoint = null;

            try
            {
                // VoxelToDicom takes a VVector, and the probe has no compile-time reference to
                // that type, so one is built from the frame's own Origin.
                object template = frame.Origin;
                Type vectorType = template.GetType();

                ConstructorInfo constructor = vectorType.GetConstructor(
                    new[] { typeof(double), typeof(double), typeof(double) });
                if (constructor == null) return false;

                // Must be dynamic, not object: frame.VoxelToDicom(...) is a dynamically bound
                // call (frame is dynamic), and the DLR resolves overloads from the argument's
                // *static* type unless that type is dynamic. A statically-typed object here
                // makes the binder look for VoxelToDicom(object) and fail to find it, even
                // though the runtime value is a real VVector built by the same reflection.
                dynamic voxel = constructor.Invoke(new object[] { (double)i, (double)j, (double)k });
                patientPoint = frame.VoxelToDicom(voxel);

                // No null check: the prior run's exception ("Operator '!=' cannot be applied to
                // operands of type 'VMS.CA.Scripting.VVector' and '<null>'") shows VVector is a
                // struct, and patientPoint != null is itself a dynamically bound comparison —
                // there is no operator!= against <null> for it, so the check throws rather than
                // answering the question. Reaching this line without an exception is success.
                return true;
            }
            catch (Exception ex)
            {
                Line("  VoxelToDicom → " + ex.GetType().Name + ": " + Shorten(ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Every plausible way of asking for a plane, each reported with what came back: an
        /// exception, or the actual value range and the first few numbers.
        /// </summary>
        private void ProbeVoxels(dynamic carrier, string carrierName, int xSize, int ySize, int plane)
        {
            if (carrier == null) return;

            var ushortXy = new ushort[xSize, ySize];
            Attempt(carrierName + ".GetVoxels(int, ushort[x,y])",
                () => carrier.GetVoxels(plane, ushortXy), () => Describe(ushortXy));

            var intXy = new int[xSize, ySize];
            Attempt(carrierName + ".GetVoxels(int, int[x,y])",
                () => carrier.GetVoxels(plane, intXy), () => Describe(intXy));

            if (xSize != ySize)
            {
                var ushortYx = new ushort[ySize, xSize];
                Attempt(carrierName + ".GetVoxels(int, ushort[y,x])",
                    () => carrier.GetVoxels(plane, ushortYx), () => Describe(ushortYx));

                var intYx = new int[ySize, xSize];
                Attempt(carrierName + ".GetVoxels(int, int[y,x])",
                    () => carrier.GetVoxels(plane, intYx), () => Describe(intYx));
            }

            var flatUshort = new ushort[xSize * ySize];
            Attempt(carrierName + ".GetVoxels(int, ushort[])",
                () => carrier.GetVoxels(plane, flatUshort), () => Describe(flatUshort));

            // Some APIs return the plane instead of filling a buffer.
            AttemptReturning(carrierName + ".GetVoxels(int) → returned value",
                () => carrier.GetVoxels(plane));

            AttemptReturning(carrierName + ".GetVoxelPlane(int)", () => carrier.GetVoxelPlane(plane));
            AttemptReturning(carrierName + ".Voxels", () => carrier.Voxels);
        }

        private void DumpDisplayRamp(dynamic carrier, string carrierName)
        {
            if (carrier == null) return;

            AttemptReturning(carrierName + ".VoxelToDisplayValue(0)", () => carrier.VoxelToDisplayValue(0));
            AttemptReturning(carrierName + ".VoxelToDisplayValue(1000)", () => carrier.VoxelToDisplayValue(1000));
        }

        // ------------------------------------------------------------------ matrix

        /// <summary>
        /// Prints the matrix exactly as it arrives: its real type, its shape, and every cell.
        /// A matrix that reads as the identity between two series in different frames of
        /// reference cannot be the registration, so the raw numbers are the evidence needed.
        /// </summary>
        private void DumpMatrixCandidate(Func<object> accessor, string name)
        {
            object value;
            try
            {
                value = accessor();
            }
            catch (Exception ex)
            {
                Line(name + " → " + ex.GetType().Name + ": " + Shorten(ex.Message));
                return;
            }

            if (value == null) { Line(name + " → null"); return; }

            Line(name + " → type " + value.GetType().FullName);

            var array = value as Array;
            if (array != null)
            {
                Line("  rank " + array.Rank + ", lengths " +
                     string.Join("x", Enumerable.Range(0, array.Rank)
                         .Select(d => array.GetLength(d).ToString(CultureInfo.InvariantCulture)).ToArray()));

                var cells = new List<string>();
                foreach (object cell in array)
                    cells.Add(Convert.ToString(cell, CultureInfo.InvariantCulture));

                Line("  cells: " + string.Join(", ", cells.ToArray()));
                return;
            }

            // Not an array: print its members and their scalar values, so a matrix stored as
            // M11..M44 or behind an indexer still shows up.
            Members(value, "  ");
            Scalars(value, "  ");

            try
            {
                PropertyInfo indexer = value.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.GetIndexParameters().Length == 2);

                if (indexer != null)
                {
                    var cells = new List<string>();
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                            cells.Add(Convert.ToString(indexer.GetValue(value, new object[] { r, c }),
                                CultureInfo.InvariantCulture));

                    Line("  via [r,c] indexer: " + string.Join(", ", cells.ToArray()));
                }
            }
            catch (Exception ex)
            {
                Line("  [r,c] indexer → " + ex.GetType().Name + ": " + Shorten(ex.Message));
            }
        }

        // ------------------------------------------------------------------ reflection

        /// <summary>Public properties and methods, with signatures. No values.</summary>
        private void Members(object instance, string indent = "")
        {
            if (instance == null) { Line(indent + "null"); return; }

            Type type;
            try { type = instance.GetType(); }
            catch { Line(indent + "type unavailable"); return; }

            Line(indent + "type: " + type.FullName);

            try
            {
                foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    string args = p.GetIndexParameters().Length == 0
                        ? string.Empty
                        : "[" + string.Join(", ", p.GetIndexParameters()
                            .Select(q => q.ParameterType.Name).ToArray()) + "]";
                    Line(indent + "  prop " + p.Name + args + " : " + p.PropertyType.Name);
                }

                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                             .Where(m => !m.IsSpecialName))
                {
                    Line(indent + "  meth " + m.Name + "(" +
                         string.Join(", ", m.GetParameters()
                             .Select(q => q.ParameterType.Name + " " + q.Name).ToArray()) +
                         ") : " + m.ReturnType.Name);
                }
            }
            catch (Exception ex)
            {
                Line(indent + "  reflection failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Values of the properties that are simple enough to print. This is what shows
        /// whether the object carries data or only structure.
        /// </summary>
        private void Scalars(object instance, string indent = "")
        {
            if (instance == null) return;

            try
            {
                foreach (PropertyInfo p in instance.GetType()
                             .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                {
                    Type t = p.PropertyType;
                    bool simple = t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(DateTime);
                    if (!simple) continue;

                    string text;
                    try { text = Convert.ToString(p.GetValue(instance, null), CultureInfo.InvariantCulture); }
                    catch (Exception ex) { text = "<" + ex.GetType().Name + ">"; }

                    Line(indent + "  " + p.Name + " = " + text);
                }
            }
            catch
            {
                // Values are a convenience; structure above is the part that matters.
            }
        }

        // ------------------------------------------------------------------ attempts

        private void Attempt(string name, Action call, Func<string> describe)
        {
            try
            {
                call();
                Line("  " + name + " → OK, " + describe());
            }
            catch (Exception ex)
            {
                Line("  " + name + " → " + ex.GetType().Name + ": " + Shorten(ex.Message));
            }
        }

        private void AttemptReturning(string name, Func<object> call)
        {
            try
            {
                object result = call();
                if (result == null) { Line("  " + name + " → null"); return; }

                var array = result as Array;
                if (array != null)
                {
                    Line("  " + name + " → " + result.GetType().Name +
                         ", rank " + array.Rank + ", length " + array.Length + ", " + DescribeArray(array));
                    return;
                }

                Line("  " + name + " → " + result.GetType().Name + " = " +
                     Convert.ToString(result, CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Line("  " + name + " → " + ex.GetType().Name + ": " + Shorten(ex.Message));
            }
        }

        // ------------------------------------------------------------------ descriptions

        private static string Describe(ushort[,] buffer)
        {
            int min = int.MaxValue, max = int.MinValue;
            foreach (ushort v in buffer)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            return Verdict(min, max, First(buffer));
        }

        private static string Describe(int[,] buffer)
        {
            int min = int.MaxValue, max = int.MinValue;
            foreach (int v in buffer)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            return Verdict(min, max, First(buffer));
        }

        private static string Describe(ushort[] buffer)
        {
            int min = int.MaxValue, max = int.MinValue;
            foreach (ushort v in buffer)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            return Verdict(min, max, First(buffer));
        }

        private static string DescribeArray(Array array)
        {
            var values = new List<string>();
            int taken = 0;
            foreach (object v in array)
            {
                values.Add(Convert.ToString(v, CultureInfo.InvariantCulture));
                if (++taken >= 12) break;
            }
            return "first values: " + string.Join(", ", values.ToArray());
        }

        private static string Verdict(int min, int max, string first)
        {
            string state = min == max
                ? "*** ALL VOXELS EQUAL — the call wrote nothing ***"
                : "REAL DATA";

            return string.Format(CultureInfo.InvariantCulture,
                "min {0}, max {1} → {2}. First values: {3}", min, max, state, first);
        }

        private static string First(ushort[,] buffer)
        {
            var values = new List<string>();
            int rows = Math.Min(2, buffer.GetLength(0));
            int cols = Math.Min(8, buffer.GetLength(1));
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    values.Add(buffer[i, j].ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", values.ToArray());
        }

        private static string First(int[,] buffer)
        {
            var values = new List<string>();
            int rows = Math.Min(2, buffer.GetLength(0));
            int cols = Math.Min(8, buffer.GetLength(1));
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    values.Add(buffer[i, j].ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", values.ToArray());
        }

        private static string First(ushort[] buffer)
        {
            var values = new List<string>();
            for (int i = 0; i < Math.Min(16, buffer.Length); i++)
                values.Add(buffer[i].ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", values.ToArray());
        }

        // ------------------------------------------------------------------ plumbing

        private dynamic Try(Func<object> accessor, string name)
        {
            try
            {
                object value = accessor();
                Line(name + " → " + (value == null ? "null" : value.GetType().Name));
                return value;
            }
            catch (Exception ex)
            {
                Line(name + " → " + ex.GetType().Name + ": " + Shorten(ex.Message));
                return null;
            }
        }

        private static int ReadInt(dynamic first, dynamic second, string property)
        {
            foreach (dynamic candidate in new[] { first, second })
            {
                if (candidate == null) continue;
                try
                {
                    PropertyInfo p = ((object)candidate).GetType()
                        .GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) continue;
                    return Convert.ToInt32(p.GetValue((object)candidate, null), CultureInfo.InvariantCulture);
                }
                catch
                {
                    // Try the next carrier.
                }
            }
            return 0;
        }

        private static string Shorten(string message)
        {
            if (string.IsNullOrEmpty(message)) return "(no message)";
            message = message.Replace(Environment.NewLine, " ");
            return message.Length <= 300 ? message : message.Substring(0, 300) + "…";
        }

        private void Section(string title)
        {
            Line("");
            Line("======== " + title + " ========");
        }

        private void Line(string text)
        {
            _out.AppendLine(text);
        }

        // ------------------------------------------------------------------ output

        private static string Save(string text)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "ESAPI_probe_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture) + ".txt");

                File.WriteAllText(path, text, Encoding.UTF8);
                return path;
            }
            catch (Exception ex)
            {
                return "(could not be saved: " + ex.Message + ")";
            }
        }

        private static void Show(string text, string path)
        {
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11
            };

            var header = new TextBlock
            {
                Text = "Saved to: " + path + "   —   select all (Ctrl+A), copy, or just send the file.",
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(header, 0);
            Grid.SetRow(box, 1);
            grid.Children.Add(header);
            grid.Children.Add(box);

            new Window
            {
                Title = "ESAPI probe",
                Content = grid,
                Width = 1000,
                Height = 700,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            }.ShowDialog();
        }
    }
}
