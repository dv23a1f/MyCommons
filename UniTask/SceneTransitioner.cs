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
        /// 流程开始：过渡画面（黑屏/Loading）开始淡入，旧场景即将被遮挡 
        /// </summary>
        TransitionStarted,

        /// <summary> 
        /// 旧场景已被完全遮挡：即将调用 LoadSceneAsync 加载新场景 
        /// </summary>
        BeforeSceneLoad,

        /// <summary> 
        /// 新场景加载并激活完成：此时过渡画面仍在遮挡，可进行新场景对象初始化 
        /// </summary>
        AfterSceneLoad, // 或 SceneReady

        /// <summary> 
        /// 全流程结束：过渡画面已完全移除，玩家可正式操作交互 
        /// </summary>
        TransitionCompleted
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
            bool freezeTimeDuringTransition = true,
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
        public static string PreviousSceneName { get; private set; }
        public static ISceneTransitionView CurrentView { get; set; }
        public static int DelayFramesAfterLoaded { get; set; } = 5;
        public static bool IsProcessing { get; private set; }

        private static readonly Dictionary<SceneTransitionPhase, List<Action<string>>> Listeners = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void AutoClear()
        {
            Listeners.ForEach(kvp => kvp.Value?.Clear());
            PreviousSceneName = null;
            CurrentView = null;
        }

        static SceneTransitioner()
        {
            foreach (SceneTransitionPhase phase in Enum.GetValues(typeof(SceneTransitionPhase)))
            {
                Listeners[phase] = new List<Action<string>>();
            }
        }

        public static async UniTask TransitionAsync(TransitionRequest request)
        {
            if (IsProcessing) return;

            if (!Application.CanStreamedLevelBeLoaded(request.TargetSceneName))
            {
                Debug.LogError($"<color=red>[SceneTransitioner] 切换场景失败！场景不存在或未加入 Build Settings：{request.TargetSceneName}</color>");
                return;
            }

            IsProcessing = true;
            bool enableTransitionView = CurrentView != null && request.EnableTransitionView;

            float originalTimeScale = Time.timeScale;

            try
            {
                PreviousSceneName = SceneManager.GetActiveScene().name;

                // 广播：切换开始
                SafeBroadcast(SceneTransitionPhase.TransitionStarted, request.TargetSceneName);

                CurrentView?.OnUpdateProgress(0f);

                if (enableTransitionView)
                {
                    try
                    {
                        // UI淡出
                        await CurrentView.ShowOverlayAsync();
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
                    if (enableTransitionView)
                    {
                        try
                        {
                            CurrentView.OnUpdateProgress(op.progress);
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
                if (enableTransitionView)
                {
                    try
                    {
                        // 进度条真正达到 100%
                        CurrentView.OnUpdateProgress(1f);
                        await CurrentView.OnWaitForConfirmAsync();
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

                if (enableTransitionView)
                {
                    try
                    {
                        // 额外等待几帧，避免淡入跳变
                        await UniTask.DelayFrame(DelayFramesAfterLoaded, PlayerLoopTiming.Update);
                        // UI淡入
                        await CurrentView.HideOverlayAsync();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                // 广播：全流程结束
                SafeBroadcast(SceneTransitionPhase.TransitionCompleted, request.TargetSceneName);
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