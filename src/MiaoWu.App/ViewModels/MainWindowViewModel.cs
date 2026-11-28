using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.App.Services;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Documents;
using MiaoWu.Core.Layout;
using MiaoWu.Core.Markdown;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Settings;
using MiaoWu.Core.Workspaces;
using MiaoWu.Infrastructure.Skills;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 主窗口壳的状态与命令：布局、左栏、中栏问答、右栏文件/预览、浮层、通知与状态持久化。
    /// 窗口级行为（WindowChrome、DPI、最大化）仍在 MainWindow。
    /// </summary>
    public sealed class MainWindowViewModel : ObservableObject, IDisposable
    {
        private readonly ISettingsStore _settingsStore;
        private readonly IWorkspaceStore _workspaceStore;
        private readonly AppSettings _settings;
        private readonly AppServices _services;
        private readonly IDialogService _dialogs;
        private readonly UpdateCoordinator _updates;
        private readonly ISystemNotificationService _systemNotifications;
        private readonly PaneLayoutPolicy _layoutPolicy;
        private readonly Dictionary<string, bool> _runningSessions =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>上一次真正生效的会话选择；后台刷新重建行时 id 不变，不应当成用户切走。</summary>
        private string _lastSessionSelectionId;

        /// <summary>上一次生效的工作区；用于能力市场在切换工作区后重新扫描。</summary>
        private string _lastWorkspaceId;

        private readonly DispatcherTimer _refreshTimer;
        private PaneLayout _layout;
        private double _windowWidth = 1440;
        private double _windowHeight = 900;
        private double _layoutWidth = 1440;
        private double _rightExpandedWidth = 580;
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
            AppServices services = null,
            ConfirmPromptViewModel confirmPrompt = null,
            ISystemNotificationService systemNotifications = null)
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
            _systemNotifications = systemNotifications;
            if (services != null)
            {
                Dispatcher dispatcher = Application.Current == null
                    ? Dispatcher.CurrentDispatcher
                    : Application.Current.Dispatcher;
                _updates = new UpdateCoordinator(services, dispatcher);
            }

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

            // 启动默认收起右栏（文件 / 预览），把中间问答放到最大；用户仍可用
            // Ctrl+J 或会话头按钮展开。这里不恢复上次的展开状态，避免冷启动又变成三栏挤在一起。
            _isRightPaneCollapsed = true;
            _layout = _layoutPolicy.Compute(_windowWidth, _isRightPaneCollapsed);

            WorkspaceCollection collection = LoadWorkspaces(_workspaceStore);

            // 确认浮层先于左栏创建：左栏的删除文件确认也走同一份浮层。
            ConfirmPrompt = confirmPrompt ?? new ConfirmPromptViewModel();

            Left = new LeftPaneViewModel(
                sessionCatalog,
                fileCatalog,
                sessionEditor,
                fileEditor,
                folderPicker,
                filePicker,
                dialogs,
                collection,
                now(),
                _updates);

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
            Left.ScheduledTasksRequested += (sender, args) => ToggleSchedules();
            Left.MarketplaceRequested += (sender, args) => ToggleMarket();
            Left.SessionRenamed += OnLeftSessionRenamed;
            Left.SessionDeleteRequested += OnLeftSessionDeleteRequested;
            Left.SessionExportRequested += OnLeftSessionExportRequested;
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

                Settings = new SettingsViewModel(services, _updates, dialogs);
                Onboarding = new OnboardingViewModel(services, folderPicker);
                Onboarding.Completed += OnOnboardingCompleted;
                Onboarding.CloseRequested += OnOnboardingCloseRequested;

                Schedules = new SchedulesViewModel(
                    services.Schedules,
                    () => Conversation != null && Conversation.HasSession
                        ? Conversation.SessionId
                        : Left.ActiveSessionId,
                    () => Left.ActiveWorkspace == null ? null : Left.ActiveWorkspace.Id,
                    id => Left.FindWorkspaceName(id),
                    () => Left.Workspaces.Select(item => item.Model).ToList(),
                    () => services.Models.Models,
                    () => services.ResolveDefaultModel(),
                    action =>
                    {
                        Application app = Application.Current;
                        if (app == null)
                        {
                            action();
                        }
                        else
                        {
                            app.Dispatcher.BeginInvoke(action);
                        }
                    },
                    (title, message, confirmText, cancelText, destructive) =>
                        ConfirmPrompt.Confirm(title, message, confirmText, cancelText, destructive),
                    () => new DateTimeOffset(now()));

                Market = new MarketViewModel(
                    new SkillCatalogService(services.Paths.UserSkillsDirectory),
                    () => Left.ActiveWorkspace == null ? null : Left.ActiveWorkspace.Path,
                    () => Left.ActiveWorkspace == null ? null : Left.ActiveWorkspace.Name,
                    folderPicker,
                    clipboard,
                    path => Process.Start(path),
                    folder => Process.Start("explorer.exe", "\"" + folder + "\""));

                Conversation.SessionListChanged += OnConversationSessionListChanged;
                Schedules.PropertyChanged += OnPagePropertyChanged;
                Market.PropertyChanged += OnPagePropertyChanged;
                Conversation.SettingsRequested += (sender, args) => ShowSettings();
                Conversation.SessionPinRequested += OnConversationSessionPinRequested;
                Conversation.SessionArchiveRequested += OnConversationSessionArchiveRequested;
                Left.SessionSelectionChanged += OnSessionSelectionChanged;
                Left.NewSessionRequested += (sender, args) => RequestNewSession();
                services.Runtime.SessionChanged += OnRuntimeSessionChanged;
                services.Approvals.Changed += OnApprovalChanged;
                services.Questions.Changed += OnQuestionChanged;

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
            OnPropertyChanged(nameof(ConfirmPrompt));
            OnPropertyChanged(nameof(Conversation));
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(Schedules));
            OnPropertyChanged(nameof(Market));
            OnPropertyChanged(nameof(Onboarding));
            OnPropertyChanged(nameof(IsOnboarding));
            OnPropertyChanged(nameof(Notifications));
            SyncPageStates();
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

        /// <summary>通用二次确认浮层：删除会话 / 删除文件 / 清除会话 / 撤销写入 / 移除工作区。</summary>
        public ConfirmPromptViewModel ConfirmPrompt { get; }

        public ConversationViewModel Conversation { get; }

        public SettingsViewModel Settings { get; }

        /// <summary>定时任务页面：左栏入口打开，占满中/右两张面板的原位置。</summary>
        public SchedulesViewModel Schedules { get; }

        /// <summary>能力市场页面：左栏入口打开，与定时任务页同一宿主位置。</summary>
        public MarketViewModel Market { get; }

        /// <summary>定时任务或能力市场任一页面打开；中/右栏原内容整体让位。</summary>
        public bool IsPageOpen
        {
            get
            {
                return (Schedules != null && Schedules.IsOpen)
                    || (Market != null && Market.IsOpen);
            }
        }

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

            OnPropertyChanged(nameof(LeftPaneWidth));
            OnPropertyChanged(nameof(MiddlePaneWidth));
            OnPropertyChanged(nameof(RightPaneWidth));
            OnPropertyChanged(nameof(RightExpandedWidth));
            OnPropertyChanged(nameof(LeftPaneMax));
            OnPropertyChanged(nameof(RightPaneMax));
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
            ToggleMarket();
        }

        /// <summary>左栏「能力市场」入口：打开 / 收起页面态；与定时任务页互斥。</summary>
        public void ToggleMarket()
        {
            if (Market == null)
            {
                Overlay.Show("能力市场不可用", "当前进程没有装载技能服务，无法打开能力市场。");
                return;
            }

            if (Market.IsOpen)
            {
                Market.Close();
                return;
            }

            if (Schedules != null && Schedules.IsOpen)
            {
                Schedules.Close();
            }

            CloseSettings();
            Overlay.Close();
            Market.Open();
        }

        /// <summary>左栏「定时任务」入口：打开 / 收起页面态，不另外开窗口。</summary>
        public void ToggleSchedules()
        {
            if (Schedules == null)
            {
                Overlay.Show("定时任务不可用", "当前进程没有装载定时任务服务，无法打开定时任务页面。");
                return;
            }

            if (Schedules.IsOpen)
            {
                Schedules.Close();
                return;
            }

            if (Market != null && Market.IsOpen)
            {
                Market.Close();
            }

            CloseSettings();
            Overlay.Close();
            Schedules.Open();
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

            FireAndForget(CreateNewSessionAsync());
        }

        /// <summary>新建后确保左栏选中新会话；右栏已经切过去，左栏不能还停在旧会话上。</summary>
        private async System.Threading.Tasks.Task CreateNewSessionAsync()
        {
            if (Conversation == null)
            {
                return;
            }

            await Conversation.NewSessionAsync();
            if (Conversation == null || !Conversation.HasSession)
            {
                return;
            }

            Left.SelectSession(Conversation.SessionId);
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

        /// <summary>
        /// 进入主界面后由 AppHost 调用：自动更新开启时安排第一次后台检查。
        /// 离屏快照不调用，避免快照触发真实网络请求。
        /// </summary>
        public void StartAutoUpdate()
        {
            if (_updates != null)
            {
                _updates.Start();
            }
        }

        /// <summary>退出或窗口关闭时停止自动更新计时器。</summary>
        public void StopAutoUpdate()
        {
            if (_updates != null)
            {
                _updates.Stop();
            }
        }

        /// <summary>由 AppHost.ShutdownAsync 调用：不再弹关闭确认，直接落盘。</summary>
        public void PrepareExit()
        {
            IsExiting = true;
            TrySaveState();
            SuspendConversation();
            StopAutoUpdate();
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

            if (_updates != null)
            {
                _updates.Dispose();
            }

            if (_services != null)
            {
                _services.Runtime.SessionChanged -= OnRuntimeSessionChanged;
                _services.Approvals.Changed -= OnApprovalChanged;
                _services.Questions.Changed -= OnQuestionChanged;
            }

            if (Schedules != null)
            {
                Schedules.PropertyChanged -= OnPagePropertyChanged;
                Schedules.Dispose();
            }

            if (Market != null)
            {
                Market.PropertyChanged -= OnPagePropertyChanged;
                Market.Dispose();
            }

            if (Conversation != null)
            {
                Conversation.SessionPinRequested -= OnConversationSessionPinRequested;
                Conversation.SessionArchiveRequested -= OnConversationSessionArchiveRequested;
                Conversation.Dispose();
            }

            foreach (NotificationViewModel notification in Notifications)
            {
                notification.MarkResolved();
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

        private void OnLeftSessionExportRequested(object sender, SessionActionRequestedEventArgs e)
        {
            FireAndForget(ExportSessionAsync(e));
        }

        /// <summary>左栏 / 右栏会话菜单共用：读取 transcript 后交给 Exporter 落盘。</summary>
        private async System.Threading.Tasks.Task ExportSessionAsync(SessionActionRequestedEventArgs e)
        {
            if (_services == null || e == null || e.Workspace == null)
            {
                return;
            }

            try
            {
                ConversationTranscript transcript = await _services
                    .GetStore(e.Workspace.Model)
                    .LoadAsync(e.SessionId, System.Threading.CancellationToken.None);

                if (transcript == null)
                {
                    Overlay.Show("导出失败", "会话内容为空，无法导出。");
                    return;
                }

                string folder = _services.Exporter.ExportSession(transcript, e.Workspace.Model.Path);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception exception)
            {
                Overlay.Show("导出失败", exception.Message);
            }
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
            SyncConversationSessionState();
            RaiseStateChanged();
        }

        private void OnPagePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e != null && string.Equals(e.PropertyName, "IsOpen", StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(IsPageOpen));
                SyncPageStates();
            }
        }

        private void SyncPageStates()
        {
            Left.SetPageStates(
                Schedules != null && Schedules.IsOpen,
                Market != null && Market.IsOpen);
        }

        private void OnConversationSessionPinRequested(object sender, EventArgs e)
        {
            ToggleActiveSessionFlag(false);
        }

        private void OnConversationSessionArchiveRequested(object sender, EventArgs e)
        {
            ToggleActiveSessionFlag(true);
        }

        /// <summary>右侧“更多”菜单与左栏会话菜单共用同一组动作；这里只转发到当前会话行。</summary>
        private void ToggleActiveSessionFlag(bool archive)
        {
            if (Conversation == null || !Conversation.HasSession || Left.ActiveWorkspace == null)
            {
                return;
            }

            SessionRowViewModel row = Left.ActiveWorkspace.SelectedSession;
            if (row == null || row.Session == null
                || !string.Equals(row.Session.Id, Conversation.SessionId, StringComparison.Ordinal))
            {
                return;
            }

            if (archive)
            {
                Left.ActiveWorkspace.ToggleSessionArchive(row);
            }
            else
            {
                Left.ActiveWorkspace.ToggleSessionPin(row);
            }

            SyncConversationSessionState();
        }

        /// <summary>把左栏当前会话行的置顶 / 归档状态同步给右侧菜单文案。</summary>
        private void SyncConversationSessionState()
        {
            if (Conversation == null)
            {
                return;
            }

            SessionRowViewModel row = Left.ActiveWorkspace == null
                ? null
                : Left.ActiveWorkspace.SelectedSession;

            bool matches = row != null
                && row.Session != null
                && string.Equals(row.Session.Id, Conversation.SessionId, StringComparison.Ordinal);

            Conversation.ApplySessionFlags(
                matches && row.IsPinned,
                matches && row.IsArchived);
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
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

            // 用户切到另一个会话意味着离开整页（定时任务 / 能力市场）；后台刷新会重建左栏会话行，
            // 即使 id 没变也会触发一次 SelectedSession 变更，不能把它当成用户切走。
            string activeSessionId = Left.ActiveSessionId;
            bool sessionChanged = !string.Equals(
                _lastSessionSelectionId,
                activeSessionId,
                StringComparison.Ordinal);
            _lastSessionSelectionId = activeSessionId;
            if (sessionChanged)
            {
                if (Schedules != null && Schedules.IsOpen)
                {
                    Schedules.Close();
                }

                if (Market != null && Market.IsOpen)
                {
                    Market.Close();
                }
            }

            WorkspaceItemViewModel workspace = Left.ActiveWorkspace;
            Workspace model = workspace == null ? null : workspace.Model;
            string sessionId = Left.ActiveSessionId;
            string workspaceId = model == null ? null : model.Id;
            bool workspaceChanged = !string.Equals(_lastWorkspaceId, workspaceId, StringComparison.Ordinal);
            _lastWorkspaceId = workspaceId;
            if (workspaceChanged && Market != null && Market.IsOpen)
            {
                Market.OnWorkspaceChanged();
            }

            if (model == null)
            {
                // 没有活动工作区时必须把右栏的工作区也清掉，否则 CanSend 仍为 true。
                Conversation.SetWorkspace(null);
                DiscardPendingNewSession();
                SyncConversationSessionState();
                return;
            }

            Conversation.SetWorkspace(model);

            if (string.IsNullOrEmpty(sessionId))
            {
                DiscardPendingNewSession();
                SyncConversationSessionState();
                return;
            }

            if (!string.Equals(Conversation.SessionId, sessionId, StringComparison.Ordinal))
            {
                FireAndForget(SwitchSessionAsync(model, sessionId));
            }

            SyncConversationSessionState();
        }

        /// <summary>
        /// 切到另一个会话 / 工作区：先丢弃上一个“新建但还没发消息”的空白会话，再打开目标，
        /// 最后刷新左栏把空记录移出列表。
        /// </summary>
        private async System.Threading.Tasks.Task SwitchSessionAsync(Workspace model, string sessionId)
        {
            if (Conversation == null)
            {
                return;
            }

            Workspace discarded = await Conversation.DiscardPendingNewSessionAsync();
            if (Conversation == null)
            {
                return;
            }

            await Conversation.AttachAsync(model, sessionId);
            if (discarded != null)
            {
                Left.RefreshSessions(discarded);
            }
        }

        private void DiscardPendingNewSession()
        {
            if (Conversation == null || !Conversation.HasPendingNewSession)
            {
                return;
            }

            FireAndForget(DiscardPendingNewSessionAsync());
        }

        private async System.Threading.Tasks.Task DiscardPendingNewSessionAsync()
        {
            if (Conversation == null)
            {
                return;
            }

            Workspace discarded = await Conversation.DiscardPendingNewSessionAsync();
            if (discarded != null)
            {
                Left.RefreshSessions(discarded);
            }
        }

        private void OnConversationSessionListChanged(object sender, EventArgs e)
        {
            // 先把左栏模型选中项对齐到右栏会话，再刷新；否则 RefreshSessions 会按旧 id
            // 重新选中旧会话，触发一次假的“切走”，把刚新建的空白会话丢掉。
            if (Conversation != null
                && !string.IsNullOrEmpty(Conversation.SessionId)
                && Left.ActiveWorkspace != null
                && ReferenceEquals(Left.ActiveWorkspace.Model, Conversation.Workspace))
            {
                Left.AlignSelectedSession(Conversation.SessionId);
            }

            Left.RefreshSessions();
            if (Conversation != null && !string.IsNullOrEmpty(Conversation.SessionId))
            {
                Left.SelectSession(Conversation.SessionId);
            }
            else
            {
                OnSessionSelectionChanged(this, EventArgs.Empty);
            }

            SyncConversationSessionState();
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
                // 普通提示不再进应用内通知（输入卡只留给审批与提问），改走系统通知；
                // 窗口在前台时由通知服务自己压掉，避免在用户眼前弹气泡。
                if (_systemNotifications != null)
                {
                    _systemNotifications.Notify(
                        "回答完成",
                        string.IsNullOrWhiteSpace(args.Title) ? "后台会话已完成" : args.Title);
                }
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
                    BuildApprovalTitle(request),
                    request.Target + (string.IsNullOrWhiteSpace(request.Summary) ? string.Empty : "\n" + request.Summary),
                    true,
                    ResolveApproval,
                    request.SessionId,
                    request.RequestId,
                    request.CanApproveForSession,
                    request.Diff,
                    request.CanShowDiff));
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

        /// <summary>
        /// 提问与审批共用覆盖输入卡的弹窗：提问是选择题，也可以自己写一句或跳过；
        /// 弹窗本身承担答复入口，消息流里只留工具结果。
        /// </summary>
        private void OnQuestionChanged(object sender, UserQuestionEventArgs args)
        {
            if (args == null || args.Request == null)
            {
                return;
            }

            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => OnQuestionChanged(sender, args)));
                return;
            }

            UserQuestionRequest request = args.Request;
            if (args.Kind == UserQuestionChangeKind.Requested)
            {
                AddNotification(NotificationViewModel.CreateQuestion(
                    request,
                    AnswerQuestion,
                    SkipQuestion,
                    AnswerQuestionText));
                return;
            }

            for (int i = Notifications.Count - 1; i >= 0; i--)
            {
                NotificationViewModel notification = Notifications[i];
                if (notification.IsQuestion
                    && string.Equals(notification.RequestId, request.RequestId, StringComparison.Ordinal))
                {
                    RemoveNotification(notification);
                }
            }
        }

        private void AnswerQuestion(string requestId, IReadOnlyList<string> optionIds)
        {
            if (_services == null)
            {
                return;
            }

            _services.Questions.TryAnswer(requestId, optionIds);
        }

        private void SkipQuestion(string requestId)
        {
            if (_services == null)
            {
                return;
            }

            _services.Questions.TryCancel(requestId);
        }

        /// <summary>自由文本回答；服务端会再次校验“是否允许自由文本”。</summary>
        private void AnswerQuestionText(string requestId, string text)
        {
            if (_services == null)
            {
                return;
            }

            _services.Questions.TryAnswerText(requestId, text);
        }

        /// <summary>审批标题带上风险级别：通知是审批的唯一入口，风险要和动作放在一起。</summary>
        private static string BuildApprovalTitle(ToolApprovalRequest request)
        {
            string risk = request.Risk == ToolRisk.High
                ? "高风险"
                : (request.Risk == ToolRisk.Medium ? "中风险" : null);
            return string.IsNullOrEmpty(risk)
                ? "需要审批 · " + request.Title
                : "需要审批 · " + request.Title + " · " + risk;
        }

        private void AddNotification(NotificationViewModel notification)
        {
            if (notification == null)
            {
                return;
            }

            // 审批与提问都必须显式处理，不能因为栈满被挤掉；多了就整栈滚动。
            Notifications.Add(notification);
        }

        private void RemoveNotification(NotificationViewModel notification)
        {
            if (notification == null)
            {
                return;
            }

            notification.MarkResolved();
            Notifications.Remove(notification);
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

            // 引导刚写入供应商与密钥：立刻按 /models 补齐该供应商的完整模型列表。
            if (_services != null)
            {
                _services.RefreshProviderModelsInBackground();
            }
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
