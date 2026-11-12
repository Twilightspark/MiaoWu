using System;
using System.Collections.Generic;
using MiaoWu.Core.Models;

namespace MiaoWu.Core.Agents
{
    public enum AgentMessageRole
    {
        User,
        Assistant,

        /// <summary>工具结果消息。不直接显示为气泡，渲染成工具卡。</summary>
        ToolResult
    }

    /// <summary>
    /// 消息状态。磁盘上只持久化 Pending / Streaming / Completed / Failed；
    /// 加载时仍为 Streaming 的助手消息会被重放为 <see cref="Interrupted"/>。
    /// </summary>
    public enum AgentMessageStatus
    {
        Pending,
        Streaming,
        Completed,
        Interrupted,
        Failed
    }

    /// <summary>
    /// 消息内容块。P3 只使用 text / thinking；image 与 toolCall 为 P3.5 / P4 预留，
    /// 序列化时保留未知字段，避免旧版本读写新版本会话时丢数据。
    /// </summary>
    public sealed class AgentContentBlock
    {
        /// <summary>"text" | "thinking" | "image" | "toolCall"。</summary>
        public string Type { get; set; }

        public string Text { get; set; }

        public string Thinking { get; set; }

        public string MimeType { get; set; }

        public string Data { get; set; }

        public string ToolCallId { get; set; }

        public string ToolName { get; set; }

        public string ArgumentsJson { get; set; }

        /// <summary>toolResult 块：结果正文、是否错误、截断与完整输出路径。</summary>
        public string ResultContent { get; set; }

        public bool IsError { get; set; }

        public bool Truncated { get; set; }

        public string FullOutputPath { get; set; }

        /// <summary>thinking signature / thoughtSignature，原样回放。</summary>
        public string Signature { get; set; }

        public AgentContentBlock Clone()
        {
            return new AgentContentBlock
            {
                Type = Type,
                Text = Text,
                Thinking = Thinking,
                MimeType = MimeType,
                Data = Data,
                ToolCallId = ToolCallId,
                ToolName = ToolName,
                ArgumentsJson = ArgumentsJson,
                ResultContent = ResultContent,
                IsError = IsError,
                Truncated = Truncated,
                FullOutputPath = FullOutputPath,
                Signature = Signature
            };
        }
    }

    /// <summary>
    /// 会话中的一条消息。助手占位先以 Streaming 落盘，完成/失败时再追加
    /// 权威内容，因此“正在生成”的消息在崩溃后可以恢复成“已中断”。
    /// </summary>
    public sealed class AgentMessage
    {
        public AgentMessage()
        {
            Blocks = new List<AgentContentBlock>();
            CreatedAt = DateTimeOffset.UtcNow;
            Status = AgentMessageStatus.Pending;
        }

        public string Id { get; set; }

        public AgentMessageRole Role { get; set; }

        public AgentMessageStatus Status { get; set; }

        public List<AgentContentBlock> Blocks { get; set; }

        public string ProviderId { get; set; }

        public string ModelId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public TokenUsage Usage { get; set; }

        public StopReason? StopReason { get; set; }

        /// <summary>失败时给用户看的短句；详细诊断在会话事件里。</summary>
        public string ErrorMessage { get; set; }

        /// <summary>role = ToolResult 时必填，关联助手消息里的 ToolCall.Id。</summary>
        public string ToolCallId { get; set; }

        public string ToolName { get; set; }

        public bool IsError { get; set; }

        public AgentMessage Clone()
        {
            var copy = new AgentMessage
            {
                Id = Id,
                Role = Role,
                Status = Status,
                ProviderId = ProviderId,
                ModelId = ModelId,
                CreatedAt = CreatedAt,
                Usage = Usage == null ? null : Usage.Clone(),
                StopReason = StopReason,
                ErrorMessage = ErrorMessage,
                ToolCallId = ToolCallId,
                ToolName = ToolName,
                IsError = IsError,
                Blocks = new List<AgentContentBlock>()
            };

            if (Blocks != null)
            {
                foreach (AgentContentBlock block in Blocks)
                {
                    if (block != null)
                    {
                        copy.Blocks.Add(block.Clone());
                    }
                }
            }

            return copy;
        }

        /// <summary>拼接所有文本块，供标题、预览与纯文本导出使用。</summary>
        public string GetText()
        {
            if (Blocks == null || Blocks.Count == 0)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (AgentContentBlock block in Blocks)
            {
                if (block == null)
                {
                    continue;
                }

                if (string.Equals(block.Type, "text", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(block.Text))
                {
                    builder.Append(block.Text);
                }
                else if (string.Equals(block.Type, "toolResult", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(block.ResultContent))
                {
                    builder.Append(block.ResultContent);
                }
            }

            return builder.ToString();
        }

        public static AgentMessage CreateUser(string id, string text)
        {
            var message = new AgentMessage
            {
                Id = id,
                Role = AgentMessageRole.User,
                Status = AgentMessageStatus.Completed
            };

            message.Blocks.Add(new AgentContentBlock
            {
                Type = "text",
                Text = text ?? string.Empty
            });

            return message;
        }

        public static AgentMessage CreateAssistantPlaceholder(
            string id,
            string providerId,
            string modelId)
        {
            return new AgentMessage
            {
                Id = id,
                Role = AgentMessageRole.Assistant,
                Status = AgentMessageStatus.Streaming,
                ProviderId = providerId,
                ModelId = modelId
            };
        }

        public static AgentMessage CreateToolResult(
            string id,
            string toolCallId,
            string toolName,
            string content,
            bool isError,
            bool truncated,
            string fullOutputPath)
        {
            var message = new AgentMessage
            {
                Id = id,
                Role = AgentMessageRole.ToolResult,
                Status = AgentMessageStatus.Completed,
                ToolCallId = toolCallId,
                ToolName = toolName,
                IsError = isError
            };

            message.Blocks.Add(new AgentContentBlock
            {
                Type = "toolResult",
                ToolCallId = toolCallId,
                ToolName = toolName,
                ResultContent = content ?? string.Empty,
                IsError = isError,
                Truncated = truncated,
                FullOutputPath = fullOutputPath
            });

            return message;
        }
    }

    /// <summary>
    /// UI 交给运行时的一条输入。ClientMessageId 由 UI 生成，用于乐观回显去重。
    /// </summary>
    public sealed class AgentInput
    {
        public string ClientMessageId { get; set; }

        public List<AgentContentBlock> Blocks { get; set; }

        /// <summary>为空表示使用会话默认 Provider。</summary>
        public string ProviderId { get; set; }

        /// <summary>为空表示使用会话默认模型。</summary>
        public string ModelId { get; set; }

        public string GetText()
        {
            if (Blocks == null)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (AgentContentBlock block in Blocks)
            {
                if (block != null && !string.IsNullOrEmpty(block.Text))
                {
                    builder.Append(block.Text);
                }
            }

            return builder.ToString();
        }
    }
}
