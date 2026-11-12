using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MiaoWu.Infrastructure.Logging;

namespace MiaoWu.Infrastructure.Auditing
{
    /// <summary>
    /// 审计日志的查看/清空/导出。审计文件已经是脱敏后的 JSONL，
    /// 导出时再跑一次通用脱敏，避免历史版本写入的敏感值外泄。
    /// </summary>
    public sealed class ToolAuditLogService
    {
        private readonly string _logsDirectory;
        private readonly string _exportsDirectory;
        private readonly object _gate = new object();

        public ToolAuditLogService(string logsDirectory, string exportsDirectory)
        {
            _logsDirectory = logsDirectory;
            _exportsDirectory = exportsDirectory;
        }

        public IReadOnlyList<LogFileInfo> List()
        {
            var list = new List<LogFileInfo>();
            try
            {
                if (!Directory.Exists(_logsDirectory))
                {
                    return list;
                }

                foreach (string file in Directory.GetFiles(_logsDirectory, "audit-*.jsonl"))
                {
                    var info = new FileInfo(file);
                    list.Add(new LogFileInfo
                    {
                        Path = file,
                        Name = Path.GetFileName(file),
                        Length = info.Length,
                        LastWriteTime = info.LastWriteTime
                    });
                }

                list.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            }
            catch (Exception)
            {
            }

            return list;
        }

        public string ReadTail(int maxChars)
        {
            if (maxChars <= 0)
            {
                maxChars = 200000;
            }

            IReadOnlyList<LogFileInfo> files = List();
            if (files.Count == 0)
            {
                return string.Empty;
            }

            try
            {
                using (var stream = new FileStream(
                    files[0].Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                {
                    long start = Math.Max(0, stream.Length - maxChars);
                    stream.Seek(start, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        string text = reader.ReadToEnd();
                        return start > 0 ? "…\n" + text : text;
                    }
                }
            }
            catch (Exception exception)
            {
                return "读取审计日志失败：" + exception.Message;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                foreach (LogFileInfo file in List())
                {
                    try
                    {
                        File.Delete(file.Path);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        public string Export()
        {
            Directory.CreateDirectory(_exportsDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string target = Path.Combine(_exportsDirectory, "miaowu-audit-" + stamp + ".jsonl");

            var builder = new StringBuilder();
            IReadOnlyList<LogFileInfo> files = List();
            for (int i = files.Count - 1; i >= 0; i--)
            {
                try
                {
                    builder.AppendLine("# " + files[i].Name);
                    builder.AppendLine(LogSanitizer.Sanitize(File.ReadAllText(files[i].Path)));
                }
                catch (Exception)
                {
                }
            }

            File.WriteAllText(target, builder.ToString(), new UTF8Encoding(false));
            return target;
        }
    }
}
