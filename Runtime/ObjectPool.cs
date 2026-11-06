using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Utility.Pooling
{
    /// <summary>
    /// 对象池对外暴露的核心 API 接口（不包含任何构建阶段的配置方法）
    /// </summary>
    public interface IObjectPool<T> : IObjectPoolReleaser<T>, IDisposable where T : Component
    {
        /// <summary> 从池中获取一个对象 </summary>
        T Get();

        /// <summary> 回收当前所有处于激活/使用中的对象回池内 </summary>
        void ReleaseAll();

        /// <summary> 彻底销毁池中当前所有闲置的物体，释放物理内存 </summary>
        void Clear();

        /// <summary> 
        /// 当前所有在外面处于激活/使用中的实体集合（只读）
        /// </summary>
        IReadOnlyCollection<T> ActiveElements { get; }

        /// <summary> 当前在外面活跃使用的数量 </summary>
        int CountActive { get; }

        /// <summary> 当前在池内休眠闲置的数量 </summary>
        int CountInactive { get; }

        /// <summary> 总托管数量 </summary>
        int CountAll { get; }

        bool IsDisposed { get; }
    }

    /// <summary>
    /// 仅供池化物体自己将自己归还用
    /// </summary>
    public interface IObjectPoolReleaser<T> where T : Component
    {
        /// <summary> 仅提供归还物体的能力 </summary>
        void Release(T element);
    }
    /// <summary>
    /// 对象池实现类
    /// </summary>
    public class ObjectPool<T> : IObjectPool<T> where T : Component
    {

        private Transform _rootTransform;
        private bool _ownsRoot;
        private readonly int _maxSize;

        // 生命周期回调
        private Action<T, IObjectPoolReleaser<T>> _onCreate;
        private Action<T> _onGet;
        private Action<T> _onRelease;
        private Action<T> _onDestroy;

        private Func<T> _factory;

        private readonly Stack<T> _inactivePool;
        private readonly HashSet<T> _activePool;
        private readonly List<T> _tempBuffer = new List<T>();

        private CancellationTokenRegistration _ctr;
        private bool _isDisposed;
        public bool IsDisposed => _isDisposed;

        public IReadOnlyCollection<T> ActiveElements => _activePool;

        public int CountActive => _activePool.Count;
        public int CountInactive => _inactivePool.Count;
        public int CountAll => CountActive + CountInactive;

        internal ObjectPool(
            Func<T> factory,
            int initialCapacity,
            int maxSize,
            Transform rootTransform,
            Action<T, IObjectPoolReleaser<T>> onCreate,
            Action<T> onGet,
            Action<T> onRelease,
            Action<T> onDestroy,
            CancellationToken cancellationToken)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _maxSize = maxSize;
            _onCreate = onCreate;
            _onGet = onGet;
            _onRelease = onRelease;
            _onDestroy = onDestroy;

            _rootTransform = rootTransform;

            if (IsTargetAlive(_rootTransform) == false)
            {
                _rootTransform = CreateRootObject();
                _ownsRoot = true;
            }

            _inactivePool = new Stack<T>(initialCapacity);
            _activePool = new HashSet<T>();

            // 直接创建入池，触发 OnCreate 但不触发 OnGet
            for (int i = 0; i < initialCapacity; i++)
            {
                _inactivePool.Push(CreateNewInstance());
            }

            if (cancellationToken.CanBeCanceled)
            {
                _ctr = cancellationToken.Register(Dispose);
            }
        }

        public static ObjectPoolBuilder<T> Create(T prefab) => new(prefab);
        public static ObjectPoolBuilder<T> Create(Func<T> factory) => new(factory);

        public T Get()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ObjectPool<T>), "ObjectPool has already been disposed.");

            T element = null;

            // 防御外部意外 Object.Destroy
            while (_inactivePool.Count > 0)
            {
                element = _inactivePool.Pop();
                if (IsTargetAlive(element)) break;
            }

            if (element == null)
            {
                element = CreateNewInstance();
            }

            _activePool.Add(element);

            // 激活并触发业务获取回调
            element.gameObject.SetActive(true);
            _onGet?.Invoke(element);

            return element;
        }

        public void Release(T element)
        {
            if (_isDisposed)
            {
                DestroyInternal(element);
                return;
            }

            if (IsTargetAlive(element) == false) return;

            if (!_activePool.Remove(element))
            {
                Debug.LogWarning($"[ObjectPool] 试图释放一个不属于此对象池或已被释放的对象: {element.name}");
                return;
            }

            // 超过上限则直接销毁
            if (_inactivePool.Count >= _maxSize)
            {
                DestroyInternal(element);
                return;
            }

            _onRelease?.Invoke(element);
            element.gameObject.SetActive(false);

            element.transform.SetParent(GetValidRoot(), false);

            _inactivePool.Push(element);
        }

        public void ReleaseAll()
        {
            if (_isDisposed || _activePool.Count == 0) return;

            _tempBuffer.Clear();
            _tempBuffer.AddRange(_activePool);

            for (int i = 0; i < _tempBuffer.Count; i++)
            {
                Release(_tempBuffer[i]);
            }

            _tempBuffer.Clear();
        }

        public void Clear()
        {
            while (_inactivePool.Count > 0)
            {
                var item = _inactivePool.Pop();
                DestroyInternal(item);
            }

            if (_activePool.Count == 0 && _ownsRoot && _rootTransform != null)
            {
                Object.Destroy(_rootTransform.gameObject);
            }
        }

        private Transform GetValidRoot()
        {
            if (!IsTargetAlive(_rootTransform))
            {
                // 发现被外部误删了，自建一个新 Root 接盘
                _rootTransform = CreateRootObject();
                _ownsRoot = true;
                Debug.LogWarning("[ObjectPool] 根节点被销毁，尝试新建");
            }
            return _rootTransform;
        }

        Transform CreateRootObject()
        {
            var autoRoot = new GameObject($"[Pool] {typeof(T).Name}");

            return autoRoot.transform;
        }

        private T CreateNewInstance()
        {
            T instance = _factory.Invoke();

            // 被其他脚本意外销毁的情况
            if (instance is MonoBehaviour mb)
            {
                mb.destroyCancellationToken.Register(() =>
                {
                    // 1. 如果池子已经销毁，无需处理
                    if (_isDisposed) return;
                    // 2. 仅将其从激活池中静默移除
                    _activePool.Remove(instance);
                });
            }

            instance.gameObject.SetActive(false);

            instance.transform.SetParent(GetValidRoot(), false);

            // 仅在对象诞生时注入一次 Releaser
            _onCreate?.Invoke(instance, this);

            return instance;
        }

        private void DestroyInternal(T element)
        {
            if (IsTargetAlive(element) == false) return;

            _onDestroy?.Invoke(element);
            Object.Destroy(element.gameObject);
        }

        private static bool IsTargetAlive(object target)
        {
            if (target == null) return false;

            if (target is UnityEngine.Object unityObj)
            {
                return unityObj != null;
            }

            return true;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _ctr.Dispose();

            Clear();

            foreach (var item in _activePool)
            {
                DestroyInternal(item);
            }
            _activePool.Clear();

            // 仅销毁内部自动生成的根节点
            if (_ownsRoot && IsTargetAlive(_rootTransform))
            {
                Object.Destroy(_rootTransform.gameObject);
            }
            // 清理所有委托引用
            _factory = null;
            _onCreate = null;
            _onGet = null;
            _onRelease = null;
            _onDestroy = null;
        }

    }

    /// <summary>
    /// 对象池建造者
    /// </summary>
    public class ObjectPoolBuilder<T> where T : Component
    {
        private readonly T _prefab;
        private Func<T> _customFactory;
        private Transform _parent;

        private int _initialSize = 0;
        private int _maxSize = 100;

        private Action<T, IObjectPoolReleaser<T>> _onCreate;
        private Action<T> _onGet;
        private Action<T> _onRelease;
        private Action<T> _onDestroy;
        private CancellationToken _cancellationToken = CancellationToken.None;

        // 构造方式 1：标准 Prefab
        public ObjectPoolBuilder(T prefab)
        {
            _prefab = prefab != null ? prefab : throw new ArgumentNullException(nameof(prefab));
            _customFactory = () => Object.Instantiate(_prefab);
        }

        // 构造方式 2：无 Prefab，纯工厂模式
        public ObjectPoolBuilder(Func<T> factory)
        {
            _customFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public ObjectPoolBuilder<T> SetParent(Transform parent)
        {
            _parent = parent;
            return this;
        }

        public ObjectPoolBuilder<T> SetFactory(Func<T> factory)
        {
            _customFactory = factory;
            return this;
        }

        public ObjectPoolBuilder<T> SetCapacity(int initialSize, int maxSize = 100)
        {
            _initialSize = Mathf.Max(0, initialSize);
            _maxSize = Mathf.Max(initialSize, maxSize);
            return this;
        }

        public ObjectPoolBuilder<T> SetOnCreate(Action<T> onCreate)
        {
            if (onCreate == null) return this;
            _onCreate = (item, _) => onCreate(item);
            return this;
        }
        public ObjectPoolBuilder<T> SetOnCreate(Action<T, IObjectPoolReleaser<T>> onCreate)
        {
            _onCreate = onCreate;
            return this;
        }
        public ObjectPoolBuilder<T> SetOnGet(Action<T> onGet)
        {
            _onGet = onGet;
            return this;
        }

        public ObjectPoolBuilder<T> SetOnRelease(Action<T> onRelease)
        {
            _onRelease = onRelease;
            return this;
        }

        public ObjectPoolBuilder<T> SetOnDestroy(Action<T> onDestroy)
        {
            _onDestroy = onDestroy;
            return this;
        }

        public ObjectPoolBuilder<T> DisposeWith(CancellationToken token)
        {
            _cancellationToken = token;
            return this;
        }

        public IObjectPool<T> Build()
        {
            return new ObjectPool<T>(
                _customFactory,
                _initialSize,
                _maxSize,
                _parent,

                _onCreate,
                _onGet,
                _onRelease,
                _onDestroy,
                _cancellationToken
            );
        }
    }

}