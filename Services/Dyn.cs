using System;
using System.Globalization;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Tolerant access to <c>dynamic</c> objects from the Varian API.
    ///
    /// The API is queried dynamically because the VMS.IRS surface varies between Eclipse
    /// versions and not every property exists in all of them. What is not acceptable is for
    /// that tolerance to be silent: every failed attempt is recorded in the
    /// <see cref="DiagnosticLog"/> with the operation and the specific exception, so the
    /// physicist can see why a metric came out unavailable.
    ///
    /// All helpers take the access wrapped in a delegate so that property chains
    /// (<c>() =&gt; reg.SourceImage.Frame.Origin</c>) can be used without losing an
    /// exception thrown partway along the chain.
    /// </summary>
    public static class Dyn
    {
        /// <summary>
        /// Evaluates the accessor and returns true if it did not throw and the result is
        /// not null.
        /// </summary>
        public static bool TryGet(string operation, Func<object> accessor, DiagnosticLog log, out dynamic value)
        {
            value = null;
            try
            {
                object result = accessor();
                if (result == null)
                {
                    if (log != null) log.Info(operation, "the property exists but is null");
                    return false;
                }
                value = result;
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log.Failure(operation, ex);
                return false;
            }
        }

        public static bool TryGetDouble(string operation, Func<object> accessor, DiagnosticLog log, out double value)
        {
            value = 0.0;
            try
            {
                object result = accessor();
                if (result == null)
                {
                    if (log != null) log.Info(operation, "the property exists but is null");
                    return false;
                }

                value = Convert.ToDouble(result, CultureInfo.InvariantCulture);

                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    if (log != null) log.Warning(operation, "non-finite value: " + value.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log.Failure(operation, ex);
                return false;
            }
        }

        public static bool TryGetInt(string operation, Func<object> accessor, DiagnosticLog log, out int value)
        {
            value = 0;
            try
            {
                object result = accessor();
                if (result == null)
                {
                    if (log != null) log.Info(operation, "the property exists but is null");
                    return false;
                }
                value = Convert.ToInt32(result, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log.Failure(operation, ex);
                return false;
            }
        }

        public static bool TryGetString(string operation, Func<object> accessor, DiagnosticLog log, out string value)
        {
            value = null;
            try
            {
                object result = accessor();
                if (result == null)
                {
                    if (log != null) log.Info(operation, "the property exists but is null");
                    return false;
                }

                value = Convert.ToString(result, CultureInfo.InvariantCulture);
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                if (log != null) log.Failure(operation, ex);
                return false;
            }
        }

        /// <summary>
        /// Runs an action against the API, tolerating failure but recording it. Returns true
        /// if it completed.
        /// </summary>
        public static bool TryInvoke(string operation, Action action, DiagnosticLog log)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log.Failure(operation, ex);
                return false;
            }
        }

        /// <summary>
        /// Returns the first accessor that succeeds from a list of alternatives, recording
        /// which one worked. Useful when the same information lives under different property
        /// names depending on the API version.
        /// </summary>
        public static bool TryGetFirst(
            string operation,
            DiagnosticLog log,
            out dynamic value,
            out string usedAlternative,
            params Tuple<string, Func<object>>[] alternatives)
        {
            value = null;
            usedAlternative = null;

            if (alternatives == null) return false;

            foreach (var alternative in alternatives)
            {
                dynamic candidate;
                if (TryGet(operation + " (" + alternative.Item1 + ")", alternative.Item2, log, out candidate))
                {
                    value = candidate;
                    usedAlternative = alternative.Item1;
                    return true;
                }
            }

            return false;
        }

        public static Tuple<string, Func<object>> Alt(string name, Func<object> accessor)
        {
            return Tuple.Create(name, accessor);
        }
    }
}
