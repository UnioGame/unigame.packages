# SVE11 Replication Post-Lease Addendum

## Precedence

This compatibility addendum applies to replication work based on or after `SVE11-NETWORK-LEASE-V1`. It overrides only the nullable `PacketLease` ownership wording in `SVE11-network-replication.md`. Every other replication requirement remains unchanged.

## Capture ownership

- `Replicator<TWorld>.Capture(out PacketLease payload)` assigns `payload = default` before validation or allocation.
- A successful capture transfers the local owner with `payload = PacketLease.Transfer(ref lease)`.
- Every result other than `CaptureResult.Success` leaves the output default and invalid.
- Cleanup releases only a local lease that remains valid after the attempted transfer.
- `Capture`, `CaptureResult`, and all other public replication names and result values remain unchanged.

## Staging ownership

- Every replication call to `PayloadStager.TryStage` passes a mutable valid lease by `ref`.
- Staging consumes that valid lease on success, returned failure, or exception according to the packet-lease contract.
- Tests assert `payload.IsValid == false` for every failed capture output; nullable assertions are not valid for the value handle.

## Allocation evidence

- The warmed capture test uses a dedicated `IWorldType` and the test-assembly-shared `PoolTestGate.Sync` fixture gate.
- Setup creates a fixed non-empty snapshot with representative records and relations, then performs and disposes one complete capture to warm query, schema, array-pool, and lease-state paths.
- The measured interval contains only repeated `Capture`, primitive result accumulation, payload-length accumulation, and valid-owner disposal. It contains no assertions, LINQ, closures, world mutations, or setup.
- Post-loop assertions require every result to be `Success`, every output to have been valid and non-empty, and `GC.GetAllocatedBytesForCurrentThread` delta to equal zero.

