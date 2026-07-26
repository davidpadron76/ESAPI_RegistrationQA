using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace ESAPI_RegistrationQA.Services
{
    public enum DiagnosticLevel { Info, Warning, Failure }

    public sealed class DiagnosticEntry
    {
        public DateTime Timestamp { get; private set; }
        public DiagnosticLevel Level { get; private set; }

        /// <summary>Qué se intentaba hacer, en lenguaje del dominio.</summary>
        public string Operation { get; private set; }

        public string Detail { get; private set; }

        public DiagnosticEntry(DiagnosticLevel level, string operation, string detail)
        {
            Timestamp = DateTime.Now;
            Level = level;
            Operation = operation;
            Detail = detail;
        }

        public string LevelText
        {
            get
            {
                switch (Level)
                {
                    case DiagnosticLevel.Failure: return "FALLO";
                    case DiagnosticLevel.Warning: return "AVISO";
                    default: return "INFO";
                }
            }
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "[{0}] {1}: {2}",
                LevelText, Operation, Detail);
        }
    }

    /// <summary>
    /// Bitácora de todo lo que se intentó leer de la API y no salió como se esperaba.
    ///
    /// Reemplaza los bloques catch vacíos de la versión anterior. En una herramienta de QA
    /// un fallo silencioso es peor que un error visible: produce un reporte firmado cuyos
    /// datos de respaldo nadie sabe que lo son. Todo lo que se registra aquí acaba siendo
    /// visible en la pestaña de diagnóstico y en el reporte exportado.
    /// </summary>
    public sealed class DiagnosticLog
    {
        private readonly List<DiagnosticEntry> _entries = new List<DiagnosticEntry>();

        public ReadOnlyCollection<DiagnosticEntry> Entries
        {
            get { return _entries.AsReadOnly(); }
        }

        public bool HasFailures
        {
            get { return _entries.Exists(e => e.Level == DiagnosticLevel.Failure); }
        }

        public void Info(string operation, string detail)
        {
            _entries.Add(new DiagnosticEntry(DiagnosticLevel.Info, operation, detail));
        }

        public void Warning(string operation, string detail)
        {
            _entries.Add(new DiagnosticEntry(DiagnosticLevel.Warning, operation, detail));
        }

        public void Failure(string operation, string detail)
        {
            _entries.Add(new DiagnosticEntry(DiagnosticLevel.Failure, operation, detail));
        }

        public void Failure(string operation, Exception exception)
        {
            _entries.Add(new DiagnosticEntry(DiagnosticLevel.Failure, operation, Describe(exception)));
        }

        public static string Describe(Exception exception)
        {
            if (exception == null) return "excepción nula";

            string text = exception.GetType().Name + ": " + exception.Message;
            if (exception.InnerException != null)
                text += " → " + exception.InnerException.GetType().Name + ": " + exception.InnerException.Message;

            return text;
        }

        public void Clear()
        {
            _entries.Clear();
        }
    }
}
