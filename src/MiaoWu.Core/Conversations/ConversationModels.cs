using System;
using System.Collections.Generic;
using MiaoWu.Core.Agents;
using MiaoWu.Core.Errors;
using MiaoWu.Core.Models;

namespace MiaoWu.Core.Conversations
{
    /// <summary>
    /// 一条会话的完整内容。加载时由 JSONL 事件日志重放得到，
    /// 不是磁盘上的原样结构；写盘只有追加事件一种形式。
    /// </summary>
    public sealed class ConversationTranscript
    {
        public ConversationTranscript()
        {
            Messages = new List<AgentMessage>();
        }

        public string SessionId { get; set; }

        public string WorkspaceId { get; set; }

        public string Title { get; set; }

        public string ProviderId { get; set; }

        public string ModelId { get; set; }

        public bool IsPinned { get; set; }

        public bool IsArchived { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>磁盘上的消息总数（含已被 5000 条内存上限裁掉的更早消息）。</summary>
        public int MessageCount { get; set; }

        /// <summary>加载进内存的消息；最多 5000 条，按时间顺序。</summary>
        public List<AgentMessage> Messages { get; set; }

        /// <summary>最近一次压缩；为空表示还没有压缩过。压缩不删消息，只改变请求上下文。</summary>
        public AgentCompaction Compaction { get; set; }

        /// <summary>
        /// 会话级工作统计。落盘在 <c>session.meter</c>，消息被 5000 条内存上限裁掉后仍然完整。
        /// 旧会话没有这条记录时为 <c>null</c>，调用方回退到消息折叠。
        /// </summary>
        public SessionStats Stats { get; set; }

        /// <summary>
        /// 会话级 token 分桶。与 <see cref="Stats"/> 同一条记录落盘，同样不受内存裁剪影响。
        /// </summary>
        public SessionTokenUsage Usage { get; set; }

        public string GetPlainText()
        {
            var builder = new System.Text.StringBuilder();
            foreach (AgentMessage message in Messages)
            {
                if (message == null)
                {
                    continue;
                }

                string text = message.GetText();
                if (text.Length == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append("\n\n");
                }

                builder.Append(text);
            }

            return builder.ToString();
        }
    }

    /// <summary>左栏与启动恢复所需的会话摘要。索引里只存这些字段。</summary>
    public sealed class ConversationIndexEntry
    {
        public string SessionId { get; set; }

        public string WorkspaceId { get; set; }

        public string Title { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public bool IsPinned { get; set; }

        public bool IsArchived { get; set; }

        public int MessageCount { get; set; }

        /// <summary>"providerId/modelId"；未配置时为空。</summary>
        public string LastModel { get; set; }

        /// <summary>最后一条助手文本的前 80 字，用于列表二次识别。</summary>
        public string Preview { get; set; }

        public ConversationIndexEntry Clone()
        {
            return new ConversationIndexEntry
            {
                SessionId = SessionId,
                WorkspaceId = WorkspaceId,
                Title = Title,
                UpdatedAt = UpdatedAt,
                CreatedAt = CreatedAt,
                IsPinned = IsPinned,
                IsArchived = IsArchived,
                MessageCount = MessageCount,
                LastModel = LastModel,
                Preview = Preview
            };
        }
    }

    /// <summary>每个工作区一份 <c>.agent/sessions/index.json</c>。</summary>
    public sealed class ConversationIndex
    {
        public const int CurrentSchemaVersion = 1;

        public ConversationIndex()
        {
            SchemaVersion = CurrentSchemaVersion;
            Sessions = new List<ConversationIndexEntry>();
        }

        public int SchemaVersion { get; set; }

        public List<ConversationIndexEntry> Sessions { get; set; }

        public ConversationIndex Clone()
        {
            var copy = new ConversationIndex { SchemaVersion = SchemaVersion };
            if (Sessions != null)
            {
                foreach (ConversationIndexEntry entry in Sessions)
                {
                    if (entry != null)
                    {
                        copy.Sessions.Add(entry.Clone());
                    }
                }
            }

            return copy;
        }
    }

    /// <summary>创建会话时由 UI 提供的描述信息。</summary>
    public sealed class ConversationDescriptor
    {
        public string SessionId { get; set; }

        public string WorkspaceId { get; set; }

        public string Title { get; set; }

        public string ProviderId { get; set; }

        public string ModelId { get; set; }

        public DateTimeOffset? CreatedAt { get; set; }
    }

    /// <summary>
    /// 会话存储。所有实现都必须满足：单写者追加、完成消息先落盘再发布、
    /// 崩溃最多丢当前正在生成的一条、索引损坏可重建。
    /// </summary>
    public interface IConversationStore
    {
        /// <summary>加载（必要时重建）索引；不加载消息正文。</summary>
        System.Threading.Tasks.Task<ConversationIndex> LoadIndexAsync(
            System.Threading.CancellationToken cancellationToken);

        /// <summary>创建会话；对已存在的会话幂等。</summary>
        System.Threading.Tasks.Task CreateAsync(
            ConversationDescriptor descriptor,
            System.Threading.CancellationToken cancellationToken);

        /// <summary>重放一条会话；不存在时返回 <c>null</c>。</summary>
        System.Threading.Tasks.Task<ConversationTranscript> LoadAsync(
            string sessionId,
            System.Threading.CancellationToken cancellationToken);

        System.Threading.Tasks.Task AppendMessageAsync(
            string sessionId,
            AgentMessage message,
            System.Threading.CancellationToken cancellationToken);

        System.Threading.Tasks.Task CompleteMessageAsync(
            string sessionId,
            AgentMessage message,
            System.Threading.CancellationToken cancellationToken);

        System.Threading.Tasks.Task FailMessageAsync(
            string sessionId,
            AgentMessage message,
            AgentError error,
            System.Threading.CancellationToken cancellationToken);

        /// <summary>移除一条消息（重试 / 编辑截断）。磁盘上追加 removed 事件。</summary>
        System.Threading.Tasks.Task RemoveMessageAsync(
            string sessionId,
            string messageId,
            string reason,
            System.Threading.CancellationToken cancellationToken);

        System.Threading.Tasks.Task RenameAsync(
            string sessionId,
            string title,
            System.Threading.CancellationToken cancellationToken);

        /// <summary>追加一条压缩记录（<c>session.compacted</c>）；消息本身不删除。</summary>
        System.Threading.Tasks.Task CompactAsync(
            string sessionId,
            AgentCompaction compaction,
            System.Threading.CancellationToken cancellationToken);

        /// <summary>
        /// 追加一条会话读数快照（<c>session.meter</c>）：累计统计与 token 分桶。
        /// 只保留最后一次，重放时覆盖。
        /// </summary>
        System.Threading.Tasks.Task SaveMeterAsync(
            string sessionId,
            SessionStats stats,
            SessionTokenUsage usage,
            System.Threading.CancellationToken cancellationToken);

        /// <summary>把脏索引与未刷新的尾部写到磁盘。</summary>
        System.Threading.Tasks.Task FlushAsync(
            string sessionId,
            System.Threading.CancellationToken cancellationToken);

        System.Threading.Tasks.Task DeleteAsync(
            string sessionId,
            System.Threading.CancellationToken cancellationToken);
    }
}
