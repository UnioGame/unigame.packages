using System;

namespace UniGame.StaticEcs.Network
{
    /// <summary>Frames and validates owned packets in the required version-one validation order.</summary>
    public static class PacketFraming
    {
        /// <summary>Creates a packet by transforming and hashing canonical decoded payload bytes.</summary>
        public static bool TryEncode(PacketHeader header, ReadOnlySpan<byte> decodedPayload, IPayloadTransform transform, out PacketLease packet)
            => TryEncode(header, decodedPayload, transform, null, out packet);

        /// <summary>Creates a schema-validated packet by transforming and hashing canonical decoded payload bytes.</summary>
        public static bool TryEncode(PacketHeader header, ReadOnlySpan<byte> decodedPayload, IPayloadTransform transform, Schema schema, out PacketLease packet)
        {
            packet = null; if (transform == null || transform.Id != 0 || decodedPayload.Length > ProtocolLimits.MaxDecodedPayloadBytes || RequiresSchema(header.Kind) && (schema == null || header.SchemaHash != schema.Hash) || !ValidatePayload(header.Kind, decodedPayload, schema)) return false;
            var max = transform.MaxEncodedLength(decodedPayload.Length); if (max < 0 || max > ProtocolLimits.MaxWirePayloadBytes) return false; var lease = PacketLease.Rent(PacketHeader.Size + max);
            if (!transform.TryEncode(decodedPayload, lease.CapacitySpan.Slice(PacketHeader.Size, max), out var encoded) || encoded < 0 || encoded > max) { lease.Dispose(); return false; }
            header.TransformId = transform.Id; header.DecodedPayloadLength = (uint)decodedPayload.Length; header.WirePayloadLength = (uint)encoded; header.PayloadHash = Hashing.XxHash64(decodedPayload);
            if (!header.TryWrite(lease.CapacitySpan)) { lease.Dispose(); return false; } lease.SetLength(PacketHeader.Size + encoded); packet = lease; return true;
        }

        /// <summary>Validates framing, bounded transform output and hash before returning an owned typed stage.</summary>
        public static bool TryDecode(PacketLease packet, IPayloadTransform transform, out PacketHeader header, out StagedPayload staged)
            => TryDecode(packet, transform, null, out header, out staged);

        /// <summary>Validates framing, transform, hash and schema before returning an owned typed stage for later ECS consumption.</summary>
        public static bool TryDecode(PacketLease packet, IPayloadTransform transform, Schema schema, out PacketHeader header, out StagedPayload staged)
        {
            header = default; staged = null; if (packet == null || transform == null || packet.Length < PacketHeader.Size || !PacketHeader.TryRead(packet.Span, out header)) return false;
            if (header.TransformId != transform.Id || packet.Length != PacketHeader.Size + header.WirePayloadLength || RequiresSchema(header.Kind) && (schema == null || header.SchemaHash != schema.Hash)) return false;
            var lease = PacketLease.Rent((int)header.DecodedPayloadLength); if (!transform.TryDecode(packet.Span.Slice(PacketHeader.Size), lease.CapacitySpan.Slice(0, (int)header.DecodedPayloadLength), out var written) || written != header.DecodedPayloadLength) { lease.Dispose(); return false; }
            lease.SetLength(written); if (Hashing.XxHash64(lease.Span) != header.PayloadHash) { lease.Dispose(); return false; }
            return PayloadStager.TryStage(header.Kind, lease, schema, out staged);
        }

        private static bool ValidatePayload(PacketKind kind, ReadOnlySpan<byte> payload, Schema schema)
        {
            switch (kind)
            {
                case PacketKind.Hello: return PayloadCodec.TryReadHello(payload, out _);
                case PacketKind.HelloAck: return PayloadCodec.TryReadHelloAck(payload, out _);
                case PacketKind.CommandBatch:
                case PacketKind.FullSnapshot:
                    return schema != null && ValidateStaged(kind, payload, schema);
                case PacketKind.Ack: return PayloadCodec.TryReadAck(payload);
                case PacketKind.ResyncRequest: return PayloadCodec.TryReadResyncRequest(payload, out _);
                case PacketKind.Disconnect: return PayloadCodec.TryReadDisconnect(payload, out _);
                default: return false;
            }
        }

        private static bool ValidateStaged(PacketKind kind, ReadOnlySpan<byte> payload, Schema schema)
        {
            if (kind == PacketKind.CommandBatch && !PayloadCodec.ValidateCommandBatchFraming(payload) ||
                kind == PacketKind.FullSnapshot && !PayloadCodec.ValidateSnapshotFraming(payload)) return false;
            var lease = PacketLease.Rent(payload.Length); payload.CopyTo(lease.CapacitySpan); lease.SetLength(payload.Length);
            if (!PayloadStager.TryStage(kind, lease, schema, out var staged)) return false;
            staged.Dispose(); return true;
        }

        private static bool RequiresSchema(PacketKind kind) => kind == PacketKind.CommandBatch || kind == PacketKind.FullSnapshot;
    }
}
