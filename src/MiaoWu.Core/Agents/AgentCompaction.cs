using System;

namespace MiaoWu.Core.Agents
{
    /// <summary>
    /// 一次对话压缩的结果。消息本身不删：界面上仍能回看，只是下一次请求不再把它们
    /// 逐条送给模型，而是用 <see cref="Summary"/> 代替。
    /// </summary>
    public sealed class AgentCompaction
    {
        /// <summary>模型写的结构化摘要（六段：目标 / 进展 / 决定 / 文件 / 待办 / 风险）。</summary>
        public string Summary { get; set; }

        /// <summary>压缩时消息列表里已被摘要覆盖的消息总数（绝对条数）。</summary>
        public int CoveredMessages { get; set; }

        /// <summary>被覆盖的最后一条消息 id；重放时优先按它定位切点，避免条数漂移。</summary>
        public string LastCoveredMessageId { get; set; }

        public DateTimeOffset CompactedAt { get; set; }

        public AgentCompaction Clone()
        {
            return new AgentCompaction
            {
                Summary = Summary,
                CoveredMessages = CoveredMessages,
                LastCoveredMessageId = LastCoveredMessageId,
                CompactedAt = CompactedAt
            };
        }
    }

    /// <summary>`/compact` 的返回值；失败时带一句可展示的中文原因。</summary>
    public sealed class AgentCompactionResult
    {
        public bool Succeeded { get; set; }

        public string Summary { get; set; }

        /// <summary>本次新收进摘要的消息条数。</summary>
        public int CoveredMessages { get; set; }

        public string Error { get; set; }

        public static AgentCompactionResult Success(string summary, int coveredMessages)
        {
            return new AgentCompactionResult
            {
                Succeeded = true,
                Summary = summary,
                CoveredMessages = coveredMessages
            };
        }

        public static AgentCompactionResult Failure(string error)
        {
            return new AgentCompactionResult
            {
                Succeeded = false,
                Error = error ?? "压缩失败。"
            };
        }
    }
}
