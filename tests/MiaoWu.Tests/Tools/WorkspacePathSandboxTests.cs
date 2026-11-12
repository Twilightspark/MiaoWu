using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Agents.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class WorkspacePathSandboxTests
    {
        private const string Root = @"C:\Work\Project";

        private static WorkspacePathSandbox CreateSandbox(FakePathProbe probe = null)
        {
            return new WorkspacePathSandbox(Root, probe);
        }

        [Fact]
        public void RelativePathInsideRootIsAllowed()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            PathSandboxResult result = sandbox.Resolve(@"src\app.cs", PathAccessKind.File, true);

            Assert.True(result.IsAllowed, result.Detail);
            Assert.Equal(@"C:\Work\Project\src\app.cs", result.NormalizedPath);
            Assert.Equal(@"src\app.cs", result.RelativePath);
        }

        [Fact]
        public void AbsolutePathInsideRootIsAllowed()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            PathSandboxResult result = sandbox.Resolve(
                @"C:\Work\Project\README.md",
                PathAccessKind.File,
                true);

            Assert.True(result.IsAllowed, result.Detail);
            Assert.Equal("README.md", result.RelativePath);
        }

        [Fact]
        public void RootItselfIsAllowedAsDirectory()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            PathSandboxResult result = sandbox.Resolve(".", PathAccessKind.Directory, false);

            Assert.True(result.IsAllowed, result.Detail);
            Assert.Equal(string.Empty, result.RelativePath);
        }

        [Fact]
        public void NestedTraversalCannotEscapeRoot()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            PathSandboxResult result = sandbox.Resolve(
                @"sub\..\..\Windows",
                PathAccessKind.Directory,
                false);

            Assert.Equal(PathSandboxStatus.RootEscape, result.Status);
        }

        [Fact]
        public void ParentTraversalIsRejected()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            Assert.Equal(
                PathSandboxStatus.RootEscape,
                sandbox.Resolve(@"..\secret.txt", PathAccessKind.File, true).Status);
            Assert.Equal(
                PathSandboxStatus.RootEscape,
                sandbox.Resolve(@"C:\Work\Other\secret.txt", PathAccessKind.File, true).Status);
        }

        [Fact]
        public void SiblingPrefixIsNotTreatedAsChild()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            PathSandboxResult result = sandbox.Resolve(
                @"C:\Work\Project2\file.txt",
                PathAccessKind.File,
                true);

            Assert.Equal(PathSandboxStatus.RootEscape, result.Status);
        }

        [Theory]
        [InlineData(@"\\server\share\file.txt")]
        [InlineData(@"\\?\C:\Work\Project\file.txt")]
        [InlineData(@"\\.\PhysicalDrive0")]
        [InlineData(@"C:relative.txt")]
        [InlineData(@"\Windows\System32")]
        public void UncDeviceAndRootRelativePathsAreRejected(string candidate)
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            PathSandboxResult result = sandbox.Resolve(candidate, PathAccessKind.File, true);

            Assert.Equal(PathSandboxStatus.UncOrDevice, result.Status);
        }

        [Theory]
        [InlineData(@"file.txt:stream")]
        [InlineData(@"..\..\Windows\System32\drivers\etc\hosts:ads")]
        [InlineData(@"CON")]
        [InlineData(@"con.txt")]
        [InlineData(@"sub\NUL")]
        [InlineData(@"*.txt")]
        [InlineData(@"file?.txt")]
        public void InvalidNamesAndDeviceNamesAreRejected(string candidate)
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            PathSandboxResult result = sandbox.Resolve(candidate, PathAccessKind.File, true);

            Assert.True(
                result.Status == PathSandboxStatus.InvalidPath
                || result.Status == PathSandboxStatus.RootEscape,
                result.Status.ToString());
        }

        [Fact]
        public void EmptyAndWhitespaceAreRejected()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            Assert.Equal(PathSandboxStatus.Empty, sandbox.Resolve(null, PathAccessKind.Any).Status);
            Assert.Equal(PathSandboxStatus.Empty, sandbox.Resolve("   ", PathAccessKind.Any).Status);
        }

        [Fact]
        public void ReparsePointOnIntermediateSegmentIsRejected()
        {
            var probe = new FakePathProbe();
            probe.SetDirectory(@"C:\Work\Project");
            probe.SetReparsePoint(@"C:\Work\Project\link");
            WorkspacePathSandbox sandbox = CreateSandbox(probe);

            PathSandboxResult result = sandbox.Resolve(
                @"link\outside.txt",
                PathAccessKind.File,
                true);

            Assert.Equal(PathSandboxStatus.ReparsePoint, result.Status);
        }

        [Fact]
        public void ReparsePointOnTargetIsRejected()
        {
            var probe = new FakePathProbe();
            probe.SetDirectory(@"C:\Work\Project");
            probe.SetReparsePoint(@"C:\Work\Project\linked.md");
            WorkspacePathSandbox sandbox = CreateSandbox(probe);

            PathSandboxResult result = sandbox.Resolve(
                "linked.md",
                PathAccessKind.File,
                false);

            Assert.Equal(PathSandboxStatus.ReparsePoint, result.Status);
        }

        [Fact]
        public void WrongKindIsRejected()
        {
            var probe = new FakePathProbe();
            probe.SetDirectory(@"C:\Work\Project");
            probe.SetFile(@"C:\Work\Project\file.txt", 10);
            probe.SetDirectory(@"C:\Work\Project\src");
            WorkspacePathSandbox sandbox = CreateSandbox(probe);

            Assert.Equal(
                PathSandboxStatus.WrongKind,
                sandbox.Resolve("file.txt", PathAccessKind.Directory, false).Status);
            Assert.Equal(
                PathSandboxStatus.WrongKind,
                sandbox.Resolve("src", PathAccessKind.File, false).Status);
        }

        [Fact]
        public void MissingPathIsRejectedUnlessAllowed()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            Assert.Equal(
                PathSandboxStatus.NotFound,
                sandbox.Resolve("missing.txt", PathAccessKind.File, false).Status);

            PathSandboxResult allowed = sandbox.Resolve(
                "missing.txt",
                PathAccessKind.File,
                true);
            Assert.True(allowed.IsAllowed, allowed.Detail);
            Assert.Equal("missing.txt", allowed.RelativePath);
        }

        [Fact]
        public void ComparisonIsCaseInsensitive()
        {
            WorkspacePathSandbox sandbox = new WorkspacePathSandbox(
                @"c:\work\PROJECT",
                null);

            PathSandboxResult result = sandbox.Resolve(
                @"C:\Work\Project\Src\App.cs",
                PathAccessKind.File,
                true);

            Assert.True(result.IsAllowed, result.Detail);
        }

        [Fact]
        public void DotSegmentsNormalizeInsideRoot()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            PathSandboxResult result = sandbox.Resolve(
                @".\src\..\src\app.cs",
                PathAccessKind.File,
                true);

            Assert.True(result.IsAllowed, result.Detail);
            Assert.Equal(@"src\app.cs", result.RelativePath);
        }

        [Fact]
        public void ToRelativePathReturnsNullOutsideRoot()
        {
            WorkspacePathSandbox sandbox = CreateSandbox();

            Assert.Equal("src\\app.cs", sandbox.ToRelativePath(@"C:\Work\Project\src\app.cs"));
            Assert.Null(sandbox.ToRelativePath(@"C:\Work\Other\app.cs"));
        }

        private sealed class FakePathProbe : IPathProbe
        {
            private readonly Dictionary<string, FileAttributes> _attributes =
                new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);

            public void SetDirectory(string path)
            {
                _attributes[path] = FileAttributes.Directory;
            }

            public void SetFile(string path, long size)
            {
                _attributes[path] = FileAttributes.Normal;
            }

            public void SetReparsePoint(string path)
            {
                _attributes[path] = FileAttributes.ReparsePoint;
            }

            public bool TryGetAttributes(string path, out FileAttributes attributes)
            {
                return _attributes.TryGetValue(path, out attributes);
            }
        }
    }
}
