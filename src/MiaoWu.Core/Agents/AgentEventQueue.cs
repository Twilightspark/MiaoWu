using System;
using System.Collections.Generic;

namespace MiaoWu.Core.Agents
{
    /// <summary>
    /// 订阅者有界事件队列：满了就丢最旧的事件并标记溢出，
    /// 由订阅者用 Snapshot() 重同步。不要阻塞发布线程。
    /// </summary>
    public sealed class AgentEventQueue
    {
        private readonly Queue<AgentEvent> _items;
        private readonly int _capacity;
        private readonly object _gate = new object();
        private bool _overflow;

        public AgentEventQueue(int capacity)
        {
            _capacity = capacity < 16 ? 16 : capacity;
            _items = new Queue<AgentEvent>(_capacity);
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _items.Count;
                }
            }
        }

        public bool TryEnqueue(AgentEvent evt)
        {
            if (evt == null)
            {
                return false;
            }

            lock (_gate)
            {
                if (_items.Count >= _capacity)
                {
                    _items.Dequeue();
                    _overflow = true;
                }

                _items.Enqueue(evt);
                return !_overflow;
            }
        }

        public bool TryDequeue(out AgentEvent evt)
        {
            lock (_gate)
            {
                if (_items.Count == 0)
                {
                    evt = null;
                    return false;
                }

                evt = _items.Dequeue();
                return true;
            }
        }

        public bool TakeOverflow()
        {
            lock (_gate)
            {
                bool value = _overflow;
                _overflow = false;
                return value;
            }
        }

        public void MarkOverflow()
        {
            lock (_gate)
            {
                _overflow = true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _items.Clear();
                _overflow = false;
            }
        }
    }
}
