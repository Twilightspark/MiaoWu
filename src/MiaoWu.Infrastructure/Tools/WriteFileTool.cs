using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Text;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// write：整文件写入。审批前记录文件哈希与 mtime，执行时再次校验，
    /// 外部修改过就拒绝覆盖；覆盖前备份，临时文件原子替换，写后哈希校验。
    /// </summary>
    public sealed class WriteTool : IAgentTool, IToolAuditRedactor
    {
        private const int MaxPathLength = 400;
        private const int MaxContentChars = 2 * 1024 * 1024;
        private const int MaxFileBytes = 2 * 1024 * 1024;

        private readonly FileBackupService _backups;
        private readonly IClock _clock;

        public WriteTool(FileBackupService backups, IClock clock)
        {
            _backups = backups ?? throw new ArgumentNullException(nameof(backups));
            _clock = clock ?? SystemClock.Instance;
        }

        public string Name
        {
            get { return ToolNames.Write; }
        }

        public string Description
        {
            get
            {
                return "写入工作区内的文本文件（整文件覆盖）。参数：path（相对路径）、content（完整新内容，UTF-8 文本）。"
                    + "覆盖前会备份到 backups，文件在审批后被外部修改会拒绝写入；只支持 ≤2 MB 的文本文件；"
                    + "目标文件必须是 UTF-8，非 UTF-8 会拒绝写入，请先转换。";
            }
        }

        public ToolRisk Risk
        {
            get { return ToolRisk.High; }
        }

        public bool RequiresApproval
        {
            get { return true; }
        }

        public ToolApprovalPreview Describe(ToolCall call, ToolContext context)
        {
            WritePlan plan = Prepare(call, context);
            return plan.Preview;
        }

        public string GetAuditArguments(ToolCall call, ToolContext context)
        {
            try
            {
                ToolArguments arguments = ToolArguments.Parse(call.ArgumentsJson);
                string path = arguments.GetRequiredString(ToolArgumentNames.Path, MaxPathLength);
                string content = arguments.GetRequiredStringAllowEmpty("content", MaxContentChars);
                return "path=" + path
                    + "; content=" + content.Length.ToString(CultureInfo.InvariantCulture) + " 字符";
            }
            catch (ToolArgumentException)
            {
                return "（参数无法解析）";
            }
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
                    "写入已取消。");
            }
        }

        private ToolResult ExecuteCore(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            WritePlan plan;
            try
            {
                plan = Prepare(call, context);
            }
            catch (ToolArgumentException exception)
            {
                return ToolResultFactory.InvalidArguments(call, exception);
            }
            catch (ToolPreparationException exception)
            {
                return ToolResult.Failure(call, exception.Status, exception.Code, exception.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();

            ToolApprovalPreview approval = context.ApprovalPreview;
            if (approval == null || string.IsNullOrWhiteSpace(approval.StateToken))
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "approval_state_missing",
                    "写文件必须先经过审批，且审批状态已丢失；请重试。");
            }

            if (!StateMatches(approval.StateToken, plan))
            {
                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "file_changed",
                    "文件在审批后被外部修改，已拒绝覆盖。请重新读取后再试。");
            }

            // 审批已经通过，才允许产生副作用：先补建缺失的父目录，再走备份 / 原子写入。
            if (plan.CreatesParent)
            {
                try
                {
                    Directory.CreateDirectory(plan.ParentPath);
                }
                catch (Exception exception) when (
                    exception is IOException
                    || exception is UnauthorizedAccessException
                    || exception is NotSupportedException)
                {
                    return ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "mkdir_failed",
                        "创建目标目录失败：" + Shorten(exception.Message, 160));
                }
            }

            string backupPath = null;
            if (plan.Exists)
            {
                try
                {
                    backupPath = _backups.Backup(plan.NormalizedPath, _clock.UtcNow);
                }
                catch (Exception exception) when (
                    exception is IOException
                    || exception is UnauthorizedAccessException
                    || exception is NotSupportedException)
                {
                    return ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "backup_failed",
                        "创建备份失败，未写入文件。");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            bool written = false;
            try
            {
                ToolAtomicWrite.WriteAllBytes(plan.NormalizedPath, plan.NewBytes);
                written = true;

                string actualHash = ToolHashing.Sha256Hex(File.ReadAllBytes(plan.NormalizedPath));
                if (!string.Equals(actualHash, plan.NewHash, StringComparison.OrdinalIgnoreCase))
                {
                    Rollback(plan, backupPath);
                    return ToolResult.Failure(
                        call,
                        ToolResultStatus.Failed,
                        "write_verification_failed",
                        "写入后校验失败，已回滚到写入前状态。");
                }
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is NotSupportedException)
            {
                if (written)
                {
                    Rollback(plan, backupPath);
                }

                return ToolResult.Failure(
                    call,
                    ToolResultStatus.Failed,
                    "write_failed",
                    "写入文件失败：" + Shorten(exception.Message, 160));
            }

            var result = ToolResult.Success(
                call,
                BuildSuccessContent(plan, backupPath),
                plan.RelativeDisplay + " · " + FormatChange(plan),
                false,
                false);
            result.BackupPath = backupPath;
            return result;
        }

        private WritePlan Prepare(ToolCall call, ToolContext context)
        {
            ToolArguments arguments;
            string requestedPath;
            string content;
            try
            {
                arguments = ToolArguments.Parse(call.ArgumentsJson);
                requestedPath = arguments.GetRequiredString(ToolArgumentNames.Path, MaxPathLength);
                content = arguments.GetRequiredStringAllowEmpty("content", MaxContentChars);
            }
            catch (ToolArgumentException exception)
            {
                throw new ToolPreparationException(
                    exception.Code,
                    exception.Message,
                    ToolResultStatus.InvalidArguments);
            }

            PathSandboxResult resolved = context.Sandbox.Resolve(
                requestedPath,
                PathAccessKind.File,
                true);
            if (!resolved.IsAllowed)
            {
                throw new ToolPreparationException(
                    ToolResultFactory.ToErrorCode(resolved.Status),
                    resolved.Detail);
            }

            string parent = Path.GetDirectoryName(resolved.NormalizedPath);
            if (string.IsNullOrEmpty(parent))
            {
                throw new ToolPreparationException("invalid_path", "无法解析目标文件的父目录。");
            }

            // 父目录不存在不算错误：审批时先记下来，执行时（批准之后）再建。
            // 自测里 write 因为没建目录失败过一次，模型只好多跑一轮 pwsh mkdir。
            bool createsParent = !Directory.Exists(parent);

            if (Directory.Exists(resolved.NormalizedPath))
            {
                throw new ToolPreparationException("wrong_path_kind", "目标是目录，不是文件。");
            }

            bool exists = File.Exists(resolved.NormalizedPath);
            byte[] oldBytes = new byte[0];
            string oldText = string.Empty;
            string oldHash = string.Empty;
            long lastWriteTicks = 0;

            if (exists)
            {
                long length;
                try
                {
                    var info = new FileInfo(resolved.NormalizedPath);
                    length = info.Length;
                    lastWriteTicks = info.LastWriteTimeUtc.Ticks;
                }
                catch (Exception exception) when (
                    exception is IOException || exception is UnauthorizedAccessException)
                {
                    throw new ToolPreparationException("read_failed", "无法读取目标文件属性。");
                }

                if (length > MaxFileBytes)
                {
                    throw new ToolPreparationException(
                        "file_too_large",
                        "目标文件超过 2 MB，write 不做整文件覆盖。");
                }

                try
                {
                    oldBytes = ReadAllBytesShared(resolved.NormalizedPath);
                }
                catch (Exception exception) when (
                    exception is IOException || exception is UnauthorizedAccessException)
                {
                    throw new ToolPreparationException("read_failed", "无法读取目标文件，可能被占用。");
                }

                if (ToolText.LooksBinary(oldBytes, oldBytes.Length))
                {
                    throw new ToolPreparationException(
                        "binary_file",
                        "目标文件包含二进制内容，write 只写文本文件。");
                }

                if (oldBytes.Length > 0)
                {
                    Utf8TextInfo old = Utf8Text.Read(oldBytes);
                    if (!old.IsValid)
                    {
                        throw new ToolPreparationException(
                            "not_text",
                            "目标文件不是 UTF-8 文本（可能是 GBK / ANSI / UTF-16）。请先转换为 UTF-8，再写入。");
                    }

                    oldText = old.Text;
                }

                oldHash = ToolHashing.Sha256Hex(oldBytes);
            }

            // 与 DSH 的 write 一致：content 原样写入，不按旧文件重排行尾。
            string newText = content;
            if (newText.Length > MaxContentChars)
            {
                throw new ToolPreparationException("content_too_large", "新内容超过 2 MB 字符上限。");
            }

            // 与 DSH 对齐：写入只有 UTF-8，且不写 BOM；原文件的 UTF-8 BOM 在读取时已剥离。
            const string encodingName = "UTF-8";
            byte[] newBytes;
            try
            {
                newBytes = TextEncodings.Utf8Strict.GetBytes(newText);
            }
            catch (EncoderFallbackException)
            {
                throw new ToolPreparationException(
                    "encoding_unsupported",
                    "新内容包含无法编码为 UTF-8 的字符（孤立代理项），写入已取消。");
            }

            if (newBytes.Length > MaxFileBytes)
            {
                throw new ToolPreparationException(
                    "content_too_large",
                    "新内容编码后超过 2 MB，write 拒绝写入。");
            }

            string relative = context.Sandbox.ToRelativePath(resolved.NormalizedPath);
            string display = ToolText.ToDisplayPath(relative);
            string parentDisplay = string.Empty;
            if (createsParent)
            {
                string parentRelative = context.Sandbox.ToRelativePath(parent);
                parentDisplay = string.IsNullOrWhiteSpace(parentRelative)
                    ? "."
                    : ToolText.ToDisplayPath(parentRelative);
            }

            UnifiedDiffResult diff = UnifiedDiffBuilder.Build(
                oldText,
                newText,
                display,
                new UnifiedDiffOptions { MaxOutputChars = 120000 });

            string summary = (exists ? "覆盖 " : "新建 ")
                + display
                + " · " + FormatChange(diff.AddedCount, diff.RemovedCount)
                + " · " + encodingName
                + (exists ? " · 覆盖前自动备份" : string.Empty)
                + (createsParent ? " · 自动创建目录 " + parentDisplay : string.Empty);

            var preview = new ToolApprovalPreview
            {
                Title = exists ? "写入文件" : "新建文件",
                Target = display,
                Summary = summary,
                CanApproveForSession = true,
                CanShowDiff = !diff.IsEmpty,
                Diff = diff.Text,
                ScopeKey = resolved.NormalizedPath,
                StateToken = BuildStateToken(exists, oldHash, lastWriteTicks)
            };

            return new WritePlan
            {
                NormalizedPath = resolved.NormalizedPath,
                RelativeDisplay = display,
                ParentPath = parent,
                ParentDisplay = parentDisplay,
                CreatesParent = createsParent,
                Exists = exists,
                OldHash = oldHash,
                LastWriteTicks = lastWriteTicks,
                NewBytes = newBytes,
                NewHash = ToolHashing.Sha256Hex(newBytes),
                AddedCount = diff.AddedCount,
                RemovedCount = diff.RemovedCount,
                DiffTruncated = diff.Truncated,
                EncodingName = encodingName,
                Preview = preview
            };
        }

        private static string BuildSuccessContent(WritePlan plan, string backupPath)
        {
            var builder = new StringBuilder();
            builder.Append("已写入 ").Append(plan.RelativeDisplay)
                .Append("（").Append(FormatChange(plan))
                .Append("，").Append(plan.EncodingName).Append("）");

            if (!string.IsNullOrEmpty(backupPath))
            {
                builder.Append("\n备份：").Append(backupPath);
            }

            if (plan.CreatesParent)
            {
                builder.Append("\n目录：").Append(plan.ParentDisplay).Append("（已自动创建）");
            }

            return builder.ToString();
        }

        private static string FormatChange(WritePlan plan)
        {
            return FormatChange(plan.AddedCount, plan.RemovedCount);
        }

        private static string FormatChange(int added, int removed)
        {
            return "+" + added.ToString(CultureInfo.InvariantCulture)
                + "/-" + removed.ToString(CultureInfo.InvariantCulture) + " 行";
        }

        private void Rollback(WritePlan plan, string backupPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
                {
                    _backups.Restore(backupPath, plan.NormalizedPath);
                }
                else if (File.Exists(plan.NormalizedPath))
                {
                    File.Delete(plan.NormalizedPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                // 回滚失败只能把备份路径留在结果与审计里，由用户手动恢复。
            }
        }

        internal static byte[] ReadAllBytesShared(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                var bytes = new byte[(int)length];
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

                if (offset != bytes.Length)
                {
                    Array.Resize(ref bytes, offset);
                }

                return bytes;
            }
        }

        private static string BuildStateToken(bool exists, string hash, long ticks)
        {
            return "v1|" + (exists ? "1" : "0")
                + "|" + (hash ?? string.Empty)
                + "|" + ticks.ToString(CultureInfo.InvariantCulture);
        }

        private static bool StateMatches(string token, WritePlan plan)
        {
            string[] parts = (token ?? string.Empty).Split('|');
            if (parts.Length != 4 || parts[0] != "v1")
            {
                return false;
            }

            bool exists = parts[1] == "1";
            if (exists != plan.Exists)
            {
                return false;
            }

            if (!exists)
            {
                return true;
            }

            if (!string.Equals(parts[2], plan.OldHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            long ticks;
            if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
            {
                return false;
            }

            return ticks == plan.LastWriteTicks;
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";
        }

        private sealed class WritePlan
        {
            public string NormalizedPath;

            /// <summary>父目录绝对路径；<see cref="CreatesParent"/> 时在执行阶段创建。</summary>
            public string ParentPath;

            /// <summary>父目录的相对显示路径；不需要建目录时为空串。</summary>
            public string ParentDisplay;

            /// <summary>审批时父目录还不存在，需要执行时补建。</summary>
            public bool CreatesParent;

            public string RelativeDisplay;
            public bool Exists;
            public string OldHash;
            public long LastWriteTicks;
            public byte[] NewBytes;
            public string NewHash;
            public int AddedCount;
            public int RemovedCount;
            public bool DiffTruncated;
            public string EncodingName;
            public ToolApprovalPreview Preview;
        }
    }
}
