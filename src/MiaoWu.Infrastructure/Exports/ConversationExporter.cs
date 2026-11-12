using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Conversations;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Logging;

namespace MiaoWu.Infrastructure.Exports
{
    /// <summary>
    /// 会话导出：Markdown + JSON（+ 图片附件目录）。
    /// 不包含 API Key、Authorization 或 Provider 请求头。
    /// </summary>
    public sealed class ConversationExporter
    {
        private readonly string _exportsDirectory;

        public ConversationExporter(string exportsDirectory)
        {
            if (string.IsNullOrWhiteSpace(exportsDirectory))
            {
                throw new ArgumentException("导出目录不能为空。", nameof(exportsDirectory));
            }

            _exportsDirectory = exportsDirectory;
        }

        public string ExportSession(ConversationTranscript transcript, string workspacePath)
        {
            if (transcript == null)
            {
                throw new ArgumentNullException(nameof(transcript));
            }

            string label = string.IsNullOrWhiteSpace(transcript.Title) ? "会话" : transcript.Title;
            string folder = CreateUniqueDirectory(label);
            WriteMarkdown(Path.Combine(folder, "conversation.md"), new[] { transcript }, workspacePath);
            WriteJson(Path.Combine(folder, "conversation.json"), new[] { transcript }, workspacePath);
            WriteAttachments(folder, new[] { transcript });
            return folder;
        }

        public string ExportAll(IEnumerable<ConversationTranscript> transcripts, string workspacePath)
        {
            var list = new List<ConversationTranscript>();
            if (transcripts != null)
            {
                foreach (ConversationTranscript transcript in transcripts)
                {
                    if (transcript != null)
                    {
                        list.Add(transcript);
                    }
                }
            }

            string folder = CreateUniqueDirectory("全部会话");
            WriteMarkdown(Path.Combine(folder, "conversations.md"), list, workspacePath);
            WriteJson(Path.Combine(folder, "conversations.json"), list, workspacePath);
            WriteAttachments(folder, list);
            return folder;
        }

        private string CreateUniqueDirectory(string label)
        {
            Directory.CreateDirectory(_exportsDirectory);
            string safe = SanitizeFileName(label);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string baseName = safe + "-" + stamp;
            string target = Path.Combine(_exportsDirectory, baseName);
            int index = 2;
            while (Directory.Exists(target))
            {
                target = Path.Combine(_exportsDirectory, baseName + "-" + index);
                index++;
            }

            Directory.CreateDirectory(target);
            return target;
        }

        private static void WriteMarkdown(
            string path,
            IReadOnlyList<ConversationTranscript> transcripts,
            string workspacePath)
        {
            var builder = new StringBuilder();
            bool first = true;

            foreach (ConversationTranscript transcript in transcripts)
            {
                if (!first)
                {
                    builder.AppendLine();
                    builder.AppendLine("---");
                    builder.AppendLine();
                }

                first = false;
                builder.Append("# ")
                    .AppendLine(string.IsNullOrWhiteSpace(transcript.Title) ? "会话" : transcript.Title);
                builder.AppendLine();
                builder.Append("- 会话 ID：").AppendLine(transcript.SessionId);
                if (!string.IsNullOrWhiteSpace(workspacePath))
                {
                    builder.Append("- 工作区：").AppendLine(workspacePath);
                }

                builder.Append("- 模型：")
                    .AppendLine((transcript.ProviderId ?? "-") + "/" + (transcript.ModelId ?? "-"));
                builder.Append("- 创建：")
                    .AppendLine(transcript.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                builder.Append("- 更新：")
                    .AppendLine(transcript.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                builder.AppendLine();

                foreach (AgentMessage message in transcript.Messages ?? new List<AgentMessage>())
                {
                    AppendMessage(builder, message);
                }
            }

            File.WriteAllText(
                path,
                LogSanitizer.Sanitize(builder.ToString()),
                new UTF8Encoding(false));
        }

        private static void AppendMessage(StringBuilder builder, AgentMessage message)
        {
            if (message == null)
            {
                return;
            }

            switch (message.Role)
            {
                case AgentMessageRole.User:
                    builder.AppendLine("## 用户");
                    builder.AppendLine();
                    builder.AppendLine(message.GetText());
                    builder.AppendLine();
                    break;

                case AgentMessageRole.Assistant:
                    builder.AppendLine("## 助手");
                    if (!string.IsNullOrWhiteSpace(message.ModelId))
                    {
                        builder.AppendLine();
                        builder.Append("> 模型：").Append(message.ModelId);
                        if (message.Usage != null)
                        {
                            builder.Append(" · tokens ").Append(message.Usage.GetTotal());
                        }

                        builder.AppendLine();
                    }

                    builder.AppendLine();
                    builder.AppendLine(message.GetText());
                    builder.AppendLine();

                    if (message.Blocks != null)
                    {
                        foreach (AgentContentBlock block in message.Blocks)
                        {
                            if (block != null
                                && string.Equals(block.Type, "toolCall", StringComparison.Ordinal))
                            {
                                builder.Append("> 调用工具：`")
                                    .Append(block.ToolName ?? "?")
                                    .Append("` ")
                                    .AppendLine(LogSanitizer.Shorten(block.ArgumentsJson, 300));
                            }
                        }
                    }

                    if (message.Status == AgentMessageStatus.Interrupted)
                    {
                        builder.AppendLine();
                        builder.AppendLine("> 已中断。");
                    }

                    if (message.Status == AgentMessageStatus.Failed && !string.IsNullOrEmpty(message.ErrorMessage))
                    {
                        builder.AppendLine();
                        builder.Append("> 失败：").AppendLine(message.ErrorMessage);
                    }

                    builder.AppendLine();
                    break;

                case AgentMessageRole.ToolResult:
                {
                    string content = message.GetText() ?? string.Empty;
                    builder.Append("> 工具结果 `")
                        .Append(message.ToolName ?? "?")
                        .Append("`")
                        .AppendLine(message.IsError ? "（错误）" : string.Empty);
                    builder.AppendLine();
                    builder.AppendLine(LogSanitizer.Shorten(content, 2000));
                    builder.AppendLine();
                    break;
                }
            }
        }

        private static void WriteJson(
            string path,
            IReadOnlyList<ConversationTranscript> transcripts,
            string workspacePath)
        {
            JsonValue root = JsonValue.NewObject();
            root["schemaVersion"] = JsonValue.From(1);
            root["exportedAt"] = JsonValue.From(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(workspacePath))
            {
                root["workspace"] = JsonValue.From(workspacePath);
            }

            var sessions = JsonValue.NewArray();
            foreach (ConversationTranscript transcript in transcripts)
            {
                JsonValue session = JsonValue.NewObject();
                session["id"] = JsonValue.From(transcript.SessionId ?? string.Empty);
                session["title"] = JsonValue.From(transcript.Title ?? string.Empty);
                session["providerId"] = JsonValue.From(transcript.ProviderId ?? string.Empty);
                session["modelId"] = JsonValue.From(transcript.ModelId ?? string.Empty);
                session["createdAt"] = JsonValue.From(transcript.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
                session["updatedAt"] = JsonValue.From(transcript.UpdatedAt.ToString("o", CultureInfo.InvariantCulture));

                var messages = JsonValue.NewArray();
                foreach (AgentMessage message in transcript.Messages ?? new List<AgentMessage>())
                {
                    if (message != null)
                    {
                        messages.Add(ConversationJson.WriteMessage(message));
                    }
                }

                session["messages"] = messages;
                sessions.Add(session);
            }

            root["sessions"] = sessions;

            // 导出也要脱敏：即使消息正文里出现了密钥前缀，也不让它们离开本机。
            string json = LogSanitizer.Sanitize(JsonWriter.WriteIndented(root));
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        private static void WriteAttachments(string folder, IReadOnlyList<ConversationTranscript> transcripts)
        {
            string attachments = Path.Combine(folder, "attachments");
            bool created = false;

            foreach (ConversationTranscript transcript in transcripts)
            {
                foreach (AgentMessage message in transcript.Messages ?? new List<AgentMessage>())
                {
                    if (message == null || message.Blocks == null)
                    {
                        continue;
                    }

                    foreach (AgentContentBlock block in message.Blocks)
                    {
                        if (block == null
                            || !string.Equals(block.Type, "image", StringComparison.Ordinal)
                            || string.IsNullOrWhiteSpace(block.Data))
                        {
                            continue;
                        }

                        if (!created)
                        {
                            Directory.CreateDirectory(attachments);
                            created = true;
                        }

                        string extension = ExtensionFor(block.MimeType);
                        string name = SanitizeFileName(message.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 6))
                            + extension;

                        try
                        {
                            File.WriteAllBytes(
                                Path.Combine(attachments, name),
                                Convert.FromBase64String(block.Data));
                        }
                        catch (FormatException)
                        {
                        }
                    }
                }
            }
        }

        private static string ExtensionFor(string mimeType)
        {
            switch ((mimeType ?? string.Empty).ToLowerInvariant())
            {
                case "image/jpeg":
                    return ".jpg";
                case "image/gif":
                    return ".gif";
                case "image/webp":
                    return ".webp";
                default:
                    return ".png";
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "会话";
            }

            var builder = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name.Trim())
            {
                bool bad = false;
                foreach (char candidate in invalid)
                {
                    if (c == candidate)
                    {
                        bad = true;
                        break;
                    }
                }

                builder.Append(bad ? '_' : c);
                if (builder.Length >= 40)
                {
                    break;
                }
            }

            string value = builder.ToString().Trim();
            return value.Length == 0 ? "会话" : value;
        }
    }
}
