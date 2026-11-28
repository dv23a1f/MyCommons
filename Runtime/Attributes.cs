using System;
using UnityEngine;

namespace DX.Core.Attributes
{
    public enum ComponentScope
    {
        Self,
        InChildren,
        InParent
    }

    /// <summary>
    /// 在编辑器模式下自动获取挂载的组件，该字段必须是可序列化的（public 或标记了 SerializeField）
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class AutoGetAttribute : PropertyAttribute
    {
        public ComponentScope Scope { get; }
        public bool IncludeInactive { get; }
        public bool HideInInspector { get; }

        public AutoGetAttribute(
            ComponentScope scope = ComponentScope.Self,
            bool includeInactive = false,
            bool hideInInspector = false)
        {
            Scope = scope;
            IncludeInactive = includeInactive;
            HideInInspector = hideInInspector;
        }
    }
}