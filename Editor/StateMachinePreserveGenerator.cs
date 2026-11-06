#if UNITY_EDITOR
using System;
using System.Linq;
using System.Xml;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Framework.ReactiveStateMachine;

// 这个类会在打包(Build) IL2CPP 前自动运行
public class StateMachinePreserveGenerator : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        // 1. 找到你的状态基类或接口（IStateNode）
        Type baseType = typeof(IStateNode);

        // 2. 利用反射扫描所有程序集，找出所有它的实现类
        var stateTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return new Type[0]; }
            })
            .Where(t => t.IsClass && !t.IsAbstract && baseType.IsAssignableFrom(t))
            .ToList();

        if (stateTypes.Count == 0) return;

        // 3. 生成 link.xml 的内容
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
                // fullname 必须是完整的类名，如果是嵌套类，C# 用 '+' 分隔
                typeNode.SetAttribute("fullname", type.FullName.Replace('+', '/'));
                typeNode.SetAttribute("preserve", "all"); // 保留所有方法(包括无参构造)
                assemblyNode.AppendChild(typeNode);
            }
            root.AppendChild(assemblyNode);
        }

        // 4. 保存为 XML 给 IL2CPP 读取
        xmlDoc.Save(linkXmlPath);
        AssetDatabase.ImportAsset(linkXmlPath);

        UnityEngine.Debug.Log($"[框架打包工具] 已自动为 {stateTypes.Count} 个状态类生成防裁剪配置: {linkXmlPath}");
    }
}
#endif