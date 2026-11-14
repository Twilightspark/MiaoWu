using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.Core.Abstractions
{
    /// <summary>
    /// 左栏会话操作：重命名、置顶、归档、删除与清空。
    /// 与只读的 <see cref="ISessionCatalog"/> 分开，数据目录仍由实现方决定。
    /// </summary>
    public interface ISessionEditor
    {
        /// <summary>修改会话标题；空标题由实现方回退为默认标题。</summary>
        Task RenameAsync(
            Workspace workspace,
            string sessionId,
            string title,
            CancellationToken cancellationToken);

        /// <summary>置顶 / 取消置顶。置顶状态写入会话日志，重启后仍有效。</summary>
        Task SetPinnedAsync(
            Workspace workspace,
            string sessionId,
            bool pinned,
            CancellationToken cancellationToken);

        /// <summary>归档 / 取消归档。归档只改变列表可见性，不删除任何消息。</summary>
        Task SetArchivedAsync(
            Workspace workspace,
            string sessionId,
            bool archived,
            CancellationToken cancellationToken);

        /// <summary>删除单个会话及其 JSONL 文件。</summary>
        Task DeleteAsync(
            Workspace workspace,
            string sessionId,
            CancellationToken cancellationToken);

        /// <summary>清空工作区的全部会话记录；不触碰工作区文件与 .agent 其它目录。</summary>
        Task ClearAsync(Workspace workspace, CancellationToken cancellationToken);
    }
}
