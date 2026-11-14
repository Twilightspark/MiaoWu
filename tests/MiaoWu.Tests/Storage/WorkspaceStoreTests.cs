using System;
using System.IO;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Workspaces;
using MiaoWu.Infrastructure.Storage;
using Xunit;

namespace MiaoWu.Tests.Storage
{
    public sealed class WorkspaceStoreTests : IDisposable
    {
        private readonly string _directory;
        private readonly string _path;

        public WorkspaceStoreTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "miaowu-workspaces-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "workspaces.json");
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        [Fact]
        public void MissingFileReturnsEmptyCollection()
        {
            WorkspaceCollection collection = new WorkspaceStore(_path).Load();

            Assert.Empty(collection.Workspaces);
        }

        [Fact]
        public void RoundTripsWorkspaceState()
        {
            var workspaces = new WorkspaceCollection { ActiveWorkspaceId = "b" };
            Workspace first = Workspace.Create("a", "MiaoWu", @"D:\AI\MiaoWu", 0);
            first.IsExpanded = false;
            first.View = WorkspaceView.Files;
            first.ScrollOffset = 88;
            first.IsPinned = true;
            first.SelectedSessionId = "s1";
            first.SelectedFilePath = @"D:\AI\MiaoWu\README.md";
            workspaces.Workspaces.Add(first);

            Workspace second = Workspace.Create("b", "reports", @"D:\reports", 1);
            second.IsExpanded = true;
            second.View = WorkspaceView.Sessions;
            workspaces.Workspaces.Add(second);

            new WorkspaceStore(_path).Save(workspaces);

            WorkspaceCollection reloaded = new WorkspaceStore(_path).Load();
            Assert.Equal("b", reloaded.ActiveWorkspaceId);
            Assert.Equal(2, reloaded.Workspaces.Count);

            Workspace restored = WorkspacePolicy.FindById(reloaded.Workspaces, "a");
            Assert.NotNull(restored);
            Assert.Equal(@"D:\AI\MiaoWu", restored.Path);
            Assert.Equal(WorkspaceView.Files, restored.View);
            Assert.Equal(88, restored.ScrollOffset);
            Assert.True(restored.IsPinned);
            Assert.Equal("s1", restored.SelectedSessionId);
            Assert.Equal(@"D:\AI\MiaoWu\README.md", restored.SelectedFilePath);
            Assert.True(WorkspacePolicy.FindById(reloaded.Workspaces, "b").IsExpanded);
        }

        [Fact]
        public void InvalidEntriesAreSkippedAndDuplicatesRemoved()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"workspaces\":[" +
                "{\"id\":\"a\",\"path\":\"" + Escape(@"D:\one") + "\"}," +
                "{\"id\":\"b\",\"path\":\"" + Escape(@"D:\one\") + "\"}," +
                "{\"id\":\"c\"}," +
                "{\"id\":\"d\",\"path\":\"" + Escape(@"D:\two") + "\"}" +
                "]}");

            WorkspaceCollection collection = new WorkspaceStore(_path).Load();

            Assert.Equal(2, collection.Workspaces.Count);
        }

        [Fact]
        public void CorruptFileFallsBackToEmptyAndKeepsBackup()
        {
            File.WriteAllText(_path, "not json");

            WorkspaceCollection collection = new WorkspaceStore(_path).Load();

            Assert.Empty(collection.Workspaces);
            Assert.NotEmpty(Directory.GetFiles(_directory, "workspaces.json.corrupt-*"));
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\");
        }
    }
}
