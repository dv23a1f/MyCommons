using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DX.Core.EditorScripts
{
    public class SmartAssetOverwriter : AssetPostprocessor
    {
        private static bool _isProcessing = false;

        private static readonly HashSet<string> ExcludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".prefab", ".unity", ".meta", ".asset"
    };

        private static readonly Regex DuplicatePattern = new Regex(@"^(.*?)(?:[\s_\-\(]*|\s*)(\d+)\)?$");

        private class OverwriteTask
        {
            public string DuplicateAssetPath;
            public string OriginalAssetPath;
            public string DuplicateFullPath;
            public string OriginalFullPath;
            public string NewFileName;
            public string OriginalFileName;
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (_isProcessing) return;
            if (importedAssets == null || importedAssets.Length == 0) return;

            // 【关键点 1】：检测是否是由外部拖拽引发的操作
            // 如果是 Ctrl+D 复制、右键 Duplicate、代码生成等操作，DragAndDrop.paths 长度必为 0
            if (DragAndDrop.paths == null || DragAndDrop.paths.Length == 0)
            {
                return;
            }

            // 收集所有拖入的外部源文件的文件名集合（用于二次精准匹配）
            HashSet<string> draggedSourceFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in DragAndDrop.paths)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    draggedSourceFileNames.Add(Path.GetFileName(path));
                }
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath).Replace("\\", "/");
            List<OverwriteTask> tasks = new List<OverwriteTask>();

            foreach (var assetPath in importedAssets)
            {
                string unityPath = assetPath.Replace("\\", "/");
                if (!unityPath.StartsWith("Assets/")) continue;

                string ext = Path.GetExtension(unityPath);
                if (ExcludedExtensions.Contains(ext)) continue;

                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(unityPath);
                string directory = Path.GetDirectoryName(unityPath).Replace("\\", "/");

                Match match = DuplicatePattern.Match(fileNameWithoutExt);
                if (match.Success)
                {
                    string baseName = match.Groups[1].Value.TrimEnd();
                    if (string.IsNullOrEmpty(baseName)) continue;

                    string originalFileName = baseName + ext;

                    // 【关键点 2】：双重确认
                    // 只有当“拖入的源文件名”正好等于我们想要覆盖的“原文件名”时，才确认这是覆盖意图
                    // 彻底杜绝任何形式的误判
                    if (!draggedSourceFileNames.Contains(originalFileName))
                    {
                        continue;
                    }

                    string originalUnityPath = $"{directory}/{originalFileName}";
                    string originalFullPath = $"{projectRoot}/{originalUnityPath}";
                    string duplicateFullPath = $"{projectRoot}/{unityPath}";

                    if (File.Exists(originalFullPath) && !string.Equals(duplicateFullPath, originalFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        tasks.Add(new OverwriteTask
                        {
                            DuplicateAssetPath = unityPath,
                            OriginalAssetPath = originalUnityPath,
                            DuplicateFullPath = duplicateFullPath,
                            OriginalFullPath = originalFullPath,
                            NewFileName = Path.GetFileName(unityPath),
                            OriginalFileName = originalFileName
                        });
                    }
                }
            }

            if (tasks.Count == 0) return;

            try
            {
                _isProcessing = true;
                ProcessTasksSynchronous(tasks);
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private static void ProcessTasksSynchronous(List<OverwriteTask> tasks)
        {
            bool applyToAll = false;
            bool overwriteAll = false;

            foreach (var task in tasks)
            {
                if (!File.Exists(task.DuplicateFullPath) || !File.Exists(task.OriginalFullPath))
                {
                    continue;
                }

                bool doOverwrite = false;

                if (applyToAll)
                {
                    doOverwrite = overwriteAll;
                }
                else
                {
                    int option = EditorUtility.DisplayDialogComplex(
                        "检测到同名文件拖入",
                        $"拖入的文件与已有文件重名：\n\n新生成副本: {task.NewFileName}\n目标原文件: {task.OriginalFileName}\n\n是否直接【覆盖】原文件？\n（覆盖将保留原文件的 GUID 和场景/预制体引用）",
                        "覆盖",       // 0
                        "保留副本",   // 1
                        "全部覆盖"    // 2
                    );

                    if (option == 0)
                    {
                        doOverwrite = true;
                    }
                    else if (option == 2)
                    {
                        doOverwrite = true;
                        applyToAll = true;
                        overwriteAll = true;
                    }
                    else
                    {
                        doOverwrite = false;
                        if (tasks.Count > 1)
                        {
                            if (EditorUtility.DisplayDialog("提示", "后续所有重名文件是否全部【保留副本】？", "是", "否单个询问"))
                            {
                                applyToAll = true;
                                overwriteAll = false;
                            }
                        }
                    }
                }

                if (doOverwrite)
                {
                    ExecuteDirectOverwrite(task);
                }
            }
        }

        private static void ExecuteDirectOverwrite(OverwriteTask task)
        {
            try
            {
                var fileInfo = new FileInfo(task.OriginalFullPath);
                if (fileInfo.IsReadOnly) fileInfo.IsReadOnly = false;

                File.Copy(task.DuplicateFullPath, task.OriginalFullPath, overwrite: true);
                AssetDatabase.DeleteAsset(task.DuplicateAssetPath);
                AssetDatabase.ImportAsset(task.OriginalAssetPath, ImportAssetOptions.ForceUpdate);

                Debug.Log($"<color=#00FF7F><b>[成功覆盖]</b></color> 已覆盖原文件: {task.OriginalAssetPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[覆盖失败] {task.OriginalAssetPath} 异常: {ex.Message}");
            }
        }
    }
}