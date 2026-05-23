using FFS.Libraries.StaticEcs;
using unigame.staticecs;

namespace unigame.staticecs.features {
    public class CharacteristicFeature<TWorld, TCharacteristic> : StaticEcsFeature<TWorld>
        where TWorld : struct, IWorldType
        where TCharacteristic : struct, ICharacteristicType {
        public override void RegisterTypes(World<TWorld>.TypeRegistrar types) {
            types
                .Component<CharacteristicComponent<TCharacteristic>>()
                .Event<CharacteristicChangedEvent<TCharacteristic>>();
        }
    }
}
