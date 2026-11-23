using System;
using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Physics;
using V12.Core.UI;

namespace V12.Gravity.Components
{
    /// <summary>
    /// Marks an element as a point-mass gravity source (planet, moon, star).
    /// The element's world position is the source position. Mu is the standard
    /// gravitational parameter (G * mass); SurfaceGravity is a convenience view
    /// for authoring.
    /// </summary>
    [ComponentEntry("PHYSICS", "Gravity Source", SingleInstance = true, SortOrder = 5, Icon = "globe")]
    public class GravitySourceComponent : ComponentBase
    {
        private float _mu = 3.986004418e14f;       // Earth
        private float _surfaceRadius = 6.371e6f;   // Earth mean radius, metres
        private bool _enabled = true;

        public override string Name => "Gravity Source";
        public override string Description => "Point-mass gravity source";

        /// <summary>Standard gravitational parameter, G * mass (m^3/s^2).</summary>
        public float Mu
        {
            get => _mu;
            set => SetSync(ref _mu, MathF.Max(0f, value));
        }

        /// <summary>Surface radius used to keep the field finite inside the body.</summary>
        public float SurfaceRadius
        {
            get => _surfaceRadius;
            set => SetSync(ref _surfaceRadius, MathF.Max(0.001f, value));
        }

        /// <summary>Surface gravity in m/s^2. Reading derives it from Mu and radius;
        /// writing it adjusts Mu so the surface value matches.</summary>
        public float SurfaceGravity
        {
            get => _surfaceRadius > 0f ? _mu / (_surfaceRadius * _surfaceRadius) : 0f;
            set
            {
                if (_surfaceRadius <= 0f) return;
                Mu = value * _surfaceRadius * _surfaceRadius;
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set => SetSync(ref _enabled, value);
        }

        public GravitySourceComponent() { }

        public GravitySourceComponent(float mu, float surfaceRadius)
        {
            _mu = MathF.Max(0f, mu);
            _surfaceRadius = MathF.Max(0.001f, surfaceRadius);
        }

        /// <summary>Copy radius and mu from a planet profile.</summary>
        public void Apply(PlanetProfile profile)
        {
            Mu = (float)profile.Mu;
            SurfaceRadius = (float)profile.Radius;
        }

        public static GravitySourceComponent FromProfile(PlanetProfile profile)
        {
            var component = new GravitySourceComponent();
            component.Apply(profile);
            return component;
        }

        /// <summary>World position of this source, using the element's world matrix.</summary>
        public Vector3 WorldPosition
        {
            get
            {
                if (Owner == null) return Vector3.Zero;
                var matrix = Owner.WorldTransform;
                return new Vector3(matrix.M41, matrix.M42, matrix.M43);
            }
        }

        public GravitySource ToSource() => new(WorldPosition, _mu, _surfaceRadius);

        public override void BuildInspector(IInspector inspector)
        {
            inspector.Section("Gravity Source");
            inspector.Float("Mu", () => Mu, v => Mu = v);
            inspector.Float("Surface Radius", () => SurfaceRadius, v => SurfaceRadius = v);
            inspector.Float("Surface Gravity", () => SurfaceGravity, v => SurfaceGravity = v);
            inspector.Bool("Enabled", () => Enabled, v => Enabled = v);
        }

        public override IWorldElement BuildUI() => new Element();

        public override string ToString()
            => $"GravitySource(Mu:{Mu:G6} R:{SurfaceRadius:G6} g:{SurfaceGravity:F2})";
    }
}
