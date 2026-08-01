using System;
using System.Buffers;
using System.Collections.Generic;

namespace UniGame.StaticEcs.Network
{
    /// <summary>Owns one pooled packet buffer until explicitly transferred or disposed.</summary>
    public sealed class PacketLease : IDisposable
    {
        private byte[] _buffer;
        private int _length;
        private PacketLease(byte[] buffer, int length) { _buffer = buffer; _length = length; }
        /// <summary>Rents a writable packet buffer with the requested capacity.</summary>
        public static PacketLease Rent(int capacity) { if (capacity < 0 || capacity > ProtocolLimits.MaxDecodedPayloadBytes + PacketHeader.Size) throw new ArgumentOutOfRangeException(nameof(capacity)); return new PacketLease(ArrayPool<byte>.Shared.Rent(Math.Max(1, capacity)), 0); }
        /// <summary>Gets whether this lease still owns its storage.</summary>
        public bool IsValid => _buffer != null;
        /// <summary>Gets the committed packet length.</summary>
        public int Length { get { EnsureValid(); return _length; } }
        /// <summary>Gets writable committed storage.</summary>
        public Span<byte> Span { get { EnsureValid(); return _buffer.AsSpan(0, _length); } }
        /// <summary>Gets read-only committed storage.</summary>
        public ReadOnlyMemory<byte> Memory { get { EnsureValid(); return new ReadOnlyMemory<byte>(_buffer, 0, _length); } }
        /// <summary>Gets writable storage across the full rented capacity.</summary>
        public Span<byte> CapacitySpan { get { EnsureValid(); return _buffer; } }
        /// <summary>Commits a new packet length within the rented capacity.</summary>
        public void SetLength(int length) { EnsureValid(); if (length < 0 || length > _buffer.Length) throw new ArgumentOutOfRangeException(nameof(length)); _length = length; }
        /// <summary>Creates an independent pooled copy of the committed bytes.</summary>
        public PacketLease Copy() { EnsureValid(); var copy = Rent(_length); _buffer.AsSpan(0, _length).CopyTo(copy.CapacitySpan); copy.SetLength(_length); return copy; }
        /// <summary>Returns owned storage to the shared pool.</summary>
        public void Dispose() { if (_buffer == null) throw new InvalidOperationException("Packet storage was already returned or transferred."); var buffer = _buffer; _buffer = null; _length = 0; ArrayPool<byte>.Shared.Return(buffer); }
        internal static PacketLease Transfer(ref PacketLease lease)
        {
            if (lease == null || !lease.IsValid) throw new InvalidOperationException("A valid packet lease is required.");
            var source = lease; var result = new PacketLease(source._buffer, source._length);
            source._buffer = null; source._length = 0; lease = null; return result;
        }
        private void EnsureValid() { if (_buffer == null) throw new InvalidOperationException("Packet storage has already been returned or transferred."); }
    }

    /// <summary>Identifies transport delivery behavior.</summary>
    public enum Channel
    {
        /// <summary>Exactly-once and in-order until disconnect.</summary>
        ReliableOrdered,
        /// <summary>May drop stale snapshots while preserving newest sequence.</summary>
        UnreliableSequenced
    }

    /// <summary>Reports transport lifecycle state.</summary>
    public enum TransportState
    {
        /// <summary>The transport accepts packets.</summary>
        Connected = 0,
        /// <summary>The transport faulted and drained ownership.</summary>
        Faulted = 1,
        /// <summary>The transport is disposed.</summary>
        Disposed = 2,
        /// <summary>The connected peer disposed its endpoint.</summary>
        Closed = 3
    }

    /// <summary>Identifies the immutable cause of the current transport state.</summary>
    public enum TransportError : byte
    {
        /// <summary>The connected transport has no error.</summary>
        None = 0,
        /// <summary>A reliable packet exceeded the bounded receive queue.</summary>
        QueueOverflow = 1,
        /// <summary>The connected peer disposed its endpoint.</summary>
        RemoteClosed = 2,
        /// <summary>An unreliable packet or channel violated the transport contract.</summary>
        InvalidPacket = 3,
        /// <summary>The local endpoint was disposed.</summary>
        Disposed = 4
    }

    /// <summary>Transfers owned packets across a delivery boundary.</summary>
    public interface ITransport : IDisposable
    {
        /// <summary>Gets transport lifecycle state.</summary>
        TransportState State { get; }
        /// <summary>Gets the immutable cause of the current transport state.</summary>
        TransportError Error { get; }
        /// <summary>Consumes a valid lease and reports whether it entered the delivery queue.</summary>
        bool TrySend(Channel channel, ref PacketLease packet);
        /// <summary>Transfers the next received lease to the caller.</summary>
        bool TryReceive(out Channel channel, out PacketLease packet);
    }

    /// <summary>Creates bounded, single-thread-affine in-memory transports with deterministic delivery semantics.</summary>
    public sealed class MemoryTransport : ITransport
    {
        private readonly LinkedList<Item> _incoming = new();
        private readonly int _capacity;
        private MemoryTransport _peer;
        private uint _latestUnreliable;
        private MemoryTransport(int capacity) { _capacity = capacity; State = TransportState.Connected; Error = TransportError.None; }
        /// <summary>Gets transport lifecycle state.</summary>
        public TransportState State { get; private set; }
        /// <summary>Gets the immutable cause of the current transport state.</summary>
        public TransportError Error { get; private set; }
        /// <summary>Creates a connected pair with a bounded receive queue.</summary>
        public static void CreatePair(int queueCapacity, out MemoryTransport left, out MemoryTransport right) { if (queueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(queueCapacity)); left = new MemoryTransport(queueCapacity); right = new MemoryTransport(queueCapacity); left._peer = right; right._peer = left; }
        /// <inheritdoc />
        public bool TrySend(Channel channel, ref PacketLease packet)
        {
            var owned = PacketLease.Transfer(ref packet);
            if (State != TransportState.Connected)
            {
                owned.Dispose();
                return false;
            }

            var peer = _peer;
            if (peer == null || peer.State != TransportState.Connected)
            {
                owned.Dispose();
                return false;
            }

            if (channel != Channel.ReliableOrdered && channel != Channel.UnreliableSequenced)
            {
                owned.Dispose();
                TerminatePair(
                    TransportState.Faulted,
                    TransportError.InvalidPacket,
                    TransportState.Closed,
                    TransportError.RemoteClosed);
                return false;
            }

            uint sequence = 0;
            if (channel == Channel.UnreliableSequenced)
            {
                if (owned.Length < PacketHeader.Size || !PacketHeader.TryRead(owned.Span, out var header) ||
                    header.Kind != PacketKind.FullSnapshot || header.PacketSequence == 0 ||
                    owned.Length != PacketHeader.Size + header.WirePayloadLength)
                {
                    owned.Dispose();
                    TerminatePair(
                        TransportState.Faulted,
                        TransportError.InvalidPacket,
                        TransportState.Closed,
                        TransportError.RemoteClosed);
                    return false;
                }

                sequence = header.PacketSequence;
                if (sequence <= peer._latestUnreliable)
                {
                    owned.Dispose();
                    return false;
                }

                var node = peer._incoming.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.Channel == Channel.UnreliableSequenced)
                    {
                        peer._incoming.Remove(node);
                        node.Value.Packet.Dispose();
                    }
                    node = next;
                }
            }

            if (peer._incoming.Count >= peer._capacity)
            {
                owned.Dispose();
                if (channel == Channel.ReliableOrdered)
                    TerminatePair(
                        TransportState.Faulted,
                        TransportError.QueueOverflow,
                        TransportState.Faulted,
                        TransportError.QueueOverflow);
                return false;
            }
            if (channel == Channel.UnreliableSequenced) peer._latestUnreliable = sequence;
            peer._incoming.AddLast(new Item(channel, owned)); return true;
        }
        /// <inheritdoc />
        public bool TryReceive(out Channel channel, out PacketLease packet) { if (State != TransportState.Connected || _incoming.Count == 0) { channel = default; packet = null; return false; } var item = _incoming.First.Value; _incoming.RemoveFirst(); channel = item.Channel; packet = item.Packet; return true; }
        /// <summary>Disposes the transport and drains every queued lease.</summary>
        public void Dispose()
        {
            if (State == TransportState.Disposed) return;
            if (State == TransportState.Connected)
            {
                TerminatePair(
                    TransportState.Disposed,
                    TransportError.Disposed,
                    TransportState.Closed,
                    TransportError.RemoteClosed);
                return;
            }

            _peer = null;
            State = TransportState.Disposed;
            Error = TransportError.Disposed;
        }

        private void TerminatePair(
            TransportState localState,
            TransportError localError,
            TransportState peerState,
            TransportError peerError)
        {
            var peer = _peer;
            _peer = null;
            if (peer != null) peer._peer = null;
            TransitionFromConnected(localState, localError);
            peer?.TransitionFromConnected(peerState, peerError);
        }

        private void TransitionFromConnected(TransportState state, TransportError error)
        {
            if (State != TransportState.Connected) return;
            State = state;
            Error = error;
            Drain();
        }

        private void Drain() { while (_incoming.Count > 0) { var packet = _incoming.First.Value.Packet; _incoming.RemoveFirst(); packet.Dispose(); } }
        private readonly struct Item { internal Item(Channel channel, PacketLease packet) { Channel = channel; Packet = packet; } internal Channel Channel { get; } internal PacketLease Packet { get; } }
    }

    /// <summary>Transforms payload bytes within explicit decoded and encoded bounds.</summary>
    public interface IPayloadTransform
    {
        /// <summary>Gets the versioned transform identifier.</summary>
        byte Id { get; }
        /// <summary>Returns the maximum encoded bytes for a decoded length.</summary>
        int MaxEncodedLength(int decodedLength);
        /// <summary>Encodes one complete decoded payload.</summary>
        bool TryEncode(ReadOnlySpan<byte> decoded, Span<byte> destination, out int written);
        /// <summary>Decodes one complete encoded payload within the expected output bound.</summary>
        bool TryDecode(ReadOnlySpan<byte> encoded, Span<byte> destination, out int written);
    }

    /// <summary>Implements version-one transform zero as an exact bounded copy.</summary>
    public readonly struct NoOpTransform : IPayloadTransform
    {
        /// <inheritdoc />
        public byte Id => 0;
        /// <inheritdoc />
        public int MaxEncodedLength(int decodedLength) => decodedLength;
        /// <inheritdoc />
        public bool TryEncode(ReadOnlySpan<byte> decoded, Span<byte> destination, out int written) { if (decoded.Length > destination.Length) { written = 0; return false; } decoded.CopyTo(destination); written = decoded.Length; return true; }
        /// <inheritdoc />
        public bool TryDecode(ReadOnlySpan<byte> encoded, Span<byte> destination, out int written) { if (encoded.Length > destination.Length) { written = 0; return false; } encoded.CopyTo(destination); written = encoded.Length; return true; }
    }
}
