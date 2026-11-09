using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DX.Core.Addressable
{
    public static class AssetLoader
    {
        // 缓存所有加载过的句柄
        private static readonly Dictionary<string, AsyncOperationHandle> _handles = new();

        /// <summary>
        /// 加载单一资产 (支持 UniTask 和 取消机制)
        /// </summary>
        public static async UniTask<T> LoadAsset<T>(string address, CancellationToken ct = default) where T : Object
        {
            // 1. 如果已经加载过，直接返回缓存
            if (_handles.TryGetValue(address, out var existingHandle))
            {
                if (existingHandle.IsDone)
                {
                    return existingHandle.Result as T;
                }
                // 如果还在加载中，等待它完成
                await existingHandle.ToUniTask(cancellationToken: ct);
                return existingHandle.Result as T;
            }

            // 2. 开启新加载
            var handle = Addressables.LoadAssetAsync<T>(address);
            _handles.Add(address, handle);

            try
            {
                // 使用 UniTask 转换，支持绑定 GameObject 的生命周期 (GetCancellationTokenOnDestroy)
                await handle.ToUniTask(cancellationToken: ct);

                if (handle.Status == AsyncOperationStatus.Succeeded)
                {
                    return handle.Result;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            Debug.LogError($"[AssetLoader] 加载资产失败: {address}");
            Unload(address);
            return null;
        }

        // =========================================================================
        // 加载并实例化
        // =========================================================================

        /// <summary>
        /// 实例化 GameObject，并捕获销毁时机自动释放 Handle。
        /// 备注：当场上所有实例都被销毁时会释放prefab，不受Unload影响
        /// </summary>
        public static async UniTask<GameObject> InstantiateAsync(
            string address,
            Transform parent = null,
            bool inWorldSpace = false,
            CancellationToken ct = default)
        {
            var handle = Addressables.InstantiateAsync(address, parent, inWorldSpace);

            try
            {
                // 等待完成
                await handle.ToUniTask(cancellationToken: ct);

                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    if (handle.IsValid()) Addressables.Release(handle);
                    Debug.LogError($"[AssetLoader] 实例化资产失败: {address}");
                    return null;
                }

                var instance = handle.Result;

                // 捕获实例销毁时机，自动释放该 handle
                // 只有全部实例被销毁，底层 Prefab 计数归零才会被完全卸载
                instance.GetCancellationTokenOnDestroy().Register(() =>
                {
                    if (handle.IsValid())
                    {
                        Addressables.Release(handle);
                    }
                });

                return instance;
            }
            catch (OperationCanceledException)
            {
                if (handle.IsValid()) Addressables.Release(handle);
                throw;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                if (handle.IsValid()) Addressables.Release(handle);
                return null;
            }
        }

        /// <summary>
        /// 泛型重载：实例化后直接返回 GameObject 或指定挂载的 Component
        /// </summary>
        public static async UniTask<T> InstantiateAsync<T>(
            string address,
            Transform parent = null,
            bool inWorldSpace = false,
            CancellationToken ct = default) where T : Object
        {
            var instance = await InstantiateAsync(address, parent, inWorldSpace, ct);
            if (instance == null) return null;

            if (typeof(T) == typeof(GameObject))
            {
                return instance as T;
            }

            if (typeof(Component).IsAssignableFrom(typeof(T)))
            {
                if (instance.TryGetComponent(typeof(T), out var comp))
                {
                    return comp as T;
                }

                Debug.LogError($"[AssetLoader] 实例 '{instance.name}' 上未找到组件: {typeof(T).Name}");
                Object.Destroy(instance); // 触发 Destroy 会调用上面的 Register 自动释放
                return null;
            }

            Debug.LogError($"[AssetLoader] 不支持的实例化类型: {typeof(T).Name}");
            Object.Destroy(instance);
            return null;
        }

        // =========================================================================
        // 加载 Prefab 链式调用
        // =========================================================================

        /// <summary>
        /// 链式调用入口：AssetLoader.Load<T>("Key").OnSuccess(...).Run();
        /// </summary>
        public static AssetLoadChain<T> Load<T>(string address) where T : Object
        {
            return new AssetLoadChain<T>(address);
        }

        /// <summary>
        /// 链式调用的包装器
        /// </summary>
        public class AssetLoadChain<T> where T : Object
        {
            private readonly string _address;
            private Action<T> _onSuccess;
            private Action _onFail;
            private Action _onCancel;

            public AssetLoadChain(string address)
            {
                _address = address;
            }

            public AssetLoadChain<T> OnSuccess(Action<T> callback)
            {
                _onSuccess = callback;
                return this;
            }

            public AssetLoadChain<T> OnFail(Action callback)
            {
                _onFail = callback;
                return this;
            }

            public AssetLoadChain<T> OnCancel(Action callback)
            {
                _onCancel = callback;
                return this;
            }
            public void Run()
            {
                ExecuteInternalAsync(CancellationToken.None).Forget();
            }

            public UniTask Run(CancellationToken ct)
            {
                return ExecuteInternalAsync(ct);
            }

            /// <summary>
            /// 核心执行逻辑：加载资源并触发回调
            /// </summary>
            private async UniTask ExecuteInternalAsync(CancellationToken ct)
            {
                try
                {
                    var asset = await AssetLoader.LoadAsset<T>(_address, ct);
                    if (asset != null)
                    {
                        _onSuccess?.Invoke(asset);
                    }
                    else
                    {
                        _onFail?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    _onCancel?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    _onFail?.Invoke();
                }
                finally
                {

                    _onSuccess = null;
                    _onFail = null;
                    _onCancel = null;
                }
            }
        }

        // =========================================================================
        // 实例化链式包装入口
        // =========================================================================

        /// <summary>
        /// 链式实例化 GameObject：AssetLoader.Instantiate("Key").Under(parent).OnSuccess(...).Run();
        /// </summary>
        public static AssetInstantiateChain<GameObject> Instantiate(string address)
        {
            return new AssetInstantiateChain<GameObject>(address);
        }

        /// <summary>
        /// 泛型链式实例化（直接获取组件）：AssetLoader.Instantiate<Bullet>("Key").Under(parent).OnSuccess(...).Run();
        /// </summary>
        public static AssetInstantiateChain<T> Instantiate<T>(string address) where T : Component
        {
            return new AssetInstantiateChain<T>(address);
        }

        /// <summary>
        /// 实例化的链式包装器（零重复代码，纯作为调用壳）
        /// </summary>
        public class AssetInstantiateChain<T> where T : Object
        {
            private readonly string _address;
            private Transform _parent;
            private bool _instantiateInWorldSpace;
            private Action<T> _onSuccess;
            private Action _onFail;
            private Action _onCancel;

            public AssetInstantiateChain(string address)
            {
                _address = address;
            }

            public AssetInstantiateChain<T> Under(Transform parent, bool worldPositionStays = false)
            {
                _parent = parent;
                _instantiateInWorldSpace = worldPositionStays;
                return this;
            }

            public AssetInstantiateChain<T> OnSuccess(Action<T> callback)
            {
                _onSuccess = callback;
                return this;
            }

            public AssetInstantiateChain<T> OnFail(Action callback)
            {
                _onFail = callback;
                return this;
            }
            public AssetInstantiateChain<T> OnCancel(Action callback)
            {
                _onCancel = callback;
                return this;
            }
            /// <summary>
            /// 【同步调用入口】无需 await，普通方法中一行调用
            /// </summary>
            public void Run()
            {
                ExecuteAsync(CancellationToken.None).Forget();
            }

            public UniTask Run(CancellationToken ct)
            {
                return ExecuteAsync(ct);
            }

            private async UniTask ExecuteAsync(CancellationToken ct)
            {
                try
                {
                    var result = await AssetLoader.InstantiateAsync<T>(_address, _parent, _instantiateInWorldSpace, ct);
                    if (result != null)
                    {
                        _onSuccess?.Invoke(result);
                    }
                    else
                    {
                        _onFail?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    _onCancel?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    _onFail?.Invoke();
                }
                finally
                {
                    _onSuccess = null;
                    _onFail = null;
                    _onCancel = null;
                }
            }
        }

        //!===================================================

        /// <summary>
        /// 批量加载/预热资产
        /// </summary>
        public static async UniTask PreloadAssets<T>(IEnumerable<string> addresses, IProgress<float> progress = null, CancellationToken ct = default) where T : Object
        {
            List<UniTask> tasks = new();

            foreach (var addr in addresses)
            {
                tasks.Add(LoadAsset<T>(addr, ct));
            }

            // 并行加载全部
            await UniTask.WhenAll(tasks);
        }

        /// <summary>
        /// 根据 Label 批量加载某一类的所有资产
        /// </summary>
        public static async UniTask<IList<T>> LoadAssetsByLabel<T>(string label, CancellationToken ct = default) where T : Object
        {
            // Addressables 原生支持传入 Label 进行批量加载
            var handle = Addressables.LoadAssetsAsync<T>(label, null);

            await handle.ToUniTask(cancellationToken: ct);

            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                return handle.Result;
            }

            Debug.LogError($"[AssetLoader] 按标签加载失败: {label}");
            return null;
        }

        /// <summary>
        /// 检查某资产是否已经加载到内存中
        /// </summary>
        public static bool IsLoaded(string address)
        {
            return _handles.TryGetValue(address, out var handle) && handle.IsDone && handle.Status == AsyncOperationStatus.Succeeded;
        }

        /// <summary>
        /// 单个资产卸载（通过别名精确卸载）
        /// 仅卸载通过 LoadAsset 加载的资产（缓存），不影响 InstantiateAsync
        /// </summary>
        public static void Unload(string address)
        {
            if (_handles.TryGetValue(address, out var handle))
            {
                Addressables.Release(handle);
                _handles.Remove(address);
            }
        }

        /// <summary>
        /// 清理所有缓存的资源（一般在切换大关卡、返回主菜单时调用）
        /// </summary>
        public static void ClearAll()
        {
            foreach (var handle in _handles.Values)
            {
                Addressables.Release(handle);
            }
            _handles.Clear();
        }
    }
}