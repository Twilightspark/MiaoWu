using System;

namespace MiaoWu.Core.Sessions
{
    /// <summary>
    /// 左栏会话列表需要的最小会话信息。P3 的完整会话索引会实现同一形状，
    /// 因此 P2 的列表、分组与选中状态可以直接复用。
    /// </summary>
    public sealed class SessionSummary
    {
        public string Id { get; set; }

        public string WorkspaceId { get; set; }

        public string Title { get; set; }

        /// <summary>最后更新时间（本地时间），用于分组与时间显示。</summary>
        public DateTime UpdatedAt { get; set; }

        public bool IsPinned { get; set; }

        /// <summary>已归档的会话默认不出现在最近会话里，可在列表底部展开。</summary>
        public bool IsArchived { get; set; }

        public bool IsUnread { get; set; }

        public bool HasPendingApproval { get; set; }

        public SessionSummary Clone()
        {
            return new SessionSummary
            {
                Id = Id,
                WorkspaceId = WorkspaceId,
                Title = Title,
                UpdatedAt = UpdatedAt,
                IsPinned = IsPinned,
                IsArchived = IsArchived,
                IsUnread = IsUnread,
                HasPendingApproval = HasPendingApproval
            };
        }
    }
}
