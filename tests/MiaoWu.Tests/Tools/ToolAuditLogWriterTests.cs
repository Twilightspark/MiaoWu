using System;
using System.Globalization;
using System.IO;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    public sealed class ToolAuditLogWriterTests : IDisposable
    {
        private readonly string _directory;

        public ToolAuditLogWriterTests()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "miaowu-audit-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, true);
                }
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public void WriteCreatesDailyJsonLine()
        {
            var writer = new ToolAuditLogWriter(_directory);
            DateTimeOffset timestamp = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(8));

            writer.Write(new ToolAuditEntry
            {
                Timestamp = timestamp,
                SessionId = "s1",
                WorkspaceId = "w1",
                ToolName = "write_file",
                Target = @"src\a.cs",
                ArgumentsSummary = "path=src\\a.cs",
                Risk = ToolRisk.High,
                Decision = "approved_once",
                Status = ToolResultStatus.Succeeded,
                DurationMilliseconds = 12,
                Truncated = true
            });

            string path = writer.GetFilePath(timestamp);
            Assert.True(File.Exists(path));
            Assert.EndsWith(
                "audit-" + timestamp.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl",
                path);

            string[] lines = File.ReadAllLines(path);
            Assert.Single(lines);

            JsonValue line = JsonParser.Parse(lines[0]);
            Assert.Equal("write_file", line.Get("tool").AsString());
            Assert.Equal("s1", line.Get("sessionId").AsString());
            Assert.Equal("w1", line.Get("workspaceId").AsString());
            Assert.Equal("high", line.Get("risk").AsString());
            Assert.Equal("approved_once", line.Get("decision").AsString());
            Assert.Equal("succeeded", line.Get("status").AsString());
            Assert.Equal(12, line.Get("durationMs").AsInt());
            Assert.Equal(@"src\a.cs", line.Get("target").AsString());
            Assert.True(line.Get("truncated").AsBoolean());
            Assert.Equal(
                timestamp.ToUniversalTime().UtcDateTime,
                DateTimeOffset.Parse(
                    line.Get("ts").AsString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind).UtcDateTime);
        }

        [Fact]
        public void EntriesAppendInOrder()
        {
            var writer = new ToolAuditLogWriter(_directory);
            DateTimeOffset timestamp = new DateTimeOffset(2026, 11, 12, 11, 0, 0, TimeSpan.FromHours(8));

            writer.Write(Entry(timestamp, "list_dir", ToolRisk.Low, "auto"));
            writer.Write(Entry(timestamp, "read_file", ToolRisk.Low, "auto"));

            string[] lines = File.ReadAllLines(writer.GetFilePath(timestamp));
            Assert.Equal(2, lines.Length);
            Assert.Equal("list_dir", JsonParser.Parse(lines[0]).Get("tool").AsString());
            Assert.Equal("read_file", JsonParser.Parse(lines[1]).Get("tool").AsString());
        }

        [Fact]
        public void DifferentDaysUseDifferentFiles()
        {
            var writer = new ToolAuditLogWriter(_directory);
            DateTimeOffset first = new DateTimeOffset(2026, 11, 12, 23, 59, 0, TimeSpan.FromHours(8));
            DateTimeOffset second = first.AddMinutes(2);

            writer.Write(Entry(first, "list_dir", ToolRisk.Low, "auto"));
            writer.Write(Entry(second, "list_dir", ToolRisk.Low, "auto"));

            if (first.ToLocalTime().Date == second.ToLocalTime().Date)
            {
                Assert.Single(File.ReadAllLines(writer.GetFilePath(first)));
            }
            else
            {
                Assert.Single(File.ReadAllLines(writer.GetFilePath(first)));
                Assert.Single(File.ReadAllLines(writer.GetFilePath(second)));
            }
        }

        [Fact]
        public void WriteFailureIsSwallowed()
        {
            string file = Path.Combine(_directory, "not-a-directory");
            Directory.CreateDirectory(_directory);
            File.WriteAllText(file, "x");

            var writer = new ToolAuditLogWriter(file);

            writer.Write(Entry(DateTimeOffset.UtcNow, "list_dir", ToolRisk.Low, "auto"));
        }

        [Fact]
        public void RedactionMasksCommonSecrets()
        {
            string redacted = ToolAuditRedaction.RedactOneLine(
                "api_key=sk-1234567890abcdef password: hunter2 Authorization: Bearer abc.def.ghi");

            Assert.DoesNotContain("sk-1234567890abcdef", redacted);
            Assert.DoesNotContain("hunter2", redacted);
            Assert.DoesNotContain("abc.def.ghi", redacted);
            Assert.Contains("***", redacted);
        }

        [Fact]
        public void RedactionKeepsFirstLineOnly()
        {
            string redacted = ToolAuditRedaction.RedactOneLine("first\nsecond\r\nthird", 512);

            Assert.Equal("first second third", redacted);
        }

        [Fact]
        public void RedactionTruncatesLongText()
        {
            string redacted = ToolAuditRedaction.RedactOneLine(new string('a', 100), 20);

            Assert.Equal(new string('a', 20) + "…", redacted);
        }

        private static ToolAuditEntry Entry(
            DateTimeOffset timestamp,
            string tool,
            ToolRisk risk,
            string decision)
        {
            return new ToolAuditEntry
            {
                Timestamp = timestamp,
                SessionId = "s1",
                WorkspaceId = "w1",
                ToolName = tool,
                Target = "target",
                ArgumentsSummary = "args",
                Risk = risk,
                Decision = decision,
                Status = ToolResultStatus.Succeeded,
                DurationMilliseconds = 1
            };
        }
    }
}
