using System;
using System.Windows.Threading;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 右下角通知。审批通知不自动消失；完成通知 4 秒后自动消失。
    /// 最多 3 条由 MainWindowViewModel 维护。
    /// </summary>
    public sealed class NotificationViewModel : ObservableObject
    {
        private readonly Action<NotificationViewModel> _dismiss;
        private readonly Action<string, ToolApprovalOutcome> _resolveApproval;
        private readonly DispatcherTimer _timer;
        private bool _isResolved;
        private string _resolutionText;

        public NotificationViewModel(
            string title,
            string body,
            bool isApproval,
            Action<NotificationViewModel> dismiss,
            Action<string, ToolApprovalOutcome> resolveApproval = null,
            string sessionId = null,
            string requestId = null,
            bool canApproveForSession = false,
            TimeSpan? autoDismissAfter = null)
        {
            Id = Guid.NewGuid().ToString("N");
            Title = title;
            Body = body;
            IsApproval = isApproval;
            SessionId = sessionId;
            RequestId = requestId;
            CanApproveForSession = canApproveForSession;
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

        public bool IsNotice
        {
            get { return !IsApproval; }
        }

        public string SessionId { get; }

        public string RequestId { get; }

        public bool CanApproveForSession { get; }

        public bool IsResolved
        {
            get { return _isResolved; }
            private set
            {
                if (SetProperty(ref _isResolved, value))
                {
                    OnPropertyChanged(nameof(IsPending));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsPending
        {
            get { return !_isResolved; }
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
