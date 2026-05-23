using unigame.staticecs.unity;
using UnityEditor;
using UnityEngine;

namespace UniGame.StaticEcs.Editor.Drawers {
    [CustomPropertyDrawer(typeof(StaticEcsSystemsConfig))]
    public sealed class StaticEcsSystemsConfigDrawer : PropertyDrawer {
        private static readonly string[] LoopFields = {
            "update",
            "fixedUpdate",
            "lateUpdate",
            "cleanup"
        };

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            EditorGUI.BeginProperty(position, label, property);

            property.isExpanded = EditorGUI.Foldout(
                new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight),
                property.isExpanded,
                label,
                true);

            if (!property.isExpanded) {
                EditorGUI.EndProperty();
                return;
            }

            var y = position.y + EditorGUIUtility.singleLineHeight + 2f;
            EditorGUI.indentLevel++;

            EditorGUI.LabelField(
                new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                "Loops",
                EditorStyles.boldLabel);
            y += EditorGUIUtility.singleLineHeight + 2f;

            foreach (var fieldName in LoopFields) {
                var prop = property.FindPropertyRelative(fieldName);
                if (prop == null) {
                    continue;
                }

                EditorGUI.PropertyField(
                    new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                    prop);
                y += EditorGUIUtility.singleLineHeight + 2f;
            }

            var baseSize = property.FindPropertyRelative("baseSize");
            if (baseSize != null) {
                EditorGUI.PropertyField(
                    new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                    baseSize);
            }

            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) {
            if (!property.isExpanded) {
                return EditorGUIUtility.singleLineHeight;
            }

            var lines = 1 + 1 + LoopFields.Length + 1;
            return EditorGUIUtility.singleLineHeight * lines + lines * 2f;
        }
    }
}
