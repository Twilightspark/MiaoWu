using System;

namespace MiaoWu.Core.Abstractions
{
    /// <summary>
    /// 可替换的时钟：测试用假时钟，生产用系统 UTC 时间。
    /// 会话事件的时间戳统一用 UTC，展示时再转本地时间。
    /// </summary>
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }
}
