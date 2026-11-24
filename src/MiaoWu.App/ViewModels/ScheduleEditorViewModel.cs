using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Agents.Schedules;
using MiaoWu.Core.Models;
using MiaoWu.Core.Workspaces;

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

    /// <summary>「重复」下拉的一项。</summary>
    public sealed class ScheduleRepeatOption
    {
        public ScheduleRepeatOption(ScheduleRepeatMode mode, string label)
        {
            Mode = mode;
            Label = label;
        }

        public ScheduleRepeatMode Mode { get; }

        public string Label { get; }
    }

    /// <summary>「执行位置」下拉的一项。</summary>
    public sealed class ScheduleTargetOption
    {
        public ScheduleTargetOption(ScheduleTarget target, string label)
        {
            Target = target;
            Label = label;
        }

        public ScheduleTarget Target { get; }

        public string Label { get; }
    }

    /// <summary>「关联工作区」下拉的一项。</summary>
    public sealed class ScheduleWorkspaceOption
    {
        public ScheduleWorkspaceOption(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id { get; }

        public string Label { get; }
    }

    /// <summary>「执行模型」下拉的一项；ProviderId / ModelId 为空表示默认模型。</summary>
    public sealed class ScheduleModelOption
    {
        public ScheduleModelOption(
            string providerId,
            string modelId,
            string label,
            bool isDefault,
            bool supportsReasoning)
        {
            ProviderId = providerId;
            ModelId = modelId;
            Label = label;
            IsDefault = isDefault;
            SupportsReasoning = supportsReasoning;
        }

        public string ProviderId { get; }

        public string ModelId { get; }

        public string Label { get; }

        public bool IsDefault { get; }

        public bool SupportsReasoning { get; }
    }

    /// <summary>「推理等级」下拉的一项；Value 为空表示默认档。</summary>
    public sealed class ScheduleReasoningOption
    {
        public ScheduleReasoningOption(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; }

        public string Label { get; }
    }

    /// <summary>
    /// 单条定时任务的编辑草稿。字段都是表单值（时间用字符串，允许输入中间态），
    /// 保存时再由 <see cref="ScheduleRuleDraft"/> 生成规则；<see cref="HasChanges"/> 只比较少数字段。
    /// </summary>
    public sealed class ScheduleEditorViewModel : ObservableObject
    {
        private static readonly ScheduleRepeatOption[] RepeatOptionList =
        {
            new ScheduleRepeatOption(ScheduleRepeatMode.Once, "仅一次"),
            new ScheduleRepeatOption(ScheduleRepeatMode.Daily, "每天"),
            new ScheduleRepeatOption(ScheduleRepeatMode.Weekly, "每周"),
            new ScheduleRepeatOption(ScheduleRepeatMode.Monthly, "每月"),
            new ScheduleRepeatOption(ScheduleRepeatMode.Custom, "自定义")
        };

        private static readonly ScheduleTargetOption[] TargetOptionList =
        {
            new ScheduleTargetOption(ScheduleTarget.NewOnce, "新建会话并复用"),
            new ScheduleTargetOption(ScheduleTarget.ThisSession, "当前会话"),
            new ScheduleTargetOption(ScheduleTarget.New, "每次新建会话")
        };

        private static readonly ScheduleReasoningOption[] ReasoningOptionList =
        {
            new ScheduleReasoningOption(null, "默认"),
            new ScheduleReasoningOption("low", "低"),
            new ScheduleReasoningOption("medium", "中"),
            new ScheduleReasoningOption("high", "高")
        };

        private readonly Func<DateTimeOffset> _now;
        private readonly Func<string, string> _workspaceName;
        private readonly Func<IReadOnlyList<Workspace>> _workspaces;
        private readonly Func<IReadOnlyList<ModelDescriptor>> _models;
        private readonly Func<ModelDescriptor> _defaultModel;

        private string _id;
        private string _title = string.Empty;
        private string _prompt = string.Empty;
        private ScheduleRepeatMode _mode = ScheduleRepeatMode.Daily;
        private string _timeText = "09:00:00";
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
        private List<ScheduleWorkspaceOption> _workspaceOptions = new List<ScheduleWorkspaceOption>();
        private ScheduleWorkspaceOption _selectedWorkspaceOption;
        private string _providerId;
        private string _modelId;
        private string _reasoningEffort;
        private List<ScheduleModelOption> _modelOptions = new List<ScheduleModelOption>();
        private ScheduleModelOption _selectedModelOption;
        private ScheduleReasoningOption _selectedReasoningOption;
        private ScheduleDefinition _model;
        private DateTimeOffset? _baselineUpdatedAt;
        private bool _isConflict;
        private string _errorMessage;
        private string _initialKey = string.Empty;
        private bool _loading;

        public ScheduleEditorViewModel(
            Func<DateTimeOffset> now,
            Func<string, string> workspaceName,
            Func<IReadOnlyList<Workspace>> workspaces = null,
            Func<IReadOnlyList<ModelDescriptor>> models = null,
            Func<ModelDescriptor> defaultModel = null)
        {
            _now = now ?? (() => DateTimeOffset.Now);
            _workspaceName = workspaceName ?? (id => id);
            _workspaces = workspaces;
            _models = models;
            _defaultModel = defaultModel;
            History = new ObservableCollection<ScheduleHistoryRowViewModel>();
        }

        /// <summary>任务 id；新建草稿为 null。</summary>
        public string Id
        {
            get { return _id; }
            private set
            {
                if (!SetProperty(ref _id, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsExisting));
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(HeaderTitle));
                OnPropertyChanged(nameof(SaveButtonText));
                OnPropertyChanged(nameof(CanSave));
            }
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
                    return "手动创建";
                }

                return string.IsNullOrWhiteSpace(_title) ? "未命名任务" : _title.Trim();
            }
        }

        public IReadOnlyList<ScheduleRepeatOption> RepeatOptions
        {
            get { return RepeatOptionList; }
        }

        public ScheduleRepeatOption SelectedRepeatOption
        {
            get
            {
                for (int i = 0; i < RepeatOptionList.Length; i++)
                {
                    if (RepeatOptionList[i].Mode == _mode)
                    {
                        return RepeatOptionList[i];
                    }
                }

                return RepeatOptionList[1];
            }
            set
            {
                if (value != null && value.Mode != _mode)
                {
                    Mode = value.Mode;
                }
            }
        }

        public IReadOnlyList<ScheduleTargetOption> TargetOptions
        {
            get { return TargetOptionList; }
        }

        public ScheduleTargetOption SelectedTargetOption
        {
            get
            {
                for (int i = 0; i < TargetOptionList.Length; i++)
                {
                    if (TargetOptionList[i].Target == _target)
                    {
                        return TargetOptionList[i];
                    }
                }

                return TargetOptionList[0];
            }
            set
            {
                if (value != null && value.Target != _target)
                {
                    Target = value.Target;
                }
            }
        }

        public IReadOnlyList<ScheduleModelOption> ModelOptions
        {
            get { return _modelOptions; }
        }

        public ScheduleModelOption SelectedModelOption
        {
            get { return _selectedModelOption; }
            set
            {
                if (ReferenceEquals(_selectedModelOption, value) || value == null)
                {
                    return;
                }

                _selectedModelOption = value;
                _providerId = value.IsDefault ? null : value.ProviderId;
                _modelId = value.IsDefault ? null : value.ModelId;
                if (!value.SupportsReasoning)
                {
                    _reasoningEffort = null;
                    _selectedReasoningOption = ReasoningOptionList[0];
                    OnPropertyChanged(nameof(SelectedReasoningOption));
                }

                OnPropertyChanged(nameof(SelectedModelOption));
                OnPropertyChanged(nameof(ProviderId));
                OnPropertyChanged(nameof(ModelId));
                OnPropertyChanged(nameof(IsReasoningEnabled));
                OnEdited();
            }
        }

        public string ProviderId
        {
            get { return _providerId; }
        }

        public string ModelId
        {
            get { return _modelId; }
        }

        public IReadOnlyList<ScheduleReasoningOption> ReasoningOptions
        {
            get { return ReasoningOptionList; }
        }

        public ScheduleReasoningOption SelectedReasoningOption
        {
            get
            {
                for (int i = 0; i < ReasoningOptionList.Length; i++)
                {
                    if (string.Equals(ReasoningOptionList[i].Value, _reasoningEffort, StringComparison.OrdinalIgnoreCase))
                    {
                        return ReasoningOptionList[i];
                    }
                }

                return ReasoningOptionList[0];
            }
            set
            {
                if (value == null)
                {
                    return;
                }

                _reasoningEffort = string.IsNullOrWhiteSpace(value.Value) ? null : value.Value;
                OnPropertyChanged(nameof(SelectedReasoningOption));
                OnPropertyChanged(nameof(ReasoningEffort));
                OnEdited();
            }
        }

        public string ReasoningEffort
        {
            get { return _reasoningEffort; }
        }

        public bool IsReasoningEnabled
        {
            get
            {
                if (_selectedModelOption == null)
                {
                    return true;
                }

                return _selectedModelOption.SupportsReasoning;
            }
        }

        /// <summary>底栏主按钮：新建是「创建」，编辑是「保存」。</summary>
        public string SaveButtonText
        {
            get { return IsExisting ? "保存" : "创建"; }
        }

        /// <summary>时区跟随系统；MiaoWu 的规则按本地时间计算，这里只读展示。</summary>
        public string TimeZoneLabel
        {
            get
            {
                TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(_now());
                string sign = offset < TimeSpan.Zero ? "-" : "+";
                offset = offset.Duration();
                int hours = (int)offset.TotalHours;
                int minutes = offset.Minutes;
                string name = TimeZoneInfo.Local.StandardName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "本地时间";
                }

                return "UTC" + sign
                    + hours.ToString("00", CultureInfo.InvariantCulture)
                    + ":" + minutes.ToString("00", CultureInfo.InvariantCulture)
                    + " · " + name + "（系统）";
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
                OnPropertyChanged(nameof(SelectedRepeatOption));
                OnPropertyChanged(nameof(IntervalUnit));
                OnPropertyChanged(nameof(ShowsInterval));
                OnPropertyChanged(nameof(ShowsWeekdays));
                OnPropertyChanged(nameof(ShowsTime));
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

        /// <summary>自定义规则自己带 DTSTART，表单时间与星期都不再参与生成。</summary>
        public bool ShowsTime
        {
            get { return _mode != ScheduleRepeatMode.Custom; }
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

        /// <summary>时间输入，接受 HH:mm 或 HH:mm:ss；秒只用于显示，规则落到分钟。</summary>
        public string TimeText
        {
            get { return _timeText; }
            set { if (SetProperty(ref _timeText, value)) OnEdited(); }
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
                OnPropertyChanged(nameof(SelectedTargetOption));
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

        public IReadOnlyList<ScheduleWorkspaceOption> WorkspaceOptions
        {
            get { return _workspaceOptions; }
        }

        public bool HasWorkspaceOptions
        {
            get { return _workspaceOptions.Count > 0; }
        }

        public bool HasSelectedWorkspace
        {
            get { return _selectedWorkspaceOption != null; }
        }

        public ScheduleWorkspaceOption SelectedWorkspaceOption
        {
            get { return _selectedWorkspaceOption; }
            set
            {
                if (ReferenceEquals(_selectedWorkspaceOption, value))
                {
                    return;
                }

                _selectedWorkspaceOption = value;
                _workspaceId = value == null ? null : value.Id;
                _workspaceDisplay = value == null ? null : value.Label;
                OnPropertyChanged(nameof(SelectedWorkspaceOption));
                OnPropertyChanged(nameof(HasSelectedWorkspace));
                OnPropertyChanged(nameof(WorkspaceId));
                OnPropertyChanged(nameof(WorkspaceLabel));
                OnEdited();
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
            private set
            {
                if (!SetProperty(ref _isConflict, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CanSave));
            }
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
                || !string.Equals(_model.ProviderId, definition.ProviderId, StringComparison.Ordinal)
                || !string.Equals(_model.ModelId, definition.ModelId, StringComparison.Ordinal)
                || !string.Equals(_model.ReasoningEffort, definition.ReasoningEffort, StringComparison.OrdinalIgnoreCase)
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
            _providerId = definition.ProviderId;
            _modelId = definition.ModelId;
            _reasoningEffort = NormalizeReasoning(definition.ReasoningEffort);
            RebuildModelOptions();
            OnPropertyChanged(nameof(Model));
            OnPropertyChanged(nameof(ToggleActiveText));
            OnPropertyChanged(nameof(NextRunLabel));
            OnPropertyChanged(nameof(LastRunLabel));
            OnPropertyChanged(nameof(HistoryEmptyText));
            RefreshValidation();
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

        public string ToggleActiveText
        {
            get { return _model != null && _model.Active ? "暂停使用" : "恢复使用"; }
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
            get
            {
                if (_loading)
                {
                    return false;
                }

                // 新建草稿没有已保存版本，天然就是未保存状态；底栏因此显示「有未保存的修改」。
                return !IsExisting || !string.Equals(SnapshotKey(), _initialKey, StringComparison.Ordinal);
            }
        }

        /// <summary>底栏按钮只在有改动、必填项齐备且规则可生成时可用。</summary>
        public bool CanSave
        {
            get
            {
                if (_loading || IsConflict || !HasChanges)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(_title) || string.IsNullOrWhiteSpace(_prompt))
                {
                    return false;
                }

                try
                {
                    BuildRule();
                    return true;
                }
                catch (ScheduleException)
                {
                    return false;
                }
            }
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
                _providerId = definition.ProviderId;
                _modelId = definition.ModelId;
                _reasoningEffort = NormalizeReasoning(definition.ReasoningEffort);
                RebuildWorkspaceOptions();
                RebuildModelOptions();
                _baselineUpdatedAt = definition.UpdatedAt;
                _isConflict = false;
                _errorMessage = null;

                ScheduleRuleDraft draft = ScheduleRuleDraft.FromRule(definition.Rule, _now());
                _mode = draft.Mode;
                _timeText = FormatTime(draft.Hour, draft.Minute);
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
            RefreshValidation();
        }

        /// <summary>新建草稿：默认每周一当前整点、新建会话并复用。</summary>
        public void LoadNew(string workspaceId, string sessionId)
        {
            _loading = true;
            try
            {
                _model = null;
                Id = null;
                _title = string.Empty;
                _prompt = string.Empty;
                _mode = ScheduleRepeatMode.Weekly;
                DateTimeOffset now = _now();
                _timeText = FormatTime(now.Hour, 0);
                _intervalText = "1";
                _customRule = string.Empty;
                ApplyWeekdays(new List<DayOfWeek> { DayOfWeek.Monday });
                _target = ScheduleTarget.NewOnce;
                _sessionId = null;
                _currentSessionId = sessionId;
                _workspaceId = workspaceId;
                _workspaceDisplay = _workspaceName(workspaceId);
                _providerId = null;
                _modelId = null;
                _reasoningEffort = null;
                RebuildWorkspaceOptions();
                RebuildModelOptions();
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
            RefreshValidation();
        }

        /// <summary>生成规则文本；字段不合法时抛 <see cref="ScheduleException"/>。</summary>
        public string BuildRule()
        {
            int hour;
            int minute;
            if (!TryParseTime(_timeText, out hour, out minute))
            {
                throw new ScheduleException("invalid_time", "时间要写成 时:分，例如 09:30 或 22:15:00。");
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

            RefreshValidation();
            OnPropertyChanged(nameof(HasChanges));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(SaveButtonText));
        }

        private void RefreshValidation()
        {
            try
            {
                BuildRule();
                if (!IsConflict)
                {
                    ErrorMessage = null;
                }
            }
            catch (ScheduleException exception)
            {
                ErrorMessage = exception.Message;
            }
        }

        private void RaiseAll()
        {
            string[] properties =
            {
                nameof(Id), nameof(IsExisting), nameof(PageTitle), nameof(HeaderTitle), nameof(Title), nameof(Prompt),
                nameof(Mode), nameof(IsOnce), nameof(IsDaily), nameof(IsWeekly), nameof(IsMonthly),
                nameof(IsCustom), nameof(RepeatOptions), nameof(SelectedRepeatOption),
                nameof(TargetOptions), nameof(SelectedTargetOption),
                nameof(ShowsInterval), nameof(ShowsWeekdays), nameof(ShowsTime), nameof(ShowsCustomRule),
                nameof(IntervalUnit), nameof(TimeText), nameof(IntervalText),
                nameof(Sunday), nameof(Monday), nameof(Tuesday), nameof(Wednesday), nameof(Thursday),
                nameof(Friday), nameof(Saturday), nameof(CustomRule), nameof(Target),
                nameof(IsTargetNewOnce), nameof(IsTargetThisSession), nameof(IsTargetNew),
                nameof(SessionLabel), nameof(WorkspaceLabel), nameof(WorkspaceOptions),
                nameof(HasWorkspaceOptions), nameof(HasSelectedWorkspace), nameof(SelectedWorkspaceOption),
                nameof(ModelOptions), nameof(SelectedModelOption), nameof(ProviderId), nameof(ModelId),
                nameof(ReasoningOptions), nameof(SelectedReasoningOption), nameof(ReasoningEffort),
                nameof(IsReasoningEnabled), nameof(TimeZoneLabel),
                nameof(SaveButtonText), nameof(IsConflict), nameof(ConflictText),
                nameof(ErrorMessage), nameof(HasError),
                nameof(ToggleActiveText), nameof(NextRunLabel), nameof(LastRunLabel),
                nameof(HasHistory), nameof(HistoryEmptyText), nameof(HasChanges), nameof(CanSave)
            };

            for (int i = 0; i < properties.Length; i++)
            {
                OnPropertyChanged(properties[i]);
            }
        }

        private static string NormalizeReasoning(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "low" || normalized == "medium" || normalized == "high"
                ? normalized
                : null;
        }

        private static string FormatTime(int hour, int minute)
        {
            return hour.ToString("00", CultureInfo.InvariantCulture)
                + ":" + minute.ToString("00", CultureInfo.InvariantCulture)
                + ":00";
        }

        private static bool TryParseTime(string text, out int hour, out int minute)
        {
            hour = 0;
            minute = 0;
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return false;
            }

            string[] parts = value.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
            {
                return false;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute))
            {
                return false;
            }

            if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
            {
                return false;
            }

            if (parts.Length == 3)
            {
                int second;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out second)
                    || second < 0 || second > 59)
                {
                    return false;
                }
            }

            return true;
        }

        private string CanonicalTime()
        {
            int hour;
            int minute;
            return TryParseTime(_timeText, out hour, out minute)
                ? hour.ToString("00", CultureInfo.InvariantCulture)
                    + ":" + minute.ToString("00", CultureInfo.InvariantCulture)
                : (_timeText ?? string.Empty).Trim();
        }

        private void RebuildWorkspaceOptions()
        {
            var options = new List<ScheduleWorkspaceOption>();
            IReadOnlyList<Workspace> source = _workspaces == null ? null : _workspaces();
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    Workspace workspace = source[i];
                    if (workspace == null || string.IsNullOrWhiteSpace(workspace.Id))
                    {
                        continue;
                    }

                    string label = string.IsNullOrWhiteSpace(workspace.Name) ? workspace.Id : workspace.Name;
                    options.Add(new ScheduleWorkspaceOption(workspace.Id, label));
                }
            }

            if (!string.IsNullOrWhiteSpace(_workspaceId))
            {
                bool present = false;
                for (int i = 0; i < options.Count; i++)
                {
                    if (string.Equals(options[i].Id, _workspaceId, StringComparison.Ordinal))
                    {
                        present = true;
                        break;
                    }
                }

                if (!present)
                {
                    options.Insert(0, new ScheduleWorkspaceOption(
                        _workspaceId,
                        string.IsNullOrWhiteSpace(_workspaceDisplay) ? _workspaceId : _workspaceDisplay));
                }
            }

            _workspaceOptions = options;
            _selectedWorkspaceOption = null;
            for (int i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].Id, _workspaceId, StringComparison.Ordinal))
                {
                    _selectedWorkspaceOption = options[i];
                    break;
                }
            }

            OnPropertyChanged(nameof(WorkspaceOptions));
            OnPropertyChanged(nameof(HasWorkspaceOptions));
            OnPropertyChanged(nameof(SelectedWorkspaceOption));
            OnPropertyChanged(nameof(HasSelectedWorkspace));
        }

        private void RebuildModelOptions()
        {
            var options = new List<ScheduleModelOption>();
            ModelDescriptor defaultDescriptor = _defaultModel == null ? null : _defaultModel();
            string defaultLabel = defaultDescriptor == null
                ? "默认模型"
                : "默认模型（" + defaultDescriptor.GetDisplayName() + "）";
            options.Add(new ScheduleModelOption(
                null,
                null,
                defaultLabel,
                true,
                defaultDescriptor == null || defaultDescriptor.SupportsReasoning));

            IReadOnlyList<ModelDescriptor> source = _models == null ? null : _models();
            var ordered = new List<ModelDescriptor>();
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    if (source[i] != null && !string.IsNullOrWhiteSpace(source[i].ModelId))
                    {
                        ordered.Add(source[i]);
                    }
                }
            }

            ordered.Sort((a, b) =>
            {
                int provider = string.Compare(
                    a.ProviderId,
                    b.ProviderId,
                    StringComparison.OrdinalIgnoreCase);
                return provider != 0
                    ? provider
                    : string.Compare(a.GetDisplayName(), b.GetDisplayName(), StringComparison.OrdinalIgnoreCase);
            });

            for (int i = 0; i < ordered.Count; i++)
            {
                ModelDescriptor model = ordered[i];
                options.Add(new ScheduleModelOption(
                    model.ProviderId,
                    model.ModelId,
                    model.GetDisplayName(),
                    false,
                    model.SupportsReasoning));
            }

            if (!string.IsNullOrWhiteSpace(_modelId))
            {
                bool present = false;
                for (int i = 0; i < options.Count; i++)
                {
                    if (string.Equals(options[i].ModelId, _modelId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(options[i].ProviderId, _providerId, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        break;
                    }
                }

                if (!present)
                {
                    options.Insert(1, new ScheduleModelOption(
                        _providerId,
                        _modelId,
                        "已移除的模型（" + _modelId + "）",
                        false,
                        false));
                }
            }

            _modelOptions = options;
            _selectedModelOption = options[0];
            for (int i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].ProviderId, _providerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(options[i].ModelId, _modelId, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedModelOption = options[i];
                    break;
                }
            }

            OnPropertyChanged(nameof(ModelOptions));
            OnPropertyChanged(nameof(SelectedModelOption));
            OnPropertyChanged(nameof(ProviderId));
            OnPropertyChanged(nameof(ModelId));
            OnPropertyChanged(nameof(IsReasoningEnabled));
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
                CanonicalTime(),
                _mode == ScheduleRepeatMode.Custom
                    ? string.Empty
                    : (_intervalText ?? string.Empty).Trim(),
                _mode == ScheduleRepeatMode.Custom ? string.Empty : WeekdayBits(),
                _mode == ScheduleRepeatMode.Custom ? (_customRule ?? string.Empty).Trim() : string.Empty,
                ((int)_target).ToString(CultureInfo.InvariantCulture),
                _providerId ?? string.Empty,
                _modelId ?? string.Empty,
                _reasoningEffort ?? string.Empty
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
