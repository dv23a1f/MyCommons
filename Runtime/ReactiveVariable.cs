using System;
using System.Collections.Generic;
using System.Threading;
using DX.Core.Utility;

namespace DX.Core.ReactiveVariable
{
    /// <summary>
    /// 强类型共享变量的读写接口（兼容单实例与 Live 动态代理）
    /// </summary>
    public interface IReactiveVar<T> : IReadOnlyReactiveVar<T>
    {
        bool IsDisposed { get; }
        /// <summary>当前值（支持读写）</summary>
        new T Value { get; set; }
        /// <summary>修改值并强制广播通知</summary>
        void SetValueAndForceNotify(T value);
        /// <summary>修改值且不触发广播通知</summary>
        void SetValueWithoutNotify(T value);

        /// <summary>
        /// 将当前值重置为 C# 类型默认值 default(T) (数值为0, 引用类型为null)
        /// </summary>
        void ResetToDefault(bool notify = true);
        /// <summary>在不改变当前值的情况下，强制向总线和监听者重新广播通知一次</summary>
        void ForceNotify();
    }

    /// <summary>
    /// 强类型共享变量的只读视图接口，仅提供读取与订阅能力
    /// </summary>
    public interface IReadOnlyReactiveVar<out T>
    {
        /// <summary>当前值（只读）</summary>
        T Value { get; }

        /// <summary> 订阅最新值 </summary>
        IDisposable Subscribe(Action<T> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default);
    }

    /// <summary>
    /// 通用响应式变量基类
    /// </summary>

    public class ReactiveVar<T> : IReactiveVar<T>, IDisposable
    {
        protected T _value;
        protected bool _isDisposed;

        private Action<T> _onValueChangedListener;
        private List<CancellationTokenRegistration> _tokenRegistrations;

        /// <summary>值发生改变时的本地回调 (旧值, 新值)</summary>
        [Obsolete("请改用 .Subscribe() 进行更安全的订阅")]
        public event Action<T, T> OnValueChanged;

        public bool IsDisposed => _isDisposed;

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
        public ReactiveVar() { }
        public ReactiveVar(T initialValue)
        {
            _value = initialValue;
        }

        protected virtual void NotifyValueChanged(T oldValue, T newValue)
        {
            OnValueChanged?.Invoke(oldValue, newValue);
            _onValueChangedListener?.Invoke(newValue);
        }

        public IDisposable Subscribe(Action<T> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null || _isDisposed || ct.IsCancellationRequested)
                return new ActionDisposable(null);

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

            return new ActionDisposable(() =>
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
            _isDisposed = true;
        }

        public static implicit operator T(ReactiveVar<T> rv) => rv != null ? rv.Value : default;
    }

    //!==============
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
        public static IReadOnlyReactiveVar<TResult> Select<TSource, TResult>(
            this IReadOnlyReactiveVar<TSource> source,
            Func<TSource, TResult> selector,
            bool distinctUntilChanged = true)
        {
            return new SelectReadOnlySharedVar<TSource, TResult>(source, selector, distinctUntilChanged);
        }

    }

    /// <summary>
    /// 响应式数据映射只读视图 (Select Projection)
    /// </summary>
    public sealed class SelectReadOnlySharedVar<TSource, TResult> : IReadOnlyReactiveVar<TResult>
    {
        private readonly IReadOnlyReactiveVar<TSource> _source;
        private readonly Func<TSource, TResult> _selector;
        private readonly bool _distinctUntilChanged;

        public SelectReadOnlySharedVar(IReadOnlyReactiveVar<TSource> source, Func<TSource, TResult> selector, bool distinctUntilChanged = true)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _selector = selector ?? throw new ArgumentNullException(nameof(selector));
            _distinctUntilChanged = distinctUntilChanged;
        }

        /// <summary>当前计算后的映射值</summary>
        public TResult Value => _selector(_source.Value);

        /// <summary>
        /// 订阅映射后的数据变更
        /// </summary>
        public IDisposable Subscribe(Action<TResult> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new ActionDisposable(null);
            if (ct.IsCancellationRequested) return new ActionDisposable(null);

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

    //# Where 操作符扩展
    public static class SharedVarWhereExtensions
    {
        /// <summary>
        /// 对共享变量进行条件过滤（仅当满足 predicate 条件时才下发广播与通知）
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="source">源变量</param>
        /// <param name="predicate">过滤条件谓词 (返回 true 时放行通知)</param>
        public static IReadOnlyReactiveVar<T> Where<T>(
            this IReadOnlyReactiveVar<T> source,
            Func<T, bool> predicate)
        {
            return new WhereReadOnlySharedVar<T>(source, predicate);
        }

    }
    /// <summary>
    /// 响应式数据条件过滤只读视图 (Where Filter)
    /// </summary>
    public sealed class WhereReadOnlySharedVar<T> : IReadOnlyReactiveVar<T>
    {
        private readonly IReadOnlyReactiveVar<T> _source;
        private readonly Func<T, bool> _predicate;
        private T _lastValidValue;
        private bool _hasValidValue;

        public WhereReadOnlySharedVar(IReadOnlyReactiveVar<T> source, Func<T, bool> predicate)
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

        /// <summary>
        /// 订阅数据变更（仅当数据满足过滤条件时才会收到通知）
        /// </summary>
        public IDisposable Subscribe(Action<T> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new ActionDisposable(null);
            if (ct.IsCancellationRequested) return new ActionDisposable(null);

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
        public static IReadOnlyReactiveVar<TResult> Combine<T1, T2, TResult>(
            this IReadOnlyReactiveVar<T1> source1,
            IReadOnlyReactiveVar<T2> source2,
            Func<T1, T2, TResult> combiner,
            bool distinctUntilChanged = true)
        {
            return new CombineReadOnlySharedVar<T1, T2, TResult>(source1, source2, combiner, distinctUntilChanged);
        }

    }

    /// <summary>
    /// 双变量响应式组合只读视图 (CombineLatest 模式)
    /// </summary>
    public sealed class CombineReadOnlySharedVar<T1, T2, TResult> : IReadOnlyReactiveVar<TResult>
    {
        private readonly IReadOnlyReactiveVar<T1> _source1;
        private readonly IReadOnlyReactiveVar<T2> _source2;
        private readonly Func<T1, T2, TResult> _combiner;
        private readonly bool _distinctUntilChanged;

        public CombineReadOnlySharedVar(
            IReadOnlyReactiveVar<T1> source1,
            IReadOnlyReactiveVar<T2> source2,
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

        /// <summary>
        /// 订阅组合后的数据变化
        /// </summary>
        public IDisposable Subscribe(Action<TResult> onValueChanged, bool notifyImmediately = true, CancellationToken ct = default)
        {
            if (onValueChanged == null) return new ActionDisposable(null);
            if (ct.IsCancellationRequested) return new ActionDisposable(null);

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
            return new ActionDisposable(() =>
            {
                sub1.Dispose();
                sub2.Dispose();
            });
        }
    }
}