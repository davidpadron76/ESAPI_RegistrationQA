using System;
using ESAPI_RegistrationQA.Models;

namespace ESAPI_RegistrationQA.Services
{
    /// <summary>
    /// Maps a point in patient coordinates from the source image to the target image
    /// according to the registration.
    ///
    /// The abstraction exists because a rigid and a deformable registration are evaluated
    /// identically once the mapping is available: what differs is how it is obtained.
    /// Applying only the linear component to a deformable registration would produce metrics
    /// that do not describe the registration under audit.
    /// </summary>
    public interface IPointMapper
    {
        bool TryMap(Vec3 source, out Vec3 target);

        /// <summary>Where the mapping came from, for the report.</summary>
        string Description { get; }

        /// <summary>
        /// A sensible budget of points to map. Matrix mapping is essentially free; mapping
        /// that invokes the API per point is not.
        /// </summary>
        int RecommendedSampleBudget { get; }
    }

    /// <summary>Rigid matrix mapping: pure arithmetic, no invocation cost.</summary>
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
    /// Mapping delegated to the Varian API point by point. This is the only correct route
    /// for a deformable registration, but each dynamic invocation costs orders of magnitude
    /// more than a matrix multiplication, hence the reduced sample budget.
    /// </summary>
    public sealed class DynamicPointMapper : IPointMapper
    {
        /// <summary>
        /// Enough for a stable NCC and for a 32x32 joint histogram with ~50 samples per
        /// cell, while keeping total runtime in the order of seconds.
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

        /// <summary>true when mapping has failed persistently and is not worth retrying.</summary>
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
                // The detail was already recorded when the mapper was built; here we only
                // count so we can abort if the API fails systematically.
                _consecutiveFailures++;
                return false;
            }
        }
    }
}
