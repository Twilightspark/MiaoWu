using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using MiaoWu.Core.Errors;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Logging;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>
    /// 把异常与 HTTP 状态映射成 AgentError。分类顺序：
    /// HttpStatus → Provider code/type → 文本关键词 → Unknown。
    /// </summary>
    public sealed class AgentErrorClassifier : IAgentErrorClassifier
    {
        public AgentError Classify(Exception exception)
        {
            if (exception == null)
            {
                return AgentError.Create(AgentErrorKind.Unknown, "发生未知错误。");
            }

            var runException = exception as AgentRunException;
            if (runException != null)
            {
                return runException.Error;
            }

            if (exception is OperationCanceledException)
            {
                return AgentError.Create(AgentErrorKind.Cancelled, "已取消。");
            }

            if (exception is TimeoutException)
            {
                return new AgentError
                {
                    Kind = AgentErrorKind.Timeout,
                    Message = "请求超时，可重试或调大超时时间。",
                    Detail = LogSanitizer.Shorten(exception.Message, 200),
                    Retryable = true
                };
            }

            if (NetworkErrorText.IsTls(exception))
            {
                return new AgentError
                {
                    Kind = AgentErrorKind.Tls,
                    Message = "TLS 连接失败，检查系统时间、根证书或代理。",
                    Detail = LogSanitizer.Shorten(exception.Message, 200),
                    Retryable = false
                };
            }

            if (exception is SocketException || exception is HttpRequestException)
            {
                return new AgentError
                {
                    Kind = AgentErrorKind.Network,
                    Message = "网络不可用，请检查连接或代理设置。",
                    Detail = LogSanitizer.Shorten(exception.Message, 200),
                    Retryable = true
                };
            }

            if (exception is UnauthorizedAccessException)
            {
                return new AgentError
                {
                    Kind = AgentErrorKind.Persistence,
                    Message = "没有权限写入本地文件。",
                    Detail = LogSanitizer.Shorten(exception.Message, 200),
                    Retryable = false
                };
            }

            return new AgentError
            {
                Kind = AgentErrorKind.Unknown,
                Message = "发生未知错误，请查看日志。",
                Detail = LogSanitizer.Shorten(exception.Message, 300),
                Retryable = false
            };
        }

        public static AgentError FromHttp(int status, string body, string requestId)
        {
            string providerCode;
            string providerMessage;
            ParseErrorBody(body, out providerCode, out providerMessage);

            var error = new AgentError
            {
                HttpStatus = status,
                Code = providerCode,
                RequestId = requestId,
                Detail = LogSanitizer.Shorten(providerMessage, 300)
            };

            if (status == 401)
            {
                error.Kind = AgentErrorKind.Auth;
                error.Message = "API Key 无效或已过期，请打开设置检查。";
                return error;
            }

            if (status == 403)
            {
                error.Kind = AgentErrorKind.Permission;
                error.Message = "没有权限访问该模型。";
                return error;
            }

            if (status == 404)
            {
                error.Kind = AgentErrorKind.NotFound;
                error.Message = "模型或端点不可用，请检查模型 ID 与 Base URL。";
                return error;
            }

            if (status == 408)
            {
                error.Kind = AgentErrorKind.Timeout;
                error.Message = "请求超时，可重试。";
                error.Retryable = true;
                return error;
            }

            if (status == 409)
            {
                error.Kind = AgentErrorKind.InvalidRequest;
                error.Message = "请求与当前会话状态冲突，请重试。";
                return error;
            }

            if (status == 429)
            {
                error.Kind = AgentErrorKind.RateLimited;
                error.Message = "请求过于频繁，稍后会自动重试。";
                error.Retryable = true;
                return error;
            }

            if (status >= 500)
            {
                error.Kind = AgentErrorKind.Server;
                error.Message = "服务暂时不可用，稍后会自动重试。";
                error.Retryable = true;
                return error;
            }

            if (status == 400)
            {
                string text = ((providerCode ?? string.Empty) + " " + (providerMessage ?? string.Empty))
                    .ToLowerInvariant();

                if (text.Contains("context_length")
                    || text.Contains("maximum context")
                    || text.Contains("too many tokens")
                    || text.Contains("上下文"))
                {
                    error.Kind = AgentErrorKind.ContextOverflow;
                    error.Message = "上下文超出模型上限，请新建会话或减少历史。";
                    return error;
                }

                if (text.Contains("content_filter") || text.Contains("content policy"))
                {
                    error.Kind = AgentErrorKind.ContentFilter;
                    error.Message = "内容被服务方过滤。";
                    return error;
                }

                error.Kind = AgentErrorKind.InvalidRequest;
                error.Message = "请求无效：" + (string.IsNullOrWhiteSpace(providerMessage)
                    ? "请检查模型参数。"
                    : LogSanitizer.Shorten(providerMessage, 160));
                return error;
            }

            error.Kind = AgentErrorKind.Unknown;
            error.Message = "请求失败（HTTP " + status + "）。";
            return error;
        }

        private static void ParseErrorBody(string body, out string code, out string message)
        {
            code = null;
            message = null;
            if (string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            try
            {
                JsonValue root = JsonParser.Parse(body);
                if (root == null || root.Kind != JsonKind.Object)
                {
                    message = body;
                    return;
                }

                JsonValue error = root.Get("error");
                if (error != null && error.Kind == JsonKind.Object)
                {
                    code = error.Get("code") == null ? null : error.Get("code").AsString(null);
                    if (string.IsNullOrWhiteSpace(code) && error.Get("type") != null)
                    {
                        code = error.Get("type").AsString(null);
                    }

                    message = error.Get("message") == null ? null : error.Get("message").AsString(null);
                }
                else if (error != null && error.Kind == JsonKind.String)
                {
                    message = error.AsString();
                }
                else
                {
                    code = root.Get("code") == null ? null : root.Get("code").AsString(null);
                    message = root.Get("message") == null ? null : root.Get("message").AsString(null);
                }
            }
            catch (Exception)
            {
                message = body;
            }
        }
    }
}
