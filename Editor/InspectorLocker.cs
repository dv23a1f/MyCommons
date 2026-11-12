using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DX.Core.EditorScripts
{
    internal static class InspectorLocker
    {
        private static PropertyInfo _isLockedProperty;
        private static Type _inspectorType;

        [MenuItem("Edit/Toggle Lock &q")]
        private static void Toggle()
        {
            // 1. 初始化反射信息（仅在初次使用时执行一次，保证性能）
            if (_inspectorType == null)
            {
                _inspectorType = typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");
                if (_inspectorType == null) return;
            }

            if (_isLockedProperty == null)
            {
                _isLockedProperty = _inspectorType.GetProperty("isLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_isLockedProperty == null) return;
            }

            // 2. 智能获取目标 Inspector 窗口
            EditorWindow targetInspector = null;

            // 优先情况 A：鼠标正停留在某个 Inspector 上
            if (EditorWindow.mouseOverWindow != null && EditorWindow.mouseOverWindow.GetType() == _inspectorType)
            {
                targetInspector = EditorWindow.mouseOverWindow;
            }
            // 优先情况 B：当前激活（获取焦点）的窗口就是 Inspector
            else if (EditorWindow.focusedWindow != null && EditorWindow.focusedWindow.GetType() == _inspectorType)
            {
                targetInspector = EditorWindow.focusedWindow;
            }
            // 兜底情况 C（你的场景：焦点在 Hierarchy 或 Project）：动态寻找当前正在显示的 Inspector
            else
            {
                var allInspectors = Resources.FindObjectsOfTypeAll(_inspectorType);
                if (allInspectors != null && allInspectors.Length > 0)
                {
                    // 找到第一个未被销毁的有效 Inspector
                    foreach (var obj in allInspectors)
                    {
                        if (obj is EditorWindow window && window != null)
                        {
                            targetInspector = window;
                            break;
                        }
                    }
                }
            }

            // 3. 执行锁定/解锁
            if (targetInspector != null)
            {
                bool currentState = (bool)_isLockedProperty.GetValue(targetInspector, null);
                _isLockedProperty.SetValue(targetInspector, !currentState, null);
                targetInspector.Repaint(); // 刷新界面以立即看到小锁变化
            }
        }
    }
}