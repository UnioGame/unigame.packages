using System;
using FFS.Libraries.StaticEcs;
using NUnit.Framework;

namespace UniGame.StaticEcs.Network.Tests
{
    public sealed class ReplicationTests
    {
        private const uint Chunk = 9;
        private const ushort Cluster = 3;

        [Test]
        public void CaptureIsCanonicalAndApplyReplacesCompleteReplicaState()
        {
            CreateWorld<AuthorityWorld>(ChunkOwnerType.Self);
            CreateWorld<ReplicaWorld>(ChunkOwnerType.Other);
            try
            {
                var authoritySchema = Schema<AuthorityWorld>();
                var replicaSchema = Schema<ReplicaWorld>();
                var map = Mapping();
                using var authorityScope = new ReplicaScope<AuthorityWorld>(ScopeRole.Authority, map);
                using var replicaScope = new ReplicaScope<ReplicaWorld>(ScopeRole.Replica, map);
                using var authority = new Replicator<AuthorityWorld>(authoritySchema, authorityScope);
                using var replica = new Replicator<ReplicaWorld>(replicaSchema, replicaScope);

                var target = World<AuthorityWorld>.NewEntityInChunk<NetEntity>(Chunk);
                target.Set<ReplicatedTag>();
                target.Set(new Value { Number = 17 });
                World<AuthorityWorld>.Components<Value>.Instance.Disable(target);
                target.Set<StateTag>();
                ref var values = ref target.Add<World<AuthorityWorld>.Multi<Item>>();
                values.Add(new Item { Number = 2 });
                values.Add(new Item { Number = 1 });

                var source = World<AuthorityWorld>.NewEntityInChunk<NetEntity>(Chunk);
                source.Set<ReplicatedTag>();
                source.Set(new Value { Number = 42 });
                source.Set(new World<AuthorityWorld>.Link<ParentLink>(target));
                ref var links = ref source.Add<World<AuthorityWorld>.Links<TargetLinks>>();
                links.Add(target);
                source.Disable();

                Assert.That(authority.Capture(out var first), Is.EqualTo(CaptureResult.Success));
                Assert.That(authority.Capture(out var second), Is.EqualTo(CaptureResult.Success));
                CollectionAssert.AreEqual(first.Span.ToArray(), second.Span.ToArray());
                second.Dispose();
                Assert.That(PayloadStager.TryStage(PacketKind.FullSnapshot, first, replicaSchema, out var staged), Is.True);
                using (staged)
                {
                    Assert.That(replica.Apply(staged), Is.EqualTo(ApplyResult.Success));
                }

                Assert.That(source.GID.TryUnpack<ReplicaWorld>(out var replicaSource), Is.True);
                Assert.That(replicaSource.Read<Value>().Number, Is.EqualTo(42));
                Assert.That(replicaSource.IsDisabled, Is.True);
                Assert.That(replicaSource.Read<World<ReplicaWorld>.Link<ParentLink>>().Value, Is.EqualTo(target.GID));
                World<ReplicaWorld>.Components<World<ReplicaWorld>.Link<ParentLink>>.Instance.Disable(replicaSource);
                Assert.That(World<ReplicaWorld>.Components<World<ReplicaWorld>.Link<ParentLink>>.Instance.HasDisabled(replicaSource), Is.True);
                Assert.That(target.GID.TryUnpack<ReplicaWorld>(out var replicaTarget), Is.True);
                Assert.That(replicaTarget.Has<StateTag>(), Is.True);
                Assert.That(World<ReplicaWorld>.Components<Value>.Instance.HasDisabled(replicaTarget), Is.True);
                Assert.That(replicaTarget.Read<World<ReplicaWorld>.Multi<Item>>().AsReadOnlySpan[0].Number, Is.EqualTo(2));

                Assert.That(authority.Capture(out var normalize), Is.EqualTo(CaptureResult.Success));
                Assert.That(PayloadStager.TryStage(PacketKind.FullSnapshot, normalize, replicaSchema, out staged), Is.True);
                using (staged) Assert.That(replica.Apply(staged), Is.EqualTo(ApplyResult.Success));
                Assert.That(World<ReplicaWorld>.Components<World<ReplicaWorld>.Link<ParentLink>>.Instance.HasDisabled(replicaSource), Is.False);

                var sourceGid = source.GID;
                source.Destroy();
                Assert.That(authority.Capture(out var despawn), Is.EqualTo(CaptureResult.Success));
                Assert.That(PayloadStager.TryStage(PacketKind.FullSnapshot, despawn, replicaSchema, out staged), Is.True);
                using (staged) Assert.That(replica.Apply(staged), Is.EqualTo(ApplyResult.Success));
                Assert.That(sourceGid.TryUnpack<ReplicaWorld>(out _), Is.False);
                Assert.That(target.GID.TryUnpack<ReplicaWorld>(out _), Is.True);
            }
            finally
            {
                World<AuthorityWorld>.Destroy();
                World<ReplicaWorld>.Destroy();
            }
        }

        [Test]
        public void CaptureRejectsRelationOutsideSameSnapshotWithoutLeakingLease()
        {
            CreateWorld<AuthorityWorld>(ChunkOwnerType.Self);
            try
            {
                var map = Mapping();
                using var scope = new ReplicaScope<AuthorityWorld>(ScopeRole.Authority, map);
                using var replicator = new Replicator<AuthorityWorld>(Schema<AuthorityWorld>(), scope);
                var target = World<AuthorityWorld>.NewEntityInChunk<NetEntity>(Chunk);
                var source = World<AuthorityWorld>.NewEntityInChunk<NetEntity>(Chunk);
                source.Set<ReplicatedTag>();
                source.Set(new World<AuthorityWorld>.Link<ParentLink>(target));

                Assert.That(replicator.Capture(out var payload), Is.EqualTo(CaptureResult.MissingTarget));
                Assert.That(payload, Is.Null);
            }
            finally
            {
                World<AuthorityWorld>.Destroy();
            }
        }

        [Test]
        public void CaptureRejectsDisabledRelationStorageInVersionOne()
        {
            CreateWorld<AuthorityWorld>(ChunkOwnerType.Self);
            try
            {
                var map = Mapping();
                using var scope = new ReplicaScope<AuthorityWorld>(ScopeRole.Authority, map);
                using var replicator = new Replicator<AuthorityWorld>(Schema<AuthorityWorld>(), scope);
                var target = World<AuthorityWorld>.NewEntityInChunk<NetEntity>(Chunk);
                target.Set<ReplicatedTag>();
                var source = World<AuthorityWorld>.NewEntityInChunk<NetEntity>(Chunk);
                source.Set<ReplicatedTag>();
                source.Set(new World<AuthorityWorld>.Link<ParentLink>(target));
                World<AuthorityWorld>.Components<World<AuthorityWorld>.Link<ParentLink>>.Instance.Disable(source);

                Assert.That(replicator.Capture(out var payload), Is.EqualTo(CaptureResult.DisabledUnsupported));
                Assert.That(payload, Is.Null);
            }
            finally
            {
                World<AuthorityWorld>.Destroy();
            }
        }

        [Test]
        public void FfsTagStorageCannotRepresentDisabledTagState()
        {
            CreateWorld<AuthorityWorld>(ChunkOwnerType.Self);
            try
            {
                var entity = World<AuthorityWorld>.NewEntityInChunk<NetEntity>(Chunk);
                entity.Set<StateTag>();
                Assert.Catch<Exception>(() => World<AuthorityWorld>.Components<StateTag>.Instance.Disable(entity));
                Assert.That(entity.Has<StateTag>(), Is.True);
            }
            finally
            {
                World<AuthorityWorld>.Destroy();
            }
        }

        [Test]
        public void RolesAndForeignReplicaOccupantsFailBeforeMutation()
        {
            CreateWorld<AuthorityWorld>(ChunkOwnerType.Self);
            CreateWorld<ReplicaWorld>(ChunkOwnerType.Other);
            try
            {
                var map = Mapping();
                using var authorityScope = new ReplicaScope<AuthorityWorld>(ScopeRole.Authority, map);
                using var replicaScope = new ReplicaScope<ReplicaWorld>(ScopeRole.Replica, map);
                using var authority = new Replicator<AuthorityWorld>(Schema<AuthorityWorld>(), authorityScope);
                using var replica = new Replicator<ReplicaWorld>(Schema<ReplicaWorld>(), replicaScope);
                Assert.That(replica.Capture(out _), Is.EqualTo(CaptureResult.WrongRole));

                var source = World<AuthorityWorld>.NewEntityInChunk<NetEntity>(Chunk);
                source.Set<ReplicatedTag>();
                Assert.That(authority.Capture(out var payload), Is.EqualTo(CaptureResult.Success));
                Assert.That(PayloadStager.TryStage(PacketKind.FullSnapshot, payload, Schema<ReplicaWorld>(), out var staged), Is.True);
                var foreignGid = new EntityGID((Chunk << Const.ENTITIES_IN_CHUNK_SHIFT) + 100, 1, Cluster);
                var foreign = World<ReplicaWorld>.NewEntityByGID<NetEntity>(foreignGid);
                foreign.Set(new Value { Number = 99 });

                using (staged) Assert.That(replica.Apply(staged), Is.EqualTo(ApplyResult.EntityConflict));
                Assert.That(foreign.Read<Value>().Number, Is.EqualTo(99));
                Assert.That(source.GID.TryUnpack<ReplicaWorld>(out _), Is.False);
            }
            finally
            {
                World<AuthorityWorld>.Destroy();
                World<ReplicaWorld>.Destroy();
            }
        }

        [Test]
        public void InvalidTopologyMappingsAreRejectedWithoutTopologyMutation()
        {
            CreateWorld<AuthorityWorld>(ChunkOwnerType.Self);
            try
            {
                World<AuthorityWorld>.RegisterChunk(Chunk + 1, ChunkOwnerType.Other, Cluster);
                using var missing = new ReplicaScope<AuthorityWorld>(ScopeRole.Authority,
                    new[] { new ChunkMapping { Chunk = 77, Cluster = Cluster, Role = 1 } });
                using var wrongCluster = new ReplicaScope<AuthorityWorld>(ScopeRole.Authority,
                    new[] { new ChunkMapping { Chunk = Chunk, Cluster = (ushort)(Cluster + 1), Role = 1 } });
                using var wrongOwner = new ReplicaScope<AuthorityWorld>(ScopeRole.Authority,
                    new[] { new ChunkMapping { Chunk = Chunk + 1, Cluster = Cluster, Role = 1 } });
                using var a = new Replicator<AuthorityWorld>(Schema<AuthorityWorld>(), missing);
                using var b = new Replicator<AuthorityWorld>(Schema<AuthorityWorld>(), wrongCluster);
                using var c = new Replicator<AuthorityWorld>(Schema<AuthorityWorld>(), wrongOwner);

                Assert.That(a.Capture(out _), Is.EqualTo(CaptureResult.ScopeInvalid));
                Assert.That(b.Capture(out _), Is.EqualTo(CaptureResult.ScopeInvalid));
                Assert.That(c.Capture(out _), Is.EqualTo(CaptureResult.ScopeInvalid));
                Assert.That(World<AuthorityWorld>.GetChunkOwner(Chunk), Is.EqualTo(ChunkOwnerType.Self));
                Assert.That(World<AuthorityWorld>.GetChunkClusterId(Chunk), Is.EqualTo(Cluster));
            }
            finally
            {
                World<AuthorityWorld>.Destroy();
            }
        }

        [Test]
        public void ApplyRejectsIdentityAndSegmentConflictsThenReplacesLedgerOwnedGeneration()
        {
            CreateWorld<ReplicaWorld>(ChunkOwnerType.Other);
            try
            {
                var schema = Schema<ReplicaWorld>();
                var map = Mapping();
                using var scope = new ReplicaScope<ReplicaWorld>(ScopeRole.Replica, map);
                using var replica = new Replicator<ReplicaWorld>(schema, scope);
                var id = Chunk << Const.ENTITIES_IN_CHUNK_SHIFT;

                using (var zero = Stage(schema, Snapshot(new WireEntityId(id, Cluster, 0), Id(1))))
                    Assert.That(replica.Apply(zero), Is.EqualTo(ApplyResult.InvalidEntity));
                using (var duplicate = Stage(schema, Snapshot(
                           new SnapshotEntity { Entity = new WireEntityId(id, Cluster, 1), KindId = Id(1) },
                           new SnapshotEntity { Entity = new WireEntityId(id, Cluster, 2), KindId = Id(1) })))
                    Assert.That(replica.Apply(duplicate), Is.EqualTo(ApplyResult.InvalidEntity));
                using (var segment = Stage(schema, Snapshot(
                           new SnapshotEntity { Entity = new WireEntityId(id, Cluster, 1), KindId = Id(1) },
                           new SnapshotEntity { Entity = new WireEntityId(id + 1, Cluster, 1), KindId = Id(7) })))
                    Assert.That(replica.Apply(segment), Is.EqualTo(ApplyResult.InvalidEntity));
                Assert.That(new EntityGID(id, 1, Cluster).TryUnpack<ReplicaWorld>(out _), Is.False);

                using (var first = Stage(schema, Snapshot(new WireEntityId(id, Cluster, 1), Id(1))))
                    Assert.That(replica.Apply(first), Is.EqualTo(ApplyResult.Success));
                using (var replacement = Stage(schema, Snapshot(new WireEntityId(id, Cluster, 2), Id(1))))
                    Assert.That(replica.Apply(replacement), Is.EqualTo(ApplyResult.Success));
                Assert.That(new EntityGID(id, 1, Cluster).TryUnpack<ReplicaWorld>(out _), Is.False);
                Assert.That(new EntityGID(id, 2, Cluster).TryUnpack<ReplicaWorld>(out var current), Is.True);
                Assert.That(current.Has<ReplicatedTag>(), Is.True);
            }
            finally
            {
                World<ReplicaWorld>.Destroy();
            }
        }

        [Test]
        public void ApplyRejectsSchemaMismatchAndPropagatesCodecExceptionWithoutRollbackPromise()
        {
            CreateWorld<ReplicaWorld>(ChunkOwnerType.Other);
            try
            {
                var schema = Schema<ReplicaWorld>();
                var map = Mapping();
                using var scope = new ReplicaScope<ReplicaWorld>(ScopeRole.Replica, map);
                using var replica = new Replicator<ReplicaWorld>(schema, scope);
                var id = Chunk << Const.ENTITIES_IN_CHUNK_SHIFT;
                var entity = new SnapshotEntity
                {
                    Entity = new WireEntityId(id, Cluster, 1),
                    KindId = Id(1),
                    Records = new[] { new SnapshotRecord { TypeId = Id(2), Kind = RecordKind.Component, Version = 1, ElementCount = 1, Payload = BitConverter.GetBytes(7) } }
                };
                using var staged = Stage(schema, new FullSnapshotPayload { Entities = new[] { entity } });
                var otherSchema = new SchemaBuilder<ReplicaWorld>().EntityKind<NetEntity>(Id(1)).EntityKind<OtherEntity>(Id(7)).Freeze();
                using var mismatched = new Replicator<ReplicaWorld>(otherSchema, scope);
                Assert.That(mismatched.Apply(staged), Is.EqualTo(ApplyResult.SchemaMismatch));

                ValueCodec.Reads = 0;
                ValueCodec.ThrowOnReadCall = 2;
                try
                {
                    Assert.Throws<InvalidOperationException>(() => replica.Apply(staged));
                }
                finally
                {
                    ValueCodec.ThrowOnReadCall = 0;
                    ValueCodec.Reads = 0;
                }
                Assert.That(new EntityGID(id, 1, Cluster).TryUnpack<ReplicaWorld>(out var partial), Is.True,
                    "Typed apply exceptions propagate after the documented mutation boundary; rollback is not promised.");
                Assert.That(partial.Has<ReplicatedTag>(), Is.True);
            }
            finally
            {
                ValueCodec.ThrowOnReadCall = 0;
                World<ReplicaWorld>.Destroy();
            }
        }

        private static void CreateWorld<TWorld>(ChunkOwnerType owner) where TWorld : struct, IWorldType
        {
            World<TWorld>.Create(WorldConfig.Default());
            World<TWorld>.Types().EntityType<NetEntity>().Tag<ReplicatedTag>().Tag<StateTag>()
                .EntityType<OtherEntity>().Component<Value>().Link<ParentLink>().Links<TargetLinks>().Multi<Item>();
            World<TWorld>.Initialize();
            World<TWorld>.RegisterCluster(Cluster);
            World<TWorld>.RegisterChunk(Chunk, owner, Cluster);
        }

        private static Schema Schema<TWorld>() where TWorld : struct, IWorldType => new SchemaBuilder<TWorld>()
            .EntityKind<NetEntity>(Id(1))
            .EntityKind<OtherEntity>(Id(7))
            .Component<Value, ValueCodec>(Id(2), 1, Codec(2), 4)
            .Tag<StateTag>(Id(3), 1)
            .Link<ParentLink>(Id(4), 1)
            .Links<TargetLinks>(Id(5), 1, 8)
            .Multi<Item, ItemCodec>(Id(6), 1, Codec(6), 8, 4)
            .Freeze();

        private static ChunkMapping[] Mapping() => new[] { new ChunkMapping { Chunk = Chunk, Cluster = Cluster, Role = 1 } };
        private static FullSnapshotPayload Snapshot(WireEntityId entity, TypeId kind) => Snapshot(new SnapshotEntity { Entity = entity, KindId = kind });
        private static FullSnapshotPayload Snapshot(params SnapshotEntity[] entities) => new() { Entities = entities };
        private static StagedPayload Stage(Schema schema, FullSnapshotPayload snapshot)
        {
            var bytes = new byte[1024];
            Assert.That(PayloadCodec.TryWrite(snapshot, bytes, out var length), Is.True);
            var lease = PacketLease.Rent(length);
            lease.SetLength(length);
            bytes.AsSpan(0, length).CopyTo(lease.Span);
            Assert.That(PayloadStager.TryStage(PacketKind.FullSnapshot, lease, schema, out var staged), Is.True);
            return staged;
        }
        private static TypeId Id(int value) => new(new Guid(value, 0, 0, new byte[8]));
        private static CodecId Codec(int value) => new(new Guid(value, 1, 0, new byte[8]));

        private struct AuthorityWorld : IWorldType { }
        private struct ReplicaWorld : IWorldType { }
        private struct NetEntity : IEntityType { public byte Id() => 11; }
        private struct OtherEntity : IEntityType { public byte Id() => 12; }
        private struct StateTag : ITag, IDisableable { }
        private struct ParentLink : ILinkType { }
        private struct TargetLinks : ILinksType { }
        private struct Value : IComponent, IDisableable { public int Number; }
        private struct Item : IMultiComponent { public int Number; }
        private struct ValueCodec : ICodec<Value>
        {
            internal static int Reads;
            internal static int ThrowOnReadCall;
            public bool TryWrite(in Value value, Span<byte> destination, out int written) { if (destination.Length < 4) { written = 0; return false; } BitConverter.TryWriteBytes(destination, value.Number); written = 4; return true; }
            public bool TryRead(ReadOnlySpan<byte> source, out Value value, out int read) { if (++Reads == ThrowOnReadCall) throw new InvalidOperationException("codec hook"); if (source.Length != 4) { value = default; read = 0; return false; } value = new Value { Number = BitConverter.ToInt32(source) }; read = 4; return true; }
        }
        private struct ItemCodec : ICodec<Item>
        {
            public bool TryWrite(in Item value, Span<byte> destination, out int written) { if (destination.Length < 4) { written = 0; return false; } BitConverter.TryWriteBytes(destination, value.Number); written = 4; return true; }
            public bool TryRead(ReadOnlySpan<byte> source, out Item value, out int read) { if (source.Length != 4) { value = default; read = 0; return false; } value = new Item { Number = BitConverter.ToInt32(source) }; read = 4; return true; }
        }
    }
}
