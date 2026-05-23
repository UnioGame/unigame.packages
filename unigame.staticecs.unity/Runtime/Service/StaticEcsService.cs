using System;
using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using UniGame.Core.Runtime;
using UniGame.GameFlow.Runtime;
using UniGame.Runtime.DataFlow;
using unigame.staticecs;

namespace unigame.staticecs.unity {
    public sealed class StaticEcsService<TWorld> : IStaticEcsService
        where TWorld : struct, IWorldType {
        private readonly LifeTime _lifeTime = new();
        private readonly StaticEcsWorldConfig _worldConfig;
        private readonly StaticEcsSystemsConfig _systemsConfig;
        private bool _updateSystemsCreated;
        private bool _fixedSystemsCreated;
        private bool _lateSystemsCreated;
        private bool _cleanupSystemsCreated;

        public StaticEcsService(
            StaticEcsWorldConfig worldConfig,
            StaticEcsSystemsConfig systemsConfig) {
            _worldConfig = worldConfig;
            _systemsConfig = systemsConfig;
            Report = new StaticEcsStartupReport();
            StaticEcsServiceRegistry.Register(this);
        }

        public StaticEcsStartupReport Report { get; }

        public ILifeTime LifeTime => _lifeTime;

        public bool IsInitialized => World<TWorld>.Status == WorldStatus.Initialized;

        public void Initialize(IReadOnlyList<StaticEcsModuleConfig> modules) {
            DestroyWorldIfNeeded();

            World<TWorld>.Create(_worldConfig.CreateWorldConfig());
            Report.worldCreated = true;

            var registrar = World<TWorld>.Types();
            var moduleCount = 0;

            if (modules != null) {
                for (var i = 0; i < modules.Count; i++) {
                    if (!TryGetModule(modules[i], out var module)) {
                        continue;
                    }

                    module.RegisterTypes(registrar);
                    moduleCount++;
                }
            }

            Report.modulesRegistered = moduleCount;
            Report.typesRegistered = true;

            World<TWorld>.Initialize(_worldConfig.baseEntitiesCapacity);
            Report.worldInitialized = true;

            CreateSystems();

            if (modules != null) {
                for (var i = 0; i < modules.Count; i++) {
                    if (!TryGetModule(modules[i], out var module)) {
                        continue;
                    }

                    module.RegisterUpdateSystems(this);
                    module.RegisterFixedUpdateSystems(this);
                    module.RegisterLateUpdateSystems(this);
                    module.RegisterCleanupSystems(this);
                }
            }

            InitializeSystems();

            if (modules != null) {
                for (var i = 0; i < modules.Count; i++) {
                    if (TryGetModule(modules[i], out var module)) {
                        module.OnWorldInitialized(this);
                    }
                }
            }

            Report.message = $"Static ECS world `{typeof(TWorld).Name}` initialized. Modules: {moduleCount}.";
        }

        public StaticEcsService<TWorld> AddUpdateSystem<TSystem>(TSystem system, short order = 0)
            where TSystem : ISystem {
            World<TWorld>.Systems<StaticEcsUpdateSystems>.Add(system, order);
            return this;
        }

        public StaticEcsService<TWorld> AddFixedUpdateSystem<TSystem>(TSystem system, short order = 0)
            where TSystem : ISystem {
            World<TWorld>.Systems<StaticEcsFixedUpdateSystems>.Add(system, order);
            return this;
        }

        public StaticEcsService<TWorld> AddLateUpdateSystem<TSystem>(TSystem system, short order = 0)
            where TSystem : ISystem {
            World<TWorld>.Systems<StaticEcsLateUpdateSystems>.Add(system, order);
            return this;
        }

        public StaticEcsService<TWorld> AddCleanupSystem<TSystem>(TSystem system, short order = 0)
            where TSystem : ISystem {
            World<TWorld>.Systems<StaticEcsCleanupSystems>.Add(system, order);
            return this;
        }

        public void Update() {
            if (!_updateSystemsCreated) {
                return;
            }

            World<TWorld>.Systems<StaticEcsUpdateSystems>.Update();
            World<TWorld>.Tick();
            Report.updateCount++;
        }

        public void FixedUpdate() {
            if (_fixedSystemsCreated) {
                World<TWorld>.Systems<StaticEcsFixedUpdateSystems>.Update();
            }
        }

        public void LateUpdate() {
            if (_lateSystemsCreated) {
                World<TWorld>.Systems<StaticEcsLateUpdateSystems>.Update();
            }
        }

        public void CleanupUpdate() {
            if (_cleanupSystemsCreated) {
                World<TWorld>.Systems<StaticEcsCleanupSystems>.Update();
            }
        }

        public void Dispose() {
            DestroySystems();
            DestroyWorldIfNeeded();
            StaticEcsServiceRegistry.Unregister(this);
            _lifeTime.Terminate();
        }

        private static bool TryGetModule(
            StaticEcsModuleConfig module,
            out StaticEcsModuleConfig<TWorld> typedModule) {
            typedModule = module as StaticEcsModuleConfig<TWorld>;
            return typedModule != null && typedModule.enabled;
        }

        private void CreateSystems() {
            if (_systemsConfig.update) {
                World<TWorld>.Systems<StaticEcsUpdateSystems>.Create(_systemsConfig.baseSize);
                _updateSystemsCreated = true;
            }

            if (_systemsConfig.fixedUpdate) {
                World<TWorld>.Systems<StaticEcsFixedUpdateSystems>.Create(_systemsConfig.baseSize);
                _fixedSystemsCreated = true;
            }

            if (_systemsConfig.lateUpdate) {
                World<TWorld>.Systems<StaticEcsLateUpdateSystems>.Create(_systemsConfig.baseSize);
                _lateSystemsCreated = true;
            }

            if (_systemsConfig.cleanup) {
                World<TWorld>.Systems<StaticEcsCleanupSystems>.Create(_systemsConfig.baseSize);
                _cleanupSystemsCreated = true;
            }
        }

        private void InitializeSystems() {
            if (_updateSystemsCreated) {
                World<TWorld>.Systems<StaticEcsUpdateSystems>.Initialize();
            }

            if (_fixedSystemsCreated) {
                World<TWorld>.Systems<StaticEcsFixedUpdateSystems>.Initialize();
            }

            if (_lateSystemsCreated) {
                World<TWorld>.Systems<StaticEcsLateUpdateSystems>.Initialize();
            }

            if (_cleanupSystemsCreated) {
                World<TWorld>.Systems<StaticEcsCleanupSystems>.Initialize();
            }

            Report.systemsInitialized = true;
        }

        private void DestroySystems() {
            if (_cleanupSystemsCreated) {
                World<TWorld>.Systems<StaticEcsCleanupSystems>.Destroy();
                _cleanupSystemsCreated = false;
            }

            if (_lateSystemsCreated) {
                World<TWorld>.Systems<StaticEcsLateUpdateSystems>.Destroy();
                _lateSystemsCreated = false;
            }

            if (_fixedSystemsCreated) {
                World<TWorld>.Systems<StaticEcsFixedUpdateSystems>.Destroy();
                _fixedSystemsCreated = false;
            }

            if (_updateSystemsCreated) {
                World<TWorld>.Systems<StaticEcsUpdateSystems>.Destroy();
                _updateSystemsCreated = false;
            }
        }

        private static void DestroyWorldIfNeeded() {
            if (World<TWorld>.Status != WorldStatus.NotCreated) {
                World<TWorld>.Destroy();
            }
        }
    }
}
