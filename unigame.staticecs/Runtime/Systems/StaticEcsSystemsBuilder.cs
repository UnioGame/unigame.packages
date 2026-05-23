using FFS.Libraries.StaticEcs;

namespace unigame.staticecs {
    public readonly struct StaticEcsSystemsBuilder<TWorld, TSystemsType>
        where TWorld : struct, IWorldType
        where TSystemsType : struct, ISystemsType {
        public StaticEcsSystemsBuilder<TWorld, TSystemsType> Add<TSystem>(TSystem system, short order = 0)
            where TSystem : ISystem {
            World<TWorld>.Systems<TSystemsType>.Add(system, order);
            return this;
        }
    }
}
