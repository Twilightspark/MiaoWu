using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MiaoWu.Core.Agents.Tools
{
    /// <summary>
    /// 工具的风险等级。审批与审计都以此为准，不允许工具自行把高风险降级。
    /// </summary>
    public enum ToolRisk
    {
        /// <summary>只读且限定在工作区内，可自动放行。</summary>
        Low = 0,

        /// <summary>可能改变工作区状态，但影响可逆；默认仍需审批。</summary>
        Medium = 1,

        /// <summary>写文件、执行命令、访问网络等不可轻易撤销的动作。</summary>
        High = 2,

        /// <summary>命中危险命令黑名单或越界，直接拒绝，不进入审批。</summary>
        Blocked = 3
    }

    /// <summary>
    /// 暴露给模型的内置工具名。名字是协议的一部分，与 DSH 的工具词表一致；
    /// 改名等于改协议，旧名不再保留别名。
    /// </summary>
    public static class ToolNames
    {
        public const string Read = "read";
        public const string Write = "write";
        public const string Edit = "edit";
        public const string Glob = "glob";
        public const string Grep = "grep";
        public const string Pwsh = "pwsh";
        public const string Skill = "skill";
        public const string WorkingDirectory = "working_directory";
        public const string WebFetch = "web_fetch";
        public const string WebSearch = "web_search";
        public const string JobList = "job_list";
        public const string JobOutput = "job_output";
        public const string JobKill = "job_kill";
        public const string Present = "present";
        public const string CreateGoal = "create_goal";
        public const string GetGoal = "get_goal";
        public const string UpdateGoal = "update_goal";
        public const string AskUserQuestion = "ask_user_question";
        public const string ExitPlanMode = "exit_plan_mode";
        public const string ScheduleCreate = "schedule_create";
        public const string ScheduleEdit = "schedule_edit";
        public const string ScheduleList = "schedule_list";
        public const string ScheduleView = "schedule_view";
    }

    /// <summary>
    /// 会话级工作目录。命令默认在它下面执行；文件类工具仍以工作区根为基准解析相对路径。
    /// 由会话实现，工具只读当前值并通过 <see cref="TrySet"/> 请求切换。
    /// </summary>
    public interface ISessionWorkingDirectory
    {
        /// <summary>当前工作目录的绝对路径；默认是工作区根。</summary>
        string Current { get; }

        /// <summary>切换工作目录；路径越界或不存在时返回 false。</summary>
        bool TrySet(string absolutePath);
    }

    /// <summary>
    /// 会话级审批策略。对齐 DSH 的 approval/policy：
    /// <see cref="Ask"/> 每个需要审批的动作都询问；<see cref="Never"/> 从不询问，
    /// 需要审批的动作直接放行（风险拦截仍然生效）。
    /// </summary>
    public enum ToolApprovalPolicy
    {
        Ask = 0,

        Never = 1
    }

    /// <summary>模型请求的一次工具调用。ArgumentsJson 保留模型原始参数，不在此处解析。</summary>
    public sealed class ToolCall
    {
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>模型给出的 JSON 对象文本；可能为空、畸形或不是对象。</summary>
        public string ArgumentsJson { get; set; }
    }

    /// <summary>工具执行结果的归一化状态。业务失败不抛异常，一律落到这里。</summary>
    public enum ToolResultStatus
    {
        Succeeded = 0,

        /// <summary>工具已执行但失败（文件不存在、命令退出码非 0 等）。</summary>
        Failed = 1,

        /// <summary>用户拒绝了审批。</summary>
        Denied = 2,

        /// <summary>风险分级直接拦截，没有进入审批。</summary>
        Blocked = 3,

        /// <summary>用户停止、会话关闭或超时。</summary>
        Cancelled = 4,

        /// <summary>参数缺失或类型不对，工具没有执行。</summary>
        InvalidArguments = 5
    }

    /// <summary>
    /// 交给模型与 UI 的工具结果。Content 是模型可见文本，可能来自工作区文件或命令输出，
    /// 因此 <see cref="IsUntrusted"/> 为 true 时必须由上层加上“不可信内容”标记。
    /// </summary>
    public sealed class ToolResult
    {
        public string CallId { get; set; }

        public string ToolName { get; set; }

        public ToolResultStatus Status { get; set; }

        /// <summary>模型可见正文；失败时也可以是可读的错误说明。</summary>
        public string Content { get; set; }

        /// <summary>机器可读的错误码，例如 <c>path_outside_workspace</c>。</summary>
        public string ErrorCode { get; set; }

        /// <summary>给用户看的一句话，不包含堆栈与路径细节。</summary>
        public string ErrorMessage { get; set; }

        /// <summary>结果来自文件、命令输出等不可信来源。</summary>
        public bool IsUntrusted { get; set; }

        /// <summary>结果因大小/条数上限被截断。</summary>
        public bool Truncated { get; set; }

        /// <summary>执行耗时，由调度器回填。</summary>
        public long DurationMilliseconds { get; set; }

        /// <summary>一行摘要（参数或结果），供工具卡与审计日志使用。</summary>
        public string Summary { get; set; }

        /// <summary>写文件前的备份路径；仅 write / edit 成功时填写，供撤销使用。</summary>
        public string BackupPath { get; set; }

        /// <summary>溢出正文的落盘路径；未溢出为 null。</summary>
        public string FullOutputPath { get; set; }

        /// <summary>截断明细，供 UI 显示“已截断”与“查看完整输出”。</summary>
        public ToolOutputTruncation Truncation { get; set; }

        /// <summary>建议结束本 run；整批工具都为 true 时才生效。</summary>
        public bool Terminate { get; set; }

        public bool IsSuccess
        {
            get { return Status == ToolResultStatus.Succeeded; }
        }

        public static ToolResult Success(
            ToolCall call,
            string content,
            string summary,
            bool untrusted,
            bool truncated)
        {
            return new ToolResult
            {
                CallId = call == null ? null : call.Id,
                ToolName = call == null ? null : call.Name,
                Status = ToolResultStatus.Succeeded,
                Content = content ?? string.Empty,
                Summary = summary,
                IsUntrusted = untrusted,
                Truncated = truncated
            };
        }

        public static ToolResult Failure(
            ToolCall call,
            ToolResultStatus status,
            string errorCode,
            string errorMessage)
        {
            return new ToolResult
            {
                CallId = call == null ? null : call.Id,
                ToolName = call == null ? null : call.Name,
                Status = status,
                Content = errorMessage ?? string.Empty,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                IsUntrusted = false
            };
        }
    }

    /// <summary>
    /// 审批通知需要的最小信息。由工具在真正执行前给出，禁止在这里做任何写操作。
    /// </summary>
    public sealed class ToolApprovalPreview
    {
        /// <summary>通知标题，例如“写入文件”或“执行命令”。</summary>
        public string Title { get; set; }

        /// <summary>等宽显示的目标：规范化路径或命令行。</summary>
        public string Target { get; set; }

        /// <summary>影响摘要：行数变化、是否备份、失效条件。</summary>
        public string Summary { get; set; }

        /// <summary>是否允许“本次会话都允许”；命令类工具固定为 false。</summary>
        public bool CanApproveForSession { get; set; }

        /// <summary>是否有 diff 可看（“查看 diff”按钮）。</summary>
        public bool CanShowDiff { get; set; }

        /// <summary>统一 diff 文本，受长度上限保护；没有 diff 时为空。</summary>
        public string Diff { get; set; }

        /// <summary>
        /// 会话级授权的匹配键：写文件用规范化路径，命令为空。
        /// 由工具给出，调度器原样传给审批服务。
        /// </summary>
        public string ScopeKey { get; set; }

        /// <summary>
        /// 工具私有的不透明状态（例如审批时的文件哈希与 mtime），不显示给用户。
        /// 调度器原样回传给执行阶段，用于“审批后文件被外部修改”校验。
        /// </summary>
        public string StateToken { get; set; }
    }

    /// <summary>一次工具执行的上下文。由会话运行时构造，工具只读。</summary>
    public sealed class ToolContext
    {
        public string SessionId { get; set; }

        public string WorkspaceId { get; set; }

        /// <summary>工作区根目录的规范化绝对路径。</summary>
        public string WorkspaceRoot { get; set; }

        /// <summary>该工作区的路径沙箱；工具必须用它解析所有路径。</summary>
        public WorkspacePathSandbox Sandbox { get; set; }

        /// <summary>本会话的审批策略；默认 Ask。</summary>
        public ToolApprovalPolicy ApprovalPolicy { get; set; }

        /// <summary>会话级工作目录；为空时按工作区根处理。</summary>
        public ISessionWorkingDirectory WorkingDirectory { get; set; }

        /// <summary>会话级目标状态；为空时 goal 三件套返回不可用。</summary>
        public MiaoWu.Core.Agents.Goals.ISessionGoalState Goals { get; set; }

        /// <summary>会话级计划模式；为空时 `exit_plan_mode` 返回不可用。</summary>
        public ISessionPlanMode Plan { get; set; }

        /// <summary>本次工具调用的 ID，与 <see cref="ToolCall.Id"/> 相同。</summary>
        public string CallId { get; set; }

        /// <summary>
        /// 审批通过时的预览；自动放行的工具为 null。写文件用它带回
        /// <see cref="ToolApprovalPreview.StateToken"/> 做读后写校验。
        /// </summary>
        public ToolApprovalPreview ApprovalPreview { get; set; }

        public DateTimeOffset Now { get; set; }
    }

    /// <summary>
    /// 一个内置工具。实现不得把异常抛给调度器：可预期失败一律返回结构化 <see cref="ToolResult"/>。
    /// </summary>
    public interface IAgentTool
    {
        string Name { get; }

        /// <summary>给模型看的简短说明，写清楚边界与返回格式。</summary>
        string Description { get; }

        ToolRisk Risk { get; }

        /// <summary>是否必须审批。只读工具为 false；写文件与命令为 true。</summary>
        bool RequiresApproval { get; }

        /// <summary>在执行前给出审批预览；不需要审批的工具可以返回 null。</summary>
        ToolApprovalPreview Describe(ToolCall call, ToolContext context);

        Task<ToolResult> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken);
    }

    /// <summary>风险会随参数变化的工具（例如命令执行）实现此接口。</summary>
    public interface IToolRiskEvaluator
    {
        ToolRiskAssessment AssessRisk(ToolCall call, ToolContext context);
    }

    /// <summary>
    /// 参数里包含大段内容或敏感文本的工具实现此接口，给审计提供安全摘要，
    /// 避免把文件正文、附件内容写进日志。
    /// </summary>
    public interface IToolAuditRedactor
    {
        /// <summary>返回可写入审计的参数摘要；不得包含文件内容、命令输出或密钥。</summary>
        string GetAuditArguments(ToolCall call, ToolContext context);
    }

    /// <summary>一次风险评估的结果。</summary>
    public sealed class ToolRiskAssessment
    {
        public ToolRiskAssessment(ToolRisk risk, string reason)
        {
            Risk = risk;
            Reason = reason ?? string.Empty;
        }

        public ToolRisk Risk { get; }

        /// <summary>给用户与审计看的理由，例如“命中危险命令黑名单：format”。</summary>
        public string Reason { get; }

        public bool IsBlocked
        {
            get { return Risk == ToolRisk.Blocked; }
        }
    }

    /// <summary>按名分发的工具注册表。</summary>
    public interface IToolRegistry
    {
        void Register(IAgentTool tool);

        bool TryGet(string name, out IAgentTool tool);

        IReadOnlyList<IAgentTool> GetAll();
    }

    /// <summary>
    /// 默认注册表：名称按序号忽略大小写比较；重复注册同名工具时后注册的覆盖前者，
    /// 便于测试替换与热插拔内置工具。
    /// </summary>
    public sealed class ToolRegistry : IToolRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, IAgentTool> _tools =
            new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);

        public void Register(IAgentTool tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                throw new ArgumentException("工具名不能为空。", nameof(tool));
            }

            lock (_gate)
            {
                _tools[tool.Name.Trim()] = tool;
            }
        }

        public bool TryGet(string name, out IAgentTool tool)
        {
            tool = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (_gate)
            {
                return _tools.TryGetValue(name.Trim(), out tool);
            }
        }

        public IReadOnlyList<IAgentTool> GetAll()
        {
            lock (_gate)
            {
                var list = new List<IAgentTool>(_tools.Count);
                foreach (IAgentTool tool in _tools.Values)
                {
                    list.Add(tool);
                }

                return list;
            }
        }
    }

    /// <summary>调度器最终执行的决策，供审计与工具卡状态使用。</summary>
    public enum ToolExecutionDecision
    {
        /// <summary>只读工具自动放行。</summary>
        Auto = 0,

        /// <summary>本次审批通过。</summary>
        ApprovedOnce = 1,

        /// <summary>命中本会话已有的审批授权。</summary>
        ApprovedForSession = 2,

        Denied = 3,

        Blocked = 4,

        /// <summary>工具名未知或参数无效，没有执行。</summary>
        Invalid = 5,

        Cancelled = 6
    }

    /// <summary>一次工具执行的完整记录：原始调用、风险、审批决策与结果。</summary>
    public sealed class ToolExecution
    {
        public ToolCall Call { get; set; }

        public ToolRisk Risk { get; set; }

        public ToolExecutionDecision Decision { get; set; }

        public ToolResult Result { get; set; }

        /// <summary>风险理由（来自风险评估器或工具声明）。</summary>
        public string RiskReason { get; set; }

        /// <summary>审批票据；自动放行或直接拦截时为 null。</summary>
        public ToolApprovalTicket Approval { get; set; }

        public string GetAuditDecision()
        {
            switch (Decision)
            {
                case ToolExecutionDecision.Auto:
                    return "auto";
                case ToolExecutionDecision.ApprovedOnce:
                    return "approved_once";
                case ToolExecutionDecision.ApprovedForSession:
                    return "approved_session";
                case ToolExecutionDecision.Denied:
                    return "denied";
                case ToolExecutionDecision.Blocked:
                    return "blocked";
                case ToolExecutionDecision.Cancelled:
                    return "cancelled";
                default:
                    return "invalid";
            }
        }
    }

    public enum ToolExecutionMode
    {
        /// <summary>按注解推导：只读可并行，写/命令串行。</summary>
        Auto = 0,

        Parallel = 1,

        Sequential = 2
    }

    /// <summary>MCP 语义的提示，供审批与 UI 参考；不参与执行。</summary>
    public sealed class ToolAnnotations
    {
        public bool IsReadOnly { get; set; }

        public bool IsDestructive { get; set; }

        public bool IsIdempotent { get; set; }

        public bool IsOpenWorld { get; set; }
    }

    /// <summary>
    /// 工具的可选能力。拆成独立接口而不是往 IAgentTool 加成员，
    /// 避免破坏已实现的工具与测试。
    /// </summary>
    public interface IToolSchemaProvider
    {
        /// <summary>JSON Schema 对象文本；直接嵌入请求，不做二次序列化。</summary>
        string ParametersJson { get; }

        /// <summary>系统提示里的一行，例如“Read file contents”。</summary>
        string PromptSnippet { get; }

        /// <summary>系统提示规则条目，必须自带工具名。</summary>
        IReadOnlyList<string> PromptGuidelines { get; }
    }

    public interface IToolAnnotationsProvider
    {
        ToolAnnotations Annotations { get; }
    }

    /// <summary>运行时看到的不可变工具声明；从注册表构建，按会话缓存。</summary>
    public sealed class ToolDefinition
    {
        public string Name { get; set; }

        public string Description { get; set; }

        public string ParametersJson { get; set; }

        public string PromptSnippet { get; set; }

        public IReadOnlyList<string> PromptGuidelines { get; set; }

        public ToolRisk Risk { get; set; }

        public ToolAnnotations Annotations { get; set; }

        public ToolExecutionMode ExecutionMode { get; set; }

        /// <summary>
        /// 单次调用的超时（毫秒）。0 表示不设超时，由工具自己控制
        /// （例如 pwsh 的 timeoutMs 参数）。对齐 DSH 的 tool.timeoutMs。
        /// </summary>
        public int TimeoutMs { get; set; }
    }

    /// <summary>持久化到 system 消息里的工具声明；不含提示词片段与渲染信息。</summary>
    public sealed class ToolDeclaration
    {
        public string Name { get; set; }

        public string Description { get; set; }

        public string ParametersJson { get; set; }
    }

    public interface IToolDefinitionProvider
    {
        IReadOnlyList<ToolDefinition> GetDefinitions();
    }

    public sealed class ToolOutputTruncation
    {
        public string Content { get; set; }

        public bool Truncated { get; set; }

        /// <summary>"lines" | "bytes" | null。</summary>
        public string TruncatedBy { get; set; }

        public int TotalLines { get; set; }

        public int TotalBytes { get; set; }

        public int OutputLines { get; set; }

        public int OutputBytes { get; set; }

        public bool LastLinePartial { get; set; }

        public bool FirstLineExceedsLimit { get; set; }

        public string FullOutputPath { get; set; }
    }
}
