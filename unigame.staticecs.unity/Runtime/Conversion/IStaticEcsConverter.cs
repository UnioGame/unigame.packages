using FFS.Libraries.StaticEcs;
using UnityEngine;

namespace unigame.staticecs.unity {
    public interface IStaticEcsConverter<TWorld>
        where TWorld : struct, IWorldType {
        bool IsEnabled { get; }
        void Apply(World<TWorld>.Entity entity, GameObject host);
    }

    public interface IStaticEcsConverterDestroyHandler<TWorld>
        where TWorld : struct, IWorldType {
        void OnEntityDestroyed(World<TWorld>.Entity entity, GameObject host);
    }

    public interface IStaticEcsLinkResolver<TWorld>
        where TWorld : struct, IWorldType {
        void ResolveLinks(World<TWorld>.Entity entity, GameObject host);
    }
}
