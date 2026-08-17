namespace UniGame.StaticEcs.Network.UnityTransport
{
    using System;

    /// <summary>Defines immutable packet limits supported by the Unity Transport adapter.</summary>
    public static class UnityTransportLimits
    {
        /// <summary>Maximum application packet size supported by the unreliable UTP pipeline.</summary>
        public const int MaximumUnreliableBytes = 1400;
    }

    /// <summary>Configures one Unity Transport driver and its bounded packet queues.</summary>
    [Serializable]
    public struct UnityTransportSettings
    {
        /// <summary>Default game port.</summary>
        public const ushort DefaultPort = 7777;
        /// <summary>Maximum reliable packet supported by the UTP fragmentation stage.</summary>
        public const int MaximumReliableBytes = 64 * 1024;

        /// <summary>Address used by a client or listener.</summary>
        public string Address;
        /// <summary>UDP port.</summary>
        public ushort Port;
        /// <summary>Maximum packet sent through the unreliable pipeline.</summary>
        public int MaximumUnreliableBytes;
        /// <summary>Maximum queued received packets per connection.</summary>
        public int ReceiveQueueCapacity;
        /// <summary>Maximum accepted server connections.</summary>
        public int MaximumConnections;

        /// <summary>Gets conservative defaults for a separated endpoint.</summary>
        public static UnityTransportSettings Default => new UnityTransportSettings
        {
            Address = "127.0.0.1",
            Port = DefaultPort,
            MaximumUnreliableBytes = UnityTransportLimits.MaximumUnreliableBytes,
            ReceiveQueueCapacity = 256,
            MaximumConnections = 128,
        };

        /// <summary>Validates and normalizes optional zero values.</summary>
        public UnityTransportSettings Normalize(bool listener)
        {
            var value = this;
            if (string.IsNullOrWhiteSpace(value.Address))
                value.Address = listener ? "0.0.0.0" : "127.0.0.1";
            if (value.Port == 0)
                value.Port = DefaultPort;
            if (value.MaximumUnreliableBytes <= PacketHeader.Size ||
                value.MaximumUnreliableBytes > UnityTransportLimits.MaximumUnreliableBytes)
                value.MaximumUnreliableBytes = UnityTransportLimits.MaximumUnreliableBytes;
            if (value.ReceiveQueueCapacity <= 0)
                value.ReceiveQueueCapacity = 256;
            if (value.MaximumConnections <= 0)
                value.MaximumConnections = 128;
            return value;
        }
    }

    /// <summary>Captures transport counters without exposing driver-native state.</summary>
    public struct UnityTransportDiagnostics
    {
        /// <summary>Number of active endpoint objects.</summary>
        public int Connections;
        /// <summary>Number of packets accepted from the driver.</summary>
        public long ReceivedPackets;
        /// <summary>Number of packets submitted to the driver.</summary>
        public long SentPackets;
        /// <summary>Number of packets or lifecycle notifications dropped by limits, queues, or UTP.</summary>
        public long DroppedPackets;
        /// <summary>Number of observed transport disconnects.</summary>
        public long Disconnects;
        /// <summary>Number of currently queued receive packets.</summary>
        public int QueuedPackets;
        /// <summary>Number of receive leases currently owned outside the transport pool.</summary>
        public int OutstandingLeases;
    }
}
