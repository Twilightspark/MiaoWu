using System.Collections.Generic;
using System.Linq;
using MiaoWu.Core.Workspaces;
using Xunit;

namespace MiaoWu.Tests.Workspaces
{
    public sealed class FileTreePolicyTests
    {
        [Fact]
        public void SortPutsDirectoriesFirstThenNames()
        {
            var nodes = new List<FileNode>
            {
                new FileNode("b.txt", @"C:\w\b.txt", false, 1),
                new FileNode("A.txt", @"C:\w\A.txt", false, 1),
                new FileNode("z-folder", @"C:\w\z-folder", true, 1),
                new FileNode("a-folder", @"C:\w\a-folder", true, 1)
            };

            FileTreePolicy.SortInPlace(nodes);

            Assert.Equal(
                new[] { "a-folder", "z-folder", "A.txt", "b.txt" },
                nodes.Select(n => n.Name).ToArray());
        }

        [Fact]
        public void FlattenOnlyIncludesChildrenOfExpandedLoadedNodes()
        {
            var root = new FileNode("root", @"C:\w", true, 0);
            var child = new FileNode("child", @"C:\w\child", true, 1);
            var leaf = new FileNode("a.md", @"C:\w\child\a.md", false, 2);
            root.Children.Add(child);
            child.Children.Add(leaf);

            root.State = FileNodeState.Loaded;
            child.State = FileNodeState.Loaded;

            Assert.Equal(new[] { "root" }, FileTreePolicy.Flatten(new[] { root }).Select(n => n.Name));

            root.IsExpanded = true;
            Assert.Equal(
                new[] { "root", "child" },
                FileTreePolicy.Flatten(new[] { root }).Select(n => n.Name));

            child.IsExpanded = true;
            Assert.Equal(
                new[] { "root", "child", "a.md" },
                FileTreePolicy.Flatten(new[] { root }).Select(n => n.Name));
        }

        [Fact]
        public void FlattenSkipsChildrenOfErrorNodes()
        {
            var root = new FileNode("root", @"C:\w", true, 0);
            root.IsExpanded = true;
            root.SetError("无法读取");

            Assert.Single(FileTreePolicy.Flatten(new[] { root }));
            Assert.False(root.HasLoadedChildren);
            Assert.True(root.IsErrorRow);
        }

        [Fact]
        public void IndentGrowsThirteenPerLevel()
        {
            Assert.Equal(6, FileTreePolicy.GetIndent(0));
            Assert.Equal(19, FileTreePolicy.GetIndent(1));
            Assert.Equal(136, FileTreePolicy.GetIndent(10));
        }

        [Fact]
        public void ContentWidthGrowsWithDepth()
        {
            Assert.Equal(
                FileTreePolicy.GetContentWidth(8) + FileTreePolicy.IndentPerLevel * 2,
                FileTreePolicy.GetContentWidth(10));

            // 10 层时超过 262 的左栏默认宽度，需要横向滚动。
            Assert.True(FileTreePolicy.GetContentWidth(10) > 262);
            Assert.True(FileTreePolicy.GetContentWidth(8) <= 262);
        }

        [Theory]
        [InlineData(@"C:\w\README.md", FilePreviewKind.Markdown)]
        [InlineData(@"C:\w\notes.MARKDOWN", FilePreviewKind.Markdown)]
        [InlineData(@"C:\w\logo.png", FilePreviewKind.Image)]
        [InlineData(@"C:\w\app.exe", FilePreviewKind.Binary)]
        [InlineData(@"C:\w\Program.cs", FilePreviewKind.Text)]
        [InlineData(@"C:\w\data.json", FilePreviewKind.Text)]
        [InlineData(@"C:\w\noext", FilePreviewKind.Text)]
        public void PreviewKindFollowsExtension(string path, FilePreviewKind expected)
        {
            Assert.Equal(expected, FileTreePolicy.GetPreviewKind(path));
        }

        [Theory]
        [InlineData(@"C:\w\Program.cs", true)]
        [InlineData(@"C:\w\app.config", true)]
        [InlineData(@"C:\w\README.md", false)]
        public void CodeFilesUseMonospace(string path, bool expected)
        {
            Assert.Equal(expected, FileTreePolicy.IsCodeFile(path));
        }

        [Fact]
        public void ReparsePointCannotExpand()
        {
            var node = new FileNode("junction", @"C:\w\junction", true, 1)
            {
                IsReparsePoint = true
            };

            Assert.False(node.CanExpand);
            Assert.True(new FileNode("dir", @"C:\w\dir", true, 1).CanExpand);
        }
    }
}
