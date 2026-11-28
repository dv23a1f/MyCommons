using System;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using System.Runtime.CompilerServices;

#region InterfaceScanner

namespace DX.Core.Utility
{
    public static class InterfaceScanner
    {
        public static List<T> FindAll<T>(bool includeInactive = false) where T : class
        {
            // 确保传入的是接口类型
            if (!typeof(T).IsInterface)
            {
                Debug.LogError($"{typeof(T).Name} 不是一个接口！");
                return null;
            }

            List<T> results = new List<T>();
            Scene activeScene = SceneManager.GetActiveScene();
            GameObject[] rootObjects = activeScene.GetRootGameObjects();

            foreach (GameObject root in rootObjects)
            {
                // GetComponentsInChildren 支持接口类型
                T[] components = root.GetComponentsInChildren<T>(includeInactive);
                results.AddRange(components);
            }

            return results;
        }
    }
    public class InterfaceFinder
    {
        private readonly List<Action<MonoBehaviour>> _collectors = new List<Action<MonoBehaviour>>();

        // 私有化构造函数，强制通过静态方法创建
        private InterfaceFinder() { }

        /// <summary>
        /// 入口：开始构建扫描器
        /// </summary>
        public static InterfaceFinder Begin()
        {
            return new InterfaceFinder();
        }

        /// <summary>
        /// 注册需要查找的目标接口和接收的 List
        /// </summary>
        public InterfaceFinder Find<T>(List<T> destinationList, bool clearListBeforeScan = true) where T : class
        {
            if (destinationList == null)
                throw new ArgumentNullException(nameof(destinationList));

            if (clearListBeforeScan)
            {
                destinationList.Clear();
            }

            // 注册一个收集器委托：如果 MonoBehaviour 实现了接口 T，则存入对应的 list
            _collectors.Add(mb =>
            {
                if (mb is T match)
                {
                    destinationList.Add(match);
                }
            });

            return this; // 返回自身以支持链式调用
        }

        /// <summary>
        /// 执行扫描：只遍历一次场景，完成所有列表的填充
        /// </summary>
        public void Execute(bool include = true)
        {
            if (_collectors.Count == 0) return;

            // 获取当前活动场景的所有根物体
            Scene activeScene = SceneManager.GetActiveScene();
            GameObject[] rootObjects = activeScene.GetRootGameObjects();

            // 缓存一个内部 List，复用内存以消除 GetComponentsInChildren 的 GC 垃圾
            List<MonoBehaviour> mbBuffer = new List<MonoBehaviour>(128);

            // --- 核心：单次场景遍历 ---
            foreach (GameObject root in rootObjects)
            {
                mbBuffer.Clear();
                // 无 GC 的重载方法
                root.GetComponentsInChildren(include, mbBuffer);

                int compCount = mbBuffer.Count;
                int collectorCount = _collectors.Count;

                for (int i = 0; i < compCount; i++)
                {
                    MonoBehaviour mb = mbBuffer[i];
                    // 防御：物体上可能存在丢失的脚本（Missing Script）
                    if (mb == null) continue;

                    // 对每一个扫描到的组件，检测是否符合已注册的各个目标接口
                    for (int j = 0; j < collectorCount; j++)
                    {
                        _collectors[j](mb);
                    }
                }
            }
        }
    }
}

#endregion

#region SmoothDamp

namespace DX.Core.Utility
{
    [Serializable]
    public class SmoothDampVector3
    {
        [Tooltip("平滑时间，值越小越快到达目标")]
        public float smoothTime = 0.2f;

        [Tooltip("最大移动速度")]
        public float maxSpeed = Mathf.Infinity;

        // 内部状态
        public Vector3 CurrentValue { get; set; }
        public Vector3 Velocity { get; private set; }

        /// <summary>
        /// 构造函数：初始化配置
        /// </summary>
        public SmoothDampVector3(float smoothTime = 0.2f, Vector3 initialValue = default, float maxSpeed = Mathf.Infinity)
        {
            this.smoothTime = smoothTime;
            this.CurrentValue = initialValue;
            this.maxSpeed = maxSpeed;
            this.Velocity = Vector3.zero;
        }

        /// <summary>
        /// 核心调用：只需输入目标值，返回计算后的平滑值
        /// </summary>
        public Vector3 Update(Vector3 target, float? customDeltaTime = null)
        {
            float dt = customDeltaTime ?? Time.deltaTime;

            Vector3 currentVelocity = Velocity;
            CurrentValue = Vector3.SmoothDamp(CurrentValue, target, ref currentVelocity, smoothTime, maxSpeed, dt);
            Velocity = currentVelocity;

            return CurrentValue;
        }

        /// <summary>
        /// 重置/瞬移到某个位置（清除当前速度，避免回弹）
        /// </summary>
        public void Reset(Vector3 value)
        {
            CurrentValue = value;
            Velocity = Vector3.zero;
        }
    }
}
#endregion

#region Timer

namespace DX.Core.Utility
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using UnityEngine;

    /// <summary>
    /// 通用计时器：延迟执行 / 重复执行，支持暂停、恢复、取消，可选忽略 Time.timeScale。
    /// 无需手动挂载，首次使用时自动创建驱动对象。
    /// </summary>
    public sealed class Timer
    {
        // ---------- 对外 API ----------

        /// <summary>延迟 seconds 秒后执行一次。</summary>
        public static Timer Delay(float seconds, Action onComplete, bool unscaled = false,
                                  CancellationToken token = default)
            => Create(seconds, 1, null, onComplete, unscaled, token);

        /// <summary>每隔 interval 秒执行一次；count &lt;= 0 表示无限循环。</summary>
        public static Timer Repeat(float interval, Action onTick, int count = -1,
                                   Action onComplete = null, bool unscaled = false,
                                   CancellationToken token = default)
            => Create(interval, count, onTick, onComplete, unscaled, token);

        public bool IsDone { get; private set; }
        public bool IsPaused { get; private set; }
        /// <summary>当前这一轮的进度 0~1。</summary>
        public float Progress => Mathf.Clamp01(elapsed / interval);

        public void Pause() => IsPaused = true;
        public void Resume() => IsPaused = false;
        public void Cancel() => IsDone = true;   // 取消，不会触发 onComplete

        // ---------- 实现 ----------

        readonly float interval;
        readonly int repeat;
        readonly bool unscaled;
        readonly Action onTick, onComplete;
        readonly CancellationToken token;
        float elapsed;
        int count;

        Timer(float interval, int repeat, Action onTick, Action onComplete, bool unscaled, CancellationToken token)
        {
            this.token = token;
            this.interval = Mathf.Max(interval, 0.0001f);
            this.repeat = repeat;
            this.onTick = onTick;
            this.onComplete = onComplete;
            this.unscaled = unscaled;
        }

        static Timer Create(float interval, int repeat, Action onTick, Action onComplete, bool unscaled, CancellationToken token)
        {
            if (driver == null)
            {
                var go = new GameObject("[Timer]") { hideFlags = HideFlags.HideInHierarchy };
                UnityEngine.Object.DontDestroyOnLoad(go);
                driver = go.AddComponent<Driver>();
            }
            var t = new Timer(interval, repeat, onTick, onComplete, unscaled, token);
            pending.Add(t);
            return t;
        }

        void Update(float dt)
        {
            if (IsDone) return;
            if (token.IsCancellationRequested) { IsDone = true; return; }  // 外部令牌取消，不触发 onComplete
            if (IsPaused) return;
            elapsed += dt;
            while (elapsed >= interval && !IsDone)
            {
                elapsed -= interval;
                onTick?.Invoke();
                if (IsDone || token.IsCancellationRequested) break;  // 回调里可能已取消
                if (repeat > 0 && ++count >= repeat)
                {
                    IsDone = true;
                    onComplete?.Invoke();
                }
            }
        }

        static readonly List<Timer> timers = new List<Timer>();
        static readonly List<Timer> pending = new List<Timer>();  // 回调中新建的计时器，下一帧生效
        static Driver driver;

        sealed class Driver : MonoBehaviour
        {
            // 退出播放模式 / 退出应用时：清理静态状态并销毁自身（兼容关闭域重载）
            void Awake() => Application.quitting += OnQuit;

            void OnQuit()
            {
                Application.quitting -= OnQuit;
                timers.Clear(); pending.Clear(); driver = null;
                Destroy(gameObject);
            }

            void Update()
            {
                if (pending.Count > 0) { timers.AddRange(pending); pending.Clear(); }
                float dt = Time.deltaTime, udt = Time.unscaledDeltaTime;
                for (int i = 0; i < timers.Count; i++)
                    timers[i].Update(timers[i].unscaled ? udt : dt);
                timers.RemoveAll(t => t.IsDone);
            }
        }
    }
}

namespace DX.Core.Utility
{

    using UnityEngine;

    [System.Serializable]
    public class Cooldown
    {
        [Tooltip("冷却总时长（秒）")]
        public float duration = 1.0f;

        private float nextReadyTime = 0f;
        private float currentDuration = 0f;

        public Cooldown() { }
        public Cooldown(float duration)
        {
            this.duration = duration;
        }

        /// <summary>
        /// 冷却是否已就绪
        /// </summary>
        public bool IsReady => Time.time >= nextReadyTime;

        /// <summary>
        /// 剩余冷却时间（秒）
        /// </summary>
        public float RemainingTime => Mathf.Max(0f, nextReadyTime - Time.time);

        /// <summary>
        /// 剩余进度（1 -> 0）：刚触发时为 1，冷却结束时为 0
        /// （最适合直接赋值给技能图标遮罩的 Image.fillAmount）
        /// </summary>
        public float RemainingProgress
        {
            get
            {
                if (IsReady || currentDuration <= 0f) return 0f;
                return Mathf.Clamp01((nextReadyTime - Time.time) / currentDuration);
            }
        }

        /// <summary>
        /// 完成进度（0 -> 1）：刚触发时为 0，冷却结束时为 1
        /// （适合用于充能条、Slider 进度条）
        /// </summary>
        public float CompletionProgress => 1f - RemainingProgress;

        /// <summary>
        /// 开始进入冷却
        /// </summary>
        public void Start()
        {
            currentDuration = Mathf.Max(0f, duration);
            nextReadyTime = Time.time + currentDuration;
        }

        /// <summary>
        /// 立即结束冷却（例如吃了刷新技能CD的道具）
        /// </summary>
        public void Reset()
        {
            nextReadyTime = 0f;
        }
    }
}
#endregion

namespace DX.Core.Utility
{
    /// <summary>
    /// 简易的标签系统
    /// </summary>
    [Serializable]
    public class TagContainer<TTag>
    {
        private readonly Dictionary<TTag, int> _tagCounts;

#if UNITY_EDITOR
        [SerializeField]
        private List<TTag> _debugActiveTags = new List<TTag>();
#endif

        public TagContainer(IEqualityComparer<TTag> comparer = null)
        {
            _tagCounts = new Dictionary<TTag, int>(comparer);
        }
        /// <summary>
        /// 当标签成功添加时触发（计数 0->1）
        /// </summary>
        public event Action<TTag> OnTagAdded;

        /// <summary>
        /// 当标签成功移除时触发（计数 1->0）
        /// </summary>
        public event Action<TTag> OnTagRemoved;

        /// <summary>
        /// 设定一个标签，无论是否存在都将计数设置为 1（用于不可重复添加的场景）
        /// </summary>
        /// <param name="tag"></param>
        public void SetTag(TTag tag)
        {
            if (_tagCounts.TryGetValue(tag, out int count))
            {
                if (count != 1) _tagCounts[tag] = 1;
            }
            else
            {
                _tagCounts[tag] = 1;
#if UNITY_EDITOR
                _debugActiveTags.Add(tag);
#endif
                OnTagAdded?.Invoke(tag);
            }
        }

        /// <summary>
        /// 添加一个标签，如存在则计数 +1 （适用于碰撞体 isGround 检查，或多个脚本添加同一个标签的场景）
        /// </summary>
        public void AddTag(TTag tag)
        {
            if (_tagCounts.TryGetValue(tag, out int count))
            {
                _tagCounts[tag] = count + 1;
            }
            else
            {
                _tagCounts[tag] = 1;
#if UNITY_EDITOR
                _debugActiveTags.Add(tag);
#endif
                OnTagAdded?.Invoke(tag);
            }
        }

        /// <summary>
        /// 当计数为 0 则删除该标签
        /// </summary>
        public void RemoveTag(TTag tag)
        {
            if (!_tagCounts.TryGetValue(tag, out int count)) return;

            if (count > 1)
            {
                _tagCounts[tag] = count - 1;
            }
            else
            {
                _tagCounts.Remove(tag);
#if UNITY_EDITOR
                _debugActiveTags.Remove(tag);
#endif
                OnTagRemoved?.Invoke(tag);
            }
        }

        /// <summary>
        /// 查询指定标签的当前计数，不存在则返回0
        /// </summary>
        public int Count(TTag tag) => _tagCounts.GetValueOrDefault(tag, 0);

        /// <summary>
        /// 是否包含指定标签
        /// </summary>
        public bool HasTag(TTag tag) => _tagCounts.ContainsKey(tag);

        /// <summary>
        /// 是否包含任意一个标签
        /// </summary>
        public bool HasAnyTag(params TTag[] tags)
        {
            for (int i = 0; i < tags.Length; i++)
            {
                if (_tagCounts.ContainsKey(tags[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// 是否包含所有标签
        /// </summary>
        public bool HasAllTag(params TTag[] tags)
        {
            for (int i = 0; i < tags.Length; i++)
            {
                if (!_tagCounts.ContainsKey(tags[i])) return false;
            }
            return true;
        }

        public void Clear()
        {
            _tagCounts.Clear();
#if UNITY_EDITOR
            _debugActiveTags.Clear();
#endif
        }
    }
}