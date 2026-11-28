using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cysharp.Threading.Tasks;

namespace DX.Core.Extensions
{
    public static class AutoLockExtensions
    {
        private static ConditionalWeakTable<object, HashSet<string>> _table;
        private static ConditionalWeakTable<object, HashSet<string>> Table => _table ??= new();

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnEnterPlayMode()
        {
            // 兼容 Reload Domain (ScriptableObject 的情况)
            _table = null;
        }
        /// <summary>
        /// 为当前对象的当前方法上锁，通常用于 async 方法。（可手动传入自定义 key）
        /// 用法：using var scope = this.EnterLock(); if (scope.IsBusy) return;
        /// 当前方法执行完毕或 return 后自行解锁
        /// </summary>
        public static LockScope EnterLock<T>(this T owner, [CallerMemberName] string key = "") where T : class
        {
            var set = Table.GetOrCreateValue(owner);
            lock (set)
            {
                // 如果已经存在该 key，说明正在执行中，返回空 Scope
                if (!set.Add(key))
                {
                    return default;
                }
            }
            return new LockScope(set, key);
        }

        public readonly struct LockScope : IDisposable
        {
            private readonly HashSet<string> _set;
            private readonly string _key;

            internal LockScope(HashSet<string> set, string key)
            {
                _set = set;
                _key = key;
            }

            public bool IsBusy => _set == null;

            // 允许直接写成 if (!scope) return;
            public static implicit operator bool(LockScope scope) => scope._set != null;

            public void Dispose()
            {
                if (_set != null)
                {
                    lock (_set)
                    {
                        _set.Remove(_key);
                    }
                }
            }
        }

        public static async UniTaskVoid RunLockedVoid<T>(
            this T owner,
            Func<UniTask> asyncAction,
            [CallerMemberName] string key = "") where T : class
        {
            using var scope = owner.EnterLock(key);
            if (scope.IsBusy) return;

            if (asyncAction != null)
            {
                await asyncAction();
            }
        }

    }
}