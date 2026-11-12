using System;
using System.Collections.Generic;

namespace MiaoWu.Core.Text
{
    /// <summary>一次命中：起始下标与长度。</summary>
    public sealed class TextMatch
    {
        public TextMatch(int index, int length)
        {
            Index = index;
            Length = length;
        }

        /// <summary>在原文本中的起始下标。</summary>
        public int Index { get; }

        /// <summary>命中长度，等于查询串长度。</summary>
        public int Length { get; }
    }

    /// <summary>
    /// 查找策略：不分词、不重叠、大小写可控，命中数量有上限，
    /// 避免在 2MB 预览文本上生成几十万个结果拖垮界面。
    /// </summary>
    public static class TextSearchPolicy
    {
        /// <summary>单次查找最多返回的命中数。</summary>
        public const int MaxMatches = 500;

        /// <summary>
        /// 查找所有非重叠命中。text 为空或 query 为空/纯空白时返回空列表；
        /// 比较使用 Ordinal / OrdinalIgnoreCase，与区域设置无关。
        /// </summary>
        public static IReadOnlyList<TextMatch> FindAll(string text, string query, bool matchCase = false)
        {
            List<TextMatch> matches = new List<TextMatch>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
            {
                return matches;
            }

            StringComparison comparison = matchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            int position = 0;
            while (position <= text.Length && matches.Count < MaxMatches)
            {
                int index = text.IndexOf(query, position, comparison);
                if (index < 0)
                {
                    break;
                }

                matches.Add(new TextMatch(index, query.Length));
                // 非重叠：前进一个查询长度（空查询已被上面挡掉，这里保底 1 防死循环）。
                position = index + Math.Max(1, query.Length);
            }

            return matches;
        }

        /// <summary>
        /// 在当前命中下标上前后移动并循环。count 为 0 时返回 -1；
        /// current 越界时向前从 0 开始、向后从末尾开始。
        /// </summary>
        public static int NextIndex(int current, int count, bool forward)
        {
            if (count <= 0)
            {
                return -1;
            }

            if (forward)
            {
                if (current < 0)
                {
                    return 0;
                }

                int next = current + 1;
                return next >= count ? 0 : next;
            }

            if (current < 0 || current >= count)
            {
                return count - 1;
            }

            int previous = current - 1;
            return previous < 0 ? count - 1 : previous;
        }
    }
}
