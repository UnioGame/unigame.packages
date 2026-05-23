using FFS.Libraries.StaticEcs;
using UnityEngine;

namespace unigame.staticecs.unity {
    public abstract class StaticEcsModuleConfig : ScriptableObject {
        public bool enabled = true;
        public string moduleName;
    }

    public abstract class StaticEcsModuleConfig<TWorld> : StaticEcsModuleConfig
        where TWorld : struct, IWorldType {
        public virtual void RegisterTypes(World<TWorld>.TypeRegistrar types) { }

        public virtual void RegisterUpdateSystems(StaticEcsService<TWorld> service) { }

        public virtual void RegisterFixedUpdateSystems(StaticEcsService<TWorld> service) { }

        public virtual void RegisterLateUpdateSystems(StaticEcsService<TWorld> service) { }

        public virtual void RegisterCleanupSystems(StaticEcsService<TWorld> service) { }

        public virtual void OnWorldInitialized(StaticEcsService<TWorld> service) { }
    }
}
