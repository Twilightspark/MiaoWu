using System.Collections.Generic;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.Core.Abstractions
{
    /// <summary>
    /// 会话索引。P2 由种子数据提供，P3 起由工作区 <c>.miaowu\sessions</c> 提供；
    /// 左栏只依赖这个接口，不感知存储形态。传入工作区（而非仅 ID）是因为
    /// 会话库按工作区目录寻址。
    /// </summary>
    public interface ISessionCatalog
    {
        IReadOnlyList<SessionSummary> GetSessions(Workspace workspace);
    }
}
