using System;
using FFS.Libraries.StaticEcs;
using NUnit.Framework;

namespace UniGame.StaticEcs.Network.Tests
{
    public sealed class RuntimeTests
    {
        [Test]
        public void SchemaHashIsIndependentOfRegistrationOrderAndDuplicatesFail()
        {
            var a = new SchemaBuilder<TestWorld>().Tag<TestTag>(Id(2), 1).Component<TestComponent, IntCodec>(Id(1), 3, Codec(4), 4).Freeze();
            var b = new SchemaBuilder<TestWorld>().Component<TestComponent, IntCodec>(Id(1), 3, Codec(4), 4).Tag<TestTag>(Id(2), 1).Freeze();
            Assert.That(a.Hash, Is.EqualTo(b.Hash)); Assert.That(a.Entries[0].Kind, Is.EqualTo(SchemaKind.Component));
            var duplicate = new SchemaBuilder<TestWorld>().Tag<TestTag>(Id(1), 1);
            Assert.Throws<InvalidOperationException>(() => duplicate.Component<TestComponent, IntCodec>(Id(1), 1, Codec(2), 4));
        }

        [Test]
        public void SchemaRetainsEveryTypedInvokerAndEnforcesCollectionStorageLimits()
        {
            var schema = new SchemaBuilder<TestWorld>()
                .EntityKind<TestEntityType>(Id(20))
                .Component<TestComponent, IntCodec>(Id(1), 1, Codec(1), 4)
                .Tag<TestTag>(Id(2), 1)
                .Link<TestLink>(Id(3), 1)
                .Links<TestLinks>(Id(4), 1, 32768)
                .Multi<TestMulti, MultiIntCodec>(Id(5), 1, Codec(5), 32768, 4)
                .Command<TestCommand, TestCommandCodec, TestAuthorizer>(Id(10), 1, Codec(10), 4)
                .Freeze();

            foreach (var entry in schema.Entries)
            {
                Assert.That(entry.Invoker, Is.Not.Null);
                Assert.That(entry.Invoker.RuntimeType, Is.EqualTo(entry.RuntimeType));
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaBuilder<TestWorld>().Links<TestLinks>(Id(4), 1, 32769));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaBuilder<TestWorld>().Multi<TestMulti, MultiIntCodec>(Id(5), 1, Codec(5), 32769, 4));
            Assert.Throws<InvalidOperationException>(() => new SchemaBuilder<TestWorld>().Tag<ReplicatedTag>(Id(6), 1));
        }

        [Test]
        public void CodecReportsExactConsumptionAndBounds()
        {
            var codec = new IntCodec(); var bytes = new byte[4]; var value = 42;
            Assert.That(codec.TryWrite(in value, bytes, out var written), Is.True); Assert.That(written, Is.EqualTo(4));
            Assert.That(codec.TryRead(bytes, out int decoded, out var read), Is.True); Assert.That(decoded, Is.EqualTo(value)); Assert.That(read, Is.EqualTo(4));
            Assert.That(codec.TryRead(bytes.AsSpan(0, 3), out int _, out _), Is.False);
        }

        [Test]
        public void TransportTransfersOwnershipOrdersReliablePacketsAndFaultsOnOverflow()
        {
            MemoryTransport.CreatePair(2, out var sender, out var receiver); var first = Lease(1); var second = Lease(2);
            Assert.That(sender.TrySend(Channel.ReliableOrdered, ref first), Is.True); Assert.That(first, Is.Null);
            Assert.That(sender.TrySend(Channel.ReliableOrdered, ref second), Is.True);
            Assert.That(receiver.TryReceive(out _, out var received), Is.True); Assert.That(received.Span[0], Is.EqualTo(1)); received.Dispose();
            Assert.That(receiver.TryReceive(out _, out received), Is.True); Assert.That(received.Span[0], Is.EqualTo(2)); received.Dispose(); sender.Dispose(); receiver.Dispose();

            MemoryTransport.CreatePair(1, out sender, out receiver); first = Lease(1); second = Lease(2); sender.TrySend(Channel.ReliableOrdered, ref first);
            Assert.That(sender.TrySend(Channel.ReliableOrdered, ref second), Is.False); Assert.That(sender.State, Is.EqualTo(TransportState.Faulted)); Assert.That(receiver.State, Is.EqualTo(TransportState.Faulted)); sender.Dispose(); receiver.Dispose();
        }

        [Test]
        public void HistoryEvictsWholeTicksByCountBytesAndOwnsLeases()
        {
            using var history = new TickHistory(2, 2, 4); Assert.That(history.Add(Record(1, 2)), Is.True); Assert.That(history.Add(Record(2, 2)), Is.True); Assert.That(history.Add(Record(3, 2)), Is.True);
            Assert.That(history.Count, Is.EqualTo(2)); Assert.That(history.TryGet(1, out _), Is.EqualTo(HistoryLookup.Evicted)); Assert.That(history.Reconcile(1, 0), Is.EqualTo(ReconcileResult.HistoryUnavailable));
            Assert.That(history.Reconcile(3, 3), Is.EqualTo(ReconcileResult.Match)); Assert.That(history.Reconcile(3, 4), Is.EqualTo(ReconcileResult.NeedsRollback));
            var oversized = Record(4, 5); Assert.That(history.Add(oversized), Is.False); Assert.That(history.TryGet(4, out _), Is.EqualTo(HistoryLookup.Evicted));
        }

        [Test]
        public void CommandStageRetainsAotAuthorizerAndUsesTrustedContext()
        {
            var schema = new SchemaBuilder<TestWorld>().Command<TestCommand, TestCommandCodec, TestAuthorizer>(Id(10), 1, Codec(10), 4).Freeze();
            Assert.That(schema.Entries[0].AuthorizerType, Is.EqualTo(typeof(TestAuthorizer)));
            var bytes = new byte[64]; var payload = new CommandBatchPayload { Commands = new[] { new CommandRecord { TypeId = Id(10), Version = 1, Sequence = 1, ClientTick = 4, Payload = BitConverter.GetBytes(42) } } };
            Assert.That(PayloadCodec.TryWrite(payload, bytes, out var length), Is.True);
            var header = Header(PacketKind.CommandBatch, PacketFlags.ReliableOrdered, 1, schema.Hash);
            Assert.That(PacketFraming.TryEncode(header, bytes.AsSpan(0, length), new NoOpTransform(), schema, out var packet), Is.True);
            Assert.That(PacketFraming.TryDecode(packet, new NoOpTransform(), schema, out _, out var staged), Is.True);
            Assert.That(staged.SchemaHash, Is.EqualTo(schema.Hash));
            var trusted = new CommandContext(7, 1, 4); Assert.That(schema.TryAuthorizeCommand(staged, 0, in trusted, out TestCommand command), Is.True); Assert.That(command.Value, Is.EqualTo(42));
            var untrusted = new CommandContext(8, 1, 4); Assert.That(schema.TryAuthorizeCommand(staged, 0, in untrusted, out command), Is.False);
            staged.Dispose(); packet.Dispose();
        }

        [Test]
        public void SchemaLessStageRetainsEmptySchemaIdentity()
        {
            var payload = PacketLease.Rent(1);
            payload.SetLength(0);
            Assert.That(PayloadStager.TryStage(PacketKind.Ack, payload, null, out var staged), Is.True);
            Assert.That(staged.SchemaHash, Is.EqualTo(TypeId.Empty));
            staged.Dispose();
        }

        [Test]
        public void CommandDispatcherEmitsAcceptedEventWithTrustedContext()
        {
            var schema = DispatchSchema();
            World<DispatchWorld>.Create(WorldConfig.Default());
            World<DispatchWorld>.Types().Event<CommandAcceptedEvent<DispatchCommand>>().Event<CommandRejectedEvent<DispatchCommand>>();
            World<DispatchWorld>.Initialize();
            var receiver = World<DispatchWorld>.RegisterEventReceiver<CommandAcceptedEvent<DispatchCommand>>();
            try
            {
                using var staged = StageDispatchCommand(schema, 11, 19, 42);
                Assert.That(new CommandDispatcher<DispatchWorld>(schema).Dispatch(staged, 0, 7), Is.EqualTo(DispatchResult.Accepted));
                var count = 0;
                foreach (var item in receiver)
                {
                    count++;
                    Assert.That(item.Value.Command.Value, Is.EqualTo(42));
                    Assert.That(item.Value.Context.PeerId, Is.EqualTo(7));
                    Assert.That(item.Value.Context.Sequence, Is.EqualTo(11));
                    Assert.That(item.Value.Context.ClientTick, Is.EqualTo(19));
                }
                Assert.That(count, Is.EqualTo(1));
            }
            finally
            {
                World<DispatchWorld>.DeleteEventReceiver(ref receiver);
                World<DispatchWorld>.Destroy();
            }
        }

        [Test]
        public void CommandDispatcherEmitsRejectedEvent()
        {
            var schema = DispatchSchema();
            World<DispatchWorld>.Create(WorldConfig.Default());
            World<DispatchWorld>.Types().Event<CommandAcceptedEvent<DispatchCommand>>().Event<CommandRejectedEvent<DispatchCommand>>();
            World<DispatchWorld>.Initialize();
            var receiver = World<DispatchWorld>.RegisterEventReceiver<CommandRejectedEvent<DispatchCommand>>();
            try
            {
                using var staged = StageDispatchCommand(schema, 12, 20, 42);
                Assert.That(new CommandDispatcher<DispatchWorld>(schema).Dispatch(staged, 0, 8), Is.EqualTo(DispatchResult.Rejected));
                var count = 0;
                foreach (var item in receiver)
                {
                    count++;
                    Assert.That(item.Value.Context.PeerId, Is.EqualTo(8));
                }
                Assert.That(count, Is.EqualTo(1));
            }
            finally
            {
                World<DispatchWorld>.DeleteEventReceiver(ref receiver);
                World<DispatchWorld>.Destroy();
            }
        }

        [Test]
        public void CommandDispatcherDistinguishesConfigurationAndReceiverFailures()
        {
            var schema = DispatchSchema();
            using var staged = StageDispatchCommand(schema, 1, 2, 42);

            World<DispatchWorld>.Create(WorldConfig.Default());
            World<DispatchWorld>.Types().Event<CommandAcceptedEvent<DispatchCommand>>();
            World<DispatchWorld>.Initialize();
            try
            {
                Assert.That(new CommandDispatcher<DispatchWorld>(schema).Dispatch(staged, 0, 7), Is.EqualTo(DispatchResult.ConfigurationError));
            }
            finally
            {
                World<DispatchWorld>.Destroy();
            }

            World<DispatchWorld>.Create(WorldConfig.Default());
            World<DispatchWorld>.Types().Event<CommandAcceptedEvent<DispatchCommand>>().Event<CommandRejectedEvent<DispatchCommand>>();
            World<DispatchWorld>.Initialize();
            try
            {
                Assert.That(new CommandDispatcher<DispatchWorld>(schema).Dispatch(staged, 0, 7), Is.EqualTo(DispatchResult.NoReceiver));
            }
            finally
            {
                World<DispatchWorld>.Destroy();
            }
        }

        [Test]
        public void CommandDispatcherRejectsWrongPayloadSchemaAndIndexBeforeMutation()
        {
            var schema = DispatchSchema();
            var dispatcher = new CommandDispatcher<DispatchWorld>(schema);
            using var staged = StageDispatchCommand(schema, 1, 2, 42);
            Assert.That(dispatcher.Dispatch(staged, -1, 7), Is.EqualTo(DispatchResult.InvalidCommand));
            Assert.That(dispatcher.Dispatch(staged, 1, 7), Is.EqualTo(DispatchResult.InvalidCommand));

            var other = new SchemaBuilder<DispatchWorld>()
                .Command<DispatchCommand, DispatchCommandCodec, DispatchAuthorizer>(Id(31), 1, Codec(30), 4)
                .Freeze();
            Assert.That(new CommandDispatcher<DispatchWorld>(other).Dispatch(staged, 0, 7), Is.EqualTo(DispatchResult.SchemaMismatch));

            var ackLease = PacketLease.Rent(1);
            ackLease.SetLength(0);
            Assert.That(PayloadStager.TryStage(PacketKind.Ack, ackLease, null, out var ack), Is.True);
            Assert.That(dispatcher.Dispatch(ack, 0, 7), Is.EqualTo(DispatchResult.WrongPayload));
            ack.Dispose();

            var disposed = StageDispatchCommand(schema, 1, 2, 42);
            disposed.Dispose();
            Assert.That(dispatcher.Dispatch(disposed, 0, 7), Is.EqualTo(DispatchResult.InvalidCommand));
        }

        [Test]
        public void MalformedCommandCodecIsRejectedBeforeStagedPayloadEscapes()
        {
            var schema = new SchemaBuilder<TestWorld>().Command<TestCommand, TestCommandCodec, TestAuthorizer>(Id(10), 1, Codec(10), 4).Freeze();
            var bytes = new byte[64];
            var payload = new CommandBatchPayload
            {
                Commands = new[]
                {
                    new CommandRecord { TypeId = Id(10), Version = 1, Sequence = 1, ClientTick = 4, Payload = new byte[3] }
                }
            };
            Assert.That(PayloadCodec.TryWrite(payload, bytes, out var length), Is.True);
            var header = Header(PacketKind.CommandBatch, PacketFlags.ReliableOrdered, 1, schema.Hash);
            Assert.That(PacketFraming.TryEncode(header, bytes.AsSpan(0, length), new NoOpTransform(), schema, out _), Is.False);

            var direct = PacketLease.Rent(length);
            direct.SetLength(length);
            bytes.AsSpan(0, length).CopyTo(direct.Span);
            Assert.That(PayloadStager.TryStage(PacketKind.CommandBatch, direct, schema, out var directStage), Is.False);
            Assert.That(directStage, Is.Null);
            Assert.That(direct.IsValid, Is.False);

            header.WirePayloadLength = (uint)length;
            header.DecodedPayloadLength = (uint)length;
            // Frozen xxHash64 for this canonical malformed CommandBatch.
            header.PayloadHash = 5696635365932090410UL;
            var raw = PacketLease.Rent(PacketHeader.Size + length);
            raw.SetLength(PacketHeader.Size + length);
            Assert.That(header.TryWrite(raw.Span), Is.True);
            bytes.AsSpan(0, length).CopyTo(raw.Span.Slice(PacketHeader.Size));
            Assert.That(PacketFraming.TryDecode(raw, new NoOpTransform(), schema, out _, out var staged), Is.False);
            Assert.That(staged, Is.Null);
            raw.Dispose();
        }

        [Test]
        public void SnapshotStageValidatesEveryRecordShapeAndCodec()
        {
            var schema = new SchemaBuilder<TestWorld>()
                .EntityKind<TestEntityType>(Id(20)).Component<TestComponent, IntCodec>(Id(1), 1, Codec(1), 4)
                .Tag<TestTag>(Id(2), 1).Link<TestLink>(Id(3), 1).Links<TestLinks>(Id(4), 1, 2)
                .Multi<TestMulti, MultiIntCodec>(Id(5), 1, Codec(5), 2, 4).Freeze();
            var link = EntityBytes(1); var links = new byte[16]; EntityBytes(1).CopyTo(links, 0); EntityBytes(2).CopyTo(links, 8);
            var multi = new byte[8]; BitConverter.GetBytes(4).CopyTo(multi, 0); BitConverter.GetBytes(9).CopyTo(multi, 4);
            var snapshot = new FullSnapshotPayload { Entities = new[] { new SnapshotEntity { Entity = new WireEntityId(1, 0, 1), KindId = Id(20), Records = new[] {
                new SnapshotRecord { TypeId = Id(1), Kind = RecordKind.Component, Version = 1, ElementCount = 1, Payload = BitConverter.GetBytes(6) },
                new SnapshotRecord { TypeId = Id(2), Kind = RecordKind.Tag, Version = 1, Payload = Array.Empty<byte>() },
                new SnapshotRecord { TypeId = Id(3), Kind = RecordKind.Link, Version = 1, ElementCount = 1, Payload = link },
                new SnapshotRecord { TypeId = Id(4), Kind = RecordKind.Links, Version = 1, ElementCount = 2, Payload = links },
                new SnapshotRecord { TypeId = Id(5), Kind = RecordKind.Multi, Version = 1, ElementCount = 1, Payload = multi }
            } } } };
            var bytes = new byte[512]; Assert.That(PayloadCodec.TryWrite(snapshot, bytes, out var length), Is.True); var header = Header(PacketKind.FullSnapshot, 0, 2, schema.Hash);
            Assert.That(PacketFraming.TryEncode(header, bytes.AsSpan(0, length), new NoOpTransform(), schema, out var packet), Is.True);
            Assert.That(PacketFraming.TryDecode(packet, new NoOpTransform(), schema, out _, out var staged), Is.True); Assert.That(staged.SchemaHash, Is.EqualTo(schema.Hash)); Assert.That(staged.Entities.Length, Is.EqualTo(1)); Assert.That(staged.Records.Length, Is.EqualTo(5)); staged.Dispose();
            var wrongSchema = new SchemaBuilder<TestWorld>().EntityKind<TestEntityType>(Id(20)).Freeze(); Assert.That(PacketFraming.TryDecode(packet, new NoOpTransform(), wrongSchema, out _, out _), Is.False);
            packet.Span[PacketHeader.Size] ^= 1; Assert.That(PacketFraming.TryDecode(packet, new NoOpTransform(), schema, out _, out _), Is.False); packet.Dispose();
            snapshot.Entities[0].Records[0] = new SnapshotRecord { TypeId = Id(1), Kind = RecordKind.Component, Version = 1, ElementCount = 1, Payload = new byte[3] };
            Assert.That(PayloadCodec.TryWrite(snapshot, bytes, out length), Is.True); Assert.That(PacketFraming.TryEncode(header, bytes.AsSpan(0, length), new NoOpTransform(), schema, out _), Is.False);
        }

        [Test]
        public void UnreliableSequencedRetainsOnlyLatestAndRejectsMalformedOrZeroSequence()
        {
            MemoryTransport.CreatePair(4, out var sender, out var receiver); var reliable = HeaderPacket(PacketKind.Ack, PacketFlags.ReliableOrdered, 1); sender.TrySend(Channel.ReliableOrdered, ref reliable);
            var first = HeaderPacket(PacketKind.FullSnapshot, 0, 1); var firstAlias = first; Assert.That(sender.TrySend(Channel.UnreliableSequenced, ref first), Is.True);
            var latest = HeaderPacket(PacketKind.FullSnapshot, 0, 2); Assert.That(sender.TrySend(Channel.UnreliableSequenced, ref latest), Is.True); Assert.That(firstAlias.IsValid, Is.False); Assert.Throws<InvalidOperationException>(() => { var _ = firstAlias.Span; });
            Assert.That(receiver.TryReceive(out var channel, out var received), Is.True); Assert.That(channel, Is.EqualTo(Channel.ReliableOrdered)); received.Dispose();
            Assert.That(receiver.TryReceive(out channel, out received), Is.True); Assert.That(channel, Is.EqualTo(Channel.UnreliableSequenced)); PacketHeader.TryRead(received.Span, out var header); Assert.That(header.PacketSequence, Is.EqualTo(2)); received.Dispose();
            var zero = HeaderPacket(PacketKind.FullSnapshot, 0, 0); var zeroAlias = zero; Assert.That(sender.TrySend(Channel.UnreliableSequenced, ref zero), Is.False); Assert.That(zeroAlias.IsValid, Is.False);
            var malformed = Lease(1); var malformedAlias = malformed; Assert.That(sender.TrySend(Channel.UnreliableSequenced, ref malformed), Is.False); Assert.That(malformedAlias.IsValid, Is.False); sender.Dispose(); receiver.Dispose();
        }

        [Test]
        public void LeaseRejectsDoubleReturnAndUseAfterTransfer()
        {
            var lease = Lease(1); lease.Dispose(); Assert.Throws<InvalidOperationException>(() => lease.Dispose()); Assert.Throws<InvalidOperationException>(() => { var _ = lease.Span; });
            MemoryTransport.CreatePair(1, out var sender, out var receiver); var sent = Lease(2); var alias = sent; sender.TrySend(Channel.ReliableOrdered, ref sent); Assert.That(sent, Is.Null); Assert.Throws<InvalidOperationException>(() => { var _ = alias.Span; }); receiver.TryReceive(out _, out var received); received.Dispose(); sender.Dispose(); receiver.Dispose();
        }

        private static TickRecord Record(uint tick, int bytes) { var lease = PacketLease.Rent(bytes); lease.SetLength(bytes); return new TickRecord(tick, lease, null, null, tick, tick, tick, 0, 0, Array.Empty<PacketLease>()); }
        private static PacketLease Lease(byte value) { var lease = PacketLease.Rent(1); lease.SetLength(1); lease.Span[0] = value; return lease; }
        private static Schema DispatchSchema() => new SchemaBuilder<DispatchWorld>()
            .Command<DispatchCommand, DispatchCommandCodec, DispatchAuthorizer>(Id(30), 1, Codec(30), 4)
            .Freeze();
        private static StagedPayload StageDispatchCommand(Schema schema, uint sequence, uint clientTick, int value)
        {
            var bytes = new byte[64];
            var payload = new CommandBatchPayload { Commands = new[] { new CommandRecord { TypeId = Id(30), Version = 1, Sequence = sequence, ClientTick = clientTick, Payload = BitConverter.GetBytes(value) } } };
            Assert.That(PayloadCodec.TryWrite(payload, bytes, out var length), Is.True);
            var lease = PacketLease.Rent(length); lease.SetLength(length); bytes.AsSpan(0, length).CopyTo(lease.Span);
            Assert.That(PayloadStager.TryStage(PacketKind.CommandBatch, lease, schema, out var staged), Is.True);
            Assert.That(staged.SchemaHash, Is.EqualTo(schema.Hash));
            return staged;
        }
        private static TypeId Id(int value) => new(new Guid(value, 0, 0, new byte[8]));
        private static CodecId Codec(int value) => new(new Guid(value, 0, 0, new byte[8]));
        private static PacketHeader Header(PacketKind kind, PacketFlags flags, uint sequence, TypeId schema) => new() { Kind = kind, Flags = flags, PacketSequence = sequence, BaselineTick = PacketHeader.NoneTick, SchemaHash = schema };
        private static PacketLease HeaderPacket(PacketKind kind, PacketFlags flags, uint sequence) { var lease = PacketLease.Rent(PacketHeader.Size); lease.SetLength(PacketHeader.Size); Header(kind, flags, sequence, TypeId.Empty).TryWrite(lease.Span); return lease; }
        private static byte[] EntityBytes(uint id) => new byte[] { (byte)id, (byte)(id >> 8), (byte)(id >> 16), (byte)(id >> 24), 0, 0, 1, 0 };
        private struct TestWorld : IWorldType { }
        private struct DispatchWorld : IWorldType { }
        private struct TestEntityType : IEntityType { public byte Id() => 1; }
        private struct TestTag : ITag { }
        private struct TestLink : ILinkType { }
        private struct TestLinks : ILinksType { }
        private struct TestMulti : IMultiComponent { public int Value; }
        private struct TestComponent : IComponent { public int Value; }
        private struct TestCommand { public int Value; }
        private struct DispatchCommand { public int Value; }
        private struct TestAuthorizer : ICommandAuthorizer<TestWorld, TestCommand> { public bool Authorize(in CommandContext context, in TestCommand command) => context.PeerId == 7 && command.Value == 42; }
        private struct DispatchAuthorizer : ICommandAuthorizer<DispatchWorld, DispatchCommand> { public bool Authorize(in CommandContext context, in DispatchCommand command) => context.PeerId == 7 && command.Value == 42; }
        private struct TestCommandCodec : ICodec<TestCommand> { public bool TryWrite(in TestCommand value, Span<byte> destination, out int written) { var raw = value.Value; return new IntCodec().TryWrite(in raw, destination, out written); } public bool TryRead(ReadOnlySpan<byte> source, out TestCommand value, out int read) { var ok = new IntCodec().TryRead(source, out int raw, out read); value = new TestCommand { Value = raw }; return ok; } }
        private struct DispatchCommandCodec : ICodec<DispatchCommand> { public bool TryWrite(in DispatchCommand value, Span<byte> destination, out int written) { var raw = value.Value; return new IntCodec().TryWrite(in raw, destination, out written); } public bool TryRead(ReadOnlySpan<byte> source, out DispatchCommand value, out int read) { var ok = new IntCodec().TryRead(source, out int raw, out read); value = new DispatchCommand { Value = raw }; return ok; } }
        private struct MultiIntCodec : ICodec<TestMulti> { public bool TryWrite(in TestMulti value, Span<byte> destination, out int written) { var raw = value.Value; return new IntCodec().TryWrite(in raw, destination, out written); } public bool TryRead(ReadOnlySpan<byte> source, out TestMulti value, out int read) { var ok = new IntCodec().TryRead(source, out int raw, out read); value = new TestMulti { Value = raw }; return ok; } }
        private struct IntCodec : ICodec<TestComponent>, ICodec<int>
        {
            public bool TryWrite(in TestComponent value, Span<byte> destination, out int written) { var raw = value.Value; return TryWrite(in raw, destination, out written); }
            public bool TryRead(ReadOnlySpan<byte> source, out TestComponent value, out int read) { var ok = TryRead(source, out int raw, out read); value = new TestComponent { Value = raw }; return ok; }
            public bool TryWrite(in int value, Span<byte> destination, out int written) { if (destination.Length < 4) { written = 0; return false; } BitConverter.TryWriteBytes(destination, value); written = 4; return true; }
            public bool TryRead(ReadOnlySpan<byte> source, out int value, out int read) { if (source.Length != 4) { value = 0; read = 0; return false; } value = BitConverter.ToInt32(source); read = 4; return true; }
        }
    }
}
