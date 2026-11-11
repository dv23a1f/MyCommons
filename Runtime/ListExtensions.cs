#nullable enable 

using System;
using System.Collections.Generic;

namespace DX.Core.Extensions.List
{

    /// <summary>
    /// 基于线程静态缓存的极简 List 池（每个线程独立持有缓存，保证多线程安全且零锁开销）
    /// </summary>
    internal static class LocalListPool<T>
    {
        [ThreadStatic]
        private static List<T>? _cachedList;

        public readonly struct PooledObject : IDisposable
        {
            public readonly List<T> List;

            public PooledObject(List<T> list) => List = list;

            public void Dispose()
            {
                List.Clear();
                _cachedList = List; // 归还到当前线程缓存
            }
        }

        public static PooledObject Get(out List<T> list)
        {
            list = _cachedList ?? new List<T>();
            _cachedList = null;
            list.Clear(); // <-- 加这一句防御性清空，即便有残留数据也绝不会出 Bug
            return new PooledObject(list);
        }
    }

    /// <summary>
    /// 基于线程静态缓存的极简 HashSet 池
    /// </summary>
    internal static class LocalHashSetPool<T>
    {
        [ThreadStatic]
        private static HashSet<T>? _cachedSet;

        public readonly struct PooledObject : IDisposable
        {
            public readonly HashSet<T> Set;

            public PooledObject(HashSet<T> set) => Set = set;

            public void Dispose()
            {
                Set.Clear();
                _cachedSet = Set; // 归还
            }
        }

        public static PooledObject Get(out HashSet<T> set)
        {
            set = _cachedSet ?? new HashSet<T>();
            _cachedSet = null;
            return new PooledObject(set);
        }
    }

    public static class ListExtensions
    {
        /// <summary>
        /// 获取指定元素在只读列表中的索引
        /// </summary>
        public static int IndexOf<T>(this IReadOnlyList<T>? list, T item)
        {
            if (list == null) return -1;

            if (list is IList<T> concreteList)
            {
                return concreteList.IndexOf(item);
            }

            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < list.Count; i++)
            {
                if (comparer.Equals(list[i], item))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 检查列表或数组是否为null或空
        /// </summary>
        public static bool IsNullOrEmpty<T>(this IEnumerable<T>? source)
        {
            if (source == null) return true;

            if (source is ICollection<T> collection)
            {
                return collection.Count == 0;
            }

            if (source is IReadOnlyCollection<T> readOnlyCollection)
            {
                return readOnlyCollection.Count == 0;
            }

            using (var enumerator = source.GetEnumerator())
            {
                return !enumerator.MoveNext();
            }
        }

        /// <summary>
        /// 空安全的索引检查
        /// </summary>
        public static bool IsValidIndex<T>(this IList<T>? list, int index)
        {
            if (list is null) return false;
            return (uint)index < (uint)list.Count;
        }

        /// <summary>
        /// 将列表用指定值填充满，可指定填充大小
        /// </summary>
        public static void Fill<T>(this List<T> list, T value, int count)
        {
            if (list == null) return;
            list.Clear();
            if (list.Capacity < count) list.Capacity = count;

            for (int i = 0; i < count; i++)
            {
                list.Add(value);
            }
        }
        /// <summary>
        /// 获取指定条件值最大的元素（0 分配，O(N) 遍历）
        /// 典型应用：寻找距离最近的敌人、寻找血量最少的单位
        /// </summary>
        public static T? MaxBy<T, TKey>(this IReadOnlyList<T> list, Func<T, TKey> selector) where TKey : IComparable<TKey>
        {
            if (list == null || list.Count == 0) return default;

            T maxItem = list[0];
            TKey maxValue = selector(maxItem);

            for (int i = 1; i < list.Count; i++)
            {
                T item = list[i];
                TKey val = selector(item);
                if (val.CompareTo(maxValue) > 0)
                {
                    maxValue = val;
                    maxItem = item;
                }
            }
            return maxItem;
        }

        public static T? MinBy<T, TKey>(this IReadOnlyList<T> list, Func<T, TKey> selector) where TKey : IComparable<TKey>
        {
            if (list == null || list.Count == 0) return default;

            T minItem = list[0];
            TKey minValue = selector(minItem);

            for (int i = 1; i < list.Count; i++)
            {
                T item = list[i];
                TKey val = selector(item);
                if (val.CompareTo(minValue) < 0)
                {
                    minValue = val;
                    minItem = item;
                }
            }
            return minItem;
        }
        /// <summary>
        /// 环状安全索引（向前翻页、向后翻页永远不会越界）
        /// 例如 count 为 5: index=-1 会返回 4; index=5 会返回 0
        /// </summary>
        public static T? GetAtLooped<T>(this IReadOnlyList<T> list, int index)
        {
            if (list == null || list.Count == 0) return default;
            int count = list.Count;
            int wrappedIndex = (index % count + count) % count; // 解决负数取模依然为负的问题
            return list[wrappedIndex];
        }

        /// <summary>
        /// 零 GC 的条件查找（补充 IReadOnlyList 的 Find ）
        /// </summary>
        public static T? Find<T>(this IReadOnlyList<T> list, Predicate<T> match)
        {
            if (list == null) return default;
            for (int i = 0; i < list.Count; i++)
            {
                if (match(list[i])) return list[i];
            }
            return default;
        }

        /// <summary>
        /// 零 GC 的条件统计
        /// </summary>
        public static int CountIf<T>(this IReadOnlyList<T> list, Predicate<T> match)
        {
            if (list == null) return 0;
            int count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (match(list[i])) count++;
            }
            return count;
        }

        /// <summary>
        /// Fisher-Yates 洗牌算法
        /// </summary>
        public static void Shuffle<T>(this IList<T> source, Random? rng = null)
        {
            if (source == null || source.Count <= 1) return;
            rng ??= ThreadSafeRandom.Instance;

            for (int i = source.Count - 1; i > 0; i--)
            {
                int j = rng.Next(0, i + 1);
                (source[i], source[j]) = (source[j], source[i]);
            }
        }

        /// <summary>
        /// 按照权重对List进行洗牌 (加权不放回采样)
        /// 权重越大，排在前面的概率越高。
        /// 算法复杂度: O(N log N)
        /// </summary>
        /// <param name="weightSelector">获取权重的委托 (返回值必须大于0)</param>
        public static void WeightedShuffle<T>(this IList<T> list, Func<T, float> weightSelector, Random? rng = null)
        {
            if (list == null || list.Count <= 1) return;
            rng ??= ThreadSafeRandom.Instance;

            using (LocalListPool<KeyValuePair<T, double>>.Get(out var weightedItems))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    T item = list[i];
                    float weight = weightSelector(item);

                    double score;
                    if (!(weight > 0f))
                    {
                        score = double.NegativeInfinity;
                    }
                    else
                    {
                        double r = rng.NextDouble();
                        if (r <= 0.0) r = double.Epsilon;
                        // score = log(r) / weight
                        score = Math.Log(r) / weight;
                    }

                    weightedItems.Add(new KeyValuePair<T, double>(item, score));
                }

                // 降序排序
                weightedItems.Sort((a, b) => b.Value.CompareTo(a.Value));

                for (int i = 0; i < weightedItems.Count; i++)
                {
                    list[i] = weightedItems[i].Key;
                }
            }
        }

        /// <summary>
        /// 从 List 中删除多个元素 (双指针 O(N) 性能)
        /// </summary>
        public static void RemoveElements<T>(this List<T>? list, IEnumerable<T>? elementsToRemove)
        {
            if (list == null || elementsToRemove == null || list.Count == 0) return;

            using (LocalHashSetPool<T>.Get(out var pooledSet))
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

        /// <summary>
        /// 验证当前集合是否包含 targets 中的【所有】元素（超集判断）
        /// </summary>
        public static bool ContainsAll<T>(this IEnumerable<T>? source, IEnumerable<T>? targets)
        {
            if (source == null || targets == null) return false;

            if (source is ISet<T> sourceSet)
            {
                return sourceSet.IsSupersetOf(targets);
            }

            using (LocalHashSetPool<T>.Get(out var pooledSet))
            {
                foreach (var item in source) pooledSet.Add(item);
                return pooledSet.IsSupersetOf(targets);
            }
        }

        /// <summary>
        /// 验证当前集合是否包含 targets 中的【任意一个】元素（重叠判断）
        /// </summary>
        public static bool ContainsAny<T>(this IEnumerable<T>? source, IEnumerable<T>? targets)
        {
            if (source == null || targets == null) return false;

            if (source is ISet<T> sourceSet)
            {
                return sourceSet.Overlaps(targets);
            }

            if (targets is ISet<T> targetSet)
            {
                return targetSet.Overlaps(source);
            }

            using (LocalHashSetPool<T>.Get(out var pooledSet))
            {
                foreach (var item in source) pooledSet.Add(item);
                return pooledSet.Overlaps(targets);
            }
        }
    }

    public static class ListRandomExtensions
    {
        /// <summary>
        /// 等概率获取 1 个随机元素
        /// </summary>
        public static T? GetRandom<T>(this IReadOnlyList<T>? list, Random? rng = null)
        {
            if (list == null || list.Count == 0) return default;
            rng ??= ThreadSafeRandom.Instance;
            return list[rng.Next(0, list.Count)];
        }
        /// <summary>
        /// 等概率获取多个不重复的元素（不放回抽样）
        /// </summary>
        public static List<T> GetRandomUnique<T>(this IReadOnlyList<T>? list, int count, Random? rng = null)
        {
            if (list == null || list.Count == 0 || count <= 0) return new List<T>(0);
            rng ??= ThreadSafeRandom.Instance;

            int total = list.Count;
            int actualCount = Math.Min(count, total);
            List<T> result = new List<T>(actualCount);

            int[] indices = new int[total];
            for (int i = 0; i < total; i++) indices[i] = i;

            for (int i = 0; i < actualCount; i++)
            {
                int randomIndex = rng.Next(i, total);
                (indices[i], indices[randomIndex]) = (indices[randomIndex], indices[i]);
                result.Add(list[indices[i]]);
            }

            return result;
        }
        /// <summary>
        /// 等概率获取多个元素（有放回抽样）
        /// </summary>
        public static List<T> GetRandomWithReplacement<T>(this IReadOnlyList<T>? list, int count, Random? rng = null)
        {
            if (list == null || list.Count == 0 || count <= 0) return new List<T>(0);
            rng ??= ThreadSafeRandom.Instance;

            List<T> result = new List<T>(count);
            int total = list.Count;

            for (int i = 0; i < count; i++)
            {
                result.Add(list[rng.Next(0, total)]);
            }

            return result;
        }

        /// <summary>
        /// 按权重获取 1 个随机元素
        /// </summary>
        public static T? GetWeightedRandom<T>(this IReadOnlyList<T>? list, Func<T, int> weightSelector, Random? rng = null)
        {
            if (list == null || list.Count == 0) return default;
            rng ??= ThreadSafeRandom.Instance;

            int totalWeight = 0;
            for (int i = 0; i < list.Count; i++)
            {
                int w = weightSelector(list[i]);
                if (w > 0) totalWeight += w;
            }

            if (totalWeight <= 0) return list[0];

            int randomValue = rng.Next(0, totalWeight);

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
        public static List<T> GetWeightedRandomUnique<T>(this IReadOnlyList<T>? list, int count, Func<T, int> weightSelector, Random? rng = null)
        {
            if (list == null || list.Count == 0 || count <= 0) return new List<T>(0);
            rng ??= ThreadSafeRandom.Instance;

            using (LocalListPool<(T item, int weight)>.Get(out var pool))
            {
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

                int actualCount = Math.Min(count, pool.Count);
                List<T> result = new List<T>(actualCount);

                for (int i = 0; i < actualCount; i++)
                {
                    if (totalWeight <= 0 || pool.Count == 0) break;

                    int randomValue = rng.Next(0, totalWeight);

                    for (int j = 0; j < pool.Count; j++)
                    {
                        int weight = pool[j].weight;
                        if (randomValue < weight)
                        {
                            result.Add(pool[j].item);
                            totalWeight -= weight;

                            // 用最后一个覆盖当前元素，做到 O(1) 移除
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
        }

        /// <summary>
        /// 按权重获取多个元素（有放回抽样）
        /// </summary>
        public static List<T> GetWeightedRandomWithReplacement<T>(this IReadOnlyList<T>? list, int count, Func<T, int> weightSelector, Random? rng = null)
        {
            if (list == null || list.Count == 0 || count <= 0) return new List<T>(0);
            rng ??= ThreadSafeRandom.Instance;

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
                int randomValue = rng.Next(0, totalWeight);
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
    }

    public static class ListUtilityExtensions
    {
        /// <summary>
        /// 从列表头部抓取指定数量的元素，返回新列表，并在原列表中删除它们
        /// </summary>
        /// <param name="list">原始列表</param>
        /// <param name="count">需要抓取的元素数量</param>
        /// <returns>包含被抓取元素的新列表</returns>
        public static List<T> PopFirst<T>(this List<T> list, int count)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (count <= 0 || list.Count == 0) return new List<T>(0);

            int actualCount = Math.Min(count, list.Count);
            List<T> grabbedList = list.GetRange(0, actualCount);
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
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (count <= 0 || list.Count == 0) return new List<T>(0);

            int actualCount = Math.Min(count, list.Count);
            int startIndex = list.Count - actualCount;

            List<T> grabbedList = list.GetRange(startIndex, actualCount);
            list.RemoveRange(startIndex, actualCount);

            if (reverseOrder)
            {
                grabbedList.Reverse();
            }

            return grabbedList;
        }

    }
    /// <summary>
    /// 兼容各版本的线程安全 Random
    /// (.NET 6+ 会直接使用 Random.Shared，老版本使用 ThreadLocal)
    /// </summary>
    internal static class ThreadSafeRandom
    {
#if NET6_0_OR_GREATER
        public static Random Instance => Random.Shared;
#else
        private static readonly System.Threading.ThreadLocal<Random> LocalRandom =
            new System.Threading.ThreadLocal<Random>(() => new Random(Guid.NewGuid().GetHashCode()));

        public static Random Instance => LocalRandom.Value!;
#endif
    }
}