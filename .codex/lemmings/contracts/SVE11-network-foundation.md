# SVE11 Network Foundation Contract

## Boundary

Create `unigame.staticecs.network`, package id `com.unigame.staticecs.network`, namespace root `UniGame.StaticEcs.Network`.

Foundation owns the Unity-free protocol and Static ECS-neutral runtime contracts. It must not depend on gameplay assemblies, `Main`, Unity presentation, Unity Transport, Photon, Nakama, or the legacy network package.

Use namespace-first public names. Root Unity-facing feature types may retain the `Network` prefix later; foundation names do not repeat it.

## Assemblies

- `unigame.staticecs.network.protocol`: packet framing, wire enums, ids, hashing, codecs, limits; no Unity engine references.
- `unigame.staticecs.network`: schema, typed registrations, owned buffers, transport, transforms, history, commands, replication markers; depends on protocol and base Static ECS only.
- Editor tests use `unigame.staticecs.network.tests`.

## Wire v1

All numeric values are little-endian. UUID values use RFC 4122 canonical byte order. `EntityGID` is encoded as `Id u32`, `ClusterId u16`, `Version u16`; never block-copy `Raw`.

The fixed header is 72 bytes:

| Offset | Field |
| ---: | --- |
| 0 | magic `SECS`, `u32` |
| 4 | protocol version `u16 = 1` |
| 6 | header size `u16 = 72` |
| 8 | packet kind `u8` |
| 9 | packet flags `u8` |
| 10 | transform id `u8` |
| 11 | reserved zero `u8` |
| 12 | session epoch `u32` |
| 16 | packet sequence `u32` |
| 20 | server tick `u32` |
| 24 | baseline tick `u32` |
| 28 | acknowledged snapshot tick `u32` |
| 32 | wire payload length `u32` |
| 36 | decoded payload length `u32` |
| 40 | schema hash, 16 bytes |
| 56 | xxHash64 of decoded canonical payload `u64` |
| 64 | header CRC32 `u32` |
| 68 | acknowledged command sequence `u32` |

CRC covers all 72 bytes with its own field zeroed. `NoneTick = 0xffffffff`; sequence zero means none. `baselineTick` is always `NoneTick` in v1.

Enums are fixed:

- `PacketKind`: Hello=1, HelloAck=2, CommandBatch=3, FullSnapshot=4, Ack=5, ResyncRequest=6, Disconnect=7.
- `PacketFlags`: ReliableOrdered=bit 0. Every kind except FullSnapshot requires it; FullSnapshot forbids it.
- `EntityFlags`: Disabled=bit 0.
- `RecordKind`: Component=1, Tag=2, Link=3, Links=4, Multi=5.
- `RecordFlags`: Disabled=bit 0 and only for an `IDisableable` component.
- `CommandFlags`: v1 value is zero.
- `ConnectResult`: Accepted=0, ProtocolVersionMismatch=1, SchemaMismatch=2, TickRateUnsupported=3, LimitsRejected=4, ChunkMapRejected=5.
- `ResyncReason`: HashMismatch=1, SnapshotRejected=2, QueueOverflow=3, LocalStateConflict=4, UnexpectedEpoch=5.
- `DisconnectReason`: ProtocolViolation=1, SchemaMismatch=2, LimitsExceeded=3, UnexpectedEpoch=4, TransportClosed=5, SequenceExhausted=6, ServerShutdown=7.

Unknown enum values, unknown flag bits, unsupported transforms, non-zero reserved fields, invalid lengths and non-canonical ordering are rejected.

Payload layouts:

- Hello: nonce `u64`, min/max tick rate `u16/u16`, max wire/decoded sizes `u32/u32`, capabilities `u32`.
- HelloAck: result `u16`, tick rate `u16`, peer id `u32`, server nonce `u64`, chunk count `u16`, reserved `u16`, then chunk records `[chunk u32, cluster u16, role u8, reserved u8]`. Role 1 is AuthoritySelf.
- CommandBatch: count/reserved `u16/u16`, then `[type id 16, version u16, flags u16, command sequence u32, client tick u32, length u32, payload]`.
- FullSnapshot: entity count `u32`, then `[id u32, cluster u16, version u16, kind id 16, flags u16, record count u16]`; records are `[type id 16, kind u8, flags u8, version u16, element count u32, length u32, payload]`.
- Ack: empty.
- ResyncRequest: reason/reserved `u16/u16`, last accepted tick `u32`.
- Disconnect: reason/reserved `u16/u16`.

Limits are 8 MiB wire payload, 32 MiB decoded payload, 65,535 entities, 256 records per entity, 256 commands per batch, 64 KiB per command, 1 MiB per component and 4,096 chunk mappings. Runtime configuration may lower but not raise them.

## Canonical schema and codecs

Public names: `TypeId`, `CodecId`, `Schema`, `SchemaBuilder<TWorld>`, `ICodec<T>`.

`SchemaBuilder<TWorld>` provides AOT-safe generic registrations:

- `EntityKind<TEntityType>(TypeId)` where `TEntityType : struct, IEntityType`.
- `Component<T,TCodec>(TypeId, version, CodecId, maxBytes)`.
- `Tag<T>(TypeId, version)`.
- `Link<T>(TypeId, version)`.
- `Links<T>(TypeId, version, maxCount)`.
- `Multi<T,TCodec>(TypeId, version, CodecId, maxCount, maxItemBytes)`.
- `Command<T,TCodec,TAuthorizer>(TypeId, version, CodecId, maxBytes)`.

`ICodec<T>` is a pure bounded codec with exact-consumption `TryWrite` and `TryRead`. Native Static ECS serialization hooks are not the default wire codec. Reflection and per-value boxing are forbidden in packet hot paths.

Schema hash is the first 16 bytes of SHA-256 over prefix `SECS-SCHEMA-V1` and fixed manifest records `[kind, flags, version, stable type id, codec id, max payload, max count]`, ordered by kind then RFC UUID bytes. Type names are diagnostic only. Duplicate ids/types and missing entity factories are rejected when freezing the schema.

Canonical order is entity `(ClusterId, Id, Version)`, record `(RecordKind, TypeId bytes)`, command sequence, and unique Links targets `(ClusterId, Id, Version)`. Multi preserves source order.

Record invariants:

- Component has element count 1 and one exactly consumed codec payload.
- Tag has element count and length zero.
- Link has element count 1 and length 8.
- Links length is element count multiplied by 8; targets are unique and canonical.
- Multi payload contains exactly element count repetitions of `[item length u32, item bytes]`.

## Commands and ECS markers

Public contracts: `ICommandAuthorizer<TWorld,TCommand>`, `ITargetCommand`, `CommandContext`, `SendCommandEvent<T>`, `CommandAcceptedEvent<T>`, `CommandRejectedEvent<T>`, `ReplicatedTag`, `PeerOwnerComponent`.

Submitted and accepted events are distinct types. Peer identity comes from the endpoint, never command payload. `OwnerAuthorizer` compares trusted peer id with `PeerOwnerComponent`. Closed generic event types are registered by an assembly registrar in Unity-facing consumers.

## Buffers, transport and transform

Public contracts: `PacketLease`, `PacketHeader`, `Channel`, `TransportState`, `ITransport`, `IPayloadTransform`, `NoOpTransform`.

Packet storage is pooled and ownership is explicit. Sending consumes the lease; receiving transfers a lease to the caller; dispose/fault drains all queues. Double return and use-after-return are debug failures.

`ITransport` guarantees exactly-once, in-order delivery for every accepted `ReliableOrdered` packet until disconnect. Future unreliable carriers implement retransmit/order/dedup below this interface. `UnreliableSequenced` may drop stale snapshots. Reliable queue overflow faults with QueueOverflow rather than reporting a successful send.

Validation order is header/CRC/hard limits, bounded transform decode, decoded payload hash, complete framing and typed staging, then any ECS mutation. V1 supports transform id zero only.

## History

Public names: `TickHistory`, `TickRecord`, `HistoryLookup`, `ReconcileResult`.

History stores immutable per-tick bundles containing generated/received/post-apply canonical snapshot leases, hashes, timing metadata and command payloads. Defaults: 256 tick bundles, 4,096 commands and one shared 256 MiB budget. Evict oldest whole tick bundles until every cap holds. A single oversized record is compared in-flight but not retained.

Lookup outcomes are Found, Evicted, Missing and NotYetSeen. Reconcile outcomes are Match, NeedsRollback, HardResync and HistoryUnavailable. Eviction alone never requests resync because v1 snapshots are full and independent.

Native `WorldSnapshot` and actual restore/replay are out of scope for v1.

## Foundation acceptance

- Golden bytes and round-trip tests cover the header and every payload kind.
- Malformed, truncated, oversized, wrong CRC/hash, reserved and non-canonical inputs are rejected without over-read.
- Schema hash is deterministic and duplicate/missing registrations fail.
- Codecs consume exactly their payload and enforce bounds.
- Transport ownership, reliable ordering, queue fault and disposal are tested.
- History count/byte eviction, lookup and lease ownership are tested.
- Public API has English XML summaries and README uses Capabilities, Usage, Configuration.
- Package compiles without Unity presentation, game assemblies or legacy network dependencies.
