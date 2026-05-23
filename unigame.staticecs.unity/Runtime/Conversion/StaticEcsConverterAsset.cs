using FFS.Libraries.StaticEcs;
using UnityEngine;

namespace unigame.staticecs.unity {
    public abstract class StaticEcsConverterAssetBase : ScriptableObject { }

    public abstract class StaticEcsConverterAsset<TWorld> : StaticEcsConverterAssetBase, IStaticEcsConverter<TWorld>
        where TWorld : struct, IWorldType {
        [SerializeField]
        protected bool _isEnabled = true;

        public virtual bool IsEnabled => _isEnabled;

        public abstract void Apply(World<TWorld>.Entity entity, GameObject host);
    }

    public abstract class StaticEcsConverterAsset : StaticEcsConverterAsset<Main> { }
}
