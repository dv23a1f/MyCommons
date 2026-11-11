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
