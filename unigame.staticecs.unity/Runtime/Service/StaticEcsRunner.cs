using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using FFS.Libraries.StaticEcs;
using UnityEngine;

namespace unigame.staticecs.unity {
    public sealed class StaticEcsRunner<TWorld>
        where TWorld : struct, IWorldType {
        private readonly StaticEcsService<TWorld> _service;
        private readonly StaticEcsSystemsConfig _config;

        public StaticEcsRunner(StaticEcsService<TWorld> service, StaticEcsSystemsConfig config) {
            _service = service;
            _config = config;
        }

        public void Start() {
            var token = _service.LifeTime.Token;
            if (_config.update)      RunAsync(_service.Update,        _config.updateTiming,      token).Forget();
            if (_config.fixedUpdate) RunAsync(_service.FixedUpdate,   _config.fixedUpdateTiming, token).Forget();
            if (_config.lateUpdate)  RunAsync(_service.LateUpdate,    _config.lateUpdateTiming,  token).Forget();
            if (_config.cleanup)     RunAsync(_service.CleanupUpdate, _config.cleanupTiming,     token).Forget();
        }

        private async UniTaskVoid RunAsync(Action tick, PlayerLoopTiming timing, CancellationToken token) {
            try {
                while (Application.isPlaying && _service.IsInitialized) {
                    tick();
                    await UniTask.Yield(timing, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }
}
