using System;

namespace MiaoWu.Core.Input
{
    /// <summary>输入框里的两个触发字符。`/` 打开命令，`@` 引用文件。</summary>
    public enum TriggerChar
    {
        Slash,
        At
    }

    /// <summary>
    /// 触发哨兵。对齐 DSH 的三档：普通草稿全开；命令已认领后 `/` 不再触发；
    /// 提交 / 冻结期间两个触发都关闭。
    /// </summary>
    public enum TriggerGuardTier
    {
        Plain,
        Claimed,
        Frozen
    }

    /// <summary>token 是否位于整段草稿的开头（只有 leading 才显示带参数的命令）。</summary>
    public enum TriggerTokenPosition
    {
        Leading,
        Inline
    }

    /// <summary>
    /// 一次成功的触发检测结果。坐标以纯文本草稿为准：<see cref="Start"/> 是触发符，
    /// <see cref="End"/> 是光标；<see cref="Query"/> 是触发符与光标之间的文本。
    /// </summary>
    public sealed class TriggerHit
    {
        public TriggerChar Trigger { get; set; }

        public string Query { get; set; }

        /// <summary>只有 `@"…` 这种尚未闭合的引号 token 才为 true。</summary>
        public bool Quoted { get; set; }

        public TriggerTokenPosition Position { get; set; }

        /// <summary>触发符下标。</summary>
        public int Start { get; set; }

        /// <summary>光标下标（不含）。</summary>
        public int End { get; set; }

        /// <summary>草稿修订号；检测层恒为 0，由调用方在 track 时盖章。</summary>
        public int DraftRevision { get; set; }

        public bool IsLeading
        {
            get { return Position == TriggerTokenPosition.Leading; }
        }

        /// <summary>草稿里的原始 token，例如 <c>@src/</c> 或 <c>/pl</c>。</summary>
        public string Token
        {
            get { return Query == null ? string.Empty : Query; }
        }

        /// <summary>同一个 token 的稳定指纹，用于“关闭有记忆”的比较。</summary>
        public string Signature
        {
            get
            {
                return ((int)Trigger).ToString()
                    + "|" + Start
                    + "|" + End
                    + "|" + (Quoted ? "1" : "0")
                    + "|" + (Query ?? string.Empty);
            }
        }

        public TriggerHit Clone()
        {
            return new TriggerHit
            {
                Trigger = Trigger,
                Query = Query,
                Quoted = Quoted,
                Position = Position,
                Start = Start,
                End = End,
                DraftRevision = DraftRevision
            };
        }
    }

    /// <summary>
    /// 触发检测：纯函数，只吃 (草稿, 光标, 哨兵) 三个输入。
    ///
    /// 规则对齐 DSH 的 <c>dsh-client-ui-input-trigger</c>：
    /// - <c>@</c> 必须位于行首或空白之后，否则 <c>user@host</c> 会误触发；
    /// - <c>@"…</c> 允许内部空白，且必须尚未闭合才算一个活跃 token；
    /// - <c>/</c> 向左扫到空白为止，前面是字母 / 数字 / 下划线时不算边界；
    /// - URL 的两条例外：<c>//</c> 的第二个斜杠、<c>https:/</c> 这种紧跟在冒号后的斜杠都不触发。
    /// </summary>
    public static class TriggerDetector
    {
        /// <summary>
        /// 检测光标处的触发 token；没有触发时返回 null。
        /// </summary>
        /// <param name="draft">纯文本草稿（chip 已折算成它的 mention 文本）。</param>
        /// <param name="caret">光标下标；越界会夹到合法范围。</param>
        /// <param name="guard">触发哨兵档位。</param>
        public static TriggerHit Detect(string draft, int caret, TriggerGuardTier guard)
        {
            if (draft == null || guard == TriggerGuardTier.Frozen)
            {
                return null;
            }

            if (caret < 0)
            {
                caret = 0;
            }
            else if (caret > draft.Length)
            {
                caret = draft.Length;
            }

            TriggerHit at = DetectAt(draft, caret);
            if (at != null)
            {
                return at;
            }

            return DetectSlash(draft, caret, guard);
        }

        private static TriggerHit DetectAt(string draft, int caret)
        {
            // 1. 引号式：找光标前最后一个引号，如果它前面紧邻 @ 且 @ 处于行首 / 空白之后，
            //    就说明这个引号是“未闭合的引用开头”，内部空白属于 token。
            int quote = draft.LastIndexOf('"', Math.Max(0, caret - 1));
            if (quote > 0 && draft[quote - 1] == '@' && IsTokenBoundary(draft, quote - 1))
            {
                int start = quote - 1;
                return BuildAtHit(draft, start, caret, true);
            }

            // 2. 普通式：从光标向左扫到空白 / 行首，要求途中遇到 @。
            int index = caret - 1;
            while (index >= 0)
            {
                char ch = draft[index];
                if (char.IsWhiteSpace(ch))
                {
                    return null;
                }

                if (ch == '@')
                {
                    if (!IsTokenBoundary(draft, index))
                    {
                        return null;
                    }

                    return BuildAtHit(draft, index, caret, false);
                }

                index--;
            }

            return null;
        }

        private static TriggerHit BuildAtHit(string draft, int start, int caret, bool quoted)
        {
            int queryStart = start + (quoted ? 2 : 1);
            string query = draft.Substring(queryStart, Math.Max(0, caret - queryStart));
            return new TriggerHit
            {
                Trigger = TriggerChar.At,
                Query = query,
                Quoted = quoted,
                Position = IsLeadingToken(draft, start)
                    ? TriggerTokenPosition.Leading
                    : TriggerTokenPosition.Inline,
                Start = start,
                End = caret
            };
        }

        private static TriggerHit DetectSlash(string draft, int caret, TriggerGuardTier guard)
        {
            for (int index = caret - 1; index >= 0; index--)
            {
                char ch = draft[index];
                if (char.IsWhiteSpace(ch))
                {
                    return null;
                }

                if (ch != '/')
                {
                    continue;
                }

                if (guard == TriggerGuardTier.Claimed)
                {
                    // 命令已认领：`/` 不再打开第二个命令菜单，继续向左找（保持与 DSH 一致）。
                    continue;
                }

                if (!IsSlashBoundary(draft, index))
                {
                    continue;
                }

                return new TriggerHit
                {
                    Trigger = TriggerChar.Slash,
                    Query = draft.Substring(index + 1, caret - index - 1),
                    Quoted = false,
                    Position = IsLeadingToken(draft, index)
                        ? TriggerTokenPosition.Leading
                        : TriggerTokenPosition.Inline,
                    Start = index,
                    End = caret
                };
            }

            return null;
        }

        /// <summary>行首、空白之后或标点之后可以开启一个 token。</summary>
        private static bool IsTokenBoundary(string draft, int index)
        {
            if (index <= 0)
            {
                return true;
            }

            char previous = draft[index - 1];
            if (char.IsWhiteSpace(previous))
            {
                return true;
            }

            return char.IsPunctuation(previous)
                || char.IsSymbol(previous);
        }

        /// <summary>
        /// `/` 的词边界：前面是字母 / 数字 / 下划线则不是命令起点；
        /// 另排除 URL 的 `//` 与 `https:/` 两条例外。
        /// </summary>
        private static bool IsSlashBoundary(string draft, int index)
        {
            if (index <= 0)
            {
                return true;
            }

            char previous = draft[index - 1];
            if (char.IsLetterOrDigit(previous) || previous == '_')
            {
                return false;
            }

            if (previous == '/')
            {
                return false;
            }

            if (previous == ':' && index >= 2 && !char.IsWhiteSpace(draft[index - 2]))
            {
                return false;
            }

            return char.IsWhiteSpace(previous)
                || char.IsPunctuation(previous)
                || char.IsSymbol(previous);
        }

        private static bool IsLeadingToken(string draft, int start)
        {
            for (int i = 0; i < start; i++)
            {
                if (!char.IsWhiteSpace(draft[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
