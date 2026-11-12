namespace MiaoWu.Core.Models
{
    /// <summary>
    /// 一次生成结束的原因。与具体协议无关，由 Provider 映射层归一化。
    /// </summary>
    public enum StopReason
    {
        /// <summary>尚未结束（占位或流式进行中）。</summary>
        Pending,

        /// <summary>正常结束。</summary>
        Stop,

        /// <summary>达到输出 token 上限。</summary>
        Length,

        /// <summary>模型请求调用工具（P4 才执行）。</summary>
        ToolUse,

        /// <summary>服务方返回错误后结束。</summary>
        Error,

        /// <summary>用户取消或被中断。</summary>
        Aborted
    }

    /// <summary>
    /// 一次请求的 token 用量。Provider 返回的实际值优先；
    /// 没有实际值时本地估算器只填 <see cref="Input"/> / <see cref="Output"/>。
    /// </summary>
    public sealed class TokenUsage
    {
        public int Input { get; set; }

        public int Output { get; set; }

        public int CacheRead { get; set; }

        public int CacheWrite { get; set; }

        /// <summary>服务方给出的总量；为 0 时按分项求和。</summary>
        public int Total { get; set; }

        public int GetTotal()
        {
            if (Total > 0)
            {
                return Total;
            }

            return Input + Output + CacheRead + CacheWrite;
        }

        public TokenUsage Clone()
        {
            return new TokenUsage
            {
                Input = Input,
                Output = Output,
                CacheRead = CacheRead,
                CacheWrite = CacheWrite,
                Total = Total
            };
        }
    }
}
