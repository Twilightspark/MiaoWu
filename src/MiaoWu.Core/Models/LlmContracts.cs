using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Errors;

namespace MiaoWu.Core.Models
{
    /// <summary>协议无关的一条内容块。</summary>
    public sealed class LlmContent
    {
        /// <summary>"text" | "thinking" | "toolCall" | "image"。</summary>
        public string Type { get; set; }

        public string Text { get; set; }

        public string Thinking { get; set; }

        /// <summary>thinking signature / thoughtSignature，原样回放。</summary>
        public string Signature { get; set; }

        public string ToolCallId { get; set; }

        public string ToolName { get; set; }

        public string ArgumentsJson { get; set; }

        public string MimeType { get; set; }

        /// <summary>base64，仅 image。</summary>
        public string Data { get; set; }
    }

    /// <summary>发给模型的一条消息。</summary>
    public sealed class LlmMessage
    {
        public LlmMessage()
        {
            Content = new List<LlmContent>();
        }

        /// <summary>"user" | "assistant" | "tool"。</summary>
        public string Role { get; set; }

        public List<LlmContent> Content { get; set; }

        /// <summary>role = "tool" 时必填。</summary>
        public string ToolCallId { get; set; }

        public string ToolName { get; set; }

        public string GetPlainText()
        {
            if (Content == null)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (LlmContent content in Content)
            {
                if (content != null
                    && string.Equals(content.Type, "text", StringComparison.Ordinal))
                {
                    builder.Append(content.Text);
                }
            }

            return builder.ToString();
        }
    }

    public sealed class LlmRequest
    {
        public LlmRequest()
        {
            Messages = new List<LlmMessage>();
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string SystemPrompt { get; set; }

        public List<LlmMessage> Messages { get; set; }

        public double? Temperature { get; set; }

        public int? MaxOutputTokens { get; set; }

        /// <summary>传给支持 prompt cache 的 Provider。</summary>
        public string SessionId { get; set; }

        public Dictionary<string, string> Headers { get; set; }

        public string ReasoningEffort { get; set; }

        public List<ToolDeclaration> Tools { get; set; }

        /// <summary>"auto" | "none"。</summary>
        public string ToolChoice { get; set; }
    }

    public sealed class LlmResponse
    {
        public string ProviderId { get; set; }

        public string ModelId { get; set; }

        public string ResponseId { get; set; }

        public List<LlmContent> Content { get; set; }

        public TokenUsage Usage { get; set; }

        public StopReason StopReason { get; set; }

        public string RawStopReason { get; set; }

        public string ErrorMessage { get; set; }
    }

    public abstract class LlmStreamEvent
    {
        public int ContentIndex { get; set; }
    }

    public sealed class LlmStarted : LlmStreamEvent
    {
        public LlmResponse Partial { get; set; }
    }

    public sealed class LlmTextDelta : LlmStreamEvent
    {
        public string Delta { get; set; }
    }

    public sealed class LlmThinkingDelta : LlmStreamEvent
    {
        public string Delta { get; set; }
    }

    public sealed class LlmToolCallDelta : LlmStreamEvent
    {
        public string ToolCallId { get; set; }

        public string ToolName { get; set; }

        public string ArgumentsDelta { get; set; }
    }

    public sealed class LlmCompleted : LlmStreamEvent
    {
        public LlmResponse Response { get; set; }
    }

    public sealed class LlmFailed : LlmStreamEvent
    {
        public AgentError Error { get; set; }

        public LlmResponse Partial { get; set; }
    }

    /// <summary>一条模型流。终止事件之后 ReadAsync 返回 null；不向调用方抛业务异常。</summary>
    public interface ILlmStream : IDisposable
    {
        Task<LlmStreamEvent> ReadAsync(CancellationToken cancellationToken);
    }

    public interface IModelApi
    {
        string Id { get; }

        /// <summary>
        /// 打开一条流。本方法不阻塞、不抛协议错误；
        /// 连接/鉴权/HTTP 失败以 LlmFailed 事件返回。
        /// </summary>
        ILlmStream Open(
            ModelDescriptor model,
            LlmRequest request,
            ProviderCredential credential,
            CancellationToken cancellationToken);
    }

    public interface IModelProvider
    {
        string Id { get; }

        string DisplayName { get; }

        string ApiId { get; }

        string BaseUrl { get; }

        IReadOnlyList<ModelDescriptor> Models { get; }
    }

    public interface IModelRegistry
    {
        IReadOnlyList<ModelDescriptor> Models { get; }

        IReadOnlyList<ProviderDefinition> Providers { get; }

        ModelDescriptor Resolve(string providerId, string modelId);

        ModelDescriptor GetDefault();

        IModelApi GetApi(string apiId);

        /// <summary>未配置返回 null；不抛异常。</summary>
        Task<ProviderCredential> ResolveCredentialAsync(
            string providerId,
            CancellationToken cancellationToken);

        event EventHandler Changed;
    }

    public sealed class ProviderCredential
    {
        public string ProviderId { get; set; }

        public string ApiKey { get; set; }

        public string BaseUrl { get; set; }

        public Dictionary<string, string> Headers { get; set; }

        /// <summary>"stored" | "environment" | "explicit" | "none"。</summary>
        public string Source { get; set; }

        public bool HasKey
        {
            get { return !string.IsNullOrWhiteSpace(ApiKey); }
        }
    }
}
