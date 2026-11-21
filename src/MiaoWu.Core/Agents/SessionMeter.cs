using System;
using System.Collections.Generic;
using MiaoWu.Core.Models;

namespace MiaoWu.Core.Agents
{
    /// <summary>
    /// 会话级工作统计：轮 / 步、模型与工具耗时、首 token 与输出速度。
    /// 它数的是"发生了多少工作"，与 <see cref="SessionTokenUsage"/> 的"账单分桶"分工不同。
    /// 计数器在会话内累加并随 <c>session.meter</c> 落盘，分页与压缩不会改变它。
    /// </summary>
    public sealed class SessionStats
    {
        /// <summary>至少关闭过一个步骤的轮次数。</summary>
        public int Turns { get; set; }

        /// <summary>已关闭的步骤数（正常完成与工具后续步都算）。</summary>
        public int Steps { get; set; }

        /// <summary>组装出消息的步骤从开始到完成的累计墙钟时间。</summary>
        public long LlmMs { get; set; }

        /// <summary>工具调用的累计执行时间，来自调度器回填的耗时。</summary>
        public long ToolMs { get; set; }

        /// <summary>首次非空增量到达的累计等待时间。</summary>
        public long TtftMs { get; set; }

        /// <summary>参与首 token 统计的步数。</summary>
        public int TtftSteps { get; set; }

        /// <summary>首个增量到步骤完成的累计时间，只用于算输出速度。</summary>
        public long DecodeMs { get; set; }

        /// <summary>与 <see cref="DecodeMs"/> 同一批步骤的输出 token。</summary>
        public int DecodeTokens { get; set; }

        public bool HasData
        {
            get { return Steps > 0 || Turns > 0; }
        }

        /// <summary>输出速度（token / 秒）；没有有效样本时为 0。</summary>
        public double TokensPerSecond
        {
            get { return DecodeMs > 0 ? DecodeTokens * 1000.0 / DecodeMs : 0; }
        }

        /// <summary>首 token 平均等待；没有样本时为 0。</summary>
        public long AverageTtftMs
        {
            get { return TtftSteps > 0 ? TtftMs / TtftSteps : 0; }
        }

        public SessionStats Clone()
        {
            return new SessionStats
            {
                Turns = Turns,
                Steps = Steps,
                LlmMs = LlmMs,
                ToolMs = ToolMs,
                TtftMs = TtftMs,
                TtftSteps = TtftSteps,
                DecodeMs = DecodeMs,
                DecodeTokens = DecodeTokens
            };
        }

        /// <summary>
        /// 旧会话没有 <c>session.meter</c> 时的兜底：从消息列表折叠轮次、
        /// 步数与模型耗时。工具耗时与首 token 无法从消息恢复，保持为 0。
        /// </summary>
        public static SessionStats FromMessages(IEnumerable<AgentMessage> messages)
        {
            var stats = new SessionStats();
            if (messages == null)
            {
                return stats;
            }

            foreach (AgentMessage message in messages)
            {
                if (message == null)
                {
                    continue;
                }

                if (message.Role == AgentMessageRole.User)
                {
                    stats.Turns++;
                    continue;
                }

                if (message.Role != AgentMessageRole.Assistant)
                {
                    continue;
                }

                stats.Steps++;
                if (message.CompletedAt.HasValue)
                {
                    double elapsed = (message.CompletedAt.Value - message.CreatedAt).TotalMilliseconds;
                    if (elapsed > 0)
                    {
                        stats.LlmMs += (long)elapsed;
                    }
                }
            }

            return stats;
        }
    }

    /// <summary>整份会话的 token 分桶，来自提供方用量样本，全会话累加。</summary>
    public sealed class SessionTokenUsage
    {
        /// <summary>未命中缓存的输入 token。</summary>
        public int UncachedInput { get; set; }

        public int CacheRead { get; set; }

        public int CacheWrite { get; set; }

        public int Output { get; set; }

        /// <summary>会话总 token；等于各分桶之和。</summary>
        public int Total { get; set; }

        public bool HasData
        {
            get { return Total > 0; }
        }

        /// <summary>计费输入 = 未缓存输入 + 缓存读取 + 缓存写入。</summary>
        public int BilledInput
        {
            get { return UncachedInput + CacheRead + CacheWrite; }
        }

        /// <summary>缓存命中率（0–1）；没有输入时为 0。</summary>
        public double CacheHitRate
        {
            get { return BilledInput > 0 ? (double)CacheRead / BilledInput : 0; }
        }

        public void Add(TokenUsage usage)
        {
            if (usage == null)
            {
                return;
            }

            UncachedInput += Math.Max(0, usage.Input);
            CacheRead += Math.Max(0, usage.CacheRead);
            CacheWrite += Math.Max(0, usage.CacheWrite);
            Output += Math.Max(0, usage.Output);
            Total += Math.Max(0, usage.GetTotal());
        }

        public SessionTokenUsage Clone()
        {
            return new SessionTokenUsage
            {
                UncachedInput = UncachedInput,
                CacheRead = CacheRead,
                CacheWrite = CacheWrite,
                Output = Output,
                Total = Total
            };
        }

        /// <summary>旧会话没有落盘用量时的兜底：从带 usage 的助手消息折叠。</summary>
        public static SessionTokenUsage FromMessages(IEnumerable<AgentMessage> messages)
        {
            var usage = new SessionTokenUsage();
            if (messages == null)
            {
                return usage;
            }

            foreach (AgentMessage message in messages)
            {
                if (message != null
                    && message.Role == AgentMessageRole.Assistant
                    && message.Usage != null)
                {
                    usage.Add(message.Usage);
                }
            }

            return usage;
        }
    }

    /// <summary>
    /// 当前上下文占用：下一个请求大约会带多少、窗口多大，以及系统提示词 / 工具定义 /
    /// 对话消息三段启发式构成。构成只用来分比例，不是计费数据。
    /// </summary>
    public sealed class SessionContextUsage
    {
        public int UsedTokens { get; set; }

        /// <summary>模型声明的上下文窗口；未知时为 0，此时界面不显示百分比。</summary>
        public int WindowTokens { get; set; }

        public int SystemTokens { get; set; }

        public int ToolsTokens { get; set; }

        public int MessageTokens { get; set; }

        /// <summary>占用率为 0–100，夹紧到 100。</summary>
        public int Percent
        {
            get
            {
                if (UsedTokens <= 0 || WindowTokens <= 0)
                {
                    return 0;
                }

                double percent = UsedTokens * 100.0 / WindowTokens;
                if (percent >= 100)
                {
                    return 100;
                }

                return (int)Math.Round(percent);
            }
        }

        public bool HasData
        {
            get { return UsedTokens > 0 && WindowTokens > 0; }
        }

        public SessionContextUsage Clone()
        {
            return new SessionContextUsage
            {
                UsedTokens = UsedTokens,
                WindowTokens = WindowTokens,
                SystemTokens = SystemTokens,
                ToolsTokens = ToolsTokens,
                MessageTokens = MessageTokens
            };
        }
    }

    /// <summary>输入框下方三个读数的聚合：会话统计、Token 用量、上下文占用。</summary>
    public sealed class SessionMeter
    {
        public SessionMeter()
        {
            Stats = new SessionStats();
            Usage = new SessionTokenUsage();
            Context = new SessionContextUsage();
        }

        public SessionStats Stats { get; set; }

        public SessionTokenUsage Usage { get; set; }

        public SessionContextUsage Context { get; set; }

        public SessionMeter Clone()
        {
            return new SessionMeter
            {
                Stats = Stats == null ? new SessionStats() : Stats.Clone(),
                Usage = Usage == null ? new SessionTokenUsage() : Usage.Clone(),
                Context = Context == null ? new SessionContextUsage() : Context.Clone()
            };
        }
    }
}
