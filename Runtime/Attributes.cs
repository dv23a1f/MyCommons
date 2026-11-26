using System;
using UnityEngine;

namespace DX.Core.Attributes
{
    public enum GetComponentScope
    {
        Self,
        InChildren,
        InParent
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class AutoGetComponentAttribute : PropertyAttribute
    {
        public GetComponentScope Scope { get; }
        public bool IncludeInactive { get; }

        public AutoGetComponentAttribute(GetComponentScope scope = GetComponentScope.Self, bool includeInactive = false)
        {
            Scope = scope;
            IncludeInactive = includeInactive;
        }
    }
}