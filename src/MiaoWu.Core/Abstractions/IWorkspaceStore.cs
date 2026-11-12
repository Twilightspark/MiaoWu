using System.Collections.Generic;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.Core.Abstractions
{
    /// <summary>工作区列表的持久化契约（workspaces.json）。</summary>
    public interface IWorkspaceStore
    {
        WorkspaceCollection Load();

        void Save(WorkspaceCollection collection);
    }

    /// <summary>工作区集合 + 当前展开项。读不出来时返回空集合，不抛给启动流程。</summary>
    public sealed class WorkspaceCollection
    {
        public WorkspaceCollection()
        {
            SchemaVersion = 1;
            Workspaces = new List<Workspace>();
        }

        public int SchemaVersion { get; set; }

        public string ActiveWorkspaceId { get; set; }

        public IList<Workspace> Workspaces { get; set; }
    }
}
