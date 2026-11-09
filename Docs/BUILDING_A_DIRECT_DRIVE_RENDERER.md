# Direct drive

unlike (building a regular renderer)[BUILDING_A_RENDERER.md], building a direct drive renderer allow's for some nice features for development.


Direct drive renderers break the normal "Renderers must not know X" rule with things like:
- `IWorldElement`, `World`, `GameRoot`, `ComponentBase`, `V12.Components.*`
- `GetComponent<T>()` on a live element
- Reading or writing `TransformComponent`, `MaterialComponent`, `ToggleComponent`, and friends
- Holding live components in renderer state or packet DTOs

this allow's renderers to become smarter and allow for nicer editing capabilities