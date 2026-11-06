using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.Assertions;

namespace Framework.ReactiveStateMachine
{
    //============================================
    // 强类型声明式/响应式状态管理框架: Unity-RxFSM (支持多种驱动模式)
    //============================================

    //!============= [Attribute] =============
    #region Attribute
    /// <summary>
    /// [Payload] 标记该字段用于接收状态切换时传递的上下文数据。进入状态前会自动注入 (类型不匹配/无参切换重置为默认)(覆盖 [AutoLink])
    /// 备注: 不匹配的类型会被视为脏数据 这是预期的 避免用于多个同类型，应改用结构体 (应仅用于值类型/初始值为null的引用类型)
    /// </summary>
    [RequireAttributeUsages, Preserve]
    [AttributeUsage(AttributeTargets.Field)]
    public class PayloadAttribute : PreserveAttribute { }

    /// <summary>
    /// [AutoReset] 标记该字段在状态退出(OnExit)后，自动利用反射重置为初始值 (应仅用于值类型的清理/或初始值必须为null的引用类型)
    /// 备注：兼容 SharedVar (重置Value为初始值)
    /// </summary>
    [RequireAttributeUsages, Preserve]
    [AttributeUsage(AttributeTargets.Field)]
    public class AutoResetAttribute : PreserveAttribute { }

    /// <summary>
    /// [AutoLink] 标记该字段在状态退出时自动打快照，并在进入下一个拥有同名/同键字段的状态时自动恢复数据。
    /// 备注: 支持跨状态透传 (A->B->C, 即使B没有该字段，C也能接收到A留下的快照)
    /// 备注: 兼容 [Payload]， 同时添加 [AutoLink] [Payload] 标签可以保活数据防止被 Payload 重置（无参的情况下）
    /// 备注: 兼容 [AutoReset]，同时添加 [AutoLink] [AutoReset]  A->B->C (状态 C 标记 [AutoReset] 后续被重置，可将引用重置为null)
    /// </summary>
    [RequireAttributeUsages, Preserve]
    [AttributeUsage(AttributeTargets.Field)]
    public class AutoLinkAttribute : PreserveAttribute
    {
        public string Key { get; }
        /// <param name="key">自定义匹配键。留空则默认使用 "变量类型_变量名" 进行精确匹配</param>
        public AutoLinkAttribute(string key = null) { Key = key; }
    }

    /// <summary>
    /// [AutoInject] 标记该字段用于自动注入状态机中注册的上下文(Context) (每次OnEnter之前)(最高优先级)(覆盖其他)
    /// </summary>
    [RequireAttributeUsages, Preserve]
    [AttributeUsage(AttributeTargets.Field)]
    public class AutoInjectAttribute : PreserveAttribute { }

    /// <summary>
    /// [AutoBind] 自动绑定宿主(Owner)中的同名(或指定名称)字段的组件（或者通过 [AutoBind("宿主变量名")] 指定）(自动兜底查找)
    /// </summary>
    [RequireAttributeUsages, Preserve]
    [AttributeUsage(AttributeTargets.Field)]
    public class AutoBindAttribute : PreserveAttribute
    {
        public string OwnerFieldName { get; }
        public AutoBindAttribute(string ownerFieldName = null) { OwnerFieldName = ownerFieldName; }
    }

    /// <summary> AutoHandle 执行时机</summary>
    public enum HandleTiming
    {
        /// <summary>在当前状态 / 旧状态 OnExit 前执行 (无论是否切换状态永远执行)</summary>
        BeforeTransition,
        /// <summary>仅在新状态 OnEnter 后执行一次 (未发生切换时忽略该事件)</summary>
        AfterTransition,
    }

    /// <summary>
    /// [AutoHandle] 标记该方法用于自动注册为状态内部的指令接收器，接收 Dispatch 或子状态机冒泡的 Publish。
    /// 可使用 HandleTiming 配置执行时机：退出旧状态前/进入新状态后。默认 BeforeTransition
    /// 备注：支持 void 返回值（默认不消费）或 bool 返回值（根据返回值动态决定是否消费）
    /// 备注：支持 C# 8 默认接口方法（DIM）
    /// </summary>
    [RequireAttributeUsages]
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public class AutoHandleAttribute : Attribute
    {
        public HandleTiming Timing { get; }
        public AutoHandleAttribute(HandleTiming timing = HandleTiming.BeforeTransition)
        {
            Timing = timing;
        }
    }

    /// <summary>AutoRun 执行时机</summary>
    public enum Timing
    {
        /// <summary>在PreEnter(内部方法)执行后，OnEnter执行前</summary>
        BeforeEnter,
        /// <summary>在OnEnter执行后</summary>
        AfterEnter,
        /// <summary>在OnExit执行前</summary>
        BeforeExit,
        /// <summary>在OnEnter执行后，PostExit(内部方法)执行前</summary>
        AfterExit,

        /// <summary>在每帧Update轮询前</summary>
        BeforeUpdate,
        /// <summary>在每帧Update轮询后</summary>
        AfterUpdate,
        /// <summary>在每帧FixedUpdate轮询前</summary>
        BeforeFixedUpdate,
        /// <summary>在每帧FixedUpdate轮询后</summary>
        AfterFixedUpdate,
        /// <summary>在每帧PostUpdate轮询前</summary>
        BeforePostUpdate,
        /// <summary>在每帧PostUpdate轮询后</summary>
        AfterPostUpdate
    }

    /// <summary>
    /// [AutoRun] 在基类/接口/状态类中标记后，自动在该生命周期前后执行 (默认OnEnter前，同时机可用order排序)
    /// 备注：支持 C# 8 默认接口方法（DIM）用于组合前置的通用方法
    /// </summary>
    [RequireAttributeUsages]
    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public class AutoRunAttribute : Attribute
    {
        public Timing Timing { get; }
        public int Order { get; } // 加入优先级控制，数字越小越先执行

        public AutoRunAttribute(Timing timing = Timing.BeforeEnter, int order = 0)
        {
            Timing = timing;
            Order = order;
        }
    }

    /// <summary>分组动态路由的选中策略</summary>
    public enum GroupSelectionMode
    {
        /// <summary>选择组内 [StateWeight] 权重最高的那个状态</summary>
        HighestWeight,
        /// <summary>完全随机选择组内的一个状态（无视权重）</summary>
        RandomUniform,
        /// <summary>基于 [StateWeight] 的权重进行加权随机分配</summary>
        RandomWeighted
    }

    /// <summary>
    /// [StateWeight] 用于标记状态类的权重或优先级 (默认1)。IL2CPP下可代替 [Preserve] 进行保活
    /// 搭配 ToGroup() 动态路由时使用。数值越大，被随机到的概率越高，或作为最高优先级被选中。
    /// </summary>
    [RequireAttributeUsages]
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class StateWeightAttribute : Attribute
    {
        public int Weight { get; }
        public StateWeightAttribute(int weight = 1) { Weight = weight; }
    }

    #endregion
    //!============= [接口] =============
    #region interface
    /// <summary>供状态机持有的接口，供State调用以解耦泛型(对状态类显示，对外隐藏)</summary>
    internal interface IStateMachine
        : IStateMachineInfo, IStateContextResolver, IMachineLogProvider
    {
        Type NextStateType { get; }

        bool CanTransitionTo<T>() where T : IStateNode;
        bool CanEventTransition<TEvent>(TEvent eventData = default);
        bool IsNextState<T>() where T : class;
        bool IsNextStateOf<T>() where T : class;
        bool ChangeState(Type stateType, bool forceReenter);
        bool ChangeState(Type stateType, bool forceReenter, object payloadData = null);
        bool RevertToPreviousState(object data = null, bool ignoreTransitionRule = true, Type skipGroupType = null);
        TVar GetRawSharedVariable<TVar>() where TVar : class, ISharedVar;

        // 供状态类内部向外派发自定义事件
        bool Broadcast<TEvent>(TEvent eventData);
        bool PublishStateEvent<TEvent>(TEvent eventData);
        bool CrossTalk<TEvent>(TEvent eventData);
        bool DispatchToChildren<TEvent>(TEvent eventData);
    }

    /// <summary>框架强制约定的接口，仅供内部生命周期流转使用(对状态类隐藏)</summary>
    public interface IStateNode
    {
        bool IsDisposed { get; }
        bool HasInitialized { get; }
        bool SkipEnterOnRollback { get; }

        /// <summary>供状态机注入依赖</summary>
        internal bool SetStateMachine(IStateMachine machine);
        /// <summary>供状态机注入宿主对象</summary>
        bool SetOwner(object owner);

        // === 外部不可直接调用 ===
        void OnInit();  //实例化时仅执行一次
        void PreEnter(); // 激活 _isActive 
        void OnResume();
        void OnEnter();
        void OnUpdate();
        void OnFixedUpdate();
        void OnPostUpdate();
        void OnExit();
        void PostExit(); // 关闭 _isActive 
        void OnDispose();
        bool OnError(Exception ex);
        bool ReceiveDispatch<TPayload>(TPayload payload);
    }

    /// <summary>
    /// 状态特征/能力接口。供 C# 8.0 默认接口方法(DIM) 继承使用。
    /// 继承后可在接口内部访问状态机的核心能力
    /// 备注: 支持 [AutoRun] [AutoHandle]
    /// </summary>
    public interface IStateAbility<TContext> where TContext : class
    {
        /// <summary>宿主对象</summary>
        TContext Owner { get; }
        /// <summary>查询当前状态是否可以行动</summary>
        bool CanAct { get; }

        /// <summary>当前状态的取消令牌，使用异步方法需传入</summary>
        CancellationToken StateToken { get; }
        /// <summary>当前状态停留时间</summary>
        float TimeInState { get; }

        /// <summary>获取强类型共享变量 (状态机网络共享单例)</summary>
        TVar GetSharedVar<TVar>() where TVar : class, ISharedVar;
        /// <summary>获取已注册的上下文/组件</summary>
        bool TryGetContext<T>(out T context) where T : class;
        /// <summary>正常切换状态</summary>
        bool ChangeState<T>(bool forceReenter = false) where T : IStateNode;
        /// <summary>切换状态并传递数据 (Payload)</summary>
        bool ChangeState<T>(object payload, bool forceReenter = false) where T : IStateNode;
        /// <summary>回退到上一个状态</summary>
        bool RevertToPreviousState(object payload = null, bool ignoreTransitionRule = true, Type skipGroupType = null);
        /// <summary>向外派发事件并冒泡</summary>
        bool Publish<TEvent>(TEvent eventData = default);
        /// <summary>直接向兄弟状态机发送事件 (不冒泡)</summary>
        bool CrossTalk<TEvent>(TEvent eventData = default);
        /// <summary>向子状态机下发指令</summary>
        bool DispatchToChildren<TEvent>(TEvent eventData = default);
        /// <summary>向全网广播事件</summary>
        bool Broadcast<TEvent>(TEvent eventData = default);

        /// <summary>查询上一个状态的群组</summary>
        bool WasStateOf<T>() where T : class;
        /// <summary>中查询即将切换的群组 (使用 AutoRun 在 OnExit 中调用)</summary>
        bool IsNextStateOf<T>() where T : class;
    }
    /// <summary>
    /// 通用事件靶点：代表一个可以直接接收事件投递的目标对象。
    /// </summary>
    public interface IEventReceiver
    {
        /// <summary>
        /// 定点向该目标投递事件（点对点直发，不触发级联路由或向外冒泡）
        /// </summary>
        void Receive<TPayload>(TPayload payload = default);
    }
    /// <summary>
    /// 供 View/ViewModel 只读的接口，仅允许订阅全局事件或发送按钮指令
    /// </summary>
    public interface IReadOnlyStateMachine : IStateMachineInfo
    {
        bool Dispatch<TPayload>(TPayload payload = default);
        bool Broadcast<TPayload>(TPayload payload = default);

        /// <summary>只读订阅器，仅提供事件订阅</summary>
        IReadOnlyEventSubscriber Events { get; }

        /// <summary>只读响应式变量注册表（持久状态流，Router 重绑绝对免疫）</summary>
        IReadOnlyVariableRegistry Variables { get; }
    }
    /// <summary>
    /// 状态机基础只读信息与度量指标（供内部状态、外部业务和调试器共享）
    /// </summary>
    public interface IStateMachineInfo : IStateQueryable
    {
        string MachineId { get; }
        bool IsRunning { get; }
        bool IsDisposed { get; }
        CancellationToken MachineToken { get; }
        // === 时间与帧率度量 ===
        float DeltaTime { get; }
        float FixedDeltaTime { get; }
        float TimeInState { get; }
        float FixedTimeInState { get; }
        int FramesInState { get; }
        int FixedFramesInState { get; }
    }
    /// <summary>
    /// 对外暴露的状态机交互接口，供 Controller 使用，包含基础操作
    /// </summary>
    public interface IExternalStateMachine
        : IStateMachineInfo, IReadOnlyStateMachine, IRouterNode, IContextRegistry, IDisposable
    {
        /// <summary>向兄弟状态机发送事件</summary>
        bool CrossTalk<TPayload>(TPayload payload = default);

        /// <summary>向上冒泡事件，忽略本层，但可受 CrossTalkWhenBubbleUp 拦截</summary>
        bool BubbleUp<TPayload>(TPayload payload = default);

        /// <summary>忽略本层直接向子状态机发送事件</summary>
        bool DispatchToChildren<TPayload>(TPayload payload = default);

        /// <summary>仅在当前状态机本地消化事件，不向下路由给子状态机，不向上冒泡</summary>
        bool DispatchLocal<TPayload>(TPayload payload = default);

        void OnUpdate();
        void OnFixedUpdate();
        void OnPostUpdate();

        /// <summary>停止状态机</summary>
        void Stop();

        /// <summary>重启状态机，也可用于初始化时处于待机的重启</summary>
        /// <param name="resumeLastState">是否从上一次停止的状态开始？</param>
        void Restart(bool resumeLastState = false);

        /// <summary>注册异常处理器，根据返回值动态决定是否消费异常 (true: 消费, false: 继续抛出)</summary>
        void Catch<TException>(Func<TException, bool> errorHandler) where TException : Exception;

        /// <summary>
        /// 直接获取当前大盘底层的共享变量实例。
        /// 警告：持有此实例并在其上调用 .Subscribe() 在状态机加入/脱离 Router 时会导致断流失效，应改用 machine.Variables.Get
        /// </summary>
        TVar GetRawSharedVariable<TVar>() where TVar : class, ISharedVar;

        /// <summary>支持状态进入，退出，以及事件订阅</summary>
        new IStateEventSubscriber Events { get; }
        /// <summary>可读写响应式变量注册表（持久状态流，Router 重绑绝对免疫）</summary>
        new IVariableRegistry Variables { get; }
    }

    /// <summary>
    /// 状态断言与查询接口。任何持有/关注状态的对象均可实现此接口以获得强类型状态判定能力。
    /// </summary>
    public interface IStateQueryable
    {
        /// <summary>当前状态类型</summary>
        Type CurrentStateType { get; }
        /// <summary>上一个状态类型</summary>
        Type LastStateType { get; }

        // ==========================================
        // 默认接口方法 (DIM)
        // ==========================================

        /// <summary>检查当前状态是否为指定具体类型</summary>
        bool IsState<T>() where T : class => CurrentStateType == typeof(T);

        /// <summary>检查上一个状态是否为指定具体类型</summary>
        bool WasState<T>() where T : class => LastStateType == typeof(T);

        /// <summary>检查当前状态是否实现了特定接口或继承自特定基类 (分组判定)</summary>
        bool IsStateOf<T>() where T : class => CurrentStateType != null && typeof(T).IsAssignableFrom(CurrentStateType);

        /// <summary>检查上一个状态是否实现了特定接口或继承自特定基类 (分组判定)</summary>
        bool WasStateOf<T>() where T : class => LastStateType != null && typeof(T).IsAssignableFrom(LastStateType);

        /// <summary>检查当前状态是否属于列表中任意一个</summary>
        bool IsInAnyState(params Type[] states)
        {
            if (CurrentStateType == null || states == null) return false;
            for (int i = 0; i < states.Length; i++)
            {
                if (CurrentStateType == states[i]) return true;
            }
            return false;
        }
    }

    public interface IReadOnlyEventSubscriber
    {
        /// <summary> 任意状态下状态机收到/发出的事件 (含子兄弟状态机，子状态机的冒泡) </summary>
        IDisposable Subscribe<TEvent>(Action<TEvent> handler, CancellationToken ct = default);
        void Unsubscribe<TEvent>(Action<TEvent> handler);

        /// <summary> 订阅状态机启动（包含首次 Initialize 启动与后续 Restart 唤醒）</summary>
        IDisposable SubscribeStart(Action onStart, CancellationToken ct = default);
        void UnsubscribeStart(Action onStart);

        /// <summary> 订阅状态机停止（显式调用 Stop 停机时触发）</summary>
        IDisposable SubscribeStop(Action onStop, CancellationToken ct = default);
        void UnsubscribeStop(Action onStop);
    }

    // 事件订阅接口 (供 View/UI 使用)
    public interface IStateEventSubscriber : IReadOnlyEventSubscriber
    {
        /// <summary> 订阅状态机处于特定状态时发出/收到的事件 </summary>
        IDisposable SubscribeEventIn<TState, TEvent>(Action<TEvent> handler, CancellationToken ct = default) where TState : class, IStateNode;
        void UnsubscribeEventIn<TState, TEvent>(Action<TEvent> handler) where TState : class, IStateNode;

        /// <summary> 订阅状态的进入</summary>
        IDisposable SubscribeEnter<TState>(Action<TState> onEnter, CancellationToken ct = default) where TState : class, IStateNode;
        void UnsubscribeEnter<TState>(Action<TState> onEnter) where TState : class, IStateNode;

        /// <summary> 订阅状态的退出</summary>
        IDisposable SubscribeExit<TState>(Action<TState> onExit, CancellationToken ct = default) where TState : class, IStateNode;
        void UnsubscribeExit<TState>(Action<TState> onExit) where TState : class, IStateNode;
    }

    /// <summary>
    /// 只读响应式变量注册表（免疫 Router 重绑）
    /// </summary>
    public interface IReadOnlyVariableRegistry
    {
        /// <summary>
        /// 获取响应式只读代理（单例缓存复用，0 GC，免疫 Router 重绑）。
        /// 支持后接 .Select() 进行数据映射投影，或接 .Where() 进行条件过滤。
        /// </summary>
        IReadOnlySharedVar<TValue> Get<TVar, TValue>() where TVar : SharedVar<TValue>;

        /// <summary>直接穿透读取当前变量的最新值</summary>
        TValue Read<TVar, TValue>() where TVar : SharedVar<TValue>;

        /// <summary>直接穿透读取当前变量的基准初始值</summary>
        TValue ReadBase<TVar, TValue>() where TVar : SharedVar<TValue>;
    }

    /// <summary>
    /// 可读写响应式变量注册表
    /// </summary>
    public interface IVariableRegistry : IReadOnlyVariableRegistry
    {
        /// <summary>
        /// 获取可读写响应式代理（单例缓存复用，0 GC，免疫 Router 重绑）。
        /// </summary>
        new ISharedVar<TValue> Get<TVar, TValue>() where TVar : SharedVar<TValue>;

        /// <summary>修改值</summary>
        void Set<TVar, TValue>(TValue value) where TVar : SharedVar<TValue>;

        /// <summary>
        /// 为宿主直接绑定具体类型的共享变量引用（在大盘重绑时自动重新回调注入）
        /// </summary>
        void BindReference<TVar>(Action<TVar> binder) where TVar : class, ISharedVar;
        /// <summary>
        /// 获取当前大盘底层的原始物理单例实例（直接穿透到真实对象）。
        /// 警告：持有此实例并在其上直接调用 .Subscribe() 在状态机加入/脱离 Router 时会导致断流失效，
        /// 响应式监听请务必改用 machine.Variables.Get<TVar, TValue>().Subscribe()
        /// </summary>
        TVar GetRaw<TVar>() where TVar : class, ISharedVar;
    }
    //!--- 路由接口 ---
    /// <summary>
    /// 供内部使用的可路由状态机接口，FSM 需实现此接口以接入 Router
    /// </summary>
    internal interface IRoutableMachine : IReadOnlyStateMachine, ISharedVariableRegistry, IStateContextResolver
    {
        int BasePriority { get; }
        int GetDispatchEventPriority(Type eventType);
        void RebindSharedVariables();
        /// <summary>获取父级节点</summary>
        IRoutableMachine ParentMachine { get; }
        /// <summary>获取当前路由网络的最顶层根节点（如果自身就是根节点，则返回自身）</summary>
        IRoutableMachine RootMachine { get; }
        /// <summary>是否可以处理事件 (正在运行 or 路由模式) </summary>
        bool CanProcessEvent { get; }

        // 父子路由通讯接口 
        InternalRouterModule RouterModule { get; }
        bool HandleBubbledEvent<TEvent>(TEvent ev);
        void RegisterChild(IRoutableMachine child);
        void UnregisterChild(IRoutableMachine child);
        void InvalidateRouterCache();

    }

    /// <summary>
    /// 路由网络节点接口 (状态机和子路由器均实现此接口)
    /// </summary>
    public interface IRouterNode
    {
        /// <summary>作为子节点加入父级状态机</summary>
        RouterConnectionConfigurator JoinParent(IReadOnlyStateMachine parentMachine);
        /// <summary>脱离父级状态机，成为独立根节点</summary>
        IRouterNode LeaveParent(bool syncFromParent = true);
    }

    /// <summary>
    /// 用于专门配置状态机的接口（可选）
    /// </summary>
    /// <typeparam name="TStateNode">TStateNode: 状态节点的基类类型 继承或直接使用 StateBase</typeparam>
    public interface IStateMachineConfigurator<TStateNode> : IContextRegistry
        where TStateNode : class, IStateNode
    {
        bool UseRouterMode { get; set; }
        bool UseReflection { get; set; }

        /// <summary>宿主可以使用接口传入 如: IPlayer</summary> 
        IStateMachineConfigurator<TStateNode> SetOwner<TContext>(TContext owner) where TContext : class;
        /// <summary>将状态机的生命周期与指定的 CancellationToken 绑定，取消时自动 Dispose</summary>
        IStateMachineConfigurator<TStateNode> DisposeWith(CancellationToken cancellationToken);
        /// <summary>扫描并注册嵌套类的状态</summary> 
        IStateMachineConfigurator<TStateNode> AutoRegisterNestedStates(object owner, Type declaringType = null);
        /// <summary>扫描并注册外部继承TStateNode的状态</summary> 
        IStateMachineConfigurator<TStateNode> AutoRegisterExternalStates(object owner);
        /// <summary>手动注册状态类</summary>
        IStateMachineConfigurator<TStateNode> RegisterState<T>(T state) where T : TStateNode;
        /// <summary>手动批量注册状态类 (支持可变参数)</summary>
        IStateMachineConfigurator<TStateNode> RegisterState(params TStateNode[] states);
        /// <summary>设置时间提供者</summary>
        IStateMachineConfigurator<TStateNode> SetTimeProvider(Func<float> provider);
        /// <summary>设置时间提供者 (物理固定帧，在FixedUpdate中更新)</summary>
        IStateMachineConfigurator<TStateNode> SetFixedTimeProvider(Func<float> provider);

        /// <summary> 加入状态机路由器，并返回配置器以设置去中心化的路由规则</summary>
        RouterConnectionConfigurator JoinParent(IReadOnlyStateMachine parentMachine);

        /// <summary>配置完返回 IExternalStateMachine </summary>
        IExternalStateMachine Initialize<T>() where T : TStateNode;

        //@ 自动切换
        /// <summary>作用域闭包配置模式：在此块中声明的所有规则会自动 Apply，防止漏写</summary>
        IStateMachineConfigurator<TStateNode> ConfigureRules(Action<RuleConfigurator<TStateNode>> configureAction);
        /// <summary>
        /// (From->动态目标) 根据外部条件动态决定目标状态 (返回 null 代表放弃切换)
        /// 例: .ToDynamic(e => e.Speed > 0.1f ? typeof(Run) : typeof(Stop)) Speed大于0.1f时切换到Run
        /// </summary>
        DynamicTransitionBuilder<TFrom> BindDynamic<TFrom>(bool forceReenter = false) where TFrom : class, TStateNode;
        /// <summary>(任意->动态目标)根据外部条件动态决定目标状态 (返回 null 代表放弃切换)</summary>
        DynamicTransitionBuilder<TStateNode> BindDynamicAny(bool forceReenter = false);
        /// <summary>按接口/基类批量绑定动态目标 (返回 null 代表放弃切换)</summary>
        DynamicTransitionBuilder<TGroup> BindDynamicGroup<TGroup>(bool forceReenter = false)
            where TGroup : class;
        /// <summary>绑定特定状态之间的自动切换 (检查时机默认OnUpdate，可显式指定) 在最后执行.Apply()应用规则</summary>
        TransitionBuilder<TFrom> Bind<TFrom, TTo>(bool forceReenter = false) where TFrom : class, TStateNode where TTo : class, TStateNode;
        /// <summary>(任意->目标) 自动切换 (检查时机默认OnUpdate，可显式指定)  在最后执行.Apply()应用规则</summary>
        TransitionBuilder<TStateNode> BindAny<TTo>(bool forceReenter = false) where TTo : class, TStateNode;

        /// <summary>多对一绑定 (2个来源)  在最后执行.Apply()应用规则</summary>
        TransitionBuilder<TStateNode> BindManyToOne<TFrom1, TFrom2, TTo>(bool forceReenter = false)
            where TFrom1 : class, TStateNode
            where TFrom2 : class, TStateNode
            where TTo : class, TStateNode;
        /// <summary>多对一绑定 (3个来源) 在最后执行.Apply()应用规则</summary>
        TransitionBuilder<TStateNode> BindManyToOne<TFrom1, TFrom2, TFrom3, TTo>(bool forceReenter = false)
            where TFrom1 : class, TStateNode
            where TFrom2 : class, TStateNode
            where TFrom3 : class, TStateNode
            where TTo : class, TStateNode;
        /// <summary>多对一绑定 (不定长，需传Type数组) (不支持重入参数) 在最后执行.Apply()应用规则</summary>
        TransitionBuilder<TStateNode> BindManyToOne<TTo>(params Type[] fromStates)
            where TTo : class, TStateNode;
        /// <summary>按接口/基类批量绑定到目标</summary>
        TransitionBuilder<TGroup> BindGroupToOne<TGroup, TTo>(bool forceReenter = false)
            where TGroup : class
            where TTo : class, TStateNode;
        /// <summary>绑定一组共享相同规则的序列状态，如: 连招序列 Attack1->2->3，支持动态目标</summary>
        SequenceTransitionBuilder<TStateNode> BindSequence<TFirst>(bool forceReenter = false) where TFirst : class, TStateNode;
        /// <summary>绑定自动回退到上一个状态的规则 (回退后执行OnResume，可配置) 在最后执行.Apply()应用规则</summary>
        TransitionBuilder<TFrom> BindFallback<TFrom>(bool ignoreTransitionRule = true) where TFrom : class, TStateNode;
        /// <summary>按接口/基类批量绑定自动回退规则，并在回退时智能跳过该组内的所有历史记录</summary>
        TransitionBuilder<TGroup> BindGroupFallback<TGroup>(bool ignoreTransitionRule = true) where TGroup : class;
        /// <summary> /// (任意状态->回退) 无论当前处于什么状态，只要条件满足就自动回退到上一个状态。在最后执行 .Apply() 应用规则 /// </summary>
        TransitionBuilder<TStateNode> BindFallbackAny(bool ignoreTransitionRule = true);

        //@ 守卫规则
        /// <summary>全局拦截指定规则的迁移（From->To），在最后执行.Apply()应用规则</summary>
        GuardBuilder<TFrom> Guard<TFrom, TTo>() where TFrom : class, TStateNode where TTo : class, TStateNode;
        /// <summary>(任意->目标)全局拦截指定规则的迁移，在最后执行.Apply()应用规则</summary>
        GuardBuilder<TStateNode> GuardAny<TTo>() where TTo : class, TStateNode;
        /// <summary>(任意->按接口/基类批量目标) 全局拦截进入该组状态的迁移</summary>
        GuardBuilder<TStateNode> GuardToGroup<TGroup>() where TGroup : class;
        /// <summary>(指定目标->任意) 全局拦截离开该状态的迁移</summary>
        GuardBuilder<TFrom> GuardFrom<TFrom>() where TFrom : class, TStateNode;
        /// <summary>按接口/基类批量拦截</summary>
        GuardBuilder<TGroup> GuardFromGroup<TGroup>() where TGroup : class;
    }

    public interface IMachineLogProvider
    {
        StateMachineLogger<StateMachineLogLevel> Logger { get; }
    }

    /// <summary>
    /// Debug脚本专用查询接口
    /// </summary>
    public interface IStateMachineDebugger : IStateMachineInfo, IReadOnlyStateMachine
    {
        double MachineTime { get; }
        bool UseRouterMode { get; }
        string CurrentStateName { get; }
        string ConnectedRouterName { get; }
        int RegisteredStateCount { get; }
        Type ReallyLastStateType { get; }
        long SuccessfulTransitionCount { get; }
        double LastSuccessfulTransitionTime { get; }
        long UpdateCallCount { get; }
        long FixedUpdateCallCount { get; }
        IEnumerable<Type> RegisteredStates { get; }
        bool HasHistory(Type stateType);
        IEnumerable<StateHistoryRecorder> GetStateHistory();
        TransitionCheckResult LastTransitionResult { get; }
        int ConnectedChildMachineCount { get; }
        (bool hasField, Type fieldType, object value) MachineDebug_GetField(string fieldName);
        void Catch<TException>(Func<TException, bool> errorHandler) where TException : Exception;

        bool CanTransitionTo<T>() where T : IStateNode;
        bool CanEventTransition<TEvent>(TEvent eventData = default);
    }
    #endregion
    //!============= 状态基类(支持继承并自由扩展方法) =============
    #region StateBase
    /// <summary>
    /// 状态基类模板 (建议继承后使用，可在派生基类中按需扩展方法)
    /// 提示: 状态机支持强类型访问当前状态实例。嵌套的内部类可访问宿主 private 字段或方法。
    /// 提示: 支持使用 IStateAbility 接口组合状态公用逻辑
    /// </summary>
    /// <typeparam name="TContext">TContext: 宿主类型 (MonoBehaviour 或其他任意类型)</typeparam>
    public abstract class StateBase<TContext> : IStateNode, IStateAbility<TContext>
        where TContext : class
    {
        private string _stateName;
        /// <summary>状态机宿主</summary>
        protected TContext Owner { get; private set; }
        private IStateMachine _machine;
        protected string MachineId => _machine.MachineId;
        StateMachineLogger<StateMachineLogLevel> _debug => _machine.Logger;

        // 标记当前状态是否活跃
        private bool _isActive;
        public bool IsDisposed { get; private set; }
        public bool HasInitialized { get; private set; }

        /// <summary>查询当前状态是否可以行动 (也可供状态类在 Publish/SharedVar 成功改变状态后打断后续动作)</summary>
        public bool CanAct => HasInitialized && _isActive && !IsDisposed && Owner != null && _machine != null && _machine.IsRunning;

        // 状态内部事件
        private Dictionary<Type, Delegate> _payloadHandlers;
        /// <summary>当前状态停留时间 供状态类调用 (转发)</summary>
        protected float TimeInState => _machine?.TimeInState ?? 0f;
        /// <summary>当前帧 (精确值)</summary>
        protected int FramesInstate => _machine?.FramesInState ?? 0;
        /// <summary>之前的状态类型 供状态类调用 (转发)</summary>
        protected Type LastStateType => _machine?.LastStateType;
        /// <summary>从上一帧到当前帧的时间间隔（默认Time.deltaTime）</summary>
        protected float DeltaTime => _machine?.DeltaTime ?? 0f;
        /// <summary>物理帧固定时间间隔（默认Time.fixedDeltaTime）</summary>
        protected float FixedDeltaTime => _machine?.FixedDeltaTime ?? 0f;

        private CancellationTokenSource _cts;
        /// <summary>当前状态的取消令牌，按需惰性生成，状态退出时会自动取消</summary>
        protected CancellationToken StateToken
        {
            get
            {
                // 如果状态未激活/已销毁，返回一个已经处于取消状态的 Token
                if (!CanAct) return new CancellationToken(true);

                _cts ??= new CancellationTokenSource();
                return _cts.Token;
            }
        }
        /// <summary>随状态机销毁的取消令牌，用于长期的订阅 (状态退出时不会取消)</summary>
        protected CancellationToken MachineToken => _machine.MachineToken;

        /// <summary>回退到此状态时，使用 OnResume 代替 OnEnter 并忽略 AutoRun </summary>
        protected virtual bool SkipEnterOnRollback => false;
        bool IStateNode.SkipEnterOnRollback => this.SkipEnterOnRollback;
        // 构造函数
        protected StateBase()
        {
            _stateName = this.GetType().Name;
        }
        //!--- [初始化]---
        /// <summary>供状态机注入自身 对外隐藏</summary>
        bool IStateNode.SetStateMachine(IStateMachine machine)
        {
            if (this._machine != null)
            {
                _debug.LogWarn("仅允许在初始阶段设置StateMachine");
                return false;
            }

            this._machine = machine;
            return true;
        }

        /// <summary>供状态机注入宿主 对外隐藏</summary>
        bool IStateNode.SetOwner(object owner)
        {
            if (this.Owner != null) return true;

            if (owner is TContext ctx)
            {
                this.Owner = ctx;
                return true;
            }
            else if (owner != null)
            {
                _debug.LogErr($"状态 [{_stateName}] 注入Owner失败! 实际传入: {owner.GetType().Name}, 状态期望类型: {typeof(TContext).Name}");
            }
            return false;
        }

        //!--- [基础方法]---
        /// <summary>获取状态机中注册的上下文/组件。反射功能开启时建议使用 [AutoInject] 自动化注入</summary>
        protected bool TryGet<T>(out T context) where T : class
        {
            bool result = _machine.TryGetContext(typeof(T), out var ctx);
            context = (T)ctx;
            return result;
        }

        /// <summary>查询之前的状态 (转发)</summary>
        protected bool WasState<T>() where T : class => _machine.WasState<T>();

        /// <summary>检查上一个状态(不包含回退过程)是否属于接口或基类 (转发)</summary>
        protected bool WasStateOf<T>() where T : class => _machine.WasStateOf<T>();

        /// <summary>检查上下一个即将切换的状态 (在OnExit内查询) (转发)</summary>
        protected bool IsNextState<T>() where T : class => _machine.IsNextState<T>();

        /// <summary>检查上下一个即将切换的状态是否属于接口或基类 (在OnExit内查询) (转发)</summary>
        protected bool IsNextStateOf<T>() where T : class => _machine.IsNextStateOf<T>();

        /// <summary>查询是否可以在当前帧立即切换到目标状态 (转发)</summary>
        protected bool CanTransitionTo<T>() where T : IStateNode => CanAct && _machine.CanTransitionTo<T>();

        protected bool CanEventTransition<T>() => CanAct && _machine.CanEventTransition<T>();

        //@ 无参直接切换
        /// <summary>正常切换状态 供状态类调用 (转发)</summary>
        protected bool ChangeState<T>(bool forceReenter = false) where T : IStateNode
        {
            if (!CanAct) return false;
            return _machine.ChangeState(typeof(T), forceReenter);
        }

        //@ 带参数直接切换
        /// <summary>切换状态并传递数据 (转发) 目标状态需使用 [Payload] 标记对应类型的字段以接收数据</summary>
        protected bool ChangeState<T>(object payload, bool forceReenter = false) where T : IStateNode
        {
            if (!CanAct) return false;
            return _machine.ChangeState(typeof(T), forceReenter, payloadData: payload);
        }

        //@ 回退并注入payload
        /// <summary>回退到上一个状态 (执行 OnResume 需重写)，可选传递数据 (转发) </summary>
        protected bool RevertToPreviousState(object payload = null, bool ignoreTransitionRule = true, Type skipGroupType = null)
        {
            if (!CanAct) return false;
            return _machine?.RevertToPreviousState(payload, ignoreTransitionRule, skipGroupType) ?? false;
        }

        //!--- [事件类方法] ---
        /// <summary>
        /// (外部->内部/子级冒泡)仅允许在 OnInit() 生命周期中注册，状态内部订阅指定指令。不消费该指令（允许继续向下/向外广播）
        /// </summary>
        protected void RegisterHandler<TPayload>(Action<TPayload> handler)
        {
            if (HasInitialized)
            {
                _debug.LogWarn($"状态[{_stateName}]: RegisterHandler 仅允许在 OnInit() 中调用！请将事件注册逻辑移至 OnInit。");
                return;
            }
            _payloadHandlers ??= new Dictionary<Type, Delegate>();

            // 一次性包装为统一的 Func 签名
            Func<TPayload, bool> wrappedHandler = (payload) =>
            {
                handler?.Invoke(payload);
                return false; // Action 默认不消费
            };

            _payloadHandlers[typeof(TPayload)] = wrappedHandler;
        }

        /// <summary>
        /// (外部->内部/子级冒泡)仅允许在 OnInit() 生命周期中注册，状态内部订阅指定指令。根据 bool 返回值动态决定是否消费该指令
        /// </summary>
        protected void RegisterHandler<TPayload>(Func<TPayload, bool> handler)
        {
            if (HasInitialized)
            {
                _debug.LogWarn($"状态[{_stateName}]: RegisterHandler 仅允许在 OnInit() 中调用！请将事件注册逻辑移至 OnInit。");
                return;
            }
            _payloadHandlers ??= new Dictionary<Type, Delegate>();

            _payloadHandlers[typeof(TPayload)] = handler;
        }

        /// <summary>
        /// (内部->全网) 从最顶层根节点发起全局事件广播。
        /// 无论当前状态机处于哪一层级，事件都会从 Root 节点重新向下分发到整棵树。
        /// </summary>
        protected bool Broadcast<TEvent>(TEvent eventData = default)
        {
            if (!CanAct)
            {
                _debug.LogVerbose($"状态[{_stateName}]:Broadcast<{typeof(TEvent).Name}>: CanAction=false", LogColor.Grey, true);
                return false;
            }

            return _machine.Broadcast(eventData);
        }

        /// <summary>
        /// (内部->外部) 向外派发事件，触发本地规则或订阅，并在未消费的情况下向上冒泡(可指定 CrossTalkWhenBubbleUp 对兄弟节点派发)
        /// </summary>
        /// <returns>事件是否被消费</returns>
        protected bool Publish<TEvent>(TEvent eventData = default)
        {
            if (!CanAct)
            {
                _debug.LogVerbose($"状态[{_stateName}]:Publish<{typeof(TEvent).Name}>: CanAction=false", LogColor.Grey, true);
                return false;
            }

            return _machine.PublishStateEvent(eventData);
        }

        /// <summary>
        /// (兄弟通讯) 状态内部直接向同层级的兄弟状态机发送事件。
        /// 备注：只精确投递给兄弟节点，不向上冒泡、不触发UI监听，无视预设规则。
        /// </summary>
        protected bool CrossTalk<TEvent>(TEvent eventData = default)
        {
            if (!CanAct)
            {
                _debug.LogVerbose($"状态[{_stateName}]:CrossTalk<{typeof(TEvent).Name}>: CanAction=false", LogColor.Grey, true);
                return false;
            }

            return _machine.CrossTalk(eventData);
        }
        /// <summary>
        /// (父级->子级) 状态内部直接向所有挂载的子状态机下发指令。
        /// 备注：不会触发当前状态的 [AutoHandle]，也不会触发自身的规则。
        /// </summary>
        protected bool DispatchToChildren<TEvent>(TEvent eventData = default)
        {
            if (!CanAct) { _debug.LogVerbose($"状态[{_stateName}]:DispatchToChildren<{typeof(TEvent).Name}>: CanAction=false", LogColor.Grey, true); return false; }

            return _machine.DispatchToChildren(eventData);
        }

        /// <summary>(外部->内部) 受状态机调用 </summary>
        private bool ReceiveDispatch<TPayload>(TPayload payload)
        {
            if (!CanAct)
            {
                _debug.LogVerbose($"状态[{_stateName}]:ReceiveDispatch<{typeof(TPayload).Name}>: CanAction=false", LogColor.Grey, true);
                return false;
            }

            if (_payloadHandlers != null && _payloadHandlers.TryGetValue(typeof(TPayload), out var handler))
            {
                // 强转并安全调用
                var func = (Func<TPayload, bool>)handler;
                return func.Invoke(payload);
            }
            return false;
        }
        //!--- [提供重写的方法] ---
        /// <summary>仅在初始化执行一次（仅限初始赋值等操作，其他操作如Publish等会被拦截）</summary>
        protected virtual void OnInit() { }
        /// <summary>进入状态时在AutoRun之后执行 </summary>
        protected virtual void OnEnter() { }
        /// <summary>回退时代替OnEnter执行 </summary>
        protected virtual void OnResume() { }
        /// <summary>每帧调用的方法</summary>
        protected virtual void OnUpdate() { }
        /// <summary>物理引擎更新时调用的方法</summary>
        protected virtual void OnFixedUpdate() { }
        /// <summary>同一帧内在 Update 后调用的方法，适用于: LateUpdate/OnAnimatorIK 等</summary>
        protected virtual void OnPostUpdate() { }
        /// <summary>退出状态时执行</summary>
        protected virtual void OnExit() { }
        /// <summary>在销毁时执行</summary>
        protected virtual void OnDispose() { }

        protected virtual bool OnError(Exception ex) => false;

        //!--- [私有辅助方法] ---

        private void DisposeCancellationToken()
        {
            if (_cts == null) return;
            var cts = _cts; _cts = null;
            try
            {
                if (!cts.IsCancellationRequested) { cts.Cancel(); }
            }
            // 忽略由注册回调（如 DOTween 被 kill 时外部代码抛出）引起的异常，
            catch (AggregateException) { }
            // 忽略已释放的异常 
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _machine?.Logger?.LogWarn($"[StateBase] 取消 StateToken 触发回调时发生异常: {ex.Message}"); }
            finally { cts.Dispose(); }
        }

        //: ========== 显式实现接口，控制生命周期流程 ==========

        void IStateNode.OnInit()
        {
            if (HasInitialized || IsDisposed) return;
            if (Owner == null || _machine == null || _machine.IsDisposed)
                throw new Exception($"状态: [{_stateName}] 宿主为null或状态机已销毁");

            // 执行用户的初始化逻辑及 RegisterHandler，此时标记为false可用于判定和拦截
            OnInit();
            // 完成初始化后打开标记
            HasInitialized = true;
        }

        void IStateNode.PreEnter()
        {
            if (IsDisposed) return;

            _isActive = true;
        }

        void IStateNode.OnResume()
        {
            if (!CanAct) { _debug.LogWarn($"状态[{_stateName}]OnResume: CanAction=false"); return; }
            try { OnResume(); }
            catch
            {
                DisposeCancellationToken();
                throw;
            }
        }

        void IStateNode.OnEnter()
        {
            if (!CanAct)
            {
                _debug.LogWarn($"状态[{_stateName}]OnEnter: CanAction=false");
                return;
            }
            try { OnEnter(); }
            catch
            {
                DisposeCancellationToken();
                throw;
            }

        }

        void IStateNode.OnUpdate() { if (!CanAct) return; OnUpdate(); }

        void IStateNode.OnFixedUpdate() { if (!CanAct) return; OnFixedUpdate(); }

        void IStateNode.OnPostUpdate() { if (!CanAct) return; OnPostUpdate(); }

        void IStateNode.OnExit()
        {
            if (CanAct) OnExit();
        }

        void IStateNode.PostExit()
        {
            _isActive = false;

            DisposeCancellationToken();
        }

        void IStateNode.OnDispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            HasInitialized = false;
            try { OnDispose(); }
            finally
            {
                DisposeCancellationToken();

                _payloadHandlers?.Clear();
                _payloadHandlers = null;
                this._machine = null;
                this.Owner = null;
            }
        }
        bool IStateNode.OnError(Exception ex)
        {
            return OnError(ex);
        }

        //: ====================
        bool IStateNode.ReceiveDispatch<TPayload>(TPayload payload) => ReceiveDispatch(payload);

        //!--- [显式IStateAbility方法]
        TContext IStateAbility<TContext>.Owner => this.Owner;
        float IStateAbility<TContext>.TimeInState => this.TimeInState;
        CancellationToken IStateAbility<TContext>.StateToken => this.StateToken;
        TVar IStateAbility<TContext>.GetSharedVar<TVar>() => _machine.GetRawSharedVariable<TVar>();
        bool IStateAbility<TContext>.TryGetContext<T>(out T context) => this.TryGet(out context);
        bool IStateAbility<TContext>.WasStateOf<T>() => this.WasStateOf<T>();
        bool IStateAbility<TContext>.IsNextStateOf<T>() => this.IsNextStateOf<T>();
        bool IStateAbility<TContext>.ChangeState<T>(bool forceReenter) => this.ChangeState<T>(forceReenter);
        bool IStateAbility<TContext>.ChangeState<T>(object payload, bool forceReenter) => this.ChangeState<T>(payload, forceReenter);
        bool IStateAbility<TContext>.RevertToPreviousState(object payload, bool ignoreTransitionRule, Type skipGroupType) => RevertToPreviousState(payload, ignoreTransitionRule, skipGroupType);
        bool IStateAbility<TContext>.Publish<TEvent>(TEvent eventData) => this.Publish(eventData);
        bool IStateAbility<TContext>.CrossTalk<TEvent>(TEvent eventData) => this.CrossTalk(eventData);
        bool IStateAbility<TContext>.DispatchToChildren<TEvent>(TEvent eventData) => this.DispatchToChildren(eventData);
        bool IStateAbility<TContext>.Broadcast<TEvent>(TEvent eventData) => this.Broadcast(eventData);

    }

    //!============= 状态基类 (MonoBehaviour) =============
    /// <summary>
    /// 专供 MonoBehaviour 使用的状态基类模板 (如 UI 面板、带有 Transform/Component 的实体状态等)
    /// </summary>
    public abstract class MonoStateBase<TContext> : MonoBehaviour, IStateNode, IStateAbility<TContext>
        where TContext : class
    {
        private string _stateName => this.GetType().Name;

        /// <summary>状态机宿主</summary>
        protected TContext Owner { get; private set; }
        private IStateMachine _machine;
        protected string MachineId => _machine?.MachineId ?? "Unknown";

        // 标记当前状态是否活跃
        private bool _isActive;
        public bool IsDisposed { get; private set; }
        public bool HasInitialized { get; private set; }

        public bool CanAct => HasInitialized && _isActive && !IsDisposed && Owner != null && _machine != null && _machine.IsRunning;

        // 状态内部事件
        private Dictionary<Type, Delegate> _payloadHandlers;

        protected float TimeInState => _machine?.TimeInState ?? 0f;
        protected int FramesInstate => _machine?.FramesInState ?? 0;
        protected Type LastStateType => _machine?.LastStateType;
        protected float DeltaTime => _machine?.DeltaTime ?? 0f;
        protected float FixedDeltaTime => _machine?.FixedDeltaTime ?? 0f;
        private CancellationTokenSource _cts;
        protected CancellationToken StateToken
        {
            get
            {
                if (!CanAct) return new CancellationToken(true);
                _cts ??= new CancellationTokenSource();
                return _cts.Token;
            }
        }
        protected CancellationToken MachineToken => _machine.MachineToken;

        protected virtual bool SkipEnterOnRollback => false;
        bool IStateNode.SkipEnterOnRollback => this.SkipEnterOnRollback;

        /// <summary>
        /// (可重写) 是否在组件激活时自动将 enabled 设为 false (默认: true)。开启后可避免 Unity 原生 Update/FixedUpdate 的空轮询开销。
        /// </summary>
        protected virtual bool AutoDisableComponent => true;

        [Obsolete("该生命周期已由状态机接管", true)]
        protected void OnEnable()
        {
            if (AutoDisableComponent) { this.enabled = false; }
        }

        [Obsolete("该生命周期已由状态机接管", true)]
        protected Action Awake => null;
        [Obsolete("该生命周期已由状态机接管", true)]
        protected Action Start => null;
        [Obsolete("该生命周期已由状态机接管", true)]
        protected Action Update => null;
        [Obsolete("该生命周期已由状态机接管", true)]
        protected Action FixedUpdate => null;
        [Obsolete("该生命周期已由状态机接管", true)]
        protected Action LateUpdate => null;
        [Obsolete("该生命周期已由状态机接管", true)]
        protected Action OnDisable => null;
        [Obsolete("该生命周期已由状态机接管", true)]
        protected void OnDestroy()
        {
            ((IStateNode)this).OnDispose();
        }

        //!--- [基础方法与转发API] ---
        bool IStateNode.SetStateMachine(IStateMachine machine)
        {
            if (this._machine != null) return false;
            this._machine = machine;
            return true;
        }

        bool IStateNode.SetOwner(object owner)
        {
            if (this.Owner != null) return true;

            if (owner is TContext ctx)
            {
                this.Owner = ctx;
                return true;
            }
            else if (owner != null)
            {
                _machine.Logger.LogErr($"状态 [{_stateName}] 注入Owner失败! 实际传入: {owner.GetType().Name}, 状态期望类型: {typeof(TContext).Name}");
            }
            return false;
        }

        protected bool TryGet<T>(out T context) where T : class
        {
            bool result = _machine.TryGetContext(typeof(T), out var ctx);
            context = (T)ctx;
            return result;
        }

        protected bool WasState<T>() where T : class => _machine.WasState<T>();
        protected bool WasStateOf<T>() where T : class => _machine.WasStateOf<T>();
        protected bool IsNextState<T>() where T : class => _machine.IsNextState<T>();
        protected bool IsNextStateOf<T>() where T : class => _machine.IsNextStateOf<T>();
        protected bool CanTransitionTo<T>() where T : IStateNode => CanAct && _machine.CanTransitionTo<T>();
        protected bool CanEventTransition<T>() => CanAct && _machine.CanEventTransition<T>();

        protected bool ChangeState<T>(bool forceReenter = false) where T : IStateNode
        {
            if (!CanAct) return false;
            return _machine.ChangeState(typeof(T), forceReenter);
        }

        protected bool ChangeState<T>(object payload, bool forceReenter = false) where T : IStateNode
        {
            if (!CanAct) return false;
            return _machine.ChangeState(typeof(T), forceReenter, payloadData: payload);
        }

        protected bool RevertToPreviousState(object payload = null, bool ignoreTransitionRule = true, Type skipGroupType = null)
        {
            if (!CanAct) return false;
            return _machine?.RevertToPreviousState(payload, ignoreTransitionRule, skipGroupType) ?? false;
        }

        protected bool Broadcast<TEvent>(TEvent eventData = default)
        {
            if (!CanAct) return false;
            return _machine.Broadcast(eventData);
        }

        protected bool Publish<TEvent>(TEvent eventData = default)
        {
            if (!CanAct) return false;
            return _machine.PublishStateEvent(eventData);
        }

        protected bool CrossTalk<TEvent>(TEvent eventData = default)
        {
            if (!CanAct) return false;
            return _machine.CrossTalk(eventData);
        }

        protected bool DispatchToChildren<TEvent>(TEvent eventData = default)
        {
            if (!CanAct) return false;
            return _machine.DispatchToChildren(eventData);
        }

        protected void RegisterHandler<TPayload>(Action<TPayload> handler)
        {
            if (HasInitialized) return;
            _payloadHandlers ??= new Dictionary<Type, Delegate>();
            _payloadHandlers[typeof(TPayload)] = (Func<TPayload, bool>)((p) => { handler?.Invoke(p); return false; });
        }

        protected void RegisterHandler<TPayload>(Func<TPayload, bool> handler)
        {
            if (HasInitialized) return;
            _payloadHandlers ??= new Dictionary<Type, Delegate>();
            _payloadHandlers[typeof(TPayload)] = handler;
        }

        private bool ReceiveDispatch<TPayload>(TPayload payload)
        {
            if (!CanAct) return false;
            if (_payloadHandlers != null && _payloadHandlers.TryGetValue(typeof(TPayload), out var handler))
            {
                return ((Func<TPayload, bool>)handler).Invoke(payload);
            }
            return false;
        }

        //!--- [生命周期虚方法] ---
        protected virtual void OnInit() { }
        protected virtual void OnEnter() { }
        protected virtual void OnResume() { }
        protected virtual void OnUpdate() { }
        protected virtual void OnFixedUpdate() { }
        protected virtual void OnPostUpdate() { }
        protected virtual void OnExit() { }
        protected virtual void OnDispose() { }
        protected virtual bool OnError(Exception ex) => false;
        //!--- [私有辅助逻辑] ---
        private void DisposeCancellationToken()
        {
            if (_cts == null) return;
            var cts = _cts; _cts = null;
            try
            {
                if (!cts.IsCancellationRequested) { cts.Cancel(); }
            }
            catch (AggregateException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _machine?.Logger?.LogWarn($"[StateBase] 取消 StateToken 触发回调时发生异常: {ex.Message}"); }
            finally { cts.Dispose(); }
        }

        //!--- [显式实现 IStateNode] ---
        void IStateNode.OnInit()
        {
            if (HasInitialized || IsDisposed) return;
            bool isIllegalInstance = false;
            try
            {
                // 如果是通过 new 或 Activator 创建的，访问 gameObject 会抛异常或返回 null
                if (System.Object.ReferenceEquals(this.gameObject, null) || this.gameObject == null)
                {
                    isIllegalInstance = true;
                }
            }
            catch
            {
                isIllegalInstance = true;
            }

            if (isIllegalInstance)
            {
                throw new InvalidOperationException($"<b><color=red>状态 [{_stateName}] 并非由 Unity 实例化！应使用 RegisterState(component) 注册</color></b>");
            }

            OnInit();
            HasInitialized = true;
        }

        void IStateNode.PreEnter()
        {
            if (IsDisposed) return;
            _isActive = true;
        }

        void IStateNode.OnResume()
        {
            if (!CanAct) return;
            try { OnResume(); }
            catch { DisposeCancellationToken(); throw; }
        }

        void IStateNode.OnEnter()
        {
            if (!CanAct) return;
            try { OnEnter(); }
            catch { DisposeCancellationToken(); throw; }
        }

        void IStateNode.OnUpdate() { if (CanAct) OnUpdate(); }
        void IStateNode.OnFixedUpdate() { if (CanAct) OnFixedUpdate(); }
        void IStateNode.OnPostUpdate() { if (CanAct) OnPostUpdate(); }
        void IStateNode.OnExit() { if (CanAct) OnExit(); }

        void IStateNode.PostExit()
        {
            _isActive = false;
            DisposeCancellationToken();
        }

        void IStateNode.OnDispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            HasInitialized = false;
            try { OnDispose(); }
            finally
            {
                DisposeCancellationToken();

                _payloadHandlers?.Clear();
                _payloadHandlers = null;
                this._machine = null;
                this.Owner = null;
            }
        }

        bool IStateNode.OnError(Exception ex) => OnError(ex);

        bool IStateNode.ReceiveDispatch<TPayload>(TPayload payload) => ReceiveDispatch(payload);

        //!--- [显式实现 IStateAbility] ---
        TContext IStateAbility<TContext>.Owner => this.Owner;
        float IStateAbility<TContext>.TimeInState => this.TimeInState;
        CancellationToken IStateAbility<TContext>.StateToken => this.StateToken;
        TVar IStateAbility<TContext>.GetSharedVar<TVar>() => _machine.GetRawSharedVariable<TVar>();
        bool IStateAbility<TContext>.TryGetContext<T>(out T context) => this.TryGet(out context);
        bool IStateAbility<TContext>.WasStateOf<T>() => this.WasStateOf<T>();
        bool IStateAbility<TContext>.IsNextStateOf<T>() => this.IsNextStateOf<T>();
        bool IStateAbility<TContext>.ChangeState<T>(bool forceReenter) => this.ChangeState<T>(forceReenter);
        bool IStateAbility<TContext>.ChangeState<T>(object payload, bool forceReenter) => this.ChangeState<T>(payload, forceReenter);
        bool IStateAbility<TContext>.RevertToPreviousState(object payload, bool ignoreTransitionRule, Type skipGroupType) => RevertToPreviousState(payload, ignoreTransitionRule, skipGroupType);
        bool IStateAbility<TContext>.Publish<TEvent>(TEvent eventData) => this.Publish(eventData);
        bool IStateAbility<TContext>.CrossTalk<TEvent>(TEvent eventData) => this.CrossTalk(eventData);
        bool IStateAbility<TContext>.DispatchToChildren<TEvent>(TEvent eventData) => this.DispatchToChildren(eventData);
        bool IStateAbility<TContext>.Broadcast<TEvent>(TEvent eventData) => this.Broadcast(eventData);
    }

    #endregion
    //!============ [强类型状态机] ============
    #region StateMachine
    // 记录最后一次切换的结果
    public enum TransitionCheckResult
    {
        Allowed,
        Uninitialized,
        IsChanging,
        GuardBlocked,
        AlreadyInState,
        InfiniteLoop,
    }

    /// <summary>
    /// 强类型 泛型状态机 状态需继承 StateBase 或其派生 (非线程安全，所有调用须在主线程)
    /// </summary>
    /// <typeparam name="TStateNode">TStateNode: 状态节点的基类类型 继承或直接使用 StateBase </typeparam>
    public class StateMachine<TStateNode> :
        IStateMachine,
        IEventReceiver,
        IReadOnlyStateMachine,
        IExternalStateMachine,
        IRoutableMachine,
        ITransitionRuleReceiver,
        IStateMachineConfigurator<TStateNode>,
        IRouterRuleReceiver,
        IStateMachineDebugger,
        IDisposable
        where TStateNode : class, IStateNode
    {

        // 状态机构造函数
        public StateMachine(string machineId = default)
        {
            MachineId = string.IsNullOrWhiteSpace(machineId) ? typeof(TStateNode).Name : machineId;

            // 实例化日志系统
            _debug = new(MachineId, StateMachineLogLevel.Normal);

        }
        //!--------
        // 缓存状态机的宿主对象
        private object _stateMachineOwner;

        // 缓存 Token 注册句柄，用于自身提前 Dispose 时的反注册
        private CancellationTokenRegistration _lifecycleRegistration;
        // 自动离开父级的 token
        private CancellationTokenRegistration _autoLeaveRegistration;

        private CancellationTokenSource _machineCts;
        /// <summary>
        /// 状态机生命周期令牌：按需惰性分配，当状态机被 Dispose 时自动触发 Cancel
        /// </summary>
        public CancellationToken MachineToken
        {
            get
            {
                if (IsDisposed) return new CancellationToken(true);
                _machineCts ??= new CancellationTokenSource();
                return _machineCts.Token;
            }
        }
        // 日志处理器
        private StateMachineLogger<StateMachineLogLevel> _debug;
        public StateMachineLogger<StateMachineLogLevel> Logger => _debug;

        /// <summary>配置名称 (默认使用状态基类)</summary>
        public string LogTag { get => _debug.LogTag; set => _debug.LogTag = value; }
        public LogColor TagColor { get => _debug.TagColor; set => _debug.TagColor = value; }
        public StateMachineLogLevel LogLevel { get => _debug.LogLevel; set => _debug.LogLevel = value; }

        // (核心组件)反射处理器
        private StateReflectionProcessor<TStateNode> _reflectionProcessor;
        private StateReflectionProcessor<TStateNode> ReflectionProcessor => _reflectionProcessor ??= UseReflection ? new(_debug) : null;
        // (核心组件)事件管理器
        private StateEventManager _eventManager;
        private StateEventManager EventManager => _eventManager ??= new();
        // (核心组件)DI管理器
        private StateContextManager _contextManager;
        private StateContextManager ContextManager => _contextManager ??= new();
        // (核心组件)路由组件
        private InternalRouterModule _routerModule;
        private InternalRouterModule RouterModule => _routerModule ??= new(_debug);
        // (核心组件)变量管理模块
        private StateVariableManager _variableManager;
        private StateVariableManager VariableManager => _variableManager ??= new(this, () => _parentMachine);

        // 核心门面属性暴露
        public IVariableRegistry Variables => VariableManager;
        IReadOnlyVariableRegistry IReadOnlyStateMachine.Variables => Variables;

        // 事件订阅接口
        public IStateEventSubscriber Events => EventManager;
        // 只读接口仅订阅事件 
        IReadOnlyEventSubscriber IReadOnlyStateMachine.Events => Events;

        /// <summary>当前状态实例 (支持强类型访问)</summary>
        public TStateNode CurrentState { get; private set; }
        /// <summary>当前状态类型</summary>
        public Type CurrentStateType { get; private set; }

        /// <summary>状态机名称用于标识</summary>
        public string MachineId { get; private set; }
        public string CurrentStateName => CurrentStateType != null ? CurrentStateType.Name : NoneStateName;
        private static readonly string NoneStateName = "None";
        /// <summary>当前已连接的路由名称 </summary>
        public string ConnectedRouterName => _parentMachine?.MachineId;
        /// <summary>查询该状态机下已连接的子状态机数量</summary>
        public int ConnectedChildMachineCount => RouterModule == null ? 0 : RouterModule.ChildCount;

        private int _maxHistory = 10;
        /// 保存历史记录
        private List<StateHistoryRecorder> _stateHistory = new(10);
        /// <summary>历史记录最大容量</summary>
        public int MaxHistoryCapacity { get => _maxHistory; set => _maxHistory = System.Math.Max(1, value); }

        public IEnumerable<Type> RegisteredStates => GetAllRegisteredStates();
        /// <summary>之前的状态类型(包含回退)</summary>
        public Type ReallyLastStateType { get; private set; }
        /// <summary>之前的状态类型(排除回退)</summary>
        public Type LastStateType { get; private set; }
        /// <summary>即将切换的状态(在 OnExit之前，IsChangingState 窗口期调用)</summary>
        public Type NextStateType { get; private set; }
        /// <summary>成功切换的次数(包含回退)</summary>
        public long SuccessfulTransitionCount { get; private set; }
        /// <summary>最后一次成功切换的计时(包含回退)</summary>
        public double LastSuccessfulTransitionTime { get; private set; }
        public TransitionCheckResult LastTransitionResult { get; private set; }
        // 记录默认的入口状态类型
        private Type _defaultEntryStateType = null;
        // 记录Stop时的状态(用于精准Resume)  
        public Type LastStoppedStateType { get; private set; } = null;

        // 状态机时间相关变量
        public double MachineTime { get; private set; } = 0;
        public long UpdateCallCount { get; private set; } = 0;
        public long FixedUpdateCallCount { get; private set; } = 0;
        public long PostUpdateCallCount { get; private set; } = 0;
        public float TimeInState { get; private set; } = 0;
        public float FixedTimeInState { get; private set; } = 0;
        public int FramesInState { get; private set; } = 0;
        public int FixedFramesInState { get; private set; } = 0;
        /// <summary>从上一帧到当前帧的时间间隔(基于timeProvider)</summary>
        public float DeltaTime => _timeProvider();
        /// <summary>物理帧固定时间间隔（基于fixedTimeProvider）</summary>
        public float FixedDeltaTime => _fixedTimeProvider();
        // 定义时间提供者委托
        private Func<float> _timeProvider = () => Time.deltaTime;
        private Func<float> _fixedTimeProvider = () => Time.fixedDeltaTime;

        /// <summary>是否处于启动状态，未启动会禁用一些功能的调用</summary>
        public bool IsRunning { get; private set; }
        /// <summary>是否为纯路由节点模式(无状态)，开启后无法初始化，仅提供 Dispatch / BubbleUp 等事件发送功能</summary>
        public bool UseRouterMode { get; set; } = false;
        /// <summary>是否已销毁</summary>
        public bool IsDisposed { get; private set; }
        // 是否在执行Enter 防死循环标记
        public bool IsChangingState { get; private set; }
        // 是否可以进行事件的转发
        private bool CanProcessEvent => (IsRunning || UseRouterMode) && !IsDisposed;
        // 标记是否已反射过扫描状态类
        bool _isAutoRegisterExecuted;
        // 是否已完成初始化配置
        private bool _hasInitialized = false;

        public int RegisteredStateCount { get; private set; }
        // 缓存状态类
        private Dictionary<Type, TStateNode> _stateMap = new();

        // 全局静态缓存：字典的key是“状态机基类(TStateNode)”，值是“所有符合条件的外部状态Type”
        private static Dictionary<Type, List<Type>> _globalStateCache;
        // 全局静态缓存：字典的key是“宿主的Type”，值是“该宿主内部所有符合条件的嵌套状态Type”
        private static Dictionary<Type, List<Type>> _globalNestedStateCache;
        // 队列支持缓存注入委托
        private Queue<TransitionRequest> _nextStateQueue = new();
        // 用于缓存延迟执行的规则配置
        private Action<RuleConfigurator<TStateNode>> _deferredRuleConfigurator;

        private bool _useReflection = true;
        /// <summary> 开启反射功能，关闭时可快速初始化，状态内Attribute会失效 (在初始化前设置)</summary>
        public bool UseReflection { get => _useReflection; set { if (_hasInitialized) return; _useReflection = value; } }
        /// <summary>状态机销毁(Dispose)时是否自动执行停机流程(Stop/OnExit)。(默认 false)</summary>
        public bool StopOnDispose { get; set; } = false;

        // 代表 AnyState，匹配任意状态
        private static Type AnyStateKey => AssemblyTypeHolder.AnyStateKey;

        private int transitionDepth = 0; // 用于记录深度
        private const int MAX_DEPTH = 12; // 同一帧最大连续切换次数 防止死循环
        public bool DebugMode { get; set; } = false;

        // 异常处理存储单元
        private List<(Type ExceptionType, Func<Exception, bool> Handler)> _errorHandlers;
        //! --- [初始配置] ---

        /// <summary>
        /// 极简入口 一键启动 全自动扫描并注册所有嵌套状态类 并完成初始化 
        /// 常规启动使用 Initialize，需手动调用 RegisterState 或自动反射注册
        /// </summary>
        /// <typeparam name="T">初始化状态</typeparam>
        /// <param name="owner">宿主</param>
        /// <param name="declaringType">包含嵌套状态类的类型。如果留空，将自动使用 owner.GetType()</param>
        public StateMachine<TStateNode> AutoStart<T>(object owner, Type declaringType = null) where T : TStateNode
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (!_useReflection) { _debug.LogWarn("反射功能处于关闭状态"); return this; }
            if (_stateMap.Count > 1)
            {
                _debug.LogInfo("在扫描嵌套类前已经注册了状态，手动注册建议使用 Initialize 进行初始化", LogColor.Grey, true);
            }
            AutoRegisterNestedStates(owner, declaringType);

            Initialize<T>();

            return this;
        }

        // 配置用的方法
        IStateMachineConfigurator<TStateNode> IStateMachineConfigurator<TStateNode>.SetOwner<TContext>(TContext owner) => SetOwner(owner);
        /// <summary>显式设置宿主对象，并自动注入到当前已注册的所有状态中</summary>
        public StateMachine<TStateNode> SetOwner(object owner)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }

            if (IsRunning && DebugMode == false)
            {
                _debug.LogWarn("运行时禁止修改Owner");
                return this;
            }
            if (owner == null || owner.GetType().IsValueType) { this.OnError(new Exception($"[{MachineId}] SetOwner传入参数不合法")); }

            _stateMachineOwner = owner;
            _debug.SetLogContext(owner);

            // 尝试给已经提前注册的状态注入Owner
            foreach (var state in _stateMap.Values)
            {
                if (!state.SetOwner(owner))
                {
                    this.OnError(new Exception("状态注入宿主失败"));
                }
                // 补上延迟的 AutoBind 绑定
                ReflectionProcessor?.BindOwnerDependencies(state, owner);
            }
            return this;
        }

        IStateMachineConfigurator<TStateNode> IStateMachineConfigurator<TStateNode>.DisposeWith(CancellationToken cancellationToken) => DisposeWith(cancellationToken);
        /// <summary>
        /// 将状态机的生命周期与指定的 CancellationToken 绑定，取消时自动 Dispose
        /// </summary>
        public StateMachine<TStateNode> DisposeWith(CancellationToken cancellationToken)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (!cancellationToken.CanBeCanceled) { _debug.LogWarn("CancellationToken 无效!"); return this; }

            if (cancellationToken.IsCancellationRequested)
            {
                _debug.LogWarn("该 CancellationToken 已销毁, 立即执行销毁程序");
                Dispose();
                return this;
            }

            // 清理旧的注册（防止重复绑定）
            _lifecycleRegistration.Dispose();

            // 当 Token 取消时（如 GameObject 销毁/退出游戏），自动执行 Dispose()
            _lifecycleRegistration = cancellationToken.Register(() =>
            {
                if (!IsDisposed)
                {
                    Dispose();
                }
            });

            return this;
        }
        IStateMachineConfigurator<TStateNode> IStateMachineConfigurator<TStateNode>.RegisterState(params TStateNode[] states)
            => RegisterState(states);

        /// <summary>批量注册状态实例 (支持可变参数)</summary>
        public StateMachine<TStateNode> RegisterState(params TStateNode[] states)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (states == null || states.Length == 0) return this;

            for (int i = 0; i < states.Length; i++)
            {
                var state = states[i];
                if (state != null)
                {
                    // 显式声明泛型，强制调用 RegisterState<T> 防止 StackOverflowException
                    RegisterState<TStateNode>(state);
                }
            }
            return this;
        }
        IStateMachineConfigurator<TStateNode> IStateMachineConfigurator<TStateNode>.RegisterState<T>(T owner) => RegisterState(owner);
        /// <summary>手动注册状态实例，已配置 Owner 的前提下支持自动注入（追求性能或IL2CPP可选）</summary>
        public StateMachine<TStateNode> RegisterState<T>(T state) where T : TStateNode
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (UseRouterMode) { _debug.LogWarn("添加状态失败: 当前处于路由模式，仅提供事件转发功能"); return this; }
            if (IsRunning && DebugMode == false) { _debug.LogWarn("运行时禁止添加状态（必须先停止）"); return this; }
            if (state == null) { _debug.LogErr($"{typeof(T).Name} 传入实例为null"); return this; }
            if (state.IsDisposed) { _debug.LogErr($"{typeof(T).Name} 该实例已销毁"); return this; }

            var stateType = state.GetType();// 获取实例的真实类型

            if (_stateMap.TryAdd(stateType, state))
            {

                if (state.SetStateMachine(this))
                {

                    // === 反射字段缓存 ===
                    ReflectionProcessor?.CacheSharedValueFields(state); // 缓存 SharedVar

                    // 注入引用
                    RebindSharedVariables();
                    // 缓存并立即执行 [AutoBind] 绑定
                    if (_stateMachineOwner != null)
                    {
                        ReflectionProcessor?.BindOwnerDependencies(state, _stateMachineOwner);
                    }
                    ReflectionProcessor?.CacheInjectFields(stateType);  // 缓存 [AutoInject]
                    ReflectionProcessor?.CacheStateFields(state); // 缓存 [AutoReset]
                    ReflectionProcessor?.CacheLinkFields(state); // 缓存 [AutoLink]
                    ReflectionProcessor?.CachePayloadFields(state); // 缓存 [Payload]
                    ReflectionProcessor?.CacheAutoHandles(state); // 缓存[AutoHandle]
                    ReflectionProcessor?.CacheAutoRunMethods(state); // 缓存[AutoRun]

                    // 如果状态机已经绑定了宿主，自动注入给新注册的状态
                    if (_stateMachineOwner != null)
                    {
                        if (!state.SetOwner(_stateMachineOwner))
                        {
                            this.OnError(new Exception("状态注入宿主失败"));
                        }
                    }
                    RegisteredStateCount++;
                }
                else
                {
                    _stateMap.Remove(stateType);
                    this.OnError(new Exception($"注入状态机失败: {stateType}"));
                }
            }
            return this;
        }

        IExternalStateMachine IStateMachineConfigurator<TStateNode>.Initialize<T>() => Initialize<T>();
        /// <summary>
        /// 设置初始状态并启动状态机 可在停止后重新启用（配置完毕后必须调用才可使用状态机）
        /// </summary>
        /// <param name="startImmediately">是否立刻启动?</param>
        public StateMachine<TStateNode> Initialize<T>(bool startImmediately = true) where T : TStateNode
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (UseRouterMode) { _debug.LogWarn("初始化失败: 当前处于路由模式，仅提供事件转发功能"); return this; }
            if (_hasInitialized) { _debug.LogWarn("初始化失败: 已经初始化"); return this; }
            if (_stateMachineOwner == null) { this.OnError(new Exception("初始化失败: 未设置宿主")); }
            if (_stateMap.Count < 1) { this.OnError(new Exception("初始化失败: 没有添加任何状态")); }

            Type targetType = typeof(T);

            if (!_stateMap.TryGetValue(targetType, out var state))
            {
                this.OnError(new Exception($"未注册初始状态: [{targetType.Name}]。" +
                    $"如果是手动模式，请确认调用了 RegisterState()；" +
                    $"如果是自动扫描模式且在 IL2CPP 打包后报错，请检查 link.xml 是否保留了该类避免被代码裁剪 (Stripping)！"));
            }

            // 在状态全部注册完毕后、正式启动前，执行所有积压的规则配置
            ApplyDeferredRules();

            BakeAllTransitions();

            if (!_useReflection) { _debug.LogInfo($"反射功能被关闭，状态内的所有[Attribute]以及[SharedVar]自动注入都将失效", LogColor.Grey, true); }

            // 统一初始化所有已注册的状态节点
            foreach (var stateNode in _stateMap.Values)
            {
                if (stateNode.HasInitialized) continue;
                try
                {
                    stateNode.OnInit();
                }
                catch (Exception e)
                {
                    _debug.LogErr($"初始化时 [OnInit] 流程发生异常: <color=white>{e.Message}</color>");
                    this.OnError(e);
                }
            }

            _defaultEntryStateType = targetType;
            // 打开完成初始化标记
            _hasInitialized = true;

            if (startImmediately)
            {
                _debug.LogInfo($"初始状态: <b><color=white>[{targetType.Name}]</color></b>，" +
                            $"共: <color=green>[{RegisteredStateCount}]</color> 个状态，" +
                            $"初始化前已注册 <color=green>[{_totalTransitionRuleCount}]</color> 条迁移规则(拆分后)，" +
                            $"<color=green>[{ContextManager.ContextCount}]</color> 个外部组件。");

                HandleStartStateMachine(state);
            }
            else
            {
                // 处于 Standby 状态，IsRunning 仍为 false，CurrentState 仍为 null
                _debug.LogInfo($"<color=orange>[待机中]</color> 默认初始状态: <b><color=white>[{targetType.Name}]</color></b>，" +
                            $"共: <color=green>[{RegisteredStateCount}]</color> 个状态，" +
                            $"初始化前已注册 <color=green>[{_totalTransitionRuleCount}]</color> 条迁移规则(拆分后)，" +
                            $"<color=green>[{ContextManager.ContextCount}]</color> 个外部组件。" +
                            $"当前处于休眠(Stopped)状态，可使用Restart()唤醒。");
            }

            return this;
        }

        // 供 Initialize/Restart 调用
        private void HandleStartStateMachine(TStateNode state)
        {
            if (state == null) throw new Exception("目标状态实例为null");
            if (state.IsDisposed || !state.HasInitialized)
            {
                _debug.LogErr($"初始化/重启失败，该状态[{state.GetType().Name}]未完成初始化或已被销毁");
                return;
            }

            IsRunning = true;
            ResetStateTimer();

            CurrentState = state;
            CurrentStateType = state.GetType();

            // 刷新 AutoRun 指针
            RefreshActiveReflectionPointers(CurrentStateType);
            // 刷新 AutoTransitions缓存
            RefreshActiveTransitionRules(CurrentStateType);

            try
            {
                // 初始启动前注入依赖
                ReflectionProcessor?.InjectContextData(CurrentState, CurrentStateType, this);
                // 重置变量(停止后重启)
                ReflectionProcessor?.ResetStateFields(CurrentState);
            }
            catch (Exception e) { this.OnError(e); }

            try
            {
                //: 激活_isActive标志位
                CurrentState.PreEnter();

                // 在OnEnter前执行AutoRun BeforeEnter
                ReflectionProcessor?.InvokeAutoRun(CurrentState, Timing.BeforeEnter);

                //: 进入状态
                CurrentState.OnEnter();

                // 在OnEnter后执行AutoRun AfterEnter
                ReflectionProcessor?.InvokeAutoRun(CurrentState, Timing.AfterEnter);
            }
            catch (Exception e)
            {
                _debug.LogErr($"初始化/重启时 [OnEnter] 流程发生异常: <color=white>{e.Message}</color>");
                this.OnError(e);
            }

            // 记录
            LastTransitionResult = TransitionCheckResult.Allowed;

            try
            {
                EventManager.InvokeStart();
                EventManager.InvokeEnter(CurrentStateType, CurrentState);
            }
            catch (Exception e) { _debug.LogErr($"事件通知发生异常: <color=white>{e.Message}</color>", e); }
        }

        IStateMachineConfigurator<TStateNode> IStateMachineConfigurator<TStateNode>.SetTimeProvider(Func<float> provider) => SetTimeProvider(provider);
        /// <summary>设置时间提供者 (可选) </summary>
        /// <param name="provider">例: () => Time.deltaTime</param>
        public StateMachine<TStateNode> SetTimeProvider(Func<float> provider)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (provider == null) { _debug.LogErr($"[{MachineId}] TimeProvider为null"); return this; }
            this._timeProvider = provider;
            this.TimeInState = 0f; // 启用计时器时，初始化为0
            return this;
        }

        IStateMachineConfigurator<TStateNode> IStateMachineConfigurator<TStateNode>.SetFixedTimeProvider(Func<float> provider) => SetFixedTimeProvider(provider);
        public StateMachine<TStateNode> SetFixedTimeProvider(Func<float> provider)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (provider == null) { _debug.LogErr($"[{MachineId}] FixedTimeProvider为null"); return this; }
            this._fixedTimeProvider = provider;
            this.FixedTimeInState = 0f;
            return this;
        }

        void IStateMachineDebugger.Catch<TException>(Func<TException, bool> errorHandler) => Catch(errorHandler);
        void IExternalStateMachine.Catch<TException>(Func<TException, bool> errorHandler) => Catch(errorHandler);
        /// <summary>注册异常处理器，根据返回值动态决定是否消费异常 (true: 消费, false: 继续抛出)</summary>
        public StateMachine<TStateNode> Catch<TException>(Func<TException, bool> errorHandler) where TException : Exception
        {
            if (IsDisposed || errorHandler == null) return this;

            _errorHandlers ??= new List<(Type, Func<Exception, bool>)>();
            _errorHandlers.Add((typeof(TException), ex =>
            {
                if (ex is TException typedEx)
                {
                    return errorHandler.Invoke(typedEx);
                }
                return false;
            }
            ));

            return this;
        }

        /// <summary>为强类型共享变量统一注入初始值。（在初始化状态机之前调用，已加入路由时会覆盖路由器的值）</summary>
        [Obsolete("请直接使用 machine.Variables.Get<TVar, TValue>().SetBaseValueAndReset(..., notify: false)")]
        public StateMachine<TStateNode> InitSharedVar<TVar, TValue>(TValue baseValue) where TVar : SharedVar<TValue>, new()
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (IsRunning && DebugMode == false)
            {
                _debug.LogWarn("运行时禁止初始化 SharedVar 初始值。");
                return this;
            }

            Type exactType = typeof(TVar);
            // 直接强转公开接口 ISharedVar<TValue>，不再依赖任何内部泛型接口
            var localVar = (ISharedVar<TValue>)VariableManager.GetOrAddLocalSharedVarBoxed(exactType);
            localVar.SetBaseValueAndReset(baseValue, notify: false);

            if (_parentMachine != null)
            {
                _debug.LogVerbose("InitSharedVar 在初始化前已经加入父级，初始化本地的同时执行推送，确保数据一致", LogColor.Grey, true);
                var globalVar = (ISharedVar<TValue>)_parentMachine.GetOrAddSharedVarBoxed(exactType);
                globalVar.SetBaseValueAndReset(baseValue, notify: false);
            }

            return this;
        }

        //!--- [状态机路由配置] ---
        // --- Router 相关数据 ---
        private IRoutableMachine _parentMachine;
        IRoutableMachine IRoutableMachine.ParentMachine => _parentMachine;
        // 递归属性 获取根节点
        IRoutableMachine IRoutableMachine.RootMachine => _parentMachine != null ? _parentMachine.RootMachine : this;

        InternalRouterModule IRoutableMachine.RouterModule => RouterModule;

        bool IRoutableMachine.CanProcessEvent => this.CanProcessEvent;
        private int _routerBasePriority;

        private DispatchPriorityEvaluator _dispatchPriorityEvaluator;
        private CrossTalkEvaluator _crossTalkEvaluator;

        // 显式实现接口供 Router 查询
        int IRoutableMachine.BasePriority => _routerBasePriority;
        int IRoutableMachine.GetDispatchEventPriority(Type eventType)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
            }
            if (_dispatchPriorityEvaluator != null)
            {
                int p = _dispatchPriorityEvaluator.GetPriority(eventType);
                if (p != int.MinValue) return p;
            }
            return int.MinValue; // 代表未配置，Router 看到会去取 BasePriority 兜底
        }

        // ================== [显式实现路由配置接口] ==================
        void IRouterRuleReceiver.ExecuteJoinParent(RouterJoinRequest request)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return;
            }
            if (this._parentMachine != null) { _debug.LogWarn("加入失败：必须先执行 LeaveParent() 离开原有父级节点"); return; }
            if (request.ParentMachine == null) { _debug.LogErr("加入失败：目标节点为null，检查是否已完成初始化？"); return; }
            if (request.ParentMachine.IsDisposed) { _debug.LogWarn("加入失败：目标节点已销毁"); return; }

            CheckCyclicReference(request.ParentMachine);

            // ================== 解析并应用 CrossTalk 配置 ==================
            if (request.CrossTalkAll)
            {
                _crossTalkEvaluator ??= new CrossTalkEvaluator();
                _crossTalkEvaluator.SetCrossTalkAll(true);
            }

            if (request.CrossTalkEvents != null && request.CrossTalkEvents.Count > 0)
            {
                _crossTalkEvaluator ??= new CrossTalkEvaluator();
                for (int i = 0; i < request.CrossTalkEvents.Count; i++)
                {
                    _crossTalkEvaluator.AddType(request.CrossTalkEvents[i]);
                }
            }

            // ==================  应用 Dispatch 优先级与 Ignore 规则 ==================
            if (request.EventPriorities != null && request.EventPriorities.Count > 0)
            {
                _dispatchPriorityEvaluator ??= new DispatchPriorityEvaluator();
                foreach (var kvp in request.EventPriorities)
                {
                    _dispatchPriorityEvaluator.SetPriority(kvp.Key, kvp.Value);
                }
            }

            // ================== 执行本地数据向 Router 的推送 ==================
            IEnumerable<Type> varsToPush = null;

            if (request.PushAllSharedVars)
            {
                varsToPush = VariableManager.GetLocalVariableTypes();
            }
            else if (request.PushSharedVars != null && request.PushSharedVars.Count > 0)
            {
                varsToPush = request.PushSharedVars;
            }

            if (varsToPush != null)
            {
                VariableManager.PushToParent(request.ParentMachine, varsToPush);
            }

            // 正式连入网络 (作为子节点挂载到父节点)
            _parentMachine = request.ParentMachine;
            _routerBasePriority = request.BasePriority;
            _parentMachine?.RegisterChild(this);
            // 当加入网络时，刷新所有的引用为 Router 全局引用
            RebindSharedVariables();

            _autoLeaveRegistration.Dispose(); // 清理可能存在的旧注册

            if (request.CancellationToken.CanBeCanceled)
            {
                bool syncData = request.AutoLeaveSyncFromParent;

                if (request.CancellationToken.IsCancellationRequested)
                {
                    LeaveParent(syncDataFromParent: syncData);
                    return;
                }

                _autoLeaveRegistration = request.CancellationToken.Register(() =>
                {
                    // 在主线程执行脱离与数据同步
                    LeaveParent(syncDataFromParent: syncData);
                });
            }

            _debug.LogInfo($"已加入父级节点: <color=green>[{_parentMachine.MachineId}]</color>");
        }

        // 防环溯源检测方法
        private void CheckCyclicReference(IRoutableMachine targetParent)
        {
            if (targetParent == this)
                throw new Exception($"[StateMachine] 致命错误！[{this.MachineId}] 试图将自己设置为父节点！");

            IRoutableMachine current = targetParent;
            while (current != null)
            {
                // 顺着目标父节点的族谱往上找，如果碰到了自己，说明 targetParent 其实是自己的子孙
                if (current == (IRoutableMachine)this)
                {
                    throw new Exception($"[StateMachine] 致命错误！检测到循环引用（死循环图）！" +
                        $"[{this.MachineId}] 试图作为子节点挂载到 [{targetParent.MachineId}] 下，" +
                        $"但 [{targetParent.MachineId}] 已经是 [{this.MachineId}] 的子孙节点！");
                }
                current = current.ParentMachine;
            }
        }

        /// <summary>
        /// 准备加入状态机路由器，返回配置器以设置去中心化的路由规则。
        /// 必须调用 .Apply() 才会真正连入网络
        /// </summary>
        public RouterConnectionConfigurator JoinParent(IReadOnlyStateMachine parentMachine)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
            }
            if (parentMachine == null)
            {
                this.OnError(new ArgumentNullException("加入路由失败：目标状态机不能为 null。"));
            }

            if (parentMachine is not IRoutableMachine routableParent)
            {
                throw new ArgumentException($"[{MachineId}] 加入路由失败：传入的 parentMachine 类型({parentMachine.GetType().Name}) 未实现底层路由接口。");
            }

            // 内部依然使用强类型的 routableParent 传递给 Configurator
            return new RouterConnectionConfigurator(this, routableParent);
        }

        IRouterNode IRouterNode.LeaveParent(bool syncFromParent) => LeaveParent(syncFromParent);
        /// <summary>
        /// 离开状态机路由器并将共享变量降级为本地
        /// </summary>
        /// <param name="syncDataFromRouter">是否将离开瞬间 Router 上的最新数据同步保留到本地大盘？(默认true)</param>
        public StateMachine<TStateNode> LeaveParent(bool syncDataFromParent = true)
        {
            _autoLeaveRegistration.Dispose();

            if (_parentMachine == null || IsDisposed) return this;

            var oldParent = _parentMachine;
            _parentMachine = null; // 1. 先切断与父级的网络连接

            if (_isDisposeRequested == false)
            {
                if (syncDataFromParent)
                {
                    RebindSharedVariables(); // 强制整棵子树重新绑定
                    VariableManager.SyncFromParent(oldParent); // 2. 安全大盘降级同步 (内部已做快照)
                }
                else
                {
                    RebindSharedVariables();
                }

                _debug.LogInfo($"已断开与父级: <color=yellow>[{oldParent.MachineId}]</color>的连接，降级为独立根节点{(syncDataFromParent ? "(已同步最新数据)" : "")}。");
            }

            oldParent.UnregisterChild(this);
            _dispatchPriorityEvaluator?.Clear();
            _crossTalkEvaluator?.Clear();

            return this;
        }

        void IRoutableMachine.RegisterChild(IRoutableMachine child) => RouterModule.RegisterChild(child);
        void IRoutableMachine.UnregisterChild(IRoutableMachine child) => RouterModule.UnregisterChild(child);
        void IRoutableMachine.InvalidateRouterCache() => RouterModule.InvalidateCache();
        void IRoutableMachine.RebindSharedVariables() => RebindSharedVariables();

        // 供 FSM 初始化或连接 Router 时重新绑定内存池
        private void RebindSharedVariables()
        {
            if (_stateMap == null) return;

            // 1. 反射注入状态类内部的 SharedVar 字段
            foreach (var stateKvp in _stateMap)
            {
                ReflectionProcessor?.InjectSharedValues(stateKvp.Value, stateKvp.Key, this);
            }

            // 2. 通知所有绑定的宿主更新引用（委托给 VariableManager）
            VariableManager.NotifyReferenceBinders();

            // 3. 递归通知所有挂载的子状态机
            RouterModule.RebindChildrenSharedVariables();
        }

        // 共享变量底层注册接口直通 Manager
        object ISharedVariableRegistry.GetOrAddSharedVarBoxed(Type exactType)
            => VariableManager.GetOrAddSharedVarBoxed(exactType);

        /// <summary>
        /// 供宿主调用：绑定共享变量的引用。
        /// 立即回调注入当前引用，并在状态机加入/离开 Router (触发大盘重绑) 时自动更新宿主的引用。
        /// 宿主与状态机同生命周期，无需注销。
        /// </summary>
        [Obsolete("建议改用 machine.Variables.BindReference<TVar>(binder)")]
        public StateMachine<TStateNode> BindSharedVarReference<TVar>(Action<TVar> binder) where TVar : class, ISharedVar
        {
            if (IsDisposed) return this;
            VariableManager.BindReference(binder);
            return this;
        }
        [Obsolete("建议改用 machine.Variables.Get<TVar, TValue>()")]
        public IReadOnlySharedVar<TValue> GetReadOnlySharedVar<TVar, TValue>() where TVar : SharedVar<TValue>
            => VariableManager.Get<TVar, TValue>();

        /// <summary>
        /// 直接获取当前大盘底层的共享变量实例。
        /// 警告：持有此实例并在其上调用 .Subscribe() 在状态机加入/脱离 Router 时会导致断流失效，应改用 machine.Variables.Get
        /// </summary>
        [Obsolete("建议改用 machine.Variables.GetRaw<TVar>()")]
        public TVar GetRawSharedVariable<TVar>() where TVar : class, ISharedVar
            => VariableManager.GetRaw<TVar>();

        [Obsolete("建议改用 machine.Variables.Set<TVar, TValue>(value, notify)")]
        public void SetSharedValue<TVar, TValue>(TValue value) where TVar : SharedVar<TValue>
            => VariableManager.Set<TVar, TValue>(value);

        [Obsolete("建议改用 machine.Variables.Read<TVar, TValue>()")]
        public TValue ReadSharedValue<TVar, TValue>() where TVar : SharedVar<TValue>
                    => VariableManager.Read<TVar, TValue>();

        //!--- [反射自动注册] ---
        IStateMachineConfigurator<TStateNode> IStateMachineConfigurator<TStateNode>.AutoRegisterNestedStates(object owner, Type declaringType) => AutoRegisterNestedStates(owner, declaringType);
        /// <summary>
        /// 自动扫描并注册嵌套在指定宿主类中的所有状态类。
        /// IL2CPP 把配套的脚本 StateMachinePreserveGenerator 放入 Assets/Editor 内自动生成 link.xml 
        /// IL2CPP 的其他方案 1. 添加 [Preserve] 2. 设置 ManagedStrippingLevel.Disabled 
        /// </summary>
        /// <param name="owner">状态宿主实例 (将作为参数传入状态类的构造函数)</param>
        /// <param name="declaringType">包含嵌套状态类的类型。如果留空，将自动使用 owner.GetType()</param>
        public StateMachine<TStateNode> AutoRegisterNestedStates(object owner, Type declaringType = null)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (!_useReflection) { _debug.LogWarn("反射功能处于关闭状态"); return this; }
            if (_isAutoRegisterExecuted) return this;
            _isAutoRegisterExecuted = true; // 实例级别的防重扫描标记

            if (IsRunning && DebugMode == false)
            {
                _debug.LogWarn("运行时禁止添加状态（必须先停止）");
                return this;
            }
            this.SetOwner(owner);

            // 如果未指定声明类型，则默认查找 owner 自身的类型
            Type targetType = declaringType ?? owner.GetType();

            // 1. 确保全局缓存字典已初始化
            if (_globalNestedStateCache == null) _globalNestedStateCache = new Dictionary<Type, List<Type>>();

            // 2. 如果缓存中没有该宿主的嵌套类型，执行反射并缓存 (仅此一次)
            if (!_globalNestedStateCache.TryGetValue(targetType, out List<Type> validNestedTypes))
            {
                validNestedTypes = new List<Type>();
                var nestedTypes = targetType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);

                foreach (var type in nestedTypes)
                {
                    if (type.IsClass && !type.IsAbstract && !type.IsGenericType && !type.IsGenericTypeDefinition && typeof(TStateNode).IsAssignableFrom(type))
                    {
                        validNestedTypes.Add(type);
                    }
                }
                _globalNestedStateCache[targetType] = validNestedTypes;
            }

            // 3. 从缓存列表中快速实例化状态
            int registeredCount = 0;
            foreach (var type in validNestedTypes)
            {
                try
                {
                    var stateInstance = (TStateNode)Activator.CreateInstance(type, nonPublic: true);
                    RegisterState(stateInstance);
                    registeredCount++;
                }
                catch (MissingMethodException e)
                {
                    _debug.LogErr($"自动实例化状态 [{type.Name}] 失败！\n原因：找不到无参构造函数。请确保该状态类具有默认的无参构造。");
                    this.OnError(e);
                }
                catch (Exception e)
                {
                    _debug.LogErr($"自动实例化状态 [{type.Name}] 时发生未知异常: <color=white>{e.Message}</color>");
                    this.OnError(e);
                }
            }

            if (registeredCount == 0)
            {
                _debug.LogWarn($"[提示] 自动注册扫描完成，但找到了 0 个状态！请确保状态类是声明在宿主类 [{targetType.Name}] 内部的嵌套类，并且继承自 [{typeof(TStateNode).Name}]。");
            }
            else
            {
                _debug.LogVerbose($"自动注册完成，共注册了 <color=green>[{registeredCount}]</color> 个嵌套状态。", LogColor.Grey, true);
            }

            return this;
        }

        IStateMachineConfigurator<TStateNode> IStateMachineConfigurator<TStateNode>.AutoRegisterExternalStates(object owner) => AutoRegisterExternalStates(owner);
        /// <summary>
        /// 自动扫描并注册外部所有状态类。（带全局静态缓存）
        /// 备注: 性能敏感场景建议手动注册
        /// </summary>
        public StateMachine<TStateNode> AutoRegisterExternalStates(object owner, Assembly targetAssembly = null)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }
            if (_isAutoRegisterExecuted) return this;

            if (!_useReflection) { _debug.LogWarn("反射功能处于关闭状态"); return this; }
            _isAutoRegisterExecuted = true;
            this.SetOwner(owner);

            Type baseType = typeof(TStateNode);

            // 1. 如果缓存中没有该基类的数据，才执行一次全盘扫描（只会在第一个对象创建时执行一次）
            if (_globalStateCache == null) _globalStateCache = new Dictionary<Type, List<Type>>();

            if (!_globalStateCache.TryGetValue(baseType, out List<Type> validStateTypes))
            {
                validStateTypes = new List<Type>();
                // 如果传入了具体程序集，直接极速获取；否则走全局缓存的 AllTypes
                IEnumerable<Type> allTypes = targetAssembly != null
                    ? targetAssembly.GetTypes()
                    : AssemblyTypeHolder.AllTypes;

                foreach (var type in allTypes)
                {
                    if (type.IsClass && !type.IsAbstract && !type.IsGenericType && baseType.IsAssignableFrom(type))
                    {
                        validStateTypes.Add(type);
                    }
                }
                _globalStateCache[baseType] = validStateTypes;
                _debug.LogVerbose($"[全局缓存] 首次扫描程序集完成，找到 {validStateTypes.Count} 个继承自 {baseType.Name} 的外部状态。", LogColor.Grey, true);
            }

            // 2. 从缓存的 Type 列表中快速实例化
            int registeredCount = 0;
            foreach (var stateType in validStateTypes)
            {
                try
                {
                    var stateInstance = (TStateNode)Activator.CreateInstance(stateType, nonPublic: true);
                    RegisterState(stateInstance);
                    registeredCount++;
                }
                catch (Exception e)
                {
                    _debug.LogErr($"实例化外部状态 [{stateType.Name}] 失败: <color=white>{e.Message}</color>");
                    this.OnError(e);
                }
            }

            _debug.LogVerbose($"外部状态自动注册完成，共注册了 <color=green>[{registeredCount}]</color> 个状态。", LogColor.Grey, true);
            return this;
        }

        //! --- [核心功能] ---
        // ======= 状态切换请求参数包 =======
        private struct TransitionRequest
        {
            public Type TargetType;
            public bool ForceReenter;
            public bool IsRollback;
            public bool IgnoreRules;
            public object PayloadData;
            public Action OnTransitionCallback;

            public Action<object> OnTransitionCallbackWithPayload;
            public object EventPayloadData;

            public string TriggerInfo;

            // 构造函数：强制要求传入目标状态，并设置安全默认值
            public TransitionRequest(Type targetType)
            {
                TargetType = targetType; // 目标
                ForceReenter = false; // 重入
                IsRollback = false; // 回退
                IgnoreRules = false; // 忽略规则
                PayloadData = null; // 数据注入
                OnTransitionCallback = null; // 切换期间回调
                OnTransitionCallbackWithPayload = null;
                EventPayloadData = null;
                TriggerInfo = "Manually";
            }
        }

        // 仅供StateBase内部调用: 实现 IStateMachine 接口的 ChangeState (显式实现以隐藏非泛型API) 
        bool IStateMachine.ChangeState(Type targetType, bool forceReenter)
        {
            var req = new TransitionRequest(targetType) { ForceReenter = forceReenter };
            return ChangeState(ref req);
        }

        // 仅供StateBase内部调用: 带 payload 委托的方法
        bool IStateMachine.ChangeState(Type targetType, bool forceReenter, object payloadData)
        {
            var req = new TransitionRequest(targetType) { ForceReenter = forceReenter, PayloadData = payloadData };
            return ChangeState(ref req);
        }

        /// <summary>供外部调用切换入口</summary>
        public bool ChangeState<T>(bool forceReenter = false) where T : TStateNode
        {
            var req = new TransitionRequest(typeof(T)) { ForceReenter = forceReenter };
            return ChangeState(ref req);
        }

        /// <summary>供外部调用切换入口（带参数）检查目标状态是否已标记 [Payload] 字段</summary>
        public bool ChangeState<T>(object data, bool forceReenter = false) where T : class, TStateNode
        {
            var req = new TransitionRequest(typeof(T)) { ForceReenter = forceReenter, PayloadData = data };
            return ChangeState(ref req);
        }

        /// <summary>供外部调用，回退到上一个状态</summary>
        public bool RevertToPreviousState(object payloadData, bool ignoreTransitionRule, Type skipGroupType)
        {
            var req = new TransitionRequest(null)
            {
                PayloadData = payloadData,
                IgnoreRules = ignoreTransitionRule
            };
            return ExecuteRevert(ref req, skipGroupType);
        }

        // 供自动流转引擎内部调用的回退方法 (支持过渡回调) 
        private bool ExecuteRevert(ref TransitionRequest req, Type skipGroupType = null)
        {
            if (!IsRunning) return false;
            if (_stateHistory.Count == 0)
            {
                _debug.LogInfo("回退失败: 没有历史状态记录", LogColor.Grey, true);
                return false;
            }
            if (IsChangingState)
            {
                _debug.LogWarn("禁止在状态切换期间调用回退操作");
                return false;
            }

            int targetIndex = _stateHistory.Count - 1;

            // 在历史记录中往前找，跳过特定状态组，且自动过滤连续相同的当前状态
            while (targetIndex >= 0)
            {
                Type historyType = _stateHistory[targetIndex].StateType;
                bool isInSkipGroup = skipGroupType != null && skipGroupType.IsAssignableFrom(historyType);
                bool isSameAsCurrent = historyType == CurrentStateType;

                if (isInSkipGroup || isSameAsCurrent)
                    targetIndex--;
                else
                    break;
            }

            if (targetIndex < 0)
            {
                _debug.LogInfo($"回退失败: 历史记录中没有找到非 [{skipGroupType?.Name}] 的状态", LogColor.Grey, true);
                return false;
            }

            Type targetType = _stateHistory[targetIndex].StateType;

            var removedHistory = _stateHistory.GetRange(targetIndex, _stateHistory.Count - targetIndex);
            _stateHistory.RemoveRange(targetIndex, _stateHistory.Count - targetIndex);

            req.TargetType = targetType;
            req.IsRollback = true;

            bool success = ChangeState(ref req);

            if (!success)
            {
                //  如果切换失败，把刚刚弹出的整段历史记录还回去
                _stateHistory.AddRange(removedHistory);
                _debug.LogInfo($"回退失败: [{targetType.Name}] 恢复记录", LogColor.Grey, true);
            }

            return success;
        }

        /// <summary>
        /// * 底层核心状态切换逻辑
        /// </summary>
        private bool ChangeState(ref TransitionRequest request)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }

            // 目标状态
            Type targetType = request.TargetType;
            // 允许强制重入
            bool forceReenter = request.ForceReenter;
            // 是否回退(IsChangingState为true时被禁止回退)
            bool isRollback = request.IsRollback;
            // 是否无视规则
            bool ignoreRules = request.IgnoreRules;
            // 注入的数据 为null时重置或根据[AutoLink]保活
            object payloadData = request.PayloadData;
            // 切换时期的回调
            Action onTransitionCallback = request.OnTransitionCallback;

            string triggerInfo = request.TriggerInfo;
            // 检查是否在主线程
            Assert.IsTrue(UnityEngine.Debug.isDebugBuild ?
                System.Threading.Thread.CurrentThread.ManagedThreadId == 1
                : true, $"<b>StateMachine 必须在主线程执行！</b>");

            if (!IsRunning)
            {
                _debug.LogWarn("未初始化或处于停止状态");
                LastTransitionResult = TransitionCheckResult.Uninitialized;
                return false;
            }
            if (CurrentState == null || CurrentState?.IsDisposed == true)
            {
                LastTransitionResult = TransitionCheckResult.Uninitialized;
                throw new Exception($"当前状态为 Null 或 Disposed");
            }

            // 查找 newState 实例，找不到状态时终止
            if (targetType == null || !_stateMap.TryGetValue(targetType, out TStateNode newState))
            {
                LastTransitionResult = TransitionCheckResult.Uninitialized;
                throw new Exception($"未注册状态:{(targetType == null ? "Null" : targetType.Name)}");
            }

            if (newState.IsDisposed || !newState.HasInitialized)
            {
                LastTransitionResult = TransitionCheckResult.Uninitialized;
                throw new Exception($"目标状态:[{targetType.Name}]未完成初始化或已被 Disposed");

            }
            // 当状态一致并且 forceReenter = false 时禁止切换
            if (CurrentStateType == targetType && forceReenter == false)
            {
                LastTransitionResult = TransitionCheckResult.AlreadyInState;
                _debug.LogWarn($"正尝试切换相同的状态[{targetType.Name}]，是否忘记设置forceReenter？");
                return false;
            }

            // 当遵守规则时
            if (ignoreRules == false && IsAllGuardConditionMeet(_guardConditions, targetType) == false)
            {
                bool isEvent = triggerInfo != "Update" && triggerInfo != "FixedUpdate" && triggerInfo != "PostUpdate" && triggerInfo != "Manually";
                if (isEvent || LastTransitionResult != TransitionCheckResult.GuardBlocked)
                {
                    _debug.LogVerbose($"拦截:<color=yellow>[{CurrentStateName}] → [{targetType.Name}](守卫拦截)</color>", LogColor.Grey, true);
                }
                LastTransitionResult = TransitionCheckResult.GuardBlocked;
                return false;
            }

            // ChangingState 锁定中时先加入队列后 return
            if (IsChangingState)
            {

                _debug.LogVerbose($"已将 [{targetType.Name}] 加入等待队列，切换将在当前帧完成。", LogColor.Grey, true);
                // 连带委托一起排队

                _nextStateQueue.Enqueue(request);
                LastTransitionResult = TransitionCheckResult.IsChanging;
                return true;
            }

            IsChangingState = true;//锁定
            NextStateType = targetType;
            transitionDepth++; // 深度 +1
            try
            {
                // 防死循环保护
                if (transitionDepth > MAX_DEPTH)
                {
                    // 获取最近几次的历史记录来展示死循环轨迹
                    string historyPath = string.Join(" -> ", _stateHistory.Skip(Math.Max(0, _stateHistory.Count - 4)).Select(t => t.StateType.Name));

                    _debug.LogErr($"检测到状态切换死循环 (深度>{MAX_DEPTH})！" +
                        $"切换轨迹: {historyPath} -> {CurrentStateName} -> {targetType.Name}。已强行中断！");
                    _nextStateQueue.Clear();
                    IsChangingState = false;
                    transitionDepth--;

                    LastTransitionResult = TransitionCheckResult.InfiniteLoop;
                    this.Stop();
                    throw new Exception($"[{MachineId}]: 死循环");
                }

                // 非回退(回退时不记录历史)
                if (isRollback == false && CurrentStateType != null)
                {
                    StateHistoryRecorder history = new()
                    {
                        StateType = CurrentStateType,
                        Time = MachineTime,
                        TriggerInfo = triggerInfo,
                        TransitionCount = SuccessfulTransitionCount
                    };
                    _stateHistory.Add(history);
                    if (_stateHistory.Count > MaxHistoryCapacity)
                    {
                        _stateHistory.RemoveAt(0);
                    }
                    // 排除回退
                    LastStateType = CurrentStateType;
                }
                //~ 广播旧状态的退出
                try { EventManager.InvokeExit(CurrentStateType, CurrentState); }
                catch (Exception e) { _debug.LogErr($"<{CurrentStateName}> Exit 事件通知发生异常: <color=white>{e.Message}</color>", e); }

                // 记录从哪个状态切换而来
                ReallyLastStateType = CurrentStateType;

                try
                {
                    // 在OnExit前执行AutoRun
                    ReflectionProcessor?.InvokeAutoRun(CurrentState, Timing.BeforeExit);

                    //: 退出旧状态
                    CurrentState.OnExit();

                    ReflectionProcessor?.InvokeAutoRun(CurrentState, Timing.AfterExit);

                    // [AutoReset] 在抓取前恢复 这样确保 AutoReset+AutoLink 时能恢复默认值 A-B-C 此时 C 收到数据后重置
                    ReflectionProcessor?.ResetStateFields(CurrentState); //: Reset 旧状态

                    // [AutoLink] 抓取快照 如果被清理则抓取默认值 可切断对外部的引用 (重置为null)
                    ReflectionProcessor?.SnapshotLinkData(CurrentState, CurrentStateType); //: Link抓取
                }
                catch (Exception e)
                {
                    _debug.LogErr($"切换状态时<{CurrentStateName}>的 [OnExit] 流程发生异常: <color=white>{e.Message}</color>");
                    throw;
                }
                finally
                {
                    // 关闭标记与 清理Token
                    CurrentState.PostExit();
                }

                //# ↑↑↑ 以上(CurrentState)为旧状态 ↑↑↑
                CurrentState = newState;
                CurrentStateType = targetType;
                //# ↓↓↓ (CurrentState)赋值为新状态 ↓↓↓

                _debug.LogInfo($"{(isRollback ? "<color=grey>回退</color>" : "切换")}: " +
                            $"<b>[{ReallyLastStateType.Name}] <color=green>→</color> <color=white>[{CurrentStateType.Name}]</color></b>" +
                            $" 触发: <color=lightblue>{triggerInfo}</color>");

                // 刷新 AutoRun 指针
                RefreshActiveReflectionPointers(CurrentStateType); // 抓取

                RefreshActiveTransitionRules(CurrentStateType);

                // 重置时间
                ResetStateTimer();

                //  [AutoReset]恢复字段的默认值 (AutoReset) (再次Reset) (这里是新状态)
                ReflectionProcessor?.ResetStateFields(CurrentState); //: 再次Reset 新状态

                // 2. [AutoLink] 在重置默认值之后，Payload 覆盖之前进行快照恢复 
                ReflectionProcessor?.InjectLinkData(CurrentState, CurrentStateType); //: Link注入

                if (payloadData != null)
                {
                    // 3. [Payload] 注入 (优先级最高，会覆盖 Link 数据)
                    ReflectionProcessor?.InjectPayloadData(CurrentState, targetType, payloadData); //: Payload注入
                }
                else if (payloadData == null) // 代表外部是无参切换 ChangeState<T>()
                {
                    // [Payload] 重置或保活 （会忽略_successfullyLinkedFields 也就是 [AutoLink] 标签的字段，保活）
                    ReflectionProcessor?.InjectPayloadData(CurrentState, targetType, null);  //: Payload重置或保活
                }

                // [AutoInject] 注入依赖 永远保持最新
                ReflectionProcessor?.InjectContextData(CurrentState, CurrentStateType, this);//: 注入Context

                // Builder 植入的回调 进入状态之前执行
                if (onTransitionCallback != null)
                {
                    try { onTransitionCallback.Invoke(); }
                    catch (Exception e) { _debug.LogErr($"切换状态时 [OnTransition] 回调发生异常: <color=white>{e.Message}</color>", e); }
                }
                if (request.OnTransitionCallbackWithPayload != null && request.EventPayloadData != null)
                {
                    try { request.OnTransitionCallbackWithPayload.Invoke(request.EventPayloadData); }
                    catch { throw; }
                }
                try
                {
                    CurrentState.PreEnter();// 激活 _isActive 标志位

                    if (isRollback == false || CurrentState.SkipEnterOnRollback == false)
                    {
                        ReflectionProcessor?.InvokeAutoRun(CurrentState, Timing.BeforeEnter);

                        CurrentState.OnEnter(); //: 进入新状态

                        ReflectionProcessor?.InvokeAutoRun(CurrentState, Timing.AfterEnter);

                    }
                    else
                    {
                        CurrentState.OnResume();
                    }
                }
                catch (Exception e)
                {
                    _debug.LogErr($"切换状态时<{CurrentStateName}>的 [OnEnter] 流程发生异常: <color=white>{e.Message}</color>");
                    throw;
                }

                LastTransitionResult = TransitionCheckResult.Allowed;
                SuccessfulTransitionCount++; // 成功切换的计数
                LastSuccessfulTransitionTime = MachineTime;// 记录本次切换的时间

                //~ 广播新状态的进入
                try { EventManager.InvokeEnter(CurrentStateType, CurrentState); }
                catch (Exception e) { _debug.LogErr($"<{CurrentStateName}> Enter 事件通知发生异常: <color=white>{e.Message}</color>", e); }
            }
            // 确保必定解锁
            finally
            {
                IsChangingState = false;
                NextStateType = null;
                try
                {
                    // 确保正在运行
                    while (_nextStateQueue.Count > 0 && IsRunning && !IsDisposed)
                    {
                        var nextRequest = _nextStateQueue.Dequeue();
                        ChangeState(ref nextRequest);
                    }
                }
                finally
                {
                    transitionDepth--; // 这一层结束，深度 -1 
                }
            }
            return true;
        }

        //!--- [事件分发] ---
        /// <summary>
        /// 【全网派发/广播】自动溯源至最顶层根节点，并由根节点自顶向下分发给整棵状态机树中的所有节点。(含自身)
        /// 备注：未联网时等价于 Dispatch
        /// </summary>
        public bool Broadcast<TPayload>(TPayload payload = default)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }

            if (TypeCache<TPayload>.IsReferenceType && EqualityComparer<TPayload>.Default.Equals(payload, default))
            {
                _debug.LogErr("Broadcast 参数省略只适用于struct，引用类型必须传入实例或使用new");
                return false;
            }

            return ((IRoutableMachine)this).RootMachine.Dispatch(payload);
        }

        /// <summary> 
        /// 自顶向下分发指令。依次触发 AutoHandle -> Transition -> ToChildren(未消费) -> InvokeAnyStateEvent
        /// 备注：当事件被消费时，停止传播到其他节点。(本层所有方法无论消费都会处理该事件，不影响事件订阅)
        /// 备注：在停机状态也能使用，仅执行 InvokeAnyStateEvent，可订阅事件后执行 Restart(); 重启状态机
        /// </summary>
        /// <returns>是否被消耗? true: 消耗后不继续对其他节点传播</returns>
        public bool Dispatch<TPayload>(TPayload payload = default)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }

            if (TypeCache<TPayload>.IsReferenceType && EqualityComparer<TPayload>.Default.Equals(payload, default))
            {
                _debug.LogErr("Dispatch 参数省略只适用于struct，引用类型必须传入实例或使用new");
                return false;
            }

            // 本地优先消化
            bool finalConsumed = ProcessLocalEvent(payload);

            // 本地未消费 + 处于可工作状态(运行中/路由模式) -> 向下路由给子状态机
            if (!finalConsumed && CanProcessEvent && RouterModule.HasChildren == true)
            {
                try { finalConsumed |= RouterModule.RouteDispatchEventToChildren(payload); }
                catch (Exception e) { this.OnError(e); }
            }

            // 触发 UI 监听
            try { EventManager.InvokeEvent(CurrentStateType, payload); }
            catch (Exception e) { _debug.LogErr($"事件通知发生异常: <color=white>{e.Message}</color>", e); }

            return finalConsumed;
        }

        void IEventReceiver.Receive<TPayload>(TPayload payload) => DispatchLocal(payload);
        /// <summary>
        /// 【本地定点分发】仅在当前状态机本地消化事件，不向下路由给子状态机，不向上冒泡。
        /// </summary>
        public bool DispatchLocal<TPayload>(TPayload payload = default)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }

            if (TypeCache<TPayload>.IsReferenceType && EqualityComparer<TPayload>.Default.Equals(payload, default))
            {
                _debug.LogErr("DispatchLocal 参数省略只适用于struct，引用类型必须传入实例或使用new");
                return false;
            }

            bool finalConsumed = ProcessLocalEvent(payload);

            // 触发 UI 监听
            try { EventManager.InvokeEvent(CurrentStateType, payload); }
            catch (Exception e) { _debug.LogErr($"事件通知发生异常: <color=white>{e.Message}</color>", e); }

            return finalConsumed;
        }

        /// <summary>
        /// 本地状态消化逻辑抽取 (供 Dispatch 和 DispatchLocal 复用)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ProcessLocalEvent<TPayload>(TPayload payload)
        {
            // 非运行状态（停机/待机），直接跳过本地处理
            if (!IsRunning) return false;

            bool finalConsumed = false;
            try
            {
                // 1. 触发 [AutoHandle] (BeforeTransition)
                if (ReflectionProcessor?.TryInvokeAutoHandle(CurrentStateType, payload, HandleTiming.BeforeTransition, out bool isConsumedBefore) == true)
                    finalConsumed |= isConsumedBefore;

                // 2. 触发状态内 RegisterHandler / ReceiveDispatch
                if (CurrentState != null && CurrentState.ReceiveDispatch(payload))
                    finalConsumed |= true;
            }
            catch (Exception e)
            {
                _debug.LogErr($"<{CurrentStateName}>的 [AutoHandle](Before) 处理事件 [{typeof(TPayload).Name}] 流程发生异常: <color=white>{e.Message}</color>");
                this.OnError(e);
            }

            try
            {
                // 3. 触发基于 Dispatch 的流转规则
                if (_hasAutoTransitions_dispatch && CheckEventTransitions(_activeDispatchRules, payload, out bool isEventConsumed))
                {
                    finalConsumed |= isEventConsumed;
                    try
                    { // 4. 触发 [AutoHandle] (AfterTransition，非延迟切换时立即执行)
                        if (ReflectionProcessor?.TryInvokeAutoHandle(CurrentStateType, payload, HandleTiming.AfterTransition, out bool isConsumedAfter) == true)
                        {
                            finalConsumed |= isConsumedAfter;
                        }
                    }
                    catch (Exception e)
                    {
                        _debug.LogErr($"<{CurrentStateName}>的 [AutoHandle](After) 处理事件 [{typeof(TPayload).Name}] 流程发生异常: <color=white>{e.Message}</color>");
                        this.OnError(e);
                    }
                }
            }
            catch (Exception e)
            {
                _debug.LogErr($"(CheckEventTransitions) 事件 [{typeof(TPayload).Name}] 触发切换流程发生异常: <color=white>{e.Message}</color>");
                this.OnError(e);
            }

            return finalConsumed;
        }

        /// <summary>
        /// (跳过本地) 直接向子状态机分发事件
        /// 备注：不触发本层状态机逻辑，不触发本层 UI 监听。
        /// </summary>
        /// <returns>是否被消耗? true: 该事件被子节点消耗</returns>
        public bool DispatchToChildren<TEvent>(TEvent eventData = default)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }
            if (!CanProcessEvent) return false;

            if (TypeCache<TEvent>.IsReferenceType && EqualityComparer<TEvent>.Default.Equals(eventData, default))
            {
                _debug.LogErr("DispatchToChildren 参数省略只适用于struct，引用类型必须传入实例或使用new");
                return false;
            }

            // 直接交给路由器模块向下转发
            if (RouterModule != null && RouterModule.HasChildren == true)
            {
                try { return RouterModule.RouteDispatchEventToChildren(eventData); }
                catch (Exception e) { this.OnError(e); }
            }
            return false;
        }

        /// <summary>
        /// (跳过本地) 显式指定该事件为 CrossTalk，直接向同层级的兄弟状态机节点发送事件。
        /// 备注：不触发本层状态机逻辑，不触发本层 UI 监听。
        /// </summary>
        /// <returns>是否被消耗? true: 该事件被兄弟节点消耗</returns>
        public bool CrossTalk<TPayload>(TPayload payload = default)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }
            if (!CanProcessEvent) return false;

            if (TypeCache<TPayload>.IsReferenceType && EqualityComparer<TPayload>.Default.Equals(payload, default))
            {
                _debug.LogErr("CrossTalk 参数省略只适用于struct，引用类型必须传入实例或使用new");
                return false;
            }

            // 如果没有连接到父节点，直接返回 false
            if (_parentMachine == null) { _debug.LogWarn("CrossTalk 必须先接入父级节点"); return false; }
            if (!_parentMachine.CanProcessEvent) { _debug.LogVerbose("CrossTalk 父级节点处于停止状态"); return false; }

            // 直接调用父级的路由器，向下分发给所有子节点，同时排除自己 (this)
            try
            {
                return _parentMachine.RouterModule.RouteDispatchEventToChildren(payload, excludeFsm: this) == true;
            }
            catch (Exception e)
            {
                this.OnError(e);
                return false;
            }
        }

        /// <summary>
        /// (跳过本地) 将事件向上冒泡到父级路由器。(可使用 CrossTalkWhenBubbleUp 进行向上和横向的双向投递)
        /// 备注：不触发本层状态机逻辑，不触发本层 UI 监听。
        /// </summary>
        public bool BubbleUp<TPayload>(TPayload payload = default)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }
            if (!CanProcessEvent) return false;

            if (TypeCache<TPayload>.IsReferenceType && EqualityComparer<TPayload>.Default.Equals(payload, default))
            {
                _debug.LogErr("BubbleUp 参数省略只适用于struct，引用类型必须传入实例或使用new");
                return false;
            }

            bool isConsumed = false;

            bool canParentProcess = _parentMachine != null && _parentMachine.CanProcessEvent;

            // 中间层 CrossTalk：通知父节点对本层的兄弟节点进行广播 (排除自己)
            if (canParentProcess)
            {
                bool isCrossTalkWhenBubbleUp = _crossTalkEvaluator != null && _crossTalkEvaluator.IsMatch<TPayload>();

                if (isCrossTalkWhenBubbleUp)
                {
                    try { isConsumed = _parentMachine.RouterModule.RouteDispatchEventToChildren(payload, excludeFsm: this) == true; }
                    catch (Exception e) { this.OnError(e); }
                }
            }

            // 直接向上冒泡，交由父级状态机处理
            if (!isConsumed && _parentMachine != null)
            {
                return _parentMachine.HandleBubbledEvent(payload);
            }

            return isConsumed;
        }

        /// <summary>
        /// (内部方法)供状态类内部调用，向外派发事件，触发本地规则或订阅
        /// 在未消费的情况下向上冒泡(可指定 CrossTalkWhenBubbleUp 对兄弟节点派发)
        /// </summary>
        bool IStateMachine.PublishStateEvent<TEvent>(TEvent eventData)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }
            if (!IsRunning) return false;

            if (TypeCache<TEvent>.IsReferenceType &&
                    EqualityComparer<TEvent>.Default.Equals(eventData, default))
            {
                _debug.LogErr("Publish 参数省略只适用于struct，引用类型必须传入实例或使用new");
                return false;
            }

            bool finalConsumed = false;

            bool canParentProcess = _parentMachine != null && _parentMachine.CanProcessEvent;

            // 1. 优先触发本地基于 Publish 的状态自动流转
            if (_hasAutoTransitions_publish)
            {
                try
                {
                    if (CheckEventTransitions(_activePublishRules, eventData, out bool isConsumedLocally))
                        finalConsumed |= isConsumedLocally;
                }
                catch (Exception e) { this.OnError(e); }
            }

            // 2. 触发外部 UI 订阅 (UI订阅不影响消费标记)
            try { EventManager.InvokeEvent(CurrentStateType, eventData); }
            catch (Exception e) { _debug.LogErr($"事件通知发生异常: <color=white>{e.Message}</color>", e); }

            // 3. 中间层 CrossTalk：通知父节点对兄弟节点进行广播 (排除自己)
            if (!finalConsumed && canParentProcess)
            {
                bool isCrossTalkWhenBubbleUp = _crossTalkEvaluator != null && _crossTalkEvaluator.IsMatch<TEvent>();
                if (isCrossTalkWhenBubbleUp)
                {
                    try
                    {
                        if (_parentMachine.RouterModule.RouteDispatchEventToChildren(eventData, excludeFsm: this) == true)
                            finalConsumed |= true;
                    }
                    catch (Exception e) { this.OnError(e); }
                }
            }

            // 4. 如果没人处理，继续向上冒泡，交由父级状态机当做它自己的内部事件处理！
            if (!finalConsumed && _parentMachine != null)
            {
                if (_parentMachine.HandleBubbledEvent(eventData))
                    finalConsumed |= true;
            }

            return finalConsumed;
        }

        /// <summary>
        /// (内部方法)接收来自子状态机的冒泡事件
        /// </summary>
        bool IRoutableMachine.HandleBubbledEvent<TEvent>(TEvent ev)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }
            if (!CanProcessEvent) return false;

            bool finalConsumed = false;

            // 如果不是纯路由模式，才进行本地拦截和状态迁移
            if (IsRunning)
            {
                try
                {
                    // 优先让父级的当前“活跃状态”拦截（模拟 Dispatch 但不向下路由）
                    // 触发 [AutoHandle]
                    if (ReflectionProcessor?.TryInvokeAutoHandle(CurrentStateType, ev, HandleTiming.BeforeTransition, out bool isConsumedBefore) == true)
                        finalConsumed |= isConsumedBefore;

                    // 触发 ReceiveDispatch 重写
                    if (CurrentState != null && CurrentState.ReceiveDispatch(ev))
                        finalConsumed |= true;

                    // 触发父级 FSM 级别的状态迁移规则
                    // 子节点的冒泡被视为当前状态机发生了一次内部派发
                    if (_hasAutoTransitions_publish)
                    {
                        if (CheckEventTransitions(_activePublishRules, ev, out bool isRuleConsumed))
                        {
                            finalConsumed |= isRuleConsumed;
                            // 触发 [AutoHandle] AfterTransition
                            if (ReflectionProcessor?.TryInvokeAutoHandle(CurrentStateType, ev, HandleTiming.AfterTransition, out bool isConsumedAfter) == true)
                            {
                                finalConsumed |= isConsumedAfter;
                            }
                        }
                    }
                }
                catch (Exception e) { this.OnError(e); }
            }

            // 3. 触发当前父级 FSM 的外部监听器 (UI)
            try { EventManager.InvokeEvent(CurrentStateType, ev); }
            catch (Exception e) { _debug.LogErr($"事件通知发生异常: <color=white>{e.Message}</color>", e); }

            // 4. 中间层 CrossTalk：如果本层没有消化完事件，检查自身是否配置了串话规则
            // 如果配置了，则代为向父节点申请，对本层的兄弟节点进行横向广播 (排除自己)
            bool isConsumedByCrossTalk = false;
            bool canParentProcess = _parentMachine != null && _parentMachine.CanProcessEvent;
            if (!finalConsumed && canParentProcess)
            {
                bool isCrossTalkWhenBubbleUp = _crossTalkEvaluator != null && _crossTalkEvaluator.IsMatch<TEvent>();
                if (isCrossTalkWhenBubbleUp)
                {
                    try { isConsumedByCrossTalk = _parentMachine.RouterModule.RouteDispatchEventToChildren(ev, excludeFsm: this) == true; }
                    catch (Exception e) { this.OnError(e); }
                }
            }

            // 5. 如果本级没有消费，且兄弟节点也没有消费，继续向上方层级冒泡
            if (!finalConsumed && !isConsumedByCrossTalk && _parentMachine != null)
            {
                return _parentMachine.HandleBubbledEvent(ev);
            }

            return finalConsumed;
        }

        //!--- [状态切换判定] ---

        /// <summary>
        /// 检查是否满足轮询条件（Update/FixedUpdate/PostUpdate）并切换状态
        /// </summary>
        private bool CheckAutoTransitions(TransitionRule[] activeRules)
        {
            if (activeRules == null || !IsRunning) return false;

            // 纯线性循环，CPU 缓存行命中极高
            for (int i = 0; i < activeRules.Length; i++)
            {
                var ruleToEval = activeRules[i];

                // 1. 确定目标状态 (支持动态路由)
                Type targetState = ruleToEval.ToState;
                if (ruleToEval.DynamicToStateProvider != null)
                {
                    targetState = ruleToEval.DynamicToStateProvider.Invoke();
                    if (targetState == null) continue;
                }

                // 2. 防重入判定
                if (!ruleToEval.IsRevert && CurrentStateType == targetState && !ruleToEval.ForceReenter)
                    continue;

                // 3. 条件校验
                if (ruleToEval.Condition == null || ruleToEval.Condition.Invoke())
                {
                    object data = ruleToEval.DataProvider?.Invoke();
                    Action transitionCallback = ruleToEval.OnTransitionCallback;

                    if (ruleToEval.IsRevert)
                    {
                        var req = new TransitionRequest(null)
                        {
                            PayloadData = data,
                            IgnoreRules = ruleToEval.IgnoreRules,
                            OnTransitionCallback = transitionCallback,
                            TriggerInfo = ruleToEval.TriggerSourceInfo
                        };
                        if (ExecuteRevert(ref req, ruleToEval.SkipGroupType)) return true;
                    }
                    else
                    {
                        var req = new TransitionRequest(targetState)
                        {
                            ForceReenter = ruleToEval.ForceReenter,
                            PayloadData = data,
                            OnTransitionCallback = transitionCallback,
                            TriggerInfo = ruleToEval.TriggerSourceInfo
                        };
                        if (ChangeState(ref req)) return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 专门用于泛型事件的规则检查
        /// </summary>
        private bool CheckEventTransitions<TEvent>(Dictionary<Type, TransitionRule[]> activeEventRules, TEvent eventData, out bool isConsumed)
        {
            isConsumed = false;

            // 1. 如果当前状态根本没有配置任何事件规则，瞬间退出
            if (!IsRunning || activeEventRules == null) return false;

            // 2. 直接根据 TEvent 的类型精准命中该事件专属的规则数组（O(1) Hash 查找）
            if (!activeEventRules.TryGetValue(typeof(TEvent), out var rules) || rules == null || rules.Length == 0)
                return false;

            // 3. 线性遍历专属于当前 TEvent 的规则（无任何多余的 EventType 判定）
            for (int i = 0; i < rules.Length; i++)
            {
                var ruleToEval = rules[i];

                // 基础条件校验 (When/And/Or)
                if (ruleToEval.Condition != null && !ruleToEval.Condition.Invoke())
                    continue;

                // 强类型载荷条件校验 (强转替代 is 类型探测)
                if (ruleToEval.PayloadCondition != null)
                {
                    var payloadCond = (Func<TEvent, bool>)ruleToEval.PayloadCondition;
                    if (!payloadCond.Invoke(eventData)) continue;
                }

                // 动态计算目标状态
                Type targetState = ruleToEval.ToState;
                if (ruleToEval.DynamicPayloadToStateProvider != null)
                {
                    var typedProvider = (Func<TEvent, Type>)ruleToEval.DynamicPayloadToStateProvider;
                    targetState = typedProvider.Invoke(eventData);
                    if (targetState == null) continue;
                }
                else if (ruleToEval.DynamicToStateProvider != null)
                {
                    targetState = ruleToEval.DynamicToStateProvider.Invoke();
                    if (targetState == null) continue;
                }

                // 防重入
                if (!ruleToEval.IsRevert && CurrentStateType == targetState && !ruleToEval.ForceReenter)
                    continue;

                // 注入数据准备
                object finalData = null;
                if (ruleToEval.DynamicEventDataProvider != null)
                {
                    finalData = ruleToEval.DynamicEventDataProvider.Invoke(eventData);
                }
                else if (ruleToEval.DataProvider != null)
                {
                    finalData = ruleToEval.DataProvider.Invoke();
                }

                // 组装请求（0 GC，直接把原始参数传给 Request）
                var req = new TransitionRequest(targetState)
                {
                    ForceReenter = ruleToEval.ForceReenter,
                    PayloadData = finalData,
                    OnTransitionCallback = ruleToEval.OnTransitionCallback,
                    OnTransitionCallbackWithPayload = ruleToEval.OnTransitionCallbackWithPayload,
                    EventPayloadData = eventData, // 不产生额外闭包
                    TriggerInfo = ruleToEval.TriggerSourceInfo,
                    IgnoreRules = ruleToEval.IgnoreRules,
                    IsRollback = ruleToEval.IsRevert
                };

                bool transitionSuccess = ruleToEval.IsRevert
                    ? ExecuteRevert(ref req, ruleToEval.SkipGroupType)
                    : ChangeState(ref req);

                if (transitionSuccess)
                {
                    isConsumed = EvaluateConsumeCondition(ruleToEval, eventData);
                    return true;
                }
            }

            return false;

            // 本地消费判定逻辑保持不变
            bool EvaluateConsumeCondition(TransitionRule rule, TEvent evt)
            {
                if (rule.IsConsumed) return true;
                if (rule.ConsumeCondition != null)
                {
                    if (rule.ConsumeCondition is Func<bool> boolCond)
                        return boolCond.Invoke();
                    if (rule.ConsumeCondition is Func<TEvent, bool> payloadCond)
                        return payloadCond.Invoke(evt);
                }
                return false;
            }
        }

        // (辅助) 专门生成闭包回调，防止外部作用域产生每帧 GC (供CheckAutoTransitions/CheckEventTransitions使用)
        private static Action CreatePayloadCallback<T>(Action<object> callback, T data)
        {
            return () => callback.Invoke(data);
        }

        //! --- [生命周期] ---

        /// <summary> 供宿主调用: Update </summary>
        public void OnUpdate()
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
            }
            if (!IsRunning) return;
            if (CurrentState == null || CurrentState.IsDisposed) return;

            // 累加时间
            float dt = DeltaTime;
            TimeInState += dt;
            MachineTime += (double)dt;

            // 累加帧数
            FramesInState++;

            // 不会被重置的帧数
            UpdateCallCount++;
            try
            {
                // 在OnUpdate前执行AutoRun
                _activeAutoRunUpdate_Before?.Invoke(CurrentState);

                CurrentState.OnUpdate();

                _activeAutoRunUpdate_After?.Invoke(CurrentState);

                // 检测自动切换
                if (_hasAutoTransitions_update) CheckAutoTransitions(_activeUpdateRules);
            }
            catch (Exception e)
            {
                _debug.LogErr($"轮询时 [OnUpdate] 流程发生异常: <color=white>{e.Message}</color>");
                this.OnError(e);
            }
        }

        /// <summary> 供宿主调用: FixedUpdate </summary>
        public void OnFixedUpdate()
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
            }
            if (!IsRunning) return;
            if (CurrentState == null || CurrentState.IsDisposed) return;

            FixedTimeInState += FixedDeltaTime;

            // 累加物理帧数
            FixedFramesInState++;
            FixedUpdateCallCount++;

            try
            {
                _activeAutoRunFixedUpdate_Before?.Invoke(CurrentState);

                CurrentState.OnFixedUpdate();

                _activeAutoRunFixedUpdate_After?.Invoke(CurrentState);

                if (_hasAutoTransitions_fixed) CheckAutoTransitions(_activeFixedUpdateRules);
            }
            catch (Exception e)
            {
                _debug.LogErr($"轮询时 [OnFixedUpdate] 流程发生异常: <color=white>{e.Message}</color>");
                this.OnError(e);
            }
        }

        /// <summary> 供宿主调用: LateUpdate </summary>
        public void OnPostUpdate()
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
            }
            if (!IsRunning) return;
            if (CurrentState == null || CurrentState.IsDisposed) return;

            PostUpdateCallCount++;

            try
            {
                _activeAutoRunPostUpdate_Before?.Invoke(CurrentState);

                CurrentState.OnPostUpdate();

                _activeAutoRunPostUpdate_After?.Invoke(CurrentState);

                if (_hasAutoTransitions_last) CheckAutoTransitions(_activePostUpdateRules);
            }
            catch (Exception e)
            {
                _debug.LogErr($"轮询时 [OnPostUpdate] 流程发生异常: <color=white>{e.Message}</color>");
                this.OnError(e);
            }
        }

        /// <summary>
        /// 重启/唤醒状态机。
        /// </summary>
        /// <param name="resumeLastState">
        /// 是否恢复到上一次 Stop 时的挂起状态？(默认 false)。
        /// 若为 false，或没有历史挂起记录，则自动回退到默认初始状态。
        /// </param>
        public void Restart(bool resumeLastState = false)
        {
            if (!_hasInitialized || IsRunning || UseRouterMode) return;

            // 智能判定目标状态：如果开启了 resume 且存在上一次停止时的状态，则使用上次的状态；否则回退到默认入口
            Type targetType = (resumeLastState && LastStoppedStateType != null)
                ? LastStoppedStateType
                : _defaultEntryStateType;

            ExecuteRestart(targetType, isResumed: resumeLastState && (targetType == LastStoppedStateType));
        }

        /// <summary>
        /// 重启/唤醒状态机，并强类型精准指定要进入的目标状态。
        /// </summary>
        /// <typeparam name="T">指定要进入的状态类型</typeparam>
        public void Restart<T>() where T : TStateNode
        {

            if (!_hasInitialized || IsRunning || UseRouterMode) return;

            ExecuteRestart(typeof(T), isResumed: false);
        }

        // 统一的内部执行抽取
        private void ExecuteRestart(Type targetType, bool isResumed)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return;
            }
            if (targetType == null || !_stateMap.TryGetValue(targetType, out var targetState))
            {
                _debug.LogErr($"重启失败: 目标状态 [{targetType?.Name ?? "Null"}] 未注册！");
                return;
            }

            _debug.LogInfo($"<color=green>--- 重启状态机: <b><color=white>[{targetType.Name}]</color></b> " +
                        $"{(isResumed ? "<color=grey>(恢复上次挂起状态)</color>" : "")} ---</color>");

            HandleStartStateMachine(targetState);
        }

        /// <summary>停止状态并执行OnExit 可用于对象池</summary>
        public void Stop()
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return;
            }
            if (!IsRunning) return;
            if (CurrentState == null || CurrentState.IsDisposed) return;

            if (!_isDisposeRequested) _debug?.LogInfo("<color=orange>--- 停止状态机 ---</color>");

            LastStoppedStateType = CurrentStateType;

            IsChangingState = false;
            transitionDepth = 0;

            ResetStateTimer();
            RefreshActiveReflectionPointers(null);// 置空
            RefreshActiveTransitionRules(null);

            try
            {
                if (CurrentState != null)
                {
                    ReflectionProcessor?.InvokeAutoRun(CurrentState, Timing.BeforeExit);

                    CurrentState.OnExit();

                    ReflectionProcessor?.InvokeAutoRun(CurrentState, Timing.AfterExit);

                    if (!_isDisposeRequested) ReflectionProcessor?.ResetStateFields(CurrentState);

                    try
                    {
                        EventManager.InvokeExit(CurrentStateType, CurrentState);
                        EventManager.InvokeStop();
                    }
                    catch (Exception e) { _debug.LogErr($"事件通知发生异常: <color=white>{e.Message}</color>", e); }
                }
            }
            catch (Exception e)
            {
                _debug?.LogErr($"停止状态机时 [OnExit] 流程发生异常: <color=white>{e.Message}</color>");
                this.OnError(e);
            }
            finally
            {
                CurrentState?.PostExit();

                ReallyLastStateType = null;
                LastStateType = null;
                CurrentState = null;
                CurrentStateType = null;
                _stateHistory?.Clear();
                _nextStateQueue?.Clear();
                // 停止时清理link缓存 避免引用类型的残留
                ReflectionProcessor?.ClearLinkBlackboard();

                IsRunning = false;
            }
        }

        /// <summary>
        /// 当状态机或当前状态出现异常时调用
        /// </summary>
        private void OnError(Exception ex)
        {
            if (ex == null || IsDisposed) return;

            Type actualType = ex.GetType();
            bool handled = false;

            try
            {
                handled = CurrentState?.OnError(ex) == true;
            }
            catch (Exception stateEx)
            {
                _debug?.LogErr($"状态 [{CurrentStateName}] 自身的 OnError 执行时再次抛出异常:", stateEx);
                if (DebugMode) ExceptionDispatchInfo.Capture(stateEx).Throw();
            }

            //  匹配并执行状态机上注册的 Catch 处理器
            if (_errorHandlers != null && _errorHandlers.Count > 0)
            {
                for (int i = 0; i < _errorHandlers.Count; i++)
                {
                    var entry = _errorHandlers[i];
                    if (entry.ExceptionType.IsAssignableFrom(actualType))
                    {
                        try
                        {
                            // 根据 handler 的返回值动态累加 handled 标记
                            handled |= entry.Handler.Invoke(ex);
                        }
                        catch (Exception handlerEx)
                        {
                            _debug?.LogErr($"执行错误处理器 Catch<{entry.ExceptionType.Name}> 时抛出二次异常:", handlerEx);
                            if (DebugMode) ExceptionDispatchInfo.Capture(handlerEx).Throw();
                        }
                    }
                }
            }

            // 如果没有任何处理器接盘
            if (!handled)
            {
                try
                {
                    if (IsRunning)
                    {
                        string historyPath = string.Join(" -> ", _stateHistory.Skip(Math.Max(0, _stateHistory.Count - 4)).Select(t => t.StateType.Name));
                        string lastStateName = CurrentStateName;

                        _debug?.LogWarn($"切换轨迹: {historyPath} -> <color=red>[{lastStateName}]</color>");

                    }
                    this.Stop();

                    _debug?.LogWarn($"捕获到未处理的异常 [{actualType.Name}]: {ex.Message}");
                }
                finally
                { ExceptionDispatchInfo.Capture(ex).Throw(); }
            }
        }

        bool _isDisposeRequested;

        /// <summary>执行完全的清理</summary>
        public void Dispose()
        {
            if (IsDisposed) return;

            _isDisposeRequested = true;
            _debug?.LogVerbose("--- 开始执行状态机销毁程序 ---", LogColor.Grey, true);

            _lifecycleRegistration.Dispose();
            _autoLeaveRegistration.Dispose();

            try
            {
                if (_machineCts != null) { _machineCts.Cancel(); _machineCts.Dispose(); _machineCts = null; }
            }
            catch (Exception e) { _debug?.LogWarn($"清理 MachineToken 时发生异常: <color=white>{e.Message}</color>"); }

            try
            {
                if (StopOnDispose) Stop();
            }
            finally
            {
                LeaveParent();
                DisposeStates();
                ClearAllTransitionRule();
                DisposeModules();
                ClearCache();

                _stateMachineOwner = null;
                _isDisposeRequested = false;
                IsRunning = false;
                IsDisposed = true;
            }
        }

        void DisposeStates()
        {
            if (_stateMap != null)
            {
                foreach (var stateKvp in _stateMap)
                {
                    try { stateKvp.Value.OnDispose(); }
                    catch (Exception ex) { _debug?.LogErr($"清理状态 {stateKvp.Key} 时发生异常: {ex.Message}", ex); }
                }

                if (UseReflection && _reflectionProcessor != null)
                {
                    try { _reflectionProcessor.ClearStatesReferences(_stateMap.Keys, _stateMap); }
                    catch (Exception ex) { _debug?.LogErr($"重置字段时发生异常: {ex.Message}", ex); }
                }

                _stateMap.Clear();
                _stateMap = null;
            }
        }
        private void ClearCache()
        {
            CurrentState = null;
            CurrentStateType = null;
            RefreshActiveReflectionPointers(null);
            RefreshActiveTransitionRules(null);
            _stateHistory = null;
            _nextStateQueue = null;
            _deferredRuleConfigurator = null;

            _errorHandlers?.Clear(); _errorHandlers = null;
        }

        void DisposeModules()
        {
            _dispatchPriorityEvaluator?.Clear(); _dispatchPriorityEvaluator = null;
            _crossTalkEvaluator?.Clear(); _crossTalkEvaluator = null;

            _reflectionProcessor?.ClearCache(); _reflectionProcessor = null;
            _routerModule?.Dispose(); _routerModule = null;
            _variableManager?.Dispose(); _variableManager = null;
            _eventManager?.Clear(); _eventManager = null;
            _contextManager?.Dispose(); _contextManager = null;

            _debug = null;
        }

        private void ResetStateTimer()
        {
            TimeInState = 0f;
            FixedTimeInState = 0f;
            FramesInState = 0;
            FixedFramesInState = 0;
        }

        //!--- [缓存] ---
        // ================= 高频执行指针缓存 (状态进入时刷新，Update 中 0 查找) =================
        private Action<TStateNode> _activeAutoRunUpdate_Before;
        private Action<TStateNode> _activeAutoRunFixedUpdate_Before;
        private Action<TStateNode> _activeAutoRunPostUpdate_Before;

        private Action<TStateNode> _activeAutoRunUpdate_After;
        private Action<TStateNode> _activeAutoRunFixedUpdate_After;
        private Action<TStateNode> _activeAutoRunPostUpdate_After;

        /// <summary>统一刷新当前状态的高频反射委托指针 (在进入新状态时抓取一次)</summary>
        private void RefreshActiveReflectionPointers(Type targetStateType)
        {
            if (!_useReflection || targetStateType == null)
            {
                _activeAutoRunUpdate_Before = null;
                _activeAutoRunFixedUpdate_Before = null;
                _activeAutoRunPostUpdate_Before = null;

                _activeAutoRunUpdate_After = null;
                _activeAutoRunFixedUpdate_After = null;
                _activeAutoRunPostUpdate_After = null;

                return;
            }

            // 抓取 AutoRun 指针
            _activeAutoRunUpdate_Before = ReflectionProcessor?.GetAutoRun(targetStateType, Timing.BeforeUpdate);
            _activeAutoRunFixedUpdate_Before = ReflectionProcessor?.GetAutoRun(targetStateType, Timing.BeforeFixedUpdate);
            _activeAutoRunPostUpdate_Before = ReflectionProcessor?.GetAutoRun(targetStateType, Timing.BeforePostUpdate);

            _activeAutoRunUpdate_After = ReflectionProcessor?.GetAutoRun(targetStateType, Timing.AfterUpdate);
            _activeAutoRunFixedUpdate_After = ReflectionProcessor?.GetAutoRun(targetStateType, Timing.AfterFixedUpdate);
            _activeAutoRunPostUpdate_After = ReflectionProcessor?.GetAutoRun(targetStateType, Timing.AfterPostUpdate);
        }

        // 预烘焙字典：Type -> 该状态专属的已排好序的单数组 (初始化时一次性生成，终身只读复用)
        private Dictionary<Type, TransitionRule[]> _bakedUpdateRules;
        private Dictionary<Type, TransitionRule[]> _bakedFixedUpdateRules;
        private Dictionary<Type, TransitionRule[]> _bakedPostUpdateRules;

        // 活跃规则缓存（状态进入时刷新，Update 中 0 查找，扁平遍历）
        private TransitionRule[] _activeUpdateRules;
        private TransitionRule[] _activeFixedUpdateRules;
        private TransitionRule[] _activePostUpdateRules;

        // 预烘焙的事件规则字典
        // 结构：当前状态 -> (事件类型 -> 该状态下监听该事件的排好序的专属规则数组)
        private Dictionary<Type, Dictionary<Type, TransitionRule[]>> _bakedDispatchRules;
        private Dictionary<Type, Dictionary<Type, TransitionRule[]>> _bakedPublishRules;

        // 活跃指针：当前状态专属的事件查找表（切换状态时切换指针，0 次查找当前状态）
        private Dictionary<Type, TransitionRule[]> _activeDispatchRules;
        private Dictionary<Type, TransitionRule[]> _activePublishRules;

        private void BakeAllTransitions()
        {
            // 轮询
            if (_hasAutoTransitions_update) _bakedUpdateRules = BakeRuleDictionary(_updateTransitions);
            if (_hasAutoTransitions_fixed) _bakedFixedUpdateRules = BakeRuleDictionary(_fixedUpdateTransitions);
            if (_hasAutoTransitions_last) _bakedPostUpdateRules = BakeRuleDictionary(_postUpdateTransitions);
            // 事件
            if (_hasAutoTransitions_dispatch) _bakedDispatchRules = BakeEventRuleDictionary(_dispatchEventTransitions);
            if (_hasAutoTransitions_publish) _bakedPublishRules = BakeEventRuleDictionary(_publishTransitions);
        }

        private Dictionary<Type, TransitionRule[]> BakeRuleDictionary(Dictionary<Type, List<TransitionRule>> sourceDict)
        {
            if (sourceDict == null) return null;

            var result = new Dictionary<Type, TransitionRule[]>(_stateMap.Count);
            sourceDict.TryGetValue(AnyStateKey, out var anyRules);

            // 遍历每一个已注册的具体状态，为它量身定制预排序数组
            foreach (var stateType in _stateMap.Keys)
            {
                sourceDict.TryGetValue(stateType, out var currentRules);

                int anyCount = anyRules?.Count ?? 0;
                int currCount = currentRules?.Count ?? 0;

                if (anyCount == 0 && currCount == 0)
                {
                    result[stateType] = null;
                    continue;
                }

                // 一次性分配定长数组
                var merged = new TransitionRule[anyCount + currCount];
                int anyIndex = 0, currIndex = 0, targetIndex = 0;

                // 双指针归并：按 Priority 降序排列；优先级相同时局部规则优先
                while (anyIndex < anyCount || currIndex < currCount)
                {
                    if (anyIndex < anyCount && currIndex < currCount)
                    {
                        if (currentRules[currIndex].Priority >= anyRules[anyIndex].Priority)
                            merged[targetIndex++] = currentRules[currIndex++];
                        else
                            merged[targetIndex++] = anyRules[anyIndex++];
                    }
                    else if (currIndex < currCount)
                    {
                        merged[targetIndex++] = currentRules[currIndex++];
                    }
                    else
                    {
                        merged[targetIndex++] = anyRules[anyIndex++];
                    }
                }

                result[stateType] = merged;
            }

            return result;
        }

        /// <summary>
        /// 专门针对事件规则的二维烘焙算法
        /// </summary>
        private Dictionary<Type, Dictionary<Type, TransitionRule[]>> BakeEventRuleDictionary(Dictionary<Type, List<TransitionRule>> sourceDict)
        {
            if (sourceDict == null) return null;

            var result = new Dictionary<Type, Dictionary<Type, TransitionRule[]>>(_stateMap.Count);
            sourceDict.TryGetValue(AnyStateKey, out var anyRules);

            // 遍历每一个已注册的具体状态
            foreach (var stateType in _stateMap.Keys)
            {
                sourceDict.TryGetValue(stateType, out var currentRules);

                // 收集该状态和 AnyState 中涉及到的所有事件类型 (去重)
                var eventTypes = new HashSet<Type>();
                if (anyRules != null)
                {
                    for (int i = 0; i < anyRules.Count; i++)
                        if (anyRules[i].EventType != null) eventTypes.Add(anyRules[i].EventType);
                }
                if (currentRules != null)
                {
                    for (int i = 0; i < currentRules.Count; i++)
                        if (currentRules[i].EventType != null) eventTypes.Add(currentRules[i].EventType);
                }

                if (eventTypes.Count == 0)
                {
                    result[stateType] = null;
                    continue;
                }

                var eventMap = new Dictionary<Type, TransitionRule[]>(eventTypes.Count);

                // 针对每一种事件类型，单独归并排序提取出专属于该事件的规则数组
                foreach (var evtType in eventTypes)
                {
                    // 筛选出属于 evtType 的规则
                    var matchedAny = anyRules?.Where(r => r.EventType == evtType).ToList();
                    var matchedCurr = currentRules?.Where(r => r.EventType == evtType).ToList();

                    int anyCount = matchedAny?.Count ?? 0;
                    int currCount = matchedCurr?.Count ?? 0;

                    var merged = new TransitionRule[anyCount + currCount];
                    int anyIdx = 0, currIdx = 0, targetIdx = 0;

                    // 归并排序：按 Priority 降序排列，同优先级局部优先
                    while (anyIdx < anyCount || currIdx < currCount)
                    {
                        if (anyIdx < anyCount && currIdx < currCount)
                        {
                            if (matchedCurr[currIdx].Priority >= matchedAny[anyIdx].Priority)
                                merged[targetIdx++] = matchedCurr[currIdx++];
                            else
                                merged[targetIdx++] = matchedAny[anyIdx++];
                        }
                        else if (currIdx < currCount)
                        {
                            merged[targetIdx++] = matchedCurr[currIdx++];
                        }
                        else
                        {
                            merged[targetIdx++] = matchedAny[anyIdx++];
                        }
                    }

                    eventMap[evtType] = merged;
                }

                result[stateType] = eventMap;
            }

            return result;
        }
        private void RefreshActiveTransitionRules(Type targetStateType)
        {
            if (targetStateType == null)
            {
                _activeUpdateRules = null;
                _activeFixedUpdateRules = null;
                _activePostUpdateRules = null;
                _activeDispatchRules = null; // 置空
                _activePublishRules = null;  // 置空
                return;
            }

            _activeUpdateRules = _bakedUpdateRules != null && _bakedUpdateRules.TryGetValue(targetStateType, out var u) ? u : null;
            _activeFixedUpdateRules = _bakedFixedUpdateRules != null && _bakedFixedUpdateRules.TryGetValue(targetStateType, out var f) ? f : null;
            _activePostUpdateRules = _bakedPostUpdateRules != null && _bakedPostUpdateRules.TryGetValue(targetStateType, out var p) ? p : null;

            // 提取事件规则指针
            _activeDispatchRules = _bakedDispatchRules != null && _bakedDispatchRules.TryGetValue(targetStateType, out var d) ? d : null;
            _activePublishRules = _bakedPublishRules != null && _bakedPublishRules.TryGetValue(targetStateType, out var pub) ? pub : null;
        }

        //!--- [外部查询] ---
        /// <summary>检查当前状态</summary>
        public bool IsState<T>() where T : class => CurrentStateType == typeof(T);
        /// <summary>检查上一个状态</summary>
        public bool WasState<T>() where T : class => LastStateType == typeof(T);
        /// <summary>
        /// 检查当前状态是否属于接口或基类
        /// </summary>
        public bool IsStateOf<T>() where T : class => CurrentState is T;

        /// <summary>
        /// 检查上一个状态(不包含回退过程)是否属于接口或基类
        /// </summary>
        public bool WasStateOf<T>() where T : class
            // => LastStateType != null && _stateMap[LastStateType] is T;
            => LastStateType != null && typeof(T).IsAssignableFrom(LastStateType);

        public bool IsNextState<T>() where T : class => NextStateType == typeof(T);
        /// <summary>
        /// 检查上下一个即将切换的状态是否属于接口或基类 (在OnExit内查询)
        /// </summary>
        public bool IsNextStateOf<T>() where T : class
            => NextStateType != null && typeof(T).IsAssignableFrom(NextStateType);

        /// <summary>检查当前状态是否属于其中任意一个</summary>
        public bool IsInAnyState(params Type[] states)
        {
            if (CurrentStateType == null) return false;
            for (int i = 0; i < states.Length; i++)
            {
                if (CurrentStateType == states[i]) return true;
            }
            return false;
        }
        /// <summary>供状态类查询是否可以进行切换</summary>
        /// <typeparam name="T">目标状态</typeparam>
        public bool CanTransitionTo<T>() where T : IStateNode
        {
            if (!IsRunning || IsDisposed) return false;
            if (IsChangingState) return false;
            if (_nextStateQueue.Count > 0) return false;
            var targetType = typeof(T);
            if (CurrentStateType == targetType) return false;
            if (!_stateMap.ContainsKey(targetType)) return false;
            if (!IsAllGuardConditionMeet(_guardConditions, targetType)) return false;
            return true;
        }
        // === 内部判定切换用 ===

        // 是否满足所有守卫条件 只在ChangeState的时候调用
        private bool IsAllGuardConditionMeet(Dictionary<Type, List<GuardRule>> ruleDict, Type ToState)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return false;
            }
            // 没限制规则直接返回true
            if (ruleDict == null) return true;

            // 1. 检查 AnyState 守卫 (拦截 任意 -> ToState)
            if (!EvaluateRules(AnyStateKey)) { return false; }
            // 2. 检查 CurrentState 守卫 (拦截 Current -> ToState 或 Current -> 任意)
            if (!EvaluateRules(CurrentStateType)) { return false; }

            return true;

            bool EvaluateRules(Type stateKey)
            {
                if (ruleDict.TryGetValue(stateKey, out var rules))
                {
                    for (int i = 0; i < rules.Count; i++)
                    {
                        var rule = rules[i];

                        // 如果 rule.ToState 是 AnyStateKey，说明这是一个 GuardFrom 规则，直接放行进行条件校验
                        if (rule.ToState != AnyStateKey && rule.ToState != ToState) continue;

                        // 当 Guard 条件返回 true 时，代表触发了拦截机制，返回 false 阻止状态切换
                        if (rule.Condition?.Invoke() == true)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
        }
        //! --- [组件依赖注入] ---
        IDisposable IContextRegistry.RegisterContext<T>(T context, CancellationToken ct) where T : class
        {
            RegisterContext(context, ct);
            return new StateActionDisposable(() => UnregisterContext<T>());
        }

        /// <summary>注册一个上下文组件或数据模型。(已存在时会进行覆盖并注销token)</summary>
        public StateMachine<TStateNode> RegisterContext<T>(T context, CancellationToken ct = default) where T : class
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return this;
            }

            if (context == null) return this;
            ContextManager.Register(context, ct);
            return this;
        }

        /// <summary>手动注销指定的上下文</summary>
        public void UnregisterContext<T>()
        {
            if (IsDisposed) return;
            ContextManager.Unregister<T>();
        }

        // 供反射注入 和 父级路由查询 使用
        bool IStateContextResolver.TryGetContext(Type type, out object context)
        {
            if (IsDisposed)
            {
                context = null;
                return false;
            }

            // 1. 尝试从本地 contextManager 查找
            if (ContextManager.TryGetContext(type, out context) == true)
            {
                return true;
            }

            // 2. 本地没有，向父级路由递归查找
            if (_parentMachine != null)
            {
                return _parentMachine.TryGetContext(type, out context);
            }

            context = null;
            return false;
        }
        //!--- [Debug] ---
        /// <summary>获取所有已注册的状态类型</summary>
        private IEnumerable<Type> GetAllRegisteredStates()
        {
            if (IsDisposed || _stateMap == null) return Enumerable.Empty<Type>();
            return _stateMap.Keys;
        }
        /// <summary>
        /// 查询历史状态 按最新的排序
        /// 备注: 可搭配专用的编辑器脚本 StateMachineDebuggerWindow 使用 (必须放入Assets/Editor)
        /// 打开: Window -> Analysis -> FSM Debugger 显示切换流程（无需任何配置 选中即可显示）
        /// </summary>
        IEnumerable<StateHistoryRecorder> IStateMachineDebugger.GetStateHistory()
        {
            if (IsDisposed) yield break;
            // 使用迭代器倒序输出
            for (int i = _stateHistory.Count - 1; i >= 0; i--)
            {
                yield return _stateHistory[i];
            }
        }

        /// <summary>检查历史记录中是否包含某状态</summary>
        public bool HasHistory(Type stateType) => IsDisposed ? false : _stateHistory.Any(v => v.StateType == stateType);

        /// <summary>[Debug] 用于输出内部状态的辅助函数</summary>
        /// <param name="fieldName">内部字段名</param>
        (bool hasField, Type fieldType, object value) IStateMachineDebugger.MachineDebug_GetField(string fieldName)
        {
            Type type = this.GetType();

            FieldInfo field = type.GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                _debug.LogInfo($"未找到字段: {fieldName}");
                return (false, default, default);
            }

            object value = field.GetValue(this);
            _debug.LogInfo($"[{LogTag}] value: {value} [{field.FieldType.Name}]");
            return (true, field.FieldType, value);
        }

        // =========================================================================
        // 状态切换预测与查询 API (0 副作用)
        // =========================================================================

        /// <summary>
        /// 查询指定事件（无论是 Dispatch 还是 Publish 规则）在当前状态下是否能真正触发切换
        /// </summary>
        public bool CanEventTransition<TEvent>(TEvent eventData = default)
        {
            if (!IsRunning || IsDisposed || IsChangingState) return false;

            // 短路求值：优先判定外部 Dispatch 规则，未命中再判定内部 Publish 规则
            return CheckCanTrigger(_activeDispatchRules, eventData)
                || CheckCanTrigger(_activePublishRules, eventData);
        }

        private bool CheckCanTrigger<TEvent>(Dictionary<Type, TransitionRule[]> dict, TEvent eventData)
        {
            if (dict == null || !dict.TryGetValue(typeof(TEvent), out var rules) || rules == null)
                return false;

            for (int i = 0; i < rules.Length; i++)
            {
                var rule = rules[i];

                // 1. 条件检查
                if (rule.Condition != null && !rule.Condition()) continue;
                if (rule.PayloadCondition is Func<TEvent, bool> pCond && !pCond(eventData)) continue;

                // 2. 目标状态推导（普通/动态/回退）
                Type to = rule.DynamicPayloadToStateProvider is Func<TEvent, Type> dynPayload ? dynPayload(eventData)
                        : rule.DynamicToStateProvider != null ? rule.DynamicToStateProvider()
                        : rule.ToState;

                if (rule.IsRevert)
                {
                    to = null;
                    for (int h = _stateHistory.Count - 1; h >= 0; h--)
                    {
                        var type = _stateHistory[h].StateType;
                        if (type != CurrentStateType && (rule.SkipGroupType == null || !rule.SkipGroupType.IsAssignableFrom(type)))
                        { to = type; break; }
                    }
                }

                // 3. 状态合法性 & 防重入检查
                if (to == null || !_stateMap.TryGetValue(to, out var node) || node.IsDisposed || !node.HasInitialized) continue;
                if (!rule.IsRevert && CurrentStateType == to && !rule.ForceReenter) continue;

                // 4. 守卫拦截检查 (Guard)
                if (!rule.IgnoreRules && !IsAllGuardConditionMeet(_guardConditions, to)) continue;

                return true; // 只要找到任意一条可通过的规则即代表能切换
            }

            return false;
        }
        //!------ [流式规则构建] ------
        // ============== 切换规则字典/标记 ===============

        // 用于短路优化性能
        bool _hasAutoTransitions_update;
        bool _hasAutoTransitions_fixed;
        bool _hasAutoTransitions_last;
        bool _hasAutoTransitions_dispatch;
        bool _hasAutoTransitions_publish;
        int _totalTransitionRuleCount;
        // 自动切换规则存储 (Key为当前状态的Type，使用AnyState代替null)
        private Dictionary<Type, List<GuardRule>> _guardConditions;// 用于ChangeState的判断

        // 轮询：未添加时直接短路/分开存储优化性能
        private Dictionary<Type, List<TransitionRule>> _updateTransitions;
        private Dictionary<Type, List<TransitionRule>> _fixedUpdateTransitions;
        private Dictionary<Type, List<TransitionRule>> _postUpdateTransitions;
        private Dictionary<Type, List<TransitionRule>> _dispatchEventTransitions; // 外->内
        private Dictionary<Type, List<TransitionRule>> _publishTransitions; // 内->外

        // 懒加载 当有规则时开启标记
        private ref Dictionary<Type, List<TransitionRule>> GetDictRefByTrigger(TransitionTrigger trigger)
        {
            switch (trigger)
            {
                case TransitionTrigger.Update: _hasAutoTransitions_update = true; return ref _updateTransitions;
                case TransitionTrigger.FixedUpdate: _hasAutoTransitions_fixed = true; return ref _fixedUpdateTransitions;
                case TransitionTrigger.PostUpdate: _hasAutoTransitions_last = true; return ref _postUpdateTransitions;
                case TransitionTrigger.Dispatch: _hasAutoTransitions_dispatch = true; return ref _dispatchEventTransitions;
                case TransitionTrigger.Publish: _hasAutoTransitions_publish = true; return ref _publishTransitions;
                default: _hasAutoTransitions_update = true; return ref _updateTransitions;
            }
        }
        // =============================
        void ClearAllTransitionRule()
        {
            _guardConditions?.Clear(); _guardConditions = null;
            _updateTransitions?.Clear(); _updateTransitions = null;
            _fixedUpdateTransitions?.Clear(); _fixedUpdateTransitions = null;
            _postUpdateTransitions?.Clear(); _postUpdateTransitions = null;
            _dispatchEventTransitions?.Clear(); _dispatchEventTransitions = null;
            _publishTransitions?.Clear(); _publishTransitions = null;

            _bakedUpdateRules?.Clear(); _bakedUpdateRules = null;
            _bakedFixedUpdateRules?.Clear(); _bakedFixedUpdateRules = null;
            _bakedPostUpdateRules?.Clear(); _bakedPostUpdateRules = null;
            _bakedDispatchRules?.Clear(); _bakedDispatchRules = null;
            _bakedPublishRules?.Clear(); _bakedPublishRules = null;
            _activeUpdateRules = null;
            _activeFixedUpdateRules = null;
            _activePostUpdateRules = null;
            _activeDispatchRules = null;
            _activePublishRules = null;
        }

        //!--- [规则配置] ---
        IStateMachineConfigurator<TStateNode> IStateMachineConfigurator<TStateNode>.ConfigureRules(Action<RuleConfigurator<TStateNode>> configureAction)
            => ConfigureRules(configureAction);

        /// <summary>
        /// 作用域闭包配置模式：在此块中声明的所有规则会自动 Apply
        /// 当未注册状态时会延迟执行，支持 ConfigureRules → AutoStart 链式调用
        /// </summary>
        public StateMachine<TStateNode> ConfigureRules(Action<RuleConfigurator<TStateNode>> configureAction)
        {
            if (IsDisposed || configureAction == null) return this;

            if (_stateMap.Count == 0)
            {
                _deferredRuleConfigurator += configureAction;
                return this; // 中断后续立刻执行的逻辑
            }

            var configurator = new RuleConfigurator<TStateNode>(this);
            configureAction.Invoke(configurator);
            configurator.ApplyAll(); // 闭包结束，统一自动注册

            return this;
        }

        private void ApplyDeferredRules()
        {
            if (_deferredRuleConfigurator != null)
            {
                var configurator = new RuleConfigurator<TStateNode>(this);
                _deferredRuleConfigurator.Invoke(configurator);
                configurator.ApplyAll();

                // 执行完毕后清空缓存，释放内存
                _deferredRuleConfigurator = null;
            }
        }
        //!--- [规则配置防呆检测] ---
        private void ValidateConcreteState(params Type[] types)
        {
            foreach (var t in types)
            {
                if (t == AnyStateKey) continue;

                if (t.IsAbstract || t.IsInterface)
                {
                    _debug.LogWarn($"[规则验证] 绑定的状态 [{t.Name}] 是抽象类或接口！\n" +
                                $"普通的 Bind/Guard 方法仅支持精准匹配实体状态类。请改用带 Group 字样的方法。");
                }
            }
        }
        //!--- [Bind] ---
        IStateNode ITransitionRuleReceiver.CurrentStateNode => CurrentState;
        // 基础绑定
        public TransitionBuilder<TFrom> Bind<TFrom, TTo>(bool forceReenter = false)
            where TFrom : class, TStateNode
            where TTo : class, TStateNode
        {
            ValidateConcreteState(typeof(TFrom), typeof(TTo));
            return new TransitionBuilder<TFrom>(this, typeof(TFrom), typeof(TTo), forceReenter);
        }

        public TransitionBuilder<TStateNode> BindAny<TTo>(bool forceReenter = false) //@ API
            where TTo : class, TStateNode
        {
            ValidateConcreteState(typeof(TTo));
            return new TransitionBuilder<TStateNode>(this, AnyStateKey, typeof(TTo), forceReenter);
        }
        // 多对一
        public TransitionBuilder<TGroup> BindGroupToOne<TGroup, TTo>(bool forceReenter = false)
            where TGroup : class
            where TTo : class, TStateNode
        {
            ValidateConcreteState(typeof(TTo));
            // 自动扫描已注册状态中，实现了 TGroup (接口或基类) 的所有状态
            var matchingStates = _stateMap.Keys
                .Where(t => typeof(TGroup).IsAssignableFrom(t))
                .ToArray();

            if (matchingStates.Length == 0)
            {
                _debug.LogWarn($"BindGroup<{typeof(TGroup).Name}> 未找到任何匹配的已注册状态！请确保配置规则前已完成注册。");
            }

            return new TransitionBuilder<TGroup>(this, matchingStates, new Type[] { typeof(TTo) }, forceReenter);
        }

        public TransitionBuilder<TStateNode> BindManyToOne<TFrom1, TFrom2, TTo>(bool forceReenter = false)
            where TFrom1 : class, TStateNode
            where TFrom2 : class, TStateNode
            where TTo : class, TStateNode
        {
            ValidateConcreteState(typeof(TFrom1), typeof(TFrom2), typeof(TTo));
            return new TransitionBuilder<TStateNode>(this,
                new Type[] { typeof(TFrom1), typeof(TFrom2) },
                new Type[] { typeof(TTo) },
                forceReenter);
        }

        public TransitionBuilder<TStateNode> BindManyToOne<TFrom1, TFrom2, TFrom3, TTo>(bool forceReenter = false)
            where TFrom1 : class, TStateNode
            where TFrom2 : class, TStateNode
            where TFrom3 : class, TStateNode
            where TTo : class, TStateNode
        {
            ValidateConcreteState(typeof(TFrom1), typeof(TFrom2), typeof(TFrom3), typeof(TTo));
            return new TransitionBuilder<TStateNode>(this,
                new Type[] { typeof(TFrom1), typeof(TFrom2), typeof(TFrom3) },
                new Type[] { typeof(TTo) },
                forceReenter);
        }

        public TransitionBuilder<TStateNode> BindManyToOne<TTo>(params Type[] fromStates)
            where TTo : class, TStateNode
        {
            if (fromStates == null || fromStates.Length == 0) throw new ArgumentException("参数 fromStates 数组为空");
            ValidateConcreteState(fromStates);
            return new TransitionBuilder<TStateNode>(this, fromStates, new Type[] { typeof(TTo) }, forceReenter: false);
        }

        public SequenceTransitionBuilder<TStateNode> BindSequence<TFirst>(bool forceReenter = false)
            where TFirst : class, TStateNode
        {
            ValidateConcreteState(typeof(TFirst));
            return new SequenceTransitionBuilder<TStateNode>(this, typeof(TFirst), forceReenter);
        }

        // 动态目标
        public DynamicTransitionBuilder<TFrom> BindDynamic<TFrom>(bool forceReenter = false)
            where TFrom : class, TStateNode
        {
            ValidateConcreteState(typeof(TFrom));
            return new DynamicTransitionBuilder<TFrom>(this, new[] { typeof(TFrom) }, new Type[0], forceReenter);
        }

        public DynamicTransitionBuilder<TStateNode> BindDynamicAny(bool forceReenter = false)
        {
            return new DynamicTransitionBuilder<TStateNode>(this, new[] { AnyStateKey }, new Type[0], forceReenter);
        }

        public DynamicTransitionBuilder<TGroup> BindDynamicGroup<TGroup>(bool forceReenter = false)
            where TGroup : class
        {
            // 自动扫描已注册状态中，实现了 TGroup (接口或基类) 的所有状态
            var matchingStates = _stateMap.Keys
                .Where(t => typeof(TGroup).IsAssignableFrom(t))
                .ToArray();

            if (matchingStates.Length == 0)
            {
                _debug.LogWarn($"BindGroupDynamic<{typeof(TGroup).Name}> 未找到任何匹配的已注册状态！请确保配置规则前已完成注册。");
            }

            // 注意这里第三个参数传入的是 new Type[0]，代表目标状态是未知的（动态的）
            return new DynamicTransitionBuilder<TGroup>(this, matchingStates, new Type[0], forceReenter);
        }

        // 回退构建器
        public TransitionBuilder<TFrom> BindFallback<TFrom>(bool ignoreTransitionRule = true) where TFrom : class, TStateNode
        {
            ValidateConcreteState(typeof(TFrom));
            return new FallbackTransitionBuilder<TFrom>(this, new Type[] { typeof(TFrom) }, ignoreTransitionRule, skipGroupType: null);
        }

        public TransitionBuilder<TGroup> BindGroupFallback<TGroup>(bool ignoreTransitionRule = true) where TGroup : class
        {

            var matchingStates = _stateMap.Keys
                .Where(t => typeof(TGroup).IsAssignableFrom(t))
                .ToArray();

            if (matchingStates.Length == 0)
            {
                _debug.LogWarn($"BindGroupFallback<{typeof(TGroup).Name}> 未找到任何匹配的已注册状态！请确保配置规则前已完成注册。");
            }
            if (matchingStates.Length > (_maxHistory - 1))
            {
                _debug.LogWarn($"BindGroupFallback<{typeof(TGroup).Name}> 该组状态数大于历史记录！");
            }

            return new FallbackTransitionBuilder<TGroup>(this, matchingStates, ignoreTransitionRule, skipGroupType: typeof(TGroup));
        }

        public TransitionBuilder<TStateNode> BindFallbackAny(bool ignoreTransitionRule = true)
        {
            return new FallbackTransitionBuilder<TStateNode>(this, new[] { AnyStateKey }, ignoreTransitionRule, skipGroupType: null);
        }

        //* 供 Builder 调用的内部注册方法 添加时排序
        void ITransitionRuleReceiver.AddTransitionRule(TransitionRule rule)
        {
            if (IsDisposed)
            {
                if (DebugMode) throw new ObjectDisposedException($"{MachineId} 状态机已销毁");
                return;
            }
            if (_hasInitialized || IsRunning)
            {
                _debug.LogErr($"状态机已经完成初始化，规则表已烘焙冻结\n" +
                        $"规则: [{rule.FromState?.Name ?? "Any"}] -> [{rule.ToState?.Name ?? "Dynamic"}] (Trigger: {rule.Trigger})");
                return;
            }
            bool isSharedVar = rule.EventType != null && typeof(ISharedVarInternal).IsAssignableFrom(rule.EventType);
            // 记录触发源
            rule.TriggerSourceInfo ??= (rule.EventType != null)
                ? $"{(isSharedVar ? "SharedVar" : rule.Trigger)}<{rule.EventType.Name}>"
                : $"{rule.Trigger}";

            ref var targetDict = ref GetDictRefByTrigger(rule.Trigger);
            if (targetDict == null) targetDict = new Dictionary<Type, List<TransitionRule>>();

            Type key = rule.FromState ?? AnyStateKey;

            if (!targetDict.TryGetValue(key, out var list))
            {
                list = new List<TransitionRule>();
                targetDict[key] = list;
            }

            // --- 高性能插入排序 (按 Priority 从大到小排列) ---
            int insertIndex = 0;
            while (insertIndex < list.Count && list[insertIndex].Priority >= rule.Priority)
            {
                insertIndex++;
            }
            list.Insert(insertIndex, rule);
            _totalTransitionRuleCount++;// 成功添加规则计数
        }

        //!--- [Guard] ---
        public GuardBuilder<TFrom> Guard<TFrom, TTo>()
            where TFrom : class, TStateNode
            where TTo : class, TStateNode
        {
            ValidateConcreteState(typeof(TFrom), typeof(TTo));
            return new GuardBuilder<TFrom>(this, new[] { typeof(TFrom) }, new[] { typeof(TTo) });
        }

        public GuardBuilder<TStateNode> GuardAny<TTo>()
            where TTo : class, TStateNode
        {
            ValidateConcreteState(typeof(TTo));
            return new GuardBuilder<TStateNode>(this, new[] { AnyStateKey }, new[] { typeof(TTo) });
        }

        public GuardBuilder<TStateNode> GuardToGroup<TGroup>() where TGroup : class
        {
            // 自动扫描已注册状态中，实现了 TGroup (接口或基类) 的所有状态
            var matchingStates = _stateMap.Keys
                .Where(t => typeof(TGroup).IsAssignableFrom(t))
                .ToArray();

            if (matchingStates.Length == 0)
            {
                _debug.LogWarn($"GuardToGroup<{typeof(TGroup).Name}> 未找到任何匹配的已注册状态！请确保配置规则前已完成注册。");
            }

            // From: 任意状态 (AnyStateKey) -> To: 匹配的接口状态组 (matchingStates)
            return new GuardBuilder<TStateNode>(this, new[] { AnyStateKey }, matchingStates);
        }

        public GuardBuilder<TFrom> GuardFrom<TFrom>()
            where TFrom : class, TStateNode
        {
            ValidateConcreteState(typeof(TFrom));
            // 将 AnyStateKey 作为目标状态传入，代表拦截到任意状态的切换
            return new GuardBuilder<TFrom>(this, new[] { typeof(TFrom) }, new[] { AnyStateKey });
        }

        public GuardBuilder<TGroup> GuardFromGroup<TGroup>() where TGroup : class
        {
            var matchingStates = _stateMap.Keys
                .Where(t => typeof(TGroup).IsAssignableFrom(t))
                .ToArray();

            if (matchingStates.Length == 0)
            {
                _debug.LogWarn($"GuardFromGroup <{typeof(TGroup).Name}> 未找到任何匹配的已注册状态！请确保配置规则前已完成注册。");
            }

            return new GuardBuilder<TGroup>(this, matchingStates, new[] { AnyStateKey });
        }

        // 供 Builder 调用的内部注册方法  添加ChangeState的守卫条件
        void ITransitionRuleReceiver.AddGuardRule(GuardRule rule)
        {
            if (IsDisposed) return;
            if (_hasInitialized || IsRunning)
            {
                _debug.LogErr($"禁止在运行时注册守卫规则，状态机已经完成初始化。\n" +
                            $"规则: [{rule.FromState?.Name ?? "Any"}] -> [{rule.ToState?.Name ?? "Any"}]");
                return;
            }
            if (_guardConditions == null) _guardConditions = new Dictionary<Type, List<GuardRule>>();

            Type key = rule.FromState ?? AnyStateKey;
            if (!_guardConditions.TryGetValue(key, out var list))
            {
                list = new List<GuardRule>();
                _guardConditions[key] = list;
            }
            list.Add(rule);
        }

    }

    #endregion
    //!=============Builder=====================

    /// <summary>用于给状态机注入自动切换逻辑 通过builder调用(对外隐藏)</summary>
    public interface ITransitionRuleReceiver : IMachineLogProvider, IStateContextResolver
    {
        Type LastStateType { get; }

        float TimeInState { get; }
        float DeltaTime { get; }
        float FixedDeltaTime { get; }
        int FramesInState { get; }
        int FixedFramesInState { get; }
        double MachineTime { get; }

        /// <summary>获取当前处于激活状态的状态节点 (未启动/停止时为 null)</summary>
        IStateNode CurrentStateNode { get; }
        IEnumerable<Type> RegisteredStates { get; }
        internal void AddTransitionRule(TransitionRule rule);
        internal void AddGuardRule(GuardRule rule);
    }

    #region TransitionBuilder
    /// <summary>触发时机枚举</summary>
    public enum TransitionTrigger
    {
        Update,
        FixedUpdate,
        PostUpdate,
        Dispatch,
        Publish
    }

    //!--- [流式构建器] ---
    /// <summary>内部规则数据结构</summary>
    public class TransitionRule
    {
        public Type FromState;
        public Type ToState;
        public Func<bool> Condition;
        public TransitionTrigger Trigger;
        public Type EventType;
        public bool ForceReenter;
        // 优先级字段，默认为0，数值越大越先执行
        public int Priority = 0;

        public Delegate PayloadCondition;
        public Func<object> DataProvider;
        public Func<object, object> DynamicEventDataProvider;
        // 动态路由
        public Func<Type> DynamicToStateProvider; // 轮询触发时的动态目标状态提供者
        public Delegate DynamicPayloadToStateProvider; // 事件触发时带载荷的动态目标状态提供者

        // 过渡回调
        public Action OnTransitionCallback;
        public Action<object> OnTransitionCallbackWithPayload;
        // 自动回退相关
        public bool IsRevert;       // 标记此规则是否为回退规则
        public bool IgnoreRules;    // 执行回退时是否无视迁移规则拦截
        public Type SkipGroupType;  // 执行回退时，需要跳过的状态组(接口/基类)

        // 事件消费标记
        public bool IsConsumed = false;
        public Delegate ConsumeCondition;

        // Debug用的触发源信息
        public string TriggerSourceInfo;
        /// <summary>克隆规则模板</summary>
        public TransitionRule Clone()
        {
            return (TransitionRule)this.MemberwiseClone();
        }
    }
    //!--- [基础构建器] ---
    /// <summary>
    /// 流式状态切换构建器
    /// </summary>
    public class TransitionBuilder<TStateNode> : IAutoApplicable
        where TStateNode : class
    {

        protected bool _isApplied = false;// 防止重复 Apply 标记

        protected TransitionBuilder<TStateNode> _sourceBuilder;

        protected ITransitionRuleReceiver Machine;
        protected TransitionRule Rule;
        protected bool HasCondition = false;

        // 双向事件标记
        protected bool IsDualEvent = false;

        // 支持多个起始/目标状态
        protected Type[] FromStates;
        protected Type[] ToStates;

        // protected StateMachineLogger<StateMachineLogLevel> _debug => Machine.Logger;

        // 一对一
        protected internal TransitionBuilder(ITransitionRuleReceiver machine, Type from, Type to, bool forceReenter = false)
            : this(machine, new[] { from }, new[] { to }, forceReenter)
        {

        }

        // 接收数组
        protected internal TransitionBuilder(ITransitionRuleReceiver machine, Type[] fromStates, Type[] toStates, bool forceReenter = false)
        {
            Machine = machine;
            FromStates = fromStates;
            ToStates = toStates;
            Rule = new TransitionRule
            {
                FromState = fromStates.Length == 1 ? fromStates[0] : null, // 模板占位
                ToState = toStates.Length == 1 ? toStates[0] : null,       // 模板占位
                Trigger = TransitionTrigger.Update,
                Condition = () => true,
                ForceReenter = forceReenter
            };
        }

        // 供EventTransitionBuilder(包装器) 调用的拷贝构造函数
        protected TransitionBuilder(TransitionBuilder<TStateNode> source)
        {

            _sourceBuilder = source;

            // _debug = source._debug;
            Machine = source.Machine;
            Rule = source.Rule;
            FromStates = source.FromStates;
            ToStates = source.ToStates;
            HasCondition = source.HasCondition;
            IsDualEvent = source.IsDualEvent;
        }

        //@ Trigger
        /// <summary>在 Update 阶段轮询条件</summary>
        public TransitionBuilder<TStateNode> OnUpdate() { Rule.Trigger = TransitionTrigger.Update; return this; }

        /// <summary>在 FixedUpdate 阶段轮询条件</summary>
        public TransitionBuilder<TStateNode> OnFixedUpdate() { Rule.Trigger = TransitionTrigger.FixedUpdate; return this; }

        /// <summary>在 PostUpdate 阶段轮询条件</summary>
        public TransitionBuilder<TStateNode> OnPostUpdate() { Rule.Trigger = TransitionTrigger.PostUpdate; return this; }

        /// <summary>
        /// (外->内) 拦截外部事件时判断条件 (泛型 TEvent 代表事件类型)
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> OnDispatch<TEvent>(Func<TEvent, bool> condition = null)
        {
            Rule.Trigger = TransitionTrigger.Dispatch;
            Rule.EventType = typeof(TEvent);
            if (condition != null) Rule.PayloadCondition = condition;
            // 将自身上下文透传给 EventTransitionBuilder
            return new EventTransitionBuilder<TStateNode, TEvent>(this);
        }
        /// <summary>
        /// (内->外) 拦截状态内部派发的事件，并在满足时切换状态 
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> OnPublish<TEvent>(Func<TEvent, bool> condition = null)
        {
            Rule.Trigger = TransitionTrigger.Publish;
            Rule.EventType = typeof(TEvent);
            if (condition != null) Rule.PayloadCondition = condition;

            return new EventTransitionBuilder<TStateNode, TEvent>(this);
        }
        /// <summary>
        /// (双向) 同时拦截外部和内部派发的事件（双向监听）。
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> OnEvent<TEvent>(Func<TEvent, bool> condition = null)
        {
            // 这里默认赋一个值占位，Apply 时会拆分
            Rule.Trigger = TransitionTrigger.Dispatch;
            Rule.EventType = typeof(TEvent);
            if (condition != null) Rule.PayloadCondition = condition;

            // 打上双向监听标记
            IsDualEvent = true;

            return new EventTransitionBuilder<TStateNode, TEvent>(this);
        }
        /// <summary>
        /// 当指定的强类型共享变量发生改变时，自动触发拦截并判断条件。
        /// 备注: 完美支持跨状态机侦听 (需接入 Router)
        /// </summary>
        public EventTransitionBuilder<TStateNode, TShared> OnSharedValueChanged<TShared>(Func<TShared, bool> condition = null)
            where TShared : class, ISharedVar
        {
            return OnDispatch<TShared>(condition);
        }

        //@ 基础条件

        /// <summary>满足条件则尝试切换（优先配置）</summary>
        public TransitionBuilder<TStateNode> When(Func<bool> condition)
        {
            if (HasCondition)
            {
                Machine.Logger?.LogWarn($"重复配置了多条 When 规则，已使用 And 替代");
                return And(condition);
            }
            Rule.Condition = condition;
            HasCondition = true; // 标记已设置
            return this;
        }

        /// <summary>满足前置条件 并且满足此规则(When未配置时，And等价When)</summary>
        public TransitionBuilder<TStateNode> And(Func<bool> condition)
        {

            if (!HasCondition) { return When(condition); }

            Func<bool> prevCondition = Rule.Condition;
            Rule.Condition = () => prevCondition() && condition();
            return this;
        }

        /// <summary>满足前置条件 或满足此规则 (When未配置时，Or等价When)</summary>
        public TransitionBuilder<TStateNode> Or(Func<bool> condition)
        {
            if (!HasCondition) { return When(condition); }

            Func<bool> prevCondition = Rule.Condition;
            Rule.Condition = () => prevCondition() || condition();
            return this;
        }
        /// <summary>
        /// 除非满足此条件，否则允许切换（等价于 And( !condition )）
        /// </summary>
        public TransitionBuilder<TStateNode> Unless(Func<bool> condition)
        {
            return And(() => !condition());
        }
        /// <summary>设置优先级（数值越大优先级越高，默认0）</summary>
        public TransitionBuilder<TStateNode> WithPriority(int priority)
        {
            Rule.Priority = priority;
            return this;
        }

        //@ 上下文判定
        /// <summary>
        /// 当当前状态是指定类型 TState 时，提取该状态的强类型实例进行条件判定
        /// 备注: 支持实体状态类或接口 (访问其 public 字段或 bool 方法)
        /// </summary>
        public TransitionBuilder<TStateNode> WhenState(Func<TStateNode, bool> predicate)
        {
            return And(() =>
            {
                if (Machine.CurrentStateNode is TStateNode typedState)
                {
                    return predicate.Invoke(typedState);
                }
                return false;
            });
        }

        /// <summary>
        /// 【泛型重载】供 BindAny 或临时类型转换使用
        /// </summary>
        public TransitionBuilder<TStateNode> WhenState<TCustomGroup>(Func<TCustomGroup, bool> predicate)
            where TCustomGroup : class
        {
            return And(() =>
            {
                if (Machine.CurrentStateNode is TCustomGroup typedState)
                {
                    return predicate.Invoke(typedState);
                }
                return false;
            });
        }
        /// <summary>
        /// 当状态机中注册了指定类型 TContext 的上下文时，提取强类型实例进行条件判定
        /// 备注: 自动支持从当前状态机或父级路由查找
        /// </summary>
        public TransitionBuilder<TStateNode> WhenContext<TContext>(Func<TContext, bool> predicate) where TContext : class
        {
            return And(() =>
            {
                if (Machine.TryGetContext(typeof(TContext), out var ctxObj) && ctxObj is TContext typedCtx)
                {
                    return predicate.Invoke(typedCtx);
                }
                return false;
            });
        }

        /// <summary> 只有当上一个状态是指定类型时，才允许执行此规则</summary>
        public TransitionBuilder<TStateNode> WhenLastStateIs<TState>() where TState : class, TStateNode
        {
            return And(() => Machine.LastStateType == typeof(TState));
        }

        /// <summary>只要上一个状态不是指定类型，就允许执行此规则</summary>
        public TransitionBuilder<TStateNode> UnlessLastStateWas<TState>() where TState : class, TStateNode
        {
            return And(() => Machine.LastStateType != typeof(TState));
        }

        //@ 时间判定
        /// <summary>在当前状态停留指定的 Update 帧数后允许切换 (精确值)</summary>
        public TransitionBuilder<TStateNode> AfterFrames(int frames, bool useFixedFrames = false)
        {
            return And(() => (useFixedFrames ? Machine.FixedFramesInState : Machine.FramesInState) >= frames);
        }

        /// <summary>在当前状态停留指定时间后允许切换</summary>
        /// <param name="seconds">等待的秒数</param>
        public TransitionBuilder<TStateNode> After(float seconds)
        {
            return And(() => Machine.TimeInState >= seconds);
        }

        /// <summary>在当前状态停留动态指定的时间后允许切换 (每次轮询都会调用 provider 重新获取时间)</summary>
        /// <param name="timeProvider">动态时间提供者，例如 ()=> anim.length</param>
        public TransitionBuilder<TStateNode> After(Func<float> timeProvider)
        {
            return And(() => Machine.TimeInState >= timeProvider.Invoke());
        }

        /// <summary>或者在当前状态停留指定时间后允许切换（用于自动超时等场景）</summary>
        public TransitionBuilder<TStateNode> OrAfter(float seconds)
        {

            return Or(() => Machine.TimeInState >= seconds);
        }

        /// <summary>或者在当前状态停留动态指定的时间后允许切换（用于自动超时等场景）</summary>
        public TransitionBuilder<TStateNode> OrAfter(Func<float> timeProvider)
        {

            return Or(() => Machine.TimeInState >= timeProvider.Invoke());
        }
        /// <summary>
        /// 在当前状态停留随机的时间后允许切换（进入状态时固定一次随机值）
        /// </summary>
        public TransitionBuilder<TStateNode> AfterRandom(float minSeconds, float maxSeconds)
        {

            float? targetTime = null;
            float lastEvalTime = -1f;

            return And(() =>
            {
                float currentTime = Machine.TimeInState;

                // 如果发生状态切换（时间重置或倒退），重新生成随机时间
                if (currentTime < lastEvalTime || targetTime == null)
                {
                    targetTime = (float)(GroupRouteSelector.Random.NextDouble() * (maxSeconds - minSeconds) + minSeconds);
                }
                lastEvalTime = currentTime;

                return currentTime >= targetTime.Value;
            });
        }

        /// <summary>
        /// 在当前状态停留时间小于等于指定时间前允许切换 (例如：土狼时间、输入缓冲、连招窗口)。
        /// </summary>
        public TransitionBuilder<TStateNode> Within(float seconds)
        {
            return And(() => Machine.TimeInState <= seconds);
        }

        /// <summary>在当前状态停留时间小于等于指定时间前允许切换 (动态获取)</summary>
        public TransitionBuilder<TStateNode> Within(Func<float> timeProvider)
        {
            return And(() => Machine.TimeInState <= timeProvider.Invoke());
        }

        /// <summary>
        /// 只有当在当前状态停留的时间处于 [minSeconds, maxSeconds] 区间内时允许切换。
        /// </summary>
        public TransitionBuilder<TStateNode> Between(float minSeconds, float maxSeconds)
        {
            return And(() => Machine.TimeInState >= minSeconds && Machine.TimeInState <= maxSeconds);
        }

        /// <summary>
        /// 只有当在当前状态停留的时间处于动态获取时间区间 [min, max]，处于区间内时允许切换。(元组委托计算一次)
        /// 例：Between(() => (animLength * 0.3f, animLength * 0.6f))
        /// </summary>
        public TransitionBuilder<TStateNode> Between(Func<(float min, float max)> windowProvider)
        {
            return And(() =>
            {
                var (min, max) = windowProvider.Invoke();
                return Machine.TimeInState >= min && Machine.TimeInState <= max;
            });
        }

        /// <summary>
        /// 只有当在当前状态停留的时间处于动态获取时间区间 [min, max]，处于区间内时允许切换。(双委托计算二次)
        /// </summary>
        public TransitionBuilder<TStateNode> Between(Func<float> minProvider, Func<float> maxProvider)
        {
            return And(() => Machine.TimeInState >= minProvider.Invoke() && Machine.TimeInState <= maxProvider.Invoke());
        }

        /// <summary>冷却时间：该规则在成功触发切换后，在指定的秒数内将被冻结，无法再次触发。</summary>
        public TransitionBuilder<TStateNode> Cooldown(float cooldownSeconds)
        {
            double lastTriggerTime = double.MinValue;
            And(() => (Machine.MachineTime - lastTriggerTime) >= cooldownSeconds);
            OnTransition(() => { lastTriggerTime = Machine.MachineTime; });
            return this;
        }

        /// <summary>冷却时间 (动态)：每次验证时动态获取冷却时间，在指定的秒数内将被冻结，无法再次触发。</summary>
        public TransitionBuilder<TStateNode> Cooldown(Func<float> cooldownProvider)
        {
            double lastTriggerTime = double.MinValue;
            And(() => (Machine.MachineTime - lastTriggerTime) >= cooldownProvider());
            OnTransition(() => { lastTriggerTime = Machine.MachineTime; });
            return this;
        }

        /// <summary>
        /// 持续满足条件达到指定时间后才允许切换 (专供 Update/FixedUpdate 轮询使用)。
        /// 若此前已配置 When/And 条件，会自动作为 And 逻辑叠加（非短路求值，确保计时器稳定累加）。
        /// 例: .WhenContinuously(() => distance > 5f, 1.0f)
        /// </summary>
        public TransitionBuilder<TStateNode> WhenContinuously(Func<bool> condition, float duration)
            => WhenContinuously(condition, () => duration);

        /// <summary>
        /// 持续满足条件达到动态指定的时间后才允许切换 (每次轮询动态获取时长)。
        /// 若此前已配置 When/And 条件，会自动作为 And 逻辑叠加。
        /// 例: .WhenContinuously(() => isCharging, () => maxChargeTime)
        /// </summary>
        public TransitionBuilder<TStateNode> WhenContinuously(Func<bool> condition, Func<float> durationProvider)
        {
            var continuousCondition = CreateContinuousCondition(condition, durationProvider);

            if (!HasCondition)
            {
                Rule.Condition = continuousCondition;
                HasCondition = true;
            }
            else
            {
                // 已经存在前置条件，自动切换为 AND 逻辑
                Func<bool> prevCondition = Rule.Condition;
                Rule.Condition = () =>
                {
                    bool prevResult = prevCondition();
                    bool contResult = continuousCondition();
                    // 两边都求值，防止短路导致计时器冻结
                    return prevResult && contResult;
                };
            }
            return this;
        }
        /// <summary>
        /// 或者在当前条件持续满足达到指定时间后允许切换 (OR 逻辑)。
        /// 若此前未配置任何条件，则等价于 WhenContinuously。
        /// </summary>
        public TransitionBuilder<TStateNode> OrContinuously(Func<bool> condition, float duration)
            => OrContinuously(condition, () => duration);

        /// <summary>
        /// 或者在当前条件持续满足达到动态指定时间后允许切换 (OR 逻辑)。
        /// 若此前未配置任何条件，则等价于 WhenContinuously。
        /// </summary>
        public TransitionBuilder<TStateNode> OrContinuously(Func<bool> condition, Func<float> durationProvider)
        {
            if (!HasCondition) return WhenContinuously(condition, durationProvider);

            Func<bool> prevCondition = Rule.Condition;
            Func<bool> continuousCondition = CreateContinuousCondition(condition, durationProvider);

            Rule.Condition = () =>
            {
                bool prevResult = prevCondition();
                bool contResult = continuousCondition();
                return prevResult || contResult; // 两边均执行，保证 continuousCondition 计时器每帧正常更新
            };
            return this;
        }

        // 供 Continuously 调用的闭包逻辑 (统一支持 Func<float>)
        private Func<bool> CreateContinuousCondition(Func<bool> condition, Func<float> durationProvider)
        {
            float? meetStartTime = null;
            float lastEvalTime = -1f;

            return () =>
            {
                float currentTime = Machine.TimeInState;

                // 状态机发生过切换（进入新状态时 TimeInState 会重置为0）
                // 发现时间回退，立刻清空之前的连续计时，防止跨状态累加
                if (currentTime < lastEvalTime) meetStartTime = null;

                lastEvalTime = currentTime;

                // 核心逻辑
                if (condition.Invoke())
                {
                    if (meetStartTime == null) meetStartTime = currentTime;
                    float targetDuration = durationProvider != null ? durationProvider.Invoke() : 0f;
                    return (currentTime - meetStartTime.Value) >= targetDuration;
                }
                else
                {
                    // 条件一旦中断，立即重置计时器
                    meetStartTime = null;
                    return false;
                }
            };
        }

        /// <summary>
        /// [轮询版本] 当条件满足后启动延时倒计时，等待指定秒数后真正执行状态切换。
        /// </summary>
        /// <param name="delaySeconds">需要延时的秒数</param>
        /// <param name="cancelIf">延时期间的取消/打断条件。若返回 true 则立即中断倒计时并取消切换</param>
        public TransitionBuilder<TStateNode> Delay(float delaySeconds, Func<bool> cancelIf = null)
            => Delay(() => delaySeconds, cancelIf);

        /// <summary>
        /// [轮询版本] 当条件满足后启动延时倒计时，根据委托动态获取延时秒数后执行切换。
        /// </summary>
        /// <param name="delayProvider">动态延时秒数提供者</param>
        /// <param name="cancelIf">延时期间的取消/打断条件。若返回 true 则立即中断倒计时并取消切换</param>
        public TransitionBuilder<TStateNode> Delay(Func<float> delayProvider, Func<bool> cancelIf = null)
        {

            Func<bool> originalCondition = Rule.Condition;

            // 闭包状态变量（0 GC，伴随规则生命周期）
            bool isTriggered = false;
            float triggerTime = -1f;
            float targetDelay = 0f;
            float lastEvalTime = -1f;

            Rule.Condition = () =>
            {
                float currentTime = Machine.TimeInState;

                // 状态机发生切换（TimeInState 重置为 0），清空触发状态，防止延迟泄漏至下一状态
                if (currentTime < lastEvalTime)
                {
                    isTriggered = false;
                    triggerTime = -1f;
                }
                lastEvalTime = currentTime;

                // 1. 如果尚未触发，检查原条件
                if (!isTriggered)
                {
                    if (originalCondition())
                    {
                        isTriggered = true;
                        triggerTime = currentTime;
                        targetDelay = delayProvider != null ? delayProvider.Invoke() : 0f;
                    }
                }
                else
                {
                    // 2. 如果已在倒计时中，优先判定打断条件
                    if (cancelIf != null && cancelIf.Invoke())
                    {
                        isTriggered = false; // 打断，重置倒计时
                        triggerTime = -1f;
                        return false;
                    }
                }

                // 3. 倒计时完成判定
                if (isTriggered)
                {
                    return (currentTime - triggerTime) >= targetDelay;
                }

                return false;
            };

            HasCondition = true;
            return this;
        }

        //@ 数据
        /// <summary>
        /// 直接注入静态常量或确定的引用对象 (优先将此数据注入Payload，不影响 OnTransition 解包) 目标必须包含 [Payload] 字段 
        /// </summary>
        public TransitionBuilder<TStateNode> WithPayload<TData>(TData data)
        {
            Rule.DataProvider = () => data;
            return this;
        }

        /// <summary>
        /// 切换状态时动态注入数据，未配置时重置为默认值，(优先将此数据注入Payload，不影响 OnTransition 解包) 目标必须包含 [Payload] 字段 
        /// </summary>
        /// <param name="dataProvider">注入委托</param>
        public TransitionBuilder<TStateNode> WithPayload<TData>(Func<TData> dataProvider)
        {
            if (dataProvider == null)
            {
                throw new Exception("WithData 传入的数据提供者(dataProvider)不能为空！");
            }

            Rule.DataProvider = () => dataProvider.Invoke();
            return this;
        }

        //@ 回调
        /// <summary>状态成功切换时触发的独立回调</summary>
        /// <param name="callback">在旧状态 OnExit 之后，新状态 OnEnter 之前执行</param>
        public TransitionBuilder<TStateNode> OnTransition(Action callback)
        {
            Rule.OnTransitionCallback += callback;
            return this;
        }

        /// <summary>完成构建并应用到状态机</summary>
        public virtual void Apply()
        {
            // 如果是装饰器包装出来的 Builder，将 Apply 权限交还给源头
            if (_sourceBuilder != null)
            {
                _sourceBuilder.Apply();
                return;
            }

            if (_isApplied) return;// 防呆设计
            _isApplied = true;

            foreach (var from in FromStates)
            {
                // 如果是回退规则 或 存在动态路由，不需要固定的 ToState
                if (Rule.IsRevert || Rule.DynamicToStateProvider != null || Rule.DynamicPayloadToStateProvider != null)
                {
                    ApplyRule(from, null);
                }
                else
                {
                    foreach (var to in ToStates)
                    {
                        ApplyRule(from, to);
                    }
                }
            }
        }

        // 注册逻辑
        protected void ApplyRule(Type from, Type to)
        {
            if (IsDualEvent)
            {
                var ruleExternal = Rule.Clone();
                ruleExternal.FromState = from;
                ruleExternal.ToState = to;
                ruleExternal.Trigger = TransitionTrigger.Dispatch;
                Machine.AddTransitionRule(ruleExternal);

                var ruleInternal = Rule.Clone();
                ruleInternal.FromState = from;
                ruleInternal.ToState = to;
                ruleInternal.Trigger = TransitionTrigger.Publish;
                Machine.AddTransitionRule(ruleInternal);
            }
            else
            {
                var finalRule = Rule.Clone();
                finalRule.FromState = from;
                finalRule.ToState = to;
                Machine.AddTransitionRule(finalRule);
            }
        }

    }
    //!--- [EventTransitionBuilder] ---
    /// <summary>
    /// 带有事件类型上下文的流式构建器（中间件）
    /// </summary>
    public class EventTransitionBuilder<TStateNode, TEvent> : TransitionBuilder<TStateNode>
        where TStateNode : class
    {
        internal EventTransitionBuilder(TransitionBuilder<TStateNode> parent) : base(parent) { }

        /// <summary>
        /// 当通过事件触发时，将事件数据(TEvent)转换为目标状态所需的数据(TData)并注入 (中间件)
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> WithPayload<TData>(Func<TEvent, TData> payloadMapper)
        {
            if (payloadMapper != null)
            {
                Rule.DynamicEventDataProvider = (eventData) => payloadMapper.Invoke((TEvent)eventData);
            }
            return this;
        }
        [Obsolete("事件规则中禁止再更改为轮询触发！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> OnUpdate() => this;
        [Obsolete("事件规则中禁止再更改为轮询触发！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> OnFixedUpdate() => this;
        [Obsolete("事件规则中禁止再更改为轮询触发！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> OnPostUpdate() => this;
        [Obsolete("事件驱动模式下不支持持续性时间检查，请改用轮询(Update/FixedUpdate)规则！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> WhenContinuously(Func<bool> condition, float duration) => this;
        [Obsolete("事件驱动模式下不支持持续性时间检查，请改用轮询(Update/FixedUpdate)规则！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> WhenContinuously(Func<bool> condition, Func<float> durationProvider) => this;
        [Obsolete("事件驱动模式下不支持持续性时间检查，请改用轮询(Update/FixedUpdate)规则！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> OrContinuously(Func<bool> condition, float duration) => this;
        [Obsolete("事件驱动模式下不支持持续性时间检查，请改用轮询(Update/FixedUpdate)规则！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> OrContinuously(Func<bool> condition, Func<float> durationProvider) => this;
        [Obsolete("事件驱动模式下不支持自动超时触发 (OrAfter)。如果需要超时自动切换，请额外添加一条独立的轮询规则 (例如再写一个 fsm.Bind().After())！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> OrAfter(float seconds) => this;
        [Obsolete("事件驱动模式下不支持自动超时触发 (OrAfter)。", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> OrAfter(Func<float> timeProvider) => this;
        [Obsolete("事件驱动模式下不支持持续性时间检查，请改用轮询(Update/FixedUpdate)规则！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> Delay(Func<float> delayProvider, Func<bool> cancelIf = null) => this;
        [Obsolete("事件驱动模式下不支持持续性时间检查，请改用轮询(Update/FixedUpdate)规则！", true)]
        public new EventTransitionBuilder<TStateNode, TEvent> Delay(float delaySeconds, Func<bool> cancelIf = null) => this;

        /// <summary>状态成功切换时触发的过渡回调 (中间件支持)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> OnTransition(Action callback)
        {
            base.OnTransition(callback);
            return this;
        }

        /// <summary>
        /// 状态成功切换时触发的过渡回调，可获取触发此切换的事件载荷数据（在旧状态 OnExit 之后，新状态 OnEnter 之前执行）
        /// 备注: 解包的是原始数据 不受 WithPayload 等功能的影响
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> OnTransition(Action<TEvent> callback)
        {
            // 将强类型的委托包装为底层的 object 委托
            Rule.OnTransitionCallbackWithPayload += (obj) => callback.Invoke((TEvent)obj);
            return this;
        }
        /// <summary>
        /// 响应式过滤：仅当事件携带的数据发生变化时，才允许通过此规则进行状态切换评估。
        /// 适用于 Update 中频繁 Send 相同数据的场景，防抖拦截。
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> DistinctUntilChanged()
        {
            TEvent lastValue = default;
            bool hasValue = false;

            Func<TEvent, bool> distinctFilter = (currentValue) =>
            {
                // 使用 ZeroGcEqualityComparer 进行无 GC 比较
                if (!hasValue || !ZeroGcEqualityComparer<TEvent>.EqualsFunc(currentValue, lastValue))
                {
                    lastValue = currentValue;
                    hasValue = true;
                    return true; // 数据不同，放行
                }
                return false; // 数据相同，拦截
            };

            // 将过滤逻辑叠加到现有的 PayloadCondition 中
            if (Rule.PayloadCondition is Func<TEvent, bool> existingCond)
            {
                Rule.PayloadCondition = (Func<TEvent, bool>)((val) => existingCond(val) && distinctFilter(val));
            }
            else
            {
                Rule.PayloadCondition = distinctFilter;
            }

            return this;
        }
        /// <summary>
        /// 事件节流阀：该规则在指定秒数内只响应并放行第一次
        /// 备注: 与 WithProbability 等搭配使用需注意顺序。Throttle(1f)->WithProbability 为1秒判定一次。反过来则持续判定然后等1秒执行
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> Throttle(double durationSeconds)
        {
            double lastPassTime = -durationSeconds;

            return Where((evt) =>
            {
                double currentTime = Machine.MachineTime;

                if (currentTime - lastPassTime < durationSeconds)
                {
                    return false;
                }

                lastPassTime = currentTime;
                return true;
            });
        }

        /// <summary>仅当事件载荷满足指定条件时才允许切换 (等价于附加 PayloadCondition)</summary>
        public EventTransitionBuilder<TStateNode, TEvent> Where(Func<TEvent, bool> predicate)
        {
            if (Rule.PayloadCondition is Func<TEvent, bool> existingCond)
            {
                Rule.PayloadCondition = (Func<TEvent, bool>)((val) => existingCond(val) && predicate(val));
            }
            else
            {
                Rule.PayloadCondition = predicate;
            }
            return this;
        }

        /// <summary>
        /// 概率门控：收到事件且满足条件时，只有 percent (0.0~1.0) 的概率允许通过
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> WithProbability(float percent)
        {
            // 性能优化：100% 概率直接放行，0% 概率直接死锁
            if (percent >= 1f) return this;
            if (percent <= 0f)
            {
                Rule.PayloadCondition = (Func<TEvent, bool>)((_) => false);
                return this;
            }

            // 捕获当前的条件（如果有的话）
            Delegate currentCondition = Rule.PayloadCondition;

            Func<TEvent, bool> probabilityCheck = (evt) =>
            {
                // 优先检查原条件
                if (currentCondition != null && !((Func<TEvent, bool>)currentCondition).Invoke(evt))
                {
                    return false;
                }

                return (float)GroupRouteSelector.Random.NextDouble() <= percent;
            };

            Rule.PayloadCondition = probabilityCheck;

            return this;
        }

        /// <summary>
        /// 标记此规则触发后，消费掉该事件（阻止事件传播到其他路由节点，不影响本地）
        /// <param name="condition">可选：动态消费条件。留空则代表无条件绝对消费。</param>
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> Consume(Func<bool> condition = null)
        {
            Rule.IsConsumed = condition == null;
            Rule.ConsumeCondition = condition;
            return this;
        }

        /// <summary>
        /// 标记此规则触发后，消费掉该事件（阻止事件传播到其他路由节点，不影响本地）
        /// <param name="condition">动态消费条件：可提取事件载荷(TEvent)进行判断。</param>
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> Consume(Func<TEvent, bool> condition)
        {
            Rule.IsConsumed = condition == null;
            Rule.ConsumeCondition = condition;
            return this;
        }
        // ================== 保持链式调用上下文 ==================
        // new 关键字（方法隐藏）不影响性能，重写那些返回 TransitionBuilder 的方法 ，返回 EventTransitionBuilder 自身

        /// <summary>满足条件则尝试切换（优先配置）(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> When(Func<bool> condition) { base.When(condition); return this; }
        /// <summary>满足前置条件 并且满足此规则(When未配置时，And等价When) (中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> And(Func<bool> condition) { base.And(condition); return this; }
        /// <summary>除非满足此条件，否则允许切换（等价于 And( !condition )）(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> Unless(Func<bool> condition) { base.Unless(condition); return this; }
        /// <summary>满足前置条件 或满足此规则 (When未配置时，Or等价When) (中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> Or(Func<bool> condition) { base.Or(condition); return this; }
        /// <summary>设置优先级（数值越大优先级越高，默认0）(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> WithPriority(int priority) { base.WithPriority(priority); return this; }
        /// <summary>在当前状态停留指定的 Update 帧数后允许切换 (精确值)(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> AfterFrames(int frames, bool useFixedFrames = false) { base.AfterFrames(frames, useFixedFrames); return this; }
        /// <summary>在当前状态停留指定时间后允许切换</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> After(float seconds) { base.After(seconds); return this; }
        /// <summary>在当前状态停留动态指定的时间后允许切换 (每次轮询都会调用 provider 重新获取时间)(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> After(Func<float> timeProvider) { base.After(timeProvider); return this; }
        /// <summary>在当前状态停留随机的时间后允许切换（进入状态时固定一次随机值）(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> AfterRandom(float minSeconds, float maxSeconds) { base.AfterRandom(minSeconds, maxSeconds); return this; }
        /// <summary>在当前状态停留时间小于等于指定时间前允许切换 (中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> Within(float seconds) { base.Within(seconds); return this; }
        /// <summary>动态获取时间窗口限制 (中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> Within(Func<float> timeProvider) { base.Within(timeProvider); return this; }
        /// <summary>当前状态停留的时间处于特定区间内时允许切换 (中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> Between(float minSeconds, float maxSeconds) { base.Between(minSeconds, maxSeconds); return this; }
        /// <summary>当前状态停留的时间处于特定区间内时允许切换 元组委托 (中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> Between(Func<(float min, float max)> windowProvider) { base.Between(windowProvider); return this; }
        /// <summary>当前状态停留的时间处于特定区间内时允许切换 双委托 (中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> Between(Func<float> minProvider, Func<float> maxProvider) { base.Between(minProvider, maxProvider); return this; }
        /// <summary>冷却时间：该规则在成功触发切换后，在指定的秒数内将被冻结。(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> Cooldown(float cooldownSeconds) { base.Cooldown(cooldownSeconds); return this; }
        /// <summary>冷却时间 (动态)：每次验证时动态获取冷却时间。(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> Cooldown(Func<float> cooldownProvider) { base.Cooldown(cooldownProvider); return this; }
        /// <summary>直接注入静态常量或确定的引用对象(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> WithPayload<TData>(TData data) { base.WithPayload(data); return this; }
        /// <summary>切换状态时动态注入数据，未配置时重置为默认值，目标必须包含 [Payload] 字段 (中间件)</summary> 
        public new EventTransitionBuilder<TStateNode, TEvent> WithPayload<TData>(Func<TData> dataProvider) { base.WithPayload(dataProvider); return this; }
        /// <summary> 只有当上一个状态是指定类型时，才允许执行此规则(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> WhenLastStateIs<TState>() where TState : class, TStateNode { base.WhenLastStateIs<TState>(); return this; }
        /// <summary>只要上一个状态不是指定类型，就允许执行此规则(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> UnlessLastStateWas<TState>() where TState : class, TStateNode { base.UnlessLastStateWas<TState>(); return this; }
        /// <summary>提取当前状态的强类型实例进行条件判定(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> WhenState(Func<TStateNode, bool> predicate) { base.WhenState(predicate); return this; }
        /// <summary>当当前状态是指定类型 TCustomGroup 时，提取该状态的强类型实例进行条件判定(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> WhenState<TCustomGroup>(Func<TCustomGroup, bool> predicate) where TCustomGroup : class { base.WhenState<TCustomGroup>(predicate); return this; }
        /// <summary>当状态机中注册了指定类型 TContext 的上下文时，提取强类型实例进行条件判定(中间件)</summary>
        public new EventTransitionBuilder<TStateNode, TEvent> WhenContext<TContext>(Func<TContext, bool> predicate) where TContext : class { base.WhenContext(predicate); return this; }
    }

    //!--- [DynamicBuilder] ---
    /// <summary>
    /// 动态路由专用的构建器 
    /// </summary>
    public class DynamicTransitionBuilder<TStateNode> : TransitionBuilder<TStateNode>
        where TStateNode : class
    {
        internal DynamicTransitionBuilder(ITransitionRuleReceiver machine, Type[] fromStates, Type[] toStates, bool forceReenter)
            : base(machine, fromStates, toStates, forceReenter) { }

        // 备注: To..配置后降级为TransitionBuilder屏蔽再次调用
        /// <summary> (Dynamic) 根据外部条件动态决定目标状态</summary>
        public TransitionBuilder<TStateNode> ToDynamic(Func<Type> stateProvider)
        {
            if (stateProvider == null) throw new ArgumentNullException(nameof(stateProvider));

            Rule.DynamicToStateProvider = stateProvider;
            return this; // 隐式向上转型为基类 TransitionBuilder
        }
        /// <summary> 
        /// (Dynamic) 捕获源状态或分组的强类型实例，根据其公开属性/字段动态决定目标状态。
        /// 若当前状态类型不匹配或委托返回 null，则放弃本次切换。
        /// </summary>
        public TransitionBuilder<TStateNode> ToDynamic(Func<TStateNode, Type> stateProvider)
        {
            if (stateProvider == null) throw new ArgumentNullException(nameof(stateProvider));

            Rule.DynamicToStateProvider = () =>
            {
                if (Machine.CurrentStateNode is TStateNode typedState)
                {
                    return stateProvider.Invoke(typedState);
                }
                return null;
            };
            return this; // 隐式向上转型为 TransitionBuilder
        }

        /// <summary> (Dynamic) 切换到实现了 TGroup (接口/基类) 的状态组中的某一个。</summary>
        public TransitionBuilder<TStateNode> ToGroup<TGroup>(GroupSelectionMode mode = GroupSelectionMode.RandomWeighted) where TGroup : class
        {
            var matchingStates = Machine.RegisteredStates.Where(t => typeof(TGroup).IsAssignableFrom(t)).ToArray();
            if (matchingStates.Length == 0) throw new Exception($"ToGroup<{typeof(TGroup).Name}> 未找到匹配状态！");

            Rule.DynamicToStateProvider = GroupRouteSelector.CreateStaticProvider(matchingStates, mode);
            return this;
        }

        /// <summary> (Dynamic) 动态计算权重的分组路由 </summary>
        public TransitionBuilder<TStateNode> ToGroup<TGroup>(Func<Type, int> dynamicWeightSelector, GroupSelectionMode mode = GroupSelectionMode.RandomWeighted) where TGroup : class
        {
            var matchingStates = Machine.RegisteredStates.Where(t => typeof(TGroup).IsAssignableFrom(t)).ToArray();
            if (matchingStates.Length > 0)
                Rule.DynamicToStateProvider = GroupRouteSelector.CreateDynamicProvider(matchingStates, dynamicWeightSelector, mode);
            return this;
        }

        /// <summary>(Dynamic) 支持提取事件载荷判断动态目标</summary>
        public new DynamicEventTransitionBuilder<TStateNode, TEvent> OnDispatch<TEvent>(Func<TEvent, bool> condition = null)
        {
            base.OnDispatch(condition);
            return new DynamicEventTransitionBuilder<TStateNode, TEvent>(this);
        }
        /// <summary>(Dynamic) 支持提取事件载荷判断动态目标</summary>
        public new DynamicEventTransitionBuilder<TStateNode, TEvent> OnPublish<TEvent>(Func<TEvent, bool> condition = null)
        {
            base.OnPublish(condition);
            return new DynamicEventTransitionBuilder<TStateNode, TEvent>(this);
        }
        /// <summary>(Dynamic) 支持提取事件载荷判断动态目标</summary>
        public new DynamicEventTransitionBuilder<TStateNode, TEvent> OnEvent<TEvent>(Func<TEvent, bool> condition = null)
        {
            base.OnEvent(condition);
            return new DynamicEventTransitionBuilder<TStateNode, TEvent>(this);
        }
        /// <summary>(Dynamic) 支持提取事件载荷判断动态目标</summary>
        public new DynamicEventTransitionBuilder<TStateNode, TShared> OnSharedValueChanged<TShared>(Func<TShared, bool> condition = null)
            where TShared : class, ISharedVar
        {
            base.OnDispatch(condition);
            return new DynamicEventTransitionBuilder<TStateNode, TShared>(this);
        }
        // 保持原版的调用顺序，避免只有事件可以调用
        public new DynamicTransitionBuilder<TStateNode> OnUpdate() { base.OnUpdate(); return this; }
        public new DynamicTransitionBuilder<TStateNode> OnFixedUpdate() { base.OnFixedUpdate(); return this; }
        public new DynamicTransitionBuilder<TStateNode> OnPostUpdate() { base.OnPostUpdate(); return this; }

        /// <summary>满足条件则尝试切换（优先配置）(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> When(Func<bool> condition) { base.When(condition); return this; }
        /// <summary>满足前置条件 并且满足此规则(When未配置时，And等价When) (Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> And(Func<bool> condition) { base.And(condition); return this; }
        /// <summary>除非满足此条件，否则允许切换（等价于 And( !condition )）(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> Unless(Func<bool> condition) { base.Unless(condition); return this; }
        /// <summary>满足前置条件 或满足此规则 (When未配置时，Or等价When) (Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> Or(Func<bool> condition) { base.Or(condition); return this; }
        /// <summary>设置优先级（数值越大优先级越高，默认0）(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> WithPriority(int priority) { base.WithPriority(priority); return this; }
        /// <summary>在当前状态停留指定的 Update 帧数后允许切换 (精确值)(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> AfterFrames(int frames, bool useFixedFrames = false) { base.AfterFrames(frames, useFixedFrames); return this; }
        /// <summary>在当前状态停留指定时间后允许切换</summary>
        public new DynamicTransitionBuilder<TStateNode> After(float seconds) { base.After(seconds); return this; }
        /// <summary>在当前状态停留动态指定的时间后允许切换 (每次轮询都会调用 provider 重新获取时间)(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> After(Func<float> timeProvider) { base.After(timeProvider); return this; }
        /// <summary>或者在当前状态停留指定时间后允许切换（Dynamic）</summary>
        public new DynamicTransitionBuilder<TStateNode> OrAfter(float seconds) { base.OrAfter(seconds); return this; }
        /// <summary>或者在当前状态停留动态指定的时间后允许切换（Dynamic）</summary>
        public new DynamicTransitionBuilder<TStateNode> OrAfter(Func<float> timeProvider) { base.OrAfter(timeProvider); return this; }
        /// <summary>在当前状态停留随机的时间后允许切换（进入状态时固定一次随机值）(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> AfterRandom(float minSeconds, float maxSeconds) { base.AfterRandom(minSeconds, maxSeconds); return this; }
        /// <summary>在当前状态停留时间小于等于指定时间前允许切换 (Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> Within(float seconds) { base.Within(seconds); return this; }
        /// <summary>动态获取时间窗口限制 (Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> Within(Func<float> timeProvider) { base.Within(timeProvider); return this; }
        /// <summary>当前状态停留的时间处于特定区间内时允许切换 (Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> Between(float minSeconds, float maxSeconds) { base.Between(minSeconds, maxSeconds); return this; }
        /// <summary>当前状态停留的时间处于特定区间内时允许切换 元组委托 (Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> Between(Func<(float min, float max)> windowProvider) { base.Between(windowProvider); return this; }
        /// <summary>当前状态停留的时间处于特定区间内时允许切换 双委托 (Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> Between(Func<float> minProvider, Func<float> maxProvider) { base.Between(minProvider, maxProvider); return this; }
        /// <summary>冷却时间：该规则在成功触发切换后，在指定的秒数内将被冻结。(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> Cooldown(float cooldownSeconds) { base.Cooldown(cooldownSeconds); return this; }
        /// <summary>冷却时间 (动态)：每次验证时动态获取冷却时间。(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> Cooldown(Func<float> cooldownProvider) { base.Cooldown(cooldownProvider); return this; }
        /// <summary>直接注入静态常量或确定的引用对象(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> WithPayload<TData>(TData data) { base.WithPayload(data); return this; }
        /// <summary>切换状态时动态注入数据，未配置时重置为默认值，目标必须包含 [Payload] 字段 (Dynamic)</summary> 
        public new DynamicTransitionBuilder<TStateNode> WithPayload<TData>(Func<TData> dataProvider) { base.WithPayload(dataProvider); return this; }
        /// <summary>只有当上一个状态是指定类型时，才允许执行此规则(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> WhenLastStateIs<TState>() where TState : class, TStateNode { base.WhenLastStateIs<TState>(); return this; }
        /// <summary>只要上一个状态不是指定类型，就允许执行此规则(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> UnlessLastStateWas<TState>() where TState : class, TStateNode { base.UnlessLastStateWas<TState>(); return this; }
        /// <summary>提取当前状态的强类型实例进行条件判定(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> WhenState(Func<TStateNode, bool> predicate) { base.WhenState(predicate); return this; }
        /// <summary>当当前状态是指定类型 TCustomGroup 时，提取该状态的强类型实例进行条件判定(Dynamic)</summary>
        public new DynamicTransitionBuilder<TStateNode> WhenState<TCustomGroup>(Func<TCustomGroup, bool> predicate) where TCustomGroup : class { base.WhenState<TCustomGroup>(predicate); return this; }
        /// <summary>当状态机中注册了指定类型 TContext 的上下文时，提取强类型实例进行条件判定(中间件)</summary>
        public new DynamicTransitionBuilder<TStateNode> WhenContext<TContext>(Func<TContext, bool> predicate) where TContext : class { base.WhenContext(predicate); return this; }

        public new DynamicTransitionBuilder<TStateNode> Delay(float delaySeconds, Func<bool> cancelIf = null) { base.Delay(delaySeconds, cancelIf); return this; }
        public new DynamicTransitionBuilder<TStateNode> Delay(Func<float> delayProvider, Func<bool> cancelIf = null) { base.Delay(delayProvider, cancelIf); return this; }
        public new DynamicTransitionBuilder<TStateNode> WhenContinuously(Func<bool> condition, float duration) { base.WhenContinuously(condition, duration); return this; }
        public new DynamicTransitionBuilder<TStateNode> WhenContinuously(Func<bool> condition, Func<float> durationProvider) { base.WhenContinuously(condition, durationProvider); return this; }
        public new DynamicTransitionBuilder<TStateNode> OrContinuously(Func<bool> condition, float duration) { base.OrContinuously(condition, duration); return this; }
        public new DynamicTransitionBuilder<TStateNode> OrContinuously(Func<bool> condition, Func<float> durationProvider) { base.OrContinuously(condition, durationProvider); return this; }
    }

    /// <summary>动态路由+事件驱动 专用的构建器</summary>
    public class DynamicEventTransitionBuilder<TStateNode, TEvent> : EventTransitionBuilder<TStateNode, TEvent>
        where TStateNode : class
    {
        internal DynamicEventTransitionBuilder(DynamicTransitionBuilder<TStateNode> parent) : base(parent) { }

        /// <summary>(DynamicEvent) 根据事件载荷动态决定目标状态 </summary>
        public EventTransitionBuilder<TStateNode, TEvent> ToDynamic(Func<TEvent, Type> stateProvider)
        {
            Rule.DynamicPayloadToStateProvider = stateProvider;
            return this; // 执行完毕后降级回基类 EventTransitionBuilder
        }

        /// <summary>(DynamicEvent) 无参的动态决定目标状态 </summary>
        public EventTransitionBuilder<TStateNode, TEvent> ToDynamic(Func<Type> stateProvider)
        {
            Rule.DynamicToStateProvider = stateProvider;
            return this;
        }

        /// <summary>
        /// (DynamicEvent) 捕获源状态或分组的强类型实例，根据其属性动态决定目标状态
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> ToDynamic(Func<TStateNode, Type> stateProvider)

        {
            if (stateProvider == null) throw new ArgumentNullException(nameof(stateProvider));

            Rule.DynamicToStateProvider = () =>
            {
                if (Machine.CurrentStateNode is TStateNode typedState)
                {
                    return stateProvider.Invoke(typedState);
                }
                return null;
            };
            return this;
        }

        /// <summary>
        /// (DynamicEvent) 同时结合【当前状态实例】与【收到的事件载荷数据】，动态决定目标状态
        /// </summary>
        public EventTransitionBuilder<TStateNode, TEvent> ToDynamic(Func<TStateNode, TEvent, Type> stateProvider)
        {
            if (stateProvider == null) throw new ArgumentNullException(nameof(stateProvider));

            Rule.DynamicPayloadToStateProvider = (Func<TEvent, Type>)((evt) =>
            {
                if (Machine.CurrentStateNode is TStateNode typedState)
                {
                    return stateProvider.Invoke(typedState, evt);
                }
                return null;
            });
            return this;
        }

        /// <summary>(DynamicEvent) 无参根据 模式/StateWeight 切换到实现了 TGroup 的状态组</summary>
        public EventTransitionBuilder<TStateNode, TEvent> ToGroup<TGroup>(GroupSelectionMode mode = GroupSelectionMode.RandomWeighted) where TGroup : class
        {
            var matchingStates = Machine.RegisteredStates.Where(t => typeof(TGroup).IsAssignableFrom(t)).ToArray();
            if (matchingStates.Length > 0)
                Rule.DynamicToStateProvider = GroupRouteSelector.CreateStaticProvider(matchingStates, mode);
            return this;
        }

        /// <summary>(DynamicEvent) 根据事件载荷与状态类型实时计算权重的分组路由</summary>
        public EventTransitionBuilder<TStateNode, TEvent> ToGroup<TGroup>(Func<TEvent, Type, int> dynamicWeightSelector, GroupSelectionMode mode = GroupSelectionMode.RandomWeighted) where TGroup : class
        {
            var matchingStates = Machine.RegisteredStates.Where(t => typeof(TGroup).IsAssignableFrom(t)).ToArray();
            if (matchingStates.Length > 0)
                Rule.DynamicPayloadToStateProvider = GroupRouteSelector.CreateDynamicPayloadProvider<TEvent>(matchingStates, dynamicWeightSelector, mode);
            return this;
        }

        public new DynamicEventTransitionBuilder<TStateNode, TEvent> Throttle(double durationSeconds) { base.Throttle(durationSeconds); return this; }
        public new DynamicEventTransitionBuilder<TStateNode, TEvent> OnTransition(Action<TEvent> callback) { base.OnTransition(callback); return this; }
        public new DynamicEventTransitionBuilder<TStateNode, TEvent> OnTransition(Action callback) { base.OnTransition(callback); return this; }
        public new DynamicEventTransitionBuilder<TStateNode, TEvent> WithPayload<TData>(Func<TEvent, TData> payloadMapper) { base.WithPayload(payloadMapper); return this; }
        public new DynamicEventTransitionBuilder<TStateNode, TEvent> Where(Func<TEvent, bool> predicate) { base.Where(predicate); return this; }
        public new DynamicEventTransitionBuilder<TStateNode, TEvent> WithProbability(float percent) { base.WithProbability(percent); return this; }

        public new DynamicEventTransitionBuilder<TStateNode, TEvent> Consume(Func<bool> condition = null) { base.Consume(condition); return this; }
        public new DynamicEventTransitionBuilder<TStateNode, TEvent> Consume(Func<TEvent, bool> condition) { base.Consume(condition); return this; }

    }

    /// <summary>
    /// 序列状态切换构建器（用于配置如连招等线性状态链，共享同一套触发规则）
    /// </summary>
    public class SequenceTransitionBuilder<TStateNode> : DynamicTransitionBuilder<TStateNode>
        where TStateNode : class, IStateNode
    {
        // 存储链路上的所有步骤 (支持多对多，以及带事件载荷的动态路由)
        protected internal List<(Type[] Froms, Type[] Tos, Func<Type> DynamicTo, Delegate DynamicPayloadTo)> _sequenceSteps = new();
        // 记录当前链条末端的所有可能状态
        protected internal Type[] _currentTipTypes;

        internal SequenceTransitionBuilder(ITransitionRuleReceiver machine, Type firstState, bool forceReenter)
            : base(machine, new[] { firstState }, new Type[0], forceReenter)
        {
            _currentTipTypes = new[] { firstState };
        }

        /// <summary>连接到下一个具名状态</summary>
        public SequenceTransitionBuilder<TStateNode> Next<TNext>() where TNext : class, TStateNode
        {
            if (_currentTipTypes == null || _currentTipTypes.Length == 0)
            {
                throw new Exception("不能在未知的动态状态之后继续调用 Next。请在使用 NextDynamic 时显式传入 possibleOutcomes，或改用 NextGroup。");
            }

            _sequenceSteps.Add((_currentTipTypes, new[] { typeof(TNext) }, null, null));
            _currentTipTypes = new[] { typeof(TNext) };
            return this;
        }

        /// <summary>连接到动态决定的下一个状态</summary>
        public SequenceTransitionBuilder<TStateNode> NextDynamic(Func<Type> dynamicStateProvider, params Type[] possibleOutcomes)
        {
            if (_currentTipTypes == null || _currentTipTypes.Length == 0)
            {
                throw new Exception("不能在未知的动态状态之后继续调用 NextDynamic。");
            }

            _sequenceSteps.Add((_currentTipTypes, null, dynamicStateProvider, null));
            _currentTipTypes = (possibleOutcomes != null && possibleOutcomes.Length > 0) ? possibleOutcomes : null;
            return this;
        }

        /// <summary>连接到实现了 TGroup (接口/基类) 的状态组中的某一个</summary>
        public SequenceTransitionBuilder<TStateNode> NextGroup<TGroup>(GroupSelectionMode mode = GroupSelectionMode.RandomWeighted) where TGroup : class
        {
            if (_currentTipTypes == null || _currentTipTypes.Length == 0)
            {
                throw new Exception("不能在未知的动态状态之后继续调用 NextGroup。");
            }

            var matchingStates = Machine.RegisteredStates.Where(t => typeof(TGroup).IsAssignableFrom(t)).ToArray();
            if (matchingStates.Length == 0)
            {
                throw new Exception($"NextGroup<{typeof(TGroup).Name}> 未找到匹配的已注册状态！链条将在此断开。");

            }

            var dynamicProvider = GroupRouteSelector.CreateStaticProvider(matchingStates, mode);
            _sequenceSteps.Add((_currentTipTypes, null, dynamicProvider, null));
            _currentTipTypes = matchingStates;
            return this;
        }

        // ================== [桥接] 转入事件驱动模式 ==================
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> OnDispatch<TEvent>(Func<TEvent, bool> condition = null)
        {
            base.OnDispatch(condition);
            return new SequenceEventTransitionBuilder<TStateNode, TEvent>(this);
        }
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> OnPublish<TEvent>(Func<TEvent, bool> condition = null)
        {
            base.OnPublish(condition);
            return new SequenceEventTransitionBuilder<TStateNode, TEvent>(this);
        }
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> OnEvent<TEvent>(Func<TEvent, bool> condition = null)
        {
            base.OnEvent(condition);
            return new SequenceEventTransitionBuilder<TStateNode, TEvent>(this);
        }
        public new SequenceEventTransitionBuilder<TStateNode, TShared> OnSharedValueChanged<TShared>(Func<TShared, bool> condition = null) where TShared : class, ISharedVar
        {
            base.OnSharedValueChanged(condition);
            return new SequenceEventTransitionBuilder<TStateNode, TShared>(this);
        }

        /// <summary>重写应用逻辑：顺序解包链路并应用共享规则</summary>
        public override void Apply()
        {
            // 如果是装饰器触发的 Apply，交还给最底层的构造器统一执行
            if (_sourceBuilder != null) { _sourceBuilder.Apply(); return; }
            if (_isApplied) return;
            _isApplied = true;

            foreach (var step in _sequenceSteps)
            {
                foreach (var fromState in step.Froms)
                {
                    if (step.DynamicPayloadTo != null)
                    {
                        Rule.DynamicPayloadToStateProvider = step.DynamicPayloadTo;
                        ApplyRule(fromState, null);
                        Rule.DynamicPayloadToStateProvider = null; // 恢复，避免污染下一步
                    }
                    else if (step.DynamicTo != null)
                    {
                        Rule.DynamicToStateProvider = step.DynamicTo;
                        ApplyRule(fromState, null);
                        Rule.DynamicToStateProvider = null;
                    }
                    else
                    {
                        foreach (var toState in step.Tos)
                        {
                            ApplyRule(fromState, toState);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 支持事件载荷 (Payload) 的序列状态切换构建器包装器
    /// </summary>
    public class SequenceEventTransitionBuilder<TStateNode, TEvent> : DynamicEventTransitionBuilder<TStateNode, TEvent>
        where TStateNode : class, IStateNode
    {
        private SequenceTransitionBuilder<TStateNode> _seqSource;

        internal SequenceEventTransitionBuilder(SequenceTransitionBuilder<TStateNode> parent) : base(parent)
        {
            _seqSource = parent; // 引用父级数据源
        }

        public SequenceEventTransitionBuilder<TStateNode, TEvent> Next<TNext>() where TNext : class, TStateNode
        {
            _seqSource.Next<TNext>();
            return this;
        }

        public SequenceEventTransitionBuilder<TStateNode, TEvent> NextDynamic(Func<Type> dynamicStateProvider, params Type[] possibleOutcomes)
        {
            _seqSource.NextDynamic(dynamicStateProvider, possibleOutcomes);
            return this;
        }

        /// <summary>连接到动态决定的下一个状态，支持提取事件载荷(TEvent)</summary>
        public SequenceEventTransitionBuilder<TStateNode, TEvent> NextDynamic(Func<TEvent, Type> dynamicPayloadProvider, params Type[] possibleOutcomes)
        {
            if (_seqSource._currentTipTypes == null || _seqSource._currentTipTypes.Length == 0)
            {
                throw new Exception("不能在未知的动态状态之后继续调用 NextDynamic。");
            }

            _seqSource._sequenceSteps.Add((_seqSource._currentTipTypes, null, null, dynamicPayloadProvider));
            _seqSource._currentTipTypes = (possibleOutcomes != null && possibleOutcomes.Length > 0) ? possibleOutcomes : null;
            return this;
        }

        public SequenceEventTransitionBuilder<TStateNode, TEvent> NextGroup<TGroup>(GroupSelectionMode mode = GroupSelectionMode.RandomWeighted) where TGroup : class
        {
            _seqSource.NextGroup<TGroup>(mode);
            return this;
        }

        /// <summary>根据事件载荷动态计算权重，连接到实现了 TGroup 的状态组</summary>
        public SequenceEventTransitionBuilder<TStateNode, TEvent> NextGroup<TGroup>(Func<TEvent, Type, int> dynamicWeightSelector, GroupSelectionMode mode = GroupSelectionMode.RandomWeighted) where TGroup : class
        {
            if (_seqSource._currentTipTypes == null || _seqSource._currentTipTypes.Length == 0)
            {
                throw new Exception("不能在未知的动态状态之后继续调用 NextGroup。");
            }

            var matchingStates = Machine.RegisteredStates.Where(t => typeof(TGroup).IsAssignableFrom(t)).ToArray();
            if (matchingStates.Length == 0)
            {
                throw new Exception($"NextGroup<{typeof(TGroup).Name}> 未找到匹配的已注册状态！");
            }

            var dynamicProvider = GroupRouteSelector.CreateDynamicPayloadProvider<TEvent>(matchingStates, dynamicWeightSelector, mode);
            _seqSource._sequenceSteps.Add((_seqSource._currentTipTypes, null, null, dynamicProvider));
            _seqSource._currentTipTypes = matchingStates;
            return this;
        }

        // ================== 保持链式调用上下文 (Method Hiding) ==================
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> Throttle(double durationSeconds) { base.Throttle(durationSeconds); return this; }
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> OnTransition(Action<TEvent> callback) { base.OnTransition(callback); return this; }
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> OnTransition(Action callback) { base.OnTransition(callback); return this; }
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> WithPayload<TData>(Func<TEvent, TData> payloadMapper) { base.WithPayload(payloadMapper); return this; }
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> Where(Func<TEvent, bool> predicate) { base.Where(predicate); return this; }
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> WithProbability(float percent) { base.WithProbability(percent); return this; }

        public new SequenceEventTransitionBuilder<TStateNode, TEvent> Consume(Func<bool> condition = null) { base.Consume(condition); return this; }
        public new SequenceEventTransitionBuilder<TStateNode, TEvent> Consume(Func<TEvent, bool> condition) { base.Consume(condition); return this; }
    }

    /// <summary>回退专用构建器</summary>
    /// <typeparam name="TStateNode"></typeparam>
    internal class FallbackTransitionBuilder<TStateNode> : TransitionBuilder<TStateNode> where TStateNode : class
    {
        internal FallbackTransitionBuilder(ITransitionRuleReceiver machine, Type[] fromStates, bool ignoreRules, Type skipGroupType)
            : base(machine, fromStates, new Type[0], false)
        {
            Rule.IsRevert = true;
            Rule.IgnoreRules = ignoreRules;
            Rule.SkipGroupType = skipGroupType;
        }
    }
    #endregion
    //!============== Guard ==============
    #region Guard
    /// <summary>内部规则数据结构 (保持不变，底层检索最高效)</summary>
    internal class GuardRule
    {
        public Type FromState;
        public Type ToState;
        public Func<bool> Condition;
    }

    /// <summary>防御规则构建器，用于阻止不合法的切换</summary>
    public class GuardBuilder<TStateNode> : IAutoApplicable where TStateNode : class
    {
        private bool _isApplied = false;

        private ITransitionRuleReceiver _machine;
        private Type[] _fromStates;
        private Type[] _toStates;
        private Func<bool> _condition = () => false; //默认false

        internal GuardBuilder(ITransitionRuleReceiver machine, Type[] fromStates, Type[] toStates)
        {
            _machine = machine;
            _fromStates = fromStates;
            _toStates = toStates;
        }

        /// <summary>
        /// 设置基础拦截条件。当 condition 返回 true 时，拦截并禁止切换。
        /// </summary>
        public GuardBuilder<TStateNode> BlockIf(Func<bool> condition)
        {
            _condition = condition;
            return this;
        }

        /// <summary>
        /// [联合拦截] 前置条件 AND 当前条件都满足时，才拦截。
        /// 例: BlockIf(isAttacked).AndIf(noSuperArmor) -> 受击且没霸体时拦截
        /// </summary>
        public GuardBuilder<TStateNode> AndIf(Func<bool> condition)
        {
            Func<bool> prevCondition = _condition;
            _condition = () => prevCondition() && condition();
            return this;
        }

        /// <summary>
        /// [扩大拦截] 前置条件 OR 当前条件满足其一时，就拦截。
        /// 例: BlockIf(isStunned).OrIf(isDead) -> 眩晕或死亡时拦截
        /// </summary>
        public GuardBuilder<TStateNode> OrIf(Func<bool> condition)
        {
            Func<bool> prevCondition = _condition;
            _condition = () => prevCondition() || condition();
            return this;
        }

        /// <summary>
        /// [豁免/放行] 满足拦截条件，但如果该条件为 true，则强制放行。
        /// 例: BlockIf(isStunned).AllowIf(isInvincible) -> 眩晕时拦截，但无敌时放行
        /// </summary>
        public GuardBuilder<TStateNode> AllowIf(Func<bool> condition)
        {
            Func<bool> prevCondition = _condition;
            _condition = () => prevCondition() && !condition();
            return this;
        }

        // ==========================================
        // 提取当前状态 TState 的强类型版本
        // ==========================================

        /// <summary>
        ///  当当前状态满足指定条件时拦截
        /// </summary>
        public GuardBuilder<TStateNode> BlockIf(Func<TStateNode, bool> condition)
        {
            return BlockIf(() =>
            {
                if (_machine.CurrentStateNode is TStateNode typedState)
                    return condition.Invoke(typedState);
                return false;
            });
        }
        /// <summary>
        /// [联合拦截] 当前状态满足条件 且 前置条件满足时拦截
        /// </summary>
        public GuardBuilder<TStateNode> AndIf(Func<TStateNode, bool> condition)
        {
            return AndIf(() =>
            {
                if (_machine.CurrentStateNode is TStateNode typedState)
                    return condition.Invoke(typedState);
                return false;
            });
        }
        /// <summary>
        /// [扩大拦截] 当前状态满足条件 或 前置条件满足时拦截
        /// </summary>
        public GuardBuilder<TStateNode> OrIf(Func<TStateNode, bool> condition)
        {
            return OrIf(() =>
            {
                if (_machine.CurrentStateNode is TStateNode typedState)
                    return condition.Invoke(typedState);
                return false;
            });
        }

        /// <summary>
        /// [豁免放行] 当当前状态满足特定条件时强制放行 (即使触发了 Block 也不拦截)
        /// </summary>
        public GuardBuilder<TStateNode> AllowIf(Func<TStateNode, bool> condition)
        {
            return AllowIf(() =>
            {
                if (_machine.CurrentStateNode is TStateNode typedState)
                    return condition.Invoke(typedState);
                return false;
            });
        }
        // ==========================================
        /// <summary>
        /// (语法糖) 基于指定的强类型 Context 决定是否拦截。
        /// </summary>
        public GuardBuilder<TStateNode> BlockIfContext<TContext>(Func<TContext, bool> predicate) where TContext : class
        {
            return BlockIf(() =>
            {
                if (_machine.TryGetContext(typeof(TContext), out var ctxObj) && ctxObj is TContext typedCtx)
                {
                    return predicate(typedCtx);
                }
                return false;
            });
        }
        /// <summary>完成构建并应用到状态机</summary>
        public void Apply()
        {
            if (_isApplied) return;
            _isApplied = true;

            // 笛卡尔积展开，底层依然是 1对1 高效检索
            foreach (var from in _fromStates)
            {
                foreach (var to in _toStates)
                {
                    _machine.AddGuardRule(new GuardRule
                    {
                        FromState = from,
                        ToState = to,
                        Condition = _condition
                    });
                }
            }
        }
    }
    #endregion

    //!========= 批量配置 ===========
    #region RuleConfigurator
    /// <summary>自动应用接口，用于防重和批量执行</summary>
    internal interface IAutoApplicable { void Apply(); }

    /// <summary>
    /// 规则配置作用域。在此作用域内创建的任何规则，无需再手动调用 .Apply()
    /// </summary>
    public class RuleConfigurator<TStateNode> where TStateNode : class, IStateNode
    {
        private IStateMachineConfigurator<TStateNode> _fsm;
        private List<IAutoApplicable> _pendingBuilders = new();

        internal RuleConfigurator(IStateMachineConfigurator<TStateNode> fsm)
        {
            _fsm = fsm;
        }

        // 核心拦截器：记录生成的 Builder
        private TBuilder Track<TBuilder>(TBuilder builder) where TBuilder : IAutoApplicable
        {
            _pendingBuilders.Add(builder);
            return builder;
        }

        internal void ApplyAll()
        {
            foreach (var builder in _pendingBuilders)
            {
                builder.Apply();
            }
            _pendingBuilders.Clear();
        }
        // ================= 镜像映射所有的 Bind 和 Guard API =================

        /// <summary>绑定特定状态之间的自动切换 (检查时机默认OnUpdate，可显式指定) 可省略.Apply()</summary>
        public TransitionBuilder<TFrom> Bind<TFrom, TTo>(bool forceReenter = false) where TFrom : class, TStateNode where TTo : class, TStateNode
            => Track(_fsm.Bind<TFrom, TTo>(forceReenter));

        /// <summary>(任意->目标) 自动切换 (检查时机默认OnUpdate，可显式指定) 可省略.Apply()</summary>
        public TransitionBuilder<TStateNode> BindAny<TTo>(bool forceReenter = false) where TTo : class, TStateNode
            => Track(_fsm.BindAny<TTo>(forceReenter));

        /// <summary>(From->动态目标) 根据外部条件动态决定目标状态 (返回 null 代表放弃切换) 可省略.Apply()</summary>
        public DynamicTransitionBuilder<TFrom> BindDynamic<TFrom>(bool forceReenter = false) where TFrom : class, TStateNode
            => Track(_fsm.BindDynamic<TFrom>(forceReenter));

        /// <summary>(任意->动态目标)根据外部条件动态决定目标状态 (返回 null 代表放弃切换) 可省略.Apply()</summary>
        public DynamicTransitionBuilder<TStateNode> BindDynamicAny(bool forceReenter = false)
            => Track(_fsm.BindDynamicAny(forceReenter));

        /// <summary>按接口/基类批量绑定动态目标 (返回 null 代表放弃切换) 可省略.Apply()</summary>
        public DynamicTransitionBuilder<TGroup> BindDynamicGroup<TGroup>(bool forceReenter = false)
            where TGroup : class
            => Track(_fsm.BindDynamicGroup<TGroup>(forceReenter));

        /// <summary>多对一绑定 (2个来源)  可省略.Apply()</summary>
        public TransitionBuilder<TStateNode> BindManyToOne<TFrom1, TFrom2, TTo>(bool forceReenter = false)
            where TFrom1 : class, TStateNode where TFrom2 : class, TStateNode where TTo : class, TStateNode
            => Track(_fsm.BindManyToOne<TFrom1, TFrom2, TTo>(forceReenter));

        /// <summary>多对一绑定 (3个来源) 可省略.Apply()</summary>
        public TransitionBuilder<TStateNode> BindManyToOne<TFrom1, TFrom2, TFrom3, TTo>(bool forceReenter = false)
            where TFrom1 : class, TStateNode where TFrom2 : class, TStateNode where TFrom3 : class, TStateNode where TTo : class, TStateNode
            => Track(_fsm.BindManyToOne<TFrom1, TFrom2, TFrom3, TTo>(forceReenter));

        /// <summary>多对一绑定 (不定长，需传Type数组) (不支持重入参数) 可省略.Apply()</summary>
        public TransitionBuilder<TStateNode> BindManyToOne<TTo>(params Type[] fromStates) where TTo : class, TStateNode
            => Track(_fsm.BindManyToOne<TTo>(fromStates));

        /// <summary>按接口/基类批量绑定到目标 可省略.Apply()</summary>
        public TransitionBuilder<TGroup> BindGroupToOne<TGroup, TTo>(bool forceReenter = false)
            where TGroup : class where TTo : class, TStateNode
            => Track(_fsm.BindGroupToOne<TGroup, TTo>(forceReenter));

        /// <summary>绑定一组共享相同规则的序列状态，如: 连招序列 Attack1->2->3，支持动态目标。 可省略.Apply()</summary>
        public SequenceTransitionBuilder<TStateNode> BindSequence<TFirst>(bool forceReenter = false)
            where TFirst : class, TStateNode
            => Track(_fsm.BindSequence<TFirst>(forceReenter));

        /// <summary>绑定自动回退到上一个状态的规则 (回退后执行OnResume，可配置) 可省略.Apply()</summary>
        public TransitionBuilder<TFrom> BindFallback<TFrom>(bool ignoreTransitionRule = true) where TFrom : class, TStateNode
            => Track(_fsm.BindFallback<TFrom>(ignoreTransitionRule));

        /// <summary>按接口/基类批量绑定自动回退规则，并在回退时智能跳过该组内的所有历史记录 (回退后执行OnResume，可配置) 可省略.Apply()</summary>
        public TransitionBuilder<TGroup> BindGroupFallback<TGroup>(bool ignoreTransitionRule = true) where TGroup : class
            => Track(_fsm.BindGroupFallback<TGroup>(ignoreTransitionRule));

        /// <summary>(任意状态->回退) 无论当前处于什么状态均可自动回退，可省略 .Apply()</summary>
        public TransitionBuilder<TStateNode> BindFallbackAny(bool ignoreTransitionRule = true)
            => Track(_fsm.BindFallbackAny(ignoreTransitionRule));

        /// <summary>全局拦截指定规则的迁移（From->To）可省略.Apply()</summary>
        public GuardBuilder<TFrom> Guard<TFrom, TTo>() where TFrom : class, TStateNode where TTo : class, TStateNode
            => Track(_fsm.Guard<TFrom, TTo>());

        /// <summary>(任意->目标)全局拦截指定规则的迁移 可省略.Apply()</summary>
        public GuardBuilder<TStateNode> GuardAny<TTo>() where TTo : class, TStateNode
            => Track(_fsm.GuardAny<TTo>());

        /// <summary>(任意->按接口/基类批量目标) 全局拦截进入该组状态的迁移 可省略.Apply()</summary>
        public GuardBuilder<TStateNode> GuardToGroup<TGroup>() where TGroup : class
            => Track(_fsm.GuardToGroup<TGroup>());

        /// <summary>(指定目标->任意) 全局拦截离开该状态的迁移 可省略.Apply()</summary>
        public GuardBuilder<TFrom> GuardFrom<TFrom>() where TFrom : class, TStateNode
            => Track(_fsm.GuardFrom<TFrom>());

        /// <summary>按接口/基类批量拦截 可省略.Apply()</summary>
        public GuardBuilder<TGroup> GuardFromGroup<TGroup>() where TGroup : class
            => Track(_fsm.GuardFromGroup<TGroup>());

    }
    #endregion
    //!========================
    #region SharedVarManager
    /// <summary>
    /// 状态机变量总管理器：统一封装本地物理存储(_localSharedVariables)、大盘数据同步与0-GC响应式代理
    /// </summary>
    internal class StateVariableManager : IVariableRegistry, ISharedVarPublisher, IDisposable
    {
        private IExternalStateMachine _machine;
        private Func<IRoutableMachine> _parentMachineGetter;

        // 1. 底层真正的物理存储
        private Dictionary<Type, object> _localSharedVariables;
        // 从 StateMachine 移过来的引用更新委托列表
        private List<Action> _referenceBinders;

        // 2. 0-GC 响应式代理缓存池
        private readonly Dictionary<Type, object> _liveVarCache = new();
        // private readonly Dictionary<(Type VarType, Delegate Selector, bool Distinct), object> _projectedVarCache = new();

        public StateVariableManager(IExternalStateMachine machine, Func<IRoutableMachine> parentMachineGetter)
        {
            _machine = machine;
            _parentMachineGetter = parentMachineGetter;
        }
        // =========================================================================
        // 引用重绑管理器
        // =========================================================================
        public void BindReference<TVar>(Action<TVar> binder) where TVar : class, ISharedVar
        {
            if (binder == null) return;

            // 1. 立即注入当前最新大盘引用
            binder.Invoke(GetRawSharedVariable<TVar>());

            // 2. 注册进重绑列表（发生 Join/Leave 大盘切换时自动更新）
            _referenceBinders ??= new List<Action>();
            _referenceBinders.Add(() =>
            {
                binder.Invoke(GetRawSharedVariable<TVar>());
            });
        }

        /// <summary>
        /// 供 StateMachine.RebindSharedVariables 触发大盘切换时调用
        /// </summary>
        public void NotifyReferenceBinders()
        {
            if (_referenceBinders == null) return;
            for (int i = 0; i < _referenceBinders.Count; i++)
            {
                _referenceBinders[i].Invoke();
            }
        }

        // =========================================================================
        // 底层数据存储与大盘解析
        // =========================================================================
        public object GetOrAddSharedVarBoxed(Type exactType)
        {
            var parent = _parentMachineGetter?.Invoke();
            if (parent != null)
            {
                return parent.GetOrAddSharedVarBoxed(exactType);
            }

            return GetOrAddLocalSharedVarBoxed(exactType);
        }

        private TVar GetRawSharedVariable<TVar>() where TVar : class, ISharedVar
        {
            return (TVar)GetOrAddSharedVarBoxed(typeof(TVar));
        }

        public object GetOrAddLocalSharedVarBoxed(Type exactType)
        {
            _localSharedVariables ??= new Dictionary<Type, object>();
            if (!_localSharedVariables.TryGetValue(exactType, out var variable))
            {
                using (SharedVarSecurityGuard.OpenScope())
                {
                    variable = Activator.CreateInstance(exactType);
                }

                var internalVar = (ISharedVarInternal)variable;
                internalVar.InitReceiver(this);

                _localSharedVariables[exactType] = variable;
            }
            return variable;
        }

        void ISharedVarPublisher.PublishSharedVar(ISharedVar variable, Type exactType)
        {
            SharedVarDispatcherCache.Dispatch(_machine, exactType, variable);
        }
        // private Action<object> CreatePublishAction(Type exactType)
        // {
        //     var method = typeof(StateVariableManager)
        //         .GetMethod(nameof(TypedDispatch), BindingFlags.NonPublic | BindingFlags.Instance)
        //         .MakeGenericMethod(exactType);
        //     return (Action<object>)Delegate.CreateDelegate(typeof(Action<object>), this, method);
        // }

        // private void TypedDispatch<TVar>(object obj) where TVar : class
        // {
        //     _machine?.Dispatch((TVar)obj);
        // }

        // =========================================================================
        // 路由大盘同步（修复了集合修改异常 Bug）
        // =========================================================================
        public void SyncFromParent(IRoutableMachine parent)
        {
            if (_localSharedVariables == null || parent == null) return;

            // 快照 Keys，消除派发事件时的 Collection Modified 隐患
            var keys = _localSharedVariables.Keys.ToArray();
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                var localVar = (ISharedVarInternal)_localSharedVariables[key];
                var globalVar = (ISharedVarInternal)parent.GetOrAddSharedVarBoxed(key);

                localVar.CopyFrom(globalVar);
            }
        }

        public void PushToParent(IRoutableMachine parent, IEnumerable<Type> typesToPush)
        {
            if (parent == null || typesToPush == null) return;

            foreach (var varType in typesToPush)
            {
                var localVar = (ISharedVarInternal)GetOrAddLocalSharedVarBoxed(varType);
                var globalVar = (ISharedVarInternal)parent.GetOrAddSharedVarBoxed(varType);

                // 一行代码：0 运行时反射、0 装箱、100% IL2CPP AOT 安全
                globalVar.CopyFrom(localVar);
            }
        }

        public IEnumerable<Type> GetLocalVariableTypes() => _localSharedVariables?.Keys ?? Enumerable.Empty<Type>();

        // =========================================================================
        // 对外门面 API (IVariableRegistry)
        // =========================================================================
        public ISharedVar<TValue> Get<TVar, TValue>() where TVar : SharedVar<TValue>
            => GetOrCreateLiveVar<TVar, TValue>();

        IReadOnlySharedVar<TValue> IReadOnlyVariableRegistry.Get<TVar, TValue>()
            => GetOrCreateLiveVar<TVar, TValue>();

        public TVar GetRaw<TVar>() where TVar : class, ISharedVar
            => GetRawSharedVariable<TVar>();

        private LiveSharedVar<TVar, TValue> GetOrCreateLiveVar<TVar, TValue>() where TVar : SharedVar<TValue>
        {
            Type key = typeof(TVar);
            if (!_liveVarCache.TryGetValue(key, out var proxy))
            {
                proxy = new LiveSharedVar<TVar, TValue>(_machine);
                _liveVarCache[key] = proxy;
            }
            return (LiveSharedVar<TVar, TValue>)proxy;
        }

        public TValue Read<TVar, TValue>() where TVar : SharedVar<TValue>
        {
            var sv = GetRawSharedVariable<TVar>();
            return sv != null ? ((SharedVar<TValue>)sv).Value : default;
        }

        public TValue ReadBase<TVar, TValue>() where TVar : SharedVar<TValue>
        {
            var sv = GetRawSharedVariable<TVar>();
            return sv != null ? ((SharedVar<TValue>)sv).BaseValue : default;
        }

        public void Set<TVar, TValue>(TValue value) where TVar : SharedVar<TValue>
        {
            var sv = GetRawSharedVariable<TVar>();
            if (sv is SharedVar<TValue> typedSv)
            {
                typedSv.Value = value;
            }
        }

        public void Dispose()
        {
            _liveVarCache.Clear();

            if (_localSharedVariables != null)
            {
                foreach (var sv in _localSharedVariables.Values)
                {
                    if (sv is IDisposable internalVar)
                    {
                        internalVar.Dispose();
                    }
                }
                _localSharedVariables.Clear();
                _localSharedVariables = null;
            }

            _referenceBinders?.Clear();
            _referenceBinders = null;

            _machine = null;
            _parentMachineGetter = null;
        }

    }
    #endregion

    #region SharedVar
    /// <summary>
    /// 强类型共享变量的非泛型统一标识接口（用于对外提供泛型约束与类型识别）
    /// </summary>
    public interface ISharedVar { }

    /// <summary>
    /// 强类型共享变量的读写接口（兼容单实例与 Live 动态代理）
    /// </summary>
    public interface ISharedVar<T> : IReadOnlySharedVar<T>
    {
        bool IsDisposed { get; }
        /// <summary>当前值（支持读写）</summary>
        new T Value { get; set; }
        /// <summary>修改值并强制广播通知</summary>
        void SetValueAndForceNotify(T value);
        /// <summary>修改值且不触发广播通知</summary>
        void SetValueWithoutNotify(T value);
        /// <summary>设置基准参照值（默认不通知）</summary>
        void SetBaseValue(T baseValue, bool notify = false);
        /// <summary>设定基准参照值并同步到当前值（默认通知）</summary>
        void SetBaseValueAndReset(T baseValue, bool notify = true);
        /// <summary>将当前值重置回初始基准值</summary>
        void ResetToBaseValue(bool notify = true);
        /// <summary>
        /// 将当前值重置为 C# 类型默认值 default(T) (数值为0, 引用类型为null)
        /// 备注: 不会改变 BaseValue 基准值
        /// </summary>
        void ResetToDefault(bool notify = true);
        /// <summary>在不改变当前值的情况下，强制向总线和监听者重新广播通知一次</summary>
        void ForceNotify();
    }

    /// <summary>
    /// 强类型共享变量的只读视图接口，仅提供读取与订阅能力
    /// </summary>
    public interface IReadOnlySharedVar<out T>
    {
        /// <summary>当前值（只读）</summary>
        T Value { get; }

        /// <summary>基准初始值</summary>
        T BaseValue { get; }

        /// <summary> 订阅最新值 </summary>
        IDisposable Subscribe(Action<T> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default);
    }

    /// <summary>
    /// 共享变量宿主发布接口（用于替代 Action<object> 委托，实现零委托分配与双派发）
    /// </summary>
    internal interface ISharedVarPublisher
    {
        void PublishSharedVar(ISharedVar variable, Type exactType);
    }

    /// <summary>
    /// 内部通知接口 (供底层通讯)
    /// </summary>
    internal interface ISharedVarInternal
    {
        /// <summary>注入事件总线发布器</summary>
        void InitReceiver(ISharedVarPublisher publisher);

        /// <summary>仅供 [Payload] 反射注入未知类型的 object 数据</summary>
        void SetValueBoxed(object value);

        /// <summary>底层 0-GC 实例间直接自拷贝</summary>
        void CopyFrom(ISharedVarInternal source);

        void ResetToBaseValue(bool notify = true);
    }

    // 接收者接口 (FSM 或 Router)
    internal interface ISharedVariableRegistry
    {
        internal object GetOrAddSharedVarBoxed(Type exactType);
    }

    /// <summary>
    /// 通用响应式变量基类，支持基准值 (BaseValue) 锁定与一键重置，支持订阅与操作符扩展。
    /// </summary>
    public class ReactiveVar<T> : ISharedVar<T>, IDisposable
    {
        protected T _baseValue;
        protected T _value;
        protected bool _isDisposed;

        private Action<T> _onValueChangedListener;
        private List<CancellationTokenRegistration> _tokenRegistrations;

        /// <summary>值发生改变时的本地回调 (旧值, 新值)</summary>
        [Obsolete("请改用 .Subscribe() 进行更安全的订阅")]
        public event Action<T, T> OnValueChanged;

        public bool IsDisposed => _isDisposed;
        public T BaseValue => _baseValue;

        public T Value
        {
            get => _isDisposed ? default : _value;
            set
            {
                if (_isDisposed) return;
                if (!ZeroGcEqualityComparer<T>.EqualsFunc(_value, value))
                {
                    var old = _value;
                    _value = value;
                    NotifyValueChanged(old, _value);
                }
            }
        }

        /// <summary>无参构造：初始值均为 default(T)</summary>
        public ReactiveVar() { }

        /// <summary>带初始值的构造：立即锁定初始基准值并同步当前值</summary>
        public ReactiveVar(T baseValue)
        {
            _baseValue = baseValue;
            _value = baseValue;
        }

        protected virtual void NotifyValueChanged(T oldValue, T newValue)
        {
            OnValueChanged?.Invoke(oldValue, newValue);
            _onValueChangedListener?.Invoke(newValue);
        }

        public IDisposable Subscribe(Action<T> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null || _isDisposed || ct.IsCancellationRequested)
                return new StateActionDisposable(null);

            _onValueChangedListener += onValueChanged;

            CancellationTokenRegistration reg = default;
            bool hasToken = ct.CanBeCanceled;

            if (hasToken)
            {
                reg = ct.Register(() => _onValueChangedListener -= onValueChanged);
                _tokenRegistrations ??= new List<CancellationTokenRegistration>();
                _tokenRegistrations.Add(reg);
            }

            if (notifyImmediately) onValueChanged.Invoke(_value);

            return new StateActionDisposable(() =>
            {
                if (hasToken)
                {
                    reg.Dispose();
                    _tokenRegistrations?.Remove(reg);
                }
                _onValueChangedListener -= onValueChanged;
            });
        }
        public void SetValueAndForceNotify(T value)
        {
            if (_isDisposed) return;
            var old = _value;
            _value = value;
            NotifyValueChanged(old, _value);
        }
        public void SetValueWithoutNotify(T value)
        {
            if (_isDisposed) return;
            _value = value;
        }

        public void SetBaseValue(T baseValue, bool notify = false)
        {
            if (_isDisposed) return;
            _baseValue = baseValue;
            if (notify) ForceNotify();
        }

        public void SetBaseValueAndReset(T baseValue, bool notify = true)
        {
            if (_isDisposed) return;
            var old = _value;
            _baseValue = baseValue;
            _value = baseValue;
            if (notify) NotifyValueChanged(old, _value);
        }

        public void ResetToBaseValue(bool notify = true)
        {
            if (_isDisposed) return;
            if (notify) Value = _baseValue;
            else _value = _baseValue;
        }

        public void ResetToDefault(bool notify = true)
        {
            if (_isDisposed) return;
            if (notify) Value = default;
            else _value = default;
        }

        public void ForceNotify()
        {
            if (_isDisposed) return;
            NotifyValueChanged(_value, _value);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            if (_tokenRegistrations != null)
            {
                for (int i = 0; i < _tokenRegistrations.Count; i++) _tokenRegistrations[i].Dispose();
                _tokenRegistrations.Clear();
                _tokenRegistrations = null;
            }
            _onValueChangedListener = null;
            OnValueChanged = null;
            _value = default;
            _baseValue = default;
            _isDisposed = true;
        }

        public static implicit operator T(ReactiveVar<T> rv) => rv != null ? rv.Value : default;
    }

    /// <summary>
    /// 状态机共享变量抽象基类。必须派生出具体的类 (如 public class PlayerHp : SharedVar<int> {})
    /// 具备 ReactiveVar 全部响应式能力，并在数据变动时自动触发状态机/Router 内部总线事件广播。
    /// </summary>
    public abstract class SharedVar<T> : ReactiveVar<T>, ISharedVar, ISharedVarInternal, IDisposable
    {
        private ISharedVarPublisher _publisher;

        // 拦截非法外部 new SharedVar，不影响外部 new ReactiveVar
        protected SharedVar()
        {
            if (!SharedVarSecurityGuard.IsInstantiationAllowed)
                throw new InvalidOperationException($"禁止外部直接 new 实例化状态机共享变量 [{GetType().Name}]！请改用：new ReactiveVar<{typeof(T).Name}>()");
        }

        // 触发本地监听的同时，向状态机事件总线广播自身
        protected override void NotifyValueChanged(T oldValue, T newValue)
        {
            base.NotifyValueChanged(oldValue, newValue);
            _publisher?.PublishSharedVar(this, this.GetType());
        }

        // =========================================================================
        // 状态机内部接口 (ISharedVarInternal)
        // =========================================================================
        void ISharedVarInternal.InitReceiver(ISharedVarPublisher publisher)
        {
            if (_isDisposed) return;
            _publisher = publisher;
        }

        void ISharedVarInternal.CopyFrom(ISharedVarInternal source)
        {
            if (_isDisposed) return;
            if (source is SharedVar<T> typedSource)
            {
                this._baseValue = typedSource._baseValue;
                this._value = typedSource._value;
            }
        }

        void ISharedVarInternal.SetValueBoxed(object value) => Value = (T)value;

        [Obsolete("SharedVar 的生命周期由状态机统一管理，禁止外部手动调用 Dispose()！", true)]
        public new void Dispose()
        {
            _publisher = null;
            base.Dispose();
        }
    }

    /// <summary>
    /// 共享变量实例化内部安全守卫（对外隐藏，外部类无法看到或调用）
    /// </summary>
    internal static class SharedVarSecurityGuard
    {
        [ThreadStatic]
        private static int _instantiatingDepth;

        internal static bool IsInstantiationAllowed => _instantiatingDepth > 0;

        internal static Scope OpenScope()
        {
            _instantiatingDepth++;
            return default;
        }

        internal readonly ref struct Scope
        {
            public void Dispose() => _instantiatingDepth--;
        }
    }

    /// <summary>
    /// 共享变量高速派发调度器（全局单例缓存，消除运行时反射与装箱）
    /// </summary>
    internal static class SharedVarDispatcherCache
    {
        // 定义内部非泛型接口，用于抹平类型差异
        private interface IInvoker
        {
            void Invoke(IExternalStateMachine machine, object variable);
        }

        // AOT 泛型执行器：编译期直接生成原生 C++ 代码，完全不需要 JIT
        private sealed class TypedInvoker<TVar> : IInvoker where TVar : class
        {
            public void Invoke(IExternalStateMachine machine, object variable)
            {
                // 纯静态强类型调用，IL2CPP 会将其完全编译为原生指令，速度最快
                machine.Dispatch((TVar)variable);
            }
        }

        // 全局缓存：每个 Type 对应的 Invoker 单例
        private static readonly ConcurrentDictionary<Type, IInvoker> _invokers = new();

        public static void Dispatch(IExternalStateMachine machine, Type exactType, object variable)
        {
            if (machine == null || variable == null) return;

            // 全局每个类型只创建一次单例，后续均为纯粹的 C++ 虚函数调用
            var invoker = _invokers.GetOrAdd(exactType, static type =>
            {
                Type invokerType = typeof(TypedInvoker<>).MakeGenericType(type);
                return (IInvoker)Activator.CreateInstance(invokerType);
            });

            invoker.Invoke(machine, variable);
        }
    }
    #endregion

    //!============= [状态机路由器] =============
    #region Router
    // todo 记录历史
    public struct RouteLog
    {
        public string SenderName;
        public string EventType;
        public string ReceiverName;
        public float Time;
    }

    /// <summary>
    /// 加入父级路由器的配置请求参数包
    /// </summary>
    internal struct RouterJoinRequest
    {
        public IRoutableMachine ParentMachine;
        public int BasePriority;
        public CancellationToken CancellationToken;
        public bool AutoLeaveSyncFromParent;
        // 推送本地共享变量
        public bool PushAllSharedVars;
        public HashSet<Type> PushSharedVars;
        // 拦截冒泡事件并CrossTalk
        public bool CrossTalkAll;
        public List<Type> CrossTalkEvents;
        // Dispatch 优先级与忽略配置
        public Dictionary<Type, int> EventPriorities;
    }
    /// <summary>
    /// 用于给路由器配置器注入路由规则的接口 (对外隐藏)
    /// </summary>
    internal interface IRouterRuleReceiver
    {
        void ExecuteJoinParent(RouterJoinRequest request);
    }

    /// <summary>
    /// 状态机内部持有的路由分发模块 (管理所有挂载在自己下面的子状态机)
    /// </summary>
    internal class InternalRouterModule
    {
        private StateMachineLogger<StateMachineLogLevel> _debug;

        public bool HasChildren => _fsmList.Count > 0;
        public int ChildCount => _fsmList.Count;
        private List<IRoutableMachine> _fsmList = new();

        // 路由缓存机制
        private Dictionary<Type, List<IRoutableMachine>> _routeCache = new();
        private List<(IRoutableMachine fsm, int effectivePriority)> _tempSortList = new();
        private Stack<List<IRoutableMachine>> _listPool = new();

        private int _crossTalkDepth = 0;
        private const int MAX_CROSSTALK_DEPTH = 10;

        public InternalRouterModule(StateMachineLogger<StateMachineLogLevel> logger)
        {
            _debug = logger;
        }

        public void RegisterChild(IRoutableMachine child)
        {
            if (!_fsmList.Contains(child))
            {
                _fsmList.Add(child);
                InvalidateCache();
                _debug.LogVerbose($"已挂载子状态机: <color=green>[{child.MachineId}]</color>");
            }

        }

        public void UnregisterChild(IRoutableMachine child)
        {
            if (_fsmList.Remove(child))
            {
                InvalidateCache();
                _debug.LogVerbose($"已卸载子状态机: <color=yellow>[{child.MachineId}]</color>");
            }
        }

        public void InvalidateCache()
        {
            foreach (var list in _routeCache.Values)
            {
                list.Clear();
                _listPool.Push(list);
            }
            _routeCache.Clear();
        }

        // 【向下分发】将指令下发给所有子状态机
        public bool RouteDispatchEventToChildren<TPayload>(TPayload payload, IRoutableMachine excludeFsm = null)
        {

            if (_crossTalkDepth > MAX_CROSSTALK_DEPTH)
                throw new Exception("[Router] 路由深度超限，检测到死循环已被拦截！");

            _crossTalkDepth++;
            try
            {
                Type evtType = typeof(TPayload);
                if (!_routeCache.TryGetValue(evtType, out var targets))
                {
                    targets = _listPool.Count > 0 ? _listPool.Pop() : new List<IRoutableMachine>(_fsmList.Count);
                    _tempSortList.Clear();

                    for (int i = 0; i < _fsmList.Count; i++)
                    {
                        var f = _fsmList[i];
                        int priority = f.GetDispatchEventPriority(evtType);
                        if (priority != (int.MinValue + 1)) // 不是 Ignore
                        {
                            int effectivePriority = priority != int.MinValue ? priority : f.BasePriority;
                            _tempSortList.Add((f, effectivePriority));
                        }
                    }

                    _tempSortList.Sort((a, b) => b.effectivePriority.CompareTo(a.effectivePriority));
                    for (int i = 0; i < _tempSortList.Count; i++) targets.Add(_tempSortList[i].fsm);

                    _tempSortList.Clear();
                    _routeCache[evtType] = targets;
                }

                for (int i = 0; i < targets.Count; i++)
                {
                    var childFsm = targets[i];
                    if (childFsm == excludeFsm) continue;
                    // 向下调用 Dispatch，这会触发子 FSM 的本地逻辑，如果没消化完还会继续往下传
                    if (childFsm.Dispatch(payload)) return true;
                }
                return false;
            }
            finally
            {
                _crossTalkDepth--;
            }
        }
        internal void RebindChildrenSharedVariables()
        {
            // 向下级联通知所有挂载的子状态机重新绑定大盘
            for (int i = 0; i < _fsmList.Count; i++)
            {
                _fsmList[i].RebindSharedVariables();
            }
        }
        public void Dispose()
        {
            // 主动踢出并解绑所有子节点
            var children = _fsmList.ToArray();
            foreach (var child in children)
            {
                if (child.IsDisposed) continue;
                if (child is IRouterNode routerNode) routerNode.LeaveParent(syncFromParent: true);
            }

            _fsmList.Clear();
            _routeCache.Clear();

            _debug = null;
        }
    }

    /// <summary>
    /// 状态机端路由规则构建器 
    /// </summary>
    public class RouterConnectionConfigurator
    {
        private readonly IRouterRuleReceiver _ruleReceiver;
        private readonly IRoutableMachine _parentMachine;

        private int _basePriority = 0;
        private CancellationToken _ct = default;
        private bool _autoLeaveSyncData = true;

        private bool _pushAllSharedVars;
        private HashSet<Type> _pushSharedVars;

        private bool _crossTalkAll;
        private List<Type> _crossTalkEvents;

        private Dictionary<Type, int> _eventPriorities;

        private bool _isApplied = false;

        internal RouterConnectionConfigurator(IRouterRuleReceiver ruleReceiver, IRoutableMachine parentMachine)
        {
            _ruleReceiver = ruleReceiver;
            _parentMachine = parentMachine;
        }

        /// <summary>
        /// 设置当前子节点的基础优先级（默认 0，数值越大越先收到父级下发的事件）。
        /// </summary>
        public RouterConnectionConfigurator WithPriority(int basePriority)
        {
            _basePriority = basePriority;
            return this;
        }

        /// <summary>
        /// 绑定生命周期令牌。当令牌触发取消时，自动从父级断开离开。
        /// </summary>
        /// <param name="ct">生命周期令牌</param>
        /// <param name="syncFromParent">脱离瞬间是否将父级大盘的最新数据同步降级到本地？(默认 true)</param>
        public RouterConnectionConfigurator AutoLeaveWith(CancellationToken ct, bool syncFromParent = true)
        {
            _ct = ct;
            _autoLeaveSyncData = syncFromParent;
            return this;
        }

        /// <summary>
        /// 显式指定接收事件的优先级，越高越优先。可使用接口按组指定 (使用 DispatchPriorityEvaluator 评估)
        /// 备注: 搭配 .Consume() 或 AutoHandle 可阻断事件传播
        /// </summary>
        public RouterConnectionConfigurator Receive<TPayload>(int priority = 0)
        {
            if (priority <= int.MinValue + 1)
                throw new ArgumentOutOfRangeException(nameof(priority), "优先级过低，该数值区间被框架保留。");

            _eventPriorities ??= new Dictionary<Type, int>();
            _eventPriorities[typeof(TPayload)] = priority;
            return this;
        }

        /// <summary>
        /// 该节点无视指定事件。可使用接口按组指定 (使用 DispatchPriorityEvaluator 评估)
        /// </summary>
        public RouterConnectionConfigurator Ignore<TPayload>()
        {
            _eventPriorities ??= new Dictionary<Type, int>();
            _eventPriorities[typeof(TPayload)] = int.MinValue + 1; // 内部保留标记：Ignore
            return this;
        }

        /// <summary>
        /// 监视自身或子节点向上冒泡的【所有】事件，并全部自动串话（横向广播给同级兄弟状态机）。
        /// </summary>
        public RouterConnectionConfigurator CrossTalkAllWhenBubbleUp(bool enable = true)
        {
            _crossTalkAll = enable;
            return this;
        }

        /// <summary>
        /// 监视自身或子节点向上冒泡的事件，并进行自动串话。可使用接口按组拦截 (使用 CrossTalkEvaluator 评估)
        /// </summary>
        public RouterConnectionConfigurator CrossTalkWhenBubbleUp<TEvent>()
        {
            _crossTalkEvents ??= new List<Type>();
            _crossTalkEvents.Add(typeof(TEvent));
            return this;
        }

        /// <summary>
        /// 加入父节点时推送本地 SharedVar 的当前值值以及初始值 (加入前需完成初始赋值)
        /// </summary>
        public RouterConnectionConfigurator PushLocalSharedVar<TVar>() where TVar : class, ISharedVar
        {
            _pushSharedVars ??= new HashSet<Type>();
            _pushSharedVars.Add(typeof(TVar));
            return this;
        }

        /// <summary>
        /// 加入父节点时推送所有本地 SharedVar 的当前值值以及初始值 (加入前需完成初始赋值)
        /// </summary>
        public RouterConnectionConfigurator PushAllLocalSharedVar()
        {
            _pushAllSharedVars = true;
            return this;
        }

        public void Apply()
        {
            if (_isApplied) return;
            _isApplied = true;

            var request = new RouterJoinRequest
            {
                ParentMachine = _parentMachine,
                BasePriority = _basePriority,
                CancellationToken = _ct,
                AutoLeaveSyncFromParent = _autoLeaveSyncData,
                PushAllSharedVars = _pushAllSharedVars,
                PushSharedVars = _pushSharedVars,
                CrossTalkAll = _crossTalkAll,
                CrossTalkEvents = _crossTalkEvents,
                EventPriorities = _eventPriorities
            };

            _ruleReceiver.ExecuteJoinParent(request);
        }
    }

    #endregion

    //!=============[强类型事件分发]===================
    #region Event
    public class StateEventManager : IStateEventSubscriber
    {
        private static Type AnyStateKey => AssemblyTypeHolder.AnyStateKey;

        // =========================================================================
        // 1. 合并存储单元：一个结构体同时包含委托和它的生命周期令牌
        // =========================================================================
        private struct EventSubscription
        {
            public Delegate Handler;
            public CancellationTokenRegistration Registration;

            public EventSubscription(Delegate handler, CancellationTokenRegistration registration)
            {
                Handler = handler;
                Registration = registration;
            }
        }
        private struct LifecycleSubscription
        {
            public Action Handler;
            public CancellationTokenRegistration Registration;

            public LifecycleSubscription(Action handler, CancellationTokenRegistration registration)
            {
                Handler = handler;
                Registration = registration;
            }
        }
        private readonly List<LifecycleSubscription> _startSubscriptions = new();
        private readonly List<LifecycleSubscription> _stopSubscriptions = new();

        // 主事件存储表：(状态类型, 事件类型) -> 订阅列表
        private readonly Dictionary<(Type StateType, Type EventType), List<EventSubscription>> _customEventHandlers = new();

        // 状态进入/退出事件派发器
        private readonly Dictionary<Type, IStateLifecyclePublisher> _enterPublishers = new();
        private readonly Dictionary<Type, IStateLifecyclePublisher> _exitPublishers = new();

        public StateEventManager() { }

        // =========================================================================
        // 生命周期 (Enter/Exit) 派发器同样合并存储
        // =========================================================================
        private interface IStateLifecyclePublisher
        {
            void Invoke(IStateNode state);
            void Clear();
        }

        private class StateLifecyclePublisher<T> : IStateLifecyclePublisher where T : class, IStateNode
        {
            private struct Sub
            {
                public Action<T> Handler;
                public CancellationTokenRegistration Reg;
            }

            private readonly List<Sub> _subscribers = new();

            public void Add(Action<T> handler, CancellationToken ct)
            {
                if (handler == null) return;
                CancellationTokenRegistration reg = default;

                if (ct.CanBeCanceled)
                {
                    if (ct.IsCancellationRequested) return;
                    reg = ct.Register(() => Remove(handler));
                }

                _subscribers.Add(new Sub { Handler = handler, Reg = reg });
            }

            public void Remove(Action<T> handler)
            {
                for (int i = _subscribers.Count - 1; i >= 0; i--)
                {
                    if (_subscribers[i].Handler == handler)
                    {
                        _subscribers[i].Reg.Dispose();
                        _subscribers.RemoveAt(i);
                        break;
                    }
                }
            }

            public void Invoke(IStateNode state)
            {
                if (state is not T typedState) return;
                // 正序调用
                for (int i = 0; i < _subscribers.Count; i++)
                {
                    _subscribers[i].Handler?.Invoke(typedState);
                }
            }

            public void Clear()
            {
                for (int i = 0; i < _subscribers.Count; i++)
                {
                    _subscribers[i].Reg.Dispose();
                }
                _subscribers.Clear();
            }
        }

        private StateLifecyclePublisher<T> GetOrCreateEnterPublisher<T>() where T : class, IStateNode
        {
            if (!_enterPublishers.TryGetValue(typeof(T), out var pub))
            {
                pub = new StateLifecyclePublisher<T>();
                _enterPublishers[typeof(T)] = pub;
            }
            return (StateLifecyclePublisher<T>)pub;
        }

        private StateLifecyclePublisher<T> GetOrCreateExitPublisher<T>() where T : class, IStateNode
        {
            if (!_exitPublishers.TryGetValue(typeof(T), out var pub))
            {
                pub = new StateLifecyclePublisher<T>();
                _exitPublishers[typeof(T)] = pub;
            }
            return (StateLifecyclePublisher<T>)pub;
        }

        // ================== Start / Stop 接口实现 ==================

        public IDisposable SubscribeStart(Action onStart, CancellationToken ct = default)
        {
            return SubscribeLifecycleInternal(_startSubscriptions, onStart, ct, () => UnsubscribeStart(onStart));
        }

        public void UnsubscribeStart(Action onStart)
        {
            UnsubscribeLifecycleInternal(_startSubscriptions, onStart);
        }

        public IDisposable SubscribeStop(Action onStop, CancellationToken ct = default)
        {
            return SubscribeLifecycleInternal(_stopSubscriptions, onStop, ct, () => UnsubscribeStop(onStop));
        }

        public void UnsubscribeStop(Action onStop)
        {
            UnsubscribeLifecycleInternal(_stopSubscriptions, onStop);
        }

        private IDisposable SubscribeLifecycleInternal(
            List<LifecycleSubscription> list,
            Action handler,
            CancellationToken ct,
            Action unsubscribeAction)
        {
            if (handler == null) return new StateActionDisposable(null);
            if (ct.IsCancellationRequested) return new StateActionDisposable(null);

            CancellationTokenRegistration reg = default;
            if (ct.CanBeCanceled)
            {
                reg = ct.Register(unsubscribeAction);
            }

            list.Add(new LifecycleSubscription(handler, reg));
            return new StateActionDisposable(unsubscribeAction);
        }

        private void UnsubscribeLifecycleInternal(List<LifecycleSubscription> list, Action handler)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Handler == handler)
                {
                    list[i].Registration.Dispose();
                    list.RemoveAt(i);
                    break;
                }
            }
        }

        // ================== 外部 API 实现 ==================

        public IDisposable SubscribeEnter<TState>(Action<TState> onEnter, CancellationToken ct = default) where TState : class, IStateNode
        {
            GetOrCreateEnterPublisher<TState>().Add(onEnter, ct);
            return new StateActionDisposable(() => UnsubscribeEnter(onEnter));
        }

        public void UnsubscribeEnter<TState>(Action<TState> onEnter) where TState : class, IStateNode
        {
            if (_enterPublishers.TryGetValue(typeof(TState), out var pub))
                ((StateLifecyclePublisher<TState>)pub).Remove(onEnter);
        }

        public IDisposable SubscribeExit<TState>(Action<TState> onExit, CancellationToken ct = default) where TState : class, IStateNode
        {
            GetOrCreateExitPublisher<TState>().Add(onExit, ct);
            return new StateActionDisposable(() => UnsubscribeExit(onExit));
        }

        public void UnsubscribeExit<TState>(Action<TState> onExit) where TState : class, IStateNode
        {
            if (_exitPublishers.TryGetValue(typeof(TState), out var pub))
                ((StateLifecyclePublisher<TState>)pub).Remove(onExit);
        }

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler, CancellationToken ct = default)
        {
            return SubscribeEventInternal(AnyStateKey, handler, ct);
        }

        public void Unsubscribe<TEvent>(Action<TEvent> handler)
        {
            UnsubscribeEventInternal<TEvent>(AnyStateKey, handler);
        }

        public IDisposable SubscribeEventIn<TState, TEvent>(Action<TEvent> handler, CancellationToken ct = default) where TState : class, IStateNode
        {
            return SubscribeEventInternal(typeof(TState), handler, ct);
        }

        public void UnsubscribeEventIn<TState, TEvent>(Action<TEvent> handler) where TState : class, IStateNode
        {
            UnsubscribeEventInternal<TEvent>(typeof(TState), handler);
        }

        // ================== 核心内部通用逻辑 ==================

        private IDisposable SubscribeEventInternal<TEvent>(Type stateType, Action<TEvent> handler, CancellationToken ct)
        {
            if (handler == null) return new StateActionDisposable(null);

            // 如果 Token 已取消，直接退出
            if (ct.IsCancellationRequested) return new StateActionDisposable(null);

            var key = (stateType, typeof(TEvent));
            if (!_customEventHandlers.TryGetValue(key, out var list))
            {
                list = new List<EventSubscription>();
                _customEventHandlers[key] = list;
            }

            // 绑定 Token 自动取消回调
            CancellationTokenRegistration reg = default;
            if (ct.CanBeCanceled)
            {
                reg = ct.Register(() => UnsubscribeEventInternal<TEvent>(stateType, handler));
            }

            // 存入合并后的结构体
            list.Add(new EventSubscription(handler, reg));

            return new StateActionDisposable(() => UnsubscribeEventInternal<TEvent>(stateType, handler));
        }

        private void UnsubscribeEventInternal<TEvent>(Type stateType, Action<TEvent> handler)
        {
            var key = (stateType, typeof(TEvent));
            if (_customEventHandlers.TryGetValue(key, out var list))
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Handler == (Delegate)handler)
                    {
                        // 释放 Token 注册，避免内存泄漏
                        list[i].Registration.Dispose();
                        list.RemoveAt(i);
                        break;
                    }
                }

                if (list.Count == 0)
                {
                    _customEventHandlers.Remove(key);
                }
            }
        }

        // ================== 触发派发 ==================
        internal void InvokeStart()
        {
            // 倒序遍历，防止回调中 Unsubscribe 导致索引错位
            for (int i = _startSubscriptions.Count - 1; i >= 0; i--)
            {
                if (i < _startSubscriptions.Count)
                {
                    _startSubscriptions[i].Handler?.Invoke();
                }
            }
        }

        internal void InvokeStop()
        {
            for (int i = _stopSubscriptions.Count - 1; i >= 0; i--)
            {
                if (i < _stopSubscriptions.Count)
                {
                    _stopSubscriptions[i].Handler?.Invoke();
                }
            }
        }

        internal void InvokeEnter(Type stateType, IStateNode stateInstance)
        {
            if (stateType == null || stateInstance == null) return;
            if (_enterPublishers.TryGetValue(stateType, out var pub)) pub.Invoke(stateInstance);
        }

        internal void InvokeExit(Type stateType, IStateNode stateInstance)
        {
            if (stateType == null || stateInstance == null) return;
            if (_exitPublishers.TryGetValue(stateType, out var pub)) pub.Invoke(stateInstance);
        }

        internal void InvokeEvent<TEvent>(Type contextStateType, TEvent eventData)
        {
            Type eventType = typeof(TEvent);

            // 1. 全局监听 (AnyStateKey) 无论是否有当前状态
            TriggerHandlers((AnyStateKey, eventType), eventData);

            // 2. 特定状态监听 (仅当存在具体状态实例且不为 AnyStateKey 时触发)
            if (contextStateType != null && contextStateType != AnyStateKey)
            {
                TriggerHandlers((contextStateType, eventType), eventData);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TriggerHandlers<TEvent>((Type StateType, Type EventType) key, TEvent eventData)
        {
            if (_customEventHandlers.TryGetValue(key, out var list))
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    // 防止在同一帧前一个回调中把后一个移除了导致越界
                    if (i < list.Count && list[i].Handler is Action<TEvent> action)
                    {
                        action.Invoke(eventData);
                    }
                }
            }
        }

        internal void Clear()
        {
            foreach (var pub in _enterPublishers.Values) pub.Clear();
            _enterPublishers.Clear();

            foreach (var pub in _exitPublishers.Values) pub.Clear();
            _exitPublishers.Clear();

            foreach (var list in _customEventHandlers.Values)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    list[i].Registration.Dispose();
                }
                list.Clear();
            }
            _customEventHandlers.Clear();

            for (int i = 0; i < _startSubscriptions.Count; i++) _startSubscriptions[i].Registration.Dispose();
            _startSubscriptions.Clear();

            for (int i = 0; i < _stopSubscriptions.Count; i++) _stopSubscriptions[i].Registration.Dispose();
            _stopSubscriptions.Clear();
        }
    }
    #endregion
    //!========================
    #region DI
    /// <summary>供外部注入上下文的接口</summary>
    public interface IContextRegistry
    {
        public IDisposable RegisterContext<T>(T context, CancellationToken ct = default) where T : class;
    }

    /// <summary>
    /// 供内部反射注入使用的只读解析接口
    /// </summary>
    public interface IStateContextResolver
    {
        bool TryGetContext(Type type, out object context);
    }

    /// <summary>
    /// 状态机上下文/依赖注入管理器
    /// </summary>
    internal class StateContextManager : IDisposable
    {
        private struct ContextEntry
        {
            public object Target;
            public CancellationTokenRegistration Registration;

            public ContextEntry(object target, CancellationTokenRegistration registration)
            {
                Target = target;
                Registration = registration;
            }
        }

        private Dictionary<Type, ContextEntry> _contextMap = new();

        private bool _isDisposed;
        internal int ContextCount => _contextMap.Count;

        internal StateContextManager() { }

        internal void Register<T>(T context, CancellationToken ct = default) where T : class
        {
            if (_isDisposed) return;
            Type type = typeof(T);

            // 如果传入的 Token 已经取消，直接执行注销并退出
            if (ct != default && ct.IsCancellationRequested)
            {
                Unregister<T>();
                return;
            }

            // 1. 如果此前已注册过该类型，先安全释放旧的 Token 监听
            if (_contextMap.TryGetValue(type, out var oldEntry))
            {
                oldEntry.Registration.Dispose();
            }

            // 2. 绑定新的生命周期 Token
            CancellationTokenRegistration reg = default;
            if (ct != default && ct.CanBeCanceled)
            {
                reg = ct.Register(() => Unregister<T>());

                // 再次检查防并发/同帧取消
                if (ct.IsCancellationRequested)
                {
                    reg.Dispose();
                    _contextMap.Remove(type);
                    return;
                }
            }

            // 3. 存入单字典
            _contextMap[type] = new ContextEntry(context, reg);
        }

        internal void Unregister<T>()
        {
            if (_isDisposed) return;
            Type type = typeof(T);

            if (_contextMap.TryGetValue(type, out var entry))
            {
                entry.Registration.Dispose(); // 释放 Token 注册
                _contextMap.Remove(type);
            }
        }

        internal T Get<T>() where T : class
        {
            if (_isDisposed) return default;
            Type type = typeof(T);

            if (_contextMap.TryGetValue(type, out var entry))
            {
                return (T)entry.Target;
            }

            return default;
        }

        // 供反射注入 (StateReflectionProcessor) 调用
        internal bool TryGetContext(Type type, out object context)
        {
            if (_isDisposed)
            {
                context = null;
                return false;
            }

            if (_contextMap.TryGetValue(type, out var entry))
            {
                context = entry.Target;
                return true;
            }

            context = null;
            return false;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_contextMap != null)
            {
                foreach (var entry in _contextMap.Values)
                {
                    entry.Registration.Dispose();
                }
                _contextMap.Clear();
                _contextMap = null;
            }
        }
    }
    #endregion
    //!===============[反射处理器]=================
    #region Reflection
    /// <summary>
    /// 状态机反射处理器：专用于处理 Attribute 扫描、缓存与反射注入，剥离主状态机的复杂度
    /// </summary>
    internal class StateReflectionProcessor<TStateNode> where TStateNode : class, IStateNode
    {
        private string StopNamespace => AssemblyTypeHolder.StopNamespace;

        private StateMachineLogger<StateMachineLogLevel> _debug;
        // 缓存：状态类型 -> 该状态下挂载了 [AutoReset] 标记的字段
        private Dictionary<Type, List<FieldResetData>> _fieldResetCache;
        // 缓存：状态类型 -> 该状态下挂载了 [AutoInject] 的字段列表
        private Dictionary<Type, List<FieldInfo>> _injectFieldCache = new();
        // 缓存：状态类型 -> 该状态下挂载了 [Payload] 的字段列表
        private Dictionary<Type, List<PayloadFieldData>> _payloadFieldCache = new();
        // 缓存：状态类型 -> 该状态下挂载了 [AutoLink] 的字段列表
        private Dictionary<Type, List<LinkFieldData>> _linkFieldCache = new();
        // 缓存：状态类型 -> Tuple(事件Type, 触发时机) -> 委托
        private Dictionary<Type, Dictionary<(Type, HandleTiming), Delegate>> _autoHandleCache = new();
        // 缓存：状态类型 -> [AutoBind] 的字段
        private Dictionary<Type, Action<TStateNode, object>> _autoBindCache = new();
        // 缓存：状态类型 -> (时机 -> 按Order排序的委托列表)
        private Dictionary<Type, Dictionary<Timing, Action<TStateNode>>> _autoRunCache = new();
        // 缓存：状态类型 -> 字段列表
        private Dictionary<Type, List<SharedValueFieldData>> _sharedValueCache = new();

        // 缓存：状态类型 -> 预编译好的强类型 SharedVar 注入委托 （废弃）
        // private Dictionary<Type, Action<TStateNode, ISharedVariableRegistry>> _sharedVarInjectorCache = new();

        // 状态机的隐藏黑板（快照池），用于存储上一任留下的数据
        private Dictionary<string, object> _linkBlackboard = new();
        // 用于记录在当前切换流程中，被 AutoLink 成功恢复数据的字段 (复用以避免GC)
        private HashSet<FieldInfo> _successfullyLinkedFields = new HashSet<FieldInfo>();

        // 缓存 typeof(ISharedVarInternal)，避免重复取 Type
        private static readonly Type _sharedVarInterfaceType = typeof(ISharedVarInternal);

        internal StateReflectionProcessor(StateMachineLogger<StateMachineLogLevel> logger)
        {
            _debug = logger;
        }
        //!--- [数据容器] ---
        private class FieldResetData
        {
            public FieldInfo Field;
            public object InitialValue;
            public bool IsSharedVar;
        }
        private class PayloadFieldData
        {
            public FieldInfo Field;
            public Type DataType; // 字段原本的类型
            public object DefaultValue;
            public bool IsSharedVar;
            public Type ExpectedPayloadType; // 期望接收的真实数据类型 (T)
        }
        private class LinkFieldData
        {
            public FieldInfo Field;
            public string Key;
            public Type DataType;
            public bool CanAcceptNull;
        }
        private class SharedValueFieldData
        {
            public FieldInfo Field;
            public Type ExactType; // 强类型本身 (如 PlayerHp)
        }
        //! --- [SharedValue] ---
        /// <summary>
        /// 扫描并缓存状态类中所有派生自 SharedVar 的字段
        /// </summary>
        internal void CacheSharedValueFields(TStateNode state)
        {
            Type stateType = state.GetType();
            if (_sharedValueCache.ContainsKey(stateType)) return;

            var svFields = new List<SharedValueFieldData>();
            Type currentType = stateType;
            string stopNamespace = StopNamespace;

            while (currentType != null && currentType != typeof(object))
            {
                if (currentType.Namespace == stopNamespace) break;

                FieldInfo[] fields = currentType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                foreach (var field in fields)
                {
                    Type fieldType = field.FieldType;

                    // 过滤值类型
                    if (fieldType.IsValueType) continue;

                    // 排除 ReactiveVar<T> 等未封闭的具体泛型类
                    if (fieldType.IsGenericType) continue;

                    // 通过非泛型内部接口进行 O(1) 原生判定，取代低效的递归反射
                    if (!_sharedVarInterfaceType.IsAssignableFrom(fieldType)) continue;

                    // 抽象类拦截
                    if (fieldType.IsAbstract)
                    {
                        _debug.LogErr($"[SharedVar] 字段 {field.Name} 的类型 {fieldType.Name} 是抽象的！必须使用具体的派生类 (如 public class PlayerHp : SharedVar<int> {{}})");
                        continue;
                    }

                    svFields.Add(new SharedValueFieldData
                    {
                        Field = field,
                        ExactType = fieldType
                    });
                }
                currentType = currentType.BaseType;
            }

            if (svFields.Count > 0)
            {
                _sharedValueCache[stateType] = svFields;
            }
        }

        /// <summary>
        /// 注入共享变量引用
        /// </summary>
        internal void InjectSharedValues(TStateNode state, Type stateType, ISharedVariableRegistry registry)
        {
            if (state == null || registry == null) return;
            if (!_sharedValueCache.TryGetValue(stateType, out var fields)) return;

            for (int i = 0; i < fields.Count; i++)
            {
                var fieldData = fields[i];
                try
                {
                    object sharedVarInst = registry.GetOrAddSharedVarBoxed(fieldData.ExactType);
                    fieldData.Field.SetValue(state, sharedVarInst);
                }
                catch (Exception e)
                {
                    _debug.LogErr($"[SharedVar] 注入共享变量失败 ({fieldData.Field.Name}):", e);
                    throw;
                }
            }
        }

        // 辅助方法：判断类型是否继承自指定的泛型类 (SharedVar<T>) (废弃)
        private static bool IsSubclassOfRawGeneric(Type generic, Type toCheck)
        {
            while (toCheck != null && toCheck != typeof(object))
            {
                var cur = toCheck.IsGenericType ? toCheck.GetGenericTypeDefinition() : toCheck;
                if (generic == cur) return true;
                toCheck = toCheck.BaseType;
            }
            return false;
        }

        //!--- [AutoInject] ---
        /// <summary>
        /// 扫描并缓存带有 [AutoInject] 标记的字段
        /// </summary>
        internal void CacheInjectFields(Type stateType)
        {
            if (_injectFieldCache.ContainsKey(stateType)) return;

            var injectFields = new List<FieldInfo>();
            Type currentType = stateType;

            while (currentType != null && currentType != typeof(object))
            {
                if (currentType.Namespace == StopNamespace) break;

                FieldInfo[] fields = currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<AutoInjectAttribute>() != null)
                    {
                        injectFields.Add(field);
                    }
                }
                currentType = currentType.BaseType;
            }

            if (injectFields.Count > 0)
            {
                _injectFieldCache[stateType] = injectFields;
            }
        }

        /// <summary>
        /// 核心注入逻辑：在 OnEnter 前执行上下文注入
        /// </summary>
        internal void InjectContextData(TStateNode state, Type stateType, IStateContextResolver contextResolver)
        {
            if (!_injectFieldCache.TryGetValue(stateType, out var fields)) return;

            foreach (var field in fields)
            {
                try
                {
                    // 使用接口获取实例
                    if (contextResolver.TryGetContext(field.FieldType, out var contextValue))
                    {
                        field.SetValue(state, contextValue);
                    }
                    else
                    {
                        _debug.LogWarn($"[AutoInject] 注入失败: [{field.FieldType.Name}] 未注册。字段 [{field.Name}] 已置空。");
                        field.SetValue(state, null);
                    }
                }
                catch (Exception)
                {
                    _debug.LogErr($"注入Context到 {field.Name} 失败:");
                    throw;
                }
            }
        }

        //!--- [AutoReset] ---
        // 扫描并缓存带有 [AutoReset] 标记的字段初始值
        internal void CacheStateFields(TStateNode state) //@ [AutoReset]
        {
            _fieldResetCache ??= new Dictionary<Type, List<FieldResetData>>();
            Type stateType = state.GetType();

            // 已经扫描过就不再扫描
            if (_fieldResetCache.ContainsKey(stateType)) return;

            var cacheList = new List<FieldResetData>();
            Type currentType = stateType;

            // 向上遍历直到 Object，以便拿到父类中被标记的字段
            while (currentType != null && currentType != typeof(object))
            {
                // 遇到框架底层基类停止遍历，提升性能
                if (currentType.Namespace == StopNamespace) break;

                FieldInfo[] fields = currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (var field in fields)
                {

                    // 只有挂载了 [AutoReset] 的字段才被缓存
                    if (field.GetCustomAttribute<AutoResetAttribute>() != null)
                    {
                        // 判断字段类型是否派生自 SharedVar
                        if (_sharedVarInterfaceType.IsAssignableFrom(field.FieldType))
                        {
                            cacheList.Add(new FieldResetData
                            {
                                Field = field,
                                IsSharedVar = true,
                                InitialValue = null
                            });
                        }
                        else
                        {
                            // 常规字段的缓存
                            object initialValue = field.GetValue(state); // 只取一次

                            if (initialValue is UnityEngine.Object uObj && uObj == null) initialValue = null;

                            if (!field.FieldType.IsValueType && field.FieldType != typeof(string))
                            {
                                if (initialValue != null)
                                    _debug.LogWarn($"字段 {field.Name} 是引用类型，[AutoReset] 只能恢复其引用地址，无法重置内部数据！" +
                                        "建议仅用于值类型或初始值必须为 null 的场景。(MonoBehavior可使用[NonSerialized]防止序列化)");
                            }

                            cacheList.Add(new FieldResetData
                            {
                                Field = field,
                                IsSharedVar = false,
                                InitialValue = initialValue
                            });
                        }
                    }
                }
                currentType = currentType.BaseType;
            }

            // 优化：只有真的有字段需要重置，才存入字典
            if (cacheList.Count > 0)
            {
                _fieldResetCache[stateType] = cacheList;
            }
        }
        // 恢复状态的字段到初始值
        internal void ResetStateFields(TStateNode state)
        {
            if (_fieldResetCache == null || state == null) return;

            // 只有在字典中存在的状态，才需要重置
            if (_fieldResetCache.TryGetValue(state.GetType(), out var fields))
            {
                foreach (var data in fields)
                {
                    try
                    {
                        // 分支处理: 判定SharedVar
                        if (data.IsSharedVar)
                        {
                            var sharedVarInstance = data.Field.GetValue(state) as ISharedVarInternal;
                            sharedVarInstance?.ResetToBaseValue(notify: true);
                        }
                        else
                        {
                            // 直接覆盖字段值
                            data.Field.SetValue(state, data.InitialValue);
                        }
                    }
                    catch (Exception)
                    {
                        _debug.LogErr($"自动重置字段 {data.Field.Name} 失败:");
                        throw;
                    }
                }
            }
        }

        //!--- [Payload] ---
        // 扫描并缓存带有 [Payload] 标记的字段和它的真实初始值
        internal void CachePayloadFields(TStateNode state) //@ [Payload]
        {
            Type stateType = state.GetType();
            if (_payloadFieldCache.ContainsKey(stateType)) return;

            var payloadFields = new List<PayloadFieldData>();
            Type currentType = stateType;
            string stopNamespace = StopNamespace;

            while (currentType != null && currentType != typeof(object))
            {
                if (currentType.Namespace == stopNamespace) break;

                FieldInfo[] fields = currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<PayloadAttribute>() != null)
                    {
                        bool isSharedVar = _sharedVarInterfaceType.IsAssignableFrom(field.FieldType);
                        Type expectedType = field.FieldType;

                        // 如果是 SharedVar，提取它的泛型参数 T 作为期望接收的 Payload 类型
                        if (isSharedVar)
                        {
                            Type baseGeneric = field.FieldType;
                            while (baseGeneric != null && (!baseGeneric.IsGenericType || baseGeneric.GetGenericTypeDefinition() != typeof(SharedVar<>)))
                            {
                                baseGeneric = baseGeneric.BaseType;
                            }
                            if (baseGeneric != null) expectedType = baseGeneric.GetGenericArguments()[0];
                        }

                        payloadFields.Add(new PayloadFieldData
                        {
                            Field = field,
                            DataType = field.FieldType,
                            DefaultValue = isSharedVar ? null : field.GetValue(state),
                            IsSharedVar = isSharedVar,
                            ExpectedPayloadType = expectedType
                        });
                    }
                }
                currentType = currentType.BaseType;
            }

            if (payloadFields.Count > 0)
            {
                _payloadFieldCache[stateType] = payloadFields;
            }
        }
        /// <summary>
        /// 核心注入逻辑：根据传入的数据类型，寻找匹配的 [Payload] 字段进行注入
        /// 如果 dataWrapper 为空，则表示无参切换，需将字段重置为默认值
        /// </summary>
        internal void InjectPayloadData(TStateNode state, Type targetStateType, object dataWrapper)
        {
            bool hasPayloadFields = _payloadFieldCache.TryGetValue(targetStateType, out var fields);

            if (dataWrapper != null && (!hasPayloadFields || fields.Count == 0))
            {
                _debug.LogVerbose($"[{targetStateType.Name}] 未使用 [Payload] 标签，数据已被丢弃", LogColor.Grey, true);
                return;
            }

            if (!hasPayloadFields) return;

            foreach (var fieldData in fields)
            {
                try
                {
                    if (dataWrapper == null)
                    {
                        // 无参切换，如果是被 AutoLink 保护的则跳过
                        if (_successfullyLinkedFields.Contains(fieldData.Field)) continue;

                        // 执行重置
                        if (fieldData.IsSharedVar)
                        {
                            var sv = fieldData.Field.GetValue(state) as ISharedVarInternal;
                            sv?.ResetToBaseValue(notify: true);
                        }
                        else
                        {
                            fieldData.Field.SetValue(state, fieldData.DefaultValue);
                        }
                    }
                    else
                    {
                        // 如果 Payload 类型匹配期望的真实类型
                        if (fieldData.ExpectedPayloadType.IsAssignableFrom(dataWrapper.GetType()))
                        {
                            if (fieldData.IsSharedVar)
                            {
                                var sv = fieldData.Field.GetValue(state) as ISharedVarInternal;
                                sv?.SetValueBoxed(dataWrapper); // 将值包装注入
                            }
                            else
                            {
                                fieldData.Field.SetValue(state, dataWrapper);
                            }
                        }
                        // 如果目标是 SharedVar，且用户直接传了一个 SharedVar 实例进来
                        else if (fieldData.IsSharedVar && fieldData.DataType == dataWrapper.GetType())
                        {
                            var sv = fieldData.Field.GetValue(state) as ISharedVarInternal;
                            var passedSv = dataWrapper as ISharedVarInternal;
                            sv?.CopyFrom(passedSv);
                        }
                        else
                        {
                            // 类型不匹配，作为脏数据重置为默认
                            if (_successfullyLinkedFields.Contains(fieldData.Field)) continue;

                            _debug.LogVerbose($"状态 <color=white>[{targetStateType.Name}]</color> 的字段 <color=yellow>[{fieldData.Field.Name}]</color> 期望: <color=green>{fieldData.ExpectedPayloadType.Name}</color>，" +
                                $"实际: <color=red>{dataWrapper.GetType().Name}</color>。[Payload] 已重置为默认值。", LogColor.Grey, true);

                            if (fieldData.IsSharedVar)
                            {
                                var sv = fieldData.Field.GetValue(state) as ISharedVarInternal;
                                sv?.ResetToBaseValue(notify: true);
                            }
                            else
                            {
                                fieldData.Field.SetValue(state, fieldData.DefaultValue);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    _debug.LogErr($"注入Payload数据到 {fieldData.Field.Name} 失败:");
                    throw;
                }
            }
        }
        //!--- [AutoLink] ---
        internal void ClearLinkBlackboard()
        {
            _linkBlackboard.Clear();
        }
        // 扫描并缓存带有 [AutoLink] 标记的字段
        internal void CacheLinkFields(TStateNode state) //@ [AutoLink]
        {
            Type stateType = state.GetType();
            if (_linkFieldCache.ContainsKey(stateType)) return;

            var linkFields = new List<LinkFieldData>();
            Type currentType = stateType;

            while (currentType != null && currentType != typeof(object))
            {
                if (currentType.Namespace == StopNamespace) break;

                FieldInfo[] fields = currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (var field in fields)
                {
                    var attr = field.GetCustomAttribute<AutoLinkAttribute>();
                    if (attr != null)
                    {
                        // 如果没有指定 Key，默认使用 "类型全名_字段名" 以防同名不同类型的冲突
                        string linkKey = string.IsNullOrEmpty(attr.Key) ? $"{field.FieldType.FullName}_{field.Name}" : attr.Key;

                        bool canAcceptNull = !field.FieldType.IsValueType || Nullable.GetUnderlyingType(field.FieldType) != null;

                        linkFields.Add(new LinkFieldData
                        {
                            Field = field,
                            Key = linkKey,
                            DataType = field.FieldType,
                            CanAcceptNull = canAcceptNull
                        });
                    }
                }
                currentType = currentType.BaseType;
            }

            if (linkFields.Count > 0)
            {
                _linkFieldCache[stateType] = linkFields;
            }
        }

        // 在旧状态退出后，抓取快照
        internal void SnapshotLinkData(TStateNode state, Type stateType)
        {
            if (state == null || !_linkFieldCache.TryGetValue(stateType, out var fields)) return;

            foreach (var fieldData in fields)
            {
                try
                {
                    var snapshotValue = fieldData.Field.GetValue(state);
                    // 拦截假 Null
                    if (snapshotValue is UnityEngine.Object uObj && uObj == null) snapshotValue = null;

                    // 将当前值存入黑板/快照池
                    _linkBlackboard[fieldData.Key] = snapshotValue;
                }
                catch (Exception)
                {
                    _debug.LogErr($"抓取 [AutoLink] 快照失败 ({fieldData.Field.Name}):");
                    throw;
                }
            }
        }

        // 在新状态进入前，从快照池恢复数据
        internal void InjectLinkData(TStateNode state, Type stateType)
        {
            // 每次注入前清空上一轮的记录
            _successfullyLinkedFields.Clear();

            if (state == null || !_linkFieldCache.TryGetValue(stateType, out var fields)) return;

            foreach (var fieldData in fields)
            {
                if (_linkBlackboard.TryGetValue(fieldData.Key, out var snapshotValue))
                {
                    try
                    {
                        // 为null时检测canAcceptNull，否则匹配GetType
                        bool isValid = snapshotValue == null
                            ? fieldData.CanAcceptNull
                            : fieldData.DataType.IsAssignableFrom(snapshotValue.GetType());

                        if (isValid)
                        {
                            fieldData.Field.SetValue(state, snapshotValue);
                            // 记录这个字段已经被 AutoLink 接管了
                            _successfullyLinkedFields.Add(fieldData.Field);
                        }
                        else
                        {
                            _debug.LogWarn($"[AutoLink] 类型不匹配！期望: {fieldData.DataType.Name}, 实际: {snapshotValue.GetType().Name}。跳过注入。");
                        }
                    }
                    catch (Exception)
                    {
                        _debug.LogErr($"注入 [AutoLink] 数据失败 ({fieldData.Field.Name}):");
                        throw;
                    }

                }
            }
        }
        //!--- [AutoHandle] ---
        /// <summary>
        /// 尝试执行状态中通过 [AutoHandle] 注册的指令
        /// </summary>
        /// <returns>如果找到了对应的指令并成功执行，则返回 true</returns>
        internal bool TryInvokeAutoHandle<TPayload>(Type stateType, TPayload payload, HandleTiming targetTiming, out bool isConsumed)
        {
            isConsumed = false;
            if (stateType == null) return false;

            if (_autoHandleCache.TryGetValue(stateType, out var handleDict) &&
                handleDict.TryGetValue((typeof(TPayload), targetTiming), out var handler))
            {
                // 1. 如果是返回 bool 的方法，执行并根据返回值决定是否消费
                if (handler is Func<TPayload, bool> boolFunc)
                {
                    isConsumed = boolFunc.Invoke(payload);
                    return true;
                }

                // 2. 如果是 void 方法，执行且默认不消费 (isConsumed 保持 false)
                if (handler is Action<TPayload> voidAction)
                {
                    voidAction.Invoke(payload);
                    isConsumed = false;
                    return true;
                }
            }
            return false;
        }
        // 扫描并缓存带有 [AutoHandle] 标记的方法
        internal void CacheAutoHandles(TStateNode state)
        {
            Type stateType = state.GetType();
            if (_autoHandleCache.ContainsKey(stateType)) return;

            var handleDict = new Dictionary<(Type, HandleTiming timing), Delegate>();
            HashSet<MethodInfo> registeredMethods = new HashSet<MethodInfo>();

            // ==========================================
            // 1. 扫描当前状态实现的所有接口 (支持默认接口方法 DIM)
            // ==========================================
            Type[] interfaces = stateType.GetInterfaces();
            foreach (var iType in interfaces)
            {
                if (iType.Namespace == StopNamespace) continue;

                MethodInfo[] iMethods = iType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var method in iMethods)
                {
                    var attr = method.GetCustomAttribute<AutoHandleAttribute>();
                    if (attr != null)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 1 && (method.ReturnType == typeof(void) || method.ReturnType == typeof(bool)))
                        {
                            Type payloadType = parameters[0].ParameterType;

                            // 【改动点】使用 Delegate.CreateDelegate 原生创建
                            Delegate handler = CreateAutoHandleDelegate(state, method, payloadType);

                            var key = (payloadType, attr.Timing);
                            if (handleDict.ContainsKey(key))
                                _debug.LogWarn($"状态 [{stateType.Name}] 的接口中存在多个处理 [{payloadType.Name}] 的 [AutoHandle]，旧的将被覆盖。");

                            handleDict[key] = handler;

                            try
                            {
                                var interfaceMap = stateType.GetInterfaceMap(iType);
                                int methodIndex = Array.IndexOf(interfaceMap.InterfaceMethods, method);
                                if (methodIndex >= 0) registeredMethods.Add(interfaceMap.TargetMethods[methodIndex]);
                            }
                            catch { }
                        }
                        else
                        {
                            _debug.LogErr($"[AutoHandle] 接口方法签名不合法！必须是一个参数，且返回值为 void 或 bool: {iType.Name}.{method.Name}");
                        }
                    }
                }
            }

            // ==========================================
            // 2. 扫描类自身及基类的继承链
            // ==========================================
            Type currentType = stateType;
            while (currentType != null && currentType != typeof(object))
            {
                if (currentType.Namespace == StopNamespace) break;

                MethodInfo[] methods = currentType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                foreach (var method in methods)
                {
                    if (registeredMethods.Contains(method)) continue;

                    var attr = method.GetCustomAttribute<AutoHandleAttribute>();
                    if (attr != null)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 1 && (method.ReturnType == typeof(void) || method.ReturnType == typeof(bool)))
                        {
                            Type payloadType = parameters[0].ParameterType;

                            // 【改动点】使用 Delegate.CreateDelegate 原生创建
                            Delegate handler = CreateAutoHandleDelegate(state, method, payloadType);

                            var key = (payloadType, attr.Timing);
                            if (handleDict.ContainsKey(key))
                                _debug.LogWarn($"状态 [{stateType.Name}] 的类继承链中存在多个处理 [{payloadType.Name}] 的 [AutoHandle]，旧的将被覆盖。");

                            handleDict[key] = handler;
                        }
                        else
                        {
                            _debug.LogErr($"[AutoHandle] 类方法签名不合法！必须是一个参数，且返回值为 void 或 bool: {method.Name}");
                        }
                    }
                }
                currentType = currentType.BaseType;
            }

            if (handleDict.Count > 0)
            {
                _autoHandleCache[stateType] = handleDict;
            }
        }

        // 抽取的小工具方法：根据返回值类型绑定对应的标准泛型委托
        private static Delegate CreateAutoHandleDelegate(object target, MethodInfo method, Type payloadType)
        {
            if (method.ReturnType == typeof(bool))
            {
                // 绑定为 Func<TPayload, bool>
                Type funcType = typeof(Func<,>).MakeGenericType(payloadType, typeof(bool));
                return Delegate.CreateDelegate(funcType, target, method);
            }
            else
            {
                // 绑定为 Action<TPayload>
                Type actionType = typeof(Action<>).MakeGenericType(payloadType);
                return Delegate.CreateDelegate(actionType, target, method);
            }
        }

        //!--- [AutoBind] ---
        /// <summary>
        /// 扫描状态类中的 [AutoBind] 字段，并立即与 Owner 执行绑定
        /// </summary>
        internal void BindOwnerDependencies(TStateNode state, object owner)
        {
            if (owner == null) return;

            Type stateType = state.GetType();

            // 如果还没有缓存过这个类型的绑定逻辑，则进行反射解析
            if (!_autoBindCache.TryGetValue(stateType, out var binder))
            {
                List<Action<TStateNode, object>> bindActions = new();
                Type currentType = stateType;

                // 向上遍历继承链，直到框架底层基类
                while (currentType != null && currentType != typeof(object))
                {
                    if (currentType.Namespace == StopNamespace) break;

                    FieldInfo[] stateFields = currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    foreach (var stateField in stateFields)
                    {
                        var attr = stateField.GetCustomAttribute<AutoBindAttribute>();
                        if (attr == null) continue;

                        string targetName = string.IsNullOrEmpty(attr.OwnerFieldName) ? stateField.Name : attr.OwnerFieldName;
                        Type targetType = stateField.FieldType;

                        // 1. 尝试在 Owner 中查找同名字段 (Field)
                        FieldInfo ownerField = FindFieldInHierarchy(owner.GetType(), targetName);
                        if (ownerField != null && targetType.IsAssignableFrom(ownerField.FieldType))
                        {
                            bindActions.Add((st, o) =>
                            {
                                object val = ownerField.GetValue(o);

                                // 加入对 Unity 对象的显式 Null 检测
                                bool isNull = val == null || (val is UnityEngine.Object uObj && uObj == null);

                                if (isNull)
                                {
                                    _debug.LogWarn($"[AutoBind] 状态 <color=white>[{st.GetType().Name}]</color> 尝试绑定宿主 <color=white>[{o.GetType().Name}]</color> 的字段 <color=yellow>'{targetName}'</color>，但该字段当前为 null！\n" +
                                                $"如果是 Unity 组件，请检查 Inspector 面板是否忘记拖拽赋值？");
                                }
                                stateField.SetValue(st, val);
                            });
                            continue;
                        }

                        // 2. 尝试在 Owner 中查找同名属性 (Property)
                        PropertyInfo ownerProp = FindPropertyInHierarchy(owner.GetType(), targetName);
                        if (ownerProp != null && ownerProp.CanRead && targetType.IsAssignableFrom(ownerProp.PropertyType))
                        {
                            bindActions.Add((st, o) =>
                            {
                                object val = ownerProp.GetValue(o);

                                // 加入对 Unity 对象的显式 Null 检测
                                bool isNull = val == null || (val is UnityEngine.Object uObj && uObj == null);

                                if (isNull)
                                {
                                    _debug.LogWarn($"[AutoBind] 状态 <color=white>[{st.GetType().Name}]</color> 尝试绑定宿主 <color=white>[{o.GetType().Name}]</color> 的属性 <color=yellow>'{targetName}'</color>，但该属性当前返回 null！");
                                }
                                stateField.SetValue(st, val);
                            });
                            continue;
                        }

                        // 3. 智能回退到 GetComponent
                        if (owner is UnityEngine.Component ownerComponent && typeof(UnityEngine.Component).IsAssignableFrom(targetType))
                        {
                            bindActions.Add((st, o) =>
                            {
                                var comp = ((UnityEngine.Component)o).GetComponent(targetType);
                                if (comp != null)
                                {
                                    stateField.SetValue(st, comp);
                                }
                                else
                                {
                                    _debug.LogWarn($"[AutoBind] 在宿主上找不到组件: {targetType.Name} ({targetName})");
                                }
                            });
                            continue;
                        }

                        // 找不到时的报错
                        _debug.LogErr($"[AutoBind] 绑定失败！在 Owner 中找不到匹配的字段/属性，也不是可获取的组件: {targetName}");
                    }

                    // 步进到基类
                    currentType = currentType.BaseType;
                }

                // 存入缓存（如果没有任何绑定，存入一个空委托防止下次重复反射）
                if (bindActions.Count > 0)
                {
                    binder = (st, o) => { foreach (var action in bindActions) action(st, o); };
                }
                else
                {
                    binder = (st, o) => { };
                }

                _autoBindCache[stateType] = binder;
            }

            // 立即执行绑定
            binder.Invoke(state, owner);
        }

        // 辅助方法：沿继承链向上查找字段（由于是Processor专属，设为private即可）
        private static FieldInfo FindFieldInHierarchy(Type type, string fieldName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            while (type != null && type != typeof(object))
            {
                FieldInfo field = type.GetField(fieldName, flags);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        // 辅助方法：沿继承链向上查找属性
        private static PropertyInfo FindPropertyInHierarchy(Type type, string propertyName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            while (type != null && type != typeof(object))
            {
                PropertyInfo prop = type.GetProperty(propertyName, flags);
                if (prop != null) return prop;
                type = type.BaseType;
            }
            return null;
        }

        //!--- [AutoRun] ---
        internal Action<TStateNode> GetAutoRun(Type stateType, Timing timing)
        {
            if (_autoRunCache.TryGetValue(stateType, out var timingDict))
            {
                if (timingDict.TryGetValue(timing, out var action))
                {
                    return action;
                }
            }
            return null;
        }

        internal void InvokeAutoRun(TStateNode state, Timing timing)
        {
            if (state == null) return;
            if (_autoRunCache.TryGetValue(state.GetType(), out var actions))
            {
                if (actions.TryGetValue(timing, out var action))
                {
                    try { action.Invoke(state); }
                    catch (Exception) { _debug.LogErr($"[AutoRun] 执行异常 ({timing}):"); throw; }
                }
            }
        }
        internal void CacheAutoRunMethods(TStateNode state)
        {
            Type stateType = state.GetType();
            if (_autoRunCache.ContainsKey(stateType)) return;

            var runActions = new Dictionary<Timing, List<(int order, Action<TStateNode> action)>>();

            // 用于防重：防止同一个方法被类和接口重复注册
            HashSet<MethodInfo> registeredMethods = new HashSet<MethodInfo>();

            // ==========================================
            // 1. 扫描当前状态实现的所有接口 (支持默认接口方法 DIM)
            // ==========================================
            Type[] interfaces = stateType.GetInterfaces();
            foreach (var iType in interfaces)
            {
                // 忽略框架底层的接口，提升性能
                if (iType.Namespace == StopNamespace) continue;

                MethodInfo[] iMethods = iType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var method in iMethods)
                {
                    var attr = method.GetCustomAttribute<AutoRunAttribute>();
                    if (attr != null)
                    {
                        if (method.GetParameters().Length > 0 || method.ReturnType != typeof(void))
                        {
                            _debug.LogErr($"[AutoRun] 接口方法必须是无参且返回 void: {iType.Name}.{method.Name}");
                            continue;
                        }

                        // 【改动点】通过 Delegate.CreateDelegate 创建原生的闭包委托 (替代表达式树)
                        // 完美支持 C# 8 默认接口方法 (DIM) 的实例虚调用
                        Action rawAction = (Action)Delegate.CreateDelegate(typeof(Action), state, method);
                        Action<TStateNode> compiledAction = _ => rawAction.Invoke();

                        if (!runActions.ContainsKey(attr.Timing))
                            runActions[attr.Timing] = new List<(int, Action<TStateNode>)>();

                        runActions[attr.Timing].Add((attr.Order, compiledAction));

                        // 防重记录：加入 try-catch 防止纯 DIM 未在类重写时在个别 IL2CPP 版本抛异常
                        try
                        {
                            var interfaceMap = stateType.GetInterfaceMap(iType);
                            int methodIndex = Array.IndexOf(interfaceMap.InterfaceMethods, method);
                            if (methodIndex >= 0) registeredMethods.Add(interfaceMap.TargetMethods[methodIndex]);
                        }
                        catch { /* 忽略 IL2CPP 边缘接口映射异常 */ }
                    }
                }
            }

            // ==========================================
            // 2. 扫描类自身及基类的继承链
            // ==========================================
            Type currentType = stateType;
            while (currentType != null && currentType != typeof(object))
            {
                if (currentType.Namespace == StopNamespace) break;

                MethodInfo[] methods = currentType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (var method in methods)
                {
                    // 防重处理：如果这个方法已经在接口扫描时处理过了（比如隐式实现），则跳过
                    if (registeredMethods.Contains(method)) continue;

                    var attr = method.GetCustomAttribute<AutoRunAttribute>();
                    if (attr != null)
                    {
                        if (method.GetParameters().Length > 0 || method.ReturnType != typeof(void))
                        {
                            _debug.LogErr($"[AutoRun] 目标方法必须是无参且返回 void: {method.Name}");
                            continue;
                        }

                        // 【改动点】通过 Delegate.CreateDelegate 直接绑定到目标实例
                        Action rawAction = (Action)Delegate.CreateDelegate(typeof(Action), state, method);
                        Action<TStateNode> compiledAction = _ => rawAction.Invoke();

                        if (!runActions.ContainsKey(attr.Timing))
                            runActions[attr.Timing] = new List<(int, Action<TStateNode>)>();

                        runActions[attr.Timing].Add((attr.Order, compiledAction));
                    }
                }
                currentType = currentType.BaseType;
            }

            // ==========================================
            // 3. 排序并合并为一个多播委托
            // ==========================================
            if (runActions.Count > 0)
            {
                var finalDict = new Dictionary<Timing, Action<TStateNode>>();
                foreach (var kvp in runActions)
                {
                    // 按 Order 升序排序，Order 越小越先执行
                    var sortedActions = kvp.Value.OrderBy(x => x.order).Select(x => x.action).ToList();
                    Action<TStateNode> combinedAction = null;
                    foreach (var act in sortedActions)
                    {
                        combinedAction += act; // 委托链
                    }
                    finalDict[kvp.Key] = combinedAction;
                }
                _autoRunCache[stateType] = finalDict;
            }
        }

        //!--- [清理] ---
        /// <summary>
        /// [仅供 Dispose 时调用] 清理指定状态实例中的所有引用类型字段，断开对外部对象的强引用，防止内存泄漏。
        /// 包含: [AutoInject] / [Payload] / [AutoLink] / SharedVar 字段置 null，[AutoReset] 恢复初始值(若为引用也安全重置)
        /// </summary>
        internal void ClearInstanceReferences(TStateNode state, Type stateType)
        {
            if (state == null || stateType == null) return;

            // 1. 清空 [AutoInject] 字段 (切断对外部 Context / Service / Manager 的引用)
            if (_injectFieldCache.TryGetValue(stateType, out var injectFields))
            {
                for (int i = 0; i < injectFields.Count; i++)
                {
                    var field = injectFields[i];
                    if (!field.FieldType.IsValueType)
                    {
                        field.SetValue(state, null);
                    }
                }
            }

            // 2. 清空 [Payload] 字段 (切断随事件透传进来的引用数据)
            if (_payloadFieldCache.TryGetValue(stateType, out var payloadFields))
            {
                for (int i = 0; i < payloadFields.Count; i++)
                {
                    var fieldData = payloadFields[i];
                    if (!fieldData.Field.FieldType.IsValueType)
                    {
                        fieldData.Field.SetValue(state, null);
                    }
                }
            }

            // 3. 清空 [AutoLink] 字段 (切断跨状态透传留下的引用)
            if (_linkFieldCache.TryGetValue(stateType, out var linkFields))
            {
                for (int i = 0; i < linkFields.Count; i++)
                {
                    var fieldData = linkFields[i];
                    if (!fieldData.Field.FieldType.IsValueType)
                    {
                        fieldData.Field.SetValue(state, null);
                    }
                }
            }

            // 4. 清空状态内的 SharedVar 引用字段 (切断对大盘物理单例的持有)
            if (_sharedValueCache.TryGetValue(stateType, out var svFields))
            {
                for (int i = 0; i < svFields.Count; i++)
                {
                    svFields[i].Field.SetValue(state, null);
                }
            }

            // 5. [AutoReset] 字段恢复初始值 (若初始值是引用对象，也避免残留脏数据)
            if (_fieldResetCache != null && _fieldResetCache.TryGetValue(stateType, out var resetFields))
            {
                for (int i = 0; i < resetFields.Count; i++)
                {
                    var resetData = resetFields[i];
                    if (!resetData.Field.FieldType.IsValueType)
                    {
                        resetData.Field.SetValue(state, null);
                    }
                }
            }
        }

        /// <summary>
        /// [批量清理重载] 传入指定的 Type 列表以及对应的实例字典，对目标状态逐个执行引用字段置空。
        /// </summary>
        internal void ClearStatesReferences(IEnumerable<Type> stateTypes, Dictionary<Type, TStateNode> stateMap)
        {
            if (stateTypes == null || stateMap == null) return;

            foreach (var type in stateTypes)
            {
                if (stateMap.TryGetValue(type, out var state) && state != null)
                {
                    ClearInstanceReferences(state, type);
                }
            }
        }

        internal void ClearCache()
        {
            _fieldResetCache?.Clear();
            _injectFieldCache?.Clear();
            _payloadFieldCache?.Clear();
            _linkFieldCache?.Clear();
            _autoHandleCache?.Clear();
            _linkBlackboard?.Clear();
            _successfullyLinkedFields?.Clear();
            _autoBindCache?.Clear();
            // _sharedVarInjectorCache?.Clear();
            _sharedValueCache.Clear();
            _autoRunCache?.Clear();
            _debug = null;
        }
    }

    #endregion
    //!============== Extensions ==================
    #region Extensions

    //# Select 操作符扩展

    public static class SharedVarSelectExtensions
    {
        /// <summary>
        /// 对共享变量进行数据投影映射 (如 hp => $"HP: {hp}" 或 hp => hp > 0)
        /// </summary>
        /// <typeparam name="TSource">源类型</typeparam>
        /// <typeparam name="TResult">目标映射类型</typeparam>
        /// <param name="source">源变量</param>
        /// <param name="selector">映射转换委托</param>
        /// <param name="distinctUntilChanged">映射后的值若未发生改变是否拦截通知 (默认 true 优化 UI 性能)</param>
        public static IReadOnlySharedVar<TResult> Select<TSource, TResult>(
            this IReadOnlySharedVar<TSource> source,
            Func<TSource, TResult> selector,
            bool distinctUntilChanged = true)
        {
            return new SelectReadOnlySharedVar<TSource, TResult>(source, selector, distinctUntilChanged);
        }

        /// <summary>
        /// 对共享变量进行数据投影映射 (双参数版：支持同时传入当前值与基准初始值)
        /// 例: hp.Select((cur, init) => (float)cur / init) 计算百分比
        /// </summary>
        public static IReadOnlySharedVar<TResult> Select<TSource, TResult>(
            this IReadOnlySharedVar<TSource> source,
            Func<TSource, TSource, TResult> selector,
            bool distinctUntilChanged = true)
        {
            return new SelectDualReadOnlySharedVar<TSource, TResult>(source, selector, distinctUntilChanged);
        }
    }

    /// <summary>
    /// 响应式数据映射只读视图 (Select Projection)
    /// </summary>
    public sealed class SelectReadOnlySharedVar<TSource, TResult> : IReadOnlySharedVar<TResult>
    {
        private readonly IReadOnlySharedVar<TSource> _source;
        private readonly Func<TSource, TResult> _selector;
        private readonly bool _distinctUntilChanged;

        public SelectReadOnlySharedVar(IReadOnlySharedVar<TSource> source, Func<TSource, TResult> selector, bool distinctUntilChanged = true)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _selector = selector ?? throw new ArgumentNullException(nameof(selector));
            _distinctUntilChanged = distinctUntilChanged;
        }

        /// <summary>当前计算后的映射值</summary>
        public TResult Value => _selector(_source.Value);

        /// <summary>计算后的基准初始映射值</summary>
        public TResult BaseValue => _selector(_source.BaseValue);

        /// <summary>
        /// 订阅映射后的数据变更
        /// </summary>
        public IDisposable Subscribe(Action<TResult> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new StateActionDisposable(null);
            if (ct.IsCancellationRequested) return new StateActionDisposable(null);

            TResult lastValue = default;
            bool hasValue = false;

            // 包装源数据回调
            Action<TSource> sourceObserver = (sourceVal) =>
            {
                TResult mappedVal = _selector(sourceVal);

                // 开启防抖时，若映射结果未发生改变则不通知 UI
                if (_distinctUntilChanged)
                {
                    if (hasValue && ZeroGcEqualityComparer<TResult>.EqualsFunc(lastValue, mappedVal))
                    {
                        return;
                    }
                    lastValue = mappedVal;
                    hasValue = true;
                }

                onValueChanged.Invoke(mappedVal);
            };

            // 立即同步当前值
            if (notifyImmediately)
            {
                TResult initialMapped = Value;
                lastValue = initialMapped;
                hasValue = true;
                onValueChanged.Invoke(initialMapped);
            }

            // 将监听挂载到底层源 SharedVar 上（不重复触发 notifyImmediately，由上面接管）
            return _source.Subscribe(sourceObserver, notifyImmediately: false, ct: ct);
        }
    }

    /// <summary>
    /// 响应式数据映射只读视图 (双参数 Select: value + baseValue)
    /// </summary>
    public sealed class SelectDualReadOnlySharedVar<TSource, TResult> : IReadOnlySharedVar<TResult>
    {
        private readonly IReadOnlySharedVar<TSource> _source;
        private readonly Func<TSource, TSource, TResult> _selector;
        private readonly bool _distinctUntilChanged;

        public SelectDualReadOnlySharedVar(
            IReadOnlySharedVar<TSource> source,
            Func<TSource, TSource, TResult> selector,
            bool distinctUntilChanged = true)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _selector = selector ?? throw new ArgumentNullException(nameof(selector));
            _distinctUntilChanged = distinctUntilChanged;
        }

        /// <summary>当前计算后的映射值</summary>
        public TResult Value => _selector(_source.Value, _source.BaseValue);

        /// <summary>初始状态计算后的基准映射值 (此时 value 即 baseValue)</summary>
        public TResult BaseValue => _selector(_source.BaseValue, _source.BaseValue);

        public IDisposable Subscribe(Action<TResult> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new StateActionDisposable(null);
            if (ct.IsCancellationRequested) return new StateActionDisposable(null);

            TResult lastValue = default;
            bool hasValue = false;

            Action<TSource> sourceObserver = (sourceVal) =>
            {
                // 计算时读取当前底层的 baseValue
                TResult mappedVal = _selector(sourceVal, _source.BaseValue);

                if (_distinctUntilChanged)
                {
                    if (hasValue && ZeroGcEqualityComparer<TResult>.EqualsFunc(lastValue, mappedVal))
                    {
                        return;
                    }
                    lastValue = mappedVal;
                    hasValue = true;
                }

                onValueChanged.Invoke(mappedVal);
            };

            if (notifyImmediately)
            {
                TResult initialMapped = Value;
                lastValue = initialMapped;
                hasValue = true;
                onValueChanged.Invoke(initialMapped);
            }

            return _source.Subscribe(sourceObserver, notifyImmediately: false, ct: ct);
        }
    }

    //# Where 操作符扩展
    public static class SharedVarWhereExtensions
    {
        /// <summary>
        /// 对共享变量进行条件过滤（仅当满足 predicate 条件时才下发广播与通知）
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="source">源变量</param>
        /// <param name="predicate">过滤条件谓词 (返回 true 时放行通知)</param>
        public static IReadOnlySharedVar<T> Where<T>(
            this IReadOnlySharedVar<T> source,
            Func<T, bool> predicate)
        {
            return new WhereReadOnlySharedVar<T>(source, predicate);
        }

        /// <summary>
        /// 对共享变量进行条件过滤 (双参数版：支持同时基于当前值与基准初始值进行判断)
        /// 例: hp.Where((cur, init) => cur > init * 0.3f) 
        /// </summary>
        public static IReadOnlySharedVar<T> Where<T>(
            this IReadOnlySharedVar<T> source,
            Func<T, T, bool> predicate)
        {
            return new WhereDualReadOnlySharedVar<T>(source, predicate);
        }
    }
    /// <summary>
    /// 响应式数据条件过滤只读视图 (Where Filter)
    /// </summary>
    public sealed class WhereReadOnlySharedVar<T> : IReadOnlySharedVar<T>
    {
        private readonly IReadOnlySharedVar<T> _source;
        private readonly Func<T, bool> _predicate;
        private T _lastValidValue;
        private bool _hasValidValue;

        public WhereReadOnlySharedVar(IReadOnlySharedVar<T> source, Func<T, bool> predicate)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));

            // 初始化时检查源当前值是否符合条件
            T initial = _source.Value;
            if (_predicate(initial))
            {
                _lastValidValue = initial;
                _hasValidValue = true;
            }
        }

        /// <summary>
        /// 当前最新满足条件的有效值。
        /// 若源数据最新值符合条件则返回最新值；若不符合，则保留上一次满足条件的值。
        /// </summary>
        public T Value
        {
            get
            {
                T current = _source.Value;
                if (_predicate(current))
                {
                    _lastValidValue = current;
                    _hasValidValue = true;
                    return current;
                }
                return _hasValidValue ? _lastValidValue : default;
            }
        }

        /// <summary>底层数据源的基准初始值</summary>
        public T BaseValue => _source.BaseValue;

        /// <summary>
        /// 订阅数据变更（仅当数据满足过滤条件时才会收到通知）
        /// </summary>
        public IDisposable Subscribe(Action<T> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new StateActionDisposable(null);
            if (ct.IsCancellationRequested) return new StateActionDisposable(null);

            // 包装观察者回调：仅在符合条件时下发事件并更新最后有效值
            Action<T> sourceObserver = (val) =>
            {
                if (_predicate(val))
                {
                    _lastValidValue = val;
                    _hasValidValue = true;
                    onValueChanged.Invoke(val);
                }
            };

            // 立即同步当前值（仅当当前值符合条件时触发）
            if (notifyImmediately)
            {
                T current = _source.Value;
                if (_predicate(current))
                {
                    _lastValidValue = current;
                    _hasValidValue = true;
                    onValueChanged.Invoke(current);
                }
            }

            // 挂载到底层数据源
            return _source.Subscribe(sourceObserver, notifyImmediately: false, ct: ct);
        }
    }

    /// <summary>
    /// 响应式数据条件过滤只读视图 (双参数 Where: value + baseValue)
    /// </summary>
    public sealed class WhereDualReadOnlySharedVar<T> : IReadOnlySharedVar<T>
    {
        private readonly IReadOnlySharedVar<T> _source;
        private readonly Func<T, T, bool> _predicate;
        private T _lastValidValue;
        private bool _hasValidValue;

        public WhereDualReadOnlySharedVar(IReadOnlySharedVar<T> source, Func<T, T, bool> predicate)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));

            // 初始化时检查
            T initialVal = _source.Value;
            if (_predicate(initialVal, _source.BaseValue))
            {
                _lastValidValue = initialVal;
                _hasValidValue = true;
            }
        }

        public T Value
        {
            get
            {
                T current = _source.Value;
                if (_predicate(current, _source.BaseValue))
                {
                    _lastValidValue = current;
                    _hasValidValue = true;
                    return current;
                }
                return _hasValidValue ? _lastValidValue : default;
            }
        }

        public T BaseValue => _source.BaseValue;

        public IDisposable Subscribe(Action<T> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new StateActionDisposable(null);
            if (ct.IsCancellationRequested) return new StateActionDisposable(null);

            Action<T> sourceObserver = (val) =>
            {
                if (_predicate(val, _source.BaseValue))
                {
                    _lastValidValue = val;
                    _hasValidValue = true;
                    onValueChanged.Invoke(val);
                }
            };

            if (notifyImmediately)
            {
                T current = _source.Value;
                if (_predicate(current, _source.BaseValue))
                {
                    _lastValidValue = current;
                    _hasValidValue = true;
                    onValueChanged.Invoke(current);
                }
            }

            return _source.Subscribe(sourceObserver, notifyImmediately: false, ct: ct);
        }
    }

    // # 代理 IReadOnlySharedVar
    /// <summary>
    /// 绑定状态机事件总线的动态只读代理（直通版本，Router 动态重绑免疫）
    /// </summary>
    public sealed class LiveReadOnlySharedVar<TVar, TValue> : IReadOnlySharedVar<TValue>
        where TVar : SharedVar<TValue>
    {
        private readonly IReadOnlyStateMachine _machine;

        public LiveReadOnlySharedVar(IReadOnlyStateMachine machine)
        {
            _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        }

        /// <summary>实时从当前大盘（本地或 Router）提取最新值</summary>
        public TValue Value => _machine.Variables.Read<TVar, TValue>();

        /// <summary>实时从当前大盘（本地或 Router）提取基准参考值</summary>
        public TValue BaseValue => _machine.Variables.ReadBase<TVar, TValue>();

        /// <summary>通过状态机总线订阅，完全免疫 Router 重绑失效</summary>
        public IDisposable Subscribe(Action<TValue> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new StateActionDisposable(null);
            if (ct.IsCancellationRequested) return new StateActionDisposable(null);

            // 订阅总线派发并解包 Value
            Action<TVar> handler = sharedVar => onValueChanged.Invoke(sharedVar.Value);
            var disposable = _machine.Events.Subscribe(handler, ct);

            // 立即同步当前值
            if (notifyImmediately)
            {
                onValueChanged.Invoke(Value);
            }

            return disposable;
        }
    }

    // # 代理 ISharedVar(读写)
    /// <summary>
    /// 绑定状态机事件总线的【动态读写代理】（Router 动态重绑绝对免疫）
    /// 无论状态机何时 JoinParent / LeaveParent，读写永远指向最新大盘
    /// </summary>
    public sealed class LiveSharedVar<TVar, TValue> : ISharedVar<TValue>
        where TVar : SharedVar<TValue>
    {
        private readonly IExternalStateMachine _machine;

        public LiveSharedVar(IExternalStateMachine machine)
        {
            _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        }

        // 写入：直接打入状态机当前最新大盘，自动通知/广播
        public TValue Value
        {
            get => _machine.Variables.Read<TVar, TValue>();
            set => _machine.Variables.Set<TVar, TValue>(value);
        }

        // public TValue baseValue => _machine.Variables.GetRaw<TVar>() is { } raw ? raw.BaseValue : default;
        public TValue BaseValue
            => _machine.Variables.ReadBase<TVar, TValue>();

        public bool IsDisposed => _machine.IsDisposed;

        public void SetValueAndForceNotify(TValue value)
            => _machine.Variables.GetRaw<TVar>().SetValueAndForceNotify(value);

        public void SetValueWithoutNotify(TValue value)
            => _machine.Variables.GetRaw<TVar>().SetValueWithoutNotify(value);

        public void ResetToBaseValue(bool notify = true)
            => _machine.Variables.GetRaw<TVar>().ResetToBaseValue(notify);

        public void ResetToDefault(bool notify = true)
            => _machine.Variables.GetRaw<TVar>().ResetToDefault(notify);

        public void SetBaseValue(TValue baseValue, bool notify = false)
            => _machine.Variables.GetRaw<TVar>().SetBaseValue(baseValue, notify);

        public void SetBaseValueAndReset(TValue baseValue, bool notify = true)
            => _machine.Variables.GetRaw<TVar>().SetBaseValueAndReset(baseValue, notify);

        public void ForceNotify()
            => _machine.Variables.GetRaw<TVar>().ForceNotify();

        // 订阅：利用现有的 Live 只读通道监听最新值
        public IDisposable Subscribe(Action<TValue> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new StateActionDisposable(null);
            if (ct.IsCancellationRequested) return new StateActionDisposable(null);

            // 监听状态机总线上的解包事件
            Action<TVar> handler = sharedVar => onValueChanged.Invoke(sharedVar.Value);
            var disposable = _machine.Events.Subscribe(handler, ct);

            // 立即同步当前最新大盘值
            if (notifyImmediately)
            {
                onValueChanged.Invoke(Value);
            }

            return disposable;
        }

    }

    // # 组合类扩展
    public static class SharedVarCombineExtensions
    {
        /// <summary>
        /// 将两个响应式变量组合为一个新的只读响应式变量 (CombineLatest)
        /// 当任意一个变量的值发生变更时，重新计算并通知下游
        /// </summary>
        /// <typeparam name="T1">变量1的类型</typeparam>
        /// <typeparam name="T2">变量2的类型</typeparam>
        /// <typeparam name="TResult">组合计算后的结果类型</typeparam>
        /// <param name="source1">源变量1</param>
        /// <param name="source2">源变量2</param>
        /// <param name="combiner">组合计算委托</param>
        /// <param name="distinctUntilChanged">计算结果未发生变化时是否拦截通知（默认 true，优化 UI 性能）</param>
        public static IReadOnlySharedVar<TResult> Combine<T1, T2, TResult>(
            this IReadOnlySharedVar<T1> source1,
            IReadOnlySharedVar<T2> source2,
            Func<T1, T2, TResult> combiner,
            bool distinctUntilChanged = true)
        {
            return new CombineReadOnlySharedVar<T1, T2, TResult>(source1, source2, combiner, distinctUntilChanged);
        }

        /// <summary>
        /// 将 A 的 BaseValue 动态绑定到 B 的 Value：
        /// 1. 立即将 B 的当前 Value 设为 A 的 BaseValue 并触发通知；
        /// 2. 持续监听 B 的变化，当 B 发生改变时自动同步更新 A 的 BaseValue。
        /// </summary>
        /// <typeparam name="T">变量的值类型</typeparam>
        /// <param name="target">被绑定的目标变量 A (例如: PlayerHp)</param>
        /// <param name="source">数据提供源变量 B (例如: PlayerMaxHp)</param>
        /// <param name="syncCurrentValue">绑定或B变化时，是否同时将 A 的当前 Value 也重置为新的 BaseValue（默认 false）</param>
        /// <param name="ct">生命周期令牌（销毁时自动解绑）</param>
        /// <returns>返回解绑器 IDisposable</returns>
        public static IDisposable BindBaseValueFrom<T>(
            this ISharedVar<T> target,
            IReadOnlySharedVar<T> source,
            bool syncCurrentValue = false,
            CancellationToken ct = default)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (source == null) throw new ArgumentNullException(nameof(source));

            if (ReferenceEquals(target, source))
            {
                throw new ArgumentException("禁止将自身作为 BaseValue 提供源绑定");
            }
            bool isSyncing = false;
            // 定义同步逻辑
            void ApplySync(T newBaseValue)
            {
                if (target.IsDisposed) return;
                if (isSyncing) return;

                try
                {
                    if (syncCurrentValue)
                    {
                        // 设定基准初始值，并同步将当前 Value 重置为该值（通知）
                        target.SetBaseValueAndReset(newBaseValue, notify: true);
                    }
                    else
                    {
                        // 仅修改基准初始值，并强制广播通知一次（使得依赖 BaseValue 的 Select 能够重新计算）
                        target.SetBaseValue(newBaseValue, notify: true);
                    }
                }
                finally
                {
                    isSyncing = false;
                }
            }

            // 监听 B 的最新值，notifyImmediately: true 保证在绑定瞬间立刻执行一次初始化同步
            return source.Subscribe(ApplySync, notifyImmediately: true, ct: ct);
        }
    }

    /// <summary>
    /// 双变量响应式组合只读视图 (CombineLatest 模式)
    /// </summary>
    public sealed class CombineReadOnlySharedVar<T1, T2, TResult> : IReadOnlySharedVar<TResult>
    {
        private readonly IReadOnlySharedVar<T1> _source1;
        private readonly IReadOnlySharedVar<T2> _source2;
        private readonly Func<T1, T2, TResult> _combiner;
        private readonly bool _distinctUntilChanged;

        public CombineReadOnlySharedVar(
            IReadOnlySharedVar<T1> source1,
            IReadOnlySharedVar<T2> source2,
            Func<T1, T2, TResult> combiner,
            bool distinctUntilChanged = true)
        {
            _source1 = source1 ?? throw new ArgumentNullException(nameof(source1));
            _source2 = source2 ?? throw new ArgumentNullException(nameof(source2));
            _combiner = combiner ?? throw new ArgumentNullException(nameof(combiner));
            _distinctUntilChanged = distinctUntilChanged;
        }

        /// <summary>当前计算后的组合值</summary>
        public TResult Value => _combiner(_source1.Value, _source2.Value);

        /// <summary>由两个变量的初始值组合而成的基准初始值</summary>
        public TResult BaseValue => _combiner(_source1.BaseValue, _source2.BaseValue);

        /// <summary>
        /// 订阅组合后的数据变化
        /// </summary>
        public IDisposable Subscribe(Action<TResult> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new StateActionDisposable(null);
            if (ct.IsCancellationRequested) return new StateActionDisposable(null);

            TResult lastValue = default;
            bool hasValue = false;

            // 统一更新与派发逻辑
            void NotifyCombined()
            {
                TResult currentCombined = Value;

                if (_distinctUntilChanged)
                {
                    if (hasValue && ZeroGcEqualityComparer<TResult>.EqualsFunc(lastValue, currentCombined))
                    {
                        return;
                    }
                    lastValue = currentCombined;
                    hasValue = true;
                }

                onValueChanged.Invoke(currentCombined);
            }

            // 监听源1变化（notifyImmediately 设为 false，由外层统一在初始化时调用一次）
            var sub1 = _source1.Subscribe(_ => NotifyCombined(), notifyImmediately: false, ct: ct);
            // 监听源2变化
            var sub2 = _source2.Subscribe(_ => NotifyCombined(), notifyImmediately: false, ct: ct);

            // 立即同步初始状态
            if (notifyImmediately)
            {
                NotifyCombined();
            }

            // 返回复合注销器：同时退订两个源变量
            return new StateActionDisposable(() =>
            {
                sub1.Dispose();
                sub2.Dispose();
            });
        }
    }

    #endregion
    //!============== 其他工具/缓存 ==================
    #region Other

    public struct StateHistoryRecorder
    {
        public Type StateType;
        public double Time;
        public string TriggerInfo;
        public long TransitionCount;
    }
    /// <summary>
    /// 高性能泛型类型缓存，利用 CLR 泛型静态字段特性，实现 0 运行时开销的类型判定
    /// </summary>
    internal static class TypeCache<T>
    {
        public static readonly bool IsReferenceType = !typeof(T).IsValueType;
        public static readonly bool IsSharedVar = typeof(ISharedVar).IsAssignableFrom(typeof(T));
    }
    /// <summary>
    /// 全局程序集类型存储器（非泛型，供所有状态机及外部工具共享原始 Type[]）
    /// </summary>
    internal static class AssemblyTypeHolder
    {
        private static Type[] _allTypes;

        internal sealed class AnyState { }
        /// <summary>全局通配状态 Key（代表 AnyState，匹配任意状态）</summary>
        public static readonly Type AnyStateKey = typeof(AnyState);
        /// <summary>全局静态只读的停止命名空间 (用于优化反射性能)</summary>
        public static readonly string StopNamespace = typeof(IStateNode).Namespace;
        /// <summary>获取当前程序集的所有类型（全局仅分配一次）</summary>
        public static Type[] AllTypes => _allTypes ??= LoadAllUserTypes();

        private static Type[] LoadAllUserTypes()
        {
            var userTypes = new List<Type>(2048);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                var assembly = assemblies[i];

                // 忽略动态生成的程序集
                if (assembly.IsDynamic) continue;

                string name = assembly.GetName().Name;

                // 过滤系统库与引擎原生库，大幅提高启动速度
                if (IsSystemAssembly(name)) continue;

                Type[] types = null;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // IL2CPP 开启代码裁剪后，未被引用的类型可能导致 GetTypes 抛出异常
                    // 此时必须从 ex.Types 中提取成功加载的有效类型，防止初始化闪退
                    types = ex.Types;
                }
                catch
                {
                    // 忽略个别受限程序集的加载失败
                    continue;
                }

                if (types != null)
                {
                    for (int j = 0; j < types.Length; j++)
                    {
                        var t = types[j];
                        if (t != null) userTypes.Add(t);
                    }
                }
            }

            return userTypes.ToArray();
        }

        /// <summary>
        /// 黑名单过滤系统程序集
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSystemAssembly(string name)
        {
            return name.StartsWith("System")
                || name.StartsWith("mscorlib")
                || name.StartsWith("netstandard")
                || name.StartsWith("UnityEngine")
                || name.StartsWith("UnityEditor")
                || name.StartsWith("Microsoft")
                || name.StartsWith("Mono.")
                || name.StartsWith("nunit.")
                || name.StartsWith("UniTask");
        }
    }

    /// <summary>
    /// 通用方法重写检测与全局静态缓存工具
    /// </summary>
    internal static class MethodOverrideCache
    {
        // Key: (目标类型, 方法名) -> 是否被重写
        private static readonly ConcurrentDictionary<(Type TargetType, string MethodName), bool> _cache = new();

        /// <summary>
        /// 检查 targetType 是否重写了源自泛型基类定义（如 StateBase<>）的指定方法
        /// </summary>
        /// <param name="targetType">当前需要检测的具体类型（如 PlayerAttackState）</param>
        /// <param name="methodName">方法名</param>
        /// <param name="baseGenericDefinition">开放泛型基类定义</param>
        /// <param name="parameterTypes">方法的参数类型列表（可选，用于防止重载歧义）</param>
        public static bool IsOverriddenFromGenericBase(
            Type targetType,
            string methodName,
            Type baseGenericDefinition,
            Type[] parameterTypes = null)
        {
            var key = (targetType, methodName);

            // 使用带 factoryArgument 的 GetOrAdd 重载，避免闭包捕获产生委托 GC
            return _cache.GetOrAdd(key, static (k, arg) =>
            {
                var (baseGeneric, paramTypes) = arg;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                MethodInfo method = paramTypes == null
                    ? k.TargetType.GetMethod(k.MethodName, flags)
                    : k.TargetType.GetMethod(k.MethodName, flags, null, paramTypes, null);

                if (method == null) return false;
                Type declType = method.DeclaringType;
                if (declType == null) return false;

                // 如果方法的声明类型就是底层泛型基类本身，说明未被子类重写
                if (declType.IsGenericType && declType.GetGenericTypeDefinition() == baseGeneric)
                {
                    return false;
                }

                return true;
            }, (baseGenericDefinition, parameterTypes));
        }
    }
    /// <summary>可复用的释放器</summary>
    public sealed class StateActionDisposable : IDisposable
    {
        private Action _onDispose;

        public StateActionDisposable(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }

    /// <summary>
    /// 高性能的路由指令优先级评估器 (支持精确类型与接口/基类的 0GC 匹配)
    /// </summary>
    internal class DispatchPriorityEvaluator
    {
        private Dictionary<Type, int> _exactPriorities;
        private List<(Type Type, int Priority)> _assignablePriorities;
        private Dictionary<Type, int> _cache;

        public void SetPriority(Type type, int priority)
        {
            // 如果是接口或抽象类，存入模糊匹配列表
            if (type.IsInterface || type.IsAbstract || type == typeof(object))
            {
                _assignablePriorities ??= new List<(Type, int)>();

                // 如果重复注册，更新优先级
                int index = _assignablePriorities.FindIndex(x => x.Type == type);
                if (index >= 0) _assignablePriorities[index] = (type, priority);
                else _assignablePriorities.Add((type, priority));
            }
            else
            {
                // 如果是具体类或结构体，存入精确匹配字典
                _exactPriorities ??= new Dictionary<Type, int>();
                _exactPriorities[type] = priority;
            }

            // 规则发生变化时，清空缓存
            _cache?.Clear();
        }

        public int GetPriority(Type eventType)
        {
            // 1. 缓存拦截：热路径 O(1), 0GC
            if (_cache != null && _cache.TryGetValue(eventType, out int cachedPriority))
            {
                return cachedPriority;
            }

            int finalPriority = int.MinValue; // 默认代表未配置

            // 2. 仲裁规则 1：精确匹配绝对优先 (允许用精确匹配 Override 接口配置)
            if (_exactPriorities != null && _exactPriorities.TryGetValue(eventType, out int exactPriority))
            {
                finalPriority = exactPriority;
            }
            // 3. 仲裁规则 2：遍历接口/基类，取命中的最高优先级
            else if (_assignablePriorities != null)
            {
                bool found = false;
                int maxPriority = int.MinValue;

                for (int i = 0; i < _assignablePriorities.Count; i++)
                {
                    if (_assignablePriorities[i].Type.IsAssignableFrom(eventType))
                    {
                        found = true;
                        if (_assignablePriorities[i].Priority > maxPriority)
                        {
                            maxPriority = _assignablePriorities[i].Priority;
                        }
                    }
                }

                if (found) finalPriority = maxPriority;
            }

            // 4. 写入缓存。即使是没有匹配到（返回 int.MinValue），也会被缓存下来，
            // 避免每次未知指令来临都走一次完整的反射遍历。
            _cache ??= new Dictionary<Type, int>();
            _cache[eventType] = finalPriority;

            return finalPriority;
        }

        public void Clear()
        {
            _exactPriorities?.Clear();
            _assignablePriorities?.Clear();
            _cache?.Clear();
        }
    }
    /// <summary>
    /// 高性能的 CrossTalk 规则评估器 (支持精确类型与接口/基类的 0GC 匹配)
    /// </summary>
    internal class CrossTalkEvaluator
    {
        private bool _crossTalkAll;
        private HashSet<Type> _exactTypes;      // 精确匹配类型 (如具体的 Struct)
        private List<Type> _assignableTypes;    // 模糊匹配类型 (如 Interface, Abstract Class)
        private Dictionary<Type, bool> _cache;  // 运行时懒加载缓存，保证热路径 O(1) 且 0GC

        public void SetCrossTalkAll(bool enable)
        {
            _crossTalkAll = enable;
            _cache?.Clear();
        }
        public void AddType(Type type)
        {
            // 如果传入 object 视为全事件捕获
            if (type == typeof(object))
            {
                SetCrossTalkAll(true);
                return;
            }

            // 如果是接口或抽象类，放入模糊匹配列表
            if (type.IsInterface || type.IsAbstract)
            {
                _assignableTypes ??= new List<Type>();
                if (!_assignableTypes.Contains(type)) _assignableTypes.Add(type);
            }
            else
            {
                // 如果是具体类或结构体，放入精确匹配 HashSet
                _exactTypes ??= new HashSet<Type>();
                _exactTypes.Add(type);
            }

            // 当规则发生变化时，使缓存失效
            _cache?.Clear();
        }

        public bool IsMatch<TEvent>()
        {
            // 0. 如果开启了全事件串话，直接放行 (0GC, O(1))
            if (_crossTalkAll) return true;

            Type evtType = typeof(TEvent);

            // 1. 极速检查：是否精确命中具体的类/结构体 (O(1))
            if (_exactTypes != null && _exactTypes.Contains(evtType))
                return true;

            // 2. 如果没有注册任何接口/基类，直接短路
            if (_assignableTypes == null || _assignableTypes.Count == 0)
                return false;

            // 3. 缓存检查：是否已经评估过该类型 (O(1))
            _cache ??= new Dictionary<Type, bool>();
            if (_cache.TryGetValue(evtType, out bool isCrossTalk))
                return isCrossTalk;

            // 4. 首次遇到的类型，进行反射评估 (仅执行一次)
            isCrossTalk = false;
            for (int i = 0; i < _assignableTypes.Count; i++)
            {
                if (_assignableTypes[i].IsAssignableFrom(evtType))
                {
                    isCrossTalk = true;
                    break;
                }
            }

            // 5. 写入缓存，后续调用直接走步骤3 (0GC)
            _cache[evtType] = isCrossTalk;
            return isCrossTalk;
        }

        public void Clear()
        {
            _crossTalkAll = false;
            _exactTypes?.Clear();
            _assignableTypes?.Clear();
            _cache?.Clear();
        }
    }

    //!--- [GroupSelector] ---
    /// <summary>
    /// 状态机分组动态路由选择器（负责生成 0GC 的高速路由闭包）
    /// </summary>
    internal static class GroupRouteSelector
    {
        internal static readonly System.Random Random = new System.Random();
        // ==========================================
        // 1. 静态权重 (基于 [StateWeight] 特性)
        // ==========================================
        internal static Func<Type> CreateStaticProvider(Type[] matchingStates, GroupSelectionMode mode)
        {
            if (matchingStates == null || matchingStates.Length == 0) return null;

            // 提取并缓存权重信息 (防负数处理)
            var stateWeights = matchingStates.Select(type =>
            {
                var attr = type.GetCustomAttribute<StateWeightAttribute>();
                int weight = attr != null ? attr.Weight : 1;
                return new { Type = type, Weight = System.Math.Max(0, weight) };
            }).ToArray();

            int totalWeight = stateWeights.Sum(x => x.Weight);

            // 如果是最高权重模式，在配置阶段直接算出结果，运行时只需 O(1) 返回
            if (mode == GroupSelectionMode.HighestWeight)
            {
                var highestType = stateWeights.OrderByDescending(x => x.Weight).FirstOrDefault()?.Type;
                return () => highestType;
            }

            // 返回运行时闭包
            return () =>
            {
                if (mode == GroupSelectionMode.RandomUniform)
                {
                    return stateWeights[Random.Next(0, stateWeights.Length)].Type;
                }

                // RandomWeighted 模式
                if (totalWeight <= 0)
                    return stateWeights[Random.Next(0, stateWeights.Length)].Type;

                int randomVal = Random.Next(0, totalWeight);
                int currentWeight = 0;
                for (int i = 0; i < stateWeights.Length; i++)
                {
                    currentWeight += stateWeights[i].Weight;
                    if (randomVal < currentWeight) return stateWeights[i].Type;
                }
                return stateWeights.Last().Type;
            };
        }

        // ==========================================
        // 2. 无参动态权重 (基于 Func<Type, int>)
        // ==========================================
        internal static Func<Type> CreateDynamicProvider(Type[] matchingStates, Func<Type, int> weightSelector, GroupSelectionMode mode)
        {
            if (matchingStates == null || matchingStates.Length == 0) return null;
            int[] weightCache = new int[matchingStates.Length]; // 闭包复用缓存

            return () =>
            {
                int totalWeight = 0, validCount = 0, highestWeight = -1;
                Type highestType = null;

                for (int i = 0; i < matchingStates.Length; i++)
                {
                    int w = System.Math.Max(0, weightSelector(matchingStates[i]));
                    weightCache[i] = w;
                    totalWeight += w;

                    if (w > highestWeight)
                    {
                        highestWeight = w;
                        highestType = matchingStates[i];
                    }
                    if (w > 0) validCount++;
                }

                return ExecuteSelection(matchingStates, weightCache, totalWeight, validCount, highestType, mode);
            };
        }
        // ==========================================
        // 3. 带泛型事件载荷的动态权重 (基于 Func<TEvent, Type, int>)
        // ==========================================
        internal static Func<TEvent, Type> CreateDynamicPayloadProvider<TEvent>(Type[] matchingStates, Func<TEvent, Type, int> weightSelector, GroupSelectionMode mode)
        {
            if (matchingStates == null || matchingStates.Length == 0) return null;
            int[] weightCache = new int[matchingStates.Length]; // 闭包复用缓存

            return (evtData) =>
            {
                int totalWeight = 0, validCount = 0, highestWeight = -1;
                Type highestType = null;

                for (int i = 0; i < matchingStates.Length; i++)
                {
                    int w = System.Math.Max(0, weightSelector(evtData, matchingStates[i]));
                    weightCache[i] = w;
                    totalWeight += w;

                    if (w > highestWeight)
                    {
                        highestWeight = w;
                        highestType = matchingStates[i];
                    }
                    if (w > 0) validCount++;
                }

                return ExecuteSelection(matchingStates, weightCache, totalWeight, validCount, highestType, mode);
            };
        }

        // 统一的路由选择器
        private static Type ExecuteSelection(Type[] states, int[] weightCache, int totalWeight, int validCount, Type highestType, GroupSelectionMode mode)
        {
            if (validCount == 0) return null;

            if (mode == GroupSelectionMode.HighestWeight) return highestType;

            if (mode == GroupSelectionMode.RandomUniform)
            {
                int targetIndex = Random.Next(0, validCount);
                int currentValid = 0;
                for (int i = 0; i < states.Length; i++)
                {
                    if (weightCache[i] > 0)
                    {
                        if (currentValid == targetIndex) return states[i];
                        currentValid++;
                    }
                }
            }
            else // RandomWeighted
            {
                int randomVal = Random.Next(0, totalWeight);
                int currentWeight = 0;
                for (int i = 0; i < states.Length; i++)
                {
                    if (weightCache[i] <= 0) continue;
                    currentWeight += weightCache[i];
                    if (randomVal < currentWeight) return states[i];
                }
            }
            return null;
        }
    }
    //!--- [Logger] ---
    public enum StateMachineLogLevel { None = 0, Error = 1, Warning = 2, Normal = 3, Verbose = 4 }
    public enum LogColor { White, Green, Cyan, LightBlue, Yellow, Orange, Red, Grey, Silver, Olive, Teal, Blue, Magenta, Purple, Brown, Black }
    /// <summary>日志输出</summary>
    public class StateMachineLogger<TEnum> where TEnum : Enum
    {
        /// <summary>状态机标签颜色（日志前缀）</summary>
        public LogColor TagColor { get; set; }
        /// <summary>状态机显示标签/名称</summary>
        public string LogTag { get; set; }
        /// <summary>日志级别</summary>
        public StateMachineLogLevel LogLevel { get; set; }

        // 用于点击日志定位 hierarchy 中物体
        UnityEngine.Object _context;

        public StateMachineLogger(string logTag, StateMachineLogLevel logLevel)
        {
            LogTag = logTag;
            LogLevel = logLevel;
        }
        /// <summary>
        /// 注入宿主实例用于日志点击后定位 (可选)
        /// </summary>
        public void SetLogContext(object context)
        {
            if (context != null && context is UnityEngine.Object obj) _context = obj;
        }
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public void LogVerbose(string message, LogColor? color = null, bool applyAll = false)
        {
            if (LogLevel >= StateMachineLogLevel.Verbose)
            {
                string colorStr = (color ?? TagColor).ToTagString();
                if (applyAll) UnityEngine.Debug.Log($"<b><color={colorStr}>[{LogTag}]</b> {message}</color>", _context);
                else UnityEngine.Debug.Log($"<b><color={colorStr}>[{LogTag}]</color></b> {message}", _context);
            }
        }
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public void LogInfo(string message, LogColor? color = null, bool applyAll = false)
        {
            if (LogLevel >= StateMachineLogLevel.Normal)
            {
                string colorStr = (color ?? TagColor).ToTagString();
                if (applyAll) UnityEngine.Debug.Log($"<b><color={colorStr}>[{LogTag}]</b> {message}</color>", _context);
                else UnityEngine.Debug.Log($"<b><color={colorStr}>[{LogTag}]</color></b> {message}", _context);
            }
        }
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public void LogWarn(string message, LogColor color = LogColor.Yellow)
        {
            if (LogLevel >= StateMachineLogLevel.Warning)
            {
                string colorStr = color.ToTagString();
                UnityEngine.Debug.LogWarning($"<b><color={colorStr}>[{LogTag}] {message}</color></b>", _context);
            }
        }
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public void LogErr(string message, Exception e = null)
        {
            if (LogLevel >= StateMachineLogLevel.Error)
            {
                UnityEngine.Debug.LogError($"<b><color=red>[{LogTag}] {message}</color></b>\n{e}", _context);
                if (e != null) UnityEngine.Debug.LogException(e, _context);
            }
        }
    }
    internal static class LogColorExtensions
    {
        /// <summary>
        /// 转换为 Unity RichText 标签支持的小写颜色字符串 (0 GC)
        /// </summary>
        public static string ToTagString(this LogColor color) => color switch
        {
            LogColor.White => "white",
            LogColor.Green => "green",
            LogColor.Cyan => "cyan",
            LogColor.LightBlue => "lightblue",
            LogColor.Yellow => "yellow",
            LogColor.Orange => "orange",
            LogColor.Red => "red",
            LogColor.Grey => "grey",
            LogColor.Silver => "silver",
            LogColor.Olive => "olive",
            LogColor.Teal => "teal",
            LogColor.Blue => "blue",
            LogColor.Magenta => "magenta",
            LogColor.Purple => "purple",
            LogColor.Brown => "brown",
            LogColor.Black => "black",
            _ => "white"
        };
    }
    //!--- [ZeroGC] ---

    /// <summary>
    /// 0 GC 强类型泛型比较器
    /// 自动处理未实现 IEquatable 的自定义 Struct，避免 ValueType.Equals 引起的装箱
    /// </summary>
    public static class ZeroGcEqualityComparer<T>
    {
        public static readonly Func<T, T, bool> EqualsFunc = CreateComparer();

        private static Func<T, T, bool> CreateComparer()
        {
            Type type = typeof(T);

            // 基本类型 (int, float)、枚举、以及实现了 IEquatable<T> 的类型 (含 Vector3, Quaternion 等)
            if (typeof(IEquatable<T>).IsAssignableFrom(type) || type.IsPrimitive || type.IsEnum)
            {
                var defaultComparer = EqualityComparer<T>.Default;
                return defaultComparer.Equals;
            }

            // 引用类型
            if (!type.IsValueType)
            {
                if (type == typeof(string))
                {
                    return (Func<T, T, bool>)(object)new Func<string, string, bool>(string.Equals);
                }
                return (a, b) => ReferenceEquals(a, b);
            }

            // 查找是否重载了 == 运算符 (如某些第三方数学库)
            MethodInfo opEquality = type.GetMethod("op_Equality",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { type, type },
                null);

            if (opEquality != null && opEquality.ReturnType == typeof(bool))
            {
                return (Func<T, T, bool>)Delegate.CreateDelegate(typeof(Func<T, T, bool>), opEquality);
            }

            // 对于未实现 IEquatable 的自定义 Struct，通过表达式树逐字段展开对比
            var paramA = Expression.Parameter(type, "a");
            var paramB = Expression.Parameter(type, "b");

            Expression body = BuildStructEqualityExpression(type, paramA, paramB);

            // 编译为 Func<T, T, bool>
            return Expression.Lambda<Func<T, T, bool>>(body, paramA, paramB).Compile();
        }

        // 递归构建字段对比（支持结构体嵌套结构体）
        private static Expression BuildStructEqualityExpression(Type type, Expression left, Expression right)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (fields.Length == 0) return Expression.Constant(true); // 空结构体

            Expression combinedExpression = null;

            foreach (var field in fields)
            {
                var fieldLeft = Expression.Field(left, field);
                var fieldRight = Expression.Field(right, field);
                Expression fieldEquality;

                Type fieldType = field.FieldType;

                // 如果内部字段是值类型且没有实现 IEquatable，递归展开
                if (fieldType.IsValueType && !fieldType.IsPrimitive && !fieldType.IsEnum && !typeof(IEquatable<>).MakeGenericType(fieldType).IsAssignableFrom(fieldType))
                {
                    fieldEquality = BuildStructEqualityExpression(fieldType, fieldLeft, fieldRight);
                }
                else
                {
                    var comparerType = typeof(EqualityComparer<>).MakeGenericType(fieldType);
                    var defaultComparerProp = comparerType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
                    var equalsMethod = comparerType.GetMethod("Equals", new[] { fieldType, fieldType });

                    fieldEquality = Expression.Call(
                        Expression.Property(null, defaultComparerProp),
                        equalsMethod,
                        fieldLeft, fieldRight
                    );
                }

                // 用 && 将所有字段拼起来: (a.F1 == b.F1) && (a.F2 == b.F2)
                combinedExpression = combinedExpression == null
                    ? fieldEquality
                    : Expression.AndAlso(combinedExpression, fieldEquality);
            }

            return combinedExpression;
        }
    }

    #endregion
}
