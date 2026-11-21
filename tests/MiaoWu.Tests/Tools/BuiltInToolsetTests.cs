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
        private readonly UserQuestionService _questions = new UserQuestionService();
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

            _registry = BuiltInToolset.CreateRegistry(_paths, _clock, null, _questions);
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
        public void RegistryContainsExactlyTheDshTools()
        {
            IReadOnlyList<IAgentTool> tools = _registry.GetAll();
            var names = new List<string>();
            for (int i = 0; i < tools.Count; i++)
            {
                names.Add(tools[i].Name);
            }

            Assert.Equal(19, names.Count);
            Assert.Contains(ToolNames.Read, names);
            Assert.Contains(ToolNames.Write, names);
            Assert.Contains(ToolNames.Edit, names);
            Assert.Contains(ToolNames.Glob, names);
            Assert.Contains(ToolNames.Grep, names);
            Assert.Contains(ToolNames.Pwsh, names);
            Assert.Contains(ToolNames.Skill, names);
            Assert.Contains(ToolNames.WorkingDirectory, names);
            Assert.Contains(ToolNames.WebFetch, names);
            Assert.Contains(ToolNames.WebSearch, names);
            Assert.Contains(ToolNames.ExitPlanMode, names);
            Assert.Contains(ToolNames.JobList, names);
            Assert.Contains(ToolNames.JobOutput, names);
            Assert.Contains(ToolNames.JobKill, names);
            Assert.Contains(ToolNames.Present, names);
            Assert.Contains(ToolNames.CreateGoal, names);
            Assert.Contains(ToolNames.GetGoal, names);
            Assert.Contains(ToolNames.UpdateGoal, names);
            Assert.Contains(ToolNames.AskUserQuestion, names);
        }

        [Fact]
        public void NoLegacyToolNameIsRegistered()
        {
            IReadOnlyList<IAgentTool> tools = _registry.GetAll();
            var names = new List<string>();
            for (int i = 0; i < tools.Count; i++)
            {
                names.Add(tools[i].Name);
            }

            Assert.DoesNotContain("list_dir", names);
            Assert.DoesNotContain("read_file", names);
            Assert.DoesNotContain("search_files", names);
            Assert.DoesNotContain("find_files", names);
            Assert.DoesNotContain("edit_file", names);
            Assert.DoesNotContain("write_file", names);
            Assert.DoesNotContain("run_command", names);
            Assert.DoesNotContain("load_skill", names);
        }

        [Fact]
        public void DefinitionsUseNewNamesAndSchemas()
        {
            var provider = new BuiltInToolDefinitionProvider(_registry);
            IReadOnlyList<ToolDefinition> definitions = provider.GetDefinitions();

            Assert.Equal(19, definitions.Count);
            for (int i = 0; i < definitions.Count; i++)
            {
                ToolDefinition definition = definitions[i];
                // DSH 的宿主面/网络/目标/提问工具名带下划线，其余模型工具名不带。
                if (definition.Name != ToolNames.WorkingDirectory
                    && definition.Name != ToolNames.WebFetch
                    && definition.Name != ToolNames.WebSearch
                    && definition.Name != ToolNames.ExitPlanMode
                    && definition.Name != ToolNames.CreateGoal
                    && definition.Name != ToolNames.GetGoal
                    && definition.Name != ToolNames.UpdateGoal
                    && definition.Name != ToolNames.AskUserQuestion
                    && !definition.Name.StartsWith("job_", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("_", definition.Name);
                }
                Assert.DoesNotContain("list_dir", definition.ParametersJson, StringComparison.Ordinal);
                Assert.DoesNotContain("startLine", definition.ParametersJson, StringComparison.Ordinal);
            }

            Assert.Contains(definitions, d => d.Name == ToolNames.Read && d.ParametersJson.Contains("\"offset\""));
            Assert.Contains(definitions, d => d.Name == ToolNames.Grep && d.ParametersJson.Contains("\"pattern\""));
            Assert.Contains(definitions, d => d.Name == ToolNames.Glob && d.ParametersJson.Contains("\"limit\""));
            Assert.Contains(definitions, d => d.Name == ToolNames.Edit && d.ParametersJson.Contains("\"old_string\""));
            Assert.Contains(definitions, d => d.Name == ToolNames.Pwsh && d.ParametersJson.Contains("\"timeoutMs\""));
            Assert.Contains(definitions, d => d.Name == ToolNames.Skill && d.ParametersJson.Contains("\"name\""));
        }

        [Fact]
        public void GlobRunsWithoutApproval()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "hello");

            ToolExecution execution = Execute("glob", Args(("pattern", "*.txt")));

            Assert.Equal(ToolExecutionDecision.Auto, execution.Decision);
            Assert.True(execution.Result.IsSuccess, execution.Result.Content);
            Assert.Contains("a.txt", execution.Result.Content);
            Assert.Equal(0, _approvals.GetPendingCount());
            Assert.Equal("auto", _audit.Entries[0].Decision);
        }

        [Fact]
        public void ReadRunsWithoutApproval()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "line1\nline2\n");

            ToolExecution execution = Execute("read", Args(("path", "a.txt")));

            Assert.Equal(ToolExecutionDecision.Auto, execution.Decision);
            Assert.Contains("line1", execution.Result.Content);
            Assert.Contains("line2", execution.Result.Content);
        }

        [Fact]
        public void WriteRequiresApprovalThenWritesAndBacksUp()
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
        public void WriteSessionGrantSkipsSecondApproval()
        {
            Task<ToolExecution> first = _dispatcher.ExecuteAsync(
                WriteCall("a.txt", "v1"),
                _context,
                CancellationToken.None);
            ToolApprovalTicket pending = _approvals.GetPending()[0];
            _approvals.TryResolve(pending.Request.RequestId, ToolApprovalOutcome.ApproveForSession, null);
            first.GetAwaiter().GetResult();

            ToolExecution second = Execute("write", WriteArgs("a.txt", "v2"), "call-2");

            Assert.Equal(ToolExecutionDecision.ApprovedForSession, second.Decision);
            Assert.True(second.Result.IsSuccess, second.Result.Content);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(_workspace, "a.txt")));
            Assert.Equal(0, _approvals.GetPendingCount());
        }

        [Fact]
        public void EditRunsAfterApproval()
        {
            File.WriteAllText(Path.Combine(_workspace, "a.txt"), "before\n");

            Task<ToolExecution> task = _dispatcher.ExecuteAsync(
                EditCall("a.txt", "before", "after"),
                _context,
                CancellationToken.None);

            Assert.Equal(1, _approvals.GetPendingCount());
            ToolApprovalTicket pending = _approvals.GetPending()[0];
            Assert.Equal("编辑文件", pending.Request.Title);
            Assert.True(pending.Request.CanShowDiff);
            _approvals.TryResolve(pending.Request.RequestId, ToolApprovalOutcome.ApproveOnce, null);

            ToolExecution execution = task.GetAwaiter().GetResult();
            Assert.Equal(ToolExecutionDecision.ApprovedOnce, execution.Decision);
            Assert.True(execution.Result.IsSuccess, execution.Result.Content);
            Assert.Equal("after\n", File.ReadAllText(Path.Combine(_workspace, "a.txt")));
            Assert.Contains("替换：1 处", execution.Result.Content);
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
        public void PwshRunsAfterApproval()
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
                "pwsh",
                Args(("command", "format C:")),
                "call-1");

            Assert.Equal(ToolExecutionDecision.Blocked, execution.Decision);
            Assert.Equal(ToolResultStatus.Blocked, execution.Result.Status);
            Assert.Equal(0, _approvals.GetPendingCount());
        }

        [Fact]
        public void WriteAuditDoesNotContainFileContent()
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
        public void EditAuditDoesNotContainReplacedText()
        {
            const string Secret = "TOP-SECRET-OLD";

            File.WriteAllText(Path.Combine(_workspace, "secret.txt"), Secret);

            Task<ToolExecution> task = _dispatcher.ExecuteAsync(
                EditCall("secret.txt", Secret, "replacement"),
                _context,
                CancellationToken.None);
            ToolApprovalTicket pending = _approvals.GetPending()[0];
            _approvals.TryResolve(pending.Request.RequestId, ToolApprovalOutcome.ApproveOnce, null);
            task.GetAwaiter().GetResult();

            ToolAuditEntry entry = _audit.Entries[0];
            Assert.Contains("path=secret.txt", entry.ArgumentsSummary);
            Assert.Contains(Secret.Length.ToString() + " 字符", entry.ArgumentsSummary);
            Assert.DoesNotContain(Secret, entry.ArgumentsSummary);
            Assert.DoesNotContain("replacement", entry.ArgumentsSummary);
        }

        [Fact]
        public void PwshAuditStoresCommandButNotOutput()
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
                Call("glob", Args(("pattern", "*.txt"))),
                _context,
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(execution.Result.IsSuccess);

            string[] files = Directory.GetFiles(_paths.LogsDirectory, "audit-*.jsonl");
            Assert.Single(files);
            string[] lines = File.ReadAllLines(files[0]);
            Assert.Single(lines);

            JsonValue entry = JsonParser.Parse(lines[0]);
            Assert.Equal("glob", entry.Get("tool").AsString());
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
            return Call("write", WriteArgs(path, content), callId);
        }

        private static string WriteArgs(string path, string content)
        {
            return Args(("path", path), ("content", content));
        }

        private static ToolCall EditCall(string path, string oldString, string newString)
        {
            return Call(
                "edit",
                Args(
                    ("path", path),
                    ("old_string", oldString),
                    ("new_string", newString)));
        }

        private static ToolCall CommandCall(string command, string callId = "call-1")
        {
            return Call("pwsh", Args(("command", command)), callId);
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
