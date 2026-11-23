using V12;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;

namespace FirstProbe
{
    public sealed class ResolverProbeComponent : ComponentBase
    {
        public override IWorldElement BuildUI() => new Element();
    }
}

namespace SecondProbe
{
    public sealed class ResolverProbeComponent : ComponentBase
    {
        public override IWorldElement BuildUI() => new Element();
    }
}

namespace GamepackProbe
{
    public sealed class ProbeGamepack : IV12Gamepack, IV12GamepackLifecycle
    {
        public static bool Stopped;

        public string Name => "Probe Gamepak";

        public void Initialize() { }
        public void OnStart() { }
        public void OnStop() => Stopped = true;
    }
}
