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

`SessionError` is local runtime state and is never serialized. `DisconnectReason` remains the wire close reason. Rejected admission produces a non-accepted `Result`, `Closed`, `Error.None`, and no disconnect reason. A successful handshake produces `Result.Accepted`.

The package has no dependency on the Unity `Main` world, so no Main-default alias is added. Do not add `SessionWorld`, `Sample`, or longer facade synonyms.

## Configuration and ownership

- Client nonce, server nonce, server epoch, and server peer identifier are non-zero.
- Tick values are non-zero. The client range is ordered. The server exposes its exact tick as an equal minimum and maximum.
- Receive limits are each between 24 bytes and the matching protocol maximum. Transform zero means encoded and decoded handshake sizes are equal.
- Server mappings contain 1 through 4096 unique chunks, role one only, and valid non-zero registered cluster/chunk identities. `SessionConfig` defensively copies and sorts them by chunk for canonical transmission.
- Construction validates nulls first, schema world identity, `ISteppedTransport`, connected/no-error transport, initialized world, registered `ReplicatedTag`, then server authority topology when applicable.
- A successful constructor exclusively owns the transport. A failed constructor does not dispose the caller's transport.
- Server construction creates and validates a private `ReplicaScope<TWorld>` with `ScopeRole.Authority`, then a private `Replicator<TWorld>`. The authority scope requires current Self ownership.
- Client construction defers its replica scope until the accepted map arrives. Before the final Ack, it rejects non-strict map ordering, constructs `ScopeRole.Replica`, validates Other ownership and empty mapped chunks, calls `ValidateCurrent`, then creates the replicator.
- Scope or replicator construction failure is cleaned locally. Handshake code never calls `Capture` or `Apply` and never builds or dispatches commands.

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

The server evaluates admission after Client Hello but always sends Server Hello before HelloAck. Admission precedence is header/framing/state, schema, tick, limits/capabilities, then accepted. Accepted HelloAck uses the configured epoch, tick, peer id, server nonce, and canonical map. Rejected HelloAck uses epoch zero, a non-accepted result, zero tick, zero peer id, the same server nonce sent in Server Hello, and an empty map.

The accepted HelloAck payload length `20 + 8 * chunkCount` and Server Hello payload length 24 must fit both client receive limits. Otherwise the server returns `LimitsRejected`. A rejection payload is 20 bytes and therefore fits every valid client configuration. The client validates the server limits and nonce learned from Server Hello, then binds HelloAck to them. It waits for explicit rejected HelloAck even when Server Hello reveals a schema mismatch, but accepts no final Ack path unless schema, tick, nonce, epoch, limits, and map all agree.

Capabilities are exactly zero. Non-zero capabilities are `LimitsRejected` on the server or `SessionError.Limits` on the client. Protocol version mismatch is reserved because invalid versions fail fixed-header decoding before a Hello can be exposed; such input faults without a reply.

## Header and sequence matrix

Every handshake or close packet uses transform zero, exact payload framing/hash, `ReliableOrdered` channel and flag, `ServerTick=NoneTick`, `BaselineTick=NoneTick`, `AcknowledgedSnapshotTick=NoneTick`, `AcknowledgedCommandSequence=0`, and the local schema hash. Session compares schema on every packet even where `PacketFraming` does not require it.

Maintain independent reliable transmit, reliable receive, unreliable transmit, and unreliable receive high-water values. Core exercises the reliable domains but retains all four for transfer. Reliable receive requires exactly previous plus one. Unreliable receive later accepts only greater-than-high-water. Every Session packet sequence is non-zero. No domain wraps.

Handshake epochs follow the table above. Established and closing packets require the exact negotiated epoch. A wrong established epoch faults as `SessionError.Epoch` with `DisconnectReason.UnexpectedEpoch`; other kind, direction, channel, state, framing, duplicate, or gap violations fault as `SessionError.Protocol` with `DisconnectReason.ProtocolViolation`.

Transfer kinds (`CommandBatch`, `FullSnapshot`, established gameplay `Ack`, and `ResyncRequest`) are reserved in Core and fault if received. Their detailed matrix is frozen by the following transfer phase.

## Step and retry

For each non-terminal `Step(stepIndex)`:

1. reject a non-increasing index without transport activity;
2. call `ISteppedTransport.BeginStep(stepIndex)` exactly once;
3. map a pre-existing terminal transport state;
4. receive and fully dispose or transfer at most one inbound packet;
5. attempt at most one outbound packet;
6. map a post-send terminal transport state;
7. return flags for actual receive, successful send, and public state change.

`Step` on `Disposed` throws `ObjectDisposedException`. `Step` on `Closed` or `Faulted` returns `None` and performs no transport activity. The strictly increasing rule applies to every non-terminal call, including calls that become terminal during the step.

Outbound state is semantic intent, never a retained framed lease. `TrySend` consumes every valid lease even when it returns false. A false reliable send while the transport remains connected retains the same intent and uncommitted sequence; the next Step rebuilds byte-identical framing and retries. State and sequence advance only after success. Exhaustion faults before build or transport activity.

## Close and terminal mapping

`Close` during handshake enters local `Closed` with `Reason.Requested` and sends nothing because no epoch is mutually accepted. `Close` during `Established` enters `Closing` and schedules exactly one reliable `Disconnect(Requested)`. Repeated close is a no-op.

Receiving Requested without a locally sent request schedules exactly one echo. After that echo is successfully queued, the echo sender enters `Closed/Requested`; the request initiator enters `Closed/Requested` when it receives the peer request. Simultaneous requests are orderly. A connected false send retries the same close intent and sequence. A remote transport close during a valid sent-or-received Requested lifecycle maps to `Closed/Requested`; otherwise it maps to `Faulted/TransportClosed`.

Peer `ServerShutdown` closes cleanly with that reason. Peer protocol, schema, limits, epoch, or sequence disconnect reasons enter `Faulted` with the corresponding local error while retaining the received wire reason.

Transport terminal mapping is fixed:

| Transport state/error | Session result |
|---|---|
| `Faulted/QueueOverflow` | `Faulted`, `SessionError.Limits`, `LimitsExceeded` |
| `Faulted/InvalidPacket` | `Faulted`, `SessionError.Protocol`, `ProtocolViolation` |
| unexpected `Closed/RemoteClosed` | `Faulted`, `SessionError.Transport`, `TransportClosed` |
| `Closed/RemoteClosed` during Requested lifecycle | `Closed`, `Error.None`, `Requested` |
| externally observed `Disposed/Disposed` | `Faulted`, `SessionError.Transport`, `TransportClosed` |

Entering `Closed` or `Faulted` releases replication collaborators and pending local leases but does not automatically dispose the transport. This prevents `MemoryTransport.Dispose` from draining a just-queued reply. Only `Session.Dispose` disposes the owned transport; it is immediate, idempotent, sends nothing, and ends in `Disposed`.

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
- Mutate each handshake header field, channel, direction, sequence, epoch, payload, schema, state, and transport lifecycle independently.
- Prove exact BeginStep count/order, monotonic indices, one receive, one send attempt, connected false-send byte-identical retry, and sequence exhaustion.
- Prove initiator, peer, simultaneous, repeated, handshake, rejection, remote-close, shutdown, fault, and immediate disposal paths with complete lease/scope/transport ownership.
- Prove Core never captures, applies, builds, or dispatches gameplay data.
- Run focused Session tests, warnings-as-errors compilation, then the complete package and Unity EditMode suites.
