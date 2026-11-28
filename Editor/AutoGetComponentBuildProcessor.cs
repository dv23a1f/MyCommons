// Editor/AutoGetComponentBuildProcessor.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using DX.Core.Attributes;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace DX.Core.Attributes.Editor
{
    public class AutoGetComponentBuildProcessor : IPreprocessBuildWithReport, IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        // 缓存：记录哪些类定义了 [AutoGetComponent] 字段，每次脚本编译后自动重置
        private static Dictionary<Type, List<(FieldInfo field, AutoGetAttribute attr)>> _cachedFieldsMap;

        private static Dictionary<Type, List<(FieldInfo field, AutoGetAttribute attr)>> GetFieldsMap()
        {
            if (_cachedFieldsMap != null) return _cachedFieldsMap;

            _cachedFieldsMap = new Dictionary<Type, List<(FieldInfo, AutoGetAttribute)>>();

            // 核心优化：使用 Unity 原生索引 TypeCache，耗时 < 0.1ms，无 GC
            foreach (FieldInfo field in TypeCache.GetFieldsWithAttribute<AutoGetAttribute>())
            {
                bool isSerialized = (field.IsPublic || field.IsDefined(typeof(SerializeField)))
                    && !field.IsDefined(typeof(NonSerializedAttribute));

                if (field.IsStatic || !isSerialized || !typeof(Component).IsAssignableFrom(field.FieldType))
                    continue;

                var attr = field.GetCustomAttribute<AutoGetAttribute>();
                var declaringType = field.DeclaringType;
                if (declaringType == null) continue;

                if (!_cachedFieldsMap.TryGetValue(declaringType, out var list))
                {
                    list = new List<(FieldInfo, AutoGetAttribute)>();
                    _cachedFieldsMap[declaringType] = list;
                }
                list.Add((field, attr));
            }

            return _cachedFieldsMap;
        }

        public void OnPreprocessBuild(BuildReport report) => FixAllPrefabs();

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // 如果全工程没有任何脚本用到该特性，0 耗时直接退出
            if (GetFieldsMap().Count == 0) return;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                FixGameObjectHierarchy(root, isAssetOnDisk: false);
            }
        }

        [MenuItem("Tools/FixAllPrefabs [AutoGetComponent]")]
        public static void FixAllPrefabs()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            int fixedCount = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                int count = FixGameObjectHierarchy(prefab, isAssetOnDisk: true);
                if (count > 0)
                {
                    EditorUtility.SetDirty(prefab);
                    fixedCount += count;
                }
            }

            if (fixedCount > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"[AutoGetComponent] 自动修复了 {fixedCount} 个 Prefab 字段引用。");
            }
        }

        private static int FixGameObjectHierarchy(GameObject root, bool isAssetOnDisk)
        {
            var map = GetFieldsMap();
            if (map.Count == 0) return 0;

            int fixedCount = 0;

            foreach (var comp in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp == null) continue;

                // 沿继承链向上查找是否有缓存的字段（支持父类定义的特性），无任何反射开销
                Type currentType = comp.GetType();
                SerializedObject so = null;

                while (currentType != null && currentType != typeof(MonoBehaviour))
                {
                    if (map.TryGetValue(currentType, out var fieldList))
                    {
                        foreach (var (field, attr) in fieldList)
                        {
                            if ((field.GetValue(comp) as UnityEngine.Object) == null)
                            {
                                Component found = AutoGetComponentDrawer.FindComponent(comp, field.FieldType, attr);
                                if (found != null)
                                {
                                    so ??= new SerializedObject(comp);
                                    var prop = so.FindProperty(field.Name);
                                    if (prop != null)
                                    {
                                        prop.objectReferenceValue = found;
                                        fixedCount++;
                                    }
                                }
                            }
                        }
                    }
                    currentType = currentType.BaseType;
                }

                if (so != null)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    if (!isAssetOnDisk) EditorUtility.SetDirty(comp);
                }
            }

            return fixedCount;
        }
    }
}