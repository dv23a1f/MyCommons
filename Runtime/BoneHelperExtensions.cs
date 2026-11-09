using System;
using System.Collections.Generic;
using UnityEngine;

namespace DX.Core.Extensions.BoneHelper
{
    public static class TransformDeepSearchExtensions
    {
        #region 单个查找 (Single Find)

        /// <summary>
        /// 【广度优先-BFS】深度查找单个子节点（逐层查找）。
        /// 适合骨骼层级较浅，或优先匹配离根节点更近的节点（推荐默认使用）。
        /// </summary>
        public static Transform FindChildBFS(this Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            if (root.name == name) return root;

            Queue<Transform> queue = new Queue<Transform>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                Transform current = queue.Dequeue();
                if (current.name == name)
                    return current;

                for (int i = 0; i < current.childCount; i++)
                {
                    queue.Enqueue(current.GetChild(i));
                }
            }
            return null;
        }

        /// <summary>
        /// 【深度优先-DFS】深度查找单个子节点（非递归，栈实现）。
        /// 适合节点通常存在于某条枝干最深处的情况。
        /// </summary>
        public static Transform FindChildDFS(this Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;

            Stack<Transform> stack = new Stack<Transform>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                if (current.name == name)
                    return current;

                // 倒序入栈以保证从第一个子节点优先遍历
                for (int i = current.childCount - 1; i >= 0; i--)
                {
                    stack.Push(current.GetChild(i));
                }
            }
            return null;
        }

        #endregion

        #region 查找多个 (Find Multiple / Find All)

        /// <summary>
        /// 查找所有完全同名的子节点（每次调用会产生 GC Alloc）。
        /// </summary>
        public static List<Transform> FindChildrenByName(this Transform root, string name)
        {
            List<Transform> results = new List<Transform>();
            FindChildrenByName(root, name, results);
            return results;
        }

        /// <summary>
        /// 查找所有完全同名的子节点（【无GC版】传入外部List复用）。
        /// </summary>
        public static void FindChildrenByName(this Transform root, string name, List<Transform> results)
        {
            if (root == null || string.IsNullOrEmpty(name) || results == null) return;

            // 收集所有节点必须整棵树遍历，BFS/DFS都可以，这里使用Stack遍历
            Stack<Transform> stack = new Stack<Transform>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                if (current.name == name && current != root) // 通常排除root自身
                {
                    results.Add(current);
                }

                for (int i = current.childCount - 1; i >= 0; i--)
                {
                    stack.Push(current.GetChild(i));
                }
            }
        }

        /// <summary>
        /// 【支持模糊/条件匹配】根据自定义条件查找多个子节点（【无GC版】）。
        /// 比如查找名字包含某前缀、或者拥有某个Tag的骨骼。
        /// </summary>
        public static void FindChildrenWhere(this Transform root, Predicate<Transform> match, List<Transform> results)
        {
            if (root == null || match == null || results == null) return;

            Stack<Transform> stack = new Stack<Transform>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                if (current != root && match(current))
                {
                    results.Add(current);
                }

                for (int i = current.childCount - 1; i >= 0; i--)
                {
                    stack.Push(current.GetChild(i));
                }
            }
        }

        #endregion
    }
}