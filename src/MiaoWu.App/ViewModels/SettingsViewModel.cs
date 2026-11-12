using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Services;
using MiaoWu.Core;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Models;
using MiaoWu.Core.Settings;
using MiaoWu.Infrastructure.Logging;
using MiaoWu.Infrastructure.Providers;

namespace MiaoWu.App.ViewModels
{
    public enum SettingsPage
    {
        General,
        Providers,
        Tools,
        Appearance,
        Shortcuts,
        Data,
        About
    }

    public sealed class SettingsNavItem
    {
        public SettingsNavItem(SettingsPage page, string title)
        {
            Page = page;
            Title = title;
        }

        public SettingsPage Page { get; }

        public string Title { get; }
    }

    public sealed class ShortcutItem
    {
        public ShortcutItem(string keys, string action)
        {
            Keys = keys;
            Action = action;
        }

        public string Keys { get; }

        public string Action { get; }
    }

    /// <summary>
    /// 设置浮层：7 页、即时保存、Provider 增删改与连接测试、日志/审计/诊断/导出入口。
    /// 不新开 Window；Esc 关闭由 ShellView 的 InputBinding 处理。
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly AppServices _app;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;

        private SettingsNavItem _selectedPage;
        private ProviderEditorViewModel _selectedProvider;
        private bool _isOpen;
        private string _statusText;
        private string _providerStatus;
        private string _testStatus;
        private string _dataStatus;
        private string _logText;
        private string _auditText;
        private string _updateStatus;
        private bool _isLogVisible;
        private bool _isAuditVisible;
        private bool _isTesting;

        public SettingsViewModel(AppServices app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _dispatcher = Application.Current == null
                ? System.Windows.Threading.Dispatcher.CurrentDispatcher
                : Application.Current.Dispatcher;

            Pages = new ObservableCollection<SettingsNavItem>
            {
                new SettingsNavItem(SettingsPage.General, "通用"),
                new SettingsNavItem(SettingsPage.Providers, "模型与 Provider"),
                new SettingsNavItem(SettingsPage.Tools, "工具与权限"),
                new SettingsNavItem(SettingsPage.Appearance, "外观"),
                new SettingsNavItem(SettingsPage.Shortcuts, "快捷键"),
                new SettingsNavItem(SettingsPage.Data, "数据与隐私"),
                new SettingsNavItem(SettingsPage.About, "关于与更新")
            };

            ProviderEditors = new ObservableCollection<ProviderEditorViewModel>();
            Shortcuts = new ObservableCollection<ShortcutItem>
            {
                new ShortcutItem("Ctrl + J", "展开 / 折叠右栏对话"),
                new ShortcutItem("Ctrl + F", "在当前文档中查找"),
                new ShortcutItem("Ctrl + N", "新建会话"),
                new ShortcutItem("Ctrl + ,", "打开设置"),
                new ShortcutItem("Enter", "发送消息"),
                new ShortcutItem("Shift + Enter", "输入换行"),
                new ShortcutItem("Esc", "关闭设置 / 浮层"),
                new ShortcutItem("Tab / Shift + Tab", "在控件之间移动焦点")
            };

            _selectedPage = Pages[0];

            SelectPageCommand = new RelayCommand(parameter => SelectPage(parameter as SettingsNavItem));
            CloseCommand = new RelayCommand(() => IsOpen = false);
            SaveGeneralCommand = new RelayCommand(SaveSettings);
            AddProviderCommand = new RelayCommand(AddProvider);
            RemoveProviderCommand = new RelayCommand(parameter => RemoveProvider(parameter as ProviderEditorViewModel));
            SaveProvidersCommand = new RelayCommand(() => SaveProviders());
            TestProviderCommand = new RelayCommand(() => FireAndForget(TestProviderAsync()));
            OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_app.Paths.RootDirectory));
            OpenExportsFolderCommand = new RelayCommand(() => OpenFolder(_app.Paths.ExportsDirectory));
            ExportAllCommand = new RelayCommand(() => FireAndForget(ExportAllAsync()));
            RefreshLogsCommand = new RelayCommand(RefreshLogs);
            ClearLogsCommand = new RelayCommand(ClearLogs);
            ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
            RefreshAuditCommand = new RelayCommand(RefreshAudit);
            ClearAuditCommand = new RelayCommand(ClearAudit);
            ExportAuditCommand = new RelayCommand(ExportAudit);
            CheckUpdateCommand = new RelayCommand(() => FireAndForget(CheckUpdateAsync()));
            OpenDownloadCommand = new RelayCommand(OpenDownload);
            ClearToolOutputCommand = new RelayCommand(ClearToolOutput);

            ReloadProviders();
        }

        public ObservableCollection<SettingsNavItem> Pages { get; }

        public ObservableCollection<ProviderEditorViewModel> ProviderEditors { get; }

        public ObservableCollection<ShortcutItem> Shortcuts { get; }

        public RelayCommand SelectPageCommand { get; }

        public RelayCommand CloseCommand { get; }

        public RelayCommand SaveGeneralCommand { get; }

        public RelayCommand AddProviderCommand { get; }

        public RelayCommand RemoveProviderCommand { get; }

        public RelayCommand SaveProvidersCommand { get; }

        public RelayCommand TestProviderCommand { get; }

        public RelayCommand OpenDataFolderCommand { get; }

        public RelayCommand OpenExportsFolderCommand { get; }

        public RelayCommand ExportAllCommand { get; }

        public RelayCommand RefreshLogsCommand { get; }

        public RelayCommand ClearLogsCommand { get; }

        public RelayCommand ExportDiagnosticsCommand { get; }

        public RelayCommand RefreshAuditCommand { get; }

        public RelayCommand ClearAuditCommand { get; }

        public RelayCommand ExportAuditCommand { get; }

        public RelayCommand CheckUpdateCommand { get; }

        public RelayCommand OpenDownloadCommand { get; }

        public RelayCommand ClearToolOutputCommand { get; }

        public bool IsOpen
        {
            get { return _isOpen; }
            set
            {
                if (SetProperty(ref _isOpen, value) && value)
                {
                    ReloadProviders();
                    StatusText = null;
                    RefreshLogs();
                }
            }
        }

        public SettingsNavItem SelectedPage
        {
            get { return _selectedPage; }
            set
            {
                if (SetProperty(ref _selectedPage, value) && value != null)
                {
                    RaisePageFlags();
                }
            }
        }

        public IReadOnlyList<string> ApiOptions
        {
            get { return new[] { "openai-completions", "anthropic-messages" }; }
        }

        public ProviderEditorViewModel SelectedProvider
        {
            get { return _selectedProvider; }
            set { SetProperty(ref _selectedProvider, value); }
        }

        public string SystemPrompt
        {
            get { return _app.Settings.Agent.SystemPrompt; }
            set
            {
                if (string.Equals(_app.Settings.Agent.SystemPrompt, value, StringComparison.Ordinal))
                {
                    return;
                }

                _app.Settings.Agent.SystemPrompt = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public void SelectPage(SettingsPage page)
        {
            foreach (SettingsNavItem item in Pages)
            {
                if (item.Page == page)
                {
                    SelectedPage = item;
                    return;
                }
            }
        }

        public bool IsGeneralPage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.General; }
        }

        public bool IsProvidersPage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.Providers; }
        }

        public bool IsToolsPage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.Tools; }
        }

        public bool IsAppearancePage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.Appearance; }
        }

        public bool IsShortcutsPage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.Shortcuts; }
        }

        public bool IsDataPage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.Data; }
        }

        public bool IsAboutPage
        {
            get { return _selectedPage != null && _selectedPage.Page == SettingsPage.About; }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set
            {
                if (SetProperty(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HasStatus));
                }
            }
        }

        public bool HasStatus
        {
            get { return !string.IsNullOrEmpty(_statusText); }
        }

        public bool StartMinimized
        {
            get { return _app.Settings.Agent.StartMinimized; }
            set
            {
                if (_app.Settings.Agent.StartMinimized == value)
                {
                    return;
                }

                _app.Settings.Agent.StartMinimized = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public bool KeepRunningInTray
        {
            get { return _app.Settings.Agent.KeepRunningInTray; }
            set
            {
                if (_app.Settings.Agent.KeepRunningInTray == value)
                {
                    return;
                }

                _app.Settings.Agent.KeepRunningInTray = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public bool CheckUpdatesOnStartup
        {
            get { return _app.Settings.Agent.CheckUpdatesOnStartup; }
            set
            {
                if (_app.Settings.Agent.CheckUpdatesOnStartup == value)
                {
                    return;
                }

                _app.Settings.Agent.CheckUpdatesOnStartup = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public bool ShowLogOnError
        {
            get { return _app.Settings.Agent.ShowLogOnError; }
            set
            {
                if (_app.Settings.Agent.ShowLogOnError == value)
                {
                    return;
                }

                _app.Settings.Agent.ShowLogOnError = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public bool ToolsEnabled
        {
            get { return _app.Settings.Agent.ToolsEnabled; }
            set
            {
                if (_app.Settings.Agent.ToolsEnabled == value)
                {
                    return;
                }

                _app.Settings.Agent.ToolsEnabled = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public int MaxTurnsPerRun
        {
            get { return _app.Settings.Agent.MaxTurnsPerRun; }
            set
            {
                if (_app.Settings.Agent.MaxTurnsPerRun == value)
                {
                    return;
                }

                _app.Settings.Agent.MaxTurnsPerRun = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public int MaxToolCallsPerTurn
        {
            get { return _app.Settings.Agent.MaxToolCallsPerTurn; }
            set
            {
                if (_app.Settings.Agent.MaxToolCallsPerTurn == value)
                {
                    return;
                }

                _app.Settings.Agent.MaxToolCallsPerTurn = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public int MaxParallelReadTools
        {
            get { return _app.Settings.Agent.MaxParallelReadTools; }
            set
            {
                if (_app.Settings.Agent.MaxParallelReadTools == value)
                {
                    return;
                }

                _app.Settings.Agent.MaxParallelReadTools = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public int ToolOutputMaxLines
        {
            get { return _app.Settings.Agent.ToolOutput.MaxLines; }
            set
            {
                if (_app.Settings.Agent.ToolOutput.MaxLines == value)
                {
                    return;
                }

                _app.Settings.Agent.ToolOutput.MaxLines = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public string ProxyMode
        {
            get { return _app.Settings.Agent.ProxyMode ?? "system"; }
            set
            {
                if (string.Equals(_app.Settings.Agent.ProxyMode, value, StringComparison.Ordinal))
                {
                    return;
                }

                _app.Settings.Agent.ProxyMode = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public string ProxyUrl
        {
            get { return _app.Settings.Agent.ProxyUrl; }
            set
            {
                if (string.Equals(_app.Settings.Agent.ProxyUrl, value, StringComparison.Ordinal))
                {
                    return;
                }

                _app.Settings.Agent.ProxyUrl = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public bool RightPaneCollapsedByDefault
        {
            get { return _app.Settings.Ui.IsRightPaneCollapsed; }
            set
            {
                if (_app.Settings.Ui.IsRightPaneCollapsed == value)
                {
                    return;
                }

                _app.Settings.Ui.IsRightPaneCollapsed = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }

        public string DataDirectory
        {
            get { return _app.Paths.RootDirectory; }
        }

        public string LogFileSummary
        {
            get
            {
                IReadOnlyList<LogFileInfo> files = _app.Logs.List();
                long total = files.Sum(f => f.Length);
                return files.Count == 0
                    ? "暂无日志"
                    : files.Count + " 个文件 · " + (total / 1024) + " KB · 保留 7 天 / 10 MB";
            }
        }

        public string AuditFileSummary
        {
            get
            {
                IReadOnlyList<LogFileInfo> files = _app.AuditLogs.List();
                return files.Count == 0 ? "暂无审计记录" : files.Count + " 个文件";
            }
        }

        public string ToolOutputSummary
        {
            get
            {
                try
                {
                    string root = _app.Paths.ToolOutputCacheDirectory;
                    if (!System.IO.Directory.Exists(root))
                    {
                        return "暂无缓存";
                    }

                    long bytes = 0;
                    foreach (string file in System.IO.Directory.GetFiles(root, "*", System.IO.SearchOption.AllDirectories))
                    {
                        bytes += new System.IO.FileInfo(file).Length;
                    }

                    return (bytes / 1024) + " KB";
                }
                catch (Exception)
                {
                    return "未知";
                }
            }
        }

        public string LogText
        {
            get { return _logText; }
            private set { SetProperty(ref _logText, value); }
        }

        public bool IsLogVisible
        {
            get { return _isLogVisible; }
            private set { SetProperty(ref _isLogVisible, value); }
        }

        public string AuditText
        {
            get { return _auditText; }
            private set { SetProperty(ref _auditText, value); }
        }

        public bool IsAuditVisible
        {
            get { return _isAuditVisible; }
            private set { SetProperty(ref _isAuditVisible, value); }
        }

        public string ProviderStatus
        {
            get { return _providerStatus; }
            private set { SetProperty(ref _providerStatus, value); }
        }

        public string TestStatus
        {
            get { return _testStatus; }
            private set
            {
                if (SetProperty(ref _testStatus, value))
                {
                    OnPropertyChanged(nameof(HasTestStatus));
                }
            }
        }

        public bool HasTestStatus
        {
            get { return !string.IsNullOrEmpty(_testStatus); }
        }

        public bool IsTesting
        {
            get { return _isTesting; }
            private set { SetProperty(ref _isTesting, value); }
        }

        public string DataStatus
        {
            get { return _dataStatus; }
            private set { SetProperty(ref _dataStatus, value); }
        }

        public string UpdateStatus
        {
            get { return _updateStatus; }
            private set { SetProperty(ref _updateStatus, value); }
        }

        public string VersionText
        {
            get { return "MiaoWu " + AppInfo.Version; }
        }

        public string RuntimeText
        {
            get { return ".NET Framework " + Environment.Version + " · " + (Environment.Is64BitProcess ? "64 位" : "32 位"); }
        }

        public string OsText
        {
            get { return Environment.OSVersion.ToString(); }
        }

        public bool HighContrast
        {
            get { return System.Windows.SystemParameters.HighContrast; }
        }

        public void Open()
        {
            IsOpen = true;
            SelectPage(SettingsPage.General);
        }

        public void Close()
        {
            IsOpen = false;
        }

        public void ReloadProviders()
        {
            ProviderEditors.Clear();
            ProviderCatalog catalog = _app.ProviderConfigStore.Load();
            IReadOnlyDictionary<string, string> keys = _app.Credentials.LoadAll();

            foreach (ProviderDefinition provider in catalog.Providers)
            {
                string key;
                keys.TryGetValue(provider.Id, out key);
                ProviderEditors.Add(new ProviderEditorViewModel(provider, key));
            }

            SelectedProvider = ProviderEditors.FirstOrDefault();

            ProviderStatus = ProviderEditors.Count == 0
                ? "尚未配置 Provider。至少添加一个才能发送消息。"
                : "已配置 " + ProviderEditors.Count + " 个 Provider，密钥用 DPAPI 加密保存在本机。";
        }

        private void SelectPage(SettingsNavItem item)
        {
            if (item != null)
            {
                SelectedPage = item;
            }
        }

        private void SaveSettings()
        {
            try
            {
                _app.SaveSettings();
                StatusText = "已保存";
            }
            catch (Exception exception)
            {
                StatusText = "保存失败：" + exception.Message;
            }
        }

        private void AddProvider()
        {
            var provider = new ProviderDefinition
            {
                Id = "provider" + (ProviderEditors.Count + 1),
                DisplayName = "新 Provider",
                Api = "openai-completions",
                BaseUrl = "https://api.openai.com/v1"
            };
            provider.Models.Add(new ModelDefinition
            {
                Id = "model-id",
                Name = "新模型",
                ContextWindow = 128000,
                MaxOutputTokens = 8192
            });

            ProviderEditors.Add(new ProviderEditorViewModel(provider, null));
            ProviderStatus = "已添加草稿，填写后点“保存全部”。";
        }

        private void RemoveProvider(ProviderEditorViewModel editor)
        {
            if (editor == null)
            {
                return;
            }

            ProviderEditors.Remove(editor);
            ProviderStatus = "已从列表移除，点“保存全部”生效。";
        }

        private void SaveProviders()
        {
            var catalog = new ProviderCatalog
            {
                ActiveProviderId = _app.Settings.Agent.DefaultProviderId
            };

            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ProviderEditorViewModel editor in ProviderEditors)
            {
                ProviderDefinition definition = editor.ToDefinition(out string error);
                if (!string.IsNullOrEmpty(error))
                {
                    errors.Add(error);
                    continue;
                }

                if (!seen.Add(definition.Id))
                {
                    errors.Add("Provider ID 重复：" + definition.Id);
                    continue;
                }

                catalog.Providers.Add(definition);
            }

            if (catalog.ActiveProviderId == null && catalog.Providers.Count > 0)
            {
                catalog.ActiveProviderId = catalog.Providers[0].Id;
            }

            try
            {
                IReadOnlyList<string> validation = _app.SaveProviders(catalog);
                if (validation.Count > 0)
                {
                    errors.AddRange(validation);
                }

                if (errors.Count > 0)
                {
                    ProviderStatus = string.Join("\n", errors);
                    return;
                }

                foreach (ProviderEditorViewModel editor in ProviderEditors)
                {
                    if (!string.IsNullOrWhiteSpace(editor.ApiKey))
                    {
                        _app.Credentials.SetKey(editor.Id, editor.ApiKey);
                    }
                    else
                    {
                        _app.Credentials.Remove(editor.Id);
                    }
                }

                ReloadProviders();
                ProviderStatus = "已保存。下一条消息使用新配置。";
                StatusText = "Provider 已保存";
            }
            catch (Exception exception)
            {
                ProviderStatus = "保存失败：" + exception.Message;
            }
        }

        private async Task TestProviderAsync()
        {
            ProviderEditorViewModel editor = ProviderEditors.FirstOrDefault();
            if (editor == null)
            {
                TestStatus = "先添加一个 Provider。";
                return;
            }

            IsTesting = true;
            TestStatus = "正在连接…";
            try
            {
                ProviderDefinition definition = editor.ToDefinition(out string error);
                if (!string.IsNullOrEmpty(error))
                {
                    TestStatus = error;
                    return;
                }

                var catalog = new ProviderCatalog();
                catalog.Providers.Add(definition);
                var registry = new ModelRegistry(catalog, new TestCredentialStore(editor.Id, editor.ApiKey), new IModelApi[]
                {
                    new MiaoWu.Infrastructure.Providers.OpenAiCompletionsApi(_app.Http),
                    new MiaoWu.Infrastructure.Providers.AnthropicMessagesApi(_app.Http)
                });

                ModelDescriptor model = registry.GetDefault();
                if (model == null)
                {
                    TestStatus = "Provider 没有模型。";
                    return;
                }

                ProviderCredential credential = await registry.ResolveCredentialAsync(
                    model.ProviderId,
                    System.Threading.CancellationToken.None);

                if (!credential.HasKey)
                {
                    TestStatus = "未填写 API Key。";
                    return;
                }

                IModelApi api = registry.GetApi(model.Api);
                var request = new LlmRequest
                {
                    SystemPrompt = "ping",
                    MaxOutputTokens = 1,
                    Messages = new List<LlmMessage>
                    {
                        new LlmMessage
                        {
                            Role = "user",
                            Content = new List<LlmContent>
                            {
                                new LlmContent { Type = "text", Text = "ping" }
                            }
                        }
                    }
                };

                using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20)))
                using (ILlmStream stream = api.Open(model, request, credential, timeout.Token))
                {
                    while (true)
                    {
                        LlmStreamEvent evt = await stream.ReadAsync(timeout.Token);
                        if (evt == null)
                        {
                            TestStatus = "连接成功：模型已响应。";
                            return;
                        }

                        var failed = evt as LlmFailed;
                        if (failed != null)
                        {
                            TestStatus = "失败：" + (failed.Error == null ? "未知错误" : failed.Error.Message);
                            return;
                        }

                        if (evt is LlmStarted || evt is LlmTextDelta || evt is LlmCompleted)
                        {
                            TestStatus = "连接成功：TLS / 代理 / Key / 模型均可用。";
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                TestStatus = "超时：检查网络、代理或系统 TLS 1.2 补丁。";
            }
            catch (Exception exception)
            {
                TestStatus = "失败：" + exception.Message;
            }
            finally
            {
                IsTesting = false;
            }
        }

        private async Task ExportAllAsync()
        {
            try
            {
                DataStatus = "正在导出…";
                string folder = await Task.Run(() => _app.ExportAllWorkspaces());
                DataStatus = "已导出到 " + folder;
                OpenFolder(folder);
            }
            catch (Exception exception)
            {
                DataStatus = "导出失败：" + exception.Message;
            }
        }

        private void RefreshLogs()
        {
            LogText = _app.Logs.ReadTail(120000);
            IsLogVisible = !string.IsNullOrEmpty(LogText);
            OnPropertyChanged(nameof(LogFileSummary));
        }

        private void ClearLogs()
        {
            _app.Logs.Clear();
            LogText = string.Empty;
            IsLogVisible = false;
            OnPropertyChanged(nameof(LogFileSummary));
            DataStatus = "日志已清空。";
        }

        private void ExportDiagnostics()
        {
            try
            {
                string path = _app.Logs.ExportDiagnostics();
                DataStatus = "诊断包已导出：" + path;
                OpenFolder(System.IO.Path.GetDirectoryName(path));
            }
            catch (Exception exception)
            {
                DataStatus = "导出诊断包失败：" + exception.Message;
            }
        }

        private void RefreshAudit()
        {
            AuditText = _app.AuditLogs.ReadTail(120000);
            IsAuditVisible = !string.IsNullOrEmpty(AuditText);
            OnPropertyChanged(nameof(AuditFileSummary));
        }

        private void ClearAudit()
        {
            _app.AuditLogs.Clear();
            AuditText = string.Empty;
            IsAuditVisible = false;
            OnPropertyChanged(nameof(AuditFileSummary));
            DataStatus = "审计日志已清空。";
        }

        private void ExportAudit()
        {
            try
            {
                string path = _app.AuditLogs.Export();
                DataStatus = "审计日志已导出：" + path;
            }
            catch (Exception exception)
            {
                DataStatus = "导出审计失败：" + exception.Message;
            }
        }

        private void ClearToolOutput()
        {
            try
            {
                string root = _app.Paths.ToolOutputCacheDirectory;
                if (System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.Delete(root, true);
                    System.IO.Directory.CreateDirectory(root);
                }

                OnPropertyChanged(nameof(ToolOutputSummary));
                DataStatus = "工具输出缓存已清空。";
            }
            catch (Exception exception)
            {
                DataStatus = "清空失败：" + exception.Message;
            }
        }

        private async Task CheckUpdateAsync()
        {
            UpdateStatus = "正在检查…";
            var checker = new UpdateChecker(_app.Http, _app.Settings.Agent.UpdateUrl);
            UpdateCheckResult result = await checker.CheckAsync();
            UpdateStatus = result.Message;
        }

        private void OpenDownload()
        {
            try
            {
                Process.Start(new ProcessStartInfo(_app.Settings.Agent.UpdateUrl)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                UpdateStatus = "无法打开下载页：" + exception.Message;
            }
        }

        private void OpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception)
            {
            }
        }

        private void RaisePageFlags()
        {
            OnPropertyChanged(nameof(IsGeneralPage));
            OnPropertyChanged(nameof(IsProvidersPage));
            OnPropertyChanged(nameof(IsToolsPage));
            OnPropertyChanged(nameof(IsAppearancePage));
            OnPropertyChanged(nameof(IsShortcutsPage));
            OnPropertyChanged(nameof(IsDataPage));
            OnPropertyChanged(nameof(IsAboutPage));
        }

        private void FireAndForget(Task task)
        {
            if (task == null)
            {
                return;
            }

            task.ContinueWith(
                t =>
                {
                    if (t.Exception != null)
                    {
                        _dispatcher.BeginInvoke(new Action(() =>
                            StatusText = "操作失败：" + t.Exception.GetBaseException().Message));
                    }
                },
                TaskScheduler.Default);
        }

        /// <summary>连接测试要使用表单里刚输入、尚未保存的 Key。</summary>
        private sealed class TestCredentialStore : ICredentialStore
        {
            private readonly string _providerId;
            private readonly string _key;

            public TestCredentialStore(string providerId, string key)
            {
                _providerId = providerId;
                _key = key;
            }

            public string GetKey(string providerId)
            {
                return string.Equals(providerId, _providerId, StringComparison.OrdinalIgnoreCase)
                    ? _key
                    : null;
            }

            public void SetKey(string providerId, string apiKey)
            {
            }

            public void Remove(string providerId)
            {
            }

            public IReadOnlyDictionary<string, string> LoadAll()
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(_key))
                {
                    values[_providerId] = _key;
                }

                return values;
            }
        }
    }

    /// <summary>Provider 编辑草稿。保存前不写盘。</summary>
    public sealed class ProviderEditorViewModel : ObservableObject
    {
        private readonly ProviderDefinition _definition;
        private string _apiKey;
        private ModelEditorViewModel _selectedModel;
        private string _displayName;
        private string _id;
        private string _api;
        private string _baseUrl;

        public ProviderEditorViewModel(ProviderDefinition definition, string apiKey)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _apiKey = apiKey;
            _displayName = definition.DisplayName;
            _id = definition.Id;
            _api = definition.Api;
            _baseUrl = definition.BaseUrl;

            Models = new ObservableCollection<ModelEditorViewModel>();
            foreach (ModelDefinition model in definition.Models)
            {
                Models.Add(new ModelEditorViewModel(model));
            }

            AddModelCommand = new RelayCommand(AddModel);
            RemoveModelCommand = new RelayCommand(parameter => RemoveModel(parameter as ModelEditorViewModel));
            _selectedModel = Models.FirstOrDefault();
        }

        public ObservableCollection<ModelEditorViewModel> Models { get; }

        public RelayCommand AddModelCommand { get; }

        public RelayCommand RemoveModelCommand { get; }

        public ModelEditorViewModel SelectedModel
        {
            get { return _selectedModel; }
            set { SetProperty(ref _selectedModel, value); }
        }

        public string Id
        {
            get { return _id; }
            set { SetProperty(ref _id, value); }
        }

        public string DisplayName
        {
            get { return _displayName; }
            set { SetProperty(ref _displayName, value); }
        }

        public string Api
        {
            get { return _api; }
            set { SetProperty(ref _api, value); }
        }

        public string BaseUrl
        {
            get { return _baseUrl; }
            set { SetProperty(ref _baseUrl, value); }
        }

        public string ApiKey
        {
            get { return _apiKey; }
            set
            {
                if (SetProperty(ref _apiKey, value))
                {
                    OnPropertyChanged(nameof(KeyStatus));
                }
            }
        }

        public string KeyStatus
        {
            get { return string.IsNullOrWhiteSpace(_apiKey) ? "未配置密钥" : "已配置（保存后 DPAPI 加密）"; }
        }

        public ProviderDefinition ToDefinition(out string error)
        {
            error = null;
            string id = (_id ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                error = "Provider ID 不能为空。";
                return null;
            }

            var definition = new ProviderDefinition
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(_displayName) ? id : _displayName.Trim(),
                Api = _api,
                BaseUrl = (_baseUrl ?? string.Empty).Trim(),
                Temperature = _definition.Temperature,
                TimeoutSeconds = _definition.TimeoutSeconds,
                MaxRetries = _definition.MaxRetries,
                Compat = _definition.Compat == null ? new OpenAiCompat() : _definition.Compat.Clone()
            };

            foreach (ModelEditorViewModel model in Models)
            {
                ModelDefinition value = model.ToModel(out string modelError);
                if (!string.IsNullOrEmpty(modelError))
                {
                    error = modelError;
                    return null;
                }

                definition.Models.Add(value);
            }

            return definition;
        }

        private void AddModel()
        {
            var model = new ModelEditorViewModel(new ModelDefinition
            {
                Id = "model-id",
                Name = "新模型",
                ContextWindow = 128000,
                MaxOutputTokens = 8192
            });
            Models.Add(model);
            SelectedModel = model;
        }

        private void RemoveModel(ModelEditorViewModel model)
        {
            if (model != null)
            {
                Models.Remove(model);
            }
        }
    }

    public sealed class ModelEditorViewModel : ObservableObject
    {
        private string _id;
        private string _name;
        private int _contextWindow;
        private int _maxOutputTokens;
        private bool _supportsImages;
        private bool _supportsReasoning;

        public ModelEditorViewModel(ModelDefinition model)
        {
            _id = model.Id;
            _name = model.Name;
            _contextWindow = model.ContextWindow;
            _maxOutputTokens = model.MaxOutputTokens;
            _supportsImages = model.SupportsImages;
            _supportsReasoning = model.SupportsReasoning;
        }

        public string Id
        {
            get { return _id; }
            set { SetProperty(ref _id, value); }
        }

        public string Name
        {
            get { return _name; }
            set { SetProperty(ref _name, value); }
        }

        public int ContextWindow
        {
            get { return _contextWindow; }
            set { SetProperty(ref _contextWindow, value); }
        }

        public int MaxOutputTokens
        {
            get { return _maxOutputTokens; }
            set { SetProperty(ref _maxOutputTokens, value); }
        }

        public bool SupportsImages
        {
            get { return _supportsImages; }
            set { SetProperty(ref _supportsImages, value); }
        }

        public bool SupportsReasoning
        {
            get { return _supportsReasoning; }
            set { SetProperty(ref _supportsReasoning, value); }
        }

        public ModelDefinition ToModel(out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(_id))
            {
                error = "模型 ID 不能为空。";
                return null;
            }

            if (_contextWindow < 1024)
            {
                error = "模型 " + _id + " 的上下文窗口至少 1024。";
                return null;
            }

            if (_maxOutputTokens > _contextWindow / 2)
            {
                error = "模型 " + _id + " 的最大输出不能超过上下文窗口的一半。";
                return null;
            }

            return new ModelDefinition
            {
                Id = _id.Trim(),
                Name = string.IsNullOrWhiteSpace(_name) ? _id.Trim() : _name.Trim(),
                ContextWindow = _contextWindow,
                MaxOutputTokens = _maxOutputTokens,
                SupportsImages = _supportsImages,
                SupportsReasoning = _supportsReasoning
            };
        }
    }
}
