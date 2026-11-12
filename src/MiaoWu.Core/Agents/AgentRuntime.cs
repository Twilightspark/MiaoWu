using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Conversations;
using MiaoWu.Core.Models;

namespace MiaoWu.Core.Agents
{
    /// <summary>
    /// 进程内智能体运行时：会话注册表、热池、上游并发闸门与统一关停。
    /// 不碰 UI、不碰 HTTP；每个会话一个串行 loop。
    /// </summary>
    public sealed class AgentRuntime : IAgentRuntime
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, AgentSession> _sessions =
            new Dictionary<string, AgentSession>(StringComparer.Ordinal);

        private readonly AgentSessionServices _services;
        private readonly AgentRuntimeOptions _options;
        private readonly SemaphoreSlim _upstreamGate;
        private bool _shutdown;
        private bool _disposed;

        public AgentRuntime(AgentSessionServices services, AgentRuntimeOptions options)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _options = options ?? new AgentRuntimeOptions();
            int maxConcurrent = Math.Max(1, Math.Min(8, _options.MaxConcurrentRequests));
            _upstreamGate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        }

        public event EventHandler<AgentSessionSummaryEventArgs> SessionChanged;

        public void Start()
        {
            // 运行时是惰性的：会话在第一次 GetOrCreateAsync 时创建，loop 在第一次发送时启动。
            // 保留 Start() 作为组合根的显式启动点，后续清理任务可以挂在这里。
        }

        public async Task<IAgentSession> GetOrCreateAsync(
            AgentSessionOptions options,
            CancellationToken cancellationToken)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.SessionId))
            {
                throw new ArgumentException("会话 ID 不能为空。", nameof(options));
            }

            if (_shutdown)
            {
                throw new InvalidOperationException("运行时已关闭。");
            }

            lock (_gate)
            {
                AgentSession existing;
                if (_sessions.TryGetValue(options.SessionId, out existing))
                {
                    return existing;
                }
            }

            IConversationStore store = options.Store ?? _services.Store;
            if (store == null)
            {
                throw new InvalidOperationException("会话存储未配置。");
            }

            ConversationTranscript transcript = await store
                .LoadAsync(options.SessionId, cancellationToken)
                .ConfigureAwait(false);

            if (transcript == null)
            {
                await store.CreateAsync(
                    new ConversationDescriptor
                    {
                        SessionId = options.SessionId,
                        WorkspaceId = options.WorkspaceId,
                        Title = "新会话",
                        ProviderId = options.ProviderId,
                        ModelId = options.ModelId
                    },
                    cancellationToken).ConfigureAwait(false);

                transcript = await store
                    .LoadAsync(options.SessionId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(options.ProviderId) || string.IsNullOrWhiteSpace(options.ModelId))
            {
                ModelDescriptor fallback = _services.Models == null ? null : _services.Models.GetDefault();
                if (fallback != null)
                {
                    if (string.IsNullOrWhiteSpace(options.ProviderId))
                    {
                        options.ProviderId = fallback.ProviderId;
                    }

                    if (string.IsNullOrWhiteSpace(options.ModelId))
                    {
                        options.ModelId = fallback.ModelId;
                    }
                }
            }

            var session = new AgentSession(options, transcript, _services, _upstreamGate);
            session.SummaryChanged += OnSessionSummaryChanged;

            lock (_gate)
            {
                AgentSession existing;
                if (_sessions.TryGetValue(options.SessionId, out existing))
                {
                    session.SummaryChanged -= OnSessionSummaryChanged;
                    session.Dispose();
                    return existing;
                }

                _sessions[options.SessionId] = session;
            }

            TrimHotSessions(options.SessionId);
            return session;
        }

        public IAgentSession Find(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            lock (_gate)
            {
                AgentSession session;
                return _sessions.TryGetValue(sessionId, out session) ? session : null;
            }
        }

        public async Task CloseAsync(string sessionId, CancellationToken cancellationToken)
        {
            AgentSession session;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out session))
                {
                    return;
                }

                _sessions.Remove(sessionId);
            }

            session.SummaryChanged -= OnSessionSummaryChanged;
            try
            {
                await session.CancelAsync(true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            session.Dispose();
        }

        public async Task ShutdownAsync(TimeSpan timeout)
        {
            if (_disposed)
            {
                return;
            }

            _shutdown = true;

            List<AgentSession> sessions;
            lock (_gate)
            {
                sessions = _sessions.Values.ToList();
                _sessions.Clear();
            }

            Task cancelTask = Task.WhenAll(sessions.Select(async session =>
            {
                session.SummaryChanged -= OnSessionSummaryChanged;
                try
                {
                    await session.CancelAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }));

            Task timeoutTask = Task.Delay(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(3) : timeout);
            await Task.WhenAny(cancelTask, timeoutTask).ConfigureAwait(false);

            foreach (AgentSession session in sessions)
            {
                session.Dispose();
            }
        }

        public IReadOnlyList<AgentSessionSnapshot> GetActiveSnapshots()
        {
            List<AgentSession> sessions;
            lock (_gate)
            {
                sessions = _sessions.Values.ToList();
            }

            var snapshots = new List<AgentSessionSnapshot>(sessions.Count);
            foreach (AgentSession session in sessions)
            {
                snapshots.Add(session.Snapshot());
            }

            return snapshots;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                ShutdownAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }

            _upstreamGate.Dispose();
        }

        private void TrimHotSessions(string keepSessionId)
        {
            int max = Math.Max(1, _options.MaxHotSessions);
            List<AgentSession> evicted = null;

            lock (_gate)
            {
                if (_sessions.Count <= max)
                {
                    return;
                }

                var candidates = _sessions
                    .Where(pair => !string.Equals(pair.Key, keepSessionId, StringComparison.Ordinal))
                    .Where(pair => pair.Value.State == AgentSessionState.Idle)
                    .Where(pair => pair.Value.SubscriberCount == 0)
                    .OrderBy(pair => pair.Value.LastActivity)
                    .ToList();

                foreach (KeyValuePair<string, AgentSession> pair in candidates)
                {
                    if (_sessions.Count <= max)
                    {
                        break;
                    }

                    _sessions.Remove(pair.Key);
                    if (evicted == null)
                    {
                        evicted = new List<AgentSession>();
                    }

                    evicted.Add(pair.Value);
                }
            }

            if (evicted == null)
            {
                return;
            }

            foreach (AgentSession session in evicted)
            {
                session.SummaryChanged -= OnSessionSummaryChanged;
                session.Dispose();
            }
        }

        private void OnSessionSummaryChanged(object sender, AgentSessionSummaryEventArgs args)
        {
            EventHandler<AgentSessionSummaryEventArgs> handler = SessionChanged;
            if (handler != null && args != null)
            {
                handler(this, args);
            }
        }
    }
}
