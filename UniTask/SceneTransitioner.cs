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
    }

    public struct TransitionRequest
    {
        public string TargetSceneName;
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
            bool enableTransitionView = true,
            bool freezeTimeDuringTransition = false,
            Func<CancellationToken, UniTask> onLoadedBeforeFadeIn = null,
            float timeoutSeconds = 0f)
        {
            TargetSceneName = targetSceneName;
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
                Debug.LogWarning("<color=yellow>[Scene] 在注册前已经存在 ViewProvider，可能存在重复注册。</color>");
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
                Debug.LogError($"<color=red>[SceneTransitioner] 切换场景失败！场景不存在或未加入 Build Settings：{request.TargetSceneName}</color>");
                return false;
            }

            IsProcessing = true;

            float originalTimeScale = Time.timeScale;

            try
            {
                PreviousSceneName = SceneManager.GetActiveScene().name;

                ISceneTransitionView currentView = null;
                try
                {
                    if (HasViewProvider && request.EnableTransitionView)
                        currentView = ViewProvider?.Invoke(request.TargetSceneName);
                }
                catch (System.Exception e) { Debug.LogException(e); }

                // 广播：切换开始

                if (currentView != null)
                {
                    try
                    {
                        SafeBroadcast(SceneTransitionPhase.TransitionStarted, request.TargetSceneName);

                        currentView.OnUpdateProgress(0f);
                        // UI淡出
                        await currentView.ShowOverlayAsync();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                // 广播：加载前
                SafeBroadcast(SceneTransitionPhase.BeforeSceneLoad, request.TargetSceneName);

                // 冻结时间
                if (request.FreezeTimeDuringTransition)
                {
                    Time.timeScale = 0f;
                }

                var op = SceneManager.LoadSceneAsync(request.TargetSceneName);
                op.allowSceneActivation = false;

                while (op.progress < 0.9f)
                {
                    if (currentView != null)
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

                // 允许激活并等待新场景完全加载（Awake/Start执行）
                op.allowSceneActivation = true;
                await op;
                // 确保初始生命周期执行完成
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
                if (currentView != null)
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

                if (currentView != null)
                {
                    try
                    {
                        // 额外等待几帧，避免淡入跳变
                        await UniTask.DelayFrame(DelayFramesAfterLoaded, PlayerLoopTiming.Update);
                        // UI淡入
                        await currentView.HideOverlayAsync();

                        // 广播：全流程结束
                        SafeBroadcast(SceneTransitionPhase.TransitionCompleted, request.TargetSceneName);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                Debug.Log($"<color=cyan>[Scene]</color> 迁移成功：[{PreviousSceneName}] → <color=green>[{request.TargetSceneName}]</color>");

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