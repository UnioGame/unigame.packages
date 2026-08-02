# SVE11 Network Diagnostics Contract

## Scope and baseline

This contract extends the integrated network package at `14459a615b46826269751f034c3eb53071614a33` with optional Unity-free session observation, cumulative statistics, canonical snapshot fingerprints, strict NDJSON output, and privacy-sensitive transport trace/replay.

It does not change protocol bytes, negotiation, replication, command authorization, history retention, transport acceptance, retry ordering, sequence ownership, or Session terminal mappings. Compression, delta snapshots, rollback, prediction, a real carrier, a custom Unity Profiler window, and the gameplay sandbox are out of scope.

The base `unigame.staticecs.network` assembly remains `noEngineReferences: true`. Unity Profiler integration is a later dependent package and is not implemented in this phase.

## Frozen names and public surface

Only these new public diagnostics names are allowed:

```csharp
public interface ISessionObserver
{
    void Observe(in SessionEvent value);
}

public enum SessionEventKind : byte
{
    Step,
    Receive,
    Decode,
    Dispatch,
    Capture,
    Apply,
    Encode,
    Send,
    State,
    Fault,
    Resync
}

public enum SessionEventPhase : byte
{
    Begin,
    End,
    Point
}

public readonly struct SessionEvent
{
    public ulong Id { get; }
    public ulong Step { get; }
    public long Timestamp { get; }
    public long Elapsed { get; }
    public uint Tick { get; }
    public uint PacketSequence { get; }
    public int WireBytes { get; }
    public int DecodedBytes { get; }
    public int Count { get; }
    public ushort Code { get; }
    public ushort Reason { get; }
    public ulong Hash { get; }
    public SessionRole Role { get; }
    public SessionEventKind Kind { get; }
    public SessionEventPhase Phase { get; }
    public SessionState State { get; }
    public SessionError Error { get; }
    public PacketKind Packet { get; }
    public Channel Channel { get; }
    public bool Success { get; }
    public bool Retry { get; }
}

public readonly struct SessionStats
{
    public ulong Steps { get; }
    public ulong ReceivedPackets { get; }
    public ulong SentPackets { get; }
    public ulong ReceivedBytes { get; }
    public ulong SentBytes { get; }
    public ulong DecodedBytes { get; }
    public ulong CommandsQueued { get; }
    public ulong CommandsAccepted { get; }
    public ulong CommandsRejected { get; }
    public ulong SnapshotsCaptured { get; }
    public ulong SnapshotsApplied { get; }
    public ulong Resyncs { get; }
    public ulong SendRetries { get; }
    public ulong Faults { get; }
    public ulong ObserverErrors { get; }
}

public readonly struct TickFingerprint : IEquatable<TickFingerprint>
{
    public uint Tick { get; }
    public ulong Hash { get; }
    public long Bytes { get; }
}

public sealed partial class Session<TWorld>
{
    public Session(SessionConfig config, Schema schema, ITransport transport, ISessionObserver observer);
    public SessionStats Stats { get; }
    public HistoryLookup TryGetFingerprint(uint tick, out TickFingerprint fingerprint);
}

public sealed class NdjsonLog : ISessionObserver, IDisposable
{
    public NdjsonLog(Stream output, int capacity = 4096, uint source = 0, bool leaveOpen = false);
    public int Pending { get; }
    public ulong Dropped { get; }
    public bool Faulted { get; }
    public void Observe(in SessionEvent value);
    public void Flush();
}

public sealed class ReplayTape : IDisposable
{
    public ReplayTape(long byteCapacity);
    public bool IsComplete { get; }
    public bool IsSealed { get; }
    public ulong Dropped { get; }
    public long Bytes { get; }
    public void Seal();
    public void Save(Stream output);
    public static ReplayTape Load(Stream input, long byteCapacity);
}

public sealed class TraceTransport : ITransport, ISteppedTransport
{
    public TraceTransport(ITransport inner, ReplayTape tape);
}

public sealed class ReplayTransport : ITransport, ISteppedTransport
{
    public ReplayTransport(ReplayTape tape);
}
```

Every public type and member has an English XML summary. No `Sample`, `Demo`, `NetworkSession`, `DiagnosticsManager`, facade, service, request DTO, public mutable collection, or public replay record is added.

The existing three-argument Session constructor remains source-compatible and delegates to the observer constructor with null. The observer is caller-owned, may be shared or composed externally, and is never disposed by Session. Null observer is the default low-overhead path.

## Event privacy, timing and codes

`SessionEvent` is unmanaged. It never contains packet payload, command/world bytes, nonce, epoch, peer id, schema id, exception string, file path, or arbitrary user data. `Timestamp` and `Elapsed` use `Stopwatch` ticks; NDJSON converts them to integer nanoseconds. Begin has zero elapsed. End is emitted from `finally` and carries the paired elapsed time even when the operation throws.

The runtime assembly retains one internal full-value `SessionEvent` constructor for package tests and the later profiler adapter tests use real Session events. No public mutable builder or request DTO is added. Golden NDJSON tests construct fixed events through that internal seam, so timing values are normative inputs rather than wall-clock guesses.

`Code` is the bounded operation result selected by `Kind`: `DispatchResult`, `CaptureResult`, `ApplyResult`, transport acceptance zero/one, or zero when not applicable. `Reason` is a bounded wire reason value or zero. `Tick=PacketHeader.NoneTick`, `PacketSequence=0`, and `Packet=(PacketKind)0` are explicit absence sentinels. Channel is meaningful only for Receive, Decode, Encode and Send; NDJSON writes `none` for every other kind and otherwise writes the actual channel.

One Session-local `Id` increases for every attempted observer delivery. Observer failure still consumes its id. Event production never changes protocol or ownership state.

## Required instrumentation seams

Pairs are emitted for `Step`, `Receive`, `Decode`, `Dispatch`, `Capture`, `Apply`, `Encode`, and `Send`. `State`, `Fault`, and `Resync` are point events.

- `Step` covers the complete public Step body and always closes through `finally`, including `BeginStep`, callback, codec and transport exceptions.
- `Receive` covers exactly one `TryReceive` attempt. A successful lease reports wire bytes before ownership cleanup.
- `Decode` covers header read plus framing/staging and reports payload-only `PacketHeader.DecodedPayloadLength` only after successful decode. It excludes the fixed 72-byte header. Receive/send wire bytes are the complete leased packet length including the header.
- `Dispatch` covers each actually visited command. Accepted and authorization-rejected commands increment their separate counters; a later exception preserves prior counts.
- `Capture` wraps `Replicator.Capture`. Only the committed Session Capture increments `SnapshotsCaptured` and publishes its canonical generated hash.
- `Apply` wraps `Replicator.Apply`. Only a committed apply increments `SnapshotsApplied` and publishes the received canonical hash.
- `Encode` wraps every control and transfer framing attempt.
- `Send` wraps every `TrySend`. Sent packet/byte counters advance only after transport acceptance. `Retry=true` means an attempt after the first attempt of the same frozen intent; false and throwing sends never count as sent.
- `State` is emitted after a public state transition. It must not require a risky whole-file state-machine refactor; narrowly instrument the existing authoritative transition sites.
- `Fault` is emitted from the existing single Fault entry point after local fields are coherent. `Resync` has exactly two role-specific semantic entry points: a client emits and increments once only when `QueueResync` creates a new queued request; a server emits and increments once only after it semantically consumes a valid `ResyncRequest`, invalidates an older pending snapshot and sets `NeedsSnapshot=true`. Duplicate client failures that preserve queued or active intent and malformed server packets emit nothing. One completed request/consume round trip therefore increments one client Session and one server Session independently.

Instrumentation calls stay outside lease-transfer boundaries. An observer throwing at Begin, End or Point is caught, increments only `ObserverErrors`, and cannot replace an original Session exception, suppress a later End, change StepResult, state, high-water, retry bytes, transport calls, history, ECS mutation, or disposal.

Stats update independently of whether an observer exists. Reading `Stats` is allocation-free and remains valid after Closed, Faulted and Disposed states. `CommandsQueued` advances only on `EnqueueResult.Queued`.

## Fingerprints

`TryGetFingerprint` checks disposed first. Otherwise it delegates to the owned history lookup while history is available and returns the exact `HistoryLookup` result. Found server records use `GeneratedHash`; found client records use `ReceivedHash`; bytes are the corresponding canonical lease length. Missing, evicted and future results return default fingerprint. If terminal collaborator release has removed history, the result is `HistoryLookup.Missing`.

`TickFingerprint` is a canonical snapshot fingerprint, not a post-apply hash of every ECS allocation or non-replicated component. Equal tick/hash/bytes values are equal.

## Strict NDJSON

`NdjsonLog` owns a fixed SPSC event ring and optionally its output Stream; it performs no I/O and no per-event allocation in `Observe`. The constructor validates a writable stream and positive bounded capacity. One producer may call `Observe`; one consumer may call `Flush`; other concurrency is unsupported.

Overflow uses DropNewest, preserves retained events, increments `Dropped`, and records the first/last dropped event ids. Flush emits retained events in order and one strict gap record after the retained prefix. A gap contains only schema version, source, first id, last id and count.

Event lines are UTF-8 without BOM with `\n`, invariant integer formatting, lowercase stable enum tokens, no whitespace, and this fixed key order:

`v,source,id,step,time_ns,elapsed_ns,role,kind,phase,state,error,packet,channel,tick,packet_sequence,wire_bytes,decoded_bytes,count,code,reason,hash,success,retry`

Schema version is `1`; hash is fixed 16-character lowercase hexadecimal. A manually supplied identical event sequence produces byte-identical output across OS and culture. Session-generated timing naturally differs between runs.

The first Stream write or flush exception is caught and makes the logger terminally `Faulted`. Successfully completed earlier lines stay consumed. The possibly partial current line plus every queued line is conservatively treated as lost: the ring and pending gap are cleared, `Pending` becomes zero, and `Dropped` increases by their count. The partial output is explicitly incomplete and is never retried, so duplicate lines cannot be produced. Later Observe calls perform no allocation, increment `Dropped`, and retain nothing; later Flush calls are no-ops. The exception never escapes.

`Dispose` is idempotent. When not faulted it attempts one Flush; after a fault it never retries output. It closes the stream only when `leaveOpen=false` and catches close failures into the same terminal state. Observe after disposal is ignored and counted as dropped; Flush after disposal is a no-op.

## Trace and replay privacy boundary

Replay is an explicit privacy-sensitive opt-in facility. Unlike ordinary events and NDJSON, a tape contains complete packet bytes and can contain nonces, peer ids, commands and replicated world state. README guidance must require protected storage, bounded retention and explicit deletion.

`ReplayTape` is a bounded single-writer call transcript. Its byte budget charges the fixed encoded record header plus payload, so zero-payload BeginStep and failed receive records are bounded too. It records exact ordered `BeginStep`, `TryReceive` and `TrySend` calls; channel; result; transport state/error observed at the call boundary; and full inbound/outbound packet bytes. It stores owned independent copies.

Tape lifecycle states are Open, Recording, Sealed, DisposeRequested and Disposed. Public operation precedence is disposed/requested first, active claim/borrow second, then ordinary state. `Seal()` transitions an unclaimed Open tape to Sealed and is idempotent when already Sealed; while Recording or borrowed it throws `InvalidOperationException`. Every operation except idempotent Dispose throws `ObjectDisposedException` after DisposeRequested/Disposed.

`TraceTransport` requires an inner `ITransport` that is also `ISteppedTransport`, atomically claims an Open tape as its only writer, owns and disposes the inner transport, never owns the tape, and releases the claim in constructor rollback or Dispose. Trace Dispose uses `finally` so an inner disposal exception still releases the claim and seals the complete/incomplete tape before rethrowing the original exception. It copies outbound bytes before calling the inner transport so true, false, ordinary throw and transfer-then-throw paths remain observable without borrowing invalid memory.

Tape overflow uses DropNewest, marks `IsComplete=false`, increments `Dropped`, and never changes or suppresses the wrapped transport call/result/exception. A wrapped transport exception is recorded as an incomplete terminal trace and the original exception is rethrown. Process-level allocator failure is outside the recoverable guarantee, but finally cleanup remains mandatory.

`ReplayTransport` atomically borrows a sealed complete tape and never owns it. Construction rejects incomplete, unsealed or already borrowed tapes and releases a partial borrow on constructor failure. Dispose is idempotent and releases the borrow. It reproduces recorded state/error, receive results, channels and independently rented inbound leases. BeginStep and outbound channel/bytes must match exactly. Mismatch, truncated call order or use after transcript end faults replay with `TransportError.InvalidPacket` and throws `InvalidOperationException`; it does not consume the caller's send lease.

External tape Dispose is idempotent and never breaks an active transport call. During Recording or replay borrow it transitions to DisposeRequested, makes public tape operations unavailable, and defers record cleanup until the claim/borrow releases. Internal Trace/Replay access remains valid until release. A Trace release after DisposeRequested cleans records instead of exposing a sealed tape; a Replay release performs the deferred cleanup. This freezes the caller obligation to keep a tape alive while still making accidental early disposal memory-safe.

Tape `Save/Load` uses this normative little-endian format. The 40-byte header is:

- bytes `0..7`: ASCII magic `SECSNET1` (`53 45 43 53 4e 45 54 31`);
- `8..9`: format version `1` as `u16`;
- `10..11`: header size `40` as `u16`;
- `12..15`: record count as `u32`;
- `16..23`: record-section byte length as `u64`;
- `24..31`: xxHash64 of the exact record section only;
- `32..35`: flags `1` meaning sealed and complete;
- `36..39`: reserved zero.

Each record is a 24-byte header followed immediately by payload bytes:

- byte `0`: tag (`1=BeginStep`, `2=TrySend`, `3=TryReceive`);
- byte `1`: flags (`bit0=result true`, `bit1=wrapped call threw`, other bits zero);
- byte `2`: channel (`0=ReliableOrdered`, `1=UnreliableSequenced`, `255=none`);
- byte `3`: recorded `TransportState`;
- byte `4`: recorded `TransportError`;
- bytes `5..7`: reserved zero;
- bytes `8..15`: step index as `u64`;
- bytes `16..19`: payload length as `u32`;
- bytes `20..23`: reserved zero;
- following bytes: exact packet bytes, absent for BeginStep and false receive.

Complete persisted tapes never contain the throw flag because a wrapped exception marks the in-memory tape incomplete and Save rejects it; the bit remains reserved for in-memory validation. The byte capacity applies to the complete record section and every payload length is at most `PacketHeader.Size + ProtocolLimits.MaxWirePayloadBytes`.

Save rejects incomplete, unsealed, recording, borrowed or dispose-requested tape and never closes the caller stream. Load validates the header before allocation, requires `recordBytes <= byteCapacity`, derives a bounded record-count minimum of 24 bytes per record, validates every tag/flag/channel/state/error/reserved/length combination, requires exact record-section consumption, rejects a trailing byte, and verifies checksum transactionally. Bad magic/version, truncation, enum/count/length mismatch, budget overflow, trailing bytes or checksum mismatch releases all local owners and produces no tape. A loaded complete tape is Sealed.

## Ownership and allowed files

Implementation owns only:

- `unigame.staticecs.network/Runtime/Diagnostics/**` and paired metas;
- `unigame.staticecs.network/Runtime/SessionDiagnostics.cs` and meta;
- bounded instrumentation edits in `Runtime/Session.cs` and `Runtime/SessionTransfer.cs`;
- `Tests/Editor/DiagnosticsTests.cs`, `ReplayTests.cs` and paired metas;
- package `README.md`.

Protocol files, framing, staging, schema, replication, outbox, dispatcher, history implementation, transport implementation, asmdefs, package manifest, Unity packages/assets, profiler package and Lemmings metadata are forbidden to the implementation worker.

## Acceptance

- Preserve exact Session, protocol, retry, callback exception and ownership behavior with null, recording and throwing observers.
- Prove every Begin/End pair on success, returned failure and exception, plus bounded point events and monotonic ids.
- Prove exact counters for handshake, receive/send bytes, queued/accepted/rejected commands, capture/apply, false/throw retry, resync, fault and observer failure.
- Prove fingerprint server/client equality at tick zero, mismatch, missing, future, eviction and terminal behavior.
- Prove NDJSON golden bytes, invariant culture, LF-only output, field order, timing conversion, overflow gap, privacy and stream failure/disposal.
- Prove TraceTransport exact call/byte capture and unchanged inner behavior for true, false, receive, both channels, close, ordinary throw and transfer-then-throw.
- Prove tape overflow/incomplete behavior, seal/dispose ownership, save/load golden bytes, corruption/budget rejection and no caller stream disposal.
- Prove ReplayTransport complete reproduction, exact step/channel/outbound matching, independent inbound ownership, mismatch/truncation/end faults and tape borrowing.
- Keep base asmdef unchanged, Unity-free and `noEngineReferences: true`; add no forbidden names or dependencies.
- Run warnings-as-errors focused diagnostics/replay tests, the full standalone harness, exact ownership/meta/privacy/no-Sample scans, `git diff --check`, `lemmings check --all`, independent high review, then Unity EditMode after accepted integration.
