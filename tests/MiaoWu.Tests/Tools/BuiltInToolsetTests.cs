using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Tools;
using MiaoWu.Tests.Conversations;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class BuiltInToolsetTests : IDisposable
    {
        private readonly string _root;
        private readonly string _workspace;
        private readonly AppPaths _paths;
        private readonly ToolApprovalService _approvals = new ToolApprovalService();
        private readonly CapturingAuditLog _audit = new CapturingAuditLog();
        private readonly MutableClock _clock;
        private readonly ToolRegistry _registry;
        private readonly AgentToolDispatcher _dispatcher;
        private readonly ToolContext _context;

        public BuiltInToolsetTests()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "miaowu-lifecycle-" + Guid.NewGuid().ToString("N"));
            _workspace = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(_workspace);

            _paths = new AppPaths(Path.Combine(_root, "appdata"));
            _paths.EnsureCreated();
            _clock = new MutableClock(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));

            _registry = BuiltInToolset.CreateRegistry(_paths, _clock);
            _dispatcher = new AgentToolDispatcher(_registry, _approvals, _audit, _clock);
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
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void RegistryContainsAllBuiltInTools()
        {
            IReadOnlyList<IAgentTool> tools = _registry.GetAll();
            var names = new List<string>();
            for (int i = 0; i < tools.Count; i++)
            {
                names.Add(tools[i].Name);
            }

            Assert.Contains(ToolNames.ListDirectory, names);
            Assert.Contains(ToolNames.ReadFile, names);
            Assert.Contains(ToolNames.SearchFiles, names);
            Assert.Contains(ToolNames.WriteFile, names);
            Assert.Contains(ToolNames.RunCommand, names);
        }

        [Fact]
        public void ListDirectoryRunsWithoutApproval()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "hello");

            ToolExecution execution = Execute("list_dir", "{}");

            Assert.Equal(ToolExecutionDecision.Auto, execution.Decision);
            Assert.True(execution.Result.IsSuccess);
            Assert.Contains("a.txt", execution.Result.Content);
            Assert.Equal(0, _approvals.GetPendingCount());
            Assert.Equal("auto", _audit.Entries[0].Decision);
        }

        [Fact]
        public void ReadFileRunsWithoutApproval()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "line1\nline2\n");

            ToolExecution execution = Execute("read_file", Args(("path", "a.txt")));

            Assert.Equal(ToolExecutionDecision.Auto, execution.Decision);
            Assert.Contains("line1", execution.Result.Content);
            Assert.Contains("line2", execution.Result.Content);
        }

        [Fact]
        public void WriteFileRequiresApprovalThenWritesAndBacksUp()
        {
            Task<ToolExecution> first = _dispatcher.ExecuteAsync(
                WriteCall("a.txt", "v1"),
                _context,
                CancellationToken.None);

            Assert.Equal(1, _approvals.GetPendingCount());
            ToolApprovalTicket pending = _approvals.GetPending()[0];
            Assert.Contains("文件", pending.Request.Title);
            Assert.True(_approvals.TryResolve(pending.Request.RequestId, ToolApprovalOutcome.ApproveOnce, null));

            ToolExecution firstExecution = first.GetAwaiter().GetResult();
            Assert.Equal(ToolExecutionDecision.ApprovedOnce, firstExecution.Decision);
            Assert.True(firstExecution.Result.IsSuccess, firstExecution.Result.Content);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(_workspace, "a.txt")));

            Task<ToolExecution> second = _dispatcher.ExecuteAsync(
                WriteCall("a.txt", "v2"),
                _context,
                CancellationToken.None);

            ToolApprovalTicket secondPending = _approvals.GetPending()[0];
            _approvals.TryResolve(secondPending.Request.RequestId, ToolApprovalOutcome.ApproveOnce, null);

            ToolExecution secondExecution = second.GetAwaiter().GetResult();
            Assert.True(secondExecution.Result.IsSuccess, secondExecution.Result.Content);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(_workspace, "a.txt")));
            Assert.False(string.IsNullOrEmpty(secondExecution.Result.BackupPath));
            Assert.True(File.Exists(secondExecution.Result.BackupPath));
        }

        [Fact]
        public void WriteFileSessionGrantSkipsSecondApproval()
        {
            Task<ToolExecution> first = _dispatcher.ExecuteAsync(
                WriteCall("a.txt", "v1"),
                _context,
                CancellationToken.None);
            ToolApprovalTicket pending = _approvals.GetPending()[0];
            _approvals.TryResolve(pending.Request.RequestId, ToolApprovalOutcome.ApproveForSession, null);
            first.GetAwaiter().GetResult();

            ToolExecution second = Execute("write_file", WriteArgs("a.txt", "v2"), "call-2");

            Assert.Equal(ToolExecutionDecision.ApprovedForSession, second.Decision);
            Assert.True(second.Result.IsSuccess, second.Result.Content);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(_workspace, "a.txt")));
            Assert.Equal(0, _approvals.GetPendingCount());
        }

        [Fact]
        public void DeniedWriteLeavesFileUntouched()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "original");

            Task<ToolExecution> task = _dispatcher.ExecuteAsync(
                WriteCall("a.txt", "changed"),
                _context,
                CancellationToken.None);

            ToolApprovalTicket pending = _approvals.GetPending()[0];
            _approvals.TryResolve(pending.Request.RequestId, ToolApprovalOutcome.Deny, "不要改");

            ToolExecution execution = task.GetAwaiter().GetResult();
            Assert.Equal(ToolExecutionDecision.Denied, execution.Decision);
            Assert.Equal("original", File.ReadAllText(Path.Combine(_workspace, "a.txt")));
        }

        [Fact]
        public void RunCommandRunsAfterApproval()
        {
            Task<ToolExecution> task = _dispatcher.ExecuteAsync(
                CommandCall("Write-Output 'hi'"),
                _context,
                CancellationToken.None);

            Assert.Equal(1, _approvals.GetPendingCount());
            ToolApprovalTicket pending = _approvals.GetPending()[0];
            Assert.Equal("执行命令", pending.Request.Title);
            Assert.False(pending.Request.CanApproveForSession);
            _approvals.TryResolve(pending.Request.RequestId, ToolApprovalOutcome.ApproveOnce, null);

            ToolExecution execution = task.GetAwaiter().GetResult();
            Assert.Equal(ToolExecutionDecision.ApprovedOnce, execution.Decision);
            Assert.True(execution.Result.IsSuccess, execution.Result.Content);
            Assert.Contains("hi", execution.Result.Content);
            Assert.Equal("approved_once", _audit.Entries[0].Decision);
        }

        [Fact]
        public void BlockedCommandIsRejectedWithoutApproval()
        {
            ToolExecution execution = Execute(
                "run_command",
                Args(("command", "format C:")),
                "call-1");

            Assert.Equal(ToolExecutionDecision.Blocked, execution.Decision);
            Assert.Equal(ToolResultStatus.Blocked, execution.Result.Status);
            Assert.Equal(0, _approvals.GetPendingCount());
        }

        [Fact]
        public void WriteFileAuditDoesNotContainFileContent()
        {
            const string Secret = "TOP-SECRET-CONTENT";

            Task<ToolExecution> task = _dispatcher.ExecuteAsync(
                WriteCall("secret.txt", Secret),
                _context,
                CancellationToken.None);
            ToolApprovalTicket pending = _approvals.GetPending()[0];
            _approvals.TryResolve(pending.Request.RequestId, ToolApprovalOutcome.ApproveOnce, null);
            task.GetAwaiter().GetResult();

            ToolAuditEntry entry = _audit.Entries[0];
            Assert.Contains("path=secret.txt", entry.ArgumentsSummary);
            Assert.Contains(Secret.Length.ToString() + " 字符", entry.ArgumentsSummary);
            Assert.DoesNotContain(Secret, entry.ArgumentsSummary);
        }

        [Fact]
        public void RunCommandAuditStoresCommandButNotOutput()
        {
            Task<ToolExecution> task = _dispatcher.ExecuteAsync(
                CommandCall("Write-Output 'hi'"),
                _context,
                CancellationToken.None);
            ToolApprovalTicket pending = _approvals.GetPending()[0];
            _approvals.TryResolve(pending.Request.RequestId, ToolApprovalOutcome.ApproveOnce, null);
            task.GetAwaiter().GetResult();

            ToolAuditEntry entry = _audit.Entries[0];
            Assert.Contains("Write-Output 'hi'", entry.ArgumentsSummary);
            Assert.DoesNotContain("hi\n", entry.ArgumentsSummary);
        }

        [Fact]
        public void AuditLogWriterCreatesDailyFile()
        {
            var writer = new ToolAuditLogWriter(_paths);
            var dispatcher = new AgentToolDispatcher(_registry, _approvals, writer, _clock);

            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "hello");
            ToolExecution execution = dispatcher.ExecuteAsync(
                Call("list_dir", "{}"),
                _context,
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(execution.Result.IsSuccess);

            string[] files = Directory.GetFiles(_paths.LogsDirectory, "audit-*.jsonl");
            Assert.Single(files);
            string[] lines = File.ReadAllLines(files[0]);
            Assert.Single(lines);

            JsonValue entry = JsonParser.Parse(lines[0]);
            Assert.Equal("list_dir", entry.Get("tool").AsString());
            Assert.Equal("auto", entry.Get("decision").AsString());
            Assert.Equal("succeeded", entry.Get("status").AsString());
        }

        private ToolExecution Execute(
            string toolName,
            string arguments,
            string callId = "call-1")
        {
            return _dispatcher.ExecuteAsync(
                Call(toolName, arguments, callId),
                _context,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        private static ToolCall Call(string name, string arguments, string callId = "call-1")
        {
            return new ToolCall
            {
                Id = callId,
                Name = name,
                ArgumentsJson = arguments
            };
        }

        private static ToolCall WriteCall(string path, string content, string callId = "call-1")
        {
            return Call("write_file", WriteArgs(path, content), callId);
        }

        private static string WriteArgs(string path, string content)
        {
            return Args(("path", path), ("content", content));
        }

        private static ToolCall CommandCall(string command, string callId = "call-1")
        {
            return Call("run_command", Args(("command", command)), callId);
        }

        private static string Args(params (string Key, string Value)[] pairs)
        {
            var root = JsonValue.NewObject();
            for (int i = 0; i < pairs.Length; i++)
            {
                root.Add(pairs[i].Key, JsonValue.From(pairs[i].Value));
            }

            return JsonWriter.Write(root);
        }

        private sealed class CapturingAuditLog : IToolAuditLog
        {
            public List<ToolAuditEntry> Entries { get; } = new List<ToolAuditEntry>();

            public void Write(ToolAuditEntry entry)
            {
                Entries.Add(entry);
            }
        }
    }
}
