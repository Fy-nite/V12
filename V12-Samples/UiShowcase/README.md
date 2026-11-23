# UI Showcase

A NovaV2 sample that authors every V12 UI widget in **WorldML**. The only C# is
a small controller that fills list-based widgets and wires callbacks.

## Run

From the repo root:

```powershell
.\scripts\run-host.ps1 -Demo UiShowcase -UseGame -HotReload -Xr off
```

Or directly:

```powershell
Godot_v4.8-beta3_mono_win64_console.exe `
  --path NovaV2/NovaV2.Host --xr-mode off -- `
  --game V12-Samples/UiShowcase --hot-reload --xr 0
```

`--xr-mode off` (Godot) and `--xr 0` (NovaV2) keep it on the flat desktop path.

## Files

| File | Contents |
|---|---|
| `Scenes/Main.worldml` | The whole UI: a screen-space HUD and a world-space panel |
| `Scripts/UiShowcaseController.cs` | Registers the named actions and rotates the cube |
| `Scripts/UiShowcaseGauge.cs` | Public fields shown and edited by the property grid |

## What is authored where

Everything lives in WorldML as components, including list data and widget
handlers:

```xml
<Element name="PulseButton">
  <Component type="V12.Components.UI.ButtonComponent" Label="Pulse" Action="pulse" />
  <Component type="LayoutElementComponent" PreferredWidth="104" PreferredHeight="32" />
</Element>

<Component type="TabsComponent" Tabs="Controls,Data,About" Action="tab" />

<Component type="V12.Components.UI.TableComponent" Action="row">
  <Columns>
    <Item Header="Signal" Flex="2" />
    <Item Header="Live" Flex="1" />
  </Columns>
  <Rows>
    <Item>
      <Cells>
        <Item>Pulse count</Item>
        <Item>0</Item>
      </Cells>
    </Item>
  </Rows>
</Component>
```

C# is then only needed for **behaviour**, not structure:

```csharp
UiActions.Register("pulse", _ => { ... });
```

A component's own delegate still wins when one is attached, so gameplay code can
override an authored action without touching the world file.

## Named actions

Widgets with events take an `Action` string:

| Widget | Action receives |
|---|---|
| Button | `UiActionArgs.Owner` |
| Toggle / Checkbox | `On` |
| Slider | `Value` |
| Tabs / Table / Dropdown | `Index` |

`UiActions.Register(name, handler)` binds it, `UiActions.Unregister` removes it.
The controller unregisters on `OnDetach`, which keeps hot reload clean.

## Widget coverage

| Widget | Component | Notes |
|---|---|---|
| Canvas | `CanvasComponent` | `ScreenSpace="true"` = HUD overlay, `false` = surface in the scene |
| Label | `LabelComponent` | `UIStyleComponent` supplies `title` / `muted` / `accent` / `danger` |
| Button | `V12.Components.UI.ButtonComponent` | fully qualified: a legacy `V12.Components.ButtonComponent` also exists |
| Toggle | `ToggleComponent` | click-to-flip; the state is written back to the component |
| Checkbox | `CheckboxComponent` | click-to-flip; state persists |
| Slider | `SliderComponent` | click-to-set across `Min`..`Max`; shows its value |
| Progress bar | `V12.Components.UI.ProgressBarComponent` | fully qualified for the same reason as Button |
| Text input | `TextInputComponent` | displays text; typing is not wired in NovaV2 yet |
| Layouts | `VLayoutComponent` / `HLayoutComponent` | `Spacing`, `Padding`, `Expand` |
| Layout sizing | `LayoutElementComponent` | preferred/min/flexible width and height |
| Rect / card | `RectComponent` | `Width`/`Height` in pixels, `BackgroundColor` `#rrggbb[aa]`, `CornerRadius` |
| Separator | `SeparatorComponent` | |
| Spinner | `SpinnerComponent` | `Size` |
| Tabs + pages | `TabsComponent` + `TabPageComponent` | `Tabs="A,B,C"`; page bodies are children of the tabs element |
| Table | `TableComponent` | `<Columns>` / `<Rows>` blocks; `ScrollWidth`/`ScrollHeight` for a scroll region |
| Dropdown | `DropdownComponent` | `Items="A,B,C"` |
| Viewport | `ViewportComponent` | not used here; shows a render view inside UI |
| Property grid | `PropertyGridComponent` | reflects public fields of the components on the same element |
| Tree | `TreeComponent` | model comes from code; not included in this sample |

## Colour space

Paper colours are **linear** `0..1`. A `#rrggbb` string in WorldML is passed
through unchanged, so a mid-tone hex looks lighter on screen than in a colour
picker. The cards here use `#060504f2`; the palette is retinted in the
controller with `PaperPalette.Set`.

## Hot reload

Run with `--hot-reload` and save either file:

- WorldML edit: the world is reparsed and swapped (component values included).
- C# edit: the script assembly recompiles, gamepaks swap, the world reloads.

It is a scene reload: the player/XR rig is recreated and a network session
resets. See `Docs/HOT_RELOAD_NOVAV2.md`.

## Gotchas

- `ButtonComponent` and `ProgressBarComponent` exist twice (`V12.Components` and
  `V12.Components.UI`). Use the fully qualified name for the UI widgets; the
  parser warns when a short name is ambiguous.
- A `RectComponent` with no `Width`/`Height` hugs its content. Give it a size
  when you want a fixed card.
- `--hot-reload` needs `--game`; a packed `.v12pak` still needs a restart.
