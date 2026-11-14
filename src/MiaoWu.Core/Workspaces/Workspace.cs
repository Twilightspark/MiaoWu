using System;

namespace MiaoWu.Core.Workspaces
{
    /// <summary>工作区内部的当前视图。每个工作区各自记忆。</summary>
    public enum WorkspaceView
    {
        Sessions,
        Files
    }

    /// <summary>
    /// 工作区可恢复的界面状态：展开项、当前视图、滚动位置与选中对象。
    /// 与 <see cref="Workspace"/> 分离，便于持久化与切换工作区时整体替换。
    /// </summary>
    public sealed class WorkspaceViewState
    {
        public WorkspaceViewState()
        {
            View = WorkspaceView.Sessions;
        }

        public bool IsExpanded { get; set; }

        public WorkspaceView View { get; set; }

        public double ScrollOffset { get; set; }

        public string SelectedSessionId { get; set; }

        public string SelectedFilePath { get; set; }

        public WorkspaceViewState Clone()
        {
            return new WorkspaceViewState
            {
                IsExpanded = IsExpanded,
                View = View,
                ScrollOffset = ScrollOffset,
                SelectedSessionId = SelectedSessionId,
                SelectedFilePath = SelectedFilePath
            };
        }
    }

    /// <summary>
    /// 一个工作区：本地文件夹 + 它在左栏中的界面状态。
    /// 只依赖 BCL，不引用 WPF，P3 的会话存储可以直接复用。
    /// </summary>
    public sealed class Workspace
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string Path { get; set; }

        public int Order { get; set; }

        /// <summary>置顶的工作区排在列表最前，并显示图钉。</summary>
        public bool IsPinned { get; set; }

        /// <summary>会话数，显示在工作区行右侧。P2 由会话种子数据提供。</summary>
        public int SessionCount { get; set; }

        public bool IsExpanded { get; set; }

        public WorkspaceView View { get; set; }

        public double ScrollOffset { get; set; }

        public string SelectedSessionId { get; set; }

        public string SelectedFilePath { get; set; }

        public WorkspaceViewState CaptureState()
        {
            return new WorkspaceViewState
            {
                IsExpanded = IsExpanded,
                View = View,
                ScrollOffset = ScrollOffset,
                SelectedSessionId = SelectedSessionId,
                SelectedFilePath = SelectedFilePath
            };
        }

        public void ApplyState(WorkspaceViewState state)
        {
            if (state == null)
            {
                return;
            }

            IsExpanded = state.IsExpanded;
            View = state.View;
            ScrollOffset = state.ScrollOffset;
            SelectedSessionId = state.SelectedSessionId;
            SelectedFilePath = state.SelectedFilePath;
        }

        public Workspace Clone()
        {
            Workspace copy = new Workspace
            {
                Id = Id,
                Name = Name,
                Path = Path,
                Order = Order,
                IsPinned = IsPinned,
                SessionCount = SessionCount
            };

            copy.ApplyState(CaptureState());
            return copy;
        }

        public static Workspace Create(string id, string name, string path, int order)
        {
            return new Workspace
            {
                Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                Name = name,
                Path = path,
                Order = order,
                View = WorkspaceView.Sessions
            };
        }
    }
}
