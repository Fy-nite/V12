using System;
using System.Collections.Generic;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Physics;
using V12.Gravity.Components;

namespace V12.Gravity.Systems
{
    /// <summary>
    /// Rebuilds the active <see cref="GravityField"/> from every enabled
    /// <see cref="GravitySourceComponent"/> in the ticking worlds and hands it to
    /// the physics backend. Register this before the physics step service so the
    /// field is current for the tick.
    ///
    /// The source list is cached. Discovering sources is an O(world) tree walk,
    /// so it only runs when the world structure changes or on a slow rescan
    /// interval; the per-tick path only reads the cached source positions. A
    /// source component added to an existing element is picked up by the rescan
    /// (up to 0.25 s) rather than immediately.
    /// </summary>
    public sealed class GravityFieldSystem : IGameService
    {
        private const float RescanInterval = 0.25f;

        private readonly GameRoot _root;
        private readonly GravityField _field = new();
        private readonly List<GravitySourceComponent> _sources = new();
        private readonly HashSet<World> _observedWorlds = new();
        private IGravityFieldBackend? _backend;
        private float _rescanTimer;
        private bool _rescanPending;

        public GravityField Field => _field;
        public int LastSourceCount { get; private set; }

        public GravityFieldSystem(GameRoot root)
        {
            _root = root;
        }

        public void Initialize(GameRoot gameRoot)
        {
            _backend = gameRoot.PhysicsBackend as IGravityFieldBackend;
            if (_backend != null)
                _backend.GravityField = _field;
        }

        public void Update(GameRoot gameRoot) { }

        public void Update(float deltaTime)
        {
            if (_backend == null)
            {
                _backend = _root.PhysicsBackend as IGravityFieldBackend;
                if (_backend != null)
                    _backend.GravityField = _field;
            }

            _rescanTimer -= deltaTime;
            if (_rescanPending || _rescanTimer <= 0f)
            {
                RebuildSourceCache();
                _rescanPending = false;
                _rescanTimer = RescanInterval;
            }

            _field.Clear();
            foreach (var source in _sources)
            {
                if (source.Owner != null && source.Enabled && source.Mu > 0f)
                    _field.Add(source.ToSource());
            }

            LastSourceCount = _field.SourceCount;
        }

        private void RebuildSourceCache()
        {
            _sources.Clear();

            foreach (var world in _root.WorldManager.ActiveWorlds)
            {
                if (!world.TickingEnabled) continue;

                Observe(world);
                foreach (var root in world.Root)
                    CollectSources(root);
            }
        }

        private void Observe(World world)
        {
            if (!_observedWorlds.Add(world)) return;

            world.ElementAdded += OnWorldStructureChanged;
            world.ElementRemoved += OnWorldStructureChanged;
        }

        private void OnWorldStructureChanged(IWorldElement _) => _rescanPending = true;

        private void CollectSources(IWorldElement element)
        {
            var source = element.GetComponent<GravitySourceComponent>();
            if (source != null)
                _sources.Add(source);

            foreach (var child in element.Children)
                CollectSources(child);
        }
    }

    /// <summary>Registers the gravity field service with a V12 game root.</summary>
    public static class GravityRegistry
    {
        public static void RegisterAll(GameRoot gameRoot)
        {
            if (gameRoot.Registry.Get<GravityFieldSystem>() != null)
                return;

            gameRoot.Registry.Register("GravityFieldSystem", new GravityFieldSystem(gameRoot));
        }
    }
}
