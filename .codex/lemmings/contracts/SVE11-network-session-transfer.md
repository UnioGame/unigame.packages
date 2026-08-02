# SVE11 Session Transfer

## Scope and precedence

This contract extends the accepted Session Core at baseline `ee5968c3001c35353df45035916e9ea0f660a42e` with established-state command, full-snapshot, acknowledgement, bounded history, and resynchronization transfer. It does not change the wire format, handshake, public constructor, transport ownership, replication semantics, command authorization, or accepted Session Core terminal mappings.

Delta snapshots, rollback, prediction, replay, observers, diagnostics, compression, a real network carrier, and Unity dependencies are out of scope.

## Public surface

`Session<TWorld>` adds only:

```csharp
public EnqueueResult Enqueue<T>(in T command, uint clientTick) where T : unmanaged;
public CaptureResult Capture(uint serverTick);
public bool NeedsSnapshot { get; }
```

All public members receive English XML summaries. `Enqueue` throws after Session disposal, delegates to the client outbox only while the client is Established, and otherwise returns `Unavailable`. `Capture` throws after disposal, returns `WrongRole` on a client, returns `ScopeInvalid` unless a server is Established with valid authority collaborators, and otherwise returns the exact `Replicator.Capture` result. `serverTick == PacketHeader.NoneTick` or a non-increasing captured tick throws `ArgumentOutOfRangeException` without mutation; zero is a valid first tick.

`NeedsSnapshot` is server-facing demand. It is false for clients. It becomes true when a server establishes and whenever a valid ResyncRequest is consumed. A resync invalidates any older unsent snapshot. A successful `Capture` installs a new pending full snapshot and clears the demand. Ordinary game loops may call `Capture` every increasing server tick even when the property is false.

No `Sample`, `NetworkSession`, facade, manager, request object, result enum, public history, or public diagnostics type is added.

## Owned collaborators

- A server constructs `CommandDispatcher<TWorld>` before transport ownership is committed. Missing command result event registrations therefore fail Session construction without disposing the caller transport.
- Both roles construct and own one default-bounded `TickHistory` on successful Session construction.
- A client constructs and owns `CommandOutbox<TWorld>` after the valid Server Hello has bound the phase schema and before final Ack. Public `Enqueue` remains unavailable until Established.
- The server owns one decoded pending snapshot lease. Successful newer Capture replaces and disposes an older pending snapshot. History owns an independent clone; retry framing never borrows mutable ownership from history.
- Session disposal and every collaborator-release path dispose the outbox, history, pending snapshot, scope, and replicator exactly once. Session never disposes a staged payload twice and still exclusively disposes its transport only from `Dispose`.

One narrow internal `History` test probe is allowed. No reflection or public diagnostic seam is allowed.

## History

- Successful server Capture records an independent generated canonical lease and xxHash64 under `serverTick`, then installs the original capture as the pending snapshot. If the bounded history rejects an oversized record, transfer still succeeds and the pending snapshot remains owned by Session.
- Successful client Apply records an independent received canonical lease under the authoritative `ServerTick`. `ReceivedHash` and `PostApplyHash` equal the decoded canonical payload hash; v1 has no predicted/post-apply recapture and the post-apply lease remains default.
- Failed capture, framing, staging, dispatch, or apply never adds a TickRecord. History insertion exceptions propagate after exact lease cleanup and leave transfer high-water state unchanged.
- Existing whole-tick capacity, command capacity, byte budget, lookup, reconciliation, and disposal semantics remain unchanged. Current TickRecord/LinkedList/Dictionary setup allocations are an explicit v1 cost to expose in later diagnostics; this phase does not claim zero allocation for history insertion.

## Established receive

Each `Step` still performs at most one receive and one send attempt after one `BeginStep`. Reliable packet sequences are exact next values in the existing shared control domain. Unreliable snapshot sequences are non-zero and strictly newer; gaps are valid. Sequence state commits only after complete semantic consumption.

Common established fields are: negotiated epoch, transform zero, `BaselineTick=NoneTick`, channel-matching flags, and phase-appropriate schema and acknowledgement fields. All semantic and acknowledgement validation occurs before ECS, outbox, history, demand, or high-water mutation.

### Server inbound

- `CommandBatch`: ReliableOrdered, schema hash exact, `ServerTick=NoneTick`, acknowledged command zero. `AcknowledgedSnapshotTick` is `NoneTick` or a cumulative value no newer than the last snapshot successfully sent. Commands must start at the next processed command sequence and remain contiguous. Every command is dispatched in wire order with trusted `PeerId`. `Accepted` and authorization `Rejected` both advance the processed command high-water. `NoReceiver` or `ConfigurationError` faults `Topology` with no wire reason. `WrongPayload`, `InvalidCommand`, or impossible schema drift faults Protocol or Schema as applicable.
- Client `Ack`: ReliableOrdered, empty payload, empty schema, `ServerTick=NoneTick`, acknowledged command zero, and a valid cumulative snapshot acknowledgement.
- `ResyncRequest`: ReliableOrdered, empty schema, `ServerTick=NoneTick`, acknowledged command zero, and payload `LastAcceptedTick` exactly equal to the header snapshot acknowledgement. Consumption disposes any pending older snapshot and sets `NeedsSnapshot=true` without ECS mutation.
- `Disconnect` retains the accepted Session Core rules and does not piggyback transfer acknowledgements.

### Client inbound

- `FullSnapshot`: UnreliableSequenced, schema hash exact, non-None strictly increasing `ServerTick`, acknowledged snapshot `NoneTick`, and a valid cumulative command acknowledgement no newer than `CommandOutbox.LastSentSequence`. Successful apply advances the accepted snapshot tick, acknowledges commands, clears a pending resync, and records history.
- Server `Ack`: ReliableOrdered, empty payload, empty schema, `ServerTick=NoneTick`, acknowledged snapshot `NoneTick`, and a valid cumulative command acknowledgement.
- `Disconnect` retains Session Core rules and no transfer acknowledgements.

An acknowledgement may be stale or duplicate. A future acknowledgement is Protocol failure and mutates neither high-water nor retained data. Command acknowledgements include authorization-rejected commands because they were deterministically consumed.

## Apply and resync

`ApplyResult.Success` commits the snapshot. `SchemaMismatch` is terminal `SessionError.Schema/DisconnectReason.SchemaMismatch`. `WrongRole` is terminal Topology. Returned `ScopeInvalid` or `EntityConflict` queues a byte-stable `ResyncRequest(LocalStateConflict)` and remains Established. Returned `WrongPayload`, `InvalidEntity`, `MissingTarget`, or `LimitExceeded` queues `ResyncRequest(SnapshotRejected)` and remains Established. No failed Apply advances snapshot acknowledgement or history.

The resync payload uses the last accepted snapshot tick, or `PacketHeader.NoneTick` before any success. A successful later snapshot clears an unsent resync request. Resync send failure or exception retains intent and retries the same payload and reliable sequence. Unexpected epoch remains a Session Core terminal failure before transfer decode.

## Established send priority

At most one send is attempted per Step.

Client priority:

1. pending `ResyncRequest` on ReliableOrdered;
2. frozen unsent `CommandBatch` on ReliableOrdered;
3. empty `Ack` on ReliableOrdered when a newer snapshot acknowledgement has not been carried successfully.

Server priority:

1. pending `FullSnapshot` on UnreliableSequenced;
2. empty `Ack` on ReliableOrdered when a newer processed-command acknowledgement has not been carried successfully.

CommandBatch and ResyncRequest carry the latest accepted snapshot acknowledgement. FullSnapshot and server Ack carry the latest processed command acknowledgement. Successful piggyback updates the corresponding last-sent acknowledgement and suppresses redundant Ack. FullSnapshot carries its captured tick and `BaselineTick=NoneTick`.

`CommandOutbox.MarkSent` occurs only after successful transport acceptance. A failed or throwing send preserves the frozen batch. A successful snapshot send commits the unreliable sequence and disposes the decoded pending owner. A failed or throwing send preserves it. Reliable and unreliable exhaustion fault Sequence/SequenceExhausted before encode, rent, or send. Every encoded owner is cleaned after false, true, transfer-then-throw, and ordinary exception paths.

## Allocation and determinism

No runtime reflection, dynamic invocation, per-command boxing, LINQ, or per-packet transform boxing is allowed. Existing retained schema invokers, pooled PacketLease storage, ArrayPool staging, CommandOutbox frozen retries, Replicator, and NoOp transform are reused. Repeated false-send retries are byte-identical. History record/node allocations are explicitly allowed in v1 and must later be visible in diagnostics.

## Files and ownership

Allowed implementation paths:

- `Runtime/SessionTransfer.cs` and `.meta`;
- bounded changes to `Runtime/Session.cs` and `Runtime/SessionProtocol.cs`;
- `Tests/Editor/SessionTransferTests.cs` and `.meta`;
- package `README.md`.

Protocol files, existing replication/outbox/dispatcher/history behavior, asmdefs, package manifest, Unity packages/assets, and Lemmings implementation metadata are forbidden to the worker.

## Acceptance

- Freeze the exact three-member public API and prove role/state/disposal behavior.
- Prove constructor failure/ownership for server dispatcher, client outbox lifecycle, history, pending snapshot, exceptions, and idempotent disposal.
- Prove exact client/server one-receive/one-send priorities and both sequence domains, including unreliable gaps and stale values.
- Prove byte-exact headers, channels, schemas, ticks, baseline and both acknowledgement fields for CommandBatch, FullSnapshot, Ack, ResyncRequest, and Disconnect boundaries.
- Prove contiguous command dispatch, trusted peer context, accepted/rejected cumulative acknowledgement, future/stale ack behavior, and no-receiver/configuration terminal mapping.
- Prove increasing Capture, supersession, false/throw retry, history clone ownership, oversized-history independence, and initial/resync `NeedsSnapshot` lifecycle.
- Prove successful full apply/history/ack, every ApplyResult mapping, resync byte stability, later-success cancellation, and no ECS/history/high-water mutation on failures.
- Prove sequence exhaustion before allocation/send, transfer-then-throw cleanup, receive-commit before outbound throw, and all terminal/disposal paths.
- Run warnings-as-errors compilation, focused transfer tests, the complete package harness, exact ownership/meta/no-Sample scans, `git diff --check`, `lemmings check --all`, then Unity EditMode after accepted integration.
