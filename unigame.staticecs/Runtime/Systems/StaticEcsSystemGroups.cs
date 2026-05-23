using FFS.Libraries.StaticEcs;

namespace unigame.staticecs {
    public struct StaticEcsUpdateSystems : ISystemsType { }

    public struct StaticEcsFixedUpdateSystems : ISystemsType { }

    public struct StaticEcsLateUpdateSystems : ISystemsType { }

    public struct StaticEcsCleanupSystems : ISystemsType { }
}
