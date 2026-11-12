using System;
using MiaoWu.Core.Abstractions;

namespace MiaoWu.Infrastructure
{
    /// <summary>生产用时钟：统一返回 UTC 时间。</summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public DateTimeOffset UtcNow
        {
            get { return DateTimeOffset.UtcNow; }
        }
    }
}
