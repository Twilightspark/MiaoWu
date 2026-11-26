using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Agents.Tools;
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
            Assert.False(settings.Ui.WindowPlacementSaved);
            Assert.Empty(settings.Ui.OpenTabs);
            Assert.True(settings.FirstLaunch);
        }

        [Fact]
        public void FirstLaunchRoundTripsAndSyncsLegacyFlag()
        {
            var store = new SettingsStore(_path);
            AppSettings settings = store.Load();
            settings.FirstLaunch = false;

            store.Save(settings);

            JsonValue root = JsonParser.Parse(File.ReadAllText(_path));
            Assert.False(root.Get("firstLaunch").AsBoolean());
            Assert.True(root.Get("agent").Get("onboardingCompleted").AsBoolean());

            AppSettings reloaded = new SettingsStore(_path).Load();
            Assert.False(reloaded.FirstLaunch);
            Assert.True(reloaded.Agent.OnboardingCompleted);
        }

        [Fact]
        public void LegacyOnboardingCompletedMapsToFirstLaunchFalse()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"agent\":{\"onboardingCompleted\":true}}");

            AppSettings settings = new SettingsStore(_path).Load();

            Assert.False(settings.FirstLaunch);
            Assert.True(settings.Agent.OnboardingCompleted);
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
            settings.Ui.Theme = "dark";
            settings.Ui.Language = "en-US";
            settings.Ui.WindowPlacementSaved = true;
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
            Assert.Equal("dark", reloaded.Ui.Theme);
            Assert.Equal("en-US", reloaded.Ui.Language);
            Assert.True(reloaded.Ui.WindowPlacementSaved);
            Assert.True(reloaded.Ui.WindowMaximized);
            Assert.Equal("w1", reloaded.Ui.ActiveWorkspaceId);
            Assert.Equal("tab-diff", reloaded.Ui.SelectedTabId);
            Assert.Equal(2, reloaded.Ui.OpenTabs.Count);
            Assert.Equal(DocumentTabKind.Diff, reloaded.Ui.OpenTabs[0].Kind);
            Assert.Equal(120, reloaded.Ui.OpenTabs[1].ScrollOffset);
        }

        [Fact]
        public void LegacyWindowSizeWithoutFlagIsTreatedAsSaved()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"ui\":{\"windowWidth\":1280,\"windowHeight\":800}}");

            AppSettings settings = new SettingsStore(_path).Load();

            Assert.Equal(1280, settings.Ui.WindowWidth);
            Assert.Equal(800, settings.Ui.WindowHeight);
            Assert.True(settings.Ui.WindowPlacementSaved);
        }

        [Fact]
        public void ExplicitUnsavedWindowSizeIsRespected()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"ui\":{\"windowWidth\":1280,\"windowHeight\":800,\"windowPlacementSaved\":false}}");

            AppSettings settings = new SettingsStore(_path).Load();

            Assert.False(settings.Ui.WindowPlacementSaved);
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

        [Fact]
        public void CloseActionDefaultsToAsk()
        {
            AppSettings settings = new SettingsStore(_path).Load();

            Assert.Equal(CloseWindowAction.Ask, settings.Agent.CloseAction);
        }

        [Fact]
        public void CloseActionRoundTripsThroughConfig()
        {
            var store = new SettingsStore(_path);
            AppSettings settings = store.Load();
            settings.Agent.CloseAction = CloseWindowAction.MinimizeToTray;
            store.Save(settings);

            JsonValue root = JsonParser.Parse(File.ReadAllText(_path));
            Assert.Equal("minimizeToTray", root.Get("agent").Get("closeAction").AsString());
            Assert.Equal(
                CloseWindowAction.MinimizeToTray,
                new SettingsStore(_path).Load().Agent.CloseAction);

            settings.Agent.CloseAction = CloseWindowAction.Exit;
            store.Save(settings);
            Assert.Equal(CloseWindowAction.Exit, new SettingsStore(_path).Load().Agent.CloseAction);
        }

        [Fact]
        public void ApprovalPolicyDefaultsToAsk()
        {
            AppSettings settings = new SettingsStore(_path).Load();

            Assert.Equal(ToolApprovalPolicy.Ask, settings.Agent.ApprovalPolicy);
        }

        [Fact]
        public void ApprovalPolicyRoundTripsThroughConfig()
        {
            var store = new SettingsStore(_path);
            AppSettings settings = store.Load();
            settings.Agent.ApprovalPolicy = ToolApprovalPolicy.Never;
            store.Save(settings);

            JsonValue root = JsonParser.Parse(File.ReadAllText(_path));
            Assert.Equal("never", root.Get("agent").Get("approvalPolicy").AsString());
            Assert.Equal(
                ToolApprovalPolicy.Never,
                new SettingsStore(_path).Load().Agent.ApprovalPolicy);

            settings.Agent.ApprovalPolicy = ToolApprovalPolicy.Ask;
            store.Save(settings);
            Assert.Equal(ToolApprovalPolicy.Ask, new SettingsStore(_path).Load().Agent.ApprovalPolicy);
        }

        [Fact]
        public void UnknownApprovalPolicyFallsBackToAsk()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"agent\":{\"approvalPolicy\":\"sometimes\"}}");

            Assert.Equal(
                ToolApprovalPolicy.Ask,
                new SettingsStore(_path).Load().Agent.ApprovalPolicy);
        }

        [Fact]
        public void GoalAutoContinueDefaultsToOffAndRoundTrips()
        {
            var store = new SettingsStore(_path);
            AppSettings settings = store.Load();
            Assert.False(settings.Agent.GoalAutoContinue);

            settings.Agent.GoalAutoContinue = true;
            store.Save(settings);

            JsonValue root = JsonParser.Parse(File.ReadAllText(_path));
            Assert.True(root.Get("agent").Get("goalAutoContinue").AsBoolean(false));
            Assert.True(new SettingsStore(_path).Load().Agent.GoalAutoContinue);

            File.WriteAllText(_path, "{\"schemaVersion\":1,\"agent\":{}}");
            Assert.False(new SettingsStore(_path).Load().Agent.GoalAutoContinue);
        }

        [Fact]
        public void AutoUpdateDefaultsOnAndRoundTrips()
        {
            var store = new SettingsStore(_path);
            AppSettings settings = store.Load();
            Assert.True(settings.Agent.AutoUpdate);

            settings.Agent.AutoUpdate = false;
            store.Save(settings);

            JsonValue root = JsonParser.Parse(File.ReadAllText(_path));
            Assert.False(root.Get("agent").Get("autoUpdate").AsBoolean(true));
            Assert.False(new SettingsStore(_path).Load().Agent.AutoUpdate);

            // 旧配置没有 autoUpdate：按默认值开启。
            File.WriteAllText(_path, "{\"schemaVersion\":1,\"agent\":{}}");
            Assert.True(new SettingsStore(_path).Load().Agent.AutoUpdate);
        }

        [Fact]
        public void UnknownCloseActionFallsBackToAsk()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"agent\":{\"closeAction\":\"bogus\"}}");

            AppSettings settings = new SettingsStore(_path).Load();

            Assert.Equal(CloseWindowAction.Ask, settings.Agent.CloseAction);
        }

        [Fact]
        public void LegacyKeepRunningInTrayFalseMigratesToExit()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"agent\":{\"keepRunningInTray\":false}}");

            AppSettings settings = new SettingsStore(_path).Load();

            Assert.Equal(CloseWindowAction.Exit, settings.Agent.CloseAction);
        }

        [Fact]
        public void LegacyKeepRunningInTrayTrueMigratesToAsk()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"agent\":{\"keepRunningInTray\":true}}");

            AppSettings settings = new SettingsStore(_path).Load();

            Assert.Equal(CloseWindowAction.Ask, settings.Agent.CloseAction);
        }

        [Fact]
        public void ExplicitCloseActionWinsOverLegacyFlag()
        {
            File.WriteAllText(
                _path,
                "{\"schemaVersion\":1,\"agent\":{\"closeAction\":\"ask\",\"keepRunningInTray\":false}}");

            AppSettings settings = new SettingsStore(_path).Load();

            Assert.Equal(CloseWindowAction.Ask, settings.Agent.CloseAction);
        }
    }
}
