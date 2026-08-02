namespace UniGame.StaticEcs.Network.Profiler.Tests
{
    using System;
    using System.Collections;
    using System.Threading;
    using FFS.Libraries.StaticEcs;
    using NUnit.Framework;
    using Unity.Profiling;
    using UnityEngine.TestTools;

    public sealed class ProfilerObserverTests
    {
        private const uint Chunk = 41;
        private const ushort Cluster = 6;
        private static readonly object TestGate = new();
        private static readonly TypeId CommandId = new(new Guid(701, 0, 0, new byte[8]));
        private static readonly CodecId CommandCodecId = new(new Guid(702, 0, 0, new byte[8]));
        private static readonly TypeId EntityId = new(new Guid(703, 0, 0, new byte[8]));
        private static readonly TypeId ValueId = new(new Guid(704, 0, 0, new byte[8]));
        private static readonly CodecId ValueCodecId = new(new Guid(705, 0, 0, new byte[8]));

        [SetUp]
        public void EnterTestGate() => Monitor.Enter(TestGate);

        [TearDown]
        public void ExitTestGate() => Monitor.Exit(TestGate);

        [Test]
        public void RegistersExactNetworkMarkersAndCounters()
        {
            var observer = new ProfilerObserver(17);
            Assert.That(observer.Source, Is.EqualTo(17));

            var names = new[]
            {
                "SECS.Net.Step", "SECS.Net.Receive", "SECS.Net.Decode", "SECS.Net.Dispatch",
                "SECS.Net.Capture", "SECS.Net.Apply", "SECS.Net.Encode", "SECS.Net.Send",
                "SECS.Net.WireIn", "SECS.Net.WireOut", "SECS.Net.Decoded", "SECS.Net.Commands",
                "SECS.Net.Captures", "SECS.Net.Applies", "SECS.Net.Retries", "SECS.Net.Declines",
                "SECS.Net.Faults", "SECS.Net.Resyncs"
            };
            var recorders = new ProfilerRecorder[names.Length];
            try
            {
                for (var i = 0; i < names.Length; i++)
                {
                    recorders[i] = Start(names[i]);
                    Assert.That(recorders[i].Valid, Is.True, names[i]);
                }
            }
            finally
            {
                for (var i = 0; i < recorders.Length; i++)
                    if (recorders[i].Valid) recorders[i].Dispose();
            }
        }

        [UnityTest]
        public IEnumerator RealSessionsPublishExactPositiveCounterDeltas()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self);
            var source = World<ServerWorld>.NewEntityInChunk<NetEntity>(Chunk);
            source.Set<ReplicatedTag>();
            source.Set(new NetValue { Value = 9 });
            var observer = new ProfilerObserver(3);
            using var recorders = new CounterRecorders();
            using var sendMarker = Start("SECS.Net.Send");
            MemoryTransport.CreatePair(16, out var clientInner, out var serverTransport);
            var clientTransport = new GateTransport(clientInner, rejects: 1);
            var client = new Session<ClientWorld>(ClientConfig(), Schema<ClientWorld, ClientAuthorizer>(), clientTransport, observer);
            var server = new Session<ServerWorld>(ServerConfig(), Schema<ServerWorld, ModeAuthorizer>(), serverTransport, observer);
            var acceptedReceiver = World<ServerWorld>.RegisterEventReceiver<CommandAcceptedEvent<NetCommand>>();
            var rejectedReceiver = World<ServerWorld>.RegisterEventReceiver<CommandRejectedEvent<NetCommand>>();
            SessionStats clientStats;
            SessionStats serverStats;
            SessionStats faultStats = default;
            try
            {
                PumpEstablished(client, server, 0, 6);

                var command = new NetCommand { Value = 4 };
                Assert.That(client.Enqueue(in command, 6), Is.EqualTo(EnqueueResult.Queued));
                client.Step(7);
                server.Step(7);

                ModeAuthorizer.Reject = false;
                command.Value = 5;
                Assert.That(client.Enqueue(in command, 7), Is.EqualTo(EnqueueResult.Queued));
                client.Step(8);
                server.Step(8);

                Assert.That(server.Capture(0), Is.EqualTo(CaptureResult.Success));
                server.Step(9);
                client.Step(9);
                client.Step(10);
                Assert.That(source.GID.TryUnpack<ClientWorld>(out var replica), Is.True);
                replica.Delete<ReplicatedTag>();

                Assert.That(server.Capture(1), Is.EqualTo(CaptureResult.Success));
                server.Step(10);
                client.Step(11);
                client.Step(12);
                server.Step(11);
                client.Step(13);

                clientStats = client.Stats;
                serverStats = server.Stats;
                Assert.That(serverStats.CommandsRejected, Is.EqualTo(1));
                Assert.That(serverStats.CommandsAccepted, Is.EqualTo(1));
                Assert.That(serverStats.SnapshotsCaptured, Is.EqualTo(2));
                Assert.That(clientStats.SnapshotsApplied, Is.EqualTo(1));
                Assert.That(clientStats.Resyncs, Is.EqualTo(1));
                Assert.That(serverStats.Resyncs, Is.EqualTo(1));
            }
            finally
            {
                ModeAuthorizer.Reject = true;
                client.Dispose();
                server.Dispose();
                World<ServerWorld>.DeleteEventReceiver(ref acceptedReceiver);
                World<ServerWorld>.DeleteEventReceiver(ref rejectedReceiver);
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }

            CreateWorld<FaultWorld>(ChunkOwnerType.Other);
            MemoryTransport.CreatePair(4, out var faultTransport, out var peer);
            try
            {
                using var faultSession = new Session<FaultWorld>(ClientConfig(), EmptySchema<FaultWorld>(),
                    faultTransport, observer);
                var malformed = PacketLease.Rent(1);
                malformed.CapacitySpan[0] = 0xff;
                malformed.SetLength(1);
                Assert.That(peer.TrySend(Channel.ReliableOrdered, ref malformed), Is.True);
                faultSession.Step(0);
                Assert.That(faultSession.State, Is.EqualTo(SessionState.Faulted));
                faultStats = faultSession.Stats;
            }
            finally
            {
                peer.Dispose();
                DestroyWorld<FaultWorld>();
            }

            yield return null;

            Assert.That(recorders.WireIn.LastValue, Is.EqualTo((long)(clientStats.ReceivedBytes + serverStats.ReceivedBytes + faultStats.ReceivedBytes)));
            Assert.That(recorders.WireOut.LastValue, Is.EqualTo((long)(clientStats.SentBytes + serverStats.SentBytes + faultStats.SentBytes)));
            Assert.That(recorders.Decoded.LastValue, Is.EqualTo((long)(clientStats.DecodedBytes + serverStats.DecodedBytes + faultStats.DecodedBytes)));
            Assert.That(recorders.Commands.LastValue, Is.EqualTo(2));
            Assert.That(recorders.Captures.LastValue, Is.EqualTo(2));
            Assert.That(recorders.Applies.LastValue, Is.EqualTo(1));
            Assert.That(recorders.Retries.LastValue, Is.EqualTo((long)(clientStats.SendRetries + serverStats.SendRetries + faultStats.SendRetries)));
            Assert.That(recorders.Declines.LastValue, Is.EqualTo(1));
            Assert.That(recorders.Faults.LastValue, Is.EqualTo(1));
            Assert.That(recorders.Resyncs.LastValue, Is.EqualTo(2));
            Assert.That(CompletedSamples(sendMarker),
                Is.EqualTo((long)(clientStats.SentPackets + serverStats.SentPackets + 1)));
        }

        [UnityTest]
        public IEnumerator StepMarkerRemainsBalancedAfterTransportException()
        {
            var observer = new ProfilerObserver(8);
            using var recorder = Start("SECS.Net.Step");
            CreateWorld<ThrowWorld>(ChunkOwnerType.Other);
            try
            {
                using var failed = new Session<ThrowWorld>(ClientConfig(), EmptySchema<ThrowWorld>(),
                    new ThrowStepTransport(), observer);
                Assert.Throws<InvalidOperationException>(() => failed.Step(17));
            }
            finally
            {
                DestroyWorld<ThrowWorld>();
            }

            CreateWorld<SentinelWorld>(ChunkOwnerType.Other);
            try
            {
                MemoryTransport.CreatePair(2, out var transport, out var peer);
                using var sentinel = new Session<SentinelWorld>(ClientConfig(), EmptySchema<SentinelWorld>(),
                    transport, observer);
                Assert.DoesNotThrow(() => sentinel.Step(18));
                peer.Dispose();
            }
            finally
            {
                DestroyWorld<SentinelWorld>();
            }

            yield return null;
            Assert.That(recorder.Valid, Is.True);
            Assert.That(CompletedSamples(recorder), Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator SendMarkerRemainsBalancedAfterTransportException()
        {
            var observer = new ProfilerObserver(9);
            using var recorder = Start("SECS.Net.Send");
            CreateWorld<SendWorld>(ChunkOwnerType.Other);
            try
            {
                MemoryTransport.CreatePair(4, out var inner, out var peer);
                using var session = new Session<SendWorld>(ClientConfig(), EmptySchema<SendWorld>(),
                    new ThrowOnceSendTransport(inner), observer);
                Assert.Throws<InvalidOperationException>(() => session.Step(0));
                Assert.DoesNotThrow(() => session.Step(1));
                peer.Dispose();
            }
            finally
            {
                DestroyWorld<SendWorld>();
            }

            yield return null;
            Assert.That(recorder.Valid, Is.True);
            Assert.That(CompletedSamples(recorder), Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator DispatchMarkerRemainsBalancedAfterAuthorizerException()
        {
            var observer = new ProfilerObserver(10);
            using var recorder = Start("SECS.Net.Dispatch");
            CreateWorld<DispatchThrowClientWorld>(ChunkOwnerType.Other);
            CreateWorld<DispatchThrowServerWorld>(ChunkOwnerType.Self);
            var throwReceiver = World<DispatchThrowServerWorld>
                .RegisterEventReceiver<CommandAcceptedEvent<NetCommand>>();
            try
            {
                MemoryTransport.CreatePair(8, out var clientTransport, out var serverTransport);
                using var client = new Session<DispatchThrowClientWorld>(ClientConfig(),
                    Schema<DispatchThrowClientWorld, DispatchThrowClientAuthorizer>(), clientTransport, observer);
                using var server = new Session<DispatchThrowServerWorld>(ServerConfig(),
                    Schema<DispatchThrowServerWorld, DispatchThrowServerAuthorizer>(), serverTransport, observer);
                PumpEstablished(client, server, 0, 3);
                var command = new NetCommand { Value = 21 };
                Assert.That(client.Enqueue(in command, 2), Is.EqualTo(EnqueueResult.Queued));
                client.Step(3);
                Assert.Throws<InvalidOperationException>(() => server.Step(3));
            }
            finally
            {
                World<DispatchThrowServerWorld>.DeleteEventReceiver(ref throwReceiver);
                DestroyWorld<DispatchThrowClientWorld>();
                DestroyWorld<DispatchThrowServerWorld>();
            }

            CreateWorld<DispatchSentinelClientWorld>(ChunkOwnerType.Other);
            CreateWorld<DispatchSentinelServerWorld>(ChunkOwnerType.Self);
            var sentinelReceiver = World<DispatchSentinelServerWorld>
                .RegisterEventReceiver<CommandAcceptedEvent<NetCommand>>();
            try
            {
                MemoryTransport.CreatePair(8, out var clientTransport, out var serverTransport);
                using var client = new Session<DispatchSentinelClientWorld>(ClientConfig(),
                    Schema<DispatchSentinelClientWorld, DispatchSentinelClientAuthorizer>(), clientTransport, observer);
                using var server = new Session<DispatchSentinelServerWorld>(ServerConfig(),
                    Schema<DispatchSentinelServerWorld, DispatchSentinelServerAuthorizer>(), serverTransport, observer);
                PumpEstablished(client, server, 0, 3);
                var command = new NetCommand { Value = 22 };
                Assert.That(client.Enqueue(in command, 2), Is.EqualTo(EnqueueResult.Queued));
                client.Step(3);
                Assert.DoesNotThrow(() => server.Step(3));
                Assert.That(server.Stats.CommandsAccepted, Is.EqualTo(1));
            }
            finally
            {
                World<DispatchSentinelServerWorld>.DeleteEventReceiver(ref sentinelReceiver);
                DestroyWorld<DispatchSentinelClientWorld>();
                DestroyWorld<DispatchSentinelServerWorld>();
            }

            yield return null;
            Assert.That(recorder.Valid, Is.True);
            Assert.That(CompletedSamples(recorder), Is.EqualTo(2));
        }

        private static ProfilerRecorder Start(string name) => ProfilerRecorder.StartNew(
            ProfilerCategory.Network,
            name,
            64,
            ProfilerRecorderOptions.StartImmediately |
            ProfilerRecorderOptions.WrapAroundWhenCapacityReached |
            ProfilerRecorderOptions.SumAllSamplesInFrame);

        private static long CompletedSamples(ProfilerRecorder recorder)
        {
            long count = 0;
            for (var i = 0; i < recorder.Count; i++)
                count += recorder.GetSample(i).Count;
            return count;
        }

        private static void PumpEstablished<TClient, TServer>(
            Session<TClient> client,
            Session<TServer> server,
            ulong first,
            ulong exclusiveEnd)
            where TClient : struct, IWorldType
            where TServer : struct, IWorldType
        {
            for (var step = first; step < exclusiveEnd; step++)
            {
                client.Step(step);
                server.Step(step);
            }
            Assert.That(client.State, Is.EqualTo(SessionState.Established));
            Assert.That(server.State, Is.EqualTo(SessionState.Established));
        }

        private static void CreateWorld<TWorld>(ChunkOwnerType owner) where TWorld : struct, IWorldType
        {
            World<TWorld>.Create(WorldConfig.Default());
            World<TWorld>.Types()
                .Tag<ReplicatedTag>()
                .EntityType<NetEntity>()
                .Component<NetValue>()
                .Event<CommandAcceptedEvent<NetCommand>>()
                .Event<CommandRejectedEvent<NetCommand>>();
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

        private static Schema Schema<TWorld, TAuthorizer>()
            where TWorld : struct, IWorldType
            where TAuthorizer : struct, ICommandAuthorizer<TWorld, NetCommand> =>
            new SchemaBuilder<TWorld>()
                .EntityKind<NetEntity>(EntityId)
                .Component<NetValue, NetValueCodec>(ValueId, 1, ValueCodecId, 4)
                .Command<NetCommand, NetCommandCodec, TAuthorizer>(CommandId, 1, CommandCodecId, 4)
                .Freeze();

        private static Schema EmptySchema<TWorld>() where TWorld : struct, IWorldType =>
            new SchemaBuilder<TWorld>().Freeze();

        private sealed class CounterRecorders : IDisposable
        {
            internal CounterRecorders()
            {
                WireIn = Start("SECS.Net.WireIn");
                WireOut = Start("SECS.Net.WireOut");
                Decoded = Start("SECS.Net.Decoded");
                Commands = Start("SECS.Net.Commands");
                Captures = Start("SECS.Net.Captures");
                Applies = Start("SECS.Net.Applies");
                Retries = Start("SECS.Net.Retries");
                Declines = Start("SECS.Net.Declines");
                Faults = Start("SECS.Net.Faults");
                Resyncs = Start("SECS.Net.Resyncs");
            }

            internal ProfilerRecorder WireIn;
            internal ProfilerRecorder WireOut;
            internal ProfilerRecorder Decoded;
            internal ProfilerRecorder Commands;
            internal ProfilerRecorder Captures;
            internal ProfilerRecorder Applies;
            internal ProfilerRecorder Retries;
            internal ProfilerRecorder Declines;
            internal ProfilerRecorder Faults;
            internal ProfilerRecorder Resyncs;

            public void Dispose()
            {
                WireIn.Dispose(); WireOut.Dispose(); Decoded.Dispose(); Commands.Dispose(); Captures.Dispose();
                Applies.Dispose(); Retries.Dispose(); Declines.Dispose(); Faults.Dispose(); Resyncs.Dispose();
            }
        }

        private sealed class GateTransport : ITransport, ISteppedTransport
        {
            private readonly ITransport _inner;
            private readonly ISteppedTransport _stepped;
            private int _rejects;

            internal GateTransport(ITransport inner, int rejects)
            {
                _inner = inner;
                _stepped = (ISteppedTransport)inner;
                _rejects = rejects;
            }

            public TransportState State => _inner.State;
            public TransportError Error => _inner.Error;
            public void BeginStep(ulong stepIndex) => _stepped.BeginStep(stepIndex);
            public bool TrySend(Channel channel, ref PacketLease packet)
            {
                if (_rejects <= 0) return _inner.TrySend(channel, ref packet);
                _rejects--;
                return false;
            }
            public bool TryReceive(out Channel channel, out PacketLease packet) =>
                _inner.TryReceive(out channel, out packet);
            public void Dispose() => _inner.Dispose();
        }

        private sealed class ThrowStepTransport : ITransport, ISteppedTransport
        {
            public TransportState State { get; private set; } = TransportState.Connected;
            public TransportError Error { get; private set; } = TransportError.None;
            public void BeginStep(ulong stepIndex) => throw new InvalidOperationException("step");
            public bool TrySend(Channel channel, ref PacketLease packet) => false;
            public bool TryReceive(out Channel channel, out PacketLease packet)
            {
                channel = default;
                packet = default;
                return false;
            }
            public void Dispose()
            {
                State = TransportState.Disposed;
                Error = TransportError.Disposed;
            }
        }

        private sealed class ThrowOnceSendTransport : ITransport, ISteppedTransport
        {
            private readonly ITransport _inner;
            private readonly ISteppedTransport _stepped;
            private bool _throws = true;

            internal ThrowOnceSendTransport(ITransport inner)
            {
                _inner = inner;
                _stepped = (ISteppedTransport)inner;
            }

            public TransportState State => _inner.State;
            public TransportError Error => _inner.Error;
            public void BeginStep(ulong stepIndex) => _stepped.BeginStep(stepIndex);
            public bool TrySend(Channel channel, ref PacketLease packet)
            {
                if (!_throws) return _inner.TrySend(channel, ref packet);
                _throws = false;
                throw new InvalidOperationException("send");
            }
            public bool TryReceive(out Channel channel, out PacketLease packet) =>
                _inner.TryReceive(out channel, out packet);
            public void Dispose() => _inner.Dispose();
        }

        private struct NetCommand { public int Value; }
        private struct NetCommandCodec : ICodec<NetCommand>
        {
            public bool TryWrite(in NetCommand value, Span<byte> destination, out int written)
            {
                if (destination.Length < 4) { written = 0; return false; }
                BitConverter.TryWriteBytes(destination, value.Value); written = 4; return true;
            }
            public bool TryRead(ReadOnlySpan<byte> source, out NetCommand value, out int read)
            {
                if (source.Length != 4) { value = default; read = 0; return false; }
                value = new NetCommand { Value = BitConverter.ToInt32(source) }; read = 4; return true;
            }
        }
        private struct ClientAuthorizer : ICommandAuthorizer<ClientWorld, NetCommand>
        { public bool Authorize(in CommandContext context, in NetCommand command) => true; }
        private struct ModeAuthorizer : ICommandAuthorizer<ServerWorld, NetCommand>
        {
            internal static bool Reject = true;
            public bool Authorize(in CommandContext context, in NetCommand command) => !Reject;
        }
        private struct DispatchThrowClientAuthorizer : ICommandAuthorizer<DispatchThrowClientWorld, NetCommand>
        { public bool Authorize(in CommandContext context, in NetCommand command) => true; }
        private struct DispatchThrowServerAuthorizer : ICommandAuthorizer<DispatchThrowServerWorld, NetCommand>
        { public bool Authorize(in CommandContext context, in NetCommand command) => throw new InvalidOperationException("authorize"); }
        private struct DispatchSentinelClientAuthorizer : ICommandAuthorizer<DispatchSentinelClientWorld, NetCommand>
        { public bool Authorize(in CommandContext context, in NetCommand command) => true; }
        private struct DispatchSentinelServerAuthorizer : ICommandAuthorizer<DispatchSentinelServerWorld, NetCommand>
        { public bool Authorize(in CommandContext context, in NetCommand command) => true; }
        private struct NetEntity : IEntityType { public byte Id() => 27; }
        private struct NetValue : IComponent { public int Value; }
        private struct NetValueCodec : ICodec<NetValue>
        {
            public bool TryWrite(in NetValue value, Span<byte> destination, out int written)
            {
                if (destination.Length < 4) { written = 0; return false; }
                BitConverter.TryWriteBytes(destination, value.Value); written = 4; return true;
            }
            public bool TryRead(ReadOnlySpan<byte> source, out NetValue value, out int read)
            {
                if (source.Length != 4) { value = default; read = 0; return false; }
                value = new NetValue { Value = BitConverter.ToInt32(source) }; read = 4; return true;
            }
        }

        private struct ClientWorld : IWorldType { }
        private struct ServerWorld : IWorldType { }
        private struct FaultWorld : IWorldType { }
        private struct ThrowWorld : IWorldType { }
        private struct SentinelWorld : IWorldType { }
        private struct SendWorld : IWorldType { }
        private struct DispatchThrowClientWorld : IWorldType { }
        private struct DispatchThrowServerWorld : IWorldType { }
        private struct DispatchSentinelClientWorld : IWorldType { }
        private struct DispatchSentinelServerWorld : IWorldType { }
    }
}
