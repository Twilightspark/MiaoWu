using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows.Input;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Services;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Markdown;
using MiaoWu.Core.Models;
using MiaoWu.Core.Text;

namespace MiaoWu.App.ViewModels
{
    /// <summary>对话列表中的一条可渲染项。用隐式 DataTemplate 按类型选择模板。</summary>
    public abstract class ChatItemViewModel : ObservableObject
    {
        protected ChatItemViewModel(DateTimeOffset timestamp)
        {
            Timestamp = timestamp;
        }

        public DateTimeOffset Timestamp { get; private set; }

        public string TimeText
        {
            get { return Timestamp.ToLocalTime().ToString("HH:mm"); }
        }

        public abstract string AutomationName { get; }
    }

    public sealed class UserChatItemViewModel : ChatItemViewModel
    {
        private string _text;
        private bool _isPending;

        public UserChatItemViewModel(string id, string text, DateTimeOffset timestamp)
            : base(timestamp)
        {
            Id = id;
            _text = text ?? string.Empty;
        }

        public string Id { get; }

        public string Text
        {
            get { return _text; }
            set { SetProperty(ref _text, value); }
        }

        public bool IsPending
        {
            get { return _isPending; }
            set { SetProperty(ref _isPending, value); }
        }

        public override string AutomationName
        {
            get { return "用户消息"; }
        }
    }

    public sealed class AssistantChatItemViewModel : ChatItemViewModel
    {
        private readonly StringBuilder _builder = new StringBuilder();
        private readonly StringBuilder _thinking = new StringBuilder();
        private readonly IClipboardService _clipboard;

        private string _statusText;
        private string _errorText;
        private string _usageText;
        private string _modelText;
        private bool _isStreaming;
        private bool _isFailed;
        private bool _isInterrupted;
        private bool _hasThinking;
        private bool _isThinkingExpanded;
        private bool _isMarkdownDirty;

        public AssistantChatItemViewModel(string id, IClipboardService clipboard, DateTimeOffset timestamp)
            : base(timestamp)
        {
            Id = id;
            _clipboard = clipboard;
            Blocks = new ObservableCollection<MarkdownBlockViewModel>();
            CopyCommand = new RelayCommand(CopyText);
            RetryCommand = new RelayCommand(() => RetryRequested?.Invoke(this, EventArgs.Empty));
            ToggleThinkingCommand = new RelayCommand(() => IsThinkingExpanded = !IsThinkingExpanded);
        }

        public event EventHandler RetryRequested;

        public string Id { get; }

        public ObservableCollection<MarkdownBlockViewModel> Blocks { get; }

        public bool HasBlocks
        {
            get { return Blocks.Count > 0; }
        }

        public bool HasText
        {
            get { return _builder.Length > 0; }
        }

        public bool ShowRetry
        {
            get { return _isFailed || _isInterrupted; }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorText); }
        }

        public string Text
        {
            get { return _builder.ToString(); }
        }

        public string ThinkingText
        {
            get { return _thinking.ToString(); }
        }

        public bool HasThinking
        {
            get { return _hasThinking; }
            private set { SetProperty(ref _hasThinking, value); }
        }

        public bool IsThinkingExpanded
        {
            get { return _isThinkingExpanded; }
            set { SetProperty(ref _isThinkingExpanded, value); }
        }

        public bool IsStreaming
        {
            get { return _isStreaming; }
            private set { SetProperty(ref _isStreaming, value); }
        }

        public bool IsFailed
        {
            get { return _isFailed; }
            private set { SetProperty(ref _isFailed, value); }
        }

        public bool IsInterrupted
        {
            get { return _isInterrupted; }
            private set { SetProperty(ref _isInterrupted, value); }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set { SetProperty(ref _statusText, value); }
        }

        public string ErrorText
        {
            get { return _errorText; }
            private set { SetProperty(ref _errorText, value); }
        }

        public string UsageText
        {
            get { return _usageText; }
            private set { SetProperty(ref _usageText, value); }
        }

        public string ModelText
        {
            get { return _modelText; }
            private set { SetProperty(ref _modelText, value); }
        }

        public bool IsMarkdownDirty
        {
            get { return _isMarkdownDirty; }
            internal set { _isMarkdownDirty = value; }
        }

        public RelayCommand CopyCommand { get; }

        public RelayCommand RetryCommand { get; }

        public RelayCommand ToggleThinkingCommand { get; }

        public override string AutomationName
        {
            get { return "助手消息"; }
        }

        public void AppendText(string delta)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            _builder.Append(delta);
            _isMarkdownDirty = true;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(HasText));
        }

        public void AppendThinking(string delta)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            _thinking.Append(delta);
            HasThinking = true;
            OnPropertyChanged(nameof(ThinkingText));
        }

        public void Apply(AgentMessage message, bool rebuildMarkdown)
        {
            if (message == null)
            {
                return;
            }

            _builder.Clear();
            _thinking.Clear();

            if (message.Blocks != null)
            {
                foreach (AgentContentBlock block in message.Blocks)
                {
                    if (block == null)
                    {
                        continue;
                    }

                    if (string.Equals(block.Type, "text", StringComparison.Ordinal))
                    {
                        _builder.Append(block.Text);
                    }
                    else if (string.Equals(block.Type, "thinking", StringComparison.Ordinal))
                    {
                        _thinking.Append(block.Thinking);
                    }
                    else if (string.Equals(block.Type, "toolCall", StringComparison.Ordinal))
                    {
                        // 工具调用由工具卡渲染；助手正文不重复显示参数。
                    }
                }
            }

            HasThinking = _thinking.Length > 0;
            IsStreaming = message.Status == AgentMessageStatus.Streaming;
            IsFailed = message.Status == AgentMessageStatus.Failed;
            IsInterrupted = message.Status == AgentMessageStatus.Interrupted;
            ErrorText = message.ErrorMessage;
            ModelText = BuildModelText(message);
            UsageText = BuildUsageText(message.Usage);

            StatusText = BuildStatusText(message);
            IsMarkdownDirty = true;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(HasText));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowRetry));
            OnPropertyChanged(nameof(ThinkingText));

            if (rebuildMarkdown)
            {
                RebuildMarkdown();
            }
        }

        public void MarkCompleted(StopReason reason)
        {
            IsStreaming = false;
            if (reason == StopReason.Length)
            {
                StatusText = "已达到输出上限";
            }
            else if (reason == StopReason.ToolUse)
            {
                StatusText = "调用工具";
            }
        }

        public void MarkFailed(string error)
        {
            IsStreaming = false;
            IsFailed = true;
            ErrorText = error;
            StatusText = "失败";
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowRetry));
        }

        public void MarkInterrupted()
        {
            IsStreaming = false;
            IsInterrupted = true;
            StatusText = "已中断";
            OnPropertyChanged(nameof(ShowRetry));
        }

        public void RebuildMarkdown()
        {
            _isMarkdownDirty = false;
            Blocks.Clear();
            string text = _builder.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                OnPropertyChanged(nameof(HasBlocks));
                return;
            }

            try
            {
                MarkdownDocument document = MarkdownParser.Parse(text);
                bool first = true;
                foreach (MarkdownBlock block in document.Blocks)
                {
                    MarkdownBlockViewModel viewModel = MarkdownViewModelFactory.Create(block, _clipboard, first);
                    if (viewModel == null)
                    {
                        continue;
                    }

                    Blocks.Add(viewModel);
                    first = false;
                }
            }
            catch (Exception)
            {
                Blocks.Clear();
                Blocks.Add(new MarkdownParagraphViewModel(
                    new MarkdownParagraph(new System.Collections.Generic.List<MarkdownInline>
                    {
                        new MarkdownInline(MarkdownInlineKind.Text, text)
                    })));
            }

            OnPropertyChanged(nameof(HasBlocks));
        }

        private void CopyText()
        {
            if (_clipboard != null)
            {
                _clipboard.SetText(_builder.ToString());
            }
        }

        private static string BuildStatusText(AgentMessage message)
        {
            switch (message.Status)
            {
                case AgentMessageStatus.Streaming:
                    return "正在生成…";
                case AgentMessageStatus.Failed:
                    return "失败";
                case AgentMessageStatus.Interrupted:
                    return "已中断";
                case AgentMessageStatus.Pending:
                    return "排队中";
                default:
                    if (message.StopReason == StopReason.Length)
                    {
                        return "已达到输出上限";
                    }

                    if (message.StopReason == StopReason.ToolUse)
                    {
                        return "调用工具";
                    }

                    return null;
            }
        }

        private static string BuildUsageText(TokenUsage usage)
        {
            if (usage == null || usage.GetTotal() <= 0)
            {
                return null;
            }

            return TokenEstimator.Format(usage.GetTotal()) + " tokens";
        }

        private static string BuildModelText(AgentMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.ModelId))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(message.ProviderId)
                ? message.ModelId
                : message.ProviderId + "/" + message.ModelId;
        }
    }

    /// <summary>一次工具调用的卡片：状态、参数摘要、结果预览、完整输出入口。</summary>
    public sealed class ToolCardViewModel : ChatItemViewModel
    {
        private string _statusText;
        private string _summary;
        private string _output;
        private string _errorText;
        private string _fullOutputPath;
        private bool _isPending;
        private bool _isError;
        private bool _isBlocked;
        private bool _isTruncated;
        private bool _isUntrusted;
        private bool _isExpanded;
        private string _riskText;
        private string _decisionText;
        private string _backupPath;
        private bool _canUndo;

        public ToolCardViewModel(ToolCall call, DateTimeOffset timestamp)
            : base(timestamp)
        {
            CallId = call == null ? null : call.Id;
            ToolName = call == null ? "tool" : call.Name;
            ArgumentsJson = call == null ? null : call.ArgumentsJson;
            Summary = BuildArgumentsSummary(ArgumentsJson);
            StatusText = "准备中";
            _isPending = true;

            ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
            OpenFullOutputCommand = new RelayCommand(OpenFullOutput);
            UndoCommand = new RelayCommand(() => UndoRequested?.Invoke(this, EventArgs.Empty), () => CanUndo);
        }

        public event EventHandler UndoRequested;

        public string CallId { get; }

        public string ToolName { get; }

        public string ArgumentsJson { get; }

        public string BackupPath
        {
            get { return _backupPath; }
            private set
            {
                if (SetProperty(ref _backupPath, value))
                {
                    OnPropertyChanged(nameof(CanUndo));
                }
            }
        }

        public bool CanUndo
        {
            get { return _canUndo; }
            private set
            {
                if (SetProperty(ref _canUndo, value))
                {
                    OnPropertyChanged(nameof(CanUndo));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string Summary
        {
            get { return _summary; }
            private set { SetProperty(ref _summary, value); }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set { SetProperty(ref _statusText, value); }
        }

        public string Output
        {
            get { return _output; }
            private set
            {
                if (SetProperty(ref _output, value))
                {
                    OnPropertyChanged(nameof(HasOutput));
                }
            }
        }

        public bool HasOutput
        {
            get { return !string.IsNullOrEmpty(_output); }
        }

        public string ErrorText
        {
            get { return _errorText; }
            private set { SetProperty(ref _errorText, value); }
        }

        public string FullOutputPath
        {
            get { return _fullOutputPath; }
            private set
            {
                if (SetProperty(ref _fullOutputPath, value))
                {
                    OnPropertyChanged(nameof(CanOpenFullOutput));
                }
            }
        }

        public bool CanOpenFullOutput
        {
            get { return !string.IsNullOrEmpty(_fullOutputPath); }
        }

        public bool IsPending
        {
            get { return _isPending; }
            private set { SetProperty(ref _isPending, value); }
        }

        public bool IsError
        {
            get { return _isError; }
            private set { SetProperty(ref _isError, value); }
        }

        public bool IsBlocked
        {
            get { return _isBlocked; }
            private set { SetProperty(ref _isBlocked, value); }
        }

        public bool IsTruncated
        {
            get { return _isTruncated; }
            private set { SetProperty(ref _isTruncated, value); }
        }

        public bool IsUntrusted
        {
            get { return _isUntrusted; }
            private set { SetProperty(ref _isUntrusted, value); }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { SetProperty(ref _isExpanded, value); }
        }

        public string RiskText
        {
            get { return _riskText; }
            private set { SetProperty(ref _riskText, value); }
        }

        public string DecisionText
        {
            get { return _decisionText; }
            private set { SetProperty(ref _decisionText, value); }
        }

        public RelayCommand ToggleExpandedCommand { get; }

        public RelayCommand OpenFullOutputCommand { get; }

        public RelayCommand UndoCommand { get; }

        public override string AutomationName
        {
            get { return "工具 " + ToolName; }
        }

        public void ApplyExecution(ToolExecution execution)
        {
            if (execution == null)
            {
                return;
            }

            RiskText = RiskToText(execution.Risk);
            DecisionText = DecisionToText(execution.Decision);

            if (execution.Result == null)
            {
                IsPending = true;
                StatusText = "运行中";
                return;
            }

            ApplyResult(execution.Result);
        }

        public void ApplyResult(ToolResult result)
        {
            if (result == null)
            {
                return;
            }

            IsPending = false;
            IsError = !result.IsSuccess && result.Status != ToolResultStatus.Blocked;
            IsBlocked = result.Status == ToolResultStatus.Blocked;
            IsTruncated = result.Truncated;
            IsUntrusted = result.IsUntrusted;
            FullOutputPath = result.FullOutputPath;
            BackupPath = result.BackupPath;
            CanUndo = !string.IsNullOrEmpty(result.BackupPath) && result.IsSuccess;
            Output = result.Content;
            ErrorText = result.ErrorMessage;
            StatusText = StatusToText(result.Status);
            Summary = string.IsNullOrWhiteSpace(result.Summary) ? Summary : result.Summary;
        }

        public void ApplyMessage(AgentMessage message)
        {
            if (message == null)
            {
                return;
            }

            bool isError = message.IsError;
            StatusText = isError ? "失败" : "完成";
            IsPending = false;
            IsError = isError;

            if (message.Blocks != null)
            {
                foreach (AgentContentBlock block in message.Blocks)
                {
                    if (block != null && string.Equals(block.Type, "toolResult", StringComparison.Ordinal))
                    {
                        Output = block.ResultContent;
                        IsTruncated = block.Truncated;
                        FullOutputPath = block.FullOutputPath;
                    }
                }
            }
        }

        public void MarkUndone()
        {
            CanUndo = false;
            StatusText = "已撤销写入";
        }

        private void OpenFullOutput()
        {
            if (string.IsNullOrWhiteSpace(FullOutputPath))
            {
                return;
            }

            try
            {
                Process.Start("explorer.exe", "/select,\"" + FullOutputPath + "\"");
            }
            catch (Exception)
            {
            }
        }

        private static string BuildArgumentsSummary(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            string value = json.Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= 160 ? value : value.Substring(0, 160) + "…";
        }

        private static string StatusToText(ToolResultStatus status)
        {
            switch (status)
            {
                case ToolResultStatus.Succeeded:
                    return "完成";
                case ToolResultStatus.Denied:
                    return "已拒绝";
                case ToolResultStatus.Blocked:
                    return "已拦截";
                case ToolResultStatus.Cancelled:
                    return "已取消";
                case ToolResultStatus.InvalidArguments:
                    return "参数无效";
                default:
                    return "失败";
            }
        }

        private static string RiskToText(ToolRisk risk)
        {
            switch (risk)
            {
                case ToolRisk.Low:
                    return "只读";
                case ToolRisk.Medium:
                    return "中风险";
                case ToolRisk.High:
                    return "高风险";
                default:
                    return "已拦截";
            }
        }

        private static string DecisionToText(ToolExecutionDecision decision)
        {
            switch (decision)
            {
                case ToolExecutionDecision.Auto:
                    return "自动放行";
                case ToolExecutionDecision.ApprovedOnce:
                    return "已允许一次";
                case ToolExecutionDecision.ApprovedForSession:
                    return "本会话已允许";
                case ToolExecutionDecision.Denied:
                    return "已拒绝";
                case ToolExecutionDecision.Blocked:
                    return "策略拦截";
                case ToolExecutionDecision.Cancelled:
                    return "已取消";
                default:
                    return null;
            }
        }
    }

    /// <summary>内联审批卡：允许一次 / 本会话允许 / 拒绝，diff 可展开。</summary>
    public sealed class ApprovalCardViewModel : ChatItemViewModel
    {
        private bool _isResolved;
        private string _resolutionText;
        private bool _isDiffVisible;

        public ApprovalCardViewModel(
            ToolApprovalRequest request,
            Action<string, ToolApprovalOutcome> resolve,
            DateTimeOffset timestamp)
            : base(timestamp)
        {
            RequestId = request.RequestId;
            Title = request.Title;
            Target = request.Target;
            Summary = request.Summary;
            Diff = request.Diff;
            CanApproveForSession = request.CanApproveForSession;
            CanShowDiff = request.CanShowDiff && !string.IsNullOrWhiteSpace(request.Diff);
            RiskText = request.Risk == ToolRisk.High ? "高风险" : "中风险";

            AllowOnceCommand = new RelayCommand(() => Resolve(resolve, ToolApprovalOutcome.ApproveOnce));
            AllowSessionCommand = new RelayCommand(
                () => Resolve(resolve, ToolApprovalOutcome.ApproveForSession),
                () => CanApproveForSession);
            DenyCommand = new RelayCommand(() => Resolve(resolve, ToolApprovalOutcome.Deny));
            ToggleDiffCommand = new RelayCommand(() => IsDiffVisible = !IsDiffVisible);
        }

        public string RequestId { get; }

        public string Title { get; }

        public string Target { get; }

        public string Summary { get; }

        public string Diff { get; }

        public string RiskText { get; }

        public bool CanApproveForSession { get; }

        public bool CanShowDiff { get; }

        public bool IsResolved
        {
            get { return _isResolved; }
            private set { SetProperty(ref _isResolved, value); }
        }

        public string ResolutionText
        {
            get { return _resolutionText; }
            private set { SetProperty(ref _resolutionText, value); }
        }

        public bool IsDiffVisible
        {
            get { return _isDiffVisible; }
            set { SetProperty(ref _isDiffVisible, value); }
        }

        public RelayCommand AllowOnceCommand { get; }

        public RelayCommand AllowSessionCommand { get; }

        public RelayCommand DenyCommand { get; }

        public RelayCommand ToggleDiffCommand { get; }

        public override string AutomationName
        {
            get { return "审批：" + Title; }
        }

        public void MarkResolved(string text)
        {
            IsResolved = true;
            ResolutionText = text;
            CommandManager.InvalidateRequerySuggested();
        }

        private void Resolve(Action<string, ToolApprovalOutcome> resolve, ToolApprovalOutcome outcome)
        {
            if (IsResolved || resolve == null)
            {
                return;
            }

            resolve(RequestId, outcome);
            MarkResolved(outcome == ToolApprovalOutcome.Deny ? "已拒绝" : "已允许");
        }
    }

    /// <summary>错误/提示卡：失败、已中断、达到上限等。</summary>
    public sealed class NoticeChatItemViewModel : ChatItemViewModel
    {
        public NoticeChatItemViewModel(
            string title,
            string body,
            bool isError,
            DateTimeOffset timestamp)
            : base(timestamp)
        {
            Title = title;
            Body = body;
            IsError = isError;
        }

        public string Title { get; }

        public string Body { get; }

        public bool IsError { get; }

        public string ActionText { get; set; }

        public RelayCommand ActionCommand { get; set; }

        /// <summary>用于去重/关联（例如 assistant 消息 ID）。</summary>
        public string Tag { get; set; }

        public bool HasAction
        {
            get { return ActionCommand != null; }
        }

        public override string AutomationName
        {
            get { return Title; }
        }
    }
}
