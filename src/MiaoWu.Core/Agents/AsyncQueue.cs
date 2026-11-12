using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MiaoWu.Core.Agents
{
    /// <summary>
    /// 极简异步队列。net48 的 BCL 没有 System.Threading.Channels，
    /// 这里用一个信号量 + 队列实现“有界/无界”两用队列。
    /// </summary>
    public sealed class AsyncQueue<T>
    {
        private readonly Queue<T> _items = new Queue<T>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly object _gate = new object();
        private bool _completed;

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

        public bool IsCompleted
        {
            get
            {
                lock (_gate)
                {
                    return _completed;
                }
            }
        }

        public void Enqueue(T item)
        {
            lock (_gate)
            {
                if (_completed)
                {
                    throw new InvalidOperationException("队列已关闭。");
                }

                _items.Enqueue(item);
            }

            _signal.Release();
        }

        /// <summary>返回 default(T) 表示队列已关闭且为空。</summary>
        public async Task<T> DequeueAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    if (_items.Count > 0)
                    {
                        return _items.Dequeue();
                    }

                    if (_completed)
                    {
                        return default(T);
                    }
                }
            }
        }

        public bool TryDequeue(out T item)
        {
            item = default(T);
            lock (_gate)
            {
                if (_items.Count == 0)
                {
                    return false;
                }

                item = _items.Dequeue();
            }

            _signal.Wait(0);
            return true;
        }

        public List<T> Drain()
        {
            var drained = new List<T>();
            lock (_gate)
            {
                while (_items.Count > 0)
                {
                    drained.Add(_items.Dequeue());
                }

                _completed = true;
            }

            // 让所有等待者醒来并看到完成标记。
            int count = drained.Count;
            if (count > 0)
            {
                _signal.Release(count);
            }

            _signal.Release();
            return drained;
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
            }

            _signal.Release();
        }
    }
}
