using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Workspaces;
using MiaoWu.Infrastructure.Conversations;

namespace MiaoWu.Infrastructure.Sessions
{
    /// <summary>
    /// 从工作区 <c>.agent\sessions</c> 读取会话列表。每次取列表都会重新读索引
    /// （索引已缓存且原子写，读取成本低），这样新建/重命名/删除后左栏刷新即可见。
    /// </summary>
    /// <remarks>
    /// 工作区不可写（只读盘、权限不足）时返回空列表而不是抛异常，
    /// 让左栏能显示“暂无会话”；错误由 P3 的内联错误条承担。
    /// </remarks>
    public sealed class WorkspaceConversationCatalog : ISessionCatalog, ISessionEditor
    {
        private readonly ConcurrentDictionary<string, WorkspaceConversationStore> _stores =
            new ConcurrentDictionary<string, WorkspaceConversationStore>(StringComparer.OrdinalIgnoreCase);

        private readonly IClock _clock;

        public WorkspaceConversationCatalog()
            : this(SystemClock.Instance)
        {
        }

        public WorkspaceConversationCatalog(IClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public IReadOnlyList<SessionSummary> GetSessions(Workspace workspace)
        {
            string path = AgentWorkspaceLayout.GetWorkspacePath(workspace);
            if (path == null)
            {
                return new SessionSummary[0];
            }

            try
            {
                WorkspaceConversationStore store = GetStore(workspace, path);
                ConversationIndex index = store
                    .LoadIndexAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                var sessions = new List<SessionSummary>(index.Sessions.Count);
                foreach (ConversationIndexEntry entry in index.Sessions)
                {
                    sessions.Add(ToSummary(entry));
                }

                return sessions;
            }
            catch (Exception exception)
            {
                if (IsRecoverable(exception))
                {
                    return new SessionSummary[0];
                }

                throw;
            }
        }

        /// <summary>取得（或创建）某个工作区的会话存储，供发送/恢复复用。</summary>
        public WorkspaceConversationStore GetStore(Workspace workspace)
        {
            string path = AgentWorkspaceLayout.GetWorkspacePath(workspace);
            if (path == null)
            {
                throw new ArgumentException("工作区没有有效路径。", nameof(workspace));
            }

            return GetStore(workspace, path);
        }

        public Task RenameAsync(
            Workspace workspace,
            string sessionId,
            string title,
            CancellationToken cancellationToken)
        {
            return GetStore(workspace).RenameAsync(sessionId, title, cancellationToken);
        }

        public Task SetPinnedAsync(
            Workspace workspace,
            string sessionId,
            bool pinned,
            CancellationToken cancellationToken)
        {
            return GetStore(workspace).SetPinnedAsync(sessionId, pinned, cancellationToken);
        }

        public Task SetArchivedAsync(
            Workspace workspace,
            string sessionId,
            bool archived,
            CancellationToken cancellationToken)
        {
            return GetStore(workspace).SetArchivedAsync(sessionId, archived, cancellationToken);
        }

        public Task DeleteAsync(
            Workspace workspace,
            string sessionId,
            CancellationToken cancellationToken)
        {
            return GetStore(workspace).DeleteAsync(sessionId, cancellationToken);
        }

        public Task ClearAsync(Workspace workspace, CancellationToken cancellationToken)
        {
            return GetStore(workspace).ClearAsync(cancellationToken);
        }

        private WorkspaceConversationStore GetStore(Workspace workspace, string path)
        {
            string key = System.IO.Path.GetFullPath(path);
            return _stores.GetOrAdd(
                key,
                _ => new WorkspaceConversationStore(workspace.Id, path, _clock));
        }

        private static SessionSummary ToSummary(ConversationIndexEntry entry)
        {
            return new SessionSummary
            {
                Id = entry.SessionId,
                WorkspaceId = entry.WorkspaceId,
                Title = string.IsNullOrWhiteSpace(entry.Title)
                    ? WorkspaceConversationStore.DefaultSessionTitle
                    : entry.Title,
                UpdatedAt = entry.UpdatedAt.ToLocalTime().DateTime,
                IsPinned = entry.IsPinned,
                IsArchived = entry.IsArchived,
                IsUnread = false,
                HasPendingApproval = false
            };
        }

        private static bool IsRecoverable(Exception exception)
        {
            return exception is System.IO.IOException
                || exception is UnauthorizedAccessException
                || exception is System.Security.SecurityException
                || exception is ArgumentException
                || exception is NotSupportedException;
        }
    }
}
