using System;
using System.Threading;
using UnityEngine;

namespace DX.Core.Services
{
    public static class ServiceLocator
    {
        private static class ServiceCache<T> where T : class
        {
            public static T Instance;
            public static CancellationTokenRegistration? TokenRegistration;
            public static Action<T> OnReadyCallbacks;
        }

        /// <summary>
        /// 注册服务（MonoBehaviour 自动绑定销毁生命周期，纯C#类常驻）
        /// </summary>
        public static void Register<T>(T service, CancellationToken cancellationToken = default) where T : class
        {
            if (service == null)
            {
                Debug.LogError($"[Service] 注册失败，实例为空: {typeof(T).Name}");
                return;
            }

            if (TryGet<T>(out var existingService))
            {
                // 1. 同一实例重复注册：直接拦截并提示
                if (ReferenceEquals(existingService, service))
                {
                    Debug.LogWarning($"[Service] 重复注册相同实例，已忽略: {typeof(T).Name}");
                    return;
                }

                // 2. 不同实例覆盖注册：发出警告
                Debug.LogWarning($"[Service] 检测到已存在同类型服务: {typeof(T).Name}，原实例将被覆盖！");
            }

            // 1. 清理该类型先前的 Token 绑定
            ServiceCache<T>.TokenRegistration?.Dispose();

            // 2. 自动检测 MonoBehaviour 的销毁生命周期
            if (cancellationToken == default && service is MonoBehaviour mb)
            {
                cancellationToken = mb.destroyCancellationToken;
            }

            // 3. 绑定自动注销
            if (cancellationToken.CanBeCanceled)
            {
                T targetInstance = service;
                ServiceCache<T>.TokenRegistration = cancellationToken.Register(() =>
                {
                    // 仅当缓存的还是当前这个实例时才清理（防止被新注册覆盖后误删）
                    if (ReferenceEquals(ServiceCache<T>.Instance, targetInstance))
                    {
                        Unregister<T>();
                    }
                });
            }
            else
            {
                ServiceCache<T>.TokenRegistration = null;
            }

            ServiceCache<T>.Instance = service;

            if (ServiceCache<T>.OnReadyCallbacks != null)
            {
                var callbacks = ServiceCache<T>.OnReadyCallbacks;
                ServiceCache<T>.OnReadyCallbacks = null;
                callbacks.Invoke(service);
            }
        }

        /// <summary>
        /// 获取必须存在的强依赖服务（直接内联返回，零开销）
        /// </summary>
        public static T Get<T>() where T : class
        {
            return ServiceCache<T>.Instance;
        }

        /// <summary>
        /// 尝试获取可选服务（兼顾纯C#对象与 Unity 伪 Null 检测）
        /// </summary>
        public static bool TryGet<T>(out T service) where T : class
        {
            service = ServiceCache<T>.Instance;

            if (service == null)
            {
                return false;
            }

            if (service is UnityEngine.Object unityObj && unityObj == null)
            {
                Unregister<T>();
                service = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 手动注销服务
        /// </summary>
        public static void Unregister<T>() where T : class
        {
            if (ServiceCache<T>.Instance != null)
            {
                ServiceCache<T>.TokenRegistration?.Dispose();
                ServiceCache<T>.TokenRegistration = null;
                ServiceCache<T>.Instance = null;
                ServiceCache<T>.OnReadyCallbacks = null;
            }
        }
    }
}