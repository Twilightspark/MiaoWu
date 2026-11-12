using System;
using System.Collections.Generic;
using System.Linq;
using MiaoWu.Core.Sessions;
using Xunit;

namespace MiaoWu.Tests.Sessions
{
    public sealed class SessionGroupPolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 11, 16, 0, 0, DateTimeKind.Local);

        [Fact]
        public void BuildCreatesHeaderThenItemsInGroupOrder()
        {
            var sessions = new List<SessionSummary>
            {
                Create("today-1", Now.AddHours(-2)),
                Create("pinned-old", new DateTime(2026, 10, 1, 9, 0, 0), pinned: true),
                Create("yesterday-1", Now.AddDays(-1)),
                Create("earlier-1", Now.AddDays(-30)),
                Create("today-2", Now.AddMinutes(-10))
            };

            IReadOnlyList<SessionListRow> rows = SessionGroupPolicy.Build(sessions, Now);

            Assert.Equal(4, rows.Count(r => r.IsHeader));
            Assert.Equal(
                new[] { SessionGroup.Pinned, SessionGroup.Today, SessionGroup.Yesterday, SessionGroup.Earlier },
                rows.Where(r => r.IsHeader).Select(r => r.Group).ToArray());

            string[] ids = rows.Where(r => !r.IsHeader).Select(r => r.Session.Id).ToArray();
            Assert.Equal(new[] { "pinned-old", "today-2", "today-1", "yesterday-1", "earlier-1" }, ids);
        }

        [Fact]
        public void EmptyGroupsDoNotCreateHeaders()
        {
            var sessions = new List<SessionSummary>
            {
                Create("today", Now.AddHours(-1))
            };

            IReadOnlyList<SessionListRow> rows = SessionGroupPolicy.Build(sessions, Now);

            Assert.Equal(2, rows.Count);
            Assert.True(rows[0].IsHeader);
            Assert.Equal(SessionGroup.Today, rows[0].Group);
            Assert.Equal("today", rows[1].Session.Id);
        }

        [Fact]
        public void BuildHandlesNullAndEmpty()
        {
            Assert.Empty(SessionGroupPolicy.Build(null, Now));
            Assert.Empty(SessionGroupPolicy.Build(new SessionSummary[0], Now));
        }

        [Fact]
        public void PinnedWinsRegardlessOfAge()
        {
            SessionSummary pinned = Create("pinned", new DateTime(2020, 1, 1), pinned: true);

            Assert.Equal(SessionGroup.Pinned, SessionGroupPolicy.GetGroup(pinned, Now));
        }

        [Fact]
        public void FutureTimestampIsTreatedAsToday()
        {
            SessionSummary future = Create("future", Now.AddDays(2));

            Assert.Equal(SessionGroup.Today, SessionGroupPolicy.GetGroup(future, Now));
        }

        [Theory]
        [InlineData(0, SessionGroup.Today)]
        [InlineData(1, SessionGroup.Yesterday)]
        [InlineData(2, SessionGroup.Earlier)]
        [InlineData(365, SessionGroup.Earlier)]
        public void GroupFollowsCalendarDayDistance(int daysAgo, SessionGroup expected)
        {
            SessionSummary session = Create("s", Now.Date.AddDays(-daysAgo).AddHours(9));

            Assert.Equal(expected, SessionGroupPolicy.GetGroup(session, Now));
        }

        [Fact]
        public void FormatTimeUsesClockForToday()
        {
            SessionSummary session = Create("s", new DateTime(2026, 11, 11, 10, 38, 0));

            Assert.Equal("10:38", SessionGroupPolicy.FormatTime(session, Now));
        }

        [Fact]
        public void FormatTimeUsesMonthDayForSameYear()
        {
            SessionSummary session = Create("s", new DateTime(2026, 11, 10, 23, 59, 0));

            Assert.Equal("11-10", SessionGroupPolicy.FormatTime(session, Now));
        }

        [Fact]
        public void FormatTimeUsesFullDateForOtherYear()
        {
            SessionSummary session = Create("s", new DateTime(2025, 12, 31, 8, 5, 0));

            Assert.Equal("2025-12-31", SessionGroupPolicy.FormatTime(session, Now));
        }

        [Fact]
        public void CountInGroupMatchesBuild()
        {
            var sessions = new List<SessionSummary>
            {
                Create("a", Now.AddMinutes(-1)),
                Create("b", Now.AddDays(-1)),
                Create("c", Now.AddDays(-3)),
                Create("d", Now, pinned: true)
            };

            Assert.Equal(1, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Pinned, Now));
            Assert.Equal(1, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Today, Now));
            Assert.Equal(1, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Yesterday, Now));
            Assert.Equal(1, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Earlier, Now));
        }

        [Fact]
        public void RowsExposeSessionForItemsOnly()
        {
            SessionSummary session = Create("s", Now);

            SessionListRow header = SessionListRow.Header(SessionGroup.Today);
            SessionListRow item = SessionListRow.ForSession(SessionGroup.Today, session);

            Assert.True(header.IsHeader);
            Assert.Null(header.Session);
            Assert.False(item.IsHeader);
            Assert.Same(session, item.Session);
            Assert.Throws<ArgumentNullException>(() => SessionListRow.ForSession(SessionGroup.Today, null));
        }

        private static SessionSummary Create(string id, DateTime updatedAt, bool pinned = false)
        {
            return new SessionSummary
            {
                Id = id,
                WorkspaceId = "w1",
                Title = id,
                UpdatedAt = updatedAt,
                IsPinned = pinned
            };
        }
    }
}
