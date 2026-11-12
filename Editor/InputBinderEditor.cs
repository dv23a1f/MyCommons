#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using System.Linq;

namespace DX.Core.EditorScripts.Input
{
    [CustomEditor(typeof(InputBinder))]
    public class InputBinderEditor : Editor
    {
        private SerializedProperty _assetTemplateProp;
        private SerializedProperty _defaultActionMapProp;
        private SerializedProperty _setAsGlobalDefaultProp;

        private void OnEnable()
        {
            _assetTemplateProp = serializedObject.FindProperty("_assetTemplate");
            _defaultActionMapProp = serializedObject.FindProperty("_defaultActionMap");
            _setAsGlobalDefaultProp = serializedObject.FindProperty("_setAsGlobalDefault");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_setAsGlobalDefaultProp);
            EditorGUILayout.PropertyField(_assetTemplateProp);

            var asset = _assetTemplateProp.objectReferenceValue as InputActionAsset;

            if (asset != null)
            {
                // 1. 构建下拉选项列表，把 [All] 和 [None] 放在最前面
                List<string> options = new List<string>
                {
                    IInputService.MapAll,
                    IInputService.MapNone
                };

                // 2. 解析 Asset 资源中实际配置的所有 ActionMaps
                options.AddRange(asset.actionMaps.Select(m => m.name));

                // 3. 找到当前选中的索引
                string currentVal = _defaultActionMapProp.stringValue;
                int currentIndex = options.IndexOf(currentVal);
                if (currentIndex == -1) currentIndex = 0; // 默认选中 [All]

                // 4. 绘制下拉菜单
                int newIndex = EditorGUILayout.Popup("Default Action Map", currentIndex, options.ToArray());
                _defaultActionMapProp.stringValue = options[newIndex];
            }
            else
            {
                EditorGUILayout.HelpBox("请先指定 InputActionAsset 模板以选择 ActionMap", MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif