using System;
using System.Collections.Generic;
using System.Reflection;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticEcs.Unity;
using FFS.Libraries.StaticEcs.Unity.Editor;
using unigame.staticecs;
using UnityEditor;
using UnityEngine;

namespace UniGame.StaticEcs.Editor.View.Tabs {
    public sealed class FeatureCatalogTab<TWorld> : IStaticEcsViewTab
        where TWorld : struct, IWorldType {
        private const string TabName = "Feature Catalog";

        private readonly List<Type> _featureTypes = new();
        private readonly List<Type> _systemsFeatureTypes = new();
        private string _filter = string.Empty;
        private Vector2 _scroll;

        public string Name() => TabName;

        public void Init() {
            RefreshCatalog();
        }

        public void OnWorldChanged(AbstractWorldData newWorldData) { }

        public void Destroy() {
            _featureTypes.Clear();
            _systemsFeatureTypes.Clear();
        }

        public void Draw() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField("Filter", GUILayout.Width(50));
                _filter = EditorGUILayout.TextField(_filter);
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) {
                    RefreshCatalog();
                }
            }

            EditorGUILayout.LabelField($"Features: {_featureTypes.Count}, Systems features: {_systemsFeatureTypes.Count}",
                EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawSection($"IStaticEcsFeature<{typeof(TWorld).Name}>", _featureTypes);
            DrawSection($"IStaticEcsSystemsFeature<{typeof(TWorld).Name}, *>", _systemsFeatureTypes);

            EditorGUILayout.EndScrollView();
        }

        private void DrawSection(string title, List<Type> types) {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            if (types.Count == 0) {
                EditorGUILayout.HelpBox("No types found.", MessageType.None);
                return;
            }

            foreach (var type in types) {
                if (!Match(type)) {
                    continue;
                }

                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox)) {
                    EditorGUILayout.LabelField(type.FullName);
                    EditorGUILayout.LabelField(type.Assembly.GetName().Name, EditorStyles.miniLabel,
                        GUILayout.Width(220));
                }
            }
        }

        private bool Match(Type type) {
            if (string.IsNullOrEmpty(_filter)) {
                return true;
            }

            return type.FullName != null && type.FullName.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RefreshCatalog() {
            _featureTypes.Clear();
            _systemsFeatureTypes.Clear();

            var featureContract = typeof(IStaticEcsFeature<TWorld>);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                Type[] types;
                try {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex) {
                    types = ex.Types;
                }

                foreach (var type in types) {
                    if (type == null || type.IsAbstract || type.IsInterface) {
                        continue;
                    }

                    if (!featureContract.IsAssignableFrom(type)) {
                        continue;
                    }

                    var isSystemsFeature = false;
                    foreach (var iface in type.GetInterfaces()) {
                        if (iface.IsGenericType
                            && iface.GetGenericTypeDefinition() == typeof(IStaticEcsSystemsFeature<,>)
                            && iface.GetGenericArguments()[0] == typeof(TWorld)) {
                            isSystemsFeature = true;
                            break;
                        }
                    }

                    if (isSystemsFeature) {
                        _systemsFeatureTypes.Add(type);
                    }
                    else {
                        _featureTypes.Add(type);
                    }
                }
            }

            _featureTypes.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
            _systemsFeatureTypes.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
        }
    }
}
