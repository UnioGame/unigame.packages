using FFS.Libraries.StaticEcs;
using UnityEngine;

namespace unigame.staticecs.unity {
    public abstract class StaticEcsMonoConverter<TWorld> : MonoBehaviour, IStaticEcsConverter<TWorld>
        where TWorld : struct, IWorldType {
        [SerializeField]
        protected bool _isEnabled = true;

        public virtual bool IsEnabled => _isEnabled && isActiveAndEnabled;

        public abstract void Apply(World<TWorld>.Entity entity, GameObject host);
    }

    public abstract class StaticEcsMonoConverter<TWorld, TComponent> : StaticEcsMonoConverter<TWorld>
        where TWorld : struct, IWorldType
        where TComponent : struct, IComponent {
        public override void Apply(World<TWorld>.Entity entity, GameObject host) {
            entity.Set(Build(host));
        }

        protected abstract TComponent Build(GameObject host);
    }

    public abstract class StaticEcsMonoConverter : StaticEcsMonoConverter<Main> { }
}
