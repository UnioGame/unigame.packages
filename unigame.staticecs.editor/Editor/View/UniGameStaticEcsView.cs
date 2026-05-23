using System.Collections.Generic;
using System.Reflection;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticEcs.Unity;
using FFS.Libraries.StaticEcs.Unity.Editor;
using UnityEditor;
using UnityEngine;

namespace UniGame.StaticEcs.Editor.View {
    public abstract class UniGameStaticEcsView<TWorld, TEntityProvider, TEventProvider>
        : StaticEcsView<TWorld, TEntityProvider, TEventProvider>
        where TWorld : struct, IWorldType
        where TEntityProvider : StaticEcsEntityProvider<TWorld>
        where TEventProvider : StaticEcsEventProvider<TWorld> {
        private static readonly FieldInfo TabsField = typeof(StaticEcsView<TWorld, TEntityProvider, TEventProvider>)
            .GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic);

        private bool _projectTabsInjected;
        private bool _injectionWarningLogged;

        protected virtual IEnumerable<IStaticEcsViewTab> CreateProjectTabs() {
            yield return new Tabs.GameModulesTab();
            yield return new Tabs.BootstrapReportTab();
            yield return new Tabs.FeatureCatalogTab<TWorld>();
        }

        private void Update() {
            if (_projectTabsInjected) {
                return;
            }

            if (TabsField == null) {
                if (!_injectionWarningLogged) {
                    Debug.LogWarning(
                        "[UniGameStaticEcsView] StaticEcsView._tabs field not found. Project tabs will not be injected. " +
                        "Update unigame.staticecs.editor against the current Static ECS Unity package.");
                    _injectionWarningLogged = true;
                }

                _projectTabsInjected = true;
                return;
            }

            if (TabsField.GetValue(this) is not List<IStaticEcsViewTab> tabs || tabs.Count == 0) {
                return;
            }

            foreach (var tab in CreateProjectTabs()) {
                if (tab == null) {
                    continue;
                }

                tab.SetNavigation(this);
                tab.Init();
                tabs.Add(tab);
            }

            _projectTabsInjected = true;
            Repaint();
        }
    }
}
