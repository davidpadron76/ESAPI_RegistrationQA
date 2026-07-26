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

        /// <summary>What was being attempted, in domain terms.</summary>
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
                    case DiagnosticLevel.Failure: return "FAILURE";
                    case DiagnosticLevel.Warning: return "WARNING";
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
    /// A record of everything that was attempted against the API and did not go as expected.
    ///
    /// This replaces the empty catch blocks of the previous version. In a QA tool a silent
    /// failure is worse than a visible error: it produces a signed report whose fallback
    /// data nobody knows is fallback. Everything logged here ends up visible in the
    /// diagnostics tab and in the exported report.
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
            if (exception == null) return "null exception";

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
