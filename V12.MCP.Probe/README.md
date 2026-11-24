# V12.MCP.Probe

In-game half of V12 MCP. A loadable module that serves the engine's
`SceneIntrospection` surface over loopback HTTP so the MCP server (or `curl`)
can read and edit a running game.

It is deliberately not referenced by the engine, NovaV2 or any game. A host
loads it from a modules folder only when asked, so a build that never passes
`--mcp` carries no probe code.

> Full guide: [`Docs/MCP.md`](../../Docs/MCP.md)

## Load it

```powershell
dotnet build .
Copy-Item bin/Debug/net10.0/V12.MCP.Probe.dll <game>/modules/
NovaV2 --xr-mode off --mcp -- --mcp-port 7420 --mcp-token dev
```

Switches: `--mcp`, `--mcp-port`, `--mcp-bind`, `--mcp-token`,
`--mcp-readonly`, `--mcp-snapshot-timeout-ms`, `--mcp-command-timeout-ms`.

The URL and token are written to `%TEMP%\v12-mcp\probe-<pid>.json` and printed
to the log.

## Routes

`GET /v1/health` (no token), `/v1/worlds`, `/v1/modules`, `/v1/mods`,
`/v1/snapshot`, `/v1/element/{id}`, `/v1/journal`, `/v1/receipts/{id}`;
`POST /v1/commands`.

Reads are answered on the tick thread through
`SceneIntrospection.RequestSnapshotAsync`; writes go through the same command
queue the rest of the engine uses.
