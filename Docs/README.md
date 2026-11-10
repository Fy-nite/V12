# V12 Docs

Cross-repo documentation for the V12 engine, its optional packages, renderers,
and authoring tools.

| Document | Purpose |
|---|---|
| `ARCHITECTURE.md` | Repo map, ownership rules, renderer and UI boundaries, data flow |
| `BOUNDARY_REFACTOR_PLAN.md` | Staged plan for the V12.Paper / V12.Editor split and cleanup |
| `WORKING_ON_V12.md` | Checkout, build, test, and contribution workflow |
| `GAMEPAK_PROJECTS.md` | .NET game paks: component discovery, MSBuild reference keys, pak layout and manifest keys |
| `INPUT_SYSTEM.md` | Input events, `InputActionMap`, key bindings, and rebinding |
| `BUILDING_A_RENDERER.md` | Implementing a backend renderer against V12 packets |
| `RendererContract.md` | Normative contract between the engine and a renderer |
| `SHADERS.md` | Custom shaders: the `shader://` asset, Godot's three doors, and the phase plan |
| `FEDERATED_INSTANCES.md` | VNode/V12 split, world regions, host leases and handoff, workers, identity, and optional federation |
| `BUILDING_GAMES.md` | Gameplay cheatsheet: fixed-step rules, transforms, WorldML, SyncValue, and common recipes |
| `CHANGELOG.md` | Engine change history and migration notes |

Other docs remain close to their code:

- Engine internals: `V12-Engine/Docs/`, including the
  [SyncValue&lt;T&gt; usage guide](../V12-Engine/Docs/SYNC_VALUE_GUIDE.md)
  and the [simulation timing guide](../V12-Engine/Docs/SIMULATION_TIMING.md)
- Godot/Nova: `Nova/Docs/` and `Nova/Nova.Shared/readme.md`
- Studio: `V12Studio/docs/`
