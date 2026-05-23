using FFS.Libraries.StaticEcs;

namespace unigame.staticecs {
    public abstract class StaticEcsFeature<TWorld> : IStaticEcsFeature<TWorld>
        where TWorld : struct, IWorldType {
        public virtual string FeatureName => GetType().Name;

        public virtual bool IsEnabled => true;

        public virtual void RegisterTypes(World<TWorld>.TypeRegistrar types) { }
    }
}
