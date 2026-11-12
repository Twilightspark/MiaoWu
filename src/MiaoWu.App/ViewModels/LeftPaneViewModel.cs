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
            IFolderPicker folderPicker,
            IFilePicker filePicker,
            IDialogService dialogs,
            WorkspaceCollection collection,
            DateTime now)
        {
            _sessionCatalog = sessionCatalog ?? throw new ArgumentNullException(nameof(sessionCatalog));
            _fileCatalog = fileCatalog ?? throw new ArgumentNullException(nameof(fileCatalog));
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

        /// <summary>活动工作区的选中会话变化（含首次加载与新建后选中）。</summary>
        public event EventHandler SessionSelectionChanged;

        /// <summary>用户点击“新建会话”。由主窗口创建真实的 .agent 会话。</summary>
        public event EventHandler NewSessionRequested;

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

        public void SelectSession(string sessionId)
        {
            if (_activeWorkspace != null)
            {
                _activeWorkspace.SelectSessionById(sessionId);
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
                Strings.Cancel);

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

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
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
