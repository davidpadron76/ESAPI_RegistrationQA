using System;
using System.Collections.Generic;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// The stages of one measurement pass, in the order <see cref="RegistrationAnalyzer.Analyze"/>
    /// runs them.
    ///
    /// Named stages rather than a percentage of voxels processed, and the reason is diagnostic
    /// rather than cosmetic: when a case appears to hang, the stage on screen says which API call
    /// it is sitting in. Every field failure in this project so far has been one specific read
    /// blocking or returning nothing, and "Reading the registered volume" narrows that to one
    /// line of code where "47 %" would not.
    /// </summary>
    public enum MeasurementStage
    {
        Starting,
        IdentifyingRegistration,
        LoadingSourceVolume,
        LoadingRegisteredVolume,
        ReadingTransform,
        BuildingPointMapping,
        IntensitySimilarity,
        StructureMetrics,
        DeformationField,
        TargetRegistrationError,
        InverseConsistency,
        Finished
    }

    /// <summary>
    /// Receives stage changes during a measurement. Deliberately not <see cref="IProgress{T}"/>:
    /// that interface is built for marshalling across threads, and the measurement runs on the UI
    /// thread precisely because the Varian API objects cannot be assumed safe to touch from
    /// another one. What the UI implementation does on each report is yield to the dispatcher so
    /// the window repaints, which is a same-thread concern.
    ///
    /// Implementations must not throw. A progress sink that fails would abort a measurement that
    /// was otherwise fine, so <see cref="MeasurementProgress.Report"/> swallows anything it
    /// raises.
    /// </summary>
    public interface IMeasurementProgressSink
    {
        void Report(MeasurementStage stage, int completed, int total, string description);
    }

    /// <summary>
    /// Turns a stage into a position and a caption, and forwards it to a sink if there is one.
    ///
    /// Safe to construct with a null sink, which is what the report builders and the tests do:
    /// reporting progress nobody is watching costs a switch statement.
    /// </summary>
    public sealed class MeasurementProgress
    {
        private readonly IMeasurementProgressSink _sink;

        /// <summary>
        /// Stages in the order they run. Excludes <see cref="MeasurementStage.Starting"/> and
        /// <see cref="MeasurementStage.Finished"/>, which are the endpoints rather than work.
        /// </summary>
        private static readonly MeasurementStage[] Ordered =
        {
            MeasurementStage.IdentifyingRegistration,
            MeasurementStage.LoadingSourceVolume,
            MeasurementStage.LoadingRegisteredVolume,
            MeasurementStage.ReadingTransform,
            MeasurementStage.BuildingPointMapping,
            MeasurementStage.IntensitySimilarity,
            MeasurementStage.StructureMetrics,
            MeasurementStage.DeformationField,
            MeasurementStage.TargetRegistrationError,
            MeasurementStage.InverseConsistency
        };

        public MeasurementProgress(IMeasurementProgressSink sink)
        {
            _sink = sink;
        }

        public static int StageCount { get { return Ordered.Length; } }

        public void Report(MeasurementStage stage)
        {
            if (_sink == null) return;

            try
            {
                _sink.Report(stage, PositionOf(stage), Ordered.Length, Describe(stage));
            }
            catch
            {
                // A failing progress sink must not take the measurement with it. The numbers are
                // a courtesy to the reader; the audit is the deliverable.
            }
        }

        /// <summary>
        /// How many stages are complete once this one is under way. Starting is 0 and Finished is
        /// all of them, so the bar reaches its end rather than stopping one short.
        /// </summary>
        private static int PositionOf(MeasurementStage stage)
        {
            if (stage == MeasurementStage.Starting) return 0;
            if (stage == MeasurementStage.Finished) return Ordered.Length;

            for (int i = 0; i < Ordered.Length; i++)
                if (Ordered[i] == stage) return i;

            return 0;
        }

        /// <summary>
        /// Wording aimed at someone watching a case that has stopped moving: each caption names
        /// the object being read, not the metric being produced, because the object is what the
        /// diagnostics will be about.
        /// </summary>
        public static string Describe(MeasurementStage stage)
        {
            switch (stage)
            {
                case MeasurementStage.Starting:
                    return "Starting…";
                case MeasurementStage.IdentifyingRegistration:
                    return "Identifying the registration…";
                case MeasurementStage.LoadingSourceVolume:
                    return "Reading the source volume…";
                case MeasurementStage.LoadingRegisteredVolume:
                    return "Reading the registered volume…";
                case MeasurementStage.ReadingTransform:
                    return "Reading the registration transform…";
                case MeasurementStage.BuildingPointMapping:
                    return "Building the point mapping…";
                case MeasurementStage.IntensitySimilarity:
                    return "Comparing voxel intensities (NCC, NMI, SSD)…";
                case MeasurementStage.StructureMetrics:
                    return "Rasterising structures (DSC, MDA, HD95)…";
                case MeasurementStage.DeformationField:
                    return "Reading the deformation field…";
                case MeasurementStage.TargetRegistrationError:
                    return "Matching landmarks (TRE)…";
                case MeasurementStage.InverseConsistency:
                    return "Evaluating inverse consistency…";
                case MeasurementStage.Finished:
                    return "Complete.";
                default:
                    return "Working…";
            }
        }

        /// <summary>Every stage in order, for a UI that wants to list them up front.</summary>
        public static IEnumerable<MeasurementStage> AllStages
        {
            get { return (MeasurementStage[])Ordered.Clone(); }
        }
    }
}
