using FFS.Libraries.StaticEcs;

namespace unigame.staticecs.features {
    public static class CharacteristicOperations {
        public static bool SetValue<TWorld, TCharacteristic>(
            ref CharacteristicComponent<TCharacteristic> characteristic,
            EntityGID target,
            float value)
            where TWorld : struct, IWorldType
            where TCharacteristic : struct, ICharacteristicType {
            var previous = characteristic.Value;
            characteristic.SetValue(value);

            if (previous == characteristic.Value) {
                return false;
            }

            SendChanged<TWorld, TCharacteristic>(target, previous, in characteristic);
            return true;
        }

        public static bool AddValue<TWorld, TCharacteristic>(
            ref CharacteristicComponent<TCharacteristic> characteristic,
            EntityGID target,
            float delta)
            where TWorld : struct, IWorldType
            where TCharacteristic : struct, ICharacteristicType {
            return SetValue<TWorld, TCharacteristic>(ref characteristic, target, characteristic.Value + delta);
        }

        private static void SendChanged<TWorld, TCharacteristic>(
            EntityGID target,
            float previous,
            in CharacteristicComponent<TCharacteristic> characteristic)
            where TWorld : struct, IWorldType
            where TCharacteristic : struct, ICharacteristicType {
            World<TWorld>.SendEvent(new CharacteristicChangedEvent<TCharacteristic> {
                Target = target,
                PreviousValue = previous,
                Value = characteristic.Value,
                BaseValue = characteristic.BaseValue,
                MinValue = characteristic.MinValue,
                MaxValue = characteristic.MaxValue
            });
        }
    }
}
