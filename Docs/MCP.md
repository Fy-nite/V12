# V12 MCP

V12 MCP gives an MCP client read and write access to a **running** V12 game.
Ask an agent what is in the scene, move an element, change a light, attach a
component or press a key, and it happens in the live process at the next tick.

Nothing is compiled into the game. The probe is a module the host loads only
when you ask for it, and the MCP server is a separate process your client
launches. The module system itself is documented in
[`MODULES.md`](MODULES.md).

---

## How it fits together

```
MCP client (OpenCode, Claude, ...)
      │  stdio, JSON-RPC
      ▼
V12.MCP                    separate process, no engine reference
      │  HTTP + bearer token, loopback
      ▼
V12.MCP.Probe              loaded module inside the game process
      │  SceneIntrospection
      ▼
GameRoot                   commands queued, drained at the tick boundary
```

Three pieces, three jobs:

| Piece | Owns | Shipped with the game? |
|---|---|---|
| `V12.MCP.Probe` | transport, auth, live snapshot/command hop | only if you copy it into `modules/` |
| `V12.MCP` | MCP tools and JSON shaping | no, it is launched by the client |
| `SceneIntrospection` | the actual read/write surface on the engine | always (it is the engine) |

The engine and the renderer reference neither binary. A game launched without
`--mcp` runs exactly as before, and a build that never ships the probe DLL has
no code path that can open a port.

### The threading contract

This is the part that makes it safe:

- **Writes are queued.** A command goes into a concurrent queue and is applied
  at the start of a fixed step, before any world ticks. The tree is never
  mutated from an HTTP thread.
- **Reads are queued too.** `RequestSnapshotAsync` is answered by the same
  drain, after the commands queued before it, so a write followed by a read
  always observes the write.
- **Paused games still drain.** `FixedUpdate` runs no steps while paused, so
  `GameRoot.Update` drains with `paused: true` instead. Editing works with the
  clock stopped.
- **Timeouts instead of hangs.** If the host stops ticking, a snapshot request
  fails with `TimeoutException` and a command call returns 504 rather than
  blocking forever.

Any number of threads can call in at once. Each snapshot request gets its own
sequence number and is built on the tick thread, so no caller ever touches a
live component.

---

## Quick start

### 1. Build

```powershell
dotnet build V12.MCP.Probe
dotnet build V12.MCP
```

### 2. Put the probe where the game can find it

```powershell
New-Item -ItemType Directory -Force <game>/modules
Copy-Item V12.MCP.Probe/bin/Debug/net10.0/V12.MCP.Probe.dll <game>/modules/
```

Only that one DLL is needed. `V12.dll` and the framework are already in the game
process; the loader skips anything already loaded by name, so copying the whole
build output there is also safe.

### 3. Launch the game with the probe

```powershell
NovaV2 --xr-mode off --mcp -- --mcp-port 7420 --mcp-token dev
```

| Switch | Effect |
|---|---|
| `--mcp` | load `modules/V12.MCP.Probe.dll` and start it |
| `--module <dll>` | load any module assembly |
| `--modules <dir>` | load every module in a directory |
| `--mcp-port <n>` | listen port, default 7420; `0` picks a free one |
| `--mcp-bind <ip>` | default `127.0.0.1`; `0.0.0.0` exposes it to the network |
| `--mcp-readonly` | reject every write at the probe |
| `--mcp-token <t>` | fixed token; otherwise one is generated per launch |
| `--no-modules` | disable module loading, including `--mcp` |

Without `--mcp-token`, the generated token and URL are written to
`%TEMP%\v12-mcp\probe-<pid>.json`:

```json
{
  "url": "http://127.0.0.1:7420/",
  "token": "…",
  "readOnly": false,
  "pid": 33164,
  "process": "NovaV2"
}
```

### 4. Point the client at it

OpenCode (`opencode.jsonc`):

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "v12": {
        "type": "local",
        "command": ["dotnet", "D:/git/V12-Monorepo/V12.MCP/bin/Debug/net10.0/V12.MCP.dll"],
        "environment": {
          "V12_MCP_URL": "http://127.0.0.1:7420",
          "V12_MCP_TOKEN": "{env:V12_MCP_TOKEN}"
        }
      }
    }
  }
}
```

Any stdio MCP host works the same way: run `V12.MCP.dll` (or `V12.MCP.exe`) and
give it two variables.

| Variable | Default |
|---|---|
| `V12_MCP_URL` | `http://127.0.0.1:7420` |
| `V12_MCP_TOKEN` | required, from the handshake file or `--mcp-token` |

`V12.MCP` also accepts `--url` and `--token` directly.

---

## Probe HTTP API

Useful for debugging without an MCP client, and for scripts.

| Route | Purpose |
|---|---|
| `GET /v1/health` | liveness. **No token required**; exposes pid, port, read-only, world count, tick |
| `GET /v1/worlds` | world headers |
| `GET /v1/modules` | modules considered, with start state and rejection reasons |
| `GET /v1/mods` | mod paks considered, with load state and rejection reasons |
| `GET /v1/snapshot` | flat elements; `depth`, `limit`, `name`, `component`, `active`, `components=false` |
| `GET /v1/element/{id}` | one element; `components=false` for a cheap read |
| `GET /v1/journal?take=n` | recent applied commands |
| `GET /v1/receipts/{requestId}` | result of one command |
| `POST /v1/commands` | apply one command, an array, or `{"commands":[…]}` |

```powershell
$t = "dev"
curl.exe -H "Authorization: Bearer $t" http://127.0.0.1:7420/v1/health
curl.exe -H "Authorization: Bearer $t" "http://127.0.0.1:7420/v1/snapshot?depth=1&components=false"
```

A command and its receipt:

```json
{
  "requestId": "light-1",
  "op": "setProperty",
  "worldId": "8077ab29-4917-46cf-85f7-d291f80e047a",
  "targetId": 42,
  "componentType": "GenericLightComponent",
  "property": "Energy",
  "value": { "kind": "float", "number": 4 }
}
```

```json
[
  {
    "requestId": "light-1",
    "op": "setProperty",
    "accepted": true,
    "appliedTick": 132,
    "previousValue": { "kind": "float", "number": 1 },
    "worldName": "Scene"
  }
]
```

Commands are idempotent by `requestId`: re-sending the same id returns the
original receipt instead of applying twice.

---

## Tools

### Reads

| Tool | Returns |
|---|---|
| `probe_status` | pid, port, read-only, world count, tick |
| `list_worlds` | world ids, names, authority, element counts |
| `list_modules` | modules, versions, start state, rejection reasons |
| `list_mods` | mod paks, versions, load state, rejection reasons |
| `get_tree` | indented tree, filterable by depth/name/component |
| `get_element` | one element, every component and property |
| `find_elements` | compact matches by name/component/active |
| `get_journal` | recent applied commands, with their payloads |
| `get_receipt` | result of one request id |

### Writes

Every write returns a receipt with `accepted`, `error`, `appliedTick`,
`previousValue` or `previousTransform`, and `createdId`.

| Tool | Notes |
|---|---|
| `set_transform` | omitted components keep their current value |
| `set_active` | show/hide; inactive elements are skipped by rendering |
| `set_property` | value inferred: bool, integer, float, `x,y,z`, else text |
| `spawn_element` | empty element, optional parent and position |
| `despawn_element` | removes the element and its subtree |
| `attach_component` | by type name |
| `detach_component` | by id or type name |
| `send_input` | injects through the real input service |
| `set_simulation` | pause and/or time scale |

`get_tree` returns text, not JSON, because a tree is what a model navigates:

```
world Scene  id=8077ab29-…  authority=Local  elements=3
  #1 Root
    #2 Lamp (1 comp)
    #3 Crate (2 comp) [inactive]
```

`get_element` returns full JSON when you need the property bag.

Snapshots always carry the local frame the renderer and physics use. When a
world has a non-zero floating origin, `get_element` also returns
`globalPosition` (double `Origin` plus local), and the world header carries
`origin`. Use `globalPosition` for gameplay coordinates and `local` for the
bubble the GPU and BEPU actually see.

---

## Recipes

**See what is actually in the scene**

> Use v12 get_tree with depth 3, then get_element on the two meshes you find.

**Tune a value without restarting**

> find_elements with component GenericLightComponent, then set_property Energy 4.5 on the one in the scene.

The receipt returns the previous value, so undo is another `set_property` with
that number.

**Reproduce an input bug**

> Pause the simulation, send_input buttonDown Jump, resume, then read get_journal.

`send_input` goes through `InputService`, so action maps, gameplay handlers and
script runtimes see it exactly like a real key. Tagged `Meta["source"] =
"introspection"` so handlers can tell it apart.

**Reproduce a session**

`get_journal` includes the command payloads. Re-post the entries in order with
fresh `requestId`s to replay the same edits. The deterministic fixed-step clock
makes input and edits repeatable.

**Spawn a marker, then clean up**

> spawn_element Marker at 0,2,0, then despawn_element it once you have checked the result.

---

## Security

- Loopback by default. `--mcp-bind 0.0.0.0` is an explicit decision, not a default.
- A bearer token is required on every route except `/v1/health`, including on
  loopback. A local port is reachable by every process on the machine.
- `--mcp-readonly` is enforced by the probe, not just hidden from the tool list.
- Writes to a `WorldAuthority.Remote` world are rejected by the engine.
- Tokens are per-process and regenerated each launch unless you pass one.
- `probe_status` and `/v1/health` expose no scene data, only liveness.

---

## Extending

**A new tool** is one annotated method in `V12.MCP/Tools/`:

```csharp
[McpServerTool(Name = "get_camera"), Description("Current camera transform.")]
public static async Task<string> GetCamera(CancellationToken cancellationToken = default)
    => Wire.Pretty(await ProbeClient.Current.GetAsync("v1/camera", cancellationToken));
```

`WithToolsFromAssembly` picks it up; no registration list to update.

**A new probe route** is a branch in `ProbeServer.RouteAsync`. Keep the handler
on `ProbeClient`/`SceneIntrospection` and off live components.

**A new command op** needs three edits: a value in `SceneOp`, a case in
`SceneCommandApplier.Apply`, and a tool in `WriteTools`. Commands must route
through `World`/`IWorldElement` entry points so `DirtyTracker` and replication
still see them.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| `--mcp was passed but ... does not exist` | probe DLL not in `modules/` (the older `plugins/` folder is still checked); use `--module <path>` |
| `Started module` never appears in the log | module was not loaded; check the launch arguments |
| `probe returned 401` | `V12_MCP_TOKEN` does not match the running process |
| `probe returned 504` | the game stopped ticking; it is hung or the window is blocked |
| `V12_MCP_TOKEN is required` | the client's environment is missing the token |
| `element … was not found` | wrong id or wrong world; re-read with `get_tree` |
| edit accepted but nothing changes | element inactive, or the component reads the value elsewhere; check `get_element` |

---

## Limits

- No screenshots or renderer readback.
- No raycast route yet (`SceneQuery` is available, the route is not written).
- `spawn_element` creates empty elements; templates come later.
- No automated replay tool; the journal makes one trivial to build.
- The probe is not deployed by `vcsdk pack`. Copying it into `modules/` is manual
  on purpose, so a pak cannot opt a game into a debug port on its own.
- One probe per process. Several games can run at once, each with its own port
  and handshake file.

---

## File map

| Path | Contents |
|---|---|
| `V12-Engine/Core/Introspection/` | snapshot DTOs, command queue, applier, journal |
| `V12-Engine/Core/Modules/` | `IV12Module`, `ModuleHost` |
| `V12.MCP.Probe/` | HTTP probe, options, module entry point |
| `V12.MCP/` | stdio MCP server, probe client, tools |
| `V12.Architecture.Tests/SceneIntrospectionTests.cs` | concurrency, ordering, paused drain, authority rules |
