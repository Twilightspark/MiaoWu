using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.App.Services;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Documents;
using MiaoWu.Core.Layout;
using MiaoWu.Core.Markdown;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Settings;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 主窗口壳的状态与命令：布局、左栏、中栏、右栏会话、浮层、通知与状态持久化。
    /// 窗口级行为（WindowChrome、DPI、最大化）仍在 MainWindow。
    /// </summary>
    public sealed class MainWindowViewModel : ObservableObject, IDisposable
    {
        private const double WindowButtonsWidth = 132;
        private const double ToggleGap = 8;
        private const double ToggleSize = 28;

        private readonly ISettingsStore _settingsStore;
        private readonly IWorkspaceStore _workspaceStore;
        private readonly AppSettings _settings;
        private readonly AppServices _services;
        private readonly IDialogService _dialogs;
        private readonly PaneLayoutPolicy _layoutPolicy;
        private readonly Dictionary<string, bool> _runningSessions =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private readonly DispatcherTimer _refreshTimer;
        private PaneLayout _layout;
        private double _windowWidth = 1440;
        private double _windowHeight = 900;
        private double _layoutWidth = 1440;
        private double _rightExpandedWidth = 428;
        private double _rightPaneVisualWidth = 428;
        private bool _isRightPaneCollapsed;
        private bool _isMaximized;
        private bool _isOnboarding;
        private bool _hasPendingApproval;
        private bool _isExiting;
        private bool _trayAvailable = true;
        private string _lastSaveError;

        public MainWindowViewModel(
            ISettingsStore settingsStore,
            IWorkspaceStore workspaceStore,
            ISessionCatalog sessionCatalog,
            IFileCatalog fileCatalog,
            ISessionEditor sessionEditor,
            IFileEditor fileEditor,
            IFilePreviewService previewService,
            IFolderPicker folderPicker,
            IFilePicker filePicker,
            IDialogService dialogs,
            IClipboardService clipboard,
            Func<DateTime> clock = null,
            AppServices services = null)
        {
            _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
            if (sessionCatalog == null)
            {
                throw new ArgumentNullException(nameof(sessionCatalog));
            }

            if (fileCatalog == null)
            {
                throw new ArgumentNullException(nameof(fileCatalog));
            }

            if (sessionEditor == null)
            {
                throw new ArgumentNullException(nameof(sessionEditor));
            }

            if (fileEditor == null)
            {
                throw new ArgumentNullException(nameof(fileEditor));
            }

            if (previewService == null)
            {
                throw new ArgumentNullException(nameof(previewService));
            }

            if (folderPicker == null)
            {
                throw new ArgumentNullException(nameof(folderPicker));
            }

            if (filePicker == null)
            {
                throw new ArgumentNullException(nameof(filePicker));
            }

            if (dialogs == null)
            {
                throw new ArgumentNullException(nameof(dialogs));
            }

            if (clipboard == null)
            {
                throw new ArgumentNullException(nameof(clipboard));
            }

            Func<DateTime> now = clock ?? (() => DateTime.Now);
            _services = services;
            _dialogs = dialogs;

            _settings = UiSettingsPolicy.Normalize(
                services != null ? services.Settings : (_settingsStore.Load() ?? new AppSettings()),
                new UiLimits());

            _isOnboarding = services != null && _settings.FirstLaunch;

            _windowWidth = _settings.Ui.WindowWidth;
            _windowHeight = _settings.Ui.WindowHeight;
            _layoutWidth = _windowWidth;

            // 数值与 Themes/Tokens.xaml 中的 Metric.* 保持一致。
            _layoutPolicy = new PaneLayoutPolicy(new PaneMetrics());
            _layoutPolicy.SetPreferred(_settings.Ui.LeftPaneWidth, _settings.Ui.RightPaneWidth);
            _isRightPaneCollapsed = _settings.Ui.IsRightPaneCollapsed;
            _layout = _layoutPolicy.Compute(_windowWidth, _isRightPaneCollapsed);

            WorkspaceCollection collection = LoadWorkspaces(_workspaceStore);
            Left = new LeftPaneViewModel(
                sessionCatalog,
                fileCatalog,
                sessionEditor,
                fileEditor,
                folderPicker,
                filePicker,
                dialogs,
                collection,
                now());

            Canvas = new CanvasViewModel(
                previewService,
                clipboard,
                filePicker,
                new MarkdownAstCache(),
                _settings.Ui,
                now());

            Overlay = new OverlayViewModel();
            ClosePrompt = new ClosePromptViewModel();
            Notifications = new ObservableCollection<NotificationViewModel>();

            ToggleRightPaneCommand = new RelayCommand(ToggleRightPane);
            OpenSettingsCommand = new RelayCommand(ShowSettings);
            CloseSettingsCommand = new RelayCommand(CloseSettings);
            NewSessionCommand = new RelayCommand(RequestNewSession);
            ExportSessionCommand = new RelayCommand(() => ConversationCommand("export"));
            ExitCommand = new RelayCommand(RequestExit);

            Left.StateChanged += OnChildStateChanged;
            Left.FileOpenRequested += OnFileOpenRequested;
            Left.SettingsRequested += (sender, args) => ShowSettings();
            Left.MarketplaceRequested += (sender, args) => ShowMarketplacePlaceholder();
            Left.ScheduledTasksRequested += (sender, args) => ShowScheduledTasksPlaceholder();
            Left.SessionRenamed += OnLeftSessionRenamed;
            Left.SessionDeleteRequested += OnLeftSessionDeleteRequested;
            Left.ClearSessionsRequested += OnLeftClearSessionsRequested;
            Left.FileRenamed += OnLeftFileRenamed;
            Left.FileDeleted += OnLeftFileDeleted;
            Canvas.StateChanged += OnChildStateChanged;
            Canvas.AddToConversationRequested += OnAddToConversationRequested;
            Canvas.FileOpened += OnCanvasFileOpened;
            Canvas.FileClosed += OnCanvasFileClosed;

            // 启动恢复的文件标签也要在左栏文件树里显示为已打开。
            foreach (DocumentTabViewModel tab in Canvas.Tabs)
            {
                if (tab.IsFileTab)
                {
                    Left.MarkFileOpened(tab.FilePath);
                }
            }

            if (services != null)
            {
                Conversation = new ConversationViewModel(
                    services,
                    dialogs,
                    clipboard,
                    folderPicker,
                    filePicker,
                    now);

                Settings = new SettingsViewModel(services);
                Onboarding = new OnboardingViewModel(services, folderPicker);
                Onboarding.Completed += OnOnboardingCompleted;
                Onboarding.CloseRequested += OnOnboardingCloseRequested;

                Conversation.SessionListChanged += OnConversationSessionListChanged;
                Conversation.SettingsRequested += (sender, args) => ShowSettings();
                Left.SessionSelectionChanged += OnSessionSelectionChanged;
                Left.NewSessionRequested += (sender, args) => RequestNewSession();
                services.Runtime.SessionChanged += OnRuntimeSessionChanged;
                services.Approvals.Changed += OnApprovalChanged;

                _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(450)
                };
                _refreshTimer.Tick += OnRefreshTick;
                _refreshTimer.Start();

                if (!string.IsNullOrEmpty(_settings.Ui.ActiveSessionId))
                {
                    Left.SelectSession(_settings.Ui.ActiveSessionId);
                }

                OnSessionSelectionChanged(this, EventArgs.Empty);
            }

            ApplyLayout(_layoutWidth);
            OnPropertyChanged(nameof(Left));
            OnPropertyChanged(nameof(Canvas));
            OnPropertyChanged(nameof(Overlay));
            OnPropertyChanged(nameof(ClosePrompt));
            OnPropertyChanged(nameof(Conversation));
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(Onboarding));
            OnPropertyChanged(nameof(IsOnboarding));
            OnPropertyChanged(nameof(Notifications));
        }

        /// <summary>左栏、中栏或窗口状态变化时触发，由 MainWindow 做防抖保存。</summary>
        public event EventHandler StateChanged;

        /// <summary>真正退出（托盘退出或关闭且未启用托盘）时触发，由 App 统一关停运行时。</summary>
        public event EventHandler ExitRequested;

        /// <summary>窗口隐藏到托盘时触发一次，用于首次提示气泡。</summary>
        public event EventHandler HiddenToTray;

        public string Title
        {
            get { return Strings.AppName; }
        }

        public LeftPaneViewModel Left { get; }

        public CanvasViewModel Canvas { get; }

        public OverlayViewModel Overlay { get; }

        /// <summary>关闭确认浮层；仅当 CloseAction=Ask 时打开。</summary>
        public ClosePromptViewModel ClosePrompt { get; }

        public ConversationViewModel Conversation { get; }

        public SettingsViewModel Settings { get; }

        public OnboardingViewModel Onboarding { get; }

        /// <summary>首次启动时主窗口客户区只显示引导页，完成后切换为三栏工作台。</summary>
        public bool IsOnboarding
        {
            get { return _isOnboarding; }
        }

        public ObservableCollection<NotificationViewModel> Notifications { get; }

        public RelayCommand ToggleRightPaneCommand { get; }

        public RelayCommand OpenSettingsCommand { get; }

        public RelayCommand CloseSettingsCommand { get; }

        public RelayCommand NewSessionCommand { get; }

        public RelayCommand ExportSessionCommand { get; }

        public RelayCommand ExitCommand { get; }

        public AppSettings SettingsModel
        {
            get { return _settings; }
        }

        public bool IsMaximized
        {
            get { return _isMaximized; }
            set { SetProperty(ref _isMaximized, value); }
        }

        public bool HasPendingApproval
        {
            get { return _hasPendingApproval; }
            set { SetProperty(ref _hasPendingApproval, value); }
        }

        public bool IsExiting
        {
            get { return _isExiting; }
            private set { SetProperty(ref _isExiting, value); }
        }

        public CloseWindowAction CloseAction
        {
            get
            {
                AgentSettings agent = _services != null ? _services.Settings.Agent : _settings.Agent;
                return agent == null ? CloseWindowAction.Ask : agent.CloseAction;
            }
        }

        /// <summary>
        /// 记住关闭选择并立即落盘。服务未装配（离屏快照）时只更新内存模型，
        /// 不写 config.json；设置页打开时同步刷新下拉选中项。
        /// </summary>
        public void RememberCloseAction(CloseWindowAction action)
        {
            if (action == CloseWindowAction.Ask)
            {
                return;
            }

            if (_services != null)
            {
                _services.Settings.Agent.CloseAction = action;
                try
                {
                    _services.SaveSettings();
                }
                catch (Exception exception)
                {
                    if (_services.Logger != null)
                    {
                        _services.Logger.Warn(
                            "app.closeAction",
                            "保存关闭行为失败：" + exception.Message);
                    }
                }
            }
            else
            {
                _settings.Agent.CloseAction = action;
            }

            if (Settings != null)
            {
                Settings.NotifyCloseActionChanged();
            }
        }

        public bool IsRightPaneCollapsed
        {
            get { return _isRightPaneCollapsed; }
            set
            {
                if (SetProperty(ref _isRightPaneCollapsed, value))
                {
                    ApplyLayout(_layoutWidth);
                    OnPropertyChanged(nameof(ToggleRightPaneToolTip));
                    RaiseStateChanged();
                }
            }
        }

        public string ToggleRightPaneToolTip
        {
            get
            {
                return _isRightPaneCollapsed
                    ? Strings.ToggleRightPaneExpand
                    : Strings.ToggleRightPaneCollapse;
            }
        }

        public double LeftPaneWidth
        {
            get { return _layout.Left; }
            set
            {
                if (Math.Abs(value - _layout.Left) < 0.1)
                {
                    return;
                }

                SetLeftPreferred(value);
            }
        }

        public double MiddlePaneWidth
        {
            get { return _layout.Middle; }
        }

        /// <summary>右栏展开时的目标宽度；折叠时为 0 由 CollapsiblePane 动画控制。</summary>
        public double RightExpandedWidth
        {
            get { return _rightExpandedWidth; }
            set
            {
                if (Math.Abs(value - _rightExpandedWidth) < 0.1)
                {
                    return;
                }

                SetRightPreferred(value);
            }
        }

        public double RightPaneWidth
        {
            get { return _layout.Right; }
            set
            {
                if (Math.Abs(value - RightExpandedWidth) < 0.1)
                {
                    return;
                }

                SetRightPreferred(value);
            }
        }

        public double LeftPaneMax { get; private set; }

        public double RightPaneMax { get; private set; }

        /// <summary>顶栏折叠按钮的右边距：展开时贴右栏，折叠时停在窗口按钮左侧。</summary>
        public Thickness TopBarToggleMargin { get; private set; }

        public double PreferredWindowWidth
        {
            get { return _windowWidth; }
        }

        public double PreferredWindowHeight
        {
            get { return _windowHeight; }
        }

        /// <summary>三栏内容设计决定的最小窗口宽度。</summary>
        public double MinimumWindowWidth
        {
            get { return _layoutPolicy.MinimumWindowWidth; }
        }

        /// <summary>三栏内容设计决定的最小窗口高度。</summary>
        public double MinimumWindowHeight
        {
            get { return _layoutPolicy.MinimumWindowHeight; }
        }

        /// <summary>
        /// 上次退出时是否已记录窗口尺寸。首次启动按默认尺寸收敛；
        /// 之后启动恢复记住的尺寸，并始终居中到主屏幕工作区。
        /// </summary>
        public bool HasSavedWindowPlacement
        {
            get { return _settings.Ui.WindowPlacementSaved; }
        }

        public string LastSaveError
        {
            get { return _lastSaveError; }
        }

        public void ApplyLayout(double contentWidth)
        {
            if (contentWidth <= 0)
            {
                return;
            }

            // 这是 Shell 客户区宽度；启动或引导切换时它可能与窗口实际宽度短暂不一致，
            // 不能写回 _windowWidth，否则持久化的窗口尺寸会被错误改写。
            _layoutWidth = contentWidth;

            PaneLayout expanded = _layoutPolicy.Compute(contentWidth, false);
            PaneLayout actual = _layoutPolicy.Compute(contentWidth, _isRightPaneCollapsed);
            _layout = actual;
            _rightExpandedWidth = expanded.Right;
            LeftPaneMax = _layoutPolicy.GetLeftMaximum(contentWidth, actual.Right);
            RightPaneMax = _layoutPolicy.GetRightMaximum(contentWidth, actual.Left);

            UpdateTopBarToggleMargin();

            OnPropertyChanged(nameof(LeftPaneWidth));
            OnPropertyChanged(nameof(MiddlePaneWidth));
            OnPropertyChanged(nameof(RightPaneWidth));
            OnPropertyChanged(nameof(RightExpandedWidth));
            OnPropertyChanged(nameof(LeftPaneMax));
            OnPropertyChanged(nameof(RightPaneMax));
        }

        /// <summary>由 CollapsiblePane 的实际宽度驱动，让顶栏按钮在折叠动画中跟随。</summary>
        public void SetRightPaneVisualWidth(double width)
        {
            if (Math.Abs(width - _rightPaneVisualWidth) < 0.1)
            {
                return;
            }

            _rightPaneVisualWidth = width;
            UpdateTopBarToggleMargin();
        }

        public void UpdateWindowBounds(double width, double height, bool maximized)
        {
            _isMaximized = maximized;

            // 最大化时 ActualWidth/Height 是屏幕尺寸，不能覆盖上次的正常窗口尺寸。
            if (!maximized)
            {
                bool changed = false;
                if (width > 0 && Math.Abs(width - _windowWidth) > 0.1)
                {
                    _windowWidth = width;
                    changed = true;
                }

                if (height > 0 && Math.Abs(height - _windowHeight) > 0.1)
                {
                    _windowHeight = height;
                    changed = true;
                }

                if (width > 0 || height > 0)
                {
                    _settings.Ui.WindowPlacementSaved = true;
                }

                if (changed)
                {
                    OnPropertyChanged(nameof(PreferredWindowWidth));
                    OnPropertyChanged(nameof(PreferredWindowHeight));
                }
            }

            RaiseStateChanged();
        }

        /// <summary>只同步最大化状态，不触碰记忆中的正常窗口尺寸。</summary>
        public void SetWindowMaximized(bool maximized)
        {
            if (_isMaximized == maximized)
            {
                return;
            }

            _isMaximized = maximized;
            RaiseStateChanged();
        }

        /// <summary>把当前界面状态写入 config.json 与 workspaces.json。异常不外抛。</summary>
        public bool TrySaveState()
        {
            try
            {
                SaveStateCore();
                _lastSaveError = null;
                OnPropertyChanged(nameof(LastSaveError));
                return true;
            }
            catch (Exception ex)
            {
                _lastSaveError = ex.Message;
                OnPropertyChanged(nameof(LastSaveError));
                return false;
            }
        }

        public void ShowSettings()
        {            if (Settings == null)
            {
                Overlay.Show(Strings.SettingsPendingTitle, Strings.SettingsPendingBody);
                return;
            }

            Overlay.Close();
            Settings.Open();
        }

        public void CloseSettings()
        {
            if (Settings != null)
            {
                Settings.IsOpen = false;
            }
        }

        public void ShowMarketplacePlaceholder()
        {
            Overlay.Show(Strings.CapabilityMarketPendingTitle, Strings.CapabilityMarketPendingBody);
        }

        public void ShowScheduledTasksPlaceholder()
        {
            Overlay.Show(Strings.ScheduledTasksPendingTitle, Strings.ScheduledTasksPendingBody);
        }

        /// <summary>兼容离屏快照工具的旧入口：没有真实服务时退化为文本浮层。</summary>
        public void ShowSettingsPlaceholder()
        {
            ShowSettings();
        }

        public void RequestNewSession()
        {
            if (Conversation == null)
            {
                return;
            }

            FireAndForget(Conversation.NewSessionAsync());
        }

        public void RequestExit()
        {
            if (IsExiting)
            {
                return;
            }

            IsExiting = true;
            TrySaveState();
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>由 AppHost.ShutdownAsync 调用：不再弹关闭确认，直接落盘。</summary>
        public void PrepareExit()
        {
            IsExiting = true;
            TrySaveState();
            SuspendConversation();
        }

        public void NotifyHiddenToTray()
        {
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>托盘图标是否可用；低完整性进程由 AppHost 置为 false。</summary>
        public bool TrayAvailable
        {
            get { return _trayAvailable; }
        }

        public void SetTrayAvailability(bool available)
        {
            _trayAvailable = available;
            OnPropertyChanged(nameof(TrayAvailable));
            ClosePrompt.SetTrayAvailability(available);
        }

        /// <summary>启动时提示托盘不可用及替代入口；只在服务已装配时显示。</summary>
        public void NotifyTrayUnavailable()
        {
            AddNotification(new NotificationViewModel(
                "系统托盘图标不可用",
                "当前以受限权限运行（例如从 VS Code 或沙箱终端启动），Windows 不会显示托盘图标。"
                + "关闭窗口将最小化到任务栏。请从资源管理器直接运行 MiaoWu.exe；"
                + "若图标仍收在任务栏 ^ 中，可在“设置 > 个性化 > 任务栏 > 其他系统托盘图标”打开 MiaoWu。",
                false,
                RemoveNotification,
                autoDismissAfter: TimeSpan.FromSeconds(12)));
        }

        /// <summary>窗口隐藏时断开重量级订阅，只保留左栏由 SessionChanged 更新。</summary>
        public void SuspendConversation()
        {
            if (Conversation != null)
            {
                Conversation.Detach();
            }
        }

        /// <summary>窗口重新显示时按当前选中会话重挂订阅。</summary>
        public void ResumeConversation()
        {
            OnSessionSelectionChanged(this, EventArgs.Empty);
        }

        public void RequestExportSession()
        {
            ConversationCommand("export");
        }

        public void ReloadWorkspacesFromDisk()
        {
            WorkspaceCollection collection = _workspaceStore.Load() ?? new WorkspaceCollection();
            Left.Reload(collection);
            OnSessionSelectionChanged(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Tick -= OnRefreshTick;
            }

            if (_services != null)
            {
                _services.Runtime.SessionChanged -= OnRuntimeSessionChanged;
                _services.Approvals.Changed -= OnApprovalChanged;
            }

            if (Conversation != null)
            {
                Conversation.Dispose();
            }

            foreach (NotificationViewModel notification in Notifications)
            {
                notification.StopTimer();
            }

            Notifications.Clear();
        }

        private void SaveStateCore()
        {
            UiSettings ui = _settings.Ui;
            ui.WindowWidth = _windowWidth;
            ui.WindowHeight = _windowHeight;
            ui.WindowMaximized = _isMaximized;
            ui.LeftPaneWidth = _layoutPolicy.LeftPreferred;
            ui.RightPaneWidth = _layoutPolicy.RightPreferred;
            ui.IsRightPaneCollapsed = _isRightPaneCollapsed;

            WorkspaceItemViewModel active = Left.ActiveWorkspace;
            ui.ActiveWorkspaceId = active == null ? null : active.Id;
            ui.ActiveSessionId = active == null ? null : active.Model.SelectedSessionId;
            ui.ActiveFilePath = active == null ? null : active.Model.SelectedFilePath;

            Canvas.Save(ui);

            _settingsStore.Save(_settings);
            _workspaceStore.Save(Left.GetCollection());
        }

        private void OnFileOpenRequested(object sender, FileOpenRequestedEventArgs e)
        {
            Canvas.OpenFile(e.FilePath, e.DisplayName);
        }

        private void OnLeftSessionRenamed(object sender, SessionRenamedEventArgs e)
        {
            if (Conversation != null)
            {
                Conversation.ApplySessionTitle(e.SessionId, e.Title);
            }
        }

        private void OnLeftSessionDeleteRequested(object sender, SessionActionRequestedEventArgs e)
        {
            FireAndForget(DeleteSessionAsync(e));
        }

        private void OnLeftClearSessionsRequested(object sender, WorkspaceActionRequestedEventArgs e)
        {
            FireAndForget(ClearWorkspaceSessionsAsync(e.Workspace));
        }

        private void OnLeftFileRenamed(object sender, FileRenamedEventArgs e)
        {
            Canvas.HandleFileRenamed(e.OldPath, e.NewPath);
        }

        private void OnLeftFileDeleted(object sender, FileDeletedEventArgs e)
        {
            Canvas.HandleFileDeleted(e.FilePath);
        }

        /// <summary>左栏删除会话：先关停运行中的会话，再删 JSONL 与索引项。</summary>
        private async System.Threading.Tasks.Task DeleteSessionAsync(SessionActionRequestedEventArgs e)
        {
            if (_services == null || e == null || e.Workspace == null)
            {
                return;
            }

            string title = string.IsNullOrWhiteSpace(e.Title) ? Strings.NewSession : e.Title;
            if (!_dialogs.Confirm(
                Strings.DeleteSession,
                "删除“" + title + "”后，本会话的 JSONL 记录会从工作区移除，无法恢复。",
                Strings.Delete,
                Strings.Cancel))
            {
                return;
            }

            try
            {
                await _services.Runtime.CloseAsync(e.SessionId, System.Threading.CancellationToken.None);

                if (Conversation != null
                    && string.Equals(Conversation.SessionId, e.SessionId, StringComparison.Ordinal))
                {
                    Conversation.Detach();
                }

                await _services.GetStore(e.Workspace.Model).DeleteAsync(
                    e.SessionId,
                    System.Threading.CancellationToken.None);

                Left.RefreshSessions();
                OnSessionSelectionChanged(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                Overlay.Show("删除失败", exception.Message);
            }
        }

        /// <summary>清空工作区会话：先关停该工作区所有会话，再删除记录文件。</summary>
        private async System.Threading.Tasks.Task ClearWorkspaceSessionsAsync(
            WorkspaceItemViewModel workspace)
        {
            if (_services == null || workspace == null)
            {
                return;
            }

            if (!_dialogs.Confirm(
                Strings.ClearWorkspaceSessions,
                "将清除“" + workspace.Name + "”下的全部会话记录，无法恢复；工作区文件不受影响。",
                Strings.ClearWorkspaceSessions,
                Strings.Cancel))
            {
                return;
            }

            try
            {
                var sessions = _services.SessionCatalog.GetSessions(workspace.Model);
                foreach (SessionSummary session in sessions)
                {
                    await _services.Runtime.CloseAsync(
                        session.Id,
                        System.Threading.CancellationToken.None);
                }

                if (Conversation != null && workspace.Model != null
                    && ReferenceEquals(Conversation.Workspace, workspace.Model))
                {
                    Conversation.Detach();
                }

                await _services.SessionCatalog.ClearAsync(
                    workspace.Model,
                    System.Threading.CancellationToken.None);

                workspace.RefreshSessions();
                Left.RefreshSessions();
                OnSessionSelectionChanged(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                Overlay.Show("清除失败", exception.Message);
            }
        }

        private void OnAddToConversationRequested(object sender, EventArgs e)
        {
            Overlay.Show(Strings.AddToConversation, Strings.AddToConversationPendingHint);
        }

        private void OnCanvasFileOpened(object sender, FileOpenedEventArgs e)
        {
            Left.MarkFileOpened(e.FilePath);
        }

        private void OnCanvasFileClosed(object sender, FileOpenedEventArgs e)
        {
            Left.UnmarkFileOpened(e.FilePath);
        }

        private void OnChildStateChanged(object sender, EventArgs e)
        {
            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateTopBarToggleMargin()
        {
            double toggleMargin = Math.Max(
                _rightPaneVisualWidth - ToggleSize,
                WindowButtonsWidth + ToggleGap);

            TopBarToggleMargin = new Thickness(0, 0, toggleMargin, 0);
            OnPropertyChanged(nameof(TopBarToggleMargin));
        }

        private void SetLeftPreferred(double value)
        {
            double clamped = Math.Max(0, Math.Min(value, LeftPaneMax));
            _layoutPolicy.SetPreferred(clamped, _layoutPolicy.RightPreferred);
            ApplyLayout(_layoutWidth);
            RaiseStateChanged();
        }

        private void SetRightPreferred(double value)
        {
            double clamped = Math.Max(0, Math.Min(value, RightPaneMax));
            _layoutPolicy.SetPreferred(_layoutPolicy.LeftPreferred, clamped);
            ApplyLayout(_layoutWidth);
            RaiseStateChanged();
        }

        private void ToggleRightPane()
        {
            IsRightPaneCollapsed = !IsRightPaneCollapsed;
        }

        private void OnSessionSelectionChanged(object sender, EventArgs e)
        {
            if (Conversation == null)
            {
                return;
            }

            WorkspaceItemViewModel workspace = Left.ActiveWorkspace;
            Workspace model = workspace == null ? null : workspace.Model;
            string sessionId = Left.ActiveSessionId;

            if (model == null)
            {
                Conversation.Detach();
                return;
            }

            Conversation.SetWorkspace(model);

            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            if (!string.Equals(Conversation.SessionId, sessionId, StringComparison.Ordinal))
            {
                FireAndForget(Conversation.AttachAsync(model, sessionId));
            }
        }

        private void OnConversationSessionListChanged(object sender, EventArgs e)
        {
            Left.RefreshSessions();
            if (Conversation != null && !string.IsNullOrEmpty(Conversation.SessionId))
            {
                Left.SelectSession(Conversation.SessionId);
            }
            else
            {
                OnSessionSelectionChanged(this, EventArgs.Empty);
            }
        }

        private void OnRuntimeSessionChanged(object sender, AgentSessionSummaryEventArgs args)
        {
            if (args == null)
            {
                return;
            }

            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => OnRuntimeSessionChanged(sender, args)));
                return;
            }

            bool wasRunning;
            _runningSessions.TryGetValue(args.SessionId, out wasRunning);

            if (args.State == AgentSessionState.Running)
            {
                _runningSessions[args.SessionId] = true;
            }
            else
            {
                _runningSessions[args.SessionId] = false;
            }

            if (wasRunning && args.State == AgentSessionState.Idle
                && Conversation != null
                && !string.Equals(args.SessionId, Conversation.SessionId, StringComparison.Ordinal))
            {
                AddNotification(new NotificationViewModel(
                    "回答完成",
                    string.IsNullOrWhiteSpace(args.Title) ? "后台会话已完成" : args.Title,
                    false,
                    RemoveNotification));
            }

            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            }
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
            }

            Left.RefreshSessions();
        }

        private void OnApprovalChanged(object sender, ToolApprovalEventArgs args)
        {
            if (args == null || args.Ticket == null)
            {
                return;
            }

            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => OnApprovalChanged(sender, args)));
                return;
            }

            ToolApprovalRequest request = args.Ticket.Request;
            HasPendingApproval = _services.Approvals.GetPendingCount() > 0;

            if (args.Kind == ToolApprovalChangeKind.Requested)
            {
                AddNotification(new NotificationViewModel(
                    "需要审批 · " + request.Title,
                    request.Target + (string.IsNullOrWhiteSpace(request.Summary) ? string.Empty : "\n" + request.Summary),
                    true,
                    RemoveNotification,
                    ResolveApproval,
                    request.SessionId,
                    request.RequestId,
                    request.CanApproveForSession));
                return;
            }

            for (int i = Notifications.Count - 1; i >= 0; i--)
            {
                NotificationViewModel notification = Notifications[i];
                if (notification.IsApproval
                    && string.Equals(notification.RequestId, request.RequestId, StringComparison.Ordinal))
                {
                    RemoveNotification(notification);
                }
            }

            HasPendingApproval = _services.Approvals.GetPendingCount() > 0;
        }

        private void AddNotification(NotificationViewModel notification)
        {
            if (notification == null)
            {
                return;
            }

            Notifications.Add(notification);
            OnPropertyChanged(nameof(HasNotifications));

            while (Notifications.Count > 3)
            {
                NotificationViewModel oldest = null;
                foreach (NotificationViewModel item in Notifications)
                {
                    if (!item.IsApproval)
                    {
                        oldest = item;
                        break;
                    }
                }

                if (oldest == null)
                {
                    break;
                }

                RemoveNotification(oldest);
            }
        }

        private void RemoveNotification(NotificationViewModel notification)
        {
            if (notification == null)
            {
                return;
            }

            notification.StopTimer();
            Notifications.Remove(notification);
            OnPropertyChanged(nameof(HasNotifications));
        }

        private void ResolveApproval(string requestId, ToolApprovalOutcome outcome)
        {
            if (_services == null)
            {
                return;
            }

            _services.Approvals.TryResolve(
                requestId,
                outcome,
                outcome == ToolApprovalOutcome.Deny ? "用户拒绝。" : null);
            HasPendingApproval = _services.Approvals.GetPendingCount() > 0;
        }

        public bool HasNotifications
        {
            get { return Notifications.Count > 0; }
        }

        private void ConversationCommand(string command)
        {
            if (Conversation == null)
            {
                return;
            }

            if (string.Equals(command, "export", StringComparison.OrdinalIgnoreCase))
            {
                FireAndForget(Conversation.ExportAsync());
            }
        }

        private void OnOnboardingCompleted(object sender, EventArgs e)
        {
            _isOnboarding = false;
            OnPropertyChanged(nameof(IsOnboarding));
            ReloadWorkspacesFromDisk();
            ApplyLayout(_layoutWidth);
        }

        private void OnOnboardingCloseRequested(object sender, EventArgs e)
        {
            RequestExit();
        }

        private void FireAndForget(System.Threading.Tasks.Task task)
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
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            Overlay.Show("操作失败", t.Exception.GetBaseException().Message)));
                    }
                },
                System.Threading.Tasks.TaskScheduler.Default);
        }

        private static WorkspaceCollection LoadWorkspaces(IWorkspaceStore store)
        {
            WorkspaceCollection collection = store.Load() ?? new WorkspaceCollection();
            if (collection.Workspaces == null)
            {
                collection.Workspaces = new List<Workspace>();
            }

            // 不再自动把当前目录/环境变量塞成默认工作区：首个工作区由首次向导或用户手动添加。
            return collection;
        }
    }
}
