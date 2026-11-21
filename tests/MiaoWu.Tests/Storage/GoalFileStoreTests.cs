using System;
using System.IO;
using MiaoWu.Core.Agents.Goals;
using MiaoWu.Infrastructure.Storage;
using Xunit;

namespace MiaoWu.Tests.Storage
{
    public sealed class GoalFileStoreTests : IDisposable
    {
        private readonly string _root;
        private readonly string _path;

        public GoalFileStoreTests()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "miaowu-goal-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_root, ".agent", "goals", "s1.json");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void SaveAndLoadRoundTrip()
        {
            var store = new GoalFileStore(_path);
            var goal = new AgentGoal
            {
                Id = "goal_abc123",
                Objective = "完成 agent loop 对齐",
                Phase = GoalPhase.Blocked,
                Revision = 4,
                RoundsStarted = 3,
                MaxGoalRounds = 12,
                BlockedReason = "等待外部依赖",
                CreatedAt = new DateTimeOffset(2026, 11, 21, 1, 2, 3, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 11, 21, 2, 3, 4, TimeSpan.Zero)
            };

            store.Save(goal);
            AgentGoal loaded = store.Load();

            Assert.True(File.Exists(_path));
            Assert.NotNull(loaded);
            Assert.Equal(goal.Id, loaded.Id);
            Assert.Equal(goal.Objective, loaded.Objective);
            Assert.Equal(GoalPhase.Blocked, loaded.Phase);
            Assert.Equal(4, loaded.Revision);
            Assert.Equal(3, loaded.RoundsStarted);
            Assert.Equal(12, loaded.MaxGoalRounds);
            Assert.Equal("等待外部依赖", loaded.BlockedReason);
            Assert.Equal(goal.CreatedAt.ToLocalTime(), loaded.CreatedAt);
            Assert.Equal(goal.UpdatedAt.ToLocalTime(), loaded.UpdatedAt);
        }

        [Fact]
        public void SaveCreatesMissingDirectories()
        {
            var store = new GoalFileStore(_path);

            store.Save(new AgentGoal
            {
                Id = "goal_new",
                Objective = "目标",
                Phase = GoalPhase.Active,
                Revision = 1,
                MaxGoalRounds = 20
            });

            Assert.True(Directory.Exists(Path.GetDirectoryName(_path)));
            Assert.True(File.Exists(_path));
        }

        [Fact]
        public void MissingFileReturnsNull()
        {
            var store = new GoalFileStore(_path);

            Assert.Null(store.Load());
        }

        [Fact]
        public void CorruptFileIsBackedUpAndIgnored()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, "{ not json");
            var store = new GoalFileStore(_path);

            AgentGoal loaded = store.Load();

            Assert.Null(loaded);
            Assert.False(File.Exists(_path));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_path), "s1.json.corrupt-*"));
        }

        [Fact]
        public void UnknownPhaseIsTreatedAsCorrupt()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(
                _path,
                "{\"id\":\"goal_x\",\"objective\":\"目标\",\"phase\":\"exploded\",\"revision\":2}");
            var store = new GoalFileStore(_path);

            Assert.Null(store.Load());
        }

        [Fact]
        public void EmptyPathIsRejected()
        {
            Assert.Throws<ArgumentException>(() => new GoalFileStore(" "));
        }
    }
}
