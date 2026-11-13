using System;
using System.IO;
using MiaoWu.Infrastructure;
using Xunit;

namespace MiaoWu.Tests.Storage
{
    public sealed class AppPathsTests : IDisposable
    {
        private readonly string _root;

        public AppPathsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "miaowu-paths-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Fact]
        public void ConfigFilesLiveAtRootAndUseConfigJson()
        {
            var paths = new AppPaths(_root);

            Assert.Equal(_root, paths.DataDirectory);
            Assert.Equal(Path.Combine(_root, "config.json"), paths.SettingsFilePath);
            Assert.Equal(Path.Combine(_root, "workspaces.json"), Path.Combine(paths.DataDirectory, "workspaces.json"));
            Assert.Equal(Path.Combine(_root, "providers.json"), paths.ProvidersFilePath);
            Assert.Equal(Path.Combine(_root, "secrets.dat"), paths.SecretsFilePath);
        }

        [Fact]
        public void EnsureCreatedIsIdempotent()
        {
            var paths = new AppPaths(_root);

            paths.EnsureCreated();
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.LogsDirectory));
            Assert.True(Directory.Exists(paths.CacheDirectory));
            Assert.True(Directory.Exists(paths.BackupsDirectory));
        }
    }
}
