using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using FFS.Libraries.StaticEcs;
using NUnit.Framework;

namespace UniGame.StaticEcs.Network.Tests
{
    public sealed class DiagnosticsTests
    {
        private const uint Chunk = 41;
        private const ushort Cluster = 6;

        [SetUp]
        public void EnterPoolTestLock() => Monitor.Enter(PoolTestGate.Sync);

        [TearDown]
        public void ExitPoolTestLock() => Monitor.Exit(PoolTestGate.Sync);

        [Test]
        public void EventAndFingerprintAreValueOnly()
        {
            Assert.That(RuntimeHelpers.IsReferenceOrContainsReferences<SessionEvent>(), Is.False);
            var first = new TickFingerprint(0, 17, 23);
            var same = new TickFingerprint(0, 17, 23);
            Assert.That(first, Is.EqualTo(same));
            Assert.That(first == same, Is.True);
            Assert.That(first != new TickFingerprint(1, 17, 23), Is.True);
        }

        [Test]
        public void NdjsonGoldenIsInvariantPrivateAndLfOnly()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            try
            {
                using var output = new MemoryStream();
                using (var log = new NdjsonLog(output, 2, 9, true))
                {
                    var value = Event(7, Stopwatch.Frequency, 0, SessionEventKind.Send,
                        SessionEventPhase.End, PacketKind.CommandBatch, Channel.ReliableOrdered,
                        0x0102030405060708UL);
                    log.Observe(in value);
                    log.Flush();
                }
                var text = Encoding.UTF8.GetString(output.ToArray());
                var expected = "{\"v\":1,\"source\":9,\"id\":7,\"step\":3,\"time_ns\":1000000000," +
                    "\"elapsed_ns\":0,\"role\":\"client\",\"kind\":\"send\",\"phase\":\"end\"," +
                    "\"state\":\"established\",\"error\":\"none\",\"packet\":\"command_batch\"," +
                    "\"channel\":\"reliable_ordered\",\"tick\":0,\"packet_sequence\":5," +
                    "\"wire_bytes\":91,\"decoded_bytes\":19,\"count\":2,\"code\":1," +
                    "\"reason\":0,\"hash\":\"0102030405060708\",\"success\":true,\"retry\":false}\n";
                Assert.That(text, Is.EqualTo(expected));
                Assert.That(text, Does.Not.Contain("nonce"));
                Assert.That(text, Does.Not.Contain("peer"));
                Assert.That(text, Does.Not.Contain("payload"));
                Assert.That(text, Does.Not.Contain("\r"));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Test]
        public void OverflowWritesRetainedPrefixThenOneGapThenLaterEvent()
        {
            using var output = new MemoryStream();
            using var log = new NdjsonLog(output, 2, 4, true);
            var one = Event(1); var two = Event(2); var three = Event(3); var four = Event(4);
            log.Observe(in one); log.Observe(in two); log.Observe(in three); log.Observe(in four);
            Assert.That(log.Pending, Is.EqualTo(2));
            Assert.That(log.Dropped, Is.EqualTo(2));
            log.Flush();
            var five = Event(5); log.Observe(in five); log.Flush();
            var lines = Encoding.UTF8.GetString(output.ToArray()).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(lines.Length, Is.EqualTo(4));
            Assert.That(lines[0], Does.Contain("\"id\":1,"));
            Assert.That(lines[1], Does.Contain("\"id\":2,"));
            Assert.That(lines[2], Is.EqualTo("{\"v\":1,\"source\":4,\"first_id\":3,\"last_id\":4,\"count\":2}"));
            Assert.That(lines[3], Does.Contain("\"id\":5,"));
        }

        [Test]
        public void StreamFailureIsTerminalAndClearsPendingWithoutThrowing()
        {
            using var stream = new ThrowingStream();
            var log = new NdjsonLog(stream, 2, leaveOpen: true);
            var one = Event(1); var two = Event(2);
            log.Observe(in one); log.Observe(in two);
            Assert.DoesNotThrow(log.Flush);
            Assert.That(log.Faulted, Is.True);
            Assert.That(log.Pending, Is.Zero);
            Assert.That(log.Dropped, Is.EqualTo(2));
            var three = Event(3); log.Observe(in three);
            Assert.That(log.Dropped, Is.EqualTo(3));
            Assert.DoesNotThrow(log.Flush);
            Assert.DoesNotThrow(log.Dispose);
        }

        [Test]
        public void DisposeIsIdempotentAndPostDisposeObserveCountsAsDropped()
        {
            var output = new MemoryStream();
            var log = new NdjsonLog(output, 1, leaveOpen: true);
            log.Dispose();
            log.Dispose();
            var value = Event(1);
            log.Observe(in value);
            Assert.That(log.Dropped, Is.EqualTo(1));
            Assert.DoesNotThrow(log.Flush);
        }

        [Test]
        public void ThrowingObserverCannotChangeHandshakeAndCountsEveryFailure()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self);
            MemoryTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), EmptySchema<ClientWorld>(),
                clientTransport, new ThrowingObserver());
            using var server = new Session<ServerWorld>(ServerConfig(), EmptySchema<ServerWorld>(),
                serverTransport, new ThrowingObserver());
            try
            {
                PumpEstablished(client, server);
                Assert.That(client.Error, Is.EqualTo(SessionError.None));
                Assert.That(server.Error, Is.EqualTo(SessionError.None));
                Assert.That(client.Stats.ObserverErrors, Is.GreaterThan(0));
                Assert.That(server.Stats.ObserverErrors, Is.GreaterThan(0));
                Assert.That(client.Stats.Steps, Is.EqualTo(3));
                Assert.That(server.Stats.Steps, Is.EqualTo(3));
                Assert.That(client.Stats.SentPackets, Is.EqualTo(2));
                Assert.That(server.Stats.SentPackets, Is.EqualTo(2));
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void StepExceptionStillPairsEventsWithRequestedStepAndFailure()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other);
            var observer = new RecordingObserver();
            using var session = new Session<ClientWorld>(ClientConfig(), EmptySchema<ClientWorld>(),
                new ThrowStepTransport(), observer);
            try
            {
                Assert.Throws<InvalidOperationException>(() => session.Step(17));
                Assert.That(observer.Events.Count, Is.EqualTo(2));
                Assert.That(observer.Events[0].Kind, Is.EqualTo(SessionEventKind.Step));
                Assert.That(observer.Events[0].Phase, Is.EqualTo(SessionEventPhase.Begin));
                Assert.That(observer.Events[0].Step, Is.EqualTo(17));
                Assert.That(observer.Events[1].Phase, Is.EqualTo(SessionEventPhase.End));
                Assert.That(observer.Events[1].Step, Is.EqualTo(17));
                Assert.That(observer.Events[1].Success, Is.False);
                Assert.That(observer.Events[1].Id, Is.EqualTo(observer.Events[0].Id + 1));
                Assert.That(session.Stats.Steps, Is.EqualTo(1));
            }
            finally { DestroyWorld<ClientWorld>(); }
        }

        [Test]
        public void CaptureApplyFingerprintsAndStatsUseCanonicalBytesAtTickZero()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self);
            MemoryTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            var clientEvents = new RecordingObserver();
            var serverEvents = new RecordingObserver();
            using var client = new Session<ClientWorld>(ClientConfig(), EmptySchema<ClientWorld>(), clientTransport, clientEvents);
            using var server = new Session<ServerWorld>(ServerConfig(), EmptySchema<ServerWorld>(), serverTransport, serverEvents);
            try
            {
                PumpEstablished(client, server);
                Assert.That(server.Capture(0), Is.EqualTo(CaptureResult.Success));
                server.Step(3);
                client.Step(3);
                Assert.That(server.TryGetFingerprint(0, out var generated), Is.EqualTo(HistoryLookup.Found));
                Assert.That(client.TryGetFingerprint(0, out var received), Is.EqualTo(HistoryLookup.Found));
                Assert.That(generated, Is.EqualTo(received));
                Assert.That(generated.Tick, Is.Zero);
                Assert.That(generated.Bytes, Is.GreaterThan(0));
                Assert.That(server.Stats.SnapshotsCaptured, Is.EqualTo(1));
                Assert.That(client.Stats.SnapshotsApplied, Is.EqualTo(1));
                Assert.That(serverEvents.Events.Exists(value => value.Kind == SessionEventKind.Capture &&
                    value.Phase == SessionEventPhase.End && value.Success && value.Hash == generated.Hash), Is.True);
                Assert.That(clientEvents.Events.Exists(value => value.Kind == SessionEventKind.Apply &&
                    value.Phase == SessionEventPhase.End && value.Success && value.Hash == received.Hash), Is.True);
                server.Dispose();
                client.Step(4);
                Assert.That(client.State, Is.EqualTo(SessionState.Faulted));
                Assert.That(client.TryGetFingerprint(0, out _), Is.EqualTo(HistoryLookup.Missing));
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        private static SessionEvent Event(ulong id, long timestamp = 0, long elapsed = 0,
            SessionEventKind kind = SessionEventKind.Step, SessionEventPhase phase = SessionEventPhase.Point,
            PacketKind packet = (PacketKind)0, Channel channel = default, ulong hash = 0) =>
            new(id, 3, timestamp, elapsed, 0, 5, 91, 19, 2, 1, 0, hash,
                SessionRole.Client, kind, phase, SessionState.Established, SessionError.None,
                packet, channel, true, false);

        private sealed class ThrowingStream : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count) => throw new IOException("write");
        }

        private static void PumpEstablished(Session<ClientWorld> client, Session<ServerWorld> server)
        {
            for (ulong step = 0; step < 3; step++) { client.Step(step); server.Step(step); }
            Assert.That(client.State, Is.EqualTo(SessionState.Established));
            Assert.That(server.State, Is.EqualTo(SessionState.Established));
        }

        private static void CreateWorld<TWorld>(ChunkOwnerType owner) where TWorld : struct, IWorldType
        {
            World<TWorld>.Create(WorldConfig.Default());
            World<TWorld>.Types().Tag<ReplicatedTag>();
            World<TWorld>.Initialize();
            World<TWorld>.RegisterCluster(Cluster);
            World<TWorld>.RegisterChunk(Chunk, owner, Cluster);
        }

        private static void DestroyWorld<TWorld>() where TWorld : struct, IWorldType
        {
            if (World<TWorld>.Status != WorldStatus.NotCreated) World<TWorld>.Destroy();
        }

        private static SessionConfig ClientConfig() => SessionConfig.Client(51, 20, 40);
        private static SessionConfig ServerConfig() => SessionConfig.Server(7, 9, 53, 30,
            new[] { new ChunkMapping { Chunk = Chunk, Cluster = Cluster, Role = 1 } });
        private static Schema EmptySchema<TWorld>() where TWorld : struct, IWorldType =>
            new SchemaBuilder<TWorld>().Freeze();

        private sealed class RecordingObserver : ISessionObserver
        {
            internal readonly List<SessionEvent> Events = new();
            public void Observe(in SessionEvent value) => Events.Add(value);
        }

        private sealed class ThrowingObserver : ISessionObserver
        {
            public void Observe(in SessionEvent value) => throw new InvalidOperationException("observer");
        }

        private sealed class ThrowStepTransport : ITransport, ISteppedTransport
        {
            public TransportState State { get; private set; } = TransportState.Connected;
            public TransportError Error { get; private set; } = TransportError.None;
            public void BeginStep(ulong stepIndex) => throw new InvalidOperationException("step");
            public bool TrySend(Channel channel, ref PacketLease packet) => false;
            public bool TryReceive(out Channel channel, out PacketLease packet) { channel = default; packet = default; return false; }
            public void Dispose() { State = TransportState.Disposed; Error = TransportError.Disposed; }
        }

        private struct ClientWorld : IWorldType { }
        private struct ServerWorld : IWorldType { }
    }
}
