# UniGame Static ECS Network

Transport-neutral protocol and replication foundations for deterministic Static ECS sessions.

## Capabilities

- Defines bounded version-one packet framing, canonical payload codecs, stable RFC UUID identifiers, CRC32, xxHash64, and schema hashing.
- Provides AOT-safe typed schema registration, pooled packet ownership, transport and transform contracts, command markers, and bounded tick history.
- Rejects unknown flags and enum values, unsupported transforms, malformed lengths, invalid hashes, reserved fields, and non-canonical ordering before ECS mutation.

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

Packet payloads are written with `PayloadCodec`, framed with `PacketFraming`, and passed as owned `PacketLease` instances through an `ITransport`. Successful decode returns a disposable `StagedPayload`; consume its pooled typed indexes and canonical payload slices before disposing it. Commands are decoded and authorized through `Schema.TryAuthorizeCommand` using a trusted endpoint `CommandContext`.

## Configuration

- Runtime limits may lower, but never raise, the constants in `ProtocolLimits`.
- Version one accepts only `NoOpTransform` with transform id zero.
- Register closed generic command events in the Unity-facing consumer assembly.
- See the repository [Static ECS knowledge base](../../../docs/knowledge/static-ecs/) for world and marker lifecycle.
