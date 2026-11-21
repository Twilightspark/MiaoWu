using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Text;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// read：按行范围读取文本文件。参数 offset（1 基）与 limit（默认 2000，上限 2000）。
    /// 拒绝二进制、超过 2 MB 的文件与工作区外路径。
    /// </summary>
    public sealed class ReadTool : IAgentTool
    {
        private const int MaxPathLength = 400;
        private const int DefaultLineLimit = 2000;
        private const int MaxLineLimit = 2000;

        public string Name
        {
            get { return ToolNames.Read; }
        }

        public string Description
        {
            get
            {
                return "读取工作区内的文本文件。参数："
                    + ToolArgumentDocs.Path
                    + ToolArgumentDocs.Offset
                    + ToolArgumentDocs.LimitLines
                    + " 输出带行号的文本；二进制与超过 2 MB 的文件会被拒绝。";
            }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.Low; }
        }

        public bool RequiresApproval
        {
            get { return false; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            return null;
        }

        public async Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(
                    () => ExecuteCore(call, context, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Cancelled,
                    "cancelled",
                    "操作已取消。");
            }
        }

        private static ToolResult ExecuteCore(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            ToolArguments arguments;
            try
            {
                arguments = ToolArguments.Parse(call.ArgumentsJson);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResultFactory.InvalidArguments(call, exception);
            }

            string requestedPath;
            int offset;
            int limit;
            try
            {
                requestedPath = arguments.GetRequiredString(ToolArgumentNames.Path, MaxPathLength);
                offset = arguments.GetOptionalInt(ToolArgumentNames.Offset, 1, 1, int.MaxValue);
                limit = arguments.GetOptionalInt(
                    ToolArgumentNames.Limit,
                    DefaultLineLimit,
                    1,
                    MaxLineLimit);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResultFactory.InvalidArguments(call, exception);
            }

            PathSandboxResult resolved = context.Sandbox.Resolve(requestedPath, PathAccessKind.File);
            if (!resolved.IsAllowed)
            {
                return ToolResultFactory.PathFailure(call, resolved);
            }

            cancellationToken.ThrowIfCancellationRequested();

            long length;
            try
            {
                length = new FileInfo(resolved.NormalizedPath).Length;
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "read_failed",
                    "无法读取文件属性。");
            }

            if (length > ToolText.MaxFileBytes)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "file_too_large",
                    "文件超过 2 MB，read 不读取大文件。请改用 grep 或指定更小的目标。");
            }

            byte[] bytes;
            try
            {
                using (var stream = new FileStream(
                    resolved.NormalizedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    bytes = new byte[(int)length];
                    int readOffset = 0;
                    while (readOffset < bytes.Length)
                    {
                        int read = stream.Read(bytes, readOffset, bytes.Length - readOffset);
                        if (read <= 0)
                        {
                            break;
                        }

                        readOffset += read;
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "read_failed",
                    "读取文件失败，可能被其他程序占用。");
            }

            if (ToolText.LooksBinary(bytes, bytes.Length))
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "binary_file",
                    "文件包含二进制内容，read 只读取文本。");
            }

            TextEncodingDetection detection = TextEncodingDetector.Detect(bytes);
            string text = detection.Encoding.GetString(
                bytes,
                detection.BomLength,
                Math.Max(0, bytes.Length - detection.BomLength));
            List<string> lines = ToolText.SplitLines(text);

            string display = ToolText.ToDisplayPath(resolved.RelativePath);
            if (lines.Count == 0)
            {
                return ToolResult.Success(
                    call,
                    "文件：" + display + "（空文件，" + detection.DisplayName + "）\n",
                    display + " · 空文件",
                    true,
                    false);
            }

            if (offset > lines.Count)
            {
                return ToolResult.Success(
                    call,
                    "文件：" + display + " 只有 " + lines.Count.ToString(CultureInfo.InvariantCulture)
                        + " 行，offset=" + offset.ToString(CultureInfo.InvariantCulture) + " 超出范围。\n",
                    display + " · 行号超出范围",
                    true,
                    false);
            }

            int endLine = offset + limit - 1;
            if (endLine > lines.Count)
            {
                endLine = lines.Count;
            }

            var builder = new StringBuilder();
            builder.Append("文件：").Append(display)
                .Append("（共 ").Append(lines.Count.ToString(CultureInfo.InvariantCulture)).Append(" 行，显示 ")
                .Append(offset.ToString(CultureInfo.InvariantCulture)).Append('-')
                .Append(endLine.ToString(CultureInfo.InvariantCulture)).Append(" 行，")
                .Append(detection.DisplayName).Append("）\n");

            for (int i = offset - 1; i < endLine; i++)
            {
                builder.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(5))
                    .Append(" | ")
                    .Append(lines[i])
                    .Append('\n');
            }

            bool truncated = endLine < lines.Count;
            if (truncated)
            {
                builder.Append("…（还有 ")
                    .Append((lines.Count - endLine).ToString(CultureInfo.InvariantCulture))
                    .Append(" 行未显示，可用 offset 继续）\n");
            }

            return ToolResult.Success(
                call,
                builder.ToString(),
                display + " · " + offset.ToString(CultureInfo.InvariantCulture)
                    + "-" + endLine.ToString(CultureInfo.InvariantCulture)
                    + "/" + lines.Count.ToString(CultureInfo.InvariantCulture) + " 行",
                true,
                truncated);
        }
    }

    /// <summary>
    /// grep：在工作区内按正则搜索文本文件。跳过构建目录与二进制文件，
    /// 有文件数、单文件大小与总耗时上限；非法正则按参数错误返回。
    /// </summary>
    public sealed class GrepTool : IAgentTool
    {
        private const int MaxPathLength = 400;
        private const int MaxPatternLength = 400;
        private const int MaxFiles = 5000;
        private const int MaxFileBytes = 1024 * 1024;
        private const int MaxResultsLimit = 200;
        private const int ScanBudgetMilliseconds = 5000;
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

        private static readonly HashSet<string> ExcludedDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".svn", ".hg", "node_modules", "bin", "obj", "packages",
                ".vs", ".artifacts"
            };

        private static readonly HashSet<string> ExcludedAgentChildren =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "sessions", "attachments", "cache"
            };

        public string Name
        {
            get { return ToolNames.Grep; }
        }

        public string Description
        {
            get
            {
                return "在工作区内按正则搜索文本。参数："
                    + ToolArgumentDocs.Pattern
                    + ToolArgumentDocs.Path
                    + ToolArgumentDocs.Include
                    + "caseSensitive（可选，默认 false）。返回 文件:行号: 行内容，最多 200 条。";
            }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.Low; }
        }

        public bool RequiresApproval
        {
            get { return false; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            return null;
        }

        public async Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(
                    () => ExecuteCore(call, context, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Cancelled,
                    "cancelled",
                    "操作已取消。");
            }
        }

        private ToolResult ExecuteCore(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            ToolArguments arguments;
            try
            {
                arguments = ToolArguments.Parse(call.ArgumentsJson);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResultFactory.InvalidArguments(call, exception);
            }

            string pattern;
            string requestedPath;
            string include;
            int limit;
            bool caseSensitive;
            try
            {
                pattern = arguments.GetRequiredString(ToolArgumentNames.Pattern, MaxPatternLength);
                requestedPath = arguments.GetOptionalString(ToolArgumentNames.Path, ".", MaxPathLength);
                include = arguments.GetOptionalString(ToolArgumentNames.Include, "*", 200);
                limit = arguments.GetOptionalInt(
                    ToolArgumentNames.Limit,
                    50,
                    1,
                    MaxResultsLimit);
                caseSensitive = arguments.GetOptionalBool(ToolArgumentNames.CaseSensitive, false);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResultFactory.InvalidArguments(call, exception);
            }

            Regex regex;
            try
            {
                RegexOptions options = RegexOptions.CultureInvariant;
                if (!caseSensitive)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                regex = new Regex(pattern, options, MatchTimeout);
            }
            catch (ArgumentException)
            {
                return ToolResultFactory.InvalidArguments(
                    call,
                    "invalid_pattern",
                    "pattern 不是合法的正则表达式：" + ToolText.ShortenLine(pattern, 120));
            }

            PathSandboxResult resolved = context.Sandbox.Resolve(
                requestedPath,
                PathAccessKind.Directory);
            if (!resolved.IsAllowed)
            {
                return ToolResultFactory.PathFailure(call, resolved);
            }

            var results = new List<string>(limit);
            var stopwatch = Stopwatch.StartNew();
            int scannedFiles = 0;
            int skippedBinary = 0;
            int unreadable = 0;
            bool truncated = false;
            bool budgetExceeded = false;
            bool matchTimedOut = false;

            var pending = new Stack<string>();
            pending.Push(resolved.NormalizedPath);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stopwatch.ElapsedMilliseconds > ScanBudgetMilliseconds)
                {
                    budgetExceeded = true;
                    break;
                }

                string directory = pending.Pop();
                string[] children;
                try
                {
                    children = Directory.GetFileSystemEntries(directory);
                }
                catch (Exception exception) when (
                    exception is IOException || exception is UnauthorizedAccessException)
                {
                    continue;
                }

                for (int i = 0; i < children.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (stopwatch.ElapsedMilliseconds > ScanBudgetMilliseconds)
                    {
                        budgetExceeded = true;
                        break;
                    }

                    if (results.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    string child = children[i];
                    string name = Path.GetFileName(child);

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(child);
                    }
                    catch (Exception exception) when (
                        exception is IOException || exception is UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        if (ShouldSkipDirectory(name, directory))
                        {
                            continue;
                        }

                        pending.Push(child);
                        continue;
                    }

                    if (!MatchesIncludePattern(name, include))
                    {
                        continue;
                    }

                    if (scannedFiles >= MaxFiles)
                    {
                        truncated = true;
                        break;
                    }

                    scannedFiles++;

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(child);
                        if (info.Length == 0)
                        {
                            continue;
                        }

                        if (info.Length > MaxFileBytes)
                        {
                            continue;
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException || exception is UnauthorizedAccessException)
                    {
                        unreadable++;
                        continue;
                    }

                    byte[] bytes;
                    try
                    {
                        bytes = File.ReadAllBytes(child);
                    }
                    catch (Exception exception) when (
                        exception is IOException || exception is UnauthorizedAccessException)
                    {
                        unreadable++;
                        continue;
                    }

                    if (ToolText.LooksBinary(bytes, bytes.Length))
                    {
                        skippedBinary++;
                        continue;
                    }

                    TextEncodingDetection detection = TextEncodingDetector.Detect(bytes);
                    string text = detection.Encoding.GetString(
                        bytes,
                        detection.BomLength,
                        Math.Max(0, bytes.Length - detection.BomLength));
                    List<string> lines = ToolText.SplitLines(text);
                    string relative = context.Sandbox.ToRelativePath(child);
                    string display = ToolText.ToDisplayPath(relative);

                    for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                    {
                        if (results.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }

                        bool matched;
                        try
                        {
                            matched = regex.IsMatch(lines[lineIndex]);
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            matchTimedOut = true;
                            break;
                        }

                        if (matched)
                        {
                            results.Add(display
                                + ":" + (lineIndex + 1).ToString(CultureInfo.InvariantCulture)
                                + ": " + ToolText.ShortenLine(lines[lineIndex], 200));
                        }
                    }

                    if (truncated || matchTimedOut)
                    {
                        break;
                    }
                }

                if (truncated || matchTimedOut)
                {
                    break;
                }

                if (budgetExceeded)
                {
                    break;
                }
            }

            stopwatch.Stop();

            var builder = new StringBuilder();
            builder.Append("搜索：/").Append(pattern).Append("/ · ")
                .Append(ToolText.ToDisplayPath(resolved.RelativePath))
                .Append(" · ").Append(results.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" 处（扫描 ").Append(scannedFiles.ToString(CultureInfo.InvariantCulture))
                .Append(" 个文件）\n");

            if (results.Count == 0)
            {
                builder.Append("没有找到匹配内容。\n");
            }
            else
            {
                for (int i = 0; i < results.Count; i++)
                {
                    builder.Append(results[i]).Append('\n');
                }
            }

            if (truncated)
            {
                builder.Append("… 结果已截断（达到 limit 或扫描上限）。\n");
            }

            if (budgetExceeded)
            {
                builder.Append("… 搜索超过 5 秒预算，已返回部分结果。\n");
            }

            if (matchTimedOut)
            {
                builder.Append("… 正则匹配超过 2 秒，已停止；请简化 pattern。\n");
            }

            if (skippedBinary > 0)
            {
                builder.Append("（跳过 ").Append(skippedBinary.ToString(CultureInfo.InvariantCulture))
                    .Append(" 个二进制文件）\n");
            }

            if (unreadable > 0)
            {
                builder.Append("（").Append(unreadable.ToString(CultureInfo.InvariantCulture))
                    .Append(" 个文件无法读取）\n");
            }

            bool wasTruncated = truncated || budgetExceeded || matchTimedOut;
            return ToolResult.Success(
                call,
                builder.ToString(),
                "/" + pattern + "/ · " + results.Count.ToString(CultureInfo.InvariantCulture) + " 处",
                true,
                wasTruncated);
        }

        private static bool ShouldSkipDirectory(string name, string parentDirectory)
        {
            if (ExcludedDirectories.Contains(name))
            {
                return true;
            }

            if (string.Equals(name, ".agent", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string parentName = Path.GetFileName(parentDirectory.TrimEnd('\\', '/'));
            if (string.Equals(parentName, ".agent", StringComparison.OrdinalIgnoreCase)
                && ExcludedAgentChildren.Contains(name))
            {
                return true;
            }

            return false;
        }

        private static bool MatchesIncludePattern(string fileName, string include)
        {
            if (string.IsNullOrWhiteSpace(include) || include == "*")
            {
                return true;
            }

            string[] patterns = include.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < patterns.Length; i++)
            {
                string pattern = patterns[i].Trim();
                if (pattern.Length == 0)
                {
                    continue;
                }

                if (GetIncludeRegex(pattern).IsMatch(fileName))
                {
                    return true;
                }
            }

            return false;
        }

        private static Regex GetIncludeRegex(string pattern)
        {
            string escaped = Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".");
            return new Regex(
                "^" + escaped + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    /// <summary>
    /// glob：按文件名模式查找工作区文件，支持 <c>**</c>、<c>*</c>、<c>?</c>。
    /// 跳过 .git、bin、obj、node_modules、.agent；返回排序后的相对路径，limit 默认 200。
    /// </summary>
    public sealed class GlobTool : IAgentTool
    {
        private const int MaxPathLength = 400;
        private const int MaxPatternLength = 400;
        private const int DefaultLimit = 200;
        private const int MaxLimit = 2000;
        private const int MaxMatches = 20000;
        private const int ScanBudgetMilliseconds = 5000;
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

        private static readonly HashSet<string> ExcludedDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", "bin", "obj", "node_modules", ".agent"
            };

        public string Name
        {
            get { return ToolNames.Glob; }
        }

        public string Description
        {
            get
            {
                return "按文件名模式查找工作区文件。参数："
                    + ToolArgumentDocs.Pattern
                    + ToolArgumentDocs.Path
                    + ToolArgumentDocs.Limit
                    + " 支持 **（任意层目录）、*（段内任意字符）、?（单个字符）；"
                    + "返回按路径排序的相对路径。";
            }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.Low; }
        }

        public bool RequiresApproval
        {
            get { return false; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            return null;
        }

        public async Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(
                    () => ExecuteCore(call, context, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Cancelled,
                    "cancelled",
                    "操作已取消。");
            }
        }

        private static ToolResult ExecuteCore(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            ToolArguments arguments;
            try
            {
                arguments = ToolArguments.Parse(call.ArgumentsJson);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResultFactory.InvalidArguments(call, exception);
            }

            string pattern;
            string requestedPath;
            int limit;
            try
            {
                pattern = arguments.GetRequiredString(ToolArgumentNames.Pattern, MaxPatternLength);
                requestedPath = arguments.GetOptionalString(ToolArgumentNames.Path, ".", MaxPathLength);
                limit = arguments.GetOptionalInt(
                    ToolArgumentNames.Limit,
                    DefaultLimit,
                    1,
                    MaxLimit);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResultFactory.InvalidArguments(call, exception);
            }

            PathSandboxResult resolved = context.Sandbox.Resolve(
                requestedPath,
                PathAccessKind.Directory);
            if (!resolved.IsAllowed)
            {
                return ToolResultFactory.PathFailure(call, resolved);
            }

            GlobPattern compiled;
            try
            {
                compiled = GlobPattern.Compile(pattern);
            }
            catch (ArgumentException)
            {
                return ToolResultFactory.InvalidArguments(
                    call,
                    "invalid_pattern",
                    "pattern 不合法：" + ToolText.ShortenLine(pattern, 120));
            }

            var matches = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            bool budgetExceeded = false;
            bool scanLimitReached = false;
            int scannedFiles = 0;

            var pending = new Stack<string>();
            pending.Push(resolved.NormalizedPath);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stopwatch.ElapsedMilliseconds > ScanBudgetMilliseconds)
                {
                    budgetExceeded = true;
                    break;
                }

                string directory = pending.Pop();
                string[] children;
                try
                {
                    children = Directory.GetFileSystemEntries(directory);
                }
                catch (Exception exception) when (
                    exception is IOException || exception is UnauthorizedAccessException)
                {
                    continue;
                }

                for (int i = 0; i < children.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string child = children[i];
                    string name = Path.GetFileName(child);

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(child);
                    }
                    catch (Exception exception) when (
                        exception is IOException || exception is UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        if (!ExcludedDirectories.Contains(name))
                        {
                            pending.Push(child);
                        }

                        continue;
                    }

                    scannedFiles++;
                    if (scannedFiles > MaxMatches)
                    {
                        scanLimitReached = true;
                        break;
                    }

                    string relative = context.Sandbox.ToRelativePath(child);
                    if (relative == null)
                    {
                        continue;
                    }

                    if (compiled.IsMatch(relative, name))
                    {
                        matches.Add(ToolText.ToDisplayPath(relative));
                        if (matches.Count >= MaxMatches)
                        {
                            scanLimitReached = true;
                            break;
                        }
                    }
                }

                if (scanLimitReached)
                {
                    break;
                }
            }

            stopwatch.Stop();

            matches.Sort(StringComparer.OrdinalIgnoreCase);
            bool truncated = matches.Count > limit || scanLimitReached || budgetExceeded;
            if (matches.Count > limit)
            {
                matches.RemoveRange(limit, matches.Count - limit);
            }

            var builder = new StringBuilder();
            builder.Append("匹配：").Append(pattern)
                .Append(" · ").Append(ToolText.ToDisplayPath(resolved.RelativePath))
                .Append(" · ").Append(matches.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" 个文件").Append(truncated ? "（已截断）" : string.Empty)
                .Append('\n');

            if (matches.Count == 0)
            {
                builder.Append("没有匹配的文件。\n");
            }
            else
            {
                for (int i = 0; i < matches.Count; i++)
                {
                    builder.Append(matches[i]).Append('\n');
                }
            }

            if (budgetExceeded)
            {
                builder.Append("… 遍历超过 5 秒预算，已返回部分结果。\n");
            }

            return ToolResult.Success(
                call,
                builder.ToString(),
                pattern + " · " + matches.Count.ToString(CultureInfo.InvariantCulture) + " 个文件",
                true,
                truncated);
        }

        /// <summary>
        /// 编译后的 glob。不含路径分隔符的模式只匹配文件名；含分隔符的模式逐段匹配相对路径，
        /// <c>**</c> 匹配零到多段。
        /// </summary>
        private sealed class GlobPattern
        {
            private readonly Regex[] _segments;
            private readonly bool _fileNameOnly;

            private GlobPattern(Regex[] segments, bool fileNameOnly)
            {
                _segments = segments;
                _fileNameOnly = fileNameOnly;
            }

            public static GlobPattern Compile(string pattern)
            {
                string normalized = (pattern ?? string.Empty).Trim().Replace('\\', '/');
                if (normalized.Length == 0)
                {
                    throw new ArgumentException("pattern 不能为空。");
                }

                bool fileNameOnly = normalized.IndexOf('/') < 0;
                string[] parts = normalized.Split('/');
                var segments = new Regex[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    if (part == "**")
                    {
                        segments[i] = null;
                        continue;
                    }

                    string expression = Regex.Escape(part)
                        .Replace("\\*", "[^/]*")
                        .Replace("\\?", "[^/]");
                    segments[i] = new Regex(
                        "^" + expression + "$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        MatchTimeout);
                }

                return new GlobPattern(segments, fileNameOnly);
            }

            public bool IsMatch(string relativePath, string fileName)
            {
                if (_fileNameOnly)
                {
                    return _segments.Length == 1 && _segments[0] != null && _segments[0].IsMatch(fileName);
                }

                string[] pathParts = relativePath
                    .Replace('\\', '/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                return Match(0, pathParts, 0);
            }

            private bool Match(int patternIndex, string[] pathParts, int pathIndex)
            {
                if (patternIndex == _segments.Length)
                {
                    return pathIndex == pathParts.Length;
                }

                Regex segment = _segments[patternIndex];
                if (segment == null)
                {
                    if (Match(patternIndex + 1, pathParts, pathIndex))
                    {
                        return true;
                    }

                    return pathIndex < pathParts.Length && Match(patternIndex, pathParts, pathIndex + 1);
                }

                return pathIndex < pathParts.Length
                    && segment.IsMatch(pathParts[pathIndex])
                    && Match(patternIndex + 1, pathParts, pathIndex + 1);
            }
        }
    }
}
