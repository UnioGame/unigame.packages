using System.Collections.Generic;
using FFS.Libraries.StaticEcs.Unity.Editor;
using unigame.staticecs.unity;
using UnityEditor;
using UnityEngine;

namespace UniGame.StaticEcs.Editor.View.Tabs {
    using FFS.Libraries.StaticEcs.Unity;

    public sealed class GameModulesTab : IStaticEcsViewTab {
        private const string TabName = "Game Modules";

        private readonly List<ScriptableObject> _sources = new();
        private Vector2 _scroll;
        private double _lastScanTime;

        public string Name() => TabName;

        public void Init() {
            RefreshSources();
        }

        public void OnWorldChanged(AbstractWorldData newWorldData) { }

        public void Destroy() {
            _sources.Clear();
        }

        public void Draw() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField($"Service sources: {_sources.Count}", EditorStyles.boldLabel);
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) {
                    RefreshSources();
                }
            }

            if (_sources.Count == 0) {
                EditorGUILayout.HelpBox(
                    "No StaticEcsServiceSource assets found in the project. Create one via Assets/Create/UniGame/Static ECS.",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var source in _sources) {
                if (source == null) {
                    continue;
                }

                DrawSource(source);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSource(ScriptableObject source) {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(source.name, EditorStyles.boldLabel);
                    if (GUILayout.Button("Ping", GUILayout.Width(60))) {
                        EditorGUIUtility.PingObject(source);
                    }

                    if (GUILayout.Button("Select", GUILayout.Width(60))) {
                        Selection.activeObject = source;
                    }
                }

                var so = new SerializedObject(source);
                var modulesProperty = so.FindProperty("modules");
                if (modulesProperty == null) {
                    EditorGUILayout.HelpBox("Source has no `modules` field.", MessageType.None);
                    return;
                }

                EditorGUILayout.LabelField($"Modules: {modulesProperty.arraySize}");
                for (var i = 0; i < modulesProperty.arraySize; i++) {
                    var element = modulesProperty.GetArrayElementAtIndex(i);
                    DrawModuleEntry(element);
                }
            }
        }

        private static void DrawModuleEntry(SerializedProperty element) {
            using (new EditorGUILayout.HorizontalScope()) {
                var module = element.objectReferenceValue as StaticEcsModuleConfig;
                if (module == null) {
                    EditorGUILayout.LabelField("(missing module)");
                    return;
                }

                var label = string.IsNullOrEmpty(module.moduleName) ? module.name : module.moduleName;
                var icon = module.enabled ? "\u25CF" : "\u25CB";
                EditorGUILayout.LabelField($"{icon} {label}");
                if (GUILayout.Button("Ping", GUILayout.Width(60))) {
                    EditorGUIUtility.PingObject(module);
                }
            }
        }

        private void RefreshSources() {
            _sources.Clear();

            var guids = AssetDatabase.FindAssets("t:StaticEcsModuleConfig");
            // We list ServiceSource assets, not modules; modules are listed inside each source.
            // ScriptableObject service sources are subclasses of StaticEcsServiceSource<>.
            // Fall back to scanning for any ScriptableObject ending with "ServiceSource".
            var serviceGuids = AssetDatabase.FindAssets("t:ScriptableObject ServiceSource");
            foreach (var guid in serviceGuids) {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path) as ScriptableObject;
                if (asset == null) {
                    continue;
                }

                var so = new SerializedObject(asset);
                if (so.FindProperty("modules") == null) {
                    continue;
                }

                _sources.Add(asset);
            }

            _lastScanTime = EditorApplication.timeSinceStartup;
            // 'guids' kept to keep the API call referenced even if not consumed.
            _ = guids;
            _ = _lastScanTime;
        }
    }
}
