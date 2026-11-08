using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Sirenix.Utilities;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DX.Core.SceneManagement
{
    /// <summary>
    /// 场景切换广播时机
    /// </summary>
    public enum SceneTransitionPhase
    {
        /// <summary> 
        /// 流程开始：开始过渡画面（黑屏/Loading）
        /// </summary>
        TransitionStarted,

        /// <summary> 
        /// 即将调用 LoadSceneAsync 加载新场景 
        /// </summary>
        BeforeSceneLoad,

        /// <summary> 
        /// 新场景加载完毕，场景内组件 Awake/Start 已执行
        /// 初始场景加载后，且未在 Awake/Start 中切换场景时会补发该事件
        /// </summary>
        SceneReady,

        /// <summary> 
        /// 过渡画面流程已结束
        /// </summary>
        TransitionCompleted,

        /// <summary>
        /// 即将卸载 Additive 加载的场景
        /// </summary>
        BeforeUnload,

        /// <summary>
        /// Additive 卸载完毕
        /// </summary>
        UnloadCompleted
    }

    public struct TransitionRequest
    {
        public string TargetSceneName;
        public LoadSceneMode LoadMode;
        /// <summary> 
        /// [Additive] 设置需要卸载的旧场景名。若为 null / 空字符串，则不卸载任何场景（纯追加加载）。
        /// 若需要替换当前场景，直接传入 SceneTransitioner.ActiveSceneName 即可。
        /// </summary>
        public string SceneToUnload;
        /// <summary>  [Additive] 是否激活加载的目标场景 </summary>
        public bool SetActiveOnLoaded;
        /// <summary> 是否启用淡入淡出等过渡效果（需配置 ISceneTransitionView，默认true） </summary>
        public bool EnableTransitionView;
        /// <summary> 回调函数，提供 CancellationToken 用于超时中止 </summary>
        public Func<CancellationToken, UniTask> OnLoadedBeforeFadeIn;
        /// <summary> 超时时间（秒），<= 0 表示不超时无限等待 </summary>
        public float TimeoutSeconds;
        /// <summary> 加载及初始化期间是否冻结游戏时间（Time.timeScale = 0） </summary>
        public bool FreezeTimeDuringTransition;

        public TransitionRequest(
            string targetSceneName,
            LoadSceneMode loadMode = LoadSceneMode.Single,
            string sceneToUnload = null,
            bool setActiveOnLoaded = true,
            bool enableTransitionView = true,
            bool freezeTimeDuringTransition = true,
            Func<CancellationToken, UniTask> onLoadedBeforeFadeIn = null,
            float timeoutSeconds = 0f)
        {
            TargetSceneName = targetSceneName;
            LoadMode = loadMode;
            SceneToUnload = sceneToUnload;
            SetActiveOnLoaded = setActiveOnLoaded;
            EnableTransitionView = enableTransitionView;
            FreezeTimeDuringTransition = freezeTimeDuringTransition;
            OnLoadedBeforeFadeIn = onLoadedBeforeFadeIn;
            TimeoutSeconds = timeoutSeconds;
        }

        public static implicit operator TransitionRequest(string targetSceneName) => new(targetSceneName);
    }

    public struct UnloadRequest
    {
        /// <summary> 需要卸载的目标场景列表 </summary>
        public IReadOnlyList<string> SceneNames;
        /// <summary> 是否启用遮罩过渡动画 </summary>
        public bool EnableTransitionView;
        /// <summary> 卸载完成后是否触发无用资源清理（GC + UnloadUnusedAssets），默认 true </summary>
        public bool TriggerGC;

        public UnloadRequest(string sceneName)
        {
            SceneNames = new[] { sceneName };
            EnableTransitionView = true;
            TriggerGC = true;
        }

        public UnloadRequest(IEnumerable<string> sceneNames)
        {
            SceneNames = new List<string>(sceneNames);
            EnableTransitionView = true;
            TriggerGC = true;
        }

        // 隐式转换：可以直接把单个场景名当 Request 传入
        public static implicit operator UnloadRequest(string sceneName) => new(sceneName);
    }

    public interface ISceneTransitionView
    {
        /// <summary> 展现遮罩/过渡画面（如：淡入黑屏、展开百叶窗） </summary>
        UniTask ShowOverlayAsync();
        /// <summary> 显示进度 </summary>
        void OnUpdateProgress(float progress);
        /// <summary> 是否等待输入后再切换，不等待 return UniTask.CompletedTask </summary>
        UniTask OnWaitForConfirmAsync();
        /// <summary> 隐藏遮罩/过渡画面（如：黑屏淡出，露出新场景） </summary>
        UniTask HideOverlayAsync();
    }

    public static class SceneTransitioner
    {
        /// <summary> 当前处于激活状态的主场景名称 </summary>
        public static string ActiveSceneName => SceneManager.GetActiveScene().name;
        /// <summary> 上一个被激活的场景名称 </summary>
        public static string PreviousSceneName { get; private set; }

        private static readonly HashSet<string> SubScenes = new();
        /// <summary> 当前已挂载的所有未激活 Additive 子场景</summary>
        public static IReadOnlyCollection<string> LoadedSubScenes => SubScenes;
        /// <summary> 当前用于遮罩/过渡的组件提供者 </summary>
        private static Func<string, ISceneTransitionView> ViewProvider { get; set; }
        public static bool HasViewProvider => ViewProvider != null;

        public static int DelayFramesAfterLoaded { get; set; } = 5;
        public static bool IsProcessing { get; private set; }

        private static readonly Dictionary<SceneTransitionPhase, List<Action<string>>> Listeners = new();

        //! =============== 初始化相关 ===============
        static SceneTransitioner()
        {
            foreach (SceneTransitionPhase phase in Enum.GetValues(typeof(SceneTransitionPhase)))
            {
                Listeners[phase] = new List<Action<string>>();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void AutoClear()
        {
            Listeners.ForEach(kvp => kvp.Value?.Clear());
            PreviousSceneName = null;
            ViewProvider = null;
            SubScenes.Clear();
        }

        // 首场景加载后自动触发
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InitializeFirstScene()
        {
            BroadcastFirstSceneAsync().Forget();
        }

        private static async UniTaskVoid BroadcastFirstSceneAsync()
        {
            // 等待到首帧生命周期末尾，确保首场景物体的 Awake/Start 和 Subscribe 已全部完成
            await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);

            // 如果在首帧内有人主动调用了 TransitionAsync，
            // 此时 PreviousSceneName 会被赋值，且 IsProcessing 为 true，直接跳过补发
            if (PreviousSceneName != null || IsProcessing) return;

            string currentSceneName = SceneManager.GetActiveScene().name;

            //  补发场景就绪广播
            SafeBroadcast(SceneTransitionPhase.SceneReady, currentSceneName);

            // 保持 PreviousSceneName 为 null，代表当前场景没有前置场景。
        }

        //! =============================
        /// <summary>
        /// 直接注册具体的过渡视图组件。（可传入token用于自动注销）
        /// </summary>
        public static void RegisterTransitionView(ISceneTransitionView view, CancellationToken cancellationToken = default)
        {
            if (view == null) return;
            RegisterTransitionView(_ => view, cancellationToken);
        }

        /// <summary>
        /// 注册动态视图提供者（根据目标场景名称决定使用的 View）
        /// </summary>
        public static void RegisterTransitionView(Func<string, ISceneTransitionView> provider, CancellationToken cancellationToken = default)
        {
            if (provider == null) return;
            if (HasViewProvider)
            {
                Debug.LogWarning("<color=yellow>[Scene]</color> 在注册前已经存在 ViewProvider，可能存在重复注册行为");
            }
            ViewProvider = provider;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    // 只有当当前存储的 ViewProvider 依然是本次注册的那个对象实例时，才清空
                    if (object.ReferenceEquals(ViewProvider, provider))
                    {
                        ViewProvider = null;
                    }
                });
            }
        }

        public static async UniTask<bool> TransitionAsync(TransitionRequest request)
        {
            if (IsProcessing) return false;

            if (!Application.CanStreamedLevelBeLoaded(request.TargetSceneName))
            {
                Debug.LogError($"<color=red>[Scene]</color> 切换场景失败！目标场景不存在或未加入 Build Settings：<color=red>{request.TargetSceneName}</color>");
                return false;
            }
            if (request.LoadMode == LoadSceneMode.Additive && IsSubSceneLoaded(request.TargetSceneName))
            {
                Debug.LogWarning($"<color=yellow>[Scene]</color> 子场景 [{request.TargetSceneName}] 已经加载过，跳过重复加载。");
                return false;
            }

            IsProcessing = true;

            float originalTimeScale = Time.timeScale;

            try
            {

                // 记录发起切换时的场景
                PreviousSceneName = SceneManager.GetActiveScene().name;
                ISceneTransitionView currentView = null;
                try
                {
                    currentView = ViewProvider?.Invoke(request.TargetSceneName);
                }
                catch (System.Exception e) { Debug.LogException(e); }

                // 1. 广播：切换开始
                if (currentView != null && request.EnableTransitionView)
                {
                    SafeBroadcast(SceneTransitionPhase.TransitionStarted, request.TargetSceneName);

                    currentView.OnUpdateProgress(0f);
                }

                // 2. UI遮罩黑屏淡入
                if (currentView != null && request.EnableTransitionView)
                {
                    try
                    {
                        await currentView.ShowOverlayAsync();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                // 广播：加载前
                SafeBroadcast(SceneTransitionPhase.BeforeSceneLoad, request.TargetSceneName);

                // 3. 卸载与清理逻辑
                if (request.LoadMode == LoadSceneMode.Additive)
                {
                    // 显式指定了才卸载，没指定直接跳过（纯叠加）
                    if (!string.IsNullOrEmpty(request.SceneToUnload))
                    {
                        Scene sceneToUnload = SceneManager.GetSceneByName(request.SceneToUnload);
                        if (sceneToUnload.IsValid() && sceneToUnload.isLoaded && request.SceneToUnload != request.TargetSceneName)
                        {
                            await SceneManager.UnloadSceneAsync(sceneToUnload);
                            SubScenes.Remove(request.SceneToUnload);
                            // 卸载了旧场景，清理其遗留资产
                            GC.Collect();
                            await Resources.UnloadUnusedAssets();
                        }
                    }
                }

                // 4. 冻结时间
                if (request.FreezeTimeDuringTransition)
                {
                    Time.timeScale = 0f;
                }

                // 5. 异步加载新场景（场景 B）
                var op = SceneManager.LoadSceneAsync(request.TargetSceneName, request.LoadMode);
                op.allowSceneActivation = false;

                while (op.progress < 0.9f)
                {
                    if (currentView != null && request.EnableTransitionView)
                    {
                        try
                        {
                            currentView.OnUpdateProgress(op.progress);
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                        }
                    }
                    await UniTask.Yield();
                }

                // 允许激活并等待新场景完全加载
                op.allowSceneActivation = true;
                await op;

                if (request.LoadMode == LoadSceneMode.Single)
                {
                    // Single 模式下，旧的所有 Additive 场景已被 Unity 物理清空
                    SubScenes.Clear();
                }
                // 6. 如果是 Additive 且要求设为活动场景，将新场景 B 设为 ActiveScene
                else if (request.LoadMode == LoadSceneMode.Additive)
                {
                    if (request.SetActiveOnLoaded)
                    {
                        Scene loadedScene = SceneManager.GetSceneByName(request.TargetSceneName);
                        if (loadedScene.IsValid())
                        {
                            SceneManager.SetActiveScene(loadedScene);
                        }
                        // 如果被设为了 Active，确保它不在子场景集合里（防御性移除）
                        SubScenes.Remove(request.TargetSceneName);
                    }
                    else
                    {
                        // 满足条件：是 Additive 且 未被设为 Active，加入记录
                        SubScenes.Add(request.TargetSceneName);
                    }
                }

                // 确保新场景所有组件生命周期（Awake/Start）执行完成
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);

                // 执行进入新场景后的准备工作（带超时控制）
                // 此时新场景对象已经可以安全访问
                if (request.OnLoadedBeforeFadeIn != null)
                {
                    try
                    {
                        if (request.TimeoutSeconds > 0)
                        {
                            using var cts = new CancellationTokenSource();
                            cts.CancelAfterSlim(TimeSpan.FromSeconds(request.TimeoutSeconds));
                            await request.OnLoadedBeforeFadeIn(cts.Token);
                        }
                        else
                        {
                            await request.OnLoadedBeforeFadeIn(CancellationToken.None);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.LogWarning($"<color=yellow>[Scene]</color> 场景 [{request.TargetSceneName}] 的 OnLoadedBeforeFadeIn 超时，已跳过。");
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                // 等待用户确认
                if (currentView != null && request.EnableTransitionView)
                {
                    try
                    {
                        // 进度条真正达到 100%
                        currentView.OnUpdateProgress(1f);
                        await currentView.OnWaitForConfirmAsync();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                // 恢复时间
                if (request.FreezeTimeDuringTransition)
                {
                    Time.timeScale = originalTimeScale;
                }

                // 广播：加载完成后、淡入前
                SafeBroadcast(SceneTransitionPhase.SceneReady, request.TargetSceneName);

                if (currentView != null && request.EnableTransitionView)
                {
                    try
                    {
                        // 额外等待几帧，避免淡入跳变
                        await UniTask.DelayFrame(DelayFramesAfterLoaded, PlayerLoopTiming.Update);
                        // UI淡入
                        await currentView.HideOverlayAsync();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                if (currentView != null && request.EnableTransitionView)
                {
                    // 广播：全流程结束
                    SafeBroadcast(SceneTransitionPhase.TransitionCompleted, request.TargetSceneName);
                }

                if (request.LoadMode == LoadSceneMode.Single)
                {
                    Debug.Log($"<color=cyan>[Scene]</color> 迁移成功：<color=yellow>[{PreviousSceneName}]</color> → <color=green>[{request.TargetSceneName}]</color>");
                }
                else if (request.LoadMode == LoadSceneMode.Additive)
                {
                    Debug.Log($"<color=brown>[Scene]</color> 加载子场景：<color=green>[{request.TargetSceneName}]</color>");
                }
                return true;
            }
            finally
            {
                if (request.FreezeTimeDuringTransition)
                {
                    Time.timeScale = originalTimeScale;
                }
                IsProcessing = false;
            }
        }

        /// <summary>
        /// 支持单场景或批量场景卸载
        /// </summary>
        public static async UniTask<bool> UnloadAsync(UnloadRequest request)
        {
            if (IsProcessing) return false;
            if (request.SceneNames == null || request.SceneNames.Count == 0) return false;

            // 1. 过滤出真正合法、已加载的场景，并严格排除 Active 场景
            Scene activeScene = SceneManager.GetActiveScene();
            var validScenesToUnload = new List<Scene>();

            foreach (var sceneName in request.SceneNames)
            {
                Scene scene = SceneManager.GetSceneByName(sceneName);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    Debug.LogWarning($"<color=yellow>[Scene]</color> 跳过无效场景：[{sceneName}]");
                    continue;
                }

                if (scene == activeScene)
                {
                    Debug.LogError($"<color=red>[Scene]</color> 卸载拦截：场景 [{sceneName}] 是当前活跃场景，已跳过！");
                    continue;
                }

                validScenesToUnload.Add(scene);
            }

            if (validScenesToUnload.Count == 0) return false;

            IsProcessing = true;
            try
            {
                // 2. 遮罩淡入（只需执行一次）
                ISceneTransitionView currentView = null;
                if (request.EnableTransitionView)
                {
                    try
                    {
                        // 使用首个场景名称作为获取 View 的标识
                        currentView = ViewProvider?.Invoke(validScenesToUnload[0].name);
                    }
                    catch (Exception e) { Debug.LogException(e); }

                    if (currentView != null)
                    {
                        SafeBroadcast(SceneTransitionPhase.TransitionStarted, validScenesToUnload[0].name);
                        await currentView.ShowOverlayAsync();
                    }
                }

                // 3. 广播所有场景的 BeforeUnload，并并发发起异步卸载
                var unloadTasks = new List<UniTask>();
                var unloadedSceneNames = new List<string>();

                foreach (var scene in validScenesToUnload)
                {
                    string sceneName = scene.name; // 提前记录场景名称
                    unloadedSceneNames.Add(sceneName);

                    SafeBroadcast(SceneTransitionPhase.BeforeUnload, sceneName);
                    unloadTasks.Add(SceneManager.UnloadSceneAsync(scene).ToUniTask());
                }

                // 4. 等待所有场景物理卸载完毕
                await UniTask.WhenAll(unloadTasks);

                // 5. 维护 HashSet：将成功卸载的场景移出集合
                foreach (var sceneName in unloadedSceneNames)
                {
                    SubScenes.Remove(sceneName); // 正确移除真实场景名
                    Debug.Log($"<color=brown>[Scene]</color> 卸载子场景: <color=yellow>[{sceneName}]</color>");
                    SafeBroadcast(SceneTransitionPhase.UnloadCompleted, sceneName);
                }

                // 6. 统一执行一次全局垃圾回收（只耗费一次卡顿时间）
                if (request.TriggerGC)
                {
                    GC.Collect();
                    await Resources.UnloadUnusedAssets();
                }

                // 7. 遮罩淡出（只需执行一次）
                if (currentView != null && request.EnableTransitionView)
                {
                    await currentView.HideOverlayAsync();
                    SafeBroadcast(SceneTransitionPhase.TransitionCompleted, validScenesToUnload[0].name);
                }

                return true;
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// 检查某个子场景是否已经被挂载
        /// </summary>
        public static bool IsSubSceneLoaded(string sceneName) => SubScenes.Contains(sceneName);

        /// <summary> 便捷重载：卸载单个场景 </summary>
        public static UniTask<bool> UnloadAsync(string sceneName, bool enableTransitionView = true)
            => UnloadAsync(new UnloadRequest(sceneName) { EnableTransitionView = enableTransitionView });

        /// <summary>
        /// 一键卸载所有挂载的子场景（遮罩只播放一次，GC 只运行一次）
        /// </summary>
        public static async UniTask<bool> UnloadAllSubScenesAsync(bool enableTransitionView = true)
        {
            if (SubScenes.Count == 0) return true;

            // 直接打包所有 SubScenes 发起批量卸载
            return await UnloadAsync(new UnloadRequest(SubScenes) { EnableTransitionView = enableTransitionView });
        }

        //~ ========== 事件订阅 ==========
        /// <summary>
        /// 订阅场景切换事件
        /// </summary>
        /// <param name="phase">广播时机</param>
        /// <param name="callback">回调（参数为目标场景名）</param>
        /// <param name="cancellationToken">用于自动注销的 Token。若为 default 且 callback 绑定在 MonoBehaviour 上，将尝试自动绑定其 destroyToken</param>
        public static void Subscribe(SceneTransitionPhase phase, Action<string> callback, CancellationToken cancellationToken = default)
        {
            if (callback == null) return;

            var list = Listeners[phase];
            list.Add(callback);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    Unsubscribe(phase, callback);
                });
            }
        }

        /// <summary>
        /// 手动注销
        /// </summary>
        public static void Unsubscribe(SceneTransitionPhase phase, Action<string> callback)
        {
            if (callback == null) return;
            if (Listeners.TryGetValue(phase, out var list))
            {
                list.Remove(callback);
            }
        }

        private static void SafeBroadcast(SceneTransitionPhase phase, string sceneName)
        {
            if (!Listeners.TryGetValue(phase, out var list)) return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                try
                {
                    list[i]?.Invoke(sceneName);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }
    }
}