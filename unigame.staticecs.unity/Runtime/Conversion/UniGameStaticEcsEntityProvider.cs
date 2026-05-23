using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticEcs.Unity;
using UnityEngine;

namespace unigame.staticecs.unity {
    public abstract class UniGameStaticEcsEntityProvider<TWorld> : StaticEcsEntityProvider<TWorld>
        where TWorld : struct, IWorldType {
        [SerializeReference]
        public List<IStaticEcsConverter<TWorld>> serializableConverters = new();

        [SerializeField]
        public List<StaticEcsConverterAsset<TWorld>> assetConverters = new();

        private readonly List<IStaticEcsConverter<TWorld>> _runtime = new();
        private readonly List<IStaticEcsConverter<TWorld>> _monoBuf = new();
        private readonly List<IStaticEcsConverter<TWorld>> _registered = new();

        public IReadOnlyList<IStaticEcsConverter<TWorld>> RuntimeConverters => _runtime;

        public void RegisterRuntime(IStaticEcsConverter<TWorld> converter) {
            if (converter == null || _registered.Contains(converter)) {
                return;
            }
            _registered.Add(converter);
        }

        public bool UnregisterRuntime(IStaticEcsConverter<TWorld> converter) {
            return converter != null && _registered.Remove(converter);
        }

        public override bool CreateEntity() {
            if (!base.CreateEntity()) {
                return false;
            }

            if (!entityGid.TryUnpack<TWorld>(out var entity)) {
                return true;
            }

            CollectConverters();

            for (var i = 0; i < _runtime.Count; i++) {
                var c = _runtime[i];
                if (c == null || !c.IsEnabled) {
                    continue;
                }
                c.Apply(entity, gameObject);
            }

            return true;
        }

        public override void ResolveLinks() {
            base.ResolveLinks();

            if (World<TWorld>.Status != WorldStatus.Initialized) return;
            if (entityGid.Status<TWorld>() != GIDStatus.Active) return;
            if (!entityGid.TryUnpack<TWorld>(out var entity)) return;

            for (var i = 0; i < _runtime.Count; i++) {
                if (_runtime[i] is IStaticEcsLinkResolver<TWorld> r) {
                    r.ResolveLinks(entity, gameObject);
                }
            }
        }

        private new void OnDestroy() {
            if (World<TWorld>.Status == WorldStatus.Initialized
                && entityGid.Status<TWorld>() == GIDStatus.Active
                && entityGid.TryUnpack<TWorld>(out var entity)) {
                for (var i = 0; i < _runtime.Count; i++) {
                    if (_runtime[i] is IStaticEcsConverterDestroyHandler<TWorld> h) {
                        h.OnEntityDestroyed(entity, gameObject);
                    }
                }
            }

            if (onDestroyType == OnDestroyType.DestroyEntity
                && World<TWorld>.Status == WorldStatus.Initialized
                && entityGid.Status<TWorld>() == GIDStatus.Active) {
                entityGid.Unpack<TWorld>().Destroy();
            }

            entityGid = default;
            _runtime.Clear();
            _monoBuf.Clear();
        }

        private void CollectConverters() {
            _runtime.Clear();
            _monoBuf.Clear();
            GetComponents(_monoBuf);

            for (var i = 0; i < _monoBuf.Count; i++) {
                var c = _monoBuf[i];
                if (c == null || ReferenceEquals(c, this)) continue;
                _runtime.Add(c);
            }

            if (serializableConverters != null) {
                for (var i = 0; i < serializableConverters.Count; i++) {
                    var c = serializableConverters[i];
                    if (c != null) _runtime.Add(c);
                }
            }

            if (assetConverters != null) {
                for (var i = 0; i < assetConverters.Count; i++) {
                    var c = assetConverters[i];
                    if (c != null) _runtime.Add(c);
                }
            }

            for (var i = 0; i < _registered.Count; i++) {
                var c = _registered[i];
                if (c != null) _runtime.Add(c);
            }
        }
    }
}
