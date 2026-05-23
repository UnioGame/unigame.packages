using FFS.Libraries.StaticEcs.Unity;
using FFS.Libraries.StaticEcs.Unity.Editor;
using unigame.staticecs.unity;
using UnityEditor;
using UnityEngine;

namespace UniGame.StaticEcs.Editor.View.Tabs {
    public sealed class BootstrapReportTab : IStaticEcsViewTab {
        private const string TabName = "Bootstrap Report";

        public string Name() => TabName;

        public void Init() { }

        public void OnWorldChanged(AbstractWorldData newWorldData) { }

        public void Destroy() { }

        public void Draw() {
            EditorGUILayout.LabelField("StaticEcsStartupReport", EditorStyles.boldLabel);

            var report = StaticEcsServiceRegistry.LastReport;
            if (report == null) {
                EditorGUILayout.HelpBox(
                    "No StaticEcsService is currently registered. Reports become available once a StaticEcsServiceSource publishes a service into the GameContext during play mode.",
                    MessageType.Info);
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                EditorGUILayout.Toggle("World Created", report.worldCreated);
                EditorGUILayout.Toggle("Types Registered", report.typesRegistered);
                EditorGUILayout.Toggle("World Initialized", report.worldInitialized);
                EditorGUILayout.Toggle("Systems Initialized", report.systemsInitialized);
                EditorGUILayout.IntField("Modules Registered", report.modulesRegistered);
                EditorGUILayout.IntField("Update Count", report.updateCount);
                EditorGUILayout.LabelField("Message", report.message ?? string.Empty);
                EditorGUILayout.LabelField("Success", report.IsSuccess.ToString());
            }

            if (GUILayout.Button("Repaint")) {
                EditorWindow.focusedWindow?.Repaint();
            }
        }
    }
}
