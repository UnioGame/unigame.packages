# Static ECS Network Profiler

## Capabilities

This optional package projects the synchronous diagnostics stream from `Session<TWorld>` into the Unity Profiler. It registers eight `SECS.Net.*` timeline markers and ten delta counters in the Network category. Counters are process-wide aggregates across all sessions and threads; marker metadata carries the logical step, tick (including the network absence sentinel), and a caller-selected numeric source lane.

`SECS.Net.Declines` counts unsuccessful send attempts, whether the transport returned `false` or threw. It does not claim packet loss or backpressure. Idle receives and zero deltas are not sampled. Unity's profiler APIs retain their supported no-op behavior when profiling is disabled.

## Usage

```csharp
var observer = new ProfilerObserver(source: 1);
var session = new Session<GameWorld>(config, schema, transport, observer);
```

The observer is caller-owned, allocation-free after construction, and has no disposable state. The session does not take ownership of it.

## Configuration

Choose a privacy-safe `source` value when multiple session lanes must be distinguished. Do not derive it from peer identifiers, epochs, schemas, payloads, or other private session data.

`ProfilerRecorder` is a debug or test consumer-owned disposable value. It is not stored or managed by `ProfilerObserver`; create, configure, and dispose recorders at the inspection site.
