using System;
using FFS.Libraries.StaticEcs;

namespace unigame.staticecs.unity {
    [Serializable]
    public struct StaticEcsWorldConfig {
        public uint baseEntitiesCapacity;
        public uint baseComponentTypesCount;
        public ushort baseClustersCapacity;
        public int threadCount;
        public uint workerSpinCount;
        public bool independent;
        public bool trackCreated;
        public byte trackingBufferSize;

        public static StaticEcsWorldConfig Default => new() {
            baseEntitiesCapacity = 4096,
            baseComponentTypesCount = 64,
            baseClustersCapacity = 16,
            threadCount = 0,
            workerSpinCount = 256,
            independent = true,
            trackCreated = false,
            trackingBufferSize = 8
        };

        public WorldConfig CreateWorldConfig() {
            return new WorldConfig {
                BaseComponentTypesCount = baseComponentTypesCount,
                BaseClustersCapacity = baseClustersCapacity,
                ThreadCount = threadCount < 0 ? WorldConfig.MaxThreadCount : (uint)threadCount,
                WorkerSpinCount = workerSpinCount,
                Independent = independent,
                TrackCreated = trackCreated,
                TrackingBufferSize = trackingBufferSize
            };
        }
    }
}
