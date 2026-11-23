using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;

namespace UiShowcase;

/// <summary>
/// A plain component with public fields, placed on the same element as a
/// <see cref="V12.Components.UI.PropertyGridComponent"/>. The property grid
/// reflects these fields and edits them live, which is the simplest way to
/// expose tunables from WorldML.
/// </summary>
public sealed class UiShowcaseGauge : ComponentBase
{
    public float Temperature = 21.5f;
    public float Pressure = 1.02f;
    public int Samples = 32;
    public bool Enabled = true;
    public string Label = "ambient";

    public override string Name => "Showcase Gauge";
    public override string Description => "Fields shown in the Origami property grid";
    public override IWorldElement BuildUI() => new Element();
}
