using System;
using System.IO;
using NUnit.Framework;

namespace UniGame.StaticEcs.Network.Tests
{
    public sealed class ReplayTests
    {
        [Test]
        public void TraceSaveLoadAndReplayPreserveExactCallsAndOwnership()
        {
            var inner = new ScriptTransport { SendMode = SendMode.Accept };
            inner.Inbound = new byte[] { 7, 8, 9 };
            var tape = new ReplayTape(4096);
            var trace = new TraceTransport(inner, tape);
            trace.BeginStep(11);
            var accepted = Lease(1, 2, 3);
            Assert.That(trace.TrySend(Channel.ReliableOrdered, ref accepted), Is.True);
            Assert.That(accepted.IsValid, Is.False);
            inner.SendMode = SendMode.Reject;
            var rejected = Lease(4, 5);
            Assert.That(trace.TrySend(Channel.UnreliableSequenced, ref rejected), Is.False);
            Assert.That(rejected.IsValid, Is.True);
            rejected.Dispose();
            Assert.That(trace.TryReceive(out var inboundChannel, out var inbound), Is.True);
            Assert.That(inboundChannel, Is.EqualTo(Channel.UnreliableSequenced));
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, inbound.Span.ToArray());
            inbound.Dispose();
            trace.Dispose();
            Assert.That(inner.DisposeCount, Is.EqualTo(1));
            Assert.That(tape.IsSealed, Is.True);
            Assert.That(tape.IsComplete, Is.True);

            using var saved = new MemoryStream();
            tape.Save(saved);
            Assert.That(saved.CanWrite, Is.True);
            var bytes = saved.ToArray();
            CollectionAssert.AreEqual(new byte[] { 0x53, 0x45, 0x43, 0x53, 0x4e, 0x45, 0x54, 0x31 }, bytes.AsSpan(0, 8).ToArray());
            Assert.That(Read32(bytes, 12), Is.EqualTo(4));
            Assert.That(Read32(bytes, 32), Is.EqualTo(1));
            Assert.That(Read32(bytes, 36), Is.Zero);

            saved.Position = 0;
            using var loaded = ReplayTape.Load(saved, 4096);
            using var replay = new ReplayTransport(loaded);
            Assert.That(replay.State, Is.EqualTo(TransportState.Connected));
            Assert.That(replay.Error, Is.EqualTo(TransportError.None));
            replay.BeginStep(11);
            var replayAccepted = Lease(1, 2, 3);
            Assert.That(replay.TrySend(Channel.ReliableOrdered, ref replayAccepted), Is.True);
            Assert.That(replayAccepted.IsValid, Is.False);
            var replayRejected = Lease(4, 5);
            Assert.That(replay.TrySend(Channel.UnreliableSequenced, ref replayRejected), Is.False);
            Assert.That(replayRejected.IsValid, Is.True);
            replayRejected.Dispose();
            Assert.That(replay.TryReceive(out var channel, out var packet), Is.True);
            Assert.That(channel, Is.EqualTo(Channel.UnreliableSequenced));
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, packet.Span.ToArray());
            packet.Span[0] = 99;
            packet.Dispose();
        }

        [Test]
        public void ReplayMismatchDoesNotConsumeSendAndFaultsWithoutRepeatingOnDispose()
        {
            using var tape = RecordSingleSend(new byte[] { 1, 2 }, true);
            var replay = new ReplayTransport(tape);
            var wrong = Lease(1, 3);
            Assert.Throws<InvalidOperationException>(() => replay.TrySend(Channel.ReliableOrdered, ref wrong));
            Assert.That(wrong.IsValid, Is.True);
            Assert.That(replay.State, Is.EqualTo(TransportState.Faulted));
            Assert.That(replay.Error, Is.EqualTo(TransportError.InvalidPacket));
            Assert.DoesNotThrow(replay.Dispose);
            wrong.Dispose();
        }

        [Test]
        public void EarlyReplayDisposeReportsTruncationAndReleasesBorrow()
        {
            using var tape = RecordSingleSend(new byte[] { 1 }, true);
            var replay = new ReplayTransport(tape);
            Assert.Throws<InvalidOperationException>(replay.Dispose);
            Assert.That(replay.State, Is.EqualTo(TransportState.Faulted));
            Assert.DoesNotThrow(replay.Dispose);
            using var replayAgain = new ReplayTransport(tape);
            var packet = Lease(1);
            Assert.That(replayAgain.TrySend(Channel.ReliableOrdered, ref packet), Is.True);
        }

        [Test]
        public void OverflowMarksTapeIncompleteWithoutChangingWrappedResult()
        {
            var inner = new ScriptTransport { SendMode = SendMode.Accept };
            using var tape = new ReplayTape(24);
            using (var trace = new TraceTransport(inner, tape))
            {
                trace.BeginStep(1);
                var packet = Lease(3);
                Assert.That(trace.TrySend(Channel.ReliableOrdered, ref packet), Is.True);
            }
            Assert.That(tape.IsComplete, Is.False);
            Assert.That(tape.Dropped, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => tape.Save(new MemoryStream()));
            Assert.Throws<InvalidOperationException>(() => new ReplayTransport(tape));
        }

        [TestCase(SendMode.Throw)]
        [TestCase(SendMode.TransferThenThrow)]
        public void WrappedSendExceptionIsRethrownAndProducesIncompleteTerminalTrace(SendMode mode)
        {
            var inner = new ScriptTransport { SendMode = mode };
            using var tape = new ReplayTape(1024);
            using (var trace = new TraceTransport(inner, tape))
            {
                var packet = Lease(6);
                var error = Assert.Throws<InvalidOperationException>(() => trace.TrySend(Channel.ReliableOrdered, ref packet));
                Assert.That(error.Message, Is.EqualTo("send"));
                Assert.That(packet.IsValid, Is.EqualTo(mode == SendMode.Throw));
                if (packet.IsValid) packet.Dispose();
            }
            Assert.That(tape.IsComplete, Is.False);
        }

        [Test]
        public void ExternalDisposeIsDeferredUntilActiveClaimReleases()
        {
            var inner = new ScriptTransport();
            var tape = new ReplayTape(1024);
            var trace = new TraceTransport(inner, tape);
            tape.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = tape.Bytes);
            Assert.DoesNotThrow(() => trace.BeginStep(1));
            trace.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = tape.IsSealed);
            Assert.DoesNotThrow(tape.Dispose);
        }

        [Test]
        public void LoadRejectsCorruptionBudgetTrailingAndTruncationTransactionally()
        {
            using var tape = RecordSingleSend(new byte[] { 9 }, true);
            using var output = new MemoryStream();
            tape.Save(output);
            var bytes = output.ToArray();

            var corrupt = (byte[])bytes.Clone(); corrupt[corrupt.Length - 1] ^= 1;
            Assert.Throws<InvalidDataException>(() => ReplayTape.Load(new MemoryStream(corrupt), 1024));
            Assert.Throws<InvalidDataException>(() => ReplayTape.Load(new MemoryStream(bytes), 24));
            var trailing = new byte[bytes.Length + 1]; bytes.CopyTo(trailing, 0);
            Assert.Throws<InvalidDataException>(() => ReplayTape.Load(new MemoryStream(trailing), 1024));
            var truncated = new byte[bytes.Length - 1]; Array.Copy(bytes, truncated, truncated.Length);
            Assert.Throws<EndOfStreamException>(() => ReplayTape.Load(new MemoryStream(truncated), 1024));
        }

        [Test]
        public void ZeroPayloadSendRoundTripsThroughPersistence()
        {
            using var tape = RecordSingleSend(Array.Empty<byte>(), true);
            using var output = new MemoryStream();
            tape.Save(output);
            output.Position = 0;
            using var loaded = ReplayTape.Load(output, 1024);
            using var replay = new ReplayTransport(loaded);
            var empty = Lease(Array.Empty<byte>());
            Assert.That(replay.TrySend(Channel.ReliableOrdered, ref empty), Is.True);
            Assert.That(empty.IsValid, Is.False);
        }

        [Test]
        public void ConstructorValidationDoesNotTakeOwnershipOrClaimTape()
        {
            var tape = new ReplayTape(1024);
            var invalid = new ScriptTransport { State = TransportState.Faulted, Error = TransportError.InvalidPacket };
            Assert.Throws<InvalidOperationException>(() => new TraceTransport(invalid, tape));
            Assert.That(invalid.DisposeCount, Is.Zero);
            Assert.DoesNotThrow(tape.Seal);
            tape.Dispose();
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayTape(23));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayTape((long)int.MaxValue + 1));
        }

        private static ReplayTape RecordSingleSend(byte[] bytes, bool accepted)
        {
            var tape = new ReplayTape(1024);
            var inner = new ScriptTransport { SendMode = accepted ? SendMode.Accept : SendMode.Reject };
            using (var trace = new TraceTransport(inner, tape))
            {
                var packet = Lease(bytes);
                Assert.That(trace.TrySend(Channel.ReliableOrdered, ref packet), Is.EqualTo(accepted));
                if (packet.IsValid) packet.Dispose();
            }
            return tape;
        }

        private static PacketLease Lease(params byte[] bytes)
        {
            var lease = PacketLease.Rent(bytes.Length);
            bytes.AsSpan().CopyTo(lease.CapacitySpan);
            lease.SetLength(bytes.Length);
            return lease;
        }

        private static uint Read32(byte[] bytes, int offset) =>
            (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);

        public enum SendMode { Accept, Reject, Throw, TransferThenThrow }

        private sealed class ScriptTransport : ITransport, ISteppedTransport
        {
            public TransportState State { get; set; } = TransportState.Connected;
            public TransportError Error { get; set; } = TransportError.None;
            public SendMode SendMode { get; set; } = SendMode.Reject;
            public byte[] Inbound { get; set; }
            public int DisposeCount { get; private set; }
            public void BeginStep(ulong stepIndex) { }
            public bool TrySend(Channel channel, ref PacketLease packet)
            {
                if (SendMode == SendMode.Throw) throw new InvalidOperationException("send");
                if (SendMode == SendMode.TransferThenThrow)
                {
                    var owned = PacketLease.Transfer(ref packet); owned.Dispose();
                    throw new InvalidOperationException("send");
                }
                if (SendMode == SendMode.Reject) return false;
                var accepted = PacketLease.Transfer(ref packet); accepted.Dispose();
                return true;
            }
            public bool TryReceive(out Channel channel, out PacketLease packet)
            {
                if (Inbound == null) { channel = default; packet = default; return false; }
                channel = Channel.UnreliableSequenced;
                packet = Lease(Inbound);
                Inbound = null;
                return true;
            }
            public void Dispose() { if (State == TransportState.Disposed) return; DisposeCount++; State = TransportState.Disposed; Error = TransportError.Disposed; }
        }
    }
}
