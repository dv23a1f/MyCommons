using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DX.Core.EditorScripts
{
    internal static class InspectorLocker
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type _inspectorType;
        private static PropertyInfo _isLockedProperty;
        private static FieldInfo _parentField;          // EditorWindow.m_Parent
        private static PropertyInfo _actualViewProperty; // HostView.actualView

        [MenuItem("Edit/Toggle Lock &q")]
        private static void Toggle()
        {
            if (!InitReflection()) return;

            EditorWindow target = FindTarget();
            if (target == null) return;

            bool locked = (bool)_isLockedProperty.GetValue(target, null);
            _isLockedProperty.SetValue(target, !locked, null);
            target.Repaint();
        }

        private static bool InitReflection()
        {
            _inspectorType ??= typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");
            if (_inspectorType == null) return false;

            _isLockedProperty ??= _inspectorType.GetProperty("isLocked", Flags);
            _parentField ??= typeof(EditorWindow).GetField("m_Parent", Flags);
            return _isLockedProperty != null;
        }

        private static EditorWindow FindTarget()
        {
            // A：鼠标悬停
            var over = EditorWindow.mouseOverWindow;
            if (over != null && over.GetType() == _inspectorType) return over;

            // B：焦点窗口
            var focused = EditorWindow.focusedWindow;
            if (focused != null && focused.GetType() == _inspectorType) return focused;

            // C：在所有 Inspector 中选“当前可见”的标签页
            EditorWindow firstVisible = null;
            EditorWindow fallback = null;

            foreach (var obj in Resources.FindObjectsOfTypeAll(_inspectorType))
            {
                if (!(obj is EditorWindow w) || w == null) continue;

                fallback ??= w;
                if (!IsVisibleTab(w)) continue;

                // 多个都可见时，优先选“未锁定”的（即跟随选择的那个）
                bool locked = (bool)_isLockedProperty.GetValue(w, null);
                if (!locked) return w;

                firstVisible ??= w;
            }

            return firstVisible ?? fallback;
        }

        private static bool IsVisibleTab(EditorWindow w)
        {
            if (_parentField == null) return true;

            var parent = _parentField.GetValue(w);
            if (parent == null) return false; // 没有停靠父级，通常是已关闭/游离的

            _actualViewProperty ??= parent.GetType().GetProperty("actualView", Flags);
            if (_actualViewProperty == null) return true;

            return ReferenceEquals(_actualViewProperty.GetValue(parent, null), w);
        }
    }
}