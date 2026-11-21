using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Input;

namespace MiaoWu.Infrastructure.Workspaces
{
    /// <summary>
    /// 工作区文件索引：为 `@` 补全提供一次性扫描 + 内存过滤。
    /// 只读、可丢弃：索引过期或工作区换掉时重建，单次扫描有文件数与深度上限，
    /// 跳过分隔符链接（重解析点）与常见的依赖 / 缓存目录。
    /// </summary>
    public sealed class WorkspaceFileIndex
    {
        /// <summary>单次扫描最多收录的条目数，防止超大仓库拖慢启动。</summary>
        public const int MaxEntries = 20000;

        /// <summary>相对工作区根的最大扫描深度。</summary>
        public const int MaxDepth = 12;

        /// <summary>索引过期时间；过期后下一次查询在后台重建。</summary>
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(45);

        private static readonly HashSet<string> ExcludedDirectories = new HashSet<string>(
            new[] { ".agent", ".artifacts", ".git", ".svn", ".vs", "node_modules", "packages", "bin", "obj", "TestResults" },
            StringComparer.OrdinalIgnoreCase);

        private readonly string _root;
        private readonly object _gate = new object();
        private readonly object _buildGate = new object();
        private List<WorkspaceFileEntry> _entries;
        private DateTimeOffset _builtAt;
        private bool _building;
        private bool _truncated;

        public WorkspaceFileIndex(string workspaceRoot)
        {
            _root = workspaceRoot;
        }

        public string RootPath
        {
            get { return _root; }
        }

        /// <summary>扫描是否因为上限被截断；界面可以据此提示“结果可能不全”。</summary>
        public bool IsTruncated
        {
            get
            {
                lock (_gate)
                {
                    return _truncated;
                }
            }
        }

        /// <summary>
        /// 同步查询。首次调用（索引为空）会同步扫描一次，保证第一个 `@` 就有候选；
        /// 之后的过期重建放到后台，避免击键被磁盘扫描拖住。
        /// </summary>
        public IReadOnlyList<WorkspaceFileEntry> Search(string query, int limit)
        {
            IReadOnlyList<WorkspaceFileEntry> snapshot;
            lock (_gate)
            {
                snapshot = _entries;
            }

            if (snapshot == null)
            {
                // 预热可能正在扫描：等它结束并复用同一份结果，避免第一次 `@` 再扫一遍。
                snapshot = BuildOnce(false);
            }
            else
            {
                bool expired = DateTimeOffset.UtcNow - _builtAt > CacheLifetime;
                if (expired)
                {
                    StartBackgroundRebuild();
                }
            }

            if (snapshot == null)
            {
                return new List<WorkspaceFileEntry>();
            }

            return MentionSearch.Rank(snapshot, query, limit);
        }

        /// <summary>强制丢弃缓存，下一次查询重建。</summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                _entries = null;
                _builtAt = DateTimeOffset.MinValue;
            }
        }

        /// <summary>同步重建；主要供预热与测试使用。</summary>
        public IReadOnlyList<WorkspaceFileEntry> Rebuild()
        {
            return BuildOnce(true);
        }

        private List<WorkspaceFileEntry> BuildOnce(bool force)
        {
            lock (_buildGate)
            {
                if (!force)
                {
                    lock (_gate)
                    {
                        if (_entries != null
                            && DateTimeOffset.UtcNow - _builtAt <= CacheLifetime)
                        {
                            _building = false;
                            return _entries;
                        }
                    }
                }

                List<WorkspaceFileEntry> entries = Scan();
                lock (_gate)
                {
                    _entries = entries;
                    _builtAt = DateTimeOffset.UtcNow;
                    _building = false;
                }

                return entries;
            }
        }

        /// <summary>后台预热；失败只留下空索引，不向 UI 抛错。</summary>
        public void Warm()
        {
            lock (_gate)
            {
                if (_entries != null && DateTimeOffset.UtcNow - _builtAt <= CacheLifetime)
                {
                    return;
                }
            }

            StartBackgroundRebuild();
        }

        /// <summary>等待后台重建收敛；测试与快照用。</summary>
        public async Task WaitForIndexAsync(CancellationToken cancellationToken)
        {
            for (int i = 0; i < 100; i++)
            {
                lock (_gate)
                {
                    if (!_building)
                    {
                        return;
                    }
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }

        private void StartBackgroundRebuild()
        {
            lock (_gate)
            {
                if (_building)
                {
                    return;
                }

                _building = true;
            }

            Task.Run(() =>
            {
                try
                {
                    BuildOnce(false);
                }
                catch (Exception)
                {
                    lock (_gate)
                    {
                        _building = false;
                    }
                }
            });
        }

        private List<WorkspaceFileEntry> Scan()
        {
            var entries = new List<WorkspaceFileEntry>();
            if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
            {
                return entries;
            }

            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(_root, 0));

            while (queue.Count > 0 && entries.Count < MaxEntries)
            {
                KeyValuePair<string, int> current = queue.Dequeue();
                IEnumerable<FileSystemInfo> children;
                try
                {
                    children = new DirectoryInfo(current.Key).EnumerateFileSystemInfos();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (FileSystemInfo child in children)
                {
                    if (entries.Count >= MaxEntries)
                    {
                        break;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = child.Attributes;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    string relative = RelativeOf(child.FullName);
                    if (relative.Length == 0)
                    {
                        continue;
                    }

                    if (isDirectory && ExcludedDirectories.Contains(child.Name))
                    {
                        continue;
                    }

                    entries.Add(new WorkspaceFileEntry(relative, isDirectory));

                    if (isDirectory && current.Value + 1 < MaxDepth)
                    {
                        queue.Enqueue(new KeyValuePair<string, int>(child.FullName, current.Value + 1));
                    }
                }
            }

            if (entries.Count >= MaxEntries)
            {
                lock (_gate)
                {
                    _truncated = true;
                }
            }

            return entries;
        }

        private string RelativeOf(string fullPath)
        {
            string root = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.Length <= root.Length + 1)
            {
                return string.Empty;
            }

            string relative = fullPath.Substring(root.Length).TrimStart(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return MentionFormatter.NormalizeRelativePath(relative);
        }
    }
}
