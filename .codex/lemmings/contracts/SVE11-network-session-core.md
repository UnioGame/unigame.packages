# SVE11 Session Core

## Scope and precedence

This contract freezes the deterministic connection state machine that owns one stepped transport and one replication scope. It implements negotiation, packet semantics, retry, orderly close, and terminal mapping. It deliberately does not capture or apply snapshots, build or dispatch commands, acknowledge gameplay data, log diagnostics, or depend on Unity.

This contract takes precedence over earlier planning notes. The accepted handshake is exactly `Client Hello -> Server Hello -> Server HelloAck -> Client Ack`. There is no final server Ack. Both Hello payloads advertise endpoint receive limits.

## Public API

```csharp
public enum SessionRole : byte
{
    Client = 0,
    Server = 1
}

public enum SessionState : byte
{
    Handshaking = 0,
    Established = 1,
    Closing = 2,
    Closed = 3,
    Faulted = 4,
    Disposed = 5
}

public enum SessionError : byte
{
    None = 0,
    Protocol = 1,
    Schema = 2,
    Limits = 3,
    Topology = 4,
    Epoch = 5,
    Sequence = 6,
    Transport = 7
}

[Flags]
public enum StepResult : byte
{
    None = 0,
    Received = 1,
    Sent = 2,
    StateChanged = 4
}

public sealed class SessionConfig
{
    public static SessionConfig Client(
        ulong nonce,
        ushort minTickRate,
        ushort maxTickRate,
        uint maxWireBytes = ProtocolLimits.MaxWirePayloadBytes,
        uint maxDecodedBytes = ProtocolLimits.MaxDecodedPayloadBytes);

    public static SessionConfig Server(
        uint epoch,
        uint peerId,
        ulong nonce,
        ushort tickRate,
        ReadOnlySpan<ChunkMapping> chunks,
        uint maxWireBytes = ProtocolLimits.MaxWirePayloadBytes,
        uint maxDecodedBytes = ProtocolLimits.MaxDecodedPayloadBytes);

    public SessionRole Role { get; }
}

public sealed partial class Session<TWorld> : IDisposable
    where TWorld : struct, IWorldType
{
    public Session(SessionConfig config, Schema schema, ITransport transport);

    public SessionRole Role { get; }
    public SessionState State { get; }
    public SessionError Error { get; }
    public ConnectResult? Result { get; }
    public DisconnectReason? Reason { get; }
    public uint Epoch { get; }
    public uint PeerId { get; }
    public ushort TickRate { get; }

    public StepResult Step(ulong stepIndex);
    public void Close();
    public void Dispose();
}
```

`SessionError` is local runtime state and is never serialized. `DisconnectReason` is a wire-compatible terminal reason: it may be received, sent, or synthesized locally for a matching transport/session failure. Rejected admission produces a non-accepted `Result`, `Error.None`, and no disconnect reason. A successful handshake publishes `Result.Accepted` only at establishment.

The package has no dependency on the Unity `Main` world, so no Main-default alias is added. Do not add `SessionWorld`, `Sample`, or longer facade synonyms.

## Configuration and ownership

- Client nonce, server nonce, server epoch, and server peer identifier are non-zero.
- Tick values are non-zero. The client range is ordered. The server exposes its exact tick as an equal minimum and maximum.
- Receive limits are payload limits excluding the 72-byte packet header. Each is between 24 bytes and the matching protocol maximum. Transform zero means encoded and decoded handshake sizes are equal.
- Server mappings contain 1 through 4096 unique chunks and role one only. Chunk and cluster zero are valid when registered. Validation requires registered chunk/cluster identities, matching cluster, and correct ownership. `SessionConfig` defensively copies and sorts mappings by chunk for canonical transmission.
- Construction validates nulls first, schema world identity, `ISteppedTransport`, connected/no-error transport, initialized world, registered `ReplicatedTag`, then server authority topology when applicable.
- Null config, schema, or transport throws `ArgumentNullException`. Invalid factory scalars or limits throw `ArgumentOutOfRangeException`; duplicate/invalid map data throws `ArgumentException`. Wrong-world schema, terminal transport, uninitialized world, missing tag, or invalid registered topology throws `InvalidOperationException`. A transport that does not implement `ISteppedTransport` throws `ArgumentException` for `transport`.
- A successful constructor exclusively owns the transport. A failed constructor does not dispose the caller's transport.
- Server construction creates and validates a private `ReplicaScope<TWorld>` with `ScopeRole.Authority`, then a private `Replicator<TWorld>`. The authority scope requires current Self ownership.
- Client construction defers its replica scope until the accepted map arrives. Before the final Ack, it rejects non-strict map ordering, constructs `ScopeRole.Replica`, validates Other ownership and empty mapped chunks, calls `ValidateCurrent`, then creates the replicator.
- Scope or replicator construction failure is cleaned locally. Narrow internal `HasScope` and `HasReplicator` test probes are allowed; reflection and public diagnostic seams are not. Handshake code never calls `Capture` or `Apply` and never builds or dispatches commands.
- Authority scope is revalidated immediately before building or retrying an Accepted HelloAck and before accepting the final Ack. Client scope is revalidated immediately before every final-Ack build/retry. After establishment, every non-terminal Step revalidates current world/scope after the transport terminal check and before receive. Lifecycle or topology drift faults as `Topology`, with `Result` unchanged, `Reason=null`, no synthesized packet, and no ECS mutation.

## Exact handshake

The accepted three-flight, four-packet order is:

| Packet | Reliable sequence | Epoch |
|---|---:|---:|
| Client to server `Hello` | client 1 | 0 |
| Server to client `Hello` | server 1 | 0 |
| Server to client `HelloAck(Accepted)` | server 2 | assigned non-zero epoch |
| Client to server final empty `Ack` | client 2 | assigned non-zero epoch |

Client internal progression is `SendHello`, `AwaitServerHello`, `AwaitHelloAck`, `SendFinalAck`, `Established`. Server progression is `AwaitClientHello`, `SendServerHello`, `SendHelloAck`, `AwaitFinalAck`, `Established`.

The client becomes established only after its final Ack is successfully queued. The server becomes established only after receiving that Ack. With a capacity-one client-first pair pump, establishment completes in three shared pump iterations. The server must not attempt HelloAck in the same Step that successfully sends Server Hello.

Client Hello carries the client nonce, requested tick range, client receive limits, zero capabilities, and client schema hash. Server Hello carries the server nonce, `[tickRate, tickRate]`, server receive limits, zero capabilities, and server schema hash.

The server evaluates admission after Client Hello but always sends Server Hello before HelloAck. Admission precedence is header/framing/state, schema, tick, limits/capabilities, then accepted. It may emit only `Accepted`, `SchemaMismatch`, `TickRateUnsupported`, or `LimitsRejected`. `ProtocolVersionMismatch` cannot be emitted in v1 because fixed-header decoding fails first. `ChunkMapRejected` is reserved for a local client topology outcome. Accepted HelloAck uses the configured epoch, tick, peer id, server nonce, and canonical map. Rejected HelloAck uses epoch zero, zero tick, zero peer id, the same server nonce sent in Server Hello, and an empty map.

The accepted HelloAck payload length `20 + 8 * chunkCount` and Server Hello payload length 24 must fit both client receive limits. Otherwise the server returns `LimitsRejected`. A rejection payload is 20 bytes and therefore fits every valid client configuration. The client validates the server limits and nonce learned from Server Hello, then binds HelloAck to them. It waits for explicit rejected HelloAck even when Server Hello reveals a schema mismatch, but accepts no final Ack path unless schema, tick, nonce, epoch, limits, and map all agree.

`Result` is null throughout an in-progress accepted handshake. Client publishes `Accepted` after its final Ack is successfully queued; server publishes it after receiving and validating that Ack. A connected-false send preserves intent, sequence, state, and null Result.

For rejection, client publishes the received result and enters Closed when it consumes the canonical rejected HelloAck. The server retries the same rejected HelloAck until `TrySend` succeeds, then remains Closing with an internal rejection-sent marker and null public Result. Client ownership must then dispose its terminal Session; server observes `RemoteClosed`, publishes the rejection result, and enters Closed. This is the safe MemoryTransport delivery barrier; disposing the rejecting server immediately after queueing would drain the rejection.

After an Accepted HelloAck, a canonical strictly ordered map that conflicts with local registered topology yields `Faulted/Topology`, `Result=ChunkMapRejected`, and `Reason=null`. A duplicate, unsorted, invalid-role, empty, or otherwise non-canonical wire map yields `Faulted/Protocol`, `Result=ChunkMapRejected`, and `Reason=ProtocolViolation`. Both paths release partial collaborators, send no Ack, and preserve ECS state.

Capabilities are exactly zero. Non-zero capabilities are `LimitsRejected` on the server or `SessionError.Limits` on the client. Protocol version mismatch is reserved because invalid versions fail fixed-header decoding before a Hello can be exposed; such input faults without a reply.

Client validates rejection coherence with Server Hello and its own request. `SchemaMismatch` requires unequal hashes, and `TickRateUnsupported` requires the advertised exact server tick outside the client range. A canonical `LimitsRejected` is accepted whenever no higher-priority observable schema or tick cause exists: it may reflect the server's private canonical-map payload size, which is intentionally omitted from a rejected HelloAck and cannot be proven by the client. An unsupported result value, wrong precedence, non-zero rejected scalar, non-empty rejected map, or nonce mismatch is `Protocol/ProtocolViolation` and leaves `Result` null.

## Header, limits, and sequence matrix

Every handshake or close packet uses transform zero, exact payload framing/hash, `ReliableOrdered` channel and flag, `ServerTick=NoneTick`, `BaselineTick=NoneTick`, `AcknowledgedSnapshotTick=NoneTick`, `AcknowledgedCommandSequence=0`. Before `PacketFraming.TryDecode`, Session must perform `PacketHeader.TryRead`, exact packet-length validation, and checks of header wire and decoded payload lengths against local configured receive limits. A bound failure faults as `Limits/LimitsExceeded` without renting the attacker-declared decoded size. Other fixed-header or exact-length failures fault as `Protocol/ProtocolViolation`.

Schema binding is phase-specific. Client Hello carries the client schema and server stores it for admission rather than faulting on mismatch. Server Hello carries the server schema and client stores it even when it differs locally so an explicit SchemaMismatch rejection can arrive. HelloAck header schema must equal the stored Server Hello schema. A rejected `SchemaMismatch` is coherent only when that stored hash differs from the client schema. Accepted requires equality with the client schema. Final Ack uses and requires the negotiated server schema. Close packets require the negotiated schema after establishment.

Handshake semantic mutations are classified before state advancement:

| Mutation | Server behavior | Client behavior |
|---|---|---|
| Client Hello nonce zero or invalid non-limit tick shape | `Faulted/Protocol/ProtocolViolation` | not applicable |
| Client Hello advertised receive limit below 24 | continue two-Hello flow, then `LimitsRejected` | not applicable |
| Server Hello nonce zero, unequal/non-zero tick shape, or invalid exact tick | not applicable | `Faulted/Protocol/ProtocolViolation` |
| Server Hello advertised receive limit below 24 or non-zero capabilities | not applicable | `Faulted/Limits/LimitsExceeded` |
| Globally out-of-range Hello values rejected by PayloadCodec/framing | `Faulted/Protocol/ProtocolViolation` | `Faulted/Protocol/ProtocolViolation` |
| Accepted HelloAck with zero epoch or peer id, wrong nonce/tick, non-empty rule violation, or wrong common fields | not applicable | `Faulted/Protocol/ProtocolViolation`, Result unchanged |
| Rejected HelloAck with non-zero epoch or any non-canonical rejected scalar/map | not applicable | `Faulted/Protocol/ProtocolViolation`, Result unchanged |
| Final Ack with wrong negotiated schema | `Faulted/Schema/SchemaMismatch` | not applicable |
| Final Ack with wrong negotiated epoch | `Faulted/Epoch/UnexpectedEpoch` | not applicable |
| Final Ack with non-empty payload or wrong common fields | `Faulted/Protocol/ProtocolViolation` | not applicable |

Here `State/Error/Reason` shorthand leaves Result unchanged unless the explicit admission rules above say otherwise. All semantic failures consume and dispose the received lease and do not commit the next handshake state.

Maintain independent reliable transmit, reliable receive, unreliable transmit, and unreliable receive high-water values. Core exercises the reliable domains but retains all four for transfer. Reliable receive requires exactly previous plus one. Unreliable receive later accepts only greater-than-high-water. Every Session packet sequence is non-zero. No domain wraps.

Handshake epochs follow the table above. Established and closing packets require the exact negotiated epoch. A wrong established epoch faults as `SessionError.Epoch` with `DisconnectReason.UnexpectedEpoch`; other kind, direction, channel, state, framing, duplicate, or gap violations fault as `SessionError.Protocol` with `DisconnectReason.ProtocolViolation`.

Transfer kinds (`CommandBatch`, `FullSnapshot`, established gameplay `Ack`, and `ResyncRequest`) are reserved in Core and fault if received. Their detailed matrix is frozen by the following transfer phase.

## Step and retry

For each non-terminal `Step(stepIndex)`:

1. accept any first index, including zero; later non-increasing values throw `ArgumentOutOfRangeException(nameof(stepIndex))` without transport activity;
2. call `ISteppedTransport.BeginStep(stepIndex)` exactly once;
3. map a pre-existing terminal transport state;
4. receive and fully dispose or transfer at most one inbound packet;
5. attempt at most one outbound packet;
6. map a post-send terminal transport state;
7. return flags for actual receive, successful send, and public state change.

`Received` is set whenever `TryReceive` succeeds, even if semantic validation then rejects the packet. `Sent` is set only when `TrySend` returns true. `StateChanged` is set only when the public `SessionState` changes, not for internal handshake-stage or property changes. `Step` on `Disposed` throws `ObjectDisposedException`. `Step` on `Closed` or `Faulted` returns `None` and performs no transport activity. The strictly increasing rule applies to every non-terminal call, including calls that become terminal during the step.

Transport abstraction exceptions propagate unchanged. The step index becomes consumed immediately before invoking `BeginStep`, so a throw from `BeginStep`, `TryReceive`, or `TrySend` cannot reuse that index. `finally` cleanup disposes any locally visible received, framed, or staged owner. A throwing send does not commit transmit sequence, successful-send state, or outbound intent; the semantic intent remains retryable on the next higher step. State already committed by a successfully processed inbound packet is not rolled back. No throwing Step returns a synthetic `StepResult` or rewrites the transport exception into Session state.

Outbound state is semantic intent, never a retained framed lease. `TrySend` consumes every valid lease even when it returns false. A false reliable send while the transport remains connected retains the same intent and uncommitted sequence; the next Step rebuilds byte-identical framing and retries. State and sequence advance only after success. Exhaustion is checked after mandatory BeginStep, terminal mapping, scope validation, and receive, but before Rent, encode, or TrySend.

## Close and terminal mapping

`Close` during handshake enters local `Closed` with `Reason.Requested` and sends nothing because no epoch is mutually accepted. `Close` during `Established` enters `Closing` and schedules exactly one reliable `Disconnect(Requested)`. `Close` on `Closing`, `Closed`, `Faulted`, or `Disposed` is a no-op.

Receiving Requested without a locally sent request schedules exactly one echo. After that echo is successfully queued, the echo sender enters `Closed/Requested`; the request initiator enters `Closed/Requested` when it receives the peer request. Simultaneous requests are orderly. A connected false send retries the same close intent and sequence. A remote transport close during a valid sent-or-received Requested lifecycle maps to `Closed/Requested`; otherwise it maps to `Faulted/TransportClosed`.

`ServerShutdown` is clean only when received by the client from the server. The same reason in the opposite direction is `Protocol/ProtocolViolation`. Peer protocol, schema, limits, epoch, sequence, and transport disconnect reasons enter Faulted with the corresponding local error while retaining the received wire reason. Locally detected protocol, schema, limits, topology, epoch, or sequence faults do not synthesize a Disconnect packet in Core.

Terminal mapping is fixed. Unless stated otherwise, `Result` is unchanged:

| Cause | State | Error | Result | Reason |
|---|---|---|---|---|
| malformed framing, kind, direction, state, duplicate, or reliable gap | `Faulted` | `Protocol` | unchanged | `ProtocolViolation` |
| header exceeds local payload limits before decode | `Faulted` | `Limits` | unchanged | `LimitsExceeded` |
| HelloAck schema differs from stored Server Hello schema, or rejection fields are incoherent | `Faulted` | `Protocol` | unchanged | `ProtocolViolation` |
| Accepted while stored server schema differs locally | `Faulted` | `Schema` | `SchemaMismatch` | `SchemaMismatch` |
| non-canonical accepted wire map | `Faulted` | `Protocol` | `ChunkMapRejected` | `ProtocolViolation` |
| canonical map or later scope/world lifecycle conflict | `Faulted` | `Topology` | `ChunkMapRejected` during client admission, otherwise unchanged | null |
| established epoch mismatch | `Faulted` | `Epoch` | unchanged | `UnexpectedEpoch` |
| transmit sequence exhaustion | `Faulted` | `Sequence` | unchanged | `SequenceExhausted` |
| transport `Faulted/QueueOverflow` | `Faulted` | `Limits` | unchanged | `LimitsExceeded` |
| transport `Faulted/InvalidPacket` | `Faulted` | `Protocol` | unchanged | `ProtocolViolation` |
| unexpected transport `Closed/RemoteClosed` | `Faulted` | `Transport` | unchanged | `TransportClosed` |
| transport `Closed/RemoteClosed` during Requested lifecycle | `Closed` | `None` | unchanged | `Requested` |
| transport `Closed/RemoteClosed` after server sent rejection | `Closed` | `None` | stored rejection | null |
| externally observed transport `Disposed/Disposed` | `Faulted` | `Transport` | unchanged | `TransportClosed` |
| received `Disconnect(TransportClosed)` | `Faulted` | `Transport` | unchanged | `TransportClosed` |
| client receives server `Disconnect(ServerShutdown)` | `Closed` | `None` | unchanged | `ServerShutdown` |

Entering `Closed` or `Faulted` releases replication collaborators and pending local leases but does not automatically dispose the transport. This prevents `MemoryTransport.Dispose` from draining a just-queued reply. Only `Session.Dispose` disposes the owned transport; it is immediate, idempotent, sends nothing, and ends in `Disposed`.

## Security boundary

Version one provides deterministic framing, not security. Nonces, epoch, CRC32, and xxHash do not authenticate a peer, protect confidentiality, or prevent replay; the client nonce is not echoed by the current wire layout. Session Core therefore requires a dedicated authenticated and integrity-protected transport boundary when used outside the in-process mock. The caller must generate non-zero server nonce and epoch values that are not reused across live or restarted sessions. No code or documentation may claim that this handshake alone is secure on an untrusted transport.

## Files and boundaries

Add:

- `Runtime/SessionTypes.cs` and `.meta`
- `Runtime/SessionProtocol.cs` and `.meta`
- `Runtime/Session.cs` and `.meta`
- `Tests/Editor/SessionTests.cs` and `.meta`

Modify only `README.md` outside those additions. No protocol layout, payload codec, framing, staging, replication, existing test, asmdef, manifest, Unity package, or Lemmings worker metadata changes.

`SessionProtocol.cs` contains only internal header rules, sequence domains, pending-control encoding, and transport-result mapping. Test transports and the pair pump remain nested in `SessionTests.cs`.

## Validation

- Freeze every public enum value and API signature.
- Prove config bounds, defensive canonical map copy, null/world/transport ordering, server scope preflight, and constructor ownership.
- Prove exact four packets, per-endpoint sequences `1,2`, three capacity-one pump iterations, and no two server sends in one Step.
- Prove both Hello limit advertisements, asymmetric limit rejection, explicit schema rejection after Server Hello, nonce binding, accepted/rejected scalar and epoch rules, and canonical map rejection.
- Prove client replica scope and replicator exist before final Ack, invalid or occupied topology prevents Ack, and failure causes no ECS mutation.
- Prove authority/client lifecycle drift at every acceptance, retry, establishment, and established-Step seam using the internal collaborator probes without reflection.
- Mutate each handshake header field, channel, direction, sequence, epoch, payload, schema, state, and transport lifecycle independently.
- Prove payload limits are rejected from the header before decoded-buffer allocation, including configured-minimum endpoints and globally valid attacker-declared lengths.
- Prove exact BeginStep count/order, monotonic indices, one receive, one send attempt, connected false-send byte-identical retry, and sequence exhaustion.
- Prove the complete terminal table, Result publication timing, safe rejection delivery barrier, initiator, peer, simultaneous, repeated, handshake, remote-close, shutdown, fault, and immediate disposal paths with complete lease/scope/transport ownership.
- Prove Core never captures, applies, builds, or dispatches gameplay data.
- Run focused Session tests, warnings-as-errors compilation, then the complete package and Unity EditMode suites.
