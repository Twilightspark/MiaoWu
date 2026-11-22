using System;
using System.Collections.Generic;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Core.Settings
{
    /// <summary>
    /// 关闭主窗口时的行为。Ask 每次弹窗询问；其余两项是用户勾选“记住”后的直接动作。
    /// </summary>
    public enum CloseWindowAction
    {
        Ask = 0,
        MinimizeToTray = 1,
        Exit = 2
    }

    /// <summary>关闭行为与 config.json 字符串值之间的转换，读写两侧共用同一套拼写。</summary>
    public static class CloseWindowActionText
    {
        public const string Ask = "ask";
        public const string MinimizeToTray = "minimizeToTray";
        public const string Exit = "exit";

        public static string ToJson(CloseWindowAction action)
        {
            switch (action)
            {
                case CloseWindowAction.MinimizeToTray:
                    return MinimizeToTray;
                case CloseWindowAction.Exit:
                    return Exit;
                default:
                    return Ask;
            }
        }

        /// <summary>未知/损坏值回落到 Ask，保证旧配置仍能启动并重新询问。</summary>
        public static CloseWindowAction Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return CloseWindowAction.Ask;
            }

            string value = text.Trim();
            if (string.Equals(value, MinimizeToTray, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "tray", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "minimize", StringComparison.OrdinalIgnoreCase))
            {
                return CloseWindowAction.MinimizeToTray;
            }

            if (string.Equals(value, Exit, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "close", StringComparison.OrdinalIgnoreCase))
            {
                return CloseWindowAction.Exit;
            }

            return CloseWindowAction.Ask;
        }
    }

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
    /// config.json 的 agent 段。与 Provider/密钥分开，避免把端点与凭据混进界面状态。
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
            CloseAction = CloseWindowAction.Ask;
            RequestTimeoutSeconds = 120;
            IdleTimeoutSeconds = 60;
            ToolsEnabled = true;
            MaxTurnsPerRun = 24;
            MaxToolCallsPerTurn = 8;
            MaxParallelReadTools = 4;
            ApprovalPolicy = ToolApprovalPolicy.Ask;
            ToolOutput = new ToolOutputSettings();
            SkillsEnabled = true;
            ContextFilesEnabled = true;
            AutoTitle = true;
            GoalAutoContinue = false;
            ShowLogOnError = true;
            ProxyMode = "system";
            StartMinimized = false;
            CheckUpdatesOnStartup = true;
            UpdateUrl = "https://github.com/miaowu-app/miaowu/releases";
        }

        public string SystemPrompt { get; set; }

        public string DefaultProviderId { get; set; }

        public string DefaultModelId { get; set; }

        /// <summary>
        /// 推理等级：<c>null</c>（默认）/ <c>low</c> / <c>medium</c> / <c>high</c>。
        /// 与模型一样是全局配置；只有模型声明了思考通道时才会发给 Provider。
        /// </summary>
        public string ReasoningEffort { get; set; }

        public double Temperature { get; set; }

        public int? MaxOutputTokens { get; set; }

        public int HistoryLimit { get; set; }

        public int MaxConcurrentRequests { get; set; }

        public int MaxHotSessions { get; set; }

        /// <summary>关闭窗口时是询问、最小化到托盘还是退出；Ask 为默认。</summary>
        public CloseWindowAction CloseAction { get; set; }

        public int RequestTimeoutSeconds { get; set; }

        public int IdleTimeoutSeconds { get; set; }

        public bool ToolsEnabled { get; set; }

        public int MaxTurnsPerRun { get; set; }

        public int MaxToolCallsPerTurn { get; set; }

        public int MaxParallelReadTools { get; set; }

        /// <summary>
        /// 会话级审批策略：Ask 每个需要审批的动作都询问；Never 从不询问（自动放行）。
        /// 对齐 DSH 的 approval/policy，风险拦截不受它影响。
        /// </summary>
        public ToolApprovalPolicy ApprovalPolicy { get; set; }

        public ToolOutputSettings ToolOutput { get; set; }

        public bool SkillsEnabled { get; set; }

        public List<string> SkillPaths { get; set; }

        public bool ContextFilesEnabled { get; set; }

        public bool AutoTitle { get; set; }

        /// <summary>
        /// 目标自动续跑：为 true 时，一轮正常结束后若会话目标仍是进行中且还有轮次预算，
        /// 宿主会自动接一轮 continuation。默认关闭——桌面进程不应在用户离开后自行消耗额度。
        /// </summary>
        public bool GoalAutoContinue { get; set; }

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
                ReasoningEffort = ReasoningEffort,
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens,
                HistoryLimit = HistoryLimit,
                MaxConcurrentRequests = MaxConcurrentRequests,
                MaxHotSessions = MaxHotSessions,
                CloseAction = CloseAction,
                RequestTimeoutSeconds = RequestTimeoutSeconds,
                IdleTimeoutSeconds = IdleTimeoutSeconds,
                ToolsEnabled = ToolsEnabled,
                MaxTurnsPerRun = MaxTurnsPerRun,
                MaxToolCallsPerTurn = MaxToolCallsPerTurn,
                MaxParallelReadTools = MaxParallelReadTools,
                ApprovalPolicy = ApprovalPolicy,
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
                GoalAutoContinue = GoalAutoContinue,
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
            value.ReasoningEffort = NormalizeReasoningEffort(value.ReasoningEffort);
            value.HistoryLimit = Clamp(value.HistoryLimit, 10, 5000, 200);
            value.MaxConcurrentRequests = Clamp(value.MaxConcurrentRequests, 1, 8, 4);
            value.MaxHotSessions = Clamp(value.MaxHotSessions, 1, 32, 8);
            if (!Enum.IsDefined(typeof(CloseWindowAction), value.CloseAction))
            {
                value.CloseAction = CloseWindowAction.Ask;
            }

            value.RequestTimeoutSeconds = Clamp(value.RequestTimeoutSeconds, 15, 600, 120);
            value.IdleTimeoutSeconds = Clamp(value.IdleTimeoutSeconds, 10, 600, 60);
            value.MaxTurnsPerRun = Clamp(value.MaxTurnsPerRun, 1, 60, 24);
            value.MaxToolCallsPerTurn = Clamp(value.MaxToolCallsPerTurn, 1, 16, 8);
            value.MaxParallelReadTools = Clamp(value.MaxParallelReadTools, 1, 8, 4);
            if (!Enum.IsDefined(typeof(ToolApprovalPolicy), value.ApprovalPolicy))
            {
                value.ApprovalPolicy = ToolApprovalPolicy.Ask;
            }

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

        /// <summary>空串与未知值都归一成 null（默认档），避免把非法字符串发给 Provider。</summary>
        public static string NormalizeReasoningEffort(string effort)
        {
            if (string.IsNullOrWhiteSpace(effort))
            {
                return null;
            }

            string value = effort.Trim().ToLowerInvariant();
            return string.Equals(value, "low", StringComparison.Ordinal)
                || string.Equals(value, "medium", StringComparison.Ordinal)
                || string.Equals(value, "high", StringComparison.Ordinal)
                ? value
                : null;
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
