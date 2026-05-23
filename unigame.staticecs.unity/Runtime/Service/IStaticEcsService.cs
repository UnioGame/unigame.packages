using UniGame.GameFlow.Runtime;

namespace unigame.staticecs.unity {
    public interface IStaticEcsService : IGameService {
        StaticEcsStartupReport Report { get; }

        bool IsInitialized { get; }

        void Update();

        void FixedUpdate();

        void LateUpdate();

        void CleanupUpdate();
    }
}
