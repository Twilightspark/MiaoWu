using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure;
using MiaoWu.Infrastructure.Conversations;
using MiaoWu.Infrastructure.Providers;
using Xunit;

namespace MiaoWu.Tests.Agents
{
    /// <summary>压缩的提示词、持久化与请求切点。</summary>
    public class SessionCompactorTests
    {
        private static AgentMessage User(string id, string text)
        {
            return AgentMessage.CreateUser(id, text);
        }

        private static AgentMessage Assistant(string id, string text)
        {
            AgentMessage message = AgentMessage.CreateAssistantPlaceholder(id, "p", "m");
            message.Status = AgentMessageStatus.Completed;
            message.Blocks.Add(new AgentContentBlock { Type = "text", Text = text });
            return message;
        }

        [Fact]
        public void RenderTranscript_StartsAtGivenIndex()
        {
            var messages = new List<AgentMessage>
            {
                User("u1", "旧问题"),
                Assistant("a1", "旧回答"),
                User("u2", "新问题")
            };

            string text = SessionCompactor.RenderTranscript(messages, 2);

            Assert.Contains("新问题", text);
            Assert.DoesNotContain("旧问题", text);
            Assert.DoesNotContain("旧回答", text);
        }

        [Fact]
        public void RenderTranscript_IncludesToolCallsAndResults()
        {
            var messages = new List<AgentMessage>
            {
                User("u1", "看看 README"),
                Assistant("a1", "我读一下。")
            };
            messages[1].Blocks.Add(new AgentContentBlock
            {
                Type = "toolCall",
                ToolName = "read_file",
                ToolCallId = "c1",
                ArgumentsJson = "{\"path\":\"README.md\"}"
            });
            messages.Add(AgentMessage.CreateToolResult(
                "t1",
                "c1",
                "read_file",
                "第一行 # Demo",
                false,
                false,
                null));

            string text = SessionCompactor.RenderTranscript(messages, 0);

            Assert.Contains("[调用工具 read_file]", text);
            Assert.Contains("[工具结果 read_file]", text);
            Assert.Contains("第一行 # Demo", text);
        }

        [Fact]
        public void BuildRequest_RejectsEmptyTranscript()
        {
            Assert.Null(SessionCompactor.BuildRequest(new List<AgentMessage>(), 0, "s", 512));
            Assert.Null(SessionCompactor.BuildRequest(
                new List<AgentMessage> { User("u1", string.Empty) },
                0,
                "s",
                512));
        }

        [Fact]
        public void BuildRequest_ContainsTranscriptAndOutputLimit()
        {
            var messages = new List<AgentMessage> { User("u1", "帮我把进度改到 v0.1.0") };

            LlmRequest request = SessionCompactor.BuildRequest(messages, 0, "s1", 777);

            Assert.NotNull(request);
            Assert.Equal("s1", request.SessionId);
            Assert.Equal(777, request.MaxOutputTokens);
            Assert.Single(request.Messages);
            Assert.Contains("v0.1.0", request.Messages[0].Content[0].Text);
        }

        [Fact]
        public void Normalize_StripsCodeFenceAndRejectsShortOutput()
        {
            string summary = SessionCompactor.Normalize(
                "```markdown\n## 目标\n完成输入框触发菜单。\n## 进展\n检测、菜单与 chip 已完成。\n```");

            Assert.NotNull(summary);
            Assert.DoesNotContain("```", summary);
            Assert.StartsWith("## 目标", summary);
            Assert.Null(SessionCompactor.Normalize("太短"));
        }

        [Fact]
        public void Limit_KeepsHeadAndTail()
        {
            string text = new string('a', 100) + "中间" + new string('b', 100);

            string limited = SessionCompactor.Limit(text, 60);

            Assert.True(limited.Length < text.Length);
            Assert.StartsWith("aaaa", limited);
            Assert.EndsWith("bbbb", limited);
            Assert.Contains("省略", limited);
        }
    }

    /// <summary>`/compact` 端到端：模型摘要 → 落盘 → 下一次请求只带摘要与切点之后的消息。</summary>
    public sealed class AgentCompactionTests : IDisposable
    {
        private readonly string _workspace;
        private readonly WorkspaceConversationStore _store;
        private readonly ScriptedModelApi _api;
        private readonly ModelRegistry _registry;
        private readonly AgentSessionServices _services;
        private readonly AgentRuntime _runtime;

        public AgentCompactionTests()
        {
            _workspace = Path.Combine(Path.GetTempPath(), "miaowu-compact-" + Guid.NewGuid().ToString("N"));
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
        public async Task Compact_SetsSummaryAndPublishesEvent()
        {
            IAgentSession session = await CreateSessionAsync("s1");
            var sink = new CollectingSink();
            session.Subscribe(sink);
            await SendAsync(session, "第一个问题", "第一个回答");
            await SendAsync(session, "第二个问题", "第二个回答");

            _api.EnqueueText(
                "## 目标\n验证压缩。\n## 进展\n两轮对话完成。\n## 关键决定\n无。\n"
                + "## 文件与路径\n无。\n## 待办\n无。\n## 风险与注意事项\n无。");
            AgentCompactionResult result = await session.CompactAsync(CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(4, result.CoveredMessages);
            AgentSessionSnapshot snapshot = session.Snapshot();
            Assert.NotNull(snapshot.Compaction);
            Assert.Equal(4, snapshot.Compaction.CoveredMessages);
            Assert.Equal(4, snapshot.Messages.Count);
            Assert.Contains(sink.Events, evt => evt is SessionCompactedEvent);
        }

        [Fact]
        public async Task Compact_NextRequestUsesSummaryAndOnlyNewMessages()
        {
            IAgentSession session = await CreateSessionAsync("s1");
            await SendAsync(session, "第一个问题", "第一个回答");
            await SendAsync(session, "第二个问题", "第二个回答");

            _api.EnqueueText(
                "## 目标\n验证切点。\n## 进展\n已压缩两轮。\n## 关键决定\n无。\n"
                + "## 文件与路径\n无。\n## 待办\n无。\n## 风险与注意事项\n无。");
            AgentCompactionResult result = await session.CompactAsync(CancellationToken.None);
            Assert.True(result.Succeeded, result.Error);

            _api.EnqueueText("第三个回答");
            await SendAsync(session, "第三个问题", "第三个回答");

            LlmRequest request = _api.LastRequest;
            Assert.NotNull(request);
            Assert.Contains("会话摘要", request.SystemPrompt);
            Assert.Contains("验证切点", request.SystemPrompt);
            Assert.Single(request.Messages);
            Assert.Equal("第三个问题", request.Messages[0].Content[0].Text);
        }

        [Fact]
        public async Task Compact_PersistsAcrossReload()
        {
            IAgentSession session = await CreateSessionAsync("s1");
            await SendAsync(session, "第一个问题", "第一个回答");
            await SendAsync(session, "第二个问题", "第二个回答");

            _api.EnqueueText(
                "## 目标\n重启后仍生效。\n## 进展\n已写盘。\n## 关键决定\n无。\n"
                + "## 文件与路径\n无。\n## 待办\n无。\n## 风险与注意事项\n无。");
            Assert.True((await session.CompactAsync(CancellationToken.None)).Succeeded);

            await _runtime.CloseAsync("s1", CancellationToken.None);
            IAgentSession reloaded = await CreateSessionAsync("s1");

            AgentSessionSnapshot snapshot = reloaded.Snapshot();
            Assert.NotNull(snapshot.Compaction);
            Assert.Equal(4, snapshot.Compaction.CoveredMessages);
            Assert.Contains("重启后仍生效", snapshot.Compaction.Summary);
        }

        [Fact]
        public async Task Compact_WithoutMessagesReturnsReadableFailure()
        {
            IAgentSession session = await CreateSessionAsync("s-empty");

            AgentCompactionResult result = await session.CompactAsync(CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        private async Task<IAgentSession> CreateSessionAsync(string id)
        {
            return await _runtime.GetOrCreateAsync(Options(id), CancellationToken.None);
        }

        private static AgentSessionOptions Options(string id)
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
                AutoTitle = false
            };
        }

        private static AgentInput TextInput(string text)
        {
            return new AgentInput
            {
                ClientMessageId = "c_" + Guid.NewGuid().ToString("N"),
                Blocks = new List<AgentContentBlock>
                {
                    new AgentContentBlock { Type = "text", Text = text }
                }
            };
        }

        private async Task SendAsync(IAgentSession session, string question, string answer)
        {
            _api.EnqueueText(answer);
            AgentRun run = await session.SendAsync(TextInput(question), AgentSendMode.Queue, CancellationToken.None);
            await run.Completion;
            await WaitIdleAsync(session);
        }

        private static async Task WaitIdleAsync(IAgentSession session)
        {
            for (int i = 0; i < 200; i++)
            {
                if (session.Snapshot().State == AgentSessionState.Idle)
                {
                    return;
                }

                await Task.Delay(10);
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
