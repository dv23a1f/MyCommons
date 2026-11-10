using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using Object = UnityEngine.Object;

namespace DX.Core.Addressable
{
    public static class AssetLoader
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void AutoReset()
        {
            _isCatalogInitialized = false;
        }

        // 用于Exists查询之前先进行初始化的标记
        private static bool _isCatalogInitialized = false;

        // 缓存所有加载过的句柄
        private static readonly Dictionary<string, AsyncOperationHandle> _handles = new();

        /// <summary>
        /// 加载 GameObject 类型的资产
        /// </summary>
        public static UniTask<GameObject> LoadAsync(string address, CancellationToken ct = default)
        {
            return LoadAsync<GameObject>(address, ct);
        }

        /// <summary>
        /// 加载单一资产，指定具体类型 (支持 UniTask 和 取消机制)
        /// </summary>
        public static async UniTask<T> LoadAsync<T>(string address, CancellationToken ct = default) where T : Object
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
        // 同步加载方法(冻结主线程)
        // =========================================================================
        /// <summary>
        /// 立刻加载 GameObject 类型的资源并在完成之前冻结主线程
        /// </summary>
        public static GameObject LoadImmediate(string address) => LoadImmediate<GameObject>(address);

        /// <summary>
        /// 立刻加载指定类型的资源并在完成之前冻结主线程
        /// </summary>
        public static T LoadImmediate<T>(string address) where T : Object
        {
            if (_handles.TryGetValue(address, out var existingHandle))
            {
                if (!existingHandle.IsDone) existingHandle.WaitForCompletion();
                return existingHandle.Result as T;
            }

            var handle = Addressables.LoadAssetAsync<T>(address);
            _handles.Add(address, handle);

            try
            {
                handle.WaitForCompletion();
                if (handle.Status == AsyncOperationStatus.Succeeded)
                    return handle.Result;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            Debug.LogError($"[AssetLoader] 立即加载资产失败: {address}");
            Unload(address);
            return null;
        }

        /// <summary>
        /// 立刻实例化 GameObject 类型的资源并在完成之前冻结主线程
        /// </summary>
        public static GameObject InstantiateImmediate(string address, Transform parent = null, bool inWorldSpace = false)
        {
            var handle = Addressables.InstantiateAsync(address, parent, inWorldSpace);
            try
            {
                handle.WaitForCompletion();
                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    if (handle.IsValid()) Addressables.Release(handle);
                    Debug.LogError($"[AssetLoader] 立即实例化失败: {address}");
                    return null;
                }

                var instance = handle.Result;
                instance.GetCancellationTokenOnDestroy().Register(() =>
                {
                    if (handle.IsValid()) Addressables.Release(handle);
                });

                return instance;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                if (handle.IsValid()) Addressables.Release(handle);
                return null;
            }
        }
        /// <summary>
        /// 立刻实例化指定类型的资源并在完成之前冻结主线程
        /// </summary>
        public static T InstantiateImmediate<T>(string address, Transform parent = null, bool inWorldSpace = false) where T : Object
        {
            var instance = InstantiateImmediate(address, parent, inWorldSpace);
            if (instance == null) return null;

            if (typeof(T) == typeof(GameObject)) return instance as T;

            if (typeof(Component).IsAssignableFrom(typeof(T)))
            {
                if (instance.TryGetComponent(typeof(T), out var comp)) return comp as T;
                Debug.LogError($"[AssetLoader] 未在实例上找到组件: {typeof(T).Name}");
                Object.Destroy(instance);
                return null;
            }

            Object.Destroy(instance);
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
        /// GameObject 链式调用入口
        /// </summary>
        public static AssetLoadChain<GameObject> Load(string address)
        {
            return new AssetLoadChain<GameObject>(address);
        }

        /// <summary>
        /// 泛型链式调用入口：AssetLoader.Load<T>("Key").OnSuccess(...).Run();
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
                    var asset = await AssetLoader.LoadAsync<T>(_address, ct);
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
                tasks.Add(LoadAsync<T>(addr, ct));
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
        /// 清理所有通过 LoadAsset 加载的资源
        /// </summary>
        public static void UnloadAll()
        {
            foreach (var handle in _handles.Values)
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
            }
            _handles.Clear();
        }

        // =========================================================================
        // 资产存在性检测 (纯同步)
        // =========================================================================

        /// <summary>
        /// 确保寻址目录已加载进内存（如果尚未就绪，通过 WaitForCompletion 同步卡帧初始化）
        /// </summary>
        private static void EnsureCatalogInitialized()
        {
            if (_isCatalogInitialized) return;

            // 检查 Addressables 底层是否已由其他操作初始化
            foreach (var _ in Addressables.ResourceLocators)
            {
                _isCatalogInitialized = true;
                return;
            }

            // 强制阻断初始化目录
            var handle = Addressables.InitializeAsync();
            handle.WaitForCompletion();
            if (handle.IsValid())
            {
                Addressables.Release(handle);
            }

            _isCatalogInitialized = true;
        }

        /// <summary>
        /// 检查某资产 Key 是否在 Addressables 中注册且能被加载（纯同步、不加载实际资产）
        /// </summary>
        /// <param name="address">资源别名或标签</param>
        /// <param name="type">资源类型，传 null 则匹配任意类型</param>
        public static bool Exists(string address, Type type = null)
        {
            if (string.IsNullOrEmpty(address)) return false;

            // 1. 确保目录就绪
            EnsureCatalogInitialized();

            type ??= typeof(Object);

            // 2. 内存字典检索
            foreach (var locator in Addressables.ResourceLocators)
            {
                if (locator.Locate(address, type, out var locations))
                {
                    if (locations != null && locations.Count > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 泛型版本：检查某资产 Key 是否存在且类型匹配（比如是否是 GameObject 或 Sprite）
        /// </summary>
        public static bool Exists<T>(string address) where T : Object
        {
            return Exists(address, typeof(T));
        }
    }

    // =========================================================
    // 扩展
    // =========================================================

    public static class AddressablesExtensions
    {
        /// <summary>
        /// 判断 Inspector 中是否真正拖入了资源
        /// </summary>
        public static bool IsAssigned(this AssetReference assetRef)
        {
            return assetRef != null && assetRef.RuntimeKeyIsValid();
        }

        /// <summary>
        /// 将 Handle 绑定到 CancellationToken 生命周期，取消/销毁时自动 Release
        /// </summary>
        public static AsyncOperationHandle<T> AutoRelease<T>(
            this AsyncOperationHandle<T> handle,
            CancellationToken cancellationToken)
        {
            // 如果调用时物体其实已经销毁了，立刻释放并返回
            if (cancellationToken.IsCancellationRequested)
            {
                if (handle.IsValid()) Addressables.Release(handle);
                return handle;
            }

            // 注册取消回调（物体销毁时自动触发）
            cancellationToken.Register(() =>
            {
                // 只要 Handle 还合法，就执行释放
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
            });

            return handle; // 返回自身以支持链式调用
        }

        public static UniTask<T> LoadAssetAutoReleaseAsync<T>(
            this AssetReferenceT<T> assetRef,
            CancellationToken cancellationToken) where T : UnityEngine.Object
        {
            //  如果调用时物体已经没了，直接取消
            if (cancellationToken.IsCancellationRequested)
            {
                return UniTask.FromCanceled<T>(cancellationToken);
            }

            var handle = assetRef.LoadAssetAsync();
            var utc = new UniTaskCompletionSource<T>();

            // 只要这个 Token 触发（物体销毁），立刻释放资源并打断任务
            cancellationToken.Register(() =>
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
                utc.TrySetCanceled(cancellationToken);
            });

            handle.Completed += h =>
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (h.Status == AsyncOperationStatus.Succeeded)
                {
                    utc.TrySetResult(h.Result);
                }
                else
                {
                    utc.TrySetException(h.OperationException ?? new Exception($"Addressable 加载失败: {assetRef.SubObjectName}"));
                }
            };

            return utc.Task;
        }
    }

    // =========================================================
    //  Debug辅助
    // =========================================================

    public static class AssetDebugger
    {
        public static void CheckMemory()
        {
            var rm = Addressables.ResourceManager;
            Type rmType = typeof(UnityEngine.ResourceManagement.ResourceManager);

            // 获取资产缓存表
            FieldInfo cacheField = rmType.GetField("m_AssetOperationCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var cache = cacheField?.GetValue(rm) as IDictionary;

            Debug.Log($"<color=cyan>====== 1. Addressables 驻留资产列表 ({cache?.Count ?? 0} 个) ======</color>");
            if (cache != null)
            {
                foreach (DictionaryEntry entry in cache)
                {
                    object keyObj = entry.Key;
                    object op = entry.Value;

                    // 1. 拆解 LocationCacheKey，提取真正的资产名字/地址
                    string assetAddress = GetAssetAddressFromKey(keyObj);

                    // 2. 获取引用计数
                    var refProp = op?.GetType().GetProperty("ReferenceCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    int refCount = refProp != null ? (int)refProp.GetValue(op) : -1;

                    // 3. 获取真实加载的对象
                    var resProp = op?.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    object result = resProp?.GetValue(op);

                    Debug.Log($"➤ <color=green>[Key]: {assetAddress}</color> | 内存对象: <color=white>[{result}]</color> | 引用计数: <color=yellow><b>{refCount}</b></color>");
                }
            }

            // 获取场景克隆实例表
            FieldInfo instField = rmType.GetField("m_TrackedInstanceOperations", BindingFlags.NonPublic | BindingFlags.Instance);
            var instSet = instField?.GetValue(rm) as IEnumerable;

            Debug.Log($"<color=cyan>====== 2. Addressables 场景实例追踪表 ======</color>");
            if (instSet != null)
            {
                foreach (var instOp in instSet)
                {
                    var resProp = instOp?.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    object result = resProp?.GetValue(instOp);
                    Debug.Log($"➤ 场景存活实例: <color=green>{result}</color>");
                }
            }
            Debug.Log("<color=cyan>=========================================================</color>");
        }

        // 从 LocationCacheKey 中提取真实 Address 的辅助方法
        private static string GetAssetAddressFromKey(object keyObj)
        {
            if (keyObj == null) return "null";

            Type keyType = keyObj.GetType();

            // 尝试反射获取 LocationCacheKey 内部的 Location 属性或字段
            IResourceLocation location = null;
            PropertyInfo locProp = keyType.GetProperty("Location", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (locProp != null)
            {
                location = locProp.GetValue(keyObj) as IResourceLocation;
            }

            if (location == null)
            {
                FieldInfo locField = keyType.GetField("m_Location", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (locField != null)
                {
                    location = locField.GetValue(keyObj) as IResourceLocation;
                }
            }

            // 如果拿到了 location，直接读取它的 PrimaryKey（你在面板里配的 Address 地址）
            if (location != null)
            {
                return $"[{location.PrimaryKey}] (物理路径: {location.InternalId})";
            }

            return keyObj.ToString();
        }
    }

}