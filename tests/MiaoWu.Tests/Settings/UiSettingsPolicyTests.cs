using System.Collections.Generic;
using MiaoWu.Core.Documents;
using MiaoWu.Core.Settings;
using Xunit;

namespace MiaoWu.Tests.Settings
{
    public sealed class UiSettingsPolicyTests
    {
        private static readonly UiLimits Limits = new UiLimits();

        [Fact]
        public void NullSettingsBecomeDefaults()
        {
            AppSettings settings = UiSettingsPolicy.Normalize(null, Limits);

            Assert.NotNull(settings.Ui);
            Assert.Equal(1440, settings.Ui.WindowWidth);
            Assert.Equal(900, settings.Ui.WindowHeight);
            Assert.Equal(262, settings.Ui.LeftPaneWidth);
            Assert.Equal(580, settings.Ui.RightPaneWidth);
            Assert.Equal("light", settings.Ui.Theme);
            Assert.Equal("zh-CN", settings.Ui.Language);
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        }

        [Theory]
        [InlineData(double.NaN, 262)]
        [InlineData(double.PositiveInfinity, 262)]
        [InlineData(10, 208)]
        [InlineData(5000, 320)]
        public void LeftWidthIsClamped(double value, double expected)
        {
            var settings = new AppSettings();
            settings.Ui.LeftPaneWidth = value;

            UiSettingsPolicy.Normalize(settings, Limits);

            Assert.Equal(expected, settings.Ui.LeftPaneWidth);
        }

        [Theory]
        [InlineData(double.NaN, 580)]
        [InlineData(0, 384)]
        [InlineData(999, 640)]
        public void RightWidthIsClamped(double value, double expected)
        {
            var settings = new AppSettings();
            settings.Ui.RightPaneWidth = value;

            UiSettingsPolicy.Normalize(settings, Limits);

            Assert.Equal(expected, settings.Ui.RightPaneWidth);
        }

        [Fact]
        public void WindowSizeHonorsWorkAreaAndContentMinimum()
        {
            var large = new AppSettings();
            large.Ui.WindowWidth = 4000;
            large.Ui.WindowHeight = 2000;
            UiSettingsPolicy.Normalize(large, Limits, 1366, 728);
            Assert.Equal(1366, large.Ui.WindowWidth);
            Assert.Equal(760, large.Ui.WindowHeight);

            var small = new AppSettings();
            small.Ui.WindowWidth = 900;
            small.Ui.WindowHeight = 600;
            UiSettingsPolicy.Normalize(small, Limits);
            Assert.Equal(1080, small.Ui.WindowWidth);
            Assert.Equal(760, small.Ui.WindowHeight);
        }

        [Fact]
        public void TabsAreDeduplicatedCappedAndSelectionFollowsTheList()
        {
            var dedupe = new AppSettings();
            dedupe.Ui.OpenTabs = new List<DocumentTab>
            {
                new DocumentTab { Kind = DocumentTabKind.File, FilePath = @"C:\w\a.md" },
                new DocumentTab { Kind = DocumentTabKind.File, FilePath = @"c:\W\A.MD" },
                new DocumentTab { Kind = DocumentTabKind.Diff, Title = "" },
                null,
                new DocumentTab { Kind = DocumentTabKind.File, FilePath = " " }
            };

            UiSettingsPolicy.Normalize(dedupe, Limits);

            Assert.Equal(2, dedupe.Ui.OpenTabs.Count);
            Assert.Equal("a.md", dedupe.Ui.OpenTabs[0].Title);
            Assert.False(string.IsNullOrEmpty(dedupe.Ui.OpenTabs[0].Id));
            Assert.Equal("diff", dedupe.Ui.OpenTabs[1].Title);

            var many = new AppSettings();
            many.Ui.OpenTabs = new List<DocumentTab>();
            for (int index = 0; index < 40; index++)
            {
                many.Ui.OpenTabs.Add(DocumentTab.CreateFile(
                    @"C:\w\file" + index + ".md",
                    "file" + index + ".md"));
            }

            many.Ui.SelectedTabId = "missing";
            many.Ui.DocumentScrollOffset = -50;
            UiSettingsPolicy.Normalize(many, Limits);

            Assert.Equal(TabStripPolicy.MaxTabs, many.Ui.OpenTabs.Count);
            Assert.Equal(many.Ui.OpenTabs[0].Id, many.Ui.SelectedTabId);
            Assert.Equal(0, many.Ui.DocumentScrollOffset);

            var empty = new AppSettings();
            empty.Ui.SelectedTabId = "x";
            empty.Ui.OpenTabs = new List<DocumentTab>();
            UiSettingsPolicy.Normalize(empty, Limits);
            Assert.Null(empty.Ui.SelectedTabId);
        }

        [Theory]
        [InlineData("dark", "en-US", "dark", "en-US")]
        [InlineData("SYSTEM", "ZH-cn", "system", "zh-CN")]
        [InlineData("neon", "fr-FR", "light", "zh-CN")]
        [InlineData(null, null, "light", "zh-CN")]
        public void ReservedThemeAndLanguageAreValidated(
            string theme,
            string language,
            string expectedTheme,
            string expectedLanguage)
        {
            var settings = new AppSettings();
            settings.Ui.Theme = theme;
            settings.Ui.Language = language;

            UiSettingsPolicy.Normalize(settings, Limits);

            Assert.Equal(expectedTheme, settings.Ui.Theme);
            Assert.Equal(expectedLanguage, settings.Ui.Language);
        }
    }
}
