using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DX.Core.Extensions.UI
{
    //!=====================================
    //! 基于 EventSystems 的点击、拖拽等通用基础扩展方法
    //!=====================================
    #region IEventBindable
    public interface IEventBindable
    {
        bool Interactable { get; set; }
    }

    internal interface IInteractableDispatcher
    {
        void UpdateActiveState();
    }

    internal interface IBindableScope
    {
        GameObject gameObject { get; }
        CancellationToken cancellationToken { get; }
    }

    internal static class BindableInternalUtility
    {
        /// <summary>
        /// 安全 Link Token，当触发取消时自动释放 LinkedCTS，防止内存泄漏
        /// </summary>
        public static CancellationToken LinkTokenSafely(CancellationToken destroyToken, CancellationToken customCt)
        {
            if (!customCt.CanBeCanceled) return destroyToken;
            if (customCt == destroyToken) return destroyToken;
            if (destroyToken.IsCancellationRequested || customCt.IsCancellationRequested)
                return new CancellationToken(true);

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(destroyToken, customCt);
            linkedCts.Token.Register(state => ((CancellationTokenSource)state)?.Dispose(), linkedCts);
            return linkedCts.Token;
        }

        public static TDispatcher GetOrAddDispatcher<TDispatcher>(GameObject go)
            where TDispatcher : Component, IInteractableDispatcher
        {
            if (!go.TryGetComponent<TDispatcher>(out var dispatcher))
            {
                dispatcher = go.AddComponent<TDispatcher>();
                dispatcher.hideFlags = HideFlags.HideAndDontSave;
            }
            return dispatcher;
        }

        /// <summary>
        /// 通用手势绑定与生命周期自动注销引擎
        /// </summary>
        public static EventBindableScope Bind<TDispatcher>(
            EventBindableScope scope,
            Action<TDispatcher, Action<PointerEventData>> addHandler,
            Action<TDispatcher, Action<PointerEventData>> removeHandler,
            Action<PointerEventData> callback,
            Func<PointerEventData, bool> filter)
            where TDispatcher : Component, IInteractableDispatcher
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (scope.cancellationToken.IsCancellationRequested) return scope;

            // 只有绑定了具体的事件类型时，才会按需挂载对应的 Dispatcher
            TDispatcher dispatcher = GetOrAddDispatcher<TDispatcher>(scope.gameObject);

            Action<PointerEventData> handler = eventData =>
            {
                if (filter != null && !filter.Invoke(eventData)) return;
                callback.Invoke(eventData);
            };

            addHandler(dispatcher, handler);
            dispatcher.UpdateActiveState();

            scope.cancellationToken.Register(() =>
            {
                if (dispatcher != null)
                {
                    removeHandler(dispatcher, handler);
                    dispatcher.UpdateActiveState();
                }
            });

            return scope;
        }

        /// <summary>
        /// 无参事件绑定（不干扰组件自身的休眠/激活状态）
        /// </summary>
        public static EventBindableScope BindVoid<TDispatcher>(
            EventBindableScope scope,
            Action<TDispatcher, Action> addHandler,
            Action<TDispatcher, Action> removeHandler,
            Action callback)
            where TDispatcher : Component, IInteractableDispatcher
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (scope.cancellationToken.IsCancellationRequested) return scope;

            TDispatcher dispatcher = GetOrAddDispatcher<TDispatcher>(scope.gameObject);

            addHandler(dispatcher, callback);

            scope.cancellationToken.Register(() =>
            {
                if (dispatcher != null)
                {
                    removeHandler(dispatcher, callback);
                }
            });

            return scope;
        }
    }

    /// <summary>
    /// 统一的轻量级交互作用域结构体 
    /// </summary>
    public readonly struct EventBindableScope : IBindableScope, IEventBindable
    {
        public GameObject gameObject { get; }
        public CancellationToken cancellationToken { get; }

        public EventBindableScope(GameObject go, CancellationToken customCt = default)
        {
            if (go == null) throw new ArgumentNullException(nameof(go));
            gameObject = go;
            cancellationToken = BindableInternalUtility.LinkTokenSafely(go.GetCancellationTokenOnDestroy(), customCt);
        }

        public EventBindableScope(Component component, CancellationToken customCt = default)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            gameObject = component.gameObject;
            cancellationToken = BindableInternalUtility.LinkTokenSafely(component.GetCancellationTokenOnDestroy(), customCt);
        }

        /// <summary>
        /// 一键控制当前物体上所有已挂载的 Dispatcher 的启用/禁用
        /// </summary>
        bool IEventBindable.Interactable
        {
            get
            {
                // 只要有任意一个分发器启用，就视为可交互
                using var listPool = UnityEngine.Pool.ListPool<IEventBindable>.Get(out var dispatchers);
                gameObject.GetComponents(dispatchers);
                for (int i = 0; i < dispatchers.Count; i++)
                {
                    if (dispatchers[i].Interactable) return true;
                }
                return false;
            }
            set
            {
                // 批量控制：同时禁用/启用 Pointer、Drag、Drop 等所有已注册分发器
                using var listPool = UnityEngine.Pool.ListPool<IEventBindable>.Get(out var dispatchers);
                gameObject.GetComponents(dispatchers);
                for (int i = 0; i < dispatchers.Count; i++)
                {
                    dispatchers[i].Interactable = value;
                }
            }
        }
    }

    public static class BindableExtensions
    {
        // =========================================================================
        // 统一入口扩展方法
        // =========================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EventBindableScope AsEventBindable(this Component component, CancellationToken customCt = default)
            => new EventBindableScope(component, customCt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EventBindableScope AsEventBindable(this GameObject go, CancellationToken customCt = default)
            => new EventBindableScope(go, customCt);

        // =========================================================================
        // IPointer 系列手势（按需挂载 PointerEventDispatcher）
        // =========================================================================

        public static EventBindableScope OnPointerClick(this EventBindableScope scope, Action<PointerEventData> onClick, Func<PointerEventData, bool> filter = null)
            => BindableInternalUtility.Bind<PointerEventDispatcher>(scope, (d, h) => d.OnClick += h, (d, h) => d.OnClick -= h, onClick, filter);

        public static EventBindableScope OnPointerDown(this EventBindableScope scope, Action<PointerEventData> onDown, Func<PointerEventData, bool> filter = null)
            => BindableInternalUtility.Bind<PointerEventDispatcher>(scope, (d, h) => d.OnDown += h, (d, h) => d.OnDown -= h, onDown, filter);

        public static EventBindableScope OnPointerUp(this EventBindableScope scope, Action<PointerEventData> onUp, Func<PointerEventData, bool> filter = null)
            => BindableInternalUtility.Bind<PointerEventDispatcher>(scope, (d, h) => d.OnUp += h, (d, h) => d.OnUp -= h, onUp, filter);

        public static EventBindableScope OnPointerEnter(this EventBindableScope scope, Action<PointerEventData> onEnter, Func<PointerEventData, bool> filter = null)
            => BindableInternalUtility.Bind<PointerEventDispatcher>(scope, (d, h) => d.OnEnter += h, (d, h) => d.OnEnter -= h, onEnter, filter);

        public static EventBindableScope OnPointerExit(this EventBindableScope scope, Action<PointerEventData> onExit, Func<PointerEventData, bool> filter = null)
            => BindableInternalUtility.Bind<PointerEventDispatcher>(scope, (d, h) => d.OnExit += h, (d, h) => d.OnExit -= h, onExit, filter);

        // Pointer状态监听
        public static EventBindableScope OnPointerEnable(this EventBindableScope scope, Action onEnable)
            => BindableInternalUtility.BindVoid<PointerEventDispatcher>(scope, (d, h) => d.OnEnableInteractable += h, (d, h) => d.OnEnableInteractable -= h, onEnable);

        public static EventBindableScope OnPointerDisable(this EventBindableScope scope, Action onDisable)
            => BindableInternalUtility.BindVoid<PointerEventDispatcher>(scope, (d, h) => d.OnDisableInteractable += h, (d, h) => d.OnDisableInteractable -= h, onDisable);

        // =========================================================================
        // IDrag 系列手势（按需挂载 DragEventDispatcher）
        // =========================================================================

        public static EventBindableScope OnBeginDrag(this EventBindableScope scope, Action<PointerEventData> onBeginDrag, Func<PointerEventData, bool> filter = null)
            => BindableInternalUtility.Bind<DragEventDispatcher>(scope, (d, h) => d.OnBeginDragAction += h, (d, h) => d.OnBeginDragAction -= h, onBeginDrag, filter);

        public static EventBindableScope OnDrag(this EventBindableScope scope, Action<PointerEventData> onDrag, Func<PointerEventData, bool> filter = null)
            => BindableInternalUtility.Bind<DragEventDispatcher>(scope, (d, h) => d.OnDragAction += h, (d, h) => d.OnDragAction -= h, onDrag, filter);

        public static EventBindableScope OnEndDrag(this EventBindableScope scope, Action<PointerEventData> onEndDrag, Func<PointerEventData, bool> filter = null)
            => BindableInternalUtility.Bind<DragEventDispatcher>(scope, (d, h) => d.OnEndDragAction += h, (d, h) => d.OnEndDragAction -= h, onEndDrag, filter);

        // Drag状态监听
        public static EventBindableScope OnDragEnable(this EventBindableScope scope, Action onEnable)
            => BindableInternalUtility.BindVoid<DragEventDispatcher>(scope, (d, h) => d.OnEnableInteractable += h, (d, h) => d.OnEnableInteractable -= h, onEnable);

        public static EventBindableScope OnDragDisable(this EventBindableScope scope, Action onDisable)
            => BindableInternalUtility.BindVoid<DragEventDispatcher>(scope, (d, h) => d.OnDisableInteractable += h, (d, h) => d.OnDisableInteractable -= h, onDisable);

        // =========================================================================
        // IDrop 系列手势（按需挂载 DropEventDispatcher）
        // =========================================================================
        public static EventBindableScope OnDrop(this EventBindableScope scope, Action<PointerEventData> onDrop, Func<PointerEventData, bool> filter = null)
            => BindableInternalUtility.Bind<DropEventDispatcher>(scope, (d, h) => d.OnDropAction += h, (d, h) => d.OnDropAction -= h, onDrop, filter);

        public static EventBindableScope OnDropEnable(this EventBindableScope scope, Action onEnable)
            => BindableInternalUtility.BindVoid<DropEventDispatcher>(scope, (d, h) => d.OnEnableInteractable += h, (d, h) => d.OnEnableInteractable -= h, onEnable);

        public static EventBindableScope OnDropDisable(this EventBindableScope scope, Action onDisable)
            => BindableInternalUtility.BindVoid<DropEventDispatcher>(scope, (d, h) => d.OnDisableInteractable += h, (d, h) => d.OnDisableInteractable -= h, onDisable);
    }

    // =========================================================================
    // 轻量级原生事件分发代理
    // =========================================================================
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public abstract class EventDispatcherBase : MonoBehaviour,
        IInteractableDispatcher,
        IEventBindable
    {
        public event Action OnEnableInteractable;
        public event Action OnDisableInteractable;

        public virtual bool HasAnyListeners { get; }

        private bool _interactable = true;

        public bool Interactable
        {
            get => _interactable && HasAnyListeners;
            set
            {
                if (_interactable == value) return;
                _interactable = value;

                UpdateActiveState();
            }
        }

        protected virtual void OnEnable()
        {
            OnEnableInteractable?.Invoke();
        }

        protected virtual void OnDisable()
        {
            OnDisableInteractable?.Invoke();
        }

        void IInteractableDispatcher.UpdateActiveState() => UpdateActiveState();
        private void UpdateActiveState()
        {
            bool shouldBeEnabled = _interactable && HasAnyListeners;

            if (this.enabled != shouldBeEnabled)
            {
                this.enabled = shouldBeEnabled;
            }
        }
    }

    public sealed class PointerEventDispatcher : EventDispatcherBase,
        IPointerClickHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerEnterHandler,
        IPointerExitHandler
    {
        public event Action<PointerEventData> OnClick;
        public event Action<PointerEventData> OnDown;
        public event Action<PointerEventData> OnUp;
        public event Action<PointerEventData> OnEnter;
        public event Action<PointerEventData> OnExit;

        public override bool HasAnyListeners => OnClick != null || OnDown != null || OnUp != null || OnEnter != null || OnExit != null;

        private bool _isHovered;
        private bool _isPressed;
        private PointerEventData _lastEventData;

        public void OnPointerClick(PointerEventData eventData) => OnClick?.Invoke(eventData);

        public void OnPointerDown(PointerEventData eventData)
        {
            _isPressed = true;
            _lastEventData = eventData;
            OnDown?.Invoke(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!_isPressed) return;
            _isPressed = false;
            OnUp?.Invoke(eventData);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _isHovered = true;
            _lastEventData = eventData;
            OnEnter?.Invoke(eventData);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_isHovered) return;
            _isHovered = false;
            OnExit?.Invoke(eventData);

            if (!_isPressed) _lastEventData = null;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            ForceReleaseStates();
        }

        private void ForceReleaseStates()
        {
            if (_isPressed)
            {
                _isPressed = false;
                OnUp?.Invoke(_lastEventData);
            }

            if (_isHovered)
            {
                _isHovered = false;
                OnExit?.Invoke(_lastEventData);
            }

            _lastEventData = null;
        }

        private void OnDestroy()
        {
            OnClick = null; OnDown = null; OnUp = null; OnEnter = null; OnExit = null;
            _lastEventData = null;
        }
    }
    // =========================================================================
    // 1. 拖拽发起代理（只负责被拖走，如：道具图标）
    // =========================================================================

    public sealed class DragEventDispatcher : EventDispatcherBase,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        public event Action<PointerEventData> OnBeginDragAction;
        public event Action<PointerEventData> OnDragAction;
        public event Action<PointerEventData> OnEndDragAction;

        public override bool HasAnyListeners => OnBeginDragAction != null || OnDragAction != null || OnEndDragAction != null;

        private bool _isDragging;
        private PointerEventData _lastEventData;

        public void OnBeginDrag(PointerEventData eventData)
        {
            _isDragging = true;
            _lastEventData = eventData;
            OnBeginDragAction?.Invoke(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_isDragging) return;
            _lastEventData = eventData;
            OnDragAction?.Invoke(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_isDragging) return;
            _isDragging = false;
            OnEndDragAction?.Invoke(eventData);
            _lastEventData = null;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            ForceReleaseStates();
        }

        private void ForceReleaseStates()
        {
            if (_isDragging)
            {
                _isDragging = false;
                OnEndDragAction?.Invoke(_lastEventData);
            }
            _lastEventData = null;
        }

        private void OnDestroy()
        {
            OnBeginDragAction = null; OnDragAction = null; OnEndDragAction = null;
            _lastEventData = null;
        }
    }

    // =========================================================================
    // 2. 放置接收代理（只负责接收别人放上来，如：背包格子、垃圾桶）
    // =========================================================================

    public sealed class DropEventDispatcher : EventDispatcherBase,
        IDropHandler
    {
        public event Action<PointerEventData> OnDropAction;

        public override bool HasAnyListeners => OnDropAction != null;

        public void OnDrop(PointerEventData eventData)
        {
            OnDropAction?.Invoke(eventData);
        }

        private void OnDestroy() => OnDropAction = null;
    }
    #endregion
    //!==============================================
    #region ButtonExtensions

    public static class ButtonExtensions
    {
        /// <summary>
        /// 为 Button 添加点击事件监听 ，可传入 CancellationToken 自动取消订阅
        /// 备注：池化对象传入 cancellationToken 并且手动调用 RemoveAllListeners 后会造成泄露
        /// </summary>
        public static void AddListener(this Button button, UnityAction action, CancellationToken cancellationToken = default)
        {
            if (button == null) throw new ArgumentNullException(nameof(button));
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (cancellationToken.IsCancellationRequested) return;

            button.onClick.AddListener(action);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    if (button != null)
                    {
                        button.onClick.RemoveListener(action);

                    }
                });
            }
        }

        /// <summary>
        /// 转发 onClick.RemoveAllListeners() 简化调用
        /// </summary>
        public static void RemoveAllListeners(this Button button) => button.onClick.RemoveAllListeners();

        /// <summary>
        /// 为 Button 添加带【动态冷却时间】的点击事件监听（冷却时间由回调函数决定）
        /// </summary>
        /// <param name="button">目标按钮</param>
        /// <param name="action">点击回调</param>
        /// <param name="cooldownProvider">动态返回冷却时间（秒）的委托</param>
        /// <param name="disableInteractable">是否在冷却中将按钮置灰</param>
        /// <param name="ignoreTimeScale">冷却是否忽略时间缩放（默认 true，与 UI 保持一致）</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        public static void AddCooldownListener(
            this Button button,
            UnityAction action,
            Func<float> cooldownProvider,
            bool disableInteractable = false,
            bool ignoreTimeScale = true,
            CancellationToken cancellationToken = default)
        {
            if (button == null) throw new ArgumentNullException(nameof(button));
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (cooldownProvider == null) throw new ArgumentNullException(nameof(cooldownProvider));
            if (cancellationToken.IsCancellationRequested) return;

            CancellationToken token = BindableInternalUtility.LinkTokenSafely(button.destroyCancellationToken, cancellationToken);
            UnityAction targetAction;

            if (!disableInteractable)
            {
                // 模式 A：轻量时间戳比对
                // 初值设为一个足够小的负数，保证第一次点击必然生效
                float lastClickTime = float.MinValue / 2;

                targetAction = () =>
                {
                    float cooldown = cooldownProvider();
                    if (cooldown <= 0f)
                    {
                        action.Invoke();
                        return;
                    }

                    float currentTime = ignoreTimeScale ? Time.unscaledTime : Time.time;
                    if (currentTime - lastClickTime >= cooldown)
                    {
                        lastClickTime = currentTime;
                        action.Invoke();
                    }
                };
            }
            else
            {
                // 模式 B：置灰模式
                targetAction = async () =>
                {
                    if (!button.interactable) return;

                    float cooldown = cooldownProvider();

                    // 如果动态计算出的冷却 <= 0，则不置灰直接执行
                    if (cooldown <= 0f)
                    {
                        action.Invoke();
                        return;
                    }

                    button.interactable = false;
                    try
                    {
                        action.Invoke();

                        // 统一时间缩放行为
                        await UniTask.Delay(
                            TimeSpan.FromSeconds(cooldown),
                            ignoreTimeScale: ignoreTimeScale,
                            cancellationToken: token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    finally
                    {
                        if (button != null)
                        {
                            button.interactable = true;
                        }
                    }
                };
            }

            button.onClick.AddListener(targetAction);

            token.Register(() =>
            {
                if (button != null)
                {
                    button.onClick.RemoveListener(targetAction);

                    if (disableInteractable && !button.interactable)
                    {
                        button.interactable = true;
                    }
                }
            });
        }

        /// <summary>
        /// 为 Button 添加带固定冷却时间的点击事件监听
        /// </summary>
        public static void AddCooldownListener(
            this Button button,
            UnityAction action,
            float cooldown,
            bool disableInteractable = false,
            bool ignoreTimeScale = true,
            CancellationToken cancellationToken = default)
        {
            // 如果 cooldown <= 0，退化为普通监听
            if (cooldown <= 0f)
            {
                button.AddListener(action, cancellationToken);
                return;
            }

            // 转调动态重载
            button.AddCooldownListener(
                action,
                () => cooldown,
                disableInteractable,
                ignoreTimeScale,
                cancellationToken);
        }

        /// <summary>
        /// 为 Button 添加带防重锁的异步点击事件监听
        /// </summary>
        /// <param name="button">目标按钮</param>
        /// <param name="asyncAction">异步回调，携带当前执行的 CancellationToken</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        /// <param name="disableInteractable">执行任务期间是否自动将 button.interactable 置为 false</param>
        public static void AddListenerAsync(
            this Button button,
            Func<CancellationToken, UniTask> asyncAction,
            CancellationToken cancellationToken = default,
            bool disableInteractable = false)
        {
            if (button == null) throw new ArgumentNullException(nameof(button));
            if (asyncAction == null) throw new ArgumentNullException(nameof(asyncAction));

            // 1. 如果一开始就已经取消，直接不绑定
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // 2. 合并外部 Token 和 Button 自身的销毁 Token
            var token = BindableInternalUtility.LinkTokenSafely(button.destroyCancellationToken, cancellationToken);

            // 3. 状态锁
            bool isRunning = false;

            // 4. 定义异步执行方法
            async UniTaskVoid OnButtonClickAsync()
            {
                // 上锁：正在执行中则忽略
                if (isRunning) return;

                isRunning = true;
                bool previousInteractable = button.interactable;
                if (disableInteractable)
                {
                    button.interactable = false;
                }

                try
                {
                    // 把 token 传入异步逻辑，方便内部耗时任务感知取消
                    await asyncAction(token);
                }
                catch (OperationCanceledException)
                {
                    // 静默忽略任务取消
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                }
                finally
                {
                    isRunning = false;
                    // 确保 button 没被 Destroy 时才恢复状态
                    if (button != null && disableInteractable)
                    {
                        button.interactable = previousInteractable;
                    }
                }
            }

            // 5. 将委托缓存为具名变量，以便后续精准注销
            UnityAction clickAction = () => OnButtonClickAsync().Forget();

            // 6. 注册点击事件
            button.onClick.AddListener(clickAction);

            // 取消时的清理
            token.Register(() =>
            {
                if (button != null)
                {
                    button.onClick.RemoveListener(clickAction);

                    if (disableInteractable && !button.interactable)
                    {
                        button.interactable = true;
                    }
                }
            });
        }

        /// <summary>
        /// 为 Button 添加多击/双击事件监听（在指定时间内连续点击达到 N 次才触发）
        /// </summary>
        /// <param name="button">目标按钮</param>
        /// <param name="action">达到多击要求后的回调</param>
        /// <param name="requiredClicks">需要连续点击的次数（如 2 为双击，5 为连续点击5次）</param>
        /// <param name="timeLimit">每次点击之间的最大允许时间间隔（秒），默认 0.3 秒</param>
        /// <param name="ignoreTimeScale">是否忽略时间缩放（默认 true，不受暂停影响）</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        public static void AddMultiClickListener(
            this Button button,
            UnityAction action,
            int requiredClicks = 2,
            float timeLimit = 0.3f,
            bool ignoreTimeScale = true,
            CancellationToken cancellationToken = default)
        {
            if (button == null) throw new ArgumentNullException(nameof(button));
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (requiredClicks <= 1)
            {
                // 只有 1 次需求，退化为普通监听
                button.AddListener(action, cancellationToken);
                return;
            }
            if (cancellationToken.IsCancellationRequested) return;

            var token = BindableInternalUtility.LinkTokenSafely(button.destroyCancellationToken, cancellationToken);

            // 状态记录（由闭包持有生命周期）
            int currentClicks = 0;
            float lastClickTime = 0f;

            UnityAction clickAction = () =>
            {
                float currentTime = ignoreTimeScale ? Time.unscaledTime : Time.time;

                // 核心逻辑：如果当前已有连击计数，且两次点击间隔在允许范围内，则连击数 +1；否则重新从第 1 次算起
                if (currentClicks > 0 && (currentTime - lastClickTime <= timeLimit))
                {
                    currentClicks++;
                }
                else
                {
                    currentClicks = 1;
                }

                lastClickTime = currentTime;

                // 达到目标连击数
                if (currentClicks >= requiredClicks)
                {
                    // 达成后重置计数器，防止第 3 次、第 4 次点击发生“黏连触发”
                    currentClicks = 0;
                    lastClickTime = 0f;

                    action.Invoke();
                }
            };

            button.onClick.AddListener(clickAction);

            // 取消时的清理
            token.Register(() =>
            {
                if (button != null)
                {
                    button.onClick.RemoveListener(clickAction);
                }
            });
        }
        // =========================================================================
        // 基于 AsEventBindable 扩展
        // =========================================================================
        /// <summary>
        /// 为 Button 添加右键点击事件监听
        /// </summary>
        /// <param name="button">目标按钮</param>
        /// <param name="action">右键点击回调</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        public static void AddRightClickListener(
            this Button button,
            UnityAction action,
            CancellationToken cancellationToken = default)
        {
            if (button == null) throw new ArgumentNullException(nameof(button));
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (cancellationToken.IsCancellationRequested) return;

            button.AsEventBindable(cancellationToken)
                .OnPointerClick(
                    _ => action.Invoke(),
                    filter: eventData => button.enabled && button.interactable && eventData.button == PointerEventData.InputButton.Right
                );
        }

        /// <summary>
        /// 为 Button 添加右键点击事件监听（携带 PointerEventData，常用于根据鼠标坐标弹出右键菜单）
        /// </summary>
        /// <param name="button">目标按钮</param>
        /// <param name="action">右键点击回调（携带 PointerEventData）</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        public static void AddRightClickListener(
            this Button button,
            Action<PointerEventData> action,
            CancellationToken cancellationToken = default)
        {
            if (button == null) throw new ArgumentNullException(nameof(button));
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (cancellationToken.IsCancellationRequested) return;

            button.AsEventBindable(cancellationToken)
                .OnPointerClick(
                    action,
                    filter: eventData => button.enabled && button.interactable && eventData.button == PointerEventData.InputButton.Right
                );
        }

        /// <summary>
        /// 为原生 Button 扩展长按监听
        /// </summary>
        /// <param name="button">目标按钮</param>
        /// <param name="onLongPress">长按触发回调</param>
        /// <param name="duration">按住多久触发长按（秒），默认 0.8s</param>
        /// <param name="continuous">是否持续触发（如按住连发）</param>
        /// <param name="continuousInterval">持续触发间隔（秒），仅在 continuous 为 true 时有效</param>
        /// <param name="ignoreTimeScale">是否忽略时间缩放</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        public static void AddLongPressListener(
            this Button button,
            UnityAction onLongPress,
            float duration = 0.8f,
            bool continuous = false,
            float continuousInterval = 0.1f,
            bool ignoreTimeScale = true,
            CancellationToken cancellationToken = default)
        {
            if (button == null) throw new ArgumentNullException(nameof(button));
            if (onLongPress == null) throw new ArgumentNullException(nameof(onLongPress));
            if (cancellationToken.IsCancellationRequested) return;

            // 1. 获取现有的 EventBindableScope（内部已自动处理销毁 Token 与外部 Token 的安全合并）
            var scope = button.AsEventBindable(cancellationToken);

            // 2. 状态管理
            CancellationTokenSource pressCts = null;

            void CancelCurrentPress()
            {
                if (pressCts != null)
                {
                    pressCts.Cancel();
                    pressCts.Dispose();
                    pressCts = null;
                }
            }

            // 3. 定义长按异步流程
            async UniTaskVoid StartLongPressAsync(CancellationToken linkedToken)
            {
                try
                {
                    // 等待达到长按阈值
                    await UniTask.Delay(TimeSpan.FromSeconds(duration), ignoreTimeScale: ignoreTimeScale, cancellationToken: linkedToken);

                    // 首次触发长按
                    onLongPress.Invoke();

                    // 持续连发逻辑
                    if (continuous)
                    {
                        while (!linkedToken.IsCancellationRequested)
                        {
                            await UniTask.Delay(TimeSpan.FromSeconds(continuousInterval), ignoreTimeScale: ignoreTimeScale, cancellationToken: linkedToken);
                            onLongPress.Invoke();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常松手、移出或物体销毁，静默退出
                }
            }

            // 4. 利用 AsEventBindable 现有的手势接口进行组合监听
            scope.OnPointerDown(eventData =>
                {
                    if (eventData.button != PointerEventData.InputButton.Left) return;
                    if (!button.interactable) return;

                    CancelCurrentPress();

                    pressCts = new CancellationTokenSource();
                    // 合并作用域 Token，保证物体被 Destroy 或外部 Cancel 时异步任务立即中断
                    var combinedToken = BindableInternalUtility.LinkTokenSafely(scope.cancellationToken, pressCts.Token);

                    StartLongPressAsync(combinedToken).Forget();
                })
                // 抬起：打断长按
                .OnPointerUp(eventData =>
                {
                    if (eventData.button == PointerEventData.InputButton.Left)
                    {
                        CancelCurrentPress();
                    }
                })
                // 移出：打断长按（手指/鼠标滑出按钮区域）
                .OnPointerExit(eventData =>
                {
                    CancelCurrentPress();
                });

            // 5. 作用域生命周期结束时，清理可能残留的 pressCts
            scope.cancellationToken.Register(CancelCurrentPress);
        }

    }

    #endregion

    //!========================================================
    #region TMP
    public static class TMPFadeTypewriterExtensions
    {
        // 纯 C# 上下文状态机
        private class TypewriterContext
        {
            public bool IsPlaying;
            public CancellationTokenSource PlayCts;
            public TMP_Text TargetText;

            public void Stop()
            {
                IsPlaying = false;
                if (PlayCts != null)
                {
                    PlayCts.Cancel();
                    PlayCts.Dispose();
                    PlayCts = null;
                }
            }
        }

        // 静态字典：存储所有 TMP 对应的打字状态
        private static readonly ConditionalWeakTable<TMP_Text, TypewriterContext> _contexts = new();

        /// <summary>
        /// ADV 风格文字透明渐变打字机
        /// </summary>
        /// <param name="textComponent">目标 TMP 组件</param>
        /// <param name="content">显示的台词文本</param>
        /// <param name="timeBetweenChars">出字间隔基础时长（秒）</param>
        /// <param name="fadeDuration">单字淡入过渡时长（秒）</param>
        /// <param name="punctuationDelayFunc">
        /// 可注入的标点延迟计算策略：参数1为当前字符，参数2为基础间隔(timeBetweenChars)，返回值为最终该字符等待的时长。
        /// </param>
        /// <param name="onCharacterShow">每个可见字符开始显现时的回调（用于音效等）</param>
        public static async UniTask FadeTypewriterAsync(
            this TMP_Text textComponent,
            string content,
            float timeBetweenChars = 0.04f,
            float fadeDuration = 0.2f,
            Func<char, float, float> punctuationDelayFunc = null,
            Action<char> onCharacterShow = null)
        {
            if (textComponent == null) return;

            var ctx = _contexts.GetOrCreateValue(textComponent);
            ctx.Stop();

            // 瞬间显示配置
            if (timeBetweenChars <= 0f)
            {
                textComponent.maxVisibleCharacters = int.MaxValue;
                textComponent.text = content;
                textComponent.ForceMeshUpdate();
                ResetAllMaterialsAlpha(textComponent, 255);
                return;
            }

            textComponent.maxVisibleCharacters = int.MaxValue;
            textComponent.text = content;
            textComponent.ForceMeshUpdate();

            TMP_TextInfo textInfo = textComponent.textInfo;
            int totalChars = textInfo.characterCount;
            if (totalChars == 0) return;

            ResetAllMaterialsAlpha(textComponent, 0);

            ctx.PlayCts = new CancellationTokenSource();
            var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(
                ctx.PlayCts.Token,
                textComponent.GetCancellationTokenOnDestroy()
            ).Token;

            ctx.IsPlaying = true;

            // 如果外部未注入自定义策略，采用默认策略
            var delayStrategy = punctuationDelayFunc ?? DefaultPunctuationDelay;

            try
            {
                for (int i = 0; i < totalChars; i++)
                {
                    linkedToken.ThrowIfCancellationRequested();

                    if (!textInfo.characterInfo[i].isVisible) continue;

                    char currentChar = textInfo.characterInfo[i].character;
                    onCharacterShow?.Invoke(currentChar);

                    // 启动单字淡入
                    FadeSingleCharAsync(textComponent, i, fadeDuration, linkedToken).Forget();

                    // 使用注入的计算策略获取停顿时间
                    float delay = delayStrategy(currentChar, timeBetweenChars);
                    if (delay > 0)
                    {
                        await UniTask.Delay(TimeSpan.FromSeconds(delay), cancellationToken: linkedToken);
                    }
                }

                if (fadeDuration > 0)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(fadeDuration), cancellationToken: linkedToken);
                }

                ctx.IsPlaying = false;
            }
            catch (OperationCanceledException)
            {
                ctx.IsPlaying = false;
            }
        }

        /// <summary>
        /// 状态查询：检查该文本组件当前是否正在打字中
        /// </summary>
        public static bool IsTypewriterPlaying(this TMP_Text textComponent)
        {
            if (textComponent == null) return false;
            return _contexts.TryGetValue(textComponent, out var ctx) && ctx.IsPlaying;
        }

        /// <summary>
        /// 瞬间完成当前打字机动画（跳过看全文）
        /// </summary>
        public static void CompleteTypewriter(this TMP_Text textComponent)
        {
            if (textComponent == null) return;
            if (_contexts.TryGetValue(textComponent, out var ctx))
            {
                if (!ctx.IsPlaying) return;
                ctx.Stop();
                textComponent.ForceMeshUpdate();
                ResetAllMaterialsAlpha(textComponent, 255);
            }
        }

        // ================== 底层辅助逻辑 ==================

        private static async UniTaskVoid FadeSingleCharAsync(TMP_Text textComponent, int charIndex, float duration, CancellationToken token)
        {
            if (duration <= 0f)
            {
                SetCharAlpha(textComponent, charIndex, 255);
                return;
            }

            float timer = 0f;
            while (timer < duration)
            {
                if (token.IsCancellationRequested) return;

                timer += Time.deltaTime;
                byte alpha = (byte)Mathf.Clamp((timer / duration) * 255, 0, 255);
                SetCharAlpha(textComponent, charIndex, alpha);

                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }

            SetCharAlpha(textComponent, charIndex, 255);
        }

        private static void SetCharAlpha(TMP_Text textComponent, int charIndex, byte alpha)
        {
            if (textComponent == null) return;
            TMP_TextInfo textInfo = textComponent.textInfo;

            int matIndex = textInfo.characterInfo[charIndex].materialReferenceIndex;
            int vertIndex = textInfo.characterInfo[charIndex].vertexIndex;
            Color32[] colors = textInfo.meshInfo[matIndex].colors32;

            for (int v = 0; v < 4; v++) colors[vertIndex + v].a = alpha;
            textComponent.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }

        private static void ResetAllMaterialsAlpha(TMP_Text textComponent, byte alpha)
        {
            TMP_TextInfo textInfo = textComponent.textInfo;
            for (int i = 0; i < textInfo.materialCount; i++)
            {
                var colors = textInfo.meshInfo[i].colors32;
                if (colors == null) continue;
                for (int v = 0; v < colors.Length; v++) colors[v].a = alpha;
            }
            textComponent.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }

        public static float DefaultPunctuationDelay(char c, float baseDelay)
        {
            if (baseDelay <= 0f) return 0f;

            return baseDelay;

            // switch (c)
            // {
            //     case '，': case ',': return baseDelay + 0.15f;
            //     case '。': case '！': case '？': case '.': case '!': case '?': return baseDelay + 0.25f;
            //     case '…': return baseDelay + 0.2f;
            //     default: return baseDelay;
            // }
        }
    }
    #endregion
    //!==============================================
    #region RectTransformExtensions
    public static class RectTransformExtensions
    {
        private static readonly Vector3[] s_Corners = new Vector3[4];

        /// <summary>
        /// 一行代码实现 UI 拖拽（天然保持鼠标偏移，全 Canvas 模式自适应）
        /// </summary>
        /// <param name="clampToScreen">是否限制在屏幕/安全区内</param>
        /// <param name="padding">距离屏幕边缘的内边距（像素）</param>
        public static void Drag(this RectTransform rectTransform, PointerEventData eventData, bool clampToScreen = false, float padding = 0f)
        {
            RectTransform parentRect = rectTransform.parent as RectTransform;
            if (parentRect == null) return;

            Camera eventCamera = GetCamera(rectTransform, eventData);

            // 1. 获取当前和上一帧鼠标在父节点下的局部坐标
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, eventData.position, eventCamera, out Vector2 currentPoint) &&
                RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, eventData.position - eventData.delta, eventCamera, out Vector2 prevPoint))
            {
                Vector2 localDelta = currentPoint - prevPoint;
                rectTransform.localPosition += (Vector3)localDelta;

                // 2. 限制在屏幕内
                if (clampToScreen)
                {
                    rectTransform.ClampToScreen(eventCamera, padding);
                }
            }
        }

        /// <summary>
        /// 在 OnDrag 中结合 offset 更新位置，并支持边缘限制
        /// </summary>
        public static void DragWithOffset(this RectTransform rectTransform, PointerEventData eventData, Vector2 offset, bool clampToScreen = false, float padding = 0f)
        {
            RectTransform parentRect = rectTransform.parent as RectTransform;
            if (parentRect == null) return;

            Camera eventCamera = GetCamera(rectTransform, eventData);

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, eventData.position, eventCamera, out Vector2 localPoint))
            {
                rectTransform.localPosition = localPoint + offset;

                if (clampToScreen)
                {
                    rectTransform.ClampToScreen(eventCamera, padding);
                }
            }
        }

        /// <summary>
        /// 强制将 RectTransform 约束在屏幕（或安全区）可视范围内
        /// </summary>
        public static void ClampToScreen(this RectTransform rectTransform, Camera eventCamera = null, float padding = 0f)
        {
            RectTransform parentRect = rectTransform.parent as RectTransform;
            if (parentRect == null) return;

            // 如果没有传摄像机，自动寻找对应模式的摄像机
            if (eventCamera == null)
            {
                eventCamera = GetCamera(rectTransform, null);
            }

            // 1. 获取物体在世界空间下的 4 个角 (0:左下, 1:左上, 2:右上, 3:右下)
            rectTransform.GetWorldCorners(s_Corners);

            // 2. 转换为屏幕坐标包围盒
            Vector2 minScreen = RectTransformUtility.WorldToScreenPoint(eventCamera, s_Corners[0]);
            Vector2 maxScreen = minScreen;

            for (int i = 1; i < 4; i++)
            {
                Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(eventCamera, s_Corners[i]);
                minScreen = Vector2.Min(minScreen, screenPoint);
                maxScreen = Vector2.Max(maxScreen, screenPoint);
            }

            // 3. 计算屏幕边界的偏移（如果需要支持刘海屏，可将 Screen.width/height 改为 Screen.safeArea）
            Vector2 screenCorrection = Vector2.zero;
            float minXLimit = padding;
            float maxXLimit = Screen.width - padding;
            float minYLimit = padding;
            float maxYLimit = Screen.height - padding;

            // X 轴修正 (先看右，再看左，如果UI本身比屏幕还宽，优先保证左边缘在屏幕内)
            if (maxScreen.x > maxXLimit) screenCorrection.x = maxXLimit - maxScreen.x;
            if (minScreen.x + screenCorrection.x < minXLimit) screenCorrection.x = minXLimit - minScreen.x;

            // Y 轴修正 (先看上，再看下)
            if (maxScreen.y > maxYLimit) screenCorrection.y = maxYLimit - maxScreen.y;
            if (minScreen.y + screenCorrection.y < minYLimit) screenCorrection.y = minYLimit - minScreen.y;

            // 4. 如果发生了溢出，将屏幕偏移量转换为父物体的 localPosition 进行修正
            if (screenCorrection != Vector2.zero)
            {
                Vector2 screenCenter = (minScreen + maxScreen) * 0.5f;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, screenCenter, eventCamera, out Vector2 localCenter) &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, screenCenter + screenCorrection, eventCamera, out Vector2 correctedLocalCenter))
                {
                    rectTransform.localPosition += (Vector3)(correctedLocalCenter - localCenter);
                }
            }
        }

        /// <summary>
        /// 获取当前 Canvas 正确的 Camera（Overlay 模式必须为 null）
        /// </summary>
        private static Camera GetCamera(RectTransform rectTransform, PointerEventData eventData)
        {
            Canvas canvas = rectTransform.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                return null; // Overlay 模式必须返回 null
            }

            return eventData?.pressEventCamera ?? canvas.worldCamera ?? Camera.main;
        }

        public static Vector2 GetDragOffset(this RectTransform rectTransform, PointerEventData eventData)
        {
            RectTransform parentRect = rectTransform.parent as RectTransform;
            if (parentRect != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parentRect, eventData.position, GetCamera(rectTransform, eventData), out Vector2 localPoint))
            {
                return (Vector2)rectTransform.localPosition - localPoint;
            }
            return Vector2.zero;
        }
    }
    #endregion
    //!==============================================
    #region Transform3D
    public static class Transform3DExtensions
    {
        // 模式 1：纯平面拖拽专用缓存
        private static Plane s_PurePlane;
        private static Vector3 s_PurePlaneOffset;

        // 模式 2：自由浮空与边界约束专用缓存
        private static Plane s_CamPlane;
        private static Plane s_BarrierPlane;
        private static Vector3 s_CamOffset;

        /// <summary>
        /// 开始 3D 拖拽
        /// </summary>
        /// <param name="planeNormal">
        /// 平面法线：
        /// - minDistance 为 null 时：物体滑动的水平面法线（默认 Vector3.up）
        /// - minDistance 有值时：障碍表面（如地面/墙面）的法线
        /// </param>
        /// <param name="planePoint">
        /// 平面基准点（仅在 minDistance 有值时生效，代表地面/墙面所在的位置，默认为 Vector3.zero）
        /// </param>
        public static void OnBeginDrag3D(
            this Transform transform,
            PointerEventData eventData,
            Vector3? planeNormal = null,
            Vector3? planePoint = null,
            Camera eventCamera = null)
        {
            Camera cam = ResolveCamera(eventData, eventCamera);
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(eventData.position);
            Vector3 normal = planeNormal.HasValue ? planeNormal.Value.normalized : Vector3.up;

            // =========================================================================
            // 准备【模式 1：纯平面滑行】所需的平面与偏移
            // 平面必须严格穿过物体当前位置 transform.position，保证原有高度 offset 绝不丢失
            // =========================================================================
            s_PurePlane = new Plane(normal, transform.position);
            if (s_PurePlane.Raycast(ray, out float enterPure))
            {
                s_PurePlaneOffset = transform.position - ray.GetPoint(enterPure);
            }
            else
            {
                s_PurePlaneOffset = Vector3.zero;
            }

            // =========================================================================
            // 准备【模式 2：自由浮空+边界阻挡】所需的平面与偏移
            // =========================================================================
            // 1. 视线平面（相机朝向，穿过物体当前位置）
            s_CamPlane = new Plane(-cam.transform.forward, transform.position);
            if (s_CamPlane.Raycast(ray, out float enterCam))
            {
                s_CamOffset = transform.position - ray.GetPoint(enterCam);
            }
            else
            {
                s_CamOffset = Vector3.zero;
            }

            // 2. 障碍表面（如地表），穿过指定的 planePoint
            Vector3 barrierPoint = planePoint.HasValue ? planePoint.Value : Vector3.zero;
            s_BarrierPlane = new Plane(normal, barrierPoint);
        }

        /// <summary>
        /// 3D 拖拽更新
        /// </summary>
        /// <param name="minDistance">
        /// - 传 null：【模式 1】纯平面滑动，保持原高度 1:1 跟手移动，绝不陷地
        /// - 传数值：【模式 2】自由浮空拖拽，一旦触碰表面则平滑阻挡（0 跳变）
        /// </param>
        public static void OnDrag3D(
            this Transform transform,
            PointerEventData eventData,
            float? minDistance = null,
            Bounds? clampBounds = null,
            Camera eventCamera = null)
        {
            Camera cam = ResolveCamera(eventData, eventCamera);
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(eventData.position);
            Vector3 targetPos;

            // =========================================================================
            // 轨道 1：未设置 minDistance -> 纯平面精准滑行（推箱子/下棋/平移）
            // =========================================================================
            if (!minDistance.HasValue)
            {
                // 直接与目标平面相交，1:1 跟手，保留所有高度 Offset
                if (s_PurePlane.Raycast(ray, out float enter))
                {
                    targetPos = ray.GetPoint(enter) + s_PurePlaneOffset;
                    ApplyBoundsAndSetPos(transform, targetPos, clampBounds);
                }
                return;
            }

            // =========================================================================
            // 轨道 2：设置了 minDistance -> 自由抓取升降 + 接触表面无缝正交投影（零跳变）
            // =========================================================================
            if (s_CamPlane.Raycast(ray, out float enterCam))
            {
                Vector3 airPos = ray.GetPoint(enterCam) + s_CamOffset;

                // 计算当前物体距离障碍平面的有向垂直距离
                float currentDist = s_BarrierPlane.GetDistanceToPoint(airPos);

                // 触碰或穿透表面时，沿法线推回，形成零突变的顺滑阻挡
                if (currentDist < minDistance.Value)
                {
                    airPos += s_BarrierPlane.normal * (minDistance.Value - currentDist);
                }

                targetPos = airPos;
                ApplyBoundsAndSetPos(transform, targetPos, clampBounds);
            }
        }

        private static void ApplyBoundsAndSetPos(Transform transform, Vector3 targetPos, Bounds? clampBounds)
        {
            if (clampBounds.HasValue) targetPos = clampBounds.Value.ClosestPoint(targetPos);
            transform.position = targetPos;
        }

        private static Camera ResolveCamera(PointerEventData eventData, Camera customCam)
        {
            if (customCam != null) return customCam;
            if (eventData.pressEventCamera != null) return eventData.pressEventCamera;
            return Camera.main;
        }
    }
    #endregion
}
