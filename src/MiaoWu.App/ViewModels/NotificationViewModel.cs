using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 右下角通知。审批与提问通知不自动消失、不提供关闭按钮（必须显式处理）；
    /// 完成通知 4 秒后自动消失。最多 3 条由 MainWindowViewModel 维护。
    /// </summary>
    public sealed class NotificationViewModel : ObservableObject
    {
        private readonly Action<NotificationViewModel> _dismiss;
        private readonly Action<string, ToolApprovalOutcome> _resolveApproval;
        private readonly Action<string, IReadOnlyList<string>> _answerQuestion;
        private readonly Action<string, string> _answerQuestionText;
        private readonly Action<string> _skipQuestion;
        private readonly HashSet<string> _selectedOptionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            bool canShowDiff = false,
            IReadOnlyList<UserQuestionOption> questionOptions = null,
            bool questionMultiSelect = false,
            Action<string, IReadOnlyList<string>> answerQuestion = null,
            Action<string> skipQuestion = null,
            bool questionAllowFreeText = false,
            Action<string, string> answerQuestionText = null)
        {
            Id = Guid.NewGuid().ToString("N");
            Title = title;
            Body = body;
            IsApproval = isApproval;
            IsQuestion = !isApproval && answerQuestion != null;
            QuestionMultiSelect = IsQuestion && questionMultiSelect;
            AllowFreeText = IsQuestion && questionAllowFreeText && answerQuestionText != null;
            _answerQuestionText = answerQuestionText;
            SessionId = sessionId;
            RequestId = requestId;
            CanApproveForSession = canApproveForSession;
            Diff = diff;
            HasDiff = canShowDiff && !string.IsNullOrWhiteSpace(diff);
            _dismiss = dismiss;
            _resolveApproval = resolveApproval;
            _answerQuestion = answerQuestion;
            _skipQuestion = skipQuestion;

            Options = new ObservableCollection<QuestionOptionViewModel>();
            if (IsQuestion && questionOptions != null)
            {
                for (int i = 0; i < questionOptions.Count; i++)
                {
                    UserQuestionOption option = questionOptions[i];
                    if (option == null)
                    {
                        continue;
                    }

                    Options.Add(new QuestionOptionViewModel(
                        option,
                        OnOptionChosen,
                        () => IsQuestion && !_isResolved));
                }
            }

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
            SubmitAnswerCommand = new RelayCommand(
                SubmitAnswer,
                () => IsQuestion && !_isResolved && QuestionMultiSelect && _selectedOptionIds.Count > 0);
            SkipQuestionCommand = new RelayCommand(
                SkipQuestion,
                () => IsQuestion && !_isResolved);
            SubmitFreeTextCommand = new RelayCommand(
                SubmitFreeText,
                () => AllowFreeText && !_isResolved && !string.IsNullOrWhiteSpace(FreeText));

            if (!isApproval && !IsQuestion)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = autoDismissAfter ?? TimeSpan.FromSeconds(4)
                };
                _timer.Tick += OnTimerTick;
                _timer.Start();
            }
        }

        /// <summary>从一条提问构造通知：标题带 header，正文是问题，动作是选项按钮。</summary>
        public static NotificationViewModel CreateQuestion(
            UserQuestionRequest request,
            Action<NotificationViewModel> dismiss,
            Action<string, IReadOnlyList<string>> answerQuestion,
            Action<string> skipQuestion,
            Action<string, string> answerQuestionText = null)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string title = string.IsNullOrWhiteSpace(request.Header)
                ? "需要你回答"
                : "需要你回答 · " + request.Header;

            return new NotificationViewModel(
                title,
                request.Question,
                false,
                dismiss,
                null,
                request.SessionId,
                request.RequestId,
                false,
                null,
                null,
                false,
                request.Options,
                request.MultiSelect,
                answerQuestion,
                skipQuestion,
                request.AllowFreeText,
                answerQuestionText);
        }

        public string Id { get; }

        public string Title { get; }

        public string Body { get; }

        public bool IsApproval { get; }

        /// <summary>提问通知：必须选一个选项或跳过。</summary>
        public bool IsQuestion { get; }

        /// <summary>提问允许用户输入自由文本作答。</summary>
        public bool AllowFreeText { get; }

        private string _freeText;

        /// <summary>自由文本回答的草稿；“提交”可用性跟着它走。</summary>
        public string FreeText
        {
            get { return _freeText; }
            set
            {
                if (SetProperty(ref _freeText, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool QuestionMultiSelect { get; }

        /// <summary>问题选项；非提问通知为空集合。</summary>
        public ObservableCollection<QuestionOptionViewModel> Options { get; }

        /// <summary>非审批、非提问的普通通知可以手动关闭。</summary>
        public bool IsNotice
        {
            get { return !IsApproval && !IsQuestion; }
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
                    OnPropertyChanged(nameof(ShowQuestionActions));
                    InvalidateOptionState();
                    CommandManager.InvalidateRequerySuggested();
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

        /// <summary>提问的选项与“确认 / 跳过”只在未答复时显示。</summary>
        public bool ShowQuestionActions
        {
            get { return IsQuestion && !_isResolved; }
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

        public RelayCommand SubmitAnswerCommand { get; }

        public RelayCommand SkipQuestionCommand { get; }

        public RelayCommand SubmitFreeTextCommand { get; }

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

        private void OnOptionChosen(QuestionOptionViewModel option)
        {
            if (!IsQuestion || _isResolved || option == null || RequestId == null)
            {
                return;
            }

            if (!QuestionMultiSelect)
            {
                Answer(new[] { option.Id });
                return;
            }

            option.SetSelected(!option.IsSelected);
            if (option.IsSelected)
            {
                _selectedOptionIds.Add(option.Id);
            }
            else
            {
                _selectedOptionIds.Remove(option.Id);
            }

            CommandManager.InvalidateRequerySuggested();
        }

        private void SubmitAnswer()
        {
            if (_selectedOptionIds.Count == 0)
            {
                return;
            }

            var ids = new List<string>(_selectedOptionIds);
            Answer(ids);
        }

        private void Answer(IReadOnlyList<string> optionIds)
        {
            if (_answerQuestion == null || RequestId == null || _isResolved)
            {
                return;
            }

            _answerQuestion(RequestId, optionIds);
            MarkResolved("已回答");
        }

        private void SkipQuestion()
        {
            if (_skipQuestion != null && RequestId != null)
            {
                _skipQuestion(RequestId);
            }

            MarkResolved("已跳过");
        }

        /// <summary>自由文本回答：空白与超长都不提交；提交后按钮禁用。</summary>
        private void SubmitFreeText()
        {
            if (!AllowFreeText || _isResolved || RequestId == null || _answerQuestionText == null)
            {
                return;
            }

            string text = (FreeText ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > UserQuestionRequest.MaxAnswerTextLength)
            {
                return;
            }

            _answerQuestionText(RequestId, text);
            MarkResolved("已回答");
        }

        private void InvalidateOptionState()
        {
            for (int i = 0; i < Options.Count; i++)
            {
                Options[i].SetActive(false);
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

    /// <summary>提问通知里的一个选项。多选时点击只切换选中，单选时点击即回答。</summary>
    public sealed class QuestionOptionViewModel : ObservableObject
    {
        private readonly Action<QuestionOptionViewModel> _choose;
        private readonly Func<bool> _canChoose;
        private bool _isSelected;
        private bool _isActive = true;

        public QuestionOptionViewModel(
            UserQuestionOption option,
            Action<QuestionOptionViewModel> choose,
            Func<bool> canChoose)
        {
            if (option == null)
            {
                throw new ArgumentNullException(nameof(option));
            }

            Id = option.Id;
            Label = option.Label;
            Description = option.Description;
            _choose = choose;
            _canChoose = canChoose;
            ChooseCommand = new RelayCommand(
                () => _choose(this),
                () => _isActive && _canChoose != null && _canChoose());
        }

        public string Id { get; }

        public string Label { get; }

        public string Description { get; }

        public bool HasDescription
        {
            get { return !string.IsNullOrWhiteSpace(Description); }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
        }

        public RelayCommand ChooseCommand { get; }

        /// <summary>多选切换选中态；单选不会调用。</summary>
        public void SetSelected(bool value)
        {
            if (SetProperty(ref _isSelected, value, nameof(IsSelected)))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>通知被答复后禁用按钮，避免重复点击。</summary>
        public void SetActive(bool active)
        {
            if (_isActive == active)
            {
                return;
            }

            _isActive = active;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
