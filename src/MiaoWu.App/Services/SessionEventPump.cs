using System;
using System.Collections.Generic;
using System.Windows.Threading;
using MiaoWu.Core.Agents;

namespace MiaoWu.App.Services
{
    /// <summary>
    /// 会话事件泵：发布线程只入队，UI 线程每 40ms 批量消费并合并 delta。
    /// 订阅顺序是“先订阅、再快照”，快照 revision 之前的事件会被丢弃。
    /// </summary>
    public sealed class SessionEventPump : IAgentEventSink, IDisposable
    {
        private readonly IAgentSession _session;
        private readonly Dispatcher _dispatcher;
        private readonly AgentEventQueue _queue = new AgentEventQueue(2048);
        private readonly DispatcherTimer _timer;
        private readonly Action<AgentEvent> _apply;
        private readonly Action<AgentSessionSnapshot> _resync;
        private readonly List<AgentEvent> _buffered = new List<AgentEvent>();
        private readonly object _gate = new object();

        private IDisposable _subscription;
        private bool _paused;
        private bool _started;
        private long _minimumSequence;

        public SessionEventPump(
            IAgentSession session,
            Dispatcher dispatcher,
            Action<AgentEvent> apply,
            Action<AgentSessionSnapshot> resync)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _resync = resync ?? throw new ArgumentNullException(nameof(resync));

            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _timer.Tick += OnTick;
        }

        public IAgentSession Session
        {
            get { return _session; }
        }

        public void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _paused = true;
            _subscription = _session.Subscribe(this);

            AgentSessionSnapshot snapshot = _session.Snapshot();
            _minimumSequence = snapshot.Revision;
            _resync(snapshot);
            _paused = false;

            AgentEvent evt;
            while (_queue.TryDequeue(out evt))
            {
                Apply(evt);
            }

            List<AgentEvent> buffered;
            lock (_gate)
            {
                buffered = new List<AgentEvent>(_buffered);
                _buffered.Clear();
            }

            foreach (AgentEvent item in buffered)
            {
                Apply(item);
            }

            _timer.Start();
        }

        public void OnEvent(AgentEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            lock (_gate)
            {
                if (_paused)
                {
                    _buffered.Add(evt);
                    return;
                }
            }

            _queue.TryEnqueue(evt);
        }

        public void OnOverflow()
        {
            _queue.MarkOverflow();
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            if (_subscription != null)
            {
                _subscription.Dispose();
                _subscription = null;
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            AgentEvent evt;
            int processed = 0;
            while (processed < 400 && _queue.TryDequeue(out evt))
            {
                Apply(evt);
                processed++;
            }

            if (_queue.TakeOverflow())
            {
                _resync(_session.Snapshot());
            }
        }

        private void Apply(AgentEvent evt)
        {
            if (evt == null || evt.Sequence <= _minimumSequence)
            {
                return;
            }

            try
            {
                _apply(evt);
            }
            catch (Exception)
            {
                // UI 更新失败不应打断事件泵。
            }
        }
    }
}
