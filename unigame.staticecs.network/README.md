# UniGame Static ECS Network

Transport-neutral protocol and replication foundations for deterministic Static ECS sessions.

## Capabilities

- Defines bounded version-one packet framing, canonical payload codecs, stable RFC UUID identifiers, CRC32, xxHash64, and schema hashing.
- Provides AOT-safe typed schema registration with retained entity, record, and command invokers, generation-checked pooled packet ownership, stepped transport and transform contracts, a bounded command outbox, command markers, and bounded tick history.
- Rejects unknown flags and enum values, unsupported transforms, malformed lengths, invalid hashes, reserved fields, and non-canonical ordering before ECS mutation.
- Binds schema-validated command and snapshot stages to the exact schema identity that accepted them.
- Dispatches commands through retained codecs and authorizers, then emits typed accepted or rejected Static ECS events.
- Captures deterministic full snapshots from tagged authority entities and applies them through retained AOT-safe invokers to an exact replica ledger.
- Preflights the complete snapshot, mapped topology, physical occupants, segment kinds, schema records, flags, codecs, and relation targets before any ECS mutation.
- Negotiates schema, tick rate, payload limits, epoch, peer identity, and canonical chunk topology through a deterministic stepped session handshake.

## Usage

```csharp
var schema = new SchemaBuilder<ServerWorld>()
    .EntityKind<PlayerEntity>(new TypeId("3ee9226f-9459-48ef-a572-d567f297a997"))
    .Component<PositionComponent, PositionCodec>(
        new TypeId("f7da29ce-318f-4745-a01c-acf4fbd36c62"),
        version: 1,
        new CodecId("3a77e68f-799f-4425-9a90-2d5ea76b53d0"),
        maxBytes: 12)
    .Freeze();
```

Before `World<ServerWorld>.Initialize()`, register `ReplicatedTag` and every entity, component, tag, link, link-set, and multi-value type used by the schema through `World<ServerWorld>.Types()`. Freezing a schema retains codecs and invokers but does not register Static ECS storage.

Packet payloads are written with `PayloadCodec`, framed with `PacketFraming`, and passed as owned `PacketLease` instances through an `ITransport`. Successful decode returns a disposable `StagedPayload`; consume its pooled typed indexes and canonical payload slices before disposing it. Schema-bound stages expose the validating `SchemaHash`.

`PacketLease` is a value handle with one logical owner. Pass ownership only to APIs that consume it by `ref`; ordinary copies are borrowed aliases and become invalid when ownership transfers or returns. `Span` and aggregate `ReadOnlyMemory<byte>` views are borrowed and must not cross a transfer, disposal, or thread handoff. Call `Copy()` when bytes need independent retention.

```csharp
var packet = PacketLease.Rent(256);
try
{
    packet.SetLength(0);
    transport.TrySend(Channel.ReliableOrdered, ref packet);
}
finally
{
    if (packet.IsValid)
        packet.Dispose();
}
```

Create a `CommandDispatcher<TWorld>` from the same frozen schema and pass it only staged command batches. The dispatcher derives sequence and client tick from the stage, accepts the trusted peer id from the endpoint, and returns an exhaustive `DispatchResult` without transferring stage ownership.

Create one `CommandOutbox<TWorld>` per session epoch after negotiating its decoded command-batch capacity. Enqueue uses the schema's retained typed codec. `TryBuild` returns an owned canonical decoded payload and freezes its sequence prefix until the exact `MarkSent` call. Mark only after a successful reliable transport send; cumulative acknowledgements release sent entries.

```csharp
using var outbox = new CommandOutbox<ServerWorld>(schema, byteCapacity: negotiatedBytes);
var result = outbox.Enqueue(in command, clientTick);
if (result == EnqueueResult.Queued && outbox.TryBuild(out var commands, out var throughSequence))
{
    try
    {
        if (TryFrameAndSendReliably(commands.Span))
            outbox.MarkSent(throughSequence);
    }
    finally
    {
        if (commands.IsValid)
            commands.Dispose();
    }
}
```

When a transport also implements `ISteppedTransport`, call `BeginStep` once with the deterministic logical step index before receiving or sending session packets. `MemoryTransport` implements this barrier as a no-op.

`Session<TWorld>` owns its transport and the negotiated replication collaborators. A client sends `Hello`, the server replies with `Hello` and then `HelloAck`, and the client completes admission with `Ack`. Advance both endpoints with strictly increasing logical step indices until they become established or terminal.

```csharp
var clientConfig = SessionConfig.Client(
    nonce: 0x5E5510UL,
    minTickRate: 20,
    maxTickRate: 60);

using var client = new Session<ClientWorld>(clientConfig, schema, transport);
var work = client.Step(stepIndex);
if (client.State == SessionState.Established)
{
    var epoch = client.Epoch;
    var trustedPeer = client.PeerId;
}
```

Servers use `SessionConfig.Server` with a non-zero epoch, trusted peer id, exact tick rate, and canonical authority chunk map. A rejecting server remains `Handshaking` while a false send retries the same `HelloAck`. After the rejection is queued it enters `Closing` with a null public result. The client must consume and publish that result, then dispose its session; a later server step observes `RemoteClosed`, publishes the same result, and closes. Do not dispose the rejecting server immediately after enqueue because `MemoryTransport` would drain the queued rejection. `Close()` requests an orderly disconnect. A session validates its scope again at send, receive, and established-step seams, so chunk ownership changes become terminal topology failures before replication work proceeds.

Register the negotiated chunks before creating a scope. The wire map always uses role `1` (`AuthoritySelf`); the local scope selects whether those chunks must be `Self` or `Other` owned.

```csharp
var map = new[]
{
    new ChunkMapping { Chunk = 7, Cluster = 2, Role = 1 }
};

using var scope = new ReplicaScope<ServerWorld>(ScopeRole.Authority, map);
using var replicator = new Replicator<ServerWorld>(schema, scope);

if (replicator.Capture(out var snapshot) == CaptureResult.Success)
{
    try
    {
        // Read snapshot.Span here, or transfer ownership to a consuming API by ref.
    }
    finally
    {
        if (snapshot.IsValid)
            snapshot.Dispose();
    }
}
```

On a replica world, stage the decoded `FullSnapshot` with the equivalent replica-world schema and pass it to `Replicator<TWorld>.Apply`. The scope ledger owns only exact entity GIDs created by successful applies. Missing ledger entities are despawned by later complete snapshots; unrelated entities never enter the ledger.

## Configuration

- Runtime limits may lower, but never raise, the constants in `ProtocolLimits`.
- Session wire and decoded limits are negotiated independently. Packet framing is checked against local configured limits before payload decode, and the accepted limits cannot exceed either endpoint's advertisement.
- Session transports must be connected, error-free, and implement `ISteppedTransport`; a successfully constructed session owns and disposes the transport.
- Session step indices are strictly increasing. Each step begins the transport exactly once, receives at most one packet, and sends at most one packet. Failed sends retry the same semantic control packet and sequence.
- Only control packets are accepted during the handshake. Established sessions reserve gameplay packet kinds for later orchestration and do not capture snapshots, apply replicas, or dispatch commands automatically.
- Version one supplies deterministic framing, not security. Nonces, epoch, CRC32, and xxHash do not authenticate peers, provide confidentiality, or prevent replay, and the client nonce is not echoed by the wire layout. Use a dedicated authenticated and integrity-protected transport across an untrusted boundary, and generate non-zero server nonce and epoch values that are not reused across live or restarted sessions.
- Packet ownership handoffs must be serialized; the handle does not permit concurrent mutation through borrowed aliases.
- Version one accepts only `NoOpTransform` with transform id zero.
- Schema values, markers, and commands must be unmanaged. Links and multi-value registrations are capped at 32,768 elements.
- `ReplicatedTag` is control state and cannot be registered as an ordinary schema record.
- Authority capture includes only `ReplicatedTag` entities in the exact mapped chunks. Every relation target must appear in the same snapshot.
- Replica chunks must be empty when `ReplicaScope<TWorld>` is created. Scope construction and replication never register, free, load, unload, or remap chunks.
- Version one preserves disabled entities and ordinary disableable components. FFS tag storage does not represent a disabled tag state; disabled links, link sets, and multi-components are rejected as `DisabledUnsupported`.
- Apply validates the full snapshot before mutation. Typed lifecycle hooks and user codecs run directly; exceptions propagate, and no rollback guarantee is made after mutation starts.
- Explicitly register both `CommandAcceptedEvent<T>` and `CommandRejectedEvent<T>` closed generic event types before initializing the world. `CommandDispatcher<TWorld>` construction rejects an uninitialized world or a missing result type. `ConfigurationError` remains a defensive result if world registration drifts after construction; a registered result event without a receiver returns `NoReceiver`.
- Command outboxes accept capacities from 36 bytes through `MaxWirePayloadBytes`. A codec always receives its complete registered command bound, so `CodecFailed`, standalone `TooLarge`, and current-capacity `Full` remain distinct outcomes.
- See the repository [Static ECS knowledge base](../../../docs/knowledge/static-ecs/) for world and marker lifecycle.
