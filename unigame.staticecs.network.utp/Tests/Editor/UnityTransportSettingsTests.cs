namespace UniGame.StaticEcs.Network.UnityTransport.Tests
{
    using NUnit.Framework;

    public sealed class UnityTransportSettingsTests
    {
        [Test]
        public void NormalizeAppliesBoundedDefaults()
        {
            var value = default(UnityTransportSettings).Normalize(false);

            Assert.AreEqual("127.0.0.1", value.Address);
            Assert.AreEqual(UnityTransportSettings.DefaultPort, value.Port);
            Assert.AreEqual(1400, value.MaximumUnreliableBytes);
            Assert.AreEqual(256, value.ReceiveQueueCapacity);
            Assert.AreEqual(128, value.MaximumConnections);
        }

        [Test]
        public void RejectedPacketLeaseIsConsumed()
        {
            using var pool = new NetworkBufferPool(1024);
            using var host = new UnityTransportClientHost(UnityTransportSettings.Default);
            var packet = pool.Copy(new byte[1]);

            Assert.IsFalse(host.Endpoint.TrySend(packet));
            Assert.AreEqual(0, packet.Length);
            Assert.AreEqual(0, pool.CaptureDiagnostics().OutstandingLeases);
        }
    }
}
