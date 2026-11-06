using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace DX.Core.Message
{
    public static class EventBus
    {
        // 全局日志开关
        public static bool ShowLog = false;

        private static readonly HashSet<Action> _cleanupActions = new HashSet<Action>();
        private static readonly object _cleanupLock = new object();

        // 内部注册清理回调
        private static void RegisterCleanup(Action cleanupAction)
        {
            lock (_cleanupLock)
            {
                _cleanupActions.Add(cleanupAction);
            }
        }

        /// <summary>
        /// 支持 Unity 域重载 (Domain Reload) 关闭时的静态资源重置
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ClearAll()
        {
            lock (_cleanupLock)
            {
                foreach (var action in _cleanupActions)
                {
                    action?.Invoke();
                }
                _cleanupActions.Clear();
            }
        }

        /// <summary>
        /// 订阅事件，返回 IDisposable 用于取消订阅
        /// </summary>
        public static IDisposable Subscribe<T>(Action<T> listener)
        {
            return Cache<T>.Subscribe(listener);
        }

        /// <summary>
        /// 订阅事件，绑定 CancellationToken
        /// </summary>
        public static void Subscribe<T>(Action<T> listener, CancellationToken cancellationToken)
        {
            Cache<T>.Subscribe(listener, cancellationToken);
        }

        /// <summary>
        /// 取消订阅
        /// </summary>
        public static void Unsubscribe<T>(Action<T> listener)
        {
            Cache<T>.Unsubscribe(listener);
        }

        /// <summary>
        /// 广播事件 (支持类型推导)
        /// </summary>
        public static void Publish<T>(T eventData)
        {
            Cache<T>.Publish(eventData);
        }

        private static class Cache<T>
        {
            private static Action<T> _onEvent;
            private static readonly Dictionary<Action<T>, CancellationTokenRegistration> _tokenRegistrations = new();
            private static readonly object _lock = new object();

            static Cache()
            {
                // 每一个类型首次使用时，自动向外部注册清理逻辑
                EventBus.RegisterCleanup(UnregisterAll);
            }

            public static void UnregisterAll()
            {
                lock (_lock)
                {
                    _onEvent = null;

                    foreach (var registration in _tokenRegistrations.Values)
                    {
                        registration.Dispose();
                    }
                    _tokenRegistrations.Clear();
                }
            }

            public static IDisposable Subscribe(Action<T> listener)
            {
                lock (_lock)
                {
                    if (_onEvent != null && Array.IndexOf(_onEvent.GetInvocationList(), listener) != -1)
                    {
                        Debug.LogWarning($"<b><color=red>[Event]</color></b> 重复订阅事件参数类型: <b><color=white>{typeof(T).Name}</color></b>");
                        return new EventBusDisposable(() => { });
                    }

                    _onEvent += listener;
                    if (ShowLog) Debug.Log($"<b><color=green>[Event]</color></b> 新增订阅: <b><color=white>{typeof(T).Name}</color></b>");

                    return new EventBusDisposable(() => Unsubscribe(listener));
                }
            }

            public static void Subscribe(Action<T> listener, CancellationToken cancellationToken)
            {
                if (!cancellationToken.CanBeCanceled)
                {
                    throw new ArgumentException($"{listener.Method.Name} 传入的 CancellationToken 无效");
                }

                lock (_lock)
                {
                    if (_onEvent != null && Array.IndexOf(_onEvent.GetInvocationList(), listener) != -1)
                    {
                        return;
                    }

                    _onEvent += listener;
                    if (ShowLog) Debug.Log($"<b><color=green>[Event]</color></b> 新增Token订阅: <b><color=white>{typeof(T).Name}</color></b>");

                    var registration = cancellationToken.Register(() =>
                    {
                        Unsubscribe(listener);
                    });

                    _tokenRegistrations[listener] = registration;
                }
            }

            public static void Unsubscribe(Action<T> listener)
            {
                lock (_lock)
                {
                    _onEvent -= listener;

                    if (ShowLog) Debug.Log($"<b><color=cyan>[Event]</color></b> 取消订阅成功: <b><color=grey>{typeof(T).Name}</color></b>");

                    if (_tokenRegistrations.TryGetValue(listener, out var registration))
                    {
                        registration.Dispose();
                        _tokenRegistrations.Remove(listener);
                    }
                }
            }

            public static void Publish(T eventData)
            {
                Action<T> actionToInvoke;

                lock (_lock)
                {
                    if (_onEvent == null)
                    {
                        if (ShowLog) Debug.Log($"<b><color=yellow>[Event]</color></b> 广播无响应: <b><color=yellow>{typeof(T).Name}</color></b>, 暂无订阅者");
                        return;
                    }

                    actionToInvoke = _onEvent;
                }

                actionToInvoke.Invoke(eventData);
            }
        }

    }

    public class EventBusDisposable : IDisposable
    {
        private Action _onDispose;

        public EventBusDisposable(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }

}