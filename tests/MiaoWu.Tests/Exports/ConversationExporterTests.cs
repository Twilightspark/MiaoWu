using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Exports;
using MiaoWu.Infrastructure.Json;
using Xunit;

namespace MiaoWu.Tests.Exports
{
    public sealed class ConversationExporterTests
    {
        [Fact]
        public void ExportsMarkdownJsonAndAttachmentsWithoutKeys()
        {
            string root = Path.Combine(Path.GetTempPath(), "miaowu-export-" + Guid.NewGuid().ToString("N"));
            try
            {
                var transcript = new ConversationTranscript
                {
                    SessionId = "s1",
                    Title = "导出测试",
                    ProviderId = "p",
                    ModelId = "m",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                AgentMessage user = AgentMessage.CreateUser("m1", "你好");
                transcript.Messages.Add(user);

                AgentMessage assistant = AgentMessage.CreateAssistantPlaceholder("m2", "p", "m");
                assistant.Blocks.Add(new AgentContentBlock { Type = "text", Text = "回答内容 sk-abcdefgh12345678" });
                assistant.Blocks.Add(new AgentContentBlock
                {
                    Type = "toolCall",
                    ToolCallId = "c1",
                    ToolName = "read",
                    ArgumentsJson = "{\"path\":\"a.txt\"}"
                });
                assistant.Blocks.Add(new AgentContentBlock
                {
                    Type = "image",
                    MimeType = "image/png",
                    Data = "aGVsbG8="
                });
                assistant.Status = AgentMessageStatus.Completed;
                assistant.StopReason = StopReason.Stop;
                assistant.Usage = new TokenUsage { Input = 10, Output = 5, Total = 15 };
                transcript.Messages.Add(assistant);

                transcript.Messages.Add(AgentMessage.CreateToolResult(
                    "m3",
                    "c1",
                    "read",
                    "文件内容",
                    false,
                    true,
                    null));

                var exporter = new ConversationExporter(root);
                string folder = exporter.ExportSession(transcript, "D:\\work");

                Assert.True(File.Exists(Path.Combine(folder, "conversation.md")));
                Assert.True(File.Exists(Path.Combine(folder, "conversation.json")));
                Assert.True(Directory.Exists(Path.Combine(folder, "attachments")));

                string markdown = File.ReadAllText(Path.Combine(folder, "conversation.md"));
                Assert.Contains("导出测试", markdown);
                Assert.Contains("回答内容", markdown);
                Assert.Contains("read", markdown);
                Assert.DoesNotContain("sk-abcdefgh12345678", markdown);

                string json = File.ReadAllText(Path.Combine(folder, "conversation.json"));
                Assert.DoesNotContain("sk-abcdefgh12345678", json);
                JsonValue parsed = JsonParser.Parse(json);
                Assert.NotNull(parsed);
                Assert.Single(parsed.Get("sessions").Items);
                Assert.Equal(3, parsed.Get("sessions").Get(0).Get("messages").Items.Count);

                string[] images = Directory.GetFiles(Path.Combine(folder, "attachments"), "*.png");
                Assert.Single(images);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        [Fact]
        public void ExportAllDoesNotOverwriteExistingFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "miaowu-export-" + Guid.NewGuid().ToString("N"));
            try
            {
                var exporter = new ConversationExporter(root);
                var transcripts = new List<ConversationTranscript>
                {
                    new ConversationTranscript
                    {
                        SessionId = "s1",
                        Title = "A",
                        Messages = new List<AgentMessage>
                        {
                            AgentMessage.CreateUser("m1", "hi")
                        }
                    }
                };

                string first = exporter.ExportAll(transcripts, null);
                string second = exporter.ExportAll(transcripts, null);

                Assert.NotEqual(first, second);
                Assert.True(Directory.Exists(first));
                Assert.True(Directory.Exists(second));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
