namespace UniGame.StaticEcs.Network.Profiler
{
    using Unity.Profiling;

    /// <summary>Projects privacy-safe session events into Unity Profiler markers and process-wide delta counters.</summary>
    public sealed class ProfilerObserver : ISessionObserver
    {
        private const string StepMetadata = "Step";
        private const string TickMetadata = "Tick";
        private const string SourceMetadata = "Source";

        private static readonly ProfilerMarker<ulong, uint, uint> StepMarker = Marker("SECS.Net.Step");
        private static readonly ProfilerMarker<ulong, uint, uint> ReceiveMarker = Marker("SECS.Net.Receive");
        private static readonly ProfilerMarker<ulong, uint, uint> DecodeMarker = Marker("SECS.Net.Decode");
        private static readonly ProfilerMarker<ulong, uint, uint> DispatchMarker = Marker("SECS.Net.Dispatch");
        private static readonly ProfilerMarker<ulong, uint, uint> CaptureMarker = Marker("SECS.Net.Capture");
        private static readonly ProfilerMarker<ulong, uint, uint> ApplyMarker = Marker("SECS.Net.Apply");
        private static readonly ProfilerMarker<ulong, uint, uint> EncodeMarker = Marker("SECS.Net.Encode");
        private static readonly ProfilerMarker<ulong, uint, uint> SendMarker = Marker("SECS.Net.Send");

        private static readonly ProfilerCounter<long> WireInCounter =
            Counter<long>("SECS.Net.WireIn", ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> WireOutCounter =
            Counter<long>("SECS.Net.WireOut", ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<long> DecodedCounter =
            Counter<long>("SECS.Net.Decoded", ProfilerMarkerDataUnit.Bytes);
        private static readonly ProfilerCounter<int> CommandsCounter =
            Counter<int>("SECS.Net.Commands", ProfilerMarkerDataUnit.Count);
        private static readonly ProfilerCounter<int> CapturesCounter =
            Counter<int>("SECS.Net.Captures", ProfilerMarkerDataUnit.Count);
        private static readonly ProfilerCounter<int> AppliesCounter =
            Counter<int>("SECS.Net.Applies", ProfilerMarkerDataUnit.Count);
        private static readonly ProfilerCounter<int> RetriesCounter =
            Counter<int>("SECS.Net.Retries", ProfilerMarkerDataUnit.Count);
        private static readonly ProfilerCounter<int> DeclinesCounter =
            Counter<int>("SECS.Net.Declines", ProfilerMarkerDataUnit.Count);
        private static readonly ProfilerCounter<int> FaultsCounter =
            Counter<int>("SECS.Net.Faults", ProfilerMarkerDataUnit.Count);
        private static readonly ProfilerCounter<int> ResyncsCounter =
            Counter<int>("SECS.Net.Resyncs", ProfilerMarkerDataUnit.Count);

        static ProfilerObserver()
        {
        }

        /// <summary>Creates an observer for a caller-selected privacy-safe numeric source lane.</summary>
        public ProfilerObserver(uint source = 0)
        {
            Source = source;
        }

        /// <summary>Gets the caller-selected privacy-safe numeric source lane.</summary>
        public uint Source { get; }

        /// <summary>Projects one session event into matching profiler markers and positive delta counters.</summary>
        public void Observe(in SessionEvent value)
        {
            if (value.Phase == SessionEventPhase.Begin)
            {
                Begin(in value);
                return;
            }

            if (value.Phase == SessionEventPhase.End)
            {
                End(in value);
                return;
            }

            if (value.Phase != SessionEventPhase.Point)
                return;

            if (value.Kind == SessionEventKind.Fault)
                FaultsCounter.Sample(1);
            else if (value.Kind == SessionEventKind.Resync)
                ResyncsCounter.Sample(1);
        }

        private static ProfilerMarker<ulong, uint, uint> Marker(string name) =>
            new(ProfilerCategory.Network, name, StepMetadata, TickMetadata, SourceMetadata);

        private static ProfilerCounter<T> Counter<T>(string name, ProfilerMarkerDataUnit unit)
            where T : unmanaged => new(ProfilerCategory.Network, name, unit);

        private void Begin(in SessionEvent value)
        {
            switch (value.Kind)
            {
                case SessionEventKind.Step: StepMarker.Begin(value.Step, value.Tick, Source); break;
                case SessionEventKind.Receive: ReceiveMarker.Begin(value.Step, value.Tick, Source); break;
                case SessionEventKind.Decode: DecodeMarker.Begin(value.Step, value.Tick, Source); break;
                case SessionEventKind.Dispatch: DispatchMarker.Begin(value.Step, value.Tick, Source); break;
                case SessionEventKind.Capture: CaptureMarker.Begin(value.Step, value.Tick, Source); break;
                case SessionEventKind.Apply: ApplyMarker.Begin(value.Step, value.Tick, Source); break;
                case SessionEventKind.Encode: EncodeMarker.Begin(value.Step, value.Tick, Source); break;
                case SessionEventKind.Send: SendMarker.Begin(value.Step, value.Tick, Source); break;
            }
        }

        private static void End(in SessionEvent value)
        {
            switch (value.Kind)
            {
                case SessionEventKind.Step: StepMarker.End(); break;
                case SessionEventKind.Receive:
                    ReceiveMarker.End();
                    if (value.Success && value.WireBytes > 0) WireInCounter.Sample((long)value.WireBytes);
                    break;
                case SessionEventKind.Decode:
                    DecodeMarker.End();
                    if (value.Success && value.DecodedBytes > 0) DecodedCounter.Sample((long)value.DecodedBytes);
                    break;
                case SessionEventKind.Dispatch:
                    DispatchMarker.End();
                    if (value.Success && value.Count > 0) CommandsCounter.Sample(value.Count);
                    break;
                case SessionEventKind.Capture:
                    CaptureMarker.End();
                    if (value.Success) CapturesCounter.Sample(1);
                    break;
                case SessionEventKind.Apply:
                    ApplyMarker.End();
                    if (value.Success) AppliesCounter.Sample(1);
                    break;
                case SessionEventKind.Encode: EncodeMarker.End(); break;
                case SessionEventKind.Send:
                    SendMarker.End();
                    if (value.Success && value.WireBytes > 0) WireOutCounter.Sample((long)value.WireBytes);
                    if (value.Retry) RetriesCounter.Sample(1);
                    if (!value.Success) DeclinesCounter.Sample(1);
                    break;
            }
        }
    }
}
