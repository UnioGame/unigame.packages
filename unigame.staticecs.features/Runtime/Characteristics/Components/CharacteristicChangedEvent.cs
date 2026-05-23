using FFS.Libraries.StaticEcs;

namespace unigame.staticecs.features {
    public struct CharacteristicChangedEvent<TCharacteristic> : IEvent
        where TCharacteristic : struct, ICharacteristicType {
        public EntityGID Target;
        public float PreviousValue;
        public float Value;
        public float BaseValue;
        public float MinValue;
        public float MaxValue;
    }
}
