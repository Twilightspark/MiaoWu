using System;
using System.Windows.Threading;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 右下角通知。审批通知不自动消失、不提供关闭按钮（必须显式允许或拒绝）；
    /// 完成通知 4 秒后自动消失。最多 3 条由 MainWindowViewModel 维护。
    /// </summary>
    public sealed class NotificationViewModel : ObservableObject
    {
        private readonly Action<NotificationViewModel> _dismiss;
        private readonly Action<string, ToolApprovalOutcome> _resolveApproval;
        private readonly DispatcherTimer _timer;
        private bool _isResolved;
        private string _resolutionText;
        private bool _isDiffVisible;

        public NotificationViewModel(
            string title,
            string body,
            bool isApproval,
            Action<NotificationViewModel> dismiss,
            Action<string, ToolApprovalOutcome> resolveApproval = null,
            string sessionId = null,
            string requestId = null,
            bool canApproveForSession = false,
            TimeSpan? autoDismissAfter = null,
            string diff = null,
            bool canShowDiff = false)
        {
            Id = Guid.NewGuid().ToString("N");
            Title = title;
            Body = body;
            IsApproval = isApproval;
            SessionId = sessionId;
            RequestId = requestId;
            CanApproveForSession = canApproveForSession;
            Diff = diff;
            HasDiff = canShowDiff && !string.IsNullOrWhiteSpace(diff);
            _dismiss = dismiss;
            _resolveApproval = resolveApproval;

            DismissCommand = new RelayCommand(() => _dismiss(this));
            AllowOnceCommand = new RelayCommand(
                () => Resolve(ToolApprovalOutcome.ApproveOnce),
                () => IsApproval && !_isResolved);
            AllowSessionCommand = new RelayCommand(
                () => Resolve(ToolApprovalOutcome.ApproveForSession),
                () => IsApproval && !_isResolved && CanApproveForSession);
            DenyCommand = new RelayCommand(
                () => Resolve(ToolApprovalOutcome.Deny),
                () => IsApproval && !_isResolved);
            ToggleDiffCommand = new RelayCommand(() => IsDiffVisible = !IsDiffVisible);

            if (!isApproval)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = autoDismissAfter ?? TimeSpan.FromSeconds(4)
                };
                _timer.Tick += OnTimerTick;
                _timer.Start();
            }
        }

        public string Id { get; }

        public string Title { get; }

        public string Body { get; }

        public bool IsApproval { get; }

        /// <summary>非审批通知可以手动关闭；审批通知必须显式处理。</summary>
        public bool IsNotice
        {
            get { return !IsApproval; }
        }

        public string SessionId { get; }

        public string RequestId { get; }

        public bool CanApproveForSession { get; }

        /// <summary>审批附带的 unified diff；为空表示这一条没有可看的改动。</summary>
        public string Diff { get; }

        public bool HasDiff { get; }

        public bool IsDiffVisible
        {
            get { return _isDiffVisible; }
            set
            {
                if (SetProperty(ref _isDiffVisible, value))
                {
                    OnPropertyChanged(nameof(DiffToggleLabel));
                }
            }
        }

        public string DiffToggleLabel
        {
            get { return _isDiffVisible ? "收起改动" : "查看改动"; }
        }

        public bool IsResolved
        {
            get { return _isResolved; }
            private set
            {
                if (SetProperty(ref _isResolved, value))
                {
                    OnPropertyChanged(nameof(IsPending));
                    OnPropertyChanged(nameof(ShowApprovalActions));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsPending
        {
            get { return !_isResolved; }
        }

        /// <summary>只给尚未答复的审批显示动作按钮；答复后只留一句结果。</summary>
        public bool ShowApprovalActions
        {
            get { return IsApproval && !_isResolved; }
        }

        public string ResolutionText
        {
            get { return _resolutionText; }
            private set { SetProperty(ref _resolutionText, value); }
        }

        public RelayCommand DismissCommand { get; }

        public RelayCommand AllowOnceCommand { get; }

        public RelayCommand AllowSessionCommand { get; }

        public RelayCommand DenyCommand { get; }

        public RelayCommand ToggleDiffCommand { get; }

        public void MarkResolved(string text)
        {
            IsResolved = true;
            ResolutionText = text;
            StopTimer();
        }

        public void StopTimer()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTimerTick;
            }
        }

        private void Resolve(ToolApprovalOutcome outcome)
        {
            if (!IsApproval || _isResolved || _resolveApproval == null || RequestId == null)
            {
                return;
            }

            _resolveApproval(RequestId, outcome);
            MarkResolved(outcome == ToolApprovalOutcome.Deny ? "已拒绝" : "已允许");
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            StopTimer();
            if (_dismiss != null)
            {
                _dismiss(this);
            }
        }
    }
}
