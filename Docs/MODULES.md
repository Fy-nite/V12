# V12 Modules

Modules are the trusted-code half of V12's mod support. A module is a .NET
assembly, loaded into a **running** game, that attaches to `GameRoot` and can do
anything the game process can: draw overlays, add services, expose tools, patch
behaviour, host an integration.

Content and components already have a home: game paks. Modules are for code that
needs the live process and the engine's own types.

```
modules/
  MyOverlay/
    MyOverlay.dll
  V12.MCP.Probe.dll
```

## Opt-in by construction

Nothing loads unless the host is asked to load it.

| Switch | Effect |
|---|---|
| `--modules` | load every module in `modules/` next to the executable |
| `--modules <dir>` | load every module in that directory |
| `--module <dll>` | load one assembly, repeatable |
| `--no-modules` | disable the system entirely, including the switches above |

Relative paths in these switches resolve against the host's base directory, not
the working directory.

A game that wants mods without a switch can call the loader directly:

```csharp
root.Modules.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "mods"), args);
```

Nothing is scanned by default, so a build that never opts in has no module code
running and no attack surface.

## Writing a module

```csharp
using System;
using System.Collections.Generic;
using V12.Core.Modules;

public sealed class MyOverlay : IV12Module
{
    public string Name => "my.overlay";              // stable id
    public string? Version => "1.0.0";
    public IReadOnlyList<string> Dependencies => Array.Empty<string>();

    public bool Start(ModuleContext context)
    {
        context.Log.Information("overlay attached at {Tick}", context.Root.SimulationTick);
        // register services, subscribe to events, open a window, ...
        return true;
    }

    public void Dispose() { }
}
```

Requirements:

- reference `V12.dll`
- public, non-abstract type with a parameterless constructor
- implement `IV12Module`
- loaded through the host's assembly loader so it shares the engine's identity

Return `false` from `Start` to decline (for example, when the module's own switch
is absent). A declined module is disposed and not kept.

### `ModuleContext`

| Member | Meaning |
|---|---|
| `Root` | the live `GameRoot` |
| `Args` | the full command line, including user args after `--` |
| `AssemblyPath` | absolute path of the module assembly |
| `ModuleDirectory` | directory the assembly was loaded from |
| `DataDirectory` | writable per-module folder under local app data, created if possible |
| `Log` | the engine's Serilog logger |

Use `DataDirectory` for settings and caches. Writing next to the game fails in
Program Files and breaks per-user separation.

## Lifecycle

1. The host discovers assemblies (`--modules`, `--module`, or a direct call).
2. Each type implementing `IV12Module` is constructed.
3. Modules are started in dependency order.
4. `Start` returns true: the module is kept and reported as started.
5. On `GameRoot.Shutdown` (or host exit), modules are disposed in reverse start
   order.

Modules are **not** unloadable. They are loaded non-collectible so engine type
identity stays stable, which means no hot reload: restart the game to pick up a
new module build.

## Dependencies

`Dependencies` lists module ids that must be started first.

- A dependency already started (or in the same batch and resolvable) is waited
  for.
- A dependency that is missing entirely rejects the module with
  `missing dependency: ...`.
- A cycle is reported and every member of the cycle is rejected, rather than
  half-starting a set.
- One bad module never stops the rest: rejections are recorded and the remaining
  modules still start.
- If a dependency is rejected or declines, its dependents are rejected too
  (their dependency never entered the started set).

Id matching is case-insensitive. Ids must be unique; a duplicate is rejected.

## Diagnostics

Every considered module gets a `ModuleInfo` entry:

```csharp
foreach (var info in root.Modules.Diagnostics)
    Console.WriteLine($"{info.Name} started={info.Started} error={info.Error}");
```

`root.Modules.Describe()` returns a readable summary:

```
  [ok] my.overlay 1.0.0
  [--] broken: missing dependency: my.core
```

The MCP probe exposes the same list at `GET /v1/modules`, and the MCP server
wraps it as the `list_modules` tool.

## Trust and failure containment

- Modules run with full process privileges. `--module` is a trust decision; only
  load code you would run anyway.
- There is no sandbox, signing or permission model. Do not load third-party
  modules you have not reviewed.
- Assemblies already present in the process are skipped by simple name, so a
  modules folder can also contain dependencies without creating duplicate
  identities.
- Construction, start and dispose failures are caught, logged and reported.
  A broken optional module cannot take the game down.
- The engine never references a module, so shipping builds cannot accidentally
  include one.

## Modules vs game paks

| | Module | Mod pak | Game pak |
|---|---|---|---|
| Contains | code that attaches to the live root | a `module.json` plus content and optional code | content, worlds, components, gameplay |
| Loaded by | `ModuleHost`, on request | `PakModHost`, on request | `V12PakLoader`, from `paks/`/`.v12pak` |
| Discovery | `--modules` / `--module` | `--mods` / `--mod` | pak directory / manifest |
| Trust | full process trust | content is data; its DLLs/scripts are code | content is data; pak DLLs are also code |
| Unload | no | no | world can be closed, assembly stays |

A mod pak is a game pak with `module.json`, loaded later and optionally carrying
`IV12Module` code. Content mods need no code at all.

A mod that adds a weapon is a pak. A mod that adds a settings overlay or a
debug console is a module. Some projects will ship both.

## Mods as paks

A mod is an ordinary `.v12pak` whose root contains `module.json`. Everything
else uses the normal pak layout, so `vcsdk pack` builds mods and content paks
with the same command.

```
MyMod/
  module.json
  worlds/            content worlds (normal pak layout)
  assets/            textures, models, audio
  paks/*.dll         gameplay and module assemblies
  bin/               third-party dependencies
  scripts/           .ct entrypoints and live .cs sources
```

`module.json`:

```json
{
  "apiVersion": 1,
  "id": "my.mod",
  "name": "My Mod",
  "version": "1.0.0",
  "author": "you",
  "description": "what it does",
  "dependencies": ["other.mod"]
}
```

| Field | Required | Meaning |
|---|---|---|
| `apiVersion` | yes | must equal the engine's supported version (currently `1`) |
| `id` | yes | unique, stable, used for dependencies and the disabled list |
| `name`, `version`, `author`, `description` | no | shown in diagnostics |
| `dependencies` | no | mod ids or module ids that must load first |

Build and install:

```powershell
vcsdk pack D:\git\MyMod            # produces bin/MyMod.v12pak
copy bin\MyMod.v12pak <game>\mods\
```

`vcsdk pack` copies `module.json` into the archive automatically. It also stages
assets, worlds, scripts and third-party dependencies exactly as it does for a
game pak.

Load:

```powershell
NovaV2 --xr-mode off -- --mods          # every mod in mods/
NovaV2 --xr-mode off -- --mod mods\MyMod.v12pak
NovaV2 --xr-mode off -- --mods --disable-mod other.mod
```

| Switch | Effect |
|---|---|
| `--mods` | load every `*.v12pak` under `mods/`, recursively |
| `--mods <dir>` | load from another directory |
| `--mod <pak>` | load one mod pak |
| `--disable-mod <id>` | skip a mod id, repeatable |
| `--no-mods` | disable mod loading entirely |

Disabling is persisted in
`%LOCALAPPDATA%\V12\<executable name>\mods.json`, so it survives the next launch
without editing a file by hand.

Load order per mod: validate the manifest, check dependencies, load the pak
(content, scripts, DLLs), start any gamepaks it contributed, then start its
`IV12Module` types with the rest of the modules. A mod pak with no module code is
a valid content mod.

A mod's dependencies may name another mod id or a raw module id (for example the
MCP probe). A missing dependency, an API mismatch, or a duplicate id refuses the
mod and reports why; other mods still load.

Diagnostics are available three ways: `root.Registry.Get<IModHost>("Mods")`,
`GET /v1/mods` on the MCP probe, and the `list_mods` MCP tool.

### Trust

A mod pak can load DLLs and compile scripts, exactly like a game pak. There is no
sandbox, no signing and no capability model yet. Enabling a mod is a decision to
run its code with the game's privileges. Only enable mods you trust, and treat a
`mods/` folder like a `paks/` folder, not like user data.

## Host integration

One call in the startup path after the root exists:

```csharp
GameRoot root = ...;
root.Modules.EnableFromArgs(args, AppContext.BaseDirectory);
```

NovaV2 does this and adds `--mcp` as sugar for loading
`modules/V12.MCP.Probe.dll`. Any other host (Pulsar, Rockstar, Studio) gets the
same behaviour from the same call.

## Limits

- No unload or hot reload for modules or mods.
- No manifest file for raw modules yet: identity, version and dependencies live
  on the type. Mod paks use `module.json`.
- No version constraints between mods, only id presence.
- No sandbox, signing or capability model. Enabling a mod runs its code.
- Mod enable/disable is applied at load time; changing it does not unload a
  running mod.
- No cross-process isolation; a module or mod runs in the game.
- Directory scans order by file name; dependency order is resolved within a
  batch, not across separate `--modules` directories.
