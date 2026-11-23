using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Core.Interfaces.Physics;

namespace V12.Gravity
{
    /// <summary>
    /// A mutable set of point-mass gravity sources. The physics backend snapshots
    /// this field at the start of each step, so add or remove sources between
    /// steps (the GravityFieldSystem rebuilds it every fixed tick).
    /// </summary>
    public sealed class GravityField : IGravityField
    {
        private readonly List<GravitySource> _sources = new();

        public int SourceCount => _sources.Count;
        public IReadOnlyList<GravitySource> Sources => _sources;

        public void Clear() => _sources.Clear();

        public void Add(GravitySource source) => _sources.Add(source);

        public void Add(Vector3 position, float mu, float surfaceRadius = 0f)
            => _sources.Add(new GravitySource(position, mu, surfaceRadius));

        public bool RemoveAt(int index)
        {
            if ((uint)index >= (uint)_sources.Count) return false;
            _sources.RemoveAt(index);
            return true;
        }

        public int CopySources(Span<GravitySource> destination)
        {
            int count = Math.Min(destination.Length, _sources.Count);
            for (int i = 0; i < count; i++)
                destination[i] = _sources[i];
            return count;
        }

        public Vector3 SampleAcceleration(Vector3 position)
        {
            var acceleration = Vector3.Zero;
            for (int i = 0; i < _sources.Count; i++)
                acceleration += _sources[i].AccelerationAt(position);
            return acceleration;
        }
    }
}
