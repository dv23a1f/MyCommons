using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Pool;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

#if !NET5_0_OR_GREATER

namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}

#endif
namespace DX.Core.Extensions
{
    public static class UnityObjectExtensions
    {
        /// <summary>
        /// 如果 Unity 对象已被销毁（伪 null），则返回真正的 C# null。
        /// 可以安全地与 ?. 操作符配合使用。
        /// </summary>
        public static T AsNull<T>(this T obj) where T : UnityEngine.Object
        {
            // 这里会触发 Unity 重载的 == 操作符判断
            // 如果底层 C++ 对象已被销毁，obj == null 返回 true，直接返回真正的 null
            return obj == null ? null : obj;
        }
    }
    public static class PrefabExtensions
    {
        /// <summary>
        /// 判断该物体/组件是否作为实例存在于当前场景中 (true: 存在于场景而非 Prefab)
        /// </summary>
        public static bool IsInScene(this GameObject go)
        {
            return go != null && go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.name);
        }

        /// <summary>
        /// 判断该物体/组件是否作为实例存在于当前场景中 (true: 存在于场景而非 Prefab)
        /// </summary>
        public static bool IsInScene(this Component component)
        {
            return component != null && component.gameObject.IsInScene();
        }
    }
    public static class ListExtensions
    {
        /// <summary>
        /// 获取指定元素在只读列表中的索引
        /// </summary>
        public static int IndexOf<T>(this IReadOnlyList<T> list, T item)
        {
            if (list == null) return -1;

            if (list is IList<T> concreteList)
            {
                return concreteList.IndexOf(item);
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], item))
                {
                    return i;
                }
            }

            return -1;
        }
        //!------------------------------------------------------
        /// <summary>
        /// 检查列表或数组是否为null或空
        /// </summary>
        /// <returns>为空时返回true</returns>
        public static bool IsNullOrEmpty<T>(this IEnumerable<T> source)
        {
            if (source == null)
                return true;

            // 1. 检查是否为 ICollection<T> (List<T>, T[], IList<T> 都会命中这里)
            // 这是最常见的情况，性能 O(1)
            if (source is ICollection<T> collection)
            {
                return collection.Count == 0;
            }

            // 2. 检查是否为 IReadOnlyCollection<T> (IReadOnlyList<T> 会命中这里)
            // 性能 O(1)
            if (source is IReadOnlyCollection<T> readOnlyCollection)
            {
                return readOnlyCollection.Count == 0;
            }

            // 3. 如果都不是 (比如是 Linq 查询结果或者自定义迭代器)
            // 使用 GetEnumerator 检查是否有元素，避免遍历整个列表
            // 注意：不要直接用 .Count()，因为对于非集合类型那是 O(N)
            using (var enumerator = source.GetEnumerator()) // using类似try...finally 并在finally执行.Dispose();
            {
                return !enumerator.MoveNext();
            }
        }
        // 空安全的索引检查
        public static bool IsValidIndex<T>(this IList<T> list, int index)
        {
            // 现代 C# 推荐使用 is null 检查，比 == null 更安全（无视运算符重载）
            if (list is null) return false;

            return (uint)index < (uint)list.Count;
        }
        //!------------------------------------------------------
        public static void Shuffle<T>(this IList<T> source)
        {
            for (int i = source.Count - 1; i > 0; i--)
            {

                var j = UnityEngine.Random.Range(0, i + 1);
                (source[i], source[j]) = (source[j], source[i]);
            }
        }

        /// <summary>
        /// 按照权重对List进行洗牌 (加权不放回采样)
        /// 权重越大，排在前面的概率越高。
        /// 算法复杂度: O(N log N)
        /// </summary>
        /// <typeparam name="T">列表元素类型</typeparam>
        /// <param name="list">目标列表</param>
        /// <param name="weightSelector">获取权重的委托 (返回值必须大于0)</param>
        public static void WeightedShuffle<T>(this IList<T> list, Func<T, float> weightSelector)
        {
            if (list == null || list.Count <= 1) return;

            // 创建一个临时列表来存储 (原始元素, 随机分数)
            // 使用 Efraimidis-Spirakis 算法: Key = random ^ (1 / weight)
            var weightedItems = new List<KeyValuePair<T, float>>(list.Count);

            for (int i = 0; i < list.Count; i++)
            {
                T item = list[i];
                float weight = weightSelector(item);

                // 防止权重 <= 0 导致数学错误 (除以0或负数幂)
                // 如果权重为0，给一个极小值，确保它排在最后
                if (weight <= 0) weight = 0.00001f;

                // UnityEngine.Random.value 返回 [0.0, 1.0]
                // 为了避免 log(0) 或 pow(0, ...) 的极端边界，可以做一个微小的偏移或重新随机
                float r = UnityEngine.Random.value;
                if (r == 0) r = 0.00001f;

                // 计算分数
                float score = Mathf.Pow(r, 1f / weight);

                weightedItems.Add(new KeyValuePair<T, float>(item, score));
            }

            // 根据分数降序排列 (分数越大，越靠前)
            // 使用 List<T>.Sort 进行排序，比较器比较 Value
            weightedItems.Sort((a, b) => b.Value.CompareTo(a.Value));

            // 将排序后的结果写回原列表
            for (int i = 0; i < list.Count; i++)
            {
                list[i] = weightedItems[i].Key;
            }
        }
        //!------------------------------------------------------
        /// <summary>
        /// 从列表中删除包含在指定集合中的所有元素(0GC)
        /// </summary>
        public static void RemoveElements<T>(this List<T> list, IEnumerable<T> elementsToRemove)
        {
            if (list == null || elementsToRemove == null || list.Count == 0) return;

            // 从 Unity 的对象池中借用 HashSet
            using (HashSetPool<T>.Get(out HashSet<T> pooledSet))
            {
                foreach (var item in elementsToRemove)
                {
                    pooledSet.Add(item);
                }

                int writeIndex = 0;
                for (int readIndex = 0; readIndex < list.Count; readIndex++)
                {
                    T currentItem = list[readIndex];

                    if (!pooledSet.Contains(currentItem))
                    {
                        list[writeIndex] = currentItem;
                        writeIndex++;
                    }
                }

                int removeCount = list.Count - writeIndex;
                if (removeCount > 0)
                {
                    list.RemoveRange(writeIndex, removeCount);
                }
            }
        }
    }

    //# List取随机
    public static class ListRandomExtensions
    {
        //@ 1. 等概率随机 (Uniform Random)

        /// <summary>
        /// 等概率获取 1 个随机元素
        /// </summary>
        public static T GetRandom<T>(this IReadOnlyList<T> list)
        {
            if (list == null || list.Count == 0) return default;
            return list[UnityEngine.Random.Range(0, list.Count)];
        }

        /// <summary>
        /// 等概率获取多个不重复的元素（不放回抽样）
        /// </summary>
        public static List<T> GetRandomUnique<T>(this IReadOnlyList<T> list, int count)
        {
            if (list == null || list.Count == 0 || count <= 0) return new List<T>(0);

            int total = list.Count;
            int actualCount = Mathf.Min(count, total);
            List<T> result = new List<T>(actualCount);

            // 局部 Fisher-Yates 算法
            int[] indices = new int[total];
            for (int i = 0; i < total; i++) indices[i] = i;

            for (int i = 0; i < actualCount; i++)
            {
                int randomIndex = UnityEngine.Random.Range(i, total);
                (indices[i], indices[randomIndex]) = (indices[randomIndex], indices[i]);
                result.Add(list[indices[i]]);
            }

            return result;
        }

        /// <summary>
        /// 等概率获取多个元素（有放回抽样）
        /// </summary>
        public static List<T> GetRandomWithReplacement<T>(this IReadOnlyList<T> list, int count)
        {
            if (list == null || list.Count == 0 || count <= 0) return new List<T>(0);

            List<T> result = new List<T>(count);
            int total = list.Count;

            for (int i = 0; i < count; i++)
            {
                result.Add(list[UnityEngine.Random.Range(0, total)]);
            }

            return result;
        }

        //@ 2. 权重随机 (Weighted Random)

        /// <summary>
        /// 按权重获取 1 个随机元素
        /// </summary>
        public static T GetWeightedRandom<T>(this IReadOnlyList<T> list, Func<T, int> weightSelector)
        {
            if (list == null || list.Count == 0) return default;

            int totalWeight = 0;
            for (int i = 0; i < list.Count; i++)
            {
                int w = weightSelector(list[i]);
                if (w > 0) totalWeight += w;
            }

            if (totalWeight <= 0) return list[0];

            int randomValue = UnityEngine.Random.Range(0, totalWeight);

            for (int i = 0; i < list.Count; i++)
            {
                T item = list[i];
                int weight = weightSelector(item);
                if (weight <= 0) continue;

                if (randomValue < weight) return item;
                randomValue -= weight;
            }

            return list[list.Count - 1];
        }

        /// <summary>
        /// 按权重获取多个不重复的元素（不放回抽样）
        /// </summary>
        public static List<T> GetWeightedRandomUnique<T>(this IReadOnlyList<T> list, int count, Func<T, int> weightSelector)
        {
            if (list == null || list.Count == 0 || count <= 0) return new List<T>(0);

            List<(T item, int weight)> pool = new List<(T, int)>(list.Count);
            int totalWeight = 0;

            for (int i = 0; i < list.Count; i++)
            {
                T item = list[i];
                int w = weightSelector(item);
                if (w > 0)
                {
                    pool.Add((item, w));
                    totalWeight += w;
                }
            }

            int actualCount = Mathf.Min(count, pool.Count);
            List<T> result = new List<T>(actualCount);

            for (int i = 0; i < actualCount; i++)
            {
                if (totalWeight <= 0 || pool.Count == 0) break;

                int randomValue = UnityEngine.Random.Range(0, totalWeight);

                for (int j = 0; j < pool.Count; j++)
                {
                    int weight = pool[j].weight;
                    if (randomValue < weight)
                    {
                        result.Add(pool[j].item);
                        totalWeight -= weight;

                        int lastIndex = pool.Count - 1;
                        pool[j] = pool[lastIndex];
                        pool.RemoveAt(lastIndex);
                        break;
                    }

                    randomValue -= weight;
                }
            }

            return result;
        }

        /// <summary>
        /// 按权重获取多个元素（有放回抽样）
        /// </summary>
        public static List<T> GetWeightedRandomWithReplacement<T>(this IReadOnlyList<T> list, int count, Func<T, int> weightSelector)
        {
            if (list == null || list.Count == 0 || count <= 0) return new List<T>(0);

            List<T> result = new List<T>(count);

            int totalWeight = 0;
            for (int i = 0; i < list.Count; i++)
            {
                int w = weightSelector(list[i]);
                if (w > 0) totalWeight += w;
            }

            if (totalWeight <= 0)
            {
                for (int i = 0; i < count; i++) result.Add(list[0]);
                return result;
            }

            for (int i = 0; i < count; i++)
            {
                int randomValue = UnityEngine.Random.Range(0, totalWeight);
                for (int j = 0; j < list.Count; j++)
                {
                    T item = list[j];
                    int weight = weightSelector(item);
                    if (weight <= 0) continue;

                    if (randomValue < weight)
                    {
                        result.Add(item);
                        break;
                    }

                    randomValue -= weight;
                }
            }

            return result;
        }

        //!------------------------------------------------------
        /// <summary>
        /// 从列表头部抓取指定数量的元素，返回新列表，并在原列表中删除它们
        /// </summary>
        /// <param name="list">原始列表</param>
        /// <param name="count">需要抓取的元素数量</param>
        /// <returns>包含被抓取元素的新列表</returns>
        public static List<T> PopFirst<T>(this List<T> list, int count)
        {
            if (list == null || list.Count == 0 || count <= 0)
            {
                throw new ArgumentNullException(nameof(list));
            }

            // 如果请求的数量超过了列表当前拥有的数量，则抓取全部
            int actualCount = Mathf.Min(count, list.Count);

            // 1. 获取从 index 0 开始的指定数量元素（生成新列表）
            List<T> grabbedList = list.GetRange(0, actualCount);

            // 2. 从原列表中移除这些元素
            list.RemoveRange(0, actualCount);

            return grabbedList;
        }

        /// <summary>
        /// 从列表末尾抓取指定数量的元素，返回新列表，并从原列表中删除它们
        /// </summary>
        /// <param name="list">原始列表</param>
        /// <param name="count">需要抓取的元素数量</param>
        /// <param name="reverseOrder">
        /// 是否反转顺序：
        /// false(默认): "A", "B", "C", "D", "E" -> " D, E "；
        /// true: "A", "B", "C", "D", "E" -> " E, D "
        /// </param>
        /// <returns>包含被抓取元素的新列表</returns>
        public static List<T> PopLast<T>(this List<T> list, int count, bool reverseOrder = false)
        {
            if (list == null || list.Count == 0 || count <= 0)
            {
                throw new ArgumentNullException(nameof(list));
            }

            // 实际能抓取的数量（防止越界）
            int actualCount = Mathf.Min(count, list.Count);

            // 计算末尾切片的起始索引
            int startIndex = list.Count - actualCount;

            // 1. 获取末尾的元素
            List<T> grabbedList = list.GetRange(startIndex, actualCount);

            // 2. 从原列表中移除
            list.RemoveRange(startIndex, actualCount);

            // 3. 可选：是否反转顺序（模拟真正的“从最后一个逐个抽出来”的效果）
            if (reverseOrder)
            {
                grabbedList.Reverse();
            }

            return grabbedList;
        }

    }

    //# Pose
    public static class PoseExtensions
    {

        /// <summary>
        /// 获取 Transform 的世界坐标 Pose
        /// </summary>
        public static Pose ToPose(this Transform transform)
        {
            return new Pose(transform.position, transform.rotation);
        }

        /// <summary>
        /// 获取 Transform 的本地坐标 Pose
        /// </summary>
        public static Pose ToLocalPose(this Transform transform)
        {
            return new Pose(transform.localPosition, transform.localRotation);
        }

        /// <summary>
        /// 将 Pose 应用为 Transform 的世界坐标和旋转
        /// </summary>
        public static void SetPose(this Transform transform, Pose pose)
        {
            // 使用 SetPositionAndRotation 避免重复刷新底层 Transform 脏标记
            transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        /// <summary>
        /// 将 Pose 应用为 Transform 的本地坐标和旋转
        /// </summary>
        public static void SetLocalPose(this Transform transform, Pose pose)
        {
            transform.SetLocalPositionAndRotation(pose.position, pose.rotation);
        }

    }
    public static class TransformExtensions
    {
        public static void CopyTo(this Transform transform, Transform target)
        {
            target.position = transform.position;
            target.rotation = transform.rotation;
            target.localScale = transform.localScale;
        }

        public static void CopyFrom(this Transform transform, Transform target)
        {
            transform.position = target.position;
            transform.rotation = target.rotation;
            transform.localScale = target.localScale;
        }

        /// <summary>
        /// 让物体的任意本地轴指向目标
        /// </summary>
        /// <param name="localAxis">要对准目标的轴，例如 transform.up (Y) 或 transform.right (X)</param>
        public static void CustomLookAt(this Transform transform, Vector3 targetPosition, Vector3 localAxis, Vector3 upVector = default)
        {
            Vector3 dir = (targetPosition - transform.position).normalized;
            if (dir.sqrMagnitude < 0.001f) return;

            // Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            Vector3 up = upVector == default ? Vector3.up : upVector;

            Quaternion lookRotation = Quaternion.LookRotation(dir, up);

            Vector3 rawLocalAxis = transform.InverseTransformDirection(localAxis); // 获取未受旋转影响的模型轴空间
            Quaternion offset = Quaternion.FromToRotation(rawLocalAxis, Vector3.forward);

            transform.rotation = lookRotation * offset;
        }
    }

    public static class AvatarMaskExtensions
    {
        public static AvatarMask Invert(this AvatarMask mask)
        {
            if (mask == null)
            {
                Debug.LogError("AvatarMask 为空，无法进行反转操作！");
                return null;
            }
            AvatarMask result = new();
            // 1. 反转 Humanoid 身体部位的开关状态
            for (AvatarMaskBodyPart part = 0; part < AvatarMaskBodyPart.LastBodyPart; part++)
            {
                bool currentState = mask.GetHumanoidBodyPartActive(part);
                result.SetHumanoidBodyPartActive(part, !currentState);
            }

            // 2. 反转 Transform (骨骼列表) 的开关状态
            for (int i = 0; i < mask.transformCount; i++)
            {
                bool currentState = mask.GetTransformActive(i);
                result.SetTransformActive(i, !currentState);
            }

            return result;
        }
    }
    //# 反射检测null
    public static class MonoBehaviourExtensions
    {
        // 只在编辑器模式或勾选了 Development Build 时执行，正式打包（Release）会被完全剔除，零性能消耗
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void AssertFieldsNotNull(this MonoBehaviour monoBehaviour)
        {
            if (monoBehaviour == null) return;

            Type currentType = monoBehaviour.GetType();

            // 递归向上扫描，直到到达 MonoBehaviour 或 UnityEngine.Object 自身
            while (currentType != null && currentType != typeof(MonoBehaviour) && currentType != typeof(Object))
            {
                // 获取当前类型声明的所有字段（包括 private 和 public）
                FieldInfo[] fields = currentType.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly
                );

                foreach (var field in fields)
                {
                    // 1. 过滤：只检测 Unity 序列化的字段（public 或者带有 [SerializeField]）
                    bool isSerialized = field.IsPublic || Attribute.IsDefined(field, typeof(SerializeField));
                    if (!isSerialized) continue;

                    // 2. 过滤：字段类型必须是 Component 或 ScriptableObject（排除纯 C# 类）
                    Type fieldType = field.FieldType;
                    bool isTargetUnityType = typeof(Component).IsAssignableFrom(fieldType) ||
                                             typeof(ScriptableObject).IsAssignableFrom(fieldType);

                    if (!isTargetUnityType) continue;

                    // 3. 获取值并进行 Unity 重载的 == null 检查
                    // (转换成 UnityEngine.Object 使用其重载的 == 操作符，防止“伪null”穿透)
                    object value = field.GetValue(monoBehaviour);
                    Object unityObject = value as Object;

                    if (unityObject == null)
                    {
                        Debug.LogError(
                            $"<color=red>[Missing Reference]</color> 字段 <b>{field.Name}</b> 未赋值！" +
                            $"\n脚本: <b>{currentType.Name}</b> | 物体: <b>{monoBehaviour.gameObject.name}</b>",
                            monoBehaviour
                        );
                    }
                }

                // 检查基类
                currentType = currentType.BaseType;
            }
        }
    }

    #region Copy
    public static class CopyUtility
    {
        /// <summary>
        /// 将 source 的所有字段浅拷贝覆盖到 target 中
        /// </summary>
        public static void CopyValuesTo<T>(this T source, T target) where T : class
        {
            if (source == null || target == null) return;

            Type type = typeof(T);
            // 获取所有实例字段（包括公有和私有）
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                field.SetValue(target, field.GetValue(source));
            }
        }
    }
    #endregion

    #region EventExtensions
    public static class EventExtensions
    {
        // 无参数的 Action 安全调用
        public static void SafeInvoke(this Action action)
        {
            if (action == null) return;

            // 获取所有的订阅者列表
            Delegate[] invocationList = action.GetInvocationList();

            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    // 强制转换回 Action 执行，比 DynamicInvoke 性能好很多
                    ((Action)invocationList[i])();
                }
                catch (Exception ex)
                {
                    // 使用 Debug.LogException 可以保留完整的报错堆栈信息
                    Debug.LogException(ex);
                }
            }
        }

        // 带一个参数的 Action<T> 安全调用
        public static void SafeInvoke<T>(this Action<T> action, T arg)
        {
            if (action == null) return;

            Delegate[] invocationList = action.GetInvocationList();

            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<T>)invocationList[i])(arg);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        // 更多参数的 Action<T1, T2> 可以依此类推添加...
    }

    #endregion

    #region TransformSmoothDamp

    public static class TransformSmoothDampExtensions
    {
        private class SmoothData
        {
            // === 统一的移动物理量 ===
            public Vector3 Velocity;             // 统一移动速度 (m/s)：供 Move 和 Position 共用
            public Vector3 MoveAcceleration;     // 移动加速度 (m/s²)：仅用于连续输入的二次平滑

            // === 转向平滑数据 ===
            public float YawVelocity;            // Y 轴平滑角速度
            public Vector3 ForwardVelocity;      // 3D 朝向平滑速度
        }

        private static readonly ConditionalWeakTable<Transform, SmoothData> _table = new();

        /// <summary>
        /// 根据输入方向和最大速度平滑移动（自带加速/刹车惯性，支持与定点移动无缝衔接）
        /// </summary>
        public static Vector3 SmoothDampMove(
            this Transform transform,
            Vector3 inputDirection,
            float targetSpeed,
            float smoothTime = 0.12f,
            bool isLocalSpace = false,
            float stopThreshold = 0.01f,
            float? customDeltaTime = null)
        {
            SmoothData data = _table.GetOrCreateValue(transform);
            float dt = customDeltaTime ?? Time.deltaTime;

            bool hasNoInput = inputDirection.sqrMagnitude < 0.0001f;
            float sqrThreshold = stopThreshold * stopThreshold;

            // --- 核心短路优化 ---
            if (hasNoInput && data.Velocity.sqrMagnitude < sqrThreshold)
            {
                data.Velocity = Vector3.zero;
                data.MoveAcceleration = Vector3.zero;
                return Vector3.zero;
            }

            // 1. 计算期望的目标速度向量
            Vector3 targetVelocity = hasNoInput ? Vector3.zero : inputDirection.normalized * targetSpeed;
            if (isLocalSpace && !hasNoInput)
            {
                targetVelocity = transform.TransformDirection(targetVelocity);
            }

            // 2. 平滑速度向量（共享 Velocity）
            Vector3 currentAccel = data.MoveAcceleration;
            data.Velocity = Vector3.SmoothDamp(
                data.Velocity,
                targetVelocity,
                ref currentAccel,
                smoothTime,
                Mathf.Infinity,
                dt
            );
            data.MoveAcceleration = currentAccel;

            // 3. 应用位移
            transform.position += data.Velocity * dt;

            return data.Velocity;
        }

        /// <summary>
        /// 平滑移动到绝对世界坐标，并继承当前已有的物理惯性速度
        /// </summary>
        public static bool SmoothDampPosition(
            this Transform transform,
            Vector3 targetPosition,
            float smoothTime = 0.2f,
            float maxSpeed = Mathf.Infinity,
            float snapThreshold = 0.005f,
            float? customDeltaTime = null)
        {
            SmoothData data = _table.GetOrCreateValue(transform);
            float dt = customDeltaTime ?? Time.deltaTime;

            Vector3 toTarget = targetPosition - transform.position;
            float sqrDist = toTarget.sqrMagnitude;
            float sqrThreshold = snapThreshold * snapThreshold;

            // --- 核心短路与吸附逻辑 ---
            if (sqrDist < sqrThreshold && data.Velocity.sqrMagnitude < sqrThreshold)
            {
                if (transform.position != targetPosition)
                {
                    transform.position = targetPosition;
                }

                data.Velocity = Vector3.zero;
                data.MoveAcceleration = Vector3.zero; // 顺便清空加速度
                return true;
            }

            // 正常平滑运算（传入共享的 Velocity）
            Vector3 currentVel = data.Velocity;
            transform.position = Vector3.SmoothDamp(
                transform.position,
                targetPosition,
                ref currentVel,
                smoothTime,
                maxSpeed,
                dt
            );
            data.Velocity = currentVel;

            // 因为 Position 模式接管了速度控制，清空上一个模式遗留的加速度，防止切回 Move 时抽搐
            data.MoveAcceleration = Vector3.zero;

            return false;
        }

        public static void SmoothDampLookDirection(
            this Transform transform,
            Vector3 direction,
            float smoothTime = 0.1f,
            bool onlyYaw = true,
            float? customDeltaTime = null)
        {
            if (direction.sqrMagnitude < 0.0001f) return;

            SmoothData data = _table.GetOrCreateValue(transform);
            float dt = customDeltaTime ?? Time.deltaTime;

            if (onlyYaw)
            {
                float targetAngle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                float currentAngle = Mathf.SmoothDampAngle(
                    transform.eulerAngles.y,
                    targetAngle,
                    ref data.YawVelocity,
                    smoothTime,
                    Mathf.Infinity,
                    dt
                );
                transform.rotation = Quaternion.Euler(0f, currentAngle, 0f);
            }
            else
            {
                Vector3 currentVel = data.ForwardVelocity;
                Vector3 smoothForward = Vector3.SmoothDamp(
                    transform.forward,
                    direction.normalized,
                    ref currentVel,
                    smoothTime,
                    Mathf.Infinity,
                    dt
                );
                data.ForwardVelocity = currentVel;

                if (smoothForward.sqrMagnitude > 0.001f)
                {
                    transform.rotation = Quaternion.LookRotation(smoothForward);
                }
            }
        }

        public static void SmoothDampLookAt(
            this Transform transform,
            Vector3 targetPosition,
            float smoothTime = 0.1f,
            bool onlyYaw = true,
            float? customDeltaTime = null)
        {
            Vector3 direction = targetPosition - transform.position;
            if (onlyYaw) direction.y = 0f;
            transform.SmoothDampLookDirection(direction, smoothTime, onlyYaw, customDeltaTime);
        }

        /// <summary>
        /// 重置所有移动和转向惯性
        /// </summary>
        public static void ResetSmoothMovement(this Transform transform)
        {
            if (_table.TryGetValue(transform, out var data))
            {
                data.Velocity = Vector3.zero;
                data.MoveAcceleration = Vector3.zero;
                data.YawVelocity = 0f;
                data.ForwardVelocity = Vector3.zero;
            }
        }

    }
    #endregion

    #region CCSmoothDamp

    public static class CharacterControllerExtensions
    {
        private class ControllerSmoothData
        {
            public Vector3 Velocity;
            public Vector3 MoveAcceleration;
            public float YawVelocity;
        }

        private static readonly ConditionalWeakTable<CharacterController, ControllerSmoothData> _table = new();

        /// <summary>
        /// 平滑移动 CharacterController（自带加速、刹车惯性，并完美触发碰撞检测）
        /// </summary>
        /// <param name="extraMotion">附加位移（比如垂直方向的重力位移：new Vector3(0, verticalSpeed, 0)）</param>
        /// <returns>返回当前帧的碰撞标记位 CollisionFlags</returns>
        public static CollisionFlags SmoothDampMove(
            this CharacterController controller,
            Vector3 inputDirection,
            float targetSpeed,
            float smoothTime = 0.12f,
            bool isLocalSpace = false,
            Vector3 extraMotion = default,
            float stopThreshold = 0.01f,
            float? customDeltaTime = null)
        {
            ControllerSmoothData data = _table.GetOrCreateValue(controller);
            float dt = customDeltaTime ?? Time.deltaTime;

            bool hasNoInput = inputDirection.sqrMagnitude < 0.0001f;
            float sqrThreshold = stopThreshold * stopThreshold;

            // --- 静止短路 ---
            if (hasNoInput && data.Velocity.sqrMagnitude < sqrThreshold)
            {
                data.Velocity = Vector3.zero;
                data.MoveAcceleration = Vector3.zero;

                // 就算水平速度为0，如果有重力等额外位移，仍需执行
                if (extraMotion.sqrMagnitude > 0.0001f)
                {
                    return controller.Move(extraMotion * dt);
                }
                return CollisionFlags.None;
            }

            // 1. 计算目标水平速度
            Vector3 targetVelocity = hasNoInput ? Vector3.zero : inputDirection.normalized * targetSpeed;
            if (isLocalSpace && !hasNoInput)
            {
                targetVelocity = controller.transform.TransformDirection(targetVelocity);
            }

            // 2. 惯性平滑
            Vector3 currentAccel = data.MoveAcceleration;
            data.Velocity = Vector3.SmoothDamp(
                data.Velocity,
                targetVelocity,
                ref currentAccel,
                smoothTime,
                Mathf.Infinity,
                dt
            );
            data.MoveAcceleration = currentAccel;

            // 3. 【核心区别】通过 controller.Move 应用物理位移
            Vector3 totalMotion = (data.Velocity + extraMotion) * dt;
            return controller.Move(totalMotion);
        }

        /// <summary>
        /// 平滑定点移动 CharacterController（具备防穿墙抗阻机制）
        /// </summary>
        public static bool SmoothDampPosition(
            this CharacterController controller,
            Vector3 targetPosition,
            float smoothTime = 0.2f,
            float maxSpeed = Mathf.Infinity,
            float snapThreshold = 0.02f,
            Vector3 extraMotion = default,
            float? customDeltaTime = null)
        {
            ControllerSmoothData data = _table.GetOrCreateValue(controller);
            float dt = customDeltaTime ?? Time.deltaTime;

            Transform tf = controller.transform;
            Vector3 currentPos = tf.position;
            Vector3 toTarget = targetPosition - currentPos;
            float sqrDist = toTarget.sqrMagnitude;
            float sqrThreshold = snapThreshold * snapThreshold;

            // --- 到达吸附 ---
            if (sqrDist < sqrThreshold && data.Velocity.sqrMagnitude < sqrThreshold)
            {
                // 通过微小位移尝试贴合，避免直接修改 position 引发物理异常
                controller.Move(toTarget);
                data.Velocity = Vector3.zero;
                data.MoveAcceleration = Vector3.zero;
                return true;
            }

            // 1. 估算理论目标位置
            Vector3 currentVel = data.Velocity;
            Vector3 nextTargetPos = Vector3.SmoothDamp(
                currentPos,
                targetPosition,
                ref currentVel,
                smoothTime,
                maxSpeed,
                dt
            );
            data.Velocity = currentVel;
            data.MoveAcceleration = Vector3.zero; // 抑制加速度

            // 2. 计算本帧位移增量并通过物理引擎执行
            Vector3 deltaMotion = (nextTargetPos - currentPos) + extraMotion * dt;
            controller.Move(deltaMotion);

            return false;
        }

        /// <summary>
        /// 平滑面向水平目标方向
        /// </summary>
        public static void SmoothDampLookDirection(
            this CharacterController controller,
            Vector3 direction,
            float smoothTime = 0.1f,
            float? customDeltaTime = null)
        {
            if (direction.sqrMagnitude < 0.0001f) return;

            ControllerSmoothData data = _table.GetOrCreateValue(controller);
            float dt = customDeltaTime ?? Time.deltaTime;

            float targetAngle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            float currentAngle = Mathf.SmoothDampAngle(
                controller.transform.eulerAngles.y,
                targetAngle,
                ref data.YawVelocity,
                smoothTime,
                Mathf.Infinity,
                dt
            );
            controller.transform.rotation = Quaternion.Euler(0f, currentAngle, 0f);
        }

        /// <summary>
        /// 重置控制器上的所有运动惯性
        /// </summary>
        public static void ResetSmoothMovement(this CharacterController controller)
        {
            if (_table.TryGetValue(controller, out var data))
            {
                data.Velocity = Vector3.zero;
                data.MoveAcceleration = Vector3.zero;
                data.YawVelocity = 0f;
            }
        }

        /// <summary>
        /// 获取当前内部平滑算法的物理水平速度 (m/s)
        /// </summary>
        public static Vector3 GetSmoothVelocity(this CharacterController controller)
        {
            return _table.TryGetValue(controller, out var data) ? data.Velocity : Vector3.zero;
        }

    }
    #endregion

    #region Rb2dSmoothDamp

    public static class Rigidbody2DExtensions
    {
        private class Smooth2DData
        {
            public Vector2 MoveAcceleration;  // 速度变化的一阶导（即加速度）
            public Vector2 PosVelocity;       // 定点平滑时的虚拟导数
        }

        private static readonly ConditionalWeakTable<Rigidbody2D, Smooth2DData> _table = new();

        /// <summary>
        /// 【横版平台跳跃专用】平滑控制水平移动，自动保留物理重力和跳跃 (Y 轴速度不变)
        /// </summary>
        public static void SmoothDampHorizontal(
            this Rigidbody2D rb,
            float inputHorizontal,
            float targetSpeed,
            float smoothTime = 0.1f,
            float stopThreshold = 0.01f,
            float? customFixedDeltaTime = null)
        {
            Smooth2DData data = _table.GetOrCreateValue(rb);
            float dt = customFixedDeltaTime ?? Time.fixedDeltaTime;

            // Unity 6+ 使用 linearVelocity，旧版本请换成 rb.velocity
            Vector2 currentVel = rb.linearVelocity;
            bool hasNoInput = Mathf.Abs(inputHorizontal) < 0.0001f;

            // 静止短路优化
            if (hasNoInput && Mathf.Abs(currentVel.x) < stopThreshold)
            {
                rb.linearVelocity = new Vector2(0f, currentVel.y);
                data.MoveAcceleration.x = 0f;
                return;
            }

            float targetSpeedX = inputHorizontal * targetSpeed;
            float currentAccelX = data.MoveAcceleration.x;

            // 仅对 X 轴进行平滑
            float newSpeedX = Mathf.SmoothDamp(
                currentVel.x,
                targetSpeedX,
                ref currentAccelX,
                smoothTime,
                Mathf.Infinity,
                dt
            );
            data.MoveAcceleration.x = currentAccelX;

            // 保留原有的 Y 轴物理速度（重力、跳跃等）
            rb.linearVelocity = new Vector2(newSpeedX, currentVel.y);
        }

        /// <summary>
        /// 【俯视角 Top-Down 专用】全向平滑移动
        /// </summary>
        public static void SmoothDampMove(
            this Rigidbody2D rb,
            Vector2 inputDirection,
            float targetSpeed,
            float smoothTime = 0.1f,
            float stopThreshold = 0.01f,
            float? customFixedDeltaTime = null)
        {
            Smooth2DData data = _table.GetOrCreateValue(rb);
            float dt = customFixedDeltaTime ?? Time.fixedDeltaTime;

            Vector2 currentVel = rb.linearVelocity;
            bool hasNoInput = inputDirection.sqrMagnitude < 0.0001f;
            float sqrThreshold = stopThreshold * stopThreshold;

            // 静止短路
            if (hasNoInput && currentVel.sqrMagnitude < sqrThreshold)
            {
                rb.linearVelocity = Vector2.zero;
                data.MoveAcceleration = Vector2.zero;
                return;
            }

            Vector2 targetVel = hasNoInput ? Vector2.zero : inputDirection.normalized * targetSpeed;
            Vector2 currentAccel = data.MoveAcceleration;

            // 平滑速度向量
            Vector2 newVel = Vector2.SmoothDamp(
                currentVel,
                targetVel,
                ref currentAccel,
                smoothTime,
                Mathf.Infinity,
                dt
            );
            data.MoveAcceleration = currentAccel;

            rb.linearVelocity = newVel;
        }

        /// <summary>
        /// 平滑移动刚体到绝对坐标（使用 MovePosition 保证不会穿墙）
        /// </summary>
        public static bool SmoothDampPosition(
            this Rigidbody2D rb,
            Vector2 targetPosition,
            float smoothTime = 0.2f,
            float maxSpeed = Mathf.Infinity,
            float snapThreshold = 0.01f,
            float? customFixedDeltaTime = null)
        {
            Smooth2DData data = _table.GetOrCreateValue(rb);
            float dt = customFixedDeltaTime ?? Time.fixedDeltaTime;

            Vector2 currentPos = rb.position;
            Vector2 toTarget = targetPosition - currentPos;
            float sqrDist = toTarget.sqrMagnitude;
            float sqrThreshold = snapThreshold * snapThreshold;

            // 吸附完成判断
            if (sqrDist < sqrThreshold && data.PosVelocity.sqrMagnitude < sqrThreshold)
            {
                rb.MovePosition(targetPosition);
                data.PosVelocity = Vector2.zero;
                data.MoveAcceleration = Vector2.zero;
                rb.linearVelocity = Vector2.zero;
                return true;
            }

            Vector2 currentVel = data.PosVelocity;
            Vector2 nextPos = Vector2.SmoothDamp(
                currentPos,
                targetPosition,
                ref currentVel,
                smoothTime,
                maxSpeed,
                dt
            );
            data.PosVelocity = currentVel;

            // 【关键点】使用 MovePosition 走物理通道，保证碰墙被挡住，绝不穿模
            rb.MovePosition(nextPos);

            return false;
        }

        public static void ResetSmoothMovement(this Rigidbody2D rb)
        {
            if (_table.TryGetValue(rb, out var data))
            {
                data.MoveAcceleration = Vector2.zero;
                data.PosVelocity = Vector2.zero;
            }
            rb.linearVelocity = Vector2.zero;
        }

    }

    #endregion
}
