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
            float baseHeight = EditorGUI.GetPropertyHeight(property, label, true);

            // 如果类型不合法，或者当前依然为空（说明物体上确实没挂该组件），额外预留警告框高度
            if (NeedsHelpBox(property))
            {
                return baseHeight + HelpBoxHeight + Spacing;
            }
            return baseHeight;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // 1. 校验字段类型是否合法（必须是 Component 子类且是引用类型）
            if (property.propertyType != SerializedPropertyType.ObjectReference ||
                !typeof(Component).IsAssignableFrom(fieldInfo.FieldType))
            {
                EditorGUI.HelpBox(position, $"[AutoGetComponent] 只能用于继承自 Component 的序列化字段。", MessageType.Error);
                return;
            }

            // 2. 自动查找并赋值逻辑（仅在非运行模式、单选、且当前引用为空时触发）
            if (!Application.isPlaying &&
                !property.serializedObject.isEditingMultipleObjects &&
                property.objectReferenceValue == null)
            {
                if (property.serializedObject.targetObject is Component targetComponent)
                {
                    var attr = (AutoGetAttribute)attribute;
                    Component found = FindComponent(targetComponent, fieldInfo.FieldType, attr);

                    if (found != null)
                    {
                        property.objectReferenceValue = found;
                        // 应用修改，这会标记场景/Prefab为脏(Dirty)，支持 Ctrl+S 保存和 Ctrl+Z 撤销
                        property.serializedObject.ApplyModifiedProperties();
                    }
                }
            }

            // 3. 计算绘制区域
            bool showWarning = NeedsHelpBox(property);
            Rect fieldRect = position;
            fieldRect.height = EditorGUI.GetPropertyHeight(property, label, true);

            // 4. 绘制属性字段
            EditorGUI.BeginProperty(fieldRect, label, property);

            // 可选：如果你不希望策划/程序手动拖拽修改它，可以取消下面 DisabledScope 的注释使其变为只读
            // using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.PropertyField(fieldRect, property, label, true);
            }

            EditorGUI.EndProperty();

            // 5. 如果找不到组件，在下方给出红色错误提示，防呆设计
            if (showWarning)
            {
                Rect helpBoxRect = new Rect(
                    position.x,
                    fieldRect.yMax + Spacing,
                    position.width,
                    HelpBoxHeight
                );
                EditorGUI.HelpBox(helpBoxRect, $"未找到组件: {fieldInfo.FieldType.Name} ({((AutoGetAttribute)attribute).Scope})", MessageType.Warning);
            }
        }

        private bool NeedsHelpBox(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.ObjectReference ||
                !typeof(Component).IsAssignableFrom(fieldInfo.FieldType))
            {
                return false; // 错误信息直接替换绘制区域，不需要额外高度
            }

            return !property.serializedObject.isEditingMultipleObjects && property.objectReferenceValue == null;
        }

        // 抽离为 public static，方便后续供全局扫描脚本复用
        public static Component FindComponent(Component target, Type componentType, AutoGetAttribute attr)
        {
            return attr.Scope switch
            {
                GetComponentScope.Self => target.GetComponent(componentType),
                GetComponentScope.InChildren => target.GetComponentInChildren(componentType, attr.IncludeInactive),
                GetComponentScope.InParent => target.GetComponentInParent(componentType, attr.IncludeInactive),
                _ => null
            };
        }
    }
}