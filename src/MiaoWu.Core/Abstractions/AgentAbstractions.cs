using System;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Core.Abstractions
{
    /// <summary>
    /// 运行时日志。只写事件名、会话 ID、消息 ID、耗时与错误分类；
    /// 不得写 prompt、回复正文、API Key 或 Authorization。
    /// </summary>
    public interface IAgentLogger
    {
        void Info(string eventName, string message);

        void Warn(string eventName, string message);

        void Error(string eventName, string message, Exception exception);
    }

    /// <summary>
    /// 工具执行入口。实现放在 Infrastructure（AgentToolDispatcher），
    /// Core 的会话 loop 只依赖这个接口，因此可以在测试里替换。
    /// </summary>
    public interface IToolExecutor
    {
        Task<ToolExecution> ExecuteAsync(
            ToolCall call,
            ToolContext context,
            CancellationToken cancellationToken);
    }

    /// <summary>空日志实现，供测试与未装配日志的宿主使用。</summary>
    public sealed class NullAgentLogger : IAgentLogger
    {
        public static readonly NullAgentLogger Instance = new NullAgentLogger();

        public void Info(string eventName, string message)
        {
        }

        public void Warn(string eventName, string message)
        {
        }

        public void Error(string eventName, string message, Exception exception)
        {
        }
    }
}
