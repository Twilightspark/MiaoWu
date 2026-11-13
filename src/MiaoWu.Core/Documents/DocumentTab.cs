using System;
using System.Collections.Generic;
using System.Linq;

namespace MiaoWu.Core.Documents
{
    /// <summary>中栏标签页类型。P4 直接增加“工具”标签，不改宿主结构。</summary>
    public enum DocumentTabKind
    {
        File,
        Diff,
        CommandOutput,
        ToolTimeline
    }

    /// <summary>
    /// 一个中栏标签。P2 只持久化打开集合与滚动位置，内容由对应的文档视图重建。
    /// </summary>
    public sealed class DocumentTab
    {
        public string Id { get; set; }

        public DocumentTabKind Kind { get; set; }

        public string Title { get; set; }

        /// <summary>文件标签的完整路径；Diff / 命令输出等静态标签为 null。</summary>
        public string FilePath { get; set; }

        public double ScrollOffset { get; set; }

        public bool IsFileTab
        {
            get { return Kind == DocumentTabKind.File && !string.IsNullOrEmpty(FilePath); }
        }

        public DocumentTab Clone()
        {
            return new DocumentTab
            {
                Id = Id,
                Kind = Kind,
                Title = Title,
                FilePath = FilePath,
                ScrollOffset = ScrollOffset
            };
        }

        public static DocumentTab CreateFile(string filePath, string title)
        {
            return new DocumentTab
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = DocumentTabKind.File,
                Title = title,
                FilePath = filePath
            };
        }

        public static DocumentTab CreateStatic(DocumentTabKind kind, string title, string id = null)
        {
            return new DocumentTab
            {
                Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id,
                Kind = kind,
                Title = title
            };
        }

        public override string ToString()
        {
            return Title ?? string.Empty;
        }
    }

    /// <summary>
    /// 标签行规则：固定 168 宽、6 间距、行尾 ＋；关闭后焦点落到相邻标签。
    /// 数值来自 docs/03-ui-design.md 第 4、6、8.5 节。
    /// </summary>
    public static class TabStripPolicy
    {
        public const double TabWidth = 168;

        public const double TabHeight = 28;

        public const double TabGap = 6;

        public const double PlusButtonWidth = 28;

        public const int MaxTabs = 24;

        /// <summary>关闭第 index 个标签后应选中的下标；没有标签时返回 -1。</summary>
        public static int GetNextSelectionAfterClose(int closedIndex, int remainingCount)
        {
            if (remainingCount <= 0)
            {
                return -1;
            }

            if (closedIndex < 0)
            {
                return 0;
            }

            return Math.Min(closedIndex, remainingCount - 1);
        }

        public static int NormalizeSelection(int index, int count)
        {
            if (count <= 0)
            {
                return -1;
            }

            if (index < 0)
            {
                return 0;
            }

            if (index >= count)
            {
                return count - 1;
            }

            return index;
        }

        public static DocumentTab FindByPath(IEnumerable<DocumentTab> tabs, string path)
        {
            if (tabs == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return tabs.FirstOrDefault(t => t.IsFileTab
                && string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        }

        public static bool HasOverflow(int tabCount, double viewportWidth)
        {
            if (tabCount <= 0 || viewportWidth <= 0)
            {
                return false;
            }

            double available = viewportWidth - PlusButtonWidth;
            return GetContentWidth(tabCount) > available;
        }

        public static double GetContentWidth(int tabCount)
        {
            if (tabCount <= 0)
            {
                return 0;
            }

            return tabCount * TabWidth + (tabCount - 1) * TabGap;
        }
    }
}
