using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.App.Services;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;
using MiaoWu.Core.Text;
using MiaoWu.Core.Workspaces;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Tools;

namespace MiaoWu.App.ViewModels
{
    /// <summary>输入框右下角的模型下拉项。</summary>
    public sealed class ModelOption
    {
        public ModelOption(
            string providerId,
            string modelId,
            string displayName,
            string providerName,
            bool isDefault)
        {
            ProviderId = providerId;
            ModelId = modelId;
            DisplayName = displayName;
            ProviderName = providerName;
            IsDefault = isDefault;
        }

        public string ProviderId { get; }

        public string ModelId { get; }

        public string Key
        {
            get { return ProviderId + "/" + ModelId; }
        }

        public string DisplayName { get; }

        public string ProviderName { get; }

        public bool IsDefault { get; }

        /// <summary>下拉行右侧只标「默认」；供应商与模型 ID 放进行的 tooltip。</summary>
        public string Badge
        {
            get { return IsDefault ? "默认" : string.Empty; }
        }

        public string Detail
        {
            get { return ProviderName + " · " + ModelId; }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>模型下拉里按提供商分区的一组模型。</summary>
    public sealed class ModelGroup
    {
        public ModelGroup(string providerId, string providerName)
        {
            ProviderId = providerId;
            ProviderName = providerName;
            Models = new List<ModelOption>();
        }

        public string ProviderId { get; }

        public string ProviderName { get; }

        public List<ModelOption> Models { get; }
    }

    /// <summary>
    /// 右栏会话：订阅一个 AgentSession、渲染消息/工具卡/审批卡、承载输入框。
    /// 切换会话只换订阅，不打断后台正在生成的会话。
    /// </summary>
    public sealed class ConversationViewModel : ObservableObject, IDisposable
    {
        private readonly AppServices _app;
        private readonly IDialogService _dialogs;
        private readonly IClipboardService _clipboard;
        private readonly Dispatcher _dispatcher;
        private readonly Func<DateTime> _clock;

        private readonly Dictionary<string, UserChatItemViewModel> _userItems =
            new Dictionary<string, UserChatItemViewModel>(StringComparer.Ordinal);
        private readonly Dictionary<string, AssistantChatItemViewModel> _assistantItems =
            new Dictionary<string, AssistantChatItemViewModel>(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolCardViewModel> _toolCards =
            new Dictionary<string, ToolCardViewModel>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ApprovalCardViewModel> _approvalCards =
            new Dictionary<string, ApprovalCardViewModel>(StringComparer.Ordinal);
        private readonly DispatcherTimer _markdownTimer;

        private SessionEventPump _pump;
        private Workspace _workspace;
        private string _sessionId;
        private string _title;
        private string _draft;
        private string _statusText;
        private string _usageText;
        private string _contextText;
        private ModelOption _selectedModel;
        private bool _isRunning;
        private bool _isRenaming;
        private string _renameDraft;
        private bool _isAttached;
        private bool _isBusy;
        private bool _isPinned;
        private bool _isArchived;
        private int _attachVersion;

        public ConversationViewModel(
            AppServices app,
            IDialogService dialogs,
            IClipboardService clipboard,
            IFolderPicker folderPicker,
            Func<DateTime> clock = null)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _dialogs = dialogs;
            _clipboard = clipboard;
            _dispatcher = Application.Current == null
                ? Dispatcher.CurrentDispatcher
                : Application.Current.Dispatcher;
            _clock = clock ?? (() => DateTime.Now);

            Items = new ObservableCollection<ChatItemViewModel>();
            ModelOptions = new ObservableCollection<ModelOption>();
            ModelGroups = new ObservableCollection<ModelGroup>();

            SendCommand = new RelayCommand(() => FireAndForget(SendAsync()), () => CanSend);
            StopCommand = new RelayCommand(() => FireAndForget(StopAsync()), () => IsRunning);
            NewSessionCommand = new RelayCommand(() => FireAndForget(NewSessionAsync()), () => _workspace != null);
            RetryCommand = new RelayCommand(() => FireAndForget(RetryLastAsync()), () => HasRetryTarget);
            RenameCommand = new RelayCommand(BeginRename, () => HasSession);
            CommitRenameCommand = new RelayCommand(() => FireAndForget(CommitRenameAsync()));
            CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
            TogglePinCommand = new RelayCommand(
                () => SessionPinRequested?.Invoke(this, EventArgs.Empty),
                () => HasSession);
            ToggleArchiveCommand = new RelayCommand(
                () => SessionArchiveRequested?.Invoke(this, EventArgs.Empty),
                () => HasSession);
            DeleteCommand = new RelayCommand(() => FireAndForget(DeleteAsync()), () => HasSession);
            ExportCommand = new RelayCommand(() => FireAndForget(ExportAsync()), () => HasSession);
            OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
            ClearCommand = new RelayCommand(() => Items.Clear(), () => HasSession);

            _markdownTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(160)
            };
            _markdownTimer.Tick += OnMarkdownTick;
            _markdownTimer.Start();

            _app.Approvals.Changed += OnApprovalChanged;
            _app.ProvidersChanged += OnProvidersChanged;
            RefreshModelOptions();
        }

        public event EventHandler SessionListChanged;

        public event EventHandler SettingsRequested;

        /// <summary>请求主窗口对当前会话执行置顶；状态与左栏会话行保持一致。</summary>
        public event EventHandler SessionPinRequested;

        /// <summary>请求主窗口对当前会话执行归档；状态与左栏会话行保持一致。</summary>
        public event EventHandler SessionArchiveRequested;

        public ObservableCollection<ChatItemViewModel> Items { get; }

        public ObservableCollection<ModelOption> ModelOptions { get; }

        /// <summary>模型下拉的数据源：按提供商分区，区内按模型目录顺序。</summary>
        public ObservableCollection<ModelGroup> ModelGroups { get; }

        public RelayCommand SendCommand { get; }

        public RelayCommand StopCommand { get; }

        public RelayCommand NewSessionCommand { get; }

        public RelayCommand RetryCommand { get; }

        public RelayCommand RenameCommand { get; }

        /// <summary>与左栏会话菜单同名，供共享菜单模板绑定。</summary>
        public RelayCommand BeginRenameCommand
        {
            get { return RenameCommand; }
        }

        public RelayCommand TogglePinCommand { get; }

        public RelayCommand ToggleArchiveCommand { get; }

        public RelayCommand CommitRenameCommand { get; }

        public RelayCommand CancelRenameCommand { get; }

        public RelayCommand DeleteCommand { get; }

        public RelayCommand ExportCommand { get; }

        public RelayCommand OpenSettingsCommand { get; }

        public RelayCommand ClearCommand { get; }

        public Workspace Workspace
        {
            get { return _workspace; }
        }

        public string SessionId
        {
            get { return _sessionId; }
        }

        public string Title
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_title))
                {
                    return HasSession ? "新会话" : "对话";
                }

                return _title;
            }
        }

        public string Draft
        {
            get { return _draft; }
            set
            {
                if (SetProperty(ref _draft, value))
                {
                    OnPropertyChanged(nameof(CanSend));
                    OnPropertyChanged(nameof(IsEmpty));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set
            {
                if (SetProperty(ref _statusText, value))
                {
                    OnPropertyChanged(nameof(HeaderText));
                }
            }
        }

        public string UsageText
        {
            get { return _usageText; }
            private set { SetProperty(ref _usageText, value); }
        }

        public string ContextText
        {
            get { return _contextText; }
            private set
            {
                if (SetProperty(ref _contextText, value))
                {
                    OnPropertyChanged(nameof(HeaderText));
                }
            }
        }

        /// <summary>输入框头部右侧只显示一条：临时状态优先，否则显示上下文用量。</summary>
        public string HeaderText
        {
            get
            {
                return string.IsNullOrWhiteSpace(_statusText) ? _contextText : _statusText;
            }
        }

        public bool IsPinned
        {
            get { return _isPinned; }
        }

        public bool IsArchived
        {
            get { return _isArchived; }
        }

        /// <summary>菜单文案与左栏会话菜单一致，随当前状态切换。</summary>
        public string PinActionText
        {
            get { return _isPinned ? Strings.UnpinSession : Strings.PinSession; }
        }

        public string ArchiveActionText
        {
            get { return _isArchived ? Strings.UnarchiveSession : Strings.ArchiveSession; }
        }

        public string ComposerPlaceholder
        {
            get
            {
                if (_workspace == null)
                {
                    return "先在标题栏选择工作区";
                }

                return "描述你想让 MiaoWu 做的事，Enter 发送";
            }
        }

        public string EmptyTitle
        {
            get { return _workspace == null ? "先选择工作区" : "开始一个新会话"; }
        }

        public string EmptyBody
        {
            get
            {
                return _workspace == null
                    ? "工作区是会话的归属。先在左栏新建工作区，或在标题栏选择已有工作区，然后回到这里输入。"
                    : "在下方描述你想让 MiaoWu 做的事。写文件或执行命令前，它会先征求你的同意。";
            }
        }

        public bool CanPickModel
        {
            get { return ModelOptions.Count > 0; }
        }

        public bool HasModelGroups
        {
            get { return ModelGroups.Count > 0; }
        }

        public string SelectedModelName
        {
            get { return _selectedModel == null ? "选择模型" : _selectedModel.DisplayName; }
        }

        public string ModelEmptyText
        {
            get { return "还没有配置模型。打开“功能设置 > 模型”添加供应商和模型。"; }
        }

        public string ModelToolTip
        {
            get
            {
                return HasModelGroups
                    ? "选择这条消息使用的模型（按提供商分组）"
                    : "还没有配置模型，点击查看设置";
            }
        }

        public string SendToolTip
        {
            get
            {
                if (_workspace == null)
                {
                    return "请先在标题栏选择工作区";
                }

                if (ModelOptions.Count == 0)
                {
                    return "尚未配置模型，请在“功能设置 > 模型”中添加";
                }

                return IsRunning ? "发送（排队）" : "发送（Enter）";
            }
        }

        public ModelOption SelectedModel
        {
            get { return _selectedModel; }
            set
            {
                if (ReferenceEquals(_selectedModel, value))
                {
                    return;
                }

                SelectModel(value);
            }
        }

        /// <summary>模型下拉行点击后调用；先更新本地下一次发送的选择，再同步给已挂载会话。</summary>
        public void SelectModel(ModelOption option)
        {
            if (option == null)
            {
                return;
            }

            _selectedModel = option;
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(SelectedModelName));

            IAgentSession session = CurrentSession;
            if (session != null && !IsRunning)
            {
                try
                {
                    session.SetModelAsync(option.ProviderId, option.ModelId, CancellationToken.None);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>由主窗口同步左栏当前会话行的置顶 / 归档状态，保证两处菜单文案一致。</summary>
        public void ApplySessionFlags(bool pinned, bool archived)
        {
            if (_isPinned == pinned && _isArchived == archived)
            {
                return;
            }

            _isPinned = pinned;
            _isArchived = archived;
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(IsArchived));
            OnPropertyChanged(nameof(PinActionText));
            OnPropertyChanged(nameof(ArchiveActionText));
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    OnPropertyChanged(nameof(CanSend));
                    OnPropertyChanged(nameof(IsIdle));
                    OnPropertyChanged(nameof(SendToolTip));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsIdle
        {
            get { return !_isRunning; }
        }

        public bool HasSession
        {
            get { return !string.IsNullOrEmpty(_sessionId); }
        }

        public bool HasWorkspace
        {
            get { return _workspace != null; }
        }

        /// <summary>没有消息且草稿为空时显示空态；开始输入后空态让位给正文。</summary>
        public bool IsEmpty
        {
            get { return Items.Count == 0 && string.IsNullOrWhiteSpace(_draft); }
        }

        public bool IsRenaming
        {
            get { return _isRenaming; }
            private set { SetProperty(ref _isRenaming, value); }
        }

        public string RenameDraft
        {
            get { return _renameDraft; }
            set { SetProperty(ref _renameDraft, value); }
        }

        /// <summary>
        /// 新会话没有 <c>_sessionId</c> 也可以发送：首次发送会先创建并挂载会话。
        /// 只有当没有工作区或正在创建 / 发送时才禁用。
        /// </summary>
        public bool CanSend
        {
            get
            {
                return _workspace != null
                    && !_isBusy
                    && !string.IsNullOrWhiteSpace(_draft);
            }
        }

        public bool HasRetryTarget
        {
            get
            {
                AssistantChatItemViewModel last = Items.OfType<AssistantChatItemViewModel>().LastOrDefault();
                return last != null && (last.IsFailed || last.IsInterrupted);
            }
        }

        public string ComposerHint
        {
            get { return "Enter 发送 · Shift+Enter 换行"; }
        }

        public string SessionSubtitle
        {
            get
            {
                if (!HasSession)
                {
                    return "从左栏选择会话，或新建一个";
                }

                return _workspace == null ? null : _workspace.Name;
            }
        }

        private IAgentSession CurrentSession
        {
            get
            {
                return _sessionId == null ? null : _app.Runtime.Find(_sessionId);
            }
        }

        public void SetWorkspace(Workspace workspace)
        {
            bool sameWorkspace = ReferenceEquals(_workspace, workspace)
                || (_workspace != null
                    && workspace != null
                    && string.Equals(_workspace.Id, workspace.Id, StringComparison.Ordinal));
            if (sameWorkspace)
            {
                _workspace = workspace;
                OnPropertyChanged(nameof(ComposerPlaceholder));
                OnPropertyChanged(nameof(EmptyTitle));
                OnPropertyChanged(nameof(EmptyBody));
                OnPropertyChanged(nameof(SendToolTip));
                return;
            }

            // 切换工作区时必须先断开旧会话；否则右栏还会挂着上一个工作区的 session，
            // 新工作区没有选中会话时看起来就像“新会话不能输入”。
            Detach();
            _workspace = workspace;
            OnPropertyChanged(nameof(Workspace));
            OnPropertyChanged(nameof(HasWorkspace));
            OnPropertyChanged(nameof(SessionSubtitle));
            OnPropertyChanged(nameof(ComposerPlaceholder));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyBody));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(SendToolTip));
            CommandManager.InvalidateRequerySuggested();
        }

        public async Task AttachAsync(Workspace workspace, string sessionId)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(sessionId))
            {
                Detach();
                return;
            }

            if (_isAttached
                && ReferenceEquals(_workspace, workspace)
                && string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            int version = ++_attachVersion;
            DetachCore();
            _workspace = workspace;
            _sessionId = sessionId;

            try
            {
                AgentSessionOptions options = _app.CreateSessionOptions(
                    workspace,
                    sessionId,
                    _selectedModel == null ? null : _selectedModel.ProviderId,
                    _selectedModel == null ? null : _selectedModel.ModelId);
                IAgentSession session = await _app.Runtime.GetOrCreateAsync(options, CancellationToken.None);
                if (version != _attachVersion)
                {
                    return;
                }

                _pump = new SessionEventPump(session, _dispatcher, ApplyEvent, ApplySnapshot);
                _pump.Start();
                _isAttached = true;

                AgentSessionSnapshot snapshot = session.Snapshot();
                ApplySnapshot(snapshot);
                SyncModelSelection(snapshot);
            }
            catch (Exception exception)
            {
                if (version != _attachVersion)
                {
                    return;
                }

                _sessionId = null;
                _isAttached = false;
                if (_pump != null)
                {
                    _pump.Dispose();
                    _pump = null;
                }

                Items.Clear();
                ApplySessionFlags(false, false);
                StatusText = "打开会话失败：" + exception.Message;
                _app.Logger.Error("conversation.attach", "打开会话失败。", exception);
            }

            OnPropertyChanged(nameof(Workspace));
            OnPropertyChanged(nameof(HasWorkspace));
            OnPropertyChanged(nameof(SessionId));
            OnPropertyChanged(nameof(HasSession));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(SessionSubtitle));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ComposerPlaceholder));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyBody));
            OnPropertyChanged(nameof(SendToolTip));
            CommandManager.InvalidateRequerySuggested();
        }

        public void Detach()
        {
            _attachVersion++;
            DetachCore();
        }

        private void DetachCore()
        {
            if (_pump != null)
            {
                _pump.Dispose();
                _pump = null;
            }

            _isAttached = false;
            _sessionId = null;
            _title = null;
            Items.Clear();
            _userItems.Clear();
            _assistantItems.Clear();
            _toolCards.Clear();
            _approvalCards.Clear();
            IsRunning = false;
            StatusText = null;
            UsageText = null;
            ContextText = null;
            ApplySessionFlags(false, false);
            OnPropertyChanged(nameof(SessionId));
            OnPropertyChanged(nameof(HasSession));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(SendToolTip));
        }

        public async Task NewSessionAsync()
        {
            if (_isBusy)
            {
                return;
            }

            if (_workspace == null)
            {
                StatusText = "请先在标题栏新建或选择工作区。";
                return;
            }

            // 已经站在一个空会话上时，新建只会制造重复空记录；把焦点交还输入框。
            if (HasSession && Items.Count == 0 && !IsRunning)
            {
                FocusComposer();
                return;
            }

            _isBusy = true;
            OnPropertyChanged(nameof(CanSend));
            CommandManager.InvalidateRequerySuggested();
            try
            {
                string sessionId = NewSessionId();
                await AttachAsync(_workspace, sessionId);
                if (!HasSession)
                {
                    return;
                }

                StatusText = "已新建会话";
                SessionListChanged?.Invoke(this, EventArgs.Empty);
                FocusComposer();
            }
            finally
            {
                _isBusy = false;
                OnPropertyChanged(nameof(CanSend));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public async Task SendAsync()
        {
            if (_isBusy)
            {
                return;
            }

            string text = (_draft ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return;
            }

            if (_workspace == null)
            {
                StatusText = "请先在标题栏新建或选择工作区。";
                return;
            }

            if (_app.Models.Models.Count == 0)
            {
                StatusText = "尚未配置模型，请在“功能设置 > 模型”中添加供应商和模型。";
                return;
            }

            _isBusy = true;
            OnPropertyChanged(nameof(CanSend));
            CommandManager.InvalidateRequerySuggested();

            try
            {
                // 新会话：先创建并挂载，成功后才清空草稿。创建失败时保留用户输入。
                if (_sessionId == null)
                {
                    string sessionId = NewSessionId();
                    await AttachAsync(_workspace, sessionId);
                    if (_sessionId == null)
                    {
                        return;
                    }

                    SessionListChanged?.Invoke(this, EventArgs.Empty);
                }

                IAgentSession session = CurrentSession;
                if (session == null)
                {
                    StatusText = "会话尚未就绪，请重试。";
                    return;
                }

                string clientId = "m_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                var optimistic = new UserChatItemViewModel(clientId, text, _clock());
                Items.Add(optimistic);
                _userItems[clientId] = optimistic;
                Draft = string.Empty;
                OnPropertyChanged(nameof(IsEmpty));

                try
                {
                    var input = new AgentInput
                    {
                        ClientMessageId = clientId,
                        Blocks = new List<AgentContentBlock>
                        {
                            new AgentContentBlock { Type = "text", Text = text }
                        },
                        ProviderId = _selectedModel == null ? null : _selectedModel.ProviderId,
                        ModelId = _selectedModel == null ? null : _selectedModel.ModelId
                    };

                    await session.SendAsync(input, AgentSendMode.Queue, CancellationToken.None);
                    IsRunning = true;
                    FocusComposer();
                }
                catch (AgentRunException exception)
                {
                    Items.Remove(optimistic);
                    _userItems.Remove(clientId);
                    RestoreDraft(text);
                    AddNotice("发送失败", exception.Error == null ? exception.Message : exception.Error.Message, true);
                }
                catch (Exception exception)
                {
                    Items.Remove(optimistic);
                    _userItems.Remove(clientId);
                    RestoreDraft(text);
                    AddNotice("发送失败", exception.Message, true);
                }
            }
            finally
            {
                _isBusy = false;
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(IsEmpty));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>失败时只在用户没有继续输入时还原草稿，避免覆盖刚敲的内容。</summary>
        private void RestoreDraft(string text)
        {
            if (string.IsNullOrWhiteSpace(Draft))
            {
                Draft = text;
            }
        }

        private static string NewSessionId()
        {
            return "s_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_"
                + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private void FocusComposer()
        {
            _dispatcher.BeginInvoke(new Action(FocusComposerRequested), DispatcherPriority.Input);
        }

        public async Task StopAsync()
        {
            IAgentSession session = CurrentSession;
            if (session == null)
            {
                IsRunning = false;
                return;
            }

            try
            {
                await session.CancelAsync(true, CancellationToken.None);
            }
            catch (Exception)
            {
            }

            IsRunning = false;
            StatusText = "已停止";
        }

        public async Task RetryLastAsync()
        {
            IAgentSession session = CurrentSession;
            if (session == null)
            {
                return;
            }

            AssistantChatItemViewModel assistant = Items.OfType<AssistantChatItemViewModel>().LastOrDefault();
            if (assistant == null)
            {
                return;
            }

            await RetryAsync(assistant);
        }

        public async Task RetryAsync(AssistantChatItemViewModel assistant)
        {
            IAgentSession session = CurrentSession;
            if (session == null || assistant == null)
            {
                return;
            }

            int index = Items.IndexOf(assistant);
            if (index >= 0)
            {
                while (Items.Count > index)
                {
                    ChatItemViewModel item = Items[Items.Count - 1];
                    Items.RemoveAt(Items.Count - 1);
                    Untrack(item);
                }
            }

            try
            {
                await session.RetryAsync(assistant.Id, CancellationToken.None);
                IsRunning = true;
                StatusText = "正在重试…";
            }
            catch (Exception exception)
            {
                AddNotice("重试失败", exception.Message, true);
            }

            OnPropertyChanged(nameof(IsEmpty));
        }

        public async Task CommitRenameAsync()
        {
            IsRenaming = false;
            string title = (_renameDraft ?? string.Empty).Trim();
            if (title.Length == 0 || _sessionId == null)
            {
                return;
            }

            IAgentSession session = CurrentSession;
            if (session == null)
            {
                return;
            }

            try
            {
                await session.RenameAsync(title, CancellationToken.None);
                _title = title;
                OnPropertyChanged(nameof(Title));
                SessionListChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                StatusText = "重命名失败：" + exception.Message;
            }
        }

        public async Task DeleteAsync()
        {
            if (_sessionId == null || _workspace == null)
            {
                return;
            }

            if (_dialogs != null && !_dialogs.Confirm(
                "删除会话",
                "删除后本会话的 JSONL 记录会从工作区移除，无法恢复。",
                "删除",
                "取消"))
            {
                return;
            }

            string sessionId = _sessionId;
            try
            {
                IAgentSession session = CurrentSession;
                if (session != null)
                {
                    await session.CancelAsync(true, CancellationToken.None);
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    {
                        try
                        {
                            await session.WaitForIdleAsync(timeout.Token);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                }

                await _app.Runtime.CloseAsync(sessionId, CancellationToken.None);
                await _app.GetStore(_workspace).DeleteAsync(sessionId, CancellationToken.None);
                Detach();
                SessionListChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                AddNotice("删除失败", exception.Message, true);
            }
        }

        public async Task ExportAsync()
        {
            if (_sessionId == null || _workspace == null)
            {
                return;
            }

            try
            {
                ConversationTranscript transcript = await _app.GetStore(_workspace)
                    .LoadAsync(_sessionId, CancellationToken.None);

                if (transcript == null)
                {
                    StatusText = "会话内容为空，无法导出。";
                    return;
                }

                string folder = _app.Exporter.ExportSession(transcript, _workspace.Path);
                StatusText = "已导出到 " + folder;
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception exception)
            {
                AddNotice("导出失败", exception.Message, true);
            }
        }

        /// <summary>左栏重命名后同步右栏标题；只处理当前会话。</summary>
        public void ApplySessionTitle(string sessionId, string title)
        {
            if (string.IsNullOrEmpty(sessionId)
                || !string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            _title = title;
            OnPropertyChanged(nameof(Title));

            // 左栏直接改的是存储；同步给运行中的会话，避免首条消息自动标题覆盖。
            IAgentSession session = CurrentSession;
            if (session == null)
            {
                return;
            }

            try
            {
                session.RenameAsync(title, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                StatusText = "重命名保存失败：" + exception.Message;
            }
        }

        public void FocusComposerRequested()
        {
            ComposerFocusRequested?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler ComposerFocusRequested;

        public void Dispose()
        {
            _markdownTimer.Stop();
            _markdownTimer.Tick -= OnMarkdownTick;
            _app.Approvals.Changed -= OnApprovalChanged;
            _app.ProvidersChanged -= OnProvidersChanged;
            Detach();
        }

        // ---------------------------------------------------------------
        // 事件应用
        // ---------------------------------------------------------------

        private void ApplySnapshot(AgentSessionSnapshot snapshot)
        {
            if (snapshot == null || !string.Equals(snapshot.SessionId, _sessionId, StringComparison.Ordinal))
            {
                return;
            }

            Items.Clear();
            _userItems.Clear();
            _assistantItems.Clear();
            _toolCards.Clear();
            _approvalCards.Clear();

            _title = snapshot.Title;
            IsRunning = snapshot.State == AgentSessionState.Running || snapshot.State == AgentSessionState.Cancelling;
            UpdateUsage(snapshot.LastUsage, snapshot.EstimatedContextTokens);

            foreach (AgentMessage message in snapshot.Messages ?? new List<AgentMessage>())
            {
                ApplyMessage(message, false);
            }

            foreach (ToolExecution execution in snapshot.ToolExecutions ?? new List<ToolExecution>())
            {
                ApplyToolExecution(execution);
            }

            foreach (ToolApprovalTicket ticket in _app.Approvals.GetPending(snapshot.SessionId))
            {
                AddApprovalCard(ticket.Request);
            }

            AgentMessage last = snapshot.Messages == null || snapshot.Messages.Count == 0
                ? null
                : snapshot.Messages[snapshot.Messages.Count - 1];

            if (last != null && last.Role == AgentMessageRole.Assistant)
            {
                if (last.Status == AgentMessageStatus.Interrupted)
                {
                    AddInterruptedNotice(last.Id);
                }
                else if (last.Status == AgentMessageStatus.Failed)
                {
                    AddFailedNotice(last.ErrorMessage, last.Id);
                }
            }

            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasRetryTarget));
            OnPropertyChanged(nameof(CanSend));
            CommandManager.InvalidateRequerySuggested();
        }

        private void ApplyEvent(AgentEvent evt)
        {
            var stateChanged = evt as StateChangedEvent;
            if (stateChanged != null)
            {
                IsRunning = stateChanged.State == AgentSessionState.Running
                    || stateChanged.State == AgentSessionState.Cancelling;
                StatusText = stateChanged.State == AgentSessionState.Cancelling
                    ? "正在停止…"
                    : null;
                return;
            }

            var runStarted = evt as RunStartedEvent;
            if (runStarted != null)
            {
                IsRunning = true;
                StatusText = null;
                return;
            }

            var messageStarted = evt as MessageStartedEvent;
            if (messageStarted != null)
            {
                ApplyMessage(messageStarted.Message, false);
                return;
            }

            var delta = evt as MessageDeltaEvent;
            if (delta != null && delta.MessageId != null)
            {
                AssistantChatItemViewModel assistant;
                if (_assistantItems.TryGetValue(delta.MessageId, out assistant))
                {
                    assistant.AppendText(delta.Delta);
                }

                return;
            }

            var thinking = evt as ThinkingDeltaEvent;
            if (thinking != null && thinking.MessageId != null)
            {
                AssistantChatItemViewModel assistant;
                if (_assistantItems.TryGetValue(thinking.MessageId, out assistant))
                {
                    assistant.AppendThinking(thinking.Delta);
                }

                return;
            }

            var completed = evt as MessageCompletedEvent;
            if (completed != null && completed.Message != null)
            {
                AssistantChatItemViewModel assistant;
                if (_assistantItems.TryGetValue(completed.Message.Id, out assistant))
                {
                    assistant.Apply(completed.Message, true);
                    assistant.MarkCompleted(completed.StopReason);
                }
                else
                {
                    AddAssistant(completed.Message, true);
                }

                UpdateUsage(completed.Usage, -1);
                return;
            }

            var runCompleted = evt as RunCompletedEvent;
            if (runCompleted != null)
            {
                IsRunning = false;
                StatusText = runCompleted.StopReason == StopReason.Length
                    ? "已达到输出上限"
                    : null;

                if (runCompleted.StopReason == StopReason.ToolUse)
                {
                    AddNotice("工具未执行", "当前会话没有可用的工具执行器。", false);
                }

                RaiseHasRetry();
                return;
            }

            var cancelled = evt as RunCancelledEvent;
            if (cancelled != null)
            {
                IsRunning = false;
                AssistantChatItemViewModel assistant;
                if (cancelled.AssistantMessageId != null
                    && _assistantItems.TryGetValue(cancelled.AssistantMessageId, out assistant))
                {
                    assistant.MarkInterrupted();
                }

                AddInterruptedNotice(cancelled.AssistantMessageId);
                RaiseHasRetry();
                return;
            }

            var failed = evt as RunFailedEvent;
            if (failed != null)
            {
                IsRunning = false;
                AssistantChatItemViewModel assistant;
                if (failed.AssistantMessageId != null
                    && _assistantItems.TryGetValue(failed.AssistantMessageId, out assistant))
                {
                    assistant.MarkFailed(failed.Error == null ? "生成失败。" : failed.Error.Message);
                }

                AddFailedNotice(
                    failed.Error,
                    failed.AssistantMessageId);
                RaiseHasRetry();
                return;
            }

            var titleChanged = evt as SessionTitleChangedEvent;
            if (titleChanged != null)
            {
                _title = titleChanged.Title;
                OnPropertyChanged(nameof(Title));
                SessionListChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var usage = evt as UsageChangedEvent;
            if (usage != null)
            {
                UpdateUsage(usage.Usage, usage.EstimatedContextTokens);
                return;
            }

            var toolStarted = evt as ToolExecutionStartedEvent;
            if (toolStarted != null)
            {
                ApplyToolExecution(toolStarted.Execution);
                return;
            }

            var toolCompleted = evt as ToolExecutionCompletedEvent;
            if (toolCompleted != null)
            {
                ApplyToolExecution(toolCompleted.Execution);
                return;
            }

            var turnStarted = evt as TurnStartedEvent;
            if (turnStarted != null && turnStarted.TurnIndex > 1)
            {
                StatusText = "第 " + turnStarted.TurnIndex + " 轮…";
            }
        }

        private void ApplyMessage(AgentMessage message, bool live)
        {
            if (message == null)
            {
                return;
            }

            switch (message.Role)
            {
                case AgentMessageRole.User:
                    if (!_userItems.ContainsKey(message.Id))
                    {
                        var item = new UserChatItemViewModel(message.Id, message.GetText(), message.CreatedAt);
                        Items.Add(item);
                        _userItems[message.Id] = item;
                        OnPropertyChanged(nameof(IsEmpty));
                    }

                    break;

                case AgentMessageRole.Assistant:
                    AddAssistant(message, live);
                    break;

                case AgentMessageRole.ToolResult:
                    ApplyToolResultMessage(message);
                    break;
            }
        }

        private void AddAssistant(AgentMessage message, bool live)
        {
            if (message == null)
            {
                return;
            }

            AssistantChatItemViewModel item;
            if (!_assistantItems.TryGetValue(message.Id, out item))
            {
                item = new AssistantChatItemViewModel(message.Id, _clipboard, message.CreatedAt);
                item.RetryRequested += (sender, args) => FireAndForget(RetryAsync(item));
                Items.Add(item);
                _assistantItems[message.Id] = item;
                OnPropertyChanged(nameof(IsEmpty));
            }

            item.Apply(message, live);
            EnsureToolCardsForMessage(message);

            if (!live)
            {
                item.RebuildMarkdown();
            }
            else if (message.Status == AgentMessageStatus.Streaming && IsRunning)
            {
                item.RebuildMarkdown();
            }
        }

        private void EnsureToolCardsForMessage(AgentMessage message)
        {
            if (message == null || message.Blocks == null)
            {
                return;
            }

            foreach (AgentContentBlock block in message.Blocks)
            {
                if (block == null || !string.Equals(block.Type, "toolCall", StringComparison.Ordinal))
                {
                    continue;
                }

                string key = string.IsNullOrWhiteSpace(block.ToolCallId)
                    ? message.Id + "/" + block.ToolName
                    : block.ToolCallId;

                if (_toolCards.ContainsKey(key))
                {
                    continue;
                }

                var card = CreateToolCard(
                    new ToolCall
                    {
                        Id = block.ToolCallId,
                        Name = block.ToolName,
                        ArgumentsJson = block.ArgumentsJson
                    },
                    message.CreatedAt);

                Items.Add(card);
                _toolCards[key] = card;
            }
        }

        private void ApplyToolExecution(ToolExecution execution)
        {
            if (execution == null || execution.Call == null)
            {
                return;
            }

            string key = string.IsNullOrWhiteSpace(execution.Call.Id)
                ? execution.Call.Name
                : execution.Call.Id;

            ToolCardViewModel card;
            if (!_toolCards.TryGetValue(key, out card))
            {
                card = CreateToolCard(execution.Call, _clock());
                Items.Add(card);
                _toolCards[key] = card;
            }

            card.ApplyExecution(execution);
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void ApplyToolResultMessage(AgentMessage message)
        {
            string key = string.IsNullOrWhiteSpace(message.ToolCallId) ? message.ToolName : message.ToolCallId;
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            ToolCardViewModel card;
            if (!_toolCards.TryGetValue(key, out card))
            {
                var call = new ToolCall { Id = message.ToolCallId, Name = message.ToolName };
                card = CreateToolCard(call, message.CreatedAt);
                Items.Add(card);
                _toolCards[key] = card;
            }

            card.ApplyMessage(message);
        }

        private ToolCardViewModel CreateToolCard(ToolCall call, DateTimeOffset timestamp)
        {
            var card = new ToolCardViewModel(call, timestamp);
            card.UndoRequested += (sender, args) => FireAndForget(UndoWriteAsync(card));
            return card;
        }

        private async Task UndoWriteAsync(ToolCardViewModel card)
        {
            if (card == null || string.IsNullOrEmpty(card.BackupPath) || _workspace == null)
            {
                return;
            }

            string relative = ExtractPath(card.ArgumentsJson);
            if (string.IsNullOrEmpty(relative))
            {
                AddNotice("无法撤销", "找不到这次写入的目标路径。", true);
                return;
            }

            string target;
            try
            {
                target = System.IO.Path.GetFullPath(System.IO.Path.Combine(_workspace.Path, relative));
                string root = System.IO.Path.GetFullPath(_workspace.Path)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;

                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    AddNotice("无法撤销", "目标路径在工作区之外，已拒绝。", true);
                    return;
                }
            }
            catch (Exception exception)
            {
                AddNotice("无法撤销", exception.Message, true);
                return;
            }

            if (_dialogs != null && !_dialogs.Confirm(
                "撤销写入",
                "将用写入前的备份恢复：" + target + "\n\n这会覆盖当前文件内容。",
                "撤销写入",
                "取消"))
            {
                return;
            }

            try
            {
                var backups = new FileBackupService(_app.Paths.BackupsDirectory);
                backups.Restore(card.BackupPath, target);
                card.MarkUndone();
                AddNotice("已撤销写入", "文件已恢复到这次写入之前的内容。", false);
            }
            catch (Exception exception)
            {
                AddNotice("撤销失败", exception.Message, true);
            }

            await Task.CompletedTask;
        }

        private static string ExtractPath(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                return null;
            }

            try
            {
                JsonValue value = JsonParser.Parse(argumentsJson);
                JsonValue path = value == null ? null : value.Get("path");
                return path == null ? null : path.AsString(null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void AddInterruptedNotice(string assistantId)
        {
            if (assistantId != null && Items.OfType<NoticeChatItemViewModel>().Any(
                n => string.Equals(n.Tag, assistantId, StringComparison.Ordinal)))
            {
                return;
            }

            var notice = new NoticeChatItemViewModel(
                "已中断",
                "保留了已生成的内容。重试不会重复发送用户消息。",
                false,
                _clock())
            {
                Tag = assistantId,
                ActionText = "重试",
                ActionCommand = new RelayCommand(() => FireAndForget(RetryLastAsync()))
            };

            Items.Add(notice);
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void AddFailedNotice(AgentError error, string assistantId)
        {
            string message = error == null || string.IsNullOrWhiteSpace(error.Message)
                ? "生成失败。"
                : error.Message;

            if (assistantId != null && Items.OfType<NoticeChatItemViewModel>().Any(
                n => string.Equals(n.Tag, "error:" + assistantId, StringComparison.Ordinal)))
            {
                return;
            }

            var notice = new NoticeChatItemViewModel("生成失败", message, true, _clock())
            {
                Tag = "error:" + assistantId
            };

            if (error != null
                && (error.Kind == AgentErrorKind.Auth
                    || error.Kind == AgentErrorKind.Permission
                    || error.Kind == AgentErrorKind.NotFound))
            {
                notice.ActionText = "打开设置";
                notice.ActionCommand = new RelayCommand(
                    () => SettingsRequested?.Invoke(this, EventArgs.Empty));
            }
            else if (error != null && error.Kind == AgentErrorKind.ContextOverflow)
            {
                notice.ActionText = "新建会话";
                notice.ActionCommand = new RelayCommand(() => FireAndForget(NewSessionAsync()));
            }
            else
            {
                notice.ActionText = "重试";
                notice.ActionCommand = new RelayCommand(() => FireAndForget(RetryLastAsync()));
            }

            Items.Add(notice);
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void AddFailedNotice(string message, string assistantId)
        {
            if (assistantId != null && Items.OfType<NoticeChatItemViewModel>().Any(
                n => string.Equals(n.Tag, "error:" + assistantId, StringComparison.Ordinal)))
            {
                return;
            }

            var notice = new NoticeChatItemViewModel(
                "生成失败",
                message,
                true,
                _clock())
            {
                Tag = "error:" + assistantId,
                ActionText = "重试",
                ActionCommand = new RelayCommand(() => FireAndForget(RetryLastAsync()))
            };

            Items.Add(notice);
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void AddNotice(string title, string body, bool isError)
        {
            Items.Add(new NoticeChatItemViewModel(title, body, isError, _clock()));
            OnPropertyChanged(nameof(IsEmpty));
        }

        // ---------------------------------------------------------------
        // 审批
        // ---------------------------------------------------------------

        private void OnApprovalChanged(object sender, ToolApprovalEventArgs args)
        {
            if (args == null || args.Ticket == null)
            {
                return;
            }

            _dispatcher.BeginInvoke(new Action(() =>
            {
                ToolApprovalRequest request = args.Ticket.Request;
                if (request == null || !string.Equals(request.SessionId, _sessionId, StringComparison.Ordinal))
                {
                    return;
                }

                if (args.Kind == ToolApprovalChangeKind.Requested)
                {
                    AddApprovalCard(request);
                    IsRunning = true;
                    return;
                }

                ApprovalCardViewModel card;
                if (request.RequestId != null && _approvalCards.TryGetValue(request.RequestId, out card))
                {
                    card.MarkResolved(args.Kind == ToolApprovalChangeKind.Cancelled
                        ? "已取消"
                        : "已处理");
                }
            }));
        }

        private void AddApprovalCard(ToolApprovalRequest request)
        {
            if (request == null || request.RequestId == null || _approvalCards.ContainsKey(request.RequestId))
            {
                return;
            }

            var card = new ApprovalCardViewModel(
                request,
                ResolveApproval,
                _clock());

            Items.Add(card);
            _approvalCards[request.RequestId] = card;
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void ResolveApproval(string requestId, ToolApprovalOutcome outcome)
        {
            try
            {
                _app.Approvals.TryResolve(requestId, outcome, outcome == ToolApprovalOutcome.Deny ? "用户拒绝。" : null);
            }
            catch (Exception exception)
            {
                AddNotice("审批失败", exception.Message, true);
            }
        }

        // ---------------------------------------------------------------
        // 其他
        // ---------------------------------------------------------------

        private void UpdateUsage(TokenUsage usage, int estimatedContext)
        {
            if (usage != null && usage.GetTotal() > 0)
            {
                UsageText = TokenEstimator.Format(usage.GetTotal()) + " tokens";
            }

            if (estimatedContext > 0)
            {
                ModelDescriptor model = _app.Models.Resolve(
                    _selectedModel == null ? null : _selectedModel.ProviderId,
                    _selectedModel == null ? null : _selectedModel.ModelId);

                int window = model == null ? 0 : model.ContextWindow;
                ContextText = window > 0
                    ? TokenEstimator.Format(estimatedContext) + " / " + TokenEstimator.Format(window)
                    : TokenEstimator.Format(estimatedContext);
            }
        }

        private void RefreshModelOptions()
        {
            ModelOptions.Clear();
            ModelGroups.Clear();
            ModelDescriptor defaultModel = _app.Models.GetDefault();
            string defaultKey = defaultModel == null ? null : defaultModel.Key;
            var groups = new Dictionary<string, ModelGroup>(StringComparer.OrdinalIgnoreCase);

            // 只列已配置提供商（providers.json 里带模型的条目），按目录顺序分区。
            foreach (ModelDescriptor model in _app.Models.Models)
            {
                var option = new ModelOption(
                    model.ProviderId,
                    model.ModelId,
                    model.GetDisplayName(),
                    GetProviderName(model.ProviderId),
                    string.Equals(model.Key, defaultKey, StringComparison.OrdinalIgnoreCase));

                ModelOptions.Add(option);

                ModelGroup group;
                if (!groups.TryGetValue(model.ProviderId, out group))
                {
                    group = new ModelGroup(model.ProviderId, option.ProviderName);
                    groups[model.ProviderId] = group;
                    ModelGroups.Add(group);
                }

                group.Models.Add(option);
            }

            OnPropertyChanged(nameof(CanPickModel));
            OnPropertyChanged(nameof(HasModelGroups));
            OnPropertyChanged(nameof(ModelToolTip));
            OnPropertyChanged(nameof(ModelEmptyText));
            OnPropertyChanged(nameof(SendToolTip));
            SyncModelSelection(null);
        }

        private string GetProviderName(string providerId)
        {
            foreach (ProviderDefinition provider in _app.Models.Providers)
            {
                if (provider != null
                    && string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(provider.DisplayName)
                        ? provider.Id
                        : provider.DisplayName;
                }
            }

            return providerId;
        }

        private void SyncModelSelection(AgentSessionSnapshot snapshot)
        {
            // 没挂载会话时保留用户刚在下拉里选中的模型；配置刷新后仍按 key 匹配。
            string providerId = snapshot != null
                ? snapshot.ProviderId
                : (_selectedModel == null ? null : _selectedModel.ProviderId);
            string modelId = snapshot != null
                ? snapshot.ModelId
                : (_selectedModel == null ? null : _selectedModel.ModelId);

            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            {
                ModelDescriptor fallback = _app.Models.GetDefault();
                if (fallback != null)
                {
                    providerId = fallback.ProviderId;
                    modelId = fallback.ModelId;
                }
            }

            ModelOption match = ModelOptions.FirstOrDefault(
                m => string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

            if (match == null && ModelOptions.Count > 0)
            {
                match = ModelOptions[0];
            }

            _selectedModel = match;
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(SelectedModelName));
        }

        private void OnMarkdownTick(object sender, EventArgs e)
        {
            foreach (AssistantChatItemViewModel assistant in _assistantItems.Values)
            {
                if (assistant.IsMarkdownDirty)
                {
                    assistant.RebuildMarkdown();
                }
            }
        }

        private void OnProvidersChanged(object sender, EventArgs e)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(RefreshModelOptions));
                return;
            }

            RefreshModelOptions();
        }

        private void BeginRename()
        {
            if (!HasSession)
            {
                return;
            }

            RenameDraft = Title;
            IsRenaming = true;
        }

        private void Untrack(ChatItemViewModel item)
        {
            var user = item as UserChatItemViewModel;
            if (user != null)
            {
                _userItems.Remove(user.Id);
            }

            var assistant = item as AssistantChatItemViewModel;
            if (assistant != null)
            {
                _assistantItems.Remove(assistant.Id);
            }

            var tool = item as ToolCardViewModel;
            if (tool != null && tool.CallId != null)
            {
                _toolCards.Remove(tool.CallId);
            }

            var approval = item as ApprovalCardViewModel;
            if (approval != null)
            {
                _approvalCards.Remove(approval.RequestId);
            }
        }

        private void RaiseHasRetry()
        {
            OnPropertyChanged(nameof(HasRetryTarget));
            CommandManager.InvalidateRequerySuggested();
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
                            AddNotice("操作失败", t.Exception.GetBaseException().Message, true)));
                    }
                },
                TaskScheduler.Default);
        }

        public void RefreshModels()
        {
            RefreshModelOptions();
        }
    }
}
