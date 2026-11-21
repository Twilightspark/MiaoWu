using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure;
using MiaoWu.Infrastructure.Conversations;
using MiaoWu.Infrastructure.Providers;
using Xunit;

namespace MiaoWu.Tests.Agents
{
    /// <summary>三条读数的纯投影：轮 / 步、token 分桶与上下文百分比。</summary>
    public sealed class SessionMeterTests
    {
        [Fact]
        public void StatsFromMessages_CountsTurnsStepsAndModelTime()
        {
            AgentMessage user = AgentMessage.CreateUser("u1", "问题");
            AgentMessage assistant = AgentMessage.CreateAssistantPlaceholder("a1", "p", "m");
            assistant.Status = AgentMessageStatus.Completed;
            assistant.CreatedAt = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
            assistant.CompletedAt = assistant.CreatedAt.AddMilliseconds(1500);
            assistant.Blocks.Add(new AgentContentBlock { Type = "text", Text = "回答" });

            SessionStats stats = SessionStats.FromMessages(new[] { user, assistant });

            Assert.Equal(1, stats.Turns);
            Assert.Equal(1, stats.Steps);
            Assert.Equal(1500, stats.LlmMs);
            Assert.True(stats.HasData);
        }

        [Fact]
        public void TokenUsageFromMessages_SumsBucketsAndCacheHit()
        {
            AgentMessage first = AgentMessage.CreateAssistantPlaceholder("a1", "p", "m");
            first.Usage = new TokenUsage
            {
                Input = 100,
                CacheRead = 60,
                CacheWrite = 20,
                Output = 40
            };

            AgentMessage second = AgentMessage.CreateAssistantPlaceholder("a2", "p", "m");
            second.Usage = new TokenUsage
            {
                Input = 50,
                Output = 10
            };

            SessionTokenUsage usage = SessionTokenUsage.FromMessages(new[] { first, second });

            Assert.Equal(150, usage.UncachedInput);
            Assert.Equal(60, usage.CacheRead);
            Assert.Equal(20, usage.CacheWrite);
            Assert.Equal(50, usage.Output);
            Assert.Equal(280, usage.Total);
            Assert.True(usage.CacheHitRate > 0.26 && usage.CacheHitRate < 0.27);
        }

        [Fact]
        public void ContextUsage_ClampsPercentAndHidesWithoutWindow()
        {
            var context = new SessionContextUsage
            {
                UsedTokens = 5000,
                WindowTokens = 8192
            };

            Assert.Equal(61, context.Percent);
            Assert.True(context.HasData);

            var unknown = new SessionContextUsage { UsedTokens = 5000 };
            Assert.False(unknown.HasData);
            Assert.Equal(0, unknown.Percent);
        }

        [Fact]
        public void Stats_ComputeAverageTtftAndOutputSpeed()
        {
            var stats = new SessionStats
            {
                TtftMs = 2400,
                TtftSteps = 2,
                DecodeMs = 2000,
                DecodeTokens = 100
            };

            Assert.Equal(1200, stats.AverageTtftMs);
            Assert.Equal(50.0, stats.TokensPerSecond, 1);
        }
    }

    /// <summary>真实会话：发送一轮后三条读数出现，重启后统计与 token 仍在。</summary>
    public sealed class AgentSessionMeterTests : IDisposable
    {
        private readonly string _workspace;
        private readonly WorkspaceConversationStore _store;
        private readonly ScriptedModelApi _api;
        private readonly ModelRegistry _registry;
        private readonly AgentSessionServices _services;
        private readonly AgentRuntime _runtime;

        public AgentSessionMeterTests()
        {
            _workspace = Path.Combine(Path.GetTempPath(), "miaowu-meter-" + Guid.NewGuid().ToString("N"));
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
        public async Task Send_PublishesMeterAndSnapshotCarriesIt()
        {
            IAgentSession session = await CreateSessionAsync("s1");
            var sink = new CollectingSink();
            session.Subscribe(sink);

            await SendAsync(session, "第一个问题", "第一个回答");

            AgentSessionSnapshot snapshot = session.Snapshot();
            Assert.NotNull(snapshot.Meter);
            Assert.Equal(1, snapshot.Meter.Stats.Turns);
            Assert.Equal(1, snapshot.Meter.Stats.Steps);
            Assert.True(snapshot.Meter.Stats.LlmMs >= 0);
            Assert.Equal(20, snapshot.Meter.Usage.Total);
            Assert.Equal(12, snapshot.Meter.Usage.UncachedInput);
            Assert.Equal(8, snapshot.Meter.Usage.Output);
            Assert.Equal(8192, snapshot.Meter.Context.WindowTokens);
            Assert.True(snapshot.Meter.Context.UsedTokens > 0);
            Assert.Equal(snapshot.Meter.Context.UsedTokens, snapshot.EstimatedContextTokens);

            UsageChangedEvent changed = sink.Events.OfType<UsageChangedEvent>()
                .LastOrDefault(evt => evt.Meter != null);
            Assert.NotNull(changed);
            Assert.Equal(20, changed.Meter.Usage.Total);
        }

        [Fact]
        public async Task Meter_SurvivesReload()
        {
            IAgentSession session = await CreateSessionAsync("s1");
            await SendAsync(session, "第一个问题", "第一个回答");

            await _runtime.CloseAsync("s1", CancellationToken.None);
            IAgentSession reloaded = await CreateSessionAsync("s1");

            AgentSessionSnapshot snapshot = reloaded.Snapshot();
            Assert.Equal(1, snapshot.Meter.Stats.Turns);
            Assert.Equal(1, snapshot.Meter.Stats.Steps);
            Assert.Equal(20, snapshot.Meter.Usage.Total);
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
