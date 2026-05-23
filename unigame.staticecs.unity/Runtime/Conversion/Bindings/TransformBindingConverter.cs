using FFS.Libraries.StaticEcs;
using UnityEngine;

namespace unigame.staticecs.unity {
    public abstract class TransformBindingConverter<TWorld> : StaticEcsMonoConverter<TWorld, TransformBindingComponent>
        where TWorld : struct, IWorldType {
        [SerializeField]
        private Transform _target;

        protected override TransformBindingComponent Build(GameObject host) {
            return new TransformBindingComponent { Transform = _target != null ? _target : host.transform };
        }
    }

    [AddComponentMenu("Static ECS/Transform Binding Converter")]
    public sealed class TransformBindingConverter : TransformBindingConverter<Main> { }
}
