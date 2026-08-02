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

All public members receive English XML summaries. `Enqueue` checks disposed first, delegates to the client outbox only while the client is Established, and otherwise returns `Unavailable`. `Capture` checks in this exact order: disposed throws; client role returns `WrongRole`; non-Established server or missing authority collaborators returns `ScopeInvalid`; then `serverTick == PacketHeader.NoneTick` or a non-increasing captured tick throws `ArgumentOutOfRangeException`; otherwise it returns the exact `Replicator.Capture` result. Zero is a valid first tick.

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

- Successful server Capture records an independent generated canonical lease and xxHash64 under `serverTick`, then installs the original capture as the pending snapshot. Default Session history cannot reject one protocol-bounded snapshot by size; a defensive false return still leaves transfer successful and the pending snapshot owned by Session. Existing TickHistory tests own reduced-capacity rejection coverage.
- Successful client Apply records an independent received canonical lease under the authoritative `ServerTick`. `ReceivedHash` and `PostApplyHash` equal the decoded canonical payload hash; v1 has no predicted/post-apply recapture and the post-apply lease remains default.
- Failed capture, framing, staging, dispatch, or returned apply never adds a TickRecord. Session supplies strictly increasing ticks, one protocol-bounded lease, and distinct ownership, so ordinary TickHistory validation and capacity rejection are unreachable for these default records. Process-level allocator failures such as `OutOfMemoryException` are not a recoverable Session protocol guarantee or an injected test case; locally held leases still use `finally` cleanup where the runtime permits execution.
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

`ApplyResult.Success` commits the snapshot. A pure internal result mapper freezes every defensive value: `SchemaMismatch` is terminal `SessionError.Schema/DisconnectReason.SchemaMismatch`; `WrongPayload` is terminal Protocol/ProtocolViolation; `WrongRole` and `ScopeInvalid` are terminal Topology with no wire reason; `EntityConflict` queues `ResyncRequest(LocalStateConflict)`; `InvalidEntity`, `MissingTarget`, and `LimitExceeded` queue `ResyncRequest(SnapshotRejected)`. Framing, schema binding, fixed replica role, and pre-Step scope validation make WrongPayload, SchemaMismatch, WrongRole, and normally ScopeInvalid unreachable through a valid client receive; tests cover them through the internal pure mapper, not reflection or collaborator injection. No failed Apply advances snapshot acknowledgement or history.

The resync payload uses the last accepted snapshot tick, or `PacketHeader.NoneTick` before any success. All five defined `ResyncReason` values are accepted by the server and request a fresh snapshot; the header epoch is still validated independently. The first failed snapshot freezes the queued resync reason and last accepted tick. Further failed snapshots preserve that queued intent. A successful snapshot clears a queued resync that has not become the active reliable intent. Once a ResyncRequest has been attempted and is the active failed reliable intent, later snapshot success marks the request obsolete but does not cancel or rewrite it; it still commits to preserve sequence certainty. After a successful resync send, a later failure may create a new intent. Unexpected header epoch remains a Session Core terminal failure before transfer decode.

## Established send priority

At most one send is attempted per Step.

Client priority:

1. pending `ResyncRequest` on ReliableOrdered;
2. frozen unsent `CommandBatch` on ReliableOrdered;
3. empty `Ack` on ReliableOrdered when a newer snapshot acknowledgement has not been carried successfully.

Server priority:

1. pending `FullSnapshot` on UnreliableSequenced;
2. empty `Ack` on ReliableOrdered when a newer processed-command acknowledgement has not been carried successfully.

Priority selects a new reliable intent only when no reliable intent is active. The first attempt freezes exactly one active ReliableOrdered intent, its next uncommitted reliable sequence, payload, schema, and acknowledgement fields. That intent is retried until transport acceptance, explicit Requested-close cancellation, or terminal Session state; no CommandBatch, ResyncRequest, or Ack can supersede it. A client snapshot success may make an active ResyncRequest obsolete, but the request is still sent to preserve reliable sequence certainty. New commands, resync demand, and acknowledgements queue behind the active intent and are reconsidered after it commits. On a server, a pending unreliable FullSnapshot may use its independent sequence domain before a frozen reliable Ack retry; no other reliable intent may do so.

CommandBatch and ResyncRequest carry the latest accepted snapshot acknowledgement at the moment their outbound intent is first frozen. FullSnapshot and server Ack carry the latest processed command acknowledgement when their outbound intent is first frozen. A false or throwing send retains every frozen header field and sequence; later inbound progress cannot rewrite a retry. Every successfully accepted acknowledgement-carrying packet, including empty Ack, updates its last-carried tracker monotonically. Command acknowledgement zero is bottom and real values use ordinary unsigned max. Snapshot acknowledgement `PacketHeader.NoneTick` is the logical bottom despite its numeric `uint.MaxValue`; any real tick including zero is newer than NoneTick, and only two real ticks use unsigned max. Independent unreliable and reliable success can therefore never regress either tracker. A later high-water produces a subsequent packet or Ack. FullSnapshot carries its captured tick and `BaselineTick=NoneTick`.

CommandBatch payload and acknowledgement freeze on the first build. FullSnapshot payload freezes at Capture and its command acknowledgement freezes on the first send attempt. Resync freezes at the first apply failure. Ack freezes its role-specific high-water on the first send attempt. Tests interleave a successful receive between false send attempts and require byte-identical retry followed by a later acknowledgement.

`CommandOutbox.MarkSent` occurs only after successful transport acceptance. A failed or throwing send preserves the frozen batch. A successful snapshot send commits the unreliable sequence and disposes the decoded pending owner. A failed or throwing send preserves it. Reliable and unreliable exhaustion fault Sequence/SequenceExhausted before encode, rent, or send. Every encoded owner is cleaned after false, true, transfer-then-throw, and ordinary exception paths.

`Capture` commits nothing until Replicator capture, history clone construction, and ordinary history insertion have completed. If Replicator, a codec, or a capture callback throws, the original exception propagates; State, Error, Reason, NeedsSnapshot, last successful capture tick, history, and any older pending snapshot remain unchanged; every new local lease is disposed exactly once; the same `serverTick` may be retried. The same transaction and cleanup apply if clone/record construction throws. Only after that transaction does Capture replace the older pending snapshot, advance the capture tick, and clear demand.

An empty staged CommandBatch is a Protocol/ProtocolViolation failure and emits no event or acknowledgement. Non-empty batches are completely preflighted for contiguous next command sequences before the first dispatch. Each returned Accepted or Rejected result immediately commits that command high-water. If authorization, codec, or event delivery throws at the first or a later command, Session faults Topology with no wire reason, does not commit the reliable packet sequence or peer snapshot acknowledgement, emits no Ack/history for that packet, cleans all packet/stage owners, and rethrows the original exception. Earlier command events and their already committed command high-water are not rolled back; terminal state prevents redispatch.

If Replicator.Apply or an ECS hook throws, Session faults Topology with no wire reason, does not commit the unreliable packet sequence, peer command acknowledgement, accepted snapshot tick, history, Ack, or resync, cleans all packet/stage owners, preserves any documented partial ECS mutation, and rethrows the original exception. Terminal state prevents reapplication.

## Requested close interlock

Local `Close`, inbound Requested, and simultaneous Requested close take precedence over transfer. If a transfer CommandBatch, ResyncRequest, or Ack owns an active failed reliable intent, Session cancels that intent without committing its sequence and reuses the same next reliable sequence for the Core Disconnect. A canceled CommandBatch remains retained and unmarked in its outbox until collaborator disposal; queued or active resync and Ack intent state is cleared. No canceled transfer packet is retried in Closing. This follows the existing transport contract that only a `true` TrySend commits acceptance; false or thrown attempts never advance Session sequence state. Local, remote, and simultaneous close tests cover every active intent kind, byte/lease cleanup, same-sequence Disconnect, and final Core state.

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
- Prove one active reliable intent across failed-batch then resync and failed-Ack then command/resync interleavings; server unreliable snapshot may preempt only a reliable Ack without consuming its sequence.
- Prove queued versus active resync cancellation and local, remote, and simultaneous Requested close cancellation of every active transfer intent with same-next-sequence Disconnect.
- Prove byte-exact headers, channels, schemas, ticks, baseline and both acknowledgement fields for CommandBatch, FullSnapshot, Ack, ResyncRequest, and Disconnect boundaries.
- Prove contiguous command dispatch, trusted peer context, accepted/rejected cumulative acknowledgement, future/stale ack behavior, and no-receiver/configuration terminal mapping.
- Prove increasing Capture and exact role/state/argument precedence, supersession, frozen-ack false/throw retry, history clone ownership, and initial/resync `NeedsSnapshot` lifecycle. Reduced-capacity rejection remains a TickHistory unit concern.
- Prove capture/codec callback throws with and without an older pending snapshot, unchanged state/demand/high-water/history, exact cleanup, and same-tick retry.
- Prove successful full apply/history/ack, every pure ApplyResult mapping plus all reachable end-to-end outcomes, first-failure resync stability, all accepted reasons, later-success cancellation, and no ECS/history/high-water mutation on returned failures.
- Prove empty CommandBatch rejection and first/later thrown Dispatch or Apply callbacks with exact partial-mutation, high-water, terminal, cleanup, and no-retry semantics.
- Prove sequence exhaustion before allocation/send, transfer-then-throw cleanup, receive-commit before outbound throw, and all terminal/disposal paths.
- Prove acknowledgement tracking for snapshot NoneTick-to-zero, NoneTick-to-positive, real-to-stale, and command zero-to-real, plus a newer FullSnapshot succeeding between a failed stale server Ack and its later retry.
- Run warnings-as-errors compilation, focused transfer tests, the complete package harness, exact ownership/meta/no-Sample scans, `git diff --check`, `lemmings check --all`, then Unity EditMode after accepted integration.
