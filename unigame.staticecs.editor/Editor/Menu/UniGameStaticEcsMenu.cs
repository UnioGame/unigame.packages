using FFS.Libraries.StaticEcs.Unity.Editor;
using UnityEditor;

namespace UniGame.StaticEcs.Editor.Menu {
    public static class UniGameStaticEcsMenu {
        private const string Root = "Tools/UniGame/Static ECS/";

        [MenuItem(Root + "Fix Broken Providers", priority = 200)]
        public static void OpenBrokenProvidersFixer() {
            EditorWindow.GetWindow<BrokenProvidersFixerWindow>().Show();
        }

        [MenuItem(Root + "Documentation/Open Knowledge Base", priority = 400)]
        public static void OpenKnowledgeBase() {
            EditorUtility.RevealInFinder("docs/knowledge/static-ecs/index.md");
        }
    }
}
