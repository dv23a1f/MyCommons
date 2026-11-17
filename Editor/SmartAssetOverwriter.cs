using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DX.Core.EditorScripts
{
    /* internal class SmartAssetOverwriter : AssetPostprocessor
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
    } */




    public class Overwriter : AssetPostprocessor
{
	private class FilePath
	{
		public string Path;
		public string FileName;

		public FilePath(string path)
		{
			Path = path;
			FileName = System.IO.Path.GetFileName(Path);
		}
	}

	private class ExistAsset
	{
		public FilePath Source;
		public FilePath Imported;

		public ExistAsset(FilePath source, FilePath imported)
		{
			Source = source;
			Imported = imported;
		}
	}

	const string SourceExistFormat = "「{0}」と「{1}」のファイル内容が完全一致しているため、置き換えツールの動作を停止します。\n本当にインポートしますか？";

	static void OnPostprocessAllAssets(
	  string[] importedAssets,
	  string[] deletedAssets,
	  string[] movedAssets,
	  string[] movedFromPath)
	{
		int count = importedAssets.Length;
		if (count == 0 || Event.current == null || Event.current.type != EventType.DragPerform)
		{
			return;
		}

		// ドラッグ＆ドロップ対象に.metaが含まれていた場合はインポート対象外なので除外
		List<string> dragAndDropPaths = new List<string>(DragAndDrop.paths);
		for (int i = 0; i < dragAndDropPaths.Count;)
		{
			if (dragAndDropPaths[i].EndsWith(".meta"))
			{
				dragAndDropPaths.RemoveAt(i);
				continue;
			}
			++i;
		}
		if (count != dragAndDropPaths.Count)
		{
			return;
		}

		// MEMO: プレハブの置き換えはどうやら危険（Re-Serializeが終わらなくなる）なので、一旦除外している
		List<FilePath> sourcePaths = new List<FilePath>(count);
		for (int i = 0; i < count; ++i)
		{
			if (dragAndDropPaths[i].EndsWith(".prefab"))
			{
				continue;
			}
			sourcePaths.Add(new FilePath(dragAndDropPaths[i]));
		}
		List<FilePath> importedPaths = new List<FilePath>(count);
		for (int i = 0; i < count; ++i)
		{
			if (importedAssets[i].EndsWith(".prefab"))
			{
				continue;
			}
			importedPaths.Add(new FilePath(importedAssets[i]));
		}

		// 元ファイルとインポートされたファイルで一致する名前があるかどうかすべて調べる
		int matchCnt = 0;
		for (; matchCnt < count; ++matchCnt)
		{
			string source = sourcePaths[matchCnt].FileName;
			int j = 0;
			for (; j < count; ++j)
			{
				if (source.Contains(importedPaths[j].FileName))
				{
					break;
				}
			}
			if (j == count)
			{
				break;
			}
		}
		// 置き換え処理自体不要
		if (matchCnt == count)
		{
			return;
		}

		// 元ファイルの中に内容が同一なものがあるとうまく動作しないので先に調べる
		bool isExecutable = true;
		bool isDeleteImportedAssets = false;
		for (int i = 0; i < count; ++i)
		{
			for (int j = i + 1; j < count; ++j)
			{
				FilePath path1 = sourcePaths[i];
				FilePath path2 = sourcePaths[j];

				if (FileCompare(path1.Path, path2.Path))
				{
					string message = string.Format(SourceExistFormat, path1.FileName, path2.FileName);
					isDeleteImportedAssets = !EditorUtility.DisplayDialog(
						"確認",
						message,
						"インポート",
						"中止");
					isExecutable = false;
					break;
				}
			}
			if (!isExecutable)
			{
				break;
			}
		}
		if (!isExecutable)
		{
			// インポートされたファイルをすべて消す
			if (isDeleteImportedAssets)
			{
				for (int i = 0; i < importedAssets.Length; ++i)
				{
					AssetDatabase.DeleteAsset(importedAssets[i]);
				}
			}
			return;
		}

		// ファイル名の一致しているものを除外（そのままの名前でインポートできているので重複していない）
		for (int i = 0; i < sourcePaths.Count;)
		{
			bool isRemoved = false;
			FilePath source = sourcePaths[i];
			for (int j = 0; j < importedPaths.Count; ++j)
			{
				FilePath imported = importedPaths[j];
				if (source.FileName != imported.FileName)
				{
					continue;
				}
				// ファイル名が一致していても内容が一致していないケースが存在する
				// 末尾が数字のファイル(hoge0)と同時にその後ろの番号(hoge1)をインポートすると、
				// hoge0がhoge1、hoge1がhoge2として作られる
				if (!FileCompare(source.Path, imported.Path))
				{
					// この場合はhoge1とhoge2を入れ替える
					for (int k = 0; k < importedPaths.Count; ++k)
					{
						if (j == k)
						{
							continue;
						}
						if (FileCompare(source.Path, importedPaths[k].Path))
						{
							string tempPath = imported.Path + "_temp";
							FileUtil.CopyFileOrDirectory(imported.Path, tempPath);
							FileUtil.ReplaceFile(importedPaths[k].Path, imported.Path);
							FileUtil.ReplaceFile(tempPath, importedPaths[k].Path);
							FileUtil.DeleteFileOrDirectory(tempPath);
							AssetDatabase.ImportAsset(imported.Path);
							AssetDatabase.ImportAsset(importedPaths[k].Path);
							break;
						}
					}
				}
				sourcePaths.RemoveAt(i);
				importedPaths.RemoveAt(j);
				isRemoved = true;
				break;
			}
			if (!isRemoved)
			{
				++i;
			}
		}

		// DragAndDrops.pathsはOSの仕様次第で順番がバラバラなので、
		// 実際にインポートされたファイルとソースファイルの内容を見て一致しているものをまとめる
		List<ExistAsset> existAssets = new List<ExistAsset>(sourcePaths.Count);
		for (int i = 0; i < sourcePaths.Count; i++)
		{
			FilePath source = sourcePaths[i];
			for (int j = 0; j < importedPaths.Count; ++j)
			{
				FilePath imported = importedPaths[j];
				if (!FileCompare(source.Path, imported.Path))
				{
					continue;
				}
				existAssets.Add(new ExistAsset(source, imported));
				importedPaths.RemoveAt(j);
				break;
			}
		}
		// ソースファイルのパスでソート
		existAssets.Sort((a, b) => a.Source.Path.CompareTo(b.Source.Path));

		var isFirst = true;
		var isSameAction = false;
		var result = 0;
		
		foreach (var exist in existAssets)
		{
			string importedPath = exist.Imported.Path;
			string importedAssetDirectory = Path.GetDirectoryName(importedPath);
			string existingAssetPath = string.Format("{0}/{1}", importedAssetDirectory, exist.Source.FileName);

			if (!isSameAction)
			{
				result = EditorUtility.DisplayDialogComplex(
					existingAssetPath.Replace('\\', '/'),
					"同じ名前のアセットが既に存在します。アセットを置き換えますか？",
					"置き換える",
					"中止",
					"両方とも残す");
			}

			if (result == 0)
			{
				FileUtil.ReplaceFile(importedPath, existingAssetPath);
				AssetDatabase.DeleteAsset(importedPath);
				AssetDatabase.ImportAsset(existingAssetPath);
			}
			else if (result == 1)
			{
				AssetDatabase.DeleteAsset(importedPath);
			}

			if (isFirst)
			{
				if (existAssets.Count > 2)
				{
					isSameAction = EditorUtility.DisplayDialog(
						"確認",
						"同じ操作を以降すべてに適用しますか？",
						"はい",
						"いいえ");
				}
				isFirst = false;
			}
		}
	}

	static bool FileCompare(string file1, string file2)
	{
		if (file1 == file2)
		{
			return true;
		}

		FileStream fs1 = new FileStream(file1, FileMode.Open);
		FileStream fs2 = new FileStream(file2, FileMode.Open);
		int byte1;
		int byte2;
		bool ret = false;

		try
		{
			if (fs1.Length == fs2.Length)
			{
				do
				{
					byte1 = fs1.ReadByte();
					byte2 = fs2.ReadByte();
				}
				while ((byte1 == byte2) && (byte1 != -1));

				if (byte1 == byte2)
				{
					ret = true;
				}
			}
		}
		catch (System.Exception e)
		{
			Debug.LogError(e);
			return false;
		}
		finally
		{
			fs1.Close();
			fs2.Close();
		}

		return ret;
	}
}
}