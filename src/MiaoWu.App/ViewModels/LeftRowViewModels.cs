using System;
using System.Windows;
using System.Windows.Input;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.App.ViewModels
{
    /// <summary>行内重命名的公共形状，供视图在失焦时统一提交。</summary>
    public interface IInlineRenameRow
    {
        bool IsRenaming { get; }

        string RenameDraft { get; set; }

        void CommitRename();

        void CancelRename();
    }

    /// <summary>会话列表的一行：分组标题或会话。标题行不参与选中。</summary>
    public sealed class SessionRowViewModel : ObservableObject, IInlineRenameRow
    {
        private readonly WorkspaceItemViewModel _owner;
        private readonly DateTime _now;
        private bool _isHidden;
        private bool _isRenaming;
        private string _renameDraft;

        public SessionRowViewModel(
            WorkspaceItemViewModel owner,
            SessionListRow row,
            string headerText,
            DateTime now)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            _now = now;
            IsHeader = row.IsHeader;
            HeaderText = headerText ?? string.Empty;
            Session = row.Session;
            Group = row.Group;

            BeginRenameCommand = new RelayCommand(BeginRename, () => !IsHeader);
            CommitRenameCommand = new RelayCommand(CommitRename, () => _isRenaming);
            CancelRenameCommand = new RelayCommand(CancelRename, () => _isRenaming);
            TogglePinCommand = new RelayCommand(TogglePin, () => !IsHeader);
            ToggleArchiveCommand = new RelayCommand(ToggleArchive, () => !IsHeader);
            ExportCommand = new RelayCommand(Export, () => !IsHeader);
            DeleteCommand = new RelayCommand(Delete, () => !IsHeader);
        }

        public bool IsHeader { get; }

        /// <summary>超出“最近几条”时折叠该行；保留显示集合以免丢失选中项。</summary>
        public bool IsHidden
        {
            get { return _isHidden; }
            set
            {
                if (_isHidden == value)
                {
                    return;
                }

                _isHidden = value;
                OnPropertyChanged();
            }
        }

        public SessionGroup Group { get; }

        public SessionSummary Session { get; }

        public string HeaderText { get; }

        public bool IsPinned
        {
            get { return Session != null && Session.IsPinned; }
        }

        public bool IsArchived
        {
            get { return Session != null && Session.IsArchived; }
        }

        public bool IsUnread
        {
            get { return Session != null && Session.IsUnread; }
        }

        public bool HasPendingApproval
        {
            get { return Session != null && Session.HasPendingApproval; }
        }

        public string Title
        {
            get { return Session == null ? string.Empty : Session.Title; }
        }

        public string TimeText
        {
            get { return Session == null ? string.Empty : SessionGroupPolicy.FormatTime(Session, _now); }
        }

        /// <summary>菜单文案随当前状态切换：置顶/取消置顶、归档/取消归档。</summary>
        public string PinActionText
        {
            get { return IsPinned ? Strings.UnpinSession : Strings.PinSession; }
        }

        public string ArchiveActionText
        {
            get { return IsArchived ? Strings.UnarchiveSession : Strings.ArchiveSession; }
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

        public string ToolTip
        {
            get
            {
                if (Session == null)
                {
                    return HeaderText;
                }

                return TimeText.Length == 0 ? Title : Title + " · " + TimeText;
            }
        }

        public string AutomationName
        {
            get { return IsHeader ? HeaderText : Title; }
        }

        public RelayCommand BeginRenameCommand { get; }

        public RelayCommand CommitRenameCommand { get; }

        public RelayCommand CancelRenameCommand { get; }

        public RelayCommand TogglePinCommand { get; }

        public RelayCommand ToggleArchiveCommand { get; }

        public RelayCommand ExportCommand { get; }

        public RelayCommand DeleteCommand { get; }

        public void Refresh()
        {
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(IsArchived));
            OnPropertyChanged(nameof(IsUnread));
            OnPropertyChanged(nameof(HasPendingApproval));
            OnPropertyChanged(nameof(TimeText));
            OnPropertyChanged(nameof(PinActionText));
            OnPropertyChanged(nameof(ArchiveActionText));
        }

        public void BeginRename()
        {
            if (IsHeader || Session == null)
            {
                return;
            }

            RenameDraft = Title;
            IsRenaming = true;
        }

        public void CommitRename()
        {
            if (!IsRenaming)
            {
                return;
            }

            IsRenaming = false;
            _owner.CommitSessionRename(this);
        }

        public void CancelRename()
        {
            IsRenaming = false;
        }

        private void TogglePin()
        {
            if (!IsHeader)
            {
                _owner.ToggleSessionPin(this);
            }
        }

        private void ToggleArchive()
        {
            if (!IsHeader)
            {
                _owner.ToggleSessionArchive(this);
            }
        }

        private void Export()
        {
            if (!IsHeader)
            {
                _owner.ExportSession(this);
            }
        }

        private void Delete()
        {
            if (!IsHeader)
            {
                _owner.RequestDeleteSession(this);
            }
        }
    }

    /// <summary>文件树的一行。缩进与最小宽度来自 <see cref="FileTreePolicy"/>。</summary>
    public sealed class FileTreeRowViewModel : ObservableObject, IInlineRenameRow
    {
        private readonly WorkspaceItemViewModel _owner;
        private bool _isRenaming;
        private string _renameDraft;

        public FileTreeRowViewModel(WorkspaceItemViewModel owner, FileNode node)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Node = node ?? throw new ArgumentNullException(nameof(node));
            Indent = new Thickness(FileTreePolicy.GetIndent(node.Depth), 0, 0, 0);
            ToggleCommand = new RelayCommand(Toggle);
            OpenCommand = new RelayCommand(Open);
            BeginRenameCommand = new RelayCommand(BeginRename, () => CanManage);
            CommitRenameCommand = new RelayCommand(CommitRename, () => _isRenaming);
            CancelRenameCommand = new RelayCommand(CancelRename, () => _isRenaming);
            DeleteCommand = new RelayCommand(Delete, () => CanManage);
        }

        public FileNode Node { get; }

        public string Name
        {
            get { return Node.Name; }
        }

        public string ToolTip
        {
            get { return Node.FullPath; }
        }

        public int Depth
        {
            get { return Node.Depth; }
        }

        public Thickness Indent { get; }

        public double MinRowWidth
        {
            get { return FileTreePolicy.GetContentWidth(Node.Depth); }
        }

        /// <summary>
        /// 8 层以内让行宽跟随可视区（长名截断），超过 8 层固定为内容宽度以启用横向滚动。
        /// </summary>
        public double RowWidth
        {
            get { return Node.Depth > 8 ? MinRowWidth : double.NaN; }
        }

        public bool IsDirectory
        {
            get { return Node.IsDirectory; }
        }

        /// <summary>只有普通文件支持重命名 / 删除，目录与错误行不显示更多按钮。</summary>
        public bool CanManage
        {
            get { return !Node.IsDirectory && !Node.IsReparsePoint && !Node.IsErrorRow; }
        }

        public bool IsExpanded
        {
            get { return Node.IsExpanded; }
        }

        public bool CanExpand
        {
            get { return Node.CanExpand; }
        }

        public bool IsReparsePoint
        {
            get { return Node.IsReparsePoint; }
        }

        public bool IsError
        {
            get { return Node.IsErrorRow; }
        }

        public bool IsOpened
        {
            get { return Node.IsOpened; }
        }

        public bool IsLoaded
        {
            get { return Node.State == FileNodeState.Loaded; }
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

        public RelayCommand ToggleCommand { get; }

        public RelayCommand OpenCommand { get; }

        public RelayCommand BeginRenameCommand { get; }

        public RelayCommand CommitRenameCommand { get; }

        public RelayCommand CancelRenameCommand { get; }

        public RelayCommand DeleteCommand { get; }

        public string AutomationName
        {
            get
            {
                if (IsError)
                {
                    return Name;
                }

                return IsDirectory ? Name + " 文件夹" : Name + " 文件";
            }
        }

        public void Refresh()
        {
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(IsOpened));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(ToolTip));
            OnPropertyChanged(nameof(AutomationName));
            OnPropertyChanged(nameof(CanManage));
        }

        public void BeginRename()
        {
            if (!CanManage)
            {
                return;
            }

            RenameDraft = Name;
            IsRenaming = true;
        }

        public void CommitRename()
        {
            if (!IsRenaming)
            {
                return;
            }

            IsRenaming = false;
            _owner.CommitFileRename(this);
        }

        public void CancelRename()
        {
            IsRenaming = false;
        }

        private void Toggle()
        {
            _owner.ToggleNode(this);
        }

        private void Open()
        {
            _owner.OpenNode(this);
        }

        private void Delete()
        {
            if (CanManage)
            {
                _owner.RequestDeleteFile(this);
            }
        }
    }
}
