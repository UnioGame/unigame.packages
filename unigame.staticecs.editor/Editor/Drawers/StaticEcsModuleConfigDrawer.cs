using unigame.staticecs.unity;
using UnityEditor;
using UnityEngine;

namespace UniGame.StaticEcs.Editor.Drawers {
    [CustomPropertyDrawer(typeof(StaticEcsModuleConfig), true)]
    public sealed class StaticEcsModuleConfigDrawer : PropertyDrawer {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            var module = property.objectReferenceValue as StaticEcsModuleConfig;
            var icon = module != null && module.enabled ? "\u25CF" : "\u25CB";
            var name = module != null && !string.IsNullOrEmpty(module.moduleName)
                ? module.moduleName
                : module != null
                    ? module.name
                    : "(empty)";

            var prefixed = EditorGUI.PrefixLabel(position, new GUIContent($"{icon} {label.text}"));
            EditorGUI.PropertyField(prefixed, property, new GUIContent(name), false);
        }
    }
}
