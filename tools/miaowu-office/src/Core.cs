using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace MiaowuOffice
{
    /// <summary>工具身份与全局常量。</summary>
    internal static class ToolInfo
    {
        public const string Name = "miaowu_office";
        public const string Version = "1.0.0";
    }

    /// <summary>默认超时（毫秒）。Win7 + 3.75GB 内存下不能无限等 WPS。</summary>
    internal static class Timeouts
    {
        public const int Inspect = 90000;
        public const int Apply = 150000;
        public const int Render = 120000;
        public const int Excel = 60000;
    }

    /// <summary>可选调试日志：设置环境变量 MIAOWU_OFFICE_TRACE=1 后写 stderr，不影响 stdout 的 JSON。</summary>
    internal static class Trace
    {
        private static readonly bool _on = string.Equals(
            Environment.GetEnvironmentVariable("MIAOWU_OFFICE_TRACE"), "1", StringComparison.Ordinal);

        public static bool On { get { return _on; } }

        public static void Log(string message)
        {
            if (!_on) return;
            try { Console.Error.WriteLine("[trace] " + message); }
            catch { }
        }
    }

    /// <summary>读真实 Windows 版本。Environment.OSVersion 在 Win8.1+ 上会因为缺少清单谎报 6.2。</summary>
    internal static class Win32
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public int Size;
            public int Major;
            public int Minor;
            public int Build;
            public int PlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;
            public ushort ServicePackMajor;
            public ushort ServicePackMinor;
            public ushort SuiteMask;
            public byte ProductType;
            public byte Reserved;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OsVersionInfo info);

        public static Jso Describe()
        {
            var version = new Jso();
            try
            {
                var info = new OsVersionInfo();
                info.Size = Marshal.SizeOf(typeof(OsVersionInfo));
                if (RtlGetVersion(ref info) == 0)
                {
                    version.Add("major", info.Major);
                    version.Add("minor", info.Minor);
                    version.Add("build", info.Build);
                    version.Add("version", info.Major.ToString() + "." + info.Minor.ToString() + "." + info.Build.ToString());
                    if (!string.IsNullOrEmpty(info.ServicePack)) version.Add("servicePack", info.ServicePack);
                    string name;
                    if (info.Major == 6 && info.Minor == 1) name = "Windows 7";
                    else if (info.Major == 6 && info.Minor == 2) name = "Windows 8";
                    else if (info.Major == 6 && info.Minor == 3) name = "Windows 8.1";
                    else if (info.Major == 10 && info.Build >= 22000) name = "Windows 11";
                    else if (info.Major == 10) name = "Windows 10";
                    else name = "Windows " + info.Major.ToString() + "." + info.Minor.ToString();
                    version.Add("name", name);
                    version.Add("isWindows7", info.Major == 6 && info.Minor == 1);
                    return version;
                }
            }
            catch { }

            var fallback = Environment.OSVersion.Version;
            version.Add("major", fallback.Major);
            version.Add("minor", fallback.Minor);
            version.Add("build", fallback.Build);
            version.Add("version", fallback.ToString());
            version.Add("name", Environment.OSVersion.VersionString);
            version.Add("isWindows7", fallback.Major == 6 && fallback.Minor == 1);
            version.Add("fallback", true);
            return version;
        }
    }

    /// <summary>带错误码的工具异常；错误码会原样出现在 JSON 的 error.code 里。</summary>
    internal sealed class ToolException : Exception
    {
        public string Code { get; private set; }
        public object Detail { get; private set; }

        public ToolException(string code, string message) : this(code, message, null) { }

        public ToolException(string code, string message, object detail) : base(message)
        {
            Code = code;
            Detail = detail;
        }
    }

    /// <summary>有序 JSON 对象。字段顺序即插入顺序，便于人读与模型读。</summary>
    internal sealed class Jso : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly List<KeyValuePair<string, object>> _items = new List<KeyValuePair<string, object>>();

        /// <summary>写入字段；同名键就地覆盖，保证输出里不出现重复键。</summary>
        public Jso Add(string key, object value)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].Key, key, StringComparison.Ordinal))
                {
                    _items[i] = new KeyValuePair<string, object>(key, value);
                    return this;
                }
            }
            _items.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }

        public int Count { get { return _items.Count; } }

        public object this[string key]
        {
            get
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (string.Equals(_items[i].Key, key, StringComparison.Ordinal)) return _items[i].Value;
                }
                return null;
            }
        }

        public bool Has(string key)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].Key, key, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() { return _items.GetEnumerator(); }

        IEnumerator IEnumerable.GetEnumerator() { return _items.GetEnumerator(); }
    }

    /// <summary>JSON 输出。手写而不复用 JavaScriptSerializer，是为了稳定字段顺序与可读缩进。</summary>
    internal static class JsonText
    {
        public static string Write(object value, bool pretty)
        {
            var sb = new StringBuilder(4096);
            WriteValue(sb, value, pretty, 0);
            return sb.ToString();
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }

        private static void WriteValue(StringBuilder sb, object value, bool pretty, int depth)
        {
            if (value == null) { sb.Append("null"); return; }

            var jso = value as Jso;
            if (jso != null) { WriteObject(sb, jso, pretty, depth); return; }

            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                var converted = new Jso();
                foreach (var kv in dict) converted.Add(kv.Key, kv.Value);
                WriteObject(sb, converted, pretty, depth);
                return;
            }

            string s = value as string;
            if (s != null) { WriteString(sb, s); return; }

            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }

            if (value is int) { sb.Append(((int)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is long) { sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is short) { sb.Append(((short)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is byte) { sb.Append(((byte)value).ToString(CultureInfo.InvariantCulture)); return; }

            if (value is double)
            {
                double d = (double)value;
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is float)
            {
                float f = (float)value;
                if (float.IsNaN(f) || float.IsInfinity(f)) { sb.Append("null"); return; }
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal)
            {
                sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is DateTime)
            {
                WriteString(sb, ((DateTime)value).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
                return;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                var list = new List<object>();
                foreach (var item in enumerable) list.Add(item);
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (pretty) Indent(sb, depth + 1);
                    WriteValue(sb, list[i], pretty, depth + 1);
                }
                if (pretty) Indent(sb, depth);
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteObject(StringBuilder sb, Jso obj, bool pretty, int depth)
        {
            if (obj.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            bool first = true;
            foreach (var kv in obj)
            {
                if (!first) sb.Append(',');
                first = false;
                if (pretty) Indent(sb, depth + 1);
                WriteString(sb, kv.Key);
                sb.Append(':');
                if (pretty) sb.Append(' ');
                WriteValue(sb, kv.Value, pretty, depth + 1);
            }
            if (pretty) Indent(sb, depth);
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x7F || c == '\u2028' || c == '\u2029')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>命令行参数：`--name value`、`--name=value`、裸开关 `--flag`。</summary>
    internal sealed class Args
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Command { get; private set; }

        public static Args Parse(string[] argv)
        {
            var args = new Args();
            int i = 0;
            if (argv.Length > 0 && !argv[0].StartsWith("-", StringComparison.Ordinal))
            {
                args.Command = argv[0];
                i = 1;
            }
            for (; i < argv.Length; i++)
            {
                string token = argv[i];
                if (token == null) continue;
                if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    string body = token.Substring(2);
                    if (body.Length == 0) continue;
                    int eq = body.IndexOf('=');
                    if (eq > 0)
                    {
                        args._values[body.Substring(0, eq)] = body.Substring(eq + 1);
                    }
                    else if (i + 1 < argv.Length && argv[i + 1] != null && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        args._values[body] = argv[++i];
                    }
                    else
                    {
                        args._flags.Add(body);
                    }
                }
                else if (token == "-h")
                {
                    args._flags.Add("help");
                }
            }
            return args;
        }

        public bool Has(string name) { return _values.ContainsKey(name) || _flags.Contains(name); }

        public bool Flag(string name)
        {
            if (_flags.Contains(name)) return true;
            string v;
            if (_values.TryGetValue(name, out v))
            {
                return v == null || v.Length == 0 || v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        public string Get(string name, string fallback)
        {
            string v;
            if (_values.TryGetValue(name, out v) && v != null) return v;
            return fallback;
        }

        public int GetInt(string name, int fallback)
        {
            string v = Get(name, null);
            int parsed;
            if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }
    }

    /// <summary>路径守卫：拒绝 `..`、UNC、ADS、设备名，并把文件钉在允许的根内。</summary>
    internal static class Guard
    {
        private static readonly string[] DeviceNames = new string[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static string FullPath(string path, string what)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ToolException("usage", "缺少 " + what + " 参数");
            string p = path.Trim().Trim('"');
            if (p.Length == 0) throw new ToolException("usage", what + " 为空");
            if (p.IndexOfAny(new char[] { '\0' }) >= 0) throw new ToolException("bad_path", what + " 含非法字符");
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) throw new ToolException("bad_path", what + " 不能是 UNC 路径：" + p);

            string full;
            try { full = Path.GetFullPath(p); }
            catch (Exception ex) { throw new ToolException("bad_path", "无法解析 " + what + "：" + ex.Message); }

            if (full.Length >= 260) throw new ToolException("bad_path", what + " 超过 MAX_PATH(260)：" + full);
            string name = Path.GetFileName(full);
            if (name.Length == 0) throw new ToolException("bad_path", what + " 不是文件路径：" + full);
            if (name.IndexOf(':') >= 0) throw new ToolException("bad_path", what + " 含备用数据流（ADS）：" + name);
            string stem = Path.GetFileNameWithoutExtension(name).TrimEnd('.');
            string upper = stem.ToUpperInvariant();
            for (int i = 0; i < DeviceNames.Length; i++)
            {
                if (upper == DeviceNames[i]) throw new ToolException("bad_path", what + " 使用了保留设备名：" + name);
            }
            return full;
        }

        /// <summary>把 full 限制在 root 之内。root 为空表示不检查（调用方会标注未校验）。</summary>
        public static void EnsureInside(string root, string full, string what)
        {
            if (string.IsNullOrEmpty(root)) return;
            string rootFull = TrimEndSep(Path.GetFullPath(root));
            string target = TrimEndSep(full);
            if (string.Equals(target, rootFull, StringComparison.OrdinalIgnoreCase)) return;
            if (target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            throw new ToolException("outside_workspace",
                what + " 不在允许的工作区内：" + full,
                new Jso().Add("workspace", rootFull).Add("path", full));
        }

        private static string TrimEndSep(string p)
        {
            if (p.Length > 3 && (p[p.Length - 1] == Path.DirectorySeparatorChar || p[p.Length - 1] == '/'))
            {
                return p.Substring(0, p.Length - 1);
            }
            return p;
        }
    }

    /// <summary>文件工具：备份名、临时名、原子替换。</summary>
    internal static class Files
    {
        public static string BackupPath(string file)
        {
            string dir = Path.GetDirectoryName(file);
            string stem = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string candidate = Path.Combine(dir, stem + ".miaowu-backup-" + stamp + ext);
            int n = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(dir, stem + ".miaowu-backup-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture) + ext);
                n++;
            }
            return candidate;
        }

        /// <summary>
        /// restore 用：被覆盖的那一版留成 `.miaowu-replaced-*`，
        /// 和 `.miaowu-backup-*` 分开，避免 --latest 在「备份 / 还原」之间来回跳。
        /// </summary>
        public static string ReplacedPath(string file)
        {
            string dir = Path.GetDirectoryName(file);
            string stem = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string candidate = Path.Combine(dir, stem + ".miaowu-replaced-" + stamp + ext);
            int n = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(dir, stem + ".miaowu-replaced-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture) + ext);
                n++;
            }
            return candidate;
        }

        public static string TempPath(string file)        {
            string dir = Path.GetDirectoryName(file);
            string stem = Path.GetFileNameWithoutExtension(file);
            string ext = Path.GetExtension(file);
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            return Path.Combine(dir, stem + ".miaowu-tmp-" + token + ext);
        }

        /// <summary>用临时文件替换目标，同时把原文件留成备份。失败时抛出可读错误码。</summary>
        public static void ReplaceWithBackup(string temp, string target, string backup)
        {
            if (!File.Exists(temp)) throw new ToolException("internal", "临时文件已丢失：" + temp);
            try
            {
                File.Replace(temp, target, backup, true);
            }
            catch (Exception ex)
            {
                throw new ToolException("replace_failed",
                    "写回原文件失败（可能被占用或只读）：" + ex.Message,
                    new Jso().Add("target", target).Add("hint", "请关闭正在打开该文件的程序后重试").Add("backupCreated", File.Exists(backup)));
            }
        }

        public static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    /// <summary>文本归一化与短哈希。</summary>
    internal static class Txt
    {
        /// <summary>把 Word 段落文本折成一行：去掉段落标记 / 单元格标记 / 域标记，对象字符换成 U+FFFC。</summary>
        public static string Flat(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\r' || c == '\n') continue;
                if (c == '\a') continue;
                if (c == '\v') { sb.Append('\n'); continue; }
                if (c == '\u0013' || c == '\u0014' || c == '\u0015') continue;
                if (c == '\u0001') { sb.Append('\uFFFC'); continue; }
                sb.Append(c);
            }
            return sb.ToString().TrimEnd('\n');
        }

        public static bool HasField(string raw)
        {
            return raw != null && raw.IndexOf('\u0013') >= 0;
        }

        /// <summary>段落文本的短哈希（SHA1 前 8 位十六进制），用于 `expect` 乐观并发校验。</summary>
        public static string Hash(string text)
        {
            using (var sha = SHA1.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string Preview(string text, int max)
        {
            if (text == null) return string.Empty;
            if (text.Length <= max) return text;
            return text.Substring(0, max) + "…";
        }
    }

    /// <summary>OOXML 完整性校验：能被解成 zip、含 [Content_Types].xml、关键部件存在。</summary>
    internal static class Ooxml
    {
        public static Jso Validate(string path)
        {
            var result = new Jso();
            try
            {
                using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
                {
                    int entries = 0;
                    bool contentTypes = false;
                    bool mainPart = false;
                    int sampled = 0;
                    foreach (var entry in zip.Entries)
                    {
                        entries++;
                        if (string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) contentTypes = true;
                        if (entry.FullName == "word/document.xml" || entry.FullName == "xl/workbook.xml" || entry.FullName == "ppt/presentation.xml") mainPart = true;
                        if (sampled < 300 && entry.Length > 0)
                        {
                            // 真正读一遍，触发 CRC 校验，能抓到写入中断导致的坏包。
                            using (var s = entry.Open())
                            {
                                var buffer = new byte[8192];
                                while (s.Read(buffer, 0, buffer.Length) > 0) { }
                            }
                            sampled++;
                        }
                    }
                    result.Add("entries", entries);
                    result.Add("contentTypes", contentTypes);
                    result.Add("mainPart", mainPart);
                    result.Add("ok", entries > 0 && contentTypes && mainPart);
                    if (entries == 0 || !contentTypes) result.Add("reason", "OOXML 包缺少 [Content_Types].xml 或为空");
                    else if (!mainPart) result.Add("reason", "OOXML 包缺少正文部件");
                }
            }
            catch (Exception ex)
            {
                result.Add("ok", false);
                result.Add("reason", "解包失败：" + ex.Message);
            }
            return result;
        }

        public static bool IsOoxml(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".docx" || ext == ".xlsx" || ext == ".pptx" || ext == ".docm" || ext == ".xlsm" || ext == ".pptm";
        }
    }

    /// <summary>读计划 / 参数 JSON。</summary>
    internal static class JsonRead
    {
        public static object Load(string path)
        {
            if (!File.Exists(path)) throw new ToolException("file_not_found", "计划文件不存在：" + path);
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex) { throw new ToolException("file_unreadable", "读取计划文件失败：" + ex.Message); }
            if (string.IsNullOrWhiteSpace(text)) throw new ToolException("bad_plan", "计划文件为空：" + path);
            try
            {
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 64 * 1024 * 1024;
                serializer.RecursionLimit = 200;
                return serializer.DeserializeObject(text);
            }
            catch (Exception ex)
            {
                throw new ToolException("bad_plan", "计划文件不是合法 JSON：" + ex.Message);
            }
        }

        public static string Str(Dictionary<string, object> dict, string key, string fallback)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return fallback;
        }

        public static bool Bool(Dictionary<string, object> dict, string key, bool fallback)
        {
            object v;
            if (dict == null || !dict.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static int Int(Dictionary<string, object> dict, string key, int fallback)
        {
            object v;
            if (dict == null || !dict.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static bool Has(Dictionary<string, object> dict, string key)
        {
            return dict != null && dict.ContainsKey(key) && dict[key] != null;
        }

        public static List<object> List(Dictionary<string, object> dict, string key)
        {
            object v;
            if (dict == null || !dict.TryGetValue(key, out v) || v == null) return new List<object>();
            var list = v as ArrayList;
            if (list != null) return new List<object>(list.ToArray());
            var arr = v as object[];
            if (arr != null) return new List<object>(arr);
            var direct = v as List<object>;
            if (direct != null) return direct;
            return new List<object>();
        }

        public static Dictionary<string, object> Dict(Dictionary<string, object> dict, string key)
        {
            object v;
            if (dict != null && dict.TryGetValue(key, out v) && v != null)
            {
                return v as Dictionary<string, object>;
            }
            return null;
        }
    }
}
