# SVE11 Network Profiler Contract

## Scope and baseline

This phase adds an optional Unity Profiler adapter over the integrated diagnostics API at exact Game.Packages baseline `56b3672539eea260f17ec100ff42841d8f161124`.

The adapter is a separate UPM package. It does not change Session, protocol bytes, diagnostics events, transports, replication, history, serializers, the base network asmdefs, or gameplay. The base `unigame.staticecs.network` assembly remains Unity-free with `noEngineReferences: true`.

## Frozen public surface

The only new public type is:

```csharp
namespace UniGame.StaticEcs.Network.Profiler
{
    public sealed class ProfilerObserver : ISessionObserver
    {
        public ProfilerObserver(uint source = 0);
        public uint Source { get; }
        public void Observe(in SessionEvent value);
    }
}
```

Every public type and member has an English XML summary. There is no manager, service, settings asset, mutable registry, `IDisposable`, `Sample`, `Demo`, `NetworkSession`, enable switch, or recorder ownership inside the observer. `source` is a caller-selected privacy-safe numeric lane used to distinguish sessions; it is never derived from peer id, epoch, schema, payload, or other private session data.

## Package boundary

The package root is `unigame.staticecs.network.profiler`, package id `com.unigame.staticecs.network.profiler`, version `2026.0.1`, Unity floor `2023.2`, and dependencies:

- `com.unigame.staticecs.network: 2026.0.1`;
- `com.unity.profiling.core: 1.0.3`.

The runtime asmdef is `unigame.staticecs.network.profiler`, root namespace `UniGame.StaticEcs.Network.Profiler`, references `unigame.staticecs.network` and `Unity.Profiling.Core`, uses no unsafe code, and has `noEngineReferences: false`. The package is optional; no reverse dependency from the base network package is allowed.

## Markers

All markers use `ProfilerCategory.Network` and static `ProfilerMarker<ulong,uint,uint>` handles with metadata names `Step`, `Tick`, and `Source`.

Exact marker names:

- `SECS.Net.Step`
- `SECS.Net.Receive`
- `SECS.Net.Decode`
- `SECS.Net.Dispatch`
- `SECS.Net.Capture`
- `SECS.Net.Apply`
- `SECS.Net.Encode`
- `SECS.Net.Send`

Only the matching eight operation kinds are mapped. Begin calls marker Begin with the event step, tick including its explicit absence sentinel, and observer source. End always calls marker End, including unsuccessful or throwing operations. State, Fault and Resync point events do not create timeline markers.

The observer stores no sample stack or event dictionary. Real Session streams are synchronous, same-thread and LIFO for each operation pair; Unity owns the thread-local sample stack. Reentrant or manually fabricated non-LIFO streams are outside the contract. One static marker set is shared across observers and threads. Unity marker duration is the wall interval between observer deliveries; for Capture and Apply this may include Session bookkeeping performed after the core captured its `SessionEvent.Elapsed` endpoint.

## Counters

Counters are static `ProfilerCounter<T>` delta samples, never shared mutable `ProfilerCounterValue<T>` instances. They are process-wide aggregates across sessions and threads. Zero or negative deltas are not sampled.

Exact names and mappings:

- `SECS.Net.WireIn`, `long`, Bytes: successful Receive End with `WireBytes > 0`.
- `SECS.Net.WireOut`, `long`, Bytes: successful Send End with `WireBytes > 0`.
- `SECS.Net.Decoded`, `long`, Bytes: successful Decode End with `DecodedBytes > 0`.
- `SECS.Net.Commands`, `int`, Count: successful Dispatch End with `Count > 0`; Accepted and authorization-Rejected commands are both processed workload.
- `SECS.Net.Captures`, `int`, Count: successful Capture End, delta one.
- `SECS.Net.Applies`, `int`, Count: successful Apply End, delta one.
- `SECS.Net.Retries`, `int`, Count: every Send End with `Retry=true`, independent of acceptance.
- `SECS.Net.Declines`, `int`, Count: every unsuccessful Send End, including false return or throw.
- `SECS.Net.Faults`, `int`, Count: every Fault Point.
- `SECS.Net.Resyncs`, `int`, Count: every Resync Point.

`Declines` deliberately does not claim packet loss or backpressure: the current observer stream cannot distinguish a transport false return from a throwing send and has no true packet-drop seam. Receive false is ordinary idle polling and is never counted. A future `Drops` counter requires a new core event at an actual transport/queue discard site and is out of scope here.

## Runtime and ownership semantics

Construction initializes the static profiler handles and retains only `Source`. `Observe` performs no managed allocation or I/O. The observer is caller-owned but has nothing to dispose. Session still isolates any observer exception and increments only `ObserverErrors`; the adapter does not add a second exception policy.

Profiler APIs compile to their supported no-op behavior when profiling is disabled. The adapter must not branch on Editor state, use reflection, subscribe to Unity lifecycle events, create GameObjects, or retain Session/world references.

## Files and integration ownership

The implementation worker owns only the new package and paired Unity metas:

- `unigame.staticecs.network.profiler/package.json`;
- `unigame.staticecs.network.profiler/README.md`;
- `unigame.staticecs.network.profiler/Runtime/unigame.staticecs.network.profiler.asmdef`;
- `unigame.staticecs.network.profiler/Runtime/ProfilerObserver.cs`;
- `unigame.staticecs.network.profiler/Tests/Editor/unigame.staticecs.network.profiler.tests.asmdef`;
- `unigame.staticecs.network.profiler/Tests/Editor/ProfilerObserverTests.cs`;
- every required folder/file `.meta`.

The orchestrator, only after Accepted review, owns project integration in `GameClient/Packages/manifest.json` and Unity-generated `packages-lock.json`: add the local file dependency and the profiler package id to `testables`. The implementation worker must not edit project manifests or any base package.

README is English and follows Capabilities / Usage / Configuration. It documents global aggregation, source metadata, Declines semantics, profiler-disabled behavior, and that `ProfilerRecorder` is a debug/test consumer-owned disposable value rather than observer state.

## Acceptance

- Prove all eight exact marker registrations and ten exact counter registrations with valid `ProfilerRecorder`s in `ProfilerCategory.Network`.
- Drive real Session handshake, command dispatch, capture/apply, false-send retry, fault and both client/server resync paths; do not fabricate internal SessionEvent values across the package boundary.
- Prove marker Begin/End balance after success, returned failure and callback/transport exception by recording a later sentinel operation with the same marker.
- Prove exact positive counter deltas: header-inclusive wire bytes, payload-only decoded bytes, processed accepted/rejected commands, separate capture/apply, retry attempts, declines, faults and role-independent resync points. Idle/zero events produce no samples.
- Use `ProfilerRecorder` with capacity at least 64 and wrap/sum-all-samples behavior, validate `Valid`, avoid current-thread-only collection, and dispose each recorder exactly once.
- Serialize profiler tests with a non-parallel fixture/gate because static marker/counter names are global.
- Keep runtime observer allocation-free after construction and free of mutable shared managed state.
- Verify package/asmdef dependencies, XML docs, metas, README, no forbidden names, no base-package changes, `git diff --check`, `lemmings check --all`, independent high review, then project manifest integration and Unity EditMode tests.

