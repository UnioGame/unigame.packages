using System;
using System.Collections.Generic;

namespace UniGame.StaticEcs.Network
{
    /// <summary>Reports the state of a tick-history lookup.</summary>
    public enum HistoryLookup
    {
        /// <summary>The tick bundle is retained.</summary>
        Found,
        /// <summary>The tick was seen but evicted.</summary>
        Evicted,
        /// <summary>The tick lies within seen history but was never retained.</summary>
        Missing,
        /// <summary>The tick is newer than any observed bundle.</summary>
        NotYetSeen
    }

    /// <summary>Reports the action required by authoritative reconciliation.</summary>
    public enum ReconcileResult
    {
        /// <summary>Canonical hashes match.</summary>
        Match,
        /// <summary>A retained mismatch can use a rollback boundary.</summary>
        NeedsRollback,
        /// <summary>Current retained state proves a hard resync is required.</summary>
        HardResync,
        /// <summary>The comparison tick is not retained.</summary>
        HistoryUnavailable
    }

    /// <summary>Owns an immutable bundle of canonical payloads and commands for one tick.</summary>
    public sealed class TickRecord : IDisposable
    {
        private bool _disposed;
        /// <summary>Creates an owned immutable tick bundle.</summary>
        public TickRecord(uint tick, PacketLease generated, PacketLease received, PacketLease postApply, ulong generatedHash, ulong receivedHash, ulong postApplyHash, long timestamp, long duration, IReadOnlyList<PacketLease> commands)
        {
            Tick = tick; Generated = generated; Received = received; PostApply = postApply; GeneratedHash = generatedHash; ReceivedHash = receivedHash; PostApplyHash = postApplyHash; Timestamp = timestamp; Duration = duration;
            var source = commands ?? Array.Empty<PacketLease>(); var copy = new PacketLease[source.Count]; for (var i = 0; i < copy.Length; i++) copy[i] = source[i] ?? throw new ArgumentException("Command leases cannot be null.", nameof(commands)); Commands = copy;
            Bytes = Length(generated) + Length(received) + Length(postApply); for (var i = 0; i < copy.Length; i++) Bytes += copy[i].Length;
        }
        /// <summary>Gets the bundle tick.</summary>
        public uint Tick { get; }
        /// <summary>Gets the generated canonical snapshot lease.</summary>
        public PacketLease Generated { get; }
        /// <summary>Gets the received canonical snapshot lease.</summary>
        public PacketLease Received { get; }
        /// <summary>Gets the post-apply canonical snapshot lease.</summary>
        public PacketLease PostApply { get; }
        /// <summary>Gets the generated canonical hash.</summary>
        public ulong GeneratedHash { get; }
        /// <summary>Gets the received canonical hash.</summary>
        public ulong ReceivedHash { get; }
        /// <summary>Gets the post-apply canonical hash.</summary>
        public ulong PostApplyHash { get; }
        /// <summary>Gets caller-defined timing timestamp.</summary>
        public long Timestamp { get; }
        /// <summary>Gets caller-defined processing duration.</summary>
        public long Duration { get; }
        /// <summary>Gets owned command payload leases in source order.</summary>
        public IReadOnlyList<PacketLease> Commands { get; }
        /// <summary>Gets total retained bytes.</summary>
        public long Bytes { get; }
        /// <summary>Disposes every lease owned by this bundle.</summary>
        public void Dispose() { if (_disposed) return; _disposed = true; Generated?.Dispose(); Received?.Dispose(); PostApply?.Dispose(); for (var i = 0; i < Commands.Count; i++) Commands[i].Dispose(); }
        private static int Length(PacketLease lease) => lease?.Length ?? 0;
    }

    /// <summary>Retains bounded whole-tick bundles and owns all accepted leases.</summary>
    public sealed class TickHistory : IDisposable
    {
        /// <summary>Default retained tick cap.</summary>
        public const int DefaultTickCapacity = 256;
        /// <summary>Default retained command cap.</summary>
        public const int DefaultCommandCapacity = 4096;
        /// <summary>Default shared retained-byte cap.</summary>
        public const long DefaultByteCapacity = 256L * 1024 * 1024;
        private readonly LinkedList<TickRecord> _records = new();
        private readonly Dictionary<uint, LinkedListNode<TickRecord>> _byTick = new();
        private readonly int _tickCapacity; private readonly int _commandCapacity; private readonly long _byteCapacity;
        private int _commands; private long _bytes; private uint _latest; private uint _lastEvicted; private bool _seen; private bool _disposed;
        /// <summary>Creates a history with independently enforced tick, command and shared byte caps.</summary>
        public TickHistory(int tickCapacity = DefaultTickCapacity, int commandCapacity = DefaultCommandCapacity, long byteCapacity = DefaultByteCapacity)
        { if (tickCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(tickCapacity)); if (commandCapacity < 0) throw new ArgumentOutOfRangeException(nameof(commandCapacity)); if (byteCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(byteCapacity)); _tickCapacity = tickCapacity; _commandCapacity = commandCapacity; _byteCapacity = byteCapacity; }
        /// <summary>Gets the retained bundle count.</summary>
        public int Count => _records.Count;
        /// <summary>Gets the retained command count.</summary>
        public int CommandCount => _commands;
        /// <summary>Gets shared retained bytes.</summary>
        public long Bytes => _bytes;
        /// <summary>Transfers ownership of a bundle and evicts oldest whole ticks until every cap holds.</summary>
        public bool Add(TickRecord record)
        {
            EnsureActive(); if (record == null) throw new ArgumentNullException(nameof(record)); if (_seen && record.Tick <= _latest) { record.Dispose(); throw new InvalidOperationException("Tick records must be added in increasing order."); }
            _seen = true; _latest = record.Tick; if (record.Bytes > _byteCapacity || record.Commands.Count > _commandCapacity) { _lastEvicted = record.Tick; record.Dispose(); return false; }
            var node = _records.AddLast(record); _byTick.Add(record.Tick, node); _bytes += record.Bytes; _commands += record.Commands.Count;
            while (_records.Count > _tickCapacity || _commands > _commandCapacity || _bytes > _byteCapacity) EvictOldest(); return true;
        }
        /// <summary>Looks up a retained immutable bundle without transferring ownership.</summary>
        public HistoryLookup TryGet(uint tick, out TickRecord record)
        {
            EnsureActive(); if (_byTick.TryGetValue(tick, out var node)) { record = node.Value; return HistoryLookup.Found; } record = null;
            if (!_seen || tick > _latest) return HistoryLookup.NotYetSeen; if (tick <= _lastEvicted) return HistoryLookup.Evicted; return HistoryLookup.Missing;
        }
        /// <summary>Compares an authoritative canonical hash with retained post-apply state.</summary>
        public ReconcileResult Reconcile(uint tick, ulong authoritativeHash)
        {
            var lookup = TryGet(tick, out var record); if (lookup == HistoryLookup.Found) return record.PostApplyHash == authoritativeHash ? ReconcileResult.Match : ReconcileResult.NeedsRollback;
            return lookup == HistoryLookup.Missing ? ReconcileResult.HardResync : ReconcileResult.HistoryUnavailable;
        }
        /// <summary>Disposes all retained bundles.</summary>
        public void Dispose() { if (_disposed) return; while (_records.Count > 0) EvictOldest(); _disposed = true; }
        private void EvictOldest() { var node = _records.First; if (node == null) return; var record = node.Value; _records.RemoveFirst(); _byTick.Remove(record.Tick); _bytes -= record.Bytes; _commands -= record.Commands.Count; _lastEvicted = record.Tick; record.Dispose(); }
        private void EnsureActive() { if (_disposed) throw new ObjectDisposedException(nameof(TickHistory)); }
    }
}
