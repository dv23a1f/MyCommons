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
        /// </summary>
        AfterSceneLoad, // 或 SceneReady

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
        /// <summary> 当前用于遮罩/过渡的组件提供者 </summary>
        private static Func<string, ISceneTransitionView> ViewProvider { get; set; }

        public static int DelayFramesAfterLoaded { get; set; } = 5;
        public static bool IsProcessing { get; private set; }

        private static readonly Dictionary<SceneTransitionPhase, List<Action<string>>> Listeners = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void AutoClear()
        {
            Listeners.ForEach(kvp => kvp.Value?.Clear());
            PreviousSceneName = null;
            ViewProvider = null;
        }

        static SceneTransitioner()
        {
            foreach (SceneTransitionPhase phase in Enum.GetValues(typeof(SceneTransitionPhase)))
            {
                Listeners[phase] = new List<Action<string>>();
            }
        }

        /// <summary>
        /// 注册一个 MonoBehaviour 组件 (实现 ISceneTransitionView 接口) 作为通用 view （尝试自动抓取CancellationToken并在销毁时注销）
        /// </summary>
        public static void RegisterTransitionView(MonoBehaviour monoView)
        {
            if (monoView == null) return;
            if (ViewProvider != null)
            {
                Debug.LogWarning("[SceneTransitioner] 在注册前已经存在 ViewProvider，可能存在重复注册行为", monoView);
            }
            if (monoView is ISceneTransitionView view)
            {
                RegisterTransitionView(_ => view, monoView.destroyCancellationToken);
            }
            else
            {
                Debug.LogWarning("[SceneTransitioner] 该组件未实现 ISceneTransitionView 接口", monoView);
            }
        }
        /// <summary>
        /// 注册动态视图提供者（根据目标场景名称决定使用的 View）
        /// </summary>
        public static void RegisterTransitionView(Func<string, ISceneTransitionView> provider, CancellationToken cancellationToken = default)
        {
            if (provider == null) return;
            if (ViewProvider != null)
            {
                Debug.LogWarning("[SceneTransitioner] 在注册前已经存在 ViewProvider，可能存在重复注册行为");
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

        public static async UniTask TransitionAsync(TransitionRequest request)
        {
            if (IsProcessing) return;

            if (!Application.CanStreamedLevelBeLoaded(request.TargetSceneName))
            {
                Debug.LogError($"<color=red>[SceneTransitioner] 切换场景失败！目标场景不存在或未加入 Build Settings：{request.TargetSceneName}</color>");
                return;
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

                // 6. 如果是 Additive 且要求设为活动场景，将新场景 B 设为 ActiveScene
                if (request.LoadMode == LoadSceneMode.Additive && request.SetActiveOnLoaded)
                {
                    Scene loadedScene = SceneManager.GetSceneByName(request.TargetSceneName);
                    if (loadedScene.IsValid())
                    {
                        SceneManager.SetActiveScene(loadedScene);
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
                        Debug.LogWarning($"[SceneTransitioner] 场景 [{request.TargetSceneName}] 的 OnLoadedBeforeFadeIn 超时，已跳过。");
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
                SafeBroadcast(SceneTransitionPhase.AfterSceneLoad, request.TargetSceneName);

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
        /// 纯卸载某个 Additive 场景（不加载新场景），例如关闭小游戏/抽卡/副本地块
        /// </summary>
        public static async UniTask UnloadAsync(string sceneName, bool enableTransitionView = false)
        {
            if (IsProcessing) return;
            Scene scene = SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid() || !scene.isLoaded) return;

            IsProcessing = true;
            try
            {
                ISceneTransitionView currentView = null;
                try
                {
                    currentView = ViewProvider?.Invoke(sceneName);
                }
                catch (System.Exception e) { Debug.LogException(e); }

                if (enableTransitionView && currentView != null)
                {
                    SafeBroadcast(SceneTransitionPhase.TransitionStarted, sceneName);
                    await currentView.ShowOverlayAsync();
                }

                SafeBroadcast(SceneTransitionPhase.BeforeUnload, sceneName);
                await SceneManager.UnloadSceneAsync(scene);

                GC.Collect();
                await Resources.UnloadUnusedAssets();
                SafeBroadcast(SceneTransitionPhase.UnloadCompleted, sceneName);

                if (enableTransitionView && currentView != null)
                {
                    await currentView.HideOverlayAsync();
                    SafeBroadcast(SceneTransitionPhase.TransitionCompleted, sceneName);
                }
            }
            finally
            {
                IsProcessing = false;
            }
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