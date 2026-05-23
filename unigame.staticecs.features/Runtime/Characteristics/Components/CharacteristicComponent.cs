using System;
using FFS.Libraries.StaticEcs;

namespace unigame.staticecs.features {
    [Serializable]
    public struct CharacteristicComponent<TCharacteristic> : IComponent
        where TCharacteristic : struct, ICharacteristicType {
        public float Value;
        public float BaseValue;
        public float MinValue;
        public float MaxValue;

        public CharacteristicComponent(float value, float minValue, float maxValue, float baseValue) {
            if (maxValue < minValue) {
                maxValue = minValue;
            }

            MinValue = minValue;
            MaxValue = maxValue;
            BaseValue = Clamp(baseValue, minValue, maxValue);
            Value = Clamp(value, minValue, maxValue);
        }

        public static CharacteristicComponent<TCharacteristic> Create(float value, float minValue = 0f, float maxValue = float.MaxValue) {
            return new CharacteristicComponent<TCharacteristic>(value, minValue, maxValue, value);
        }

        public float SetValue(float value) {
            Value = Clamp(value, MinValue, MaxValue);
            return Value;
        }

        public float AddValue(float delta) {
            return SetValue(Value + delta);
        }

        public float SetBaseValue(float value, bool resetValue = false) {
            BaseValue = Clamp(value, MinValue, MaxValue);
            if (resetValue) {
                Value = BaseValue;
            }

            return BaseValue;
        }

        public void SetLimits(float minValue, float maxValue, bool clampCurrent = true) {
            if (maxValue < minValue) {
                maxValue = minValue;
            }

            MinValue = minValue;
            MaxValue = maxValue;
            BaseValue = Clamp(BaseValue, MinValue, MaxValue);

            if (clampCurrent) {
                Value = Clamp(Value, MinValue, MaxValue);
            }
        }

        private static float Clamp(float value, float minValue, float maxValue) {
            if (value < minValue) {
                return minValue;
            }

            return value > maxValue ? maxValue : value;
        }
    }
}
