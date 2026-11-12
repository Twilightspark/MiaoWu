using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Documents;
using MiaoWu.Core.Settings;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Storage;
using Xunit;

namespace MiaoWu.Tests.Storage
{
    public sealed class SettingsStoreTests : IDisposable
    {
        private readonly string _directory;
        private readonly string _path;

        public SettingsStoreTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "miaowu-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "settings.json");
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        [Fact]
        public void MissingFileReturnsDefaults()
        {
            var store = new SettingsStore(_path);

            AppSettings settings = store.Load();

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(1440, settings.Ui.WindowWidth);
            Assert.Empty(settings.Ui.OpenTabs);
        }

        [Fact]
        public void RoundTripsUiSettingsAndTabs()
        {
            var store = new SettingsStore(_path);
            AppSettings settings = store.Load();
            settings.Ui.WindowWidth = 1280;
            settings.Ui.WindowHeight = 800;
            settings.Ui.LeftPaneWidth = 280;
            settings.Ui.RightPaneWidth = 460;
            settings.Ui.IsRightPaneCollapsed = true;
            settings.Ui.WindowMaximized = true;
            settings.Ui.ActiveWorkspaceId = "w1";
            settings.Ui.SelectedTabId = "tab-diff";
            settings.Ui.OpenTabs = new List<DocumentTab>
            {
                DocumentTab.CreateStatic(DocumentTabKind.Diff, "diff", "tab-diff"),
                DocumentTab.CreateFile(@"C:\work\README.md", "README.md")
            };
            settings.Ui.OpenTabs[1].ScrollOffset = 120;

            store.Save(settings);

            var reloaded = new SettingsStore(_path).Load();
            Assert.Equal(1280, reloaded.Ui.WindowWidth);
            Assert.Equal(800, reloaded.Ui.WindowHeight);
            Assert.Equal(280, reloaded.Ui.LeftPaneWidth);
            Assert.Equal(460, reloaded.Ui.RightPaneWidth);
            Assert.True(reloaded.Ui.IsRightPaneCollapsed);
            Assert.True(reloaded.Ui.WindowMaximized);
            Assert.Equal("w1", reloaded.Ui.ActiveWorkspaceId);
            Assert.Equal("tab-diff", reloaded.Ui.SelectedTabId);
            Assert.Equal(2, reloaded.Ui.OpenTabs.Count);
            Assert.Equal(DocumentTabKind.Diff, reloaded.Ui.OpenTabs[0].Kind);
            Assert.Equal(120, reloaded.Ui.OpenTabs[1].ScrollOffset);
        }

        [Fact]
        public void PreservesUnknownFields()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"theme\":\"dark\",\"ui\":{\"windowWidth\":1300,\"futureFlag\":true,\"nested\":{\"a\":1}}}");

            var store = new SettingsStore(_path);
            AppSettings settings = store.Load();
            Assert.Equal(1300, settings.Ui.WindowWidth);

            settings.Ui.LeftPaneWidth = 300;
            store.Save(settings);

            JsonValue root = JsonParser.Parse(File.ReadAllText(_path));
            Assert.Equal("dark", root.Get("theme").AsString());
            Assert.True(root.Get("ui").Get("futureFlag").AsBoolean());
            Assert.Equal(1, root.Get("ui").Get("nested").Get("a").AsInt());
            Assert.Equal(300, root.Get("ui").Get("leftPaneWidth").AsDouble());
        }

        [Fact]
        public void CorruptFileFallsBackToDefaultsAndKeepsBackup()
        {
            File.WriteAllText(_path, "{ broken json ");

            AppSettings settings = new SettingsStore(_path).Load();

            Assert.Equal(1440, settings.Ui.WindowWidth);
            Assert.False(File.Exists(_path));
            Assert.NotEmpty(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
        }

        [Fact]
        public void NewerSchemaIsNotDowngraded()
        {
            File.WriteAllText(_path, "{\"schemaVersion\":99,\"ui\":{\"windowWidth\":1000}}");

            AppSettings settings = new SettingsStore(_path).Load();

            Assert.Equal(1440, settings.Ui.WindowWidth);
            Assert.NotEmpty(Directory.GetFiles(_directory, "settings.json.unsupported-*"));
        }

        [Fact]
        public void SaveIsAtomicAndLeavesNoTempFile()
        {
            var store = new SettingsStore(_path);
            store.Save(new AppSettings());
            store.Save(new AppSettings());

            Assert.True(File.Exists(_path));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
        }
    }
}
