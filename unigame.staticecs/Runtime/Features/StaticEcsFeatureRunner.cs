using System.Collections.Generic;
using FFS.Libraries.StaticEcs;

namespace unigame.staticecs {
    public static class StaticEcsFeatureRunner {
        public static void RegisterTypes<TWorld>(IReadOnlyList<IStaticEcsFeature<TWorld>> features)
            where TWorld : struct, IWorldType {
            if (features == null) {
                return;
            }

            var types = World<TWorld>.Types();
            for (var i = 0; i < features.Count; i++) {
                var feature = features[i];
                if (feature == null || !feature.IsEnabled) {
                    continue;
                }

                feature.RegisterTypes(types);
            }
        }

        public static void RegisterSystems<TWorld, TSystemsType>(
            IReadOnlyList<IStaticEcsSystemsFeature<TWorld, TSystemsType>> features)
            where TWorld : struct, IWorldType
            where TSystemsType : struct, ISystemsType {
            if (features == null) {
                return;
            }

            var systems = new StaticEcsSystemsBuilder<TWorld, TSystemsType>();
            for (var i = 0; i < features.Count; i++) {
                var feature = features[i];
                if (feature == null || !feature.IsEnabled) {
                    continue;
                }

                feature.RegisterSystems(systems);
            }
        }
    }
}
