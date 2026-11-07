using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DX.Core.Utility;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace DX.Core.ReactiveVariable.UIExtensions
{

    /// <summary>
    /// Unity UI 组件对 RxFSM 状态机及响应式共享变量的扩展绑定工具集。
    /// </summary>
    public static class ReactiveVarUIExtensions
    {

        //# =========================================================================
        //# 交互绑定
        //# =========================================================================

        #region BindInteractable
        /// <summary>
        /// [通用原子能力] 将任意 Selectable 组件（Button, Slider, Dropdown 等）的 interactable 与响应式变量绑定
        /// 内置防抖（避免重复重绘）与生命周期自动管理。
        /// </summary>
        public static IDisposable BindInteractable(
            this Selectable selectable,
            IReadOnlyReactiveVar<bool> interactableVar,
            CancellationToken customCt = default)
        {
            if (selectable == null) throw new ArgumentNullException(nameof(selectable));
            if (interactableVar == null) return new ActionDisposable(null);

            CancellationToken destroyToken = selectable.GetCancellationTokenOnDestroy();
            CancellationToken linkedToken = customCt.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(destroyToken, customCt).Token
                : destroyToken;

            if (linkedToken.IsCancellationRequested) return new ActionDisposable(null);

            return interactableVar.Subscribe(canInteract =>
            {
                // 防抖比对，避免触发 Canvas/Graphic 不必要的重绘过渡
                if (selectable != null && selectable.interactable != canInteract)
                {
                    selectable.interactable = canInteract;
                }
            }, notifyImmediately: true, ct: linkedToken);
        }

        #endregion

        //! =========================================================================
        //! Slider 扩展
        //! =========================================================================

        #region Slider

        /// <summary>
        /// [双向绑定] Slider 与可读写变量双向同步（防回环死循环，自动抓取 Token）
        /// </summary>
        public static IDisposable BindTwoWay(
            this Slider slider,
            IReactiveVar<float> sharedVar,
            CancellationToken customCt = default)
        {
            if (slider == null) throw new ArgumentNullException(nameof(slider));
            if (sharedVar == null) throw new ArgumentNullException(nameof(sharedVar));

            CancellationToken linkedToken = ResolveToken(slider.GetCancellationTokenOnDestroy(), customCt);
            if (linkedToken.IsCancellationRequested) return new ActionDisposable(null);

            bool isUpdating = false;

            UnityAction<float> onSliderChanged = newVal =>
            {
                if (isUpdating) return;
                isUpdating = true;
                try { sharedVar.Value = newVal; }
                finally { isUpdating = false; }
            };
            slider.onValueChanged.AddListener(onSliderChanged);

            IDisposable sub = sharedVar.Subscribe(newVal =>
            {
                if (isUpdating || slider == null) return;
                isUpdating = true;
                try { slider.SetValueWithoutNotify(newVal); }
                finally { isUpdating = false; }
            }, notifyImmediately: true, ct: linkedToken);

            return CreateBindingScope(slider, onSliderChanged, sub, linkedToken);
        }

        /// <summary>
        /// [单向只读绑定] 适用于血条、进度条等场景（仅数据源 -> Slider，不可逆）
        /// </summary>
        public static IDisposable BindReadOnly(
            this Slider slider,
            IReadOnlyReactiveVar<float> readOnlyVar,
            CancellationToken customCt = default)
        {
            if (slider == null) throw new ArgumentNullException(nameof(slider));
            if (readOnlyVar == null) throw new ArgumentNullException(nameof(readOnlyVar));

            CancellationToken linkedToken = ResolveToken(slider.GetCancellationTokenOnDestroy(), customCt);
            if (linkedToken.IsCancellationRequested) return new ActionDisposable(null);

            return readOnlyVar.Subscribe(newVal =>
            {
                if (slider != null) slider.SetValueWithoutNotify(newVal);
            }, notifyImmediately: true, ct: linkedToken);
        }

        /// <summary>
        /// [自定义映射双向绑定] 支持 int / 范围映射等非 float 类型变量
        /// </summary>
        public static IDisposable BindTwoWay<TValue>(
            this Slider slider,
            IReactiveVar<TValue> sharedVar,
            Func<TValue, float> toSlider,
            Func<float, TValue> toVar,
            CancellationToken customCt = default)
        {
            if (slider == null) throw new ArgumentNullException(nameof(slider));
            if (sharedVar == null) throw new ArgumentNullException(nameof(sharedVar));
            if (toSlider == null || toVar == null) throw new ArgumentNullException("映射委托不能为空");

            CancellationToken linkedToken = ResolveToken(slider.GetCancellationTokenOnDestroy(), customCt);
            if (linkedToken.IsCancellationRequested) return new ActionDisposable(null);

            bool isUpdating = false;

            UnityAction<float> onSliderChanged = newVal =>
            {
                if (isUpdating || sharedVar.IsDisposed) return;
                isUpdating = true;
                try { sharedVar.Value = toVar(newVal); }
                finally { isUpdating = false; }
            };
            slider.onValueChanged.AddListener(onSliderChanged);

            IDisposable sub = sharedVar.Subscribe(newVal =>
            {
                if (isUpdating || slider == null) return;
                isUpdating = true;
                try { slider.SetValueWithoutNotify(toSlider(newVal)); }
                finally { isUpdating = false; }
            }, notifyImmediately: true, ct: linkedToken);

            return CreateBindingScope(slider, onSliderChanged, sub, linkedToken);
        }

        #endregion
        //! =========================================================================
        //! TMP_Dropdown 扩展
        //! =========================================================================
        #region TMP_Dropdown

        /// <summary>
        /// [双向绑定: int 索引] 绑定 Dropdown 的选项 Index
        /// </summary>
        public static IDisposable BindTwoWay(
            this TMP_Dropdown dropdown,
            IReactiveVar<int> sharedVar,
            CancellationToken customCt = default)
        {
            if (dropdown == null) throw new ArgumentNullException(nameof(dropdown));
            if (sharedVar == null) throw new ArgumentNullException(nameof(sharedVar));

            CancellationToken linkedToken = ResolveToken(dropdown.GetCancellationTokenOnDestroy(), customCt);
            if (linkedToken.IsCancellationRequested) return new ActionDisposable(null);

            bool isUpdating = false;

            UnityAction<int> onDropdownChanged = newIndex =>
            {
                if (isUpdating || sharedVar.IsDisposed) return;
                isUpdating = true;
                try { sharedVar.Value = newIndex; }
                finally { isUpdating = false; }
            };
            dropdown.onValueChanged.AddListener(onDropdownChanged);

            IDisposable sub = sharedVar.Subscribe(newIndex =>
            {
                if (isUpdating || dropdown == null) return;
                isUpdating = true;
                try
                {
                    if (newIndex >= 0 && (dropdown.options == null || newIndex < dropdown.options.Count))
                    {
                        dropdown.SetValueWithoutNotify(newIndex);
                        dropdown.RefreshShownValue();
                    }
                }
                finally { isUpdating = false; }
            }, notifyImmediately: true, ct: linkedToken);

            return CreateBindingScope(dropdown, onDropdownChanged, sub, linkedToken);
        }

        /// <summary>
        /// [双向绑定: Enum 枚举] 直接绑定枚举变量，自动转换 Index 与底层整型
        /// </summary>
        public static IDisposable BindTwoWay<TEnum>(
            this TMP_Dropdown dropdown,
            IReactiveVar<TEnum> sharedVar,
            CancellationToken customCt = default)
            where TEnum : struct, Enum
        {
            return dropdown.BindTwoWay(
                sharedVar,
                toIndex: enumVal => Convert.ToInt32(enumVal),
                toVar: index => (TEnum)Enum.ToObject(typeof(TEnum), index),
                customCt: customCt
            );
        }

        /// <summary>
        /// [双向绑定: string 文本] 与当前选项的 text 双向同步
        /// </summary>
        public static IDisposable BindTwoWay(
            this TMP_Dropdown dropdown,
            IReactiveVar<string> sharedVar,
            CancellationToken customCt = default)
        {
            return dropdown.BindTwoWay(
                sharedVar,
                toIndex: text => dropdown.options != null ? dropdown.options.FindIndex(o => o.text == text) : -1,
                toVar: index => (dropdown.options != null && index >= 0 && index < dropdown.options.Count)
                    ? dropdown.options[index].text
                    : string.Empty,
                customCt: customCt
            );
        }

        /// <summary>
        /// [自定义映射双向绑定] 支持任意对象或自定义逻辑与 Dropdown 的双向映射
        /// </summary>
        public static IDisposable BindTwoWay<TValue>(
            this TMP_Dropdown dropdown,
            IReactiveVar<TValue> sharedVar,
            Func<TValue, int> toIndex,
            Func<int, TValue> toVar,
            CancellationToken customCt = default)
        {
            if (dropdown == null) throw new ArgumentNullException(nameof(dropdown));
            if (sharedVar == null) throw new ArgumentNullException(nameof(sharedVar));
            if (toIndex == null || toVar == null) throw new ArgumentNullException("映射委托不能为空");

            CancellationToken linkedToken = ResolveToken(dropdown.GetCancellationTokenOnDestroy(), customCt);
            if (linkedToken.IsCancellationRequested) return new ActionDisposable(null);

            bool isUpdating = false;

            UnityAction<int> onDropdownChanged = newIndex =>
            {
                if (isUpdating || sharedVar.IsDisposed) return;
                isUpdating = true;
                try { sharedVar.Value = toVar(newIndex); }
                finally { isUpdating = false; }
            };
            dropdown.onValueChanged.AddListener(onDropdownChanged);

            IDisposable sub = sharedVar.Subscribe(newVal =>
            {
                if (isUpdating || dropdown == null) return;
                isUpdating = true;
                try
                {
                    int index = toIndex(newVal);
                    if (index >= 0 && (dropdown.options == null || index < dropdown.options.Count))
                    {
                        dropdown.SetValueWithoutNotify(index);
                        dropdown.RefreshShownValue();
                    }
                }
                finally { isUpdating = false; }
            }, notifyImmediately: true, ct: linkedToken);

            return CreateBindingScope(dropdown, onDropdownChanged, sub, linkedToken);
        }

        #endregion

        //! =========================================================================
        //! Text / TMP_Text 只读绑定扩展
        //! =========================================================================
        #region TMPro.TMP_Text 扩展

        /// <summary>
        /// [文本直连] 直接将 string 类型的变量绑定到 TMP_Text
        /// </summary>
        public static IDisposable BindText(
            this TMP_Text textComponent,
            IReadOnlyReactiveVar<string> sharedVar,
            CancellationToken customCt = default)
        {
            return textComponent.BindText(sharedVar, s => s ?? string.Empty, customCt);
        }

        /// <summary>
        /// [单变量格式化] 将任意变量转换为字符串展示。
        /// <param name="format">格式模板（如 "金币: {0}" 或纯数值格式如 "N0", "F2"）</param>
        /// </summary>
        public static IDisposable BindText<T>(
            this TMP_Text textComponent,
            IReadOnlyReactiveVar<T> sharedVar,
            string format = null,
            CancellationToken customCt = default)
        {
            if (string.IsNullOrEmpty(format))
                return textComponent.BindText(sharedVar, val => val?.ToString() ?? string.Empty, customCt);

            // 如果包含 {0}，走 string.Format 模板；否则走数据自带的 ToString(format)
            Func<T, string> formatter = format.Contains("{0}")
                ? val => string.Format(format, val)
                : val => (val is IFormattable formattable) ? formattable.ToString(format, null) : val?.ToString();

            return textComponent.BindText(sharedVar, formatter, customCt);
        }

        /// <summary>
        /// [单变量自定义转换] 通过委托将变量格式化为文本
        /// </summary>
        public static IDisposable BindText<T>(
            this TMP_Text textComponent,
            IReadOnlyReactiveVar<T> sharedVar,
            Func<T, string> formatter,
            CancellationToken customCt = default)
        {
            if (textComponent == null) throw new ArgumentNullException(nameof(textComponent));
            if (sharedVar == null) throw new ArgumentNullException(nameof(sharedVar));
            if (formatter == null) throw new ArgumentNullException(nameof(formatter));

            CancellationToken linkedToken = ResolveToken(textComponent.GetCancellationTokenOnDestroy(), customCt);
            if (linkedToken.IsCancellationRequested) return new ActionDisposable(null);

            return sharedVar.Subscribe(val =>
            {
                if (textComponent == null) return;
                string newText = formatter(val);
                // 性能关键优化：避免相同内容重复赋值导致 Canvas 重新生成文字网格 (Rebuild)
                if (textComponent.text != newText)
                {
                    textComponent.text = newText;
                }
            }, notifyImmediately: true, ct: linkedToken);
        }

        #endregion

        #region UnityEngine.UI.Text (原生旧版 Text 兼容)

        public static IDisposable BindText(this Text textComponent, IReadOnlyReactiveVar<string> sharedVar, CancellationToken customCt = default)
            => textComponent.BindText(sharedVar, s => s ?? string.Empty, customCt);

        public static IDisposable BindText<T>(this Text textComponent, IReadOnlyReactiveVar<T> sharedVar, string format = null, CancellationToken customCt = default)
        {
            if (string.IsNullOrEmpty(format))
                return textComponent.BindText(sharedVar, val => val?.ToString() ?? string.Empty, customCt);

            Func<T, string> formatter = format.Contains("{0}")
                ? val => string.Format(format, val)
                : val => (val is IFormattable formattable) ? formattable.ToString(format, null) : val?.ToString();

            return textComponent.BindText(sharedVar, formatter, customCt);
        }

        public static IDisposable BindText<T>(this Text textComponent, IReadOnlyReactiveVar<T> sharedVar, Func<T, string> formatter, CancellationToken customCt = default)
        {
            if (textComponent == null) throw new ArgumentNullException(nameof(textComponent));
            if (sharedVar == null) throw new ArgumentNullException(nameof(sharedVar));
            if (formatter == null) throw new ArgumentNullException(nameof(formatter));

            CancellationToken linkedToken = ResolveToken(textComponent.GetCancellationTokenOnDestroy(), customCt);
            if (linkedToken.IsCancellationRequested) return new ActionDisposable(null);

            return sharedVar.Subscribe(val =>
            {
                if (textComponent == null) return;
                string newText = formatter(val);
                if (textComponent.text != newText) textComponent.text = newText;
            }, notifyImmediately: true, ct: linkedToken);
        }

        #endregion

        //! =========================================================================
        //! CanvasGroup 交互绑定扩展
        //! =========================================================================
        #region CanvasGroup

        /// <summary>
        /// [带动画的交互绑定] 绑定 CanvasGroup 的交互状态与可见性过渡。
        /// <para>• 关闭时：瞬间关闭可交互性与射线阻挡，随后根据 duration 渐变淡出至 disabledAlpha。</para>
        /// <para>• 开启时：先阻断点击并平滑淡入至 1.0，完全过渡完毕后才打开 interactable 开关。</para>
        /// </summary>
        /// <param name="canvasGroup">目标 CanvasGroup 组件</param>
        /// <param name="interactableVar">只读响应式布尔变量</param>
        /// <param name="duration">淡入淡出时长（秒），默认 0.25s</param>
        /// <param name="disabledAlpha">不可交互时的 Alpha 目标值（默认 0f）</param>
        /// <param name="interactableDuringTransition">动画过渡期间是否允许点击交互（默认 false：过渡期间锁定并不允许点击）</param>
        /// <param name="blocksRaycastsWhenDisabled">不可交互时是否依然阻挡射线（默认 false）</param>
        /// <param name="animateOnStart">首帧初始化时是否执行过渡动画（默认 false：首帧瞬移，后续状态变化才播动画）</param>
        /// <param name="ignoreTimeScale">动画是否忽略 TimeScale 缩放（默认 true：不受游戏暂停影响）</param>
        /// <param name="onCompleted">过渡彻底完成后的回调，参数 bool 表示最终交互状态（true: 淡入/打开完成, false: 淡出/关闭完成）</param>
        /// <param name="customCt">可选生命周期取消令牌</param>
        public static IDisposable BindInteractable(
            this CanvasGroup canvasGroup,
            IReadOnlyReactiveVar<bool> interactableVar,
            float duration = 0.25f,
            float disabledAlpha = 0f,
            bool interactableDuringTransition = false,
            bool blocksRaycastsWhenDisabled = false,
            bool animateOnStart = false,
            bool ignoreTimeScale = true,
            Action<bool> onCompleted = null,
            CancellationToken customCt = default)
        {
            if (canvasGroup == null) throw new ArgumentNullException(nameof(canvasGroup));
            if (interactableVar == null) return new ActionDisposable(null);

            CancellationToken linkedToken = ResolveToken(canvasGroup.GetCancellationTokenOnDestroy(), customCt);
            if (linkedToken.IsCancellationRequested) return new ActionDisposable(null);

            CancellationTokenSource transitionCts = null;
            bool isFirstRun = true;
            bool? currentTargetState = null;

            void OnTargetStateChanged(bool canInteract)
            {
                if (canvasGroup == null) return;

                // 状态防抖：目标相同时直接忽略
                if (currentTargetState.HasValue && currentTargetState.Value == canInteract)
                {
                    return;
                }

                currentTargetState = canInteract;

                // 取消正在进行的上一段反向动画
                if (transitionCts != null)
                {
                    transitionCts.Cancel();
                    transitionCts.Dispose();
                    transitionCts = null;
                }

                float targetAlpha = canInteract ? 1f : disabledAlpha;

                // ==========================================================
                // 1. 首帧静默对齐（无动画）: 绝对不触发回调，防止首帧销毁或逻辑打断
                // ==========================================================
                if (isFirstRun && !animateOnStart)
                {
                    isFirstRun = false;
                    canvasGroup.alpha = targetAlpha;
                    canvasGroup.interactable = canInteract;
                    canvasGroup.blocksRaycasts = canInteract ? true : blocksRaycastsWhenDisabled;
                    // 这里不调用 onCompleted
                    return;
                }

                isFirstRun = false;

                // ==========================================================
                // 2. 运行时如果 duration <= 0（显式配置的瞬切）:
                // ==========================================================
                if (duration <= 0f)
                {
                    canvasGroup.alpha = targetAlpha;
                    canvasGroup.interactable = canInteract;
                    canvasGroup.blocksRaycasts = canInteract ? true : blocksRaycastsWhenDisabled;
                    onCompleted?.Invoke(canInteract);
                    return;
                }

                transitionCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
                CancellationToken transToken = transitionCts.Token;

                ExecuteTransitionAsync(
                    canvasGroup,
                    canInteract,
                    targetAlpha,
                    duration,
                    interactableDuringTransition,
                    blocksRaycastsWhenDisabled,
                    ignoreTimeScale,
                    transToken,
                    onCompleted: () =>
                    {
                        transitionCts?.Dispose();
                        transitionCts = null;
                        onCompleted?.Invoke(canInteract);
                    }
                ).Forget();
            }

            IDisposable subscription = interactableVar.Subscribe(
                OnTargetStateChanged,
                notifyImmediately: true,
                ct: linkedToken
            );

            Action cleanup = () =>
            {
                subscription?.Dispose();
                if (transitionCts != null)
                {
                    transitionCts.Cancel();
                    transitionCts.Dispose();
                    transitionCts = null;
                }
            };

            return new ActionDisposable(cleanup);
        }

        private static async UniTaskVoid ExecuteTransitionAsync(
            CanvasGroup canvasGroup,
            bool canInteract,
            float targetAlpha,
            float duration,
            bool interactableDuringTransition,
            bool blocksRaycastsWhenDisabled,
            bool ignoreTimeScale,
            CancellationToken ct,
            Action onCompleted)
        {
            try
            {
                float startAlpha = canvasGroup.alpha;

                // ==========================================================
                // 1. 动画开始瞬间
                // ==========================================================
                canvasGroup.interactable = interactableDuringTransition;
                canvasGroup.blocksRaycasts = true;

                // 2. 根据 Alpha 实际间距动态计算耗时（折返时按比例缩短）
                float alphaDistance = Mathf.Abs(targetAlpha - startAlpha);
                float actualDuration = duration * alphaDistance;

                if (actualDuration > 0f)
                {
                    float elapsed = 0f;
                    while (elapsed < actualDuration)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update, ct);

                        float dt = ignoreTimeScale ? Time.unscaledDeltaTime : Time.deltaTime;
                        elapsed += dt;

                        float t = Mathf.Clamp01(elapsed / actualDuration);
                        float smoothT = Mathf.SmoothStep(0f, 1f, t);

                        if (canvasGroup == null) return;
                        canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, smoothT);
                    }
                }

                if (canvasGroup == null) return;

                // 确保最终数值准确对齐终点
                canvasGroup.alpha = targetAlpha;

                // ==========================================================
                // 3. 动画结束瞬间：完成状态交接
                // ==========================================================
                if (!canInteract)
                {
                    canvasGroup.interactable = false;
                    canvasGroup.blocksRaycasts = blocksRaycastsWhenDisabled;
                }
                else
                {
                    canvasGroup.interactable = true;
                    canvasGroup.blocksRaycasts = true;
                }

                onCompleted?.Invoke();
            }
            catch (OperationCanceledException) { }
        }
        #endregion
        //# =========================================================================
        //# 私有通用辅助方法
        //# =========================================================================

        private static CancellationToken ResolveToken(CancellationToken componentToken, CancellationToken customCt)
        {
            if (!customCt.CanBeCanceled) return componentToken;
            if (componentToken == customCt) return componentToken;
            if (componentToken.IsCancellationRequested || customCt.IsCancellationRequested)
                return new CancellationToken(true);

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(componentToken, customCt);

            linkedCts.Token.Register(state => ((CancellationTokenSource)state)?.Dispose(), linkedCts);

            return linkedCts.Token;
        }

        private static IDisposable CreateBindingScope<TComponent, TEventVal>(
            TComponent component,
            UnityAction<TEventVal> unityAction,
            IDisposable subscription,
            CancellationToken token)
            where TComponent : Component
        {
            CancellationTokenRegistration reg = default;

            Action cleanup = () =>
            {
                reg.Dispose();
                subscription?.Dispose();

                if (component != null)
                {
                    if (component is Slider slider && unityAction is UnityAction<float> sliderAction)
                        slider.onValueChanged.RemoveListener(sliderAction);
                    else if (component is TMP_Dropdown dropdown && unityAction is UnityAction<int> dropdownAction)
                        dropdown.onValueChanged.RemoveListener(dropdownAction);
                }
            };

            reg = token.Register(cleanup);
            return new ActionDisposable(cleanup);
        }

    }

}
