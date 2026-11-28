using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 待处理弹窗（覆盖输入卡）：只有审批与提问，不会自动消失、也没有关闭按钮——
    /// 必须显式允许 / 拒绝，或选择 / 手动输入 / 跳过。最多 3 条由 MainWindowViewModel 维护。
    /// 普通提示（回答完成等）不再进应用内通知，改走系统通知。
    /// </summary>
    public sealed class NotificationViewModel : ObservableObject
    {
        private readonly Action<string, ToolApprovalOutcome> _resolveApproval;
        private readonly Action<string, IReadOnlyList<string>> _answerQuestion;
        private readonly Action<string, string> _answerQuestionText;
        private readonly Action<string> _skipQuestion;
        private readonly HashSet<string> _selectedOptionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _isResolved;
        private bool _isDiffVisible;

        public NotificationViewModel(
            string title,
            string body,
            bool isApproval,
            Action<string, ToolApprovalOutcome> resolveApproval = null,
            string sessionId = null,
            string requestId = null,
            bool canApproveForSession = false,
            string diff = null,
            bool canShowDiff = false,
            IReadOnlyList<UserQuestionOption> questionOptions = null,
            bool questionMultiSelect = false,
            Action<string, IReadOnlyList<string>> answerQuestion = null,
            Action<string> skipQuestion = null,
            Action<string, string> answerQuestionText = null)
        {
            Id = Guid.NewGuid().ToString("N");
            Title = title;
            Body = body;
            IsApproval = isApproval;
            IsQuestion = !isApproval && answerQuestion != null;
            QuestionMultiSelect = IsQuestion && questionMultiSelect;
            ShowsManualInput = IsQuestion;
            _answerQuestionText = answerQuestionText;
            SessionId = sessionId;
            RequestId = requestId;
            CanApproveForSession = canApproveForSession;
            Diff = diff;
            HasDiff = canShowDiff && !string.IsNullOrWhiteSpace(diff);
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
                () => ShowsManualInput && !_isResolved && !string.IsNullOrWhiteSpace(FreeText));
        }

        /// <summary>从一条提问构造弹窗：标题带 header，正文是问题，动作是选项按钮 + 手动输入。</summary>
        public static NotificationViewModel CreateQuestion(
            UserQuestionRequest request,
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
                null,
                request.SessionId,
                request.RequestId,
                false,
                null,
                false,
                request.Options,
                request.MultiSelect,
                answerQuestion,
                skipQuestion,
                answerQuestionText);
        }

        public string Id { get; }

        public string Title { get; }

        public string Body { get; }

        public bool IsApproval { get; }

        /// <summary>提问弹窗：必须选一个选项、自己写一句或跳过。</summary>
        public bool IsQuestion { get; }

        /// <summary>提问始终提供手动输入：选项是快捷项，不是唯一答案。</summary>
        public bool ShowsManualInput { get; }

        private string _freeText;

        /// <summary>手动输入的回答草稿；“提交”可用性跟着它走。</summary>
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

        /// <summary>问题选项；非提问弹窗为空集合。</summary>
        public ObservableCollection<QuestionOptionViewModel> Options { get; }

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
                    OnPropertyChanged(nameof(ShowApprovalActions));
                    OnPropertyChanged(nameof(ShowQuestionActions));
                    InvalidateOptionState();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>只给尚未答复的审批显示动作按钮；答复后弹窗会被移除。</summary>
        public bool ShowApprovalActions
        {
            get { return IsApproval && !_isResolved; }
        }

        /// <summary>提问的选项与“确认 / 跳过”只在未答复时显示。</summary>
        public bool ShowQuestionActions
        {
            get { return IsQuestion && !_isResolved; }
        }

        public RelayCommand AllowOnceCommand { get; }

        public RelayCommand AllowSessionCommand { get; }

        public RelayCommand DenyCommand { get; }

        public RelayCommand ToggleDiffCommand { get; }

        public RelayCommand SubmitAnswerCommand { get; }

        public RelayCommand SkipQuestionCommand { get; }

        public RelayCommand SubmitFreeTextCommand { get; }

        public void MarkResolved()
        {
            IsResolved = true;
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
            MarkResolved();
        }

        private void SkipQuestion()
        {
            if (_skipQuestion != null && RequestId != null)
            {
                _skipQuestion(RequestId);
            }

            MarkResolved();
        }

        /// <summary>手动输入的回答：空白与超长都不提交；提交后按钮禁用。</summary>
        private void SubmitFreeText()
        {
            if (!ShowsManualInput || _isResolved || RequestId == null || _answerQuestionText == null)
            {
                return;
            }

            string text = (FreeText ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > UserQuestionRequest.MaxAnswerTextLength)
            {
                return;
            }

            _answerQuestionText(RequestId, text);
            MarkResolved();
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
            MarkResolved();
        }
    }

    /// <summary>提问弹窗里的一个选项。多选时点击只切换选中，单选时点击即回答。</summary>
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

        /// <summary>弹窗被答复后禁用按钮，避免重复点击。</summary>
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
