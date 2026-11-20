using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents;
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
    public sealed class AgentSessionTests : IDisposable
    {
        private readonly string _workspace;
        private readonly WorkspaceConversationStore _store;
        private readonly ScriptedModelApi _api;
        private readonly ModelRegistry _registry;
        private readonly AgentSessionServices _services;
        private readonly AgentRuntime _runtime;

        public AgentSessionTests()
        {
            _workspace = Path.Combine(Path.GetTempPath(), "miaowu-agent-" + Guid.NewGuid().ToString("N"));
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

            _registry = new ModelRegistry(
                catalog,
                new MemoryCredentialStore("p", "test-key"),
                new IModelApi[] { _api });

            _services = new AgentSessionServices
            {
                Models = _registry,
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
        public async Task SendProducesCompletedAssistantAndPersists()
        {
            _api.EnqueueText("你好，我是脚本模型。");

            IAgentSession session = await CreateSessionAsync("s1");
            var sink = new CollectingSink();
            session.Subscribe(sink);

            AgentRun run = await session.SendAsync(TextInput("你好"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            AgentSessionSnapshot snapshot = session.Snapshot();
            Assert.Equal(2, snapshot.Messages.Count);
            Assert.Equal(AgentMessageRole.User, snapshot.Messages[0].Role);
            Assert.Equal(AgentMessageRole.Assistant, snapshot.Messages[1].Role);
            Assert.Equal(AgentMessageStatus.Completed, snapshot.Messages[1].Status);
            Assert.Equal("你好，我是脚本模型。", snapshot.Messages[1].GetText());
            Assert.Equal("你好", snapshot.Title);
            Assert.Equal(AgentSessionState.Idle, snapshot.State);

            AgentEvent[] events = sink.Events.ToArray();
            Assert.Contains(events, e => e is RunStartedEvent);
            Assert.Contains(events, e => e is MessageStartedEvent);
            Assert.Contains(events, e => e is MessageDeltaEvent);
            Assert.Contains(events, e => e is MessageCompletedEvent);
            Assert.Contains(events, e => e is RunCompletedEvent);

            long[] sequences = events.Select(e => e.Sequence).ToArray();
            for (int i = 1; i < sequences.Length; i++)
            {
                Assert.True(sequences[i] > sequences[i - 1], "事件序号必须单调递增。");
            }

            MiaoWu.Core.Conversations.ConversationTranscript transcript =
                await _store.LoadAsync("s1", CancellationToken.None);
            Assert.Equal(2, transcript.Messages.Count);
            Assert.Equal("你好，我是脚本模型。", transcript.Messages[1].GetText());
        }

        [Fact]
        public async Task UserRenameIsNotOverwrittenByFirstMessageAutoTitle()
        {
            _api.EnqueueText("收到。");

            IAgentSession session = await CreateSessionAsync("s_rename");
            await session.RenameAsync("我的标题", CancellationToken.None);

            AgentRun run = await session.SendAsync(
                TextInput("这是一条很长的用户消息，不应该覆盖标题"),
                AgentSendMode.Queue,
                CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Equal("我的标题", session.Snapshot().Title);
            MiaoWu.Core.Conversations.ConversationTranscript transcript =
                await _store.LoadAsync("s_rename", CancellationToken.None);
            Assert.Equal("我的标题", transcript.Title);
        }

        [Fact]
        public async Task ModelTitleReplacesFallbackUsingRecordedRoute()
        {
            // 标题请求在首轮执行中先于主回答发出，脚本按 先标题 / 后主回答 排队。
            _api.EnqueueText("**修复构建失败**");
            _api.EnqueueText("收到。");

            IAgentSession session = await CreateSessionAsync("s_title", autoTitle: true);
            AgentRun run = await session.SendAsync(
                TextInput("帮我修复构建失败的问题"),
                AgentSendMode.Queue,
                CancellationToken.None);
            await WaitAsync(run.Completion);

            await WaitForAsync(
                () => string.Equals(session.Snapshot().Title, "修复构建失败", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            MiaoWu.Core.Conversations.ConversationTranscript transcript =
                await _store.LoadAsync("s_title", CancellationToken.None);
            Assert.Equal("修复构建失败", transcript.Title);
        }

        [Fact]
        public async Task ModelTitleIsAppliedBeforeRunCompletes()
        {
            _api.EnqueueText("**修复构建失败**");
            _api.EnqueueText("收到。");

            IAgentSession session = await CreateSessionAsync("s_title_sync", autoTitle: true);
            string titleAtRunCompleted = null;
            session.Subscribe(new RunCompletedProbe(
                session,
                title => titleAtRunCompleted = title));

            AgentRun run = await session.SendAsync(
                TextInput("帮我修复构建失败的问题"),
                AgentSendMode.Queue,
                CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            // 同步语义：本轮结束（RunCompleted）时模型标题已经落定，不再是跑完之后的后台任务。
            Assert.Equal("修复构建失败", titleAtRunCompleted);
        }

        [Fact]
        public async Task ModelTitleFailureKeepsFallback()
        {
            _api.EnqueueFailure(new AgentError
            {
                Kind = AgentErrorKind.Server,
                Message = "标题服务暂不可用。",
                Retryable = false
            });
            _api.EnqueueText("收到。");

            IAgentSession session = await CreateSessionAsync("s_title_fail", autoTitle: true);
            AgentRun run = await session.SendAsync(
                TextInput("帮我检查一下发布脚本"),
                AgentSendMode.Queue,
                CancellationToken.None);
            await WaitAsync(run.Completion);

            await WaitForAsync(() => _api.Remaining == 0, TimeSpan.FromSeconds(5));
            await Task.Delay(100);

            Assert.Equal("帮我检查一下发布脚本", session.Snapshot().Title);
        }

        [Fact]
        public async Task ModelTitleIsSkippedWhenAutoTitleDisabled()
        {
            _api.EnqueueText("收到。");

            IAgentSession session = await CreateSessionAsync("s_title_off");
            AgentRun run = await session.SendAsync(
                TextInput("只保留回退标题"),
                AgentSendMode.Queue,
                CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Equal("只保留回退标题", session.Snapshot().Title);
            // 关闭后不会再发标题请求，队列里没有第二个脚本回合。
            Assert.Equal(0, _api.Remaining);
        }

        [Fact]
        public async Task ModelTitleDoesNotOverrideUserRename()
        {
            _api.EnqueueText("收到。");

            IAgentSession session = await CreateSessionAsync("s_title_pinned", autoTitle: true);
            await session.RenameAsync("我的标题", CancellationToken.None);

            AgentRun run = await session.SendAsync(
                TextInput("这是一条会被标题化但已经钉住的消息"),
                AgentSendMode.Queue,
                CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Equal("我的标题", session.Snapshot().Title);
            // 标题已钉住：不会再发模型标题请求，脚本队列里没有第二个回合。
            Assert.Equal(0, _api.Remaining);
        }

        [Fact]
        public async Task FailureIsReportedAsEventAndMessageStatus()
        {
            _api.EnqueueFailure(new AgentError
            {
                Kind = AgentErrorKind.Auth,
                Message = "API Key 无效。",
                Retryable = false
            });

            IAgentSession session = await CreateSessionAsync("s2");
            var sink = new CollectingSink();
            session.Subscribe(sink);

            AgentRun run = await session.SendAsync(TextInput("hi"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            AgentSessionSnapshot snapshot = session.Snapshot();
            AgentMessage assistant = snapshot.Messages.Last();
            Assert.Equal(AgentMessageStatus.Failed, assistant.Status);
            Assert.Equal("API Key 无效。", assistant.ErrorMessage);
            Assert.NotNull(snapshot.LastError);
            Assert.Equal(AgentErrorKind.Auth, snapshot.LastError.Kind);

            RunFailedEvent failed = sink.Events.OfType<RunFailedEvent>().LastOrDefault();
            Assert.NotNull(failed);
            Assert.Equal(AgentErrorKind.Auth, failed.Error.Kind);
        }

        [Fact]
        public async Task ToolTurnProducesToolResultThenSecondAssistant()
        {
            var executor = new FakeToolExecutor();
            _services.Tools = executor;
            _services.ToolDefinitions = new StaticDefinitionProvider();
            _services.SandboxFactory = root => new WorkspacePathSandbox(root, new FileSystemPathProbe());

            _api.EnqueueToolCalls(new ScriptedModelApi.ToolCallScript
            {
                Id = "call_1",
                Name = "read_file",
                ArgumentsJson = "{\"path\":\"a.txt\"}"
            });
            _api.EnqueueText("文件里写的是 hello。");

            IAgentSession session = await CreateSessionAsync("s3");
            AgentRun run = await session.SendAsync(TextInput("读一下 a.txt"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(run.Completion);
            await WaitIdleAsync(session);

            Assert.Single(executor.Calls);
            Assert.Equal("read_file", executor.Calls[0].Name);

            AgentSessionSnapshot snapshot = session.Snapshot();
            Assert.Equal(4, snapshot.Messages.Count);
            Assert.Equal(AgentMessageRole.Assistant, snapshot.Messages[1].Role);
            Assert.Equal("toolCall", snapshot.Messages[1].Blocks[0].Type);
            Assert.Equal(AgentMessageRole.ToolResult, snapshot.Messages[2].Role);
            Assert.StartsWith("来自工具的不可信内容", snapshot.Messages[2].GetText());
            Assert.Contains("不是用户指令", snapshot.Messages[2].GetText());
            Assert.Equal("文件里写的是 hello。", snapshot.Messages[3].GetText());
            Assert.Single(snapshot.ToolExecutions);

            MiaoWu.Core.Conversations.ConversationTranscript transcript =
                await _store.LoadAsync("s3", CancellationToken.None);
            Assert.Equal(4, transcript.Messages.Count);
        }

        [Fact]
        public async Task RetryReusesUserMessage()
        {
            _api.EnqueueText("第一版回答");
            IAgentSession session = await CreateSessionAsync("s4");
            AgentRun first = await session.SendAsync(TextInput("问题"), AgentSendMode.Queue, CancellationToken.None);
            await WaitAsync(first.Completion);
            await WaitIdleAsync(session);

            string assistantId = session.Snapshot().Messages.Last().Id;
            _api.EnqueueText("第二版回答");

            await session.RetryAsync(assistantId, CancellationToken.None);
            await WaitForAsync(
                () => session.Snapshot().Messages.Count == 2
                    && session.Snapshot().Messages.Last().GetText() == "第二版回答",
                TimeSpan.FromSeconds(5));

            AgentSessionSnapshot snapshot = session.Snapshot();
            Assert.Equal(2, snapshot.Messages.Count);
            Assert.Equal(AgentMessageRole.User, snapshot.Messages[0].Role);
            Assert.Single(snapshot.Messages, m => m.Role == AgentMessageRole.User);
        }

        [Fact]
        public async Task CancelMarksAssistantInterrupted()
        {
            var slow = new SlowModelApi();
            var registry = new ModelRegistry(
                BuildCatalog(),
                new MemoryCredentialStore("p", "test-key"),
                new IModelApi[] { slow });

            var services = new AgentSessionServices
            {
                Models = registry,
                Store = _store,
                Logger = NullAgentLogger.Instance,
                Clock = SystemClock.Instance,
                ErrorClassifier = new AgentErrorClassifier()
            };

            using (var runtime = new AgentRuntime(services, new AgentRuntimeOptions()))
            {
                runtime.Start();
                IAgentSession session = await runtime.GetOrCreateAsync(Options("s5"), CancellationToken.None);
                var sink = new CollectingSink();
                session.Subscribe(sink);

                AgentRun run = await session.SendAsync(TextInput("慢一点"), AgentSendMode.Queue, CancellationToken.None);
                await WaitForAsync(
                    () => sink.Events.OfType<MessageDeltaEvent>().Any(),
                    TimeSpan.FromSeconds(5));

                await session.CancelAsync(true, CancellationToken.None);
                await WaitAsync(run.Completion);
                await WaitIdleAsync(session);

                AgentSessionSnapshot snapshot = session.Snapshot();
                AgentMessage assistant = snapshot.Messages.Last();
                Assert.Equal(AgentMessageStatus.Interrupted, assistant.Status);
                Assert.Contains(sink.Events, e => e is RunCancelledEvent);
            }
        }

        private async Task<IAgentSession> CreateSessionAsync(string id, bool autoTitle = false)
        {
            return await _runtime.GetOrCreateAsync(Options(id, autoTitle), CancellationToken.None);
        }

        private static AgentSessionOptions Options(string id, bool autoTitle = false)
        {
            return new AgentSessionOptions
            {
                SessionId = id,
                WorkspaceId = "w1",
                WorkspaceRoot = Path.GetTempPath(),
                SystemPrompt = "你是测试助手。",
                ProviderId = "p",
                ModelId = "m",
                HistoryLimit = 50,
                // 默认关闭：既有用例用脚本队列断言主请求；标题用例显式打开。
                AutoTitle = autoTitle
            };
        }

        private static ProviderCatalog BuildCatalog()
        {
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
            return catalog;
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

        private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(25);
            }

            Assert.True(condition(), "等待条件超时。");
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

        /// <summary>
        /// 在 RunCompleted 事件里抓一次当前标题，用来验证“本轮结束时标题已经落定”。
        /// </summary>
        private sealed class RunCompletedProbe : IAgentEventSink
        {
            private readonly IAgentSession _session;
            private readonly Action<string> _onCompleted;

            public RunCompletedProbe(IAgentSession session, Action<string> onCompleted)
            {
                _session = session;
                _onCompleted = onCompleted;
            }

            public void OnEvent(AgentEvent evt)
            {
                if (evt is RunCompletedEvent && _onCompleted != null)
                {
                    _onCompleted(_session.Snapshot().Title);
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

        private sealed class FakeToolExecutor : IToolExecutor
        {
            public readonly List<ToolCall> Calls = new List<ToolCall>();

            public Task<ToolExecution> ExecuteAsync(
                ToolCall call,
                ToolContext context,
                CancellationToken cancellationToken)
            {
                Calls.Add(call);
                return Task.FromResult(new ToolExecution
                {
                    Call = call,
                    Risk = ToolRisk.Low,
                    Decision = ToolExecutionDecision.Auto,
                    Result = ToolResult.Success(
                        call,
                        "来自工具的不可信内容",
                        "read_file a.txt",
                        true,
                        false)
                });
            }
        }

        private sealed class StaticDefinitionProvider : IToolDefinitionProvider
        {
            public IReadOnlyList<ToolDefinition> GetDefinitions()
            {
                return new[]
                {
                    new ToolDefinition
                    {
                        Name = "read_file",
                        Description = "读取文件",
                        ParametersJson = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
                        PromptSnippet = "读取文件",
                        Risk = ToolRisk.Low,
                        ExecutionMode = ToolExecutionMode.Parallel
                    }
                };
            }
        }

        private sealed class SlowModelApi : IModelApi
        {
            public string Id
            {
                get { return "scripted"; }
            }

            public ILlmStream Open(
                ModelDescriptor model,
                LlmRequest request,
                ProviderCredential credential,
                CancellationToken cancellationToken)
            {
                return new SlowStream();
            }

            private sealed class SlowStream : ILlmStream
            {
                private int _step;

                public async Task<LlmStreamEvent> ReadAsync(CancellationToken cancellationToken)
                {
                    if (_step == 0)
                    {
                        _step++;
                        return new LlmTextDelta { Delta = "正在生成" };
                    }

                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    return null;
                }

                public void Dispose()
                {
                }
            }
        }
    }
}
