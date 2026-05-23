using FFS.Libraries.StaticEcs;

namespace unigame.staticecs {
    public interface IStaticEcsFeature<TWorld>
        where TWorld : struct, IWorldType {
        string FeatureName { get; }

        bool IsEnabled { get; }

        void RegisterTypes(World<TWorld>.TypeRegistrar types);
    }
}
