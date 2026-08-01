# SVE11 Network Replication Contract

## Boundary

Extend the Unity-free `unigame.staticecs.network` Runtime assembly with AOT-typed full-snapshot replication and typed command dispatch over the accepted v1 wire foundation. Keep the package generic on `TWorld`; do not depend on Unity, game assemblies, `Main`, reflection-driven pool access, or boxing in tick hot paths.

Implementation is sequential:

1. Retained schema invokers, registration constraints, schema-bound staging and command dispatch.
2. Replica scope, canonical capture, semantic preflight and phased apply.

## Public API

Use namespace-first short names:

- `ScopeRole { Authority, Replica }`.
- `ReplicaScope<TWorld>(ScopeRole role, ReadOnlySpan<ChunkMapping> map) : IDisposable`.
- `Replicator<TWorld>(Schema schema, ReplicaScope<TWorld> scope) : IDisposable`.
- `CaptureResult Capture(out PacketLease payload)`.
- `ApplyResult Apply(StagedPayload snapshot)`.
- `CommandDispatcher<TWorld>(Schema schema)`.
- `DispatchResult Dispatch(StagedPayload commands, int index, uint peerId)`.

`Capture` produces canonical decoded `FullSnapshot` payload bytes. On `Success`, the returned lease belongs to the caller; on every failure it is null. Packet headers, transforms and transport remain the session layer's responsibility. `Apply` accepts only a staged FullSnapshot. `Dispatch` accepts only a staged CommandBatch.

Results are exhaustive:

- `CaptureResult`: Success, WrongRole, ScopeInvalid, EntityConflict, InvalidEntity, DisabledUnsupported, MissingTarget, LimitExceeded, CodecFailed.
- `ApplyResult`: Success, WrongRole, WrongPayload, SchemaMismatch, ScopeInvalid, EntityConflict, InvalidEntity, MissingTarget, LimitExceeded.
- `DispatchResult`: Accepted, Rejected, NoReceiver, ConfigurationError, WrongPayload, SchemaMismatch, InvalidCommand.

All new public types and members require English XML summaries. The package README keeps Capabilities / Usage / Configuration and documents lease ownership, registration duties, preflight guarantees and v1 limitations.

## Typed schema and staging

`SchemaBuilder<TWorld>.Freeze()` retains sealed generic invokers for entity kinds, components, tags, links, links collections, multi values and commands. They are allocated once at setup. Tick operations use the retained arrays and merge-walk canonical entries; they do not discover pools with `System.Type`, build per-tick dictionaries, box values or use runtime reflection.

Schema registrations use `unmanaged` values/markers/commands in addition to their Static ECS marker constraints. `Links` and `Multi` maximum count is 32,768, matching FFS collection storage, not `ProtocolLimits.MaxEntities`. Registering the control `ReplicatedTag` as an ordinary schema record is rejected.

`StagedPayload` retains the exact `Schema.Hash` that validated it. Schema-less payload kinds retain `TypeId.Empty`. Apply and dispatch compare this identity before ECS/event mutation and reject a stage validated by another schema.

Command dispatch decodes through the retained codec, builds trusted `CommandContext` from endpoint peer id plus staged sequence/tick, and invokes the retained authorizer. It emits `CommandAcceptedEvent<T>` or `CommandRejectedEvent<T>`. The consumer must explicitly register both closed generic event types. Missing registration is `ConfigurationError`; registered `World<TWorld>.SendEvent` returning false is `NoReceiver`; otherwise the result is Accepted or Rejected. Invalid index, kind, framing or retained command binding is `InvalidCommand`/`WrongPayload` as applicable.

## Replica scope

Chunk mappings preserve global identity: wire `(Id, ClusterId, Version)` maps directly to `EntityGID`, and FFS chunk is `Id >> 12`. There is no identity remap table.

The scope binds an immutable local role and validates only already registered world state:

- Authority capture requires every mapped chunk to exist with the exact cluster and `ChunkOwnerType.Self`.
- Replica apply requires every mapped chunk to exist with the exact cluster and `ChunkOwnerType.Other`.
- Duplicate mappings, missing cluster/chunk, wrong owner/cluster, a non-empty foreign replica chunk at initial attachment, and later ownership drift are rejected.
- The scope never registers, adopts, moves, changes or repairs clusters/chunks.

Replica scope owns an exact per-session GID ledger. `ReplicatedTag` is a capture/control marker, not proof of session ownership. Only ledger-owned entities may be updated, removed or despawned. Any foreign physical occupant in a mapped replica chunk is a preflight conflict.

## Canonical capture

Capture enumerates `World<TWorld>.Query<All<ReplicatedTag>>().Entities(EntityStatusType.Any, mappedClusters)`, filters exact mapped chunks, collects into pooled scratch and sorts by `(ClusterId, Id, Version)`. Records merge-walk frozen schema order. Links targets use pooled scratch, are unique and canonical; Multi preserves source order.

Every GID has non-zero version and belongs to the scope. Every Link/Links target in v1 must be present in the same full snapshot. Missing/out-of-scope targets fail before a lease escapes.

Wire v1 represents disabled entity and ordinary Component state only. Capture explicitly returns `DisabledUnsupported` for a disabled registered Tag, Link, Links or Multi; it never silently serializes such state as enabled. `ReplicatedTag` is control state and is never encoded as an ordinary record.

## Semantic preflight and apply

Before the first ECS mutation, apply validates the complete staged snapshot deterministically:

- payload kind and exact schema identity;
- role, current scope owner/cluster state and all entity/target membership;
- non-zero versions, canonical uniqueness and no duplicate physical Id;
- FFS segment entity-kind consistency;
- schema kinds, records, counts, codec staging, entity/record disabled legality;
- existing physical occupants and exact ledger ownership.

A different-version occupant at the same physical Id is replaceable only when the old exact GID is ledger-owned and absent from the incoming full snapshot. Every foreign or surviving occupant is `EntityConflict`.

After successful preflight, mutation order is:

1. Destroy missing/replaced ledger replicas required to free slots.
2. Create every missing entity through its retained typed kind invoker and add control `ReplicatedTag`.
3. Apply Component, Tag and Multi data.
4. Apply Link and Links after all target identities exist.
5. Remove schema-selected records absent from the full snapshot.
6. Apply entity and ordinary Component disabled flags; force every present Tag/Link/Links/Multi enabled.
7. Despawn remaining ledger entities missing from the snapshot and atomically replace the scope ledger after success.

No returned validation failure mutates ECS state. Entity creation, component/relation operations and destruction execute user hooks; codecs and hooks used by replication must be deterministic and non-throwing during mutation. A thrown user callback propagates and is not converted to `ApplyResult`; transactional rollback is out of scope.

## Acceptance

- Dedicated `IWorldType` tests initialize only required Static ECS types/resources/events and destroy the world after each test.
- Cover canonical capture bytes and repeated capture after warm-up without managed tick allocations where the test environment can measure reliably.
- Cover capture/apply/despawn, exact session isolation, existing and different-version occupants, wrong role/owner/cluster, missing mappings, zero version, duplicate physical slots, segment kind conflicts and same-snapshot relation targets.
- Cover returned preflight failures with byte-for-byte/world-state proof of no mutation.
- Cover disabled Tag/Link/Links/Multi capture rejection and enabling normalization during apply.
- Cover 32,768 collection cap, schema mismatch and ReplicatedTag control behavior.
- Cover command accepted, rejected, configuration error and no receiver outcomes with trusted peer context.
- Document hook exception semantics and include a focused propagation test without claiming rollback.
- Run focused package tests through the real Unity Editor after accepted integration.

