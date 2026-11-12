using System;
using System.Collections.Generic;
using System.Globalization;

namespace MiaoWu.Core.Sessions
{
    /// <summary>
    /// P2 的会话种子数据：在真实会话库（P3）到位前，让左栏分组、密度与虚拟化
    /// 能按最终形态验收。同一工作区 + 同一时间的输出完全确定，便于截图对比。
    /// </summary>
    public static class SessionSeedFactory
    {
        private static readonly string[] Titles =
        {
            "旧机报告整理",
            "三栏工作台布局",
            "文件树懒加载",
            "Markdown 渲染子集",
            "编码识别与 GBK",
            "Win7 兼容排查",
            "审批通知时序",
            "diff 备份策略",
            "工具卡与消息流",
            "设置浮层结构",
            "首屏性能打点",
            "工作区状态恢复",
            "会话存储选型",
            "路径沙箱边界",
            "命令输出编码",
            "输入法回车行为"
        };

        /// <summary>按“置顶 / 今天 / 昨天 / 更早”的固定比例生成 count 条会话。</summary>
        public static IReadOnlyList<SessionSummary> Create(string workspaceId, int count, DateTime now)
        {
            if (count <= 0)
            {
                return new SessionSummary[0];
            }

            int pinned = count >= 8 ? 2 : (count >= 3 ? 1 : 0);
            int remaining = count - pinned;
            int today = remaining == 0 ? 0 : Math.Max(1, (int)Math.Round(remaining * 0.4));
            int yesterday = remaining <= 1 ? 0 : Math.Max(1, (int)Math.Round(remaining * 0.3));
            int earlier = remaining - today - yesterday;

            if (earlier < 0)
            {
                today = Math.Max(0, today + earlier);
                earlier = 0;
            }

            var sessions = new List<SessionSummary>(count);
            int index = 0;

            for (int i = 0; i < pinned; i++)
            {
                sessions.Add(CreateSession(
                    workspaceId,
                    index,
                    now.AddDays(-(3 + i)).AddHours(-2),
                    pinned: true,
                    unread: false,
                    pendingApproval: i == 0 && count >= 8));
                index++;
            }

            for (int i = 0; i < today; i++)
            {
                sessions.Add(CreateSession(
                    workspaceId,
                    index,
                    now.AddMinutes(-(12 + i * 17)),
                    pinned: false,
                    unread: i % 5 == 3,
                    pendingApproval: false));
                index++;
            }

            for (int i = 0; i < yesterday; i++)
            {
                sessions.Add(CreateSession(
                    workspaceId,
                    index,
                    now.AddDays(-1).AddMinutes(-(8 + i * 23)),
                    pinned: false,
                    unread: i % 7 == 2,
                    pendingApproval: false));
                index++;
            }

            for (int i = 0; i < earlier; i++)
            {
                sessions.Add(CreateSession(
                    workspaceId,
                    index,
                    now.AddDays(-(2 + i)).AddHours(-1),
                    pinned: false,
                    unread: false,
                    pendingApproval: false));
                index++;
            }

            return sessions;
        }

        private static SessionSummary CreateSession(
            string workspaceId,
            int index,
            DateTime updatedAt,
            bool pinned,
            bool unread,
            bool pendingApproval)
        {
            string title = Titles[index % Titles.Length];
            if (index >= Titles.Length)
            {
                title = title + " · " + (index / Titles.Length + 1).ToString(CultureInfo.InvariantCulture);
            }

            return new SessionSummary
            {
                Id = string.Format(
                    CultureInfo.InvariantCulture,
                    "seed-{0}-{1:000}",
                    workspaceId,
                    index),
                WorkspaceId = workspaceId,
                Title = title,
                UpdatedAt = updatedAt,
                IsPinned = pinned,
                IsUnread = unread,
                HasPendingApproval = pendingApproval
            };
        }
    }
}
