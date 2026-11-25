using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Agents.Goals;
using MiaoWu.Core.Agents.Schedules;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Models;
using MiaoWu.Core.Settings;
using MiaoWu.Core.Updates;
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
using MiaoWu.Infrastructure.Updates;
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
        private readonly object _catalogWriteGate = new object();
        private readonly SemaphoreSlim _modelRefreshGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, SessionGoalState> _goals =
            new Dictionary<string, SessionGoalState>(StringComparer.Ordinal);
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
        public AppServices(IAppPaths paths, IModelApi[] apis, ModelCatalogClient modelCatalog = null, IUpdateService updates = null)
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

            Updates = updates ?? new GitHubReleaseUpdateService(
                Http,
                Settings.Agent.UpdateUrl,
                MiaoWu.Core.AppInfo.Version);

            // 所有会话共用这一份模型选择；它同时是 config.json 里记录的默认模型。
            DefaultModelSelection = new ModelSelection();
            ApplyDefaultModelFromSettings();

            BackgroundJobs = new BackgroundJobRegistry();
            Questions = new UserQuestionService();
            Schedules = new ScheduleService(
                new ScheduleFileStore(System.IO.Path.Combine(paths.RootDirectory, "schedules.json")),
                SystemClock.Instance,
                DeliverScheduleAsync,
                message => Logger.Warn("schedule.deliver", message));
            Tools = BuiltInToolset.CreateRegistry(
                paths,
                SystemClock.Instance,
                BackgroundJobs,
                Questions,
                null,
                Schedules);
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
                ModelSelection = DefaultModelSelection,
                SandboxFactory = root => new WorkspacePathSandbox(root, new FileSystemPathProbe())
            };

            // 初始化完成后再订阅：启动时的默认模型落定不需要回写 config.json。
            DefaultModelSelection.Changed += OnDefaultModelSelectionChanged;

            Runtime = new AgentRuntime(SessionServices, new AgentRuntimeOptions
            {
                MaxConcurrentRequests = Settings.Agent.MaxConcurrentRequests,
                MaxHotSessions = Settings.Agent.MaxHotSessions
            });

            Logs = new LogFileService(paths.LogsDirectory, paths.ExportsDirectory, BuildDiagnostics);
            Exporter = new ConversationExporter(paths.ExportsDirectory);

            // 定时器随进程启动；关闭应用即停止投递（不注册系统计划任务）。
            Schedules.Start();

            Logger.Info("app.start", "服务初始化完成，Provider " + Models.Providers.Count
                + "，模型 " + Models.Models.Count + "。");
        }

        public IAppPaths Paths { get; }

        /// <summary>定时任务：进程内到达时间后把 prompt 作为用户消息投递回会话。</summary>
        public ScheduleService Schedules { get; }

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

        /// <summary>设置 → 关于的「检查更新」：只读 GitHub Releases，不下载也不替换。</summary>
        public IUpdateService Updates { get; }

        /// <summary>
        /// 全局共享的模型选择（所有会话共用）。在任一会话里换模型都会改到这里，
        /// 同时写回 `agent.defaultProviderId / defaultModelId`，下次启动沿用。
        /// </summary>
        public ModelSelection DefaultModelSelection { get; }

        public ToolRegistry Tools { get; }

        /// <summary>后台任务注册表：pwsh 的 run_in_background 与 job_* 工具共用；退出时统一终止。</summary>
        public BackgroundJobRegistry BackgroundJobs { get; }

        public ToolApprovalService Approvals { get; }

        /// <summary>提问通道：ask_user_question 挂起选择题，UI 通过 Changed 增删通知。</summary>
        public UserQuestionService Questions { get; }

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

        /// <summary>
        /// 自动刷新已配置供应商的模型目录：对每个有 Base URL 的供应商请求 <c>/models</c>
        /// （密钥取 secrets.dat，缺失时回退环境变量），只把缺失的模型追加进 providers.json。
        /// 已有模型保留用户设置，不删除、不覆盖；网络失败按供应商跳过并写日志，不中断其它供应商。
        /// 同一时刻只允许一次刷新，第二次调用会返回 <see cref="ProviderModelRefreshResult.Skipped"/>。
        /// </summary>
        public async Task<ProviderModelRefreshResult> RefreshProviderModelsAsync(
            CancellationToken cancellationToken)
        {
            if (!await _modelRefreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return new ProviderModelRefreshResult { Skipped = true };
            }

            try
            {
                ProviderCatalog catalog = ProviderConfigStore.Load();
                var providers = new List<ProviderDefinition>();
                if (catalog != null && catalog.Providers != null)
                {
                    foreach (ProviderDefinition provider in catalog.Providers)
                    {
                        if (provider != null && !string.IsNullOrWhiteSpace(provider.BaseUrl))
                        {
                            providers.Add(provider);
                        }
                    }
                }

                if (providers.Count == 0)
                {
                    return new ProviderModelRefreshResult();
                }

                var tasks = new Task<ProviderModelFetch>[providers.Count];
                for (int i = 0; i < providers.Count; i++)
                {
                    tasks[i] = FetchProviderModelsAsync(providers[i], cancellationToken);
                }

                ProviderModelFetch[] fetches = await Task.WhenAll(tasks).ConfigureAwait(false);
                var successes = new List<ProviderModelFetch>();
                int failed = 0;
                foreach (ProviderModelFetch fetch in fetches)
                {
                    if (fetch == null)
                    {
                        continue;
                    }

                    if (fetch.Failed)
                    {
                        failed++;
                    }
                    else if (fetch.Entries != null)
                    {
                        successes.Add(fetch);
                    }
                }

                var result = new ProviderModelRefreshResult
                {
                    AttemptedProviders = providers.Count,
                    FetchedProviders = successes.Count,
                    FailedProviders = failed
                };

                if (successes.Count == 0)
                {
                    if (failed > 0)
                    {
                        Logger.Warn(
                            "providers.autorefresh",
                            "自动刷新模型目录：没有供应商返回列表，失败 " + failed + " 个。");
                    }

                    return result;
                }

                await ApplyFetchedModelsAsync(successes, result).ConfigureAwait(false);
                return result;
            }
            finally
            {
                _modelRefreshGate.Release();
            }
        }

        /// <summary>
        /// 启动 / 首次引导完成后调用：后台自动刷新模型目录，不阻塞 UI，失败只写日志。
        /// </summary>
        public void RefreshProviderModelsInBackground()
        {
            Task task = RefreshProviderModelsAsync(CancellationToken.None);
            task.ContinueWith(
                t =>
                {
                    if (t.Exception != null)
                    {
                        Logger.Warn(
                            "providers.autorefresh",
                            "自动刷新模型目录失败：" + t.Exception.GetBaseException().Message);
                    }
                },
                TaskScheduler.Default);
        }

        /// <summary>
        /// 切换全局默认模型：所有会话共用，立即持久化。返回 false 表示这个模型不在目录里。
        /// </summary>
        public bool SetDefaultModel(string providerId, string modelId)
        {
            ModelDescriptor model = Models.Resolve(providerId, modelId);
            if (model == null)
            {
                return false;
            }

            return DefaultModelSelection.Set(model.ProviderId, model.ModelId);
        }

        /// <summary>当前默认模型描述符；目录里找不到时退回注册表的兜底模型。</summary>
        public ModelDescriptor ResolveDefaultModel()
        {
            ModelSelection selection = DefaultModelSelection;
            return Models.Resolve(selection.ProviderId, selection.ModelId) ?? Models.GetDefault();
        }

        /// <summary>
        /// 设置全局推理等级（<c>null</c> / <c>low</c> / <c>medium</c> / <c>high</c>）并立即落盘。
        /// 和模型一样是所有会话共用的配置：改完之后下一次请求就按新等级发送。
        /// </summary>
        public void SetReasoningEffort(string effort)
        {
            string normalized = NormalizeReasoningEffort(effort);
            if (string.Equals(Settings.Agent.ReasoningEffort, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Settings.Agent.ReasoningEffort = normalized;
            SaveSettings();
        }

        /// <summary>空串与未知值都归一成 <c>null</c>（默认档，不发 reasoning_effort）。</summary>
        public static string NormalizeReasoningEffort(string effort)
        {
            return AgentSettings.NormalizeReasoningEffort(effort);
        }

        /// <summary>保存 Provider 配置并热切换注册表。校验失败返回错误列表，不写盘。</summary>
        public IReadOnlyList<string> SaveProviders(ProviderCatalog catalog)
        {
            // 与自动刷新共用一把写锁：后台刷新不会在用户保存到一半时覆盖目录。
            lock (_catalogWriteGate)
            {
                return SaveProvidersCore(catalog);
            }
        }

        private IReadOnlyList<string> SaveProvidersCore(ProviderCatalog catalog)
        {
            IReadOnlyList<string> errors = ProviderConfigValidator.Validate(catalog);
            if (errors.Count > 0)
            {
                return errors;
            }

            ProviderConfigStore.Save(catalog);
            RebuildHttpTransport();

            // 目录变化后保留用户当前选的模型；它已经不在目录里时才退回兜底模型。
            ModelDescriptor fallback = Models.Resolve(
                DefaultModelSelection.ProviderId,
                DefaultModelSelection.ModelId) ?? Models.GetDefault();
            if (fallback == null)
            {
                // 目录被清空：共享选择与设置一起清掉，下次拿到模型时再落定。
                DefaultModelSelection.Set(null, null);
                Settings.Agent.DefaultProviderId = null;
                Settings.Agent.DefaultModelId = null;
                SaveSettings();
            }
            else
            {
                DefaultModelSelection.Set(fallback.ProviderId, fallback.ModelId);
            }

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

        /// <summary>
        /// 会话目标状态：按会话 ID 缓存，首次访问时从 <c>.miaowu\goals\{sessionId}.json</c> 载入。
        /// 工作区路径非法或会话 ID 不合法时退回纯内存状态，不影响会话本身。
        /// </summary>
        public SessionGoalState GetGoalState(Workspace workspace, string sessionId)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            lock (_gate)
            {
                SessionGoalState state;
                if (_goals.TryGetValue(sessionId, out state))
                {
                    return state;
                }

                try
                {
                    string path = AgentWorkspaceLayout.GetGoalFilePath(workspace.Path, sessionId);
                    state = new SessionGoalState(new GoalFileStore(path));
                }
                catch (Exception exception)
                {
                    Logger.Warn("goal.store", "目标状态文件不可用，退回内存状态：" + exception.Message);
                    state = new SessionGoalState();
                }

                _goals[sessionId] = state;
                return state;
            }
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
                ProviderId = providerId ?? DefaultModelSelection.ProviderId ?? Settings.Agent.DefaultProviderId,
                ModelId = modelId ?? DefaultModelSelection.ModelId ?? Settings.Agent.DefaultModelId,
                Temperature = Settings.Agent.Temperature,
                MaxOutputTokens = Settings.Agent.MaxOutputTokens,
                ReasoningEffort = NormalizeReasoningEffort(Settings.Agent.ReasoningEffort),
                ReasoningEffortProvider = () => NormalizeReasoningEffort(Settings.Agent.ReasoningEffort),
                HistoryLimit = Settings.Agent.HistoryLimit,
                MaxTurnsPerRun = Settings.Agent.MaxTurnsPerRun,
                MaxToolCallsPerTurn = Settings.Agent.MaxToolCallsPerTurn,
                MaxParallelToolCalls = Settings.Agent.MaxParallelReadTools,
                ApprovalPolicy = Settings.Agent.ApprovalPolicy,
                ApprovalPolicyProvider = () => Settings.Agent.ApprovalPolicy,
                Goals = GetGoalState(workspace, sessionId),
                GoalContinuation = Settings.Agent.GoalAutoContinue,
                GoalContinuationProvider = () => Settings.Agent.GoalAutoContinue,
                AutoTitle = Settings.Agent.AutoTitle
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

        /// <summary>
        /// 日程投递：把 prompt 作为用户消息排进目标会话。只支持“当前会话”目标；
        /// 会话不在内存时按 workspaceId 重新装载，工作区已删除则只记日志。
        /// </summary>
        private async Task DeliverScheduleAsync(ScheduleDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.Prompt))
            {
                return;
            }

            try
            {
                IAgentSession session;
                if (definition.Target == ScheduleTarget.ThisSession)
                {
                    session = string.IsNullOrWhiteSpace(definition.SessionId)
                        ? null
                        : Runtime.Find(definition.SessionId);

                    if (session == null)
                    {
                        Workspace workspace = FindWorkspace(definition.WorkspaceId);
                        if (workspace == null || string.IsNullOrWhiteSpace(definition.SessionId))
                        {
                            Logger.Warn("schedule.deliver", "找不到定时任务的会话或工作区：" + definition.Id);
                            return;
                        }

                        session = await Runtime.GetOrCreateAsync(
                            CreateSessionOptions(
                                workspace,
                                definition.SessionId,
                                definition.ProviderId,
                                definition.ModelId),
                            CancellationToken.None);
                    }
                }
                else
                {
                    Workspace workspace = FindWorkspace(definition.WorkspaceId);
                    if (workspace == null)
                    {
                        Logger.Warn("schedule.deliver", "找不到定时任务的工作区：" + definition.Id);
                        return;
                    }

                    // new_once 复用首次创建的会话；new 每次都开一个新会话。
                    bool reuse = definition.Target == ScheduleTarget.NewOnce
                        && !string.IsNullOrWhiteSpace(definition.SessionId);
                    string sessionId = reuse
                        ? definition.SessionId
                        : "s_" + Guid.NewGuid().ToString("N").Substring(0, 12);

                    if (!reuse)
                    {
                        Schedules.UpdateSession(definition.Id, sessionId);
                    }

                    session = await Runtime.GetOrCreateAsync(
                        CreateSessionOptions(
                            workspace,
                            sessionId,
                            definition.ProviderId,
                            definition.ModelId),
                        CancellationToken.None);
                }

                var input = new AgentInput
                {
                    ClientMessageId = "m_" + Guid.NewGuid().ToString("N").Substring(0, 10),
                    Blocks = new List<AgentContentBlock>
                    {
                        new AgentContentBlock { Type = "text", Text = definition.Prompt }
                    },
                    ProviderId = definition.ProviderId,
                    ModelId = definition.ModelId,
                    ReasoningEffort = definition.ReasoningEffort
                };

                await session.SendAsync(input, AgentSendMode.Queue, CancellationToken.None);
                Logger.Info("schedule.deliver", "已投递定时任务 " + definition.Id + "。");
            }
            catch (Exception exception)
            {
                Logger.Warn("schedule.deliver", "投递定时任务失败：" + exception.Message);
            }
        }

        private Workspace FindWorkspace(string workspaceId)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return null;
            }

            WorkspaceCollection collection = WorkspaceStore.Load();
            if (collection == null || collection.Workspaces == null)
            {
                return null;
            }

            for (int i = 0; i < collection.Workspaces.Count; i++)
            {
                Workspace workspace = collection.Workspaces[i];
                if (workspace != null && string.Equals(workspace.Id, workspaceId, StringComparison.Ordinal))
                {
                    return workspace;
                }
            }

            return null;
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
                DefaultModelSelection.Changed -= OnDefaultModelSelectionChanged;
            }
            catch (Exception)
            {
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
                BackgroundJobs.Dispose();
            }
            catch (Exception)
            {
            }

            try
            {
                Schedules.Dispose();
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

        /// <summary>
        /// 请求单个供应商的模型列表。没有密钥时跳过（不失败）；
        /// 超时或网络错误只记日志，不影响其它供应商。
        /// </summary>
        private async Task<ProviderModelFetch> FetchProviderModelsAsync(
            ProviderDefinition provider,
            CancellationToken cancellationToken)
        {
            try
            {
                ProviderCredential credential = await Models
                    .ResolveCredentialAsync(provider.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (credential == null || !credential.HasKey)
                {
                    return ProviderModelFetch.Skipped(provider.Id);
                }

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    IReadOnlyList<ModelCatalogEntry> entries = await ListModelsAsync(
                            provider.BaseUrl,
                            provider.Api,
                            credential.ApiKey,
                            timeout.Token)
                        .ConfigureAwait(false);
                    return ProviderModelFetch.Success(provider.Id, entries);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Logger.Warn("providers.autorefresh", provider.Id + "：获取模型列表超时。");
                return ProviderModelFetch.Failure(provider.Id);
            }
            catch (Exception exception)
            {
                Logger.Warn(
                    "providers.autorefresh",
                    provider.Id + "：获取模型列表失败：" + exception.Message);
                return ProviderModelFetch.Failure(provider.Id);
            }
        }

        /// <summary>
        /// 把取到的模型列表合并进最新的 providers.json 并热切换注册表。
        /// 有 UI 线程时切回 Dispatcher 执行，避免与设置页的读改写并发。
        /// </summary>
        private Task ApplyFetchedModelsAsync(
            IReadOnlyList<ProviderModelFetch> fetches,
            ProviderModelRefreshResult result)
        {
            Func<ProviderModelRefreshResult> apply = () =>
            {
                lock (_catalogWriteGate)
                {
                    ProviderCatalog latest = ProviderConfigStore.Load();
                    var additions = new List<string>();
                    int addedTotal = 0;

                    foreach (ProviderModelFetch fetch in fetches)
                    {
                        ProviderDefinition provider = latest.FindProvider(fetch.ProviderId);
                        if (provider == null)
                        {
                            continue;
                        }

                        int added = ProviderModelCatalogMerge.Merge(provider, fetch.Entries);
                        if (added > 0)
                        {
                            addedTotal += added;
                            additions.Add(provider.Id + " +" + added);
                        }
                    }

                    if (addedTotal == 0)
                    {
                        Logger.Info("providers.autorefresh", "模型目录已是最新，没有新增模型。");
                        return result;
                    }

                    IReadOnlyList<string> errors = SaveProviders(latest);
                    if (errors.Count > 0)
                    {
                        Logger.Warn(
                            "providers.autorefresh",
                            "模型目录保存失败：" + string.Join("；", errors));
                        return result;
                    }

                    result.AddedModels = addedTotal;
                    Logger.Info(
                        "providers.autorefresh",
                        "自动更新模型目录：" + string.Join("，", additions) + "。");
                    return result;
                }
            };

            System.Windows.Threading.Dispatcher dispatcher = System.Windows.Application.Current == null
                ? null
                : System.Windows.Application.Current.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                return dispatcher.InvokeAsync(apply).Task;
            }

            apply();
            return Task.CompletedTask;
        }

        /// <summary>一次供应商模型列表请求的结果；Entries 为空表示没取到列表。</summary>
        private sealed class ProviderModelFetch
        {
            private ProviderModelFetch(
                string providerId,
                IReadOnlyList<ModelCatalogEntry> entries,
                bool failed)
            {
                ProviderId = providerId;
                Entries = entries;
                Failed = failed;
            }

            public string ProviderId { get; }

            public IReadOnlyList<ModelCatalogEntry> Entries { get; }

            public bool Failed { get; }

            public static ProviderModelFetch Success(
                string providerId,
                IReadOnlyList<ModelCatalogEntry> entries)
            {
                return new ProviderModelFetch(providerId, entries, false);
            }

            public static ProviderModelFetch Failure(string providerId)
            {
                return new ProviderModelFetch(providerId, null, true);
            }

            public static ProviderModelFetch Skipped(string providerId)
            {
                return new ProviderModelFetch(providerId, null, false);
            }
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

            if (fallback == null)
            {
                return;
            }

            Settings.Agent.DefaultProviderId = fallback.ProviderId;
            Settings.Agent.DefaultModelId = fallback.ModelId;
            DefaultModelSelection.Set(fallback.ProviderId, fallback.ModelId);
        }

        /// <summary>共享模型变了：写回 config.json，下次启动仍是同一个模型。</summary>
        private void OnDefaultModelSelectionChanged(object sender, EventArgs e)
        {
            Settings.Agent.DefaultProviderId = DefaultModelSelection.ProviderId;
            Settings.Agent.DefaultModelId = DefaultModelSelection.ModelId;

            try
            {
                SaveSettings();
            }
            catch (Exception exception)
            {
                Logger.Warn("settings.model", "默认模型保存失败：" + exception.Message);
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
