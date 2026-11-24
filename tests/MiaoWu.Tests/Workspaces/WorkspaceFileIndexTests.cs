using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MiaoWu.Core.Input;
using MiaoWu.Infrastructure.Workspaces;
using Xunit;

namespace MiaoWu.Tests.Workspaces
{
    /// <summary>`@` 候选的文件索引与图片附件读取。</summary>
    public sealed class WorkspaceFileIndexTests : IDisposable
    {
        private readonly string _root;

        public WorkspaceFileIndexTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "miaowu-file-index-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            Directory.CreateDirectory(Path.Combine(_root, "docs"));
            Directory.CreateDirectory(Path.Combine(_root, "assets"));
            Directory.CreateDirectory(Path.Combine(_root, "bin"));
            Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
            Directory.CreateDirectory(Path.Combine(_root, ".miaowu", "sessions"));
            Directory.CreateDirectory(Path.Combine(_root, ".agent", "sessions"));
            File.WriteAllText(Path.Combine(_root, "README.md"), "# demo");
            File.WriteAllText(Path.Combine(_root, "src", "main.cs"), "class P {}");
            File.WriteAllText(Path.Combine(_root, "docs", "notes md 草稿.md"), "# 笔记");
            File.WriteAllText(Path.Combine(_root, "bin", "app.dll"), "binary");
            File.WriteAllText(Path.Combine(_root, "node_modules", "x.js"), "module");
            File.WriteAllText(Path.Combine(_root, ".miaowu", "sessions", "s.jsonl"), "{}");
            File.WriteAllText(Path.Combine(_root, ".agent", "sessions", "s.jsonl"), "{}");
            File.WriteAllBytes(Path.Combine(_root, "assets", "logo.png"), OnePixelPng());
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
            catch (Exception)
            {
            }
        }

        [Fact]
        public void Search_ReturnsFilesAndDirectories_AndSkipsBuildDirectories()
        {
            var index = new WorkspaceFileIndex(_root);

            IReadOnlyList<WorkspaceFileEntry> rows = index.Search(string.Empty, 40);
            string[] paths = rows.Select(row => row.RelativePath).ToArray();

            Assert.Contains("src", paths);
            Assert.Contains("src/main.cs", paths);
            Assert.Contains("README.md", paths);
            Assert.DoesNotContain("bin/app.dll", paths);
            Assert.DoesNotContain("node_modules/x.js", paths);
            Assert.DoesNotContain(paths, path => path.StartsWith(".miaowu", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, path => path.StartsWith(".agent", StringComparison.OrdinalIgnoreCase));
            Assert.True(rows.Where(row => row.IsDirectory).All(row => row.RelativePath != "bin"));
        }

        [Fact]
        public void Search_DirectoryPrefix_ListsChildren()
        {
            var index = new WorkspaceFileIndex(_root);

            IReadOnlyList<WorkspaceFileEntry> rows = index.Search("src/", 10);

            Assert.Single(rows);
            Assert.Equal("src/main.cs", rows[0].RelativePath);
        }

        [Fact]
        public void Search_PrefixMatchesFileName()
        {
            var index = new WorkspaceFileIndex(_root);

            IReadOnlyList<WorkspaceFileEntry> rows = index.Search("rea", 10);

            Assert.Contains(rows, row => row.RelativePath == "README.md");
        }

        [Fact]
        public void Search_QuotedStyleQuery_FindsFileWithSpaces()
        {
            var index = new WorkspaceFileIndex(_root);

            IReadOnlyList<WorkspaceFileEntry> rows = index.Search("docs/notes", 10);

            WorkspaceFileEntry entry = rows.First(row => row.RelativePath.EndsWith("草稿.md", StringComparison.Ordinal));
            Assert.Equal("@\"docs/notes md 草稿.md\"", MentionFormatter.Format(entry.RelativePath, false, false));
        }

        [Fact]
        public void ImageLoader_ReadsPngAndComputesHash()
        {
            string path = Path.Combine(_root, "assets", "logo.png");

            ComposerImageAttachment attachment;
            string error;
            bool loaded = ImageAttachmentLoader.TryLoad(path, "assets/logo.png", out attachment, out error);

            Assert.True(loaded, error);
            Assert.Equal("image/png", attachment.MimeType);
            Assert.Equal("logo.png", attachment.DisplayName);
            Assert.False(string.IsNullOrEmpty(attachment.Base64Data));
            Assert.Equal(64, attachment.Sha256.Length);
        }

        [Fact]
        public void ImageLoader_RejectsMissingFile()
        {
            ComposerImageAttachment attachment;
            string error;

            bool loaded = ImageAttachmentLoader.TryLoad(
                Path.Combine(_root, "missing.png"),
                "missing.png",
                out attachment,
                out error);

            Assert.False(loaded);
            Assert.Null(attachment);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        private static byte[] OnePixelPng()
        {
            return Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        }
    }
}
