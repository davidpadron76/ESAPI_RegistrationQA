using System;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Lleva un punto en coordenadas de paciente de la imagen origen a la imagen destino
    /// según el registro.
    ///
    /// La abstracción existe porque un registro rígido y uno deformable se evalúan igual
    /// una vez que se dispone del mapeo: lo que cambia es cómo se obtiene. Aplicar sólo la
    /// componente lineal a un registro deformable produciría métricas que no describen el
    /// registro evaluado.
    /// </summary>
    public interface IPointMapper
    {
        bool TryMap(Vec3 source, out Vec3 target);

        /// <summary>Descripción de la procedencia del mapeo, para el reporte.</summary>
        string Description { get; }

        /// <summary>
        /// Presupuesto razonable de puntos a mapear. Un mapeo por matriz es prácticamente
        /// gratuito; uno que invoca la API por cada punto no lo es.
        /// </summary>
        int RecommendedSampleBudget { get; }
    }

    /// <summary>Mapeo por matriz rígida: aritmética pura, sin coste de invocación.</summary>
    public sealed class RigidPointMapper : IPointMapper
    {
        private readonly RigidTransform _transform;

        public RigidPointMapper(RigidTransform transform, string description)
        {
            if (transform == null) throw new ArgumentNullException("transform");
            _transform = transform;
            Description = description;
        }

        public string Description { get; private set; }

        public int RecommendedSampleBudget { get { return VoxelPairSampler.MaxRetainedPairs; } }

        public bool TryMap(Vec3 source, out Vec3 target)
        {
            target = _transform.Apply(source);
            return true;
        }
    }

    /// <summary>
    /// Mapeo delegado a la API de Varian punto a punto. Es el único camino correcto para un
    /// registro deformable, pero cada invocación dinámica cuesta órdenes de magnitud más que
    /// una multiplicación de matrices, de ahí el presupuesto de muestras reducido.
    /// </summary>
    public sealed class DynamicPointMapper : IPointMapper
    {
        /// <summary>
        /// Suficiente para un NCC estable y para un histograma conjunto de 32x32 con ~50
        /// muestras por celda, manteniendo el tiempo total en el orden de segundos.
        /// </summary>
        public const int PointBudget = 60000;

        private readonly Func<Vec3, Vec3?> _map;
        private int _consecutiveFailures;

        public DynamicPointMapper(Func<Vec3, Vec3?> map, string description)
        {
            if (map == null) throw new ArgumentNullException("map");
            _map = map;
            Description = description;
        }

        public string Description { get; private set; }

        public int RecommendedSampleBudget { get { return PointBudget; } }

        /// <summary>true si el mapeo falló de forma sostenida y no merece seguir intentándolo.</summary>
        public bool HasCollapsed { get { return _consecutiveFailures > 200; } }

        public bool TryMap(Vec3 source, out Vec3 target)
        {
            target = Vec3.Zero;
            if (HasCollapsed) return false;

            try
            {
                Vec3? mapped = _map(source);
                if (!mapped.HasValue || !mapped.Value.IsFinite)
                {
                    _consecutiveFailures++;
                    return false;
                }

                _consecutiveFailures = 0;
                target = mapped.Value;
                return true;
            }
            catch
            {
                // El detalle ya se registró al construir el mapeador; aquí sólo se cuenta
                // para poder abortar si la API falla sistemáticamente.
                _consecutiveFailures++;
                return false;
            }
        }
    }
}
