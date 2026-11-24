using System;
using System.IO;
using MiaoWu.Core.Workspaces;
using MiaoWu.Infrastructure.Workspaces;
using Xunit;

namespace MiaoWu.Tests.Workspaces
{
    public sealed class FileSystemCatalogTests : IDisposable
    {
        private readonly string _directory;
        private readonly FileSystemCatalog _catalog = new FileSystemCatalog();

        public FileSystemCatalogTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "miaowu-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Directory.CreateDirectory(Path.Combine(_directory, "z-folder"));
            Directory.CreateDirectory(Path.Combine(_directory, "a-folder"));
            Directory.CreateDirectory(Path.Combine(_directory, ".miaowu", "sessions"));
            Directory.CreateDirectory(Path.Combine(_directory, ".agent", "sessions"));
            File.WriteAllText(Path.Combine(_directory, "b.txt"), "b");
            File.WriteAllText(Path.Combine(_directory, "A.txt"), "a");
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        [Fact]
        public void DirectoriesComeFirstThenNames()
        {
            var nodes = _catalog.GetChildren(_directory, 0);

            Assert.Equal(4, nodes.Count);
            Assert.Equal("a-folder", nodes[0].Name);
            Assert.Equal("z-folder", nodes[1].Name);
            Assert.Equal("A.txt", nodes[2].Name);
            Assert.Equal("b.txt", nodes[3].Name);
            Assert.DoesNotContain(nodes, node => node.Name == ".miaowu");
            Assert.DoesNotContain(nodes, node => node.Name == ".agent");
            Assert.True(nodes[0].IsDirectory);
            Assert.False(nodes[2].IsDirectory);
            Assert.Equal(FileNodeState.NotLoaded, nodes[0].State);
            Assert.Equal(FileNodeState.Loaded, nodes[2].State);
        }

        [Fact]
        public void DepthIsAppliedToChildren()
        {
            var nodes = _catalog.GetChildren(_directory, 3);

            Assert.All(nodes, node => Assert.Equal(3, node.Depth));
        }

        [Fact]
        public void MissingDirectoryReturnsErrorRowInsteadOfThrowing()
        {
            var nodes = _catalog.GetChildren(Path.Combine(_directory, "nope"), 1);

            Assert.Single(nodes);
            Assert.True(nodes[0].IsErrorRow);
            Assert.False(string.IsNullOrEmpty(nodes[0].ErrorMessage));
        }

        [Fact]
        public void RenameMovesFileInSameDirectory()
        {
            string source = Path.Combine(_directory, "b.txt");

            string target = _catalog.Rename(source, "renamed.txt");

            Assert.False(File.Exists(source));
            Assert.True(File.Exists(target));
            Assert.Equal(Path.Combine(_directory, "renamed.txt"), target);
        }

        [Fact]
        public void RenameRejectsExistingTargetAndInvalidName()
        {
            string source = Path.Combine(_directory, "b.txt");

            Assert.Throws<System.IO.IOException>(() => { _catalog.Rename(source, "A.txt"); });
            Assert.Throws<System.InvalidOperationException>(() => { _catalog.Rename(source, "nested/b.txt"); });
            Assert.True(File.Exists(source));
        }

        [Fact]
        public void EmptyPathReturnsErrorRow()
        {
            var nodes = _catalog.GetChildren(" ", 0);

            Assert.Single(nodes);
            Assert.True(nodes[0].IsErrorRow);
        }
    }
}
