using System;
using System.Collections.Generic;
using System.Threading;

namespace MiaoWu.Core.Markdown
{
    /// <summary>
    /// Markdown AST 缓存：按 <c>path + mtime + length</c> 命中，容量满时淘汰最久未用项。
    /// 切换标签不重复解析；文件外部修改后自动失效。
    /// </summary>
    public sealed class MarkdownAstCache
    {
        private sealed class Entry
        {
            public string Path;
            public DateTime LastWriteUtc;
            public long Length;
            public MarkdownDocument Document;
            public long UseStamp;
        }

        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private readonly int _capacity;
        private readonly object _gate = new object();
        private long _stamp;

        public MarkdownAstCache(int capacity = 32)
        {
            _capacity = capacity < 1 ? 1 : capacity;
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        public MarkdownDocument GetOrParse(
            string filePath,
            DateTime lastWriteUtc,
            long length,
            string text)
        {
            string key = filePath ?? string.Empty;

            lock (_gate)
            {
                Entry entry;
                if (_entries.TryGetValue(key, out entry)
                    && entry.LastWriteUtc == lastWriteUtc
                    && entry.Length == length)
                {
                    entry.UseStamp = ++_stamp;
                    return entry.Document;
                }
            }

            MarkdownDocument parsed = MarkdownParser.Parse(text);

            lock (_gate)
            {
                _entries[key] = new Entry
                {
                    Path = key,
                    LastWriteUtc = lastWriteUtc,
                    Length = length,
                    Document = parsed,
                    UseStamp = ++_stamp
                };

                Trim();
            }

            return parsed;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        public void Invalidate(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            lock (_gate)
            {
                _entries.Remove(filePath);
            }
        }

        private void Trim()
        {
            while (_entries.Count > _capacity)
            {
                string oldestKey = null;
                long oldestStamp = long.MaxValue;

                foreach (KeyValuePair<string, Entry> pair in _entries)
                {
                    if (pair.Value.UseStamp < oldestStamp)
                    {
                        oldestStamp = pair.Value.UseStamp;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey == null)
                {
                    return;
                }

                _entries.Remove(oldestKey);
            }
        }
    }
}
