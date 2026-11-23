using System;
using V12.Basic.Building;
using V12.Components;
using V12.Components.UI;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Networking;
using V12.Core.UI;
using V12.Core.UI.Paper;
using UIProgress = V12.Components.UI.ProgressBarComponent;

namespace UiShowcase;

/// <summary>
/// The only C# in the UI showcase. The widgets and all of their data are
/// authored in <c>Scenes/Main.worldml</c>; widgets with events name an action
/// (<c>Action="pulse"</c>) and this component registers the handlers once.
///
/// Two things still belong in code by nature: behaviour (what a click does) and
/// the palette retint. Everything structural is WorldML.
/// </summary>
public sealed class UiShowcaseController : ComponentBase
{
    private static readonly string[] ActionNames =
    {
        "pulse", "reset", "spin", "grid", "power", "tab", "row", "pick", "knock",
    };

    private bool _wired;
    private bool _spin = true;
    private int _pulses;
    private int _knocks;
    private float _power = 0.4f;
    private float _angle;
    private string _lastEvent = "none";

    public override string Name => "Showcase Controller";

    public override void Update(float deltaTime)
    {
        Wire();

        if (!_spin || Owner is null)
            return;

        var cube = Find("Spinner");
        if (cube is null)
            return;

        _angle = (_angle + 35f * deltaTime) % 360f;
        cube.GetOrAddTransform().RotationY = _angle;
    }

    public override void OnDetach(IWorldElement worldElement)
    {
        // Hot reload detaches this component; the next generation registers its
        // own handlers. Drop ours so a stale closure cannot fire.
        foreach (var name in ActionNames)
            UiActions.Unregister(name);

        base.OnDetach(worldElement);
    }

    private void Wire()
    {
        if (_wired || Owner is null)
            return;

        _wired = true;
        ApplyPalette();

        UiActions.Register("pulse", _ =>
        {
            _pulses++;
            _power = MathF.Min(1f, _power + 0.1f);
            SetEvent("pulse");
            ApplyPower();
        });

        UiActions.Register("reset", _ =>
        {
            _pulses = 0;
            _power = 0.4f;
            SetEvent("reset");
            ApplyPower();
        });

        UiActions.Register("spin", args =>
        {
            _spin = args.On;
            SetEvent($"spin {(args.On ? "on" : "off")}");
        });

        UiActions.Register("grid", args => SetEvent($"grid {(args.On ? "on" : "off")}"));

        UiActions.Register("power", args =>
        {
            _power = args.Value;
            SetEvent($"power {args.Value:0.00}");
            ApplyPower();
        });

        UiActions.Register("tab", args => SetEvent($"tab {args.Index}"));
        UiActions.Register("row", args => SetEvent($"row {args.Index}"));
        UiActions.Register("pick", args => SetEvent($"pick {args.Index}"));

        UiActions.Register("knock", _ =>
        {
            _knocks++;
            SetLabel("PanelStatus", $"knocks {_knocks}");
        });

        ApplyPower();
    }

    private void ApplyPower()
    {
        if (Find("PowerBar")?.GetComponent<UIProgress>() is { } bar)
        {
            bar.Value = _power;
            bar.RaiseDirty();
        }

        if (Find("Table")?.GetComponent<TableComponent>() is { } table && table.Rows.Count >= 3)
        {
            table.Rows[0].Cells[1] = _pulses.ToString();
            table.Rows[1].Cells[1] = _power.ToString("0.00");
            table.Rows[2].Cells[1] = _spin ? "on" : "off";
            table.RaiseDirty();
        }
    }

    private void SetLabel(string name, string text)
    {
        if (Find(name)?.GetComponent<LabelComponent>() is not { } label)
            return;

        label.Text = text;
        label.RaiseDirty();
    }

    private void SetEvent(string text)
    {
        GameRoot.Log.Information("[UiShowcase] action {Event}", text);
        _lastEvent = text;
        SetLabel("Status", $"pulses {_pulses} - {_lastEvent}");
    }

    private IWorldElement? Find(string name)
        => Owner?.GetRoot().FindChildByNameRecursive(name);

    /// <summary>
    /// Warm neutral + amber. The engine default is a cool blue-charcoal; hosts
    /// own the palette, so a game may retint it once at start-up.
    /// </summary>
    private static void ApplyPalette()
        => PaperPalette.Set(
            canvas: PaperPalette.FromHex("#151310"),
            panel: PaperPalette.FromHex("#1d1a15"),
            raised: PaperPalette.FromHex("#272319"),
            hover: PaperPalette.FromHex("#332d21"),
            selected: PaperPalette.FromHex("#3c3524"),
            border: PaperPalette.FromHex("#3a3327"),
            accent: PaperPalette.FromHex("#c9973f"),
            text: PaperPalette.FromHex("#f1e9d9"),
            muted: PaperPalette.FromHex("#a89d89"),
            disabled: PaperPalette.FromHex("#6f6656"),
            isLight: false,
            danger: PaperPalette.FromHex("#cf6650"),
            success: PaperPalette.FromHex("#8aa96f"));
}
