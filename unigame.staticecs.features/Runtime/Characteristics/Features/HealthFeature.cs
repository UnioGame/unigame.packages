using FFS.Libraries.StaticEcs;

namespace unigame.staticecs.features {
    public sealed class HealthFeature<TWorld> : CharacteristicFeature<TWorld, HealthCharacteristic>
        where TWorld : struct, IWorldType { }
}
