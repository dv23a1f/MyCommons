using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DX.Core.SceneManagement
{
    /// <summary>
    /// 场景切换广播时机
    /// </summary>
    public enum SceneTransitionPhase
    {
        /// <summary> 切换刚开始，准备执行黑屏淡出 </summary>
        Started,
        /// <summary> 已经黑屏淡出完毕，即将调用 LoadSceneAsync </summary>
        BeforeLoad,
        /// <summary> 新场景加载并激活完毕，准备执行黑屏淡入 </summary>
        AfterLoad,
        /// <summary> 已经淡入完毕，整个场景切换流程完全结束 </summary>
        Completed
    }

    public struct TransitionRequest
    {
        public string TargetSceneName;
        public Action OnLoadedBeforeFadeIn;

        public TransitionRequest(string targetSceneName, Action onLoadedBeforeFadeIn = null)
        {
            TargetSceneName = targetSceneName;
            OnLoadedBeforeFadeIn = onLoadedBeforeFadeIn;
        }

        public static implicit operator TransitionRequest(string targetSceneName) => new(targetSceneName);
    }

    public interface ISceneTransitionView
    {
        /// <summary>
        /// 淡出场景
        /// </summary>
        UniTask OnStartTransitionAsync();
        /// <summary>
        /// 显示进度
        /// </summary>
        void OnUpdateProgress(float progress);
        /// <summary>
        /// 是否等待输入后再切换，不等待 return UniTask.CompletedTask
        /// </summary>
        UniTask OnWaitForConfirmAsync();
        /// <summary>
        /// 淡入场景
        /// </summary>
        UniTask OnEndTransitionAsync();
    }

    public static class SceneTransitioner
    {
        public static string PreviousSceneName { get; private set; }
        public static ISceneTransitionView CurrentView { get; set; }
        public static int DelayFramesAfterLoaded { get; set; } = 10;
        public static bool IsProcessing { get; private set; }

        private static readonly Dictionary<SceneTransitionPhase, List<Action<string>>> Listeners = new();

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

            try
            {
                PreviousSceneName = SceneManager.GetActiveScene().name;

                // 1. 广播：切换开始
                SafeBroadcast(SceneTransitionPhase.Started, request.TargetSceneName);

                CurrentView?.OnUpdateProgress(0f);

                if (CurrentView != null)
                    await CurrentView.OnStartTransitionAsync();

                // 2. 广播：加载前
                SafeBroadcast(SceneTransitionPhase.BeforeLoad, request.TargetSceneName);

                var op = SceneManager.LoadSceneAsync(request.TargetSceneName);
                op.allowSceneActivation = false;

                while (op.progress < 0.9f)
                {
                    CurrentView?.OnUpdateProgress(Mathf.Clamp01(op.progress / 0.9f));
                    await UniTask.Yield();
                }

                CurrentView?.OnUpdateProgress(1f);

                if (CurrentView != null)
                {
                    await CurrentView.OnWaitForConfirmAsync();
                }

                op.allowSceneActivation = true;
                await op;

                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);

                // 请求自带的特定加载回调
                try
                {
                    request.OnLoadedBeforeFadeIn?.Invoke();
                }
                catch (Exception e) { Debug.LogException(e); }

                // 3. 广播：加载完成后、淡入前
                SafeBroadcast(SceneTransitionPhase.AfterLoad, request.TargetSceneName);

                await UniTask.DelayFrame(DelayFramesAfterLoaded, PlayerLoopTiming.Update);

                if (CurrentView != null)
                    await CurrentView.OnEndTransitionAsync();

                // 4. 广播：全流程结束
                SafeBroadcast(SceneTransitionPhase.Completed, request.TargetSceneName);
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