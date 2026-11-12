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
            Assert.Equal(428, settings.Ui.RightPaneWidth);
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
        [InlineData(double.NaN, 428)]
        [InlineData(0, 384)]
        [InlineData(999, 560)]
        public void RightWidthIsClamped(double value, double expected)
        {
            var settings = new AppSettings();
            settings.Ui.RightPaneWidth = value;

            UiSettingsPolicy.Normalize(settings, Limits);

            Assert.Equal(expected, settings.Ui.RightPaneWidth);
        }

        [Fact]
        public void WindowSizeRespectsLimitsAndWorkArea()
        {
            var settings = new AppSettings();
            settings.Ui.WindowWidth = 4000;
            settings.Ui.WindowHeight = 2000;

            UiSettingsPolicy.Normalize(settings, Limits, 1366, 728);

            Assert.Equal(1366, settings.Ui.WindowWidth);
            Assert.Equal(760, settings.Ui.WindowHeight);
        }

        [Fact]
        public void DuplicateFileTabsAreRemovedAndTitlesFallback()
        {
            var settings = new AppSettings();
            settings.Ui.OpenTabs = new List<DocumentTab>
            {
                new DocumentTab { Kind = DocumentTabKind.File, FilePath = @"C:\w\a.md" },
                new DocumentTab { Kind = DocumentTabKind.File, FilePath = @"c:\W\A.MD" },
                new DocumentTab { Kind = DocumentTabKind.Diff, Title = "" },
                null,
                new DocumentTab { Kind = DocumentTabKind.File, FilePath = " " }
            };

            UiSettingsPolicy.Normalize(settings, Limits);

            Assert.Equal(2, settings.Ui.OpenTabs.Count);
            Assert.Equal("a.md", settings.Ui.OpenTabs[0].Title);
            Assert.False(string.IsNullOrEmpty(settings.Ui.OpenTabs[0].Id));
            Assert.Equal("diff", settings.Ui.OpenTabs[1].Title);
        }

        [Fact]
        public void SelectedTabFallsBackToFirst()
        {
            var settings = new AppSettings();
            settings.Ui.OpenTabs = new List<DocumentTab>
            {
                DocumentTab.CreateFile(@"C:\w\a.md", "a.md"),
                DocumentTab.CreateFile(@"C:\w\b.md", "b.md")
            };
            settings.Ui.SelectedTabId = "missing";

            UiSettingsPolicy.Normalize(settings, Limits);

            Assert.Equal(settings.Ui.OpenTabs[0].Id, settings.Ui.SelectedTabId);
        }

        [Fact]
        public void SelectedTabIsClearedWhenNoTabsRemain()
        {
            var settings = new AppSettings();
            settings.Ui.SelectedTabId = "x";

            UiSettingsPolicy.Normalize(settings, Limits);

            Assert.Null(settings.Ui.SelectedTabId);
        }

        [Fact]
        public void TabCountIsCapped()
        {
            var settings = new AppSettings();
            settings.Ui.OpenTabs = new List<DocumentTab>();
            for (int index = 0; index < 40; index++)
            {
                settings.Ui.OpenTabs.Add(DocumentTab.CreateFile(
                    @"C:\w\file" + index + ".md",
                    "file" + index + ".md"));
            }

            UiSettingsPolicy.Normalize(settings, Limits);

            Assert.Equal(TabStripPolicy.MaxTabs, settings.Ui.OpenTabs.Count);
        }

        [Fact]
        public void ScrollOffsetsAreNonNegative()
        {
            var settings = new AppSettings();
            settings.Ui.DocumentScrollOffset = -50;
            settings.Ui.OpenTabs = new List<DocumentTab>
            {
                DocumentTab.CreateFile(@"C:\a.md", "a.md")
            };
            settings.Ui.OpenTabs[0].ScrollOffset = -1;

            UiSettingsPolicy.Normalize(settings, Limits);

            Assert.Equal(0, settings.Ui.DocumentScrollOffset);
            Assert.Equal(0, settings.Ui.OpenTabs[0].ScrollOffset);
        }
    }
}
