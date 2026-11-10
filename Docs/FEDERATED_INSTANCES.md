# Federated Instances, World Topology, and Worker Processes

> **Status:** design proposal.
> **Last updated:** 2026-11-10 (roadmap resequenced; trust model, revocation,
> and connectivity decisions tightened).
>
> This document captures the intended shape of VNode, V12 headless, federated
> worlds, region ownership, session handoff, and worker processes. It is not a
> commitment to a single release.

## Contents

1. [Summary](#1-summary)
2. [Design principles](#2-design-principles)
3. [Terms](#3-terms)
4. [Modes and optionality](#4-modes-and-optionality)
5. [Planes and components](#5-planes-and-components)
6. [World topology](#6-world-topology)
7. [Authority and synchronization](#7-authority-and-synchronization)
8. [Session resolution and handoff](#8-session-resolution-and-handoff)
9. [Gamepacks, assets, and config](#9-gamepacks-assets-and-config)
10. [Worker processes and multi-process runtime](#10-worker-processes-and-multi-process-runtime)
11. [Identity and authentication](#11-identity-and-authentication)
12. [Federation](#12-federation)
13. [Kubernetes and scale](#13-kubernetes-and-scale)
14. [Current code mapping](#14-current-code-mapping)
15. [Roadmap](#15-roadmap)
16. [Risks and open questions](#16-risks-and-open-questions)
17. [Decisions to lock before implementation](#17-decisions-to-lock-before-implementation)
18. [References](#18-references)

## 1. Summary

V12 is split into two planes:

- **VNode** is the control plane. It handles accounts, identity, keys, gamepack
  and asset storage, world manifests, permissions, discovery, host leases,
  session tickets, moderation, the web frontend, and optional federation.
- **V12 headless** is the data plane. It runs one authoritative simulation for a
  world region and speaks the real-time game protocol to players.

A standalone headless server must work with no VNode at all. A VNode-backed home
server adds accounts, remote gamepacks, and discovery. A federated deployment
adds multiple VNodes and multiple V12 headless hosts arranged into a world grid.

The core architectural rule is:

> **VNode and federation are optional layers. The V12 headless runtime must be
> able to boot, load a local gamepack from JSON config, and host a world on its
> own.**

## 2. Design principles

1. **Control plane and data plane are separate.** VNode is not in the frame
   loop. V12 headless does not query VNode for every tick.
2. **One authority per region.** A world is divided into regions. Each region
   has exactly one authoritative host at a time, with a fencing epoch.
3. **Local-first, remote when needed.** Local gamepacks and direct LAN play work
   without accounts or federation.
4. **Content is addressed and signed.** Gamepacks, worlds, and assets have
   hashes and signatures. Trust is explicit.
5. **Processes are the isolation boundary.** Untrusted gamepacks, heavy
   simulations, and per-user isolation can run in child processes rather than
   sharing one runtime.
6. **No Matrix-style live state replication by default.** Live game state is
   host-authoritative and snapshot/delta based. Matrix-style shared documents
   are a separate authoring layer.
7. **No hard dependency on VNode.** VNode integration is through interfaces and
   optional services, not direct calls scattered through the engine.

## 3. Terms

| Term | Meaning |
|---|---|
| **Node** | A VNode instance with a domain and signing keys. |
| **VNode** | Account, asset, gamepack, world, permission, and federation service. |
| **World** | A logical place players enter. It has a manifest and a region grid. |
| **Region** | A bounded part of a world with one authoritative host. |
| **Chunk** | A content or loading unit inside a region. Not necessarily a separate host. |
| **Host** | A V12 headless instance that simulates one or more regions. |
| **Worker** | A local child process or in-process module that runs a specific loop. |
| **Session** | The live lifetime of a world or region host, identified by an epoch. |
| **Lease** | A time-bounded grant that says which host owns a region. |
| **Epoch** | A monotonically increasing fencing token for authority. |
| **Gamepack** | A `.v12pak` or assembly that supplies components, worlds, and scripts. |
| **Ticket** | A signed token that lets an account join a world or region. |

## 4. Modes and optionality

Deployment should be described by two independent settings:

- **VNode:** `off`, `login`, `assets`, `full`
- **Federation:** `off`, `direct`, `vnode`

That produces four useful combinations:

| VNode | Federation | Mode | Behavior |
|---|---|---|---|
| off | off | Standalone | Local gamepack, local config, direct network, no accounts. |
| on | off | Home server | Accounts, gamepacks, and worlds from VNode, single node. |
| off | direct | Direct federation | Nodes exchange signed manifests and keys without a central VNode. |
| on | vnode | Federated network | Mastodon-style accounts, content, permissions, and discovery across VNodes. |

Separately from deployment mode, there is the **worker host** runtime choice: the
main headless process composes local worker processes for regions, sessions, or
custom loops.

## 5. Planes and components

```text
CLIENT
  |  login, world browser, join ticket
  v
VNODE (control plane)
  |  accounts, keys, gamepacks, world manifests,
  |  permissions, host leases, session tickets,
  |  discovery, moderation, federation
  v
WORLD DIRECTORY / SESSION BROKER
  |  etcd, Kubernetes leases, Postgres, object storage
  v
V12 HEADLESS HOSTS (data plane)
  |  one authoritative simulation per region
  |  optional local worker processes
  v
CLIENTS / GATEWAY
```

A client does not need to talk to VNode after login. It receives a signed ticket
and a host descriptor, then connects to the host. The host verifies the ticket
offline with VNode's public key. Revocation is the exception to "no VNode
calls": hosts also consume a small revocation feed (see 11.5).

## 6. World topology

### 6.1 Worlds, regions, and chunks

A **world** is the logical place. A **region** is the authority unit. A **chunk**
is a content or loading unit.

Do not hardcode 16x16 as a server boundary. It is a useful example, but region
size should be configurable. A host can own one large region or many small
chunks inside a region. The important boundary is ownership, not grid size.

A world manifest should include:

```text
WorldManifest {
  worldId
  gamepackId
  gamepackHash
  seed
  rules
  regionGrid
  regions[]
  permissions
  federationPolicy
}
```

A region descriptor should include:

```text
RegionDescriptor {
  worldId
  regionId
  bounds
  hostNodeId
  hostEndpoint
  authorityEpoch
  leaseExpiresAt
}
```

### 6.2 Region ownership

Each region has exactly one authoritative host at a time. The host:

- loads the world's gamepack,
- owns entities whose authoritative position is inside the region,
- simulates its region,
- sends state to clients interested in the region,
- reports health and lease renewal to the session broker.

Entities near a boundary need special handling. The simplest model is:

- The source region owns the entity until it crosses the boundary.
- The source freezes it and requests a transfer.
- The target accepts, assigns a new authority epoch, and confirms.
- The source removes its copy or turns it into a proxy.
- Clients update their authority mapping.

This is the Second Life region-crossing model. It is also the same mechanism
used for host failure, just triggered by a boundary instead of a crash.

### 6.3 Interest management

A client should not receive every entity in every region. It subscribes to:

- the region containing its player,
- adjacent regions within view distance,
- explicitly tracked entities such as party members or vehicles.

The default is **gateway-first**. A gateway is simpler for the client and for NAT
traversal, and it hides region topology from clients. Direct multi-host
connections are a later optimization, not a competing design.

Known weak spot: this section does not yet solve entities that straddle a
boundary, or physics interactions across regions. Treat the first version as a
likely rewrite once real gameplay hits borders, not as a settled design.

### 6.4 Global identity

Current element and component ids are process-local static counters. That is
fine inside one host, but not across a region grid.

The federation boundary needs a global identity. Options:

- a GUID per entity and component, using the existing `Uid` field as a seed,
- `(worldId, regionId, localId)` tuples,
- a 128-bit id assigned by the world directory.

Global ids are stamped on the wire early (roadmap Phase 2), even in standalone
mode, so they are never retrofitted across a live region grid.

The local `long` id can remain as a fast index inside the owning host. Network
DTOs and cross-host references should use the global id. `GameRoot.FindElement`
must become region-scoped or world-scoped; it must not return the first match
across every active world.

## 7. Authority and synchronization

### 7.1 Authority model

Default authority is the host. The host owns world simulation and validates
client input. Client authority is opt-in per entity or field.

Suggested authority values:

| Authority | Use |
|---|---|
| `Host` | World simulation, NPCs, physics, region state. |
| `Owner` | Player-owned objects, own avatar intent. |
| `Client` | Explicitly client-authoritative state. |
| `Shared` | Negotiated or delegated authority, later. |

Every authority assignment carries an epoch. A host with an older epoch is
fenced out. This applies to host handoff, region transfer, and reconnect.

### 7.2 Sync paths

The existing paths remain useful per region:

- `DirtyTracker` for component and element state.
- `WorldSync` for initial snapshots.
- `WorldDelta` for element create/delete.
- `SyncValue<T>` for hot or bulky values.
- `RpcDispatcher` for imperative calls.

They need realm and epoch awareness before they work across hosts:

- `worldId` and `regionId` on the wire,
- `authorityEpoch` on state messages,
- per-host or per-region `DirtyTracker` instances,
- per-peer interest filters instead of one global broadcast,
- `SyncValue` keys namespaced by world, region, entity, component, and field.

`SyncValue` is not a substitute for topology. It is a useful per-region hot-value
channel, but it still needs authority, epoch, and routing metadata.

### 7.3 Structural changes

Runtime state can be snapshot/delta based. Structural changes are different:

- create element,
- delete element,
- add component,
- remove component,
- reparent,
- rename,
- change reference.

Those should be a reliable, ordered operation log with a host-assigned sequence
number. This gives gap detection and resync without using OT for runtime state.
OT or a CRDT belongs in the global document and authoring layer, not in live
entity simulation.

## 8. Session resolution and handoff

### 8.1 Leases

A host owns a region through a lease:

```text
HostLease {
  worldId
  regionId
  hostNodeId
  authorityEpoch
  expiresAt
  signature
}
```

The host renews the lease with a heartbeat. If the lease expires, the region is
eligible for reassignment.

Use a consistent lease store behind an `ILeaseStore` interface. First
implementation: Postgres conditional writes (SQLite for a single node). etcd and
Kubernetes `Lease` objects are later adapters, not first-release dependencies.
Do not keep the only copy of ownership in one VNode process's memory.

The freeze/snapshot/resume protocol in 8.2 must work with a local supervisor as
the trigger before any lease store is involved (roadmap Phases 3-4).

### 8.2 Failure flow

When a host dies:

1. The lease expires or the broker detects the failure.
2. Clients receive `SessionFrozen` with the current epoch. Input stops,
   rendering continues.
3. A successor is chosen from a preferred list, or VNode asks Kubernetes to
   start a replacement.
4. The region is snapshotted if the old host is reachable. If not, clients and
   neighbors reconstruct from the last known snapshot and deltas.
5. The new host loads the gamepack, restores state, and receives a new lease
   with a higher epoch.
6. Clients receive `SessionResumed` with a new endpoint, ticket, and epoch.
7. Old-epoch messages are rejected.

For a region grid, only the affected region freezes. Adjacent regions can keep
running and buffer border traffic.

### 8.3 Successor options

Two valid strategies:

- **Appoint a successor.** A player or another headless host is promoted. The
  engine freezes the session, transfers state, and resumes.
- **Spawn a replacement.** VNode or the Kubernetes operator starts a new
  headless instance, loads the gamepack, and resumes the session.

Both use the same lease, snapshot, epoch, and resume protocol. The difference is
only who becomes the new host.

## 9. Gamepacks, assets, and config

### 9.1 Configuration

Use JSON config, not JavaScript. Remote config should never be executable code.
A local `config.json` can be passed with `--config`.

Standalone example:

```json
{
  "mode": "standalone",
  "identity": { "provider": "none" },
  "gamepack": {
    "source": "local",
    "path": "./game.v12pak",
    "world": "myworld"
  },
  "network": {
    "listen": 7777,
    "federation": "off"
  },
  "workers": {
    "enabled": false,
    "maxProcesses": 4,
    "isolation": "region"
  }
}
```

VNode-backed home server example:

```json
{
  "mode": "home",
  "identity": {
    "provider": "vnode",
    "node": "https://node.example"
  },
  "gamepack": {
    "source": "vnode",
    "world": "myworld",
    "region": "auto"
  },
  "network": {
    "listen": 7777,
    "federation": "full"
  },
  "workers": {
    "enabled": true,
    "maxProcesses": 16,
    "isolation": "region"
  }
}
```

### 9.2 Gamepack sources

Add a gamepack source abstraction:

```text
IGamepackSource
  LocalGamepackSource(path)
  VNodeGamepackSource(node, worldId, token)
  DirectHttpGamepackSource(url, hash)
```

The loader verifies the manifest hash and signature before loading. A gamepack
can depend on other gamepacks, so the manifest needs dependency hashes as well.

**Trust rule.** Signatures and hashes prove integrity and origin, not safety. A
gamepack can contain C# assemblies, so loading one is arbitrary code execution.
Therefore:

- Local packs the operator installed may run in `InProcessHost`.
- Remote, federated, or otherwise untrusted packs run **only** in
  `LocalWorkerHost` (or a remote host the operator does not share a process
  with), with mandatory resource limits and a mounted asset view.
- A pack is never promoted to `InProcessHost` because it is signed. Promotion is
  an explicit operator decision per pack hash.

Large assets should not be proxied through VNode's main process. Use S3 or
another object store with signed URLs or a CDN. VNode handles authorization,
manifests, and small metadata.

### 9.3 `v12://` and `vnode://`

Keep two namespaces:

- `v12://` is the engine content namespace. It resolves through the active
  gamepack or world mount.
- `vnode://` is a federated origin. It names a node, content id, or world.

In standalone mode, `v12://` resolves locally. In VNode-backed mode, the gamepack
source can mount remote content under `v12://` paths after verification. Do not
put network endpoints into `v12://`. The trust boundary is the mount step:
content crosses from `vnode://` to `v12://` only after hash and signature
verification, and a `v12://` path never triggers a network fetch by itself.

A useful rule:

```text
v12://<world-or-pak>/<asset>
vnode://<node>/<group>/<asset>
```

The first is what components and worlds use. The second is what the asset
resolver and federation layer use to obtain and verify content.

## 10. Worker processes and multi-process runtime

### 10.1 Why workers

A single process is simple but has limits:

- one crash takes down every world in the process,
- one untrusted gamepack can access everything,
- one heavy simulation can starve others,
- per-user isolation is impossible.

Workers provide isolation and composition:

- **Region worker:** owns one region or a group of chunks.
- **Session worker:** owns one player session or a small group.
- **Loop worker:** runs a custom simulation or script loop.
- **Content worker:** loads and validates a gamepack, then exits or stays warm.

### 10.2 One host interface

The engine should expose one host abstraction with three implementations:

| Implementation | Transport | Use |
|---|---|---|
| `InProcessHost` | direct calls | Small modules, trusted code, low overhead. |
| `LocalWorkerHost` | local socket or pipe | Child process, crash isolation, untrusted packs. |
| `RemoteRegionHost` | network | Another machine, federated region. |

This lets the main runtime treat a local worker and a remote headless region the
same way. Moving from one process to many processes to many machines becomes a
transport change, not an API rewrite.

### 10.3 Worker manifest

A worker starts from a JSON manifest:

```json
{
  "id": "region-0-0",
  "kind": "region",
  "executable": "V12.Headless",
  "args": ["--config", "region-0-0.json"],
  "gamepack": "vnode://node.example/gamepacks/eco@sha256:...",
  "world": "eco",
  "region": "0,0",
  "limits": {
    "memoryMb": 2048,
    "cpu": 2
  },
  "restart": "on-failure"
}
```

The supervisor validates the manifest, starts the process, waits for a ready
message, monitors health, and restarts or replaces it on failure.

### 10.4 Worker protocol

Use a small local protocol, not the game's broadcast bus:

- length-prefixed JSON for control messages,
- binary frames for state and snapshots,
- explicit request ids,
- explicit ready, healthy, frozen, snapshot, resume, and shutdown states.

Use named pipes or Unix domain sockets for long-lived workers. Use stdio for
one-shot tools and simple workers. Shared memory is an option for very large
snapshots, but the control protocol should stay small and versioned.

Control messages:

```text
spawn, ready, health, load-gamepack, load-world,
subscribe, unsubscribe, snapshot, delta,
freeze, resume, handoff, shutdown
```

Do not reuse `NetworkCables` broadcast semantics for worker IPC. Workers are
addressed, not broadcast.

### 10.5 Per-user processes

One process per user is possible, but it should not be the default for a large
server. Use it when:

- a user runs untrusted scripts,
- a user needs strong memory or crash isolation,
- a user has a heavy custom simulation,
- the deployment is small and simplicity matters more than density.

Default to a worker pool per region or per shard. Add per-user isolation as an
opt-in policy. Threads are cheaper than processes but do not give crash or
security isolation.

### 10.6 Composition and self-modifying images

The runtime can compose workers dynamically, but keep the composition explicit
and signed:

- a worker manifest declares what to run,
- the supervisor verifies the binary and gamepack hashes,
- workers are versioned and can be rolled back,
- a worker gets a mounted asset view, not raw host filesystem access,
- resource limits and health checks are mandatory,
- failed workers are replaced, not left half-alive.

The engine provides the ability. The gamepack or deployment policy decides the
composition.

## 11. Identity and authentication

### 11.1 Canonical identity

Use a Mastodon-style canonical id:

```text
username@domain
```

The domain is the account's home VNode. This is stable across servers and works
without exposing an email address.

Email is optional. It can be used for recovery and notifications later, but it
should not be the primary identity and should not be required for federation.

### 11.2 Credentials

Support multiple credential types, all mapped to the same account:

- password or local login,
- email magic link or verification code,
- OIDC or OAuth provider,
- WebAuthn or passkey,
- SSH public key,
- node key for server-to-server federation,
- administrator grant with no key required.

The SSH key is a credential, not a role. A user can add a key and still be a
normal player. An administrator can grant a role to an account that has no key.

### 11.3 Roles and permissions

Keep roles separate from credentials:

| Role | Capability |
|---|---|
| Player | Join worlds, use normal actions. |
| Builder | Edit or publish worlds and assets. |
| Moderator | Manage players and content. |
| Admin | Manage the node, roles, keys, and federation. |

Content permissions stay semantic and path-based, using the existing
`look/speak/run` model. Federation adds signed capability claims so a remote
node can prove that an account may join a world or edit an asset.

### 11.4 Signatures

Use signatures for:

- node discovery documents,
- account claims,
- gamepack manifests and content hashes,
- world manifests,
- session tickets,
- host leases,
- permission grants,
- structural operations,
- federation requests.

Ed25519 is a good default for node and account keys. HTTP signatures or signed
JSON envelopes are sufficient for the control plane. Replay protection needs
nonces, timestamps, and request ids.

### 11.5 Revocation

Support key rotation and revocation:

- a user can add and remove credentials,
- an admin can revoke a compromised key,
- a node can revoke a peer's trust,
- tickets and leases have short expiries,
- epoch changes invalidate old authority.

Short expiries alone do not remove a banned player from a long-lived session.
Bans are keyed on the canonical identity (`username@domain`), not on individual
tickets:

- **Domain ban:** `ban domain example.net` blocks every account on that domain.
  Because a domain's accounts are vouched for by its node key, this is the same
  thing as distrusting that node.
- **Account ban:** `ban account griefer@example.net` blocks one user and leaves
  the rest of their domain alone.
- **Scope:** a ban is node-wide or world-scoped. A world owner can ban someone
  from their world without that person being banned from the whole node.

Enforcement happens in two places:

1. **At ticket issuance.** VNode refuses to issue tickets and claims for banned
   domains and accounts. This is the main control.
2. **On live sessions.** Hosts consume a lightweight ban feed from VNode, check
   it on join and whenever it updates, and disconnect matching sessions.
   Offline ticket verification stays the fast path; the feed is the kill switch.

If the feed is unreachable, hosts keep serving existing sessions with the last
known list, and refuse new joins after a configurable maximum staleness.

Key revocation is kept for a different job: a compromised key is a security
event, not a moderation decision, so it still needs its own revocation entry
(by key id) in the same feed.

For nodes that don't want to chase bans, an **allowlist mode** (only listed
domains may join) is the stricter alternative. It also covers a banned domain
being re-registered under a new name.

## 12. Federation

### 12.1 Mastodon-style control plane

VNode-to-VNode federation should follow the Mastodon model more than the Matrix
model:

- independent nodes with their own accounts,
- `username@domain` identity,
- signed server-to-server requests,
- content manifests and hashes,
- local caching of remote content,
- blocklists, allowlists, moderation, defederation.

What federates:

- accounts and profiles,
- public keys,
- world manifests and host descriptors,
- gamepack manifests and hashes,
- permission claims,
- presence and join tickets,
- moderation decisions.

What does not federate per frame:

- entity transforms,
- component state,
- player input,
- physics,
- RPC streams.

Those stay on the data plane between the client and the region host.

### 12.2 Direct federation without VNode

A smaller direct mode is possible:

- nodes exchange public keys out of band,
- nodes exchange signed world and gamepack manifests,
- tickets are signed by the host node,
- no central account directory, no asset hosting.

This mode is useful for LANs, tournaments, and trusted clusters. It is not a
replacement for a full account federation.

### 12.3 Global documents

The global document and authoring layer is separate from live world state. If
multiple people edit the same world document at once, use OT or a CRDT there.
The current WorldML documents are a natural input to this layer. Do not put OT
on runtime entity state. The runtime host remains authoritative for live
simulation.

## 13. Kubernetes and scale

For thousands of machines:

- VNode should be a stateless API in front of Postgres or etcd plus S3-compatible
  object storage.
- Region ownership belongs in a consistent lease store, not in one VNode's
  memory.
- A Kubernetes operator or agent starts, stops, and replaces headless hosts.
- Large assets go through object storage or a CDN, not through VNode's process.
- Clients connect through a gateway or regional edge, not directly to every
  region in the world.
- Metrics, logs, and traces are per region and per host.
- VNode can be replicated. One logical VNode does not mean one process.

The VNode web frontend can stay a normal ASP.NET application. The federation and
session APIs should be ordinary HTTPS/JSON with signatures, not the live game
transport.

## 14. Current code mapping

| Area | Current state | Needed for this design |
|---|---|---|
| Config | `CLIArgs` only has `Headless`. | JSON config, mode, gamepack source, network, workers. |
| Gamepacks | Local directory scan and local `.v12pak` load. | Local and VNode sources, signed manifests, hash checks, cache. |
| Worlds | One selected world, plus persistent world. | World manifest, region grid, region-scoped active worlds. |
| Networking | One host or one client, broadcast to peers. | Region host links, interest filters, per-peer queues. |
| IDs | World ids are on the wire; element and component ids are still process-local. | Global entity and component identity at federation boundaries. |
| Authority | One `DirtyTracker.IsAuthority` bool. | Per-region and per-entity authority with epochs. |
| Session | Reconnect only. | Lease, freeze, snapshot, handoff, resume, fencing. |
| Sync | `DirtyTracker`, `WorldSync`, `WorldDelta`, `SyncValue`. | Realm/region/epoch metadata, per-host instances. |
| Identity | No account or node auth in V12 or VNode source. | Accounts, node keys, tickets, roles, optional SSH keys. |
| Federation | None. | VNode discovery, signed manifests, accounts, moderation. |
| Workers | In-process `IGameService` and components. | Worker supervisor, local IPC, child processes, limits. |
| VNode | Storage/REST service; auth and federation are docs-only. | Accounts, keys, gamepacks, world directory, leases, federation. |

### 14.1 What stays the same

- `NetworkHost` and `NetworkClient` remain the real-time transport.
- `DirtyTracker` and `SyncValue` remain the per-region state paths.
- `V12PakLoader` remains the local gamepack loader, with remote sources added
  around it.
- `GamepackLoader` remains the in-process gamepack discovery and lifecycle
  mechanism.
- WorldML remains the authored world format.
- `GameRoot` remains the runtime host, with new services registered around it.

### 14.2 Implemented so far

- `World.Id` is stable, and `WorldId` is carried by `WorldSyncDTO`,
  `ElementCreateDTO`, `ElementUpdateDTO`, `ComponentSnapshot`, and
  `ComponentRemovalDTO`.
- `World.FindElementById` supports world-scoped lookups, and
  `WorldSyncHandler` prefers the wire `WorldId` before falling back to the
  legacy name/global scan.
- `ComponentBase.SetSync` is the dirty-marking entry point for typed field
  sync. `VelocityComponent`, `TagComponent`, `CameraComponent`, and
  `HealthComponent` use it.
- `TransformComponent` is gone. Elements own their pose through
  `IWorldElement.LocalTransform`; `v12` composes it with `ScaleComponent`.
  Legacy WorldML `<TransformComponent>` nodes are read as pose data and are
  never attached as components.
- `SyncValue<T>` and delegate members are auto-ignored by the BSON component
  path through a serialization convention, so components no longer need
  `[BsonIgnore]` on every sync-value or handler property.
- `CameraComponent` now lives in `V12-Engine/Components`, not `V12.Basic`.
- Simulation now runs on a fixed-step clock with pause, time scale, tick/time
  counters, interpolation alpha, and a seeded deterministic random source. See
  `V12-Engine/Docs/SIMULATION_TIMING.md`.

## 15. Roadmap

Sequencing rule: build what pays off even if federation never ships. The
worker supervisor and global ids are useful on a single machine today, and they
exercise the freeze/snapshot/resume protocol locally before any lease store or
VNode exists. Host migration is the riskiest piece, so it is de-risked early
against a local transport, not first attempted over a network.

### Phase 0: lock decisions (short, written, no code)

- Lock identity formats: `WorldId`, `RegionId`, global `EntityId`, `AccountId`,
  `SessionId`, `AuthorityEpoch`.
- Global entity identity: pick GUID or `(worldId, regionId, localId)` and move on.
- Client connectivity: **gateway-first** (see decision 4 in section 17).
- Region size is configurable. A host may own multiple regions.

### Phase 1: config and local content

- Add JSON config and `--config`, with schema validation.
- Add `IGamepackSource` with the local implementation only.
- Add manifest hashes and verification for local packs.
- Exit criterion: standalone headless boots from config with no VNode.

### Phase 2: global ids and region-scoped lookups

- Stamp global ids on network DTOs now, even in standalone mode. The local `long`
  stays as a fast index inside the host.
- Make `GameRoot.FindElement` world- or region-scoped.
- Add `worldId`, `regionId`, and `authorityEpoch` fields to the wire format
  (single region, epoch fixed at 1 for now).
- Exit criterion: nothing in the engine assumes process-wide unique ids.

### Phase 3: worker supervisor (local-first)

- Add the unified host interface: `InProcessHost` and `LocalWorkerHost`.
- Add child-process workers, versioned IPC, worker manifests, limits, and
  restart policy.
- Trust rule enforced here: untrusted or remote packs run only in
  `LocalWorkerHost` with limits, never `InProcessHost` (see 9.2).
- Exit criterion: a region worker can crash and be replaced without taking down
  the main process.

### Phase 4: freeze, snapshot, resume, and epoch fencing (local)

- Implement `SessionFrozen` / `SessionResumed`, snapshot transfer, and the
  ordered structural-op log with host-assigned sequence numbers.
- Enforce epoch fencing: old-epoch messages are rejected.
- Drive it entirely from the local supervisor: kill a worker, snapshot, spawn a
  successor with a higher epoch, resume clients.
- Exit criterion: worker handoff and crash recovery work with no network
  dependency, no lease store, and no VNode.

### Phase 5: identity, tickets, and remote content

- Implement VNode accounts, node keys, and discovery.
- Add `VNodeGamepackSource`, signed manifests, and the trust model from 9.2.
- Issue signed session tickets, verified offline by hosts.
- Add the ban feed (11.5): domain and account bans enforced at ticket issuance and on live sessions.

### Phase 6: single-world host lease

- Add a world directory and host lease behind a lease-store interface.
  First implementation: Postgres conditional writes (SQLite for single node).
  etcd and Kubernetes `Lease` come later behind the same interface.
- One world, one host, no grid yet.
- Reuse the Phase 4 freeze/resume protocol with the lease as the trigger.
- Add `RemoteRegionHost` as the third host implementation.

### Phase 7: region grid

- Add world manifests with region grids, region hosts, and border crossing.
- Add the gateway for clients and per-peer interest filters.
- Treat boundary-straddling entities and cross-region physics as the known hard
  part (see 6.3). Expect one rewrite of interest management after real play.

### Phase 8: federation

- Add VNode-to-VNode discovery, signed requests, account lookup, and world
  manifests.
- Add blocklists, moderation, and defederation.
- Optional: direct federation without VNode.

### Phase 9: global document layer

- Add OT or CRDT document sync for collaborative world authoring.
- Keep it separate from live entity simulation.

## 16. Risks and open questions

- **Host migration is the hardest part.** Snapshot completeness, in-flight
  inputs, and client resume must be designed together.
- **Region boundaries are hard.** Cross-region entities, physics, and visibility
  need explicit ownership and transfer rules.
- **Global ids are hard to retrofit.** Stamped on the wire in Phase 2, before
  any region grid or federation work.
- **VNode can become a single point of failure.** Use a replicated control plane
  and a consistent lease store.
- **Federation security is nontrivial.** Key rotation, replay protection, signed
  manifests, and defederation are required.
- **Per-user processes can explode.** Default to worker pools, not one process
  per user.
- **Worker IPC is a protocol.** Treat it as versioned and testable, not as an
  internal implementation detail.
- **Remote config must not be executable.** JSON only, with schema validation.
- **Cross-region gameplay needs a clear authority model.** Host default, client
  opt-in, epochs for every handoff.
- **Federation and VNode are optional.** The engine must not assume either one
  exists.
- **Signed does not mean safe.** Remote gamepacks execute code. They run only
  in isolated workers (9.2).
- **Interest management across borders is unsolved.** Expect a rewrite after
  real gameplay (6.3).
- **Scope.** The full design is large. The roadmap is sequenced so each phase
  is useful alone and federation can be cut without wasting earlier work.

## 17. Decisions to lock before implementation

1. World, region, entity, account, session, and epoch identity formats.
2. Region size and ownership granularity.
3. Host authority versus client authority defaults.
4. ~~Client connectivity~~ **Locked: gateway-first.** Direct multi-host is a
   later optimization.
5. Worker isolation policy and default process model.
6. Gamepack signature format. (Trust rule is locked: untrusted packs run only
   in worker processes, see 9.2.)
7. ~~Lease store~~ **Locked for v1: Postgres conditional writes behind
   `ILeaseStore`.** Orchestration adapter (Kubernetes) still open.
8. Federation protocol and minimum feature set.
9. Global document format and editing model.
10. The exact boundary between VNode and V12 headless responsibilities.

## 18. References

- `V12-Engine/Core/GamePak/GamepackLoader.cs`
- `V12-Engine/Core/GamePak/IV12Gamepack.cs`
- `V12.Pak/V12PakLoader.cs`
- `V12.Pak/V12PakManifest.cs`
- `V12-Engine/Core/CommandLine/CLIArgs.cs`
- `V12-Engine/Core/WorldLoader.cs`
- `V12-Engine/Core/World.cs`
- `V12-Engine/Core/GameRoot.cs`
- `V12-Engine/Core/Networking/DirtyTracker.cs`
- `V12-Engine/Core/Networking/SyncValue.cs`
- `V12-Engine/Core/Networking/WorldSyncDTO.cs`
- `V12-Engine/Core/NetworkCable/NetworkHost.cs`
- `V12-Engine/Core/NetworkCable/NetworkClient.cs`
- `V12.Basic/Systems/WorldSyncHandler.cs`
- `V12.Basic/Systems/NetworkHandler.cs`
- `V12.Basic/Systems/RemotePlayerManager.cs`
- `VNode/src/Networking/URIHandler.cs`
- `VNode/src/Interfaces/perms.cs`
- `VNode/docs/VSystem_Auth.md`
- `VNode/docs/VNode_Permissions.md`