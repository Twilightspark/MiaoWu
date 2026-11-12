using System;
using System.Collections.Generic;
using System.Security.Authentication;
using System.Text.RegularExpressions;

namespace MiaoWu.Infrastructure.Logging
{
    /// <summary>
    /// 日志/诊断脱敏。所有写盘与导出的文本都先经过这里：
    /// API Key、Authorization、sk- 前缀、私钥、URL 里的 user:pass。
    /// </summary>
    public static class LogSanitizer
    {
        private static readonly Regex SkKey = new Regex(
            @"\b(?:sk|rk|pk)-[A-Za-z0-9_\-]{8,}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex AwsKey = new Regex(
            @"\bAKIA[0-9A-Z]{16}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Bearer = new Regex(
            @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Authorization = new Regex(
            @"(?i)(""?(?:authorization|x-api-key|api[-_]?key|token|password)""?\s*[:=]\s*)(""[^""]*""|[^\s,;]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex UrlCredentials = new Regex(
            @"(?i)\b(https?://)[^/\s:@]+:[^/\s@]+@",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex PrivateKey = new Regex(
            @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string value = text;
            try
            {
                value = PrivateKey.Replace(value, "[private-key]");
                value = Bearer.Replace(value, "Bearer [redacted]");
                value = SkKey.Replace(value, "[api-key]");
                value = AwsKey.Replace(value, "[api-key]");
                value = Authorization.Replace(value, "$1[redacted]");
                value = UrlCredentials.Replace(value, "$1[credentials]@");
            }
            catch (Exception)
            {
                return "[unavailable]";
            }

            return value;
        }

        /// <summary>截断到指定长度，避免把完整响应体写进 UI/日志。</summary>
        public static string Shorten(string text, int maxLength)
        {
            string value = Sanitize(text);
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "…";
        }
    }

    internal static class NetworkErrorText
    {
        public static bool IsTls(Exception exception)
        {
            if (exception == null)
            {
                return false;
            }

            if (exception is AuthenticationException)
            {
                return true;
            }

            string message = exception.Message ?? string.Empty;
            return message.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("证书", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
