using System;
using System.Collections.Generic;
using System.Linq;
using MiaoWu.Core.Sessions;
using Xunit;

namespace MiaoWu.Tests.Sessions
{
    public sealed class SessionSeedFactoryTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 11, 16, 0, 0, DateTimeKind.Local);

        [Fact]
        public void CreatesExactCountWithUniqueIds()
        {
            IReadOnlyList<SessionSummary> sessions = SessionSeedFactory.Create("w1", 24, Now);

            Assert.Equal(24, sessions.Count);
            Assert.Equal(24, sessions.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.All(sessions, s => Assert.Equal("w1", s.WorkspaceId));
            Assert.All(sessions, s => Assert.False(string.IsNullOrEmpty(s.Title)));
        }

        [Fact]
        public void DistributionMatchesGroupPolicy()
        {
            IReadOnlyList<SessionSummary> sessions = SessionSeedFactory.Create("w1", 24, Now);

            Assert.Equal(2, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Pinned, Now));
            Assert.Equal(9, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Today, Now));
            Assert.Equal(7, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Yesterday, Now));
            Assert.Equal(6, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Earlier, Now));
        }

        [Fact]
        public void SmallWorkspacesStillGetEveryGroup()
        {
            IReadOnlyList<SessionSummary> sessions = SessionSeedFactory.Create("w2", 5, Now);

            Assert.Equal(5, sessions.Count);
            Assert.Equal(1, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Pinned, Now));
            Assert.Equal(2, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Today, Now));
            Assert.Equal(1, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Yesterday, Now));
            Assert.Equal(1, SessionGroupPolicy.CountInGroup(sessions, SessionGroup.Earlier, Now));
        }

        [Fact]
        public void IsDeterministicForSameInputs()
        {
            IReadOnlyList<SessionSummary> first = SessionSeedFactory.Create("w", 12, Now);
            IReadOnlyList<SessionSummary> second = SessionSeedFactory.Create("w", 12, Now);

            Assert.Equal(
                first.Select(s => s.Id + "|" + s.Title + "|" + s.UpdatedAt).ToArray(),
                second.Select(s => s.Id + "|" + s.Title + "|" + s.UpdatedAt).ToArray());
        }

        [Fact]
        public void ZeroOrNegativeCountReturnsEmpty()
        {
            Assert.Empty(SessionSeedFactory.Create("w", 0, Now));
            Assert.Empty(SessionSeedFactory.Create("w", -3, Now));
        }
    }
}
