using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MiaoWu.Core.Abstractions;

namespace MiaoWu.Infrastructure.Logging
{
    /// <summary>
    /// 按天滚动的文本日志：logs\app-yyyyMMdd.log。
    /// 只写事件名、会话/消息 ID、耗时与错误分类；正文与密钥一律不写。
    /// 保留 7 天、总量上限 10 MB。
    /// </summary>
    public sealed class RollingFileLogger : IAgentLogger, IDisposable
    {
        private const int MaxTotalBytes = 10 * 1024 * 1024;
        private const int MaxAgeDays = 7;

        private readonly string _directory;
        private readonly object _gate = new object();
        private DateTime _lastRollDate = DateTime.MinValue;
        private bool _disposed;

        public RollingFileLogger(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("日志目录不能为空。", nameof(directory));
            }

            _directory = directory;
            Directory.CreateDirectory(_directory);
        }

        public string DirectoryPath
        {
            get { return _directory; }
        }

        public void Info(string eventName, string message)
        {
            Write("INFO", eventName, message, null);
        }

        public void Warn(string eventName, string message)
        {
            Write("WARN", eventName, message, null);
        }

        public void Error(string eventName, string message, Exception exception)
        {
            Write("ERROR", eventName, message, exception);
        }

        private void Write(string level, string eventName, string message, Exception exception)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    DateTime now = DateTime.Now;
                    RollIfNeeded(now);

                    var builder = new StringBuilder();
                    builder.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    builder.Append(' ').Append(level);
                    builder.Append(' ').Append(LogSanitizer.Sanitize(eventName ?? "event"));
                    builder.Append(' ').Append(LogSanitizer.Shorten(message, 2000));

                    if (exception != null)
                    {
                        builder.Append(" | ")
                            .Append(exception.GetType().Name)
                            .Append(": ")
                            .Append(LogSanitizer.Shorten(exception.Message, 600));
                    }

                    builder.AppendLine();

                    string path = Path.Combine(
                        _directory,
                        "app-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

                    using (var stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite))
                    {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(builder.ToString());
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                }
            }
            catch (Exception)
            {
                // 日志失败不能影响主流程。
            }
        }

        private void RollIfNeeded(DateTime now)
        {
            if (_lastRollDate.Date == now.Date)
            {
                return;
            }

            _lastRollDate = now.Date;
            try
            {
                string[] files = Directory.GetFiles(_directory, "app-*.log");
                var entries = new List<FileInfo>();
                foreach (string file in files)
                {
                    var info = new FileInfo(file);
                    entries.Add(info);
                }

                entries.Sort((a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
                long total = 0;
                foreach (FileInfo entry in entries)
                {
                    total += entry.Length;
                }

                foreach (FileInfo entry in entries)
                {
                    bool tooOld = (now - entry.CreationTime).TotalDays > MaxAgeDays;
                    bool tooLarge = total > MaxTotalBytes;
                    if (tooOld || tooLarge)
                    {
                        total -= entry.Length;
                        try
                        {
                            entry.Delete();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    public sealed class LogFileInfo
    {
        public string Path { get; set; }

        public string Name { get; set; }

        public long Length { get; set; }

        public DateTime LastWriteTime { get; set; }
    }

    /// <summary>日志查看/清空与诊断包导出。诊断包内容全部经过脱敏。</summary>
    public sealed class LogFileService
    {
        private readonly string _logDirectory;
        private readonly string _exportsDirectory;
        private readonly Func<IReadOnlyDictionary<string, string>> _diagnosticsProvider;
        private readonly object _gate = new object();

        public LogFileService(
            string logDirectory,
            string exportsDirectory,
            Func<IReadOnlyDictionary<string, string>> diagnosticsProvider)
        {
            _logDirectory = logDirectory;
            _exportsDirectory = exportsDirectory;
            _diagnosticsProvider = diagnosticsProvider;
        }

        public IReadOnlyList<LogFileInfo> List()
        {
            var list = new List<LogFileInfo>();
            try
            {
                if (!Directory.Exists(_logDirectory))
                {
                    return list;
                }

                string[] files = Directory.GetFiles(_logDirectory, "app-*.log");
                foreach (string file in files)
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
                    long length = stream.Length;
                    long start = Math.Max(0, length - maxChars);
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
                return "读取日志失败：" + exception.Message;
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

        /// <summary>把日志、配置摘要与系统信息打成一个 zip，返回路径。</summary>
        public string ExportDiagnostics()
        {
            Directory.CreateDirectory(_exportsDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string zipPath = Path.Combine(_exportsDirectory, "miaowu-diagnostics-" + stamp + ".zip");
            string tempDirectory = Path.Combine(Path.GetTempPath(), "miaowu-diag-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(tempDirectory);
                Directory.CreateDirectory(Path.Combine(tempDirectory, "logs"));

                foreach (LogFileInfo file in List())
                {
                    try
                    {
                        string content = File.ReadAllText(file.Path);
                        File.WriteAllText(
                            Path.Combine(tempDirectory, "logs", file.Name),
                            LogSanitizer.Sanitize(content),
                            new UTF8Encoding(false));
                    }
                    catch (Exception)
                    {
                    }
                }

                var summary = new StringBuilder();
                summary.AppendLine("MiaoWu 诊断包");
                summary.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                summary.AppendLine("OS：" + Environment.OSVersion);
                summary.AppendLine("CLR：" + Environment.Version);
                summary.AppendLine("64 位进程：" + Environment.Is64BitProcess);
                summary.AppendLine("数据目录：" + _logDirectory);
                summary.AppendLine();

                IReadOnlyDictionary<string, string> diagnostics = _diagnosticsProvider == null
                    ? null
                    : _diagnosticsProvider();

                if (diagnostics != null)
                {
                    foreach (KeyValuePair<string, string> pair in diagnostics)
                    {
                        summary.AppendLine(pair.Key + "：" + LogSanitizer.Sanitize(pair.Value));
                    }
                }

                File.WriteAllText(
                    Path.Combine(tempDirectory, "summary.txt"),
                    summary.ToString(),
                    new UTF8Encoding(false));

                using (var archive = System.IO.Compression.ZipFile.Open(
                    zipPath,
                    System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (string file in Directory.GetFiles(tempDirectory, "*", SearchOption.AllDirectories))
                    {
                        string relative = file.Substring(tempDirectory.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string entryName = relative.Replace('\\', '/');
                        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(
                            archive,
                            file,
                            entryName,
                            System.IO.Compression.CompressionLevel.Optimal);
                    }
                }

                return zipPath;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDirectory))
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
