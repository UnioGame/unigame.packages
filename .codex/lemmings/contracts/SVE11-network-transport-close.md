# SVE11 Network Transport Close Contract

## Boundary

Repair the Unity-free in-memory transport lifecycle before Session is added. Scope is limited to `Runtime/BuffersTransport.cs`, focused `RuntimeTests.cs`, and orchestration metadata. Do not change schema, codecs, session, replication, README or asmdefs.

`MemoryTransport` and both endpoints of a pair are single-thread-affine and must be externally serialized. Concurrent access is undefined; no locks are introduced.

## Public lifecycle

Preserve numeric compatibility and append the new state:

- `TransportState`: Connected=0, Faulted=1, Disposed=2, Closed=3.
- `TransportError : byte`: None=0, QueueOverflow=1, RemoteClosed=2, InvalidPacket=3, Disposed=4.

Replace `ITransport.FaultReason` with read-only `ITransport.Error`. This intentional bounded source/binary break removes protocol `ResyncReason` from transport-local lifecycle reporting. Baseline search found no live consumer outside tests and stale isolated worktrees; do not retain a compatibility alias.

The error is the immutable cause of the current terminal state, never a transient last-call result:

- Connected => None.
- Closed => RemoteClosed.
- Faulted => QueueOverflow or InvalidPacket.
- Disposed => Disposed.

`TryReceive` and failed calls on a terminal endpoint never overwrite the state/error.

## Ownership and transitions

`TrySend` first transfers every valid input lease on every path. Null or invalid input still throws through `PacketLease.Transfer` without changing state. A non-Connected sender disposes the transferred lease, returns false, and preserves its terminal state/error.

`TryReceive` in Closed, Faulted or Disposed always returns false/default/null. Every transition out of Connected drains that endpoint queue exactly once and invalidates all aliases to drained leases.

All pair-terminal transitions detach both peer references before draining:

- Connected local Dispose => local Disposed/Disposed, connected peer Closed/RemoteClosed, both queues drained, both detached.
- Dispose after remote Closed => closed endpoint Disposed/Disposed; already-disposed peer unchanged.
- Reliable queue overflow => both Faulted/QueueOverflow, both queues drained and detached.
- Invalid unreliable send or undefined Channel => sender Faulted/InvalidPacket, peer Closed/RemoteClosed, both queues drained and detached. Invalid means malformed/short/unreadable header, wrong FullSnapshot kind or flags, zero sequence, or inconsistent framed length. Reliable payloads remain opaque.

Dispose is idempotent from every state and first Dispose always ends locally as Disposed/Disposed.

Stale/equal unreliable sequence is a normal lossy rejection: consume, return false, remain Connected/None and preserve queued packets. Unreliable capacity rejection after normal latest replacement is also Connected/None. A valid newer snapshot disposes only older queued unreliable snapshots and preserves reliable ordering.

## Acceptance

- Initial Connected/None invariant for both endpoints.
- Peer-visible close, symmetric public detach effects and idempotent Dispose from Connected, Closed and Faulted.
- Both reliable and unreliable queued aliases become invalid on terminal transitions; terminal receive is false/default/null.
- Send after Closed/Faulted/Disposed consumes the lease and preserves terminal state/error.
- Reliable overflow consumes the trigger, invalidates queues and faults both endpoints with QueueOverflow.
- Stale/equal snapshot is consumed without fault or eviction of the queued newer snapshot.
- Malformed and zero-sequence unreliable inputs independently fault sender InvalidPacket and close peer RemoteClosed; undefined Channel is covered.
- Existing reliable ordering and latest-snapshot behavior remain covered without double return or use-after-transfer.
- Runtime/test compilation, focused Unity EditMode package tests, exact ownership, meta/diff checks and `lemmings check --all` pass.

