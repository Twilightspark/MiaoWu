using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>
    /// 供应商请求头装饰。目前只服务 OpenCode Console（含 Go 套餐 / 按量付费）：
    /// 网关要求每条推理请求带一个稳定的 <c>x-opencode-session</c>，用于会话粘性路由与
    /// prompt 缓存；缺少时返回 400「Request is missing x-opencode-session and cannot be
    /// routed efficiently」。详见 https://opencode.ai/docs/console/go/#where-can-i-use-it 。
    /// </summary>
    /// <remarks>
    /// 取值按 OpenCode 原生会话 ID 形状生成（<c>ses_</c> + 12 位时间编码 hex + 14 位 base62），
    /// 每个会话第一次请求时铸造一次并在进程内复用，因此同一会话的每一轮都落在同一路由上。
    /// 供应商自己的请求头优先：显式配置了同名头就不再追加。
    /// </remarks>
    public static class ProviderRequestHeaders
    {
        /// <summary>OpenCode 网关的会话头名称。</summary>
        public const string OpenCodeSessionHeader = "x-opencode-session";

        private const string OpenCodeSessionPrefix = "ses_";
        private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        private const int CounterMask = 0xFFF;
        private const ulong TimeMask = 0xFFFFFFFFFFFFUL;

        private static readonly object Gate = new object();
        private static readonly object RngGate = new object();
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        /// <summary>会话 ID → 请求头取值；空键给“没有会话身份”的请求（如连接测试）共用。</summary>
        private static readonly Dictionary<string, string> Sessions =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static int _counter;

        /// <summary>主机是 opencode.ai 或其子域时返回 true；非法 URL 返回 false。</summary>
        public static bool IsOpenCodeEndpoint(string baseUrl)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out uri))
            {
                return false;
            }

            string host = uri.Host;
            return string.Equals(host, "opencode.ai", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".opencode.ai", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 给请求追加供应商自己的头，再按需追加 <c>x-opencode-session</c>。
        /// 显式头（<paramref name="explicitHeaders"/>）里已有同名头时以显式值为准。
        /// </summary>
        public static void Apply(
            HttpRequestMessage request,
            string baseUrl,
            string sessionId,
            IDictionary<string, string> explicitHeaders)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (explicitHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in explicitHeaders)
                {
                    if (!string.IsNullOrWhiteSpace(header.Key))
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            if (!IsOpenCodeEndpoint(baseUrl) || request.Headers.Contains(OpenCodeSessionHeader))
            {
                return;
            }

            request.Headers.TryAddWithoutValidation(OpenCodeSessionHeader, ResolveSessionId(sessionId));
        }

        /// <summary>
        /// 会话对应的头取值：同一会话恒定；没有会话 ID 时整进程共用一个。
        /// </summary>
        public static string ResolveSessionId(string sessionId)
        {
            string key = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim();

            lock (Gate)
            {
                string value;
                if (!Sessions.TryGetValue(key, out value))
                {
                    value = NewSessionId();
                    Sessions[key] = value;
                }

                return value;
            }
        }

        /// <summary>
        /// 生成 OpenCode 原生形状的会话 ID：<c>ses_</c> + 12 位 hex（时间戳的降序编码）
        /// + 14 位 base62 随机后缀，总长与 opencode 自己的 <c>ses_</c> ID 一致。
        /// </summary>
        private static string NewSessionId()
        {
            int counter = Interlocked.Increment(ref _counter) & CounterMask;
            long milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // OpenCode 用 ~(时间毫秒 * 0x1000 + 计数器) 的低 48 位做 12 位 hex，
            // 这样 ID 字典序与时间相反，但仍是合法的时间编码值。
            ulong encoded = ~(unchecked((ulong)milliseconds) * 0x1000UL + (ulong)counter);
            string time = (encoded & TimeMask).ToString("x12");

            return OpenCodeSessionPrefix + time + RandomBase62(14);
        }

        private static string RandomBase62(int length)
        {
            var bytes = new byte[length];
            lock (RngGate)
            {
                Rng.GetBytes(bytes);
            }

            var builder = new StringBuilder(length);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(Base62[bytes[i] % Base62.Length]);
            }

            return builder.ToString();
        }
    }
}
