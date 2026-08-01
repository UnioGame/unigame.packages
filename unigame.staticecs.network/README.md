# UniGame Static ECS Network

Transport-neutral protocol and replication foundations for deterministic Static ECS sessions.

## Capabilities

- Defines bounded version-one packet framing, canonical payload codecs, stable RFC UUID identifiers, CRC32, xxHash64, and schema hashing.
- Provides AOT-safe typed schema registration with retained entity, record, and command invokers, generation-checked pooled packet ownership, transport and transform contracts, command markers, and bounded tick history.
- Rejects unknown flags and enum values, unsupported transforms, malformed lengths, invalid hashes, reserved fields, and non-canonical ordering before ECS mutation.
- Binds schema-validated command and snapshot stages to the exact schema identity that accepted them.
- Dispatches commands through retained codecs and authorizers, then emits typed accepted or rejected Static ECS events.
- Captures deterministic full snapshots from tagged authority entities and applies them through retained AOT-safe invokers to an exact replica ledger.
- Preflights the complete snapshot, mapped topology, physical occupants, segment kinds, schema records, flags, codecs, and relation targets before any ECS mutation.

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
    // The caller owns snapshot and must transfer it or dispose it.
}
```

On a replica world, stage the decoded `FullSnapshot` with the equivalent replica-world schema and pass it to `Replicator<TWorld>.Apply`. The scope ledger owns only exact entity GIDs created by successful applies. Missing ledger entities are despawned by later complete snapshots; unrelated entities never enter the ledger.

## Configuration

- Runtime limits may lower, but never raise, the constants in `ProtocolLimits`.
- Packet ownership handoffs must be serialized; the handle does not permit concurrent mutation through borrowed aliases.
- Version one accepts only `NoOpTransform` with transform id zero.
- Schema values, markers, and commands must be unmanaged. Links and multi-value registrations are capped at 32,768 elements.
- `ReplicatedTag` is control state and cannot be registered as an ordinary schema record.
- Authority capture includes only `ReplicatedTag` entities in the exact mapped chunks. Every relation target must appear in the same snapshot.
- Replica chunks must be empty when `ReplicaScope<TWorld>` is created. Scope construction and replication never register, free, load, unload, or remap chunks.
- Version one preserves disabled entities and ordinary disableable components. Disabled tags, links, link sets, and multi-components are rejected as `DisabledUnsupported`.
- Apply validates the full snapshot before mutation. Typed lifecycle hooks and user codecs run directly; exceptions propagate, and no rollback guarantee is made after mutation starts.
- Explicitly register both `CommandAcceptedEvent<T>` and `CommandRejectedEvent<T>` closed generic event types before initializing the world. A missing type returns `ConfigurationError`; a registered result event without a receiver returns `NoReceiver`.
- See the repository [Static ECS knowledge base](../../../docs/knowledge/static-ecs/) for world and marker lifecycle.
