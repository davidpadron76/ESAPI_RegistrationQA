using System;
using System.Globalization;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Acceso tolerante a objetos <c>dynamic</c> de la API de Varian.
    ///
    /// La API se consulta de forma dinámica porque la superficie de VMS.IRS varía entre
    /// versiones de Eclipse y no todas las propiedades existen en todas ellas. Lo que no es
    /// aceptable es que esa tolerancia sea silenciosa: cada intento fallido queda anotado en
    /// el <see cref="DiagnosticLog"/> con la operación y la excepción concreta, de modo que
    /// el físico pueda ver por qué una métrica salió como no disponible.
    ///
    /// Todos los helpers reciben el acceso envuelto en un delegado para poder encadenar
    /// propiedades (<c>() =&gt; reg.SourceImage.Frame.Origin</c>) sin que una excepción a
    /// mitad de la cadena se pierda.
    /// </summary>
    public static class Dyn
    {
        /// <summary>
        /// Evalúa el acceso y devuelve true si no lanzó excepción y el resultado no es nulo.
        /// </summary>
        public static bool TryGet(string operation, Func<object> accessor, DiagnosticLog log, out dynamic value)
        {
            value = null;
            try
            {
                object result = accessor();
                if (result == null)
                {
                    if (log != null) log.Info(operation, "la propiedad existe pero es nula");
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
                    if (log != null) log.Info(operation, "la propiedad existe pero es nula");
                    return false;
                }

                value = Convert.ToDouble(result, CultureInfo.InvariantCulture);

                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    if (log != null) log.Warning(operation, "valor no finito: " + value.ToString(CultureInfo.InvariantCulture));
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
                    if (log != null) log.Info(operation, "la propiedad existe pero es nula");
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
                    if (log != null) log.Info(operation, "la propiedad existe pero es nula");
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
        /// Ejecuta una acción sobre la API tolerando el fallo pero dejándolo registrado.
        /// Devuelve true si se completó.
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
        /// Devuelve el primer acceso que tenga éxito de una lista de alternativas, anotando
        /// cuál funcionó. Útil cuando la misma información vive en propiedades con nombres
        /// distintos según la versión de la API.
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
