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

        private static TickRecord Record(uint tick, int bytes) { var lease = PacketLease.Rent(bytes); lease.SetLength(bytes); return new TickRecord(tick, lease, null, null, tick, tick, tick, 0, 0, Array.Empty<PacketLease>()); }
        private static PacketLease Lease(byte value) { var lease = PacketLease.Rent(1); lease.SetLength(1); lease.Span[0] = value; return lease; }
        private static TypeId Id(int value) => new(new Guid(value, 0, 0, new byte[8]));
        private static CodecId Codec(int value) => new(new Guid(value, 0, 0, new byte[8]));
        private struct TestWorld : IWorldType { }
        private struct TestTag : ITag { }
        private struct TestComponent : IComponent { public int Value; }
        private struct IntCodec : ICodec<TestComponent>, ICodec<int>
        {
            public bool TryWrite(in TestComponent value, Span<byte> destination, out int written) { var raw = value.Value; return TryWrite(in raw, destination, out written); }
            public bool TryRead(ReadOnlySpan<byte> source, out TestComponent value, out int read) { var ok = TryRead(source, out int raw, out read); value = new TestComponent { Value = raw }; return ok; }
            public bool TryWrite(in int value, Span<byte> destination, out int written) { if (destination.Length < 4) { written = 0; return false; } BitConverter.TryWriteBytes(destination, value); written = 4; return true; }
            public bool TryRead(ReadOnlySpan<byte> source, out int value, out int read) { if (source.Length != 4) { value = 0; read = 0; return false; } value = BitConverter.ToInt32(source); read = 4; return true; }
        }
    }
}
