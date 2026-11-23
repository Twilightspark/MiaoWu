using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using MiaoWu.App.Mvvm;
using MiaoWu.Core.Agents.Schedules;

namespace MiaoWu.App.ViewModels
{
    /// <summary>列表筛选：默认只看已开启。</summary>
    public enum ScheduleFilter
    {
        Active = 0,
        Paused = 1,
        Ended = 2,
        All = 3
    }

    /// <summary>筛选 chip 的绑定项。</summary>
    public sealed class ScheduleFilterOption
    {
        public ScheduleFilterOption(ScheduleFilter filter, string label)
        {
            Filter = filter;
            Label = label;
        }

        public ScheduleFilter Filter { get; }

        public string Label { get; }
    }

    /// <summary>详情页签：规则 / 任务运行记录。</summary>
    public sealed class ScheduleTabOption
    {
        public ScheduleTabOption(bool isHistory, string label)
        {
            IsHistory = isHistory;
            Label = label;
        }

        public bool IsHistory { get; }

        public string Label { get; }
    }

    /// <summary>列表里的一条定时任务；刷新时原地更新，避免选中项与草稿被换掉。</summary>
    public sealed class ScheduleTaskRowViewModel : ObservableObject
    {
        private readonly Func<string, string> _workspaceName;
        private ScheduleDefinition _model;

        public ScheduleTaskRowViewModel(ScheduleDefinition model, Func<string, string> workspaceName)
        {
            _workspaceName = workspaceName ?? (id => id);
            Update(model);
        }

        public ScheduleDefinition Model
        {
            get { return _model; }
        }

        public string Id
        {
            get { return _model == null ? string.Empty : _model.Id; }
        }

        public string Title
        {
            get
            {
                string title = _model == null ? null : _model.Title;
                return string.IsNullOrWhiteSpace(title) ? "（未命名任务）" : title;
            }
        }

        public string StatusLabel
        {
            get
            {
                if (_model == null)
                {
                    return string.Empty;
                }

                switch (_model.Status)
                {
                    case ScheduleStatus.Active: return "已开启";
                    case ScheduleStatus.Paused: return "已暂停";
                    default: return "已结束";
                }
            }
        }

        public bool IsEnded
        {
            get { return _model != null && _model.Status == ScheduleStatus.Ended; }
        }

        public string RuleText
        {
            get { return DescribeRule(_model == null ? null : _model.Rule); }
        }

        public string Subtitle
        {
            get
            {
                if (_model == null)
                {
                    return string.Empty;
                }

                string status = _model.Status == ScheduleStatus.Active ? string.Empty : StatusLabel + " · ";
                string next = _model.Active && _model.NextRunAt.HasValue
                    ? " · 下次 " + FormatShortTime(_model.NextRunAt)
                    : string.Empty;
                return status + RuleText + next;
            }
        }

        public string NextRunLabel
        {
            get { return _model == null ? "无" : SchedulesViewModel.FormatTime(_model.NextRunAt); }
        }

        public string LastRunLabel
        {
            get
            {
                if (_model == null || !_model.LastRunAt.HasValue)
                {
                    return "还没有运行过";
                }

                return SchedulesViewModel.FormatTime(_model.LastRunAt)
                    + "（共 " + _model.RunCount.ToString(CultureInfo.InvariantCulture) + " 次）";
            }
        }

        public string TargetLabel
        {
            get
            {
                if (_model == null)
                {
                    return string.Empty;
                }

                switch (_model.Target)
                {
                    case ScheduleTarget.ThisSession:
                        return string.IsNullOrWhiteSpace(_model.SessionId)
                            ? "当前会话"
                            : "当前会话 · " + _model.SessionId;
                    case ScheduleTarget.New:
                        return "每次新建会话";
                    default:
                        return string.IsNullOrWhiteSpace(_model.SessionId)
                            ? "新建会话并复用"
                            : "复用会话 · " + _model.SessionId;
                }
            }
        }

        public string WorkspaceLabel
        {
            get
            {
                if (_model == null || string.IsNullOrWhiteSpace(_model.WorkspaceId))
                {
                    return "未关联工作区";
                }

                string name = _workspaceName(_model.WorkspaceId);
                return string.IsNullOrWhiteSpace(name) ? _model.WorkspaceId : name;
            }
        }

        public void Update(ScheduleDefinition model)
        {
            if (model == null)
            {
                return;
            }

            _model = model;
            string[] properties =
            {
                nameof(Model), nameof(Id), nameof(Title), nameof(StatusLabel), nameof(IsEnded),
                nameof(RuleText), nameof(Subtitle), nameof(NextRunLabel), nameof(LastRunLabel),
                nameof(TargetLabel), nameof(WorkspaceLabel)
            };

            for (int i = 0; i < properties.Length; i++)
            {
                OnPropertyChanged(properties[i]);
            }
        }

        internal static string DescribeRule(string rule)
        {
            try
            {
                return ScheduleRecurrence.Parse(rule).Describe();
            }
            catch (ScheduleException)
            {
                string text = (rule ?? string.Empty).Replace("\r", string.Empty).Replace('\n', ' ').Trim();
                return text.Length > 60 ? text.Substring(0, 60) + "…" : text;
            }
        }

        internal static string FormatShortTime(DateTimeOffset? value)
        {
            return value.HasValue
                ? value.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "无";
        }
    }

    /// <summary>
    /// 定时任务页面：左列任务目录（筛选 / 搜索），右列规则编辑与运行记录。
    /// 所有写入都走 <see cref="IScheduleService"/>；服务变更后由宿主派发回 UI 线程刷新。
    /// </summary>
    public sealed class SchedulesViewModel : ObservableObject, IDisposable
    {
        private const string NewDraftKey = "__new";

        private static readonly ScheduleFilterOption[] FilterOptions =
        {
            new ScheduleFilterOption(ScheduleFilter.Active, "已开启"),
            new ScheduleFilterOption(ScheduleFilter.Paused, "已暂停"),
            new ScheduleFilterOption(ScheduleFilter.Ended, "已结束"),
            new ScheduleFilterOption(ScheduleFilter.All, "全部")
        };

        private static readonly ScheduleTabOption[] TabOptions =
        {
            new ScheduleTabOption(false, "规则"),
            new ScheduleTabOption(true, "运行记录")
        };

        private readonly IScheduleService _schedules;
        private readonly Func<string> _sessionId;
        private readonly Func<string> _workspaceId;
        private readonly Func<string, string> _workspaceName;
        private readonly Action<Action> _dispatch;
        private readonly Func<string, string, string, string, bool, bool> _confirm;
        private readonly Func<DateTimeOffset> _now;
        private readonly Dictionary<string, ScheduleTaskRowViewModel> _rows =
            new Dictionary<string, ScheduleTaskRowViewModel>(StringComparer.Ordinal);
        private readonly Dictionary<string, ScheduleEditorViewModel> _drafts =
            new Dictionary<string, ScheduleEditorViewModel>(StringComparer.Ordinal);
        private readonly List<ScheduleTaskRowViewModel> _all = new List<ScheduleTaskRowViewModel>();

        private ScheduleTaskRowViewModel _selected;
        private ScheduleFilterOption _selectedFilter = FilterOptions[0];
        private ScheduleTabOption _selectedTab = TabOptions[0];
        private string _searchText = string.Empty;
        private string _statusMessage = string.Empty;
        private ScheduleEditorViewModel _editor;
        private bool _isNewDraft;
        private bool _applyingFilter;
        private bool _isOpen;
        private bool _disposed;

        public SchedulesViewModel(
            IScheduleService schedules,
            Func<string> sessionId,
            Func<string> workspaceId,
            Func<string, string> workspaceName,
            Action<Action> dispatch = null,
            Func<string, string, string, string, bool, bool> confirm = null,
            Func<DateTimeOffset> now = null)
        {
            _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
            _sessionId = sessionId ?? (() => null);
            _workspaceId = workspaceId ?? (() => null);
            _workspaceName = workspaceName ?? (id => id);
            _dispatch = dispatch ?? (action => action());
            _confirm = confirm ?? ((title, message, confirmText, cancelText, destructive) => false);
            _now = now ?? (() => DateTimeOffset.Now);

            Items = new ObservableCollection<ScheduleTaskRowViewModel>();

            NewCommand = new RelayCommand(StartNew);
            SaveCommand = new RelayCommand(Save, CanSave);
            ForceSaveCommand = new RelayCommand(() => SaveCore(true), () => _editor != null && _editor.IsConflict);
            DiscardCommand = new RelayCommand(Discard, () => _editor != null && (_editor.HasChanges || _isNewDraft));
            DeleteCommand = new RelayCommand(Delete, () => _editor != null && _editor.IsExisting);
            ToggleActiveCommand = new RelayCommand(ToggleActive, CanToggleActive);
            RunNowCommand = new RelayCommand(RunNow, CanRunNow);
            RefreshCommand = new RelayCommand(() => Reload(_editor == null ? null : _editor.Id));
            CloseCommand = new RelayCommand(Close);
            ReloadEditorCommand = new RelayCommand(ReloadEditor, () => _editor != null && _editor.IsConflict);

            _schedules.Changed += OnSchedulesChanged;
        }

        /// <summary>页面打开状态；由主壳切换中/右栏显示。</summary>
        public bool IsOpen
        {
            get { return _isOpen; }
            private set { SetProperty(ref _isOpen, value); }
        }

        public ObservableCollection<ScheduleTaskRowViewModel> Items { get; }

        public IReadOnlyList<ScheduleFilterOption> Filters
        {
            get { return FilterOptions; }
        }

        public IReadOnlyList<ScheduleTabOption> Tabs
        {
            get { return TabOptions; }
        }

        public ScheduleFilterOption SelectedFilter
        {
            get { return _selectedFilter; }
            set
            {
                if (value == null || ReferenceEquals(_selectedFilter, value))
                {
                    return;
                }

                _selectedFilter = value;
                OnPropertyChanged(nameof(SelectedFilter));
                ApplyFilter(null);
            }
        }

        public ScheduleTabOption SelectedTab
        {
            get { return _selectedTab; }
            set
            {
                if (value == null || ReferenceEquals(_selectedTab, value))
                {
                    return;
                }

                _selectedTab = value;
                OnPropertyChanged(nameof(SelectedTab));
                OnPropertyChanged(nameof(IsHistoryTab));
            }
        }

        public bool IsHistoryTab
        {
            get { return _selectedTab != null && _selectedTab.IsHistory; }
        }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                {
                    ApplyFilter(null);
                }
            }
        }

        public string StatusMessage
        {
            get { return _statusMessage; }
            private set { SetProperty(ref _statusMessage, value); }
        }

        public ScheduleTaskRowViewModel Selected
        {
            get { return _selected; }
            set
            {
                if (ReferenceEquals(_selected, value))
                {
                    return;
                }

                // 重建列表时 WPF 会先写一次 null（清空 Items 引起）；不能因此丢掉草稿或收起页面。
                if (_applyingFilter)
                {
                    _selected = value;
                    OnPropertyChanged(nameof(Selected));
                    return;
                }

                StoreDraft();
                _selected = value;
                _isNewDraft = false;
                OnPropertyChanged(nameof(Selected));
                OnPropertyChanged(nameof(HasSelection));
                ActivateEditor(value == null ? null : DraftFor(value));
            }
        }

        public bool HasSelection
        {
            get { return _selected != null || _editor != null; }
        }

        public bool HasNoItems
        {
            get { return Items.Count == 0; }
        }

        public string EmptyText
        {
            get
            {
                if (_all.Count == 0)
                {
                    return "还没有定时任务。新建一条，到点后会自动把内容投递到会话。";
                }

                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    return "没有匹配「" + _searchText.Trim() + "」的任务。";
                }

                switch (_selectedFilter.Filter)
                {
                    case ScheduleFilter.Paused: return "没有已暂停的任务。";
                    case ScheduleFilter.Ended: return "没有已结束的任务。";
                    case ScheduleFilter.All: return "还没有定时任务。";
                    default: return "没有已开启的任务。暂停或已结束的任务可以在对应筛选里找到。";
                }
            }
        }

        public ScheduleEditorViewModel Editor
        {
            get { return _editor; }
            private set
            {
                if (ReferenceEquals(_editor, value))
                {
                    return;
                }

                _editor = value;
                OnPropertyChanged(nameof(Editor));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(IsEditingExisting));
                if (value != null)
                {
                    value.PropertyChanged += OnEditorPropertyChanged;
                }
            }
        }

        public bool IsEditingExisting
        {
            get { return _editor != null && _editor.IsExisting; }
        }

        public RelayCommand NewCommand { get; }

        public RelayCommand SaveCommand { get; }

        public RelayCommand ForceSaveCommand { get; }

        public RelayCommand DiscardCommand { get; }

        public RelayCommand DeleteCommand { get; }

        public RelayCommand ToggleActiveCommand { get; }

        public RelayCommand RunNowCommand { get; }

        public RelayCommand RefreshCommand { get; }

        public RelayCommand CloseCommand { get; }

        public RelayCommand ReloadEditorCommand { get; }

        /// <summary>打开页面并重读目录；可指定选中任务。</summary>
        public void Open(string selectId = null)
        {
            IsOpen = true;
            StatusMessage = string.Empty;
            Reload(selectId);
        }

        public void Close()
        {
            StoreDraft();
            IsOpen = false;
            StatusMessage = string.Empty;
        }

        /// <summary>重读全部任务；<paramref name="selectId"/> 指定选中项。</summary>
        public void Reload(string selectId = null)
        {
            if (_disposed)
            {
                return;
            }

            IReadOnlyList<ScheduleDefinition> list = _schedules.List(true);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            _all.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                ScheduleDefinition definition = list[i];
                if (definition == null || string.IsNullOrWhiteSpace(definition.Id))
                {
                    continue;
                }

                seen.Add(definition.Id);
                ScheduleTaskRowViewModel row;
                if (!_rows.TryGetValue(definition.Id, out row))
                {
                    row = new ScheduleTaskRowViewModel(definition, _workspaceName);
                    _rows[definition.Id] = row;
                }
                else
                {
                    row.Update(definition);
                }

                _all.Add(row);
            }

            var removed = new List<string>();
            foreach (string id in _rows.Keys)
            {
                if (!seen.Contains(id))
                {
                    removed.Add(id);
                }
            }

            for (int i = 0; i < removed.Count; i++)
            {
                _rows.Remove(removed[i]);
                _drafts.Remove(removed[i]);
            }

            ApplyFilter(selectId);
            SyncEditor();
            OnPropertyChanged(nameof(HasNoItems));
            OnPropertyChanged(nameof(EmptyText));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _schedules.Changed -= OnSchedulesChanged;
            if (_editor != null)
            {
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }
        }

        public static string FormatTime(DateTimeOffset? value)
        {
            return value.HasValue
                ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "无";
        }

        private void OnSchedulesChanged(object sender, EventArgs args)
        {
            _dispatch(() =>
            {
                if (!IsOpen || _disposed)
                {
                    return;
                }

                Reload(_editor == null ? null : _editor.Id);
            });
        }

        private void OnEditorPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args == null)
            {
                return;
            }

            if (args.PropertyName == "HasChanges" || args.PropertyName == "CanSave"
                || args.PropertyName == "IsConflict")
            {
                OnPropertyChanged(nameof(IsEditingExisting));
            }
        }

        private void ActivateEditor(ScheduleEditorViewModel editor)
        {
            if (ReferenceEquals(_editor, editor))
            {
                return;
            }

            if (_editor != null)
            {
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }

            Editor = editor;
            OnPropertyChanged(nameof(IsEditingExisting));
            OnPropertyChanged(nameof(HasSelection));
        }

        private void ApplyFilter(string selectId)
        {
            string query = (_searchText ?? string.Empty).Trim();
            string keepId = selectId
                ?? (_selected == null ? null : _selected.Id)
                ?? (_editor == null ? null : _editor.Id);

            var visible = new List<ScheduleTaskRowViewModel>();
            for (int i = 0; i < _all.Count; i++)
            {
                ScheduleTaskRowViewModel row = _all[i];
                if (!MatchesFilter(row) || !MatchesSearch(row, query))
                {
                    continue;
                }

                visible.Add(row);
            }

            _applyingFilter = true;
            try
            {
                Items.Clear();
                for (int i = 0; i < visible.Count; i++)
                {
                    Items.Add(visible[i]);
                }
            }
            finally
            {
                _applyingFilter = false;
            }

            OnPropertyChanged(nameof(HasNoItems));
            OnPropertyChanged(nameof(EmptyText));

            if (_isNewDraft && string.IsNullOrEmpty(selectId))
            {
                return;
            }

            ScheduleTaskRowViewModel target = null;
            if (!string.IsNullOrEmpty(keepId))
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (string.Equals(Items[i].Id, keepId, StringComparison.Ordinal))
                    {
                        target = Items[i];
                        break;
                    }
                }
            }

            Selected = target ?? (Items.Count > 0 ? Items[0] : null);
        }

        private bool MatchesFilter(ScheduleTaskRowViewModel row)
        {
            if (row == null || row.Model == null)
            {
                return false;
            }

            switch (_selectedFilter.Filter)
            {
                case ScheduleFilter.Paused:
                    return row.Model.Status == ScheduleStatus.Paused;
                case ScheduleFilter.Ended:
                    return row.Model.Status == ScheduleStatus.Ended;
                case ScheduleFilter.All:
                    return true;
                default:
                    return row.Model.Status == ScheduleStatus.Active;
            }
        }

        private static bool MatchesSearch(ScheduleTaskRowViewModel row, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            string needle = query.ToLowerInvariant();
            if (row.Title.ToLowerInvariant().Contains(needle))
            {
                return true;
            }

            string prompt = row.Model == null ? string.Empty : row.Model.Prompt ?? string.Empty;
            if (prompt.ToLowerInvariant().Contains(needle))
            {
                return true;
            }

            string sessionId = row.Model == null ? string.Empty : row.Model.SessionId ?? string.Empty;
            return sessionId.ToLowerInvariant().Contains(needle);
        }

        private void SyncEditor()
        {
            if (_isNewDraft)
            {
                return;
            }

            if (_selected == null)
            {
                ActivateEditor(null);
                return;
            }

            ScheduleDefinition model = _selected.Model;
            if (_editor == null || !string.Equals(_editor.Id, model.Id, StringComparison.Ordinal))
            {
                ActivateEditor(DraftFor(_selected));
                return;
            }

            if (_editor.BaselineUpdatedAt != model.UpdatedAt)
            {
                if (_editor.HasChanges)
                {
                    if (_editor.ContentDiffers(model))
                    {
                        _editor.MarkConflict();
                    }
                    else
                    {
                        _editor.RefreshModel(model);
                    }
                }
                else
                {
                    _editor.Load(model);
                }
            }
            else if (!_editor.HasChanges)
            {
                // 即使版本没变，刷新状态与运行记录（例如刚点过立即执行）。
                _editor.Load(model);
            }
        }

        private ScheduleEditorViewModel DraftFor(ScheduleTaskRowViewModel row)
        {
            ScheduleEditorViewModel draft;
            if (_drafts.TryGetValue(row.Id, out draft) && draft != null)
            {
                if (draft.IsConflict)
                {
                    return draft;
                }

                if (draft.HasChanges)
                {
                    if (draft.ContentDiffers(row.Model))
                    {
                        draft.MarkConflict();
                    }
                    else
                    {
                        draft.RefreshModel(row.Model);
                    }

                    return draft;
                }
            }

            draft = new ScheduleEditorViewModel(_now, _workspaceName);
            draft.Load(row.Model);
            _drafts[row.Id] = draft;
            return draft;
        }

        private void StoreDraft()
        {
            if (_editor == null)
            {
                return;
            }

            string key = _editor.IsExisting ? _editor.Id : NewDraftKey;
            _drafts[key] = _editor;
        }

        private void StartNew()
        {
            StoreDraft();
            _isNewDraft = true;
            _selected = null;
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(HasSelection));

            ScheduleEditorViewModel draft;
            if (!_drafts.TryGetValue(NewDraftKey, out draft) || draft == null)
            {
                draft = new ScheduleEditorViewModel(_now, _workspaceName);
                draft.LoadNew(_workspaceId(), _sessionId());
                _drafts[NewDraftKey] = draft;
            }

            if (_selectedTab.IsHistory)
            {
                SelectedTab = TabOptions[0];
            }

            StatusMessage = string.Empty;
            ActivateEditor(draft);
        }

        private bool CanSave()
        {
            return _editor != null && _editor.CanSave;
        }

        private bool CanToggleActive()
        {
            return _editor != null && _editor.IsExisting && !_editor.HasChanges && !_editor.IsConflict;
        }

        private bool CanRunNow()
        {
            return _editor != null && _editor.IsExisting && !_editor.HasChanges && !_editor.IsConflict;
        }

        private void Save()
        {
            SaveCore(false);
        }

        private void SaveCore(bool force)
        {
            ScheduleEditorViewModel editor = _editor;
            if (editor == null || !editor.Validate())
            {
                return;
            }

            string rule;
            try
            {
                rule = editor.BuildRule();
            }
            catch (ScheduleException exception)
            {
                editor.SetError(exception.Message);
                return;
            }

            string title = (editor.Title ?? string.Empty).Trim();
            string prompt = (editor.Prompt ?? string.Empty).Trim();

            if (!editor.IsExisting)
            {
                string sessionId = editor.Target == ScheduleTarget.ThisSession ? _sessionId() : null;
                if (editor.Target == ScheduleTarget.ThisSession && string.IsNullOrWhiteSpace(sessionId))
                {
                    editor.SetError("当前没有打开的会话。先打开一个会话，或把执行位置改成「新建会话」。");
                    return;
                }

                try
                {
                    ScheduleDefinition created = _schedules.Create(
                        title,
                        prompt,
                        rule,
                        editor.Target,
                        sessionId,
                        string.IsNullOrWhiteSpace(editor.WorkspaceId) ? _workspaceId() : editor.WorkspaceId);
                    _drafts.Remove(NewDraftKey);
                    _isNewDraft = false;
                    ClearEditor();
                    StatusMessage = "已创建「" + created.Title + "」。";
                    Reload(created.Id);
                }
                catch (ScheduleException exception)
                {
                    editor.SetError(exception.Message);
                }

                return;
            }

            try
            {
                string session = editor.SessionId;
                if (editor.Target == ScheduleTarget.ThisSession && string.IsNullOrWhiteSpace(session))
                {
                    session = _sessionId();
                    if (string.IsNullOrWhiteSpace(session))
                    {
                        editor.SetError("当前没有打开的会话。先打开一个会话，或把执行位置改成「新建会话」。");
                        return;
                    }
                }

                ScheduleUpdateResult result = _schedules.Update(
                    editor.Id,
                    title,
                    prompt,
                    rule,
                    null,
                    force ? (DateTimeOffset?)null : editor.BaselineUpdatedAt,
                    editor.Target,
                    editor.Target == ScheduleTarget.New ? null : session,
                    editor.WorkspaceId);

                if (result.Status == ScheduleUpdateStatus.Conflict)
                {
                    editor.MarkConflict();
                    StatusMessage = string.Empty;
                    return;
                }

                if (result.Status == ScheduleUpdateStatus.NotFound)
                {
                    editor.SetError("这条任务已被删除，刷新后再试。");
                    return;
                }

                _drafts.Remove(editor.Id);
                editor.Load(result.Definition);
                StatusMessage = "已保存。";
                Reload(editor.Id);
            }
            catch (ScheduleException exception)
            {
                editor.SetError(exception.Message);
            }
        }

        private void Discard()
        {
            ScheduleEditorViewModel editor = _editor;
            if (editor == null)
            {
                return;
            }

            string key = editor.IsExisting ? editor.Id : NewDraftKey;
            _drafts.Remove(key);

            if (!editor.IsExisting)
            {
                _isNewDraft = false;
                ClearEditor();
                Selected = Items.Count > 0 ? Items[0] : null;
                StatusMessage = string.Empty;
                return;
            }

            ScheduleDefinition model = _schedules.Get(editor.Id);
            if (model == null)
            {
                StatusMessage = "这条任务已被删除。";
                Reload();
                return;
            }

            editor.Load(model);
            StatusMessage = "已放弃改动。";
            Reload(editor.Id);
        }

        private void Delete()
        {
            ScheduleEditorViewModel editor = _editor;
            if (editor == null || !editor.IsExisting)
            {
                return;
            }

            bool accepted = _confirm(
                "删除定时任务",
                "「" + editor.Title.Trim() + "」及其运行记录将被删除。已经排进会话的消息不会撤回。",
                "删除",
                "取消",
                true);
            if (!accepted)
            {
                return;
            }

            string title = editor.Title.Trim();
            _drafts.Remove(editor.Id);
            _isNewDraft = false;
            ClearEditor();
            if (_schedules.Delete(editor.Id))
            {
                StatusMessage = "已删除「" + title + "」。";
            }
            else
            {
                StatusMessage = "这条任务已经被删除。";
            }

            Reload();
        }

        private void ToggleActive()
        {
            ScheduleEditorViewModel editor = _editor;
            if (editor == null || editor.Model == null)
            {
                return;
            }

            ScheduleDefinition model = editor.Model;
            try
            {
                ScheduleUpdateResult result = _schedules.Update(
                    model.Id,
                    null,
                    null,
                    null,
                    !model.Active,
                    model.UpdatedAt);
                if (result.Status == ScheduleUpdateStatus.Conflict)
                {
                    editor.MarkConflict();
                    return;
                }

                if (result.Status == ScheduleUpdateStatus.NotFound)
                {
                    editor.SetError("这条任务已被删除，刷新后再试。");
                    return;
                }

                StatusMessage = model.Active ? "已暂停。" : "已恢复。";
                Reload(model.Id);
            }
            catch (ScheduleException exception)
            {
                editor.SetError(exception.Message);
            }
        }

        private void RunNow()
        {
            ScheduleEditorViewModel editor = _editor;
            if (editor == null || !editor.IsExisting)
            {
                return;
            }

            string id = editor.Id;
            if (_schedules.RunNow(id))
            {
                StatusMessage = "已排进会话；下一次运行时间不变。";
                Reload(id);
            }
            else
            {
                editor.SetError("立即执行失败：任务不存在。");
            }
        }

        private void ReloadEditor()
        {
            ScheduleEditorViewModel editor = _editor;
            if (editor == null || !editor.IsExisting)
            {
                return;
            }

            ScheduleDefinition model = _schedules.Get(editor.Id);
            if (model == null)
            {
                StatusMessage = "这条任务已被删除。";
                Reload();
                return;
            }

            _drafts.Remove(editor.Id);
            editor.Load(model);
            StatusMessage = "已载入最新版本。";
        }

        private void ClearEditor()
        {
            if (_editor != null)
            {
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }

            _editor = null;
            _selected = null;
            OnPropertyChanged(nameof(Editor));
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsEditingExisting));
        }
    }
}
