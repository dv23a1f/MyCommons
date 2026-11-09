using System;
using System.Collections.Generic;
using System.Threading;
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
                // 加载被取消（例如换装面板被提前关闭了）
                Unload(address);
                throw; // 向外抛出取消异常
            }

            Debug.LogError($"[AssetLoader] 加载资产失败: {address}");
            Unload(address);
            return null;
        }

        /// <summary>
        /// 【新增】链式调用入口：AssetLoader.Load<T>("Key").OnSuccess(...).Run();
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
            private Action<string> _onFail;

            public AssetLoadChain(string address)
            {
                _address = address;
            }

            public AssetLoadChain<T> OnSuccess(Action<T> callback)
            {
                _onSuccess = callback;
                return this;
            }

            public AssetLoadChain<T> OnFail(Action<string> callback)
            {
                _onFail = callback;
                return this;
            }

            public void Run()
            {
                ExecuteInternalAsync(CancellationToken.None).Forget();
            }

            public async UniTask Run(CancellationToken ct)
            {
                await ExecuteInternalAsync(ct);
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
                        _onFail?.Invoke(_address);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    _onFail?.Invoke(_address);
                }
                finally
                {

                    _onSuccess = null;
                    _onFail = null;
                }
            }
        }

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
        /// Addressable 场景加载（自动处理上一场景资源卸载）
        /// </summary>
        public static async UniTask<SceneInstance> LoadScene(string sceneAddress, LoadSceneMode loadMode = LoadSceneMode.Single, CancellationToken ct = default)
        {
            var handle = Addressables.LoadSceneAsync(sceneAddress, loadMode);
            await handle.ToUniTask(cancellationToken: ct);
            return handle.Result;
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