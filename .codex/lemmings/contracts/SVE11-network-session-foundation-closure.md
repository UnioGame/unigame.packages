# SVE11 Session Foundation Closure

## Scope and precedence

This contract freezes the prerequisites for the Session state machine. It changes only orderly-close wire enumeration, stepped transport progression, outbound typed command retention, dispatcher construction validation, and shared allocation-test isolation. It does not implement Session, change packet headers, alter replication behavior, or add Unity dependencies.

Where earlier command-dispatch documentation allowed missing result-event registrations until dispatch, this contract takes precedence: `CommandDispatcher<TWorld>` construction now requires both closed generic result event types. `DispatchResult.ConfigurationError` remains for world lifecycle drift after successful construction.

## Wire closure

- Append `DisconnectReason.Requested = 8` without renumbering values 1 through 7.
- `PayloadCodec` accepts values 1 through 8 and rejects 9.
- Freeze the Requested payload as `08 00 00 00` and retain the existing ServerShutdown golden `07 00 00 00`.
- No other wire layout, packet kind, header field, protocol limit, or disconnect reason changes.

## Stepped transport

```csharp
public interface ISteppedTransport
{
    void BeginStep(ulong stepIndex);
}
```

- The operation is finite and non-blocking and establishes the deterministic logical step barrier used by future replay transports.
- `MemoryTransport` implements the interface as a no-op in every lifecycle state. It does not change state, error, queues, sequences, ownership, or close behavior.

## Enqueue result

```csharp
public enum EnqueueResult : byte
{
    Queued = 0,
    Unavailable = 1,
    Full = 2,
    UnknownCommand = 3,
    TooLarge = 4,
    CodecFailed = 5,
    SequenceExhausted = 6
}
```

- `Unavailable` is reserved for the future Session facade. A valid bare outbox never returns it.
- Do not add another public queue result enum.

## Command outbox API

```csharp
public sealed class CommandOutbox<TWorld> : IDisposable
    where TWorld : struct, IWorldType
{
    public CommandOutbox(
        Schema schema,
        int commandCapacity = ProtocolLimits.MaxCommandsPerBatch,
        int byteCapacity = ProtocolLimits.MaxWirePayloadBytes);

    public int Count { get; }
    public int UnsentCount { get; }
    public int Bytes { get; }
    public uint LastSequence { get; }
    public uint LastSentSequence { get; }
    public uint AcknowledgedSequence { get; }

    public EnqueueResult Enqueue<T>(in T command, uint clientTick)
        where T : unmanaged;

    public bool TryBuild(out PacketLease payload, out uint throughSequence);
    public void MarkSent(uint throughSequence);
    public bool Acknowledge(uint sequence);
    public void Dispose();
}
```

The outbox is single-thread-affine and belongs to one Session epoch. Construction requires a non-null matching-world schema, command capacity 1 through 256, and canonical decoded byte capacity 36 through `MaxWirePayloadBytes`. World initialization is not required. Construction allocates the fixed `Entry[]` ring, bounded circular byte storage, and reusable scratch sized to the largest registered command `SchemaEntry.MaxPayload`.

`Bytes` is zero when empty; otherwise it is `4 + sum(32 + payloadLength)` across sent-unacknowledged and unsent entries. `Count` includes both; `UnsentCount` excludes sent entries. Sequence high-water properties start at zero and never decrease.

## Enqueue transaction

Enqueue performs these checks in order without state mutation on failure:

1. disposed throws `ObjectDisposedException`;
2. absent retained typed binding returns `UnknownCommand`;
3. exhausted last sequence returns `SequenceExhausted`;
4. encode into the complete registered maximum-payload scratch;
5. codec false, negative length, or length beyond the registered bound returns `CodecFailed`;
6. a valid canonical record that cannot fit an empty configured batch returns `TooLarge`;
7. current retained count or aggregate-byte pressure returns `Full`;
8. commit the entry and assign the next contiguous non-zero sequence, then return `Queued`.

Codec exceptions propagate after cleanup and leave state unchanged. Zero-byte commands are valid. `uint.MaxValue` may be assigned once; subsequent enqueue returns `SequenceExhausted`. `TooLarge` must remain distinguishable from `CodecFailed`, so codecs never receive a scratch span shortened to the negotiated batch capacity.

## Build, send, and acknowledgement

- The first successful `TryBuild` freezes the current unsent prefix and through-sequence.
- Repeated builds before `MarkSent` return independently owned, byte-identical `PacketLease` instances for that same prefix, even if later commands are enqueued.
- A false build returns `default` and sequence zero.
- `MarkSent` accepts only the exact frozen through-sequence, advances that range once, and clears pending state. No pending, zero, stale, future, or mismatched values throw `InvalidOperationException` without mutation.
- Future Session code calls `MarkSent` only after successful reliable transport send. Failed reliable send consumes the framed owner but does not mark the outbox.
- `Acknowledge` returns false only when the value exceeds `LastSentSequence`. Zero, stale, and duplicate values return true without mutation. A valid cumulative advance disposes only the sent head prefix through the sequence, releases capacity, and never removes pending or unsent entries.
- Disposal is idempotent, drains all retained owners, and does not affect independently built leases. Other operations after disposal throw.
- No command storage or sequence is replayed into a new Session epoch.
- An internal sequence-exhaustion test seam is allowed only on a fresh empty outbox.

## Typed command seams

- Add only internal AOT-safe retained command APIs: `Schema.TryGetCommand<T>`, `ICommandInvoker<T>.TryWrite`, and a non-allocating result-event registration probe.
- Lookup uses retained entries plus a closed generic interface test. Do not use reflection invocation, dynamic code, delegates, boxing, or a new public Schema encoder surface.
- The retained struct codec calls `ICodec<T>.TryWrite` directly.
- Integrated `IRecordInvoker<TWorld>` capture/apply behavior and all replication retained-entry semantics remain unchanged.

## Dispatcher construction

Validation order is null schema, matching world, initialized world, then both `CommandAcceptedEvent<T>` and `CommandRejectedEvent<T>` registrations for every retained command. Failure throws before codec or authorizer activity. There is no weak compatibility constructor. Dispatch retains defensive lifecycle/configuration checks and `NoReceiver` semantics.

## Shared allocation gate

Move the one internal `PoolTestGate` declaration from `RuntimeTests.cs` to a dedicated test file in the same namespace. Runtime, replication, and outbox allocation fixtures use that same Monitor gate. `ReplicationTests.cs` remains unchanged in this phase.

## Validation

- Freeze enum values and disconnect goldens, including rejection of reason 9.
- Prove `MemoryTransport.BeginStep` is a no-op across connected and terminal queues.
- Prove dispatcher construction ordering, complete registration, wrong-world precedence, and lifecycle-drift defense.
- Cover outbox constructor bounds, exact canonical bytes, zero/maximum payload behavior, unknown/codec false/invalid/throw, `TooLarge` before `Full`, count/byte pressure, both ring wrap-arounds, pending retries, exact and invalid mark, acknowledgement matrix, exhaustion, ownership, disposal, and post-warm zero allocation.
- Run the complete package suite after focused tests to protect read-only replication behavior.

