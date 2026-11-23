using System;
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
    /// </summary>
    public sealed class GravityFieldSystem : IGameService
    {
        private readonly GameRoot _root;
        private readonly GravityField _field = new();
        private IGravityFieldBackend? _backend;

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

            _field.Clear();

            foreach (var world in _root.WorldManager.ActiveWorlds)
            {
                if (!world.TickingEnabled) continue;
                foreach (var root in world.Root)
                    AddSources(root);
            }

            LastSourceCount = _field.SourceCount;
        }

        private void AddSources(IWorldElement element)
        {
            var source = element.GetComponent<GravitySourceComponent>();
            if (source != null && source.Enabled && source.Mu > 0f)
                _field.Add(source.ToSource());

            foreach (var child in element.Children)
                AddSources(child);
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
