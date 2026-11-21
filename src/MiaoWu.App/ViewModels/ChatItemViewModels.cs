using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Services;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Input;
using MiaoWu.Core.Markdown;
using MiaoWu.Core.Models;
using MiaoWu.Core.Text;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Tools;

namespace MiaoWu.App.ViewModels
{
    /// <summary>对话列表中的一条可渲染项。用隐式 DataTemplate 按类型选择模板。</summary>
    public abstract class ChatItemViewModel : ObservableObject
    {
        private bool _isProcessMember;
        private bool _isHiddenByGroup;

        protected ChatItemViewModel(DateTimeOffset timestamp)
        {
            Timestamp = timestamp;
        }

        public DateTimeOffset Timestamp { get; private set; }

        /// <summary>
        /// 是否是执行过程组的成员。模板据此缩进，组头摘要也靠它统计活动；
        /// 成员身份不会改变行在 <c>Items</c> 里的顺序。
        /// </summary>
        public bool IsProcessMember
        {
            get { return _isProcessMember; }
            internal set { SetProperty(ref _isProcessMember, value); }
        }

        /// <summary>所属过程组收起时为 true，容器折叠成 0 高但不从列表移除。</summary>
        public bool IsHiddenByGroup
        {
            get { return _isHiddenByGroup; }
            internal set { SetProperty(ref _isHiddenByGroup, value); }
        }

        public string TimeText
        {
            get { return Timestamp.ToLocalTime().ToString("HH:mm"); }
        }

        public abstract string AutomationName { get; }
    }

    public sealed class UserChatItemViewModel : ChatItemViewModel
    {
        private readonly IClipboardService _clipboard;
        private string _text;
        private bool _isPending;
        private DateTimeOffset? _turnStartedAt;
        private DateTimeOffset? _turnEndedAt;
        private bool _turnHasGroup;
        private bool _turnFailed;
        private bool _turnStopped;
        private string _turnStatusLabel = string.Empty;
        private string _turnDuration = string.Empty;

        public UserChatItemViewModel(string id, string text, DateTimeOffset timestamp, IClipboardService clipboard)
            : base(timestamp)
        {
            Id = id;
            _text = text ?? string.Empty;
            _clipboard = clipboard;
            CopyCommand = new RelayCommand(CopyText);
        }

        public string Id { get; }

        public RelayCommand CopyCommand { get; }

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

        /// <summary>本轮是否正在跑；结束（完成 / 失败 / 停止）后为 false。</summary>
        public bool IsTurnRunning
        {
            get { return _turnStartedAt != null && _turnEndedAt == null; }
        }

        /// <summary>
        /// 问题气泡下方的本轮用时：只在轮次结束后显示「已完成，用时 N秒」。
        /// 运行中的「正在探索，用时 N秒」放在助手尾部状态位，避免同一屏出现两条计时；
        /// 这一轮有执行过程组时，完成态也交给组头。
        /// </summary>
        public bool ShowTurnStatus
        {
            get { return _turnStartedAt != null && _turnEndedAt != null && !_turnHasGroup; }
        }

        /// <summary>「正在探索，用时 」/「已完成，用时 」/「处理失败」/「已停止」。</summary>
        public string TurnStatusLabel
        {
            get { return _turnStatusLabel; }
        }

        /// <summary>DSH 单位时长（12秒 / 1分5秒），失败与停止为空。</summary>
        public string TurnDuration
        {
            get { return _turnDuration; }
        }

        public string TurnStatusText
        {
            get { return _turnStatusLabel + _turnDuration; }
        }

        public override string AutomationName
        {
            get { return "用户消息"; }
        }

        /// <summary>新的一轮从用户消息时刻起算；重试同一问题时重新开始。</summary>
        public void StartTurn(DateTimeOffset at)
        {
            _turnStartedAt = at;
            _turnEndedAt = null;
            _turnHasGroup = false;
            _turnFailed = false;
            _turnStopped = false;
            UpdateTurnText(at);
            OnPropertyChanged(nameof(IsTurnRunning));
            OnPropertyChanged(nameof(ShowTurnStatus));
            OnPropertyChanged(nameof(TurnStatusText));
        }

        /// <summary>运行中每秒刷新用时；已结束的不再变动。</summary>
        public void RefreshTurn(DateTimeOffset now)
        {
            if (IsTurnRunning)
            {
                UpdateTurnText(now);
            }
        }

        /// <summary>这一轮已经建了执行过程组：组头会显示同样的状态与用时。</summary>
        public void SetHasProcessGroup()
        {
            if (_turnHasGroup)
            {
                return;
            }

            _turnHasGroup = true;
            OnPropertyChanged(nameof(ShowTurnStatus));
            OnPropertyChanged(nameof(TurnStatusText));
        }

        public void CompleteTurn(DateTimeOffset at, bool failed = false, bool stopped = false)
        {
            if (_turnStartedAt == null || _turnEndedAt != null)
            {
                return;
            }

            _turnEndedAt = at < _turnStartedAt.Value ? _turnStartedAt.Value : at;
            _turnFailed = failed;
            _turnStopped = stopped;
            UpdateTurnText(_turnEndedAt.Value);
            OnPropertyChanged(nameof(IsTurnRunning));
            OnPropertyChanged(nameof(ShowTurnStatus));
            OnPropertyChanged(nameof(TurnStatusText));
        }

        private void UpdateTurnText(DateTimeOffset now)
        {
            string label;
            string duration;
            if (_turnStopped)
            {
                label = "已停止";
                duration = string.Empty;
            }
            else if (_turnFailed)
            {
                label = "处理失败";
                duration = string.Empty;
            }
            else if (_turnEndedAt != null)
            {
                label = "已完成，用时 ";
                duration = ProcessGroupViewModel.FormatRunDuration(_turnEndedAt.Value - _turnStartedAt.Value);
            }
            else
            {
                label = "正在探索，用时 ";
                duration = ProcessGroupViewModel.FormatRunDuration(now - _turnStartedAt.Value);
            }

            if (!string.Equals(_turnStatusLabel, label, StringComparison.Ordinal))
            {
                _turnStatusLabel = label;
                OnPropertyChanged(nameof(TurnStatusLabel));
            }

            if (!string.Equals(_turnDuration, duration, StringComparison.Ordinal))
            {
                _turnDuration = duration;
                OnPropertyChanged(nameof(TurnDuration));
            }
        }

        private void CopyText()
        {
            if (_clipboard != null)
            {
                _clipboard.SetText(_text);
            }
        }

        /// <summary>提问气泡里的图片附件（缩略图），与输入框里的图片 chip 一一对应。</summary>
        public ObservableCollection<ChatImageViewModel> Images { get; } =
            new ObservableCollection<ChatImageViewModel>();

        public bool HasImages
        {
            get { return Images.Count > 0; }
        }

        /// <summary>乐观回显：把刚发出的图片附件直接显示在气泡里。</summary>
        public void SetImages(IReadOnlyList<ComposerImageAttachment> attachments)
        {
            Images.Clear();
            if (attachments != null)
            {
                foreach (ComposerImageAttachment attachment in attachments)
                {
                    if (attachment == null || string.IsNullOrEmpty(attachment.Base64Data))
                    {
                        continue;
                    }

                    ImageSource source = ChatImageViewModel.Decode(attachment.Base64Data);
                    if (source != null)
                    {
                        Images.Add(new ChatImageViewModel(source, attachment.DisplayName));
                    }
                }
            }

            OnPropertyChanged(nameof(HasImages));
        }

        /// <summary>回放：从消息块里的 image 块恢复缩略图。</summary>
        public void SetImagesFromBlocks(IEnumerable<AgentContentBlock> blocks)
        {
            Images.Clear();
            if (blocks != null)
            {
                foreach (AgentContentBlock block in blocks)
                {
                    if (block == null
                        || !string.Equals(block.Type, "image", StringComparison.Ordinal)
                        || string.IsNullOrEmpty(block.Data))
                    {
                        continue;
                    }

                    ImageSource source = ChatImageViewModel.Decode(block.Data);
                    if (source != null)
                    {
                        Images.Add(new ChatImageViewModel(source, block.MimeType));
                    }
                }
            }

            OnPropertyChanged(nameof(HasImages));
        }
    }

    /// <summary>一条用户提问里的图片缩略图。</summary>
    public sealed class ChatImageViewModel
    {
        public ChatImageViewModel(ImageSource source, string label)
        {
            Source = source;
            Label = string.IsNullOrWhiteSpace(label) ? "图片" : label;
        }

        public ImageSource Source { get; private set; }

        public string Label { get; private set; }

        /// <summary>把 base64 解码成缩略图；失败返回 null（不影响文字消息发送）。</summary>
        public static ImageSource Decode(string base64)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = 360;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception exception)
            {
                Trace.WriteLine("chat.image.decode: " + exception.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// 压缩分隔行：显示在“已被摘要覆盖的最后一条消息”之后，说明从这里往前的对话
    /// 不再逐条进入模型上下文。展开可以看摘要正文。
    /// </summary>
    public sealed class CompactionChatItemViewModel : ChatItemViewModel
    {
        private bool _isExpanded;

        public CompactionChatItemViewModel(int coveredMessages, string summary, DateTimeOffset timestamp)
            : base(timestamp)
        {
            CoveredMessages = coveredMessages < 0 ? 0 : coveredMessages;
            Summary = summary ?? string.Empty;
            ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        }

        public int CoveredMessages { get; private set; }

        public string Summary { get; private set; }

        public RelayCommand ToggleCommand { get; private set; }

        public string HeaderText
        {
            get { return "已压缩 " + CoveredMessages + " 条消息"; }
        }

        public bool HasSummary
        {
            get { return !string.IsNullOrWhiteSpace(Summary); }
        }

        /// <summary>摘要按行去掉 Markdown 标题井号，展开时读起来是六段小节而不是原始标记。</summary>
        public string DisplaySummary
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Summary))
                {
                    return string.Empty;
                }

                var builder = new StringBuilder(Summary.Length);
                foreach (string line in Summary.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    string text = line.TrimEnd();
                    if (text.StartsWith("## ", StringComparison.Ordinal))
                    {
                        text = text.Substring(3);
                    }
                    else if (string.Equals(text.Trim(), "##", StringComparison.Ordinal))
                    {
                        text = string.Empty;
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(text);
                }

                return builder.ToString().Trim();
            }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { SetProperty(ref _isExpanded, value); }
        }

        public override string AutomationName
        {
            get { return HeaderText; }
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
        private bool _isThinkingMoved;
        private bool _isThinkingExpanded;
        private bool _isMarkdownDirty;
        private bool _continuesWithTools;
        private bool _isInsideOpenGroup;
        private string _thinkingPreview;

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

        /// <summary>裁掉尾部空白后仍有正文；只有换行的过程步骤不应占一行。</summary>
        public bool HasVisibleText
        {
            get { return Text.Length > 0; }
        }

        /// <summary>流式首帧的纯文本兜底：没有 Markdown 块且确有正文时才显示，空步骤不占行高。</summary>
        public bool ShowPlainText
        {
            get { return !HasBlocks && HasVisibleText; }
        }

        public bool ShowRetry
        {
            get { return _isFailed || _isInterrupted; }
        }

        /// <summary>
        /// 这一条助手消息以工具调用收尾，属于「过程」而不是答案：
        /// 它会被收进执行过程组，组收起时整行隐藏（DSH 的 step → process 归属）。
        /// </summary>
        public bool IsProcessOnly
        {
            get { return _continuesWithTools; }
        }

        /// <summary>
        /// 尾部元信息与动作只在真正有内容（正文、错误、进行中、可重试）时出现。
        /// 以工具调用收尾的那一步是过程，不是答案：它的时间/模型/用量留到最后一条答案上，
        /// 否则元信息会插在"读文件"这句话和它下面的工具行之间。
        /// </summary>
        public bool ShowFooter
        {
            get
            {
                return (HasText || HasError || IsStreaming || ShowRetry)
                    && !_continuesWithTools
                    && !_isInsideOpenGroup;
            }
        }

        /// <summary>
        /// 这一行是否处在展开的过程组里：组头已经显示「正在探索，用时 N秒」，
        /// 流式步骤的尾部不再重复一条同样的计时与时间。
        /// </summary>
        internal void SetInsideOpenGroup(bool value)
        {
            if (SetProperty(ref _isInsideOpenGroup, value))
            {
                OnPropertyChanged(nameof(ShowFooter));
            }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorText); }
        }

        public string Text
        {
            get { return TrimTrailingBlankLines(_builder.ToString()); }
        }

        public string ThinkingText
        {
            get { return TrimTrailingBlankLines(_thinking.ToString()); }
        }

        /// <summary>收起时显示的一行摘要：最近一个段落的首行，去掉 Markdown 标记并截断。</summary>
        public string ThinkingPreview
        {
            get { return _thinkingPreview; }
            private set { SetProperty(ref _thinkingPreview, value); }
        }

        public bool HasThinking
        {
            get { return _hasThinking; }
            private set
            {
                if (SetProperty(ref _hasThinking, value))
                {
                    OnPropertyChanged(nameof(ShowThinking));
                }
            }
        }

        /// <summary>
        /// 最后一轮的思考会移到执行过程组里单独成行；移走后正文行不再重复渲染思考。
        /// </summary>
        public bool IsThinkingMoved
        {
            get { return _isThinkingMoved; }
            set
            {
                if (SetProperty(ref _isThinkingMoved, value))
                {
                    OnPropertyChanged(nameof(ShowThinking));
                }
            }
        }

        /// <summary>正文行是否显示思考折叠块；最后一个答案的思考归属过程组。</summary>
        public bool ShowThinking
        {
            get { return _hasThinking && !_isThinkingMoved; }
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
            OnPropertyChanged(nameof(HasVisibleText));
            OnPropertyChanged(nameof(ShowPlainText));
            OnPropertyChanged(nameof(ShowFooter));
        }

        public void AppendThinking(string delta)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            _thinking.Append(delta);
            HasThinking = true;
            ThinkingPreview = BuildThinkingPreview(_thinking.ToString());
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

            bool endsWithToolCall = false;
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
                        endsWithToolCall = false;
                    }
                    else if (string.Equals(block.Type, "thinking", StringComparison.Ordinal))
                    {
                        _thinking.Append(block.Thinking);
                        endsWithToolCall = false;
                    }
                    else if (string.Equals(block.Type, "toolCall", StringComparison.Ordinal))
                    {
                        // 工具调用由工具卡渲染；助手正文不重复显示参数。
                        endsWithToolCall = true;
                    }
                }
            }

            HasThinking = _thinking.Length > 0;
            ThinkingPreview = BuildThinkingPreview(_thinking.ToString());
            IsStreaming = message.Status == AgentMessageStatus.Streaming;
            _continuesWithTools = endsWithToolCall || message.StopReason == StopReason.ToolUse;
            IsFailed = message.Status == AgentMessageStatus.Failed;
            IsInterrupted = message.Status == AgentMessageStatus.Interrupted;
            ErrorText = message.ErrorMessage;
            ModelText = BuildModelText(message);
            UsageText = BuildUsageText(message.Usage);

            StatusText = BuildStatusText(message);
            IsMarkdownDirty = true;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(HasText));
            OnPropertyChanged(nameof(HasVisibleText));
            OnPropertyChanged(nameof(ShowPlainText));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowRetry));
            OnPropertyChanged(nameof(ShowFooter));
            OnPropertyChanged(nameof(ThinkingText));
            OnPropertyChanged(nameof(IsProcessOnly));

            if (rebuildMarkdown)
            {
                RebuildMarkdown();
            }
        }

        public void MarkCompleted(StopReason reason)
        {
            IsStreaming = false;
            _continuesWithTools = reason == StopReason.ToolUse;
            if (reason == StopReason.Length)
            {
                StatusText = "已达到输出上限";
            }

            OnPropertyChanged(nameof(ShowFooter));
            OnPropertyChanged(nameof(IsProcessOnly));
        }

        public void MarkFailed(string error)
        {
            IsStreaming = false;
            IsFailed = true;
            ErrorText = error;
            StatusText = "失败";
            _continuesWithTools = false;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowRetry));
            OnPropertyChanged(nameof(ShowFooter));
            OnPropertyChanged(nameof(IsProcessOnly));
        }

        public void MarkInterrupted()
        {
            IsStreaming = false;
            IsInterrupted = true;
            StatusText = "已中断";
            _continuesWithTools = false;
            OnPropertyChanged(nameof(ShowRetry));
            OnPropertyChanged(nameof(ShowFooter));
            OnPropertyChanged(nameof(IsProcessOnly));
        }

        public void RebuildMarkdown()
        {
            _isMarkdownDirty = false;
            Blocks.Clear();
            string text = Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                OnPropertyChanged(nameof(HasBlocks));
                OnPropertyChanged(nameof(ShowPlainText));
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

            // 最后一块不留底部外边距：消息末尾不该多出一条空白。
            if (Blocks.Count > 0)
            {
                Blocks[Blocks.Count - 1].IsLast = true;
            }

            OnPropertyChanged(nameof(HasBlocks));
            OnPropertyChanged(nameof(ShowPlainText));
        }

        private void CopyText()
        {
            if (_clipboard != null)
            {
                _clipboard.SetText(Text);
            }
        }

        /// <summary>
        /// 末尾的空行与空格不进渲染：模型常在正文/思考末尾补换行，
        /// 直接绑到 TextBlock 会在模块底部多出一条空行。
        /// </summary>
        internal static string TrimTrailingBlankLines(string text)
        {
            return string.IsNullOrEmpty(text) ? text : text.TrimEnd('\r', '\n', ' ', '\t');
        }

        /// <summary>
        /// 思考收起时的一行预览。取"最近一个段落"的首行：流式期间最后一段还在长，
        /// 用段首而不是段尾，摘要才不会每来一个字就整体跳动。
        /// </summary>
        internal static string BuildThinkingPreview(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            int end = lines.Length - 1;
            while (end >= 0 && string.IsNullOrWhiteSpace(lines[end]))
            {
                end--;
            }

            if (end < 0)
            {
                return null;
            }

            int start = end;
            while (start > 0 && !string.IsNullOrWhiteSpace(lines[start - 1]))
            {
                start--;
            }

            string line = lines[start].Trim().TrimStart('#', '>', '-', '*', ' ');
            line = line.Replace("**", string.Empty).Replace("`", string.Empty).Trim();
            if (line.Length == 0)
            {
                line = lines[end].Trim();
            }

            const int MaxLength = 48;
            return line.Length <= MaxLength ? line : line.Substring(0, MaxLength) + "…";
        }

        private static string BuildStatusText(AgentMessage message)
        {
            switch (message.Status)
            {
                case AgentMessageStatus.Streaming:
                    // 运行中的状态由会话计时每秒写成「正在探索，用时 N秒」，这里不再显示「正在生成…」。
                    return null;
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

    /// <summary>
    /// 最后一轮答案里的思考：从正文行里拆出来，作为执行过程组的成员单独一行。
    /// 这样组收起后思考跟着“已完成”一起收起，不会在答案上方留一条孤立的思考行。
    /// </summary>
    public sealed class ProcessThinkingViewModel : ChatItemViewModel
    {
        private bool _isThinkingExpanded;

        public ProcessThinkingViewModel(
            AssistantChatItemViewModel owner,
            string text,
            string preview,
            DateTimeOffset timestamp)
            : base(timestamp)
        {
            Owner = owner;
            ThinkingText = AssistantChatItemViewModel.TrimTrailingBlankLines(text);
            ThinkingPreview = preview ?? string.Empty;
            ToggleThinkingCommand = new RelayCommand(() => IsThinkingExpanded = !IsThinkingExpanded);
        }

        public AssistantChatItemViewModel Owner { get; }

        public string ThinkingText { get; }

        public string ThinkingPreview { get; }

        public bool IsThinkingExpanded
        {
            get { return _isThinkingExpanded; }
            set { SetProperty(ref _isThinkingExpanded, value); }
        }

        public RelayCommand ToggleThinkingCommand { get; }

        public override string AutomationName
        {
            get
            {
                return string.IsNullOrWhiteSpace(ThinkingPreview)
                    ? "思考"
                    : "思考：" + ThinkingPreview;
            }
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

            // 工具行投影：标题与摘要都从统一投影器来，认不出的工具退回旧映射。
            ToolPresentation presentation = ToolPresentationBuilder.ProjectCall(ToolName, ArgumentsJson);
            DisplayName = string.IsNullOrWhiteSpace(presentation.Title)
                ? GetDisplayName(ToolName)
                : presentation.Title;
            Summary = string.IsNullOrWhiteSpace(presentation.Summary)
                ? BuildArgumentsSummary(ToolName, ArgumentsJson)
                : presentation.Summary;
            StatusText = "准备中";
            _isPending = true;

            ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
            OpenFullOutputCommand = new RelayCommand(OpenFullOutput);
            UndoCommand = new RelayCommand(() => UndoRequested?.Invoke(this, EventArgs.Empty), () => CanUndo);
        }

        public event EventHandler UndoRequested;

        public string CallId { get; }

        public string ToolName { get; }

        /// <summary>
        /// 工具行的中文标题。DSH 的工具行读起来是动作名而不是工具 ID，
        /// 认不出的工具退回原始名字，避免出现空标题。
        /// </summary>
        public string DisplayName { get; }

        private static readonly System.Collections.Generic.Dictionary<string, string> ToolTitles =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "read", "读取文件" },
                { "grep", "搜索文件" },
                { "glob", "查找文件" },
                { "edit", "编辑文件" },
                { "write", "写入文件" },
                { "pwsh", "执行命令" },
                { "skill", "加载技能" },
                { "web_fetch", "抓取网页" },
                { "working_directory", "工作目录" },
                { "job_list", "后台任务" },
                { "job_output", "读取后台输出" },
                { "job_kill", "终止后台任务" },
                { "present", "交付文件" },
                { "create_goal", "建立目标" },
                { "get_goal", "读取目标" },
                { "update_goal", "更新目标" }
            };

        public static string GetDisplayName(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return "工具调用";
            }

            string title;
            return ToolTitles.TryGetValue(toolName, out title) ? title : toolName;
        }

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
                // 工具输出常以换行收尾；不裁掉，展开体底部会多出一条空行。
                if (SetProperty(ref _output, AssistantChatItemViewModel.TrimTrailingBlankLines(value)))
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
            Output = (result.Content ?? string.Empty).TrimEnd();
            ErrorText = result.ErrorMessage;
            StatusText = StatusToText(result.Status);
            Summary = string.IsNullOrWhiteSpace(result.Summary)
                ? ToolPresentationBuilder.ProjectResult(ToolName, result)
                : result.Summary;
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
                        Output = (block.ResultContent ?? string.Empty).TrimEnd();
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

        private static string BuildArgumentsSummary(string toolName, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            // 摘要读参数里最像“动作对象”的字段（路径 / 命令 / 查询词），
            // 不把整段 JSON 摊在行内；解析失败才回落到原文。
            try
            {
                JsonValue root = JsonParser.Parse(json);
                string text = ReadArgument(root, "command");
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = ReadArgument(root, "path");
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = ReadArgument(root, "pattern");
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = ReadArgument(root, "skill");
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    text = ReadArgument(root, "name");
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return ClipSummary(text);
                }
            }
            catch (Exception)
            {
            }

            return ClipSummary(json.Replace("\r", " ").Replace("\n", " ").Trim());
        }

        private static string ReadArgument(JsonValue value, string name)
        {
            JsonValue node = value == null ? null : value.Get(name);
            return node == null ? null : node.AsString(null);
        }

        private static string ClipSummary(string value)
        {
            string text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 160 ? text : text.Substring(0, 160) + "…";
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
            Body = AssistantChatItemViewModel.TrimTrailingBlankLines(body);
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

    /// <summary>
    /// 一轮里的「执行过程」：以工具调用收尾的助手步骤 + 工具卡。
    /// 组头是一行摘要（思考 / 读取 / 命令…），组体是成员行；收起时成员折叠成 0 高，
    /// 但不从 <c>Items</c> 移除，因此重试、追溯与虚拟化仍按原来的平铺列表工作。
    /// </summary>
    public sealed class ProcessGroupViewModel : ChatItemViewModel
    {
        private readonly System.Collections.Generic.List<ChatItemViewModel> _members =
            new System.Collections.Generic.List<ChatItemViewModel>();

        private bool _isExpanded;
        private string _summary = "执行过程";
        private string _headerLabel = "正在探索，用时 ";
        private string _headerDuration = "1秒";
        private bool _isCompleted;
        private bool _failed;
        private bool _stopped;
        private readonly DateTimeOffset _startedAt;
        private DateTimeOffset _completedAt;

        public ProcessGroupViewModel(DateTimeOffset timestamp, bool expanded)
            : base(timestamp)
        {
            _startedAt = timestamp;
            _isExpanded = expanded;
            ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
            UpdateHeader(timestamp);
        }

        /// <summary>成员清空（重试裁掉整轮）时通知持有者把组头和组一起移除。</summary>
        public event EventHandler Emptied;

        public RelayCommand ToggleExpandedCommand { get; }

        public System.Collections.Generic.IReadOnlyList<ChatItemViewModel> Members
        {
            get { return _members; }
        }

        public bool IsEmpty
        {
            get { return _members.Count == 0; }
        }

        /// <summary>组内是否已有成员；空组只显示计时，不显示展开箭头。</summary>
        public bool HasMembers
        {
            get { return _members.Count > 0; }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (!SetProperty(ref _isExpanded, value))
                {
                    return;
                }

                foreach (ChatItemViewModel member in _members)
                {
                    member.IsHiddenByGroup = !_isExpanded;
                }
            }
        }

        /// <summary>组内活动摘要：思考 · 读取 2 项 · 运行 1 条命令（组头 tooltip 与读屏用）。</summary>
        public string Summary
        {
            get { return _summary; }
            private set
            {
                if (SetProperty(ref _summary, value))
                {
                    OnPropertyChanged(nameof(HeaderToolTip));
                    OnPropertyChanged(nameof(AutomationName));
                }
            }
        }

        /// <summary>悬停提示：状态 + 组内活动摘要，组头本身只显示状态与用时。</summary>
        public string HeaderToolTip
        {
            get { return _summary; }
        }

        /// <summary>组头状态词（不含时长），对齐 DSH 的「已完成，用时 」。</summary>
        public string HeaderLabel
        {
            get { return _headerLabel; }
            private set
            {
                if (SetProperty(ref _headerLabel, value))
                {
                    OnPropertyChanged(nameof(HeaderText));
                    OnPropertyChanged(nameof(AutomationName));
                }
            }
        }

        /// <summary>组头时长片段：<c>12秒</c> / <c>1分5秒</c> / <c>1小时2分3秒</c>。</summary>
        public string HeaderDuration
        {
            get { return _headerDuration; }
            private set
            {
                if (SetProperty(ref _headerDuration, value))
                {
                    OnPropertyChanged(nameof(HeaderText));
                    OnPropertyChanged(nameof(AutomationName));
                }
            }
        }

        public string HeaderText
        {
            get { return _headerLabel + _headerDuration; }
        }

        /// <summary>整轮结束时才算完成；历史回放同样按消息时间戳得到真实用时。</summary>
        public bool IsCompleted
        {
            get { return _isCompleted; }
        }

        public override string AutomationName
        {
            get { return "执行过程：" + HeaderText + "，" + _summary; }
        }

        public void Add(ChatItemViewModel member)
        {
            if (member == null || _members.Contains(member))
            {
                return;
            }

            member.IsProcessMember = true;
            member.IsHiddenByGroup = !_isExpanded;
            member.PropertyChanged += OnMemberPropertyChanged;
            _members.Add(member);
            OnPropertyChanged(nameof(HasMembers));
            Refresh();
        }

        public void Remove(ChatItemViewModel member)
        {
            if (member == null || !_members.Remove(member))
            {
                return;
            }

            member.PropertyChanged -= OnMemberPropertyChanged;
            member.IsProcessMember = false;
            member.IsHiddenByGroup = false;
            OnPropertyChanged(nameof(HasMembers));
            Refresh();

            if (_members.Count == 0)
            {
                EventHandler emptied = Emptied;
                if (emptied != null)
                {
                    emptied(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// 整轮结束：记录收尾时刻与结局，组头从「运行中」切到「已完成 / 处理失败 / 已停止」。
        /// 失败与停止按 DSH 规则不显示用时。
        /// </summary>
        public void Complete(DateTimeOffset at, bool failed = false, bool stopped = false)
        {
            if (_isCompleted)
            {
                return;
            }

            _isCompleted = true;
            _failed = failed;
            _stopped = stopped;
            _completedAt = at < _startedAt ? _startedAt : at;
            UpdateHeader(_completedAt);
        }

        /// <summary>整轮结束时自动收起；用户手动展开的历史组不受影响。</summary>
        public void CollapseAfterTurn()
        {
            IsExpanded = false;
        }

        /// <summary>运行中每秒刷新「用时」；已完成的组不再变动。</summary>
        public void RefreshElapsed(DateTimeOffset now)
        {
            if (!_isCompleted)
            {
                UpdateHeader(now);
            }
        }

        /// <summary>
        /// DSH 的时长写法：只保留有效单位（秒 / 分 / 小时），单位紧贴数字。
        /// 不足一秒按一秒算，避免刚建组时显示 0秒。
        /// </summary>
        internal static string FormatRunDuration(TimeSpan value)
        {
            long total = (long)Math.Floor(value.TotalSeconds);
            if (total < 1)
            {
                total = 1;
            }

            long hours = total / 3600;
            long minutes = (total % 3600) / 60;
            long seconds = total % 60;
            var text = new StringBuilder();
            if (hours > 0)
            {
                text.Append(hours).Append("小时");
            }

            if (total >= 60)
            {
                text.Append(minutes).Append("分");
            }

            text.Append(seconds).Append("秒");
            return text.ToString();
        }

        private void UpdateHeader(DateTimeOffset now)
        {
            string label;
            string duration;
            if (_stopped)
            {
                label = "已停止";
                duration = string.Empty;
            }
            else if (_failed)
            {
                label = "处理失败";
                duration = string.Empty;
            }
            else if (_isCompleted)
            {
                label = "已完成，用时 ";
                duration = FormatRunDuration(_completedAt - _startedAt);
            }
            else
            {
                label = "正在探索，用时 ";
                duration = FormatRunDuration(now - _startedAt);
            }

            HeaderLabel = label;
            HeaderDuration = duration;
        }

        private void OnMemberPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AssistantChatItemViewModel.HasThinking))
            {
                Refresh();
            }
        }

        private void Refresh()
        {
            int thinking = 0;
            int read = 0;
            int search = 0;
            int write = 0;
            int command = 0;
            int skill = 0;
            int other = 0;

            foreach (ChatItemViewModel member in _members)
            {
                if (member is ProcessThinkingViewModel)
                {
                    thinking++;
                    continue;
                }

                var assistant = member as AssistantChatItemViewModel;
                if (assistant != null)
                {
                    if (assistant.HasThinking)
                    {
                        thinking++;
                    }

                    continue;
                }

                var tool = member as ToolCardViewModel;
                if (tool == null)
                {
                    continue;
                }

                string name = tool.ToolName ?? string.Empty;
                if (name == "read")
                {
                    read++;
                }
                else if (name == "grep" || name == "glob")
                {
                    search++;
                }
                else if (name == "write" || name == "edit")
                {
                    write++;
                }
                else if (name == "pwsh")
                {
                    command++;
                }
                else if (name == "skill")
                {
                    skill++;
                }
                else
                {
                    other++;
                }
            }

            var parts = new System.Collections.Generic.List<string>();
            if (thinking > 0)
            {
                parts.Add("思考");
            }

            if (read > 0)
            {
                parts.Add("读取 " + read + " 项");
            }

            if (search > 0)
            {
                parts.Add("搜索 " + search + " 次");
            }

            if (write > 0)
            {
                parts.Add("写入 " + write + " 个文件");
            }

            if (command > 0)
            {
                parts.Add("运行 " + command + " 条命令");
            }

            if (skill > 0)
            {
                parts.Add("加载 " + skill + " 个技能");
            }

            if (other > 0)
            {
                parts.Add("调用 " + other + " 个工具");
            }

            Summary = parts.Count == 0 ? "执行过程" : string.Join(" · ", parts);
        }
    }
}
