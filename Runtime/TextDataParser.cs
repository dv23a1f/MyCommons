using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Utility.TextDriven
{
    /// <summary>
    /// 数据上下文提供者接口
    /// </summary>
    public interface IVariableProvider
    {
        object Get(string varName);

        /// <summary>
        /// 写入/修改变量值。如果成功写入返回 true，找不到变量返回 false
        /// </summary>
        bool Set(string varName, object value);
    }

    //!!=========================================
    #region 通用数据行定义 (TextRow)

    /// <summary>
    /// 代表解析完成的文本指令文档
    /// </summary>
    public class TextDocument : IReadOnlyList<TextRow>
    {
        private readonly List<TextRow> _rows;

        // 行号反查缓存（使整篇文档的行号定位达到 O(1) 性能）
        private readonly Dictionary<int, TextRow> _lineLookup;

        public TextDocument(List<TextRow> rows)
        {
            _rows = rows ?? new List<TextRow>();
            _lineLookup = new Dictionary<int, TextRow>(_rows.Count);

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                _lineLookup[row.LineNumber] = row;
            }

        }

        public int Count => _rows.Count;
        public TextRow this[int index] => _rows[index];
        public IEnumerator<TextRow> GetEnumerator() => _rows.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// 覆盖通用的 O(N) 查找，利用缓存实现 O(1) 秒查
        /// </summary>
        public TextRow FindByLine(int lineNumber)
        {
            _lineLookup.TryGetValue(lineNumber, out var row);
            return row;
        }
    }
    /// <summary>
    /// 代表解析出的一行通用数据
    /// </summary>
    public class TextRow : IReadOnlyList<string>
    {
        public TextRow(int rowIndex, int lineNumber, string[] rawTokens)
        {
            RowIndex = rowIndex;
            LineNumber = lineNumber;
            RawTokens = rawTokens ?? Array.Empty<string>();
        }

        /// <summary>运行时指令数组下标（等价于 List 的 IndexOf）</summary>
        public int RowIndex { get; private set; }
        /// <summary>所在原始行号</summary>
        public int LineNumber { get; private set; }
        /// <summary>当前行切分后的所有纯字符串元素</summary>       
        public string[] RawTokens { get; private set; }

        /// <summary>
        /// 当前行绑定的数据上下文（可选）。如果赋值，后续无需每次手动传入 Blackboard
        /// </summary>
        public IVariableProvider Provider { get; set; }

        /// <summary>
        /// 当前行包含的元素个数
        /// </summary>
        public int Count => RawTokens.Length;

        /// <summary>
        /// 允许像数组一样直接访问原始字符串: row[0]
        /// </summary>
        public string this[int index] => RawTokens[index];

        /// <summary>
        /// 获取指定索引的元素，并自动翻译为目标强类型 T
        /// </summary>
        public T Get<T>(int index) => (T)Get(index, typeof(T));

        /// <summary>
        /// 反射用：获取指定索引元素并转换为 Type
        /// </summary>
        public object Get(int index, Type targetType)
        {
            if (index < 0 || index >= RawTokens.Length)
            {
                throw new IndexOutOfRangeException($"[第 {LineNumber} 行] 索引越界: 期望索引 {index}，但该行只有 {RawTokens.Length} 个元素。");
            }
            return ScriptParser.ConvertValue(RawTokens[index], targetType, LineNumber);
        }

        public override string ToString()
        {
            return $"Line {LineNumber}: [{string.Join(", ", RawTokens)}]";
        }

        public IEnumerator<string> GetEnumerator()
        {
            return ((IEnumerable<string>)RawTokens).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

    }

    #endregion
    //!!=========================================
    #region 文本解析器

    /// <summary>
    /// 通用增强文本解析器（支持 (), "", Vector3, Color 等）
    /// </summary>
    public static class ScriptParser
    {
        private static readonly Regex InterpolationRegex =
            new Regex(@"(?:\{\$|\$\{)([a-zA-Z0-9_\.]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// 合并多个纯文本脚本内容为一个完整的文本文档字符串
        /// </summary>
        /// <param name="textContents">文本内容集合</param>
        /// <param name="instructionDelimiter">
        /// 可选的指令分隔符（如 ";"）。
        /// 为 null 或空时：默认以换行符分隔（适用于按行解析模式）；
        /// 指定字符时：会自动检查并确保各文档末尾闭合该分隔符，并追加换行防止注释行吞噬。
        /// </param>
        /// <returns>合并后的单一文本文档，可直接传入 ParseDocument 解析</returns>
        public static string MergeDocuments(
            IEnumerable<string> textContents,
            string instructionDelimiter = null)
        {
            if (textContents == null) return string.Empty;

            var sb = new StringBuilder();
            bool hasDelimiter = !string.IsNullOrEmpty(instructionDelimiter);
            char delimChar = hasDelimiter ? instructionDelimiter[0] : '\0';

            foreach (var content in textContents)
            {
                if (string.IsNullOrWhiteSpace(content)) continue;

                string trimmed = content.TrimEnd();
                if (trimmed.Length == 0) continue;

                // 如果不是第一个文档，确保前面有换行隔开
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(trimmed);

                // 如果指定了指令分隔符（如 ';'），且文档末尾没有该分隔符，自动补齐
                if (hasDelimiter && !trimmed.EndsWith(instructionDelimiter))
                {
                    sb.Append(delimChar);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// params 便捷重载（string[]）
        /// </summary>
        public static string MergeDocuments(params string[] textContents)
        {
            return MergeDocuments(textContents, null);
        }

        /// <summary>
        /// 合并多个 Unity TextAsset 资源为一个完整的文本文档字符串
        /// </summary>
        public static string MergeDocuments(
            IEnumerable<TextAsset> textAssets,
            string instructionDelimiter = null)
        {
            if (textAssets == null) return string.Empty;

            var contents = new List<string>();
            foreach (var asset in textAssets)
            {
                if (asset != null && !string.IsNullOrWhiteSpace(asset.text))
                {
                    contents.Add(asset.text);
                }
            }

            return MergeDocuments(contents, instructionDelimiter);
        }

        /// <summary>
        /// params 便捷重载（TextAsset[]）
        /// </summary>
        public static string MergeDocuments(params TextAsset[] textAssets)
        {
            return MergeDocuments(textAssets, null);
        }

        /// <summary>
        /// 文本插值格式化 (例如将 "你好，{$Player.name}" 替换为真实名字)
        /// </summary>
        public static string FormatText(string rawTemplate, IVariableProvider provider)
        {
            if (string.IsNullOrEmpty(rawTemplate)) return string.Empty;

            return InterpolationRegex.Replace(rawTemplate, match =>
            {
                // 捕获组 1 直接就是干净的变量名（不需要再 TrimStart('$')）
                string varName = match.Groups[1].Value;
                object val = provider.Get(varName);

                // 如果在黑板里找到了变量，替换为值；如果找不到，保留原始写法 (如 "{$Gold}")
                return val != null ? val.ToString() : match.Value;
            });
        }

        /// <summary>
        /// 解析整个文本文档
        /// </summary>
        /// <param name="textContent">脚本纯文本</param>
        /// <param name="instructionDelimiter">
        /// 指令之间的分割符：
        /// 为 null 或空字符串时，默认按回车换行（\r\n, \n, \r）分割；
        /// 若指定字符（例如 ";"），则以此字符作为一条指令的结束标记。
        /// </param>
        public static TextDocument ParseDocument(
            string textContent,
            string instructionDelimiter = null)
        {
            var result = new List<TextRow>();
            if (string.IsNullOrWhiteSpace(textContent))
                return new TextDocument(result);

            // 如果未指定分隔符，按行切分
            if (string.IsNullOrEmpty(instructionDelimiter))
            {
                string[] lines = textContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                for (int i = 0; i < lines.Length; i++)
                {
                    int currentLineNumber = i + 1;
                    string line = lines[i].Trim();

                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("//"))
                        continue;

                    List<string> tokens = SplitLine(line);
                    if (tokens.Count == 0) continue;

                    result.Add(new TextRow(
                        rowIndex: result.Count,
                        lineNumber: currentLineNumber,
                        rawTokens: tokens.ToArray())
                    );
                }
                return new TextDocument(result);
            }

            // ===== 指定了分隔符（如 ';'）=====
            var blocks = SplitTopLevelWithComments(textContent, instructionDelimiter[0]);

            foreach (var (blockText, realLine) in blocks)
            {
                string trimmedBlock = blockText.Trim();
                if (string.IsNullOrEmpty(trimmedBlock)) continue;

                List<string> tokens = SplitLine(trimmedBlock);
                if (tokens.Count == 0) continue;

                result.Add(new TextRow(
                    rowIndex: result.Count,
                    lineNumber: realLine,
                    rawTokens: tokens.ToArray()));
            }

            return new TextDocument(result);
        }

        /// <summary>
        /// 增强版顶层切分：
        /// 1. 自动忽略并剔除 // 和 # 单行注释（双引号内部受保护不受影响）
        /// 2. 换行符不影响指令，精准累计行号
        /// </summary>
        private static List<(string content, int lineNumber)> SplitTopLevelWithComments(string text, char delimiter)
        {
            var result = new List<(string, int)>();
            var current = new StringBuilder();

            bool inQuotes = false;
            int parenDepth = 0;

            int currentLine = 1;
            int blockStartLine = -1; // -1 表示当前还没遇到新指令的第一个有效字符

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '\n')
                {
                    currentLine++;
                }

                // 1. 单行注释消除（不在双引号内部时）
                if (!inQuotes && (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/')))
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }
                    if (i < text.Length && text[i] == '\n')
                    {
                        currentLine++;
                    }
                    continue;
                }

                // 记录指令块真正的起始行号（忽略前导空格与换行）
                if (blockStartLine == -1 && !char.IsWhiteSpace(c) && c != delimiter)
                {
                    blockStartLine = currentLine;
                }

                // 2. 转义符跳过
                if (c == '\\' && i + 1 < text.Length)
                {
                    current.Append(c);
                    current.Append(text[++i]);
                    continue;
                }

                // 3. 引号和括号状态判定
                if (c == '\"')
                {
                    inQuotes = !inQuotes;
                    current.Append(c);
                    continue;
                }
                else if (c == '(' && !inQuotes)
                {
                    parenDepth++;
                    current.Append(c);
                    continue;
                }
                else if (c == ')' && !inQuotes)
                {
                    parenDepth = Mathf.Max(0, parenDepth - 1);
                    current.Append(c);
                    continue;
                }

                // 4. 遇到指令切分符（如 ';'）
                if (c == delimiter && !inQuotes && parenDepth == 0)
                {
                    if (current.Length > 0)
                    {
                        result.Add((current.ToString(), blockStartLine == -1 ? currentLine : blockStartLine));
                        current.Clear();
                    }
                    blockStartLine = -1; // 重置，等待下一条指令的第一个非空字符
                }
                else
                {
                    current.Append(c);
                }
            }

            if (inQuotes)
            {
                throw new FormatException($"解析失败：文档结尾存在未闭合的双引号！(起始于第 {blockStartLine} 行)");
            }

            if (current.Length > 0 && !string.IsNullOrWhiteSpace(current.ToString()))
            {
                result.Add((current.ToString(), blockStartLine == -1 ? currentLine : blockStartLine));
            }

            return result;
        }

        /// <summary>
        /// 切割单行指令参数：以逗号分割，完美支持转义引号 \" 以及 CSV双引号 ""
        /// </summary>
        public static List<string> SplitLine(string line)
        {
            List<string> tokens = new List<string>();
            StringBuilder currentToken = new StringBuilder();

            bool inQuotes = false;
            int parenDepth = 0;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                // ===== 核心优化 1: 处理反斜杠转义 \" 和 \\ =====
                if (c == '\\' && i + 1 < line.Length)
                {
                    char nextChar = line[i + 1];
                    if (nextChar == '\"' || nextChar == '\\')
                    {
                        // 连同反斜杠一起先存进去，保留给 CleanToken 做还原
                        currentToken.Append(c);
                        currentToken.Append(nextChar);
                        i++; // 跳过下一个字符
                        continue;
                    }
                }

                // ===== 核心优化 2: 处理标准 CSV 规范的连续双引号 "" =====
                if (c == '\"' && inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                {
                    currentToken.Append("\"\""); // 记为双引号字面量
                    i++; // 跳过下一个双引号
                    continue;
                }

                // 普通状态检测
                if (c == '\"')
                {
                    inQuotes = !inQuotes;
                    currentToken.Append(c);
                }
                else if (c == '(' && !inQuotes)
                {
                    parenDepth++;
                    currentToken.Append(c);
                }
                else if (c == ')' && !inQuotes)
                {
                    parenDepth = Mathf.Max(0, parenDepth - 1);
                    currentToken.Append(c);
                }
                else if (c == ',' && !inQuotes && parenDepth == 0)
                {
                    tokens.Add(CleanToken(currentToken.ToString()));
                    currentToken.Clear();
                }
                else
                {
                    currentToken.Append(c);
                }
            }

            if (inQuotes)
            {
                throw new FormatException($"行解析失败：存在未闭合的双引号 -> {line}");
            }
            if (parenDepth > 0)
            {
                throw new FormatException($"行解析失败：存在未闭合的括号 -> {line}");
            }

            if (currentToken.Length > 0)
            {
                tokens.Add(CleanToken(currentToken.ToString()));
            }

            return tokens;
        }

        /// <summary>
        /// 清理 Token：剥离最外层双引号，并将内部转义还原回真实字符
        /// </summary>
        private static string CleanToken(string token)
        {
            token = token.Trim();

            // 1. 剥离最外层的包裹双引号
            if (token.StartsWith("\"") && token.EndsWith("\"") && token.Length >= 2)
            {
                token = token.Substring(1, token.Length - 2);
            }

            // 2. 还原 CSV 格式的双写引号: "" -> "
            if (token.Contains("\"\""))
            {
                token = token.Replace("\"\"", "\"");
            }

            // 3. 还原反斜杠转义: \" -> " 和 \\ -> \
            if (token.Contains("\\\""))
            {
                token = token.Replace("\\\"", "\"");
            }
            if (token.Contains("\\\\"))
            {
                token = token.Replace("\\\\", "\\");
            }

            return token;
        }

        public static T ConvertValue<T>(string rawValue) => (T)ConvertValue(rawValue, typeof(T), -1);

        public static object ConvertValue(string rawValue, Type targetType, int lineNumber = -1)
        {
            rawValue = rawValue.Trim();

            try
            {

                if (targetType == typeof(string)) return rawValue;
                if (targetType == typeof(int)) return int.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(long)) return long.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(float)) return float.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(double)) return double.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(bool)) return bool.Parse(rawValue);

                if (targetType.IsArray)
                {
                    Type elementType = targetType.GetElementType();
                    return ParseArray(rawValue, elementType, lineNumber);
                }

                if (targetType == typeof(Vector2Int))
                {
                    float[] v = ParseFloatArray(rawValue, 2);
                    return new Vector2Int(Mathf.RoundToInt(v[0]), Mathf.RoundToInt(v[1]));
                }

                if (targetType == typeof(Vector3Int))
                {
                    float[] v = ParseFloatArray(rawValue, 3);
                    return new Vector3Int(Mathf.RoundToInt(v[0]), Mathf.RoundToInt(v[1]), Mathf.RoundToInt(v[2]));
                }

                if (targetType == typeof(Vector2))
                {
                    float[] v = ParseFloatArray(rawValue, 2);
                    return new Vector2(v[0], v[1]);
                }

                if (targetType == typeof(Vector3))
                {
                    float[] v = ParseFloatArray(rawValue, 3);
                    return new Vector3(v[0], v[1], v[2]);
                }

                if (targetType == typeof(Vector4))
                {
                    float[] v = ParseFloatArray(rawValue, 4);
                    return new Vector4(v[0], v[1], v[2], v[3]);
                }

                if (targetType == typeof(Quaternion))
                {
                    // 传入 -1 表示不强制长度，由实际解析出的数量决定
                    float[] v = ParseFloatArray(rawValue, -1);
                    if (v.Length == 3)
                    {
                        // 欧拉角：(0, 90, 0)
                        return Quaternion.Euler(v[0], v[1], v[2]);
                    }
                    if (v.Length == 4)
                    {
                        // 四元数：(0, 0.7071, 0, 0.7071)
                        return new Quaternion(v[0], v[1], v[2], v[3]);
                    }
                    throw new FormatException($"无法将 \"{rawValue}\" 解析为 Quaternion，分量必须为 3 个（欧拉角）或 4 个（四元数分量）。");
                }

                if (targetType == typeof(Color))
                {
                    if (rawValue.StartsWith("#"))
                    {
                        if (ColorUtility.TryParseHtmlString(rawValue, out Color c)) return c;
                    }
                    else if (rawValue.StartsWith("(") && rawValue.EndsWith(")"))
                    {
                        float[] c = ParseFloatArray(rawValue, -1);
                        if (c.Length == 3) return new Color(c[0], c[1], c[2], 1f);
                        if (c.Length == 4) return new Color(c[0], c[1], c[2], c[3]);
                    }
                    throw new FormatException($"无法解析颜色格式: {rawValue}");
                }

                if (targetType.IsEnum)
                {
                    object parsedEnum = Enum.Parse(targetType, rawValue, true);

                    // 如果是数字且在枚举中未定义，抛出更明确的异常进行拦截
                    if (!Enum.IsDefined(targetType, parsedEnum))
                    {
                        throw new ArgumentException($"数值 '{rawValue}' 不是枚举 {targetType.Name} 中已定义的有效 ID！");
                    }

                    return parsedEnum;
                }
            }
            catch (Exception ex)
            {
                string lineInfo = lineNumber > 0 ? $"[第 {lineNumber} 行] " : "";
                throw new InvalidCastException($"{lineInfo}转换失败: 无法将 \"{rawValue}\" 转换为 {targetType.Name}。原因: {ex.Message}");
            }

            throw new NotSupportedException($"未支持的转换类型: {targetType.FullName}");
        }

        // 泛型便捷重载
        public static T ConvertValue<T>(object rawValue) => (T)ConvertValue(rawValue, typeof(T), -1);

        /// <summary>
        /// 通用对象类型转换（支持 string、数值、枚举、Unity 结构体等互转）
        /// </summary>
        public static object ConvertValue(object rawValue, Type targetType, int lineNumber = -1)
        {
            if (rawValue == null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    return Activator.CreateInstance(targetType);
                return null;
            }

            // 1. 类型完全匹配或有继承关系直接返回
            if (targetType.IsInstanceOfType(rawValue))
                return rawValue;

            // 2. 解包 Nullable<T>
            Type underlyingType = Nullable.GetUnderlyingType(targetType);
            if (underlyingType != null)
                targetType = underlyingType;

            // 3. 处理 Enum 转换（支持数字装箱、字符串名称转换）
            if (targetType.IsEnum)
            {
                try
                {
                    if (rawValue is sbyte || rawValue is byte || rawValue is short || rawValue is ushort ||
                        rawValue is int || rawValue is uint || rawValue is long || rawValue is ulong)
                    {
                        object enumObj = Enum.ToObject(targetType, rawValue);
                        if (!Enum.IsDefined(targetType, enumObj))
                            throw new ArgumentException($"数值 '{rawValue}' 不是枚举 {targetType.Name} 中已定义的有效 ID！");
                        return enumObj;
                    }

                    // 非整数类型（如 string）转为字符串后解析
                    return ConvertValue(rawValue.ToString(), targetType, lineNumber);
                }
                catch (Exception ex)
                {
                    string lineInfo = lineNumber > 0 ? $"[第 {lineNumber} 行] " : "";
                    throw new InvalidCastException($"{lineInfo}转换失败: 无法将 \"{rawValue}\" 转换为枚举 {targetType.Name}。原因: {ex.Message}", ex);
                }
            }

            // 4. 如果是 string，走原本现有的 ConvertValue(string, Type, int)
            if (rawValue is string str)
            {
                return ConvertValue(str, targetType, lineNumber);
            }

            // 5. 基础类型转换兜底
            try
            {
                return Convert.ChangeType(rawValue, targetType, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                string lineInfo = lineNumber > 0 ? $"[第 {lineNumber} 行] " : "";
                throw new InvalidCastException($"{lineInfo}转换失败: 无法将类型 '{rawValue.GetType().Name}' (值: {rawValue}) 转换为 {targetType.Name}。原因: {ex.Message}", ex);
            }
        }
        /// <summary>
        /// 通用数组解析：支持 T[] (如 int[], float[], string[], MyEnum[])
        /// </summary>
        private static object ParseArray(string raw, Type elementType, int lineNumber)
        {
            string inner = raw.Trim();

            // 1. 剥离外层的 () 或 []
            if ((inner.StartsWith("(") && inner.EndsWith(")")) ||
                (inner.StartsWith("[") && inner.EndsWith("]")))
            {
                inner = inner.Substring(1, inner.Length - 2).Trim();
            }

            // 2. 空数组支持：处理 "()" 或 "[]" 或 "" 的情况
            if (string.IsNullOrEmpty(inner))
            {
                return Array.CreateInstance(elementType, 0);
            }

            // 3. 复用切分逻辑（完美支持带括号嵌套、引号的元素）
            List<string> items = SplitLine(inner);

            // 4. 创建目标强类型数组
            Array array = Array.CreateInstance(elementType, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                // 递归调用 ConvertValue：每个子元素自动应用类型转换（支持递归转换枚举、基础类型等）
                object elementVal = ConvertValue(items[i], elementType, lineNumber);
                array.SetValue(elementVal, i);
            }

            return array;
        }
        private static float[] ParseFloatArray(string raw, int expectedLength)
        {
            string inner = raw.Trim();
            if (inner.StartsWith("(") && inner.EndsWith(")"))
            {
                inner = inner.Substring(1, inner.Length - 2);
            }

            string[] parts = inner.Split(',');
            if (expectedLength > 0 && parts.Length != expectedLength)
            {
                throw new FormatException($"分量不匹配，期望 {expectedLength} 个，实际为 {parts.Length} 个");
            }

            float[] result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            }
            return result;
        }

    }

    #endregion
    //!!=========================================
    #region 自动黑板

    /// <summary>
    /// 自动黑板系统：解耦、零侵入，支持任意类/结构的字段扫描
    /// </summary>
    public class AutoBlackboard : IVariableProvider
    {
        // 统一的变量访问器元数据
        private class VariableAccessor
        {
            public object Target;          // 绑定的宿主实例（用于反查注销）
            public Func<object> Getter;    // 读委托
            public Action<object> Setter;  // 写委托
        }

        // C# 实体映射池: 变量名 -> 访问器
        private readonly Dictionary<string, VariableAccessor> _accessors =
            new Dictionary<string, VariableAccessor>(StringComparer.OrdinalIgnoreCase);

        // 临时变量池: 变量名 -> 动态值
        private readonly Dictionary<string, object> _dynamicVariables =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 注册并自动扫描实例中的字段加入黑板，可指定前缀 "Player" 后使用 Player.Hp 调用
        /// </summary>
        public AutoBlackboard Register(object target, string prefix = null)
        {
            if (target == null) return this;

            // 1. 安全拦截：严禁绑定值类型结构体（防止装箱静默修改失效）
            if (target.GetType().IsValueType)
            {
                throw new ArgumentException($"[Blackboard] 不支持注册值类型结构体 '{target.GetType().Name}'！" +
                                            $"黑板必须绑定引用类型（Class）以保证修改能同步生效。");
            }

            // 2. 先冲刷注销该实例旧的残留绑定（防止同实例重复注册）
            Unregister(target);

            Type targetType = target.GetType();
            string prefixStr = string.IsNullOrWhiteSpace(prefix) ? "" : $"{prefix.Trim()}.";

            // 3. 扫描字段 (Field)
            FieldInfo[] fields = targetType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var field in fields)
            {
                if (!field.IsPublic && !field.IsDefined(typeof(SerializeField), true))
                    continue;

                if (!TypeValidator.IsSupported(field.FieldType))
                    continue;

                string key = $"{prefixStr}{field.Name}";

                // 一旦 C# 实体认领该 Key，立刻销毁动态池中的同名幽灵数据
                _dynamicVariables.Remove(key);

                var accessor = new VariableAccessor
                {
                    Target = target,
                    Getter = () => IsTargetAlive(target) ? field.GetValue(target) : null
                };

                // 排除 readonly / const 字段
                if (!field.IsInitOnly && !field.IsLiteral)
                {
                    accessor.Setter = (val) =>
                    {
                        if (IsTargetAlive(target))
                        {
                            object converted = AdaptValue(val, field.FieldType);
                            field.SetValue(target, converted);
                        }
                    };
                }

                _accessors[key] = accessor;
            }

            // 4. 扫描属性 (Property)
            PropertyInfo[] properties = targetType.GetProperties(
                BindingFlags.Instance | BindingFlags.Public);

            foreach (var prop in properties)
            {
                if (!TypeValidator.IsSupported(prop.PropertyType))
                    continue;

                string key = $"{prefixStr}{prop.Name}";

                _dynamicVariables.Remove(key);

                var accessor = new VariableAccessor
                {
                    Target = target
                };

                if (prop.CanRead)
                {
                    accessor.Getter = () => IsTargetAlive(target) ? prop.GetValue(target) : null;
                }

                if (prop.CanWrite)
                {
                    accessor.Setter = (val) =>
                    {
                        if (IsTargetAlive(target))
                        {
                            object converted = AdaptValue(val, prop.PropertyType);
                            prop.SetValue(target, converted);
                        }
                    };
                }

                _accessors[key] = accessor;
            }

            return this;
        }

        /// <summary>
        /// 动态源注册：只扫描一次类型结构，每次求值时实时拉取 targetGetter() 返回的当前实例
        /// 注销时直接传入该委托对象：blackboard.Unregister(targetGetter);
        /// </summary>
        public AutoBlackboard Register<T>(Func<T> targetGetter, string prefix = null) where T : class
        {
            if (targetGetter == null) return this;

            // 防止委托重复注册，以委托实例作为 Target 锚点
            Unregister(targetGetter);

            T probeInstance = targetGetter();
            Type targetType = probeInstance != null ? probeInstance.GetType() : typeof(T);

            string prefixStr = string.IsNullOrWhiteSpace(prefix) ? "" : $"{prefix.Trim()}.";

            // 1. 扫描字段
            FieldInfo[] fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                if (!field.IsPublic && !field.IsDefined(typeof(SerializeField), true))
                    continue;

                if (!TypeValidator.IsSupported(field.FieldType))
                    continue;

                string key = $"{prefixStr}{field.Name}";
                _dynamicVariables.Remove(key);

                var accessor = new VariableAccessor
                {
                    Target = targetGetter, // 记录委托为宿主
                    Getter = () =>
                    {
                        var instance = targetGetter();
                        return IsTargetAlive(instance) ? field.GetValue(instance) : null;
                    }
                };

                if (!field.IsInitOnly && !field.IsLiteral)
                {
                    accessor.Setter = (val) =>
                    {
                        var instance = targetGetter();
                        if (IsTargetAlive(instance))
                        {
                            object converted = AdaptValue(val, field.FieldType);
                            field.SetValue(instance, converted);
                        }
                    };
                }

                _accessors[key] = accessor;
            }

            // 2. 扫描属性
            PropertyInfo[] properties = targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            foreach (var prop in properties)
            {
                if (!TypeValidator.IsSupported(prop.PropertyType))
                    continue;

                string key = $"{prefixStr}{prop.Name}";
                _dynamicVariables.Remove(key);

                var accessor = new VariableAccessor
                {
                    Target = targetGetter
                };

                if (prop.CanRead)
                {
                    accessor.Getter = () =>
                    {
                        var instance = targetGetter();
                        return IsTargetAlive(instance) ? prop.GetValue(instance) : null;
                    };
                }

                if (prop.CanWrite)
                {
                    accessor.Setter = (val) =>
                    {
                        var instance = targetGetter();
                        if (IsTargetAlive(instance))
                        {
                            object converted = AdaptValue(val, prop.PropertyType);
                            prop.SetValue(instance, converted);
                        }
                    };
                }

                _accessors[key] = accessor;
            }

            return this;
        }

        /// <summary>
        /// 根据实体对象精准注销（支持普通实体 object，或动态注册的 targetGetter 委托）
        /// 彻底解除内存强引用并清扫关联的动态池
        /// </summary>
        public void Unregister(object target)
        {
            if (target == null) return;

            var keysToRemove = new List<string>();

            // 1. 通过指针引用安全匹配属于该目标的所有 Key
            foreach (var kvp in _accessors)
            {
                if (ReferenceEquals(kvp.Value.Target, target))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            // 2. 拔除映射，并联动清除同名动态残留
            for (int i = 0; i < keysToRemove.Count; i++)
            {
                string key = keysToRemove[i];

                // 在移除前，主动切断内部所有引用链路
                if (_accessors.TryGetValue(key, out var acc))
                {
                    acc.Target = null;
                    acc.Getter = null;
                    acc.Setter = null;
                }

                _accessors.Remove(key);
                _dynamicVariables.Remove(key);
            }
        }
        // <summary>
        /// 根据注册时指定的前缀批量注销变量（例如传入 "Player"，将注销所有以 "Player." 开头的字段/属性及动态变量）
        /// 彻底切断委托引用，防止内存泄漏。
        /// </summary>
        /// <param name="prefix">注册时使用的前缀（如 "Player" 或 "Enemy"）</param>
        public void UnregisterByPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return;

            // 规范化匹配前缀：去除首尾空格及多余的点，强制以 "." 结尾
            // 防止 prefix 为 "Play" 时误把 "Player.Hp" 删掉
            string targetPrefix = prefix.Trim().TrimEnd('.') + ".";

            var keysToRemove = new List<string>();

            // 1. 扫描并收集所有符合前缀的 C# 映射 Key
            foreach (var key in _accessors.Keys)
            {
                if (key.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(key);
                }
            }

            // 2. 拔除 C# 访问器映射并切断强引用
            for (int i = 0; i < keysToRemove.Count; i++)
            {
                string key = keysToRemove[i];
                if (_accessors.TryGetValue(key, out var acc))
                {
                    acc.Target = null;
                    acc.Getter = null;
                    acc.Setter = null;
                }

                _accessors.Remove(key);
                _dynamicVariables.Remove(key);
            }

            // 3. 扫描并清除同前缀的动态临时变量（如运行时脚本写入的 Player.CustomVar）
            keysToRemove.Clear();
            foreach (var key in _dynamicVariables.Keys)
            {
                if (key.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(key);
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                _dynamicVariables.Remove(keysToRemove[i]);
            }
        }

        /// <summary>
        /// 获取变量值，如不存在则尝试从临时变量池查找
        /// </summary>
        public object Get(string varName)
        {
            varName = NormalizeKey(varName);

            // 1. 优先从 C# 映射中取值
            if (_accessors.TryGetValue(varName, out var accessor))
            {
                if (accessor.Getter != null)
                {
                    return accessor.Getter.Invoke();
                }
            }

            // 2. 兜底从动态变量池读取
            if (_dynamicVariables.TryGetValue(varName, out var dynVal))
            {
                return dynVal;
            }

            Debug.LogWarning($"[Blackboard] 找不到变量: '{varName}'");
            return null;
        }

        /// <summary>
        /// 设置/覆盖变量值，如不存在则写入临时变量池
        /// </summary>
        public bool Set(string varName, object value)
        {
            varName = NormalizeKey(varName);

            // 1. 检查是否存在 C# 映射
            if (_accessors.TryGetValue(varName, out var accessor))
            {
                if (accessor.Setter != null)
                {
                    accessor.Setter.Invoke(value);
                    return true;
                }

                // 只读保护拦截
                if (accessor.Getter != null)
                {
                    Debug.LogWarning($"[Blackboard] 变量 '{varName}' 是只读的 (C# 只读属性或 const/readonly)，无法赋值！");
                    return false;
                }
            }

            // 2. 确属未绑定的纯脚本变量，存入动态池
            _dynamicVariables[varName] = value;
            return true;
        }

        public object GetOrAdd(string varName, Type targetType)
        {
            varName = NormalizeKey(varName);

            // 1. 尝试从 C# 映射取
            if (_accessors.TryGetValue(varName, out var accessor) && accessor.Getter != null)
            {
                return accessor.Getter.Invoke();
            }

            // 2. 尝试从动态池取
            if (_dynamicVariables.TryGetValue(varName, out var dynVal))
            {
                return dynVal;
            }

            // 3. 都没有，则生成默认值并持久化至动态变量池
            object defaultValue;
            if (targetType == typeof(string))
            {
                defaultValue = string.Empty; // 字符串默认为空字符串而非 null，防止后续报错
            }
            else if (targetType != null && targetType.IsValueType)
            {
                defaultValue = Activator.CreateInstance(targetType);
            }
            else
            {
                defaultValue = null;
            }

            _dynamicVariables[varName] = defaultValue;
            return defaultValue;
        }

        public T GetOrAdd<T>(string varName, T defaultValue = default)
        {
            TypeValidator.EnsureValid<T>();
            varName = NormalizeKey(varName);

            if (_accessors.TryGetValue(varName, out var accessor) && accessor.Getter != null)
            {
                return (T)ScriptParser.ConvertValue(accessor.Getter.Invoke(), typeof(T));
            }

            if (_dynamicVariables.TryGetValue(varName, out var dynVal))
            {
                return (T)ScriptParser.ConvertValue(dynVal, typeof(T));
            }

            _dynamicVariables[varName] = defaultValue;
            return defaultValue;
        }

        // =========== 查询===============

        /// <summary>
        /// 查询指定的 C# 宿主实例（或委托）是否已被黑板注册
        /// </summary>
        public bool IsRegistered(object target)
        {
            if (target == null) return false;

            foreach (var accessor in _accessors.Values)
            {
                if (ReferenceEquals(accessor.Target, target))
                {
                    return true; // O(N) 遍历访问器池，只要匹配到一个即说明已注册
                }
            }

            return false;
        }

        /// <summary>
        /// 查询指定的前缀命名空间是否已被任何实体或变量占用（不区分大小写）
        /// 例如传入 "Player"，若已存在 "Player.Hp" 则返回 true
        /// </summary>
        public bool IsPrefixRegistered(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return false;

            string targetPrefix = prefix.Trim().TrimEnd('.') + ".";

            // 1. 检查 C# 映射
            foreach (var key in _accessors.Keys)
            {
                if (key.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 2. 检查动态变量池
            foreach (var key in _dynamicVariables.Keys)
            {
                if (key.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        public bool HasVariable(string varName)
        {
            varName = NormalizeKey(varName);
            return _accessors.ContainsKey(varName) || _dynamicVariables.ContainsKey(varName);
        }

        /// <summary>
        /// 导出纯动态变量字典（快照）
        /// </summary>
        public Dictionary<string, string> ExportState(bool includeBoundCSharpEntities = false)
        {
            var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (includeBoundCSharpEntities)
            {
                foreach (var kvp in _accessors)
                {
                    if (kvp.Value.Getter != null)
                    {
                        var val = kvp.Value.Getter.Invoke();
                        if (val != null)
                        {
                            state[kvp.Key] = val.ToString();
                        }
                    }
                }
            }

            foreach (var kvp in _dynamicVariables)
            {
                if (kvp.Value != null)
                {
                    state[kvp.Key] = kvp.Value.ToString();
                }
            }

            return state;
        }

        /// <summary>
        /// 还原存档状态
        /// </summary>
        public void ImportState(Dictionary<string, string> savedState, bool clearExistingDynamic = true)
        {
            if (savedState == null) return;

            if (clearExistingDynamic)
            {
                _dynamicVariables.Clear();
            }

            foreach (var kvp in savedState)
            {
                Set(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// 清空所有注册项
        /// </summary>
        public void Clear()
        {
            _accessors.Clear();
            _dynamicVariables.Clear();
        }

        // ================= 内部辅助 =================

        private static bool IsTargetAlive(object target)
        {
            if (target == null) return false;
            if (target is UnityEngine.Object unityObj)
            {
                return unityObj != null;
            }
            return true;
        }

        private static string NormalizeKey(string rawKey)
        {
            if (string.IsNullOrWhiteSpace(rawKey)) return string.Empty;
            return rawKey.Trim().TrimStart('$');
        }

        private object AdaptValue(object val, Type targetType) => ScriptParser.ConvertValue(val, targetType);
    }
    #endregion
    //!!=========================================
    #region 动态扩展方法
    // ====================================================
    // IReadOnlyList<TextRow> 扩展
    // ====================================================
    /// <summary>
    /// 列表扩展
    /// </summary>
    public static class TextRowContextExtensions
    {
        /// <summary>
        /// 全局切换/绑定数据上下文黑板
        /// </summary>
        public static void Bind(this IReadOnlyList<TextRow> rows, IVariableProvider provider)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Provider = provider;
            }
        }

        // ==========================================
        // 元素/指令过滤扩展方法 (Filter / Where)
        // ==========================================
        /// <summary>
        /// 筛选出指定列索引匹配目标文本的所有行（例如筛选所有 columnIndex 为 0 且为 "ADD" 的指令）
        /// </summary>
        /// <param name="rows">行集合</param>
        /// <param name="columnIndex">目标列的索引（从 0 开始）</param>
        /// <param name="expectedValue">期望匹配的文本内容</param>
        /// <param name="ignoreCase">是否忽略大小写（默认为 true，即 "add" 和 "ADD" 都会匹配）</param>
        public static List<TextRow> FilterBy(
            this IReadOnlyList<TextRow> rows,
            int columnIndex,
            string expectedValue,
            bool ignoreCase = true)
        {
            var result = new List<TextRow>();
            if (rows == null || rows.Count == 0 || columnIndex < 0) return result;

            StringComparison comparison = ignoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                // 安全拦截：确保当前行没有越界
                if (row != null && columnIndex < row.Count)
                {
                    if (string.Equals(row[columnIndex], expectedValue, comparison))
                    {
                        result.Add(row);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 泛型重载：支持按强类型过滤（例如按枚举 CommandType.ADD 或 int 数值直接匹配）
        /// 内部自动利用已有架构的 ConvertValue 进行比对。
        /// </summary>
        public static List<TextRow> FilterBy<T>(
            this IReadOnlyList<TextRow> rows,
            int columnIndex,
            T expectedValue)
        {
            var result = new List<TextRow>();
            if (rows == null || rows.Count == 0 || columnIndex < 0) return result;

            var comparer = EqualityComparer<T>.Default;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row != null && columnIndex < row.Count)
                {
                    try
                    {
                        // 利用现有的强类型转换进行匹配
                        T val = row.Get<T>(columnIndex);
                        if (comparer.Equals(val, expectedValue))
                        {
                            result.Add(row);
                        }
                    }
                    catch
                    {
                        // 如果当前行的该列不是目标类型（比如格式不符），静默忽略并跳过
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 按照从第 0 列开始的连续前缀匹配单行，找到即停。找不到返回 null。
        /// 例如查找：rows.FindRowByPrefix("LABEL", "Intro_Start")
        /// </summary>
        public static TextRow FindRowByPrefix(
            this IReadOnlyList<TextRow> rows,
            params string[] prefixTokens)
        {
            if (rows == null || prefixTokens == null || prefixTokens.Length == 0)
                return null;

            int matchLen = prefixTokens.Length;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.Count < matchLen)
                    continue;

                bool isMatch = true;
                for (int j = 0; j < matchLen; j++)
                {
                    if (!string.Equals(row[j], prefixTokens[j], StringComparison.OrdinalIgnoreCase))
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                {
                    return row; // 找到了立刻返回，无额外垃圾分配
                }
            }

            return null;
        }

        /// <summary>
        /// 筛选出前缀连续匹配的所有行。
        /// 例如提取所有角色 Player 的对话：rows.FilterByPrefix("DIALOGUE", "Player")
        /// </summary>
        public static List<TextRow> FilterByPrefix(
            this IReadOnlyList<TextRow> rows,
            params string[] prefixTokens)
        {
            var result = new List<TextRow>();
            if (rows == null || prefixTokens == null || prefixTokens.Length == 0)
                return result;

            int matchLen = prefixTokens.Length;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.Count < matchLen)
                    continue;

                bool isMatch = true;
                for (int j = 0; j < matchLen; j++)
                {
                    if (!string.Equals(row[j], prefixTokens[j], StringComparison.OrdinalIgnoreCase))
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                {
                    result.Add(row);
                }
            }

            return result;
        }

        /// <summary>
        /// 从指定索引开始查找下一个满足条件的行索引。
        /// </summary>
        /// <param name="rows">指令行列表</param>
        /// <param name="startIndex">开始查找的行索引（包含该索引）</param>
        /// <param name="match">匹配条件</param>
        /// <returns>找到的行索引；若未找到或参数不合法返回 -1</returns>
        public static int FindNextIndex(
            this IReadOnlyList<TextRow> rows,
            int startIndex,
            Predicate<TextRow> match)
        {
            if (rows == null || match == null || rows.Count == 0)
                return -1;

            // 边界保护：若 startIndex 小于 0，自动从 0 开始
            if (startIndex < 0) startIndex = 0;
            int count = rows.Count;

            for (int i = startIndex; i < count; i++)
            {
                var row = rows[i];
                if (row != null && match(row))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 从指定索引开始倒序查找上一个满足条件的行索引。
        /// </summary>
        /// <param name="rows">指令行列表</param>
        /// <param name="startIndex">开始查找的行索引（包含该索引，往 0 的方向搜索）</param>
        /// <param name="match">匹配条件</param>
        /// <returns>找到的行索引；若未找到或参数不合法返回 -1</returns>
        public static int FindPreviousIndex(
            this IReadOnlyList<TextRow> rows,
            int startIndex,
            Predicate<TextRow> match)
        {
            if (rows == null || match == null || rows.Count == 0)
                return -1;

            // 边界保护：若超出末尾，从最后一行开始往前找
            if (startIndex >= rows.Count) startIndex = rows.Count - 1;

            for (int i = startIndex; i >= 0; i--)
            {
                var row = rows[i];
                if (row != null && match(row))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 通用自定义条件查找单行
        /// </summary>
        public static TextRow FindRow(this IReadOnlyList<TextRow> rows, Predicate<TextRow> match)
        {
            if (rows == null || match == null) return null;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row != null && match(row))
                {
                    return row;
                }
            }
            return null;
        }

        /// <summary>
        /// 在指定的“开始条件/起始位置”与“结束条件”区间内，筛选出所有满足条件的行（不包含标记行本身）。
        /// </summary>
        /// <param name="rows">行集合</param>
        /// <param name="startIndex">开始搜索的行索引（默认为 0）</param>
        /// <param name="startMatch">开始条件：为 null 时直接以 startIndex 作为起点；不为 null 时向后定位首个标记（不包含该标记行）</param>
        /// <param name="filterMatch">行过滤条件：为 null 时视为全部匹配通过</param>
        /// <param name="endMatch">结束条件：遇到满足该条件的行立即终止（不包含该标记行）；为 null 时一直检索到末尾</param>
        /// <returns>区间内部符合条件的行列表</returns>
        public static List<TextRow> FilterBetween(
            this IReadOnlyList<TextRow> rows,
            int startIndex = 0,
            Predicate<TextRow> startMatch = null,
            Predicate<TextRow> filterMatch = null,
            Predicate<TextRow> endMatch = null)
        {
            var result = new List<TextRow>();
            if (rows == null || rows.Count == 0) return result;

            if (startIndex < 0) startIndex = 0;
            if (startIndex >= rows.Count) return result;

            // 1. 确定数据截取的真实起始索引
            int actualStart = startIndex;
            if (startMatch != null)
            {
                int found = rows.FindNextIndex(startIndex, startMatch);
                if (found == -1) return result; // 指定了开始标记但没找到，直接返回空

                // 找到了开始标记，数据从标记的下一行开始
                actualStart = found + 1;
            }

            // 2. 顺序收集区间内部的内容行
            for (int i = actualStart; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null) continue;

                // 遇到结束标记立即跳出（不收集该行）
                if (endMatch != null && endMatch(row))
                {
                    break;
                }

                // 收集内容行
                if (filterMatch == null || filterMatch(row))
                {
                    result.Add(row);
                }
            }
            return result;
        }

        // ==========================================
        // 结构化展方法
        // ==========================================
        /// <summary>
        /// 在指令流中寻找当前嵌套块对应的闭合行索引（基于深度计数）。
        /// 未找到闭合行时会抛出带准确行号的语法格式异常。
        /// </summary>
        /// <param name="rows">指令行列表</param>
        /// <param name="startIndex">块开始的行索引</param>
        /// <param name="isOpen">判断是否为进入更深一层块的开标签条件</param>
        /// <param name="isClose">判断是否为闭合当前层级块的闭标签条件</param>
        /// <returns>当前块闭合标签所在的行索引</returns>
        public static int FindBlockEnd(
            this IReadOnlyList<TextRow> rows,
            int startIndex,
            Predicate<TextRow> isOpen,
            Predicate<TextRow> isClose)
        {
            if (rows == null || startIndex < 0 || startIndex >= rows.Count || isOpen == null || isClose == null)
                return -1;

            int depth = 1;
            for (int i = startIndex + 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.Count == 0) continue;

                // 遇到同类新块开启，深度加深
                if (isOpen(row))
                {
                    depth++;
                }
                // 遇到块闭合，深度回退
                else if (isClose(row))
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i; // 成功匹配到属于本层级的真正结束标签
                    }
                }
            }

            int errorLine = rows[startIndex].LineNumber;
            throw new FormatException($"[第 {errorLine} 行] 语法错误：未闭合的代码块！起始指令: '{rows[startIndex]}'");
        }
        /// <summary>
        /// 筛选出当前代码块内部所有“同级（非嵌套）”的有效指令。
        /// 自动跳过更深层级的嵌套块，未找到闭合行时抛出语法异常。
        /// </summary>
        /// <param name="rows">指令行列表</param>
        /// <param name="startIndex">块起始标签的行索引（例如外层 IF 所在行）</param>
        /// <param name="isOpen">嵌套子块的开启条件（例如 r => r[0] == "IF"）</param>
        /// <param name="isClose">块闭合条件（例如 r => r[0] == "END_IF"）</param>
        /// <param name="filter">可选的指令过滤器：为 null 时返回当前块所有非嵌套指令；指定时只返回匹配的指令（如仅筛选同级的 ELSE / ELSE_IF）</param>
        /// <returns>当前块作用域下的直接子指令列表</returns>
        public static List<TextRow> FilterBlockElements(
            this IReadOnlyList<TextRow> rows,
            int startIndex,
            Predicate<TextRow> isOpen,
            Predicate<TextRow> isClose,
            Predicate<TextRow> filter = null)
        {
            var result = new List<TextRow>();
            if (rows == null || startIndex < 0 || startIndex >= rows.Count || isOpen == null || isClose == null)
                return result;

            int depth = 1;
            for (int i = startIndex + 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.Count == 0) continue;

                // 1. 遇到子块开启标签：深度加深，跳过该行
                if (isOpen(row))
                {
                    depth++;
                    continue;
                }

                // 2. 遇到闭合标签
                if (isClose(row))
                {
                    depth--;
                    // 深度归零，说明整个顶层块已正常结束
                    if (depth == 0)
                    {
                        return result;
                    }
                    continue; // 子嵌套块的闭合标签，跳过
                }

                // 3. 只有深度为 1 时，才是外层块的直接内容（排除所有嵌套指令）
                if (depth == 1)
                {
                    if (filter == null || filter(row))
                    {
                        result.Add(row);
                    }
                }
            }

            // 语法防护：遍历结束但括号/标签未正确闭合
            int errorLine = rows[startIndex].LineNumber;
            throw new FormatException($"[第 {errorLine} 行] 语法错误：未闭合的代码块！起始指令: '{rows[startIndex]}'");
        }
        // ==========================================
        // 实用扩展方法
        // ==========================================
        /// <summary>
        /// 截取两个行号之间的所有行（闭区间 [startLine, endLine]）
        /// </summary>
        public static List<TextRow> SliceByLines(this IReadOnlyList<TextRow> rows, int startLine, int endLine)
        {
            var result = new List<TextRow>();
            if (rows == null) return result;

            for (int i = 0; i < rows.Count; i++)
            {
                int line = rows[i].LineNumber;
                if (line >= startLine && line <= endLine)
                {
                    result.Add(rows[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// 提取所有行中指定列的数据，形成强类型的强类型列表（类似 LINQ 中的 Select(r => r.Get(col))）
        /// </summary>
        public static List<T> SelectColumn<T>(this IReadOnlyList<TextRow> rows, int columnIndex)
        {
            var list = new List<T>();
            if (rows == null) return list;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row != null && columnIndex < row.Count)
                {
                    list.Add(row.Get<T>(columnIndex));
                }
            }
            return list;
        }
    }

    // ====================================================
    // TextRow 扩展
    // ====================================================
    /// <summary>
    /// TextRow 扩展
    /// </summary>
    public static class TextRowExtensions
    {
        // <summary>
        /// 安全读取指定索引的值：如果索引越界或转换失败，则返回预设的默认值
        /// </summary>
        public static T GetOrDefault<T>(this TextRow row, int index, T defaultValue = default)
        {
            if (row == null || index < 0 || index >= row.Count)
                return defaultValue;

            try
            {
                return row.Get<T>(index);
            }
            catch
            {
                return defaultValue;
            }
        }
        /// <summary>
        /// 获取从 startIndex 开始一直到末尾的所有剩余元素，并转为强类型数组 T[]
        /// 例如: row.GetTailFrom<string>(2) 可以一口气获取第 2 列及之后的所有标签
        /// </summary>
        public static T[] GetTailFrom<T>(this TextRow row, int startIndex)
        {
            if (row == null || startIndex >= row.Count)
                return Array.Empty<T>();

            int count = row.Count - startIndex;
            T[] result = new T[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = row.Get<T>(startIndex + i);
            }
            return result;
        }

        /// <summary>
        /// 获取指定索引的文本，并自动执行黑板变量插值（例如将 "你好，{$Player.name}" 替换为真实内容）。
        /// 上下文优先使用显式传入的 provider，未传入则默认使用 row.Provider。
        /// </summary>
        public static string GetFormatted(this TextRow row, int index, IVariableProvider provider = null)
        {
            if (row == null || index < 0 || index >= row.Count)
                return string.Empty;

            string rawText = row[index];
            if (string.IsNullOrEmpty(rawText))
                return string.Empty;

            // 如果不包含插值占位符，直接返回原始字符串，避免正则开销
            if (!rawText.Contains("{$") && !rawText.Contains("${"))
            {
                return rawText;
            }

            IVariableProvider p = provider ?? row.Provider;
            if (p == null)
            {
                // 如果没绑定黑板，返回原始文本即可，避免抛异常中断剧情
                return rawText;
            }

            return ScriptParser.FormatText(rawText, p);
        }

        /// <summary>
        /// 检查指定索引的元素是否能匹配目标强类型值 T。
        /// （如果索引越界、类型无法转换或值不匹配，均返回 false）
        /// </summary>
        public static bool IsMatch<T>(this TextRow row, int index, T value)
        {
            if (row == null || index < 0 || index >= row.Count)
                return false;

            // 特殊情况：如果两者都期望匹配 null
            if (value == null && string.IsNullOrEmpty(row[index]))
                return true;

            try
            {
                T currentVal = row.Get<T>(index);
                return EqualityComparer<T>.Default.Equals(currentVal, value);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 校验当前行元素数量是否符合预期
        /// </summary>
        public static TextRow AssertMinCount(this TextRow row, int minCount, string commandHelp = null)
        {
            if (row.Count < minCount)
            {
                string help = string.IsNullOrEmpty(commandHelp) ? "" : $" 正确格式应为: {commandHelp}";
                throw new FormatException($"[第 {row.LineNumber} 行] 参数不足！当前指令需要至少 {minCount} 个参数，但实际只有 {row.Count} 个。{help}");
            }
            return row; // 支持链式调用
        }

        // =======================================
        // 动态评估
        // =======================================

        /// <summary>
        /// 动态评估以$开头的变量，并自动分流：（支持基础类型 string、bool、Enum，或基础 Unity 数据结构）
        /// 1. 条件表达式: $Gold &gt; 10 || $Hp &lt; 5 。
        /// 2. 数学公式: ($Atk * 1.5) - $Def，支持常用函数: min(a,b), max(a,b), clamp(val,min,max), abs(x), floor(x), ceil(x), round(x), sqrt(x), pow(x,y)。
        /// 3. 单变量读取: $Player.Name / $Player.Pos。
        /// 4. 静态字面量: 兜底直接返回。
        /// </summary>
        public static T Eval<T>(this TextRow row, int index, IVariableProvider provider = null)
        {
            IVariableProvider p = ResolveProvider(row, provider);
            return row.Compile<T>(index).Invoke(p);
        }

        /// <summary>
        /// 将指定列编译为无状态的强类型委托 Func&lt;IVariableProvider, T&gt;
        /// </summary>
        public static Func<IVariableProvider, T> Compile<T>(this TextRow row, int index)
        {
            var statelessFunc = ExpressionParser.Compile<T>(row.Get<string>(index), row.LineNumber);
            return statelessFunc;
        }

        /// <summary>
        /// 直接解析并执行当前列的赋值表达式（即时执行，例如 "$Player.hp += 10"）
        /// </summary>
        public static void Assign(this TextRow row, int index, IVariableProvider provider = null)
        {
            IVariableProvider p = ResolveProvider(row, provider);
            // 编译为无状态委托并立即传入上下文执行
            row.CompileAssign(index).Invoke(p);
        }

        /// <summary>
        /// 从指定列解析赋值表达式，返回无状态委托 Action&lt;IVariableProvider&gt;
        /// </summary>
        public static Action<IVariableProvider> CompileAssign(this TextRow row, int index)
        {
            string expr = row.Get<string>(index);
            return AssignmentParser.ParseAssignment(expr, row.LineNumber);
        }

        /// <summary>
        /// 将当前行指定索引列所声明的黑板变量（如 "$Player.Hp" 或 "Gold"），直接赋值为给定的强类型值 T。
        /// </summary>
        /// <typeparam name="T">支持基础数值、bool、string、枚举或 Vector3/Color 等任意类型</typeparam>
        /// <param name="row">当前指令行</param>
        /// <param name="index">存储变量名的列索引（例如 1 或 2）</param>
        /// <param name="value">要写入的具体强类型值</param>
        /// <param name="provider">可选的黑板上下文，若为 null 自动使用 row.Provider</param>
        public static void SetVariable<T>(this TextRow row, int index, T value, IVariableProvider provider = null)
        {
            if (row == null || index < 0 || index >= row.Count)
            {
                throw new IndexOutOfRangeException($"[第 {row?.LineNumber} 行] 变量写入失败：索引 {index} 越界！");
            }

            IVariableProvider p = ResolveProvider(row, provider);
            string rawVarName = row[index];

            if (string.IsNullOrWhiteSpace(rawVarName))
            {
                throw new ArgumentException($"[第 {row.LineNumber} 行] 第 {index} 列未指定有效的变量名！");
            }

            // 自动兼容去壳：无论是 "$Player.Hp" 还是 "Player.Hp" 都能正确识别
            string varName = rawVarName.Trim().TrimStart('$');

            // 写入黑板（内部会自动处理类型适配与映射）
            p.Set(varName, value);
        }

        // =======================================
        // 内部辅助
        // =======================================

        // 统一的安全获取上下文方法
        private static IVariableProvider ResolveProvider(TextRow row, IVariableProvider explicitProvider)
        {
            var p = explicitProvider ?? row.Provider;
            if (p == null)
            {
                throw new InvalidOperationException($"[第 {row.LineNumber} 行] 试图提取动态值，但当前行未绑定任何 IVariableProvider，也未显式传入！");
            }
            return p;
        }

    }
    #endregion
    //!!=========================================
    #region 表达式编译器
    /// <summary>
    /// 通用动态表达式编译器（支持 Inspector 配置字符串、CSV 单元格等）
    /// 支持布尔条件判定、数学公式、文本插值、单变量读取及静态常量。
    /// </summary>
    public static class ExpressionParser
    {

        /// <summary>
        /// 将纯字符串编译为无状态的求值函数 Func<IVariableProvider, T>
        /// 零运行期分配，适合在 Awake / 配置初始化时缓存委托。
        /// </summary>
        /// <param name="expression">表达式字符串（如 "$Gold > 10"、"($Atk * 1.5) - $Def" 等）</param>
        /// <param name="lineNumber">行号（选填，用于报错排查）</param>
        public static Func<IVariableProvider, T> Compile<T>(string expression, int lineNumber = -1)
        {
            TypeValidator.EnsureValid<T>();
            Type targetType = typeof(T);

            if (string.IsNullOrWhiteSpace(expression))
            {
                return _ => default;
            }

            string rawText = expression.Trim();

            // 1. 布尔条件表达式
            if (targetType == typeof(bool))
            {
                var conditionFunc = ConditionParser.ParseCondition(rawText);
                return (Func<IVariableProvider, T>)(object)conditionFunc;
            }

            // 2. 字符串插值 (例如 "你好，{$Player.name}")
            if (targetType == typeof(string))
            {
                return (p) => (T)(object)ScriptParser.FormatText(rawText, p);
            }

            // 3. 非数值类型（枚举、Vector2/3/4、Color、Quaternion 或直接单变量读值）
            if (!TypeValidator.IsNumeric(targetType))
            {
                if (rawText.StartsWith("$"))
                {
                    string varName = rawText.Substring(1).Trim();
                    return (p) =>
                    {
                        object val = p?.Get(varName);
                        // object val = p?.GetOrAdd(varName, targetType);
                        return (T)ScriptParser.ConvertValue(val, targetType, lineNumber);
                    };
                }

                // 编译期直接确定常数值，求值时零开销
                T constantVal = (T)ScriptParser.ConvertValue(rawText, targetType, lineNumber);
                return _ => constantVal;
            }

            // 4. 数学公式 (数值计算)
            var mathFunc = MathExpressionParser.ParseMath<double>(rawText);
            return (p) => (T)ScriptParser.ConvertValue(mathFunc(p), targetType, lineNumber);
        }

    }

    /// <summary>
    /// 全局统一类型校验器：定义文本解析与黑板系统支持的安全类型白名单
    /// </summary>
    public static class TypeValidator
    {

        /// <summary>
        /// 校验非泛型 Type 是否在白名单中（供黑板反射扫描时使用）
        /// </summary>
        public static bool IsSupported(Type type)
        {
            if (type == null) return false;

            Type underlying = Nullable.GetUnderlyingType(type) ?? type;

            return underlying == typeof(string)
                || underlying == typeof(bool)
                || underlying.IsEnum
                || IsNumeric(underlying)
                || IsUnityStruct(underlying);
        }

        /// <summary>
        /// 校验泛型 T 是否在白名单中（利用内部私有泛型缓存，0 运行时反射开销）
        /// </summary>
        public static bool IsSupported<T>() => Cache<T>.IsValid;

        /// <summary>
        /// 强校验拦截：若不支持则直接抛出异常（供 ExpressionParser.Compile 编译期把关）
        /// </summary>
        public static void EnsureValid<T>() => Cache<T>.EnsureValid();

        /// <summary>
        /// 是否是支持的基础数值类型
        /// </summary>
        public static bool IsNumeric(Type type)
        {
            Type underlying = Nullable.GetUnderlyingType(type) ?? type;

            return underlying == typeof(int)
                || underlying == typeof(float)
                || underlying == typeof(double)
                || underlying == typeof(long)
                || underlying == typeof(short)
                || underlying == typeof(byte)
                || underlying == typeof(sbyte)
                || underlying == typeof(uint)
                || underlying == typeof(ulong)
                || underlying == typeof(ushort)
                || underlying == typeof(decimal);
        }

        /// <summary>
        /// 是否是支持的 Unity 常用值类型结构体
        /// </summary>
        public static bool IsUnityStruct(Type type)
        {
            Type underlying = Nullable.GetUnderlyingType(type) ?? type;

            return underlying == typeof(Vector2)
                || underlying == typeof(Vector3)
                || underlying == typeof(Vector4)
                || underlying == typeof(Vector2Int)
                || underlying == typeof(Vector3Int)
                || underlying == typeof(Color)
                || underlying == typeof(Quaternion);
        }

        /// <summary>
        /// 私有泛型嵌套类：利用 CLR 的泛型类型静态构造机制，首次访问时求值并永久缓存
        /// </summary>
        private static class Cache<T>
        {
            public static readonly bool IsValid;

            static Cache()
            {
                IsValid = TypeValidator.IsSupported(typeof(T));
            }

            public static void EnsureValid()
            {
                if (!IsValid)
                {
                    throw new NotSupportedException($"[TypeValidator 拦截] 不支持的类型 '{typeof(T).Name}'！仅支持: 基础数值、bool、string、Enum 及 Vector/Color/Quaternion 等。");
                }
            }
        }

    }

    public static class ConditionParser
    {
        private static readonly Regex BinaryOpRegex =
            new Regex(@"^(.*?)\s*(==|!=|>=|<=|>|<)\s*(.*)$", RegexOptions.Compiled);

        // ==================== 核心：无状态编译方法 ====================

        /// <summary>
        /// 将条件表达式编译为无状态的判定委托 Func&lt;IVariableProvider, T&gt;
        /// </summary>
        public static Func<IVariableProvider, bool> ParseCondition(string rawExpr)
        {
            if (string.IsNullOrWhiteSpace(rawExpr))
                return _ => true;

            rawExpr = rawExpr.Trim();

            // 1. 处理最外层括号剥除
            while (HasEnclosingParentheses(rawExpr))
            {
                rawExpr = rawExpr.Substring(1, rawExpr.Length - 2).Trim();
            }

            // 2. 自底向上切分逻辑操作符 (优先级: || < &&)
            int orIndex = FindTopLevelOperator(rawExpr, "||");
            if (orIndex != -1)
            {
                string leftStr = rawExpr.Substring(0, orIndex);
                string rightStr = rawExpr.Substring(orIndex + 2);
                var leftFunc = ParseCondition(leftStr);
                var rightFunc = ParseCondition(rightStr);
                // 短路求值 OR (延迟传入 provider)
                return (p) => leftFunc(p) || rightFunc(p);
            }

            int andIndex = FindTopLevelOperator(rawExpr, "&&");
            if (andIndex != -1)
            {
                string leftStr = rawExpr.Substring(0, andIndex);
                string rightStr = rawExpr.Substring(andIndex + 2);
                var leftFunc = ParseCondition(leftStr);
                var rightFunc = ParseCondition(rightStr);
                // 短路求值 AND (延迟传入 provider)
                return (p) => leftFunc(p) && rightFunc(p);
            }

            // 3. 原子条件解析
            return ParseSingleCondition(rawExpr);
        }

        // ==================== 便捷重载（向下兼容） ====================

        /// <summary>
        /// 针对特定 Provider 的有状态直接绑定重载
        /// </summary>
        public static Func<bool> ParseCondition(string rawExpr, IVariableProvider provider)
        {
            var statelessFunc = ParseCondition(rawExpr);
            return () => statelessFunc(provider);
        }

        // ==================== 内部实现细节 ====================

        private static Func<IVariableProvider, bool> ParseSingleCondition(string expr)
        {
            expr = expr.Trim();

            // 比较操作符 (==, !=, >, <, >=, <=)
            var match = BinaryOpRegex.Match(expr);
            if (match.Success)
            {
                string leftRaw = match.Groups[1].Value.Trim();
                string op = match.Groups[2].Value.Trim();
                string rightRaw = match.Groups[3].Value.Trim();

                var leftEvaluator = CreateValueGetter(leftRaw);
                var rightEvaluator = CreateValueGetter(rightRaw);

                return (p) => Evaluate(leftEvaluator(p), op, rightEvaluator(p));
            }

            // 单布尔表达式 (如 "$isDead" 或 "true")
            var singleGetter = CreateValueGetter(expr);
            return (p) =>
            {
                object val = singleGetter(p);
                return ScriptParser.ConvertValue<bool>(val);
            };
        }

        /// <summary>
        /// 编译变量/常量获取器：返回 Func&lt;IVariableProvider, T&gt;
        /// </summary>
        private static Func<IVariableProvider, object> CreateValueGetter(string raw)
        {
            raw = raw.Trim();

            // 动态变量: 执行时从传入的 p 身上取
            if (raw.StartsWith("$"))
            {
                string varName = raw.Substring(1).Trim();
                return (p) =>
                {
                    object val = p.Get(varName);
                    if (val == null)
                    {
                        throw new NullReferenceException($"未找到黑板变量: '{varName}'");
                    }
                    return val;
                };
            }

            // 常量字面量: 编译期就去掉引号，求值时直接返回，零分配
            string cleanLiteral = raw;
            if (cleanLiteral.StartsWith("\"") && cleanLiteral.EndsWith("\"") && cleanLiteral.Length >= 2)
            {
                cleanLiteral = cleanLiteral.Substring(1, cleanLiteral.Length - 2);
            }

            return _ => cleanLiteral;
        }

        private static int FindTopLevelOperator(string expr, string op)
        {
            int parenDepth = 0;
            bool inQuotes = false;

            for (int i = expr.Length - op.Length; i >= 0; i--)
            {
                char c = expr[i];

                if (c == '\"') inQuotes = !inQuotes;
                if (inQuotes) continue;

                if (c == ')') parenDepth++;
                else if (c == '(') parenDepth--;

                if (parenDepth == 0 && expr.Substring(i, op.Length) == op)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool HasEnclosingParentheses(string expr)
        {
            if (!expr.StartsWith("(") || !expr.EndsWith(")")) return false;

            int depth = 0;
            for (int i = 0; i < expr.Length; i++)
            {
                if (expr[i] == '(') depth++;
                else if (expr[i] == ')') depth--;

                if (depth == 0 && i < expr.Length - 1) return false;
            }
            return depth == 0;
        }

        private static bool Evaluate(object left, string op, object right)
        {
            if (left == null || right == null)
            {
                if (op == "==") return left == right;
                if (op == "!=") return left != right;
                throw new InvalidOperationException($"含有 null 的比较只支持 == 和 != 操作符。");
            }

            // 布尔比较
            if (left is bool || right is bool ||
                (bool.TryParse(left.ToString(), out _) && bool.TryParse(right.ToString(), out _)))
            {
                bool bLeft = left is bool lb ? lb : bool.Parse(left.ToString());
                bool bRight = right is bool rb ? rb : bool.Parse(right.ToString());

                if (op == "==") return bLeft == bRight;
                if (op == "!=") return bLeft != bRight;
                throw new InvalidOperationException($"布尔类型比较不支持操作符: {op}");
            }

            // 数值比较
            if (IsNumeric(left) && IsNumeric(right))
            {
                double numLeft = Convert.ToDouble(left, CultureInfo.InvariantCulture);
                double numRight = Convert.ToDouble(right, CultureInfo.InvariantCulture);

                switch (op)
                {
                    case "==": return Math.Abs(numLeft - numRight) < 0.00001;
                    case "!=": return Math.Abs(numLeft - numRight) > 0.00001;
                    case ">": return numLeft > numRight;
                    case ">=": return numLeft >= numRight;
                    case "<": return numLeft < numRight;
                    case "<=": return numLeft <= numRight;
                }
            }

            // 字符串比较
            string strLeft = left.ToString();
            string strRight = right.ToString();

            if (op == "==") return strLeft == strRight;
            if (op == "!=") return strLeft != strRight;

            throw new NotSupportedException($"类型无法使用 '{op}' 进行比较: \"{left}\" 与 \"{right}\"");
        }

        private static bool IsNumeric(object value)
        {
            if (value is sbyte || value is byte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong ||
                value is float || value is double || value is decimal)
            {
                return true;
            }

            return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out _);
        }
    }

    public static class MathExpressionParser
    {
        private static readonly Regex TokenRegex =
            new Regex(@"(\$?[a-zA-Z0-9_\.]+|\+|\-|\*|\/|\(|\)|,)", RegexOptions.Compiled);

        // 支持的数学函数映射表 (参数个数, 求值逻辑)
        // 委托入参是已按从左到右排好序的参数数组 double[]
        private static readonly Dictionary<string, (int argCount, Func<double[], double> func)> SupportedFunctions =
            new Dictionary<string, (int, Func<double[], double>)>(StringComparer.OrdinalIgnoreCase)
            {
                { "min",   (2, args => Math.Min(args[0], args[1])) },
                { "max",   (2, args => Math.Max(args[0], args[1])) },
                { "abs",   (1, args => Math.Abs(args[0])) },
                { "clamp", (3, args => Math.Min(Math.Max(args[0], args[1]), args[2])) }, // clamp(val, min, max)
                { "floor", (1, args => Math.Floor(args[0])) },
                { "ceil",  (1, args => Math.Ceiling(args[0])) },
                { "round", (1, args => Math.Round(args[0])) },
                { "sqrt",  (1, args => args[0] < 0 ? 0 : Math.Sqrt(args[0])) },
                { "pow",   (2, args => Math.Pow(args[0], args[1])) },
            };

        /// <summary>
        /// 解析数学表达式并返回 Func&lt;T&gt; 委托
        /// 支持常用函数: min(a,b), max(a,b), clamp(val,min,max), abs(x), floor(x), ceil(x), round(x), sqrt(x), pow(x,y)
        /// </summary>
        public static Func<T> ParseMath<T>(string expr, IVariableProvider provider)
            where T : struct, IConvertible
        {
            var compiled = ParseMath<T>(expr);
            return () => compiled(provider);
        }

        /// <summary>
        /// 解析数学表达式并返回 Func&lt;IVariableProvider, T&gt; 委托
        /// 支持常用函数: min(a,b), max(a,b), clamp(val,min,max), abs(x), floor(x), ceil(x), round(x), sqrt(x), pow(x,y)
        /// </summary>
        public static Func<IVariableProvider, T> ParseMath<T>(string expr) where T : struct, IConvertible
        {
            if (string.IsNullOrWhiteSpace(expr))
                return (p) => default;

            var rawTokens = new List<string>();
            foreach (Match match in TokenRegex.Matches(expr))
            {
                string token = match.Value.Trim();
                if (!string.IsNullOrEmpty(token)) rawTokens.Add(token);
            }

            List<string> rpn = ConvertToRPN(rawTokens);

            // 编译为接收 context 的委托
            var compiledFunc = CompileRPNWithProvider(rpn);

            return (p) => (T)Convert.ChangeType(compiledFunc(p), typeof(T), CultureInfo.InvariantCulture);
        }

        private static Func<IVariableProvider, double> CompileRPNWithProvider(List<string> rpn)
        {
            return (provider) =>
            {
                var stack = new Stack<double>(rpn.Count);
                for (int i = 0; i < rpn.Count; i++)
                {
                    string token = rpn[i];
                    if (IsOperator(token))
                    {
                        double b = stack.Pop();
                        double a = stack.Pop();
                        switch (token)
                        {
                            case "+": stack.Push(a + b); break;
                            case "-": stack.Push(a - b); break;
                            case "*": stack.Push(a * b); break;
                            case "/": stack.Push(Math.Abs(b) < 1e-9 ? 0 : a / b); break;
                        }
                    }
                    else if (IsFunction(token))
                    {
                        var (argCount, func) = SupportedFunctions[token];
                        double[] args = new double[argCount];
                        for (int j = argCount - 1; j >= 0; j--) args[j] = stack.Pop();
                        stack.Push(func(args));
                    }
                    else
                    {
                        if (token.StartsWith("$"))
                        {
                            string varName = token.Substring(1);
                            object val = provider.Get(varName);
                            if (val == null)
                                throw new NullReferenceException($"未找到黑板变量 '${varName}'");
                            stack.Push(Convert.ToDouble(val, CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            stack.Push(double.Parse(token, CultureInfo.InvariantCulture));
                        }
                    }
                }
                return stack.Count > 0 ? stack.Pop() : 0.0;
            };
        }

        private static List<string> ConvertToRPN(List<string> tokens)
        {
            var output = new List<string>();
            var ops = new Stack<string>();

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];

                // 处理一元负号 (如 -5, -$Def, min(-5, 0))
                if (token == "-" && (i == 0 || tokens[i - 1] == "(" || tokens[i - 1] == "," || IsOperator(tokens[i - 1])))
                {
                    output.Add("0"); // 将 -x 转为 0 - x
                }

                if (IsFunction(token))
                {
                    // 遇到函数名，直接压栈
                    ops.Push(token);
                }
                else if (token == ",")
                {
                    // 遇到函数参数逗号，弹出栈顶直到遇到左括号 '('
                    while (ops.Count > 0 && ops.Peek() != "(")
                    {
                        output.Add(ops.Pop());
                    }

                    if (ops.Count == 0)
                        throw new FormatException("数学表达式逗号放置错误或缺少括号。");
                }
                else if (token == "(")
                {
                    ops.Push(token);
                }
                else if (token == ")")
                {
                    // 弹出直到左括号
                    while (ops.Count > 0 && ops.Peek() != "(")
                    {
                        output.Add(ops.Pop());
                    }

                    if (ops.Count > 0)
                        ops.Pop(); // 弹出并丢弃 '('

                    // 如果括号前是函数名，则把该函数名弹出输出到 RPN 中
                    if (ops.Count > 0 && IsFunction(ops.Peek()))
                    {
                        output.Add(ops.Pop());
                    }
                }
                else if (IsOperator(token))
                {
                    while (ops.Count > 0 && Precedence(ops.Peek()) >= Precedence(token))
                    {
                        output.Add(ops.Pop());
                    }
                    ops.Push(token);
                }
                else
                {
                    // 操作数 (常量或 $变量)
                    output.Add(token);
                }
            }

            while (ops.Count > 0)
            {
                string op = ops.Pop();
                if (op == "(" || op == ")")
                    throw new FormatException("数学表达式存在未闭合的括号。");
                output.Add(op);
            }

            return output;
        }

        private static bool IsOperator(string t) => t == "+" || t == "-" || t == "*" || t == "/";

        private static bool IsFunction(string t) => SupportedFunctions.ContainsKey(t);

        private static int Precedence(string op)
        {
            if (op == "*" || op == "/") return 2;
            if (op == "+" || op == "-") return 1;
            return 0;
        }
    }

    // =======================================
    // 写入
    // =======================================

    public static class AssignmentParser
    {
        // 捕获：左变量名、赋值操作符(=, +=, -=, *=, /=)、右表达式
        private static readonly Regex AssignRegex =
            new Regex(@"^\$([a-zA-Z0-9_\.]+)\s*(\+=|-=|\*=|/=|=(?!=))\s*(.+)$", RegexOptions.Compiled);

        /// <summary>
        /// 无状态编译：将形如 "$Player.hp += 1" 或 "$Flag = true" 解析为 Action<IVariableProvider> 委托。
        /// 适合预编译并缓存，运行时传入具体 provider 执行，零解析开销。
        /// </summary>
        public static Action<IVariableProvider> ParseAssignment(string rawExpr, int lineNumber = -1)
        {
            if (string.IsNullOrWhiteSpace(rawExpr))
                return _ => { };

            rawExpr = rawExpr.Trim();
            var match = AssignRegex.Match(rawExpr);
            if (!match.Success)
            {
                string lineInfo = lineNumber > 0 ? $"[第 {lineNumber} 行] " : "";
                throw new FormatException($"{lineInfo}无法解析的赋值表达式: \"{rawExpr}\"，格式应为: '$变量名 操作符(=, +=, -=, *=, /=) 值'");
            }

            string varName = match.Groups[1].Value.Trim();
            string op = match.Groups[2].Value.Trim();
            string rightRaw = match.Groups[3].Value.Trim();

            // 编译右操作数获取器 (接收 IVariableProvider 返回 object)
            Func<IVariableProvider, object> rightGetter = CreateValueGetter(rightRaw);

            // 返回无状态委托
            return (provider) =>
            {
                if (provider == null)
                {
                    string lineInfo = lineNumber > 0 ? $"[第 {lineNumber} 行] " : "";
                    throw new ArgumentNullException(nameof(provider), $"{lineInfo}执行赋值失败：未传入有效的 IVariableProvider 上下文！");
                }

                object rightVal = rightGetter(provider);

                // 1. 直接覆盖赋值: '='
                if (op == "=")
                {
                    provider.Set(varName, rightVal);
                    return;
                }

                // 2. 复合赋值运算: '+=', '-=', '*=', '/='
                object currentVal = provider.Get(varName);

                object newVal = EvaluateAssign(currentVal, op, rightVal);
                provider.Set(varName, newVal);
            };
        }

        /// <summary>
        /// 获取右侧的值获取委托（常量只解析一次，变量动态读取）
        /// </summary>
        private static Func<IVariableProvider, object> CreateValueGetter(string raw)
        {
            // 1. 动态变量: 执行时从 provider 取值
            if (raw.StartsWith("$"))
            {
                string rightVarName = raw.Substring(1).Trim();
                return (p) => p != null ? p.Get(rightVarName) : null;
            }

            // 2. 引号包裹的明确字符串 (如 "100", "true") -> 去掉引号返回 string
            if (raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2)
            {
                string cleanLiteral = raw.Substring(1, raw.Length - 2);
                return _ => cleanLiteral;
            }

            // 3. 布尔字面量 (true / false)
            if (bool.TryParse(raw, out bool boolVal))
            {
                return _ => boolVal;
            }

            // 4. 整型字面量 (如 100)
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long intVal))
            {
                // 如果在 int 范围内优先返回 int，更符合常规使用习惯
                if (intVal >= int.MinValue && intVal <= int.MaxValue)
                    return _ => (int)intVal;
                return _ => intVal;
            }

            // 5. 浮点数字面量 (如 3.14)
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleVal))
            {
                return _ => doubleVal;
            }

            // 6. 其他兜底返回原始文本
            return _ => raw;
        }

        /// <summary>
        /// 执行复合算术/拼接运算
        /// </summary>
        private static object EvaluateAssign(object current, string op, object right)
        {
            if (current == null)
            {
                // 1. 如果是 += 且右侧带引号（显式字符串），默认按空字符串 "" 拼接
                if (op == "+=" && right is string)
                {
                    current = string.Empty;
                }
                else
                {
                    // 2. 其他情况一律默认将初始状态视作数值 0
                    current = 0;
                }
            }

            // 字符串拼接支持：$Name += "_Hero"
            if (current is string strVal && op == "+=")
            {
                return strVal + right?.ToString();
            }

            // 数值运算支持
            if (double.TryParse(current.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double curNum) &&
                double.TryParse(right.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double rightNum))
            {
                switch (op)
                {
                    case "+=": return curNum + rightNum;
                    case "-=": return curNum - rightNum;
                    case "*=": return curNum * rightNum;
                    case "/=":
                        if (Math.Abs(rightNum) < double.Epsilon)
                            throw new DivideByZeroException("除法运算时右操作数不能为 0！");
                        return curNum / rightNum;
                }
            }

            throw new InvalidOperationException($"变量类型 '{current.GetType().Name}' 不支持复合操作符 '{op}'");
        }
    }
    #endregion

}