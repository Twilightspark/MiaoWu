using System;
using System.Collections.Generic;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Providers
{
    /// <summary>把归一化请求写成两种协议的 JSON 请求体。手写 JSON，无第三方依赖。</summary>
    public static class LlmRequestJson
    {
        public static string BuildOpenAiBody(ModelDescriptor model, LlmRequest request)
        {
            JsonValue body = JsonValue.NewObject();
            body["model"] = JsonValue.From(model.ModelId);
            body["stream"] = JsonValue.From(true);

            OpenAiCompat compat = model.Compat ?? new OpenAiCompat();
            if (compat.SupportsUsageInStreaming)
            {
                JsonValue options = JsonValue.NewObject();
                options["include_usage"] = JsonValue.From(true);
                body["stream_options"] = options;
            }

            if (request.Temperature.HasValue)
            {
                body["temperature"] = JsonValue.From(request.Temperature.Value);
            }

            int? maxTokens = request.MaxOutputTokens ?? model.MaxOutputTokens;
            if (maxTokens.HasValue && maxTokens.Value > 0)
            {
                body[string.IsNullOrWhiteSpace(compat.MaxTokensField)
                    ? "max_tokens"
                    : compat.MaxTokensField] = JsonValue.From(maxTokens.Value);
            }

            JsonValue messages = JsonValue.NewArray();
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                JsonValue system = JsonValue.NewObject();
                system["role"] = JsonValue.From(string.IsNullOrWhiteSpace(compat.SystemRole)
                    ? "system"
                    : compat.SystemRole);
                system["content"] = JsonValue.From(request.SystemPrompt);
                messages.Add(system);
            }

            foreach (LlmMessage message in request.Messages ?? new List<LlmMessage>())
            {
                if (message == null)
                {
                    continue;
                }

                switch (message.Role)
                {
                    case "assistant":
                        messages.Add(WriteOpenAiAssistant(message));
                        break;
                    case "tool":
                        messages.Add(WriteOpenAiTool(message));
                        break;
                    default:
                        messages.Add(WriteOpenAiUser(message));
                        break;
                }
            }

            body["messages"] = messages;

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = JsonValue.NewArray();
                foreach (ToolDeclaration tool in request.Tools)
                {
                    if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
                    {
                        continue;
                    }

                    JsonValue function = JsonValue.NewObject();
                    function["name"] = JsonValue.From(tool.Name);
                    function["description"] = JsonValue.From(tool.Description ?? string.Empty);
                    function["parameters"] = ParseSchema(tool.ParametersJson);

                    JsonValue item = JsonValue.NewObject();
                    item["type"] = JsonValue.From("function");
                    item["function"] = function;
                    tools.Add(item);
                }

                if (tools.Items.Count > 0)
                {
                    body["tools"] = tools;
                    body["tool_choice"] = JsonValue.From(
                        string.IsNullOrWhiteSpace(request.ToolChoice) ? "auto" : request.ToolChoice);
                }
            }

            return JsonWriter.Write(body);
        }

        private static JsonValue WriteOpenAiUser(LlmMessage message)
        {
            JsonValue node = JsonValue.NewObject();
            node["role"] = JsonValue.From("user");

            bool hasImage = false;
            foreach (LlmContent content in message.Content)
            {
                if (content != null && string.Equals(content.Type, "image", StringComparison.Ordinal))
                {
                    hasImage = true;
                    break;
                }
            }

            if (!hasImage)
            {
                node["content"] = JsonValue.From(message.GetPlainText());
                return node;
            }

            var parts = JsonValue.NewArray();
            foreach (LlmContent content in message.Content)
            {
                if (content == null)
                {
                    continue;
                }

                if (string.Equals(content.Type, "text", StringComparison.Ordinal))
                {
                    JsonValue part = JsonValue.NewObject();
                    part["type"] = JsonValue.From("text");
                    part["text"] = JsonValue.From(content.Text ?? string.Empty);
                    parts.Add(part);
                }
                else if (string.Equals(content.Type, "image", StringComparison.Ordinal))
                {
                    JsonValue image = JsonValue.NewObject();
                    image["url"] = JsonValue.From("data:" + (content.MimeType ?? "image/png")
                        + ";base64," + (content.Data ?? string.Empty));

                    JsonValue part = JsonValue.NewObject();
                    part["type"] = JsonValue.From("image_url");
                    part["image_url"] = image;
                    parts.Add(part);
                }
            }

            node["content"] = parts;
            return node;
        }

        private static JsonValue WriteOpenAiAssistant(LlmMessage message)
        {
            JsonValue node = JsonValue.NewObject();
            node["role"] = JsonValue.From("assistant");

            string text = message.GetPlainText();
            node["content"] = string.IsNullOrEmpty(text) ? JsonValue.Null : JsonValue.From(text);

            var toolCalls = JsonValue.NewArray();
            foreach (LlmContent content in message.Content)
            {
                if (content == null || !string.Equals(content.Type, "toolCall", StringComparison.Ordinal))
                {
                    continue;
                }

                JsonValue function = JsonValue.NewObject();
                function["name"] = JsonValue.From(content.ToolName ?? string.Empty);
                function["arguments"] = JsonValue.From(
                    string.IsNullOrWhiteSpace(content.ArgumentsJson) ? "{}" : content.ArgumentsJson);

                JsonValue call = JsonValue.NewObject();
                call["id"] = JsonValue.From(content.ToolCallId ?? string.Empty);
                call["type"] = JsonValue.From("function");
                call["function"] = function;
                toolCalls.Add(call);
            }

            if (toolCalls.Items.Count > 0)
            {
                node["tool_calls"] = toolCalls;
            }

            return node;
        }

        private static JsonValue WriteOpenAiTool(LlmMessage message)
        {
            JsonValue node = JsonValue.NewObject();
            node["role"] = JsonValue.From("tool");
            node["tool_call_id"] = JsonValue.From(message.ToolCallId ?? string.Empty);
            node["content"] = JsonValue.From(message.GetPlainText());
            if (!string.IsNullOrWhiteSpace(message.ToolName))
            {
                node["name"] = JsonValue.From(message.ToolName);
            }

            return node;
        }

        public static string BuildAnthropicBody(ModelDescriptor model, LlmRequest request)
        {
            JsonValue body = JsonValue.NewObject();
            body["model"] = JsonValue.From(model.ModelId);
            body["stream"] = JsonValue.From(true);

            int maxTokens = request.MaxOutputTokens ?? model.MaxOutputTokens;
            if (maxTokens <= 0)
            {
                maxTokens = 4096;
            }

            body["max_tokens"] = JsonValue.From(maxTokens);

            if (request.Temperature.HasValue)
            {
                body["temperature"] = JsonValue.From(request.Temperature.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                body["system"] = JsonValue.From(request.SystemPrompt);
            }

            JsonValue messages = JsonValue.NewArray();
            string currentRole = null;
            JsonValue current = null;

            foreach (LlmMessage message in request.Messages ?? new List<LlmMessage>())
            {
                if (message == null)
                {
                    continue;
                }

                string role = message.Role == "assistant" ? "assistant" : "user";
                JsonValue content = BuildAnthropicContent(message);

                if (currentRole == role && current != null)
                {
                    JsonValue existing = current.Get("content");
                    if (existing != null && existing.Kind == JsonKind.Array
                        && content.Kind == JsonKind.Array && role == "user")
                    {
                        foreach (JsonValue block in content.Items)
                        {
                            existing.Add(block);
                        }

                        continue;
                    }
                }

                JsonValue node = JsonValue.NewObject();
                node["role"] = JsonValue.From(role);
                node["content"] = content;
                messages.Add(node);
                currentRole = role;
                current = node;
            }

            body["messages"] = messages;

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = JsonValue.NewArray();
                foreach (ToolDeclaration tool in request.Tools)
                {
                    if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
                    {
                        continue;
                    }

                    JsonValue item = JsonValue.NewObject();
                    item["name"] = JsonValue.From(tool.Name);
                    item["description"] = JsonValue.From(tool.Description ?? string.Empty);
                    item["input_schema"] = ParseSchema(tool.ParametersJson);
                    tools.Add(item);
                }

                if (tools.Items.Count > 0)
                {
                    body["tools"] = tools;
                    JsonValue choice = JsonValue.NewObject();
                    choice["type"] = JsonValue.From("auto");
                    body["tool_choice"] = choice;
                }
            }

            return JsonWriter.Write(body);
        }

        private static JsonValue BuildAnthropicContent(LlmMessage message)
        {
            var blocks = JsonValue.NewArray();

            foreach (LlmContent content in message.Content)
            {
                if (content == null)
                {
                    continue;
                }

                switch (content.Type)
                {
                    case "text":
                        if (!string.IsNullOrEmpty(content.Text))
                        {
                            JsonValue text = JsonValue.NewObject();
                            text["type"] = JsonValue.From("text");
                            text["text"] = JsonValue.From(content.Text);
                            blocks.Add(text);
                        }

                        break;
                    case "thinking":
                        if (!string.IsNullOrEmpty(content.Thinking))
                        {
                            JsonValue thinking = JsonValue.NewObject();
                            thinking["type"] = JsonValue.From("thinking");
                            thinking["thinking"] = JsonValue.From(content.Thinking);
                            if (!string.IsNullOrEmpty(content.Signature))
                            {
                                thinking["signature"] = JsonValue.From(content.Signature);
                            }

                            blocks.Add(thinking);
                        }

                        break;
                    case "toolCall":
                        JsonValue toolUse = JsonValue.NewObject();
                        toolUse["type"] = JsonValue.From("tool_use");
                        toolUse["id"] = JsonValue.From(content.ToolCallId ?? string.Empty);
                        toolUse["name"] = JsonValue.From(content.ToolName ?? string.Empty);
                        toolUse["input"] = ParseSchema(content.ArgumentsJson);
                        blocks.Add(toolUse);
                        break;
                    case "toolResult":
                        JsonValue toolResult = JsonValue.NewObject();
                        toolResult["type"] = JsonValue.From("tool_result");
                        toolResult["tool_use_id"] = JsonValue.From(message.ToolCallId ?? string.Empty);
                        toolResult["content"] = JsonValue.From(message.GetPlainText());
                        if (message.GetPlainText().StartsWith("[工具返回错误]", StringComparison.Ordinal))
                        {
                            toolResult["is_error"] = JsonValue.From(true);
                        }

                        blocks.Add(toolResult);
                        break;
                    case "image":
                        JsonValue source = JsonValue.NewObject();
                        source["type"] = JsonValue.From("base64");
                        source["media_type"] = JsonValue.From(content.MimeType ?? "image/png");
                        source["data"] = JsonValue.From(content.Data ?? string.Empty);

                        JsonValue image = JsonValue.NewObject();
                        image["type"] = JsonValue.From("image");
                        image["source"] = source;
                        blocks.Add(image);
                        break;
                }
            }

            if (blocks.Items.Count == 0)
            {
                JsonValue empty = JsonValue.NewObject();
                empty["type"] = JsonValue.From("text");
                empty["text"] = JsonValue.From(message.Role == "tool" ? "(空结果)" : string.Empty);
                blocks.Add(empty);
            }

            return blocks;
        }

        private static JsonValue ParseSchema(string json)
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    JsonValue parsed = JsonParser.Parse(json);
                    if (parsed != null && parsed.Kind == JsonKind.Object)
                    {
                        return parsed;
                    }
                }
                catch (Exception)
                {
                }
            }

            JsonValue fallback = JsonValue.NewObject();
            fallback["type"] = JsonValue.From("object");
            fallback["properties"] = JsonValue.NewObject();
            return fallback;
        }
    }
}
