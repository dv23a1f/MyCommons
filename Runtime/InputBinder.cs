using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace DX.Core.Input
{
    public interface IInputService : IInputBinder
    {
        public const string MapNone = "[None]";
        public const string MapAll = "[All]";

        /// <summary>
        /// 当前生效的 ActionMap 名称
        /// </summary>
        string CurrentActionMap { get; }

        /// <summary>
        /// 切换 ActionMap。传 MapNone 禁用所有，传 MapAll 启用所有。
        /// </summary>
        void SwitchActionMap(string mapName);

        /// <summary>
        /// 直接读取当前帧的输入值（如 Vector2 移动向量、float 扳机值）
        /// </summary>
        T ReadValue<T>(string actionName) where T : struct;

        /// <summary>
        /// 判断当前按键是否正处于按下/激活状态（适合在 Update 中判断冲刺、开镜等）
        /// </summary>
        bool IsPressed(string actionName);

        /// <summary>
        /// 获取原生 InputAction
        /// </summary>
        InputAction GetAction(string actionName);
    }

    public interface IInputBinder
    {
        /// <summary>
        ///  监听该 Action 的所有阶段（started / performed / canceled）。
        /// </summary>
        void Bind(string actionName, Action<InputAction.CallbackContext> callback, CancellationToken ct = default);

        /// <summary>
        /// 监听持续输入（如移动 Vector2、滚轮 float）。监听 performed/Canceled。
        /// </summary>
        void BindValue<T>(string actionName, Action<T> onValueChanged, CancellationToken ct = default) where T : struct;

        /// <summary>
        /// 单次点击（如：跳跃、开火、交互）。仅监听 performed。
        /// </summary>
        void BindButton(string actionName, Action onPerformed, CancellationToken ct = default);

        /// <summary>
        /// 单次触发动作，同时携带当前绑定的数值 。仅监听 performed。
        /// </summary>
        void BindButton<T>(string actionName, Action<T> onPerformed, CancellationToken ct = default) where T : struct;

        /// <summary>
        /// 持续按住/松开（如：瞄准、格挡、蓄力、冲刺跑）。监听 started (按下) 和 canceled (松开)。
        /// 适用于未添加复杂 Interaction（如 Hold/Tap）的普通 Button/PassThrough Action。
        /// </summary>
        void BindHold(string actionName, Action onStarted = null, Action onCanceled = null, CancellationToken ct = default);
    }

    public class InputBinder : MonoBehaviour, IInputService
    {
        [SerializeField] private InputActionAsset _assetTemplate;
        [SerializeField] private bool _setAsGlobalDefault = true;

        [SerializeField] private string _defaultActionMap = IInputService.MapAll;
        public string CurrentActionMap { get; private set; } = IInputService.MapNone;

        private InputActionAsset _runtimeActions;
        private readonly Dictionary<string, InputAction> _actionCache = new();
        bool _initialized;

        /// <summary>
        /// 【静态工厂方法】创建一个 GameObject 并挂载组件。可指定 setAsGlobal 注册为单例模式
        /// </summary>
        public static IInputService Create(InputActionAsset asset, bool setAsGlobal = true)
        {
            if (asset == null) throw new NullReferenceException("InputActionAsset 为 null");
            if (setAsGlobal && GameInput.Binder != null)
            {
                Debug.LogWarning("已存在全局 InputBinder 单例");
                return GameInput.Binder;
            }

            GameObject go = new(nameof(InputBinder));

            // 先禁用防止自动执行 Initialize
            go.SetActive(false);

            var binder = go.AddComponent<InputBinder>();
            binder._assetTemplate = asset;

            if (setAsGlobal)
            {
                binder._setAsGlobalDefault = setAsGlobal;
                GameObject.DontDestroyOnLoad(go);
            }

            binder.Initialize();
            go.SetActive(true);

            return binder;
        }

        private void Awake()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (_initialized) return;

            if (_assetTemplate == null)
            {
                Debug.LogWarning($"[{nameof(InputBinder)}] 未指定 InputActionAsset！", this);
                return;
            }
            // 注册为全局组件
            if (_setAsGlobalDefault)
            {
                if (GameInput.Binder == null)
                {
                    GameInput.Binder = this;
                    this.transform.SetParent(null);
                    this.gameObject.name = $"[Global] {this.gameObject.name}";
                    DontDestroyOnLoad(this.gameObject);
                }
                else
                {
                    Destroy(this.gameObject);
                }
            }

            _runtimeActions = Instantiate(_assetTemplate);
            SwitchActionMap(_defaultActionMap);
            _initialized = true;
        }

        private void OnEnable()
        {
            SwitchActionMap(CurrentActionMap);
        }

        private void OnDisable()
        {
            SwitchActionMap(IInputService.MapNone);
        }

        private void OnDestroy()
        {
            if (object.ReferenceEquals(GameInput.Binder, this))
            {
                GameInput.Binder = null;
            }
            if (_runtimeActions != null)
            {
                Destroy(_runtimeActions);
                _actionCache.Clear();
            }
        }

        // ===========================
        //  API
        // ===========================
        public void SwitchActionMap(string mapName)
        {
            if (_runtimeActions == null) return;
            if (mapName == CurrentActionMap) return;

            // 1. 处理 None：全部禁用（过场动画、暂停）
            if (mapName == IInputService.MapNone || string.IsNullOrEmpty(mapName))
            {
                _runtimeActions.Disable();
                CurrentActionMap = IInputService.MapNone;
                Debug.Log($"[{nameof(InputBinder)}] ActionMap 已全部禁用。", this);
                return;
            }

            // 2. 处理 All：全部启用
            if (mapName == IInputService.MapAll)
            {
                _runtimeActions.Enable();
                CurrentActionMap = IInputService.MapAll;
                Debug.Log($"[{nameof(InputBinder)}] ActionMap 已全部启用。", this);
                return;
            }

            // 3. 处理具体指定的 Map（如 "Player" 或 "UI"）
            var targetMap = _runtimeActions.FindActionMap(mapName);
            if (targetMap == null)
            {
                Debug.LogWarning($"[{nameof(InputBinder)}] 找不到名为 \"{mapName}\" 的 ActionMap！", this);
                return;
            }

            // 先禁用全部，再单独激活目标 Map
            _runtimeActions.Disable();
            targetMap.Enable();
            CurrentActionMap = targetMap.name;
            Debug.Log($"[{nameof(InputBinder)}] ActionMap 已切换为: \"{CurrentActionMap}\"。", this);
        }

        /// <summary>
        /// 最基础的绑定：监听该 Action 的所有阶段（started / performed / canceled）。
        /// 适合需要完整 CallbackContext 进行复杂逻辑判断的场景。
        /// </summary>
        public void Bind(
            string actionName,
            Action<InputAction.CallbackContext> callback,
            CancellationToken ct = default)
        {
            if (callback == null) return;

            BindInternal(
                actionName,
                onStarted: callback,
                onPerformed: callback,
                onCanceled: callback,
                ct: ct);
        }

        /// <summary>
        /// 专用于持续输入（如移动 Vector2、滚轮 float）。
        /// 推摇杆时返回实际输入，松开回正时自动调用并返回默认值 (如 Vector2.zero)。
        /// </summary>
        public void BindValue<T>(
            string actionName,
            Action<T> onValueChanged,
            CancellationToken ct = default) where T : struct
        {
            if (onValueChanged == null) return;

            // 持续输入全阶段都需要读取：松开 canceled 时 ReadValue 会自动返回 default(T)
            Action<InputAction.CallbackContext> callback = ctx => onValueChanged(ctx.ReadValue<T>());

            BindInternal(
                actionName,
                onPerformed: callback,
                onCanceled: callback,
                ct: ct);
        }

        /// <summary>
        /// 专用于普通单次点击按键（如跳跃、交互、开火）。
        /// 仅精确监听 performed 阶段，避免 started/canceled 无效触发。
        /// </summary>
        public void BindButton(
            string actionName,
            Action onPerformed,
            CancellationToken ct = default)
        {
            if (onPerformed == null) return;

            // 只监听 performed
            BindInternal(
                actionName,
                onPerformed: _ => onPerformed(),
                ct: ct);
        }

        public void BindButton<T>(
            string actionName,
            Action<T> onPerformed,
            CancellationToken ct = default) where T : struct
        {
            if (onPerformed == null) return;

            // 只监听 performed，并在此时读取 T 类型的数据
            BindInternal(
                actionName,
                onPerformed: ctx => onPerformed(ctx.ReadValue<T>()),
                ct: ct);
        }

        /// <summary>
        /// 专用于按住类按键（瞄准、格挡、蓄力等），支持分别监听“按下”与“松开”。
        /// </summary>
        public void BindHold(
            string actionName,
            Action onDown = null,
            Action onUp = null,
            CancellationToken ct = default)
        {
            if (onDown == null && onUp == null) return;

            Action<InputAction.CallbackContext> startedCallback = onDown != null ? _ => onDown() : null;
            Action<InputAction.CallbackContext> canceledCallback = onUp != null ? _ => onUp() : null;

            BindInternal(
                actionName,
                onStarted: startedCallback,
                onCanceled: canceledCallback,
                ct: ct);
        }
        /// <summary>
        /// 核心私有绑定方法：按需订阅各阶段，并集中管理注销逻辑。
        /// 哪一个阶段传了回调就监听哪一个，不传则不订阅。
        /// </summary>
        private void BindInternal(
            string actionName,
            Action<InputAction.CallbackContext> onStarted = null,
            Action<InputAction.CallbackContext> onPerformed = null,
            Action<InputAction.CallbackContext> onCanceled = null,
            CancellationToken ct = default)
        {
            //未初始化时自动初始化 防止脚本执行时序导致的bug
            Initialize();

            if (ct.IsCancellationRequested) return;

            InputAction action = FindAction(actionName);
            if (action == null) return;

            // 按需挂载
            if (onStarted != null) action.started += onStarted;
            if (onPerformed != null) action.performed += onPerformed;
            if (onCanceled != null) action.canceled += onCanceled;

            // 通过 CancellationToken 精准按需解绑
            if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    if (action == null) return;
                    if (onStarted != null) action.started -= onStarted;
                    if (onPerformed != null) action.performed -= onPerformed;
                    if (onCanceled != null) action.canceled -= onCanceled;
                });
            }
        }

        // ===========================
        // 轮询类 API
        // ===========================
        /// <summary>
        /// 直接读取当前值。若未找到 Action 则返回 default。
        /// </summary>
        public T ReadValue<T>(string actionName) where T : struct
        {
            var action = FindAction(actionName);
            return action != null ? action.ReadValue<T>() : default;
        }

        /// <summary>
        /// 按键是否正被按住。
        /// </summary>
        public bool IsPressed(string actionName)
        {
            var action = FindAction(actionName);
            return action != null && action.IsPressed();
        }

        /// <summary>
        /// 获取底层原生 Action。调用方可以在 Start/Awake 中直接缓存该引用。
        /// </summary>
        public InputAction GetAction(string actionName)
        {
            return FindAction(actionName);
        }

        private InputAction FindAction(string actionName)
        {
            if (string.IsNullOrEmpty(actionName)) return null;

            if (_actionCache.TryGetValue(actionName, out var action))
            {
                return action;
            }

            if (_runtimeActions == null)
            {
                Debug.LogError($"[{nameof(InputBinder)}] Runtime Actions 为空！", this);
                return null;
            }

            action = _runtimeActions.FindAction(actionName);
            if (action == null)
            {
                Debug.LogWarning($"[{nameof(InputBinder)}] 找不到 Action: \"{actionName}\"", this);
                return null;
            }

            _actionCache[actionName] = action;
            return action;
        }
    }

    /// <summary>
    /// 全局输入门面：
    /// 1. 提供针对 InputBinder 的便捷静态调用（无需手动 ServiceLocator.Get）
    /// 2. 提供直接读取硬件设备（键盘/鼠标）的工具方法
    /// </summary>
    public static class GameInput
    {
        public static IInputService Binder { get; internal set; }

        public static Vector2 PointerPosition => Pointer.current?.position.ReadValue() ?? Vector2.zero;

        // 鼠标/触屏检测
        public static bool GetPointerDown() => Pointer.current != null && Pointer.current.press.wasPressedThisFrame;
        public static bool IsPointerPressed() => Pointer.current != null && Pointer.current.press.isPressed;
        public static bool GetPointerUp() => Pointer.current != null && Pointer.current.press.wasReleasedThisFrame;

        // 键盘枚举检测
        public static bool GetKeyDown(Key key) => Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
        public static bool IsKeyPressed(Key key) => Keyboard.current != null && Keyboard.current[key].isPressed;
        public static bool GetKeyUp(Key key) => Keyboard.current != null && Keyboard.current[key].wasReleasedThisFrame;

        // 键盘字符串检测
        public static bool GetKeyDown(string keyName) => Keyboard.current != null && Keyboard.current[keyName] is ButtonControl button && button.wasPressedThisFrame;
        public static bool IsKeyPressed(string keyName) => Keyboard.current != null && Keyboard.current[keyName] is ButtonControl button && button.isPressed;
        public static bool GetKeyUp(string keyName) => Keyboard.current != null && Keyboard.current[keyName] is ButtonControl button && button.wasReleasedThisFrame;
    }

    public static class InputExtensions
    {

        public static InputBindingScope AsInput(this MonoBehaviour mono, CancellationToken customToken = default)
        {
            return mono.AsInput(GameInput.Binder, customToken);
        }

        public static InputBindingScope AsInput(this MonoBehaviour mono, IInputBinder binder, CancellationToken customToken = default)
        {

            if (binder == null)
            {
                throw new ArgumentNullException(
                    nameof(binder),
                    $"[{nameof(InputExtensions)}] 传入的 {nameof(IInputBinder)} 为空！调用者: {mono.name}");
            }

            CancellationToken effectiveToken;
            if (!customToken.CanBeCanceled)
            {
                effectiveToken = mono.destroyCancellationToken;
            }
            else
            {
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    mono.destroyCancellationToken,
                    customToken);

                linkedCts.Token.Register(static state =>
                {
                    if (state is CancellationTokenSource cts)
                    {
                        cts.Dispose();
                    }
                }, linkedCts);

                effectiveToken = linkedCts.Token;
            }

            return new InputBindingScope(binder, effectiveToken);
        }
    }

    /// <summary>
    /// 纯栈上分配的结构体，链式调用产生 0 GC Alloc
    /// </summary>
    public readonly struct InputBindingScope
    {
        private readonly IInputBinder _binder;
        private readonly CancellationToken _ct;

        public InputBindingScope(IInputBinder binder, CancellationToken ct)
        {
            _binder = binder;
            _ct = ct;
        }

        /// <summary>
        /// 监听单次点击（performed）
        /// </summary>
        public readonly InputBindingScope BindButton(string actionName, Action onPerformed)
        {
            _binder.BindButton(actionName, onPerformed, _ct);
            return this;
        }

        /// <summary>
        /// 监听按住与松开（started / canceled）
        /// </summary>
        public readonly InputBindingScope BindHold(string actionName, Action onDown = null, Action onUp = null)
        {
            _binder.BindHold(actionName, onDown, onUp, _ct);
            return this;
        }

        /// <summary>
        /// 监听连续值（如 Vector2 移动摇杆）
        /// </summary>
        public readonly InputBindingScope BindValue<T>(string actionName, Action<T> onValueChanged) where T : struct
        {
            _binder.BindValue(actionName, onValueChanged, _ct);
            return this;
        }

        /// <summary>
        /// 监听完整阶段（started / performed / canceled）
        /// </summary>
        public readonly InputBindingScope Bind(string actionName, Action<InputAction.CallbackContext> callback)
        {
            _binder.Bind(actionName, callback, _ct);
            return this;
        }
    }
}