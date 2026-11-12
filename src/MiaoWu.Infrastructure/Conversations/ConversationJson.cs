using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Conversations
{
    /// <summary>
    /// 会话 JSONL / index.json 的序列化细节。事件与索引都保持向前兼容：
    /// 读取时忽略未知成员与未知事件类型，未知字段不会导致加载失败。
    /// </summary>
    internal static class ConversationJson
    {
        public const int SchemaVersion = 1;

        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public static string FormatTimestamp(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        public static DateTimeOffset ParseTimestamp(JsonValue node, DateTimeOffset fallback)
        {
            if (node == null || node.Kind != JsonKind.String)
            {
                return fallback;
            }

            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(
                node.AsString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed))
            {
                return parsed;
            }

            return fallback;
        }

        public static string GetString(JsonValue node, string name, string fallback = null)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return fallback;
            }

            JsonValue value = node.Get(name);
            if (value == null || value.Kind != JsonKind.String)
            {
                return fallback;
            }

            return value.AsString();
        }

        public static int GetInt(JsonValue node, string name, int fallback = 0)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return fallback;
            }

            JsonValue value = node.Get(name);
            return value == null ? fallback : value.AsInt(fallback);
        }

        public static bool GetBoolean(JsonValue node, string name, bool fallback = false)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return fallback;
            }

            JsonValue value = node.Get(name);
            return value == null ? fallback : value.AsBoolean(fallback);
        }

        public static string BuildEventLine(
            long sequence,
            DateTimeOffset timestamp,
            string type,
            JsonValue body)
        {
            JsonValue root = JsonValue.NewObject();
            root["v"] = JsonValue.From(SchemaVersion);
            root["seq"] = JsonValue.From(sequence);
            root["ts"] = JsonValue.From(FormatTimestamp(timestamp));
            root["type"] = JsonValue.From(type);

            if (body != null && body.Kind == JsonKind.Object)
            {
                foreach (KeyValuePair<string, JsonValue> member in body.Members)
                {
                    root[member.Key] = member.Value;
                }
            }

            return JsonWriter.Write(root);
        }

        public static JsonValue WriteMessage(AgentMessage message)
        {
            JsonValue node = JsonValue.NewObject();
            node["id"] = JsonValue.From(message.Id);
            node["role"] = JsonValue.From(RoleToText(message.Role));
            node["status"] = JsonValue.From(StatusToText(message.Status));
            node["createdAt"] = JsonValue.From(FormatTimestamp(message.CreatedAt));

            if (!string.IsNullOrEmpty(message.ProviderId))
            {
                node["providerId"] = JsonValue.From(message.ProviderId);
            }

            if (!string.IsNullOrEmpty(message.ModelId))
            {
                node["modelId"] = JsonValue.From(message.ModelId);
            }

            JsonValue blocks = JsonValue.NewArray();
            if (message.Blocks != null)
            {
                foreach (AgentContentBlock block in message.Blocks)
                {
                    if (block != null)
                    {
                        blocks.Add(WriteBlock(block));
                    }
                }
            }

            node["blocks"] = blocks;

            if (message.Usage != null)
            {
                node["usage"] = WriteUsage(message.Usage);
            }

            if (message.StopReason.HasValue)
            {
                node["stopReason"] = JsonValue.From(StopReasonToText(message.StopReason.Value));
            }

            if (!string.IsNullOrEmpty(message.ErrorMessage))
            {
                node["error"] = JsonValue.From(message.ErrorMessage);
            }

            AddIfNotEmpty(node, "toolCallId", message.ToolCallId);
            AddIfNotEmpty(node, "toolName", message.ToolName);
            if (message.IsError)
            {
                node["isError"] = JsonValue.From(true);
            }

            return node;
        }

        public static AgentMessage ReadMessage(JsonValue node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return null;
            }

            string id = GetString(node, "id");
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            var message = new AgentMessage
            {
                Id = id,
                Role = ParseRole(GetString(node, "role")),
                Status = ParseStatus(GetString(node, "status")),
                CreatedAt = ParseTimestamp(node.Get("createdAt"), DateTimeOffset.UtcNow),
                ProviderId = GetString(node, "providerId"),
                ModelId = GetString(node, "modelId"),
                Usage = ReadUsage(node.Get("usage")),
                ErrorMessage = GetString(node, "error"),
                ToolCallId = GetString(node, "toolCallId"),
                ToolName = GetString(node, "toolName"),
                IsError = GetBoolean(node, "isError")
            };

            JsonValue stopReason = node.Get("stopReason");
            if (stopReason != null && stopReason.Kind == JsonKind.String)
            {
                message.StopReason = ParseStopReason(stopReason.AsString());
            }

            message.Blocks = ReadBlocks(node.Get("blocks"));
            return message;
        }

        public static JsonValue WriteBlock(AgentContentBlock block)
        {
            JsonValue node = JsonValue.NewObject();
            node["type"] = JsonValue.From(string.IsNullOrEmpty(block.Type) ? "text" : block.Type);

            AddIfNotEmpty(node, "text", block.Text);
            AddIfNotEmpty(node, "thinking", block.Thinking);
            AddIfNotEmpty(node, "mimeType", block.MimeType);
            AddIfNotEmpty(node, "data", block.Data);
            AddIfNotEmpty(node, "toolCallId", block.ToolCallId);
            AddIfNotEmpty(node, "toolName", block.ToolName);
            AddIfNotEmpty(node, "argumentsJson", block.ArgumentsJson);
            AddIfNotEmpty(node, "resultContent", block.ResultContent);
            AddIfNotEmpty(node, "fullOutputPath", block.FullOutputPath);
            AddIfNotEmpty(node, "signature", block.Signature);
            if (block.IsError)
            {
                node["isError"] = JsonValue.From(true);
            }

            if (block.Truncated)
            {
                node["truncated"] = JsonValue.From(true);
            }

            return node;
        }

        public static List<AgentContentBlock> ReadBlocks(JsonValue node)
        {
            var blocks = new List<AgentContentBlock>();
            if (node == null || node.Kind != JsonKind.Array)
            {
                return blocks;
            }

            foreach (JsonValue item in node.Items)
            {
                if (item == null || item.Kind != JsonKind.Object)
                {
                    continue;
                }

                blocks.Add(new AgentContentBlock
                {
                    Type = GetString(item, "type", "text"),
                    Text = GetString(item, "text"),
                    Thinking = GetString(item, "thinking"),
                    MimeType = GetString(item, "mimeType"),
                    Data = GetString(item, "data"),
                    ToolCallId = GetString(item, "toolCallId"),
                    ToolName = GetString(item, "toolName"),
                    ArgumentsJson = GetString(item, "argumentsJson"),
                    ResultContent = GetString(item, "resultContent"),
                    IsError = GetBoolean(item, "isError"),
                    Truncated = GetBoolean(item, "truncated"),
                    FullOutputPath = GetString(item, "fullOutputPath"),
                    Signature = GetString(item, "signature")
                });
            }

            return blocks;
        }

        public static JsonValue WriteUsage(TokenUsage usage)
        {
            JsonValue node = JsonValue.NewObject();
            node["input"] = JsonValue.From(usage.Input);
            node["output"] = JsonValue.From(usage.Output);
            node["cacheRead"] = JsonValue.From(usage.CacheRead);
            node["cacheWrite"] = JsonValue.From(usage.CacheWrite);
            node["total"] = JsonValue.From(usage.GetTotal());
            return node;
        }

        public static TokenUsage ReadUsage(JsonValue node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return null;
            }

            return new TokenUsage
            {
                Input = GetInt(node, "input"),
                Output = GetInt(node, "output"),
                CacheRead = GetInt(node, "cacheRead"),
                CacheWrite = GetInt(node, "cacheWrite"),
                Total = GetInt(node, "total")
            };
        }

        public static JsonValue WriteError(AgentError error)
        {
            JsonValue node = JsonValue.NewObject();
            node["kind"] = JsonValue.From(ErrorKindToText(error.Kind));
            AddIfNotEmpty(node, "code", error.Code);
            AddIfNotEmpty(node, "message", error.Message);
            AddIfNotEmpty(node, "detail", error.Detail);
            node["retryable"] = JsonValue.From(error.Retryable);

            if (error.HttpStatus.HasValue)
            {
                node["httpStatus"] = JsonValue.From(error.HttpStatus.Value);
            }

            if (error.RetryAfter.HasValue)
            {
                node["retryAfterSeconds"] = JsonValue.From(error.RetryAfter.Value.TotalSeconds);
            }

            AddIfNotEmpty(node, "requestId", error.RequestId);
            return node;
        }

        public static AgentError ReadError(JsonValue node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return null;
            }

            var error = new AgentError
            {
                Kind = ParseErrorKind(GetString(node, "kind")),
                Code = GetString(node, "code"),
                Message = GetString(node, "message"),
                Detail = GetString(node, "detail"),
                Retryable = GetBoolean(node, "retryable")
            };

            JsonValue httpStatus = node.Get("httpStatus");
            if (httpStatus != null && httpStatus.Kind == JsonKind.Number)
            {
                error.HttpStatus = httpStatus.AsInt();
            }

            JsonValue retryAfter = node.Get("retryAfterSeconds");
            if (retryAfter != null && retryAfter.Kind == JsonKind.Number)
            {
                error.RetryAfter = TimeSpan.FromSeconds(retryAfter.AsDouble());
            }

            error.RequestId = GetString(node, "requestId");
            return error;
        }

        public static JsonValue WriteIndex(ConversationIndex index)
        {
            JsonValue root = JsonValue.NewObject();
            root["schemaVersion"] = JsonValue.From(
                index.SchemaVersion <= 0 ? SchemaVersion : index.SchemaVersion);

            JsonValue sessions = JsonValue.NewArray();
            if (index.Sessions != null)
            {
                foreach (ConversationIndexEntry entry in index.Sessions)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    JsonValue node = JsonValue.NewObject();
                    node["id"] = JsonValue.From(entry.SessionId);
                    node["workspaceId"] = JsonValue.From(entry.WorkspaceId);
                    node["title"] = JsonValue.From(entry.Title);
                    node["createdAt"] = JsonValue.From(FormatTimestamp(entry.CreatedAt));
                    node["updatedAt"] = JsonValue.From(FormatTimestamp(entry.UpdatedAt));
                    node["pinned"] = JsonValue.From(entry.IsPinned);
                    node["messageCount"] = JsonValue.From(entry.MessageCount);
                    AddIfNotEmpty(node, "lastModel", entry.LastModel);
                    AddIfNotEmpty(node, "preview", entry.Preview);
                    sessions.Add(node);
                }
            }

            root["sessions"] = sessions;
            return root;
        }

        public static ConversationIndex ReadIndex(string text)
        {
            JsonValue root;
            try
            {
                root = JsonParser.Parse(text);
            }
            catch (JsonParseException)
            {
                return null;
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                return null;
            }

            var index = new ConversationIndex
            {
                SchemaVersion = GetInt(root, "schemaVersion", SchemaVersion)
            };

            JsonValue sessions = root.Get("sessions");
            if (sessions == null || sessions.Kind != JsonKind.Array)
            {
                return index;
            }

            foreach (JsonValue node in sessions.Items)
            {
                if (node == null || node.Kind != JsonKind.Object)
                {
                    continue;
                }

                string id = GetString(node, "id");
                if (!AgentWorkspaceLayout.IsValidSessionId(id))
                {
                    continue;
                }

                index.Sessions.Add(new ConversationIndexEntry
                {
                    SessionId = id,
                    WorkspaceId = GetString(node, "workspaceId"),
                    Title = GetString(node, "title"),
                    CreatedAt = ParseTimestamp(node.Get("createdAt"), DateTimeOffset.UtcNow),
                    UpdatedAt = ParseTimestamp(node.Get("updatedAt"), DateTimeOffset.UtcNow),
                    IsPinned = GetBoolean(node, "pinned"),
                    MessageCount = GetInt(node, "messageCount"),
                    LastModel = GetString(node, "lastModel"),
                    Preview = GetString(node, "preview")
                });
            }

            return index;
        }

        public static AgentMessageRole ParseRole(string text)
        {
            if (string.Equals(text, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return AgentMessageRole.Assistant;
            }

            if (string.Equals(text, "toolResult", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "tool", StringComparison.OrdinalIgnoreCase))
            {
                return AgentMessageRole.ToolResult;
            }

            return AgentMessageRole.User;
        }

        public static string RoleToText(AgentMessageRole role)
        {
            switch (role)
            {
                case AgentMessageRole.Assistant:
                    return "assistant";
                case AgentMessageRole.ToolResult:
                    return "toolResult";
                default:
                    return "user";
            }
        }

        public static AgentMessageStatus ParseStatus(string text)
        {
            if (string.Equals(text, "streaming", StringComparison.OrdinalIgnoreCase))
            {
                return AgentMessageStatus.Streaming;
            }

            if (string.Equals(text, "pending", StringComparison.OrdinalIgnoreCase))
            {
                return AgentMessageStatus.Pending;
            }

            if (string.Equals(text, "interrupted", StringComparison.OrdinalIgnoreCase))
            {
                return AgentMessageStatus.Interrupted;
            }

            if (string.Equals(text, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return AgentMessageStatus.Failed;
            }

            return AgentMessageStatus.Completed;
        }

        public static string StatusToText(AgentMessageStatus status)
        {
            switch (status)
            {
                case AgentMessageStatus.Pending:
                    return "pending";
                case AgentMessageStatus.Streaming:
                    return "streaming";
                case AgentMessageStatus.Interrupted:
                    return "interrupted";
                case AgentMessageStatus.Failed:
                    return "failed";
                default:
                    return "completed";
            }
        }

        public static string StopReasonToText(StopReason reason)
        {
            switch (reason)
            {
                case StopReason.Length:
                    return "length";
                case StopReason.ToolUse:
                    return "toolUse";
                case StopReason.Error:
                    return "error";
                case StopReason.Aborted:
                    return "aborted";
                case StopReason.Pending:
                    return "pending";
                default:
                    return "stop";
            }
        }

        public static StopReason ParseStopReason(string text)
        {
            switch ((text ?? string.Empty).ToLowerInvariant())
            {
                case "length":
                    return StopReason.Length;
                case "tooluse":
                    return StopReason.ToolUse;
                case "error":
                    return StopReason.Error;
                case "aborted":
                    return StopReason.Aborted;
                case "pending":
                    return StopReason.Pending;
                default:
                    return StopReason.Stop;
            }
        }

        public static string ErrorKindToText(AgentErrorKind kind)
        {
            switch (kind)
            {
                case AgentErrorKind.Auth:
                    return "auth";
                case AgentErrorKind.Permission:
                    return "permission";
                case AgentErrorKind.NotFound:
                    return "notFound";
                case AgentErrorKind.RateLimited:
                    return "rateLimited";
                case AgentErrorKind.Server:
                    return "server";
                case AgentErrorKind.Timeout:
                    return "timeout";
                case AgentErrorKind.Tls:
                    return "tls";
                case AgentErrorKind.Network:
                    return "network";
                case AgentErrorKind.ContentFilter:
                    return "contentFilter";
                case AgentErrorKind.ContextOverflow:
                    return "contextOverflow";
                case AgentErrorKind.InvalidRequest:
                    return "invalidRequest";
                case AgentErrorKind.Cancelled:
                    return "cancelled";
                case AgentErrorKind.Persistence:
                    return "persistence";
                case AgentErrorKind.Unknown:
                    return "unknown";
                default:
                    return "none";
            }
        }

        public static AgentErrorKind ParseErrorKind(string text)
        {
            switch ((text ?? string.Empty).ToLowerInvariant())
            {
                case "auth":
                    return AgentErrorKind.Auth;
                case "permission":
                    return AgentErrorKind.Permission;
                case "notfound":
                    return AgentErrorKind.NotFound;
                case "ratelimited":
                    return AgentErrorKind.RateLimited;
                case "server":
                    return AgentErrorKind.Server;
                case "timeout":
                    return AgentErrorKind.Timeout;
                case "tls":
                    return AgentErrorKind.Tls;
                case "network":
                    return AgentErrorKind.Network;
                case "contentfilter":
                    return AgentErrorKind.ContentFilter;
                case "contextoverflow":
                    return AgentErrorKind.ContextOverflow;
                case "invalidrequest":
                    return AgentErrorKind.InvalidRequest;
                case "cancelled":
                    return AgentErrorKind.Cancelled;
                case "persistence":
                    return AgentErrorKind.Persistence;
                case "unknown":
                    return AgentErrorKind.Unknown;
                default:
                    return AgentErrorKind.None;
            }
        }

        /// <summary>把文本压成单行并截断，用于索引里的 preview。</summary>
        public static string BuildPreview(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
            bool pendingSpace = false;

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
                if (builder.Length > maxLength)
                {
                    break;
                }
            }

            if (builder.Length > maxLength)
            {
                int cut = maxLength;
                if (cut > 0 && char.IsHighSurrogate(builder[cut - 1]))
                {
                    cut--;
                }

                builder.Length = cut;
                builder.Append('…');
            }

            return builder.ToString();
        }

        private static void AddIfNotEmpty(JsonValue node, string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                node[name] = JsonValue.From(value);
            }
        }
    }
}
