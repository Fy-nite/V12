// HelloDotnet - V12 component script
using V12.Components;
using V12.Basic.Building;

namespace hellodotnet.Components
{
    /// <summary>Example component extending V12's ECS system.</summary>
    public sealed class DefaultScript : ScriptableComponent
    {
        /// <summary>Degrees per second applied to the owning element's Y rotation.</summary>
        public float Speed { get; set; } = 45f;

        public override void Update(float deltaTime)
        {
            if (Owner == null) return;
            // RY is radians; Speed is degrees per second.
            Owner.GetOrAddTransform().RY += Speed * deltaTime * (MathF.PI / 180f);
        }
    }
}
