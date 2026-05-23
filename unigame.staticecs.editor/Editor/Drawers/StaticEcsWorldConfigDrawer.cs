using unigame.staticecs.unity;
using UnityEditor;
using UnityEngine;

namespace UniGame.StaticEcs.Editor.Drawers {
    [CustomPropertyDrawer(typeof(StaticEcsWorldConfig))]
    public sealed class StaticEcsWorldConfigDrawer : PropertyDrawer {
        private static readonly string[] CapacityFields = {
            "baseEntitiesCapacity",
            "baseComponentTypesCount",
            "baseClustersCapacity"
        };

        private static readonly string[] ThreadingFields = {
            "threadCount",
            "workerSpinCount",
            "independent"
        };

        private static readonly string[] TrackingFields = {
            "trackCreated",
            "trackingBufferSize"
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

            y = DrawGroup(position, y, property, "Capacity", CapacityFields);
            y = DrawGroup(position, y, property, "Threading", ThreadingFields);
            DrawGroup(position, y, property, "Tracking", TrackingFields);

            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) {
            if (!property.isExpanded) {
                return EditorGUIUtility.singleLineHeight;
            }

            var groups = 3;
            var fields = CapacityFields.Length + ThreadingFields.Length + TrackingFields.Length;
            return EditorGUIUtility.singleLineHeight * (1 + groups + fields)
                + (groups + fields) * 2f;
        }

        private static float DrawGroup(Rect position, float y, SerializedProperty property, string title, string[] fields) {
            EditorGUI.LabelField(
                new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                title,
                EditorStyles.boldLabel);
            y += EditorGUIUtility.singleLineHeight + 2f;

            foreach (var fieldName in fields) {
                var prop = property.FindPropertyRelative(fieldName);
                if (prop == null) {
                    continue;
                }

                EditorGUI.PropertyField(
                    new Rect(position.x, y, position.width, EditorGUIUtility.singleLineHeight),
                    prop);
                y += EditorGUIUtility.singleLineHeight + 2f;
            }

            return y;
        }
    }
}
