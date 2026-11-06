using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using DX.Core.ReactiveStateMachine;

namespace DX.Core.ReactiveStateMachine.Editor
{
    public class StateMachineDebuggerWindow : EditorWindow
    {
        // === 数据结构精简 ===
        private class FSMTargetInfo
        {
            public object Instance;
            public IStateMachineDebugger DebuggerInterface; // <--- 直接使用调试接口
            public string SourceName;
            public bool IsUIExpanded = true;
        }

        private GameObject _selectedObject;
        private List<FSMTargetInfo> _fsmTargets = new List<FSMTargetInfo>();

        private string[] _displayOptions = new string[0];
        private int _selectedDisplayIndex = 0;

        private bool _enableHistoryLimit = true;
        private int _maxHistoryCount = 5;

        private GUIStyle _headerStyle;
        private GUIStyle _currentStateStyle;
        private GUIStyle _historyStateStyle;
        private GUIStyle _arrowStyle;

        private Vector2 _scrollPosition;

        [MenuItem("Window/Analysis/◆ FSM Debugger ◆")]
        public static void ShowWindow()
        {
            var window = GetWindow<StateMachineDebuggerWindow>("FSM Debugger");
            window.minSize = new Vector2(300, 400);
            window.Show();
        }

        private void OnEnable() { Selection.selectionChanged += OnSelectionChange; OnSelectionChange(); }
        private void OnDisable() { Selection.selectionChanged -= OnSelectionChange; _fsmTargets.Clear(); }

        private void OnInspectorUpdate()
        {
            if (Application.isPlaying && _fsmTargets.Any(t => t.DebuggerInterface.IsRunning))
            {
                Repaint();
            }
        }

        private void OnSelectionChange()
        {
            if (Selection.activeGameObject != null)
            {
                _selectedObject = Selection.activeGameObject;
                FindStateMachinesOnTarget();
                Repaint();
            }
        }
        private void FindStateMachinesOnTarget()
        {
            _fsmTargets.Clear();
            if (_selectedObject == null) return;

            HashSet<object> uniqueInstances = new HashSet<object>();
            var components = _selectedObject.GetComponents<MonoBehaviour>();

            foreach (var comp in components)
            {
                if (comp == null) continue;
                Type type = comp.GetType();

                // 获取所有字段和属性
                var members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property);

                foreach (var member in members)
                {
                    // 1. 跳过已被标记为废弃的属性 (解决 rigidbody deprecated 报错)
                    if (Attribute.IsDefined(member, typeof(ObsoleteAttribute)))
                        continue;

                    object instance = null;

                    try
                    {
                        if (member is FieldInfo f)
                        {
                            instance = f.GetValue(comp);
                        }
                        else if (member is PropertyInfo p && p.CanRead)
                        {
                            // 2. 跳过索引器属性 (带有参数的属性不能直接 GetValue)
                            if (p.GetIndexParameters().Length > 0)
                                continue;

                            instance = p.GetValue(comp);
                        }
                    }
                    catch (Exception)
                    {
                        // 3. 忽略在获取值时抛出异常的 Unity 内部特殊属性，防止中断扫描
                        continue;
                    }

                    // 检查是否为我们要找的状态机调试接口
                    if (instance is IStateMachineDebugger debuggerInstance)
                    {
                        if (uniqueInstances.Add(instance))
                        {
                            _fsmTargets.Add(new FSMTargetInfo
                            {
                                Instance = instance,
                                DebuggerInterface = debuggerInstance,
                                SourceName = $"{type.Name}.{member.Name}"
                            });
                        }
                    }
                }
            }
            UpdateDisplayOptions();
        }

        private void UpdateDisplayOptions()
        {
            if (_fsmTargets.Count == 0)
            {
                _displayOptions = new string[0];
                _selectedDisplayIndex = 0;
                return;
            }

            _displayOptions = new string[_fsmTargets.Count + 1];
            _displayOptions[0] = "Show All (同步显示全部)";
            for (int i = 0; i < _fsmTargets.Count; i++)
            {
                _displayOptions[i + 1] = $"[{_fsmTargets[i].DebuggerInterface.MachineId}] {_fsmTargets[i].SourceName}";
            }
            if (_selectedDisplayIndex >= _displayOptions.Length) _selectedDisplayIndex = 0;
        }

        private void InitStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
                _currentStateStyle = new GUIStyle(GUI.skin.box) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = UnityEngine.Color.green } };
                _historyStateStyle = new GUIStyle(GUI.skin.box) { fontSize = 12, alignment = TextAnchor.MiddleCenter, normal = { textColor = UnityEngine.Color.gray } };
                _arrowStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, fontStyle = FontStyle.Bold };
            }
        }

        private void OnGUI()
        {
            InitStyles();

            GUILayout.Space(10);
            EditorGUILayout.LabelField("状态机目标检测", _headerStyle);

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.ObjectField("Target", _selectedObject, typeof(GameObject), true);
            EditorGUI.EndDisabledGroup();

            if (!Application.isPlaying) { EditorGUILayout.HelpBox("请进入 Play 模式并选择挂载了状态机的 GameObject 以查看运行状态。", MessageType.Info); return; }
            if (_selectedObject == null) { EditorGUILayout.HelpBox("未选择任何 GameObject。", MessageType.Warning); return; }
            if (_fsmTargets.Count == 0) { EditorGUILayout.HelpBox($"在 {_selectedObject.name} 上未检测到状态机实例。", MessageType.Warning); return; }

            GUILayout.Space(5);
            _selectedDisplayIndex = EditorGUILayout.Popup("视图模式", _selectedDisplayIndex, _displayOptions);
            EditorGUILayout.BeginHorizontal();
            _enableHistoryLimit = EditorGUILayout.ToggleLeft("记录显示上限", _enableHistoryLimit, GUILayout.Width(130));
            if (_enableHistoryLimit) _maxHistoryCount = EditorGUILayout.IntSlider(_maxHistoryCount, 1, 30);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(5);

            IEnumerable<FSMTargetInfo> targetsToRender = _selectedDisplayIndex == 0 ? _fsmTargets :
                (_selectedDisplayIndex - 1 < _fsmTargets.Count ? new[] { _fsmTargets[_selectedDisplayIndex - 1] } : _fsmTargets);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            foreach (var target in targetsToRender)
            {
                var fsm = target.DebuggerInterface; // 直接拿到专用的调试接口
                GUILayout.Space(5);

                bool forceExpand = (_selectedDisplayIndex != 0);
                string foldoutTitle = $"❖ [{fsm.MachineId}] [{fsm.RegisteredStateCount}] ({target.SourceName})";

                if (forceExpand) { EditorGUILayout.LabelField(foldoutTitle, EditorStyles.boldLabel); target.IsUIExpanded = true; }
                else target.IsUIExpanded = EditorGUILayout.Foldout(target.IsUIExpanded, foldoutTitle, true, EditorStyles.foldoutHeader);

                if (target.IsUIExpanded)
                {
                    EditorGUILayout.BeginVertical(GUI.skin.box);
                    float originalLabelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 30f;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("名称:", $"{fsm.MachineId}");
                    EditorGUILayout.LabelField("运行:", fsm.IsRunning ? "🟢 Running" : fsm.UseRouterMode ? "🔵 RouterMode" : "🔴 Stopped");
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("状态:", fsm.CurrentStateName);
                    EditorGUILayout.LabelField("时间:", fsm.TimeInState < 0 ? "未启用 (-1)" : $"{fsm.TimeInState:F2} 秒");
                    EditorGUILayout.EndHorizontal();

                    string childCountText = fsm.ConnectedChildMachineCount > 0 ? $"- 子节点: [{fsm.ConnectedChildMachineCount}]" : "";

                    if (!string.IsNullOrEmpty(fsm.ConnectedRouterName))
                        EditorGUILayout.LabelField("路由:", $"🌐 已连入 [{fsm.ConnectedRouterName}] {childCountText}");
                    else if (fsm.ConnectedChildMachineCount > 0)
                        EditorGUILayout.LabelField("路由:", $"🌐 根路由 (无父级) {childCountText}");
                    else
                        EditorGUILayout.LabelField("路由:", "🏠 本地独立 (未连入网络)");

                    EditorGUIUtility.labelWidth = originalLabelWidth;
                    EditorGUILayout.EndVertical();

                    // GUILayout.Space(5);
                    // EditorGUILayout.LabelField("轨迹流转 (最新在顶)", EditorStyles.miniBoldLabel);

                    if (fsm.IsRunning)
                    {
                        // 【直接强类型调用，彻底告别反射】
                        var historyEnumerable = fsm.GetStateHistory();
                        var totalHistoryCount = historyEnumerable.Count();

                        if (_enableHistoryLimit) historyEnumerable = historyEnumerable.Take(_maxHistoryCount);

                        EditorGUILayout.BeginVertical(GUI.skin.box);

                        GUI.backgroundColor = new UnityEngine.Color(0.2f, 0.8f, 0.2f, 0.5f);
                        GUILayout.Box($"▶ {fsm.CurrentStateName} ◀", _currentStateStyle, GUILayout.Height(35), GUILayout.ExpandWidth(true));
                        GUI.backgroundColor = UnityEngine.Color.white;

                        if (historyEnumerable != null)
                        {
                            foreach (var historyType in historyEnumerable)
                            {
                                // GUILayout.Label("↑", _arrowStyle);
                                GUILayout.Box($"{historyType.TransitionCount}. {historyType.StateType.Name}\n{historyType.TriggerInfo}", _historyStateStyle, GUILayout.Height(30), GUILayout.ExpandWidth(true));
                            }

                            if (_enableHistoryLimit && totalHistoryCount > _maxHistoryCount)
                            {
                                // GUILayout.Label("↑", _arrowStyle);
                                GUILayout.Box($"... 以及更早的 {totalHistoryCount - _maxHistoryCount} 个状态已隐藏 ...", _historyStateStyle, GUILayout.Height(20));
                            }
                        }
                        EditorGUILayout.EndVertical();
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(fsm.UseRouterMode ? "当前处于路由模式。" : "状态机未运行，暂无轨迹。", MessageType.None);
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("强制刷新视图")) { FindStateMachinesOnTarget(); Repaint(); }
            GUILayout.Space(5);
        }
    }
}