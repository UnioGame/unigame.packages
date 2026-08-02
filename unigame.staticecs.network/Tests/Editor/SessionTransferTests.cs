using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FFS.Libraries.StaticEcs;
using NUnit.Framework;

namespace UniGame.StaticEcs.Network.Tests
{
    public sealed class SessionTransferTests
    {
        private const uint Chunk = 19;
        private const ushort Cluster = 4;
        private static readonly TypeId CommandId = new(new Guid(401, 0, 0, new byte[8]));
        private static readonly CodecId CommandCodecId = new(new Guid(402, 0, 0, new byte[8]));

        [SetUp]
        public void EnterPoolTestLock() => Monitor.Enter(PoolTestGate.Sync);

        [TearDown]
        public void ExitPoolTestLock() => Monitor.Exit(PoolTestGate.Sync);

        [Test]
        public void TransferPublicSurfaceAndRoleStatePrecedenceAreFrozen()
        {
            var declared = typeof(Session<ClientWorld>).GetMembers()
                .Where(member => member.DeclaringType == typeof(Session<ClientWorld>))
                .Select(member => member.Name)
                .ToArray();
            Assert.That(declared, Does.Contain(nameof(Session<ClientWorld>.Enqueue)));
            Assert.That(declared, Does.Contain(nameof(Session<ClientWorld>.Capture)));
            Assert.That(declared, Does.Contain(nameof(Session<ClientWorld>.NeedsSnapshot)));
            Assert.That(typeof(Session<ClientWorld>).Assembly.GetTypes().Any(type => type.Name.Contains("Sam" + "ple")), Is.False);

            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true);
            MemoryTransport.CreatePair(4, out var clientTransport, out var serverTransport);
            var client = new Session<ClientWorld>(ClientConfig(), EmptySchema<ClientWorld>(), clientTransport);
            var server = new Session<ServerWorld>(ServerConfig(), EmptySchema<ServerWorld>(), serverTransport);
            try
            {
                var command = new TransferCommand { Value = 1 };
                Assert.That(client.Enqueue(in command, 0), Is.EqualTo(EnqueueResult.Unavailable));
                Assert.That(server.Enqueue(in command, 0), Is.EqualTo(EnqueueResult.Unavailable));
                Assert.That(client.Capture(PacketHeader.NoneTick), Is.EqualTo(CaptureResult.WrongRole));
                Assert.That(server.Capture(PacketHeader.NoneTick), Is.EqualTo(CaptureResult.ScopeInvalid));
                Assert.That(client.NeedsSnapshot, Is.False);
                Assert.That(server.NeedsSnapshot, Is.False);

                PumpEstablished(client, server);
                Assert.That(server.NeedsSnapshot, Is.True);
                Assert.Throws<ArgumentOutOfRangeException>(() => server.Capture(PacketHeader.NoneTick));
                Assert.That(server.Capture(0), Is.EqualTo(CaptureResult.Success));
                Assert.That(server.NeedsSnapshot, Is.False);
                Assert.Throws<ArgumentOutOfRangeException>(() => server.Capture(0));

                client.Dispose();
                Assert.Throws<ObjectDisposedException>(() => client.Enqueue(in command, 0));
                Assert.Throws<ObjectDisposedException>(() => client.Capture(0));
                server.Dispose();
                Assert.Throws<ObjectDisposedException>(() => server.Capture(1));
            }
            finally
            {
                client.Dispose();
                server.Dispose();
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void CaptureSupersedesUnsentSnapshotAndBothHistoriesOwnIndependentCanonicalBytes()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true);
            MemoryTransport.CreatePair(4, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), EmptySchema<ClientWorld>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), EmptySchema<ServerWorld>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                Assert.That(server.Capture(0), Is.EqualTo(CaptureResult.Success));
                Assert.That(server.Capture(1), Is.EqualTo(CaptureResult.Success));
                Assert.That(server.History.TryGet(0, out var first), Is.EqualTo(HistoryLookup.Found));
                Assert.That(server.History.TryGet(1, out var second), Is.EqualTo(HistoryLookup.Found));
                Assert.That(first.Generated.IsValid, Is.True);
                Assert.That(second.Generated.IsValid, Is.True);

                AssertFlag(server.Step(3), StepResult.Sent);
                AssertFlag(client.Step(3), StepResult.Received);
                Assert.That(client.History.TryGet(0, out _), Is.EqualTo(HistoryLookup.Evicted));
                Assert.That(client.History.TryGet(1, out var received), Is.EqualTo(HistoryLookup.Found));
                Assert.That(received.Received.IsValid, Is.True);
                Assert.That(received.ReceivedHash, Is.EqualTo(received.PostApplyHash));
                Assert.That(received.PostApply.IsValid, Is.False);
                Assert.That(first.Generated.IsValid, Is.True);
                Assert.That(second.Generated.IsValid, Is.True);
                AssertFlag(server.Step(4), StepResult.Received);
                Assert.That(server.State, Is.EqualTo(SessionState.Established));
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void CommandBatchDispatchesTrustedContextAndAcknowledgesRetention()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, true);
            var receiver = World<ServerWorld>.RegisterEventReceiver<CommandAcceptedEvent<TransferCommand>>();
            MemoryTransport.CreatePair(4, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), CommandSchema<ClientWorld, ClientAuthorizer>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, ServerAuthorizer>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                var command = new TransferCommand { Value = 42 };
                Assert.That(client.Enqueue(in command, 77), Is.EqualTo(EnqueueResult.Queued));
                AssertFlag(client.Step(3), StepResult.Sent);
                var serverStep = server.Step(3);
                AssertFlag(serverStep, StepResult.Received);
                AssertFlag(serverStep, StepResult.Sent);
                var count = 0;
                foreach (var item in receiver)
                {
                    count++;
                    Assert.That(item.Value.Command.Value, Is.EqualTo(42));
                    Assert.That(item.Value.Context.PeerId, Is.EqualTo(9));
                    Assert.That(item.Value.Context.Sequence, Is.EqualTo(1));
                    Assert.That(item.Value.Context.ClientTick, Is.EqualTo(77));
                }
                Assert.That(count, Is.EqualTo(1));
                AssertFlag(client.Step(4), StepResult.Received);
                Assert.That(client.State, Is.EqualTo(SessionState.Established));
            }
            finally
            {
                World<ServerWorld>.DeleteEventReceiver(ref receiver);
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void AuthorizationRejectionStillAdvancesCumulativeCommandAcknowledgement()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, true);
            var rejected = World<ServerWorld>.RegisterEventReceiver<CommandRejectedEvent<TransferCommand>>();
            WireTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), CommandSchema<ClientWorld, ClientAuthorizer>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, RejectAuthorizer>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                var command = new TransferCommand { Value = 6 };
                client.Enqueue(in command, 12);
                client.Step(3);
                var step = server.Step(3);
                AssertFlag(step, StepResult.Received);
                AssertFlag(step, StepResult.Sent);
                var count = 0;
                foreach (var item in rejected)
                {
                    count++;
                    Assert.That(item.Value.Context.Sequence, Is.EqualTo(1));
                }
                Assert.That(count, Is.EqualTo(1));
                Assert.That(Header(serverTransport.Attempts.Last().Bytes).AcknowledgedCommandSequence, Is.EqualTo(1));
                AssertFlag(client.Step(4), StepResult.Received);
                Assert.That(client.State, Is.EqualTo(SessionState.Established));
            }
            finally
            {
                World<ServerWorld>.DeleteEventReceiver(ref rejected);
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void ReturnedEntityConflictQueuesResyncWithoutHistoryAndRestoresServerDemand()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, true);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true);
            MemoryTransport.CreatePair(4, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), EmptySchema<ClientWorld>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), EmptySchema<ServerWorld>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                Assert.That(server.Capture(0), Is.EqualTo(CaptureResult.Success));
                var id = (Chunk << Const.ENTITIES_IN_CHUNK_SHIFT) + 1;
                World<ClientWorld>.NewEntityByGID<TestEntity>(new EntityGID(id, 1, Cluster));
                AssertFlag(server.Step(3), StepResult.Sent);
                var clientStep = client.Step(3);
                AssertFlag(clientStep, StepResult.Received);
                AssertFlag(clientStep, StepResult.Sent);
                Assert.That(client.History.TryGet(0, out _), Is.EqualTo(HistoryLookup.NotYetSeen));
                Assert.That(server.NeedsSnapshot, Is.False);
                AssertFlag(server.Step(4), StepResult.Received);
                Assert.That(server.NeedsSnapshot, Is.True);
                Assert.That(server.State, Is.EqualTo(SessionState.Established));
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void ServerDispatcherConstructionFailureDoesNotTakeTransportOwnership()
        {
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true);
            var transport = WireTransport.Unpaired();
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, ServerAuthorizer>(), transport));
                Assert.That(transport.DisposeCount, Is.Zero);
                Assert.That(transport.State, Is.EqualTo(TransportState.Connected));
            }
            finally
            {
                transport.Dispose();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void CaptureCallbackThrowIsAtomicAndSameTickCanRetry()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, true, false, true);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, false, true);
            var entity = World<ServerWorld>.NewEntityInChunk<TestEntity>(Chunk);
            entity.Set<ReplicatedTag>();
            entity.Set(new TransferValue { Value = 17 });
            MemoryTransport.CreatePair(4, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), ValueSchema<ClientWorld>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), ValueSchema<ServerWorld>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                TransferValueCodec.ThrowWrite = true;
                Assert.Throws<TransferTestException>(() => server.Capture(0));
                Assert.That(server.State, Is.EqualTo(SessionState.Established));
                Assert.That(server.NeedsSnapshot, Is.True);
                Assert.That(server.History.Count, Is.Zero);

                TransferValueCodec.ThrowWrite = false;
                Assert.That(server.Capture(0), Is.EqualTo(CaptureResult.Success));
                Assert.That(server.History.TryGet(0, out var record), Is.EqualTo(HistoryLookup.Found));
                Assert.That(record.Generated.IsValid, Is.True);
                Assert.That(server.NeedsSnapshot, Is.False);
            }
            finally
            {
                TransferValueCodec.ThrowWrite = false;
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void CommandRetryIsByteIdenticalAndRequestedCloseReusesItsSequence()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, true);
            WireTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), CommandSchema<ClientWorld, ClientAuthorizer>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, ServerAuthorizer>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                var command = new TransferCommand { Value = 4 };
                Assert.That(client.Enqueue(in command, 8), Is.EqualTo(EnqueueResult.Queued));
                clientTransport.ReturnFalseNextSend = true;
                Assert.That(client.Step(3), Is.EqualTo(StepResult.None));
                var failed = clientTransport.Attempts.Last();
                Assert.That(Header(failed.Bytes).Kind, Is.EqualTo(PacketKind.CommandBatch));
                Assert.That(Header(failed.Bytes).PacketSequence, Is.EqualTo(3));

                client.Close();
                AssertFlag(client.Step(4), StepResult.Sent);
                var disconnect = clientTransport.Attempts.Last();
                var disconnectHeader = Header(disconnect.Bytes);
                Assert.That(disconnectHeader.Kind, Is.EqualTo(PacketKind.Disconnect));
                Assert.That(disconnectHeader.PacketSequence, Is.EqualTo(3));
                Assert.That(failed.Alias.IsValid, Is.False);
                AssertFlag(server.Step(3), StepResult.Received);
                Assert.That(server.State, Is.EqualTo(SessionState.Closed));
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void CommandRetryFreezesBytesAndDispatchesOnlyAfterAcceptance()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, true);
            var receiver = World<ServerWorld>.RegisterEventReceiver<CommandAcceptedEvent<TransferCommand>>();
            WireTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), CommandSchema<ClientWorld, ClientAuthorizer>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, ServerAuthorizer>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                var first = new TransferCommand { Value = 1 };
                var second = new TransferCommand { Value = 2 };
                Assert.That(client.Enqueue(in first, 10), Is.EqualTo(EnqueueResult.Queued));
                clientTransport.ReturnFalseNextSend = true;
                client.Step(3);
                var failed = clientTransport.Attempts.Last().Bytes;
                Assert.That(client.Enqueue(in second, 11), Is.EqualTo(EnqueueResult.Queued));
                AssertFlag(client.Step(4), StepResult.Sent);
                Assert.That(clientTransport.Attempts.Last().Bytes, Is.EqualTo(failed));
                AssertFlag(server.Step(3), StepResult.Received);
                var count = 0;
                foreach (var _ in receiver) count++;
                Assert.That(count, Is.EqualTo(1));

                AssertFlag(client.Step(5), StepResult.Sent);
                AssertFlag(server.Step(4), StepResult.Received);
                count = 0;
                foreach (var _ in receiver) count++;
                Assert.That(count, Is.EqualTo(1));
            }
            finally
            {
                World<ServerWorld>.DeleteEventReceiver(ref receiver);
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ThrowingSendCleansEncodedOwnerAndRetainsFrozenReliableIntent(bool transferFirst)
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, true);
            WireTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), CommandSchema<ClientWorld, ClientAuthorizer>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, ServerAuthorizer>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                var command = new TransferCommand { Value = 10 };
                client.Enqueue(in command, 3);
                if (transferFirst) clientTransport.TransferThenThrowNextSend = true;
                else clientTransport.ThrowNextSend = true;
                Assert.Throws<TransferTestException>(() => client.Step(3));
                var failed = clientTransport.Attempts.Last();
                Assert.That(failed.Alias.IsValid, Is.False);
                AssertFlag(client.Step(4), StepResult.Sent);
                Assert.That(clientTransport.Attempts.Last().Bytes, Is.EqualTo(failed.Bytes));
                Assert.That(Header(failed.Bytes).PacketSequence, Is.EqualTo(3));
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void SnapshotRetryFreezesCommandAcknowledgementAndLaterAckAdvances()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, true);
            var receiver = World<ServerWorld>.RegisterEventReceiver<CommandAcceptedEvent<TransferCommand>>();
            WireTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), CommandSchema<ClientWorld, ClientAuthorizer>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, ServerAuthorizer>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                var command = new TransferCommand { Value = 1 };
                client.Enqueue(in command, 1);
                client.Step(3);
                server.Capture(0);
                serverTransport.ReturnFalseNextSend = true;
                AssertFlag(server.Step(3), StepResult.Received);
                var failed = serverTransport.Attempts.Last();
                var frozenHeader = Header(failed.Bytes);
                Assert.That(frozenHeader.Kind, Is.EqualTo(PacketKind.FullSnapshot));
                Assert.That(frozenHeader.AcknowledgedCommandSequence, Is.EqualTo(1));

                command.Value = 2;
                client.Enqueue(in command, 2);
                client.Step(4);
                var retryStep = server.Step(4);
                AssertFlag(retryStep, StepResult.Received);
                AssertFlag(retryStep, StepResult.Sent);
                Assert.That(serverTransport.Attempts.Last().Bytes, Is.EqualTo(failed.Bytes));
                AssertFlag(client.Step(5), StepResult.Received);
                AssertFlag(server.Step(5), StepResult.Sent);
                var ack = Header(serverTransport.Attempts.Last().Bytes);
                Assert.That(ack.Kind, Is.EqualTo(PacketKind.Ack));
                Assert.That(ack.AcknowledgedCommandSequence, Is.EqualTo(2));
            }
            finally
            {
                World<ServerWorld>.DeleteEventReceiver(ref receiver);
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void FutureCommandAcknowledgementFaultsBeforeOutboxMutation()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, true);
            WireTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), CommandSchema<ClientWorld, ClientAuthorizer>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, ServerAuthorizer>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                var command = new TransferCommand { Value = 3 };
                client.Enqueue(in command, 0);
                client.Step(3);
                PacketLease packet = default;
                Assert.That(SessionProtocol.TryEncodeTransfer(PacketKind.Ack, Channel.ReliableOrdered,
                    client.Epoch, 3, PacketHeader.NoneTick, default, PacketHeader.NoneTick, 2,
                    ReadOnlySpan<byte>.Empty, null, out packet), Is.True);
                serverTransport.TrySend(Channel.ReliableOrdered, ref packet);
                AssertFlag(client.Step(4), StepResult.Received);
                Assert.That(client.State, Is.EqualTo(SessionState.Faulted));
                Assert.That(client.Error, Is.EqualTo(SessionError.Protocol));
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void EmptyCommandBatchAndNoReceiverAreTerminalWithoutAcknowledgement()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, true);
            WireTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), CommandSchema<ClientWorld, ClientAuthorizer>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, ServerAuthorizer>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                PacketLease empty = default;
                Assert.That(SessionProtocol.TryEncodeTransfer(PacketKind.CommandBatch, Channel.ReliableOrdered,
                    server.Epoch, 3, PacketHeader.NoneTick, CommandSchema<ClientWorld, ClientAuthorizer>().Hash,
                    PacketHeader.NoneTick, 0, new byte[4],
                    CommandSchema<ClientWorld, ClientAuthorizer>(), out empty), Is.True);
                clientTransport.TrySend(Channel.ReliableOrdered, ref empty);
                AssertFlag(server.Step(3), StepResult.Received);
                Assert.That(server.State, Is.EqualTo(SessionState.Faulted));
                Assert.That(serverTransport.Attempts.Count, Is.EqualTo(2));
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }

            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, true);
            WireTransport.CreatePair(8, out clientTransport, out serverTransport);
            using var clientNoReceiver = new Session<ClientWorld>(ClientConfig(), CommandSchema<ClientWorld, ClientAuthorizer>(), clientTransport);
            using var serverNoReceiver = new Session<ServerWorld>(ServerConfig(), CommandSchema<ServerWorld, ServerAuthorizer>(), serverTransport);
            try
            {
                PumpEstablished(clientNoReceiver, serverNoReceiver);
                var command = new TransferCommand { Value = 9 };
                clientNoReceiver.Enqueue(in command, 0);
                clientNoReceiver.Step(3);
                AssertFlag(serverNoReceiver.Step(3), StepResult.Received);
                Assert.That(serverNoReceiver.State, Is.EqualTo(SessionState.Faulted));
                Assert.That(serverNoReceiver.Error, Is.EqualTo(SessionError.Topology));
                Assert.That(serverNoReceiver.Reason, Is.Null);
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [TestCase(ResyncReason.LocalStateConflict)]
        [TestCase(ResyncReason.SnapshotRejected)]
        [TestCase(ResyncReason.HashMismatch)]
        [TestCase(ResyncReason.QueueOverflow)]
        [TestCase(ResyncReason.UnexpectedEpoch)]
        public void EveryDefinedResyncReasonRestoresServerDemand(ResyncReason reason)
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, false);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true);
            WireTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), EmptySchema<ClientWorld>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), EmptySchema<ServerWorld>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                Assert.That(server.Capture(0), Is.EqualTo(CaptureResult.Success));
                var payload = new byte[8];
                Assert.That(PayloadCodec.TryWrite(new ResyncRequestPayload
                {
                    Reason = reason,
                    LastAcceptedTick = PacketHeader.NoneTick
                }, payload, out var written), Is.True);
                Assert.That(written, Is.EqualTo(8));
                PacketLease packet = default;
                Assert.That(SessionProtocol.TryEncodeTransfer(PacketKind.ResyncRequest, Channel.ReliableOrdered,
                    server.Epoch, 3, PacketHeader.NoneTick, default, PacketHeader.NoneTick, 0,
                    payload, null, out packet), Is.True);
                clientTransport.TrySend(Channel.ReliableOrdered, ref packet);
                AssertFlag(server.Step(3), StepResult.Received);
                Assert.That(server.NeedsSnapshot, Is.True);
                Assert.That(server.State, Is.EqualTo(SessionState.Established));
            }
            finally
            {
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [Test]
        public void ApplyCallbackThrowFaultsTopologyRethrowsAndDoesNotRecordHistory()
        {
            CreateWorld<ClientWorld>(ChunkOwnerType.Other, true, false, true);
            CreateWorld<ServerWorld>(ChunkOwnerType.Self, true, false, true);
            var entity = World<ServerWorld>.NewEntityInChunk<TestEntity>(Chunk);
            entity.Set<ReplicatedTag>();
            entity.Set(new TransferValue { Value = 21 });
            WireTransport.CreatePair(8, out var clientTransport, out var serverTransport);
            using var client = new Session<ClientWorld>(ClientConfig(), ValueSchema<ClientWorld>(), clientTransport);
            using var server = new Session<ServerWorld>(ServerConfig(), ValueSchema<ServerWorld>(), serverTransport);
            try
            {
                PumpEstablished(client, server);
                server.Capture(0);
                server.Step(3);
                var history = client.History;
                TransferValueCodec.ThrowRead = true;
                Assert.Throws<TransferTestException>(() => client.Step(3));
                Assert.That(client.State, Is.EqualTo(SessionState.Faulted));
                Assert.That(client.Error, Is.EqualTo(SessionError.Topology));
                Assert.That(client.Reason, Is.Null);
                Assert.That(history.Count, Is.Zero);
                Assert.That(client.Step(4), Is.EqualTo(StepResult.None));
            }
            finally
            {
                TransferValueCodec.ThrowRead = false;
                DestroyWorld<ClientWorld>();
                DestroyWorld<ServerWorld>();
            }
        }

        [TestCase(ApplyResult.SchemaMismatch, SessionError.Schema, DisconnectReason.SchemaMismatch, false, (ResyncReason)0)]
        [TestCase(ApplyResult.WrongPayload, SessionError.Protocol, DisconnectReason.ProtocolViolation, false, (ResyncReason)0)]
        [TestCase(ApplyResult.WrongRole, SessionError.Topology, null, false, (ResyncReason)0)]
        [TestCase(ApplyResult.ScopeInvalid, SessionError.Topology, null, false, (ResyncReason)0)]
        [TestCase(ApplyResult.EntityConflict, SessionError.None, null, true, ResyncReason.LocalStateConflict)]
        [TestCase(ApplyResult.InvalidEntity, SessionError.None, null, true, ResyncReason.SnapshotRejected)]
        [TestCase(ApplyResult.MissingTarget, SessionError.None, null, true, ResyncReason.SnapshotRejected)]
        [TestCase(ApplyResult.LimitExceeded, SessionError.None, null, true, ResyncReason.SnapshotRejected)]
        public void ApplyFailureMappingIsFrozen(
            ApplyResult apply,
            SessionError expectedError,
            DisconnectReason? expectedReason,
            bool expectedResync,
            ResyncReason expectedResyncReason)
        {
            var queues = Session<ClientWorld>.TryMapApplyFailure(apply, out var error, out var reason, out var resync);
            Assert.That(queues, Is.EqualTo(expectedResync));
            Assert.That(error, Is.EqualTo(expectedError));
            Assert.That(reason, Is.EqualTo(expectedReason));
            Assert.That(resync, Is.EqualTo(expectedResyncReason));
        }

        private static void PumpEstablished(Session<ClientWorld> client, Session<ServerWorld> server)
        {
            for (ulong step = 0; step < 3; step++)
            {
                client.Step(step);
                server.Step(step);
            }
            Assert.That(client.State, Is.EqualTo(SessionState.Established));
            Assert.That(server.State, Is.EqualTo(SessionState.Established));
        }

        private static void AssertFlag(StepResult value, StepResult flag) =>
            Assert.That((value & flag) != 0, Is.True);

        private static void CreateWorld<TWorld>(
            ChunkOwnerType owner,
            bool registerEntity,
            bool registerCommandEvents = false,
            bool registerValue = false)
            where TWorld : struct, IWorldType
        {
            World<TWorld>.Create(WorldConfig.Default());
            World<TWorld>.Types().Tag<ReplicatedTag>();
            if (registerEntity) World<TWorld>.Types().EntityType<TestEntity>();
            if (registerValue) World<TWorld>.Types().Component<TransferValue>();
            if (registerCommandEvents)
                World<TWorld>.Types().Event<CommandAcceptedEvent<TransferCommand>>()
                    .Event<CommandRejectedEvent<TransferCommand>>();
            World<TWorld>.Initialize();
            World<TWorld>.RegisterCluster(Cluster);
            World<TWorld>.RegisterChunk(Chunk, owner, Cluster);
        }

        private static void DestroyWorld<TWorld>() where TWorld : struct, IWorldType
        {
            if (World<TWorld>.Status != WorldStatus.NotCreated) World<TWorld>.Destroy();
        }

        private static SessionConfig ClientConfig() => SessionConfig.Client(21, 20, 40);
        private static SessionConfig ServerConfig() => SessionConfig.Server(7, 9, 33, 30, Mapping());
        private static ChunkMapping[] Mapping() => new[]
        {
            new ChunkMapping { Chunk = Chunk, Cluster = Cluster, Role = 1 }
        };
        private static Schema EmptySchema<TWorld>() where TWorld : struct, IWorldType =>
            new SchemaBuilder<TWorld>().Freeze();
        private static Schema CommandSchema<TWorld, TAuthorizer>()
            where TWorld : struct, IWorldType
            where TAuthorizer : struct, ICommandAuthorizer<TWorld, TransferCommand> =>
            new SchemaBuilder<TWorld>()
                .Command<TransferCommand, TransferCommandCodec, TAuthorizer>(CommandId, 1, CommandCodecId, 4)
                .Freeze();

        private static Schema ValueSchema<TWorld>() where TWorld : struct, IWorldType =>
            new SchemaBuilder<TWorld>()
                .EntityKind<TestEntity>(new TypeId(new Guid(403, 0, 0, new byte[8])))
                .Component<TransferValue, TransferValueCodec>(
                    new TypeId(new Guid(404, 0, 0, new byte[8])), 1,
                    new CodecId(new Guid(405, 0, 0, new byte[8])), 4)
                .Freeze();

        private static PacketHeader Header(byte[] bytes)
        {
            Assert.That(PacketHeader.TryRead(bytes, out var header), Is.True);
            return header;
        }

        private struct ClientWorld : IWorldType { }
        private struct ServerWorld : IWorldType { }
        private struct TestEntity : IEntityType { public byte Id() => 1; }
        private struct TransferCommand { public int Value; }
        private struct TransferValue : IComponent { public int Value; }
        private struct ClientAuthorizer : ICommandAuthorizer<ClientWorld, TransferCommand>
        {
            public bool Authorize(in CommandContext context, in TransferCommand command) => true;
        }
        private struct ServerAuthorizer : ICommandAuthorizer<ServerWorld, TransferCommand>
        {
            public bool Authorize(in CommandContext context, in TransferCommand command) => true;
        }
        private struct RejectAuthorizer : ICommandAuthorizer<ServerWorld, TransferCommand>
        {
            public bool Authorize(in CommandContext context, in TransferCommand command) => false;
        }
        private struct TransferCommandCodec : ICodec<TransferCommand>
        {
            public bool TryWrite(in TransferCommand value, Span<byte> destination, out int written)
            {
                if (destination.Length < 4) { written = 0; return false; }
                BitConverter.TryWriteBytes(destination, value.Value);
                written = 4;
                return true;
            }

            public bool TryRead(ReadOnlySpan<byte> source, out TransferCommand value, out int read)
            {
                if (source.Length != 4) { value = default; read = 0; return false; }
                value = new TransferCommand { Value = BitConverter.ToInt32(source) };
                read = 4;
                return true;
            }
        }

        private sealed class TransferTestException : Exception { }

        private struct TransferValueCodec : ICodec<TransferValue>
        {
            internal static bool ThrowWrite;
            internal static bool ThrowRead;

            public bool TryWrite(in TransferValue value, Span<byte> destination, out int written)
            {
                if (ThrowWrite) throw new TransferTestException();
                if (destination.Length < 4) { written = 0; return false; }
                BitConverter.TryWriteBytes(destination, value.Value);
                written = 4;
                return true;
            }

            public bool TryRead(ReadOnlySpan<byte> source, out TransferValue value, out int read)
            {
                if (ThrowRead) throw new TransferTestException();
                if (source.Length != 4) { value = default; read = 0; return false; }
                value = new TransferValue { Value = BitConverter.ToInt32(source) };
                read = 4;
                return true;
            }
        }

        private readonly struct SendAttempt
        {
            internal SendAttempt(Channel channel, byte[] bytes, PacketLease alias)
            {
                Channel = channel;
                Bytes = bytes;
                Alias = alias;
            }

            internal Channel Channel { get; }
            internal byte[] Bytes { get; }
            internal PacketLease Alias { get; }
        }

        private sealed class WireTransport : ITransport, ISteppedTransport
        {
            private readonly Queue<QueuedPacket> _incoming = new();
            private readonly int _capacity;
            private WireTransport _peer;

            private WireTransport(int capacity)
            {
                _capacity = capacity;
                State = TransportState.Connected;
                Error = TransportError.None;
            }

            internal readonly List<SendAttempt> Attempts = new();
            internal bool ReturnFalseNextSend;
            internal bool ThrowNextSend;
            internal bool TransferThenThrowNextSend;
            internal int DisposeCount;

            public TransportState State { get; private set; }
            public TransportError Error { get; private set; }

            internal static WireTransport Unpaired() => new(8);

            internal static void CreatePair(int capacity, out WireTransport left, out WireTransport right)
            {
                left = new WireTransport(capacity);
                right = new WireTransport(capacity);
                left._peer = right;
                right._peer = left;
            }

            public void BeginStep(ulong stepIndex) { }

            public bool TrySend(Channel channel, ref PacketLease packet)
            {
                var alias = packet;
                Attempts.Add(new SendAttempt(channel, packet.Span.ToArray(), alias));
                if (TransferThenThrowNextSend)
                {
                    TransferThenThrowNextSend = false;
                    var transferred = PacketLease.Transfer(ref packet);
                    transferred.Dispose();
                    throw new TransferTestException();
                }
                if (ThrowNextSend)
                {
                    ThrowNextSend = false;
                    throw new TransferTestException();
                }
                var owned = PacketLease.Transfer(ref packet);
                if (ReturnFalseNextSend)
                {
                    ReturnFalseNextSend = false;
                    owned.Dispose();
                    return false;
                }
                if (_peer == null || State != TransportState.Connected || _peer.State != TransportState.Connected ||
                    _peer._incoming.Count >= _capacity)
                {
                    owned.Dispose();
                    return false;
                }
                _peer._incoming.Enqueue(new QueuedPacket(channel, ref owned));
                return true;
            }

            public bool TryReceive(out Channel channel, out PacketLease packet)
            {
                channel = default;
                packet = default;
                if (_incoming.Count == 0 || State != TransportState.Connected) return false;
                var queued = _incoming.Dequeue();
                channel = queued.Channel;
                packet = queued.Take();
                return true;
            }

            public void Dispose()
            {
                if (State == TransportState.Disposed) return;
                DisposeCount++;
                while (_incoming.Count > 0) _incoming.Dequeue().Dispose();
                State = TransportState.Disposed;
                Error = TransportError.Disposed;
                var peer = _peer;
                _peer = null;
                if (peer == null) return;
                peer._peer = null;
                if (peer.State == TransportState.Connected)
                {
                    while (peer._incoming.Count > 0) peer._incoming.Dequeue().Dispose();
                    peer.State = TransportState.Closed;
                    peer.Error = TransportError.RemoteClosed;
                }
            }

            private sealed class QueuedPacket : IDisposable
            {
                private PacketLease _packet;
                internal QueuedPacket(Channel channel, ref PacketLease packet)
                {
                    Channel = channel;
                    _packet = PacketLease.Transfer(ref packet);
                }

                internal Channel Channel { get; }
                internal PacketLease Take() => PacketLease.Transfer(ref _packet);
                public void Dispose()
                {
                    if (!_packet.IsValid) return;
                    _packet.Dispose();
                    _packet = default;
                }
            }
        }
    }
}
