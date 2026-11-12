using System;
using System.Collections.Generic;
using System.Linq;

namespace MiaoWu.Core.Workspaces
{
    /// <summary>文件树节点的加载状态。错误显示为行内节点，不弹窗。</summary>
    public enum FileNodeState
    {
        NotLoaded,
        Loading,
        Loaded,
        Error
    }

    /// <summary>
    /// 文件树节点。目录的子项在展开时才枚举，因此这里允许可变集合。
    /// </summary>
    public sealed class FileNode
    {
        public FileNode(string name, string fullPath, bool isDirectory, int depth)
        {
            Name = name ?? string.Empty;
            FullPath = fullPath ?? string.Empty;
            IsDirectory = isDirectory;
            Depth = depth < 0 ? 0 : depth;
            Children = new List<FileNode>();
        }

        public string Name { get; }

        public string FullPath { get; }

        public bool IsDirectory { get; }

        public int Depth { get; }

        public FileNodeState State { get; set; }

        public string ErrorMessage { get; set; }

        /// <summary>junction / symlink：显示但不自动递归。</summary>
        public bool IsReparsePoint { get; set; }

        public bool IsExpanded { get; set; }

        /// <summary>已在中栏打开，文件图标显示为绿色。</summary>
        public bool IsOpened { get; set; }

        public List<FileNode> Children { get; }

        public bool HasLoadedChildren
        {
            get { return State == FileNodeState.Loaded; }
        }

        public bool CanExpand
        {
            get { return IsDirectory && !IsReparsePoint; }
        }

        public bool IsErrorRow
        {
            get { return State == FileNodeState.Error; }
        }

        public void ResetChildren()
        {
            Children.Clear();
            State = FileNodeState.NotLoaded;
            ErrorMessage = null;
        }

        public void SetError(string message)
        {
            Children.Clear();
            State = FileNodeState.Error;
            ErrorMessage = message;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>文件预览类型，决定中栏使用哪种渲染器。</summary>
    public enum FilePreviewKind
    {
        Markdown,
        Text,
        Code,
        Image,
        Binary
    }

    /// <summary>
    /// 文件树的排序、可见行展开与缩进规则。数值来自 docs/02-ui-design.md 第 4、6 节：
    /// 25px 行、13px/层缩进、超过 8 层横向滚动。
    /// </summary>
    public static class FileTreePolicy
    {
        public const double RowHeight = 25;

        public const double RowPadding = 6;

        public const double IndentPerLevel = 13;

        public const double IconColumn = 22;

        public const double MinimumNameWidth = 120;

        public const double TrailingPadding = 10;

        private static readonly HashSet<string> MarkdownExtensions = new HashSet<string>(
            new[] { ".md", ".markdown", ".mdown" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(
            new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> BinaryExtensions = new HashSet<string>(
            new[]
            {
                ".exe", ".dll", ".pdb", ".zip", ".7z", ".rar", ".gz", ".tar",
                ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
                ".class", ".jar", ".ico", ".db", ".sqlite", ".nupkg", ".snk",
                ".obj", ".lib", ".bin", ".dat", ".cache"
            },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>目录优先，其次按名称（忽略大小写）。</summary>
        public static int Compare(FileNode left, FileNode right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            if (left.IsDirectory != right.IsDirectory)
            {
                return left.IsDirectory ? -1 : 1;
            }

            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }

        public static void SortInPlace(IList<FileNode> nodes)
        {
            if (nodes == null || nodes.Count < 2)
            {
                return;
            }

            var ordered = new List<FileNode>(nodes);
            ordered.Sort(Compare);
            nodes.Clear();
            foreach (FileNode node in ordered)
            {
                nodes.Add(node);
            }
        }

        /// <summary>按展开状态生成可见行（先序）。虚拟化列表直接绑定这个平铺序列。</summary>
        public static IReadOnlyList<FileNode> Flatten(IEnumerable<FileNode> roots)
        {
            var rows = new List<FileNode>();
            if (roots == null)
            {
                return rows;
            }

            var stack = new Stack<FileNode>();
            foreach (FileNode root in roots.Reverse())
            {
                stack.Push(root);
            }

            while (stack.Count > 0)
            {
                FileNode node = stack.Pop();
                rows.Add(node);

                if (!node.IsExpanded || node.State != FileNodeState.Loaded || node.Children.Count == 0)
                {
                    continue;
                }

                for (int index = node.Children.Count - 1; index >= 0; index--)
                {
                    stack.Push(node.Children[index]);
                }
            }

            return rows;
        }

        public static double GetIndent(int depth)
        {
            return RowPadding + (depth < 0 ? 0 : depth) * IndentPerLevel;
        }

        /// <summary>超过 8 层的缩进需要横向滚动；这里给出该深度下行的最小内容宽度。</summary>
        public static double GetContentWidth(int maxDepth)
        {
            return GetIndent(maxDepth) + IconColumn + MinimumNameWidth + TrailingPadding;
        }

        public static int GetMaxDepth(IEnumerable<FileNode> rows)
        {
            int max = 0;
            if (rows == null)
            {
                return max;
            }

            foreach (FileNode node in rows)
            {
                if (node != null && node.Depth > max)
                {
                    max = node.Depth;
                }
            }

            return max;
        }

        public static FilePreviewKind GetPreviewKind(string path)
        {
            string extension = string.Empty;
            try
            {
                extension = System.IO.Path.GetExtension(path ?? string.Empty);
            }
            catch (ArgumentException)
            {
                extension = string.Empty;
            }

            if (MarkdownExtensions.Contains(extension))
            {
                return FilePreviewKind.Markdown;
            }

            if (ImageExtensions.Contains(extension))
            {
                return FilePreviewKind.Image;
            }

            if (BinaryExtensions.Contains(extension))
            {
                return FilePreviewKind.Binary;
            }

            return FilePreviewKind.Text;
        }

        /// <summary>代码类文件用等宽字体显示正文。</summary>
        public static bool IsCodeFile(string path)
        {
            string extension = string.Empty;
            try
            {
                extension = System.IO.Path.GetExtension(path ?? string.Empty);
            }
            catch (ArgumentException)
            {
                extension = string.Empty;
            }

            switch (extension.ToLowerInvariant())
            {
                case ".cs":
                case ".xaml":
                case ".json":
                case ".xml":
                case ".yaml":
                case ".yml":
                case ".ps1":
                case ".bat":
                case ".cmd":
                case ".sh":
                case ".py":
                case ".js":
                case ".ts":
                case ".html":
                case ".css":
                case ".sql":
                case ".csproj":
                case ".props":
                case ".targets":
                case ".config":
                case ".ini":
                case ".resx":
                case ".sln":
                    return true;
                default:
                    return false;
            }
        }
    }
}
