using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Framework.SaveManagement.Converters;
using Framework.SaveManagement.Events;
using Framework.SaveManagement.Internal;
using Framework.SaveManagement.Security;
using Framework.SaveManagement.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Scripting;

/* 
需安装 com.unity.nuget.newtonsoft-json
 */

namespace DX.Core.SaveManagement
{
    /// <summary>
    /// 标记需要存档的字段或属性。(白名单模式：仅被此特性标记的成员会被保存，未标记的将被忽略)
    /// 如果该类实现了 IAutoSaveable ，将优先使用白名单模式。
    /// 可选参数 1. CustomKey: 自定义序列化名 2. PreserveType 强制记录类型，适用于object类的列表或字典 3. SkipSync 跳过不同场景间数据同步（不影响存档）
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    [RequireAttributeUsages, Preserve] // 提示编译器防止该 Attribute 使用的元数据被剥离
    public class SaveFieldAttribute : PreserveAttribute
    {
        public string CustomKey { get; }
        public bool PreserveType { get; }
        public bool SkipSync { get; }

        /// <param name="customKey">可选：自定义序列化字段名，默认 null 使用字段名</param>
        /// <param name="preserveType">可选：是否强制记录具体强类型信息（兼容 SaveType 的 CustomTypeKey）。默认 false</param>
        /// <param name="skipSync">可选：仅忽略跨场景内存同步，不影响存档 (如：同步基础数据，但是忽略position，防止覆盖出生点)。默认 false</param>
        public SaveFieldAttribute(string customKey = null, bool preserveType = false, bool skipSync = false)
        {
            CustomKey = customKey;
            PreserveType = preserveType;
            SkipSync = skipSync;
        }
    }

    /// <summary>
    /// 指示该类（或其派生类）在存盘时保留具体类型信息，支持对 抽象类/接口 标注。
    /// 支持指定自定义别名（对具体派生类标注），代码后续即使改名、改命名空间也不会导致旧存档损坏。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, Inherited = true)]
    [RequireAttributeUsages, Preserve]
    public class SaveTypeAttribute : PreserveAttribute
    {
        public string CustomTypeKey { get; }
        /// <param name="customKey">可选：自定义类别名，不可对抽象基类/接口标注</param>
        public SaveTypeAttribute(string customKey = null) => CustomTypeKey = customKey;
    }

    public enum SaveScope
    {
        Game,    // 随游戏进度存档走（支持多槽位）
        Global   // 全局通用（设置、图鉴、成就，单一持久化文件）
    }
    /// <summary>
    /// 存档核心事件类型枚举
    /// </summary>
    public enum SaveEventType
    {
        BeforeStartNewGame,
        NewGameStarted,

        BeforeSlotSave,
        AfterSlotSave,
        SlotSaveFailed,

        BeforeSlotLoad,
        AfterSlotLoad,
        SlotFileNotFound,
        SlotFileCorrupted,

        BeforeGlobalSave,
        GlobalSaved,
        GlobalFileNotFound,
        GlobalFileCorrupted,

        SlotDeleted,
        GlobalFileDeleted,
    }

    /// <summary>
    /// 手动 DTO 接口：若类实现了此接口，系统将直接走接口逻辑，跳过反射
    /// </summary>
    public interface ISaveable
    {
        object CaptureState();
        void RestoreState(object state);
    }

    public interface ISaveable<T> : ISaveable
    {
        new T CaptureState();
        void RestoreState(T state);

        object ISaveable.CaptureState() => CaptureState();

        void ISaveable.RestoreState(object state)
        {
            if (state == null) return;
            RestoreState(SaveConvert.To<T>(state));
        }
    }

    /// <summary>
    /// 【声明式自动存档接口】
    /// 1. 全自动模式（默认）：类中未标注 [SaveField]， 自动扫描 public 读写属性。
    /// 2. 白名单模式：类中存在 [SaveField] 标签时，仅保存被标记的字段，支持 private 字段
    /// </summary>
    public interface IAutoSaveable : ISaveable
    {
        object ISaveable.CaptureState() => SaveMetadataCache.CaptureReflectionState(this);
        void ISaveable.RestoreState(object state) => SaveMetadataCache.RestoreReflectionState(this, state);
    }

    /// <summary>
    /// 存储适配器抽象（可自由桥接 EasySave 或 原生 Json）
    /// </summary>
    public interface IStorageProvider
    {
        SaveResult Save(SaveRequest request);
        LoadResult Load(LoadRequest request);
        bool HasFile(string fileName, SaveScope scope);
        bool DeleteFile(string fileName, SaveScope scope);

        SaveMetadata LoadMetadata(string fileName, SaveScope scope);
        List<SaveMetadata> GetAllFilesMetadata(SaveScope scope);

        string GetPath(SaveScope scope, string fileName = null);
        bool RestoreBackup(string fileName, SaveScope scope, Func<SavePack, bool> validator = null);
    }

    public interface IEntityFactory
    {
        /// <summary>
        /// 捕获当前所有实例的状态
        /// </summary>
        List<object> CaptureAll();

        /// <summary>
        /// 恢复全部实例的状态 (需要先清空旧实例)
        /// </summary>
        void RestoreAll(List<object> states);
    }

    public interface IEntityFactory<TEntity> : IEntityFactory
        where TEntity : Component, ISaveable
    {
        /// <summary> 获取当前存活的所有实体 </summary>
        IEnumerable<TEntity> GetActiveEntities();

        /// <summary> 生产一个实体 </summary>
        TEntity CreateEntity();

        /// <summary> 清空所有现有实体 (未重新加载场景时用于清空) </summary>
        void ClearAllEntities() { }

        /// <summary> 扩展钩子：实体完成 RestoreState 后的初始化回调 </summary>
        void OnEntityRestored(TEntity entity, object rawState) { }

        List<object> IEntityFactory.CaptureAll()
        {
            var entities = GetActiveEntities();
            if (entities == null) return new List<object>(0);

            var result = new List<object>();
            foreach (var entity in entities)
            {
                if (entity != null)
                {
                    try
                    {
                        result.Add(entity.CaptureState());
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[SaveSystem] 工厂实体 [{entity.name}] CaptureState 失败，跳过该实体: {ex.Message}");
                    }
                }
            }
            return result;
        }

        void IEntityFactory.RestoreAll(List<object> states)
        {
            ClearAllEntities();

            if (states == null || states.Count == 0) return;

            foreach (var state in states)
            {
                try
                {
                    var entity = CreateEntity();
                    if (entity != null)
                    {
                        entity.RestoreState(state);
                        OnEntityRestored(entity, state);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SaveSystem] 批量还原单个动态实体失败: {ex}");
                }
            }
        }
    }

    /// <summary>
    /// 开箱即用的自动化实体工厂：
    /// 仅需提供 EntityPrefab 和 ActiveEntities，即可自动获得完整的生命周期与存档支持。
    /// 可选重写：EntityRoot 
    /// </summary>
    public interface IAutoEntityFactory<TEntity> : IEntityFactory<TEntity>
        where TEntity : Component, ISaveable
    {
        /// <summary>预制体引用</summary>
        TEntity EntityPrefab { get; }
        /// <summary>当前已生成的活跃中的实例</summary>
        ICollection<TEntity> ActiveEntities { get; }
        /// <summary>可选：根节点属性，自动生成时将在该节点下生成实例</summary>
        Transform EntityRoot => null;

        TEntity IEntityFactory<TEntity>.CreateEntity()
        {
            if (EntityPrefab == null) return null;
            var instance = UnityEngine.Object.Instantiate(EntityPrefab, EntityRoot);
            ActiveEntities?.Add(instance);
            return instance;
        }

        void DestroyEntity(TEntity entity)
        {
            if (entity != null) UnityEngine.Object.Destroy(entity.gameObject);
        }

        IEnumerable<TEntity> IEntityFactory<TEntity>.GetActiveEntities() => ActiveEntities;

        void IEntityFactory<TEntity>.ClearAllEntities()
        {
            var collection = ActiveEntities;
            if (collection == null || collection.Count == 0) return;

            var toClear = new List<TEntity>(collection);
            collection.Clear();

            foreach (var entity in toClear)
            {
                DestroyEntity(entity);
            }
        }
    }

    public static class SaveableListExtensions
    {
        // 1. 强类型版：单泛型推导
        public static List<TData> CaptureAll<TData>(this IList<ISaveable<TData>> entities)
        {
            if (entities == null || entities.Count == 0) return new List<TData>(0);
            var list = new List<TData>(entities.Count);
            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                list.Add(entity != null ? entity.CaptureState() : default);
            }
            return list;
        }

        public static void RestoreAll<TData>(this IList<ISaveable<TData>> entities, IList<TData> states)
        {
            if (entities == null || states == null) return;
            int count = Math.Min(entities.Count, states.Count);
            for (int i = 0; i < count; i++)
            {
                entities[i]?.RestoreState(states[i]);
            }
        }

        // 2. 弱类型版：兜底重载（用于异构集合、IAutoSaveable 反射实体或非泛型 ISaveable）
        public static List<object> CaptureAll(this IList<ISaveable> entities)
        {
            if (entities == null || entities.Count == 0) return new List<object>(0);
            var list = new List<object>(entities.Count);
            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                list.Add(entity != null ? entity.CaptureState() : null);
            }
            return list;
        }

        public static void RestoreAll(this IList<ISaveable> entities, IList<object> states)
        {
            if (entities == null || states == null) return;
            int count = Math.Min(entities.Count, states.Count);
            for (int i = 0; i < count; i++)
            {
                entities[i]?.RestoreState(states[i]);
            }
        }
    }
    [Serializable]
    public class SavePack
    {

        public SaveMetadata Metadata { get; set; }

        // 存储反射模式的组件数据：ID -> (字段名 -> 值)
        // 1. 静态实体（单例、玩家、唯一宝箱等）：ID -> 单个状态对象 (无论是 DTO 还是反射 Dictionary)
        public Dictionary<string, object> StaticEntities { get; set; } = new();

        // 2. 动态实体（工厂批量生产的怪、掉落物等）：PrefabId -> 状态列表
        public Dictionary<string, List<object>> DynamicEntities { get; set; } = new();

        public static implicit operator bool(SavePack pack)
        {
            return pack != null && pack.Metadata;
        }
    }

    [Serializable]
    public class SaveMetadata
    {
        public string DataHash { get; set; }
        public string FileName { get; set; } // 槽位名或全局文件名
        public SaveScope Scope { get; set; }

        public string SaveTime { get; set; }

        public Dictionary<string, object> ExtraData { get; set; } = new();

        // 如果引用为 null，或者未通过基础有效性验证，返回 false
        public static implicit operator bool(SaveMetadata meta)
        {
            return meta != null && !string.IsNullOrEmpty(meta.FileName);
        }
    }

    public static class SaveMetadataExtensions
    {
        /// <summary>
        /// 安全设置扩展数据
        /// </summary>
        public static void SetCustomData<T>(this SaveMetadata source, string key, T value)
        {
            source.ExtraData[key] = value;
        }

        /// <summary>
        /// 安全获取扩展数据（自动适配各种 JToken、类型转换及自定义序列化器）
        /// </summary>
        public static T GetCustomData<T>(this SaveMetadata source, string key, T defaultValue = default)
        {
            if (source?.ExtraData == null
                || !source.ExtraData.TryGetValue(key, out var rawValue)
                || SaveConvert.IsNull(rawValue))
                return defaultValue;

            return rawValue.As<T>(defaultValue);
        }

    }

    // /// <summary>
    // /// 反射数据的快照包装器
    // /// </summary>
    // [Serializable, Preserve]
    // public class ReflectionSnapshot
    // {

    //     public Dictionary<string, object> Fields = new();

    //     public ReflectionSnapshot() { }
    //     public ReflectionSnapshot(Dictionary<string, object> fields) => Fields = fields;
    // }

    //! ============================================
    //! SaveSystem
    //! ============================================
    public static class SaveSystem
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void AutoClear()
        {
            UnloadSlot();
            ClearStaticEntityRegistry();
            ClearFactoryRegistry();
            ClearDynamicMetaProperties();
            _eventHub.Clear();
            _storageProvider = null;
            _pendingLoadAction = null;
            _sceneIdResolver = null;
            GlobalPacksCache.Clear();
        }
        // 记录未就绪时被挂起的 Load 行为（只保留最新的一个）
        private static Action _pendingLoadAction;

        private static IStorageProvider _storageProvider;
        internal static IStorageProvider StorageProvider
        {
            private get => _storageProvider;
            set
            {
                if (value == null) return;
                _storageProvider = value;
                if (_storageProvider != null && _pendingLoadAction != null)
                {
                    var action = _pendingLoadAction;
                    _pendingLoadAction = null;
                    action.Invoke();
                }
            }
        }

        /// <summary>
        /// 数字槽位名称格式化模板（支持标准 C# 字符串格式化）
        /// 默认："Slot_{0}" -> Slot_0, Slot_1
        /// 如需补零可配置为："Save_{0:D2}" -> Save_01, Save_02
        /// </summary>
        public static string SlotNameFormat { get; set; } = "Slot_{0}";
        public const string DEFAULT_TIME_FORMAT = "yyyy-MM-dd HH:mm:ss";
        public const string GLOBAL_SLOT_NAME = "global_settings"; // 固定的全局存档名
        private const string VIRTUAL_SLOT_NAME = "VirtualMemorySlot";
        private const string AUTO_KEY_PREFIX = "__auto_"; // 自动类型 Key 前缀

        public static bool HasInitialized => StorageProvider != null;

        private static readonly SaveEventHub _eventHub = new();
        public static ISaveEvents Events => _eventHub;

        /// <summary>跳过重复检查（true: 数据一致也会保存）</summary>
        public static bool SkipHashCheck { get; set; } = false;
        /// <summary>开启备份 .bak</summary>
        public static bool EnableBackup { get; set; } = false;
        /// <summary>全局存档加密开关（true: 开启 GZip 压缩与 AES 加密；false: 存为纯明文 JSON）</summary>
        public static bool EnableEncryption { get; set; } = false;
        // 登记的id -> 实例
        private static readonly Dictionary<string, SaveTargetEntry> Registry = new(StringComparer.OrdinalIgnoreCase);
        // 包含所有文件的全局存档 文件名 -> 存档实例
        private static readonly Dictionary<string, SavePack> GlobalPacksCache = new(StringComparer.OrdinalIgnoreCase);
        // 存储动态求值绑定的属性字典 (Key -> Entry)
        private static readonly Dictionary<string, DynamicMetaEntry> DynamicMetaProperties = new(StringComparer.OrdinalIgnoreCase);

        // 运行时快照
        private static SavePack CurrentGamePack { get; set; }

        public static bool IsInGame => CurrentGamePack != null;
        public static SaveMetadata CurrentSlotMetadata => CurrentGamePack?.Metadata;
        public static string CurrentSlotName => CurrentGamePack?.Metadata?.FileName;
        public static int CurrentSlotId => GetSlotIndexOrDefault(CurrentSlotName, -1);
        public static bool IsVirtualSlot => string.Equals(CurrentSlotName, VIRTUAL_SLOT_NAME, StringComparison.OrdinalIgnoreCase);

        private static readonly Dictionary<string, FactoryEntry> Factories = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<object> EmptyStates = new(0);

        public static Func<IEntityFactory, string> _sceneIdResolver;
        /// <summary>
        /// 场景标识解析器：定义系统如何为工厂定位其所属的场景。(在工厂注册前配置)
        /// 可使用：UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
        /// </summary>
        public static Func<IEntityFactory, string> SceneIdResolver
        {
            private get => _sceneIdResolver;
            set { if (value == null) return; _sceneIdResolver = value; }
        }

        //# ====== 包装类 ====== 

        private class SaveEntryBase
        {
            public WeakReference TargetRef;
            public CancellationTokenRegistration TokenRegistration;
        }
        // 静态实例
        private class SaveTargetEntry : SaveEntryBase
        {
            public SaveScope Scope;
            public string GlobalFileName;
        }
        // 工厂
        private class FactoryEntry : SaveEntryBase
        {
            // 最终的存储 Key（可能是 "Goblin"，也可能是 "Level1#Goblin"）
            public string StorageKey;
        }

        private class DynamicMetaEntry
        {
            public Func<object> Getter;
            public readonly SaveScope? FilterScope;
            public CancellationTokenRegistration TokenRegistration;

            public DynamicMetaEntry(Func<object> getter, SaveScope? filterScope, CancellationTokenRegistration tokenRegistration)
            {
                Getter = getter;
                FilterScope = filterScope;
                TokenRegistration = tokenRegistration;
            }

            public void Release()
            {
                TokenRegistration.Dispose();
                Getter = null;
            }
        }

        //! ==============================
        /// <summary>
        /// 指定物理槽位显式开启新游戏（会在磁盘上立刻物理落盘建档占位）
        /// </summary>
        public static void StartNewGame(int slotIndex) => StartNewGame(FormatSlotName(slotIndex));

        /// <summary>
        /// 显式开启一局新游戏
        /// 1. 若传了具体槽位名（如 "Slot_1"）：视为物理建档，立刻在磁盘上落盘占位。
        /// 2. 若留空（null）：视为纯内存模式（VirtualMemorySlot），不落盘。
        /// </summary>
        public static void StartNewGame(string slotName = null)
        {
            if (IsInGame) { UnloadSlot(); }

            bool isGameSlot = !string.IsNullOrWhiteSpace(slotName);

            // 1. 指定了具体物理槽位：
            if (isGameSlot)
            {
                _eventHub.Publish(SaveEventType.BeforeStartNewGame, slotName);

                SaveSlot(slotName);

                _eventHub.Publish(SaveEventType.NewGameStarted, slotName);
            }
            // 2. 留空：纯内存模式
            else
            {
                CurrentGamePack = new SavePack
                {
                    Metadata = new SaveMetadata
                    {
                        FileName = VIRTUAL_SLOT_NAME,
                        Scope = SaveScope.Game,
                        SaveTime = DateTime.Now.ToString(DEFAULT_TIME_FORMAT, System.Globalization.CultureInfo.InvariantCulture)
                    }
                };
                Debug.Log($"<color=green>[SaveSystem]</color> 已启动纯内存模式（{VIRTUAL_SLOT_NAME}），不生成磁盘文件。");
            }
        }

        /// <summary>
        /// 卸载当前运行中的游戏槽位，退出游戏状态（退回主菜单时调用）
        /// 仅清理运行时内存上下文，不会删除磁盘存档文件。
        /// </summary>
        public static void UnloadSlot()
        {
            CurrentGamePack = null;
        }
        //# ====== 静态实例 ====== 
        /// <summary>
        /// 【单例专用】注册游戏槽位实体，自动提取类型名或 [SaveType] 别名作为唯一 Key。
        /// 适用于全局唯一的管理器、玩家实体等。注意：不可用于同场景多实例组件
        /// </summary>
        public static void RegisterEntity(object instance, bool autoSyncOnDestroy = false)
        {
            if (instance == null) return;
            string saveKey = ResolveSaveKeyFromType(instance);
            RegisterInternal(saveKey, instance, SaveScope.Game, null, autoRestore: true, autoSyncOnDestroy, default);
        }

        /// <summary>
        /// 注册进度存档目标（跟随具体游戏槽位存档，如玩家、背包、场景唯一机关）
        /// 注意：只适用于非动态生成的物体，如果该临时物体被销毁，并且不再注册相同的 SaveKey 可能会造成数据永久残留
        /// 如存档已加载到内存则立刻自动同步一次，未加载则等到下一次 Load 时自动同步
        /// </summary>
        /// <param name="saveKey">对象唯一识别ID</param>
        /// <param name="instance">对象实例</param>
        public static void RegisterEntity(
            string saveKey,
            object instance,
            bool autoRestore = true,
            bool autoSyncOnDestroy = false,
            CancellationToken cancellationToken = default)
        {
            RegisterInternal(saveKey, instance, SaveScope.Game, null, autoRestore, autoSyncOnDestroy, cancellationToken);
        }

        /// <summary>
        /// 【单例专用】注册全局共享实体，自动提取类型名或 [SaveType] 别名作为唯一 Key。
        /// 数据默认写入 "global_settings"，MonoBehaviour 自动托管生命周期，默认立即水合历史数据。
        /// </summary>
        public static void RegisterGlobalEntity(object instance, string fileName = GLOBAL_SLOT_NAME, bool autoSyncOnDestroy = false)
        {
            if (instance == null) return;
            string saveKey = ResolveSaveKeyFromType(instance);
            RegisterInternal(saveKey, instance, SaveScope.Global, fileName, autoRestore: true, autoSyncOnDestroy, default);
        }

        /// <summary>
        /// 注册全局存档对象，可单独指定文件名（跨周目共享，如系统设置、图鉴，进入游戏立即可取）
        /// 如存档已加载到内存则立刻自动同步一次，未加载则等到下一次 Load 时自动同步
        /// </summary>
        /// <param name="saveKey">对象唯一识别ID</param>
        /// <param name="instance">对象实例</param>
        /// <param name="fileName">归属的全局文件名，默认 "global_settings"</param>
        public static void RegisterGlobalEntity(
            string saveKey,
            object instance,
            string fileName = GLOBAL_SLOT_NAME,
            bool autoRestore = true,
            bool autoSyncOnDestroy = false,
            CancellationToken cancellationToken = default)
        {
            RegisterInternal(saveKey, instance, SaveScope.Global, fileName, autoRestore, autoSyncOnDestroy, cancellationToken);
        }

        private static void RegisterInternal(
            string saveKey,
            object instance,
            SaveScope scope,
            string globalFileName,
            bool autoRestore,
            bool autoSyncOnDestroy,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(saveKey) || !IsTargetAlive(instance)) return;

            if (Registry.TryGetValue(saveKey, out var existingEntry))
            {
                var existingTarget = existingEntry.TargetRef?.Target;

                if (ReferenceEquals(existingTarget, instance)) return;

                if (IsTargetAlive(existingTarget))
                {
                    Debug.LogWarning($"[SaveSystem] ID [{saveKey}] 存活的旧实例 [{existingTarget}] 正被新实例 [{instance}] 覆盖。");
                }

                UnregisterEntity(saveKey);
            }

            var entry = new SaveTargetEntry
            {
                TargetRef = new WeakReference(instance),
                Scope = scope,
                GlobalFileName = globalFileName
            };

            // 统一生命周期托管
            CancellationToken finalToken = cancellationToken;
            if (!finalToken.CanBeCanceled && instance is MonoBehaviour mb)
            {
                finalToken = mb.destroyCancellationToken;
            }

            if (finalToken.CanBeCanceled)
            {
                entry.TokenRegistration = finalToken.Register(() =>
                {
                    // 同步到内存
                    if (autoSyncOnDestroy) SyncEntityToMemoryInternal(saveKey, instance);
                    UnregisterIfMatch(Registry, saveKey, instance);
                });
            }

            Registry[saveKey] = entry;

            // 自动分发还原（如果是全局数据，直接从预加载的缓存中秒级水合）
            if (autoRestore)
            {
                if (scope == SaveScope.Game && IsInGame)
                {
                    RestoreSingleEntry(saveKey, entry, CurrentGamePack);
                }
                else if (scope == SaveScope.Global && globalFileName != null)
                {
                    if (GlobalPacksCache.TryGetValue(globalFileName, out var globalPack))
                    {
                        RestoreSingleEntry(saveKey, entry, globalPack);
                    }
                }
            }
        }
        /// <summary>
        /// 注销目标 使用 SaveKey
        /// </summary>
        public static void UnregisterEntity(string saveKey)
        {
            if (string.IsNullOrEmpty(saveKey)) return;

            if (Registry.TryGetValue(saveKey, out var entry))
            {
                entry.TokenRegistration.Dispose();
                Registry.Remove(saveKey);
            }
        }

        /// <summary>
        /// 根据实例的类型或 [SaveType] 别名注销目标
        /// </summary>
        public static void UnregisterEntity(object instance)
        {
            if (instance == null) return;
            UnregisterEntity(ResolveSaveKeyFromType(instance));
        }

        private static void ClearStaticEntityRegistry()
        {
            foreach (var entry in Registry.Values)
            {
                entry.TokenRegistration.Dispose();
            }
            Registry.Clear();
        }

        //# ====== 动态实体工厂 ====== 
        /// <summary>
        /// 【单类型专用工厂】使用工厂类名或其 [SaveType] 别名作为 PrefabId 注册
        /// </summary>
        public static void RegisterFactory(IEntityFactory factory)
        {
            if (factory == null) return;
            string prefabId = ResolveSaveKeyFromType(factory);
            RegisterFactory(prefabId, factory, true, default);
        }

        /// <summary>
        /// 注册动态实体工厂，该工厂实例不应该被动态生成或销毁，可能导致数据不同步。
        /// SaveKey：1. 已配置 SceneIdResolver，且返回值不为null或空时：[场景ID#prefabId]。2. 未配置：[prefabId]
        /// </summary>
        public static void RegisterFactory(
            string prefabId,
            IEntityFactory factory,
            bool autoRestore = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(prefabId) || factory == null) return;

            // 1. 动态生成最终的存储/检索 Key
            string storageKey = BuildFactoryKey(prefabId, factory);

            if (Factories.TryGetValue(storageKey, out var existingEntry))
            {
                var existingTarget = existingEntry.TargetRef?.Target;
                if (ReferenceEquals(existingTarget, factory)) return;

                if (IsTargetAlive(existingTarget))
                {
                    Debug.LogWarning($"[SaveSystem] Factory 目标 [{storageKey}] 发生冲突！存活的旧工厂 [{existingTarget}] 正被新工厂 [{factory}] 覆盖。");
                }

                UnregisterFactory(storageKey);
            }

            var entry = new FactoryEntry
            {
                TargetRef = new WeakReference(factory),
                StorageKey = storageKey
            };

            // 2. 统一生命周期托管（兼容 MonoBehaviour 与非 MonoBehaviour）
            CancellationToken finalToken = cancellationToken;
            if (!finalToken.CanBeCanceled && factory is MonoBehaviour mb)
            {
                finalToken = mb.destroyCancellationToken;
            }

            if (finalToken.CanBeCanceled)
            {
                entry.TokenRegistration = finalToken.Register(() =>
                    UnregisterIfMatch(Factories, storageKey, factory)
                );
            }

            Factories[storageKey] = entry;

            // 3. 自动水合：按计算出的 key 寻找历史数据
            if (autoRestore && IsInGame)
            {
                RestoreSingleFactory(storageKey, factory, CurrentGamePack);
            }
        }

        /// <summary>
        /// 构建动态实体的唯一存储 Key
        /// </summary>
        private static string BuildFactoryKey(string prefabId, IEntityFactory factory)
        {
            var result = prefabId;
            if (SceneIdResolver != null)
            {
                try
                {
                    string scopeId = SceneIdResolver(factory);
                    if (!string.IsNullOrEmpty(scopeId))
                    {
                        result = $"{scopeId}#{prefabId}";
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SaveSystem] SceneIdResolver 执行异常: {ex}");
                }
            }
            // else if (IsTargetAlive(factory) && factory is Component comp && comp.gameObject.scene.IsValid())
            // {
            //     var sceneName = comp.gameObject.scene.name;
            //     result = $"{sceneName}#{prefabId}";
            // }

            return result;
        }

        public static void UnregisterFactory(string registrationKey)
        {
            if (string.IsNullOrEmpty(registrationKey)) return;

            if (Factories.TryGetValue(registrationKey, out var entry))
            {
                entry.TokenRegistration.Dispose();
                Factories.Remove(registrationKey);
            }
        }
        /// <summary>
        /// 根据工厂实例的类型或 [SaveType] 别名注销工厂
        /// </summary>
        public static void UnregisterFactory(IEntityFactory factory)
        {
            if (factory == null) return;
            string storageKey = BuildFactoryKey(ResolveSaveKeyFromType(factory), factory);
            UnregisterFactory(storageKey);
        }

        private static void ClearFactoryRegistry()
        {
            foreach (var Factory in Factories.Values)
            {
                Factory.TokenRegistration.Dispose();
            }
            Factories.Clear();
        }

        //! ===================================
        // 自动推导 SaveKey 优先使用 CustomTypeKey
        private static string ResolveSaveKeyFromType(object instance)
        {
            if (instance == null) return null;
            Type type = instance.GetType();

            // 1. 优先读取 SaveTypeAttribute 上的 CustomTypeKey
            var saveTypeAttr = type.GetCustomAttribute<SaveTypeAttribute>(false);
            if (saveTypeAttr != null && !string.IsNullOrEmpty(saveTypeAttr.CustomTypeKey))
            {
                return saveTypeAttr.CustomTypeKey;
            }

            // 2. 隐式推导：加简短前缀，隔离手动 Key 与隐式自动 Key
            return $"{AUTO_KEY_PREFIX}{type.Name}";
        }

        // 通用检测对象是否存活
        private static bool IsTargetAlive(object target)
        {
            if (target == null) return false;

            if (target is UnityEngine.Object unityObj)
            {
                return unityObj != null;
            }

            return true;
        }
        // 通用安全条件注销：仅当字典中记录的实例确实等于调用者（或旧对象已死亡）时才移除
        private static void UnregisterIfMatch<TEntry>(
            Dictionary<string, TEntry> dict,
            string key,
            object callingInstance) where TEntry : SaveEntryBase
        {
            if (string.IsNullOrEmpty(key)) return;

            if (dict.TryGetValue(key, out var entry))
            {
                var currentTarget = entry.TargetRef?.Target;

                if (ReferenceEquals(currentTarget, callingInstance) || !IsTargetAlive(currentTarget))
                {
                    entry.TokenRegistration.Dispose();
                    dict.Remove(key);
                }
            }
        }
        //# ====== 元数据注入 ====== 
        //! ===================================
        /// <summary>
        /// 声明式绑定动态元数据属性：每次写盘时系统按需调用 getter 获取最新值。
        /// 调用顺序（SaveScope.Game）：BeforeSlotSave -> getter（注入的回调） ->  AfterSlotSave
        /// </summary>
        /// <param name="key">元数据 Key</param>
        /// <param name="getter">值提供函数</param>
        /// <param name="scope">作用域过滤（null 表示 Game 和 Global 都生效，默认仅 Game 生效）</param>
        /// <param name="cancellationToken">生命周期令牌（MonoBehaviour 会自动绑定销毁令牌）</param>
        public static void BindMetaProperty<T>(
            string key,
            Func<T> getter,
            SaveScope? scope = SaveScope.Game,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(key) || getter == null) return;

            // 如果同名 Key 重复注册，先安全解绑旧的
            if (DynamicMetaProperties.ContainsKey(key))
            {
                UnbindMetaProperty(key);
            }

            CancellationTokenRegistration registration = default;

            // 统一生命周期托管（若委托 target 是 MonoBehaviour，自动兜底绑定）
            CancellationToken finalToken = cancellationToken;
            if (!finalToken.CanBeCanceled && getter.Target is MonoBehaviour mb)
            {
                finalToken = mb.destroyCancellationToken;
            }

            if (finalToken.CanBeCanceled)
            {
                registration = finalToken.Register(k => UnbindMetaProperty((string)k), key);
            }

            var entry = new DynamicMetaEntry(() => getter(), scope, registration);
            DynamicMetaProperties[key] = entry;
        }

        /// <summary>
        /// 手动解绑动态元数据属性
        /// </summary>
        public static void UnbindMetaProperty(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            if (DynamicMetaProperties.TryGetValue(key, out var entry))
            {
                entry.Release();
                DynamicMetaProperties.Remove(key);
            }
        }

        private static void ClearDynamicMetaProperties()
        {
            foreach (var entry in DynamicMetaProperties.Values)
            {
                entry.Release();
            }
            DynamicMetaProperties.Clear();
        }

        //! ===================================
        private static bool SyncEntityToMemoryInternal(string saveKey, object instance)
        {
            if (!IsInGame || string.IsNullOrEmpty(saveKey) || !IsTargetAlive(instance)) return false;

            try
            {
                object state = instance is ISaveable saveable
                    ? saveable.CaptureState()
                    : SaveMetadataCache.CaptureReflectionState(instance, isMemorySync: true);

                CurrentGamePack.StaticEntities[saveKey] = state;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveSystem] 内存同步目标 [{saveKey}] 异常: {ex}");
                return false;
            }
        }

        private static void SyncFactoryToMemoryInternal(string storageKey, IEntityFactory factory)
        {
            if (!IsInGame || string.IsNullOrEmpty(storageKey) || !IsTargetAlive(factory)) return;

            try
            {
                var states = factory.CaptureAll();
                if (states != null)
                {
                    CurrentGamePack.DynamicEntities[storageKey] = states;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SaveSystem] 工厂 [{storageKey}] 内存同步异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 将最新数据同步到内存，在场景切换前调用（场景物体被销毁前）
        /// 1. 避免[场景B]读取了旧数据 2. 避免[场景A]中工厂数据不同步，导致下一次依然生成了旧存档的数据
        /// 即使未存档也可将 [场景A] 中的数据转移到 [场景B]。
        /// </summary>
        public static void SyncAllToMemory()
        {
            // 保证 CurrentGamePack 不为null
            if (!IsInGame) StartNewGame();

            // 1. 同步静态实体
            // 备注：静态实例在销毁时会使用ct自动同步以及注销，字典不会存在已销毁的实例
            foreach (var kvp in Registry)
            {
                var entry = kvp.Value;

                var target = entry.TargetRef?.Target;

                SyncEntityToMemoryInternal(kvp.Key, target);
            }

            // 2. 同步动态实体工厂
            // 备注：工厂和动态生成实体销毁时序不确定，所以不使用ct自动同步，或后续考虑生成时自动抓取LinkedCancellationToken
            foreach (var kvp in Factories)
            {
                var target = kvp.Value.TargetRef?.Target;

                if (target is IEntityFactory factory)
                {
                    SyncFactoryToMemoryInternal(kvp.Key, factory);
                }
            }
        }

        //! ===================================

        /// <summary>
        /// 业务层严格验证存档内容是否有效
        /// </summary>
        private static bool ValidateAndNormalizePack(SavePack pack, string expectedFileName, SaveScope expectedScope)
        {
            if (pack == null) return false;

            if (pack.Metadata == null)
            {
                Debug.LogError($"<color=red>[SaveSystem]</color> 业务校验失败：缺少 Metadata 元数据节点！");
                return false;
            }

            if (string.IsNullOrWhiteSpace(pack.Metadata.DataHash))
            {
                Debug.LogError($"<color=red>[SaveSystem]</color> 完整性校验失败：缺少有效的 DataHash 数据指纹！");
                return false;
            }

            if (string.IsNullOrWhiteSpace(pack.Metadata.FileName) ||
                !string.Equals(pack.Metadata.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError($"<color=red>[SaveSystem]</color> 业务校验失败：FileName 无效或与请求目标不一致（读取目标: {expectedFileName}, 档内记录: {pack.Metadata.FileName}）");
                return false;
            }

            if (pack.Metadata.Scope != expectedScope)
            {
                Debug.LogError($"<color=red>[SaveSystem]</color> 业务校验失败：Scope 作用域不匹配（预期: {expectedScope}, 档内记录: {pack.Metadata.Scope}）");
                return false;
            }

            // 2. 验证数据载荷节点存在性（防 external 篡改为 null）
            if (pack.StaticEntities == null || pack.DynamicEntities == null)
            {
                Debug.LogError($"<color=red>[SaveSystem]</color> 业务校验失败：StaticEntities 或 DynamicEntities 节点丢失！");
                return false;
            }

            return true;
        }
        //! ===================================

        /// <summary>
        /// 将槽位索引格式化为标准槽位名
        /// </summary>
        public static string FormatSlotName(int slotIndex)
        {
            return string.Format(SlotNameFormat, slotIndex);
        }

        /// <summary>
        /// 可选实用工具：尝试从槽位名中解析出数字索引（便于 UI 列表根据文件名逆向绑定槽位高亮）
        /// </summary>
        public static bool TryParseSlotIndex(string slotName, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(slotName)) return false;

            // 如果名字以纯数字结尾，提取末尾数字
            int i = slotName.Length - 1;
            while (i >= 0 && char.IsDigit(slotName[i])) i--;

            if (i < slotName.Length - 1)
            {
                return int.TryParse(slotName.Substring(i + 1), out index);
            }

            return false;
        }

        public static int GetSlotIndexOrDefault(string slotName, int defaultIndex = -1)
        {
            return TryParseSlotIndex(slotName, out int result) ? result : defaultIndex;
        }

        public static SaveMetadata SaveSlot(int slotIndex)
            => SaveSlot(FormatSlotName(slotIndex));

        public static SaveMetadata LoadSlot(int slotIndex)
            => LoadSlot(FormatSlotName(slotIndex));

        public static bool HasSlot(int slotIndex)
            => StorageProvider?.HasFile(FormatSlotName(slotIndex), SaveScope.Game) == true;

        public static bool DeleteSlot(int slotIndex)
            => DeleteFile(FormatSlotName(slotIndex), SaveScope.Game);

        public static SaveMetadata GetSlotMetadata(int slotIndex)
            => StorageProvider?.LoadMetadata(FormatSlotName(slotIndex), SaveScope.Game);

        public static List<SaveMetadata> GetAllSlotsMetadata()
            => StorageProvider?.GetAllFilesMetadata(SaveScope.Game);

        public static SaveMetadata GetGlobalFileMetadata(string fileName)
            => StorageProvider?.LoadMetadata(fileName, SaveScope.Global);

        public static List<SaveMetadata> GetAllGlobalFilesMetadata()
            => StorageProvider?.GetAllFilesMetadata(SaveScope.Global);

        /// <summary>
        /// 获取存档物理路径：1. scope：返回存档根文件夹路径；2. scope + fileName：返回存档文件路径，文件不存在则返回 null。
        /// </summary>
        public static string GetPath(SaveScope scope, string fileName = null)
            => StorageProvider?.GetPath(scope, fileName);

        public static string GetSlotPath(int slotIndex) => GetPath(SaveScope.Game, FormatSlotName(slotIndex));

        /// <summary>
        /// 尝试恢复存档，可指定 loadImmediately 立即加载恢复后的存档。（可订阅 LoadFailed 事件尝试自动恢复）
        /// </summary>
        public static bool RestoreFileBackup(string fileName, SaveScope scope)
        {
            if (StorageProvider == null) return false;

            return StorageProvider.RestoreBackup(fileName, scope, pack =>
                ValidateAndNormalizePack(pack, fileName, scope)
            );
        }

        /// <summary>
        /// 保存进度档
        /// </summary>
        /// <param name="slotName">文件名 (槽位)</param>
        public static SaveMetadata SaveSlot(string slotName)
        {
            if (StorageProvider == null) return null;
            //  广播：存盘即将开始
            _eventHub.Publish(SaveEventType.BeforeSlotSave, slotName);

            // 复用公共打包，过滤出 Game
            var pack = CreatePackInternal(slotName, SaveScope.Game, e => e.Scope == SaveScope.Game);

            // 静态实体防丢档合并机制
            // 如果内存中有旧存档数据（说明当前槽位已加载过），
            // 继承所有旧档里存在、但本次场景未加载/未注册的静态物体数据
            if (CurrentGamePack?.StaticEntities != null)
            {
                foreach (var kvp in CurrentGamePack.StaticEntities)
                {
                    if (!pack.StaticEntities.ContainsKey(kvp.Key))
                    {
                        pack.StaticEntities[kvp.Key] = kvp.Value;
                    }
                }
            }

            // 进度档额外附加动态实体
            CollectDynamicEntities(pack);

            var request = new SaveRequest(pack,
                checkDirty: false,
                createBackup: EnableBackup,
                encrypt: EnableEncryption);

            SaveResult saveResult = StorageProvider.Save(request);

            // 失败
            if (!saveResult.IsSuccess)
            {
                Debug.LogError($"[SaveSystem] 存档 [{slotName}] 失败: {saveResult.Exception?.Message}");
                _eventHub.Publish(SaveEventType.SlotSaveFailed, slotName);
                return null;
            }

            CurrentGamePack = pack;

            if (saveResult.IsWritten)
            {
                Debug.Log($"<color=green>[SaveSystem]</color> <color=white>[{slotName}]</color> 物理落盘完成。");
            }
            else if (saveResult.Status == SaveStatus.Skipped)
            {
                Debug.Log($"<color=yellow>[SaveSystem]</color> <color=white>[{slotName}]</color> 数据无变动（同帧防抖），已安全跳过写盘。");
            }

            // 广播：存盘结束
            _eventHub.Publish(SaveEventType.AfterSlotSave, slotName);
            return pack.Metadata;
        }

        /// <summary>
        /// 加载进度存档
        /// </summary>
        /// <param name="slotName">槽位名（文件名）</param>
        /// <returns> 当目标不存在或为通过验证返回 null，支持隐式 bool 转换 </returns>
        public static SaveMetadata LoadSlot(string slotName)
        {
            if (StorageProvider == null)
            {
                Debug.LogWarning($"<color=yellow>[SaveSystem]</color> StorageProvider 尚未就绪，读档请求 [{slotName}] 已挂起，将在初始化完成后自动执行。(本次调用不会获取返回值)");
                _pendingLoadAction = () => LoadSlot(slotName);
                return null;
            }

            // 广播：读盘即将开始
            _eventHub.Publish(SaveEventType.BeforeSlotLoad, slotName);

            var request = new LoadRequest(slotName, SaveScope.Game,
                pack => ValidateAndNormalizePack(pack, slotName, SaveScope.Game));

            var loadResult = StorageProvider.Load(request);
            // A. 空槽位（文件不存在）
            if (loadResult.Status == LoadStatus.FileNotFound)
            {
                _eventHub.Publish(SaveEventType.SlotFileNotFound, slotName);
                return null;
            }

            // B. 存档彻底损坏
            if (loadResult.Status == LoadStatus.Corrupted)
            {
                Debug.LogError($"<color=red>[SaveSystem]</color> [{slotName}] 读取解析失败: {loadResult.Exception?.Message}");
                _eventHub.Publish(SaveEventType.SlotFileCorrupted, slotName);
                return null;
            }

            var pack = loadResult.Pack;

            CurrentGamePack = pack;

            // A. 还原静态实体 
            var registrySnapshot = new KeyValuePair<string, SaveTargetEntry>[Registry.Count];
            ((ICollection<KeyValuePair<string, SaveTargetEntry>>)Registry).CopyTo(registrySnapshot, 0);

            for (int i = 0; i < registrySnapshot.Length; i++)
            {
                var kvp = registrySnapshot[i];
                if (kvp.Value.Scope == SaveScope.Game)
                {
                    RestoreSingleEntry(kvp.Key, kvp.Value, pack);
                }
            }

            // B. 批量重建当前所有已就绪工厂的动态物体
            RestoreDynamicEntities(pack);

            Debug.Log($"<color=green>[SaveSystem]</color> <color=white>[{slotName}]</color> 加载完成。");
            // 广播：读盘完毕，实体已就绪
            _eventHub.Publish(SaveEventType.AfterSlotLoad, slotName);

            return pack.Metadata;
        }

        /// <summary>
        /// 保存全局存档。
        /// 若不指定文件名，则会自动保存所有已注册/已加载的全局模块文件。
        /// </summary>
        /// <param name="targetFileName">可选：若指定，则仅保存该特定文件；留空则保存全部全局文件。</param>
        public static bool SaveGlobal(string targetFileName = null)
        {
            bool success = false;
            if (StorageProvider == null) return success;

            _eventHub.Publish(SaveEventType.BeforeGlobalSave, targetFileName);

            // 1. 如果指定了特定文件，仅保存该文件
            if (!string.IsNullOrEmpty(targetFileName))
            {
                success |= SaveSingleGlobalInternal(targetFileName);
                if (success) _eventHub.Publish(SaveEventType.GlobalSaved, targetFileName);
                return success;
            }

            // 2. 否则，搜集当前所有涉及的全局文件名集合
            // 来源 A：当前 Registry 中登记的所有全局对象的归属文件
            // 来源 B：已在内存缓存 GlobalPacksCache 中的文件
            var allGlobalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in GlobalPacksCache.Keys)
            {
                allGlobalFiles.Add(key);
            }

            foreach (var entry in Registry.Values)
            {
                if (entry.Scope == SaveScope.Global && !string.IsNullOrEmpty(entry.GlobalFileName))
                {
                    allGlobalFiles.Add(entry.GlobalFileName);
                }
            }

            // 3. 逐个落盘
            foreach (var fileName in allGlobalFiles)
            {
                success |= SaveSingleGlobalInternal(fileName);
            }
            return success;
        }

        /// <summary>
        /// 提取单个文件的保存内核
        /// </summary>
        private static bool SaveSingleGlobalInternal(string fileName)
        {
            // 抓取当前内存中所有已就绪的对象状态
            var pack = CreatePackInternal(fileName, SaveScope.Global, e =>
                e.Scope == SaveScope.Global && string.Equals(e.GlobalFileName, fileName, StringComparison.OrdinalIgnoreCase)
            );

            // 如果缓存中有上一轮读入的完整数据（说明不是第一次建档，磁盘曾有旧数据）
            if (GlobalPacksCache.TryGetValue(fileName, out var cachedPack) && cachedPack.StaticEntities != null)
            {
                foreach (var kvp in cachedPack.StaticEntities)
                {
                    // 如果旧存档里有这个 Key，但当前 Registry 里还没人认领（因为对应场景没加载），必须原样继承！
                    if (!pack.StaticEntities.ContainsKey(kvp.Key))
                    {
                        pack.StaticEntities[kvp.Key] = kvp.Value;
                    }
                }
            }

            // 如果没有任何数据需要存，跳过物理写盘
            if (pack.StaticEntities.Count == 0)
            {
                return false;
            }

            var request = new SaveRequest(pack,
                checkDirty: !SkipHashCheck, // 如果全局开了强制跳过则为 false，否则默认开启去重
                createBackup: EnableBackup,
                encrypt: EnableEncryption);

            SaveResult saveResult = StorageProvider.Save(request);

            GlobalPacksCache[fileName] = pack;
            if (saveResult.IsWritten)
            {
                Debug.Log($"<color=green>[SaveSystem]</color> <color=white>[{fileName}]</color> 已存档");
            }

            _eventHub.Publish(SaveEventType.GlobalSaved, fileName);
            return saveResult;
        }

        /// <summary>
        /// 加载全局设置
        /// </summary>
        public static void PreloadAllGlobals()
        {
            if (StorageProvider == null) return;

            GlobalPacksCache.Clear();
            var globalMetaList = StorageProvider.GetAllFilesMetadata(SaveScope.Global);

            foreach (var meta in globalMetaList)
            {
                var fileName = meta.FileName;
                var request = new LoadRequest(fileName, SaveScope.Global,
                    pack => ValidateAndNormalizePack(pack, fileName, SaveScope.Global));

                var loadResult = StorageProvider.Load(request);

                if (loadResult.Status == LoadStatus.FileNotFound)
                {
                    _eventHub.Publish(SaveEventType.GlobalFileNotFound, fileName);
                }
                else if (loadResult.Status == LoadStatus.Corrupted)
                {
                    Debug.LogError($"<color=red>[SaveSystem]</color> [{fileName}] 读取解析失败: {loadResult.Exception?.Message}");
                    // 备注：这时 MonoBehaviour 组件未注册，无法通过订阅事件来恢复存档
                    // 必须在 AfterAssembliesLoaded 中订阅事件，之后会在BeforeSceneLoad初始化完成后广播
                    _eventHub.Publish(SaveEventType.GlobalFileCorrupted, fileName);
                }
                else if (loadResult.Status == LoadStatus.Success)
                {
                    Debug.Log($"<color=green>[SaveSystem]</color> 全局存档 <color=cyan>[{fileName}]</color> 加载完成。");
                    GlobalPacksCache[fileName] = loadResult.Pack;
                }
            }
            // 如果极早初始化的静态类已在 Registry 中，顺手给它们同步一次
            foreach (var kvp in Registry)
            {
                var entry = kvp.Value;
                if (entry.Scope == SaveScope.Global && entry.GlobalFileName != null)
                {
                    if (GlobalPacksCache.TryGetValue(entry.GlobalFileName, out var pack))
                    {
                        RestoreSingleEntry(kvp.Key, entry, pack);
                    }
                }
            }
        }

        /// <summary>
        /// 【存盘辅助】打包动态物体（带防丢档合并机制）
        /// </summary>
        private static void CollectDynamicEntities(SavePack newPack)
        {
            // 步骤 1：原样继承旧档里所有未被当前场景激活的工厂数据（其他关卡的怪物数据绝对安全！）
            if (CurrentGamePack?.DynamicEntities != null)
            {
                foreach (var kvp in CurrentGamePack.DynamicEntities)
                {
                    // kvp.Key 现在是 "Level_01#Goblin" 这种复合键
                    if (!Factories.ContainsKey(kvp.Key))
                    {
                        newPack.DynamicEntities[kvp.Key] = kvp.Value;
                    }
                }
            }

            // 步骤 2：抓取当前场景所有激活工厂的最新数据
            foreach (var kvp in Factories)
            {
                string storageKey = kvp.Key;
                var target = kvp.Value.TargetRef.Target;

                if (!IsTargetAlive(target)) continue;

                var factory = (IEntityFactory)target;
                var states = factory.CaptureAll();

                if (states != null)
                {
                    newPack.DynamicEntities[storageKey] = states;
                }
            }
        }

        /// <summary>
        /// 【读盘辅助】全量还原所有动态物体
        /// </summary>
        private static void RestoreDynamicEntities(SavePack pack)
        {
            // 快照防止遍历时工厂集合被修改
            var factoryList = new List<KeyValuePair<string, FactoryEntry>>(Factories);

            foreach (var kvp in factoryList)
            {
                try
                {
                    string prefabId = kvp.Key;
                    var target = kvp.Value.TargetRef.Target;
                    if (!IsTargetAlive(target)) continue;

                    var factory = (IEntityFactory)target;
                    var states = pack.DynamicEntities?.GetValueOrDefault(prefabId) ?? EmptyStates;

                    factory.RestoreAll(states);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SaveSystem] 工厂 [{kvp.Key}] 批量恢复实体时发生致命异常: {ex}");
                }
            }
        }
        /// <summary>
        /// 【读盘时 / 工厂后置注册时】统一调用的单工厂还原逻辑
        /// </summary>
        private static void RestoreSingleFactory(string prefabId, IEntityFactory factory, SavePack pack)
        {
            var states = pack.DynamicEntities?.GetValueOrDefault(prefabId) ?? EmptyStates;

            factory.RestoreAll(states);
        }

        /// <summary>
        /// 统一打包函数：根据谓词过滤器抓取静态数据
        /// </summary>
        private static SavePack CreatePackInternal(
            string slotOrFileName,
            SaveScope scope,
            Func<SaveTargetEntry, bool> filter)
        {
            var meta = new SaveMetadata
            {
                FileName = slotOrFileName,
                Scope = scope,
                SaveTime = DateTime.Now.ToString(DEFAULT_TIME_FORMAT, System.Globalization.CultureInfo.InvariantCulture),
            };
            foreach (var kvp in DynamicMetaProperties)
            {
                var entry = kvp.Value;
                if (entry.FilterScope == null || entry.FilterScope.Value == scope)
                {
                    try
                    {
                        meta.ExtraData[kvp.Key] = entry.Getter?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[SaveSystem] 获取动态元数据 [{kvp.Key}] 失败: {ex}");
                    }
                }
            }

            var pack = new SavePack { Metadata = meta };

            foreach (var kvp in Registry)
            {
                var entry = kvp.Value;
                if (!filter(entry)) continue;

                var target = entry.TargetRef.Target;
                if (!IsTargetAlive(target)) continue;

                string saveKey = kvp.Key;

                if (target is ISaveable saveable)
                {
                    try
                    {
                        pack.StaticEntities[saveKey] = saveable.CaptureState();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[SaveSystem] 实体 [{saveKey}] CaptureState 执行异常: {ex}");
                    }
                }

                else
                {
                    pack.StaticEntities[saveKey] = SaveMetadataCache.CaptureReflectionState(target);
                }
            }

            return pack;
        }

        private static void RestoreSingleEntry(string saveKey, SaveTargetEntry entry, SavePack pack)
        {
            var target = entry.TargetRef.Target;
            if (!IsTargetAlive(target)) return;

            if (pack.StaticEntities != null && pack.StaticEntities.TryGetValue(saveKey, out var state))
            {
                try
                {
                    if (target is ISaveable saveable)
                    {
                        saveable.RestoreState(state);
                    }
                    else
                    {
                        SaveMetadataCache.RestoreReflectionState(target, state);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SaveSystem] 实体 [{saveKey}] 还原数据失败: {ex}");
                }
            }
        }

        public static bool DeleteFile(string slotName, SaveScope scope)
        {
            if (StorageProvider == null)
            {
                Debug.LogError("[SaveSystem] 删除失败：未配置 StorageProvider！");
                return false;
            }

            if (string.IsNullOrWhiteSpace(slotName) || slotName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Debug.LogError($"[SaveSystem] 非法的存档名: '{slotName}'");
                return false;
            }

            if (!StorageProvider.HasFile(slotName, scope))
            {
                Debug.LogWarning($"[SaveSystem] 目标存档不存在: '{slotName}' ({scope})");
                return false;
            }

            try
            {
                var meta = StorageProvider.LoadMetadata(slotName, scope);
                if (meta == null || meta.DataHash == null || meta.SaveTime == null || meta.FileName != slotName || meta.Scope != scope)
                {
                    Debug.LogWarning($"[SaveSystem] 目标文件元数据已损坏或不是合法的存档文件: {slotName}");
                    return false;
                }

                if (StorageProvider.DeleteFile(slotName, scope))
                {
                    var saveEvent = scope == SaveScope.Game ? SaveEventType.SlotDeleted : SaveEventType.GlobalFileDeleted;
                    _eventHub.Publish(saveEvent, slotName);
                }
                else
                {
                    return false;
                }

                if (scope == SaveScope.Game && string.Equals(CurrentSlotName, slotName, StringComparison.Ordinal))
                {
                    UnloadSlot();
                }
                else if (scope == SaveScope.Global)
                {
                    GlobalPacksCache.Remove(slotName);
                }

                Debug.Log($"<color=green>[SaveSystem]</color> [{scope}] 槽位 [{slotName}] 已彻底清理！");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveSystem] 删除槽位 [{slotName}] 过程中遇到未知错误: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 安全修剪指定存档（支持进度档与全局档）。
        /// </summary>
        /// <param name="fileName">槽位名（如 "Slot_0"）或全局文件名（如 "global_settings"）</param>
        /// <param name="scope">作用域：Game 进度档 或 Global 全局档</param>
        /// <param name="shouldRemoveStaticKey">断言：返回 true 表示该 Key 为废弃数据，予以删除；返回 false 保留</param>
        /// <param name="shouldRemovePrefab">断言：返回 true 表示该 PrefabId 已作废，予以删除；返回 false 保留</param>
        /// <param name="enableBackup">是否无视全局设置开启备份？默认：true</param>
        public static bool PruneFile(
            string fileName,
            SaveScope scope = SaveScope.Game,
            Func<string, bool> shouldRemoveStaticKey = null,
            Func<string, bool> shouldRemovePrefab = null,
            bool enableBackup = true)
        {
            if (StorageProvider == null || !StorageProvider.HasFile(fileName, scope))
            {
                Debug.LogWarning($"[SaveSystem] 目标文件不存在，跳过修剪: [{fileName}] ({scope})");
                return false;
            }

            // 1. 读取原档
            var loadResult = StorageProvider.Load(new LoadRequest(fileName, scope,
                pack => ValidateAndNormalizePack(pack, fileName, scope)));

            if (!loadResult.IsSuccess)
            {
                Debug.LogError($"[SaveSystem] 修剪失败，无法正常加载目标存档: [{fileName}] ({scope})");
                return false;
            }

            var pack = loadResult.Pack;
            bool modified = false;

            // 2. 清理静态实体（Game / Global 通用）
            if (shouldRemoveStaticKey != null && pack.StaticEntities != null)
            {
                var keysToRemove = new List<string>();
                foreach (var key in pack.StaticEntities.Keys)
                {
                    // 命中了删除条件（返回 true），加入删除清单
                    if (shouldRemoveStaticKey(key))
                    {
                        keysToRemove.Add(key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    pack.StaticEntities.Remove(key);
                    modified = true;
                }
            }

            // 3. 清理动态实体（主要针对 Game 档）
            if (shouldRemovePrefab != null && pack.DynamicEntities != null)
            {
                var prefabsToRemove = new List<string>();
                foreach (var prefabId in pack.DynamicEntities.Keys)
                {
                    // 命中了删除条件（返回 true），加入删除清单
                    if (shouldRemovePrefab(prefabId))
                    {
                        prefabsToRemove.Add(prefabId);
                    }
                }

                foreach (var prefabId in prefabsToRemove)
                {
                    pack.DynamicEntities.Remove(prefabId);
                    modified = true;
                }
            }

            // 如果没有任何改动，直接视为成功并退出
            if (!modified) return true;

            // 同步更新内存中的运行时缓存，防止合并机制将其复活
            if (scope == SaveScope.Global)
            {
                GlobalPacksCache[fileName] = pack;
            }
            else if (scope == SaveScope.Game && string.Equals(CurrentSlotName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                CurrentGamePack = pack;
            }

            // 重新落盘（强制写入以规避 Hash 未变检查）
            var request = new SaveRequest(
                pack,
                checkDirty: false,
                createBackup: EnableBackup || enableBackup,
                encrypt: EnableEncryption);

            bool saveSuccess = StorageProvider.Save(request);
            if (saveSuccess)
            {
                Debug.Log($"<color=green>[SaveSystem]</color> 存档 [{fileName}] ({scope}) 已成功完成修剪并同步！");
            }

            return saveSuccess;
        }
    }

}

//! ============================================
//! Adapters
//! ============================================
namespace Framework.SaveManagement.Storage
{

    // 存档包
    public readonly struct SaveRequest
    {
        public SavePack Pack { get; }

        /// <summary> 是否启用 Hash 脏检查去重（true: 数据无变化时跳过写盘；false: 无论如何直接落盘） </summary>
        public bool CheckDirty { get; }

        /// <summary> 是否生成 .bak 备份文件 </summary>
        public bool CreateBackup { get; }

        /// <summary> 是否通过后台线程写入（异步优化扩展预留） </summary>
        public bool WriteAsync { get; }

        /// <summary> 是否对存档执行压缩加密 </summary>
        public bool Encrypt { get; }

        // 主构造函数：支持指定全部参数
        public SaveRequest(
            SavePack pack,
            bool checkDirty = false,
            bool createBackup = false,
            bool encrypt = false,
            bool writeAsync = false)
        {
            Pack = pack;
            CheckDirty = checkDirty;
            CreateBackup = createBackup;
            Encrypt = encrypt;
            WriteAsync = writeAsync;
        }

        // 隐式转换：允许直接把 SavePack 传给需要 SaveRequest 的接口（使用全套默认参数）
        public static implicit operator SaveRequest(SavePack pack) => new(pack);
    }

    // 读档包
    public readonly struct LoadRequest
    {
        public string FileName { get; }
        public SaveScope Scope { get; }

        /// <summary> 
        /// 提取目标子节点的标识（例如 "Metadata"、"PlayerInfo"）。
        /// 若为 null，表示全量读取；若指定，则只流式读取该局部节点。
        /// </summary>
        public string TargetNode { get; }

        public Func<SavePack, bool> Validator { get; }

        /// <summary> 当目标主存档损坏或不存在时，是否自动尝试从 .bak 备份文件还原 </summary>
        // public bool AutoFallbackToBackup { get; }

        public LoadRequest(string fileName, SaveScope scope = SaveScope.Game, Func<SavePack, bool> validator = null)
        {
            FileName = fileName;
            Scope = scope;
            TargetNode = null;
            Validator = validator;
            // AutoFallbackToBackup = true;
        }
        public static implicit operator LoadRequest(string fileName) => new(fileName, SaveScope.Game);
    }

    public enum LoadStatus
    {
        Success,           // 读档成功
        FileNotFound,      // 文件不存在（空槽位，属于正常现象）
        Corrupted,         // 文件损坏 / JSON解析失败 / 解密失败
    }

    public readonly struct LoadResult
    {
        public LoadStatus Status { get; }
        public SavePack Pack { get; }
        public Exception Exception { get; }

        public bool IsSuccess => Status == LoadStatus.Success;

        private LoadResult(LoadStatus status, SavePack pack, Exception ex = null)
        {
            Status = status;
            Pack = pack;

            Exception = ex;
        }

        public static LoadResult Ok(SavePack pack)
            => new(LoadStatus.Success, pack);

        public static LoadResult NotFound()
            => new(LoadStatus.FileNotFound, null);

        public static LoadResult Fail(Exception ex)
            => new(LoadStatus.Corrupted, null, ex);

        // 隐式 bool 支持原有 if(result) 语法
        public static implicit operator bool(LoadResult result) => result.IsSuccess;
    }

    public enum SaveStatus
    {
        Success,  // 物理写盘成功（文件已落盘）
        Skipped,  // 命中 Hash 校验或同帧防抖，安全跳过（无需物理写盘）
        Failed    // 写盘失败（抛出异常、文件只读、磁盘已满等）
    }

    public readonly struct SaveResult
    {
        public SaveStatus Status { get; }
        public Exception Exception { get; }

        /// <summary> 
        /// 业务层面的“保存是否安全/成功”（Success 和 Skipped 均返回 true）
        /// </summary>
        public bool IsSuccess => Status == SaveStatus.Success || Status == SaveStatus.Skipped;

        /// <summary> 
        /// 物理层面是否真实写入了磁盘（仅 Success 返回 true）
        /// </summary>
        public bool IsWritten => Status == SaveStatus.Success;

        private SaveResult(SaveStatus status, Exception ex = null)
        {
            Status = status;
            Exception = ex;
        }

        public static SaveResult Ok() => new(SaveStatus.Success);
        public static SaveResult Skip() => new(SaveStatus.Skipped);
        public static SaveResult Fail(Exception ex) => new(SaveStatus.Failed, ex);

        // 核心：隐式转换为 bool。只要没报错（即 IsSuccess），就视为 true！
        public static implicit operator bool(SaveResult result) => result.IsSuccess;
    }
    //!========================================
    // Newtonsoft
    public class NewtonsoftStorageProvider : IStorageProvider
    {
        // 定义子文件夹名称常量
        private const string GLOBAL_DIR_NAME = "Global";
        private const string SAVES_DIR_NAME = "Saves";
        private const int MAX_DEPTH = 32;

        private const string JSON_EXT = ".json";    // 明文后缀
        private const string ENCRYPTED_EXT = ".sav"; // 加密压缩后缀

        // 缓存完整的绝对路径
        private static string _globalDirectory;
        private static string _savesDirectory;
        private static string GlobalDirectory => _globalDirectory ??= Path.GetFullPath(Path.Combine(Application.persistentDataPath, GLOBAL_DIR_NAME));
        private static string SavesDirectory => _savesDirectory ??= Path.GetFullPath(Path.Combine(Application.persistentDataPath, SAVES_DIR_NAME));

        // 记录每个槽位上一次物理落盘时的数据指纹 (SlotName -> Hash)
        private static readonly Dictionary<string, string> SavedHashesCache = new(StringComparer.OrdinalIgnoreCase);

        // 配置
        private static readonly JsonSerializerSettings Settings = new()
        {
            Formatting = Formatting.Indented,
            TypeNameHandling = TypeNameHandling.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            ContractResolver = SafeContractResolver.Instance,
            MaxDepth = MAX_DEPTH
        };

        private static JsonSerializer _serializer;
        public static JsonSerializer Serializer => _serializer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            // 确保存档子目录存在
            EnsureDirectoriesExist();

            Settings.Converters.Add(new StringEnumConverter());
            Settings.Converters.Add(new VectorConverter());
            Settings.Converters.Add(new PoseConverter());
            Settings.Converters.Add(new ColorConverter());
            Settings.Converters.Add(new UniversalPolymorphicConverter());

            _serializer = JsonSerializer.Create(Settings);

            SaveConvert.CustomSerializer = _serializer;

            SaveSystem.StorageProvider = new NewtonsoftStorageProvider();

            Debug.Log("<color=green>[SaveSystem]</color> <color=white>Newtonsoft.Json 已就绪！</color>");

            // 加载全局存档
            SaveSystem.PreloadAllGlobals();
        }

        public string GetPath(SaveScope scope, string fileName = null)
        {
            string directory = GetDirectoryByScope(scope);

            // 未指定文件名，纯粹索取文件夹目录
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return directory;
            }

            // 指定了文件名，获取文件的完整绝对路径
            // A. 如果用户传进来的名字本身已经带了后缀（如 "Slot_0.sav"），直接拼接返回
            if (Path.HasExtension(fileName))
            {
                return Path.Combine(directory, fileName);
            }

            // 如果磁盘上已经存在该文件（无论是 .json 还是 .sav），直接返回真实存在的物理路径
            string existingPath = ResolveReadFilePath(fileName, scope);
            if (File.Exists(existingPath))
            {
                return existingPath;
            }

            // 磁盘尚不存在该文件
            return null;
        }

        /// <summary>
        /// 【写盘专用】：根据是否加密直接确定输出文件的扩展名
        /// </summary>
        private string GetWriteFilePath(string slotName, SaveScope scope, bool isEncrypted)
        {
            string ext = isEncrypted ? ENCRYPTED_EXT : JSON_EXT;
            return Path.Combine(GetDirectoryByScope(scope), $"{slotName}{ext}");
        }

        /// <summary>
        /// 【读盘专用（自适应探测）】：根据槽位名智能寻找真实存在的文件路径。
        /// 优先级：明文(.json) -> 加密(.sav) -> 兜底默认路径
        /// </summary>
        private string ResolveReadFilePath(string slotName, SaveScope scope)
        {
            string dir = GetDirectoryByScope(scope);

            // 查明文后缀文件 (.json)
            string jsonPath = Path.Combine(dir, $"{slotName}{JSON_EXT}");
            if (File.Exists(jsonPath)) return jsonPath;

            // 查加密后缀文件 (.sav)
            string newPath = Path.Combine(dir, $"{slotName}{ENCRYPTED_EXT}");
            if (File.Exists(newPath)) return newPath;

            return jsonPath;
        }

        /// <summary>
        /// 探测指定槽位可能存在的备份文件路径
        /// 优先级：对应扩展名的 .bak -> 另一种扩展名的 .bak
        /// </summary>
        private string ResolveBackupFilePath(string slotName, SaveScope scope)
        {
            string dir = GetDirectoryByScope(scope);

            // 优先寻找与当前模式同名的 .bak
            string savBak = Path.Combine(dir, $"{slotName}{ENCRYPTED_EXT}.bak");
            if (File.Exists(savBak)) return savBak;

            string jsonBak = Path.Combine(dir, $"{slotName}{JSON_EXT}.bak");
            if (File.Exists(jsonBak)) return jsonBak;

            return null;
        }

        /// <summary>
        /// 根据作用域获取文件夹路径
        /// </summary>
        private string GetDirectoryByScope(SaveScope scope)
        {
            return scope == SaveScope.Global ? GlobalDirectory : SavesDirectory;
        }

        // 确保路径
        private static void EnsureDirectoriesExist()
        {
            if (!Directory.Exists(GlobalDirectory)) Directory.CreateDirectory(GlobalDirectory);
            if (!Directory.Exists(SavesDirectory)) Directory.CreateDirectory(SavesDirectory);
        }

        public SaveResult Save(SaveRequest request)
        {
            var pack = request.Pack;
            var meta = pack?.Metadata;
            var slotName = meta?.FileName;
            if (string.IsNullOrWhiteSpace(slotName))
                return SaveResult.Fail(new ArgumentException("SlotName 不能为空！"));

            string path = GetWriteFilePath(slotName, meta.Scope, request.Encrypt);
            string tempPath = path + ".tmp";
            string backupPath = path + ".bak";

            try
            {
                // 1. 生成 Hash 指纹（带同帧防抖）
                string currentHash;
                if (request.CheckDirty)
                {
                    var payloadObj = new { pack.StaticEntities, pack.DynamicEntities };
                    currentHash = SafeFileUtil.FastHash.ComputeFnv1a64(JsonConvert.SerializeObject(payloadObj, Settings));
                }
                else
                {
                    // 极速模式：秒级时间戳 + 帧编号，保证同帧绝对幂等
                    currentHash = SafeFileUtil.FastHash.ComputeFnv1a64($"{slotName}_{pack.Metadata.SaveTime}_{Time.frameCount}");
                }

                pack.Metadata.DataHash = currentHash;

                // 2. 检查是否可跳过写盘
                bool canSkip = SavedHashesCache.TryGetValue(slotName, out var lastHash)
                            && string.Equals(lastHash, currentHash, StringComparison.Ordinal)
                            && File.Exists(path);

                if (canSkip)
                {
                    return SaveResult.Skip();
                }

                // 3. 物理落盘写入临时文件
                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var secureStream = SaveSecurityUtil.CreateWriteStream(fileStream, request.Encrypt))
                using (var streamWriter = new StreamWriter(secureStream, Encoding.UTF8))
                using (var jsonWriter = new JsonTextWriter(streamWriter))
                {
                    // 加密/压缩时关闭缩进提升写盘速度与压缩极限；明文时保留 Indented
                    jsonWriter.Formatting = request.Encrypt ? Formatting.None : Formatting.Indented;

                    _serializer.Serialize(jsonWriter, pack);
                    jsonWriter.Flush();
                }

                // 3. 文件替换（保持原有安全替换逻辑不变）
                string sandboxDir = Application.persistentDataPath;
                if (File.Exists(path))
                {
                    SafeFileUtil.TryDeleteFile(backupPath, sandboxDir);
                    try
                    {
                        if (request.CreateBackup)
                            File.Replace(tempPath, path, backupPath, true);
                        else
                            File.Replace(tempPath, path, null, true);
                    }
                    catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException)
                    {
                        SafeFileUtil.TryDeleteFile(path, sandboxDir);
                        File.Move(tempPath, path);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }

                // 平滑交接式清理旧格式文件
                string oppositeExt = request.Encrypt ? JSON_EXT : ENCRYPTED_EXT;
                string redundantPath = Path.Combine(GetDirectoryByScope(meta.Scope), $"{slotName}{oppositeExt}");
                string redundantBakPath = redundantPath + ".bak";

                // A. 异构旧主文件：当前新主文件已经写入成功，旧主文件不再需要，直接清理
                SafeFileUtil.TryDeleteFile(redundantPath, sandboxDir);

                // B. 异构旧备份：
                // 只有当当前格式的备份 (backupPath) 已经真实存在、或者系统压根不需要备份时，
                // 才证明安全交接已完成，此时再安全删除旧备份
                if (!request.CreateBackup || File.Exists(backupPath))
                {
                    SafeFileUtil.TryDeleteFile(redundantBakPath, sandboxDir);
                }

                // 5. 缓存本次成功的指纹
                SavedHashesCache[slotName] = currentHash;

                return SaveResult.Ok();
            }
            catch (Exception e)
            {
                Debug.LogError($"<color=red>[SaveSystem]</color> 保存失败: {e}");
                SafeFileUtil.TryDeleteFile(tempPath, Application.persistentDataPath);
                return SaveResult.Fail(e);
            }
        }

        public LoadResult Load(LoadRequest request)
        {
            string slotName = request.FileName;
            string mainPath = ResolveReadFilePath(slotName, request.Scope);

            if (!File.Exists(mainPath))
            {
                return LoadResult.NotFound();
            }

            try
            {
                var pack = ReadStreamInternal<SavePack>(mainPath);
                if (pack == null)
                {
                    return LoadResult.Fail(new InvalidDataException("反序列化结果为空。"));
                }

                if (request.Validator != null && !request.Validator(pack))
                {
                    return LoadResult.Fail(new InvalidDataException($"存档 [{slotName}] 未能通过业务完整性校验。"));
                }

                SavedHashesCache[slotName] = pack.Metadata.DataHash;
                return LoadResult.Ok(pack);
            }
            catch (Exception ex)
            {
                return LoadResult.Fail(ex);
            }
        }

        public bool RestoreBackup(string fileName, SaveScope scope, Func<SavePack, bool> validator = null)
        {
            string backupPath = ResolveBackupFilePath(fileName, scope);
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
            {
                Debug.LogWarning($"<color=yellow>[SaveSystem]</color> 回滚失败：未找到存档 [{fileName}] 的备份文件。");
                return false;
            }

            try
            {
                // 1. 如果备份解析失败，直接抛异常
                var pack = ReadStreamInternal<SavePack>(backupPath)
                    ?? throw new InvalidDataException("备份反序列化结果为空");

                // 2. 校验
                if (validator != null && !validator(pack))
                {
                    Debug.LogError($"<color=red>[SaveSystem]</color> 备份文件 [{backupPath}] 未能通过业务完整性校验，拒绝还原！");
                    return false;
                }

                // 3. 物理替换
                string restoredMainPath = backupPath.Substring(0, backupPath.Length - 4);
                string currentMainPath = ResolveReadFilePath(fileName, scope);

                File.Copy(backupPath, restoredMainPath, true);

                if (!string.Equals(currentMainPath, restoredMainPath, StringComparison.OrdinalIgnoreCase))
                {
                    SafeFileUtil.TryDeleteFile(currentMainPath, Application.persistentDataPath);
                }

                SavedHashesCache.Remove(fileName);
                Debug.Log($"<color=green>[SaveSystem]</color> <color=yellow>[{fileName}]</color> 备份文件已成功还原至: {restoredMainPath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"<color=red>[SaveSystem]</color> 物理还原备份文件时发生异常: {ex}");
                return false;
            }
        }

        public SaveMetadata LoadMetadata(string fileName, SaveScope scope)
        {

            string path = ResolveReadFilePath(fileName, scope);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            try
            {
                // 一行直接流式反序列化 Metadata 节点
                return ReadStreamInternal<SaveMetadata>(path, nameof(SavePack.Metadata));
            }
            catch
            {
                return null;
            }
        }

        public List<SaveMetadata> GetAllFilesMetadata(SaveScope scope)
        {
            var result = new List<SaveMetadata>();
            string targetDirectory = GetDirectoryByScope(scope);

            if (!Directory.Exists(targetDirectory)) return result;

            var directoryInfo = new DirectoryInfo(targetDirectory);

            // 搜集所有的 .sav 和 .json 文件
            var files = directoryInfo.GetFiles("*.*");
            var processedSlotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                string ext = file.Extension;
                if (!ext.Equals(ENCRYPTED_EXT, StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(JSON_EXT, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 忽略 .tmp, .bak 或其他无关文件
                }

                string slotName = Path.GetFileNameWithoutExtension(file.Name);

                // 如果同名槽位（如 Slot_1）已经作为 .sav 加载过了，跳过其旧的 .json，防止重复
                if (processedSlotNames.Contains(slotName)) continue;

                var meta = LoadMetadata(slotName, scope);
                if (meta != null)
                {
                    result.Add(meta);
                    processedSlotNames.Add(slotName);
                }
            }

            return result;
        }

        public bool HasFile(string fileName, SaveScope scope)
        {
            // 自适应找，只要 .sav 或 .json 存在任一即可
            string path = ResolveReadFilePath(fileName, scope);
            return File.Exists(path);
        }

        public bool DeleteFile(string fileName, SaveScope scope)
        {
            string dir = GetDirectoryByScope(scope);
            string sandboxDir = Application.persistentDataPath;

            // 新老两种后缀及其 .bak 一并删除
            bool res1 = SafeFileUtil.TryDeleteFile(Path.Combine(dir, $"{fileName}{ENCRYPTED_EXT}"), sandboxDir);
            SafeFileUtil.TryDeleteFile(Path.Combine(dir, $"{fileName}{ENCRYPTED_EXT}.bak"), sandboxDir);

            bool res2 = SafeFileUtil.TryDeleteFile(Path.Combine(dir, $"{fileName}{JSON_EXT}"), sandboxDir);
            SafeFileUtil.TryDeleteFile(Path.Combine(dir, $"{fileName}{JSON_EXT}.bak"), sandboxDir);

            return res1 || res2;
        }

        /// <summary>
        /// 底层统一流式读取：自动适配解密流，支持全量反序列化或局部指定节点流式反序列化
        /// </summary>
        private T ReadStreamInternal<T>(string filePath, string targetNode = null)
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var secureStream = SaveSecurityUtil.CreateReadStream(fileStream);
            using var streamReader = new StreamReader(secureStream, Encoding.UTF8);
            using var jsonReader = new JsonTextReader(streamReader);

            if (string.IsNullOrEmpty(targetNode))
            {
                return _serializer.Deserialize<T>(jsonReader);
            }

            while (jsonReader.Read())
            {
                if (jsonReader.TokenType == JsonToken.PropertyName &&
                    string.Equals((string)jsonReader.Value, targetNode, StringComparison.OrdinalIgnoreCase))
                {
                    jsonReader.Read();
                    return _serializer.Deserialize<T>(jsonReader);
                }
            }

            return default;
        }

        // 预留，未使用
        public T Load<T>(LoadRequest request)
        {
            var path = ResolveReadFilePath(request.FileName, request.Scope);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return default;

            try
            {
                return ReadStreamInternal<T>(path, request.TargetNode);
            }
            catch (Exception ex)
            {
                Debug.LogError($"<color=red>[SaveSystem]</color> 读取 [{request.FileName}] 失败: {ex}");
                return default;
            }
        }
        //!==============================
        // 辅助

        public class SaveDataBinder : DefaultSerializationBinder
        {
            public override Type BindToType(string assemblyName, string typeName)
            {

                return base.BindToType(assemblyName, typeName);
            }
        }

    }
}

//!==============================
// !Converter
//!==============================
namespace Framework.SaveManagement
{
    public static class SaveConvert
    {
        /// <summary>
        /// 注入系统级带有 Unity 各类 Converter 的序列化器
        /// </summary>
        public static JsonSerializer CustomSerializer { get; set; }

        /// <summary>
        /// 判断存档原始值是否表示"空":C# null、JSON null、JSON undefined
        /// </summary>
        public static bool IsNull(object value)
            => value == null
            || (value is JToken t && (t.Type == JTokenType.Null || t.Type == JTokenType.Undefined));

        /// <summary>
        /// 该类型是否能接收 null(引用类型或 Nullable<T>)
        /// </summary>
        public static bool CanBeNull(Type type)
            => !type.IsValueType || Nullable.GetUnderlyingType(type) != null;

        // ==================== 非泛型核心实现（供反射和内部使用） ====================
        /// <summary>
        /// 基础两参数版本：彻底杜绝与泛型版本的可选参数发生重载冲突
        /// </summary>
        public static object To(object rawValue, Type targetType)
        {
            return To(rawValue, targetType, null);
        }

        /// <summary>
        /// 完整三参数版本
        /// </summary>
        public static object To(object rawValue, Type targetType, object defaultValue)
        {
            if (targetType == null || IsNull(rawValue)) return defaultValue;

            try
            {
                // 1. 同一类型直接返回
                if (targetType.IsInstanceOfType(rawValue))
                    return rawValue;

                // 2. Unity 常用数学向量/颜色跨类型隐式兼容
                // 2.1 浮点向量互转
                if (rawValue is Vector2 v2 && targetType == typeof(Vector3)) return (Vector3)v2;
                if (rawValue is Vector3 v3 && targetType == typeof(Vector2)) return (Vector2)v3;

                // 2.2 整型向量互转
                if (rawValue is Vector2Int v2i && targetType == typeof(Vector3Int)) return new Vector3Int(v2i.x, v2i.y, 0);
                if (rawValue is Vector3Int v3i && targetType == typeof(Vector2Int)) return new Vector2Int(v3i.x, v3i.y);

                // 2.3 整型与浮点向量互转（自动四舍五入或隐式提升）
                if (rawValue is Vector2Int v2ToFloat && targetType == typeof(Vector2)) return (Vector2)v2ToFloat;
                if (rawValue is Vector2 floatToV2 && targetType == typeof(Vector2Int)) return Vector2Int.RoundToInt(floatToV2);

                if (rawValue is Vector3Int v3ToFloat && targetType == typeof(Vector3)) return (Vector3)v3ToFloat;
                if (rawValue is Vector3 floatToV3 && targetType == typeof(Vector3Int)) return Vector3Int.RoundToInt(floatToV3);

                // 2.4 Color 与 Color32 互转
                if (rawValue is Color c && targetType == typeof(Color32)) return (Color32)c;
                if (rawValue is Color32 c32 && targetType == typeof(Color)) return (Color)c32;

                // 3. JToken 转换
                if (rawValue is JToken jToken)
                {
                    return CustomSerializer != null
                        ? jToken.ToObject(targetType, CustomSerializer)
                        : jToken.ToObject(targetType);
                }

                // 4. 枚举处理
                if (targetType.IsEnum)
                {
                    if (rawValue is string strVal)
                        return Enum.Parse(targetType, strVal, true);
                    return Enum.ToObject(targetType, rawValue);
                }

                // 5. C# 基础值类型互转
                if (rawValue is IConvertible && typeof(IConvertible).IsAssignableFrom(targetType))
                {
                    return Convert.ChangeType(rawValue, targetType);
                }

                // 6. 兜底兼容（通过 JToken 反解字典/对象）
                var serializer = CustomSerializer ?? JsonSerializer.CreateDefault();
                var fallbackToken = JToken.FromObject(rawValue, serializer);
                return fallbackToken.ToObject(targetType, serializer);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SaveConvert] 转换失败，值 [{rawValue}] 无法转换为目标类型 [{targetType.FullName}]，原因: {ex.Message}");
                return defaultValue;
            }
        }

        // ==================== 泛型版本（供外部强类型调用） ====================
        public static T To<T>(object rawValue, T defaultValue = default)
        {
            var result = To(rawValue, typeof(T), defaultValue);
            return result is T typed ? typed : defaultValue;
        }

        public static T As<T>(this object rawValue, T defaultValue = default)
            => To<T>(rawValue, defaultValue);

    }
}

namespace Framework.SaveManagement.Converters
{

    /// <summary>
    /// 全局通用多态转换器：自动为标记了 [SaveType] 的类型注入 $type 并自动还原
    /// </summary>
    public class UniversalPolymorphicConverter : JsonConverter
    {

        public override bool CanConvert(Type objectType)
        {
            return SaveTypeRegistry.GetSaveTypeAttribute(objectType) != null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var type = value.GetType();

            if (SaveTypeRegistry.GetSaveTypeAttribute(type) != null)
            {
                writer.WriteStartObject();
                // 统一调用注册器
                string typeIdentifier = SaveTypeRegistry.GetTypeIdentifier(type);

                writer.WritePropertyName(SaveContract.TYPE_TAG);
                writer.WriteValue(typeIdentifier);

                var contract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(type);
                foreach (var property in contract.Properties)
                {
                    if (property.Ignored || !property.Readable) continue;
                    var propVal = property.ValueProvider.GetValue(value);
                    if (propVal == null && serializer.NullValueHandling == NullValueHandling.Ignore) continue;

                    writer.WritePropertyName(property.PropertyName);
                    serializer.Serialize(writer, propVal);
                }

                writer.WriteEndObject();
            }
            else
            {
                JToken.FromObject(value).WriteTo(writer);
            }
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            var token = JToken.Load(reader);

            if (token is JObject jObj)
            {
                // 1. 尝试从 JSON 数据中探测是否有明确声明的 $type
                Type targetType = null;
                if (jObj.TryGetValue(SaveContract.TYPE_TAG, out var typeToken))
                {
                    string typeIdentifier = typeToken.Value<string>();
                    targetType = SaveTypeRegistry.ResolveType(typeIdentifier);
                }

                // 2. 如果数据里没有 $type（比如来自 $val 包装盒），则回退使用调用方传入的确定类型
                if (targetType == null && !objectType.IsAbstract && !objectType.IsInterface && objectType != typeof(object))
                {
                    targetType = objectType;
                }

                // 3. 此时只要锁定了具体目标类型，就能精准还原
                if (targetType != null)
                {
                    // A. 如果包含 $val 包装盒（基础类型、数组等）
                    if (jObj.TryGetValue(SaveContract.VAL_TAG, out var valToken))
                    {
                        return valToken.ToObject(targetType, serializer);
                    }

                    // B. 如果 targetType 本身是值类型或不可直接 Populate 的类型
                    if (targetType.IsValueType || targetType == typeof(string))
                    {
                        return token.ToObject(targetType);
                    }

                    // C. 实例化具体对象并填充字段
                    object instance;
                    try
                    {
                        instance = Activator.CreateInstance(targetType, nonPublic: true);
                    }
                    catch (MissingMethodException)
                    {
                        instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(targetType);
                    }

                    if (instance != null)
                    {
                        using var innerReader = jObj.CreateReader();
                        serializer.Populate(innerReader, instance);
                        return instance;
                    }
                }
            }

            return token.ToObject<object>();
        }

    }
    //! =========== Vector =========== 
    public class VectorConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
            => objectType == typeof(Vector2) ||
            objectType == typeof(Vector3) ||
            objectType == typeof(Vector4) ||
            objectType == typeof(Vector2Int) ||
            objectType == typeof(Vector3Int) ||
            objectType == typeof(Quaternion);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            switch (value)
            {
                // === 整数类型 ===
                case Vector2Int v2i:
                    writer.WritePropertyName("x"); writer.WriteValue(v2i.x);
                    writer.WritePropertyName("y"); writer.WriteValue(v2i.y);
                    break;

                case Vector3Int v3i:
                    writer.WritePropertyName("x"); writer.WriteValue(v3i.x);
                    writer.WritePropertyName("y"); writer.WriteValue(v3i.y);
                    writer.WritePropertyName("z"); writer.WriteValue(v3i.z);
                    break;

                // === 浮点类型 ===
                case Vector2 v2:
                    writer.WritePropertyName("x"); writer.WriteValue(v2.x);
                    writer.WritePropertyName("y"); writer.WriteValue(v2.y);
                    break;

                case Vector3 v3:
                    writer.WritePropertyName("x"); writer.WriteValue(v3.x);
                    writer.WritePropertyName("y"); writer.WriteValue(v3.y);
                    writer.WritePropertyName("z"); writer.WriteValue(v3.z);
                    break;

                case Vector4 v4:
                    writer.WritePropertyName("x"); writer.WriteValue(v4.x);
                    writer.WritePropertyName("y"); writer.WriteValue(v4.y);
                    writer.WritePropertyName("z"); writer.WriteValue(v4.z);
                    writer.WritePropertyName("w"); writer.WriteValue(v4.w);
                    break;

                case Quaternion q:
                    writer.WritePropertyName("x"); writer.WriteValue(q.x);
                    writer.WritePropertyName("y"); writer.WriteValue(q.y);
                    writer.WritePropertyName("z"); writer.WriteValue(q.z);
                    writer.WritePropertyName("w"); writer.WriteValue(q.w);
                    break;
            }

            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;

            var obj = JObject.Load(reader);

            // 处理整数系列
            if (objectType == typeof(Vector2Int))
            {
                int x_ = obj["x"] != null ? Convert.ToInt32(obj["x"]) : 0;
                int y_ = obj["y"] != null ? Convert.ToInt32(obj["y"]) : 0;
                return new Vector2Int(x_, y_);
            }
            if (objectType == typeof(Vector3Int))
            {
                int x_ = obj["x"] != null ? Convert.ToInt32(obj["x"]) : 0;
                int y_ = obj["y"] != null ? Convert.ToInt32(obj["y"]) : 0;
                int z_ = obj["z"] != null ? Convert.ToInt32(obj["z"]) : 0;
                return new Vector3Int(x_, y_, z_);
            }

            // 处理浮点系列
            float x = (float)(obj["x"] ?? 0f);
            float y = (float)(obj["y"] ?? 0f);
            float z = (float)(obj["z"] ?? 0f);
            float w = (float)(obj["w"] ?? 0f);

            if (objectType == typeof(Vector2)) return new Vector2(x, y);
            if (objectType == typeof(Vector3)) return new Vector3(x, y, z);
            if (objectType == typeof(Vector4)) return new Vector4(x, y, z, w);
            if (objectType == typeof(Quaternion)) return new Quaternion(x, y, z, w);

            return null;
        }
    }
    //! =========== Pose =========== 
    public class PoseConverter : JsonConverter<Pose>
    {
        public override void WriteJson(JsonWriter writer, Pose value, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("position");
            writer.WriteStartObject();
            writer.WritePropertyName("x"); writer.WriteValue(value.position.x);
            writer.WritePropertyName("y"); writer.WriteValue(value.position.y);
            writer.WritePropertyName("z"); writer.WriteValue(value.position.z);
            writer.WriteEndObject();

            writer.WritePropertyName("rotation");
            writer.WriteStartObject();
            writer.WritePropertyName("x"); writer.WriteValue(value.rotation.x);
            writer.WritePropertyName("y"); writer.WriteValue(value.rotation.y);
            writer.WritePropertyName("z"); writer.WriteValue(value.rotation.z);
            writer.WritePropertyName("w"); writer.WriteValue(value.rotation.w);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        public override Pose ReadJson(JsonReader reader, System.Type objectType, Pose existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var jObj = Newtonsoft.Json.Linq.JObject.Load(reader);

            var posToken = jObj["position"] ?? jObj["pos"];
            var rotToken = jObj["rotation"] ?? jObj["rot"];

            Vector3 pos = Vector3.zero;
            if (posToken != null)
            {
                pos = new Vector3((float)posToken["x"], (float)posToken["y"], (float)posToken["z"]);
            }

            Quaternion rot = Quaternion.identity;
            if (rotToken != null)
            {
                rot = new Quaternion((float)rotToken["x"], (float)rotToken["y"], (float)rotToken["z"], (float)rotToken["w"]);
            }

            return new Pose(pos, rot);
        }
    }

    //! =========== Color =========== 
    public class ColorConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
            => objectType == typeof(Color) || objectType == typeof(Color32);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            if (value is Color c)
            {
                writer.WritePropertyName("r"); writer.WriteValue(c.r);
                writer.WritePropertyName("g"); writer.WriteValue(c.g);
                writer.WritePropertyName("b"); writer.WriteValue(c.b);
                writer.WritePropertyName("a"); writer.WriteValue(c.a);
            }
            else if (value is Color32 c32)
            {
                writer.WritePropertyName("r"); writer.WriteValue(c32.r);
                writer.WritePropertyName("g"); writer.WriteValue(c32.g);
                writer.WritePropertyName("b"); writer.WriteValue(c32.b);
                writer.WritePropertyName("a"); writer.WriteValue(c32.a);
            }
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;

            var obj = JObject.Load(reader);
            float r = (float)(obj["r"] ?? 0f);
            float g = (float)(obj["g"] ?? 0f);
            float b = (float)(obj["b"] ?? 0f);
            float a = (float)(obj["a"] ?? 1f); // 默认 Alpha 通常为 1

            if (objectType == typeof(Color32))
            {
                return new Color32((byte)r, (byte)g, (byte)b, (byte)a);
            }

            return new Color(r, g, b, a);
        }
    }
}
//! ======================================
//! 
//! ======================================
namespace Framework.SaveManagement.Internal
{
    /// <summary>
    /// 存档序列化协议保留字与多态契约常量
    /// </summary>
    public static class SaveContract
    {
        /// <summary> 多态与类型标记键名 </summary>
        public const string TYPE_TAG = "$type";

        /// <summary> 包装值载荷键名 </summary>
        public const string VAL_TAG = "$val";

        /// <summary>
        /// 判断一个 JObject 是否为标量包装盒结构 {"$type": ..., "$val": ...}
        /// </summary>
        public static bool IsBoxedPayload(Newtonsoft.Json.Linq.JObject jObj)
        {
            return jObj != null && jObj.ContainsKey(TYPE_TAG) && jObj.ContainsKey(VAL_TAG);
        }
    }
    public static class SaveTypeRegistry
    {
        private static Assembly[] _filteredUserAssemblies;
        // 静态缓存 UnityEngine.CoreModule 程序集引用
        private static readonly Assembly UnityCoreAssembly = typeof(Vector3).Assembly;

        // 仅维护：显式打过 [SaveType("CustomKey")] 的双向别名索引 + 运行时动态解析缓存
        private static readonly Dictionary<string, Type> KeyToTypeCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Type, string> TypeToKeyCache = new();
        private static bool _isInitialized = false;

        /// <summary>
        /// 获取存储用的类型标识：
        /// 1. 显式指定了 [SaveType("Alias")] 则使用其别名；
        /// 2. 否则统一返回干净规范的 FullName（例如 "System.Int32", "UnityEngine.Vector3"）；
        /// 3. 自定义业务类若在独立程序集中，返回 "Namespace.Class, AssemblyName"
        /// </summary>
        public static string GetTypeIdentifier(Type type)
        {
            if (type == null) return string.Empty;

            EnsureInitialized();

            if (TypeToKeyCache.TryGetValue(type, out var key))
                return key;

            // 兜底逻辑：系统内置类型或未被多态标记的具体类
            string asmName = type.Assembly.GetName().Name;
            string identifier;
            if (asmName == "mscorlib" || asmName == "System.Private.CoreLib" || asmName.StartsWith("UnityEngine") ||
                asmName == "Assembly-CSharp" || asmName == "Assembly-CSharp-firstpass")
            {
                identifier = type.FullName;
            }
            else
            {
                identifier = $"{type.FullName}, {asmName}";
            }

            RegisterMapping(identifier, type);
            return identifier;
        }

        // 既检查类及其基类，也检查其实现的所有接口
        public static SaveTypeAttribute GetSaveTypeAttribute(Type type)
        {
            if (type == null) return null;

            // 1. 先查类自身及其基类继承链
            var attr = type.GetCustomAttribute<SaveTypeAttribute>(true);
            if (attr != null) return attr;

            // 2. 检查当前类实现的所有接口
            foreach (var iface in type.GetInterfaces())
            {
                var ifaceAttr = iface.GetCustomAttribute<SaveTypeAttribute>(true);
                if (ifaceAttr != null)
                {
                    // 如果接口有标记，返回该标记（或返回一个默认的新标记）
                    return ifaceAttr;
                }
            }

            return null;
        }

        /// <summary>
        /// 健壮的反向解析：天然通吃短别名、FullName、带 Version 的强签名老存档以及数组
        /// </summary>
        public static Type ResolveType(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return null;

            EnsureInitialized();

            // 1. O(1) 命中缓存（包括 null 负向缓存）
            if (KeyToTypeCache.TryGetValue(identifier, out var cachedType))
                return cachedType;

            Type resolvedType = null;
            string cleanTypeName = ExtractCleanTypeName(identifier);

            // 2. 检查数组
            if (cleanTypeName.EndsWith("[]"))
            {
                string elementTypeName = cleanTypeName.Substring(0, cleanTypeName.Length - 2);
                Type elementType = ResolveType(elementTypeName);
                if (elementType != null)
                {
                    resolvedType = elementType.MakeArrayType();
                }
            }

            // 3. 原生系统类型尝试（如 "System.Int32"）
            if (resolvedType == null)
            {
                resolvedType = Type.GetType(cleanTypeName, false, true);
            }

            // 4. 【核心修复】：如果是 UnityEngine 命名空间下的内置类型，直接从 UnityCoreAssembly 加载！
            if (resolvedType == null && cleanTypeName.StartsWith("UnityEngine."))
            {
                resolvedType = UnityCoreAssembly.GetType(cleanTypeName, false, true);
            }

            // 5. 业务程序集遍历兜底（仅扫描业务 DLL，不包含任何系统和引擎 DLL，依然极速）
            if (resolvedType == null && _filteredUserAssemblies != null)
            {
                for (int i = 0; i < _filteredUserAssemblies.Length; i++)
                {
                    resolvedType = _filteredUserAssemblies[i].GetType(cleanTypeName, false, true);
                    if (resolvedType != null) break;
                }
            }

            // 6. 缓存结果
            KeyToTypeCache[identifier] = resolvedType;
            if (resolvedType != null)
            {
                TypeToKeyCache[resolvedType] = identifier;
            }
            else
            {
                Debug.LogError($"[SaveSystem] 无法解析存档中的类型签名: '{identifier}'");
            }

            return resolvedType;
        }

        public static void RegisterMapping(string key, Type type)
        {
            KeyToTypeCache[key] = type;
            TypeToKeyCache[type] = key;
        }

        private static string ExtractCleanTypeName(string typeIdentifier)
        {
            int commaIndex = typeIdentifier.IndexOf(',');
            return (commaIndex > 0 ? typeIdentifier.Substring(0, commaIndex) : typeIdentifier).Trim();
        }

        private static string SimplifyAssemblyName(string fullSignature)
        {
            var parts = fullSignature.Split(',');
            if (parts.Length >= 2)
            {
                return $"{parts[0].Trim()}, {parts[1].Trim()}";
            }
            return fullSignature;
        }

        public static void EnsureInitialized()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            // 1. 一次性获取并过滤出纯业务程序集（只抓一次，全局复用）
            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var userAssembliesList = new List<Assembly>(allAssemblies.Length);

            for (int i = 0; i < allAssemblies.Length; i++)
            {
                var asm = allAssemblies[i];
                string asmName = asm.FullName;

                // 过滤系统库和 Unity 内部核心程序集
                if (asmName.StartsWith("UnityEngine") ||
                    asmName.StartsWith("UnityEditor") ||
                    asmName.StartsWith("System") ||
                    asmName.StartsWith("mscorlib"))
                {
                    continue;
                }

                userAssembliesList.Add(asm);
            }

            _filteredUserAssemblies = userAssembliesList.ToArray();

            // 2. 遍历已清洗的高价值程序集，建立别名索引
            for (int i = 0; i < _filteredUserAssemblies.Length; i++)
            {
                var assembly = _filteredUserAssemblies[i];
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsAbstract || type.IsInterface) continue;

                        // 使用支持接口穿透的查找方法
                        var attr = GetSaveTypeAttribute(type);
                        if (attr == null) continue;

                        // 注意：如果是在接口上打的标记，具体类应该优先使用自己的类名，
                        // 只有具体类自身显式指定了 CustomTypeKey 时才使用别名，防止多个实现类别名冲突！
                        var selfAttr = type.GetCustomAttribute<SaveTypeAttribute>(false);
                        string key = (selfAttr != null && !string.IsNullOrEmpty(selfAttr.CustomTypeKey))
                            ? selfAttr.CustomTypeKey
                            : type.Name;

                        // 重复冲突检测
                        if (KeyToTypeCache.TryGetValue(key, out var existingType))
                        {
                            if (existingType != type)
                            {
                                Debug.LogError($"<color=red>[SaveSystem]</color> SaveType Key 冲突！标识 \"{key}\" 同时被 [{existingType.FullName}] 和 [{type.FullName}] 使用！请在类上显式指定 [SaveType(\"唯一名称\")]。");
                            }
                            continue;
                        }

                        RegisterMapping(key, type);
                    }
                }
                catch { /* 忽略个别受保护无法反射的程序集 */ }
            }
        }
    }
    internal class MemberAccessor
    {
        /// <summary> 优先使用的存盘/读盘 Key（若配了 CustomKey 则为其值，否则为真实字段名） </summary>
        public string SaveKey { get; }
        /// <summary> 备用 Key（真实的字段或属性名），用于向后兼容旧存档 </summary>
        public string OriginalName { get; }
        /// <summary> 是否存在别名映射（用于快速判断是否需要走 Fallback 查找） </summary>
        public bool HasCustomKey { get; }

        public Type MemberType { get; } // 记录成员的真实类型，用于类型转换

        public bool PreserveType { get; }
        public bool SkipSync { get; }

        private readonly FieldInfo _fieldInfo;
        private readonly MethodInfo _getterMethod;
        private readonly MethodInfo _setterMethod;

        public MemberAccessor(FieldInfo fieldInfo, string customKey, bool preserveType = false, bool skipSync = false)
        {
            _fieldInfo = fieldInfo;
            MemberType = fieldInfo.FieldType;
            OriginalName = fieldInfo.Name;
            PreserveType = preserveType;
            SkipSync = skipSync;
            HasCustomKey = !string.IsNullOrEmpty(customKey) && !string.Equals(customKey, OriginalName, StringComparison.Ordinal);
            SaveKey = HasCustomKey ? customKey : OriginalName;
        }

        public MemberAccessor(PropertyInfo propertyInfo, string customKey, bool preserveType = false, bool skipSync = false)
        {
            MemberType = propertyInfo.PropertyType;
            _getterMethod = propertyInfo.GetGetMethod(true);
            _setterMethod = propertyInfo.GetSetMethod(true);
            OriginalName = propertyInfo.Name;
            PreserveType = preserveType;
            SkipSync = skipSync;
            HasCustomKey = !string.IsNullOrEmpty(customKey) && !string.Equals(customKey, OriginalName, StringComparison.Ordinal);
            SaveKey = HasCustomKey ? customKey : OriginalName;
        }

        public object GetValue(object target)
        {
            object val = null;
            if (_fieldInfo != null) val = _fieldInfo.GetValue(target);
            else if (_getterMethod != null) val = _getterMethod.Invoke(target, null);

            // 如果开启了 PreserveType，出内存时透明转换为自描述的安全类型包
            if (PreserveType && val != null)
            {
                return TypePreservePacker.Pack(val);
            }

            return val;
        }

        public void SetValue(object target, object value)
        {
            // 如果开启了 PreserveType，进内存前先透明解包还原成原生类型
            if (PreserveType && value != null)
            {
                value = TypePreservePacker.Unpack(value, MemberType);
            }

            object converted;
            if (SaveConvert.IsNull(value))
            {
                if (!SaveConvert.CanBeNull(MemberType)) return;
                converted = null;
            }
            else
            {
                converted = SaveConvert.To(value, MemberType);
                if (converted == null) return;
            }

            if (_fieldInfo != null) _fieldInfo.SetValue(target, converted);
            else _setterMethod?.Invoke(target, new[] { converted });
        }
    }

    internal static class SaveMetadataCache
    {
        private static readonly Dictionary<Type, List<MemberAccessor>> Cache = new();

        public static List<MemberAccessor> GetAccessors(Type type)
        {
            if (Cache.TryGetValue(type, out var accessors))
                return accessors;

            accessors = new List<MemberAccessor>();
            const BindingFlags allFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // 1. 白名单模式：扫描显式打了 [SaveField] 的字段/属性
            foreach (var field in type.GetFields(allFlags))
            {
                var attr = field.GetCustomAttribute<SaveFieldAttribute>();
                if (attr != null)
                {
                    if (IsUnsupportedUnityType(field.FieldType, field.Name, type)) continue;
                    accessors.Add(new MemberAccessor(
                        fieldInfo: field,
                        customKey: attr.CustomKey,
                        preserveType: attr.PreserveType,
                        skipSync: attr.SkipSync));
                }
            }

            foreach (var prop in type.GetProperties(allFlags))
            {
                var attr = prop.GetCustomAttribute<SaveFieldAttribute>();
                if (attr != null && prop.CanRead && prop.CanWrite)
                {
                    if (IsUnsupportedUnityType(prop.PropertyType, prop.Name, type)) continue;
                    accessors.Add(new MemberAccessor(
                        propertyInfo: prop,
                        customKey: attr.CustomKey,
                        preserveType: attr.PreserveType,
                        skipSync: attr.SkipSync));
                }
            }

            if (accessors.Count > 0)
            {
                Cache[type] = accessors;
                return accessors;
            }

            // 2. 自动降级模式：IAutoSaveable 默认推导（保持原样，无需额外属性）
            if (typeof(IAutoSaveable).IsAssignableFrom(type))
            {
                CollectAutoSaveMembers(type, accessors);
            }

            Cache[type] = accessors;
            return accessors;
        }

        /// <summary>
        /// 全局防御：拦截 UnityEngine.Object 及其派生类型
        /// </summary>
        private static bool IsUnsupportedUnityType(Type memberType, string memberName, Type declaringType)
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(memberType))
            {
                Debug.LogWarning($"<color=yellow>[SaveSystem]</color> 类 [{declaringType.Name}] 的成员 [{memberName}] 类型为 [{memberType.Name}]，\n" +
                                "属于 UnityEngine.Object。该字段已被自动跳过。");
                return true;
            }
            return false;
        }

        private static void CollectAutoSaveMembers(Type type, List<MemberAccessor> result)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // 字段扫描
            foreach (var field in type.GetFields(flags))
            {
                if (field.IsInitOnly || field.IsLiteral) continue;
                if (field.Name.Contains("k__BackingField")) continue;
                if (IsEngineDeclaringType(field.DeclaringType)) continue;

                // 全局拦截 UnityEngine.Object
                if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)) continue;

                if (field.IsPublic)
                {
                    result.Add(new MemberAccessor(field, null));
                }
            }

            // 属性扫描
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!prop.CanRead || !prop.CanWrite || prop.GetSetMethod(false) == null) continue;
                if (IsEngineDeclaringType(prop.DeclaringType)) continue;

                // 全局拦截 UnityEngine.Object
                if (typeof(UnityEngine.Object).IsAssignableFrom(prop.PropertyType)) continue;

                result.Add(new MemberAccessor(prop, null));
            }
        }

        private static bool IsEngineDeclaringType(Type declaringType)
        {
            return declaringType != null &&
                declaringType.Namespace != null &&
                (declaringType.Namespace.StartsWith("UnityEngine") || declaringType.Namespace.StartsWith("UnityEditor"));
        }

        /// <summary>
        /// 直接返回字典
        /// </summary>
        public static object CaptureReflectionState(object target, bool isMemorySync = false)
        {
            var accessors = GetAccessors(target.GetType());
            var data = new Dictionary<string, object>(accessors.Count);
            foreach (var acc in accessors)
            {
                if (isMemorySync && acc.SkipSync) continue;

                try
                {
                    data[acc.SaveKey] = acc.GetValue(target);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SaveSystem] 对象 [{target.GetType().Name}] 捕获字段 [{acc.SaveKey}] 失败: {ex.Message}");
                }
            }
            return data;
        }

        /// <summary>
        /// 兼容 Newtonsoft 解出来的 JObject 或原生 Dictionary
        /// </summary>
        public static void RestoreReflectionState(object target, object state)
        {
            if (state == null || target == null) return;

            Func<string, (bool found, object value)> tryGetValue = null;

            if (state is JObject jObject)
            {
                tryGetValue = key => jObject.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var jToken)
                    ? (true, jToken)
                    : (false, null);
            }
            else if (state is IDictionary<string, object> genericDict)
            {
                tryGetValue = key => genericDict.TryGetValue(key, out var val) ? (true, val) : (false, null);
            }
            else if (state is System.Collections.IDictionary legacyDict)
            {
                tryGetValue = key => legacyDict.Contains(key) ? (true, legacyDict[key]) : (false, null);
            }
            else return;

            var accessors = GetAccessors(target.GetType());
            foreach (var acc in accessors)
            {
                // 1. 优先尝试主键（CustomKey 或默认名）
                var (found, val) = tryGetValue(acc.SaveKey);

                // 2. 兜底回退：如果没找到，且该字段配了别名，尝试用真实字段名找旧存档
                if (!found && acc.HasCustomKey)
                {
                    (found, val) = tryGetValue(acc.OriginalName);
                }

                if (found)
                {
                    try
                    {
                        acc.SetValue(target, val);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[SaveSystem] 还原字段 [{acc.SaveKey}] 失败: {ex.Message}");
                    }
                }
            }
        }
    }

    public class SafeContractResolver : DefaultContractResolver
    {
        public static readonly SafeContractResolver Instance = new();

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);

            // 1. 如果是 Unity 命名空间下的成员
            if (member.DeclaringType != null &&
                member.DeclaringType.Namespace != null &&
                member.DeclaringType.Namespace.StartsWith("UnityEngine"))
            {
                if (member is PropertyInfo prop)
                {
                    // 只允许同时具备可读、可写的属性（必须有 get 和 set）
                    // 这样像 color.linear, color.gamma, bounds.size* 等只读陷阱会被统统过滤
                    if (!prop.CanRead || !prop.CanWrite)
                    {
                        property.ShouldSerialize = _ => false;
                        return property;
                    }
                }
            }

            // 2. 通用自循环属性防御：类型与返回值完全一致的只读属性（彻底断绝自身无限递归）
            if (member is PropertyInfo p && p.PropertyType == p.DeclaringType && !p.CanWrite)
            {
                property.ShouldSerialize = _ => false;
            }

            return property;
        }
    }

    internal static class TypePreservePacker
    {
        // private const string TYPE_TAG = "$type";
        // private const string VAL_TAG = "$val";

        public static object Pack(object value)
        {
            if (value == null) return null;

            if (value is System.Collections.IDictionary dict)
            {
                var packedDict = new Dictionary<string, object>();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    packedDict[entry.Key.ToString()] = PackSingleValue(entry.Value);
                }
                return packedDict;
            }

            if (value is System.Collections.IList list)
            {
                var packedList = new List<object>();
                foreach (var item in list)
                {
                    packedList.Add(PackSingleValue(item));
                }
                return packedList;
            }

            return PackSingleValue(value);
        }

        private static object PackSingleValue(object val)
        {
            if (val == null) return null;

            Type valType = val.GetType();

            // 【兼容判断】：如果该对象本身被打上了 [SaveType]，它自己序列化时内部就会带 $type
            // 不重复套娃 {"$type":..., "$val":...}，直接原样返回给序列化器处理
            if (valType.GetCustomAttribute<SaveTypeAttribute>(true) != null)
            {
                return val;
            }

            // 基础标量或普通非多态对象，才使用万能包装
            return new Dictionary<string, object>(2)
            {
                { SaveContract.TYPE_TAG, SaveTypeRegistry.GetTypeIdentifier(valType) },
                { SaveContract.VAL_TAG, val }
            };
        }

        public static object Unpack(object packedValue, Type targetType)
        {
            if (packedValue == null) return null;

            if (packedValue is JToken token)
            {
                return UnpackJToken(token, targetType);
            }

            if (packedValue is IDictionary<string, object> dict)
            {
                // 1. 判断是否命中万能包装结构 {"$type": ..., "$val": ...}
                if (dict.TryGetValue(SaveContract.TYPE_TAG, out var typeId) && dict.TryGetValue(SaveContract.VAL_TAG, out var rawVal))
                {
                    var realType = SaveTypeRegistry.ResolveType(typeId?.ToString());
                    return realType != null ? SaveConvert.To(rawVal, realType) : rawVal;
                }

                // 2. 容器字典递归还原
                var resultDict = (System.Collections.IDictionary)Activator.CreateInstance(targetType);
                resultDict.Clear();
                Type valueType = targetType.IsGenericType ? targetType.GetGenericArguments()[1] : typeof(object);

                foreach (var kv in dict)
                {
                    resultDict[kv.Key] = Unpack(kv.Value, valueType);
                }
                return resultDict;
            }

            return packedValue;
        }

        private static object UnpackJToken(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            // ================= 1. 处理复合对象/包装结构 =================
            if (token is JObject jobj)
            {
                // 命中万能包装结构 {"$type": ..., "$val": ...}（基础类型、数组、未标 SaveType 的类）
                if (jobj.ContainsKey(SaveContract.TYPE_TAG) && jobj.ContainsKey(SaveContract.VAL_TAG))
                {
                    string typeId = jobj[SaveContract.TYPE_TAG].Value<string>();
                    var realType = SaveTypeRegistry.ResolveType(typeId);
                    if (realType != null)
                    {
                        return jobj[SaveContract.VAL_TAG].ToObject(realType, SaveConvert.CustomSerializer);
                    }
                    return jobj[SaveContract.VAL_TAG].ToObject<object>(SaveConvert.CustomSerializer);
                }

                // 内部本身包含多态标记（打了 [SaveType] 的自定义对象）
                if (jobj.ContainsKey(SaveContract.TYPE_TAG))
                {
                    string typeId = jobj[SaveContract.TYPE_TAG].Value<string>();
                    var realType = SaveTypeRegistry.ResolveType(typeId);

                    // 优先使用 $type 解析出来的真实类型反序列化；解析失败才退化为 targetType
                    Type finalType = realType ?? (targetType == typeof(object) ? typeof(object) : targetType);
                    return jobj.ToObject(finalType, SaveConvert.CustomSerializer);
                }

                // 老存档中的普通对象（当时既没包 $val，也没打 [SaveType]）
                // 如果外部 targetType 是确定的非 object 类型，按该类型反序列化；否则读作 JObject 兜底保底
                if (targetType != typeof(object) && !typeof(System.Collections.IDictionary).IsAssignableFrom(targetType))
                {
                    return jobj.ToObject(targetType, SaveConvert.CustomSerializer);
                }
            }
            // ================= 2. 补全列表/数组解包 =================
            if (typeof(System.Collections.IList).IsAssignableFrom(targetType) && token is JArray jArray)
            {
                var resultList = (System.Collections.IList)Activator.CreateInstance(targetType);
                resultList.Clear();

                // 获取列表元素的泛型类型（如 ITestItem）
                Type elementType = targetType.IsGenericType ? targetType.GetGenericArguments()[0] : typeof(object);

                foreach (var itemToken in jArray)
                {
                    // 递归解包每个元素！
                    resultList.Add(UnpackJToken(itemToken, elementType));
                }
                return resultList;
            }

            // ================= 3. 处理字典容器 =================
            if (typeof(System.Collections.IDictionary).IsAssignableFrom(targetType) && token is JObject dictToken)
            {
                var resultDict = (System.Collections.IDictionary)Activator.CreateInstance(targetType);
                resultDict.Clear();

                Type valueType = targetType.IsGenericType && targetType.GetGenericArguments().Length > 1
                    ? targetType.GetGenericArguments()[1]
                    : typeof(object);

                foreach (var prop in dictToken.Properties())
                {
                    // 递归解包子项：即便子项是老存档的纯文本/标量，也能优雅解析
                    resultDict[prop.Name] = UnpackJToken(prop.Value, valueType);
                }
                return resultDict;
            }

            // ================= 3. 老存档中的原生标量 =================
            // 比如老存档在字典里直接存了 100（JValue），既没有 $type 也没有 $val
            // 这里直接通过 Newtonsoft 提取原生值（long, double, string, bool 等）
            return token.ToObject(targetType == typeof(object) ? typeof(object) : targetType, SaveConvert.CustomSerializer);
        }
    }
}
//! ============================================
//! 事件相关
//! ============================================
namespace Framework.SaveManagement.Events
{
    /// <summary>
    /// 对外暴露的只读存档事件订阅总线
    /// </summary>
    public interface ISaveEvents
    {
        IDisposable Subscribe(SaveEventType eventType, Action<string> listener, CancellationToken cancellationToken = default);
        void Unsubscribe(SaveEventType eventType, Action<string> listener);
    }

    public class SaveEventDisposable : IDisposable
    {
        private Action _onDispose;
        public SaveEventDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }

    public class SaveEventHub : ISaveEvents
    {
        private class EventChannel
        {
            public Action<string> Action;
            public readonly Dictionary<Action<string>, CancellationTokenRegistration> Tokens = new();
        }

        private readonly Dictionary<SaveEventType, EventChannel> _channels = new();

        /// <summary>
        /// 订阅事件（支持生命周期 Token 或自动适配 MonoBehaviour 销毁）
        /// </summary>
        public IDisposable Subscribe(SaveEventType eventType, Action<string> listener, CancellationToken cancellationToken = default)
        {
            if (listener == null) return null;

            if (!_channels.TryGetValue(eventType, out var channel))
            {
                channel = new EventChannel();
                _channels[eventType] = channel;
            }

            if (channel.Tokens.ContainsKey(listener))
            {
                Debug.LogWarning($"[SaveSystem] 检测到重复订阅: {eventType} -> {listener.Method.Name}，已自动忽略。");
                return new SaveEventDisposable(() => Unsubscribe(eventType, listener));
            }

            channel.Action += listener;

            CancellationToken finalToken = cancellationToken;
            if (!finalToken.CanBeCanceled && listener.Target is MonoBehaviour mb)
            {
                finalToken = mb.destroyCancellationToken;
            }

            if (finalToken.CanBeCanceled)
            {
                channel.Tokens[listener] = finalToken.Register(() => Unsubscribe(eventType, listener));
            }

            return new SaveEventDisposable(() => Unsubscribe(eventType, listener));
        }

        public void Unsubscribe(SaveEventType eventType, Action<string> listener)
        {
            if (listener == null || !_channels.TryGetValue(eventType, out var channel))
                return;

            channel.Action -= listener;

            if (channel.Tokens.TryGetValue(listener, out var reg))
            {
                reg.Dispose();
                channel.Tokens.Remove(listener);
            }

            if (channel.Action == null)
            {
                _channels.Remove(eventType);
            }
        }

        public void Publish(SaveEventType eventType, string fileName)
        {
            if (!_channels.TryGetValue(eventType, out var channel) || channel.Action == null)
                return;

            var invocationList = channel.Action.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<string>)invocationList[i]).Invoke(fileName);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SaveSystem] 派发事件 {eventType} 异常: {ex}");
                }
            }
        }

        public void Clear()
        {
            foreach (var channel in _channels.Values)
            {
                foreach (var reg in channel.Tokens.Values)
                {
                    reg.Dispose();
                }
                channel.Tokens.Clear();
                channel.Action = null;
            }
            _channels.Clear();
        }
    }
}

namespace Framework.SaveManagement.Security
{
    //!========================================
    public static class SafeFileUtil
    {
        /// <summary>
        /// 检查 filePath 是否严格属于 allowedRootFolder 目录下（防止路径穿越）
        /// </summary>
        public static bool IsPathInFolder(string filePath, string allowedRootFolder)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(allowedRootFolder))
                return false;

            // 获取完全规范化的绝对物理路径（消除 ../ 和符号链接差异）
            string fullPath = Path.GetFullPath(filePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullRoot = Path.GetFullPath(allowedRootFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 目标路径必须以沙盒根目录开头，且紧跟一个路径分隔符
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 安全物理删除文件（带沙盒保护）
        /// </summary>
        public static bool TryDeleteFile(string fullPath, string sandboxRoot = null)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                return false;

            // 如果指定了沙盒目录，必须验证该文件是否真的在沙盒内
            if (!string.IsNullOrEmpty(sandboxRoot) && !IsPathInFolder(fullPath, sandboxRoot))
            {
                Debug.LogError($"[SafeFileUtil] 安全拦截！试图删除沙盒之外的文件: {fullPath}");
                return false;
            }

            try
            {
                var attrs = File.GetAttributes(fullPath);
                if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(fullPath, attrs & ~FileAttributes.ReadOnly);
                }

                File.Delete(fullPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SafeFileUtil] 删除文件失败: '{fullPath}'，原因: {ex.Message}");
                return false;
            }
        }
        public static class FastHash
        {
            /// <summary>
            /// 高性能 64 位 FNV-1a 字符串哈希，耗时极低（微秒级）
            /// </summary>
            public static string ComputeFnv1a64(string text)
            {
                if (text == null) return string.Empty;

                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 1099511628211UL;
                }
                return hash.ToString("x16"); // 16进制字符串
            }
        }
    }
    //!========================================
    public static class SaveSecurityUtil
    {
        // 4 字节魔数 Header: 'S', 'A', 'V', 0x01 (代表当前加密格式版本 1)
        private static readonly byte[] MagicHeader = new byte[] { 0x53, 0x41, 0x56, 0x01 };

        // 16 字节的 Key 和 16 字节的 IV（AES-128）
        private static readonly byte[] AesKey = Encoding.UTF8.GetBytes("A6B7C8D9E0F1G2H3");
        private static readonly byte[] AesIV = Encoding.UTF8.GetBytes("1A2B3C4D5E6F7G8H");

        /// <summary>
        /// 写盘包装：若开启加密，则先打上 MagicHeader，再叠加 AES 加密 + GZip 压缩
        /// </summary>
        public static Stream CreateWriteStream(Stream baseStream, bool enableEncryption)
        {
            if (!enableEncryption)
            {
                return baseStream;
            }

            // 写入魔数
            baseStream.Write(MagicHeader, 0, MagicHeader.Length);

            // 叠加加密与压缩
            ICryptoTransform encryptor;
            using (var aes = Aes.Create())
            {
                aes.Key = AesKey;
                aes.IV = AesIV;
                encryptor = aes.CreateEncryptor();
            }

            var cryptoStream = new CryptoStream(baseStream, encryptor, CryptoStreamMode.Write);
            var gzipStream = new GZipStream(cryptoStream, System.IO.Compression.CompressionLevel.Fastest);

            return gzipStream;
        }
        /// <summary>
        /// 读盘包装：智能侦测。若是明文档则直读，若是加密档则解密解压
        /// </summary>
        public static Stream CreateReadStream(Stream baseStream)
        {
            // 如果文件小于 4 字节，绝不可能是加密档，直接返回
            if (baseStream.Length < MagicHeader.Length) return baseStream;

            // 1. 预读前 4 个字节探测特征
            long originalPos = baseStream.Position;
            byte[] headerBuffer = new byte[MagicHeader.Length];
            int bytesRead = baseStream.Read(headerBuffer, 0, headerBuffer.Length);

            bool isEncryptedFormat = false;
            if (bytesRead == MagicHeader.Length)
            {
                // 对比是否与预设的 MagicHeader 完全吻合
                isEncryptedFormat = (headerBuffer[0] == MagicHeader[0] &&
                                    headerBuffer[1] == MagicHeader[1] &&
                                    headerBuffer[2] == MagicHeader[2] &&
                                    headerBuffer[3] == MagicHeader[3]);
            }

            // 2. 分流处理
            if (isEncryptedFormat)
            {
                // 是新版加密档：保持流位置在第 4 字节之后，开始对接解密与解压流
                ICryptoTransform decryptor;
                using (var aes = Aes.Create())
                {
                    aes.Key = AesKey;
                    aes.IV = AesIV;
                    decryptor = aes.CreateDecryptor();
                }

                var cryptoStream = new CryptoStream(baseStream, decryptor, CryptoStreamMode.Read);
                var gzipStream = new GZipStream(cryptoStream, CompressionMode.Decompress);

                return gzipStream;
            }
            else
            {
                // 是老版本明文档：把流游标复位回开头，当作纯文本流直接交给上层 JsonReader 读取
                baseStream.Seek(originalPos, SeekOrigin.Begin);
                return baseStream;
            }
        }

    }
}

//! ============================================
//! 数据快照容器
//! ============================================
namespace Framework.SaveManagement
{
    /// <summary>
    /// 通用 3D Transform 存档数据快照
    /// </summary>
    [Serializable]
    public struct SaveTransform
    {
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;

        public SaveTransform(Transform t)
        {
            if (t == null)
            {
                localPosition = Vector3.zero;
                localRotation = Quaternion.identity;
                localScale = Vector3.one;
                return;
            }

            localPosition = t.localPosition;
            localRotation = t.localRotation;
            localScale = t.localScale;
        }

        public void ApplyTo(Transform t)
        {
            if (t == null) return;
            t.localPosition = localPosition;
            t.localRotation = localRotation;
            t.localScale = localScale;
        }

        public static implicit operator SaveTransform(Transform t) => new(t);
    }

    /// <summary>
    /// UI 专用 RectTransform 存档数据快照
    /// </summary>
    [Serializable]
    public struct SaveRectTransform
    {
        public Vector2 anchoredPosition;
        public Vector2 sizeDelta;
        public Vector3 scale;
        public Quaternion rotation;

        public SaveRectTransform(RectTransform rt)
        {
            if (rt == null)
            {
                anchoredPosition = Vector2.zero;
                sizeDelta = Vector2.zero;
                scale = Vector3.one;
                rotation = Quaternion.identity;
                return;
            }

            anchoredPosition = rt.anchoredPosition;
            sizeDelta = rt.sizeDelta;
            scale = rt.localScale;
            rotation = rt.localRotation;
        }

        public void ApplyTo(RectTransform rt)
        {
            if (rt == null) return;
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = sizeDelta;
            rt.localScale = scale;
            rt.localRotation = rotation;
        }

        public static implicit operator SaveRectTransform(RectTransform rt) => new(rt);
    }
}