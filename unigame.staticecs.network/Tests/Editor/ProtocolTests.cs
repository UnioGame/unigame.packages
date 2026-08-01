using System;
using NUnit.Framework;

namespace UniGame.StaticEcs.Network.Tests
{
    public sealed class ProtocolTests
    {
        [Test]
        public void HeaderUsesGoldenOffsetsAndRoundTrips()
        {
            var header = Header(PacketKind.Hello, PacketFlags.ReliableOrdered, 20);
            var bytes = new byte[PacketHeader.Size];
            Assert.That(header.TryWrite(bytes), Is.True);
            Assert.That(BitConverter.ToString(bytes, 0, 12).Replace("-", string.Empty), Is.EqualTo("534543530100480001010000"));
            Assert.That(BitConverter.ToUInt32(bytes, 12), Is.EqualTo(7));
            Assert.That(BitConverter.ToUInt32(bytes, 24), Is.EqualTo(PacketHeader.NoneTick));
            Assert.That(PacketHeader.TryRead(bytes, out var decoded), Is.True);
            Assert.That(decoded.PayloadHash, Is.EqualTo(header.PayloadHash));
        }

        [Test]
        public void HeaderRejectsTruncationReservedFieldsAndWrongCrc()
        {
            var bytes = new byte[PacketHeader.Size]; Header(PacketKind.Ack, PacketFlags.ReliableOrdered, 0).TryWrite(bytes);
            Assert.That(PacketHeader.TryRead(bytes.AsSpan(0, 71), out _), Is.False);
            bytes[11] = 1; Assert.That(PacketHeader.TryRead(bytes, out _), Is.False);
            bytes[11] = 0; bytes[20] ^= 1; Assert.That(PacketHeader.TryRead(bytes, out _), Is.False);
        }

        [Test]
        public void EveryPayloadKindRoundTripsAndRejectsTrailingBytes()
        {
            var bytes = new byte[4096];
            Assert.That(PayloadCodec.TryWrite(new HelloPayload { Nonce = 9, MinTickRate = 20, MaxTickRate = 60, MaxWireBytes = 1024, MaxDecodedBytes = 2048, Capabilities = 3 }, bytes, out var length), Is.True);
            Assert.That(PayloadCodec.TryReadHello(bytes.AsSpan(0, length), out _), Is.True); Assert.That(PayloadCodec.TryReadHello(bytes.AsSpan(0, length + 1), out _), Is.False);
            var ack = new HelloAckPayload { Result = ConnectResult.Accepted, TickRate = 30, PeerId = 2, ServerNonce = 8, Chunks = new[] { new ChunkMapping { Chunk = 4, Cluster = 3, Role = 1 } } };
            Assert.That(PayloadCodec.TryWrite(ack, bytes, out length), Is.True); Assert.That(PayloadCodec.TryReadHelloAck(bytes.AsSpan(0, length), out _), Is.True);
            var command = new CommandBatchPayload { Commands = new[] { new CommandRecord { TypeId = Id(1), Version = 2, Sequence = 1, ClientTick = 4, Payload = new byte[] { 7 } } } };
            Assert.That(PayloadCodec.TryWrite(command, bytes, out length), Is.True); Assert.That(PayloadCodec.TryReadCommandBatch(bytes.AsSpan(0, length), out _), Is.True);
            var snapshot = new FullSnapshotPayload { Entities = new[] { new SnapshotEntity { Entity = new WireEntityId(2, 1, 3), KindId = Id(2), Records = new[] { new SnapshotRecord { TypeId = Id(3), Kind = RecordKind.Tag, Payload = Array.Empty<byte>() } } } } };
            Assert.That(PayloadCodec.TryWrite(snapshot, bytes, out length), Is.True); Assert.That(PayloadCodec.TryReadFullSnapshot(bytes.AsSpan(0, length), out _), Is.True);
            Assert.That(PayloadCodec.TryWriteAck(bytes, out length), Is.True); Assert.That(PayloadCodec.TryReadAck(bytes.AsSpan(0, length)), Is.True);
            Assert.That(PayloadCodec.TryWrite(new ResyncRequestPayload { Reason = ResyncReason.HashMismatch, LastAcceptedTick = 4 }, bytes, out length), Is.True); Assert.That(PayloadCodec.TryReadResyncRequest(bytes.AsSpan(0, length), out _), Is.True);
            Assert.That(PayloadCodec.TryWrite(new DisconnectPayload { Reason = DisconnectReason.ServerShutdown }, bytes, out length), Is.True); Assert.That(PayloadCodec.TryReadDisconnect(bytes.AsSpan(0, length), out _), Is.True);
        }

        [Test]
        public void NonCanonicalCommandsEntitiesRecordsAndLinksAreRejected()
        {
            var bytes = new byte[1024];
            var commands = new CommandBatchPayload { Commands = new[] { new CommandRecord { TypeId = Id(1), Sequence = 2, Payload = Array.Empty<byte>() }, new CommandRecord { TypeId = Id(2), Sequence = 1, Payload = Array.Empty<byte>() } } };
            Assert.That(PayloadCodec.TryWrite(commands, bytes, out _), Is.False);
            var entities = new FullSnapshotPayload { Entities = new[] { new SnapshotEntity { Entity = new WireEntityId(2, 0, 1), KindId = Id(1) }, new SnapshotEntity { Entity = new WireEntityId(1, 0, 1), KindId = Id(1) } } };
            Assert.That(PayloadCodec.TryWrite(entities, bytes, out _), Is.False);
            var links = new byte[16]; WriteEntity(links, 0, new WireEntityId(2, 0, 1)); WriteEntity(links, 8, new WireEntityId(1, 0, 1));
            var snapshot = new FullSnapshotPayload
            {
                Entities = new[]
                {
                    new SnapshotEntity
                    {
                        Entity = new WireEntityId(1, 0, 1), KindId = Id(1),
                        Records = new[]
                        {
                            new SnapshotRecord { TypeId = Id(2), Kind = RecordKind.Links, ElementCount = 2, Payload = links }
                        }
                    }
                }
            };
            Assert.That(PayloadCodec.TryWrite(snapshot, bytes, out _), Is.False);
        }

        private static PacketHeader Header(PacketKind kind, PacketFlags flags, uint length) => new() { Kind = kind, Flags = flags, SessionEpoch = 7, PacketSequence = 8, ServerTick = 9, BaselineTick = PacketHeader.NoneTick, AcknowledgedSnapshotTick = 6, WirePayloadLength = length, DecodedPayloadLength = length, SchemaHash = Id(5), PayloadHash = 11, AcknowledgedCommandSequence = 4 };
        private static TypeId Id(int value) => new(new Guid(value, 0, 0, new byte[8]));
        private static void WriteEntity(byte[] bytes, int offset, WireEntityId id) { bytes[offset] = (byte)id.Id; bytes[offset + 4] = (byte)id.ClusterId; bytes[offset + 6] = (byte)id.Version; }
    }
}
