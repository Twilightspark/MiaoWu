using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.App.ViewModels
{
    /// <summary>
    /// 一个工作区在左栏中的状态：展开/收起、当前视图、会话行、文件树行、选中项与滚动位置。
    /// 每个工作区各自持有一份行集合，切换工作区时只换内容源，不重建左栏可视树。
    /// </summary>
    public sealed class WorkspaceItemViewModel : ObservableObject
    {
        private readonly LeftPaneViewModel _left;
        private readonly ISessionCatalog _sessionCatalog;
        private readonly IFileCatalog _fileCatalog;
        private readonly List<FileNode> _roots = new List<FileNode>();

        private bool _sessionsLoaded;
        private bool _treeLoaded;
        private SessionRowViewModel _selectedSession;
        private FileTreeRowViewModel _selectedFile;
        private string _stickyHeaderText;
        private bool _isStickyHeaderVisible;

        public WorkspaceItemViewModel(
            LeftPaneViewModel left,
            Workspace model,
            ISessionCatalog sessionCatalog,
            IFileCatalog fileCatalog)
        {
            _left = left ?? throw new ArgumentNullException(nameof(left));
            Model = model ?? throw new ArgumentNullException(nameof(model));
            _sessionCatalog = sessionCatalog ?? throw new ArgumentNullException(nameof(sessionCatalog));
            _fileCatalog = fileCatalog ?? throw new ArgumentNullException(nameof(fileCatalog));

            SessionRows = new ObservableCollection<SessionRowViewModel>();
            TreeRows = new ObservableCollection<FileTreeRowViewModel>();
            ActivateCommand = new RelayCommand(Activate);
            ToggleViewCommand = new RelayCommand(ToggleView);
            SelectSessionCommand = new RelayCommand(SelectSession);
            OpenFileCommand = new RelayCommand(OpenFileFromPicker);
            RemoveCommand = new RelayCommand(() => _left.RemoveWorkspaceItem(this));
        }

        public Workspace Model { get; }

        public string Id
        {
            get { return Model.Id; }
        }

        public string Name
        {
            get { return Model.Name; }
        }

        public string Path
        {
            get { return Model.Path; }
        }

        public string SessionCountText
        {
            get { return Model.SessionCount.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        }

        public bool IsExpanded
        {
            get { return Model.IsExpanded; }
        }

        public WorkspaceView View
        {
            get { return Model.View; }
        }

        public bool IsSessionsView
        {
            get { return Model.View == WorkspaceView.Sessions; }
            set
            {
                if (value)
                {
                    SetView(WorkspaceView.Sessions);
                }
                else
                {
                    // 单选语义：不允许通过取消勾选把两个视图都关掉。
                    OnPropertyChanged();
                }
            }
        }

        public bool IsFilesView
        {
            get { return Model.View == WorkspaceView.Files; }
            set
            {
                if (value)
                {
                    SetView(WorkspaceView.Files);
                }
                else
                {
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowSessionList
        {
            get { return IsExpanded && IsSessionsView; }
        }

        public bool ShowFileTree
        {
            get { return IsExpanded && IsFilesView; }
        }

        public bool ShowSessionEmpty
        {
            get { return _sessionsLoaded && IsSessionsView && !SessionRows.Any(r => !r.IsHeader); }
        }

        public bool ShowFileEmpty
        {
            get { return _treeLoaded && IsFilesView && TreeRows.Count == 0; }
        }

        /// <summary>只有超过 8 层的目录才显示横向滚动条。</summary>
        public bool TreeNeedsHorizontalScroll
        {
            get
            {
                if (!_treeLoaded || TreeRows.Count == 0)
                {
                    return false;
                }

                int maxDepth = TreeRows.Max(r => r.Depth);
                return maxDepth > 8;
            }
        }

        public ObservableCollection<SessionRowViewModel> SessionRows { get; }

        public ObservableCollection<FileTreeRowViewModel> TreeRows { get; }

        public SessionRowViewModel SelectedSession
        {
            get
            {
                return _selectedSession;
            }

            set
            {
                if (value != null && value.IsHeader)
                {
                    return;
                }

                if (ReferenceEquals(_selectedSession, value))
                {
                    return;
                }

                _selectedSession = value;
                Model.SelectedSessionId = value == null ? null : value.Session.Id;
                OnPropertyChanged();
                _left.NotifyWorkspaceStateChanged();
            }
        }

        public FileTreeRowViewModel SelectedFile
        {
            get
            {
                return _selectedFile;
            }

            set
            {
                if (value != null && value.IsError)
                {
                    return;
                }

                if (ReferenceEquals(_selectedFile, value))
                {
                    return;
                }

                _selectedFile = value;
                Model.SelectedFilePath = value == null ? null : value.Node.FullPath;
                OnPropertyChanged();
                _left.NotifyWorkspaceStateChanged();
            }
        }

        public double ScrollOffset
        {
            get { return Model.ScrollOffset; }
            set
            {
                double normalized = double.IsNaN(value) || value < 0 ? 0 : value;
                if (Math.Abs(Model.ScrollOffset - normalized) < 0.5)
                {
                    return;
                }

                Model.ScrollOffset = normalized;
                OnPropertyChanged();
            }
        }

        public string StickyHeaderText
        {
            get { return _stickyHeaderText; }
            set
            {
                if (string.Equals(_stickyHeaderText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _stickyHeaderText = value;
                OnPropertyChanged();
            }
        }

        public bool IsStickyHeaderVisible
        {
            get { return _isStickyHeaderVisible; }
            set
            {
                if (_isStickyHeaderVisible == value)
                {
                    return;
                }

                _isStickyHeaderVisible = value;
                OnPropertyChanged();
            }
        }

        public RelayCommand ActivateCommand { get; }

        public RelayCommand ToggleViewCommand { get; }

        public RelayCommand SelectSessionCommand { get; }

        public RelayCommand OpenFileCommand { get; }

        public RelayCommand RemoveCommand { get; }

        public string AutomationName
        {
            get { return Name + " 工作区"; }
        }

        /// <summary>不构建行，只取数量，供收起的工作区显示右侧数字。</summary>
        public void LoadSessionCount()
        {
            Model.SessionCount = _sessionCatalog.GetSessions(Model).Count;
            OnPropertyChanged(nameof(SessionCountText));
        }

        public void Activate()
        {
            _left.ActivateWorkspace(this);
        }

        public void SetView(WorkspaceView view)
        {
            if (Model.View == view)
            {
                return;
            }

            Model.View = view;
            if (view == WorkspaceView.Files)
            {
                EnsureTreeLoaded();
            }
            else
            {
                EnsureSessionsLoaded();
            }

            RefreshViewState();
            _left.NotifyWorkspaceStateChanged();
        }

        public void EnsureSessionsLoaded()
        {
            if (_sessionsLoaded)
            {
                return;
            }

            RefreshSessions();
        }

        /// <summary>重新读会话索引并重建行，尽量保持选中项。</summary>
        public void RefreshSessions()
        {
            string selectedId = Model.SelectedSessionId;
            IReadOnlyList<SessionSummary> sessions = _sessionCatalog.GetSessions(Model);
            Model.SessionCount = sessions.Count;

            SessionRows.Clear();
            IReadOnlyList<SessionListRow> rows = SessionGroupPolicy.Build(sessions, _left.Now);
            foreach (SessionListRow row in rows)
            {
                SessionRows.Add(new SessionRowViewModel(
                    row,
                    LeftPaneViewModel.GetGroupHeaderText(row.Group),
                    _left.Now));
            }

            _sessionsLoaded = true;
            OnPropertyChanged(nameof(SessionCountText));

            if (!string.IsNullOrEmpty(selectedId))
            {
                SessionRowViewModel match = SessionRows.FirstOrDefault(
                    r => !r.IsHeader && string.Equals(r.Session.Id, selectedId, StringComparison.Ordinal));

                // 会话被删除后不要保留指向已消失行的选中项。
                SelectedSession = match ?? SessionRows.FirstOrDefault(r => !r.IsHeader);
            }
            else
            {
                RestoreSessionSelection();
            }

            RefreshViewState();
        }

        public void SelectSessionById(string sessionId)
        {
            SessionRowViewModel match = SessionRows.FirstOrDefault(
                r => !r.IsHeader && string.Equals(r.Session.Id, sessionId, StringComparison.Ordinal));

            SelectedSession = match ?? SelectedSession;
        }

        public void EnsureTreeLoaded()
        {
            if (_treeLoaded)
            {
                return;
            }

            _roots.Clear();
            _roots.AddRange(_fileCatalog.GetChildren(Model.Path, 0));
            _treeLoaded = true;
            RebuildTreeRows();
            _left.ApplyOpenedState(this);
            RefreshViewState();
        }

        public void RefreshExpansion()
        {
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(IsSessionsView));
            OnPropertyChanged(nameof(IsFilesView));
            RefreshViewState();
        }

        public void RefreshViewState()
        {
            OnPropertyChanged(nameof(IsSessionsView));
            OnPropertyChanged(nameof(IsFilesView));
            OnPropertyChanged(nameof(ShowSessionList));
            OnPropertyChanged(nameof(ShowFileTree));
            OnPropertyChanged(nameof(ShowSessionEmpty));
            OnPropertyChanged(nameof(ShowFileEmpty));
            OnPropertyChanged(nameof(TreeNeedsHorizontalScroll));
        }

        public void ToggleNode(FileTreeRowViewModel row)
        {
            if (row == null || row.IsError || !row.IsDirectory)
            {
                return;
            }

            if (!row.Node.CanExpand)
            {
                return;
            }

            string selectedPath = SelectedFile == null ? null : SelectedFile.Node.FullPath;

            if (row.Node.IsExpanded)
            {
                row.Node.IsExpanded = false;
                RebuildTreeRows();
                RestoreFileSelection(selectedPath);
                return;
            }

            if (row.Node.State != FileNodeState.Loaded)
            {
                row.Node.State = FileNodeState.Loading;
                IReadOnlyList<FileNode> children = _fileCatalog.GetChildren(
                    row.Node.FullPath,
                    row.Node.Depth + 1);

                row.Node.Children.Clear();
                row.Node.Children.AddRange(children);
                row.Node.State = FileNodeState.Loaded;
            }

            row.Node.IsExpanded = true;
            RebuildTreeRows();
            RestoreFileSelection(selectedPath);
        }

        public void CollapseNode(FileTreeRowViewModel row)
        {
            if (row == null || !row.IsDirectory || !row.Node.IsExpanded)
            {
                return;
            }

            string selectedPath = SelectedFile == null ? null : SelectedFile.Node.FullPath;
            row.Node.IsExpanded = false;
            RebuildTreeRows();
            RestoreFileSelection(selectedPath);
        }

        public void OpenNode(FileTreeRowViewModel row)
        {
            if (row == null || row.IsError)
            {
                return;
            }

            if (row.IsDirectory)
            {
                ToggleNode(row);
                return;
            }

            row.Node.IsOpened = true;
            row.Refresh();
            SelectedFile = row;
            _left.RequestOpenFile(row.Node.FullPath, row.Name);
        }

        public void RestoreSessionSelection()
        {
            string id = Model.SelectedSessionId;
            SessionRowViewModel match = null;

            if (!string.IsNullOrEmpty(id))
            {
                match = SessionRows.FirstOrDefault(
                    r => !r.IsHeader && string.Equals(r.Session.Id, id, StringComparison.Ordinal));
            }

            if (match == null)
            {
                match = SessionRows.FirstOrDefault(r => !r.IsHeader);
            }

            SelectedSession = match;
        }

        private void RestoreFileSelection(string path)
        {
            FileTreeRowViewModel match = null;
            if (!string.IsNullOrEmpty(path))
            {
                match = TreeRows.FirstOrDefault(
                    r => string.Equals(r.Node.FullPath, path, StringComparison.OrdinalIgnoreCase));
            }

            SelectedFile = match;
        }

        private void RebuildTreeRows()
        {
            TreeRows.Clear();
            foreach (FileNode node in FileTreePolicy.Flatten(_roots))
            {
                TreeRows.Add(new FileTreeRowViewModel(this, node));
            }

            OnPropertyChanged(nameof(TreeNeedsHorizontalScroll));
        }

        private void SelectSession()
        {
            _left.ActivateWorkspace(this);
        }

        private void ToggleView(object parameter)
        {
            string value = parameter as string;
            WorkspaceView view = string.Equals(value, "Files", StringComparison.OrdinalIgnoreCase)
                ? WorkspaceView.Files
                : WorkspaceView.Sessions;

            SetView(view);
        }

        private void OpenFileFromPicker()
        {
            _left.OpenFileFromPicker(this);
        }
    }
}
