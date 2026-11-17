using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Models;
using MiaoWu.Core.Settings;
using MiaoWu.Core.Workspaces;
using MiaoWu.Infrastructure;
using MiaoWu.Infrastructure.Auditing;
using MiaoWu.Infrastructure.Conversations;
using MiaoWu.Infrastructure.Exports;
using MiaoWu.Infrastructure.Files;
using MiaoWu.Infrastructure.Logging;
using MiaoWu.Infrastructure.Providers;
using MiaoWu.Infrastructure.Sessions;
using MiaoWu.Infrastructure.Storage;
using MiaoWu.Infrastructure.Tools;
using MiaoWu.Infrastructure.Workspaces;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 应用组合根的共享服务：设置、Provider、工具、运行时、日志、导出。
    /// MainWindow 只通过这里拿依赖，不自己 new 基础设施对象。
    /// </summary>
    public sealed class AppServices : IDisposable
    {
        private readonly object _gate = new object();
        private readonly ModelCatalogClient _modelCatalog;
        private bool _disposed;

        public AppServices(IAppPaths paths)
            : this(paths, null)
        {
        }

        /// <summary>
        /// apis 仅供离屏快照/集成测试注入脚本 Provider；生产路径传 null，使用两种真实协议。
        /// modelCatalog 同样只供离屏探针注入，生产路径传 null，按当前 transport 新建。
        /// </summary>
        public AppServices(IAppPaths paths, IModelApi[] apis, ModelCatalogClient modelCatalog = null)
        {
            Paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _modelCatalog = modelCatalog;

            SettingsStore = new SettingsStore(paths);
            if (!File.Exists(paths.SettingsFilePath))
            {
                // 首次启动：在 .miaowu 下初始化 config.json，并写入 firstLaunch=true。
                SettingsStore.Save(new AppSettings { FirstLaunch = true });
            }

            Settings = UiSettingsPolicy.Normalize(
                SettingsStore.Load() ?? new AppSettings(),
                new UiLimits());
            Settings.Agent = AgentSettings.Normalize(Settings.Agent);

            WorkspaceStore = new WorkspaceStore(paths);
            SessionCatalog = new WorkspaceConversationCatalog();
            FileCatalog = new FileSystemCatalog();
            PreviewService = new FilePreviewService();

            Logger = new RollingFileLogger(paths.LogsDirectory);
            ProviderConfigStore = new ProviderConfigStore(paths.ProvidersFilePath);
            Credentials = new DpapiCredentialStore(paths.SecretsFilePath);

            Http = new HttpTransport(new HttpTransportOptions
            {
                ProxyMode = Settings.Agent.ProxyMode,
                ProxyUrl = Settings.Agent.ProxyUrl,
                ConnectTimeoutSeconds = 20,
                UserAgent = "MiaoWu/" + MiaoWu.Core.AppInfo.Version
            });

            Models = new ModelRegistry(
                ProviderConfigStore.Load(),
                Credentials,
                apis != null && apis.Length > 0
                    ? apis
                    : new IModelApi[]
                    {
                        new OpenAiCompletionsApi(Http),
                        new AnthropicMessagesApi(Http)
                    });

            ApplyDefaultModelFromSettings();

            Tools = BuiltInToolset.CreateRegistry(paths, SystemClock.Instance);
            Approvals = new ToolApprovalService();
            AuditWriter = new ToolAuditLogWriter(paths.LogsDirectory);
            AuditLogs = new ToolAuditLogService(paths.LogsDirectory, paths.ExportsDirectory);
            Dispatcher = new AgentToolDispatcher(Tools, Approvals, AuditWriter, SystemClock.Instance);
            Executor = new AgentToolDispatcherAdapter(Dispatcher);
            ToolDefinitions = new BuiltInToolDefinitionProvider(Tools);

            SessionServices = new AgentSessionServices
            {
                Models = Models,
                Store = null,
                Logger = Logger,
                Tools = Executor,
                ToolDefinitions = ToolDefinitions,
                ErrorClassifier = new AgentErrorClassifier(),
                Clock = SystemClock.Instance,
                SandboxFactory = root => new WorkspacePathSandbox(root, new FileSystemPathProbe())
            };

            Runtime = new AgentRuntime(SessionServices, new AgentRuntimeOptions
            {
                MaxConcurrentRequests = Settings.Agent.MaxConcurrentRequests,
                MaxHotSessions = Settings.Agent.MaxHotSessions
            });

            Logs = new LogFileService(paths.LogsDirectory, paths.ExportsDirectory, BuildDiagnostics);
            Exporter = new ConversationExporter(paths.ExportsDirectory);

            Logger.Info("app.start", "服务初始化完成，Provider " + Models.Providers.Count
                + "，模型 " + Models.Models.Count + "。");
        }

        public IAppPaths Paths { get; }

        public AppSettings Settings { get; private set; }

        public SettingsStore SettingsStore { get; }

        public WorkspaceStore WorkspaceStore { get; }

        public WorkspaceConversationCatalog SessionCatalog { get; }

        public FileSystemCatalog FileCatalog { get; }

        public FilePreviewService PreviewService { get; }

        public RollingFileLogger Logger { get; }

        public ProviderConfigStore ProviderConfigStore { get; }

        public DpapiCredentialStore Credentials { get; }

        public HttpTransport Http { get; private set; }

        public ModelRegistry Models { get; private set; }

        public ToolRegistry Tools { get; }

        public ToolApprovalService Approvals { get; }

        public ToolAuditLogWriter AuditWriter { get; }

        public ToolAuditLogService AuditLogs { get; }

        public AgentToolDispatcher Dispatcher { get; }

        public AgentToolDispatcherAdapter Executor { get; }

        public BuiltInToolDefinitionProvider ToolDefinitions { get; }

        public AgentSessionServices SessionServices { get; }

        public AgentRuntime Runtime { get; }

        public LogFileService Logs { get; }

        public ConversationExporter Exporter { get; }

        /// <summary>Provider 或模型目录变化；设置页保存后触发，UI 刷新模型胶囊。</summary>
        public event EventHandler ProvidersChanged;

        /// <summary>
        /// 读取供应商 /models 列表。每次都用当前 <see cref="Http"/>，代理切换后不会拿到旧连接。
        /// 网络与 HTTP 错误以异常抛出，由设置页转成表单提示。
        /// </summary>
        public Task<IReadOnlyList<ModelCatalogEntry>> ListModelsAsync(
            string baseUrl,
            string api,
            string apiKey,
            System.Threading.CancellationToken cancellationToken)
        {
            ModelCatalogClient client = _modelCatalog ?? new ModelCatalogClient(Http);
            return client.ListAsync(baseUrl, api, apiKey, cancellationToken);
        }

        /// <summary>把当前设置写回 config.json。异常向上抛，由调用方提示。</summary>
        public void SaveSettings()
        {
            Settings.Agent = AgentSettings.Normalize(Settings.Agent);
            SettingsStore.Save(Settings);
        }

        /// <summary>保存 Provider 配置并热切换注册表。校验失败返回错误列表，不写盘。</summary>
        public IReadOnlyList<string> SaveProviders(ProviderCatalog catalog)
        {
            IReadOnlyList<string> errors = ProviderConfigValidator.Validate(catalog);
            if (errors.Count > 0)
            {
                return errors;
            }

            ProviderConfigStore.Save(catalog);
            RebuildHttpTransport();

            ModelDescriptor fallback = Models.GetDefault();
            Settings.Agent.DefaultProviderId = fallback == null ? null : fallback.ProviderId;
            Settings.Agent.DefaultModelId = fallback == null ? null : fallback.ModelId;
            SaveSettings();

            Logger.Info("providers.saved", "Provider 配置已更新：" + catalog.Providers.Count + " 个。");

            EventHandler handler = ProvidersChanged;
            if (handler != null)
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                }
            }

            return errors;
        }

        public void RebuildHttpTransport()
        {
            // HttpClient 的代理设置在构造时确定；代理变化后重建 transport 与注册表。
            // 旧 transport 延迟释放，避免打断正在流式生成的请求。
            HttpTransport previous = Http;
            Http = new HttpTransport(new HttpTransportOptions
            {
                ProxyMode = Settings.Agent.ProxyMode,
                ProxyUrl = Settings.Agent.ProxyUrl,
                ConnectTimeoutSeconds = 20,
                UserAgent = "MiaoWu/" + MiaoWu.Core.AppInfo.Version
            });

            Models = new ModelRegistry(
                ProviderConfigStore.Load(),
                Credentials,
                new IModelApi[]
                {
                    new OpenAiCompletionsApi(Http),
                    new AnthropicMessagesApi(Http)
                });

            SessionServices.Models = Models;

            if (previous != null)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
                    try
                    {
                        previous.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                });
            }
        }

        public WorkspaceConversationStore GetStore(Workspace workspace)
        {
            return SessionCatalog.GetStore(workspace);
        }

        public AgentSessionOptions CreateSessionOptions(
            Workspace workspace,
            string sessionId,
            string providerId,
            string modelId)
        {
            return new AgentSessionOptions
            {
                SessionId = sessionId,
                WorkspaceId = workspace.Id,
                WorkspaceRoot = workspace.Path,
                Store = GetStore(workspace),
                SystemPrompt = Settings.Agent.SystemPrompt,
                ProviderId = providerId ?? Settings.Agent.DefaultProviderId,
                ModelId = modelId ?? Settings.Agent.DefaultModelId,
                Temperature = Settings.Agent.Temperature,
                MaxOutputTokens = Settings.Agent.MaxOutputTokens,
                HistoryLimit = Settings.Agent.HistoryLimit,
                MaxTurnsPerRun = Settings.Agent.MaxTurnsPerRun,
                MaxToolCallsPerTurn = Settings.Agent.MaxToolCallsPerTurn
            };
        }

        /// <summary>导出当前工作区的全部会话。</summary>
        public string ExportWorkspace(Workspace workspace)
        {
            WorkspaceConversationStore store = GetStore(workspace);
            ConversationIndex index = store.LoadIndexAsync(System.Threading.CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var transcripts = new List<ConversationTranscript>();
            foreach (ConversationIndexEntry entry in index.Sessions)
            {
                ConversationTranscript transcript = store
                    .LoadAsync(entry.SessionId, System.Threading.CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                if (transcript != null)
                {
                    transcripts.Add(transcript);
                }
            }

            return Exporter.ExportAll(transcripts, workspace.Path);
        }

        /// <summary>导出所有已添加工作区的会话。</summary>
        public string ExportAllWorkspaces()
        {
            WorkspaceCollection collection = WorkspaceStore.Load() ?? new WorkspaceCollection();
            var transcripts = new List<ConversationTranscript>();

            foreach (Workspace workspace in collection.Workspaces ?? new List<Workspace>())
            {
                try
                {
                    WorkspaceConversationStore store = GetStore(workspace);
                    ConversationIndex index = store.LoadIndexAsync(System.Threading.CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    foreach (ConversationIndexEntry entry in index.Sessions)
                    {
                        ConversationTranscript transcript = store
                            .LoadAsync(entry.SessionId, System.Threading.CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();

                        if (transcript != null)
                        {
                            transcripts.Add(transcript);
                        }
                    }
                }
                catch (Exception exception)
                {
                    Logger.Warn("export.workspace", "导出工作区失败：" + exception.Message);
                }
            }

            return Exporter.ExportAll(transcripts, null);
        }

        public void CleanupToolOutputCache()
        {
            try
            {
                string root = Paths.ToolOutputCacheDirectory;
                if (!Directory.Exists(root))
                {
                    return;
                }

                DateTime cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (string directory in Directory.GetDirectories(root))
                {
                    var info = new DirectoryInfo(directory);
                    if (info.CreationTimeUtc < cutoff && info.GetFiles().Length == 0)
                    {
                        info.Delete(true);
                    }
                }

                foreach (string file in Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        info.Delete();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            try
            {
                Runtime.Dispose();
            }
            catch (Exception)
            {
            }

            try
            {
                Http.Dispose();
            }
            catch (Exception)
            {
            }

            Logger.Info("app.exit", "服务已释放。");
            Logger.Dispose();
        }

        private void ApplyDefaultModelFromSettings()
        {
            ModelDescriptor fallback = Models.Resolve(
                Settings.Agent.DefaultProviderId,
                Settings.Agent.DefaultModelId);

            if (fallback == null)
            {
                fallback = Models.GetDefault();
            }

            if (fallback != null)
            {
                Settings.Agent.DefaultProviderId = fallback.ProviderId;
                Settings.Agent.DefaultModelId = fallback.ModelId;
            }
        }

        private IReadOnlyDictionary<string, string> BuildDiagnostics()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "版本", MiaoWu.Core.AppInfo.Version },
                { "数据目录", Paths.RootDirectory },
                { "Provider 数", Models.Providers.Count.ToString() },
                { "模型数", Models.Models.Count.ToString() },
                { "工具数", Tools.GetAll().Count.ToString() },
                { "待审批", Approvals.GetPendingCount().ToString() },
                { "代理模式", Settings.Agent.ProxyMode ?? "system" },
                { "工具启用", Settings.Agent.ToolsEnabled ? "是" : "否" },
                { "高对比", System.Windows.SystemParameters.HighContrast ? "是" : "否" }
            };

            return values;
        }
    }
}
