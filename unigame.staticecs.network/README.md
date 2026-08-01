# UniGame Static ECS Network

Transport-neutral protocol and replication foundations for deterministic Static ECS sessions.

## Capabilities

- Defines bounded version-one packet framing, canonical payload codecs, stable RFC UUID identifiers, CRC32, xxHash64, and schema hashing.
- Provides AOT-safe typed schema registration with retained entity, record, and command invokers, generation-checked pooled packet ownership, transport and transform contracts, command markers, and bounded tick history.
- Rejects unknown flags and enum values, unsupported transforms, malformed lengths, invalid hashes, reserved fields, and non-canonical ordering before ECS mutation.
- Binds schema-validated command and snapshot stages to the exact schema identity that accepted them.
- Dispatches commands through retained codecs and authorizers, then emits typed accepted or rejected Static ECS events.

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

## Configuration

- Runtime limits may lower, but never raise, the constants in `ProtocolLimits`.
- Packet ownership handoffs must be serialized; the handle does not permit concurrent mutation through borrowed aliases.
- Version one accepts only `NoOpTransform` with transform id zero.
- Schema values, markers, and commands must be unmanaged. Links and multi-value registrations are capped at 32,768 elements.
- `ReplicatedTag` is control state and cannot be registered as an ordinary schema record.
- Explicitly register both `CommandAcceptedEvent<T>` and `CommandRejectedEvent<T>` closed generic event types before initializing the world. A missing type returns `ConfigurationError`; a registered result event without a receiver returns `NoReceiver`.
- See the repository [Static ECS knowledge base](../../../docs/knowledge/static-ecs/) for world and marker lifecycle.
