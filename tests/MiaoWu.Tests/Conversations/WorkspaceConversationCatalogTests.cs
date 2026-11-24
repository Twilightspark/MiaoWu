using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Workspaces;
using MiaoWu.Infrastructure.Conversations;
using MiaoWu.Infrastructure.Sessions;
using Xunit;

namespace MiaoWu.Tests.Conversations
{
    public sealed class WorkspaceConversationCatalogTests : IDisposable
    {
        private readonly string _workspaceA;
        private readonly string _workspaceB;

        public WorkspaceConversationCatalogTests()
        {
            _workspaceA = CreateWorkspace("a");
            _workspaceB = CreateWorkspace("b");
        }

        public void Dispose()
        {
            DeleteWorkspace(_workspaceA);
            DeleteWorkspace(_workspaceB);
        }

        [Fact]
        public void SessionsAreScopedToTheirWorkspace()
        {
            var clock = new MutableClock(new DateTimeOffset(2026, 11, 12, 4, 0, 0, TimeSpan.Zero));
            var catalog = new WorkspaceConversationCatalog(clock);
            Workspace workspaceA = WorkspaceOf("w1", _workspaceA);
            Workspace workspaceB = WorkspaceOf("w2", _workspaceB);

            WorkspaceConversationStore store = catalog.GetStore(workspaceA);
            store.CreateAsync(
                    new ConversationDescriptor
                    {
                        SessionId = "s_01",
                        WorkspaceId = "w1",
                        Title = "A 的会话"
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            IReadOnlyList<SessionSummary> sessionsA = catalog.GetSessions(workspaceA);
            IReadOnlyList<SessionSummary> sessionsB = catalog.GetSessions(workspaceB);

            SessionSummary summary = Assert.Single(sessionsA);
            Assert.Equal("s_01", summary.Id);
            Assert.Equal("w1", summary.WorkspaceId);
            Assert.Equal("A 的会话", summary.Title);
            Assert.Equal(clock.UtcNow.ToLocalTime().DateTime, summary.UpdatedAt);
            Assert.Empty(sessionsB);
        }

        [Fact]
        public void ArchivedFlagIsExposedInSessionSummary()
        {
            var catalog = new WorkspaceConversationCatalog();
            Workspace workspace = WorkspaceOf("w1", _workspaceA);
            WorkspaceConversationStore store = catalog.GetStore(workspace);
            store.CreateAsync(
                    new ConversationDescriptor { SessionId = "s_01", WorkspaceId = "w1", Title = "A" },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            store.SetArchivedAsync("s_01", true, CancellationToken.None).GetAwaiter().GetResult();

            SessionSummary summary = Assert.Single(catalog.GetSessions(workspace));

            Assert.True(summary.IsArchived);
        }

        [Fact]
        public void ReadingAWorkspaceCreatesAgentLayout()
        {
            var catalog = new WorkspaceConversationCatalog();
            Workspace workspace = WorkspaceOf("w1", _workspaceA);

            IReadOnlyList<SessionSummary> sessions = catalog.GetSessions(workspace);

            Assert.Empty(sessions);
            Assert.True(Directory.Exists(Path.Combine(_workspaceA, ".miaowu", "sessions")));
            Assert.True(File.Exists(Path.Combine(_workspaceA, ".miaowu", "sessions", "index.json")));
            Assert.True(Directory.Exists(Path.Combine(_workspaceA, ".miaowu", "skills")));
        }

        [Fact]
        public void WorkspaceWithoutPathReturnsEmpty()
        {
            var catalog = new WorkspaceConversationCatalog();

            Assert.Empty(catalog.GetSessions(null));
            Assert.Empty(catalog.GetSessions(new Workspace { Id = "w1", Path = null }));
            Assert.Empty(catalog.GetSessions(new Workspace { Id = "w1", Path = "   " }));
        }

        [Fact]
        public void MissingWorkspaceReturnsEmptyWithoutRecreatingIt()
        {
            var catalog = new WorkspaceConversationCatalog();
            string missing = Path.Combine(
                Path.GetTempPath(),
                "miaowu-catalog-missing-" + Guid.NewGuid().ToString("N"));

            IReadOnlyList<SessionSummary> sessions = catalog.GetSessions(
                WorkspaceOf("w_missing", missing));

            Assert.Empty(sessions);
            Assert.False(Directory.Exists(missing));
        }

        private static Workspace WorkspaceOf(string id, string path)
        {
            return new Workspace
            {
                Id = id,
                Name = id,
                Path = path
            };
        }

        private static string CreateWorkspace(string suffix)
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "miaowu-catalog-" + suffix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteWorkspace(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
