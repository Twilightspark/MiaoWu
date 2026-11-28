using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MiaoWu.Core.Agents.Tools
{
    /// <summary>提问的生命周期。提问不自动超时，只能被回答、取消或随会话结束作废。</summary>
    public enum UserQuestionStatus
    {
        Pending = 0,
        Answered = 1,
        Cancelled = 2
    }

    /// <summary>一个可选项。Id 由工具生成（<c>q1o1</c> 这种稳定短名），Label 展示给用户。</summary>
    public sealed class UserQuestionOption
    {
        public string Id { get; set; }

        public string Label { get; set; }

        /// <summary>可选的补充说明，展示在选项下方；为空时只显示 Label。</summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// 一条需要用户选择的提问。<see cref="Question"/> 是正文，<see cref="Header"/> 是
    /// 通知标题里的一句短标签（可为空）。
    /// </summary>
    public sealed class UserQuestionRequest
    {
        public string RequestId { get; set; }

        public string SessionId { get; set; }

        /// <summary>发起提问的工具名，目前固定是 <c>ask_user_question</c>。</summary>
        public string ToolName { get; set; }

        /// <summary>通知标题里的短标签，例如“选择范围”。为空时标题只写“需要你回答”。</summary>
        public string Header { get; set; }

        public string Question { get; set; }

        /// <summary>至少一个、最多 <see cref="MaxOptions"/> 个选项。</summary>
        public IReadOnlyList<UserQuestionOption> Options { get; set; }

        /// <summary>true 时允许多选；false 时必须恰好选一个。</summary>
        public bool MultiSelect { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>单个提问允许的选项上限。</summary>
        public const int MaxOptions = 6;

        /// <summary>自由文本回答的长度上限。</summary>
        public const int MaxAnswerTextLength = 2000;
    }

    /// <summary>用户对一条提问的答复。取消时 <see cref="IsCancelled"/> 为 true。</summary>
    public sealed class UserQuestionAnswer
    {
        public string RequestId { get; set; }

        public IReadOnlyList<string> SelectedOptionIds { get; set; }

        /// <summary>与 <see cref="SelectedOptionIds"/> 一一对应的展示文本，便于直接回给模型。</summary>
        public IReadOnlyList<string> SelectedLabels { get; set; }

        /// <summary>自由文本回答；选项回答或取消时为空。</summary>
        public string Text { get; set; }

        public bool IsCancelled { get; set; }

        public DateTimeOffset AnsweredAt { get; set; }
    }

    public enum UserQuestionChangeKind
    {
        Requested = 0,
        Answered = 1,
        Cancelled = 2
    }

    public sealed class UserQuestionEventArgs : EventArgs
    {
        public UserQuestionEventArgs(UserQuestionChangeKind kind, UserQuestionRequest request)
        {
            Kind = kind;
            Request = request;
        }

        public UserQuestionChangeKind Kind { get; }

        public UserQuestionRequest Request { get; }
    }

    /// <summary>
    /// 提问通道。实现只管挂起 / 答复 / 取消，不负责展示；
    /// UI 通过 <see cref="Changed"/> 增删通知，工具通过 <see cref="AskAsync"/> 等待答复。
    /// </summary>
    public interface IUserQuestionService
    {
        event EventHandler<UserQuestionEventArgs> Changed;

        /// <summary>挂起一条提问；取消标记触发时返回取消答复，不抛异常。</summary>
        Task<UserQuestionAnswer> AskAsync(UserQuestionRequest request, CancellationToken cancellationToken);

        /// <summary>回答指定提问；不存在、已处理或选项非法时返回 false。</summary>
        bool TryAnswer(string requestId, IReadOnlyList<string> optionIds);

        /// <summary>
        /// 用自由文本回答；文本为空 / 超长、ID 不存在或已处理时返回 false。
        /// 选项覆盖不了答案时用户总能自己写一句，所以文字答复与选项答复一样总是可用。
        /// </summary>
        bool TryAnswerText(string requestId, string text);

        /// <summary>跳过指定提问（用户主动取消）；已处理或不存在时返回 false。</summary>
        bool TryCancel(string requestId);

        IReadOnlyList<UserQuestionRequest> GetPending();

        IReadOnlyList<UserQuestionRequest> GetPending(string sessionId);

        int GetPendingCount();

        /// <summary>会话结束：取消该会话的全部待回答提问。</summary>
        void ClearSession(string sessionId);
    }

    /// <summary>
    /// 默认实现：内存字典 + TaskCompletionSource，所有公开成员线程安全。
    /// 与审批一致，提问不会自动超时——必须由用户选择 / 跳过，或随会话结束作废。
    /// </summary>
    public sealed class UserQuestionService : IUserQuestionService
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, PendingQuestion> _pending =
            new Dictionary<string, PendingQuestion>(StringComparer.Ordinal);

        public event EventHandler<UserQuestionEventArgs> Changed;

        public Task<UserQuestionAnswer> AskAsync(
            UserQuestionRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                request.RequestId = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                throw new ArgumentException("提问必须带会话 ID。", nameof(request));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(new UserQuestionAnswer
                {
                    RequestId = request.RequestId,
                    IsCancelled = true,
                    SelectedOptionIds = new string[0],
                    SelectedLabels = new string[0],
                    AnsweredAt = DateTimeOffset.UtcNow
                });
            }

            PendingQuestion pending;
            lock (_gate)
            {
                if (_pending.TryGetValue(request.RequestId, out pending))
                {
                    return pending.Completion.Task;
                }

                pending = new PendingQuestion(request);
                _pending.Add(request.RequestId, pending);

                if (cancellationToken.CanBeCanceled)
                {
                    pending.HasCancellation = true;
                    pending.CancellationRegistration = cancellationToken.Register(
                        () => CancelPending(request.RequestId));
                }
            }

            RaiseChanged(new UserQuestionEventArgs(UserQuestionChangeKind.Requested, request));
            return pending.Completion.Task;
        }

        public bool TryAnswer(string requestId, IReadOnlyList<string> optionIds)
        {
            if (string.IsNullOrWhiteSpace(requestId) || optionIds == null || optionIds.Count == 0)
            {
                return false;
            }

            PendingQuestion pending;
            List<UserQuestionOption> selected;

            lock (_gate)
            {
                if (!_pending.TryGetValue(requestId, out pending))
                {
                    return false;
                }

                selected = MatchOptions(pending.Request, optionIds);
                if (selected == null)
                {
                    return false;
                }

                _pending.Remove(requestId);
            }

            pending.ReleaseCancellation();

            var answer = new UserQuestionAnswer
            {
                RequestId = requestId,
                SelectedOptionIds = BuildIds(selected),
                SelectedLabels = BuildLabels(selected),
                IsCancelled = false,
                AnsweredAt = DateTimeOffset.UtcNow
            };

            RaiseChanged(new UserQuestionEventArgs(UserQuestionChangeKind.Answered, pending.Request));
            pending.Completion.TrySetResult(answer);
            return true;
        }

        public bool TryCancel(string requestId)
        {
            return CancelPending(requestId);
        }

        public bool TryAnswerText(string requestId, string text)
        {
            if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (trimmed.Length > UserQuestionRequest.MaxAnswerTextLength)
            {
                return false;
            }

            PendingQuestion pending;
            lock (_gate)
            {
                if (!_pending.TryGetValue(requestId, out pending))
                {
                    return false;
                }

                _pending.Remove(requestId);
            }

            pending.ReleaseCancellation();

            var answer = new UserQuestionAnswer
            {
                RequestId = requestId,
                SelectedOptionIds = new string[0],
                SelectedLabels = new string[0],
                Text = trimmed,
                IsCancelled = false,
                AnsweredAt = DateTimeOffset.UtcNow
            };

            RaiseChanged(new UserQuestionEventArgs(UserQuestionChangeKind.Answered, pending.Request));
            pending.Completion.TrySetResult(answer);
            return true;
        }

        public IReadOnlyList<UserQuestionRequest> GetPending()
        {
            lock (_gate)
            {
                return SnapshotLocked(null);
            }
        }

        public IReadOnlyList<UserQuestionRequest> GetPending(string sessionId)
        {
            lock (_gate)
            {
                return SnapshotLocked(sessionId);
            }
        }

        public int GetPendingCount()
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }

        public void ClearSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            List<string> ids;
            lock (_gate)
            {
                ids = new List<string>();
                foreach (KeyValuePair<string, PendingQuestion> pair in _pending)
                {
                    if (string.Equals(pair.Value.Request.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        ids.Add(pair.Key);
                    }
                }
            }

            for (int i = 0; i < ids.Count; i++)
            {
                CancelPending(ids[i]);
            }
        }

        private bool CancelPending(string requestId)
        {
            PendingQuestion pending;
            lock (_gate)
            {
                if (!_pending.TryGetValue(requestId, out pending))
                {
                    return false;
                }

                _pending.Remove(requestId);
            }

            pending.ReleaseCancellation();

            RaiseChanged(new UserQuestionEventArgs(UserQuestionChangeKind.Cancelled, pending.Request));
            pending.Completion.TrySetResult(new UserQuestionAnswer
            {
                RequestId = requestId,
                SelectedOptionIds = new string[0],
                SelectedLabels = new string[0],
                IsCancelled = true,
                AnsweredAt = DateTimeOffset.UtcNow
            });

            return true;
        }

        private static List<UserQuestionOption> MatchOptions(
            UserQuestionRequest request,
            IReadOnlyList<string> optionIds)
        {
            var options = request.Options ?? new UserQuestionOption[0];
            var selected = new List<UserQuestionOption>(optionIds.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < optionIds.Count; i++)
            {
                string id = optionIds[i];
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id.Trim()))
                {
                    return null;
                }

                UserQuestionOption match = null;
                for (int j = 0; j < options.Count; j++)
                {
                    UserQuestionOption option = options[j];
                    if (option != null
                        && string.Equals(option.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        match = option;
                        break;
                    }
                }

                if (match == null)
                {
                    return null;
                }

                selected.Add(match);
            }

            if (!request.MultiSelect && selected.Count != 1)
            {
                return null;
            }

            return selected;
        }

        private static IReadOnlyList<string> BuildIds(IReadOnlyList<UserQuestionOption> options)
        {
            var list = new string[options.Count];
            for (int i = 0; i < options.Count; i++)
            {
                list[i] = options[i].Id;
            }

            return list;
        }

        private static IReadOnlyList<string> BuildLabels(IReadOnlyList<UserQuestionOption> options)
        {
            var list = new string[options.Count];
            for (int i = 0; i < options.Count; i++)
            {
                list[i] = options[i].Label;
            }

            return list;
        }

        private IReadOnlyList<UserQuestionRequest> SnapshotLocked(string sessionId)
        {
            var list = new List<UserQuestionRequest>(_pending.Count);
            foreach (KeyValuePair<string, PendingQuestion> pair in _pending)
            {
                if (sessionId == null
                    || string.Equals(pair.Value.Request.SessionId, sessionId, StringComparison.Ordinal))
                {
                    list.Add(pair.Value.Request);
                }
            }

            return list;
        }

        private void RaiseChanged(UserQuestionEventArgs args)
        {
            EventHandler<UserQuestionEventArgs> handler = Changed;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, args);
            }
            catch (Exception)
            {
                // 订阅者异常不能破坏提问状态机。
            }
        }

        private sealed class PendingQuestion
        {
            public PendingQuestion(UserQuestionRequest request)
            {
                Request = request;
                Completion = new TaskCompletionSource<UserQuestionAnswer>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public UserQuestionRequest Request { get; }

            public TaskCompletionSource<UserQuestionAnswer> Completion { get; }

            public CancellationTokenRegistration CancellationRegistration { get; set; }

            public bool HasCancellation { get; set; }

            public void ReleaseCancellation()
            {
                if (HasCancellation)
                {
                    CancellationRegistration.Dispose();
                    CancellationRegistration = default(CancellationTokenRegistration);
                    HasCancellation = false;
                }
            }
        }
    }
}
