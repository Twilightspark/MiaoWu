using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Conversations;
using MiaoWu.Infrastructure.Json;
using Xunit;

namespace MiaoWu.Tests.Conversations
{
    public sealed class WorkspaceConversationStoreTests : IDisposable
    {
        private const string WorkspaceId = "w_test";

        private readonly string _workspace;
        private readonly MutableClock _clock;

        public WorkspaceConversationStoreTests()
        {
            _workspace = Path.Combine(
                Path.GetTempPath(),
                "miaowu-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);
            _clock = new MutableClock(new DateTimeOffset(2026, 11, 12, 2, 0, 0, TimeSpan.Zero));
        }

        public void Dispose()
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, true);
            }
        }

        [Fact]
        public void MissingWorkspaceIsNotRecreated()
        {
            string missing = Path.Combine(
                Path.GetTempPath(),
                "miaowu-missing-" + Guid.NewGuid().ToString("N"));
            var store = new WorkspaceConversationStore(WorkspaceId, missing, _clock);

            Assert.Throws<DirectoryNotFoundException>(
                () => store.LoadIndexAsync(None).GetAwaiter().GetResult());
            Assert.False(Directory.Exists(missing));
        }

        [Fact]
        public void CreateWritesSessionCreatedEventAndIndexEntry()
        {
            WorkspaceConversationStore store = NewStore();

            store.CreateAsync(Descriptor("s_01", "第一条会话"), None).GetAwaiter().GetResult();

            string file = SessionFile("s_01");
            Assert.True(File.Exists(file));

            string[] lines = File.ReadAllLines(file);
            Assert.Single(lines);

            JsonValue evt = JsonParser.Parse(lines[0]);
            Assert.Equal("session.created", evt.Get("type").AsString());
            Assert.Equal("第一条会话", evt.Get("title").AsString());

            ConversationIndex index = Load(store);
            Assert.Single(index.Sessions);
            Assert.Equal("s_01", index.Sessions[0].SessionId);
            Assert.Equal(WorkspaceId, index.Sessions[0].WorkspaceId);
            Assert.Equal("第一条会话", index.Sessions[0].Title);
            Assert.Equal(0, index.Sessions[0].MessageCount);
        }

        [Fact]
        public void CreateIsIdempotent()
        {
            WorkspaceConversationStore store = NewStore();

            store.CreateAsync(Descriptor("s_01", "标题"), None).GetAwaiter().GetResult();
            store.CreateAsync(Descriptor("s_01", "另一个标题"), None).GetAwaiter().GetResult();

            Assert.Single(File.ReadAllLines(SessionFile("s_01")));
            Assert.Single(Load(store).Sessions);
        }

        [Fact]
        public void AppendAndCompleteRoundTripsMessages()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "你好"), None).GetAwaiter().GetResult();

            AgentMessage user = AgentMessage.CreateUser("m_01", "你好");
            store.AppendMessageAsync("s_01", user, None).GetAwaiter().GetResult();

            AgentMessage assistant = AgentMessage.CreateAssistantPlaceholder(
                "m_02",
                "openai",
                "gpt-4o-mini");
            store.AppendMessageAsync("s_01", assistant, None).GetAwaiter().GetResult();

            assistant.Blocks.Add(new AgentContentBlock { Type = "text", Text = "你好！有什么可以帮你？" });
            assistant.Usage = new TokenUsage { Input = 9, Output = 11 };
            store.CompleteMessageAsync("s_01", assistant, None).GetAwaiter().GetResult();

            ConversationTranscript transcript = LoadTranscript(store, "s_01");

            Assert.Equal(2, transcript.MessageCount);
            Assert.Equal(2, transcript.Messages.Count);
            Assert.Equal(AgentMessageRole.User, transcript.Messages[0].Role);
            Assert.Equal("你好", transcript.Messages[0].GetText());
            Assert.Equal(AgentMessageStatus.Completed, transcript.Messages[1].Status);
            Assert.Equal("你好！有什么可以帮你？", transcript.Messages[1].GetText());
            Assert.Equal(20, transcript.Messages[1].Usage.GetTotal());
            Assert.Equal(StopReason.Stop, transcript.Messages[1].StopReason);
            Assert.NotNull(transcript.Messages[1].CompletedAt);
            string completedLine = File.ReadAllLines(SessionFile("s_01"))
                .First(line => line.Contains("\"message.completed\""));
            DateTimeOffset completedAt = DateTimeOffset.Parse(
                JsonParser.Parse(completedLine).Get("ts").AsString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.Equal(completedAt, transcript.Messages[1].CompletedAt);

            ConversationIndex index = Load(store);
            ConversationIndexEntry entry = Assert.Single(index.Sessions);
            Assert.Equal(2, entry.MessageCount);
            Assert.Equal("你好！有什么可以帮你？", entry.Preview);
            Assert.Equal("openai/gpt-4o-mini", entry.LastModel);
        }

        [Fact]
        public void LoadMissingSessionReturnsNull()
        {
            WorkspaceConversationStore store = NewStore();

            ConversationTranscript transcript = LoadTranscript(store, "s_missing");

            Assert.Null(transcript);
        }

        [Fact]
        public void AppendToMissingSessionThrows()
        {
            WorkspaceConversationStore store = NewStore();

            Assert.Throws<InvalidOperationException>(() => store
                .AppendMessageAsync("s_missing", AgentMessage.CreateUser("m1", "x"), None)
                .GetAwaiter()
                .GetResult());
        }

        [Fact]
        public void TruncatedTailLineIsDroppedAndNextAppendStaysValid()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "t"), None).GetAwaiter().GetResult();
            store.AppendMessageAsync("s_01", AgentMessage.CreateUser("m_01", "一"), None)
                .GetAwaiter()
                .GetResult();

            File.AppendAllText(SessionFile("s_01"), "{\"v\":1,\"seq\":99,\"ts\":\"2026-11-");

            ConversationTranscript transcript = LoadTranscript(store, "s_01");
            Assert.Single(transcript.Messages);

            WorkspaceConversationStore reopened = NewStore();
            reopened.AppendMessageAsync("s_01", AgentMessage.CreateUser("m_02", "二"), None)
                .GetAwaiter()
                .GetResult();

            ConversationTranscript after = LoadTranscript(reopened, "s_01");
            Assert.Equal(2, after.Messages.Count);
            Assert.Equal("二", after.Messages[1].GetText());
            Assert.Equal(3, CountParseableLines(SessionFile("s_01")));
        }

        [Fact]
        public void StreamingAssistantBecomesInterruptedOnLoad()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "t"), None).GetAwaiter().GetResult();
            store.AppendMessageAsync(
                    "s_01",
                    AgentMessage.CreateAssistantPlaceholder("m_01", "openai", "gpt-4o-mini"),
                    None)
                .GetAwaiter()
                .GetResult();

            ConversationTranscript transcript = LoadTranscript(store, "s_01");

            AgentMessage assistant = Assert.Single(transcript.Messages);
            Assert.Equal(AgentMessageStatus.Interrupted, assistant.Status);
            Assert.Equal(StopReason.Aborted, assistant.StopReason);
        }

        [Fact]
        public void FailedMessagePreservesPartialContentAndError()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "t"), None).GetAwaiter().GetResult();

            AgentMessage assistant = AgentMessage.CreateAssistantPlaceholder("m_01", "openai", "gpt-4o-mini");
            assistant.Blocks.Add(new AgentContentBlock { Type = "text", Text = "部分输出" });
            store.AppendMessageAsync("s_01", assistant, None).GetAwaiter().GetResult();
            store.FailMessageAsync(
                    "s_01",
                    assistant,
                    new AgentError
                    {
                        Kind = AgentErrorKind.RateLimited,
                        Message = "请求过于频繁",
                        Retryable = true,
                        HttpStatus = 429
                    },
                    None)
                .GetAwaiter()
                .GetResult();

            ConversationTranscript transcript = LoadTranscript(store, "s_01");
            AgentMessage failed = Assert.Single(transcript.Messages);
            Assert.Equal(AgentMessageStatus.Failed, failed.Status);
            Assert.Equal("部分输出", failed.GetText());
            Assert.Equal("请求过于频繁", failed.ErrorMessage);
            Assert.Equal("部分输出", Load(store).Sessions[0].Preview);
        }

        [Fact]
        public void RemoveMessageExcludesItFromReplayAndCount()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "t"), None).GetAwaiter().GetResult();
            store.AppendMessageAsync("s_01", AgentMessage.CreateUser("m_01", "问"), None)
                .GetAwaiter()
                .GetResult();
            store.AppendMessageAsync(
                    "s_01",
                    AgentMessage.CreateAssistantPlaceholder("m_02", "openai", "gpt"),
                    None)
                .GetAwaiter()
                .GetResult();

            store.RemoveMessageAsync("s_01", "m_01", "retry", None).GetAwaiter().GetResult();

            ConversationTranscript transcript = LoadTranscript(store, "s_01");
            Assert.Single(transcript.Messages);
            Assert.Equal("m_02", transcript.Messages[0].Id);
            Assert.Equal(1, transcript.MessageCount);
            Assert.Equal(1, Load(store).Sessions[0].MessageCount);
        }

        [Fact]
        public void RenameUpdatesTranscriptAndIndex()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "旧标题"), None).GetAwaiter().GetResult();

            _clock.Advance(TimeSpan.FromMinutes(1));
            store.RenameAsync("s_01", "  新标题  ", None).GetAwaiter().GetResult();

            Assert.Equal("新标题", LoadTranscript(store, "s_01").Title);
            Assert.Equal("新标题", Load(store).Sessions[0].Title);
        }

        [Fact]
        public void PinAndArchiveSurviveIndexRebuild()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "标题"), None).GetAwaiter().GetResult();

            store.SetPinnedAsync("s_01", true, None).GetAwaiter().GetResult();
            store.SetArchivedAsync("s_01", true, None).GetAwaiter().GetResult();

            Assert.True(Load(store).Sessions[0].IsPinned);
            Assert.True(Load(store).Sessions[0].IsArchived);

            // 删除派生索引，强制从 JSONL 重放：置顶与归档不能丢。
            File.Delete(AgentWorkspaceLayout.GetIndexFilePath(_workspace));
            ConversationIndex rebuilt = Load(NewStore());

            Assert.True(rebuilt.Sessions[0].IsPinned);
            Assert.True(rebuilt.Sessions[0].IsArchived);
            Assert.True(LoadTranscript(NewStore(), "s_01").IsPinned);
            Assert.True(LoadTranscript(NewStore(), "s_01").IsArchived);
        }

        [Fact]
        public void ClearRemovesAllSessionsButKeepsWorkspaceFiles()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "A"), None).GetAwaiter().GetResult();
            store.CreateAsync(Descriptor("s_02", "B"), None).GetAwaiter().GetResult();
            string keep = Path.Combine(_workspace, "notes.txt");
            File.WriteAllText(keep, "keep");

            store.ClearAsync(None).GetAwaiter().GetResult();

            Assert.Empty(Load(store).Sessions);
            Assert.False(File.Exists(SessionFile("s_01")));
            Assert.False(File.Exists(SessionFile("s_02")));
            Assert.True(File.Exists(keep));
        }

        [Fact]
        public void RenameWithBlankTitleFallsBackToDefault()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "旧标题"), None).GetAwaiter().GetResult();

            store.RenameAsync("s_01", "   ", None).GetAwaiter().GetResult();

            Assert.Equal(WorkspaceConversationStore.DefaultSessionTitle, Load(store).Sessions[0].Title);
        }

        [Fact]
        public void DeleteRemovesFileAndIndexEntry()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "t"), None).GetAwaiter().GetResult();

            store.DeleteAsync("s_01", None).GetAwaiter().GetResult();

            Assert.False(File.Exists(SessionFile("s_01")));
            Assert.Empty(Load(store).Sessions);
            Assert.Null(LoadTranscript(store, "s_01"));
        }

        [Fact]
        public void IndexIsRebuiltWhenMissing()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "A"), None).GetAwaiter().GetResult();
            _clock.Advance(TimeSpan.FromSeconds(5));
            store.CreateAsync(Descriptor("s_02", "B"), None).GetAwaiter().GetResult();

            File.Delete(AgentWorkspaceLayout.GetIndexFilePath(_workspace));

            ConversationIndex rebuilt = Load(NewStore());

            Assert.Equal(2, rebuilt.Sessions.Count);
            Assert.Contains(rebuilt.Sessions, e => e.SessionId == "s_01" && e.Title == "A");
            Assert.Contains(rebuilt.Sessions, e => e.SessionId == "s_02" && e.Title == "B");
            Assert.True(File.Exists(AgentWorkspaceLayout.GetIndexFilePath(_workspace)));
        }

        [Fact]
        public void IndexIsBackedUpAndRebuiltWhenCorrupt()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "A"), None).GetAwaiter().GetResult();
            File.WriteAllText(AgentWorkspaceLayout.GetIndexFilePath(_workspace), "{ not json");

            ConversationIndex rebuilt = Load(NewStore());

            Assert.Single(rebuilt.Sessions);
            string[] backups = Directory.GetFiles(
                AgentWorkspaceLayout.GetSessionsDirectory(_workspace),
                "index.json.corrupt-*");
            Assert.Single(backups);
        }

        [Fact]
        public void IndexIsRebuiltWhenDirectoryHasAnUnindexedSession()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "A"), None).GetAwaiter().GetResult();
            File.Copy(SessionFile("s_01"), SessionFile("s_extra"));

            ConversationIndex rebuilt = Load(NewStore());

            Assert.Equal(2, rebuilt.Sessions.Count);
        }

        [Fact]
        public void IndexIsRebuiltWhenEntryFileIsMissing()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "A"), None).GetAwaiter().GetResult();
            File.Delete(SessionFile("s_01"));

            ConversationIndex rebuilt = Load(NewStore());

            Assert.Empty(rebuilt.Sessions);
        }

        [Fact]
        public void UnknownEventTypesAreIgnored()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "A"), None).GetAwaiter().GetResult();
            store.AppendMessageAsync("s_01", AgentMessage.CreateUser("m_01", "保留"), None)
                .GetAwaiter()
                .GetResult();

            File.AppendAllText(
                SessionFile("s_01"),
                "{\"v\":1,\"seq\":50,\"ts\":\"2026-11-12T02:10:00.000Z\",\"type\":\"future.event\",\"payload\":{\"x\":1}}\n");

            ConversationTranscript transcript = LoadTranscript(NewStore(), "s_01");

            Assert.Single(transcript.Messages);
        }

        [Fact]
        public void SessionsAreSortedByUpdatedTimeDescending()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_old", "旧"), None).GetAwaiter().GetResult();

            _clock.Advance(TimeSpan.FromMinutes(5));
            store.CreateAsync(Descriptor("s_new", "新"), None).GetAwaiter().GetResult();

            ConversationIndex index = Load(store);

            Assert.Equal("s_new", index.Sessions[0].SessionId);
            Assert.Equal("s_old", index.Sessions[1].SessionId);
        }

        [Fact]
        public void InvalidSessionIdCannotEscapeStorage()
        {
            WorkspaceConversationStore store = NewStore();

            Assert.Throws<ArgumentException>(() => store
                .CreateAsync(Descriptor("../escape", "x"), None)
                .GetAwaiter()
                .GetResult());
            Assert.Throws<ArgumentException>(() => store
                .LoadAsync("..\\..\\outside", None)
                .GetAwaiter()
                .GetResult());
            Assert.False(Directory.Exists(Path.Combine(_workspace, "..", "escape")));
        }

        [Fact]
        public void TranscriptSurvivesStoreRestart()
        {
            WorkspaceConversationStore first = NewStore();
            first.CreateAsync(Descriptor("s_01", "t"), None).GetAwaiter().GetResult();
            first.AppendMessageAsync("s_01", AgentMessage.CreateUser("m_01", "持久化"), None)
                .GetAwaiter()
                .GetResult();

            WorkspaceConversationStore second = NewStore();
            ConversationTranscript transcript = LoadTranscript(second, "s_01");

            Assert.Single(transcript.Messages);
            Assert.Equal("持久化", transcript.Messages[0].GetText());

            ConversationIndex index = Load(second);
            Assert.Equal(1, index.Sessions[0].MessageCount);
        }

        [Fact]
        public void SequenceNumbersIncreaseAfterRecovery()
        {
            WorkspaceConversationStore store = NewStore();
            store.CreateAsync(Descriptor("s_01", "t"), None).GetAwaiter().GetResult();
            store.AppendMessageAsync("s_01", AgentMessage.CreateUser("m_01", "一"), None)
                .GetAwaiter()
                .GetResult();

            File.AppendAllText(SessionFile("s_01"), "{\"v\":1,\"type\":\"partial\"");

            WorkspaceConversationStore reopened = NewStore();
            reopened.AppendMessageAsync("s_01", AgentMessage.CreateUser("m_02", "二"), None)
                .GetAwaiter()
                .GetResult();

            long[] sequences = File.ReadAllLines(SessionFile("s_01"))
                .Select(TryParseLine)
                .Where(evt => evt != null)
                .Select(evt => evt.Get("seq").AsLong())
                .ToArray();

            Assert.Equal(3, sequences.Length);
            Assert.Equal(sequences.Length, sequences.Distinct().Count());
            for (int i = 1; i < sequences.Length; i++)
            {
                Assert.True(sequences[i] > sequences[i - 1]);
            }
        }

        [Fact]
        public void ThousandMessagesLoadWithinBudget()
        {
            string file = AgentWorkspaceLayout.GetSessionFilePath(_workspace, "s_big");
            Directory.CreateDirectory(Path.GetDirectoryName(file));

            const string timestamp = "2026-11-12T02:00:00.000Z";
            var builder = new StringBuilder();
            builder.Append("{\"v\":1,\"seq\":0,\"ts\":\"").Append(timestamp)
                .Append("\",\"type\":\"session.created\",\"title\":\"大会话\",\"workspaceId\":\"")
                .Append(WorkspaceId).Append("\"}\n");

            for (int i = 1; i <= 1000; i++)
            {
                builder.Append("{\"v\":1,\"seq\":").Append(i)
                    .Append(",\"ts\":\"").Append(timestamp)
                    .Append("\",\"type\":\"message.added\",\"message\":{\"id\":\"m_")
                    .Append(i)
                    .Append("\",\"role\":\"user\",\"status\":\"completed\",\"createdAt\":\"")
                    .Append(timestamp)
                    .Append("\",\"blocks\":[{\"type\":\"text\",\"text\":\"第 ")
                    .Append(i)
                    .Append(" 条\"}]}}\n");
            }

            File.WriteAllText(file, builder.ToString());

            var watch = Stopwatch.StartNew();
            ConversationTranscript transcript = LoadTranscript(NewStore(), "s_big");
            watch.Stop();

            Assert.Equal(1000, transcript.Messages.Count);
            Assert.Equal(1000, transcript.MessageCount);
            Assert.Equal("第 1000 条", transcript.Messages[999].GetText());
            Assert.True(
                watch.ElapsedMilliseconds < 2000,
                "1000 条消息加载耗时 " + watch.ElapsedMilliseconds + "ms，超出 2s 预算。");
        }

        [Fact]
        public void LoadIndexDoesNotReplayEverySession()
        {
            WorkspaceConversationStore store = NewStore();
            for (int i = 0; i < 5; i++)
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                store.CreateAsync(Descriptor("s_0" + i, "会话" + i), None).GetAwaiter().GetResult();
            }

            // 第二个实例只加载索引，不应因为目录扫描而失败或重复条目。
            ConversationIndex index = Load(NewStore());

            Assert.Equal(5, index.Sessions.Count);
            Assert.Equal(5, index.Sessions.Select(e => e.SessionId).Distinct().Count());
        }

        private WorkspaceConversationStore NewStore()
        {
            return new WorkspaceConversationStore(WorkspaceId, _workspace, _clock);
        }

        private static System.Threading.CancellationToken None
        {
            get { return System.Threading.CancellationToken.None; }
        }

        private static ConversationDescriptor Descriptor(string id, string title)
        {
            return new ConversationDescriptor
            {
                SessionId = id,
                WorkspaceId = WorkspaceId,
                Title = title,
                ProviderId = "openai",
                ModelId = "gpt-4o-mini"
            };
        }

        private string SessionFile(string sessionId)
        {
            return AgentWorkspaceLayout.GetSessionFilePath(_workspace, sessionId);
        }

        private static ConversationIndex Load(WorkspaceConversationStore store)
        {
            return store.LoadIndexAsync(None).GetAwaiter().GetResult();
        }

        private static ConversationTranscript LoadTranscript(
            WorkspaceConversationStore store,
            string sessionId)
        {
            return store.LoadAsync(sessionId, None).GetAwaiter().GetResult();
        }

        private static int CountParseableLines(string path)
        {
            return File.ReadAllLines(path).Count(line => TryParseLine(line) != null);
        }

        private static JsonValue TryParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            try
            {
                return JsonParser.Parse(line);
            }
            catch (JsonParseException)
            {
                return null;
            }
        }
    }
}
