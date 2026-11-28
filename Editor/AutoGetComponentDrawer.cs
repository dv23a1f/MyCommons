// Editor/AutoGetComponentDrawer.cs (必须放在 Editor 文件夹下)
using System;
using DX.Core.Attributes;
using UnityEditor;
using UnityEngine;

namespace DX.Core.Attributes.Editor
{
    [CustomPropertyDrawer(typeof(AutoGetAttribute))]
    public class AutoGetComponentDrawer : PropertyDrawer
    {
        private const float HelpBoxHeight = 24f;
        private const float Spacing = 2f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!IsValidProperty(property))
            {
                return HelpBoxHeight;
            }

            var attr = (AutoGetAttribute)attribute;
            bool missing = NeedsHelpBox(property);

            if (attr.HideInInspector)
            {
                // 成功绑定：返回负的标准垂直间距，完全消除 Inspector 占位空隙
                // 未找到组件：仅保留警告框高度
                return missing ? HelpBoxHeight : -EditorGUIUtility.standardVerticalSpacing;
            }

            float baseHeight = EditorGUI.GetPropertyHeight(property, label, true);
            return missing ? baseHeight + HelpBoxHeight + Spacing : baseHeight;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // 1. 校验字段类型是否合法
            if (!IsValidProperty(property))
            {
                EditorGUI.HelpBox(position, "[AutoGet] 只能用于继承自 Component 的序列化字段。", MessageType.Error);
                return;
            }

            var attr = (AutoGetAttribute)attribute;

            // 2. 自动查找并赋值逻辑（仅在非运行模式、单选、且当前引用为空时触发）
            if (!Application.isPlaying &&
                !property.serializedObject.isEditingMultipleObjects &&
                property.objectReferenceValue == null)
            {
                if (property.serializedObject.targetObject is Component targetComponent)
                {
                    Component found = FindComponent(targetComponent, fieldInfo.FieldType, attr);
                    if (found != null)
                    {
                        property.objectReferenceValue = found;
                        property.serializedObject.ApplyModifiedProperties();
                    }
                }
            }

            bool showWarning = NeedsHelpBox(property);

            // 3. 隐藏模式：已找到组件则直接跳过绘制；未找到则仅显示警告提示
            if (attr.HideInInspector)
            {
                if (showWarning)
                {
                    Rect warnRect = new Rect(position.x, position.y, position.width, HelpBoxHeight);
                    EditorGUI.HelpBox(
                        warnRect,
                        $"[AutoGet] {label.text} 未找到组件: {fieldInfo.FieldType.Name} ({attr.Scope})",
                        MessageType.Warning
                    );
                }
                return;
            }

            // 4. 正常绘制属性字段
            Rect fieldRect = position;
            fieldRect.height = EditorGUI.GetPropertyHeight(property, label, true);

            EditorGUI.BeginProperty(fieldRect, label, property);
            // using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.PropertyField(fieldRect, property, label, true);
            }
            EditorGUI.EndProperty();

            // 5. 找不到组件时的警告提示
            if (showWarning)
            {
                Rect helpBoxRect = new Rect(
                    position.x,
                    fieldRect.yMax + Spacing,
                    position.width,
                    HelpBoxHeight
                );
                EditorGUI.HelpBox(helpBoxRect, $"未找到组件: {fieldInfo.FieldType.Name} ({attr.Scope})", MessageType.Warning);
            }
        }

        private bool IsValidProperty(SerializedProperty property)
        {
            return property.propertyType == SerializedPropertyType.ObjectReference &&
                   typeof(Component).IsAssignableFrom(fieldInfo.FieldType);
        }

        private bool NeedsHelpBox(SerializedProperty property)
        {
            if (!IsValidProperty(property))
                return false;

            return !property.serializedObject.isEditingMultipleObjects && property.objectReferenceValue == null;
        }

        public static Component FindComponent(Component target, Type componentType, AutoGetAttribute attr)
        {
            return attr.Scope switch
            {
                ComponentScope.Self => target.GetComponent(componentType),
                ComponentScope.InChildren => target.GetComponentInChildren(componentType, attr.IncludeInactive),
                ComponentScope.InParent => target.GetComponentInParent(componentType, attr.IncludeInactive),
                _ => null
            };
        }
    }
}