using System;
using System.Collections.Generic;
using System.Text;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>OpenAI-compatible SSE 映射。文本块 0、思考块 1、工具调用从 2 开始。</summary>
    public sealed class OpenAiStreamMapper : ILlmStreamMapper
    {
        private sealed class ToolAccumulator
        {
            public string Id;
            public string Name;
            public readonly StringBuilder Arguments = new StringBuilder();
        }

        private readonly StringBuilder _text = new StringBuilder();
        private readonly StringBuilder _thinking = new StringBuilder();
        private readonly SortedDictionary<int, ToolAccumulator> _tools =
            new SortedDictionary<int, ToolAccumulator>();

        private string _responseId;
        private string _modelId;
        private string _finishReason;
        private TokenUsage _usage;
        private string _signature;

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

            JsonValue error = root.Get("error");
            if (error != null && error.Kind != JsonKind.Null)
            {
                events.Add(new LlmFailed
                {
                    Error = ReadError(error)
                });
                return events;
            }

            _responseId = ReadString(root, "id") ?? _responseId;
            _modelId = ReadString(root, "model") ?? _modelId;

            TokenUsage usage = ReadUsage(root.Get("usage"));
            if (usage != null)
            {
                _usage = usage;
            }

            JsonValue choices = root.Get("choices");
            if (choices == null || choices.Kind != JsonKind.Array || choices.Items.Count == 0)
            {
                return events;
            }

            JsonValue choice = choices.Items[0];
            if (choice == null || choice.Kind != JsonKind.Object)
            {
                return events;
            }

            string finish = ReadString(choice, "finish_reason");
            if (!string.IsNullOrEmpty(finish))
            {
                _finishReason = finish;
            }

            JsonValue delta = choice.Get("delta");
            if (delta != null && delta.Kind == JsonKind.Object)
            {
                ReadDelta(delta, events);
            }

            return events;
        }

        private void ReadDelta(JsonValue delta, List<LlmStreamEvent> events)
        {
            JsonValue content = delta.Get("content");
            if (content != null && content.Kind == JsonKind.String)
            {
                string text = content.AsString();
                if (!string.IsNullOrEmpty(text))
                {
                    _text.Append(text);
                    events.Add(new LlmTextDelta { ContentIndex = 0, Delta = text });
                }
            }

            JsonValue thinking = delta.Get("reasoning_content")
                ?? delta.Get("reasoning")
                ?? delta.Get("thinking");
            if (thinking != null && thinking.Kind == JsonKind.String)
            {
                string text = thinking.AsString();
                if (!string.IsNullOrEmpty(text))
                {
                    _thinking.Append(text);
                    events.Add(new LlmThinkingDelta { ContentIndex = 1, Delta = text });
                }
            }

            JsonValue signature = delta.Get("signature");
            if (signature != null && signature.Kind == JsonKind.String)
            {
                _signature = signature.AsString();
            }

            JsonValue toolCalls = delta.Get("tool_calls");
            if (toolCalls != null && toolCalls.Kind == JsonKind.Array)
            {
                for (int i = 0; i < toolCalls.Items.Count; i++)
                {
                    JsonValue call = toolCalls.Items[i];
                    if (call == null || call.Kind != JsonKind.Object)
                    {
                        continue;
                    }

                    int index = i;
                    JsonValue indexValue = call.Get("index");
                    if (indexValue != null)
                    {
                        index = indexValue.AsInt(i);
                    }

                    ToolAccumulator accumulator;
                    if (!_tools.TryGetValue(index, out accumulator))
                    {
                        accumulator = new ToolAccumulator();
                        _tools[index] = accumulator;
                    }

                    string id = ReadString(call, "id");
                    if (!string.IsNullOrEmpty(id))
                    {
                        accumulator.Id = id;
                    }

                    JsonValue function = call.Get("function");
                    if (function != null && function.Kind == JsonKind.Object)
                    {
                        string name = ReadString(function, "name");
                        if (!string.IsNullOrEmpty(name))
                        {
                            accumulator.Name = name;
                        }

                        JsonValue arguments = function.Get("arguments");
                        if (arguments != null && arguments.Kind == JsonKind.String)
                        {
                            string fragment = arguments.AsString();
                            if (!string.IsNullOrEmpty(fragment))
                            {
                                accumulator.Arguments.Append(fragment);
                                events.Add(new LlmToolCallDelta
                                {
                                    ContentIndex = 2 + index,
                                    ToolCallId = accumulator.Id,
                                    ToolName = accumulator.Name,
                                    ArgumentsDelta = fragment
                                });
                            }
                        }
                    }
                }
            }
        }

        public LlmStreamEvent Finish()
        {
            var response = new LlmResponse
            {
                ProviderId = null,
                ModelId = _modelId,
                ResponseId = _responseId,
                Content = new List<LlmContent>(),
                Usage = _usage ?? new TokenUsage(),
                StopReason = MapStopReason(_finishReason),
                RawStopReason = _finishReason
            };

            if (_text.Length > 0)
            {
                response.Content.Add(new LlmContent { Type = "text", Text = _text.ToString() });
            }

            if (_thinking.Length > 0)
            {
                response.Content.Add(new LlmContent
                {
                    Type = "thinking",
                    Thinking = _thinking.ToString(),
                    Signature = _signature
                });
            }

            foreach (KeyValuePair<int, ToolAccumulator> pair in _tools)
            {
                if (string.IsNullOrWhiteSpace(pair.Value.Name))
                {
                    continue;
                }

                response.Content.Add(new LlmContent
                {
                    Type = "toolCall",
                    ToolCallId = pair.Value.Id,
                    ToolName = pair.Value.Name,
                    ArgumentsJson = pair.Value.Arguments.Length == 0
                        ? "{}"
                        : pair.Value.Arguments.ToString()
                });
            }

            return new LlmCompleted { Response = response };
        }

        private static StopReason MapStopReason(string finishReason)
        {
            if (string.IsNullOrEmpty(finishReason))
            {
                return StopReason.Stop;
            }

            switch (finishReason)
            {
                case "length":
                    return StopReason.Length;
                case "tool_calls":
                case "function_call":
                    return StopReason.ToolUse;
                case "content_filter":
                    return StopReason.Error;
                default:
                    return StopReason.Stop;
            }
        }

        private static AgentError ReadError(JsonValue error)
        {
            string code = ReadString(error, "code") ?? ReadString(error, "type");
            string message = ReadString(error, "message") ?? "模型返回错误。";

            var result = new AgentError
            {
                Kind = AgentErrorKind.Server,
                Code = code,
                Message = message,
                Detail = message
            };

            string lowered = ((code ?? string.Empty) + " " + (message ?? string.Empty)).ToLowerInvariant();
            if (lowered.Contains("rate_limit") || lowered.Contains("too many requests"))
            {
                result.Kind = AgentErrorKind.RateLimited;
                result.Retryable = true;
            }
            else if (lowered.Contains("auth") || lowered.Contains("api key") || lowered.Contains("unauthorized"))
            {
                result.Kind = AgentErrorKind.Auth;
            }

            return result;
        }

        private static TokenUsage ReadUsage(JsonValue usage)
        {
            if (usage == null || usage.Kind != JsonKind.Object)
            {
                return null;
            }

            var result = new TokenUsage
            {
                Output = ReadInt(usage, "completion_tokens")
            };

            JsonValue promptDetails = usage.Get("prompt_tokens_details");
            if (promptDetails != null && promptDetails.Kind == JsonKind.Object)
            {
                result.CacheRead = ReadInt(promptDetails, "cached_tokens");
            }

            // OpenAI 的 prompt_tokens 含缓存命中；本仓库统一用「未缓存输入」口径，
            // 否则会话 Token 用量里的缓存读取会被重复计算。
            result.Input = Math.Max(0, ReadInt(usage, "prompt_tokens") - result.CacheRead);

            result.Total = ReadInt(usage, "total_tokens");
            return result;
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
