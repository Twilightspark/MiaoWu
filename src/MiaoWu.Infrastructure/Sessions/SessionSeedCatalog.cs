using System;
using System.Collections.Generic;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Sessions;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.Infrastructure.Sessions
{
    /// <summary>
    /// P2 的会话种子目录。工作区按首次请求顺序拿到 24 / 5 / 2 / 8 条示例会话；
    /// 压力测试可通过 <see cref="SessionSeedOptions.CountProvider"/> 指定 120 条。
    /// P3 用 JSONL 会话库替换本实现，接口不变。
    /// </summary>
    public sealed class SessionSeedCatalog : ISessionCatalog
    {
        private readonly Dictionary<string, IReadOnlyList<SessionSummary>> _cache =
            new Dictionary<string, IReadOnlyList<SessionSummary>>(StringComparer.Ordinal);

        private readonly object _gate = new object();
        private readonly int[] _defaultCounts;
        private readonly Func<string, int> _countProvider;
        private readonly DateTime _now;
        private int _sequence;

        public SessionSeedCatalog()
            : this(new SessionSeedOptions())
        {
        }

        public SessionSeedCatalog(SessionSeedOptions options)
        {
            SessionSeedOptions effective = options ?? new SessionSeedOptions();
            _defaultCounts = effective.DefaultCounts ?? new[] { 24, 5, 2, 8 };
            _countProvider = effective.CountProvider;
            _now = effective.Now ?? DateTime.Now;
        }

        public IReadOnlyList<SessionSummary> GetSessions(Workspace workspace)
        {
            return GetSessions(workspace == null ? null : workspace.Id);
        }

        public IReadOnlyList<SessionSummary> GetSessions(string workspaceId)
        {
            if (string.IsNullOrEmpty(workspaceId))
            {
                return new SessionSummary[0];
            }

            lock (_gate)
            {
                IReadOnlyList<SessionSummary> cached;
                if (_cache.TryGetValue(workspaceId, out cached))
                {
                    return cached;
                }

                int count;
                if (_countProvider != null)
                {
                    count = _countProvider(workspaceId);
                }
                else
                {
                    int index = Math.Min(_sequence, _defaultCounts.Length - 1);
                    count = _defaultCounts[index];
                    _sequence++;
                }

                if (count < 0)
                {
                    count = 0;
                }

                IReadOnlyList<SessionSummary> sessions =
                    SessionSeedFactory.Create(workspaceId, count, _now);
                _cache[workspaceId] = sessions;
                return sessions;
            }
        }
    }

    /// <summary>会话种子的数量与时间基准。</summary>
    public sealed class SessionSeedOptions
    {
        public SessionSeedOptions()
        {
            DefaultCounts = new[] { 24, 5, 2, 8 };
            Now = null;
        }

        /// <summary>按工作区首次请求顺序取数量；最后一项会一直复用。</summary>
        public int[] DefaultCounts { get; set; }

        /// <summary>非空时优先于 DefaultCounts。</summary>
        public Func<string, int> CountProvider { get; set; }

        /// <summary>固定“现在”，让种子时间与截图可复现；null 表示 DateTime.Now。</summary>
        public DateTime? Now { get; set; }
    }
}
