using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using VMS.IRS.Scripting;
using ESAPI_RegistrationQA.Models;
using ESAPI_RegistrationQA.Services;

namespace ESAPI_RegistrationQA.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ScriptContext _context;
        private readonly DiagnosticLog _log = new DiagnosticLog();

        /// <summary>
        /// Mediciones crudas. Se calculan una sola vez: cambiar de perfil anatómico no
        /// vuelve a leer imagen ni estructuras, sólo reclasifica estos mismos valores.
        /// </summary>
        private QaMeasurements _measurements;

        private string _patientId;
        private string _globalStatusMessage;
        private QASemaphore _globalStatus = QASemaphore.NotAvailable;
        private RegistrationContext _regContext;
        private ThresholdProfile _activeProfile;

        public MainViewModel(ScriptContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));

            IntensityMetrics = new ObservableCollection<MetricResult>();
            DeformationMetrics = new ObservableCollection<MetricResult>();
            StructureQAMetrics = new ObservableCollection<MetricResult>();
            RigidTransformData = new ObservableCollection<RigidTransformItem>();
            Advisories = new ObservableCollection<Advisory>();
            Diagnostics = new ObservableCollection<DiagnosticEntry>();

            AvailableProfiles = ThresholdProfile.GetAllProfiles().AsReadOnly();

            // Asignación directa al campo: en construcción todavía no hay mediciones que
            // reevaluar, y el disparo del setter sería prematuro.
            _activeProfile = AvailableProfiles.FirstOrDefault();

            RegContext = new RegistrationContext();

            ExportReportCommand = new RelayCommand(ExecuteExportReport, _ => _measurements != null);

            RunMeasurement();
        }

        // ------------------------------------------------------------------ propiedades

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

                // Sólo se reclasifica. Los valores medidos no dependen del perfil.
                ReevaluateThresholds();
            }
        }

        public ObservableCollection<MetricResult> IntensityMetrics { get; }
        public ObservableCollection<MetricResult> DeformationMetrics { get; }
        public ObservableCollection<MetricResult> StructureQAMetrics { get; }
        public ObservableCollection<RigidTransformItem> RigidTransformData { get; }
        public ObservableCollection<Advisory> Advisories { get; }
        public ObservableCollection<DiagnosticEntry> Diagnostics { get; }

        public RelayCommand ExportReportCommand { get; }

        public string DiagnosticsSummary
        {
            get
            {
                int failures = Diagnostics.Count(d => d.Level == DiagnosticLevel.Failure);
                int warnings = Diagnostics.Count(d => d.Level == DiagnosticLevel.Warning);

                if (failures == 0 && warnings == 0)
                    return "Sin incidencias durante la lectura de datos.";

                return $"{failures} fallo(s) y {warnings} aviso(s) durante la lectura de datos de la API.";
            }
        }

        public string MeasurementSummary
        {
            get
            {
                if (_measurements == null) return "Sin mediciones.";

                var parts = new List<string>();

                if (!string.IsNullOrEmpty(_measurements.TransformSource))
                    parts.Add("Transformación: " + _measurements.TransformSource);

                if (_measurements.SampleCount > 0)
                    parts.Add($"{_measurements.SampleCount:N0} pares de vóxeles evaluados");

                if (_measurements.OverlapFraction.HasValue)
                    parts.Add($"solapamiento {_measurements.OverlapFraction.Value:P1}");

                if (_measurements.EffectiveSamplingMm.HasValue)
                    parts.Add($"muestreo efectivo {_measurements.EffectiveSamplingMm.Value:F1} mm");

                return parts.Count > 0 ? string.Join(" · ", parts) : "Sin mediciones.";
            }
        }

        // ------------------------------------------------------------------ medición

        /// <summary>
        /// Ejecuta la medición completa. Caro: lee vóxeles de ambos volúmenes. Se invoca una
        /// vez por sesión.
        /// </summary>
        private void RunMeasurement()
        {
            _log.Clear();

            // Se lee después de limpiar la bitácora para que un fallo al obtener el
            // identificador quede reflejado en la pestaña de diagnóstico.
            PatientId = ReadPatientId();

            try
            {
                var analyzer = new RegistrationAnalyzer(_log);
                _measurements = analyzer.Analyze(_context);
            }
            catch (Exception ex)
            {
                // Una excepción aquí no puede dejar la ventana en un estado ambiguo: se
                // registra y se continúa con un conjunto de mediciones vacío, que la capa
                // de evaluación traducirá en métricas no disponibles.
                _log.Failure("medición", ex);
                _measurements = new QaMeasurements { RegistrationId = "(medición interrumpida)" };
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
        }

        /// <summary>
        /// Reclasifica las mediciones existentes frente al perfil activo. Barato: no toca la
        /// API. Es lo único que ocurre al cambiar de perfil anatómico.
        /// </summary>
        private void ReevaluateThresholds()
        {
            Replace(IntensityMetrics, MetricEvaluator.Evaluate(_measurements, ActiveProfile, MetricEvaluator.IntensityKeys));
            Replace(DeformationMetrics, MetricEvaluator.Evaluate(_measurements, ActiveProfile, MetricEvaluator.DeformationKeys));
            Replace(StructureQAMetrics, MetricEvaluator.Evaluate(_measurements, ActiveProfile, MetricEvaluator.StructureKeys));
            Replace(RigidTransformData, MetricEvaluator.BuildRigidTransformRows(_measurements));

            AdvisorySet advisorySet = AdvisoryEngine.Build(
                IntensityMetrics.Concat(DeformationMetrics).Concat(StructureQAMetrics),
                ActiveProfile,
                _measurements);

            Replace(Advisories, advisorySet.Advisories);

            GlobalStatusMessage = advisorySet.OverallStatus;
            GlobalStatus = ToSemaphore(advisorySet.OverallSeverity);
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
            string id;
            if (Dyn.TryGetString("contexto: identificador de paciente", () => _context.Patient.Id, _log, out id))
                return id;

            return "(paciente sin identificador)";
        }

        // ------------------------------------------------------------------ exportación

        private void ExecuteExportReport(object parameter)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Documento HTML (*.html)|*.html",
                DefaultExt = "html",
                Title = "Guardar reporte de auditoría de registro",
                FileName = HtmlReportBuilder.SanitizeFileName(
                    $"RegistrationQA_{PatientId}_{DateTime.Now:yyyyMMdd_HHmm}")
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                var data = new ReportData
                {
                    PatientId = PatientId,
                    ProfileName = ActiveProfile?.ProfileName,
                    Measurements = _measurements,
                    Advisories = AdvisoryEngine.Build(
                        IntensityMetrics.Concat(DeformationMetrics).Concat(StructureQAMetrics),
                        ActiveProfile,
                        _measurements),
                    IntensityMetrics = IntensityMetrics.ToList(),
                    DeformationMetrics = DeformationMetrics.ToList(),
                    StructureMetrics = StructureQAMetrics.ToList(),
                    RigidTransform = RigidTransformData.ToList(),
                    Diagnostics = Diagnostics.ToList()
                };

                HtmlReportBuilder.Save(data, dialog.FileName);

                MessageBox.Show(
                    "Reporte exportado correctamente.",
                    "Exportación completada", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"No se pudo generar el reporte:{Environment.NewLine}{DiagnosticLog.Describe(ex)}",
                    "Error de exportación", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------ INPC

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
