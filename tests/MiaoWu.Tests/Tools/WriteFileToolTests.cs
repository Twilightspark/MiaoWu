using System;
using System.IO;
using System.Text;
using System.Threading;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Tools;
using MiaoWu.Tests.Conversations;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class WriteToolTests : IDisposable
    {
        private readonly string _workspace;
        private readonly string _backups;
        private readonly FileBackupService _backupService;
        private readonly WriteTool _tool;
        private readonly ToolContext _context;
        private readonly MutableClock _clock;

        public WriteToolTests()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "miaowu-write-" + Guid.NewGuid().ToString("N"));
            _workspace = Path.Combine(root, "workspace");
            _backups = Path.Combine(root, "backups");
            Directory.CreateDirectory(_workspace);

            _clock = new MutableClock(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
            _backupService = new FileBackupService(_backups);
            _tool = new WriteTool(_backupService, _clock);

            _context = new ToolContext
            {
                SessionId = "s1",
                WorkspaceId = "w1",
                WorkspaceRoot = _workspace,
                Sandbox = new WorkspacePathSandbox(_workspace, FileSystemPathProbe.Instance),
                CallId = "call-1",
                Now = _clock.UtcNow
            };
        }

        public void Dispose()
        {
            try
            {
                string root = Path.GetDirectoryName(_workspace);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void CreatesNewFile()
        {
            ToolResult result = Write("hello.txt", "hello\nworld\n");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Equal("hello\nworld\n", File.ReadAllText(Path.Combine(_workspace, "hello.txt")));
            Assert.Null(result.BackupPath);
            Assert.False(result.IsUntrusted);
        }

        [Fact]
        public void ModificationCreatesBackupAndDiff()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "a\nb\n", new UTF8Encoding(false));

            ToolApprovalPreview preview;
            ToolResult result = Write("a.txt", "a\nB\n", null, out preview);

            Assert.True(result.IsSuccess, result.Content);
            Assert.Equal("a\nB\n", File.ReadAllText(path));
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal("a\nb\n", File.ReadAllText(result.BackupPath));
            Assert.True(preview.CanShowDiff);
            Assert.Contains("+1/-1 行", preview.Summary);
            Assert.Contains("+B", preview.Diff);
            Assert.Contains("-b", preview.Diff);
        }

        [Fact]
        public void ExternalModificationAfterApprovalIsRejected()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "original", new UTF8Encoding(false));

            ToolCall call = Call("a.txt", "replacement");
            ToolApprovalPreview preview = _tool.Describe(call, _context);
            _context.ApprovalPreview = preview;

            File.WriteAllText(path, "changed by someone else", new UTF8Encoding(false));

            ToolResult result = _tool.ExecuteAsync(call, _context, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("file_changed", result.ErrorCode);
            Assert.Equal("changed by someone else", File.ReadAllText(path));
            Assert.True(
                !Directory.Exists(_backups) || Directory.GetFiles(_backups).Length == 0);
        }

        [Fact]
        public void MtimeChangeAfterApprovalIsRejected()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "same", new UTF8Encoding(false));

            ToolCall call = Call("a.txt", "new");
            ToolApprovalPreview preview = _tool.Describe(call, _context);
            _context.ApprovalPreview = preview;

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

            ToolResult result = _tool.ExecuteAsync(call, _context, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.Equal("file_changed", result.ErrorCode);
            Assert.Equal("same", File.ReadAllText(path));
        }

        [Fact]
        public void MissingApprovalStateIsRejected()
        {
            ToolResult result = _tool.ExecuteAsync(
                Call("a.txt", "content"),
                _context,
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("approval_state_missing", result.ErrorCode);
            Assert.False(File.Exists(Path.Combine(_workspace, "a.txt")));
        }

        [Fact]
        public void EmptyContentTruncatesFile()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "content", new UTF8Encoding(false));

            ToolResult result = Write("a.txt", string.Empty);

            Assert.True(result.IsSuccess, result.Content);
            Assert.Equal(0, new FileInfo(path).Length);
        }

        [Fact]
        public void MissingContentArgumentIsInvalid()
        {
            var args = JsonValue.NewObject();
            args.Add("path", JsonValue.From("a.txt"));

            ToolResult result = _tool.ExecuteAsync(
                new ToolCall { Id = "c1", Name = "write", ArgumentsJson = JsonWriter.Write(args) },
                _context,
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            Assert.Equal("missing_argument", result.ErrorCode);
        }

        [Fact]
        public void CrLfStyleIsPreserved()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "a\r\nb\r\n", new UTF8Encoding(false));

            ToolResult result = Write("a.txt", "a\nc\n");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Equal("a\r\nc\r\n", File.ReadAllText(path));
        }

        [Fact]
        public void GbkEncodingIsPreserved()
        {
            Encoding gbk;
            try
            {
                gbk = Encoding.GetEncoding(936);
            }
            catch (ArgumentException)
            {
                return;
            }

            string path = Path.Combine(_workspace, "gbk.txt");
            File.WriteAllBytes(path, gbk.GetBytes("中文\r\n"));

            ToolResult result = Write("gbk.txt", "英文\r\n");

            Assert.True(result.IsSuccess, result.Content);
            byte[] written = File.ReadAllBytes(path);
            Assert.Equal("英文\r\n", gbk.GetString(written));
        }

        [Fact]
        public void UnsupportedEncodingIsRejected()
        {
            Encoding gbk;
            try
            {
                gbk = Encoding.GetEncoding(936);
            }
            catch (ArgumentException)
            {
                return;
            }

            File.WriteAllBytes(Path.Combine(_workspace, "gbk.txt"), gbk.GetBytes("中文\r\n"));

            ToolResult result = Write("gbk.txt", "表情 😀\r\n");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("encoding_unsupported", result.ErrorCode);
            Assert.Equal("中文\r\n", gbk.GetString(File.ReadAllBytes(Path.Combine(_workspace, "gbk.txt"))));
        }

        [Fact]
        public void BinaryTargetIsRejected()
        {
            File.WriteAllBytes(Path.Combine(_workspace, "image.dat"), new byte[] { 0, 1, 2, 3 });

            ToolResult result = Write("image.dat", "text");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("binary_file", result.ErrorCode);
        }

        [Fact]
        public void MissingParentDirectoryIsRejected()
        {
            ToolResult result = Write(@"missing\a.txt", "content");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("parent_not_found", result.ErrorCode);
        }

        [Fact]
        public void PathOutsideWorkspaceIsRejected()
        {
            ToolResult result = Write(@"..\..\outside.txt", "content");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("path_outside_workspace", result.ErrorCode);
        }

        [Fact]
        public void OversizedContentIsRejected()
        {
            ToolResult result = Write("big.txt", new string('a', 2 * 1024 * 1024 + 10));

            Assert.True(
                result.Status == ToolResultStatus.InvalidArguments
                || result.ErrorCode == "content_too_large",
                result.Status + " / " + result.ErrorCode);
        }

        [Fact]
        public void BackupCanBeRestored()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "before", new UTF8Encoding(false));

            ToolResult result = Write("a.txt", "after");
            Assert.True(result.IsSuccess, result.Content);
            Assert.NotNull(result.BackupPath);

            _backupService.Restore(result.BackupPath, path);

            Assert.Equal("before", File.ReadAllText(path));
        }

        [Fact]
        public void LatestBackupCanBeFound()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "v1", new UTF8Encoding(false));
            ToolResult first = Write("a.txt", "v2");
            Assert.True(first.IsSuccess, first.Content);

            _clock.Advance(TimeSpan.FromSeconds(1));
            ToolResult second = Write("a.txt", "v3");
            Assert.True(second.IsSuccess, second.Content);

            string latest = _backupService.FindLatestBackup(path);
            Assert.Equal(second.BackupPath, latest);
        }

        [Fact]
        public void OversizedExistingFileIsRejected()
        {
            string path = Path.Combine(_workspace, "big.txt");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                stream.SetLength(2 * 1024 * 1024 + 1);
            }

            ToolResult result = Write("big.txt", "content");

            Assert.Equal("file_too_large", result.ErrorCode);
        }

        private ToolResult Write(string path, string content)
        {
            ToolApprovalPreview ignored;
            return Write(path, content, null, out ignored);
        }

        private ToolResult Write(
            string path,
            string content,
            ToolContext context,
            out ToolApprovalPreview preview)
        {
            ToolContext actual = context ?? _context;
            ToolCall call = Call(path, content);

            preview = null;
            try
            {
                preview = _tool.Describe(call, actual);
            }
            catch (Exception)
            {
                // 参数/准备失败时让 Execute 产出结构化错误。
            }

            if (preview != null)
            {
                actual.ApprovalPreview = preview;
            }

            return _tool.ExecuteAsync(call, actual, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        private static ToolCall Call(string path, string content)
        {
            var args = JsonValue.NewObject();
            args.Add("path", JsonValue.From(path));
            args.Add("content", JsonValue.From(content));

            return new ToolCall
            {
                Id = "call-1",
                Name = "write",
                ArgumentsJson = JsonWriter.Write(args)
            };
        }
    }
}
