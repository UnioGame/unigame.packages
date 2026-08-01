# SVE11 Packet Lease Contract

## Scope

This contract replaces the allocation-per-packet `PacketLease` wrapper with a pooled generational value handle. It changes only runtime buffer ownership and its existing consumers. Wire layout, protocol limits, schemas, Unity dependencies, assembly definitions, replication semantics, sessions, and diagnostics are out of scope.

## Public handle

- `PacketLease` is a `public readonly struct` containing an internal sealed pooled state reference and a `long` generation token.
- `default(PacketLease)` is the only optional-invalid value. `IsValid` is true only while the state exists, the token matches, and a buffer is attached.
- `Rent`, `Copy`, capacity/length access, `Span`, `SetLength`, `Transfer`, and `Dispose` validate the handle. Disposing a default, stale, double-disposed, or transferred handle throws. Aggregate owners remain idempotently disposable by checking and clearing their owned handle.
- Ordinary struct copies are borrowed aliases, not independent owners. Exactly one logical owner exists by contract. A borrowed alias may read while the owner remains valid but must not call `Dispose` or `Transfer`.
- Ownership moves only through `Transfer(ref PacketLease source)` or an API that explicitly consumes a handle by `ref`. Transfer advances the generation and sets the source to `default`, invalidating all handles from the previous generation.
- `Memory` is removed from `PacketLease`. `Span` and every `ReadOnlyMemory<byte>` exposed by an aggregate are borrowed views that must not cross transfer, disposal, or thread handoff. Generations cannot revoke an escaped view.

## Generation exhaustion

- Valid generations are `1..long.MaxValue` and never wrap.
- Disposing a state at `long.MaxValue` returns its buffer and permanently retires the state.
- Transferring at `long.MaxValue` acquires another state, moves the same buffer and length without copying, assigns a fresh valid generation, clears the source, and permanently retires the exhausted state.
- An internal test seam must force near-exhaustion and prove that stale aliases cannot revive.

## Pools and failures

- Lease states use an intrusive free list protected by `lock`/`Monitor`. Do not use `ConcurrentStack<T>` or an untagged lock-free stack.
- Cold start and a new concurrency high-water mark may allocate state objects. After sufficient warm-up, repeated lease state reuse allocates no managed memory.
- Dispose invalidates the generation and detaches buffer and length before returning the array and state.
- Rent is exception-safe if state acquisition or `ArrayPool<byte>.Shared.Rent` fails; no acquired state or buffer may leak.

## Ownership migration

- Read-only APIs accept `in PacketLease`; failed `out PacketLease` values are `default`.
- `PayloadStager.TryStage(..., ref PacketLease payload, ...)` consumes a valid input on both success and failure. `StagedPayload` owns one lease, returns a borrowed payload view, and clears its handle during idempotent disposal.
- `MemoryTransport.Item` ref-transfers into the queue and ref-transfers from the queue to the receiver. Queue drains dispose only queue-owned handles. Every rejection/terminal path consumes or disposes exactly once according to the existing transport contract.
- `TickRecord` ownership construction accepts mutable lease sources by `ref` and a mutable `PacketLease[]` or `Span<PacketLease>`. It validates all sources and allocates its destination command array before the first transfer, then defaults every successfully transferred source. `Generated`, `Received`, `PostApply`, and `Commands` expose borrowed aliases; callers use `Copy` to retain bytes independently.
- Framing success paths transfer local owners. `finally` blocks dispose only handles that remain valid.

## Validation

- Focused tests are non-parallel where allocation or shared pool state matters.
- Allocation checks warm the state pool, lock path, and exact `ArrayPool` bucket before measuring `GC.GetAllocatedBytesForCurrentThread` around only the loop body.
- Separate warmed loops cover Rent/Dispose and Rent/Transfer/Dispose.
- Tests cover cold/high-water behavior, stale/default/double/use-after-return failures, forced generation exhaustion, serialized cross-thread handoff, staging, history, framing, and transport ownership regressions.
- No claim is made that complete framing, history, or transport operations allocate zero memory; their other objects, arrays, and collection nodes may allocate.

