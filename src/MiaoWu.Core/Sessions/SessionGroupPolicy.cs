using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MiaoWu.Core.Sessions
{
    /// <summary>会话列表的四个平铺分组。</summary>
    public enum SessionGroup
    {
        Pinned,
        Today,
        Yesterday,
        Earlier
    }

    /// <summary>列表行类型：分组标题与真实会话行平铺进同一个集合，避免关闭虚拟化。</summary>
    public enum SessionListRowKind
    {
        Header,
        Session
    }

    /// <summary>
    /// 会话列表的一行。分组标题也是数据项，因此 ListBox 可以继续使用
    /// <c>VirtualizingStackPanel</c>，而不是 <c>CollectionViewSource</c> 分组。
    /// </summary>
    public sealed class SessionListRow
    {
        private SessionListRow(SessionListRowKind kind, SessionGroup group, SessionSummary session)
        {
            Kind = kind;
            Group = group;
            Session = session;
        }

        public SessionListRowKind Kind { get; }

        public SessionGroup Group { get; }

        public SessionSummary Session { get; }

        public bool IsHeader
        {
            get { return Kind == SessionListRowKind.Header; }
        }

        public static SessionListRow Header(SessionGroup group)
        {
            return new SessionListRow(SessionListRowKind.Header, group, null);
        }

        public static SessionListRow ForSession(SessionGroup group, SessionSummary session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            return new SessionListRow(SessionListRowKind.Session, group, session);
        }
    }

    /// <summary>
    /// 会话分组与排序：
    /// 1. 置顶优先，且不受日期分组影响；
    /// 2. 其余按“今天 / 昨天 / 更早”分组；
    /// 3. 组内按更新时间倒序，时间相同按标题；
    /// 4. 空分组不产生标题行。
    /// </summary>
    public static class SessionGroupPolicy
    {
        private static readonly SessionGroup[] GroupOrder =
        {
            SessionGroup.Pinned,
            SessionGroup.Today,
            SessionGroup.Yesterday,
            SessionGroup.Earlier
        };

        public static SessionGroup GetGroup(SessionSummary session, DateTime now)
        {
            if (session == null || session.IsPinned)
            {
                return SessionGroup.Pinned;
            }

            int days = (now.Date - session.UpdatedAt.Date).Days;
            if (days <= 0)
            {
                return SessionGroup.Today;
            }

            if (days == 1)
            {
                return SessionGroup.Yesterday;
            }

            return SessionGroup.Earlier;
        }

        public static IReadOnlyList<SessionListRow> Build(
            IEnumerable<SessionSummary> sessions,
            DateTime now)
        {
            if (sessions == null)
            {
                return new SessionListRow[0];
            }

            var byGroup = sessions
                .Where(s => s != null)
                .GroupBy(s => GetGroup(s, now))
                .ToDictionary(g => g.Key, g => g.ToList());

            var rows = new List<SessionListRow>();
            foreach (SessionGroup group in GroupOrder)
            {
                List<SessionSummary> items;
                if (!byGroup.TryGetValue(group, out items) || items.Count == 0)
                {
                    continue;
                }

                rows.Add(SessionListRow.Header(group));

                foreach (SessionSummary session in items
                    .OrderByDescending(s => s.UpdatedAt)
                    .ThenBy(s => s.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    rows.Add(SessionListRow.ForSession(group, session));
                }
            }

            return rows;
        }

        /// <summary>组内时间：今天显示 HH:mm，今年显示 MM-dd，跨年显示 yyyy-MM-dd。</summary>
        public static string FormatTime(SessionSummary session, DateTime now)
        {
            if (session == null)
            {
                return string.Empty;
            }

            DateTime updated = session.UpdatedAt;
            if (updated.Date == now.Date)
            {
                return updated.ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            if (updated.Year == now.Year)
            {
                return updated.ToString("MM-dd", CultureInfo.InvariantCulture);
            }

            return updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static int CountInGroup(
            IEnumerable<SessionSummary> sessions,
            SessionGroup group,
            DateTime now)
        {
            if (sessions == null)
            {
                return 0;
            }

            return sessions.Count(s => s != null && GetGroup(s, now) == group);
        }
    }
}
