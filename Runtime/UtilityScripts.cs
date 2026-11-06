using System;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

#region InterfaceScanner

namespace Utility.InterfaceScanner
{
    public static class InterfaceScanner
    {
        public static List<T> FindAll<T>(bool includeInactive = false) where T : class
        {
            // 确保传入的是接口类型
            if (!typeof(T).IsInterface)
            {
                Debug.LogError($"{typeof(T).Name} 不是一个接口！");
                return null;
            }

            List<T> results = new List<T>();
            Scene activeScene = SceneManager.GetActiveScene();
            GameObject[] rootObjects = activeScene.GetRootGameObjects();

            foreach (GameObject root in rootObjects)
            {
                // GetComponentsInChildren 支持接口类型
                T[] components = root.GetComponentsInChildren<T>(includeInactive);
                results.AddRange(components);
            }

            return results;
        }
    }
    public class InterfaceFinder
    {
        private readonly List<Action<MonoBehaviour>> _collectors = new List<Action<MonoBehaviour>>();

        // 私有化构造函数，强制通过静态方法创建
        private InterfaceFinder() { }

        /// <summary>
        /// 入口：开始构建扫描器
        /// </summary>
        public static InterfaceFinder Begin()
        {
            return new InterfaceFinder();
        }

        /// <summary>
        /// 注册需要查找的目标接口和接收的 List
        /// </summary>
        public InterfaceFinder Find<T>(List<T> destinationList, bool clearListBeforeScan = true) where T : class
        {
            if (destinationList == null)
                throw new ArgumentNullException(nameof(destinationList));

            if (clearListBeforeScan)
            {
                destinationList.Clear();
            }

            // 注册一个收集器委托：如果 MonoBehaviour 实现了接口 T，则存入对应的 list
            _collectors.Add(mb =>
            {
                if (mb is T match)
                {
                    destinationList.Add(match);
                }
            });

            return this; // 返回自身以支持链式调用
        }

        /// <summary>
        /// 执行扫描：只遍历一次场景，完成所有列表的填充
        /// </summary>
        public void Execute(bool include = true)
        {
            if (_collectors.Count == 0) return;

            // 获取当前活动场景的所有根物体
            Scene activeScene = SceneManager.GetActiveScene();
            GameObject[] rootObjects = activeScene.GetRootGameObjects();

            // 缓存一个内部 List，复用内存以消除 GetComponentsInChildren 的 GC 垃圾
            List<MonoBehaviour> mbBuffer = new List<MonoBehaviour>(128);

            // --- 核心：单次场景遍历 ---
            foreach (GameObject root in rootObjects)
            {
                mbBuffer.Clear();
                // 无 GC 的重载方法
                root.GetComponentsInChildren(include, mbBuffer);

                int compCount = mbBuffer.Count;
                int collectorCount = _collectors.Count;

                for (int i = 0; i < compCount; i++)
                {
                    MonoBehaviour mb = mbBuffer[i];
                    // 防御：物体上可能存在丢失的脚本（Missing Script）
                    if (mb == null) continue;

                    // 对每一个扫描到的组件，检测是否符合已注册的各个目标接口
                    for (int j = 0; j < collectorCount; j++)
                    {
                        _collectors[j](mb);
                    }
                }
            }
        }
    }
}

#endregion

#region SmoothDamp

namespace Utility.SmoothDamp
{
    [Serializable]
    public class SmoothDampVector3
    {
        [Tooltip("平滑时间，值越小越快到达目标")]
        public float smoothTime = 0.2f;

        [Tooltip("最大移动速度")]
        public float maxSpeed = Mathf.Infinity;

        // 内部状态
        public Vector3 CurrentValue { get; set; }
        public Vector3 Velocity { get; private set; }

        /// <summary>
        /// 构造函数：初始化配置
        /// </summary>
        public SmoothDampVector3(float smoothTime = 0.2f, Vector3 initialValue = default, float maxSpeed = Mathf.Infinity)
        {
            this.smoothTime = smoothTime;
            this.CurrentValue = initialValue;
            this.maxSpeed = maxSpeed;
            this.Velocity = Vector3.zero;
        }

        /// <summary>
        /// 核心调用：只需输入目标值，返回计算后的平滑值
        /// </summary>
        public Vector3 Update(Vector3 target, float? customDeltaTime = null)
        {
            float dt = customDeltaTime ?? Time.deltaTime;

            Vector3 currentVelocity = Velocity;
            CurrentValue = Vector3.SmoothDamp(CurrentValue, target, ref currentVelocity, smoothTime, maxSpeed, dt);
            Velocity = currentVelocity;

            return CurrentValue;
        }

        /// <summary>
        /// 重置/瞬移到某个位置（清除当前速度，避免回弹）
        /// </summary>
        public void Reset(Vector3 value)
        {
            CurrentValue = value;
            Velocity = Vector3.zero;
        }
    }
}
#endregion