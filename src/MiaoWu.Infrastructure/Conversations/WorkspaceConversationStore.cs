using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Storage;

namespace MiaoWu.Infrastructure.Conversations
{
    /// <summary>
    /// 工作区级会话存储：<c>&lt;workspace&gt;\.agent\sessions\index.json</c> +
    /// 每会话一个 <c>{sessionId}.jsonl</c> 追加日志。
    /// </summary>
    /// <remarks>
    /// <para>一个工作区一个实例；会话之间并行，但同一会话的写入通过会话锁串行化。
    /// 追加事件后立即 <c>Flush(true)</c>，助手 delta 不落盘，崩溃最多丢当前正在生成的一条。</para>
    /// <para>接口是异步形状，但 net48 下的追加顺序比吞吐更重要，内部使用同步 IO
    /// 并返回已完成任务；调用方仍在后台 loop 中执行，不占用 UI 线程。</para>
    /// <para>索引是派生数据：损坏/缺失/与目录不一致时从 JSONL 重建，因此索引写失败
    /// 不会丢消息。</para>
    /// </remarks>
    public sealed class WorkspaceConversationStore : IConversationStore
    {
        public const string DefaultSessionTitle = "新会话";

        private const int MaxMessagesInMemory = 5000;
        private const int MaxTitleLength = 200;
        private const int MaxPreviewLength = 80;
        private const int TailReadBytes = 8192;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _workspaceId;
        private readonly string _workspacePath;
        private readonly IClock _clock;
        private readonly object _indexGate = new object();
        private readonly ConcurrentDictionary<string, object> _sessionGates =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _lastSequences =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        private ConversationIndex _index;
        private bool _indexLoaded;
        private bool _indexDirty;

        public WorkspaceConversationStore(string workspaceId, string workspacePath, IClock clock)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                throw new ArgumentException("工作区 ID 不能为空。", nameof(workspaceId));
            }

            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                throw new ArgumentException("工作区路径不能为空。", nameof(workspacePath));
            }

            _workspaceId = workspaceId;
            _workspacePath = Path.GetFullPath(workspacePath);
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public string WorkspaceId
        {
            get { return _workspaceId; }
        }

        public string WorkspacePath
        {
            get { return _workspacePath; }
        }

        public string SessionsDirectory
        {
            get { return AgentWorkspaceLayout.GetSessionsDirectory(_workspacePath); }
        }

        public string IndexFilePath
        {
            get { return AgentWorkspaceLayout.GetIndexFilePath(_workspacePath); }
        }

        /// <summary>
        /// 创建 <c>.agent</c> 下的全部保留目录，并确认当前账户可写。重复调用幂等；
        /// 权限不足时抛出，由调用方（会话目录/左栏）转成内联错误。
        /// </summary>
        public void EnsureCreated()
        {
            // 工作区被删除/拔盘时不要“顺手”把目录建回来。
            if (!Directory.Exists(_workspacePath))
            {
                throw new DirectoryNotFoundException("工作区目录不存在：" + _workspacePath);
            }

            foreach (string directory in AgentWorkspaceLayout.GetManagedDirectories(_workspacePath))
            {
                // 工作区可能落在只读共享、或由提权进程创建：先补权限再报错。
                FolderAccess.EnsureWritable(directory);
            }
        }

        public Task<ConversationIndex> LoadIndexAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetIndexSnapshot());
        }

        public Task CreateAsync(ConversationDescriptor descriptor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            string sessionId = RequireSessionId(descriptor.SessionId);
            EnsureCreated();

            string path = AgentWorkspaceLayout.GetSessionFilePath(_workspacePath, sessionId);
            lock (GetSessionGate(sessionId))
            {
                if (File.Exists(path))
                {
                    // 幂等：文件已存在时只补齐索引，不重复写 session.created。
                    ReconcileIndex(Replay(sessionId, path));
                    return Task.CompletedTask;
                }

                DateTimeOffset createdAt = descriptor.CreatedAt ?? _clock.UtcNow;
                string title = NormalizeTitle(descriptor.Title);

                JsonValue body = JsonValue.NewObject();
                body["title"] = JsonValue.From(title);
                body["workspaceId"] = JsonValue.From(_workspaceId);

                if (!string.IsNullOrEmpty(descriptor.ProviderId)
                    || !string.IsNullOrEmpty(descriptor.ModelId))
                {
                    JsonValue model = JsonValue.NewObject();
                    AddIfNotEmpty(model, "providerId", descriptor.ProviderId);
                    AddIfNotEmpty(model, "modelId", descriptor.ModelId);
                    body["model"] = model;
                }

                AppendEvent(sessionId, path, createdAt, "session.created", body);

                var entry = new ConversationIndexEntry
                {
                    SessionId = sessionId,
                    WorkspaceId = _workspaceId,
                    Title = title,
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt,
                    IsPinned = false,
                    IsArchived = false,
                    MessageCount = 0,
                    LastModel = BuildModelRef(descriptor.ProviderId, descriptor.ModelId),
                    Preview = string.Empty
                };

                UpsertEntry(entry, true);
            }

            return Task.CompletedTask;
        }

        public Task<ConversationTranscript> LoadAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);

            string path = AgentWorkspaceLayout.GetSessionFilePath(_workspacePath, sessionId);
            lock (GetSessionGate(sessionId))
            {
                if (!File.Exists(path))
                {
                    return Task.FromResult<ConversationTranscript>(null);
                }

                ConversationTranscript transcript = Replay(sessionId, path);
                ReconcileIndex(transcript);
                return Task.FromResult(transcript);
            }
        }

        public Task AppendMessageAsync(
            string sessionId,
            AgentMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (string.IsNullOrEmpty(message.Id))
            {
                throw new ArgumentException("消息 ID 不能为空。", nameof(message));
            }

            string path = RequireSessionFile(sessionId);
            DateTimeOffset now = _clock.UtcNow;

            lock (GetSessionGate(sessionId))
            {
                AgentMessage stored = message.Clone();
                stored.CreatedAt = message.CreatedAt == default(DateTimeOffset)
                    ? now
                    : message.CreatedAt;

                JsonValue body = JsonValue.NewObject();
                body["message"] = ConversationJson.WriteMessage(stored);
                AppendEvent(sessionId, path, now, "message.added", body);

                UpdateEntryAfterAppend(sessionId, stored, now, persist: stored.Role == AgentMessageRole.User);
            }

            return Task.CompletedTask;
        }

        public Task CompleteMessageAsync(
            string sessionId,
            AgentMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (string.IsNullOrEmpty(message.Id))
            {
                throw new ArgumentException("消息 ID 不能为空。", nameof(message));
            }

            string path = RequireSessionFile(sessionId);
            DateTimeOffset now = _clock.UtcNow;

            lock (GetSessionGate(sessionId))
            {
                AgentMessage stored = message.Clone();
                stored.Status = AgentMessageStatus.Completed;
                if (!stored.StopReason.HasValue)
                {
                    stored.StopReason = StopReason.Stop;
                }

                JsonValue body = JsonValue.NewObject();
                body["messageId"] = JsonValue.From(stored.Id);
                body["blocks"] = WriteBlocks(stored.Blocks);
                if (stored.Usage != null)
                {
                    body["usage"] = ConversationJson.WriteUsage(stored.Usage);
                }

                body["stopReason"] = JsonValue.From(
                    ConversationJson.StopReasonToText(stored.StopReason.Value));
                AddIfNotEmpty(body, "modelId", stored.ModelId);
                AddIfNotEmpty(body, "providerId", stored.ProviderId);

                AppendEvent(sessionId, path, now, "message.completed", body);

                UpdateEntryAfterCompletion(sessionId, stored, now, error: null);
            }

            return Task.CompletedTask;
        }

        public Task FailMessageAsync(
            string sessionId,
            AgentMessage message,
            AgentError error,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (string.IsNullOrEmpty(message.Id))
            {
                throw new ArgumentException("消息 ID 不能为空。", nameof(message));
            }

            string path = RequireSessionFile(sessionId);
            DateTimeOffset now = _clock.UtcNow;

            lock (GetSessionGate(sessionId))
            {
                AgentMessage stored = message.Clone();
                stored.Status = AgentMessageStatus.Failed;
                stored.StopReason = StopReason.Error;

                AgentError safeError = error ?? AgentError.Create(
                    AgentErrorKind.Unknown,
                    "未知错误");

                stored.ErrorMessage = safeError.Message;

                JsonValue body = JsonValue.NewObject();
                body["messageId"] = JsonValue.From(stored.Id);
                body["blocks"] = WriteBlocks(stored.Blocks);
                if (stored.Usage != null)
                {
                    body["usage"] = ConversationJson.WriteUsage(stored.Usage);
                }

                body["error"] = ConversationJson.WriteError(safeError);
                AddIfNotEmpty(body, "modelId", stored.ModelId);
                AddIfNotEmpty(body, "providerId", stored.ProviderId);

                AppendEvent(sessionId, path, now, "message.failed", body);

                UpdateEntryAfterCompletion(sessionId, stored, now, safeError);
            }

            return Task.CompletedTask;
        }

        public Task RemoveMessageAsync(
            string sessionId,
            string messageId,
            string reason,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);
            if (string.IsNullOrEmpty(messageId))
            {
                throw new ArgumentException("消息 ID 不能为空。", nameof(messageId));
            }

            string path = RequireSessionFile(sessionId);
            DateTimeOffset now = _clock.UtcNow;

            lock (GetSessionGate(sessionId))
            {
                JsonValue body = JsonValue.NewObject();
                body["messageId"] = JsonValue.From(messageId);
                body["reason"] = JsonValue.From(string.IsNullOrEmpty(reason) ? "retry" : reason);
                AppendEvent(sessionId, path, now, "message.removed", body);

                lock (_indexGate)
                {
                    ConversationIndex index = GetIndexUnsafe();
                    ConversationIndexEntry entry = FindEntry(index, sessionId);
                    if (entry != null)
                    {
                        if (entry.MessageCount > 0)
                        {
                            entry.MessageCount--;
                        }

                        entry.UpdatedAt = now;
                    }

                    MarkIndexDirty(index, persist: true);
                }
            }

            return Task.CompletedTask;
        }

        public Task RenameAsync(
            string sessionId,
            string title,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);
            string path = RequireSessionFile(sessionId);
            DateTimeOffset now = _clock.UtcNow;
            string normalized = NormalizeTitle(title);

            lock (GetSessionGate(sessionId))
            {
                JsonValue body = JsonValue.NewObject();
                body["title"] = JsonValue.From(normalized);
                AppendEvent(sessionId, path, now, "session.renamed", body);

                lock (_indexGate)
                {
                    ConversationIndex index = GetIndexUnsafe();
                    ConversationIndexEntry entry = FindEntry(index, sessionId);
                    if (entry == null)
                    {
                        entry = new ConversationIndexEntry
                        {
                            SessionId = sessionId,
                            WorkspaceId = _workspaceId,
                            CreatedAt = now,
                            MessageCount = 0
                        };
                        index.Sessions.Add(entry);
                    }

                    entry.Title = normalized;
                    entry.UpdatedAt = now;
                    MarkIndexDirty(index, persist: true);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>追加一条压缩记录；消息不删除，重放时只恢复最近一次压缩。</summary>
        public Task CompactAsync(
            string sessionId,
            AgentCompaction compaction,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compaction == null)
            {
                throw new ArgumentNullException(nameof(compaction));
            }

            RequireSessionId(sessionId);
            string path = RequireSessionFile(sessionId);
            DateTimeOffset now = _clock.UtcNow;

            lock (GetSessionGate(sessionId))
            {
                JsonValue body = JsonValue.NewObject();
                body["summary"] = JsonValue.From(compaction.Summary ?? string.Empty);
                body["coveredMessages"] = JsonValue.From(compaction.CoveredMessages);
                if (!string.IsNullOrEmpty(compaction.LastCoveredMessageId))
                {
                    body["lastCoveredMessageId"] = JsonValue.From(compaction.LastCoveredMessageId);
                }

                body["compactedAt"] = JsonValue.From(ConversationJson.FormatTimestamp(
                    compaction.CompactedAt == default(DateTimeOffset) ? now : compaction.CompactedAt));
                AppendEvent(sessionId, path, now, "session.compacted", body);

                lock (_indexGate)
                {
                    ConversationIndex index = GetIndexUnsafe();
                    ConversationIndexEntry entry = FindEntry(index, sessionId);
                    if (entry != null)
                    {
                        entry.UpdatedAt = now;
                        MarkIndexDirty(index, persist: true);
                    }
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>追加一条会话读数快照；只保留最后一次，重放时覆盖。</summary>
        public Task SaveMeterAsync(
            string sessionId,
            SessionStats stats,
            SessionTokenUsage usage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);
            string path = RequireSessionFile(sessionId);
            DateTimeOffset now = _clock.UtcNow;

            lock (GetSessionGate(sessionId))
            {
                JsonValue body = JsonValue.NewObject();
                JsonValue statsNode = JsonValue.NewObject();
                SessionStats safeStats = stats ?? new SessionStats();
                statsNode["turns"] = JsonValue.From(safeStats.Turns);
                statsNode["steps"] = JsonValue.From(safeStats.Steps);
                statsNode["llmMs"] = JsonValue.From(safeStats.LlmMs);
                statsNode["toolMs"] = JsonValue.From(safeStats.ToolMs);
                statsNode["ttftMs"] = JsonValue.From(safeStats.TtftMs);
                statsNode["ttftSteps"] = JsonValue.From(safeStats.TtftSteps);
                statsNode["decodeMs"] = JsonValue.From(safeStats.DecodeMs);
                statsNode["decodeTokens"] = JsonValue.From(safeStats.DecodeTokens);
                body["stats"] = statsNode;

                SessionTokenUsage safeUsage = usage ?? new SessionTokenUsage();
                JsonValue usageNode = JsonValue.NewObject();
                usageNode["uncachedInput"] = JsonValue.From(safeUsage.UncachedInput);
                usageNode["cacheRead"] = JsonValue.From(safeUsage.CacheRead);
                usageNode["cacheWrite"] = JsonValue.From(safeUsage.CacheWrite);
                usageNode["output"] = JsonValue.From(safeUsage.Output);
                usageNode["total"] = JsonValue.From(safeUsage.Total);
                body["usage"] = usageNode;

                AppendEvent(sessionId, path, now, "session.meter", body);
            }

            return Task.CompletedTask;
        }

        /// <summary>置顶 / 取消置顶，并追加 <c>session.pinned</c> 事件保证索引重建后仍保留。</summary>
        public Task SetPinnedAsync(
            string sessionId,
            bool pinned,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);
            string path = RequireSessionFile(sessionId);
            DateTimeOffset now = _clock.UtcNow;

            lock (GetSessionGate(sessionId))
            {
                JsonValue body = JsonValue.NewObject();
                body["pinned"] = JsonValue.From(pinned);
                AppendEvent(sessionId, path, now, "session.pinned", body);

                lock (_indexGate)
                {
                    ConversationIndex index = GetIndexUnsafe();
                    ConversationIndexEntry entry = RequireEntry(index, sessionId, now);
                    entry.IsPinned = pinned;
                    MarkIndexDirty(index, persist: true);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>归档 / 取消归档，并追加 <c>session.archived</c> 事件。</summary>
        public Task SetArchivedAsync(
            string sessionId,
            bool archived,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);
            string path = RequireSessionFile(sessionId);
            DateTimeOffset now = _clock.UtcNow;

            lock (GetSessionGate(sessionId))
            {
                JsonValue body = JsonValue.NewObject();
                body["archived"] = JsonValue.From(archived);
                AppendEvent(sessionId, path, now, "session.archived", body);

                lock (_indexGate)
                {
                    ConversationIndex index = GetIndexUnsafe();
                    ConversationIndexEntry entry = RequireEntry(index, sessionId, now);
                    entry.IsArchived = archived;
                    MarkIndexDirty(index, persist: true);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 清空工作区的全部会话：删除 <c>sessions</c> 下合法的 JSONL 文件并重置索引。
        /// 其它目录（attachments / cache / skills）与工作区文件不受影响。
        /// </summary>
        public Task ClearAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_indexGate)
            {
                string directory = SessionsDirectory;
                if (Directory.Exists(directory))
                {
                    foreach (string file in Directory.GetFiles(
                        directory,
                        "*" + AgentWorkspaceLayout.SessionFileExtension))
                    {
                        string id = Path.GetFileNameWithoutExtension(file);
                        if (!AgentWorkspaceLayout.IsValidSessionId(id))
                        {
                            continue;
                        }

                        try
                        {
                            File.Delete(file);
                        }
                        catch (IOException)
                        {
                            // 被占用时保留该会话，清空其余会话；索引重建会补齐。
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }

                _lastSequences.Clear();
                _index = RebuildIndex();
                _indexLoaded = true;
                MarkIndexDirty(_index, persist: true);
            }

            return Task.CompletedTask;
        }

        public Task FlushAsync(string sessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_indexGate)
            {
                if (_indexDirty && _index != null)
                {
                    TryPersistIndex(_index);
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string sessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSessionId(sessionId);

            string path = AgentWorkspaceLayout.GetSessionFilePath(_workspacePath, sessionId);
            lock (GetSessionGate(sessionId))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                long ignored;
                _lastSequences.TryRemove(sessionId, out ignored);

                lock (_indexGate)
                {
                    ConversationIndex index = GetIndexUnsafe();
                    index.Sessions.RemoveAll(e => string.Equals(
                        e.SessionId,
                        sessionId,
                        StringComparison.Ordinal));
                    MarkIndexDirty(index, persist: true);
                }
            }

            return Task.CompletedTask;
        }

        private ConversationIndex GetIndexSnapshot()
        {
            lock (_indexGate)
            {
                ConversationIndex index = GetIndexUnsafe();
                if (_indexDirty)
                {
                    TryPersistIndex(index);
                }

                return index.Clone();
            }
        }

        /// <summary>必须在 <see cref="_indexGate"/> 内调用。</summary>
        private ConversationIndex GetIndexUnsafe()
        {
            if (_indexLoaded)
            {
                return _index;
            }

            _index = LoadOrRebuildIndex();
            _indexLoaded = true;
            return _index;
        }

        private ConversationIndex LoadOrRebuildIndex()
        {
            EnsureCreated();
            string indexPath = IndexFilePath;

            if (File.Exists(indexPath))
            {
                try
                {
                    ConversationIndex parsed = ConversationJson.ReadIndex(File.ReadAllText(indexPath));
                    if (parsed != null && IsIndexConsistent(parsed))
                    {
                        return parsed;
                    }
                }
                catch (IOException)
                {
                    // 落到重建。
                }
                catch (UnauthorizedAccessException)
                {
                    // 落到重建（重建通常只读，失败会得到空索引）。
                }

                AtomicFile.BackupCorrupt(indexPath, "corrupt");
            }

            ConversationIndex rebuilt = RebuildIndex();
            TryPersistIndex(rebuilt);
            return rebuilt;
        }

        /// <summary>索引与目录必须一一对应；缺文件或多文件都触发重建。</summary>
        private bool IsIndexConsistent(ConversationIndex index)
        {
            if (index.Sessions == null)
            {
                return false;
            }

            string directory = SessionsDirectory;
            if (!Directory.Exists(directory))
            {
                return index.Sessions.Count == 0;
            }

            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConversationIndexEntry entry in index.Sessions)
            {
                if (entry == null
                    || !AgentWorkspaceLayout.IsValidSessionId(entry.SessionId)
                    || !expected.Add(entry.SessionId))
                {
                    return false;
                }

                string path = AgentWorkspaceLayout.GetSessionFilePath(
                    _workspacePath,
                    entry.SessionId);
                if (!File.Exists(path))
                {
                    return false;
                }
            }

            foreach (string file in Directory.GetFiles(
                directory,
                "*" + AgentWorkspaceLayout.SessionFileExtension))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (AgentWorkspaceLayout.IsValidSessionId(id) && !expected.Contains(id))
                {
                    return false;
                }
            }

            return true;
        }

        private ConversationIndex RebuildIndex()
        {
            var index = new ConversationIndex();
            string directory = SessionsDirectory;
            if (!Directory.Exists(directory))
            {
                return index;
            }

            foreach (string file in Directory.GetFiles(
                directory,
                "*" + AgentWorkspaceLayout.SessionFileExtension))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (!AgentWorkspaceLayout.IsValidSessionId(id))
                {
                    continue;
                }

                try
                {
                    index.Sessions.Add(ToEntry(Replay(id, file)));
                }
                catch (IOException)
                {
                    // 单个会话损坏不影响其它会话。
                }
                catch (UnauthorizedAccessException)
                {
                    // 同上。
                }
            }

            SortEntries(index.Sessions);
            return index;
        }

        private ConversationTranscript Replay(string sessionId, string path)
        {
            DateTimeOffset now = _clock.UtcNow;
            var transcript = new ConversationTranscript
            {
                SessionId = sessionId,
                WorkspaceId = _workspaceId,
                Title = DefaultSessionTitle,
                CreatedAt = now,
                UpdatedAt = now,
                MessageCount = 0
            };

            var byId = new Dictionary<string, AgentMessage>(StringComparer.Ordinal);
            var ordered = new List<AgentMessage>();
            DateTimeOffset lastTimestamp = now;
            int total = 0;
            bool sawSessionCreated = false;

            using (var reader = new StreamReader(path, Utf8NoBom, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line.Trim().Length == 0)
                    {
                        continue;
                    }

                    JsonValue evt;
                    try
                    {
                        evt = JsonParser.Parse(line);
                    }
                    catch (Exception)
                    {
                        // 半行 / 断电截断：丢弃该行，继续加载。
                        continue;
                    }

                    if (evt == null || evt.Kind != JsonKind.Object)
                    {
                        continue;
                    }

                    string type = ConversationJson.GetString(evt, "type");
                    DateTimeOffset timestamp = ConversationJson.ParseTimestamp(
                        evt.Get("ts"),
                        lastTimestamp);
                    lastTimestamp = timestamp;

                    switch (type)
                    {
                        case "session.created":
                            ApplySessionCreated(transcript, evt, timestamp, ref sawSessionCreated);
                            break;

                        case "session.renamed":
                            ApplyRenamed(transcript, evt);
                            break;

                        case "session.pinned":
                            transcript.IsPinned = ConversationJson.GetBoolean(evt, "pinned");
                            break;

                        case "session.archived":
                            transcript.IsArchived = ConversationJson.GetBoolean(evt, "archived");
                            break;

                        case "session.model":
                            ApplyModel(transcript, evt);
                            break;

                        case "session.compacted":
                            ApplyCompacted(transcript, evt, timestamp);
                            break;

                        case "session.meter":
                            ApplyMeter(transcript, evt);
                            break;

                        case "message.added":
                            ApplyMessageAdded(evt, timestamp, byId, ordered, ref total);
                            break;

                        case "message.completed":
                            ApplyMessageCompleted(evt, timestamp, byId, ordered);
                            break;

                        case "message.failed":
                            ApplyMessageFailed(evt, timestamp, byId, ordered);
                            break;

                        case "message.removed":
                            ApplyMessageRemoved(evt, byId, ordered, ref total);
                            break;

                        // 未知类型按向前兼容规则跳过。
                    }
                }
            }

            foreach (AgentMessage message in ordered)
            {
                if (message.Role == AgentMessageRole.Assistant
                    && (message.Status == AgentMessageStatus.Streaming
                        || message.Status == AgentMessageStatus.Pending))
                {
                    message.Status = AgentMessageStatus.Interrupted;
                    message.StopReason = StopReason.Aborted;
                }
            }

            if (!sawSessionCreated && ordered.Count > 0)
            {
                transcript.CreatedAt = ordered[0].CreatedAt;
            }

            if (ordered.Count > MaxMessagesInMemory)
            {
                ordered.RemoveRange(0, ordered.Count - MaxMessagesInMemory);
            }

            transcript.Messages = ordered;
            transcript.MessageCount = total;
            transcript.UpdatedAt = lastTimestamp;
            if (string.IsNullOrWhiteSpace(transcript.Title))
            {
                transcript.Title = DefaultSessionTitle;
            }

            return transcript;
        }

        private static void ApplySessionCreated(
            ConversationTranscript transcript,
            JsonValue evt,
            DateTimeOffset timestamp,
            ref bool sawSessionCreated)
        {
            sawSessionCreated = true;
            transcript.CreatedAt = timestamp;
            transcript.UpdatedAt = timestamp;

            string title = ConversationJson.GetString(evt, "title");
            if (!string.IsNullOrWhiteSpace(title))
            {
                transcript.Title = Truncate(title.Trim(), MaxTitleLength);
            }

            string workspaceId = ConversationJson.GetString(evt, "workspaceId");
            if (!string.IsNullOrEmpty(workspaceId))
            {
                transcript.WorkspaceId = workspaceId;
            }

            ApplyModel(transcript, evt.Get("model"));
        }

        private static void ApplyRenamed(ConversationTranscript transcript, JsonValue evt)
        {
            string title = ConversationJson.GetString(evt, "title");
            if (!string.IsNullOrWhiteSpace(title))
            {
                transcript.Title = Truncate(title.Trim(), MaxTitleLength);
            }
        }

        private static void ApplyModel(ConversationTranscript transcript, JsonValue model)
        {
            if (model == null || model.Kind != JsonKind.Object)
            {
                return;
            }

            string providerId = ConversationJson.GetString(model, "providerId");
            string modelId = ConversationJson.GetString(model, "modelId");
            if (!string.IsNullOrEmpty(providerId))
            {
                transcript.ProviderId = providerId;
            }

            if (!string.IsNullOrEmpty(modelId))
            {
                transcript.ModelId = modelId;
            }
        }

        private static void ApplyCompacted(
            ConversationTranscript transcript,
            JsonValue evt,
            DateTimeOffset timestamp)
        {
            string summary = ConversationJson.GetString(evt, "summary");
            if (string.IsNullOrWhiteSpace(summary))
            {
                return;
            }

            transcript.Compaction = new AgentCompaction
            {
                Summary = summary,
                CoveredMessages = ConversationJson.GetInt(evt, "coveredMessages"),
                LastCoveredMessageId = ConversationJson.GetString(evt, "lastCoveredMessageId"),
                CompactedAt = ConversationJson.ParseTimestamp(evt.Get("compactedAt"), timestamp)
            };
        }

        /// <summary>最近一条 <c>session.meter</c> 覆盖之前的读数；只保留最后一次。</summary>
        private static void ApplyMeter(ConversationTranscript transcript, JsonValue evt)
        {
            JsonValue statsNode = evt == null ? null : evt.Get("stats");
            if (statsNode != null && statsNode.Kind == JsonKind.Object)
            {
                transcript.Stats = new SessionStats
                {
                    Turns = ConversationJson.GetInt(statsNode, "turns"),
                    Steps = ConversationJson.GetInt(statsNode, "steps"),
                    LlmMs = ReadLong(statsNode, "llmMs"),
                    ToolMs = ReadLong(statsNode, "toolMs"),
                    TtftMs = ReadLong(statsNode, "ttftMs"),
                    TtftSteps = ConversationJson.GetInt(statsNode, "ttftSteps"),
                    DecodeMs = ReadLong(statsNode, "decodeMs"),
                    DecodeTokens = ConversationJson.GetInt(statsNode, "decodeTokens")
                };
            }

            JsonValue usageNode = evt == null ? null : evt.Get("usage");
            if (usageNode != null && usageNode.Kind == JsonKind.Object)
            {
                transcript.Usage = new SessionTokenUsage
                {
                    UncachedInput = ConversationJson.GetInt(usageNode, "uncachedInput"),
                    CacheRead = ConversationJson.GetInt(usageNode, "cacheRead"),
                    CacheWrite = ConversationJson.GetInt(usageNode, "cacheWrite"),
                    Output = ConversationJson.GetInt(usageNode, "output"),
                    Total = ConversationJson.GetInt(usageNode, "total")
                };
            }
        }

        private static long ReadLong(JsonValue node, string name)
        {
            JsonValue value = node == null ? null : node.Get(name);
            return value == null || value.Kind != JsonKind.Number ? 0 : value.AsLong(0);
        }

        private static void ApplyMessageAdded(
            JsonValue evt,
            DateTimeOffset timestamp,
            Dictionary<string, AgentMessage> byId,
            List<AgentMessage> ordered,
            ref int total)
        {
            AgentMessage message = ConversationJson.ReadMessage(evt.Get("message"));
            if (message == null)
            {
                return;
            }

            if (message.CreatedAt == default(DateTimeOffset))
            {
                message.CreatedAt = timestamp;
            }

            AgentMessage existing;
            if (byId.TryGetValue(message.Id, out existing))
            {
                // 同一 ID 重复加入（乐观回显/重发）：原位替换，不增加计数。
                int index = ordered.IndexOf(existing);
                if (index >= 0)
                {
                    ordered[index] = message;
                }

                byId[message.Id] = message;
                return;
            }

            byId.Add(message.Id, message);
            ordered.Add(message);
            total++;
        }

        private static void ApplyMessageCompleted(
            JsonValue evt,
            DateTimeOffset timestamp,
            Dictionary<string, AgentMessage> byId,
            List<AgentMessage> ordered)
        {
            string messageId = ConversationJson.GetString(evt, "messageId");
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            AgentMessage message;
            if (!byId.TryGetValue(messageId, out message))
            {
                message = new AgentMessage
                {
                    Id = messageId,
                    Role = AgentMessageRole.Assistant,
                    CreatedAt = timestamp
                };
                byId.Add(messageId, message);
                ordered.Add(message);
            }

            message.Status = AgentMessageStatus.Completed;
            message.CompletedAt = timestamp;
            message.StopReason = ConversationJson.ParseStopReason(
                ConversationJson.GetString(evt, "stopReason"));

            JsonValue blocks = evt.Get("blocks");
            if (blocks != null && blocks.Kind == JsonKind.Array)
            {
                message.Blocks = ConversationJson.ReadBlocks(blocks);
            }

            TokenUsage usage = ConversationJson.ReadUsage(evt.Get("usage"));
            if (usage != null)
            {
                message.Usage = usage;
            }

            string modelId = ConversationJson.GetString(evt, "modelId");
            if (!string.IsNullOrEmpty(modelId))
            {
                message.ModelId = modelId;
            }

            string providerId = ConversationJson.GetString(evt, "providerId");
            if (!string.IsNullOrEmpty(providerId))
            {
                message.ProviderId = providerId;
            }
        }

        private static void ApplyMessageFailed(
            JsonValue evt,
            DateTimeOffset timestamp,
            Dictionary<string, AgentMessage> byId,
            List<AgentMessage> ordered)
        {
            string messageId = ConversationJson.GetString(evt, "messageId");
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            AgentMessage message;
            if (!byId.TryGetValue(messageId, out message))
            {
                message = new AgentMessage
                {
                    Id = messageId,
                    Role = AgentMessageRole.Assistant,
                    CreatedAt = timestamp
                };
                byId.Add(messageId, message);
                ordered.Add(message);
            }

            message.Status = AgentMessageStatus.Failed;
            message.StopReason = StopReason.Error;

            JsonValue blocks = evt.Get("blocks");
            if (blocks != null && blocks.Kind == JsonKind.Array)
            {
                message.Blocks = ConversationJson.ReadBlocks(blocks);
            }

            TokenUsage usage = ConversationJson.ReadUsage(evt.Get("usage"));
            if (usage != null)
            {
                message.Usage = usage;
            }

            AgentError error = ConversationJson.ReadError(evt.Get("error"));
            if (error != null)
            {
                message.ErrorMessage = error.Message;
            }
        }

        private static void ApplyMessageRemoved(
            JsonValue evt,
            Dictionary<string, AgentMessage> byId,
            List<AgentMessage> ordered,
            ref int total)
        {
            string messageId = ConversationJson.GetString(evt, "messageId");
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            AgentMessage existing;
            if (!byId.TryGetValue(messageId, out existing))
            {
                return;
            }

            byId.Remove(messageId);
            ordered.Remove(existing);
            if (total > 0)
            {
                total--;
            }
        }

        private void UpdateEntryAfterAppend(
            string sessionId,
            AgentMessage message,
            DateTimeOffset timestamp,
            bool persist)
        {
            lock (_indexGate)
            {
                ConversationIndex index = GetIndexUnsafe();
                ConversationIndexEntry entry = FindEntry(index, sessionId);
                if (entry == null)
                {
                    entry = new ConversationIndexEntry
                    {
                        SessionId = sessionId,
                        WorkspaceId = _workspaceId,
                        Title = DefaultSessionTitle,
                        CreatedAt = timestamp,
                        Preview = string.Empty
                    };
                    index.Sessions.Add(entry);
                }

                entry.MessageCount++;
                entry.UpdatedAt = timestamp;
                if (message.Role == AgentMessageRole.User)
                {
                    entry.Preview = ConversationJson.BuildPreview(
                        message.GetText(),
                        MaxPreviewLength);
                }

                if (!string.IsNullOrEmpty(message.ProviderId)
                    || !string.IsNullOrEmpty(message.ModelId))
                {
                    entry.LastModel = BuildModelRef(message.ProviderId, message.ModelId);
                }

                MarkIndexDirty(index, persist);
            }
        }

        private void UpdateEntryAfterCompletion(
            string sessionId,
            AgentMessage message,
            DateTimeOffset timestamp,
            AgentError error)
        {
            lock (_indexGate)
            {
                ConversationIndex index = GetIndexUnsafe();
                ConversationIndexEntry entry = FindEntry(index, sessionId);
                if (entry == null)
                {
                    entry = new ConversationIndexEntry
                    {
                        SessionId = sessionId,
                        WorkspaceId = _workspaceId,
                        Title = DefaultSessionTitle,
                        CreatedAt = timestamp,
                        MessageCount = 1
                    };
                    index.Sessions.Add(entry);
                }

                entry.UpdatedAt = timestamp;
                if (!string.IsNullOrEmpty(message.ProviderId)
                    || !string.IsNullOrEmpty(message.ModelId))
                {
                    entry.LastModel = BuildModelRef(message.ProviderId, message.ModelId);
                }

                string text = message.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    entry.Preview = ConversationJson.BuildPreview(text, MaxPreviewLength);
                }
                else if (error != null && !string.IsNullOrEmpty(error.Message))
                {
                    entry.Preview = ConversationJson.BuildPreview(
                        error.Message,
                        MaxPreviewLength);
                }

                MarkIndexDirty(index, persist: true);
            }
        }

        private void ReconcileIndex(ConversationTranscript transcript)
        {
            lock (_indexGate)
            {
                ConversationIndex index = GetIndexUnsafe();
                ConversationIndexEntry entry = FindEntry(index, transcript.SessionId);
                ConversationIndexEntry fresh = ToEntry(transcript);

                if (entry == null)
                {
                    index.Sessions.Add(fresh);
                    MarkIndexDirty(index, persist: true);
                    return;
                }

                bool changed = entry.MessageCount != fresh.MessageCount
                    || entry.IsPinned != fresh.IsPinned
                    || entry.IsArchived != fresh.IsArchived
                    || !string.Equals(entry.Title, fresh.Title, StringComparison.Ordinal)
                    || !string.Equals(entry.Preview, fresh.Preview, StringComparison.Ordinal)
                    || !string.Equals(entry.LastModel, fresh.LastModel, StringComparison.Ordinal)
                    || entry.UpdatedAt != fresh.UpdatedAt;

                if (!changed)
                {
                    return;
                }

                entry.Title = fresh.Title;
                entry.Preview = fresh.Preview;
                entry.LastModel = fresh.LastModel;
                entry.MessageCount = fresh.MessageCount;
                entry.UpdatedAt = fresh.UpdatedAt;
                entry.CreatedAt = fresh.CreatedAt;
                entry.IsPinned = fresh.IsPinned;
                entry.IsArchived = fresh.IsArchived;
                MarkIndexDirty(index, persist: true);
            }
        }

        private void UpsertEntry(ConversationIndexEntry entry, bool persist)
        {
            lock (_indexGate)
            {
                ConversationIndex index = GetIndexUnsafe();
                int existing = index.Sessions.FindIndex(e => string.Equals(
                    e.SessionId,
                    entry.SessionId,
                    StringComparison.Ordinal));

                if (existing >= 0)
                {
                    index.Sessions[existing] = entry;
                }
                else
                {
                    index.Sessions.Add(entry);
                }

                MarkIndexDirty(index, persist);
            }
        }

        private ConversationIndexEntry RequireEntry(
            ConversationIndex index,
            string sessionId,
            DateTimeOffset now)
        {
            ConversationIndexEntry entry = FindEntry(index, sessionId);
            if (entry != null)
            {
                return entry;
            }

            entry = new ConversationIndexEntry
            {
                SessionId = sessionId,
                WorkspaceId = _workspaceId,
                Title = DefaultSessionTitle,
                CreatedAt = now,
                UpdatedAt = now,
                MessageCount = 0
            };
            index.Sessions.Add(entry);
            return entry;
        }

        private void MarkIndexDirty(ConversationIndex index, bool persist)
        {
            SortEntries(index.Sessions);
            _indexDirty = true;
            if (persist)
            {
                TryPersistIndex(index);
            }
        }

        private void TryPersistIndex(ConversationIndex index)
        {
            try
            {
                AtomicFile.WriteAllText(IndexFilePath, JsonWriter.WriteIndented(
                    ConversationJson.WriteIndex(index)));
                _indexDirty = false;
            }
            catch (IOException)
            {
                _indexDirty = true;
            }
            catch (UnauthorizedAccessException)
            {
                _indexDirty = true;
            }
        }

        private static ConversationIndexEntry FindEntry(
            ConversationIndex index,
            string sessionId)
        {
            foreach (ConversationIndexEntry entry in index.Sessions)
            {
                if (entry != null && string.Equals(
                    entry.SessionId,
                    sessionId,
                    StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        private static ConversationIndexEntry ToEntry(ConversationTranscript transcript)
        {
            var entry = new ConversationIndexEntry
            {
                SessionId = transcript.SessionId,
                WorkspaceId = transcript.WorkspaceId,
                Title = string.IsNullOrWhiteSpace(transcript.Title)
                    ? DefaultSessionTitle
                    : transcript.Title,
                CreatedAt = transcript.CreatedAt,
                UpdatedAt = transcript.UpdatedAt,
                IsPinned = transcript.IsPinned,
                IsArchived = transcript.IsArchived,
                MessageCount = transcript.MessageCount,
                LastModel = BuildModelRef(transcript.ProviderId, transcript.ModelId),
                Preview = string.Empty
            };

            for (int i = transcript.Messages.Count - 1; i >= 0; i--)
            {
                AgentMessage message = transcript.Messages[i];
                if (message == null
                    || message.Role != AgentMessageRole.Assistant
                    || message.Status == AgentMessageStatus.Streaming)
                {
                    continue;
                }

                string text = message.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    entry.Preview = ConversationJson.BuildPreview(text, MaxPreviewLength);
                    break;
                }
            }

            return entry;
        }

        private static void SortEntries(List<ConversationIndexEntry> entries)
        {
            entries.Sort(CompareEntries);
        }

        private static int CompareEntries(ConversationIndexEntry left, ConversationIndexEntry right)
        {
            if (left == null || right == null)
            {
                return left == null ? (right == null ? 0 : 1) : -1;
            }

            if (left.IsArchived != right.IsArchived)
            {
                return left.IsArchived ? 1 : -1;
            }

            if (left.IsPinned != right.IsPinned)
            {
                return left.IsPinned ? -1 : 1;
            }

            int byTime = right.UpdatedAt.CompareTo(left.UpdatedAt);
            if (byTime != 0)
            {
                return byTime;
            }

            return string.Compare(left.SessionId, right.SessionId, StringComparison.Ordinal);
        }

        private string RequireSessionFile(string sessionId)
        {
            string path = AgentWorkspaceLayout.GetSessionFilePath(_workspacePath, sessionId);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("会话不存在：" + sessionId);
            }

            return path;
        }

        private object GetSessionGate(string sessionId)
        {
            return _sessionGates.GetOrAdd(sessionId, _ => new object());
        }

        private long AppendEvent(
            string sessionId,
            string path,
            DateTimeOffset timestamp,
            string type,
            JsonValue body)
        {
            long last;
            if (!_lastSequences.TryGetValue(sessionId, out last))
            {
                last = ReadLastSequence(path);
            }

            long sequence = last + 1;
            string line = ConversationJson.BuildEventLine(sequence, timestamp, type, body);

            EnsureTrailingNewline(path);

            byte[] bytes = Utf8NoBom.GetBytes(line + "\n");
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            _lastSequences[sessionId] = sequence;
            return sequence;
        }

        /// <summary>
        /// 崩溃可能在文件中间留下一行未写完的 JSON。追加前先补一个换行，
        /// 让半行独立成行，重放时被丢弃，而不是和新事件粘在一起。
        /// </summary>
        private static void EnsureTrailingNewline(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                if (stream.Length == 0)
                {
                    return;
                }

                stream.Seek(-1, SeekOrigin.End);
                int last = stream.ReadByte();
                if (last == '\n')
                {
                    return;
                }

                stream.Seek(0, SeekOrigin.End);
                stream.WriteByte((byte)'\n');
                stream.Flush(true);
            }
        }

        /// <summary>读取文件尾部最后一个可解析事件的 seq；文件不存在或损坏时返回 0。</summary>
        private static long ReadLastSequence(string path)
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                {
                    long length = stream.Length;
                    if (length == 0)
                    {
                        return 0;
                    }

                    int take = (int)Math.Min(length, TailReadBytes);
                    byte[] buffer = new byte[take];
                    stream.Seek(-take, SeekOrigin.End);
                    int read = 0;
                    while (read < take)
                    {
                        int chunk = stream.Read(buffer, read, take - read);
                        if (chunk <= 0)
                        {
                            break;
                        }

                        read += chunk;
                    }

                    string tail = Utf8NoBom.GetString(buffer, 0, read);
                    string[] lines = tail.Split('\n');
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        string line = lines[i].Trim('\r', ' ', '\t');
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        try
                        {
                            JsonValue evt = JsonParser.Parse(line);
                            JsonValue seq = evt == null ? null : evt.Get("seq");
                            if (seq != null && seq.Kind == JsonKind.Number)
                            {
                                return seq.AsLong();
                            }
                        }
                        catch (Exception)
                        {
                            // 继续向前找上一行。
                        }
                    }
                }
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }

            return 0;
        }

        private static JsonValue WriteBlocks(List<AgentContentBlock> blocks)
        {
            JsonValue array = JsonValue.NewArray();
            if (blocks != null)
            {
                foreach (AgentContentBlock block in blocks)
                {
                    if (block != null)
                    {
                        array.Add(ConversationJson.WriteBlock(block));
                    }
                }
            }

            return array;
        }

        private static string RequireSessionId(string sessionId)
        {
            if (!AgentWorkspaceLayout.IsValidSessionId(sessionId))
            {
                throw new ArgumentException(
                    "会话 ID 只能包含字母、数字、下划线与连字符，且长度不超过 64。",
                    nameof(sessionId));
            }

            return sessionId;
        }

        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return DefaultSessionTitle;
            }

            return Truncate(title.Trim(), MaxTitleLength);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            int cut = maxLength;
            if (char.IsHighSurrogate(value[cut - 1]))
            {
                cut--;
            }

            return value.Substring(0, cut);
        }

        private static string BuildModelRef(string providerId, string modelId)
        {
            if (string.IsNullOrEmpty(providerId))
            {
                return modelId;
            }

            if (string.IsNullOrEmpty(modelId))
            {
                return providerId;
            }

            return providerId + "/" + modelId;
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
