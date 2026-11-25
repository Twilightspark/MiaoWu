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
    public sealed class EditToolTests : IDisposable
    {
        private readonly string _workspace;
        private readonly string _backups;
        private readonly EditTool _tool;
        private readonly ToolContext _context;
        private readonly MutableClock _clock;

        public EditToolTests()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "miaowu-edit-" + Guid.NewGuid().ToString("N"));
            _workspace = Path.Combine(root, "workspace");
            _backups = Path.Combine(root, "backups");
            Directory.CreateDirectory(_workspace);

            _clock = new MutableClock(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
            var writer = new WriteTool(new FileBackupService(_backups), _clock);
            _tool = new EditTool(writer);

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
        public void ReplacesUniqueOccurrence()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "a\nb\nc\n", new UTF8Encoding(false));

            ToolResult result = Edit("a.txt", "b", "B");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Equal("a\nB\nc\n", File.ReadAllText(path));
            Assert.Contains("已编辑", result.Content);
            Assert.Contains("替换：1 处", result.Content);
            Assert.NotNull(result.BackupPath);
        }

        [Fact]
        public void RejectsNonUniqueMatch()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "x\nx\n", new UTF8Encoding(false));

            ToolResult result = Edit("a.txt", "x", "y");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("old_string_not_unique", result.ErrorCode);
            Assert.Contains("2 次", result.ErrorMessage);
            Assert.Equal("x\nx\n", File.ReadAllText(path));
            Assert.True(
                !Directory.Exists(_backups) || Directory.GetFiles(_backups).Length == 0);
        }

        [Fact]
        public void ReplaceAllReplacesEveryMatch()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "x\nx\nx\n", new UTF8Encoding(false));

            ToolResult result = Edit("a.txt", "x", "y", replaceAll: true);

            Assert.True(result.IsSuccess, result.Content);
            Assert.Equal("y\ny\ny\n", File.ReadAllText(path));
            Assert.Contains("替换：3 处", result.Content);
        }

        [Fact]
        public void RejectsMissingText()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "hello", new UTF8Encoding(false));

            ToolResult result = Edit("a.txt", "zzz", "y");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("old_string_not_found", result.ErrorCode);
        }

        [Fact]
        public void RejectsPathOutsideWorkspace()
        {
            ToolResult result = Edit(@"..\..\outside.txt", "a", "b");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("path_outside_workspace", result.ErrorCode);
        }

        [Fact]
        public void RejectsMissingFile()
        {
            ToolResult result = Edit("missing.txt", "a", "b");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("not_found", result.ErrorCode);
        }

        [Fact]
        public void PreservesCrLfWhenOldStringUsesLf()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "a\r\nb\r\n", new UTF8Encoding(false));

            ToolResult result = Edit("a.txt", "a\nb", "a\nc");

            Assert.True(result.IsSuccess, result.Content);
            Assert.Equal("a\r\nc\r\n", File.ReadAllText(path));
        }

        [Fact]
        public void EmptyNewStringDeletesMatchedText()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "keep\ndrop\n", new UTF8Encoding(false));

            ToolResult result = Edit("a.txt", "drop\n", string.Empty);

            Assert.True(result.IsSuccess, result.Content);
            Assert.Equal("keep\n", File.ReadAllText(path));
        }

        [Fact]
        public void PreviewShowsUnifiedDiff()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "a\nb\n", new UTF8Encoding(false));

            ToolCall call = Call("a.txt", "b", "B");
            ToolApprovalPreview preview = _tool.Describe(call, _context);

            Assert.NotNull(preview);
            Assert.Equal("编辑文件", preview.Title);
            Assert.True(preview.CanShowDiff);
            Assert.Contains("+B", preview.Diff);
            Assert.Contains("-b", preview.Diff);
        }

        [Fact]
        public void ExternalModificationAfterApprovalIsRejected()
        {
            string path = Path.Combine(_workspace, "a.txt");
            File.WriteAllText(path, "original\n", new UTF8Encoding(false));

            ToolCall call = Call("a.txt", "original", "changed");
            _context.ApprovalPreview = _tool.Describe(call, _context);

            // 外部追加内容：old_string 仍唯一存在，但文件哈希与 mtime 已变。
            File.AppendAllText(path, "extra\n", new UTF8Encoding(false));

            ToolResult result = _tool.ExecuteAsync(call, _context, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("file_changed", result.ErrorCode);
            Assert.Equal("original\nextra\n", File.ReadAllText(path));
        }

        [Fact]
        public void MissingApprovalStateIsRejected()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "a", new UTF8Encoding(false));

            ToolResult result = _tool.ExecuteAsync(
                Call("a.txt", "a", "b"),
                _context,
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("approval_state_missing", result.ErrorCode);
            Assert.Equal("a", File.ReadAllText(Path.Combine(_workspace, "a.txt")));
        }

        [Fact]
        public void RejectsOversizedFile()
        {
            string path = Path.Combine(_workspace, "big.txt");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                stream.SetLength(ToolText.MaxFileBytes + 1);
            }

            ToolResult result = Edit("big.txt", "a", "b");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("file_too_large", result.ErrorCode);
        }

        [Fact]
        public void RejectsBinaryFile()
        {
            File.WriteAllBytes(Path.Combine(_workspace, "image.dat"), new byte[] { 0, 1, 2, 3 });

            ToolResult result = Edit("image.dat", "a", "b");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("binary_file", result.ErrorCode);
        }

        [Fact]
        public void RejectsNonUtf8File()
        {
            string path = Path.Combine(_workspace, "gbk.txt");

            // GBK 的“中文”；运行期只接受 UTF-8 文本。
            File.WriteAllBytes(path, new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 });

            ToolResult result = Edit("gbk.txt", "a", "b");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal("not_text", result.ErrorCode);
            Assert.Equal(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }, File.ReadAllBytes(path));
        }

        [Fact]
        public void EditDropsUtf8Bom()
        {
            string path = Path.Combine(_workspace, "bom.txt");
            byte[] body = new UTF8Encoding(false).GetBytes("a\nb\n");
            var bytes = new byte[3 + body.Length];
            bytes[0] = 0xEF;
            bytes[1] = 0xBB;
            bytes[2] = 0xBF;
            Buffer.BlockCopy(body, 0, bytes, 3, body.Length);
            File.WriteAllBytes(path, bytes);

            ToolResult result = Edit("bom.txt", "b", "B");

            Assert.True(result.IsSuccess, result.Content);
            byte[] written = File.ReadAllBytes(path);
            Assert.False(written.Length >= 3 && written[0] == 0xEF && written[1] == 0xBB && written[2] == 0xBF);
            Assert.Equal("a\nB\n", new UTF8Encoding(false).GetString(written));
        }

        [Fact]
        public void AuditArgumentsDoNotContainReplacedText()
        {
            const string Secret = "TOP-SECRET-OLD-TEXT";

            string summary = _tool.GetAuditArguments(
                Call("a.txt", Secret, "replacement"),
                _context);

            Assert.Contains("path", summary);
            Assert.Contains(Secret.Length.ToString() + " 字符", summary);
            Assert.DoesNotContain(Secret, summary);
            Assert.DoesNotContain("replacement", summary);
        }

        private ToolResult Edit(
            string path,
            string oldString,
            string newString,
            bool replaceAll = false)
        {
            ToolCall call = Call(path, oldString, newString, replaceAll);

            try
            {
                _context.ApprovalPreview = _tool.Describe(call, _context);
            }
            catch (Exception)
            {
                // 参数/准备失败时让 Execute 产出结构化错误。
            }

            return _tool.ExecuteAsync(call, _context, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        private static ToolCall Call(
            string path,
            string oldString,
            string newString,
            bool replaceAll = false)
        {
            var args = JsonValue.NewObject();
            args.Add("path", JsonValue.From(path));
            args.Add("old_string", JsonValue.From(oldString));
            args.Add("new_string", JsonValue.From(newString));
            args.Add("replace_all", JsonValue.From(replaceAll));

            return new ToolCall
            {
                Id = "call-1",
                Name = "edit",
                ArgumentsJson = JsonWriter.Write(args)
            };
        }
    }
}
