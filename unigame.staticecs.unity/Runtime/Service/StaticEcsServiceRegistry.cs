namespace unigame.staticecs.unity {
    public static class StaticEcsServiceRegistry {
        public static IStaticEcsService Active { get; private set; }

        public static StaticEcsStartupReport LastReport { get; private set; }

        public static void Register(IStaticEcsService service) {
            Active = service;
            LastReport = service?.Report;
        }

        public static void Unregister(IStaticEcsService service) {
            if (!ReferenceEquals(Active, service)) {
                return;
            }

            Active = null;
        }
    }
}
