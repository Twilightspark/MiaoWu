using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Agents.Schedules;

namespace MiaoWu.App.ViewModels
{
    /// <summary>运行记录里的一行：发生时间、来源（到点 / 手动）与当时的指令快照。</summary>
    public sealed class ScheduleHistoryRowViewModel
    {
        public ScheduleHistoryRowViewModel(ScheduleDeliveryRecord record)
        {
            if (record == null)
            {
                return;
            }

            TimeText = record.DeliveredAt.ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            KindText = record.Kind == ScheduleDeliveryKind.Manual ? "立即执行" : "到点投递";
            Prompt = Trim(record.Prompt, 200);
            Failed = record.Failed;
            StatusText = record.Failed ? "投递失败" : "已排进会话";
        }

        public string TimeText { get; }

        public string KindText { get; }

        public string Prompt { get; }

        public bool Failed { get; }

        public string StatusText { get; }

        private static string Trim(string text, int max)
        {
            string value = (text ?? string.Empty).Replace("\r", string.Empty).Replace('\n', ' ').Trim();
            return value.Length > max ? value.Substring(0, max) + "…" : value;
        }
    }

    /// <summary>
    /// 单条定时任务的编辑草稿。字段都是表单值（时间用字符串，允许输入中间态），
    /// 保存时再由 <see cref="ScheduleRuleDraft"/> 生成规则；<see cref="HasChanges"/> 只比较少数字段。
    /// </summary>
    public sealed class ScheduleEditorViewModel : ObservableObject
    {
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<string, string> _workspaceName;

        private string _id;
        private string _title = string.Empty;
        private string _prompt = string.Empty;
        private ScheduleRepeatMode _mode = ScheduleRepeatMode.Daily;
        private string _hourText = "09";
        private string _minuteText = "00";
        private string _intervalText = "1";
        private bool _sunday;
        private bool _monday = true;
        private bool _tuesday = true;
        private bool _wednesday = true;
        private bool _thursday = true;
        private bool _friday = true;
        private bool _saturday;
        private string _customRule = string.Empty;
        private ScheduleTarget _target = ScheduleTarget.NewOnce;
        private string _sessionId;
        private string _currentSessionId;
        private string _workspaceId;
        private string _workspaceDisplay;
        private ScheduleDefinition _model;
        private DateTimeOffset? _baselineUpdatedAt;
        private bool _isConflict;
        private string _errorMessage;
        private string _rulePreview = string.Empty;
        private string _nextRunPreview = string.Empty;
        private string _initialKey = string.Empty;
        private bool _loading;

        public ScheduleEditorViewModel(
            Func<DateTimeOffset> now,
            Func<string, string> workspaceName)
        {
            _now = now ?? (() => DateTimeOffset.Now);
            _workspaceName = workspaceName ?? (id => id);
            History = new ObservableCollection<ScheduleHistoryRowViewModel>();
        }

        /// <summary>任务 id；新建草稿为 null。</summary>
        public string Id
        {
            get { return _id; }
            private set { SetProperty(ref _id, value); OnPropertyChanged(nameof(IsExisting)); OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(HeaderTitle)); }
        }

        public bool IsExisting
        {
            get { return !string.IsNullOrEmpty(_id); }
        }

        public string PageTitle
        {
            get { return IsExisting ? "任务规则" : "新建定时任务"; }
        }

        /// <summary>详情头显示任务名；未保存或未命名时退回页面标题。</summary>
        public string HeaderTitle
        {
            get
            {
                if (!IsExisting)
                {
                    return "新建定时任务";
                }

                return string.IsNullOrWhiteSpace(_title) ? "未命名任务" : _title.Trim();
            }
        }

        public string Title
        {
            get { return _title; }
            set
            {
                if (SetProperty(ref _title, value))
                {
                    OnPropertyChanged(nameof(HeaderTitle));
                    OnEdited();
                }
            }
        }

        public string Prompt
        {
            get { return _prompt; }
            set { if (SetProperty(ref _prompt, value)) OnEdited(); }
        }

        public ScheduleRepeatMode Mode
        {
            get { return _mode; }
            set
            {
                if (!SetProperty(ref _mode, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsOnce));
                OnPropertyChanged(nameof(IsDaily));
                OnPropertyChanged(nameof(IsWeekly));
                OnPropertyChanged(nameof(IsMonthly));
                OnPropertyChanged(nameof(IsCustom));
                OnPropertyChanged(nameof(IntervalUnit));
                OnPropertyChanged(nameof(ShowsInterval));
                OnPropertyChanged(nameof(ShowsWeekdays));
                OnPropertyChanged(nameof(ShowsCustomRule));
                OnEdited();
            }
        }

        public bool IsOnce
        {
            get { return _mode == ScheduleRepeatMode.Once; }
            set { if (value) Mode = ScheduleRepeatMode.Once; }
        }

        public bool IsDaily
        {
            get { return _mode == ScheduleRepeatMode.Daily; }
            set { if (value) Mode = ScheduleRepeatMode.Daily; }
        }

        public bool IsWeekly
        {
            get { return _mode == ScheduleRepeatMode.Weekly; }
            set { if (value) Mode = ScheduleRepeatMode.Weekly; }
        }

        public bool IsMonthly
        {
            get { return _mode == ScheduleRepeatMode.Monthly; }
            set { if (value) Mode = ScheduleRepeatMode.Monthly; }
        }

        public bool IsCustom
        {
            get { return _mode == ScheduleRepeatMode.Custom; }
            set { if (value) Mode = ScheduleRepeatMode.Custom; }
        }

        public bool ShowsInterval
        {
            get { return _mode != ScheduleRepeatMode.Once && _mode != ScheduleRepeatMode.Custom; }
        }

        public bool ShowsWeekdays
        {
            get { return _mode == ScheduleRepeatMode.Weekly; }
        }

        public bool ShowsCustomRule
        {
            get { return _mode == ScheduleRepeatMode.Custom; }
        }

        public string IntervalUnit
        {
            get
            {
                if (_mode == ScheduleRepeatMode.Weekly)
                {
                    return "周";
                }

                return _mode == ScheduleRepeatMode.Monthly ? "月" : "天";
            }
        }

        public string HourText
        {
            get { return _hourText; }
            set { if (SetProperty(ref _hourText, value)) OnEdited(); }
        }

        public string MinuteText
        {
            get { return _minuteText; }
            set { if (SetProperty(ref _minuteText, value)) OnEdited(); }
        }

        public string IntervalText
        {
            get { return _intervalText; }
            set { if (SetProperty(ref _intervalText, value)) OnEdited(); }
        }

        public bool Sunday
        {
            get { return _sunday; }
            set { if (SetProperty(ref _sunday, value)) OnEdited(); }
        }

        public bool Monday
        {
            get { return _monday; }
            set { if (SetProperty(ref _monday, value)) OnEdited(); }
        }

        public bool Tuesday
        {
            get { return _tuesday; }
            set { if (SetProperty(ref _tuesday, value)) OnEdited(); }
        }

        public bool Wednesday
        {
            get { return _wednesday; }
            set { if (SetProperty(ref _wednesday, value)) OnEdited(); }
        }

        public bool Thursday
        {
            get { return _thursday; }
            set { if (SetProperty(ref _thursday, value)) OnEdited(); }
        }

        public bool Friday
        {
            get { return _friday; }
            set { if (SetProperty(ref _friday, value)) OnEdited(); }
        }

        public bool Saturday
        {
            get { return _saturday; }
            set { if (SetProperty(ref _saturday, value)) OnEdited(); }
        }

        public string CustomRule
        {
            get { return _customRule; }
            set { if (SetProperty(ref _customRule, value)) OnEdited(); }
        }

        public ScheduleTarget Target
        {
            get { return _target; }
            private set
            {
                if (!SetProperty(ref _target, value))
                {
                    return;
                }

                if (value == ScheduleTarget.ThisSession && string.IsNullOrWhiteSpace(_sessionId))
                {
                    _sessionId = _currentSessionId;
                }

                if (!IsExisting && value != ScheduleTarget.ThisSession)
                {
                    _sessionId = null;
                }

                OnPropertyChanged(nameof(IsTargetNewOnce));
                OnPropertyChanged(nameof(IsTargetThisSession));
                OnPropertyChanged(nameof(IsTargetNew));
                OnPropertyChanged(nameof(SessionId));
                OnPropertyChanged(nameof(SessionLabel));
                OnEdited();
            }
        }

        public bool IsTargetNewOnce
        {
            get { return _target == ScheduleTarget.NewOnce; }
            set { if (value) Target = ScheduleTarget.NewOnce; }
        }

        public bool IsTargetThisSession
        {
            get { return _target == ScheduleTarget.ThisSession; }
            set { if (value) Target = ScheduleTarget.ThisSession; }
        }

        public bool IsTargetNew
        {
            get { return _target == ScheduleTarget.New; }
            set { if (value) Target = ScheduleTarget.New; }
        }

        public string SessionId
        {
            get { return _sessionId; }
        }

        public string WorkspaceId
        {
            get { return _workspaceId; }
        }

        public string WorkspaceLabel
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_workspaceId))
                {
                    return "未关联工作区";
                }

                return string.IsNullOrWhiteSpace(_workspaceDisplay) ? _workspaceId : _workspaceDisplay;
            }
        }

        public string SessionLabel
        {
            get
            {
                switch (_target)
                {
                    case ScheduleTarget.ThisSession:
                        string current = string.IsNullOrWhiteSpace(_sessionId) ? _currentSessionId : _sessionId;
                        return string.IsNullOrWhiteSpace(current)
                            ? "当前没有打开的会话"
                            : "当前会话 · " + current;
                    case ScheduleTarget.New:
                        return "每次运行都新建会话";
                    default:
                        return string.IsNullOrWhiteSpace(_sessionId)
                            ? "首次运行后创建会话并复用"
                            : "复用会话 · " + _sessionId;
                }
            }
        }

        public DateTimeOffset? BaselineUpdatedAt
        {
            get { return _baselineUpdatedAt; }
        }

        public ScheduleDefinition Model
        {
            get { return _model; }
        }

        public bool IsConflict
        {
            get { return _isConflict; }
            private set { if (SetProperty(ref _isConflict, value)) OnPropertyChanged(nameof(CanSave)); }
        }

        /// <summary>与当前模型相比，用户能编辑的字段是否有变化（不把投递回执当成内容变化）。</summary>
        public bool ContentDiffers(ScheduleDefinition definition)
        {
            if (definition == null || _model == null)
            {
                return false;
            }

            return !string.Equals(_model.Title, definition.Title, StringComparison.Ordinal)
                || !string.Equals(_model.Prompt, definition.Prompt, StringComparison.Ordinal)
                || !string.Equals(_model.Rule, definition.Rule, StringComparison.Ordinal)
                || _model.Target != definition.Target
                || _model.SessionId != definition.SessionId
                || _model.WorkspaceId != definition.WorkspaceId
                || _model.Active != definition.Active;
        }

        /// <summary>
        /// 投递回执让 UpdatedAt 前进、但可编辑字段没变：只刷新状态读数与 CAS 基线，
        /// 不覆盖用户正在编辑的表单。
        /// </summary>
        public void RefreshModel(ScheduleDefinition definition)
        {
            if (definition == null)
            {
                return;
            }

            _model = definition;
            _baselineUpdatedAt = definition.UpdatedAt;
            OnPropertyChanged(nameof(Model));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(ToggleActiveText));
            OnPropertyChanged(nameof(NextRunLabel));
            OnPropertyChanged(nameof(LastRunLabel));
            OnPropertyChanged(nameof(HistoryEmptyText));
            RefreshPreview();
        }

        public string ConflictText
        {
            get
            {
                return IsConflict
                    ? "这条任务已在别处被修改。重新载入会放弃本地改动，覆盖保存会以当前表单为准。"
                    : string.Empty;
            }
        }

        public string ErrorMessage
        {
            get { return _errorMessage; }
            private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorMessage); }
        }

        public string RulePreview
        {
            get { return _rulePreview; }
            private set { SetProperty(ref _rulePreview, value); }
        }

        public string NextRunPreview
        {
            get { return _nextRunPreview; }
            private set { SetProperty(ref _nextRunPreview, value); }
        }

        public string StatusLabel
        {
            get
            {
                if (_model == null)
                {
                    return "未保存";
                }

                switch (_model.Status)
                {
                    case ScheduleStatus.Active: return "已开启";
                    case ScheduleStatus.Paused: return "已暂停";
                    default: return "已结束";
                }
            }
        }

        public string ToggleActiveText
        {
            get { return _model != null && _model.Active ? "暂停" : "恢复"; }
        }

        public string NextRunLabel
        {
            get
            {
                return _model == null || !_model.NextRunAt.HasValue
                    ? "无"
                    : SchedulesViewModel.FormatTime(_model.NextRunAt);
            }
        }

        public string LastRunLabel
        {
            get
            {
                return _model == null || !_model.LastRunAt.HasValue
                    ? "还没有运行过"
                    : SchedulesViewModel.FormatTime(_model.LastRunAt) + "（共 "
                        + _model.RunCount.ToString(CultureInfo.InvariantCulture) + " 次）";
            }
        }

        public bool HasHistory
        {
            get { return History.Count > 0; }
        }

        public string HistoryEmptyText
        {
            get { return IsExisting ? "还没有运行记录。" : "保存并运行后才会产生记录。"; }
        }

        public ObservableCollection<ScheduleHistoryRowViewModel> History { get; }

        public bool HasChanges
        {
            get { return !_loading && !string.Equals(SnapshotKey(), _initialKey, StringComparison.Ordinal); }
        }

        public bool CanSave
        {
            get { return HasChanges && !IsConflict; }
        }

        /// <summary>新建或已改动时才显示表单预览；未改动的已存任务只显示真实的下次运行。</summary>
        public bool ShowsPreview
        {
            get { return !IsExisting || HasChanges; }
        }

        /// <summary>从已存任务载入表单；同时把当前值记为基线。</summary>
        public void Load(ScheduleDefinition definition)
        {
            if (definition == null)
            {
                return;
            }

            _loading = true;
            try
            {
                _model = definition;
                Id = definition.Id;
                _title = definition.Title ?? string.Empty;
                _prompt = definition.Prompt ?? string.Empty;
                _target = definition.Target;
                _sessionId = definition.SessionId;
                _currentSessionId = definition.SessionId;
                _workspaceId = definition.WorkspaceId;
                _workspaceDisplay = _workspaceName(definition.WorkspaceId);
                _baselineUpdatedAt = definition.UpdatedAt;
                _isConflict = false;
                _errorMessage = null;

                ScheduleRuleDraft draft = ScheduleRuleDraft.FromRule(definition.Rule, _now());
                _mode = draft.Mode;
                _hourText = draft.Hour.ToString("00", CultureInfo.InvariantCulture);
                _minuteText = draft.Minute.ToString("00", CultureInfo.InvariantCulture);
                _intervalText = draft.Interval.ToString(CultureInfo.InvariantCulture);
                _customRule = draft.CustomRule ?? definition.Rule ?? string.Empty;
                ApplyWeekdays(draft.Weekdays);

                History.Clear();
                if (definition.History != null)
                {
                    for (int i = definition.History.Count - 1; i >= 0; i--)
                    {
                        History.Add(new ScheduleHistoryRowViewModel(definition.History[i]));
                    }
                }
            }
            finally
            {
                _loading = false;
            }

            _initialKey = SnapshotKey();
            RaiseAll();
            RefreshPreview();
        }

        /// <summary>新建草稿：默认每天 09:00、新建会话并复用。</summary>
        public void LoadNew(string workspaceId, string sessionId)
        {
            _loading = true;
            try
            {
                _model = null;
                Id = null;
                _title = string.Empty;
                _prompt = string.Empty;
                _mode = ScheduleRepeatMode.Daily;
                DateTimeOffset now = _now();
                _hourText = now.Hour.ToString("00", CultureInfo.InvariantCulture);
                _minuteText = "00";
                _intervalText = "1";
                _customRule = string.Empty;
                ApplyWeekdays(new List<DayOfWeek> { DayOfWeek.Monday });
                _target = ScheduleTarget.NewOnce;
                _sessionId = null;
                _currentSessionId = sessionId;
                _workspaceId = workspaceId;
                _workspaceDisplay = _workspaceName(workspaceId);
                _baselineUpdatedAt = null;
                _isConflict = false;
                _errorMessage = null;
                History.Clear();
            }
            finally
            {
                _loading = false;
            }

            _initialKey = SnapshotKey();
            RaiseAll();
            RefreshPreview();
        }

        /// <summary>生成规则文本；字段不合法时抛 <see cref="ScheduleException"/>。</summary>
        public string BuildRule()
        {
            int hour;
            if (!int.TryParse(_hourText, NumberStyles.Integer, CultureInfo.InvariantCulture, out hour))
            {
                throw new ScheduleException("invalid_time", "小时必须是 0–23 的整数。");
            }

            int minute;
            if (!int.TryParse(_minuteText, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute))
            {
                throw new ScheduleException("invalid_time", "分钟必须是 0–59 的整数。");
            }

            int interval;
            if (!int.TryParse(_intervalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval))
            {
                interval = 1;
            }

            var draft = new ScheduleRuleDraft
            {
                Mode = _mode,
                Hour = hour,
                Minute = minute,
                Interval = interval,
                Weekdays = CollectWeekdays(),
                CustomRule = _customRule
            };

            return draft.Build(_now());
        }

        /// <summary>冲突时用户选择保留本地修改：清掉 CAS 基线，下一次保存覆盖。</summary>
        public void ClearConflictForOverwrite()
        {
            if (!IsConflict)
            {
                return;
            }

            IsConflict = false;
            _baselineUpdatedAt = null;
            _initialKey = SnapshotKey();
            OnPropertyChanged(nameof(HasChanges));
            OnPropertyChanged(nameof(CanSave));
        }

        public void MarkConflict()
        {
            IsConflict = true;
            OnPropertyChanged(nameof(CanSave));
        }

        /// <summary>校验并展示错误；返回是否通过。</summary>
        public bool Validate()
        {
            if (string.IsNullOrWhiteSpace(_title))
            {
                ErrorMessage = "请填写任务名称。";
                return false;
            }

            if (_title.Trim().Length > 120)
            {
                ErrorMessage = "任务名称不能超过 120 个字。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(_prompt))
            {
                ErrorMessage = "请填写到点后投递的内容。";
                return false;
            }

            try
            {
                BuildRule();
            }
            catch (ScheduleException exception)
            {
                ErrorMessage = exception.Message;
                return false;
            }

            ErrorMessage = null;
            return true;
        }

        public void SetError(string message)
        {
            ErrorMessage = message;
        }

        private void OnEdited()
        {
            if (_loading)
            {
                return;
            }

            RefreshPreview();
            OnPropertyChanged(nameof(HasChanges));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(ShowsPreview));
        }

        private void RefreshPreview()
        {
            try
            {
                string rule = BuildRule();
                ScheduleRecurrence recurrence = ScheduleRecurrence.Parse(rule);
                RulePreview = recurrence.Describe();
                DateTimeOffset? next = recurrence.FirstOnOrAfter(_now());
                NextRunPreview = next.HasValue ? SchedulesViewModel.FormatTime(next) : "没有未来的运行时间";
                if (!IsConflict)
                {
                    ErrorMessage = null;
                }
            }
            catch (ScheduleException exception)
            {
                RulePreview = "规则还需要补充";
                NextRunPreview = "—";
                ErrorMessage = exception.Message;
            }
        }

        private void RaiseAll()
        {
            string[] properties =
            {
                nameof(Id), nameof(IsExisting), nameof(PageTitle), nameof(HeaderTitle), nameof(Title), nameof(Prompt),
                nameof(Mode), nameof(IsOnce), nameof(IsDaily), nameof(IsWeekly), nameof(IsMonthly),
                nameof(IsCustom), nameof(ShowsInterval), nameof(ShowsWeekdays), nameof(ShowsCustomRule),
                nameof(IntervalUnit), nameof(HourText), nameof(MinuteText), nameof(IntervalText),
                nameof(Sunday), nameof(Monday), nameof(Tuesday), nameof(Wednesday), nameof(Thursday),
                nameof(Friday), nameof(Saturday), nameof(CustomRule), nameof(Target),
                nameof(IsTargetNewOnce), nameof(IsTargetThisSession), nameof(IsTargetNew),
                nameof(SessionLabel), nameof(WorkspaceLabel), nameof(IsConflict), nameof(ConflictText),
                nameof(ErrorMessage), nameof(HasError), nameof(RulePreview), nameof(NextRunPreview),
                nameof(StatusLabel), nameof(ToggleActiveText), nameof(NextRunLabel), nameof(LastRunLabel),
                nameof(HasHistory), nameof(HistoryEmptyText), nameof(HasChanges), nameof(CanSave),
                nameof(ShowsPreview)
            };

            for (int i = 0; i < properties.Length; i++)
            {
                OnPropertyChanged(properties[i]);
            }
        }

        private void ApplyWeekdays(IList<DayOfWeek> days)
        {
            _sunday = Contains(days, DayOfWeek.Sunday);
            _monday = Contains(days, DayOfWeek.Monday);
            _tuesday = Contains(days, DayOfWeek.Tuesday);
            _wednesday = Contains(days, DayOfWeek.Wednesday);
            _thursday = Contains(days, DayOfWeek.Thursday);
            _friday = Contains(days, DayOfWeek.Friday);
            _saturday = Contains(days, DayOfWeek.Saturday);

            if (days == null || days.Count == 0)
            {
                _monday = true;
            }
        }

        private List<DayOfWeek> CollectWeekdays()
        {
            var days = new List<DayOfWeek>();
            if (_sunday)
            {
                days.Add(DayOfWeek.Sunday);
            }

            if (_monday)
            {
                days.Add(DayOfWeek.Monday);
            }

            if (_tuesday)
            {
                days.Add(DayOfWeek.Tuesday);
            }

            if (_wednesday)
            {
                days.Add(DayOfWeek.Wednesday);
            }

            if (_thursday)
            {
                days.Add(DayOfWeek.Thursday);
            }

            if (_friday)
            {
                days.Add(DayOfWeek.Friday);
            }

            if (_saturday)
            {
                days.Add(DayOfWeek.Saturday);
            }

            return days;
        }

        private string SnapshotKey()
        {
            var parts = new List<string>
            {
                _title ?? string.Empty,
                _prompt ?? string.Empty,
                ((int)_mode).ToString(CultureInfo.InvariantCulture),
                (_hourText ?? string.Empty).Trim(),
                (_minuteText ?? string.Empty).Trim(),
                _mode == ScheduleRepeatMode.Custom
                    ? string.Empty
                    : (_intervalText ?? string.Empty).Trim(),
                _mode == ScheduleRepeatMode.Custom ? string.Empty : WeekdayBits(),
                _mode == ScheduleRepeatMode.Custom ? (_customRule ?? string.Empty).Trim() : string.Empty,
                ((int)_target).ToString(CultureInfo.InvariantCulture)
            };

            return string.Join("\u001f", parts.ToArray());
        }

        private string WeekdayBits()
        {
            var chars = new char[7];
            chars[0] = _sunday ? '1' : '0';
            chars[1] = _monday ? '1' : '0';
            chars[2] = _tuesday ? '1' : '0';
            chars[3] = _wednesday ? '1' : '0';
            chars[4] = _thursday ? '1' : '0';
            chars[5] = _friday ? '1' : '0';
            chars[6] = _saturday ? '1' : '0';
            return new string(chars);
        }

        private static bool Contains(IList<DayOfWeek> days, DayOfWeek day)
        {
            if (days == null)
            {
                return false;
            }

            for (int i = 0; i < days.Count; i++)
            {
                if (days[i] == day)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
