using System;
using System.IO;
using MiaoWu.Core.Conversations;
using MiaoWu.Infrastructure.Conversations;
using Xunit;

namespace MiaoWu.Tests.Conversations
{
    public sealed class AgentWorkspaceLayoutTests : IDisposable
    {
        private readonly string _workspace;

        public AgentWorkspaceLayoutTests()
        {
            _workspace = Path.Combine(
                Path.GetTempPath(),
                "miaowu-layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, true);
            }
        }

        [Fact]
        public void PathsLiveUnderWorkspaceAgentDirectory()
        {
            string agent = AgentWorkspaceLayout.GetAgentDirectory(_workspace);

            Assert.Equal(Path.Combine(_workspace, ".agent"), agent);
            Assert.Equal(
                Path.Combine(agent, "sessions"),
                AgentWorkspaceLayout.GetSessionsDirectory(_workspace));
            Assert.Equal(
                Path.Combine(agent, "skills"),
                AgentWorkspaceLayout.GetSkillsDirectory(_workspace));
            Assert.Equal(
                Path.Combine(agent, "attachments"),
                AgentWorkspaceLayout.GetAttachmentsDirectory(_workspace));
            Assert.Equal(
                Path.Combine(agent, "goals"),
                AgentWorkspaceLayout.GetGoalsDirectory(_workspace));
            Assert.Equal(
                Path.Combine(agent, "goals", "s_01.json"),
                AgentWorkspaceLayout.GetGoalFilePath(_workspace, "s_01"));
            Assert.Equal(
                Path.Combine(agent, "cache"),
                AgentWorkspaceLayout.GetCacheDirectory(_workspace));
            Assert.Equal(
                Path.Combine(agent, "sessions", "index.json"),
                AgentWorkspaceLayout.GetIndexFilePath(_workspace));
        }

        [Fact]
        public void SessionFilePathUsesJsonlExtension()
        {
            string path = AgentWorkspaceLayout.GetSessionFilePath(_workspace, "s_20260101120000_ab12cd34");

            Assert.Equal(
                Path.Combine(_workspace, ".agent", "sessions", "s_20260101120000_ab12cd34.jsonl"),
                path);
        }

        [Theory]
        [InlineData("s_01")]
        [InlineData("ABC-def_123")]
        [InlineData("a")]
        public void ValidSessionIdsAreAccepted(string id)
        {
            Assert.True(AgentWorkspaceLayout.IsValidSessionId(id));
        }

        [Theory]
        [InlineData("")]
        [InlineData("../escape")]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("s 01")]
        [InlineData("s.01")]
        [InlineData("C:\\temp\\x")]
        public void InvalidSessionIdsAreRejected(string id)
        {
            Assert.False(AgentWorkspaceLayout.IsValidSessionId(id));
        }

        [Fact]
        public void SessionIdLongerThan64IsRejected()
        {
            Assert.False(AgentWorkspaceLayout.IsValidSessionId(new string('a', 65)));
            Assert.True(AgentWorkspaceLayout.IsValidSessionId(new string('a', 64)));
        }

        [Fact]
        public void InvalidSessionIdCannotEscapeSessionsDirectory()
        {
            Assert.Throws<ArgumentException>(
                () => AgentWorkspaceLayout.GetSessionFilePath(_workspace, "..\\..\\outside"));
            Assert.Throws<ArgumentException>(
                () => AgentWorkspaceLayout.GetSessionFilePath(_workspace, "C:\\Windows\\system.ini"));
            Assert.Throws<ArgumentException>(
                () => AgentWorkspaceLayout.GetGoalFilePath(_workspace, "..\\..\\outside"));
        }

        [Fact]
        public void CreateSessionIdProducesValidUniqueIds()
        {
            DateTimeOffset now = new DateTimeOffset(2026, 11, 12, 3, 4, 5, TimeSpan.Zero);

            string first = AgentWorkspaceLayout.CreateSessionId(now);
            string second = AgentWorkspaceLayout.CreateSessionId(now);

            Assert.True(AgentWorkspaceLayout.IsValidSessionId(first));
            Assert.True(AgentWorkspaceLayout.IsValidSessionId(second));
            Assert.NotEqual(first, second);
            Assert.StartsWith("s_20261112030405_", first);
        }

        [Fact]
        public void InsideAgentDirectoryChecksResolvedPrefix()
        {
            string inside = Path.Combine(_workspace, ".agent", "sessions", "x.jsonl");
            string outside = Path.Combine(_workspace, "docs", "x.jsonl");
            string sibling = Path.Combine(_workspace, ".agent2", "x.jsonl");

            Assert.True(AgentWorkspaceLayout.IsInsideAgentDirectory(_workspace, inside));
            Assert.False(AgentWorkspaceLayout.IsInsideAgentDirectory(_workspace, outside));
            Assert.False(AgentWorkspaceLayout.IsInsideAgentDirectory(_workspace, sibling));
        }

        [Fact]
        public void ManagedDirectoriesCreateReservedLayout()
        {
            var store = new WorkspaceConversationStore(
                "w1",
                _workspace,
                new MutableClock(DateTimeOffset.UtcNow));

            store.EnsureCreated();

            Assert.True(Directory.Exists(Path.Combine(_workspace, ".agent", "skills")));
            Assert.True(Directory.Exists(Path.Combine(_workspace, ".agent", "sessions")));
            Assert.True(Directory.Exists(Path.Combine(_workspace, ".agent", "goals")));
            Assert.True(Directory.Exists(Path.Combine(_workspace, ".agent", "attachments")));
            Assert.True(Directory.Exists(Path.Combine(_workspace, ".agent", "cache")));

            store.EnsureCreated();
        }
    }

    internal sealed class MutableClock : MiaoWu.Core.Abstractions.IClock
    {
        public MutableClock(DateTimeOffset start)
        {
            UtcNow = start;
        }

        public DateTimeOffset UtcNow { get; set; }

        public void Advance(TimeSpan delta)
        {
            UtcNow += delta;
        }
    }
}
