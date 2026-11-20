using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>
    /// 模型是全局配置：任一会话切换后，其它会话的快照与下一次运行都跟着变。
    /// </summary>
    public sealed class SharedModelSelectionTests : IDisposable
    {
        private readonly string _workspace;
        private readonly WorkspaceConversationStore _store;
        private readonly ScriptedModelApi _api;
        private readonly ModelSelection _selection;
        private readonly AgentRuntime _runtime;

        public SharedModelSelectionTests()
        {
            _workspace = Path.Combine(Path.GetTempPath(), "miaowu-shared-model-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);

            _store = new WorkspaceConversationStore("w1", _workspace, SystemClock.Instance);
            _api = new ScriptedModelApi();

            var credentials = new TestCredentialStore();
            credentials.SetKey("p", "test-key");
            credentials.SetKey("q", "test-key");

            var registry = new ModelRegistry(
                BuildCatalog(),
                credentials,
                new IModelApi[] { _api });

            _selection = new ModelSelection();
            _selection.Set("p", "m");

            var services = new AgentSessionServices
            {
                Models = registry,
                Store = _store,
                Logger = NullAgentLogger.Instance,
                Clock = SystemClock.Instance,
                ErrorClassifier = new AgentErrorClassifier(),
                ModelSelection = _selection
            };

            _runtime = new AgentRuntime(services, new AgentRuntimeOptions());
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
        public async Task SwitchingInOneSessionChangesAllSessions()
        {
            _api.EnqueueText("A 的回答");
            _api.EnqueueText("B 的回答");

            IAgentSession first = await _runtime.GetOrCreateAsync(Options("s_a", "p", "m"), CancellationToken.None);
            IAgentSession second = await _runtime.GetOrCreateAsync(Options("s_b", "p", "m"), CancellationToken.None);

            AgentRun runA = await first.SendAsync(
                TextInput("A 的问题"),
                AgentSendMode.Queue,
                CancellationToken.None);
            await WaitAsync(runA.Completion);
            await WaitIdleAsync(first);
            Assert.Equal("m", _api.LastModel.ModelId);

            // 在第一条会话里换模型：共享的是同一份配置，第二条会话立刻跟上。
            await first.SetModelAsync("q", "n", CancellationToken.None);

            Assert.Equal("q", _selection.ProviderId);
            Assert.Equal("n", _selection.ModelId);
            Assert.Equal("q", second.Snapshot().ProviderId);
            Assert.Equal("n", second.Snapshot().ModelId);

            AgentRun runB = await second.SendAsync(
                TextInput("B 的问题"),
                AgentSendMode.Queue,
                CancellationToken.None);
            await WaitAsync(runB.Completion);
            await WaitIdleAsync(second);

            // 第二条会话的下一次运行用的是切换后的模型。
            Assert.Equal("n", _api.LastModel.ModelId);
            Assert.Equal("q", _api.LastModel.ProviderId);
        }

        [Fact]
        public async Task SessionCreatedAfterSwitchStartsOnTheSharedModel()
        {
            IAgentSession first = await _runtime.GetOrCreateAsync(Options("s_first", "p", "m"), CancellationToken.None);
            await first.SetModelAsync("q", "n", CancellationToken.None);

            // 新会话的 options 里还写着旧的 p/m，但共享配置优先。
            IAgentSession later = await _runtime.GetOrCreateAsync(Options("s_later", "p", "m"), CancellationToken.None);

            AgentSessionSnapshot snapshot = later.Snapshot();
            Assert.Equal("q", snapshot.ProviderId);
            Assert.Equal("n", snapshot.ModelId);
        }

        private static AgentSessionOptions Options(string id, string providerId, string modelId)
        {
            return new AgentSessionOptions
            {
                SessionId = id,
                WorkspaceId = "w1",
                WorkspaceRoot = Path.GetTempPath(),
                SystemPrompt = "你是测试助手。",
                ProviderId = providerId,
                ModelId = modelId,
                HistoryLimit = 50,
                AutoTitle = false
            };
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

        private static ProviderCatalog BuildCatalog()
        {
            var catalog = new ProviderCatalog { ActiveProviderId = "p" };
            catalog.Providers.Add(new ProviderDefinition
            {
                Id = "p",
                DisplayName = "脚本一",
                Api = "scripted",
                BaseUrl = "https://example.test"
            });
            catalog.Providers[0].Models.Add(new ModelDefinition
            {
                Id = "m",
                Name = "脚本一",
                ContextWindow = 8192,
                MaxOutputTokens = 1024
            });
            catalog.Providers.Add(new ProviderDefinition
            {
                Id = "q",
                DisplayName = "脚本二",
                Api = "scripted",
                BaseUrl = "https://example.test"
            });
            catalog.Providers[1].Models.Add(new ModelDefinition
            {
                Id = "n",
                Name = "脚本二",
                ContextWindow = 8192,
                MaxOutputTokens = 1024
            });
            return catalog;
        }
    }
}
