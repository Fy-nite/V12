# Peer-Hosted Worlds and Per-Entity Authority

> **Status:** implementation plan and design decision.
> **Last updated:** 2026-11-25.
>
> This narrows the authority and handoff parts of
> [`FEDERATED_INSTANCES.md`](FEDERATED_INSTANCES.md) into concrete engine
> changes. It does not replace that document's control-plane, identity, worker,
> or federation plan.

## 1. Purpose

V12 should support a social sandbox where any peer can host a world or region:

- a player's desktop or headless client,
- a friend's machine,
- a home VNode,
- a compute node or Kubernetes worker,
- a dedicated host.

Hosting must be transferable without losing the session. Federation handles
accounts, discovery, tickets, manifests, leases, and moderation. The live game
still needs one authority per region.

The goal is **peer-hosted worlds**, not a full simulation mesh.

## 2. Decisions locked by this plan

1. Live entity state is authoritative per region. One writer per entity at a
   time.
2. Per-entity authority is explicit: `Host`, `Owner`, `Client`, or `Shared`.
3. Every authority assignment carries an epoch. Older epochs are rejected.
4. A client may own its avatar pose and, while held, a grabbed object. The host
   owns world simulation, physics props at rest, NPCs, and shared world state.
5. Clients connect through a host or gateway in v1. A full mesh is out of
   scope.
6. Federation is control plane only. Transforms, input, physics, and RPC do not
   federate per frame.
7. Mobile is client-first. Hosting from a phone is opt-in and limited.
8. Client mods are allowed. Shared-world mods must match the host's mod set.
   Untrusted mods run in worker processes.
9. Host handoff uses freeze, snapshot, resume, and epoch fencing. It does not
   replay from every peer.
10. Structural changes use a reliable, host-ordered operation log. Runtime
    state uses snapshot and delta.
11. Prediction is not authority. A predicting client still accepts host
    correction.
12. Host authority is for consistency, arbitration, and griefing control, not
    an anti-cheat arms race.

## 3. What this is not

- Not a full peer-to-peer mesh.
- Not per-frame state replication between VNodes.
- Not OT or CRDT on live entity state. Authoring documents are the separate
  layer from `FEDERATED_INSTANCES.md` section 12.3.
- Not a promise that clients cannot cheat. It is a promise that shared state
  has one answer.
- Not mobile-as-default-host.

## 4. Terms

| Term | Meaning |
|---|---|
| **Region** | A bounded part of a world with exactly one authoritative host. |
| **Host** | The peer or process simulating a region. |
| **Owner** | The player allowed to write a specific entity or field. |
| **Authority epoch** | Monotonic fencing token for an authority assignment. |
| **Lease** | Time-bounded grant that a host owns a region. |
| **Gateway** | Client-facing relay that routes to the current region host. |
| **Global entity id** | Identity stable across hosts and regions. |
| **Structural op** | Create, delete, add/remove component, reparent, rename, reference change. |
| **Handoff** | Freeze, snapshot, transfer, and resume a region under a higher epoch. |
| **Peer-hosted** | Any peer can be the authoritative host. |
| **Mesh** | Every peer simulates and syncs every entity with every other peer. |

## 5. Authority model

### 5.1 Kinds

```csharp
public enum EntityAuthorityKind
{
    Host,    // region host simulates and writes
    Owner,   // one player is allowed to write
    Client,  // explicitly client-authoritative, host validates policy
    Shared,  // future: negotiated or delegated, not in v1
}
```

Default is `Host`. A gamepack opts an entity into `Owner` or `Client`. `Shared`
should stay unimplemented until a concrete gameplay need exists.

### 5.2 Authority record

Authority is session and transport state, not authored content. Keep it in a
per-session registry rather than on `Element` itself. A gamepack can still
author defaults through a component that seeds the registry on attach.

```csharp
public readonly record struct EntityAuthority(
    Guid WorldId,
    Guid EntityUid,
    EntityAuthorityKind Kind,
    long OwnerPlayerId,
    long Epoch);

public interface IEntityAuthorityStore
{
    EntityAuthority Get(World world, Guid entityUid);
    bool TrySet(EntityAuthority next, long expectedEpoch);
    bool CanWrite(World world, Guid entityUid, long writerPlayerId, long epoch);
}
```

Wire identity uses `Guid EntityUid`. The local `long` id stays as a fast index
inside the owning host. This follows `FEDERATED_INSTANCES.md` section 6.4. Every
replicated element must have a non-empty `Uid` assigned at creation and never
reused; an element with `Guid.Empty` is local-only or must be upgraded before it
can replicate.

### 5.3 Default ownership policy

| Object | Authority | Notes |
|---|---|---|
| Local avatar body, XR | `Owner` (its player) | Client pose, host validates and relays. |
| Local avatar body, desktop | `Host`, with client prediction | Client sends input, host simulates movement and collisions. |
| Remote avatar visuals | `Host` view | Interpolated from owner state. |
| Held physics object | `Owner` while held | Transfers to `Host` on release. |
| Physics object at rest | `Host` | Host simulates and resolves contacts. |
| NPC, vehicle, world prop | `Host` | |
| Shared world edit | `Host` | Validated against permissions. |
| Authoring document | OT/CRDT layer | Not live entity state. |

Desktop avatars keep the current input-command model where the host simulates
movement and collisions. That is a `Host` entity with client prediction, not
owner authority. XR avatars use `Owner` because the headset is already the
source of truth. A game can choose one model or mix them per session.

### 5.4 Epoch rules

- Every assignment starts at epoch 1 and increments on any authority change.
- A state message carries `(entityUid, writerPlayerId, epoch)`.
- A host or owner accepts state only when the epoch matches the current
  assignment and the writer matches the authority kind.
- An old epoch is rejected, not applied and not rebroadcast.
- A host may force a new epoch to reclaim an entity from a stuck or malicious
  owner.
- Handoff assigns the new host a higher epoch before clients resume.

### 5.5 Conflict rules

- Position and rotation: one writer at a time. No blending between two writers.
- Held object: owner writes while held; release is an explicit transfer back to
  the host with a new epoch.
- World edits: host validates permission and appends a structural op.
- Two clients try to grab the same object: host grants one owner and rejects the
  other.
- A client-owned body leaves the world bounds or becomes invalid: host clamps,
  teleports, or reclaims with a higher epoch.
- A client disconnects while owning an entity: host reclaims the entity.

## 6. Wire protocol

### 6.1 Authority metadata on state

Every state message that can write an entity carries:

```text
worldId
regionId
entityUid
componentUid (when applicable)
authorityEpoch
writerPlayerId
sequence (per writer / per entity)
payload
```

This applies to element updates, component snapshots, and hot-value batches.
`SyncValue` keys become:

```text
world/{worldId}/region/{regionId}/entity/{entityUid}/component/{componentUid}/{field}
```

`SyncChannel` already scopes by world, element, and component. Region and epoch
are the additions.

### 6.2 New control messages

```text
AuthorityChanged {
  worldId, regionId, entityUid,
  kind, ownerPlayerId, previousEpoch, newEpoch,
  reason
}

SessionFrozen {
  worldId, regionId, epoch, reason, expectedResumeBy
}

SessionResumed {
  worldId, regionId, epoch,
  endpoint, ticket, snapshotRevision
}

StructuralOp {
  worldId, regionId, epoch, sequence,
  op, payload
}

StateRejected {
  worldId, regionId, entityUid, epoch, reason
}
```

`AuthorityChanged`, `SessionFrozen`, `SessionResumed`, and `StructuralOp` are
reliable and ordered. Continuous state stays unreliable.

### 6.3 Structural operation log

Runtime state is snapshot plus delta. Structural changes need stronger
ordering:

- host assigns a monotonic sequence,
- clients detect gaps,
- a gap triggers a snapshot or a delta-range request,
- operations carry the authority epoch,
- old-epoch operations are rejected,
- the log is part of the handoff snapshot.

Do not put OT or CRDT on this log. A single host orders it.

### 6.4 Interest management

A client should not receive every entity in every region. Subscribe to:

- the region containing the player,
- adjacent regions within view distance,
- tracked entities such as party members, vehicles, or held objects.

`NetworkHost` currently broadcasts. The per-region path needs per-peer filters
and targeted sends. Keep broadcast for small single-region sessions until the
filter exists.

### 6.5 Snapshot contents

A handoff snapshot must include:

- world tree and element identities,
- component state,
- authority map with epochs,
- last structural sequence,
- fixed-step clock state, RNG seed, and timers,
- pending reliable operations and RPC results that affect state,
- player slots and owned entities,
- gamepack and shared mod hashes,
- asset references by hash, never embedded asset bytes.

Assets are fetched from the object store, VNode cache, or the successor's local
cache. Do not push an entire user-generated world through the live socket when
the successor can read the same content-addressed store.

## 7. Host transfer

### 7.1 When handoff runs

- The host chooses to leave.
- The host hands off to a friend's headless client or a home server.
- A lease expires or a worker fails.
- An operator drains a compute node.
- A region crosses a boundary and needs a new owner.

### 7.2 Successful handoff

```text
1. Host and successor agree to transfer.
2. Directory reserves epoch N+1 for the successor.
3. Host sends SessionFrozen(epoch N) to clients.
4. Host captures a snapshot at a fixed tick boundary.
5. Successor receives the snapshot and asset references.
6. Successor loads the gamepack and mod set, verifies hashes.
7. Successor acquires the lease and starts at epoch N+1.
8. Clients receive SessionResumed(epoch N+1, endpoint, ticket).
9. Clients resubscribe and request delta from the snapshot revision.
10. Old host rejects all epoch N writes and shuts down.
```

Clients do not vote and do not merge state. The snapshot is the truth.

### 7.3 Failure handoff

If the old host is unreachable:

- the lease expires or the gateway detects the failure,
- clients receive `SessionFrozen`,
- the successor restores from the last known snapshot plus deltas,
- entities that were client-owned are reclaimed by the new host,
- clients resync and resume with the new epoch.

Some recent state can be lost. That is preferable to split brain. The host
should snapshot often enough that the window is acceptable, or maintain a
recent delta journal with the directory.

### 7.4 What must survive

- Player identity and avatar assignment.
- Held objects and their ownership.
- Physics state, or a recent snapshot from which it can be reconstructed.
- Timers, script state, and mod state.
- Structural operation sequence.
- Permission and moderation state.

Script and mod state are the hardest part. The first handoff implementation
should require mods to be able to serialize their state, or explicitly mark a
session as non-transferable.

### 7.5 Handoff limits

- Snapshot size and freeze time must be measured for real user-generated
  worlds, not only the demo.
- Large worlds need incremental snapshots, asset caching, and a pre-warmed
  successor.
- A mobile device is not a default successor. Allow it only for small private
  worlds, opt-in.
- A successor that cannot load the exact gamepack and shared mod set must
  refuse the transfer, not silently run a different world.

## 8. Peer-hosted regions and gateway

Use the three host abstractions from `FEDERATED_INSTANCES.md` section 10.2:

| Host | Use |
|---|---|
| `InProcessHost` | Trusted local packs and small sessions. |
| `LocalWorkerHost` | Crash isolation, untrusted packs, local handoff tests. |
| `RemoteRegionHost` | A friend, homeserver, or compute node. |

A player-hosted region and a compute-node region use the same protocol. The
only difference is the transport and who pays for the machine.

Gateway-first:

- clients receive a signed ticket and a gateway endpoint,
- the gateway routes to the current region host,
- clients do not need to know the region topology,
- direct host connections are a later latency optimization,
- mobile clients never accept inbound connections.

Do not build a mesh to avoid the gateway. A mesh multiplies NAT problems,
bandwidth, and conflict resolution.

## 9. Prediction and reconciliation

Prediction is a client-side presentation feature. It does not grant authority.

| Authority | Prediction |
|---|---|
| Own avatar, XR (`Owner`) | Client predicts pose, host reconciles. |
| Own avatar, desktop (`Host`) | Client predicts input locally, host simulates and corrects. |
| Held object (`Owner`) | Client predicts local hold; host validates and confirms. |
| Host prop | Client may play a cosmetic local response; host corrects. |
| Remote avatar | Interpolate; do not predict. |
| `Shared` | Not implemented in v1. |

Reconciliation rules:

- an ack carries the authority epoch, the writer sequence, and the
  authoritative state,
- a pending prediction is valid only for the current epoch,
- an authority change invalidates pending predictions and forces a resync,
- a host reclaim (higher epoch) is authoritative even if the owner disagrees.

The current `MultiplayerDemo` desktop predictor is the shape to generalize:
sequenced input, deterministic shared step, host ack, replay pending inputs.
XR stays owner-pose because the tracked rig is already the source of truth.

## 10. Mods and trust

Client modding is a product feature, so the trust model is not "clients are
hostile." It is "clients and hosts may run different code."

- Client-local cosmetic mods are allowed without host approval.
- Shared-world mods define simulation, physics, or world rules. The host
  advertises a mod-set hash. A client with a different shared set joins as a
  spectator or is rejected.
- Untrusted mods run only in `LocalWorkerHost`, with limits and a mounted asset
  view. Signed does not mean safe.
- The host still arbitrates shared state. This prevents griefing, object
  duplication, and world corruption; it is not an anti-cheat promise.
- Permissions decide who may edit, spawn, grab, or use tools. The host enforces
  them.
- Moderation stays in the control plane: signed tickets and the VNode ban feed.

## 11. Mapping to current code

| Area | Current state | Required change |
|---|---|---|
| Session authority | `WorldAuthority.Local/Hosted/Remote` plus one `DirtyTracker.IsAuthority` bool | Keep the session role; add per-entity authority store and route writes through it. |
| Epoch | `WorldSession.AuthorityEpoch` exists but is not on the wire or enforced | Stamp it on state and control messages; reject stale epochs. |
| Handoff | `WorldCloseMode.HandoffTo` only invokes a callback | Add a handoff service with snapshot, transfer, lease, and resume. |
| Region | No region id on sessions | Add `RegionId` to session, descriptors, and wire DTOs. |
| Global ids | `Element.Uid` exists; wire mostly uses local `long` ids | Use global entity ids at the federation and region boundary. |
| Dirty tracking | One `IsAuthority` bool and broadcast batches | Per-entity `CanWrite`, per-peer filters, epoch on batches. |
| Snapshots | `WorldSync` sends the world and components | Add authority map, structural sequence, epoch, mod hashes, asset refs. |
| Deltas | `WorldElementUpdate`, `WorldDelta`, component batches | Add authority metadata and reject unowned writers. |
| Structural changes | Captured as deltas without a durable sequence | Add reliable ordered structural op log with gap detection. |
| Interest | `NetworkHost` broadcasts to every peer | Add per-peer region/entity subscriptions; keep broadcast for single-region fallback. |
| SyncValue | Scoped by world, element, component | Add region and epoch scoping. |
| Remote writes | `SceneCommandApplier` rejects all writes to `WorldAuthority.Remote` | Allow writes to owned entities; reject everything else. |
| Scene commands | Host or local only | Validate actor against authority and permissions. |
| Held objects | `PhysicsBodyComponent.[Remote] Grab/GrabMove/Release` calls | Keep the transport; add an authority grant, epoch, timeout, and host reclaim. |
| Host abstraction | `NetworkWorldLink` is direct host/client | Add `InProcessHost`, `LocalWorkerHost`, `RemoteRegionHost` behind one interface. |
| Worker IPC | `IGameService` in process | Add supervisor, versioned IPC, limits, restart. |
| Legacy networking | `GameRoot.SetupNetworking` process-wide path | Keep for standalone; session path is the supported future. |
| Mobile | No policy | Client-only by default; opt-in small host later. |

## 12. Phased implementation

Each phase should be useful without federation.

### Phase A: authority types and local store

- Add `EntityAuthorityKind`, `EntityAuthority`, and
  `IEntityAuthorityStore`.
- Add a session-scoped in-memory store.
- Add tests for assignment, epoch bump, stale rejection, and disconnect
  reclaim.
- No wire changes yet.

Exit: a single host can mark an entity as host-owned or player-owned and reject
writes from the wrong writer locally.

### Phase B: route writes through authority

- `DirtyTracker` asks `CanWrite` instead of checking only `IsAuthority`.
- `SceneCommandApplier` allows writes to owned entities.
- Client-owned avatar pose and held-object release use the same path.
- Add `AuthorityChanged` locally.

Exit: two local sessions can exchange an owner avatar and a host prop without
one overwriting the other.

### Phase C: structural op log and epoch on the wire

- Add `StructuralOp`, sequence, and gap detection.
- Stamp epoch on element, component, and hot-value messages.
- Reject stale epochs and log the rejection.
- Add resync from a snapshot on gap.

Exit: a client that misses a structural op detects the gap and recovers instead
of silently diverging.

### Phase D: local freeze, snapshot, resume

- Implement handoff with `LocalWorkerHost` and no network dependency.
- Snapshot at a fixed tick boundary.
- Kill the worker, start a successor, resume with a higher epoch.
- Add `SessionFrozen` and `SessionResumed` local events.

Exit: a region worker can crash and be replaced without taking down the process,
and clients reject old-epoch state.

### Phase E: remote handoff and leases

- Add `ILeaseStore` with the Postgres implementation from
  `FEDERATED_INSTANCES.md`.
- Add `RemoteRegionHost` and gateway routing.
- Transfer the snapshot through the control plane or object store.
- Add signed tickets and epoch enforcement at join.

Exit: a world moves from one machine to another and clients resume.

### Phase F: region grid and interest

- Add region descriptors, borders, and per-peer subscriptions.
- Transfer entities at boundaries using the same authority protocol.
- Treat cross-region physics as a known hard problem.

Exit: a multi-region world runs with one authority per region.

### Phase G: federation control plane

- VNode discovery, accounts, world manifests, tickets, moderation.
- Direct federation without VNode for trusted clusters.

Exit: independent nodes can find and join each other's worlds without a central
game server.

### Phase H: authoring documents

- OT or CRDT for collaborative world editing.
- Keep it separate from live entity state.

Exit: multiple builders can edit the same document while the live region host
stays authoritative.

## 13. Testing

- Authority assignment and epoch fencing unit tests.
- Two-root loopback tests for owner and host state.
- A missed structural op triggers resync.
- An old-epoch packet is rejected and does not mutate the world.
- A held object releases back to the host with a new epoch.
- A disconnected owner's entities are reclaimed.
- Local worker kill, snapshot, successor, resume.
- Handoff with a client that has pending predictions; predictions are dropped on
  the epoch change.
- Large snapshot timing and freeze duration measurements.
- Mod-set mismatch is rejected or degrades to spectator.
- Mobile client reconnect on network change.
- 20/50/100 client bandwidth with interest filters.

Use the existing loopback harness in `NetworkWorldLinkTests` and the local
worker supervisor tests when they exist. Do not rely only on the sample.

## 14. Risks and open questions

- **Snapshot completeness.** Scripts, mods, and physics state are hard to
  serialize. A mod that cannot serialize should make the session explicitly
  non-transferable.
- **Handoff cost.** Large user worlds can freeze for seconds. Measure early.
- **Asset availability.** The successor needs the same content. Use
  content-addressed storage and pre-warming.
- **Global id retrofit.** Do it before a region grid exists.
- **Cross-region physics.** Boundary entities are unsolved. Expect one rewrite.
- **Interest management.** Per-peer filters are a large change.
- **Ownership UX.** Players need to understand who owns a grabbed object.
- **Mobile hosting.** Keep it opt-in and capped.
- **Mod mismatch.** Silent divergence is worse than rejection.
- **Lease store availability.** A control-plane outage must not corrupt a live
  region.
- **Split brain.** Epoch fencing is mandatory, not optional.
- **Shared authority.** Do not implement until a real use case exists.

## 15. Decisions still to lock

1. Global entity id format: GUID versus `(worldId, regionId, localId)`.
2. Authority registry location: session service only, or also an authoring
   component.
3. Desktop avatar model: input commands with host simulation, or owner pose.
4. Held-object transfer semantics and validation.
5. Structural op payload format and compression.
6. Snapshot format, compression, and asset reference scheme.
7. Handoff trigger UX and who pays for a successor.
8. Mobile hosting limits.
9. Mod manifest, hash, and compatibility policy.
10. Gateway protocol and relay fallback.

## 16. Related docs

- [`FEDERATED_INSTANCES.md`](FEDERATED_INSTANCES.md): control plane, regions,
  leases, workers, identity, federation.
- `V12-Engine/Core/Worlds/WorldSession.cs` and `WorldManager.cs`: current session
  focus, hidden-world, and handoff callback behavior.
- [`BUILDING_GAMES.md`](BUILDING_GAMES.md): fixed step, transforms, `SyncValue`.
- `V12-Engine/Docs/SYNC_VALUE_GUIDE.md`: value-level replication limits.
- `V12-Samples/MultiplayerDemo/README.md`: current prediction and avatar
  replication example.
