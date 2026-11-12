using System;
using System.Collections.Generic;
using System.Text;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>Anthropic Messages SSE 映射：content_block_* 事件。</summary>
    public sealed class AnthropicStreamMapper : ILlmStreamMapper
    {
        private sealed class Block
        {
            public string Type;
            public string Id;
            public string Name;
            public readonly StringBuilder Text = new StringBuilder();
            public string Signature;
        }

        private readonly List<Block> _blocks = new List<Block>();
        private string _responseId;
        private string _modelId;
        private string _stopReason;
        private TokenUsage _usage = new TokenUsage();

        public IEnumerable<LlmStreamEvent> Map(SseFrame frame)
        {
            var events = new List<LlmStreamEvent>();
            if (frame == null || string.IsNullOrWhiteSpace(frame.Data))
            {
                return events;
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(frame.Data);
            }
            catch (Exception)
            {
                return events;
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                return events;
            }

            string type = ReadString(root, "type") ?? frame.Event;

            switch (type)
            {
                case "message_start":
                {
                    JsonValue message = root.Get("message");
                    if (message != null && message.Kind == JsonKind.Object)
                    {
                        _responseId = ReadString(message, "id") ?? _responseId;
                        _modelId = ReadString(message, "model") ?? _modelId;
                        TokenUsage usage = ReadUsage(message.Get("usage"));
                        if (usage != null)
                        {
                            _usage.Input = usage.Input;
                            _usage.CacheRead = usage.CacheRead;
                            _usage.CacheWrite = usage.CacheWrite;
                        }
                    }

                    break;
                }

                case "content_block_start":
                {
                    int index = ReadInt(root, "index");
                    JsonValue blockValue = root.Get("content_block");
                    if (blockValue == null || blockValue.Kind != JsonKind.Object)
                    {
                        break;
                    }

                    Block block = EnsureBlock(index);
                    block.Type = ReadString(blockValue, "type") ?? "text";
                    block.Id = ReadString(blockValue, "id");
                    block.Name = ReadString(blockValue, "name");

                    if (string.Equals(block.Type, "tool_use", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(block.Name))
                    {
                        events.Add(new LlmToolCallDelta
                        {
                            ContentIndex = index,
                            ToolCallId = block.Id,
                            ToolName = block.Name,
                            ArgumentsDelta = string.Empty
                        });
                    }

                    JsonValue initialText = blockValue.Get("text");
                    if (initialText != null && initialText.Kind == JsonKind.String)
                    {
                        string text = initialText.AsString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            block.Text.Append(text);
                            events.Add(new LlmTextDelta { ContentIndex = index, Delta = text });
                        }
                    }

                    break;
                }

                case "content_block_delta":
                {
                    int index = ReadInt(root, "index");
                    Block block = EnsureBlock(index);
                    JsonValue delta = root.Get("delta");
                    if (delta == null || delta.Kind != JsonKind.Object)
                    {
                        break;
                    }

                    string deltaType = ReadString(delta, "type") ?? string.Empty;
                    if (deltaType == "text_delta")
                    {
                        string text = ReadString(delta, "text") ?? string.Empty;
                        if (text.Length > 0)
                        {
                            block.Text.Append(text);
                            events.Add(new LlmTextDelta { ContentIndex = index, Delta = text });
                        }
                    }
                    else if (deltaType == "thinking_delta")
                    {
                        string thinking = ReadString(delta, "thinking") ?? string.Empty;
                        if (thinking.Length > 0)
                        {
                            block.Text.Append(thinking);
                            events.Add(new LlmThinkingDelta { ContentIndex = index, Delta = thinking });
                        }
                    }
                    else if (deltaType == "signature_delta")
                    {
                        block.Signature = ReadString(delta, "signature");
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        string partial = ReadString(delta, "partial_json") ?? string.Empty;
                        if (partial.Length > 0)
                        {
                            block.Text.Append(partial);
                            events.Add(new LlmToolCallDelta
                            {
                                ContentIndex = index,
                                ToolCallId = block.Id,
                                ToolName = block.Name,
                                ArgumentsDelta = partial
                            });
                        }
                    }

                    break;
                }

                case "message_delta":
                {
                    JsonValue delta = root.Get("delta");
                    if (delta != null && delta.Kind == JsonKind.Object)
                    {
                        _stopReason = ReadString(delta, "stop_reason") ?? _stopReason;
                    }

                    TokenUsage usage = ReadUsage(root.Get("usage"));
                    if (usage != null && usage.Output > 0)
                    {
                        _usage.Output = usage.Output;
                    }

                    break;
                }

                case "error":
                {
                    JsonValue error = root.Get("error");
                    events.Add(new LlmFailed { Error = ReadError(error) });
                    break;
                }
            }

            return events;
        }

        public LlmStreamEvent Finish()
        {
            var response = new LlmResponse
            {
                ModelId = _modelId,
                ResponseId = _responseId,
                Content = new List<LlmContent>(),
                Usage = _usage,
                StopReason = MapStopReason(_stopReason),
                RawStopReason = _stopReason
            };

            foreach (Block block in _blocks)
            {
                if (block == null)
                {
                    continue;
                }

                switch (block.Type)
                {
                    case "thinking":
                        if (block.Text.Length > 0)
                        {
                            response.Content.Add(new LlmContent
                            {
                                Type = "thinking",
                                Thinking = block.Text.ToString(),
                                Signature = block.Signature
                            });
                        }

                        break;
                    case "tool_use":
                        response.Content.Add(new LlmContent
                        {
                            Type = "toolCall",
                            ToolCallId = block.Id,
                            ToolName = block.Name,
                            ArgumentsJson = block.Text.Length == 0 ? "{}" : block.Text.ToString()
                        });
                        break;
                    default:
                        if (block.Text.Length > 0)
                        {
                            response.Content.Add(new LlmContent
                            {
                                Type = "text",
                                Text = block.Text.ToString()
                            });
                        }

                        break;
                }
            }

            return new LlmCompleted { Response = response };
        }

        private Block EnsureBlock(int index)
        {
            while (_blocks.Count <= index)
            {
                _blocks.Add(new Block { Type = "text" });
            }

            Block block = _blocks[index];
            if (block == null)
            {
                block = new Block { Type = "text" };
                _blocks[index] = block;
            }

            return block;
        }

        private static StopReason MapStopReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return StopReason.Stop;
            }

            switch (reason)
            {
                case "max_tokens":
                    return StopReason.Length;
                case "tool_use":
                    return StopReason.ToolUse;
                case "refusal":
                    return StopReason.Error;
                default:
                    return StopReason.Stop;
            }
        }

        private static AgentError ReadError(JsonValue error)
        {
            string type = error == null ? null : ReadString(error, "type");
            string message = error == null ? null : ReadString(error, "message");

            var result = new AgentError
            {
                Kind = AgentErrorKind.Server,
                Code = type,
                Message = string.IsNullOrWhiteSpace(message) ? "模型返回错误。" : message,
                Detail = message
            };

            if (string.Equals(type, "rate_limit_error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "overloaded_error", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = AgentErrorKind.RateLimited;
                result.Retryable = true;
            }
            else if (string.Equals(type, "authentication_error", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = AgentErrorKind.Auth;
            }
            else if (string.Equals(type, "permission_error", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = AgentErrorKind.Permission;
            }
            else if (string.Equals(type, "invalid_request_error", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = AgentErrorKind.InvalidRequest;
            }

            return result;
        }

        private static TokenUsage ReadUsage(JsonValue usage)
        {
            if (usage == null || usage.Kind != JsonKind.Object)
            {
                return null;
            }

            return new TokenUsage
            {
                Input = ReadInt(usage, "input_tokens"),
                Output = ReadInt(usage, "output_tokens"),
                CacheRead = ReadInt(usage, "cache_read_input_tokens"),
                CacheWrite = ReadInt(usage, "cache_creation_input_tokens")
            };
        }

        private static string ReadString(JsonValue parent, string key)
        {
            JsonValue value = parent == null ? null : parent.Get(key);
            return value == null || value.Kind != JsonKind.String ? null : value.AsString();
        }

        private static int ReadInt(JsonValue parent, string key)
        {
            JsonValue value = parent == null ? null : parent.Get(key);
            return value == null || value.Kind != JsonKind.Number ? 0 : value.AsInt(0);
        }
    }
}
