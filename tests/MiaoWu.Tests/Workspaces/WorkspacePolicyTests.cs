using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Workspaces;
using Xunit;

namespace MiaoWu.Tests.Workspaces
{
    public sealed class WorkspacePolicyTests
    {
        [Fact]
        public void NormalizePathTrimsTrailingSeparator()
        {
            string path = Path.Combine(Path.GetTempPath(), "miaowu-normalize") + Path.DirectorySeparatorChar;

            string normalized = WorkspacePolicy.NormalizePath(path);

            Assert.Equal(
                Path.Combine(Path.GetTempPath(), "miaowu-normalize"),
                normalized);
        }

        [Fact]
        public void NormalizePathKeepsRootSeparator()
        {
            string root = Path.GetPathRoot(Path.GetTempPath());

            Assert.Equal(root, WorkspacePolicy.NormalizePath(root));
        }

        [Fact]
        public void SamePathIgnoresCaseAndTrailingSeparator()
        {
            Assert.True(WorkspacePolicy.IsSamePath(@"C:\Work\MiaoWu", @"c:\work\miaowu\"));
        }

        [Fact]
        public void DisplayNameUsesLastSegment()
        {
            Assert.Equal("MiaoWu", WorkspacePolicy.GetDisplayName(@"D:\AI\MiaoWu"));
        }

        [Fact]
        public void ExpandOnlyCollapsesOtherWorkspaces()
        {
            var workspaces = new List<Workspace>
            {
                Workspace.Create("a", "a", @"C:\a", 0),
                Workspace.Create("b", "b", @"C:\b", 1)
            };
            workspaces[0].IsExpanded = true;

            bool changed = WorkspacePolicy.ExpandOnly(workspaces, "b");

            Assert.True(changed);
            Assert.False(workspaces[0].IsExpanded);
            Assert.True(workspaces[1].IsExpanded);
            Assert.Same(workspaces[1], WorkspacePolicy.GetExpanded(workspaces));
        }

        [Fact]
        public void ExpandOnlyUnknownIdCollapsesEverything()
        {
            var workspaces = new List<Workspace>
            {
                Workspace.Create("a", "a", @"C:\a", 0)
            };
            workspaces[0].IsExpanded = true;

            Assert.True(WorkspacePolicy.ExpandOnly(workspaces, "missing"));
            Assert.False(workspaces[0].IsExpanded);
        }

        [Fact]
        public void SortPutsPinnedFirstThenKeepsOrder()
        {
            var workspaces = new List<Workspace>
            {
                Workspace.Create("a", "A", @"C:\a", 0),
                Workspace.Create("b", "B", @"C:\b", 1),
                Workspace.Create("c", "C", @"C:\c", 2)
            };
            workspaces[2].IsPinned = true;

            IReadOnlyList<Workspace> ordered = WorkspacePolicy.Sort(workspaces);

            Assert.Equal(new[] { "c", "a", "b" }, new[] { ordered[0].Id, ordered[1].Id, ordered[2].Id });
        }

        [Fact]
        public void TryAddRejectsMissingFolder()
        {
            var workspaces = new List<Workspace>();
            Workspace workspace;
            string error;

            bool added = WorkspacePolicy.TryAdd(
                workspaces,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                out workspace,
                out error);

            Assert.False(added);
            Assert.Null(workspace);
            Assert.Equal(WorkspacePolicy.MissingFolderError, error);
        }

        [Fact]
        public void TryAddRejectsDuplicatePath()
        {
            string folder = CreateTempFolder();
            try
            {
                var workspaces = new List<Workspace>();
                Workspace first;
                string error;

                Assert.True(WorkspacePolicy.TryAdd(workspaces, folder, out first, out error));
                Assert.NotNull(first);

                Workspace second;
                Assert.False(WorkspacePolicy.TryAdd(
                    workspaces,
                    folder + Path.DirectorySeparatorChar,
                    out second,
                    out error));
                Assert.Equal(WorkspacePolicy.DuplicatePathError, error);
                Assert.Single(workspaces);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void TryAddAssignsIncreasingOrder()
        {
            string folder = CreateTempFolder();
            try
            {
                var workspaces = new List<Workspace>();
                Workspace first;
                Workspace second;
                string error;

                Assert.True(WorkspacePolicy.TryAdd(workspaces, folder, out first, out error));

                string child = Path.Combine(folder, "child");
                Directory.CreateDirectory(child);
                Assert.True(WorkspacePolicy.TryAdd(workspaces, child, out second, out error));

                Assert.Equal(0, first.Order);
                Assert.Equal(1, second.Order);
                Assert.Equal("child", second.Name);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void CaptureAndApplyStateRoundTrips()
        {
            Workspace workspace = Workspace.Create("id", "name", @"C:\work", 0);
            workspace.IsExpanded = true;
            workspace.View = WorkspaceView.Files;
            workspace.ScrollOffset = 120;
            workspace.SelectedSessionId = "s1";
            workspace.SelectedFilePath = @"C:\work\a.md";

            WorkspaceViewState state = workspace.CaptureState();
            Workspace copy = Workspace.Create("id", "name", @"C:\work", 0);
            copy.ApplyState(state);

            Assert.True(copy.IsExpanded);
            Assert.Equal(WorkspaceView.Files, copy.View);
            Assert.Equal(120, copy.ScrollOffset);
            Assert.Equal("s1", copy.SelectedSessionId);
            Assert.Equal(@"C:\work\a.md", copy.SelectedFilePath);

            state.View = WorkspaceView.Sessions;
            Assert.Equal(WorkspaceView.Files, workspace.View);
        }

        private static string CreateTempFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "miaowu-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}
