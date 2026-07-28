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
        ///
        /// An alternative that does not answer is not a fault — it is how the search works.
        /// Trying four property names and finding the fourth is a success, and the earlier
        /// three misses belong at Info, not at Failure. Getting that wrong made a working
        /// Eclipse report five failures, none of which was the actual problem, while the real
        /// fault sat further down the list as an Info line. Only exhausting every alternative
        /// is recorded as a failure.
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

            var misses = new System.Collections.Generic.List<string>();

            foreach (var alternative in alternatives)
            {
                dynamic candidate;
                string miss;

                if (TryGetQuiet(alternative.Item2, out candidate, out miss))
                {
                    value = candidate;
                    usedAlternative = alternative.Item1;

                    if (log != null && misses.Count > 0)
                    {
                        log.Info(operation,
                            "resolved via " + alternative.Item1 + "; earlier alternatives did not answer — " +
                            string.Join("; ", misses.ToArray()));
                    }

                    return true;
                }

                misses.Add(alternative.Item1 + " (" + miss + ")");
            }

            if (log != null)
            {
                log.Failure(operation,
                    "no alternative answered — " + string.Join("; ", misses.ToArray()));
            }

            return false;
        }

        /// <summary>
        /// Evaluates an accessor without logging, reporting why it did not answer so the
        /// caller can decide what severity the miss deserves.
        /// </summary>
        private static bool TryGetQuiet(Func<object> accessor, out dynamic value, out string reason)
        {
            value = null;
            reason = null;

            try
            {
                object result = accessor();
                if (result == null)
                {
                    reason = "present but null";
                    return false;
                }
                value = result;
                return true;
            }
            catch (Exception ex)
            {
                reason = DiagnosticLog.Describe(ex);
                return false;
            }
        }

        public static Tuple<string, Func<object>> Alt(string name, Func<object> accessor)
        {
            return Tuple.Create(name, accessor);
        }
    }
}
