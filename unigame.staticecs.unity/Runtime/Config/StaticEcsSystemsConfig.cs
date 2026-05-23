using System;
using Cysharp.Threading.Tasks;

namespace unigame.staticecs.unity {
    [Serializable]
    public struct StaticEcsSystemsConfig {
        public bool update;
        public bool fixedUpdate;
        public bool lateUpdate;
        public bool cleanup;
        public uint baseSize;

        public PlayerLoopTiming updateTiming;
        public PlayerLoopTiming fixedUpdateTiming;
        public PlayerLoopTiming lateUpdateTiming;
        public PlayerLoopTiming cleanupTiming;

        public static StaticEcsSystemsConfig Default => new() {
            update = true,
            fixedUpdate = false,
            lateUpdate = false,
            cleanup = false,
            baseSize = 16,
            updateTiming = PlayerLoopTiming.Update,
            fixedUpdateTiming = PlayerLoopTiming.FixedUpdate,
            lateUpdateTiming = PlayerLoopTiming.PostLateUpdate,
            cleanupTiming = PlayerLoopTiming.LastPostLateUpdate
        };
    }
}
