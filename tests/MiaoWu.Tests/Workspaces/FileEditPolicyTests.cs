using System.IO;
using MiaoWu.Core.Workspaces;
using Xunit;

namespace MiaoWu.Tests.Workspaces
{
    public sealed class FileEditPolicyTests
    {
        [Fact]
        public void NormalNameIsAccepted()
        {
            string normalized;
            string error;

            Assert.True(FileEditPolicy.TryNormalizeName("  报告 2026.md  ", out normalized, out error));
            Assert.Equal("报告 2026.md", normalized);
            Assert.Null(error);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a/b.txt")]
        [InlineData("a\\b.txt")]
        [InlineData("a:b.txt")]
        [InlineData("a*.txt")]
        [InlineData("..")]
        [InlineData("name.")]
        public void InvalidNamesAreRejected(string name)
        {
            string normalized;
            string error;

            Assert.False(FileEditPolicy.TryNormalizeName(name, out normalized, out error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Theory]
        [InlineData("CON")]
        [InlineData("con.txt")]
        [InlineData("LPT1.md")]
        public void ReservedNamesAreRejected(string name)
        {
            string normalized;
            string error;

            Assert.False(FileEditPolicy.TryNormalizeName(name, out normalized, out error));
        }

        [Fact]
        public void TargetPathStaysInSameDirectory()
        {
            string target;
            string error;

            Assert.True(FileEditPolicy.TryBuildTargetPath(
                Path.Combine("C:", "work", "old.txt"),
                "new.txt",
                out target,
                out error));
            Assert.Equal(Path.Combine("C:", "work", "new.txt"), target);
        }

        [Fact]
        public void SameNameIsRejectedIgnoringCase()
        {
            string target;
            string error;

            Assert.False(FileEditPolicy.TryBuildTargetPath(
                Path.Combine("C:", "work", "Old.txt"),
                "old.txt",
                out target,
                out error));
            Assert.Equal(FileEditPolicy.SameNameError, error);
        }
    }
}
