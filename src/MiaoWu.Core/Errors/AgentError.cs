using System;

namespace MiaoWu.Core.Errors
{
    /// <summary>
    /// 归一化后的智能体错误类别。UI 按类别给出“发生了什么、怎么办”，
    /// 不直接展示 Provider 原始响应体。
    /// </summary>
    public enum AgentErrorKind
    {
        None,
        Auth,
        Permission,
        NotFound,
        RateLimited,
        Server,
        Timeout,
        Tls,
        Network,
        ContentFilter,
        ContextOverflow,
        InvalidRequest,
        Cancelled,
        Persistence,
        Unknown
    }

    /// <summary>
    /// 一次运行失败的结构化描述。<see cref="Detail"/> 必须已脱敏，
    /// 不允许出现 API Key、Authorization 或完整响应体。
    /// </summary>
    public sealed class AgentError
    {
        public AgentErrorKind Kind { get; set; }

        /// <summary>Provider 原始错误码，如 invalid_api_key；可为空。</summary>
        public string Code { get; set; }

        /// <summary>给用户看的中文短句。</summary>
        public string Message { get; set; }

        /// <summary>已脱敏的诊断信息。</summary>
        public string Detail { get; set; }

        public bool Retryable { get; set; }

        public int? HttpStatus { get; set; }

        public TimeSpan? RetryAfter { get; set; }

        public string RequestId { get; set; }

        public AgentError Clone()
        {
            return new AgentError
            {
                Kind = Kind,
                Code = Code,
                Message = Message,
                Detail = Detail,
                Retryable = Retryable,
                HttpStatus = HttpStatus,
                RetryAfter = RetryAfter,
                RequestId = RequestId
            };
        }

        public static AgentError Create(AgentErrorKind kind, string message)
        {
            return new AgentError
            {
                Kind = kind,
                Message = message
            };
        }
    }

    /// <summary>loop 内部用于把结构化错误抛到统一 catch，不跨层暴露。</summary>
    public sealed class AgentRunException : Exception
    {
        public AgentRunException(AgentError error)
            : base(error == null ? "运行失败。" : error.Message)
        {
            Error = error;
        }

        public AgentError Error { get; private set; }
    }

    /// <summary>把意外异常映射成 AgentError；实现放在 Infrastructure，Core 只依赖接口。</summary>
    public interface IAgentErrorClassifier
    {
        AgentError Classify(Exception exception);
    }
}
