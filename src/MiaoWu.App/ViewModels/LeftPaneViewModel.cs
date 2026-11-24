using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.App.Services;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 左栏：工作区手风琴（同时只展开一个）+ 活动工作区的会话/文件视图。
    /// 工作区行固定在上方，活动内容在下方独立虚拟化，保证 120 条会话时其它工作区仍可达。
    /// </summary>
    public sealed class LeftPaneViewModel : ObservableObject
    {
        private readonly ISessionCatalog _sessionCatalog;
        private readonly IFileCatalog _fileCatalog;
        private readonly ISessionEditor _sessionEditor;
        private readonly IFileEditor _fileEditor;
        private readonly IFolderPicker _folderPicker;
        private readonly IFilePicker _filePicker;
        private readonly IDialogService _dialogs;
        private readonly ObservableCollection<WorkspaceItemViewModel> _workspaces;
        private readonly HashSet<string> _openedFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private WorkspaceItemViewModel _activeWorkspace;
        private string _inlineError;

        public LeftPaneViewModel(
            ISessionCatalog sessionCatalog,
            IFileCatalog fileCatalog,
            ISessionEditor sessionEditor,
            IFileEditor fileEditor,
            IFolderPicker folderPicker,
            IFilePicker filePicker,
            IDialogService dialogs,
            WorkspaceCollection collection,
            DateTime now)
        {
            _sessionCatalog = sessionCatalog ?? throw new ArgumentNullException(nameof(sessionCatalog));
            _fileCatalog = fileCatalog ?? throw new ArgumentNullException(nameof(fileCatalog));
            _sessionEditor = sessionEditor ?? throw new ArgumentNullException(nameof(sessionEditor));
            _fileEditor = fileEditor ?? throw new ArgumentNullException(nameof(fileEditor));
            _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
            _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            Now = now;

            _workspaces = new ObservableCollection<WorkspaceItemViewModel>();
            AddWorkspaceCommand = new RelayCommand(AddWorkspace);
            RemoveWorkspaceCommand = new RelayCommand(RemoveWorkspace);
            ActivateWorkspaceCommand = new RelayCommand(ActivateWorkspaceFromParameter);
            ToggleViewCommand = new RelayCommand(ToggleViewFromParameter);
            OpenSettingsCommand = new RelayCommand(RaiseSettingsRequested);
            OpenMarketplaceCommand = new RelayCommand(RaiseMarketplaceRequested);
            OpenScheduledTasksCommand = new RelayCommand(RaiseScheduledTasksRequested);
            NewSessionCommand = new RelayCommand(RaiseNewSessionRequested, () => _activeWorkspace != null);
            SearchCommand = new RelayCommand(() => { }, () => false);

            IEnumerable<Workspace> workspaces = collection == null
                ? new Workspace[0]
                : WorkspacePolicy.Sort(collection.Workspaces);

            foreach (Workspace workspace in workspaces)
            {
                var item = new WorkspaceItemViewModel(this, workspace, _sessionCatalog, _fileCatalog);
                AttachWorkspaceItem(item);
                item.LoadSessionCount();
                _workspaces.Add(item);
            }

            WorkspaceItemViewModel target = _workspaces.FirstOrDefault(
                w => collection != null && string.Equals(w.Id, collection.ActiveWorkspaceId, StringComparison.Ordinal));

            if (target == null)
            {
                target = _workspaces.FirstOrDefault(w => w.Model.IsExpanded);
            }

            if (target == null && _workspaces.Count > 0)
            {
                target = _workspaces[0];
            }

            if (target != null)
            {
                ActivateWorkspace(target);
                if (collection != null && !string.IsNullOrEmpty(collection.ActiveWorkspaceId)
                    && string.Equals(target.Id, collection.ActiveWorkspaceId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(collection.ActiveWorkspaceId))
                {
                    // 会话选中由 UiSettings.ActiveSessionId 恢复；这里先把行建出来。
                    target.EnsureSessionsLoaded();
                }
            }

            OnPropertyChanged(nameof(HasWorkspaces));
            OnPropertyChanged(nameof(IsEmpty));
        }

        public event EventHandler StateChanged;

        public event EventHandler<FileOpenRequestedEventArgs> FileOpenRequested;

        public event EventHandler SettingsRequested;

        public event EventHandler MarketplaceRequested;

        /// <summary>用户点击“定时任务”。与能力市场一致，先给出即将推出的说明。</summary>
        public event EventHandler ScheduledTasksRequested;

        /// <summary>活动工作区的选中会话变化（含首次加载与新建后选中）。</summary>
        public event EventHandler SessionSelectionChanged;

        /// <summary>用户点击“新建会话”。由主窗口创建真实的 .miaowu 会话。</summary>
        public event EventHandler NewSessionRequested;

        /// <summary>左栏重命名会话成功后通知主窗口同步右栏标题。</summary>
        public event EventHandler<SessionRenamedEventArgs> SessionRenamed;

        /// <summary>用户要求删除会话；由主窗口先关停运行中的会话再删文件。</summary>
        public event EventHandler<SessionActionRequestedEventArgs> SessionDeleteRequested;

        /// <summary>用户要求导出会话；由主窗口读取 transcript 并落盘。</summary>
        public event EventHandler<SessionActionRequestedEventArgs> SessionExportRequested;

        /// <summary>用户要求清空工作区会话；由主窗口统一关停并删除。</summary>
        public event EventHandler<WorkspaceActionRequestedEventArgs> ClearSessionsRequested;

        /// <summary>文件重命名/删除后通知主窗口同步中栏标签。</summary>
        public event EventHandler<FileRenamedEventArgs> FileRenamed;

        public event EventHandler<FileDeletedEventArgs> FileDeleted;

        public DateTime Now { get; }

        public ObservableCollection<WorkspaceItemViewModel> Workspaces
        {
            get { return _workspaces; }
        }

        public WorkspaceItemViewModel ActiveWorkspace
        {
            get { return _activeWorkspace; }
        }

        public bool HasWorkspaces
        {
            get { return _workspaces.Count > 0; }
        }

        public bool IsEmpty
        {
            get { return _workspaces.Count == 0; }
        }

        public bool HasActiveWorkspace
        {
            get { return _activeWorkspace != null; }
        }

        public string InlineError
        {
            get { return _inlineError; }
            private set
            {
                if (string.Equals(_inlineError, value, StringComparison.Ordinal))
                {
                    return;
                }

                _inlineError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasInlineError));
            }
        }

        public bool HasInlineError
        {
            get { return !string.IsNullOrEmpty(_inlineError); }
        }

        /// <summary>P3 起可从任意工作区新建会话。</summary>
        public bool CanCreateSession
        {
            get { return _activeWorkspace != null; }
        }

        public string NewSessionToolTip
        {
            get { return "新建会话 (Ctrl+N)"; }
        }

        public string ActiveSessionId
        {
            get
            {
                return _activeWorkspace == null || _activeWorkspace.SelectedSession == null
                    ? null
                    : _activeWorkspace.SelectedSession.Session.Id;
            }
        }

        /// <summary>按工作区 id 找显示名；定时任务页面用来显示关联工作区。</summary>
        public string FindWorkspaceName(string workspaceId)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return null;
            }

            for (int i = 0; i < _workspaces.Count; i++)
            {
                if (string.Equals(_workspaces[i].Id, workspaceId, StringComparison.Ordinal))
                {
                    return _workspaces[i].Name;
                }
            }

            return null;
        }

        public string SearchToolTip
        {
            get { return Strings.SearchPendingHint; }
        }

        public RelayCommand AddWorkspaceCommand { get; }

        public RelayCommand RemoveWorkspaceCommand { get; }

        public RelayCommand ActivateWorkspaceCommand { get; }

        public RelayCommand ToggleViewCommand { get; }

        public RelayCommand OpenSettingsCommand { get; }

        public RelayCommand OpenMarketplaceCommand { get; }

        public RelayCommand OpenScheduledTasksCommand { get; }

        public RelayCommand NewSessionCommand { get; }

        public RelayCommand SearchCommand { get; }

        /// <summary>手风琴：展开指定工作区，其余收起；已展开的保持不动。</summary>
        public void ActivateWorkspace(WorkspaceItemViewModel workspace)
        {
            if (workspace == null || !_workspaces.Contains(workspace))
            {
                return;
            }

            WorkspacePolicy.ExpandOnly(_workspaces.Select(w => w.Model), workspace.Id);
            _activeWorkspace = workspace;

            foreach (WorkspaceItemViewModel item in _workspaces)
            {
                item.RefreshExpansion();
            }

            workspace.EnsureSessionsLoaded();
            if (workspace.IsFilesView)
            {
                workspace.EnsureTreeLoaded();
            }

            InlineError = null;
            OnPropertyChanged(nameof(ActiveWorkspace));
            OnPropertyChanged(nameof(HasActiveWorkspace));
            OnPropertyChanged(nameof(ActiveSessionId));
            OnPropertyChanged(nameof(CanCreateSession));
            RaiseStateChanged();
            RaiseSessionSelectionChanged();
        }

        /// <summary>单击工作区行：已展开的收起，其余展开（保持手风琴单选）。</summary>
        public void ToggleWorkspace(WorkspaceItemViewModel workspace)
        {
            if (workspace == null || !_workspaces.Contains(workspace))
            {
                return;
            }

            bool collapsedHere = ReferenceEquals(workspace, _activeWorkspace) && workspace.Model.IsExpanded;
            if (!collapsedHere)
            {
                ActivateWorkspace(workspace);
                return;
            }

            WorkspacePolicy.ExpandOnly(_workspaces.Select(w => w.Model), null);
            foreach (WorkspaceItemViewModel item in _workspaces)
            {
                item.RefreshExpansion();
            }

            InlineError = null;
            RaiseStateChanged();
        }

        public void SetActiveView(WorkspaceView view)
        {
            if (_activeWorkspace == null)
            {
                return;
            }

            _activeWorkspace.SetView(view);
            OnPropertyChanged(nameof(ActiveView));
        }

        public WorkspaceView ActiveView
        {
            get
            {
                return _activeWorkspace == null
                    ? WorkspaceView.Sessions
                    : _activeWorkspace.Model.View;
            }
        }

        public void NotifyWorkspaceStateChanged()
        {
            RaiseStateChanged();
        }

        /// <summary>内联错误：一次只显示一条，下一次成功操作时清除。</summary>
        public void ReportError(string message)
        {
            InlineError = message;
        }

        /// <summary>在指定工作区新建会话：先激活该工作区，再走统一的 NewSessionRequested。</summary>
        public void NewSessionFor(WorkspaceItemViewModel workspace)
        {
            if (workspace == null || !_workspaces.Contains(workspace))
            {
                return;
            }

            ActivateWorkspace(workspace);
            RaiseNewSessionRequested();
        }

        /// <summary>按置顶优先刷新工作区行顺序；活动工作区引用不变。</summary>
        public void SortWorkspaceItems()
        {
            IReadOnlyList<Workspace> orderedModels = WorkspacePolicy.Sort(
                _workspaces.Select(w => w.Model));
            var orderedItems = new List<WorkspaceItemViewModel>(orderedModels.Count);

            foreach (Workspace model in orderedModels)
            {
                WorkspaceItemViewModel match = _workspaces.FirstOrDefault(
                    w => ReferenceEquals(w.Model, model));
                if (match != null)
                {
                    orderedItems.Add(match);
                }
            }

            for (int index = 0; index < orderedItems.Count; index++)
            {
                int current = _workspaces.IndexOf(orderedItems[index]);
                if (current != index)
                {
                    _workspaces.Move(current, index);
                }
            }
        }

        public void RenameSession(WorkspaceItemViewModel workspace, string sessionId, string title)
        {
            if (workspace == null || string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            try
            {
                _sessionEditor
                    .RenameAsync(workspace.Model, sessionId, title, System.Threading.CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                InlineError = null;
                workspace.RefreshSessions();
                workspace.SelectSessionById(sessionId);
                SessionRenamed?.Invoke(this, new SessionRenamedEventArgs(workspace, sessionId, title));
            }
            catch (Exception exception)
            {
                ReportError(exception.Message);
            }
        }

        public void SetSessionPinned(WorkspaceItemViewModel workspace, string sessionId, bool pinned)
        {
            if (workspace == null || string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            try
            {
                _sessionEditor
                    .SetPinnedAsync(
                        workspace.Model,
                        sessionId,
                        pinned,
                        System.Threading.CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                InlineError = null;
                workspace.RefreshSessions();
                workspace.SelectSessionById(sessionId);
            }
            catch (Exception exception)
            {
                ReportError(exception.Message);
            }
        }

        public void SetSessionArchived(WorkspaceItemViewModel workspace, string sessionId, bool archived)
        {
            if (workspace == null || string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            try
            {
                _sessionEditor
                    .SetArchivedAsync(
                        workspace.Model,
                        sessionId,
                        archived,
                        System.Threading.CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                InlineError = null;
                workspace.RefreshSessions();
                if (!archived)
                {
                    workspace.SelectSessionById(sessionId);
                }
            }
            catch (Exception exception)
            {
                ReportError(exception.Message);
            }
        }

        public void RequestDeleteSession(
            WorkspaceItemViewModel workspace,
            string sessionId,
            string title)
        {
            if (workspace == null || string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            SessionDeleteRequested?.Invoke(
                this,
                new SessionActionRequestedEventArgs(workspace, sessionId, title));
        }

        public void RequestExportSession(
            WorkspaceItemViewModel workspace,
            string sessionId,
            string title)
        {
            if (workspace == null || string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            SessionExportRequested?.Invoke(
                this,
                new SessionActionRequestedEventArgs(workspace, sessionId, title));
        }

        public void RequestClearSessions(WorkspaceItemViewModel workspace)
        {
            if (workspace == null)
            {
                return;
            }

            ClearSessionsRequested?.Invoke(
                this,
                new WorkspaceActionRequestedEventArgs(workspace));
        }

        /// <summary>重命名文件；成功后在已打开集合里同步旧路径，并通知中栏。</summary>
        public string RenameFile(string filePath, string newName)
        {
            string oldPath = System.IO.Path.GetFullPath(filePath);
            string newPath = System.IO.Path.GetFullPath(_fileEditor.Rename(oldPath, newName));
            InlineError = null;

            if (_openedFiles.Remove(oldPath))
            {
                _openedFiles.Add(newPath);
            }

            FileRenamed?.Invoke(this, new FileRenamedEventArgs(oldPath, newPath));
            return newPath;
        }

        /// <summary>删除文件（回收站），并从已打开集合移除。</summary>
        public void DeleteFile(string filePath)
        {
            string path = System.IO.Path.GetFullPath(filePath);
            _fileEditor.Delete(path);
            InlineError = null;

            if (_openedFiles.Remove(path))
            {
                foreach (WorkspaceItemViewModel workspace in _workspaces)
                {
                    ApplyOpenedState(workspace);
                }
            }

            FileDeleted?.Invoke(this, new FileDeletedEventArgs(path));
        }

        public bool ConfirmFileDelete(string fileName)
        {
            return _dialogs.Confirm(
                Strings.DeleteFileConfirmTitle,
                "“" + (fileName ?? string.Empty) + "”将被移到回收站，可从回收站恢复。",
                Strings.Delete,
                Strings.Cancel);
        }

        /// <summary>用磁盘上的最新工作区列表重建左栏（首次向导完成后调用）。</summary>
        public void Reload(WorkspaceCollection collection)
        {
            _workspaces.Clear();
            _activeWorkspace = null;

            IEnumerable<Workspace> workspaces = collection == null
                ? new Workspace[0]
                : WorkspacePolicy.Sort(collection.Workspaces);

            foreach (Workspace workspace in workspaces)
            {
                var item = new WorkspaceItemViewModel(this, workspace, _sessionCatalog, _fileCatalog);
                AttachWorkspaceItem(item);
                item.LoadSessionCount();
                _workspaces.Add(item);
            }

            WorkspaceItemViewModel target = _workspaces.FirstOrDefault(
                w => collection != null && string.Equals(w.Id, collection.ActiveWorkspaceId, StringComparison.Ordinal));
            if (target == null && _workspaces.Count > 0)
            {
                target = _workspaces[0];
            }

            if (target != null)
            {
                ActivateWorkspace(target);
            }

            OnPropertyChanged(nameof(HasWorkspaces));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ActiveWorkspace));
            OnPropertyChanged(nameof(HasActiveWorkspace));
            RaiseStateChanged();
        }

        /// <summary>恢复工作区列表：保持选中项，重新读会话索引。</summary>
        public void RefreshSessions()
        {
            WorkspaceItemViewModel active = _activeWorkspace;
            foreach (WorkspaceItemViewModel item in _workspaces)
            {
                item.LoadSessionCount();
            }

            if (active != null)
            {
                active.RefreshSessions();
                OnPropertyChanged(nameof(ActiveSessionId));
            }
        }

        /// <summary>只刷新指定工作区的会话行（用于删掉非活动工作区里的空白会话）。</summary>
        public void RefreshSessions(Workspace workspace)
        {
            WorkspaceItemViewModel item = workspace == null
                ? null
                : _workspaces.FirstOrDefault(w => ReferenceEquals(w.Model, workspace));
            if (item == null)
            {
                return;
            }

            item.LoadSessionCount();
            item.RefreshSessions();
            if (ReferenceEquals(item, _activeWorkspace))
            {
                OnPropertyChanged(nameof(ActiveSessionId));
            }
        }

        public void SelectSession(string sessionId)
        {
            if (_activeWorkspace != null)
            {
                _activeWorkspace.SelectSessionById(sessionId);
            }
        }

        /// <summary>
        /// 刷新前把模型选中项对齐到指定会话：RefreshSessions 会按旧 id 重建选中项，
        /// 不先对齐就会在刷新过程中触发一次指向旧会话的“切换”事件。
        /// </summary>
        public void AlignSelectedSession(string sessionId)
        {
            if (_activeWorkspace != null && !string.IsNullOrEmpty(sessionId))
            {
                _activeWorkspace.Model.SelectedSessionId = sessionId;
            }
        }

        /// <summary>与中栏标签同步：已打开的文件在树里显示绿色图标。</summary>
        public void MarkFileOpened(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            string normalized;
            try
            {
                normalized = System.IO.Path.GetFullPath(filePath);
            }
            catch (Exception)
            {
                return;
            }

            _openedFiles.Add(normalized);

            foreach (WorkspaceItemViewModel workspace in _workspaces)
            {
                ApplyOpenedState(workspace);
            }
        }

        public bool IsFileOpened(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string normalized;
            try
            {
                normalized = System.IO.Path.GetFullPath(filePath);
            }
            catch (Exception)
            {
                return false;
            }

            return _openedFiles.Contains(normalized);
        }

        public void UnmarkFileOpened(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            string normalized;
            try
            {
                normalized = System.IO.Path.GetFullPath(filePath);
            }
            catch (Exception)
            {
                return;
            }

            if (!_openedFiles.Remove(normalized))
            {
                return;
            }

            foreach (WorkspaceItemViewModel workspace in _workspaces)
            {
                ApplyOpenedState(workspace);
            }
        }

        public void ApplyOpenedState(WorkspaceItemViewModel workspace)
        {
            if (workspace == null)
            {
                return;
            }

            foreach (FileTreeRowViewModel row in workspace.TreeRows)
            {
                if (row.IsDirectory)
                {
                    continue;
                }

                bool opened = IsFileOpened(row.Node.FullPath);
                if (row.Node.IsOpened != opened)
                {
                    row.Node.IsOpened = opened;
                    row.Refresh();
                }
            }
        }

        public void RequestOpenFile(string filePath, string displayName)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            FileOpenRequested?.Invoke(
                this,
                new FileOpenRequestedEventArgs(filePath, string.IsNullOrWhiteSpace(displayName)
                    ? System.IO.Path.GetFileName(filePath)
                    : displayName));
        }

        public WorkspaceCollection GetCollection()
        {
            var collection = new WorkspaceCollection
            {
                SchemaVersion = 1,
                ActiveWorkspaceId = _activeWorkspace == null ? null : _activeWorkspace.Id
            };

            foreach (WorkspaceItemViewModel item in _workspaces)
            {
                collection.Workspaces.Add(item.Model);
            }

            return collection;
        }

        public void OpenFileFromPicker(WorkspaceItemViewModel workspace)
        {
            if (workspace == null)
            {
                return;
            }

            string file = _filePicker.PickFile(
                Strings.OpenFileDialogTitle,
                workspace.Path,
                "文本与 Markdown|*.md;*.markdown;*.txt;*.json;*.cs;*.xaml;*.xml;*.log;*.ps1;*.config;*.yml;*.yaml;*.sql;*.html;*.css;*.js;*.ts|图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*");

            if (!string.IsNullOrEmpty(file))
            {
                RequestOpenFile(file, Path.GetFileName(file));
            }
        }

        public static string GetGroupHeaderText(SessionGroup group)
        {
            switch (group)
            {
                case SessionGroup.Pinned:
                    return Strings.Pinned;
                case SessionGroup.Today:
                    return Strings.Today;
                case SessionGroup.Yesterday:
                    return Strings.Yesterday;
                case SessionGroup.Archived:
                    return Strings.Archived;
                default:
                    return Strings.Earlier;
            }
        }

        private void AddWorkspace()
        {
            string initialPath = _activeWorkspace == null ? null : _activeWorkspace.Path;
            string path = _folderPicker.PickFolder(Strings.AddWorkspaceTooltip, initialPath);
            if (path == null)
            {
                return;
            }

            var models = _workspaces.Select(w => w.Model).ToList();
            Workspace created;
            string error;

            if (!WorkspacePolicy.TryAdd(models, path, out created, out error))
            {
                InlineError = error;
                return;
            }

            InlineError = null;
            var item = new WorkspaceItemViewModel(this, created, _sessionCatalog, _fileCatalog);
            AttachWorkspaceItem(item);
            item.LoadSessionCount();
            _workspaces.Add(item);

            OnPropertyChanged(nameof(HasWorkspaces));
            OnPropertyChanged(nameof(IsEmpty));
            ActivateWorkspace(item);
        }

        private void RemoveWorkspace(object parameter)
        {
            RemoveWorkspaceItem(parameter as WorkspaceItemViewModel);
        }

        public void RemoveWorkspaceItem(WorkspaceItemViewModel workspace)
        {
            if (workspace == null || !_workspaces.Contains(workspace))
            {
                return;
            }

            bool confirmed = _dialogs.Confirm(
                Strings.RemoveWorkspaceConfirmTitle,
                Strings.RemoveWorkspaceConfirmBody,
                Strings.Remove,
                Strings.Cancel,
                false);

            if (!confirmed)
            {
                return;
            }

            int index = _workspaces.IndexOf(workspace);
            bool wasActive = ReferenceEquals(_activeWorkspace, workspace);
            _workspaces.Remove(workspace);
            WorkspacePolicy.Renumber(_workspaces.Select(w => w.Model).ToList());

            if (wasActive)
            {
                _activeWorkspace = null;
                if (_workspaces.Count > 0)
                {
                    ActivateWorkspace(_workspaces[Math.Min(index, _workspaces.Count - 1)]);
                }
            }

            OnPropertyChanged(nameof(HasWorkspaces));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ActiveWorkspace));
            OnPropertyChanged(nameof(HasActiveWorkspace));
            RaiseStateChanged();
        }

        private void ActivateWorkspaceFromParameter(object parameter)
        {
            ActivateWorkspace(parameter as WorkspaceItemViewModel);
        }

        private void ToggleViewFromParameter(object parameter)
        {
            string value = parameter as string;
            WorkspaceView view = string.Equals(value, "Files", StringComparison.OrdinalIgnoreCase)
                ? WorkspaceView.Files
                : WorkspaceView.Sessions;

            SetActiveView(view);
        }

        private void RaiseSettingsRequested()
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseNewSessionRequested()
        {
            NewSessionRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseSessionSelectionChanged()
        {
            OnPropertyChanged(nameof(ActiveSessionId));
            SessionSelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void AttachWorkspaceItem(WorkspaceItemViewModel item)
        {
            item.PropertyChanged += (sender, args) =>
            {
                if (string.Equals(args.PropertyName, "SelectedSession", StringComparison.Ordinal)
                    && ReferenceEquals(item, _activeWorkspace))
                {
                    RaiseSessionSelectionChanged();
                }
            };
        }

        private void RaiseMarketplaceRequested()
        {
            MarketplaceRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseScheduledTasksRequested()
        {
            ScheduledTasksRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class SessionRenamedEventArgs : EventArgs
    {
        public SessionRenamedEventArgs(WorkspaceItemViewModel workspace, string sessionId, string title)
        {
            Workspace = workspace;
            SessionId = sessionId;
            Title = title;
        }

        public WorkspaceItemViewModel Workspace { get; }

        public string SessionId { get; }

        public string Title { get; }
    }

    public sealed class SessionActionRequestedEventArgs : EventArgs
    {
        public SessionActionRequestedEventArgs(
            WorkspaceItemViewModel workspace,
            string sessionId,
            string title)
        {
            Workspace = workspace;
            SessionId = sessionId;
            Title = title;
        }

        public WorkspaceItemViewModel Workspace { get; }

        public string SessionId { get; }

        public string Title { get; }
    }

    public sealed class WorkspaceActionRequestedEventArgs : EventArgs
    {
        public WorkspaceActionRequestedEventArgs(WorkspaceItemViewModel workspace)
        {
            Workspace = workspace;
        }

        public WorkspaceItemViewModel Workspace { get; }
    }

    public sealed class FileRenamedEventArgs : EventArgs
    {
        public FileRenamedEventArgs(string oldPath, string newPath)
        {
            OldPath = oldPath;
            NewPath = newPath;
        }

        public string OldPath { get; }

        public string NewPath { get; }
    }

    public sealed class FileDeletedEventArgs : EventArgs
    {
        public FileDeletedEventArgs(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }
    }

    public sealed class FileOpenRequestedEventArgs : EventArgs
    {
        public FileOpenRequestedEventArgs(string filePath, string displayName)
        {
            FilePath = filePath;
            DisplayName = displayName;
        }

        public string FilePath { get; }

        public string DisplayName { get; }
    }
}
