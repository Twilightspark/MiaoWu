using System;
using System.Windows;
using System.Windows.Input;
using MiaoWu.App.Mvvm;
using MiaoWu.App.Resources;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.App.ViewModels
{
    /// <summary>会话列表的一行：分组标题或会话。标题行不参与选中。</summary>
    public sealed class SessionRowViewModel : ObservableObject
    {
        private readonly DateTime _now;

        public SessionRowViewModel(SessionListRow row, string headerText, DateTime now)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            _now = now;
            IsHeader = row.IsHeader;
            HeaderText = headerText ?? string.Empty;
            Session = row.Session;
            Group = row.Group;
        }

        public bool IsHeader { get; }

        public SessionGroup Group { get; }

        public SessionSummary Session { get; }

        public string HeaderText { get; }

        public bool IsPinned
        {
            get { return Session != null && Session.IsPinned; }
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

        public void Refresh()
        {
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(IsUnread));
            OnPropertyChanged(nameof(HasPendingApproval));
            OnPropertyChanged(nameof(TimeText));
        }
    }

    /// <summary>文件树的一行。缩进与最小宽度来自 <see cref="FileTreePolicy"/>。</summary>
    public sealed class FileTreeRowViewModel : ObservableObject
    {
        private readonly WorkspaceItemViewModel _owner;

        public FileTreeRowViewModel(WorkspaceItemViewModel owner, FileNode node)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Node = node ?? throw new ArgumentNullException(nameof(node));
            Indent = new Thickness(FileTreePolicy.GetIndent(node.Depth), 0, 0, 0);
            ToggleCommand = new RelayCommand(Toggle);
            OpenCommand = new RelayCommand(Open);
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

        public RelayCommand ToggleCommand { get; }

        public RelayCommand OpenCommand { get; }

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
        }

        private void Toggle()
        {
            _owner.ToggleNode(this);
        }

        private void Open()
        {
            _owner.OpenNode(this);
        }
    }
}
