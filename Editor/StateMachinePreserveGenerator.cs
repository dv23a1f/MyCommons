#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Framework.ReactiveStateMachine;

// 这个类会在打包(Build) IL2CPP 前自动运行
public class StateMachinePreserveGenerator : IPreprocessBuildWithReport
{
    // 优先级保持 0，比 EventChannel (callbackOrder = 1) 稍早执行
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        // 1. 获取当前打包平台的后端类型 (消除过时警告)
        NamedBuildTarget namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);
        ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(namedBuildTarget);

        // 2. 如果是 Mono 模式，直接跳过生成
        if (backend != ScriptingImplementation.IL2CPP)
        {
            Debug.Log("[StateMachine] 当前非 IL2CPP 模式 (使用 Mono)，跳过 link.xml 生成。");
            return;
        }

        // 3. IL2CPP 模式下执行生成
        GenerateLinkXml();
    }

    [MenuItem("Tools/PreserveGenerator/手动生成 StateMachine link.xml")]
    public static void GenerateLinkXml()
    {
        // 1. 找到状态基类或接口
        Type baseType = typeof(IStateNode);

        // 2. 利用反射扫描所有程序集，找出所有实现类
        var stateTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return new Type[0]; }
            })
            .Where(t => t.IsClass && !t.IsAbstract && baseType.IsAssignableFrom(t))
            .ToList();

        if (stateTypes.Count == 0)
        {
            Debug.LogWarning("[StateMachine] 未扫描到任何实现了 IStateNode 的状态类！");
            return;
        }

        // 3. 构建 link.xml
        string linkXmlPath = "Assets/StateMachine_Auto_link.xml";
        XmlDocument xmlDoc = new XmlDocument();
        XmlElement root = xmlDoc.CreateElement("linker");
        xmlDoc.AppendChild(root);

        // 按程序集分组
        var groups = stateTypes.GroupBy(t => t.Assembly.GetName().Name);
        foreach (var group in groups)
        {
            XmlElement assemblyNode = xmlDoc.CreateElement("assembly");
            assemblyNode.SetAttribute("fullname", group.Key);

            foreach (var type in group)
            {
                XmlElement typeNode = xmlDoc.CreateElement("type");
                // fullname 必须是完整的类名，嵌套类使用 '/' 替代 '+'
                typeNode.SetAttribute("fullname", type.FullName.Replace('+', '/'));
                typeNode.SetAttribute("preserve", "all"); // 保留所有方法(包括无参构造)
                assemblyNode.AppendChild(typeNode);
            }
            root.AppendChild(assemblyNode);
        }

        // 4. 保存为 XML 给 IL2CPP 读取并刷新
        xmlDoc.Save(linkXmlPath);
        AssetDatabase.ImportAsset(linkXmlPath);

        Debug.Log($"<color=green>[StateMachine] 已成功为 {stateTypes.Count} 个状态类生成防裁剪配置 -> {linkXmlPath}</color>");
    }
}
#endif