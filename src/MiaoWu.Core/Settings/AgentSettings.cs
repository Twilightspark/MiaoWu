using System;
using System.Collections.Generic;

namespace MiaoWu.Core.Settings
{
    /// <summary>工具输出预算。P3 起统一收敛到 2000 行 / 50 KB。</summary>
    public sealed class ToolOutputSettings
    {
        public ToolOutputSettings()
        {
            MaxLines = 2000;
            MaxBytes = 51200;
            SpillToFile = true;
        }

        public int MaxLines { get; set; }

        public int MaxBytes { get; set; }

        public bool SpillToFile { get; set; }
    }

    /// <summary>
    /// settings.json 的 agent 段。与 Provider/密钥分开，避免把端点与凭据混进界面状态。
    /// </summary>
    public sealed class AgentSettings
    {
        public const string DefaultSystemPrompt =
            "你是 MiaoWu 的本地桌面助手。回答简洁、准确；不确定时说明不确定。" +
            "不要编造文件和命令的输出。涉及写文件或执行命令时，先说清影响范围。";

        public AgentSettings()
        {
            SystemPrompt = DefaultSystemPrompt;
            Temperature = 0.7;
            HistoryLimit = 200;
            MaxConcurrentRequests = 4;
            MaxHotSessions = 8;
            KeepRunningInTray = true;
            RequestTimeoutSeconds = 120;
            IdleTimeoutSeconds = 60;
            ToolsEnabled = true;
            MaxTurnsPerRun = 24;
            MaxToolCallsPerTurn = 8;
            MaxParallelReadTools = 4;
            ToolOutput = new ToolOutputSettings();
            SkillsEnabled = true;
            ContextFilesEnabled = true;
            AutoTitle = true;
            ShowLogOnError = true;
            ProxyMode = "system";
            StartMinimized = false;
            CheckUpdatesOnStartup = true;
            UpdateUrl = "https://github.com/miaowu-app/miaowu/releases";
        }

        public string SystemPrompt { get; set; }

        public string DefaultProviderId { get; set; }

        public string DefaultModelId { get; set; }

        public double Temperature { get; set; }

        public int? MaxOutputTokens { get; set; }

        public int HistoryLimit { get; set; }

        public int MaxConcurrentRequests { get; set; }

        public int MaxHotSessions { get; set; }

        public bool KeepRunningInTray { get; set; }

        public int RequestTimeoutSeconds { get; set; }

        public int IdleTimeoutSeconds { get; set; }

        public bool ToolsEnabled { get; set; }

        public int MaxTurnsPerRun { get; set; }

        public int MaxToolCallsPerTurn { get; set; }

        public int MaxParallelReadTools { get; set; }

        public ToolOutputSettings ToolOutput { get; set; }

        public bool SkillsEnabled { get; set; }

        public List<string> SkillPaths { get; set; }

        public bool ContextFilesEnabled { get; set; }

        public bool AutoTitle { get; set; }

        public bool ShowLogOnError { get; set; }

        /// <summary>首次启动向导是否已完成；false 时主窗口启动后打开向导。</summary>
        public bool OnboardingCompleted { get; set; }

        /// <summary>"system" | "direct" | "manual"。</summary>
        public string ProxyMode { get; set; }

        public string ProxyUrl { get; set; }

        public bool StartMinimized { get; set; }

        public bool CheckUpdatesOnStartup { get; set; }

        public string UpdateUrl { get; set; }

        public AgentSettings Clone()
        {
            return new AgentSettings
            {
                SystemPrompt = SystemPrompt,
                DefaultProviderId = DefaultProviderId,
                DefaultModelId = DefaultModelId,
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens,
                HistoryLimit = HistoryLimit,
                MaxConcurrentRequests = MaxConcurrentRequests,
                MaxHotSessions = MaxHotSessions,
                KeepRunningInTray = KeepRunningInTray,
                RequestTimeoutSeconds = RequestTimeoutSeconds,
                IdleTimeoutSeconds = IdleTimeoutSeconds,
                ToolsEnabled = ToolsEnabled,
                MaxTurnsPerRun = MaxTurnsPerRun,
                MaxToolCallsPerTurn = MaxToolCallsPerTurn,
                MaxParallelReadTools = MaxParallelReadTools,
                ToolOutput = ToolOutput == null
                    ? new ToolOutputSettings()
                    : new ToolOutputSettings
                    {
                        MaxLines = ToolOutput.MaxLines,
                        MaxBytes = ToolOutput.MaxBytes,
                        SpillToFile = ToolOutput.SpillToFile
                    },
                SkillsEnabled = SkillsEnabled,
                SkillPaths = SkillPaths == null ? null : new List<string>(SkillPaths),
                ContextFilesEnabled = ContextFilesEnabled,
                AutoTitle = AutoTitle,
                ShowLogOnError = ShowLogOnError,
                OnboardingCompleted = OnboardingCompleted,
                ProxyMode = ProxyMode,
                ProxyUrl = ProxyUrl,
                StartMinimized = StartMinimized,
                CheckUpdatesOnStartup = CheckUpdatesOnStartup,
                UpdateUrl = UpdateUrl
            };
        }

        /// <summary>把越界或损坏的值修复到可用范围，读取旧配置时也要能启动。</summary>
        public static AgentSettings Normalize(AgentSettings settings)
        {
            AgentSettings value = settings ?? new AgentSettings();

            if (string.IsNullOrWhiteSpace(value.SystemPrompt))
            {
                value.SystemPrompt = DefaultSystemPrompt;
            }

            value.Temperature = Clamp(value.Temperature, 0, 2, 0.7);
            value.HistoryLimit = Clamp(value.HistoryLimit, 10, 5000, 200);
            value.MaxConcurrentRequests = Clamp(value.MaxConcurrentRequests, 1, 8, 4);
            value.MaxHotSessions = Clamp(value.MaxHotSessions, 1, 32, 8);
            value.RequestTimeoutSeconds = Clamp(value.RequestTimeoutSeconds, 15, 600, 120);
            value.IdleTimeoutSeconds = Clamp(value.IdleTimeoutSeconds, 10, 600, 60);
            value.MaxTurnsPerRun = Clamp(value.MaxTurnsPerRun, 1, 60, 24);
            value.MaxToolCallsPerTurn = Clamp(value.MaxToolCallsPerTurn, 1, 16, 8);
            value.MaxParallelReadTools = Clamp(value.MaxParallelReadTools, 1, 8, 4);

            if (value.ToolOutput == null)
            {
                value.ToolOutput = new ToolOutputSettings();
            }

            value.ToolOutput.MaxLines = Clamp(value.ToolOutput.MaxLines, 100, 10000, 2000);
            value.ToolOutput.MaxBytes = Clamp(value.ToolOutput.MaxBytes, 4096, 1048576, 51200);

            if (value.SkillPaths == null)
            {
                value.SkillPaths = new List<string>();
            }

            if (string.IsNullOrWhiteSpace(value.ProxyMode))
            {
                value.ProxyMode = "system";
            }

            if (value.UpdateUrl == null)
            {
                value.UpdateUrl = "https://github.com/miaowu-app/miaowu/releases";
            }

            return value;
        }

        private static int Clamp(int value, int min, int max, int fallback)
        {
            if (value < min || value > max)
            {
                return value == 0 ? fallback : Math.Max(min, Math.Min(max, value));
            }

            return value;
        }

        private static double Clamp(double value, double min, double max, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
            {
                return fallback;
            }

            return value;
        }
    }
}
