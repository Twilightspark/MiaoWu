using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Agents.Goals;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure;
using MiaoWu.Infrastructure.Conversations;
using MiaoWu.Infrastructure.Providers;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Agents
{
    /// <summary>
    /// 对齐 DSH 之后的新循环语义：step 边界事件、next-step 插话、并行调度与取消补结果。
    /// </summary>
    public sealed class AgentStepLoopTests : IDisposable
    {
        private readonly string _workspace;
        private readonly WorkspaceConversationStore _store;
        private readonly ScriptedModelApi _api;
        private readonly AgentSessionServices _services;
        private readonly AgentRuntime _runtime;

        public AgentStepLoopTests()
        {
            _workspace = Path.Combine(Path.GetTempPath(), "miaowu-step-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);

            _store = new WorkspaceConversationStore("w1", _workspace, SystemClock.Instance);
            _api = new ScriptedModelApi();

            var catalog = new ProviderCatalog { ActiveProviderId = "p" };
            var provider = new ProviderDefinition
            {
                Id = "p",
                DisplayName = "Scripted",
                Api = "scripted",
                BaseUrl = "https://example.test"
            };
            provider.Models.Add(new ModelDefinition
            {
                Id = "m",
                Name = "Scripted",
                ContextWindow = 8192,
                MaxOutputTokens = 1024
            });
            catalog.Providers.Add(provider);

            var registry = new ModelRegistry(
                catalog,
                new MemoryCredentialStore("p", "test-key"),
                new IModelApi[] { _api });

            _services = new AgentSessionServices
            {
                Models = registry,
                Store = _store,
                Logger = NullAgentLogger.Instance,
                Clock = SystemClock.Instance,
                ErrorClassifier = new AgentErrorClassifier()
            };

            _runtime = new AgentRuntime(_services, new AgentRuntimeOptions());
            _runtime.Start();
        }

        public void Dispose()
        {
            _runtime.Dispose();
            try
            {
                if (Directory.Exists(_workspace))
                {
                    Directory.Delete(_workspace, true);
                }
            }
            catch (Exception)
            {
            }
        }

        [Fact]
        public async Task StepEventsTrackEveryModelRequest()
        {
            var executor = new ImmediateExecutor();
            IAgentSession session = await CreateSessionAsync(executor, ReadDefinitions());

            _api.EnqueueToolCalls(new ScriptedModelApi.ToolCallScript
            {
                Id = "call_1",
                Name = "read",
                ArgumentsJson = "{\"path\":\"a.txt\"}"
            });
            _api.EnqueueText("做完了。");

            var sink = new CollectingSink();
            session.Subscribe(sink);

            AgentRun run = await session.SendAsync(TextInput("开始"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            StepStartedEvent[] started = sink.Events.OfType<StepStartedEvent>().ToArray();
            StepCompletedEvent[] completed = sink.Events.OfType<StepCompletedEvent>().ToArray();

            string diag = "started=" + started.Length
                + ";completed=" + completed.Length
                + ";error=" + (session.Snapshot().LastError == null
                    ? "-"
                    : session.Snapshot().LastError.Kind + "/" + session.Snapshot().LastError.Message);

            Assert.True(started.Length == 2, diag);
            Assert.Equal(new[] { 1, 2 }, started.Select(e => e.StepIndex).ToArray());
            Assert.True(started[0].IsFirstStep);
            Assert.False(started[1].IsFirstStep);

            Assert.Equal(2, completed.Length);
            Assert.Equal(StepEndReason.ToolCalls, completed[0].Reason);
            Assert.True(completed[0].WillContinue);
            Assert.Equal(1, completed[0].ToolCallCount);
            Assert.Equal(StepEndReason.Completed, completed[1].Reason);
            Assert.False(completed[1].WillContinue);

            Assert.Equal(2, session.Snapshot().StepIndex);
        }

        [Fact]
        public async Task ParallelToolCallsRunTogetherAndKeepModelOrder()
        {
            var executor = new ConcurrentExecutor();
            IAgentSession session = await CreateSessionAsync(executor, ReadDefinitions());

            _api.EnqueueToolCalls(
                new ScriptedModelApi.ToolCallScript
                {
                    Id = "call_a",
                    Name = "read",
                    ArgumentsJson = "{\"path\":\"a.txt\"}"
                },
                new ScriptedModelApi.ToolCallScript
                {
                    Id = "call_b",
                    Name = "read",
                    ArgumentsJson = "{\"path\":\"b.txt\"}"
                });
            _api.EnqueueText("两个都读完了。");

            AgentRun run = await session.SendAsync(TextInput("并行读"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Equal(2, executor.MaxConcurrency);

            AgentMessage[] toolResults = session.Snapshot().Messages
                .Where(m => m.Role == AgentMessageRole.ToolResult)
                .ToArray();

            Assert.Equal(2, toolResults.Length);
            Assert.Equal("call_a", toolResults[0].ToolCallId);
            Assert.Equal("call_b", toolResults[1].ToolCallId);
        }

        [Fact]
        public async Task SteerIsInjectedAtNextStepBoundary()
        {
            var executor = new GatedExecutor();
            IAgentSession session = await CreateSessionAsync(executor, ReadDefinitions());

            _api.EnqueueToolCalls(new ScriptedModelApi.ToolCallScript
            {
                Id = "call_1",
                Name = "read",
                ArgumentsJson = "{\"path\":\"a.txt\"}"
            });
            _api.EnqueueText("收到插话后的回答。");

            AgentRun run = await session.SendAsync(TextInput("原始问题"), AgentSendMode.Queue, CancellationToken.None);

            await WaitForAsync(() => executor.Entered == 1, TimeSpan.FromSeconds(5));
            await session.SendAsync(TextInput("补充一句"), AgentSendMode.Steer, CancellationToken.None);
            executor.Release();

            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            List<AgentMessage> messages = session.Snapshot().Messages;
            int steerIndex = messages.FindIndex(
                m => m.Role == AgentMessageRole.User && m.GetText() == "补充一句");
            int answerIndex = messages.FindIndex(
                m => m.Role == AgentMessageRole.Assistant && m.GetText() == "收到插话后的回答。");

            Assert.True(steerIndex > 0, "插话没有进入消息历史。");
            Assert.True(answerIndex > steerIndex, "插话应排在下一步的请求之前。");
            Assert.Single(messages, m => m.Role == AgentMessageRole.User && m.GetText() == "补充一句");
        }

        [Fact]
        public async Task CancelFillsToolResultsForEveryCall()
        {
            var executor = new BlockingExecutor();
            IAgentSession session = await CreateSessionAsync(executor, ReadDefinitions());

            _api.EnqueueToolCalls(
                new ScriptedModelApi.ToolCallScript
                {
                    Id = "call_a",
                    Name = "read",
                    ArgumentsJson = "{\"path\":\"a.txt\"}"
                },
                new ScriptedModelApi.ToolCallScript
                {
                    Id = "call_b",
                    Name = "read",
                    ArgumentsJson = "{\"path\":\"b.txt\"}"
                });

            AgentRun run = await session.SendAsync(TextInput("慢慢来"), AgentSendMode.Queue, CancellationToken.None);
            await WaitForAsync(
                () => executor.Entered >= 1,
                TimeSpan.FromSeconds(5),
                () => "state=" + session.State
                    + ";entered=" + executor.Entered
                    + ";msgs=" + session.Snapshot().Messages.Count
                    + ";error=" + (session.Snapshot().LastError == null
                        ? "-"
                        : session.Snapshot().LastError.Message));

            await session.CancelAsync(true, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            AgentMessage[] toolResults = session.Snapshot().Messages
                .Where(m => m.Role == AgentMessageRole.ToolResult)
                .ToArray();

            Assert.Equal(2, toolResults.Length);
            Assert.Equal(new[] { "call_a", "call_b" }, toolResults.Select(m => m.ToolCallId).ToArray());
            Assert.All(toolResults, m => Assert.True(m.IsError));
        }

        [Fact]
        public async Task ToolTimeoutBecomesResultAndRunContinues()
        {
            var executor = new SlowHonoringExecutor();
            IAgentSession session = await CreateSessionAsync(executor, new TimeoutDefinitions(50));

            _api.EnqueueToolCalls(new ScriptedModelApi.ToolCallScript
            {
                Id = "call_slow",
                Name = "read",
                ArgumentsJson = "{\"path\":\"a.txt\"}"
            });
            _api.EnqueueText("工具超时了，我换一种办法。");

            AgentRun run = await session.SendAsync(TextInput("读一下"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            ToolExecution execution = Assert.Single(session.Snapshot().ToolExecutions);
            Assert.Equal(ToolResultStatus.Failed, execution.Result.Status);
            Assert.Equal("tool_timeout", execution.Result.ErrorCode);
            Assert.Contains("超过 50 ms", execution.Result.Content);

            // 超时是工具结果，不是会话取消：下一轮照样跑完。
            Assert.Equal("工具超时了，我换一种办法。", session.Snapshot().Messages.Last().GetText());
        }

        [Fact]
        public async Task ActiveGoalCountsOneRoundPerUserTurn()
        {
            var goals = new SessionGoalState();
            Assert.True(goals.Create("完成对齐", null).IsSuccess);

            var executor = new ImmediateExecutor();
            IAgentSession session = await CreateSessionAsync(executor, ReadDefinitions(), goals);

            _api.EnqueueText("第一版回答");
            AgentRun run = await session.SendAsync(TextInput("开始"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Equal(1, goals.Current.RoundsStarted);

            // retry 重跑同一条用户消息，不应当再累加目标轮次。
            string assistantId = session.Snapshot().Messages.Last().Id;
            _api.EnqueueText("第二版回答");
            await session.RetryAsync(assistantId, CancellationToken.None);
            await WaitIdleAsync(session);

            Assert.Equal(1, goals.Current.RoundsStarted);
        }

        [Fact]
        public async Task ActiveGoalAutoContinuesUntilConsecutiveCap()
        {
            var goals = new SessionGoalState();
            Assert.True(goals.Create("完成对齐", 50).IsSuccess);

            var executor = new ImmediateExecutor();
            IAgentSession session = await CreateSessionAsync(
                executor,
                ReadDefinitions(),
                goals,
                true,
                2);

            _api.EnqueueText("第一轮回答");
            _api.EnqueueText("自动续跑一");
            _api.EnqueueText("自动续跑二");
            _api.EnqueueText("多余的回答");

            AgentRun run = await session.SendAsync(TextInput("开始"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitForAsync(
                () => goals.Current.RoundsStarted >= 3,
                TimeSpan.FromSeconds(10),
                () => "rounds=" + goals.Current.RoundsStarted);
            await WaitIdleAsync(session);

            Assert.Equal(3, goals.Current.RoundsStarted);
            List<AgentMessage> messages = session.Snapshot().Messages;
            Assert.Equal(3, messages.Count(m => m.Role == AgentMessageRole.User));
            Assert.Equal(3, messages.Count(m => m.Role == AgentMessageRole.Assistant));
            Assert.Contains(messages, m => m.GetText().Contains("继续推进目标"));
            Assert.Equal(1, _api.Remaining);
        }

        [Fact]
        public async Task GoalContinuationStopsWhenGoalIsNotActive()
        {
            var goals = new SessionGoalState();
            GoalMutationResult created = goals.Create("目标", 50);
            Assert.True(goals.Update(created.Goal.Id, 1, "pause", null, null, null).IsSuccess);

            var executor = new ImmediateExecutor();
            IAgentSession session = await CreateSessionAsync(
                executor,
                ReadDefinitions(),
                goals,
                true,
                8);

            _api.EnqueueText("暂停后的回答");
            _api.EnqueueText("不该被消费的回答");

            AgentRun run = await session.SendAsync(TextInput("开始"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Equal(0, goals.Current.RoundsStarted);
            Assert.Equal(1, _api.Remaining);
        }

        [Fact]
        public async Task GoalContinuationRespectsGoalRoundBudget()
        {
            var goals = new SessionGoalState();
            Assert.True(goals.Create("只允许一轮", 1).IsSuccess);

            var executor = new ImmediateExecutor();
            IAgentSession session = await CreateSessionAsync(
                executor,
                ReadDefinitions(),
                goals,
                true,
                8);

            _api.EnqueueText("唯一一轮");
            _api.EnqueueText("不该被消费的回答");

            AgentRun run = await session.SendAsync(TextInput("开始"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Equal(1, goals.Current.RoundsStarted);
            Assert.Equal(1, _api.Remaining);
        }

        [Fact]
        public async Task GoalContinuationIsOffByDefault()
        {
            var goals = new SessionGoalState();
            Assert.True(goals.Create("目标", 50).IsSuccess);

            var executor = new ImmediateExecutor();
            IAgentSession session = await CreateSessionAsync(executor, ReadDefinitions(), goals);

            _api.EnqueueText("只有这一轮");
            _api.EnqueueText("不该被消费的回答");

            AgentRun run = await session.SendAsync(TextInput("开始"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Equal(1, goals.Current.RoundsStarted);
            Assert.Equal(1, _api.Remaining);
        }

        [Fact]
        public async Task ToolContextReadsApprovalPolicyPerCall()
        {
            ToolApprovalPolicy current = ToolApprovalPolicy.Ask;
            var executor = new PolicyRecordingExecutor();
            IAgentSession session = await CreateSessionAsync(
                executor,
                ReadDefinitions(),
                approvalPolicyProvider: () => current);

            _api.EnqueueToolCalls(new ScriptedModelApi.ToolCallScript
            {
                Id = "call_a",
                Name = "read",
                ArgumentsJson = "{\"path\":\"a.txt\"}"
            });
            _api.EnqueueText("第一轮完成。");
            AgentRun run = await session.SendAsync(TextInput("读 a"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            // 设置页把“每次询问”改成“从不询问”后，下一次工具调用立刻用新策略。
            current = ToolApprovalPolicy.Never;
            _api.EnqueueToolCalls(new ScriptedModelApi.ToolCallScript
            {
                Id = "call_b",
                Name = "read",
                ArgumentsJson = "{\"path\":\"b.txt\"}"
            });
            _api.EnqueueText("第二轮完成。");
            run = await session.SendAsync(TextInput("读 b"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Equal(
                new[] { ToolApprovalPolicy.Ask, ToolApprovalPolicy.Never },
                executor.Policies);
        }

        [Fact]
        public async Task GoalContinuationProviderOverridesSnapshot()
        {
            var goals = new SessionGoalState();
            Assert.True(goals.Create("目标", 50).IsSuccess);

            var executor = new ImmediateExecutor();
            // 快照是关闭的，provider 打开：每次判断续跑都读 provider。
            IAgentSession session = await CreateSessionAsync(
                executor,
                ReadDefinitions(),
                goals,
                false,
                1,
                null,
                () => true);

            _api.EnqueueText("第一轮回答");
            _api.EnqueueText("自动续跑一");
            _api.EnqueueText("多余的回答");

            AgentRun run = await session.SendAsync(TextInput("开始"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitForAsync(
                () => goals.Current.RoundsStarted == 2,
                TimeSpan.FromSeconds(5),
                () => "目标续跑没有发生。");
            await WaitIdleAsync(session);

            Assert.Equal(2, session.Snapshot().Messages.Count(m => m.Role == AgentMessageRole.User));
            Assert.Equal(1, _api.Remaining);
        }

        [Fact]
        public async Task PlanModeTogglesAndPublishesEvent()
        {
            IAgentSession session = await CreateSessionAsync(new ImmediateExecutor(), ReadDefinitions());
            var sink = new CollectingSink();
            session.Subscribe(sink);

            Assert.False(session.PlanMode);

            session.SetPlanMode(true);
            Assert.True(session.PlanMode);
            Assert.True(session.Snapshot().PlanMode);

            // 重复设置不发事件。
            session.SetPlanMode(true);
            session.SetPlanMode(false);
            Assert.False(session.PlanMode);

            PlanModeChangedEvent[] events = sink.Events.OfType<PlanModeChangedEvent>().ToArray();
            Assert.Equal(2, events.Length);
            Assert.True(events[0].Active);
            Assert.False(events[1].Active);
        }

        [Fact]
        public async Task SystemPromptCarriesPlanModeState()
        {
            IAgentSession session = await CreateSessionAsync(new ImmediateExecutor(), new ReadWriteDefinitions());
            session.SetPlanMode(true);

            _api.EnqueueText("计划模式回答。");
            AgentRun run = await session.SendAsync(TextInput("先调研"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            string prompt = _api.LastRequest.SystemPrompt;
            Assert.Contains("当前模式：计划模式（只读）", prompt);
            Assert.Contains("<plan-mode>", prompt);
            Assert.Contains("exit_plan_mode", prompt);
            Assert.Contains("不要声称已经写入", prompt);
            Assert.Contains("- read:", prompt);
            Assert.DoesNotContain("- write:", prompt);
        }

        [Fact]
        public async Task SystemPromptReportsNormalModeAndToolCount()
        {
            IAgentSession session = await CreateSessionAsync(new ImmediateExecutor(), new ReadWriteDefinitions());

            _api.EnqueueText("普通模式回答。");
            AgentRun run = await session.SendAsync(TextInput("开始"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            string prompt = _api.LastRequest.SystemPrompt;
            Assert.Contains("当前模式：普通模式（可读写）", prompt);
            Assert.Contains("可用工具：2 个", prompt);
            Assert.DoesNotContain("<plan-mode>", prompt);
            Assert.Contains("- write:", prompt);
        }

        [Fact]
        public async Task ToolFailureAppendsNextStepGuidance()
        {
            IAgentSession session = await CreateSessionAsync(
                new FailingExecutor("timeout", "抓取超时（20 秒）。"),
                ReadDefinitions());

            _api.EnqueueToolCalls(new ScriptedModelApi.ToolCallScript
            {
                Id = "call_web",
                Name = "web_search",
                ArgumentsJson = "{\"query\":\"powershell\"}"
            });
            _api.EnqueueText("换个办法继续。");

            AgentRun run = await session.SendAsync(TextInput("搜一下"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            AgentMessage tool = session.Snapshot().Messages.First(m => m.Role == AgentMessageRole.ToolResult);
            string content = tool.GetText();
            Assert.Contains("[下一步建议]", content);
            Assert.Contains("web_fetch", content);
        }

        [Fact]
        public async Task RepeatedIdenticalFailureIsEscalated()
        {
            IAgentSession session = await CreateSessionAsync(
                new FailingExecutor("timeout", "抓取超时（20 秒）。"),
                ReadDefinitions());

            // 同一步里原样重试一次：第二次要明确提示“已连续失败 2 次”。
            _api.EnqueueToolCalls(
                new ScriptedModelApi.ToolCallScript
                {
                    Id = "call_1",
                    Name = "web_search",
                    ArgumentsJson = "{\"query\":\"powershell\"}"
                },
                new ScriptedModelApi.ToolCallScript
                {
                    Id = "call_2",
                    Name = "web_search",
                    ArgumentsJson = "{\"query\":\"powershell\"}"
                });
            _api.EnqueueText("停止重试。");

            AgentRun run = await session.SendAsync(TextInput("搜两次"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            AgentMessage[] tools = session.Snapshot().Messages
                .Where(m => m.Role == AgentMessageRole.ToolResult)
                .ToArray();
            Assert.Equal(2, tools.Length);
            Assert.DoesNotContain("连续失败 2 次", tools[0].GetText());
            Assert.Contains("连续失败 2 次", tools[1].GetText());
        }

        [Fact]
        public async Task ToolCatalogRefreshesAfterLeavingPlanMode()
        {
            var executor = new PlanExitingExecutor();
            IAgentSession session = await CreateSessionAsync(executor, new ReadWriteDefinitions());
            executor.Session = session;

            session.SetPlanMode(true);
            _api.EnqueueToolCalls(new ScriptedModelApi.ToolCallScript
            {
                Id = "call_1",
                Name = "read",
                ArgumentsJson = "{\"path\":\"a.txt\"}"
            });
            _api.EnqueueText("已退出计划模式，继续执行。");

            AgentRun run = await session.SendAsync(TextInput("先调研"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            LlmRequest[] requests = _api.Requests.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.DoesNotContain(
                requests[0].Tools ?? new List<ToolDeclaration>(),
                t => t.Name == "write");
            Assert.Contains(
                requests[1].Tools ?? new List<ToolDeclaration>(),
                t => t.Name == "write");
        }

        private async Task<IAgentSession> CreateSessionAsync(
            IToolExecutor executor,
            IToolDefinitionProvider definitions,
            ISessionGoalState goals = null,
            bool goalContinuation = false,
            int maxGoalContinuationRounds = 0,
            Func<ToolApprovalPolicy> approvalPolicyProvider = null,
            Func<bool> goalContinuationProvider = null)
        {
            _services.Tools = executor;
            _services.ToolDefinitions = definitions;
            _services.SandboxFactory = root => new WorkspacePathSandbox(root, new FileSystemPathProbe());

            return await _runtime.GetOrCreateAsync(
                new AgentSessionOptions
                {
                    SessionId = "s_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    WorkspaceId = "w1",
                    WorkspaceRoot = _workspace,
                    SystemPrompt = "你是测试助手。",
                    ProviderId = "p",
                    ModelId = "m",
                    HistoryLimit = 50,
                    AutoTitle = false,
                    MaxParallelToolCalls = 4,
                    Goals = goals,
                    GoalContinuation = goalContinuation,
                    MaxGoalContinuationRounds = maxGoalContinuationRounds,
                    ApprovalPolicyProvider = approvalPolicyProvider,
                    GoalContinuationProvider = goalContinuationProvider
                },
                CancellationToken.None);
        }

        private static IToolDefinitionProvider ReadDefinitions()
        {
            return new StaticDefinitions();
        }

        private static AgentInput TextInput(string text)
        {
            return new AgentInput
            {
                ClientMessageId = "m_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Blocks = new List<AgentContentBlock>
                {
                    new AgentContentBlock { Type = "text", Text = text }
                }
            };
        }

        private static async Task WaitAsync(Task task)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(completed, task), "等待运行超时。");
            await task;
        }

        private static async Task WaitIdleAsync(IAgentSession session)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            {
                try
                {
                    await session.WaitForIdleAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail("等待会话空闲超时。");
                }
            }
        }

        private static async Task WaitForAsync(
            Func<bool> condition,
            TimeSpan timeout,
            Func<string> diagnostics = null)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.True(condition(), "等待条件超时。" + (diagnostics == null ? string.Empty : diagnostics()));
        }

        private sealed class StaticDefinitions : IToolDefinitionProvider
        {
            public IReadOnlyList<ToolDefinition> GetDefinitions()
            {
                return new[]
                {
                    new ToolDefinition
                    {
                        Name = "read",
                        Description = "读取文件",
                        ParametersJson = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
                        PromptSnippet = "读取文件",
                        Risk = ToolRisk.Low,
                        ExecutionMode = ToolExecutionMode.Parallel
                    }
                };
            }
        }

        private sealed class ReadWriteDefinitions : IToolDefinitionProvider
        {
            public IReadOnlyList<ToolDefinition> GetDefinitions()
            {
                return new[]
                {
                    new ToolDefinition
                    {
                        Name = "read",
                        Description = "读取文件",
                        ParametersJson = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
                        PromptSnippet = "读取文件",
                        Risk = ToolRisk.Low,
                        ExecutionMode = ToolExecutionMode.Parallel
                    },
                    new ToolDefinition
                    {
                        Name = "write",
                        Description = "写入文件",
                        ParametersJson = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}},\"required\":[\"path\",\"content\"]}",
                        PromptSnippet = "写入文件",
                        Risk = ToolRisk.High
                    }
                };
            }
        }

        private sealed class PlanExitingExecutor : IToolExecutor
        {
            public IAgentSession Session { get; set; }

            public Task<ToolExecution> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                // 模拟 exit_plan_mode 获批：上一步的工具执行后退出计划模式。
                if (Session != null)
                {
                    Session.SetPlanMode(false);
                }

                return Task.FromResult(new ToolExecution
                {
                    Call = call,
                    Risk = ToolRisk.Low,
                    Decision = ToolExecutionDecision.Auto,
                    Result = ToolResult.Success(call, "读到了", call.Name, true, false)
                });
            }
        }

        private sealed class FailingExecutor : IToolExecutor
        {
            private readonly string _code;
            private readonly string _message;

            public FailingExecutor(string code, string message)
            {
                _code = code;
                _message = message;
            }

            public Task<ToolExecution> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new ToolExecution
                {
                    Call = call,
                    Risk = ToolRisk.Low,
                    Decision = ToolExecutionDecision.Auto,
                    Result = ToolResult.Failure(call, ToolResultStatus.Failed, _code, _message)
                });
            }
        }

        private sealed class TimeoutDefinitions : IToolDefinitionProvider
        {
            private readonly int _timeoutMs;

            public TimeoutDefinitions(int timeoutMs)
            {
                _timeoutMs = timeoutMs;
            }

            public IReadOnlyList<ToolDefinition> GetDefinitions()
            {
                return new[]
                {
                    new ToolDefinition
                    {
                        Name = "read",
                        Description = "读取文件",
                        ParametersJson = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
                        PromptSnippet = "读取文件",
                        Risk = ToolRisk.Low,
                        ExecutionMode = ToolExecutionMode.Parallel,
                        TimeoutMs = _timeoutMs
                    }
                };
            }
        }

        private sealed class SlowHonoringExecutor : IToolExecutor
        {
            public async Task<ToolExecution> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                await Task.Delay(5000, cancellationToken);
                return new ToolExecution
                {
                    Call = call,
                    Risk = ToolRisk.Low,
                    Decision = ToolExecutionDecision.Auto,
                    Result = ToolResult.Success(call, "太慢了", call.Name, true, false)
                };
            }
        }

        private sealed class PolicyRecordingExecutor : IToolExecutor
        {
            private readonly object _gate = new object();
            private readonly List<ToolApprovalPolicy> _policies = new List<ToolApprovalPolicy>();

            public ToolApprovalPolicy[] Policies
            {
                get
                {
                    lock (_gate)
                    {
                        return _policies.ToArray();
                    }
                }
            }

            public Task<ToolExecution> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                lock (_gate)
                {
                    _policies.Add(context.ApprovalPolicy);
                }

                return Task.FromResult(new ToolExecution
                {
                    Call = call,
                    Risk = ToolRisk.Low,
                    Decision = ToolExecutionDecision.Auto,
                    Result = ToolResult.Success(call, "读到了", call.Name, true, false)
                });
            }
        }

        private sealed class ImmediateExecutor : IToolExecutor
        {
            public Task<ToolExecution> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new ToolExecution
                {
                    Call = call,
                    Risk = ToolRisk.Low,
                    Decision = ToolExecutionDecision.Auto,
                    Result = ToolResult.Success(call, "读到了", call.Name, true, false)
                });
            }
        }

        private sealed class ConcurrentExecutor : IToolExecutor
        {
            private int _inFlight;

            public int MaxConcurrency { get; private set; }

            public async Task<ToolExecution> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                int current = Interlocked.Increment(ref _inFlight);
                if (current > MaxConcurrency)
                {
                    MaxConcurrency = current;
                }

                try
                {
                    // 第一个调用故意慢，第二个快：完成顺序与模型顺序相反。
                    await Task.Delay(call.Id == "call_a" ? 120 : 10, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }

                return new ToolExecution
                {
                    Call = call,
                    Risk = ToolRisk.Low,
                    Decision = ToolExecutionDecision.Auto,
                    Result = ToolResult.Success(call, "内容 " + call.Id, call.Name, true, false)
                };
            }
        }

        private sealed class GatedExecutor : IToolExecutor
        {
            private readonly TaskCompletionSource<object> _gate = new TaskCompletionSource<object>();
            private int _entered;

            public int Entered
            {
                get { return Volatile.Read(ref _entered); }
            }

            public void Release()
            {
                _gate.TrySetResult(null);
            }

            public async Task<ToolExecution> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _entered);
                await _gate.Task;
                cancellationToken.ThrowIfCancellationRequested();

                return new ToolExecution
                {
                    Call = call,
                    Risk = ToolRisk.Low,
                    Decision = ToolExecutionDecision.Auto,
                    Result = ToolResult.Success(call, "读到了", call.Name, true, false)
                };
            }
        }

        private sealed class BlockingExecutor : IToolExecutor
        {
            private int _entered;

            public int Entered
            {
                get { return Volatile.Read(ref _entered); }
            }

            public async Task<ToolExecution> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _entered);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            }
        }

        private sealed class CollectingSink : IAgentEventSink
        {
            private readonly object _gate = new object();
            private readonly List<AgentEvent> _events = new List<AgentEvent>();

            public IReadOnlyList<AgentEvent> Events
            {
                get
                {
                    lock (_gate)
                    {
                        return _events.ToArray();
                    }
                }
            }

            public void OnEvent(AgentEvent evt)
            {
                lock (_gate)
                {
                    _events.Add(evt);
                }
            }

            public void OnOverflow()
            {
            }
        }

        private sealed class MemoryCredentialStore : ICredentialStore
        {
            private readonly Dictionary<string, string> _keys =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public MemoryCredentialStore(string providerId, string key)
            {
                _keys[providerId] = key;
            }

            public string GetKey(string providerId)
            {
                string value;
                return _keys.TryGetValue(providerId, out value) ? value : null;
            }

            public void SetKey(string providerId, string apiKey)
            {
                _keys[providerId] = apiKey;
            }

            public void Remove(string providerId)
            {
                _keys.Remove(providerId);
            }

            public IReadOnlyDictionary<string, string> LoadAll()
            {
                return new Dictionary<string, string>(_keys, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
