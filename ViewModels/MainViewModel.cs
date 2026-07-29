using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using ESAPI_RegistrationQA.Models;
using ESAPI_RegistrationQA.Services;

namespace ESAPI_RegistrationQA.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// The Eclipse script context, held as <c>dynamic</c>.
        ///
        /// It is deliberately untyped here so that this class does not depend on the Varian
        /// assemblies at all: the only file that names <c>ScriptContext</c> is the script
        /// entry point, which must. Everything the analyzer reads from the context already
        /// goes through <see cref="Dyn"/>, so a static type would buy nothing and would add
        /// a compile-time dependency on VMS.IRS.Scripting resolving correctly from this
        /// namespace.
        /// </summary>
        private readonly dynamic _context;

        private readonly DiagnosticLog _log = new DiagnosticLog();

        /// <summary>
        /// Raw measurements. Computed exactly once: switching anatomical profile does not
        /// re-read the image or the structures, it only reclassifies these same values.
        /// </summary>
        private QaMeasurements _measurements;

        private string _patientId;
        private string _globalStatusMessage;
        private QASemaphore _globalStatus = QASemaphore.NotAvailable;
        private RegistrationContext _regContext;
        private ThresholdProfile _activeProfile;

        /// <param name="context">
        /// The Eclipse <c>ScriptContext</c>. Typed as <see cref="object"/> so this assembly's
        /// view layer stays independent of the Varian scripting assemblies.
        /// </param>
        public MainViewModel(object context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));

            IntensityMetrics = new ObservableCollection<MetricResult>();
            DeformationMetrics = new ObservableCollection<MetricResult>();
            SpatialAccuracyMetrics = new ObservableCollection<MetricResult>();
            StructureQAMetrics = new ObservableCollection<MetricResult>();
            RigidTransformData = new ObservableCollection<RigidTransformItem>();
            Advisories = new ObservableCollection<Advisory>();
            Diagnostics = new ObservableCollection<DiagnosticEntry>();
            NotApplicableMetrics = new ObservableCollection<MetricResult>();

            // One flat, grouped collection is what the window shows now. The four above are
            // kept because the HTML report and the CSV consume them section by section.
            AllMetrics = new ObservableCollection<MetricResult>();

            AvailableProfiles = ThresholdProfile.GetAllProfiles().AsReadOnly();

            // Assigned directly to the field: during construction there are no measurements
            // to re-evaluate yet, so firing the setter would be premature.
            _activeProfile = AvailableProfiles.FirstOrDefault();

            RegContext = new RegistrationContext();

            ExportReportCommand = new RelayCommand(ExecuteExportReport, _ => _measurements != null);
            ExportCsvCommand = new RelayCommand(ExecuteExportCsv, _ => _measurements != null);
            ShowAdvisoriesCommand = new RelayCommand(_ => ShowAdvisories());
            ShowDiagnosticsCommand = new RelayCommand(_ => ShowDiagnostics());

            RunMeasurement();
        }

        // ------------------------------------------------------------------ properties

        public string PatientId
        {
            get => _patientId;
            private set { _patientId = value; OnPropertyChanged(); }
        }

        public string GlobalStatusMessage
        {
            get => _globalStatusMessage;
            private set { _globalStatusMessage = value; OnPropertyChanged(); }
        }

        public QASemaphore GlobalStatus
        {
            get => _globalStatus;
            private set { _globalStatus = value; OnPropertyChanged(); }
        }

        public RegistrationContext RegContext
        {
            get => _regContext;
            private set { _regContext = value; OnPropertyChanged(); }
        }

        public IReadOnlyList<ThresholdProfile> AvailableProfiles { get; }

        public ThresholdProfile ActiveProfile
        {
            get => _activeProfile;
            set
            {
                if (_activeProfile == value) return;
                _activeProfile = value;
                OnPropertyChanged();

                // Reclassification only. The measured values do not depend on the profile.
                ReevaluateThresholds();
            }
        }

        public ObservableCollection<MetricResult> IntensityMetrics { get; }
        public ObservableCollection<MetricResult> DeformationMetrics { get; }
        public ObservableCollection<MetricResult> SpatialAccuracyMetrics { get; }
        public ObservableCollection<MetricResult> StructureQAMetrics { get; }
        public ObservableCollection<RigidTransformItem> RigidTransformData { get; }
        public ObservableCollection<Advisory> Advisories { get; }
        public ObservableCollection<DiagnosticEntry> Diagnostics { get; }

        /// <summary>
        /// Every evaluated metric in one collection, grouped in the view by
        /// <see cref="MetricResult.Group"/>. Four tabs meant four clicks to find out what had
        /// been measured; the point of a QA screen is to be read in one glance.
        /// </summary>
        public ObservableCollection<MetricResult> AllMetrics { get; }

        /// <summary>
        /// Metrics that did not apply to this case. They are kept out of the metric tables —
        /// a row that would say the same thing on every case is noise — and accounted for
        /// here instead, so the omission stays visible and traceable.
        /// </summary>
        public ObservableCollection<MetricResult> NotApplicableMetrics { get; }

        public RelayCommand ExportReportCommand { get; }
        public RelayCommand ExportCsvCommand { get; }
        public RelayCommand ShowAdvisoriesCommand { get; }
        public RelayCommand ShowDiagnosticsCommand { get; }

        /// <summary>
        /// Button labels carry their counts. A tab called "Diagnostics" says nothing about
        /// whether it is worth opening; "Diagnostics (3 failures)" does.
        /// </summary>
        public string AdvisoriesButtonText
        {
            get { return $"Advisories ({Advisories.Count})"; }
        }

        public string DiagnosticsButtonText
        {
            get
            {
                int failures = Diagnostics.Count(d => d.Level == DiagnosticLevel.Failure);
                return failures > 0
                    ? $"Diagnostics ({failures} failure{(failures == 1 ? string.Empty : "s")})"
                    : $"Diagnostics ({Diagnostics.Count})";
            }
        }

        /// <summary>
        /// Translations and rotations on one line. They belonged to a tab of their own, which
        /// is more ceremony than six numbers deserve; the full table is still in the report.
        /// </summary>
        public string TransformSummary
        {
            get
            {
                if (_measurements == null || _measurements.Transform == null)
                    return "Transform: not available.";

                Vec3 t = _measurements.Transform.Translation;
                string text = string.Format(CultureInfo.InvariantCulture,
                    "Translation {0:F2} / {1:F2} / {2:F2} mm (LR / AP / CC)", t.X, t.Y, t.Z);

                if (_measurements.RigidEulerAngles.HasValue)
                {
                    EulerAngles a = _measurements.RigidEulerAngles.Value;
                    text += string.Format(CultureInfo.InvariantCulture,
                        "   ·   Rotation {0:F2} / {1:F2} / {2:F2}° (pitch / roll / yaw)",
                        a.PitchX, a.RollY, a.YawZ);

                    if (a.GimbalLock) text += "   ·   gimbal lock: read the angles as a set";
                }

                return text;
            }
        }

        public bool HasIntensityMetrics { get { return IntensityMetrics.Count > 0; } }
        public bool HasDeformationMetrics { get { return DeformationMetrics.Count > 0; } }
        public bool HasSpatialAccuracyMetrics { get { return SpatialAccuracyMetrics.Count > 0; } }
        public bool HasStructureMetrics { get { return StructureQAMetrics.Count > 0; } }
        public bool HasNotApplicableMetrics { get { return NotApplicableMetrics.Count > 0; } }

        public string DiagnosticsSummary
        {
            get
            {
                int failures = Diagnostics.Count(d => d.Level == DiagnosticLevel.Failure);
                int warnings = Diagnostics.Count(d => d.Level == DiagnosticLevel.Warning);

                if (failures == 0 && warnings == 0)
                    return "No issues while reading data.";

                return $"{failures} failure(s) and {warnings} warning(s) while reading data from the API.";
            }
        }

        public string MeasurementSummary
        {
            get
            {
                if (_measurements == null) return "No measurements.";

                var parts = new List<string>();

                if (!string.IsNullOrEmpty(_measurements.TransformSource))
                    parts.Add("Transform: " + _measurements.TransformSource);

                if (_measurements.SampleCount > 0)
                    parts.Add($"{_measurements.SampleCount:N0} voxel pairs evaluated");

                if (_measurements.OverlapFraction.HasValue)
                    parts.Add($"{_measurements.OverlapFraction.Value:P1} overlap");

                if (_measurements.EffectiveSamplingMm.HasValue)
                    parts.Add($"{_measurements.EffectiveSamplingMm.Value:F1} mm effective sampling");

                return parts.Count > 0 ? string.Join(" · ", parts) : "No measurements.";
            }
        }

        // ------------------------------------------------------------------ measurement

        /// <summary>
        /// Runs the full measurement. Expensive: it reads voxels from both volumes. Invoked
        /// once per session.
        /// </summary>
        private void RunMeasurement()
        {
            _log.Clear();

            // Read after clearing the log so that a failure to obtain the identifier shows
            // up in the diagnostics tab.
            PatientId = ReadPatientId();

            try
            {
                var analyzer = new RegistrationAnalyzer(_log);
                _measurements = analyzer.Analyze(_context);
            }
            catch (Exception ex)
            {
                // An exception here must not leave the window in an ambiguous state: it is
                // recorded and we continue with an empty measurement set, which the
                // evaluation layer will translate into unavailable metrics.
                _log.Failure("measurement", ex);
                _measurements = new QaMeasurements { RegistrationId = "(measurement interrupted)" };
            }

            RegContext = new RegistrationContext
            {
                RegType = _measurements.RegType,
                ModalityFixed = _measurements.FixedModality,
                ModalityMoving = _measurements.MovingModality
            };

            Diagnostics.Clear();
            foreach (DiagnosticEntry entry in _log.Entries)
                Diagnostics.Add(entry);

            OnPropertyChanged(nameof(DiagnosticsSummary));
            OnPropertyChanged(nameof(MeasurementSummary));

            ReevaluateThresholds();
            ExportReportCommand.RaiseCanExecuteChanged();
            ExportCsvCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Reclassifies the existing measurements against the active profile. Cheap: it does
        /// not touch the API. This is all that happens when the anatomical profile changes.
        /// </summary>
        private void ReevaluateThresholds()
        {
            // The profile is resolved against this case before anything is classified. Four of
            // the five Table III tolerances are not constants: the report states them as the
            // maximum voxel dimension of the images being registered, so the numbers only exist
            // once there is a measurement to read them from.
            ThresholdProfile effective = ActiveProfile != null
                ? ActiveProfile.Resolve(_measurements)
                : null;

            Replace(IntensityMetrics, MetricEvaluator.Evaluate(
                _measurements, effective, MetricEvaluator.IntensityKeys, "Intensity similarity"));
            Replace(DeformationMetrics, MetricEvaluator.Evaluate(
                _measurements, effective, MetricEvaluator.DeformationKeys, "Deformation and topology"));
            Replace(SpatialAccuracyMetrics, MetricEvaluator.Evaluate(
                _measurements, effective, MetricEvaluator.SpatialAccuracyKeys, "Spatial accuracy (TG-132 Table III)"));
            Replace(StructureQAMetrics, MetricEvaluator.Evaluate(
                _measurements, effective, MetricEvaluator.StructureKeys, "Structures and surface"));
            Replace(RigidTransformData, MetricEvaluator.BuildRigidTransformRows(_measurements));
            Replace(NotApplicableMetrics, MetricEvaluator.NotApplicable(_measurements));

            Replace(AllMetrics, IntensityMetrics
                .Concat(DeformationMetrics)
                .Concat(SpatialAccuracyMetrics)
                .Concat(StructureQAMetrics));

            AdvisorySet advisorySet = AdvisoryEngine.Build(
                IntensityMetrics.Concat(DeformationMetrics)
                    .Concat(SpatialAccuracyMetrics).Concat(StructureQAMetrics),
                effective,
                _measurements);

            Replace(Advisories, advisorySet.Advisories);

            GlobalStatusMessage = advisorySet.OverallStatus;
            GlobalStatus = ToSemaphore(advisorySet.OverallSeverity);

            OnPropertyChanged(nameof(HasIntensityMetrics));
            OnPropertyChanged(nameof(HasDeformationMetrics));
            OnPropertyChanged(nameof(HasSpatialAccuracyMetrics));
            OnPropertyChanged(nameof(HasStructureMetrics));
            OnPropertyChanged(nameof(HasNotApplicableMetrics));
            OnPropertyChanged(nameof(TransformSummary));
            OnPropertyChanged(nameof(AdvisoriesButtonText));
            OnPropertyChanged(nameof(DiagnosticsButtonText));
        }

        // ------------------------------------------------------------------ detail windows

        /// <summary>
        /// Advisories and diagnostics moved out of the main window into these two. They are
        /// long-form text: valuable when something needs explaining, noise on a screen whose
        /// job is to show four numbers and a colour.
        /// </summary>
        private void ShowAdvisories()
        {
            UI.DetailWindow.Show("Clinical advisories", Advisories,
                new[]
                {
                    Tuple.Create("Severity", "SeverityText", 90.0),
                    Tuple.Create("Category", "Category", 190.0),
                    Tuple.Create("Advice", "Message", 0.0)
                });
        }

        private void ShowDiagnostics()
        {
            UI.DetailWindow.Show("Varian API access — " + DiagnosticsSummary, Diagnostics,
                new[]
                {
                    Tuple.Create("Level", "Level", 90.0),
                    Tuple.Create("Operation", "Operation", 260.0),
                    Tuple.Create("Detail", "Detail", 0.0)
                },
                NotApplicableMetrics,
                new[]
                {
                    Tuple.Create("Metric not applicable", "MetricName", 220.0),
                    Tuple.Create("Why", "UnavailableReason", 0.0)
                });
        }

        private static QASemaphore ToSemaphore(AdvisorySeverity severity)
        {
            switch (severity)
            {
                case AdvisorySeverity.Critical: return QASemaphore.Red;
                case AdvisorySeverity.Warning: return QASemaphore.Yellow;
                case AdvisorySeverity.Ok: return QASemaphore.Green;
                default: return QASemaphore.NotAvailable;
            }
        }

        private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            target.Clear();
            foreach (T item in items) target.Add(item);
        }

        private string ReadPatientId()
        {
            if (Dyn.TryGetString("context: patient identifier", () => _context.Patient.Id, _log, out string id))
                return id;

            return "(unidentified patient)";
        }

        // ------------------------------------------------------------------ export

        /// <summary>Snapshot of the current state, shared by both export paths.</summary>
        private ReportData BuildReportData()
        {
            return new ReportData
            {
                PatientId = PatientId,
                ProfileName = ActiveProfile?.ProfileName,
                Measurements = _measurements,
                Advisories = AdvisoryEngine.Build(
                    IntensityMetrics.Concat(DeformationMetrics)
                    .Concat(SpatialAccuracyMetrics).Concat(StructureQAMetrics),
                    ActiveProfile != null ? ActiveProfile.Resolve(_measurements) : null,
                    _measurements),
                IntensityMetrics = IntensityMetrics.ToList(),
                DeformationMetrics = DeformationMetrics.ToList(),
                StructureMetrics = StructureQAMetrics.ToList(),
                RigidTransform = RigidTransformData.ToList(),
                Diagnostics = Diagnostics.ToList()
            };
        }

        private void ExecuteExportReport(object parameter)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "HTML document (*.html)|*.html",
                DefaultExt = "html",
                Title = "Save registration audit report",
                FileName = HtmlReportBuilder.SanitizeFileName(
                    $"RegistrationQA_{PatientId}_{DateTime.Now:yyyyMMdd_HHmm}")
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                HtmlReportBuilder.Save(BuildReportData(), dialog.FileName);

                MessageBox.Show(
                    "Report exported successfully.",
                    "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"The report could not be generated:{Environment.NewLine}{DiagnosticLog.Describe(ex)}",
                    "Export error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Appends the current case to a cumulative CSV dataset.
        ///
        /// The dialog offers a fixed default file name rather than a per-patient one,
        /// because the intended use is to point every audit at the same file and let it grow
        /// into a local baseline. Selecting an existing file appends to it.
        /// </summary>
        private void ExecuteExportCsv(object parameter)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV dataset (*.csv)|*.csv",
                DefaultExt = "csv",
                Title = "Append this case to a registration QA dataset",
                FileName = "RegistrationQA_dataset.csv",
                OverwritePrompt = false
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                CsvDatasetExporter.AppendResult result =
                    CsvDatasetExporter.Append(BuildReportData(), dialog.FileName);

                string message = result.FileCreated
                    ? "Dataset created and this case added."
                    : "Case appended to the existing dataset.";

                if (result.TotalRows >= 0)
                    message += $"{Environment.NewLine}{result.TotalRows} case(s) in the file.";

                message += Environment.NewLine + Environment.NewLine +
                           "No patient identifier is written to the file. Fill the PhysicistVerdict " +
                           "column with your own judgement of the registration: that column is what " +
                           "makes it possible to derive tolerance limits from the data.";

                MessageBox.Show(message, "Dataset updated",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"The dataset could not be written:{Environment.NewLine}{DiagnosticLog.Describe(ex)}",
                    "Export error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------ INPC

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
