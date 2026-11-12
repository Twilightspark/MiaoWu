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
    /// list_dir：列出工作区目录。只读、自动放行；结果标记为不可信内容。
    /// </summary>
    public sealed class ListDirectoryTool : IAgentTool
    {
        private const int MaxPathLength = 400;
        private const int MaxEntries = 1000;

        public string Name
        {
            get { return ToolNames.ListDirectory; }
        }

        public string Description
        {
            get
            {
                return "列出工作区内的目录。参数："
                    + ToolArgumentDocs.Path
                    + ToolArgumentDocs.Depth
                    + ToolArgumentDocs.Limit
                    + " 返回每行的类型（d 目录 / f 文件 / l 重解析点）、相对路径与大小。";
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
            int depth;
            int limit;
            try
            {
                requestedPath = arguments.GetOptionalString(
                    ToolArgumentNames.Path,
                    ".",
                    MaxPathLength);
                depth = arguments.GetOptionalInt(ToolArgumentNames.Depth, 1, 1, 3);
                limit = arguments.GetOptionalInt(ToolArgumentNames.Limit, 200, 1, MaxEntries);
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

            var entries = new List<DirectoryEntry>();
            bool truncated = false;
            Collect(
                resolved.NormalizedPath,
                resolved.RelativePath,
                depth,
                limit,
                entries,
                ref truncated,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var builder = new StringBuilder();
            string display = ToolText.ToDisplayPath(resolved.RelativePath);
            builder.Append("目录：").Append(display)
                .Append("（递归 ").Append(depth.ToString(CultureInfo.InvariantCulture)).Append(" 层，")
                .Append(entries.Count.ToString(CultureInfo.InvariantCulture)).Append(" 项")
                .Append(truncated ? "，已截断" : string.Empty)
                .Append("）\n");

            if (entries.Count == 0)
            {
                builder.Append("（空目录）\n");
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    DirectoryEntry entry = entries[i];
                    switch (entry.Kind)
                    {
                        case 'd':
                            builder.Append("d  ").Append(ToolText.ToDisplayPath(entry.RelativePath)).Append("/\n");
                            break;
                        case 'l':
                            builder.Append("l  ").Append(ToolText.ToDisplayPath(entry.RelativePath))
                                .Append("  -> 重解析点（不跟随）\n");
                            break;
                        default:
                            builder.Append("f  ").Append(ToolText.ToDisplayPath(entry.RelativePath))
                                .Append("  ").Append(FileSizeFormatter.Format(entry.Size))
                                .Append('\n');
                            break;
                    }
                }
            }

            if (truncated)
            {
                builder.Append("… 结果超过 limit，已截断。\n");
            }

            return ToolResult.Success(
                call,
                builder.ToString(),
                display + " · " + entries.Count.ToString(CultureInfo.InvariantCulture) + " 项",
                true,
                truncated);
        }

        private static void Collect(
            string fullDirectory,
            string relativeDirectory,
            int depth,
            int limit,
            List<DirectoryEntry> entries,
            ref bool truncated,
            CancellationToken cancellationToken)
        {
            if (entries.Count >= limit)
            {
                truncated = true;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            string[] children;
            try
            {
                children = Directory.GetFileSystemEntries(fullDirectory);
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is DirectoryNotFoundException)
            {
                return;
            }

            var batch = new List<DirectoryEntry>(children.Length);
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

                var entry = new DirectoryEntry
                {
                    FullPath = child,
                    RelativePath = CombineRelative(relativeDirectory, name)
                };

                if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    entry.Kind = 'l';
                }
                else if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    entry.Kind = 'd';
                }
                else
                {
                    entry.Kind = 'f';
                    try
                    {
                        entry.Size = new FileInfo(child).Length;
                    }
                    catch (Exception exception) when (
                        exception is IOException || exception is UnauthorizedAccessException)
                    {
                        entry.Size = 0;
                    }
                }

                batch.Add(entry);
            }

            batch.Sort(CompareEntries);

            for (int i = 0; i < batch.Count; i++)
            {
                if (entries.Count >= limit)
                {
                    truncated = true;
                    return;
                }

                entries.Add(batch[i]);

                if (batch[i].Kind == 'd' && depth > 1)
                {
                    Collect(
                        batch[i].FullPath,
                        batch[i].RelativePath,
                        depth - 1,
                        limit,
                        entries,
                        ref truncated,
                        cancellationToken);
                }
            }
        }

        private static int CompareEntries(DirectoryEntry left, DirectoryEntry right)
        {
            bool leftDirectory = left.Kind == 'd';
            bool rightDirectory = right.Kind == 'd';
            if (leftDirectory != rightDirectory)
            {
                return leftDirectory ? -1 : 1;
            }

            return string.Compare(
                left.RelativePath,
                right.RelativePath,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string CombineRelative(string parent, string name)
        {
            if (string.IsNullOrEmpty(parent))
            {
                return name;
            }

            return parent + "\\" + name;
        }

        private sealed class DirectoryEntry
        {
            public string FullPath;
            public string RelativePath;
            public char Kind;
            public long Size;
        }
    }

    /// <summary>
    /// read_file：按行范围读取文本文件。拒绝二进制、超过 2 MB 的文件与工作区外路径。
    /// </summary>
    public sealed class ReadFileTool : IAgentTool
    {
        private const int MaxPathLength = 400;
        private const int DefaultMaxLines = 200;
        private const int HardMaxLines = 2000;

        public string Name
        {
            get { return ToolNames.ReadFile; }
        }

        public string Description
        {
            get
            {
                return "读取工作区内的文本文件。参数："
                    + ToolArgumentDocs.Path
                    + ToolArgumentDocs.StartLine
                    + ToolArgumentDocs.MaxLines
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
            int startLine;
            int maxLines;
            try
            {
                requestedPath = arguments.GetRequiredString(ToolArgumentNames.Path, MaxPathLength);
                startLine = arguments.GetOptionalInt(ToolArgumentNames.StartLine, 1, 1, int.MaxValue);
                maxLines = arguments.GetOptionalInt(
                    ToolArgumentNames.MaxLines,
                    DefaultMaxLines,
                    1,
                    HardMaxLines);
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
                    "文件超过 2 MB，read_file 不读取大文件。请改用 search_files 或指定更小的目标。");
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
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0)
                        {
                            break;
                        }

                        offset += read;
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
                    "文件包含二进制内容，read_file 只读取文本。");
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

            if (startLine > lines.Count)
            {
                return ToolResult.Success(
                    call,
                    "文件：" + display + " 只有 " + lines.Count.ToString(CultureInfo.InvariantCulture)
                        + " 行，startLine=" + startLine.ToString(CultureInfo.InvariantCulture) + " 超出范围。\n",
                    display + " · 行号超出范围",
                    true,
                    false);
            }

            int endLine = startLine + maxLines - 1;
            if (endLine > lines.Count)
            {
                endLine = lines.Count;
            }

            var builder = new StringBuilder();
            builder.Append("文件：").Append(display)
                .Append("（共 ").Append(lines.Count.ToString(CultureInfo.InvariantCulture)).Append(" 行，显示 ")
                .Append(startLine.ToString(CultureInfo.InvariantCulture)).Append('-')
                .Append(endLine.ToString(CultureInfo.InvariantCulture)).Append(" 行，")
                .Append(detection.DisplayName).Append("）\n");

            for (int i = startLine - 1; i < endLine; i++)
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
                    .Append(" 行未显示，可用 startLine 继续）\n");
            }

            return ToolResult.Success(
                call,
                builder.ToString(),
                display + " · " + startLine.ToString(CultureInfo.InvariantCulture)
                    + "-" + endLine.ToString(CultureInfo.InvariantCulture)
                    + "/" + lines.Count.ToString(CultureInfo.InvariantCulture) + " 行",
                true,
                truncated);
        }
    }

    /// <summary>
    /// search_files：在工作区内按子串搜索文本文件。跳过构建目录与二进制文件，
    /// 有文件数、单文件大小与总耗时上限。
    /// </summary>
    public sealed class SearchFilesTool : IAgentTool
    {
        private const int MaxPathLength = 400;
        private const int MaxQueryLength = 200;
        private const int MaxFiles = 5000;
        private const int MaxFileBytes = 1024 * 1024;
        private const int MaxResultsLimit = 200;
        private const int ScanBudgetMilliseconds = 5000;

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
            get { return ToolNames.SearchFiles; }
        }

        public string Description
        {
            get
            {
                return "在工作区内搜索文本。参数："
                    + ToolArgumentDocs.Query
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

            string query;
            string requestedPath;
            string include;
            int maxResults;
            bool caseSensitive;
            try
            {
                query = arguments.GetRequiredString(ToolArgumentNames.Query, MaxQueryLength);
                requestedPath = arguments.GetOptionalString(ToolArgumentNames.Path, ".", MaxPathLength);
                include = arguments.GetOptionalString(ToolArgumentNames.Include, "*", 200);
                maxResults = arguments.GetOptionalInt(
                    ToolArgumentNames.MaxResults,
                    50,
                    1,
                    MaxResultsLimit);
                caseSensitive = arguments.GetOptionalBool(ToolArgumentNames.CaseSensitive, false);
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

            StringComparison comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            var results = new List<string>(maxResults);
            var stopwatch = Stopwatch.StartNew();
            int scannedFiles = 0;
            int skippedBinary = 0;
            int unreadable = 0;
            bool truncated = false;
            bool budgetExceeded = false;

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

                    if (results.Count >= maxResults)
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
                        if (results.Count >= maxResults)
                        {
                            truncated = true;
                            break;
                        }

                        if (lines[lineIndex].IndexOf(query, comparison) >= 0)
                        {
                            results.Add(display
                                + ":" + (lineIndex + 1).ToString(CultureInfo.InvariantCulture)
                                + ": " + ToolText.ShortenLine(lines[lineIndex], 200));
                        }
                    }

                    if (truncated)
                    {
                        break;
                    }
                }

                if (truncated)
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
            builder.Append("搜索：\"").Append(query).Append("\" · ")
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
                builder.Append("… 结果已截断（达到 maxResults 或扫描上限）。\n");
            }

            if (budgetExceeded)
            {
                builder.Append("… 搜索超过 5 秒预算，已返回部分结果。\n");
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

            bool wasTruncated = truncated || budgetExceeded;
            return ToolResult.Success(
                call,
                builder.ToString(),
                "\"" + query + "\" · " + results.Count.ToString(CultureInfo.InvariantCulture) + " 处",
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

                if (GetPattern(pattern).IsMatch(fileName))
                {
                    return true;
                }
            }

            return false;
        }

        private static Regex GetPattern(string pattern)
        {
            string escaped = Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".");
            return new Regex(
                "^" + escaped + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
