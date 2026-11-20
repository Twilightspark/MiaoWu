using System;
using System.IO;
using MiaoWu.Infrastructure.Storage;
using Xunit;

namespace MiaoWu.Tests.Storage
{
    public sealed class FolderAccessTests : IDisposable
    {
        private readonly string _root;

        public FolderAccessTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "miaowu-access-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Fact]
        public void IsWritableIsTrueForAnExistingDirectory()
        {
            Directory.CreateDirectory(_root);

            Assert.True(FolderAccess.IsWritable(_root));
        }

        [Fact]
        public void IsWritableIsFalseForAMissingDirectory()
        {
            Assert.False(FolderAccess.IsWritable(_root));
        }

        [Fact]
        public void EnsureWritableCreatesMissingDirectoriesAndStaysIdempotent()
        {
            string nested = Path.Combine(_root, "cache", "tool-output");

            FolderAccess.EnsureWritable(nested);
            FolderAccess.EnsureWritable(nested);

            Assert.True(Directory.Exists(nested));
            Assert.True(FolderAccess.IsWritable(nested));
        }

        [Fact]
        public void EnsureWritableRejectsEmptyPath()
        {
            Assert.Throws<ArgumentException>(() => FolderAccess.EnsureWritable(" "));
        }

        [Fact]
        public void WriteProbeLeavesNoResidue()
        {
            Directory.CreateDirectory(_root);

            Assert.True(FolderAccess.IsWritable(_root));
            Assert.Empty(Directory.GetFiles(_root));
        }

        [Fact]
        public void GrantingFullControlToCurrentUserSucceedsOnOwnedDirectory()
        {
            Directory.CreateDirectory(_root);

            // 低完整性 / 受限令牌下改不动 ACL，这里只要求“不抛异常”，
            // 具体结果由 IsWritable 的探针判断。
            FolderAccess.TryGrantCurrentUserFullControl(_root);
            Assert.True(FolderAccess.IsWritable(_root));
        }
    }
}
