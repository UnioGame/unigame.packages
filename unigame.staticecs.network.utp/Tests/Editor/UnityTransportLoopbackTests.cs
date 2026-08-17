namespace UniGame.StaticEcs.Network.UnityTransport.Tests
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using NUnit.Framework;

    internal sealed class UnityTransportLoopbackTests
    {
        /// <summary>Verifies reliable fragmentation and unreliable sequencing in both directions.</summary>
        [Test]
        public void LoopbackTransfersBothChannelsAndReturnsReceiveLeases()
        {
            var settings = Settings(ReservePort());
            using var server = new UnityTransportServerHost(settings);
            using var client = new UnityTransportClientHost(settings);
            var accepted = WaitForConnection(server, client);
            using var sendPool = new NetworkBufferPool(256 * 1024);

            var reliable = Packet(sendPool, PacketFlags.ReliableOrdered,
                UnityTransportSettings.MaximumReliableBytes);
            Assert.That(client.Endpoint.TrySend(reliable), Is.True);
            Assert.That(reliable.Length, Is.Zero);
            client.Flush();
            var receivedReliable = WaitForPacket(server, client, accepted);
            Assert.That(receivedReliable.Length,
                Is.EqualTo(UnityTransportSettings.MaximumReliableBytes));
            Assert.That(server.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(1));
            receivedReliable.Dispose();
            Assert.That(server.CaptureDiagnostics().OutstandingLeases, Is.Zero);

            var unreliable = Packet(sendPool, PacketFlags.UnreliableSequenced,
                settings.MaximumUnreliableBytes);
            Assert.That(accepted.TrySend(unreliable), Is.True);
            Assert.That(unreliable.Length, Is.Zero);
            server.Flush();
            var receivedUnreliable = WaitForPacket(server, client, client.Endpoint);
            Assert.That(receivedUnreliable.Length,
                Is.EqualTo(settings.MaximumUnreliableBytes));
            Assert.That(client.CaptureDiagnostics().OutstandingLeases, Is.EqualTo(1));
            receivedUnreliable.Dispose();
            Assert.That(client.CaptureDiagnostics().OutstandingLeases, Is.Zero);
        }

        /// <summary>Verifies the server rejects connections beyond its configured bound.</summary>
        [Test]
        public void ServerEnforcesMaximumConnections()
        {
            var settings = Settings(ReservePort());
            settings.MaximumConnections = 1;
            using var server = new UnityTransportServerHost(settings);
            using var first = new UnityTransportClientHost(settings);
            using var second = new UnityTransportClientHost(settings);

            for (var attempt = 0; attempt < 500; attempt++)
            {
                first.Update();
                second.Update();
                server.Update();
                if (server.CaptureDiagnostics().DroppedPackets > 0)
                    break;
                Thread.Sleep(1);
            }

            var accepted = 0;
            while (server.TryAccept(out _))
                accepted++;
            var diagnostics = server.CaptureDiagnostics();
            Assert.That(accepted, Is.EqualTo(1));
            Assert.That(diagnostics.Connections, Is.EqualTo(1));
            Assert.That(diagnostics.DroppedPackets, Is.GreaterThanOrEqualTo(1));
        }

        /// <summary>Verifies an endpoint disconnected before admission is never returned later.</summary>
        [Test]
        public void TryAcceptPrunesDisconnectedEndpoint()
        {
            var settings = Settings(ReservePort());
            using var server = new UnityTransportServerHost(settings);
            using var client = new UnityTransportClientHost(settings);

            WaitUntil(() =>
            {
                client.Update();
                server.Update();
                return client.Connected && server.CaptureDiagnostics().Connections == 1;
            }, "Connection was not accepted by the driver.");

            client.Endpoint.Dispose();
            client.Flush();
            WaitUntil(() =>
            {
                server.Update();
                return server.CaptureDiagnostics().Connections == 0;
            }, "Server did not observe the disconnect.");

            Assert.That(server.TryAccept(out _), Is.False);
        }

        /// <summary>Verifies receive queues remain bounded and overflow is diagnosed.</summary>
        [Test]
        public void ReceiveQueueOverflowDropsExcessPacket()
        {
            var settings = Settings(ReservePort());
            settings.ReceiveQueueCapacity = 1;
            using var server = new UnityTransportServerHost(settings);
            using var client = new UnityTransportClientHost(settings);
            var accepted = WaitForConnection(server, client);
            using var pool = new NetworkBufferPool(4096);

            Assert.That(client.Endpoint.TrySend(Packet(pool,
                PacketFlags.ReliableOrdered, PacketHeader.Size)), Is.True);
            Assert.That(client.Endpoint.TrySend(Packet(pool,
                PacketFlags.ReliableOrdered, PacketHeader.Size)), Is.True);
            client.Flush();
            WaitUntil(() =>
            {
                client.Update();
                server.Update();
                return server.CaptureDiagnostics().DroppedPackets > 0;
            }, "Receive queue did not report overflow.");

            Assert.That(server.CaptureDiagnostics().QueuedPackets, Is.EqualTo(1));
            Assert.That(accepted.TryReceive(out var received), Is.True);
            received.Dispose();
            Assert.That(server.CaptureDiagnostics().OutstandingLeases, Is.Zero);
        }

        /// <summary>Verifies a failed listener construction releases native ownership.</summary>
        [Test]
        public void FailedListenDoesNotPoisonLaterConstruction()
        {
            var settings = Settings(ReservePort());
            using (var first = new UnityTransportServerHost(settings))
                Assert.Throws<InvalidOperationException>(() =>
                    new UnityTransportServerHost(settings));
            using var later = new UnityTransportServerHost(settings);
            Assert.That(later.CaptureDiagnostics().Connections, Is.Zero);
        }

        private static INetworkTransport WaitForConnection(
            UnityTransportServerHost server, UnityTransportClientHost client)
        {
            INetworkTransport accepted = null;
            WaitUntil(() =>
            {
                client.Update();
                server.Update();
                if (accepted == null)
                    server.TryAccept(out accepted);
                return client.Connected && accepted != null;
            }, "UTP loopback connection was not established.");
            return accepted;
        }

        private static NetworkBufferLease WaitForPacket(UnityTransportServerHost server,
            UnityTransportClientHost client, INetworkTransport endpoint)
        {
            NetworkBufferLease received = null;
            WaitUntil(() =>
            {
                client.Update();
                server.Update();
                return endpoint.TryReceive(out received);
            }, "UTP loopback packet was not received.");
            return received;
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            for (var attempt = 0; attempt < 1_000; attempt++)
            {
                if (condition())
                    return;
                Thread.Sleep(1);
            }
            Assert.Fail(message);
        }

        private static NetworkBufferLease Packet(NetworkBufferPool pool, PacketFlags flags,
            int packetBytes)
        {
            var payload = new byte[packetBytes - PacketHeader.Size];
            var header = new PacketHeader { Kind = PacketKind.Ping, Flags = flags };
            Assert.That(NetworkPacket.TryEncode(pool, header, payload, out var packet), Is.True);
            return packet;
        }

        private static UnityTransportSettings Settings(ushort port)
        {
            var settings = UnityTransportSettings.Default;
            settings.Address = "127.0.0.1";
            settings.Port = port;
            return settings;
        }

        private static ushort ReservePort()
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return checked((ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port);
        }
    }
}
