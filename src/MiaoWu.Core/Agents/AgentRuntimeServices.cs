using System;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;

namespace MiaoWu.Core.Agents
{
    /// <summary>
    /// 会话 loop 的外部依赖。全部是 Core 接口，因此可以在 xUnit 里用
    /// ScriptedModelApi + 临时目录跑完整 loop，不需要 UI 或网络。
    /// </summary>
    public sealed class AgentSessionServices
    {
        public IModelRegistry Models { get; set; }

        public IConversationStore Store { get; set; }

        public IAgentLogger Logger { get; set; }

        public IToolExecutor Tools { get; set; }

        public IToolDefinitionProvider ToolDefinitions { get; set; }

        public IAgentErrorClassifier ErrorClassifier { get; set; }

        public IClock Clock { get; set; }

        /// <summary>
        /// 全局共享的模型选择；为空时每个会话保留自己的 provider / model
        /// （测试与嵌入宿主可以不给）。给了之后所有会话共用同一份配置。
        /// </summary>
        public ModelSelection ModelSelection { get; set; }

        /// <summary>按工作区根创建路径沙箱；为空时工具回合被禁用。</summary>
        public Func<string, WorkspacePathSandbox> SandboxFactory { get; set; }
    }

    public sealed class AgentRuntimeOptions
    {
        public AgentRuntimeOptions()
        {
            MaxHotSessions = 8;
            MaxConcurrentRequests = 4;
        }

        public int MaxHotSessions { get; set; }

        public int MaxConcurrentRequests { get; set; }
    }
}
