# V12.MCP

Stdio MCP server that gives an MCP client live read and write access to a running
V12 game: inspect the scene tree, move things, change component values, attach
components, inject input.

> Full guide: [`Docs/MCP.md`](../../Docs/MCP.md)

This project does **not** reference `V12-Engine`. It speaks HTTP to
[`V12.MCP.Probe`](../V12.MCP.Probe), a module the game loads only when launched
with `--mcp`. The engine stays free of transport and tooling code.

## Run

```powershell
dotnet build ..\V12.MCP.Probe
dotnet build .

# copy the probe next to the game
New-Item -ItemType Directory -Force <game>/modules
Copy-Item ..\V12.MCP.Probe\bin\Debug\net10.0\V12.MCP.Probe.dll <game>/modules/

NovaV2 --xr-mode off --mcp -- --mcp-port 7420 --mcp-token dev
```

Then point an MCP host at this project's DLL:

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
          "V12_MCP_TOKEN": "dev"
        }
      }
    }
  }
}
```

| Variable | Default |
|---|---|
| `V12_MCP_URL` | `http://127.0.0.1:7420` |
| `V12_MCP_TOKEN` | required |

Without `--mcp-token`, the generated token is in
`%TEMP%\v12-mcp\probe-<pid>.json`.

## Tools

Reads: `probe_status`, `list_worlds`, `list_modules`, `list_mods`, `get_tree`,
`get_element`, `find_elements`, `get_journal`, `get_receipt`.

Writes: `set_transform`, `set_active`, `set_property`, `spawn_element`,
`despawn_element`, `attach_component`, `detach_component`, `send_input`,
`set_simulation`.

Every write goes through the game's command queue and is applied on its own
thread at the next tick. See [`Docs/MCP.md`](../../Docs/MCP.md) for the tool
reference, recipes, security and troubleshooting.
