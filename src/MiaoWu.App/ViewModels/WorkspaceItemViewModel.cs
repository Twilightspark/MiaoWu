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
        private bool _showAllSessions;
        private bool _showArchivedSessions;
        private bool _hasMoreSessions;
        private bool _hasArchivedSessions;
        private int _archivedCount;
        private bool _isRenaming;
        private string _renameDraft;

        /// <summary>默认只展示最近几条会话，避免左栏过长；展开后走外层滚动。</summary>
        public const int RecentSessionLimit = 6;

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
            ToggleExpandedCommand = new RelayCommand(ToggleExpanded);
            ToggleViewCommand = new RelayCommand(ToggleView);
            SelectSessionCommand = new RelayCommand(SelectSession);
            OpenFileCommand = new RelayCommand(OpenFileFromPicker);
            ToggleAllSessionsCommand = new RelayCommand(ToggleAllSessions);
            ToggleArchivedSessionsCommand = new RelayCommand(ToggleArchivedSessions);
            TogglePinCommand = new RelayCommand(TogglePin);
            BeginRenameCommand = new RelayCommand(BeginRename);
            CommitRenameCommand = new RelayCommand(CommitRename, () => _isRenaming);
            CancelRenameCommand = new RelayCommand(CancelRename, () => _isRenaming);
            ClearSessionsCommand = new RelayCommand(() => _left.RequestClearSessions(this));
            NewSessionCommand = new RelayCommand(() => _left.NewSessionFor(this));
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

        public bool IsPinned
        {
            get { return Model.IsPinned; }
        }

        public string PinActionText
        {
            get { return Model.IsPinned ? Strings.UnpinWorkspace : Strings.PinWorkspace; }
        }

        public bool IsRenaming
        {
            get { return _isRenaming; }
            private set
            {
                if (_isRenaming == value)
                {
                    return;
                }

                _isRenaming = value;
                OnPropertyChanged();
            }
        }

        public string RenameDraft
        {
            get { return _renameDraft; }
            set { SetProperty(ref _renameDraft, value); }
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

        /// <summary>会话是否全部展开（默认只显示最近 <see cref="RecentSessionLimit"/> 条）。</summary>
        public bool ShowAllSessions
        {
            get { return _showAllSessions; }
            private set
            {
                if (_showAllSessions == value)
                {
                    return;
                }

                _showAllSessions = value;
                ApplySessionVisibility();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SessionMoreText));
            }
        }

        public bool HasMoreSessions
        {
            get { return _hasMoreSessions; }
        }

        public bool ShowSessionMore
        {
            get { return IsExpanded && IsSessionsView && _hasMoreSessions; }
        }

        public string SessionMoreText
        {
            get
            {
                int visible = Model.SessionCount + (_showArchivedSessions ? _archivedCount : 0);
                return _showAllSessions
                    ? "收起，只看最近 " + RecentSessionLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 条"
                    : "查看全部 " + visible.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 条会话";
            }
        }

        /// <summary>有归档会话时显示“查看已归档 / 收起已归档”入口。</summary>
        public bool ShowArchivedToggle
        {
            get { return IsExpanded && IsSessionsView && _hasArchivedSessions; }
        }

        public string ArchivedToggleText
        {
            get
            {
                string count = _archivedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return _showArchivedSessions ? "收起已归档会话" : "查看已归档 " + count + " 条";
            }
        }

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
                ApplySessionVisibility();
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

        public RelayCommand ActivateCommand { get; }

        /// <summary>单击工作区行：展开或收起（手风琴单选）。</summary>
        public RelayCommand ToggleExpandedCommand { get; }

        public RelayCommand ToggleViewCommand { get; }

        public RelayCommand SelectSessionCommand { get; }

        public RelayCommand OpenFileCommand { get; }

        public RelayCommand ToggleAllSessionsCommand { get; }

        public RelayCommand ToggleArchivedSessionsCommand { get; }

        public RelayCommand TogglePinCommand { get; }

        public RelayCommand BeginRenameCommand { get; }

        public RelayCommand CommitRenameCommand { get; }

        public RelayCommand CancelRenameCommand { get; }

        public RelayCommand ClearSessionsCommand { get; }

        public RelayCommand NewSessionCommand { get; }

        public RelayCommand RemoveCommand { get; }

        public string AutomationName
        {
            get { return Name + " 工作区"; }
        }

        public void BeginRename()
        {
            RenameDraft = Name;
            IsRenaming = true;
        }

        /// <summary>工作区内联重命名：提交后只改显示名，不动磁盘文件夹。</summary>
        public void CommitRename()
        {
            if (!IsRenaming)
            {
                return;
            }

            IsRenaming = false;
            string name = (_renameDraft ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                _left.ReportError(Strings.WorkspaceNameEmptyError);
                return;
            }

            if (string.Equals(name, Model.Name, StringComparison.Ordinal))
            {
                return;
            }

            Model.Name = name;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(AutomationName));
            _left.ReportError(null);
            _left.SortWorkspaceItems();
            _left.NotifyWorkspaceStateChanged();
        }

        public void CancelRename()
        {
            IsRenaming = false;
        }

        public void TogglePin()
        {
            Model.IsPinned = !Model.IsPinned;
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(PinActionText));
            _left.SortWorkspaceItems();
            _left.NotifyWorkspaceStateChanged();
        }

        /// <summary>由会话行调用：置顶 / 取消置顶，并保持当前选中会话。</summary>
        public void ToggleSessionPin(SessionRowViewModel row)
        {
            if (row == null || row.Session == null)
            {
                return;
            }

            _left.SetSessionPinned(this, row.Session.Id, !row.Session.IsPinned);
        }

        /// <summary>由会话行调用：归档 / 取消归档。</summary>
        public void ToggleSessionArchive(SessionRowViewModel row)
        {
            if (row == null || row.Session == null)
            {
                return;
            }

            _left.SetSessionArchived(this, row.Session.Id, !row.Session.IsArchived);
        }

        public void CommitSessionRename(SessionRowViewModel row)
        {
            if (row == null || row.Session == null)
            {
                return;
            }

            string title = (row.RenameDraft ?? string.Empty).Trim();
            if (title.Length == 0)
            {
                _left.ReportError(Strings.SessionTitleEmptyError);
                return;
            }

            _left.RenameSession(this, row.Session.Id, title);
        }

        public void RequestDeleteSession(SessionRowViewModel row)
        {
            if (row == null || row.Session == null)
            {
                return;
            }

            _left.RequestDeleteSession(this, row.Session.Id, row.Title);
        }

        /// <summary>由会话行调用：把会话导出为 Markdown / JSON。</summary>
        public void ExportSession(SessionRowViewModel row)
        {
            if (row == null || row.Session == null)
            {
                return;
            }

            _left.RequestExportSession(this, row.Session.Id, row.Title);
        }

        /// <summary>由文件行调用：重命名成功后同步节点、选中项与中栏标签。</summary>
        public void CommitFileRename(FileTreeRowViewModel row)
        {
            if (row == null || !row.CanManage)
            {
                return;
            }

            string newName = (row.RenameDraft ?? string.Empty).Trim();
            string oldPath = row.Node.FullPath;

            try
            {
                string newPath = _left.RenameFile(oldPath, newName);
                row.Node.Rename(System.IO.Path.GetFileName(newPath), newPath);
                RebuildTreeRows();
                RestoreFileSelection(newPath);
                _left.ApplyOpenedState(this);
                RefreshViewState();
            }
            catch (Exception exception)
            {
                _left.ReportError(exception.Message);
            }
        }

        public void RequestDeleteFile(FileTreeRowViewModel row)
        {
            if (row == null || !row.CanManage)
            {
                return;
            }

            if (!_left.ConfirmFileDelete(row.Name))
            {
                return;
            }

            try
            {
                _left.DeleteFile(row.Node.FullPath);
                RemoveNode(row.Node);
                RebuildTreeRows();
                SelectedFile = null;
                RefreshViewState();
            }
            catch (Exception exception)
            {
                _left.ReportError(exception.Message);
            }
        }

        /// <summary>不构建行，只取数量，供收起的工作区显示右侧数字。</summary>
        public void LoadSessionCount()
        {
            // 右侧数字只算未归档的活跃会话；归档会话在展开后单独查看。
            int count = 0;
            foreach (SessionSummary session in _sessionCatalog.GetSessions(Model))
            {
                if (session != null && !session.IsArchived)
                {
                    count++;
                }
            }

            Model.SessionCount = count;
            OnPropertyChanged(nameof(SessionCountText));
        }

        public void Activate()
        {
            _left.ActivateWorkspace(this);
        }

        public void ToggleExpanded()
        {
            _left.ToggleWorkspace(this);
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
            IReadOnlyList<SessionSummary> all = _sessionCatalog.GetSessions(Model);
            int activeCount = 0;
            int archivedCount = 0;
            var visible = new List<SessionSummary>(all.Count);

            foreach (SessionSummary session in all)
            {
                if (session == null)
                {
                    continue;
                }

                if (session.IsArchived)
                {
                    archivedCount++;
                    if (!_showArchivedSessions)
                    {
                        continue;
                    }
                }
                else
                {
                    activeCount++;
                }

                visible.Add(session);
            }

            Model.SessionCount = activeCount;
            _archivedCount = archivedCount;
            _hasArchivedSessions = archivedCount > 0;

            SessionRows.Clear();
            IReadOnlyList<SessionListRow> rows = SessionGroupPolicy.Build(visible, _left.Now);
            foreach (SessionListRow row in rows)
            {
                SessionRows.Add(new SessionRowViewModel(
                    this,
                    row,
                    LeftPaneViewModel.GetGroupHeaderText(row.Group),
                    _left.Now));
            }

            _sessionsLoaded = true;
            OnPropertyChanged(nameof(SessionCountText));
            OnPropertyChanged(nameof(ShowArchivedToggle));
            OnPropertyChanged(nameof(ArchivedToggleText));

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

            ApplySessionVisibility();
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
            OnPropertyChanged(nameof(ShowSessionMore));
            RefreshViewState();
        }

        /// <summary>切换“只看最近几条 / 查看全部”。</summary>
        public void ToggleAllSessions()
        {
            ShowAllSessions = !ShowAllSessions;
        }

        /// <summary>展开 / 收起已归档会话；归档只影响可见性。</summary>
        public void ToggleArchivedSessions()
        {
            _showArchivedSessions = !_showArchivedSessions;
            OnPropertyChanged(nameof(ArchivedToggleText));
            RefreshSessions();
        }

        /// <summary>
        /// 默认只显示最近 <see cref="RecentSessionLimit"/> 条会话；分组标题只在该组还有可见行时保留。
        /// 当前选中的会话始终可见，避免外层滚动下丢失会话上下文。
        /// </summary>
        private void ApplySessionVisibility()
        {
            int seen = 0;
            SessionRowViewModel header = null;
            bool headerHasVisible = false;

            foreach (SessionRowViewModel row in SessionRows)
            {
                if (row.IsHeader)
                {
                    if (header != null)
                    {
                        header.IsHidden = !headerHasVisible;
                    }

                    header = row;
                    headerHasVisible = false;
                    continue;
                }

                seen++;
                // 归档组只要展开就完整显示，不受“最近 6 条”影响。
                bool archivedRow = row.Group == SessionGroup.Archived;
                row.IsHidden = !ShowAllSessions && !archivedRow && seen > RecentSessionLimit;
                headerHasVisible |= !row.IsHidden;
            }

            if (header != null)
            {
                header.IsHidden = !headerHasVisible;
            }

            SessionRowViewModel selected = _selectedSession;
            if (!ShowAllSessions && selected != null && selected.IsHidden)
            {
                SessionRowViewModel selectedHeader = null;
                SessionRowViewModel lastHeader = null;
                foreach (SessionRowViewModel row in SessionRows)
                {
                    if (row.IsHeader)
                    {
                        lastHeader = row;
                    }
                    else if (ReferenceEquals(row, selected))
                    {
                        selectedHeader = lastHeader;
                        break;
                    }
                }

                selected.IsHidden = false;
                if (selectedHeader != null)
                {
                    selectedHeader.IsHidden = false;
                }
            }

            int visibleCount = SessionRows.Count(
                r => !r.IsHeader && r.Group != SessionGroup.Archived);
            bool hasMore = visibleCount > RecentSessionLimit;
            if (hasMore != _hasMoreSessions)
            {
                _hasMoreSessions = hasMore;
                OnPropertyChanged(nameof(HasMoreSessions));
                OnPropertyChanged(nameof(ShowSessionMore));
            }

            OnPropertyChanged(nameof(SessionMoreText));
        }

        public void RefreshViewState()
        {
            OnPropertyChanged(nameof(IsSessionsView));
            OnPropertyChanged(nameof(IsFilesView));
            OnPropertyChanged(nameof(ShowSessionList));
            OnPropertyChanged(nameof(ShowFileTree));
            OnPropertyChanged(nameof(ShowSessionEmpty));
            OnPropertyChanged(nameof(ShowFileEmpty));
            OnPropertyChanged(nameof(ShowSessionMore));
            OnPropertyChanged(nameof(ShowArchivedToggle));
            OnPropertyChanged(nameof(ArchivedToggleText));
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

        /// <summary>从树中移除已删除的文件节点；只处理文件，不会递归删除目录。</summary>
        private void RemoveNode(FileNode node)
        {
            if (node == null)
            {
                return;
            }

            if (_roots.Remove(node))
            {
                return;
            }

            foreach (FileNode root in _roots)
            {
                if (RemoveNodeFrom(root, node))
                {
                    return;
                }
            }
        }

        private static bool RemoveNodeFrom(FileNode parent, FileNode node)
        {
            if (parent == null || !parent.IsExpanded || parent.Children.Count == 0)
            {
                return false;
            }

            if (parent.Children.Remove(node))
            {
                return true;
            }

            foreach (FileNode child in parent.Children)
            {
                if (RemoveNodeFrom(child, node))
                {
                    return true;
                }
            }

            return false;
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
