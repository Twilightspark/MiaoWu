using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Documents;

namespace MiaoWu.Core.Settings
{
    /// <summary>
    /// config.json 的强类型模型。schemaVersion 用于后续迁移；
    /// 未知字段由基础设施层的存储实现原样保留，这里不丢弃。
    /// </summary>
    public sealed class AppSettings
    {
        public const int CurrentSchemaVersion = 1;

        public AppSettings()
        {
            SchemaVersion = CurrentSchemaVersion;
            FirstLaunch = true;
            Ui = new UiSettings();
            Agent = new AgentSettings();
        }

        public int SchemaVersion { get; set; }

        /// <summary>
        /// 首次启动配置项。true 时主窗口只显示首次引导；引导完成后写回 false。
        /// 配置文件为 <c>用户目录\.miaowu\config.json</c>。
        /// </summary>
        public bool FirstLaunch { get; set; }

        public UiSettings Ui { get; set; }

        public AgentSettings Agent { get; set; }
    }

    /// <summary>
    /// 界面状态：窗口尺寸、三栏宽度、右栏折叠、最近工作区/会话/文件、标签集合。
    /// </summary>
    public sealed class UiSettings
    {
        public UiSettings()
        {
            WindowWidth = 1440;
            WindowHeight = 900;
            LeftPaneWidth = 262;
            RightPaneWidth = 428;
            Theme = "light";
            Language = "zh-CN";
            OpenTabs = new List<DocumentTab>();
        }

        public double WindowWidth { get; set; }

        public double WindowHeight { get; set; }

        /// <summary>
        /// 是否已经记录过用户窗口尺寸。首次启动为 false，窗口按默认尺寸收敛；
        /// 之后启动按记住的尺寸恢复，并始终居中到主屏幕工作区。
        /// </summary>
        public bool WindowPlacementSaved { get; set; }

        public bool WindowMaximized { get; set; }

        public double LeftPaneWidth { get; set; }

        public double RightPaneWidth { get; set; }

        public bool IsRightPaneCollapsed { get; set; }

        /// <summary>
        /// 预留的外观偏好："light" | "dark" | "system"。当前只保存选择，
        /// 深色与跟随系统在后续版本生效。
        /// </summary>
        public string Theme { get; set; }

        /// <summary>预留的界面语言偏好："zh-CN" | "en-US"。当前只保存选择。</summary>
        public string Language { get; set; }

        public string ActiveWorkspaceId { get; set; }

        public string ActiveSessionId { get; set; }

        public string ActiveFilePath { get; set; }

        public List<DocumentTab> OpenTabs { get; set; }

        public string SelectedTabId { get; set; }

        public double DocumentScrollOffset { get; set; }
    }

    /// <summary>界面尺寸的合法范围。默认值与 Themes/Tokens.xaml 及 02 第 4 节一致。</summary>
    public sealed class UiLimits
    {
        public UiLimits()
        {
            MinWindowWidth = 1080;
            MinWindowHeight = 760;
            MaxWindowWidth = 3840;
            MaxWindowHeight = 2160;
            LeftMin = 208;
            LeftMax = 320;
            RightMin = 384;
            RightMax = 560;
            MaxTabs = TabStripPolicy.MaxTabs;
        }

        public double MinWindowWidth { get; set; }

        public double MinWindowHeight { get; set; }

        public double MaxWindowWidth { get; set; }

        public double MaxWindowHeight { get; set; }

        public double LeftMin { get; set; }

        public double LeftMax { get; set; }

        public double RightMin { get; set; }

        public double RightMax { get; set; }

        public int MaxTabs { get; set; }
    }

    /// <summary>
    /// 设置值的校验与修复：越界值夹紧、NaN/无穷回退默认、标签集合去重与上限。
    /// 读取损坏或过期的 config.json 时，界面仍要能启动。
    /// </summary>
    public static class UiSettingsPolicy
    {
        public static AppSettings CreateDefault()
        {
            return new AppSettings();
        }

        public static AppSettings Normalize(AppSettings settings, UiLimits limits)
        {
            return Normalize(settings, limits, double.NaN, double.NaN);
        }

        /// <summary>
        /// 按工作区尺寸修复设置。workAreaWidth/Height 传入 NaN 表示未知，只用 limits 的范围。
        /// </summary>
        public static AppSettings Normalize(
            AppSettings settings,
            UiLimits limits,
            double workAreaWidth,
            double workAreaHeight)
        {
            UiLimits effective = limits ?? new UiLimits();
            AppSettings target = settings ?? new AppSettings();

            if (target.SchemaVersion <= 0)
            {
                target.SchemaVersion = AppSettings.CurrentSchemaVersion;
            }

            UiSettings ui = target.Ui ?? new UiSettings();
            target.Ui = ui;

            double maxWindowWidth = effective.MaxWindowWidth;
            double maxWindowHeight = effective.MaxWindowHeight;
            if (!double.IsNaN(workAreaWidth) && workAreaWidth > 0)
            {
                maxWindowWidth = Math.Min(maxWindowWidth, Math.Max(effective.MinWindowWidth, workAreaWidth));
            }

            if (!double.IsNaN(workAreaHeight) && workAreaHeight > 0)
            {
                maxWindowHeight = Math.Min(maxWindowHeight, Math.Max(effective.MinWindowHeight, workAreaHeight));
            }

            ui.WindowWidth = Clamp(ui.WindowWidth, effective.MinWindowWidth, maxWindowWidth, 1440);
            ui.WindowHeight = Clamp(ui.WindowHeight, effective.MinWindowHeight, maxWindowHeight, 900);
            ui.LeftPaneWidth = Clamp(ui.LeftPaneWidth, effective.LeftMin, effective.LeftMax, 262);
            ui.RightPaneWidth = Clamp(ui.RightPaneWidth, effective.RightMin, effective.RightMax, 428);
            ui.DocumentScrollOffset = Clamp(ui.DocumentScrollOffset, 0, double.MaxValue, 0);
            ui.Theme = NormalizeChoice(ui.Theme, "light", "dark", "system");
            ui.Language = NormalizeChoice(ui.Language, "zh-CN", "en-US");

            if (ui.OpenTabs == null)
            {
                ui.OpenTabs = new List<DocumentTab>();
            }

            var tabs = new List<DocumentTab>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DocumentTab tab in ui.OpenTabs)
            {
                if (tab == null || !Enum.IsDefined(typeof(DocumentTabKind), tab.Kind))
                {
                    continue;
                }

                if (tab.Kind == DocumentTabKind.File)
                {
                    if (string.IsNullOrWhiteSpace(tab.FilePath) || !seenPaths.Add(tab.FilePath))
                    {
                        continue;
                    }
                }

                DocumentTab copy = tab.Clone();
                copy.Id = string.IsNullOrWhiteSpace(copy.Id) ? Guid.NewGuid().ToString("N") : copy.Id;
                copy.Title = string.IsNullOrWhiteSpace(copy.Title)
                    ? GetFallbackTitle(copy)
                    : copy.Title;
                copy.ScrollOffset = Clamp(copy.ScrollOffset, 0, double.MaxValue, 0);
                tabs.Add(copy);

                if (tabs.Count >= effective.MaxTabs)
                {
                    break;
                }
            }

            ui.OpenTabs = tabs;

            if (tabs.Count == 0)
            {
                ui.SelectedTabId = null;
            }
            else if (string.IsNullOrEmpty(ui.SelectedTabId)
                || tabs.Find(t => string.Equals(t.Id, ui.SelectedTabId, StringComparison.Ordinal)) == null)
            {
                ui.SelectedTabId = tabs[0].Id;
            }

            ui.ActiveWorkspaceId = Trim(ui.ActiveWorkspaceId);
            ui.ActiveSessionId = Trim(ui.ActiveSessionId);
            ui.ActiveFilePath = Trim(ui.ActiveFilePath);

            return target;
        }

        public static double Clamp(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = fallback;
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = minimum;
            }

            if (maximum < minimum)
            {
                maximum = minimum;
            }

            if (value < minimum)
            {
                return minimum;
            }

            if (value > maximum)
            {
                return maximum;
            }

            return value;
        }

        private static string GetFallbackTitle(DocumentTab tab)
        {
            if (!string.IsNullOrEmpty(tab.FilePath))
            {
                try
                {
                    return Path.GetFileName(tab.FilePath);
                }
                catch (ArgumentException)
                {
                    return tab.FilePath;
                }
            }

            switch (tab.Kind)
            {
                case DocumentTabKind.Diff:
                    return "diff";
                case DocumentTabKind.CommandOutput:
                    return "命令输出";
                case DocumentTabKind.ToolTimeline:
                    return "工具";
                default:
                    return "文档";
            }
        }

        private static string Trim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>把预留选项收敛到已知值；空值或损坏值回退到第一个候选。</summary>
        private static string NormalizeChoice(string value, params string[] allowed)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                string trimmed = value.Trim();
                foreach (string candidate in allowed)
                {
                    if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return allowed[0];
        }
    }
}
