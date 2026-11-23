using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Schedules;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 进程内定时器：每 15 秒检查一次到点任务，交给宿主的投递委托。
    /// 先把下一次触发算好并落盘再投递，同一时刻不会重复入队；应用关闭期间不投递。
    /// </summary>
    public sealed class ScheduleService : IScheduleService, IDisposable
    {
        /// <summary>每个任务保留的投递记录条数；超出时丢弃最旧的。</summary>
        public const int HistoryLimit = 50;

        private readonly object _gate = new object();
        private readonly ScheduleFileStore _store;
        private readonly IClock _clock;
        private readonly Func<ScheduleDefinition, Task> _deliver;
        private readonly Action<string> _log;
        private readonly Dictionary<string, ScheduleDefinition> _schedules =
            new Dictionary<string, ScheduleDefinition>(StringComparer.Ordinal);

        private Timer _timer;
        private bool _disposed;

        public ScheduleService(
            ScheduleFileStore store,
            IClock clock,
            Func<ScheduleDefinition, Task> deliver,
            Action<string> log = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? SystemClock.Instance;
            _deliver = deliver;
            _log = log;

            IReadOnlyList<ScheduleDefinition> loaded = _store.LoadAll();
            for (int i = 0; i < loaded.Count; i++)
            {
                if (loaded[i] != null)
                {
                    _schedules[loaded[i].Id] = loaded[i];
                }
            }
        }

        public event EventHandler Changed;

        /// <summary>当前本地时间；日程按本地时间触发。</summary>
        private DateTimeOffset Now
        {
            get { return _clock.UtcNow.ToLocalTime(); }
        }

        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                if (_timer != null)
                {
                    return;
                }

                _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }
        }

        public ScheduleDefinition Create(
            string title,
            string prompt,
            string rule,
            ScheduleTarget target,
            string sessionId,
            string workspaceId)
        {
            ScheduleRecurrence recurrence = ScheduleRecurrence.Parse(rule);
            DateTimeOffset? nextRun = recurrence.FirstOnOrAfter(Now);
            if (!nextRun.HasValue)
            {
                throw new ScheduleException("not_future", "规则没有未来的运行时间。");
            }

            var definition = new ScheduleDefinition
            {
                Id = "sch_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                Title = (title ?? string.Empty).Trim(),
                Prompt = (prompt ?? string.Empty).Trim(),
                Rule = (rule ?? string.Empty).Trim(),
                Target = target,
                SessionId = sessionId,
                WorkspaceId = workspaceId,
                Active = true,
                CreatedAt = Now,
                UpdatedAt = Now,
                RunCount = 0,
                NextRunAt = nextRun,
                History = new List<ScheduleDeliveryRecord>()
            };

            lock (_gate)
            {
                _schedules[definition.Id] = definition;
                SaveLocked();
            }

            RaiseChanged();
            return Clone(definition);
        }

        public ScheduleUpdateResult Update(
            string id,
            string title,
            string prompt,
            string rule,
            bool? active,
            DateTimeOffset? expectedUpdatedAt = null,
            ScheduleTarget? target = null,
            string sessionId = null,
            string workspaceId = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return new ScheduleUpdateResult(ScheduleUpdateStatus.NotFound, null);
            }

            ScheduleRecurrence recurrence = null;
            string trimmedRule = null;
            if (rule != null)
            {
                trimmedRule = rule.Trim();
                recurrence = ScheduleRecurrence.Parse(trimmedRule);
            }

            ScheduleDefinition snapshot;
            lock (_gate)
            {
                ScheduleDefinition definition;
                if (!_schedules.TryGetValue(id, out definition))
                {
                    return new ScheduleUpdateResult(ScheduleUpdateStatus.NotFound, null);
                }

                if (expectedUpdatedAt.HasValue && definition.UpdatedAt != expectedUpdatedAt.Value)
                {
                    return new ScheduleUpdateResult(ScheduleUpdateStatus.Conflict, null);
                }

                if (title != null)
                {
                    definition.Title = title.Trim();
                }

                if (prompt != null)
                {
                    definition.Prompt = prompt.Trim();
                }

                if (trimmedRule != null
                    && !string.Equals(definition.Rule, trimmedRule, StringComparison.Ordinal))
                {
                    definition.Rule = trimmedRule;
                    definition.NextRunAt = recurrence.FirstOnOrAfter(Now);
                }

                if (target.HasValue)
                {
                    definition.Target = target.Value;
                    if (target.Value == ScheduleTarget.New)
                    {
                        definition.SessionId = null;
                    }
                }

                if (sessionId != null)
                {
                    definition.SessionId = sessionId;
                }

                if (workspaceId != null)
                {
                    definition.WorkspaceId = workspaceId;
                }

                if (active.HasValue)
                {
                    if (active.Value && !definition.NextRunAt.HasValue)
                    {
                        DateTimeOffset? resumed = SafeNext(definition.Rule, Now, true);
                        if (!resumed.HasValue)
                        {
                            throw new ScheduleException(
                                "not_future",
                                "请选择未来的运行时间；恢复前请先修改任务时间。");
                        }

                        definition.NextRunAt = resumed;
                    }

                    definition.Active = active.Value;
                }

                definition.UpdatedAt = NextStamp(definition.UpdatedAt);
                SaveLocked();
                snapshot = Clone(definition);
            }

            RaiseChanged();
            return new ScheduleUpdateResult(ScheduleUpdateStatus.Updated, snapshot);
        }

        public bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (_gate)
            {
                if (!_schedules.Remove(id))
                {
                    return false;
                }

                SaveLocked();
            }

            RaiseChanged();
            return true;
        }

        public bool RunNow(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || _disposed)
            {
                return false;
            }

            ScheduleDefinition snapshot;
            lock (_gate)
            {
                ScheduleDefinition definition;
                if (!_schedules.TryGetValue(id, out definition))
                {
                    return false;
                }

                DateTimeOffset now = Now;
                definition.LastRunAt = now;
                definition.RunCount++;
                definition.UpdatedAt = NextStamp(definition.UpdatedAt);
                AppendHistory(definition, now, ScheduleDeliveryKind.Manual);
                SaveLocked();
                snapshot = Clone(definition);
            }

            RaiseChanged();
            Dispatch(snapshot);
            return true;
        }

        public bool UpdateSession(string id, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            lock (_gate)
            {
                ScheduleDefinition definition;
                if (!_schedules.TryGetValue(id, out definition))
                {
                    return false;
                }

                definition.SessionId = sessionId;
                definition.UpdatedAt = NextStamp(definition.UpdatedAt);
                SaveLocked();
            }

            RaiseChanged();
            return true;
        }

        public IReadOnlyList<ScheduleDefinition> List(bool includeInactive)
        {
            var list = new List<ScheduleDefinition>();
            lock (_gate)
            {
                foreach (ScheduleDefinition definition in _schedules.Values)
                {
                    if (!includeInactive && !definition.Active)
                    {
                        continue;
                    }

                    list.Add(Clone(definition));
                }
            }

            list.Sort((a, b) =>
            {
                DateTimeOffset left = a.NextRunAt ?? DateTimeOffset.MaxValue;
                DateTimeOffset right = b.NextRunAt ?? DateTimeOffset.MaxValue;
                return left.CompareTo(right);
            });
            return list;
        }

        public ScheduleDefinition Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            lock (_gate)
            {
                ScheduleDefinition definition;
                return _schedules.TryGetValue(id, out definition) ? Clone(definition) : null;
            }
        }

        /// <summary>按当前时钟检查一次；测试直接用它，不依赖真实时间流逝。</summary>
        public void Tick()
        {
            Tick(Now);
        }

        public void Tick(DateTimeOffset now)
        {
            if (_disposed || _deliver == null)
            {
                return;
            }

            var due = new List<ScheduleDefinition>();
            lock (_gate)
            {
                foreach (ScheduleDefinition definition in _schedules.Values)
                {
                    if (definition.Active
                        && definition.NextRunAt.HasValue
                        && definition.NextRunAt.Value <= now)
                    {
                        due.Add(Clone(definition));
                    }
                }
            }

            for (int i = 0; i < due.Count; i++)
            {
                Deliver(due[i], now);
            }
        }

        private void Deliver(ScheduleDefinition candidate, DateTimeOffset now)
        {
            ScheduleDefinition snapshot;
            lock (_gate)
            {
                ScheduleDefinition current;
                if (!_schedules.TryGetValue(candidate.Id, out current)
                    || !current.Active
                    || !current.NextRunAt.HasValue
                    || current.NextRunAt.Value > now)
                {
                    return;
                }

                current.RunCount++;
                current.LastRunAt = now;
                current.UpdatedAt = NextStamp(current.UpdatedAt);
                current.NextRunAt = SafeNext(current.Rule, now, false);
                if (!current.NextRunAt.HasValue)
                {
                    current.Active = false;
                }

                AppendHistory(current, now, ScheduleDeliveryKind.Automatic);
                snapshot = Clone(current);
                SaveLocked();
            }

            RaiseChanged();
            Dispatch(snapshot);
        }

        private void Dispatch(ScheduleDefinition snapshot)
        {
            if (_deliver == null)
            {
                return;
            }

            try
            {
                Task task = _deliver(snapshot);
                if (task != null)
                {
                    task.ContinueWith(
                        t => LogFailure(snapshot, t),
                        TaskScheduler.Default);
                }
            }
            catch (Exception exception)
            {
                MarkFailed(snapshot);
                if (_log != null)
                {
                    _log("日程投递失败（" + snapshot.Id + "）：" + exception.Message);
                }
            }
        }

        private void LogFailure(ScheduleDefinition snapshot, Task task)
        {
            if (!task.IsFaulted)
            {
                return;
            }

            MarkFailed(snapshot);
            if (_log != null)
            {
                Exception error = task.Exception == null ? null : task.Exception.GetBaseException();
                _log("日程投递失败（" + snapshot.Id + "）：" + (error == null ? "未知错误" : error.Message));
            }
        }

        private void MarkFailed(ScheduleDefinition snapshot)
        {
            if (snapshot == null || snapshot.History == null || snapshot.History.Count == 0)
            {
                return;
            }

            ScheduleDeliveryRecord attempt = snapshot.History[snapshot.History.Count - 1];
            lock (_gate)
            {
                ScheduleDefinition current;
                if (!_schedules.TryGetValue(snapshot.Id, out current) || current.History == null)
                {
                    return;
                }

                for (int i = current.History.Count - 1; i >= 0; i--)
                {
                    ScheduleDeliveryRecord record = current.History[i];
                    if (record != null
                        && record.DeliveredAt == attempt.DeliveredAt
                        && record.Kind == attempt.Kind)
                    {
                        record.Failed = true;
                        SaveLocked();
                        return;
                    }
                }
            }
        }

        private static void AppendHistory(
            ScheduleDefinition definition,
            DateTimeOffset at,
            ScheduleDeliveryKind kind)
        {
            if (definition.History == null)
            {
                definition.History = new List<ScheduleDeliveryRecord>();
            }

            definition.History.Add(new ScheduleDeliveryRecord
            {
                DeliveredAt = at,
                Prompt = definition.Prompt ?? string.Empty,
                Kind = kind,
                Failed = false
            });

            while (definition.History.Count > HistoryLimit)
            {
                definition.History.RemoveAt(0);
            }
        }

        private DateTimeOffset NextStamp(DateTimeOffset previous)
        {
            DateTimeOffset now = Now;
            return now <= previous ? previous.AddTicks(1) : now;
        }

        private static DateTimeOffset? SafeNext(string rule, DateTimeOffset now, bool inclusive)
        {
            try
            {
                ScheduleRecurrence recurrence = ScheduleRecurrence.Parse(rule);
                return inclusive ? recurrence.FirstOnOrAfter(now) : recurrence.NextAfter(now);
            }
            catch (ScheduleException)
            {
                return null;
            }
        }

        private void SaveLocked()
        {
            try
            {
                _store.SaveAll(new List<ScheduleDefinition>(_schedules.Values));
            }
            catch (Exception exception)
            {
                if (_log != null)
                {
                    _log("日程保存失败：" + exception.Message);
                }
            }
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
        }

        private static ScheduleDefinition Clone(ScheduleDefinition definition)
        {
            var clone = new ScheduleDefinition
            {
                Id = definition.Id,
                Title = definition.Title,
                Prompt = definition.Prompt,
                Rule = definition.Rule,
                Target = definition.Target,
                SessionId = definition.SessionId,
                WorkspaceId = definition.WorkspaceId,
                Active = definition.Active,
                CreatedAt = definition.CreatedAt,
                UpdatedAt = definition.UpdatedAt,
                LastRunAt = definition.LastRunAt,
                NextRunAt = definition.NextRunAt,
                RunCount = definition.RunCount,
                History = new List<ScheduleDeliveryRecord>()
            };

            if (definition.History != null)
            {
                for (int i = 0; i < definition.History.Count; i++)
                {
                    ScheduleDeliveryRecord record = definition.History[i];
                    if (record == null)
                    {
                        continue;
                    }

                    clone.History.Add(new ScheduleDeliveryRecord
                    {
                        DeliveredAt = record.DeliveredAt,
                        Prompt = record.Prompt,
                        Kind = record.Kind,
                        Failed = record.Failed
                    });
                }
            }

            return clone;
        }
    }
}
