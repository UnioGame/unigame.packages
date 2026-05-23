using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using FFS.Libraries.StaticEcs;
using UniGame.Context.Runtime;
using UniGame.Core.Runtime;
using UnityEngine;

namespace unigame.staticecs.unity {
    public abstract class StaticEcsServiceSource<TWorld> : ServiceDataSourceAsset<IStaticEcsService>
        where TWorld : struct, IWorldType {
        public StaticEcsWorldConfig world = StaticEcsWorldConfig.Default;
        public StaticEcsSystemsConfig systems = StaticEcsSystemsConfig.Default;
        public List<StaticEcsModuleConfig> modules = new();

        protected override UniTask<IStaticEcsService> CreateServiceInternalAsync(IContext context) {
            var service = new StaticEcsService<TWorld>(world, systems);
            service.Initialize(modules);

            context.Publish(service.Report);

            new StaticEcsRunner<TWorld>(service, systems).Start();

            return UniTask.FromResult<IStaticEcsService>(service);
        }
    }

    [CreateAssetMenu(menuName = "Static ECS/Service Source", fileName = nameof(StaticEcsServiceSource))]
    public sealed class StaticEcsServiceSource : StaticEcsServiceSource<Main> { }
}
