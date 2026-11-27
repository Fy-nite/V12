# The Simulating User

> **Status:** implemented core, no epoch fencing yet.
> **Code:** `V12-Engine/Core/Worlds/EntityAuthorityStore.cs`,
> `V12-Engine/Core/Worlds/WorldSession.cs`,
> `V12-Engine/Core/Worlds/NetworkWorldLink.cs`,
> `V12.Basic/Systems/PhysicsLocomotionSystem.cs`.
> **Design context:** [`PEER_HOSTING_AND_AUTHORITY.md`](PEER_HOSTING_AND_AUTHORITY.md) §5.

## The rule

Every element has exactly one **simulating user**: the peer responsible for its
state. The host is the default. A player becomes the simulating user of an object
it holds, wears, or is otherwise driving.

The simulating user does the work; everyone else applies the result:

| | simulating user | everyone else |
|---|---|---|
| components | `Update` runs (scope `Simulator`) | does not run |
| physics body | dynamic, stepped | kinematic follower, driven by the element pose |
| pose | writes `LocalTransform`, which is sent | applies the received pose |
| values | writes `SyncValue`, which is relayed | applies |
| script / VM | `on_update` runs | does not run |

Only one peer changes an object at a time, so there is nothing to blend and no
divergence to reconcile. That is the whole point: it is not "everyone simulates
and we hope they agree", it is "one peer simulates and tells everyone".

## Who calls what

### Host: on client join

Hand the peer its player id. This is your own welcome message; the engine does
not impose an identity protocol. The demo's `PlayerWelcome` is the reference.

```csharp
// Host, when a client connects (NetworkWorldLink.ClientJoined, or your own
// protocol). The id comes from the host's NetworkHost assignment.
long playerId = _link.HostPlayerId; // for your own player
// For a connected client, use the id you got from ClientJoined(playerId).

_link.SendToClient(playerId, new MessageDTO
{
    Sender = new Uri("networkcables://worldhost"),
    MessageType = MessageType.PlayerWelcome,   // your own protocol type
    Message = JsonSerializer.SerializeToUtf8Bytes(new PlayerWelcome
    {
        PlayerId = playerId,
        // avatar assignment, tokens, whatever else you send
    }),
});
```

### Client: on receiving the welcome

Tell the session who it is. Do this **before** the client can own anything, so
that the first `EntityAuthority` message it receives is understood.

```csharp
// Client, in the welcome handler.
session.LocalPlayerId = welcome.PlayerId;
```

If `LocalPlayerId` stays 0, the client treats every element as a replica: no
components tick, no poses are sent, no physics is stepped. That is a safe
default, not a broken state.

### Host: on grab

```csharp
// Host. Returns false when this peer is not the host, or the element has no
// stable Uid (an element without a Uid cannot be delegated).
bool ok = _link.AssignSimulatingUser(element, playerId);
```

This applies the assignment on the host, then broadcasts
`MessageType.EntityAuthority` (reliable ordered). The new owner starts
simulating on its next fixed step; every other peer stops and treats the element
as a replica.

### Host: on release

```csharp
_link.AssignSimulatingUser(element, 0);   // 0 = the host takes it back
```

The host drops to a kinematic follower driven by the released pose, and resumes
simulating the element itself.

### Client: on grab / release (hot path)

The client does not call `AssignSimulatingUser`; it is host-only. It asks instead:

```csharp
// Client. Returns false for a host/solo world, or before LocalPlayerId is known.
_link.RequestSimulatingUser(element);   // claim
_link.ReleaseSimulatingUser(element);   // give it back
```

The host grants a claim when the element is unowned or already yours, so
**first claim wins**; a denied claim is simply never answered and the client keeps
treating the element as a replica. Keep driving the body locally while you wait
so the grab feels immediate; the grant is what makes those poses replicate.

`PickupSystem` and `GrabberComponent` do this for you: they claim on grab, keep
driving the body locally, re-resolve the body after the grant rebuilds it, and
release on drop. A client that grabs without the host assigning it is ignored:
the host only accepts element poses from the element's simulating user.

## Disconnects

Closing a client notifies the host immediately (`NetworkClient.Disconnect` sends
a disconnect instead of vanishing). The host then hands every element that player
was simulating back to itself and broadcasts the new assignments before your
`ClientLeft` handler runs, so:

- nothing stays frozen on the remaining peers,
- the host starts simulating those objects again on the next tick,
- other clients drop to followers.

`ClientLeft(playerId)` handlers should still clean up game-side state (avatars,
session records), but they no longer need to fix ownership themselves.


## Order of operations

```
client connects
  → host assigns a player id
  → host sends your welcome
  → client sets session.LocalPlayerId
  → host sends the world snapshot
  → client has elements (all replicas)
player grabs an object
  → host calls AssignSimulatingUser(element, playerId)
  → EntityAuthority broadcast: owner flips
  → client: body rebuilt as dynamic, components start ticking
  → client writes LocalTransform / SyncValue
  → host applies it, relays to the other peers
player releases
  → host calls AssignSimulatingUser(element, 0)
  → host: body rebuilt as dynamic, resumes simulation
  → everyone else: kinematic follower of the host pose
```

The welcome must arrive before the first assignment for that client, and both
must arrive before the client simulates anything. `LocalPlayerId` can be set late
without breaking anything: the client simply ignores assignments until it knows
who it is, and the host re-sends nothing today, so set it before you assign.

## What a game must not do

- Do not call `AssignSimulatingUser` from a client. It returns `false`.
- Do not let two players grab the same object without arbitrating on the host.
  `AssignSimulatingUser` is last-call-wins; reject the loser in your grab handler
  if you want to tell them why.
- Do not delegate an element that was spawned without a `Uid`. `Assign` returns
  0 and nothing happens.
- Do not write host-owned transforms from a client. The client gate refuses to
  send them and the host refuses to apply them.

## Physics

`PhysicsLocomotionSystem` follows the same answer, per element:

- simulating user: a dynamic body, stepped locally, element pose written from the
  body.
- everyone else: a kinematic follower rebuilt from the element pose each tick.

The body is destroyed and recreated when the role flips, so a follower never
keeps the inertia of the dynamic body it replaced. The flip happens on the first
tick after the assignment arrives.

## Values vs poses

- **Poses** travel as `WorldElementUpdate`. A client may only send them for
  elements it simulates (`WorldSession.IsSimulatedLocally` → the session's
  `DirtyTracker.CanSendElement`), and the host filters again before applying and
  relaying, trusting only the pose fields.
- **Values** (`SyncValue<T>`) travel as `SyncBatch`, last-write-wins. Writes are
  relayed to the whole session.

## What the host enforces

- **Poses.** A client may only report `LocalTransform` for elements it simulates.
  The host filters each `WorldElementUpdate` through `CanWrite` and trusts only
  the pose fields; name, description, parent and uid stay host-authoritative.
- **Values.** `SyncBatch` keys are parsed back to their element and checked the
  same way, so a client cannot write a `SyncValue` or a `[Sync]` field on an
  element it does not simulate. Only the allowed entries are applied and relayed.
- **Epochs.** `EntityAuthorityStore.Apply` rejects a stale or duplicate epoch, so
  a reordered or replayed message cannot revert ownership.
- **Joins.** A new client receives the current assignment map with its snapshot,
  so it knows who owns what from the first tick.
- **Leaves.** A disconnected client's elements are handed back to the host and
  broadcast before `ClientLeft` runs.

## Not implemented yet

- **Authorization policy.** First claim wins, and any client may claim any
  unowned element. There are no permissions yet, so a world that needs them must
  vet claims itself (the host is the only one that decides).
- **Stale-owner reclaim.** A client that is connected but hung keeps its
  elements. Only a transport disconnect reclaims them today.
- **Component state from clients.** A client's component (`MarkDirty`) changes are
  drained, not sent. Only poses, `[Sync]` fields and `SyncValue` writes leave a
  client.
- **Interest management.** Every peer receives every element; `NetworkHost`
  broadcasts.
