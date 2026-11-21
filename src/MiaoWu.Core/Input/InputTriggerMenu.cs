using System;
using System.Collections.Generic;
using System.Linq;

namespace MiaoWu.Core.Input
{
    /// <summary>候选组的取数状态。pending 期间保留旧行，避免改良查询时闪空。</summary>
    public enum InputTriggerGroupStatus
    {
        Pending,
        Ready
    }

    /// <summary>
    /// 菜单里的一行候选。来源只填数据，不碰 UI：命令行用 <see cref="Id"/> 识别，
    /// 引用行用相对路径识别。
    /// </summary>
    public sealed class InputTriggerCandidate
    {
        public InputTriggerCandidate()
        {
            Section = string.Empty;
        }

        /// <summary>组内唯一标识（命令名 / 相对路径）。</summary>
        public string Id { get; set; }

        /// <summary>主标题：命令标题或文件名。</summary>
        public string Label { get; set; }

        /// <summary>右侧说明：命令名 / 父目录 / 相对时间。</summary>
        public string Detail { get; set; }

        /// <summary>组内小节标题（`/` 菜单的「添加」「指令」）；为空表示不分区。</summary>
        public string Section { get; set; }

        /// <summary>目录行：选中即把目录本身作为引用，Tab / 箭头才下钻。</summary>
        public bool IsDirectory { get; set; }

        /// <summary>来源自定义数据；菜单不解释它。</summary>
        public object Payload { get; set; }
    }

    /// <summary>一个 source 在菜单里的分组。</summary>
    public sealed class InputTriggerGroup
    {
        public InputTriggerGroup(string source, string title, int order, bool showTitle, IEnumerable<InputTriggerCandidate> items)
        {
            Source = source;
            Title = title ?? string.Empty;
            Order = order;
            ShowTitle = showTitle;
            Status = InputTriggerGroupStatus.Pending;
            Items = items == null
                ? new List<InputTriggerCandidate>()
                : new List<InputTriggerCandidate>(items);
        }

        public string Source { get; private set; }

        public string Title { get; private set; }

        public int Order { get; private set; }

        public bool ShowTitle { get; private set; }

        public InputTriggerGroupStatus Status { get; set; }

        public List<InputTriggerCandidate> Items { get; set; }

        public InputTriggerGroup Clone()
        {
            return new InputTriggerGroup(Source, Title, Order, ShowTitle, Items)
            {
                Status = Status
            };
        }
    }

    /// <summary>
    /// 触发菜单的不可变状态。所有归约返回新对象（或原对象表示“无变化”），
    /// 这样过期代际、高亮迁移与自动关闭都可以脱离 UI 单测。
    /// </summary>
    public sealed class InputTriggerMenuState
    {
        public InputTriggerMenuState()
        {
            Groups = new List<InputTriggerGroup>();
            HighlightIndex = -1;
        }

        public bool IsOpen { get; set; }

        public TriggerHit Hit { get; set; }

        public long Generation { get; set; }

        public IReadOnlyList<InputTriggerGroup> Groups { get; set; }

        /// <summary>高亮所在组；没有高亮时为 null。</summary>
        public string HighlightSource { get; set; }

        /// <summary>高亮在组内的下标；没有高亮时为 -1。</summary>
        public int HighlightIndex { get; set; }

        public bool HasHighlight
        {
            get { return HighlightSource != null && HighlightIndex >= 0; }
        }

        /// <summary>所有组都已就绪（不含被移除的失败组）。</summary>
        public bool IsSettled
        {
            get { return Groups.All(group => group.Status == InputTriggerGroupStatus.Ready); }
        }

        public bool IsEmpty
        {
            get { return Groups.All(group => group.Items == null || group.Items.Count == 0); }
        }

        public InputTriggerCandidate Highlighted
        {
            get
            {
                InputTriggerGroup group = FindGroup(HighlightSource);
                if (group == null || HighlightIndex < 0 || HighlightIndex >= group.Items.Count)
                {
                    return null;
                }

                return group.Items[HighlightIndex];
            }
        }

        public InputTriggerGroup FindGroup(string source)
        {
            if (source == null)
            {
                return null;
            }

            foreach (InputTriggerGroup group in Groups)
            {
                if (string.Equals(group.Source, source, StringComparison.Ordinal))
                {
                    return group;
                }
            }

            return null;
        }

        public InputTriggerMenuState Clone()
        {
            var groups = new List<InputTriggerGroup>(Groups.Count);
            foreach (InputTriggerGroup group in Groups)
            {
                groups.Add(group.Clone());
            }

            return new InputTriggerMenuState
            {
                IsOpen = IsOpen,
                Hit = Hit == null ? null : Hit.Clone(),
                Generation = Generation,
                Groups = groups,
                HighlightSource = HighlightSource,
                HighlightIndex = HighlightIndex
            };
        }
    }

    public abstract class InputTriggerMenuEvent
    {
    }

    /// <summary>新的 token（或同一 token 的查询变化）。</summary>
    public sealed class MenuHitEvent : InputTriggerMenuEvent
    {
        public MenuHitEvent(TriggerHit hit)
        {
            Hit = hit;
        }

        public TriggerHit Hit { get; private set; }
    }

    /// <summary>某一代际、某个 source 的候选回来了。</summary>
    public sealed class MenuGroupSettledEvent : InputTriggerMenuEvent
    {
        public MenuGroupSettledEvent(long generation, string source, IReadOnlyList<InputTriggerCandidate> items)
        {
            Generation = generation;
            Source = source;
            Items = items ?? new List<InputTriggerCandidate>();
        }

        public long Generation { get; private set; }

        public string Source { get; private set; }

        public IReadOnlyList<InputTriggerCandidate> Items { get; private set; }
    }

    /// <summary>某一代际、某个 source 取数失败：整组移除，不弹错误。</summary>
    public sealed class MenuGroupFailedEvent : InputTriggerMenuEvent
    {
        public MenuGroupFailedEvent(long generation, string source)
        {
            Generation = generation;
            Source = source;
        }

        public long Generation { get; private set; }

        public string Source { get; private set; }
    }

    /// <summary>↑ / ↓：在所有就绪行之间循环。</summary>
    public sealed class MenuMoveEvent : InputTriggerMenuEvent
    {
        public MenuMoveEvent(int delta)
        {
            Delta = delta;
        }

        public int Delta { get; private set; }
    }

    /// <summary>指针移动到某一行（与键盘共用同一个高亮）。</summary>
    public sealed class MenuHoverEvent : InputTriggerMenuEvent
    {
        public MenuHoverEvent(string source, int index)
        {
            Source = source;
            Index = index;
        }

        public string Source { get; private set; }

        public int Index { get; private set; }
    }

    /// <summary>关闭菜单；保留代际，好让在途返回继续被丢弃。</summary>
    public sealed class MenuCloseEvent : InputTriggerMenuEvent
    {
    }

    /// <summary>
    /// 菜单归约：一个纯函数 + 五个事件。规则对齐 DSH 的 <c>core/menu.ts</c>：
    /// 1. 每次新 token 换代，pending 组保留旧行（stale-while-revalidate）；
    /// 2. 过期代际的 settled / failed 直接丢弃；
    /// 3. 所有组就绪且都为空时自动关闭；
    /// 4. 移动 / 悬停只在就绪行之间循环，指针与键盘共用一个高亮。
    /// </summary>
    public static class InputTriggerMenuReducer
    {
        /// <summary>会话开始 / 切换时种下 source 花名册；菜单关闭、没有代际。</summary>
        public static InputTriggerMenuState Seed(IEnumerable<InputTriggerGroup> groups)
        {
            var state = new InputTriggerMenuState();
            if (groups != null)
            {
                state.Groups = groups
                    .Where(group => group != null)
                    .OrderBy(group => group.Order)
                    .Select(group => new InputTriggerGroup(
                        group.Source,
                        group.Title,
                        group.Order,
                        group.ShowTitle,
                        null))
                    .ToList();
            }

            return state;
        }

        public static InputTriggerMenuState Reduce(InputTriggerMenuState state, InputTriggerMenuEvent evt)
        {
            if (state == null)
            {
                state = new InputTriggerMenuState();
            }

            if (evt is MenuHitEvent)
            {
                return ApplyHit(state, ((MenuHitEvent)evt).Hit);
            }

            if (evt is MenuGroupSettledEvent)
            {
                var settled = (MenuGroupSettledEvent)evt;
                return ApplySettled(state, settled.Generation, settled.Source, settled.Items);
            }

            if (evt is MenuGroupFailedEvent)
            {
                var failed = (MenuGroupFailedEvent)evt;
                return ApplyFailed(state, failed.Generation, failed.Source);
            }

            if (evt is MenuMoveEvent)
            {
                return ApplyMove(state, ((MenuMoveEvent)evt).Delta);
            }

            if (evt is MenuHoverEvent)
            {
                var hover = (MenuHoverEvent)evt;
                return ApplyHover(state, hover.Source, hover.Index);
            }

            if (evt is MenuCloseEvent)
            {
                return ApplyClose(state);
            }

            return state;
        }

        private static InputTriggerMenuState ApplyHit(InputTriggerMenuState state, TriggerHit hit)
        {
            if (hit == null)
            {
                return ApplyClose(state);
            }

            string signature = hit.Signature;
            string current = state.Hit == null ? null : state.Hit.Signature;
            if (state.IsOpen && string.Equals(signature, current, StringComparison.Ordinal))
            {
                // 同一个 token 的重复 track：只刷新命中本身，不换代、不重开。
                InputTriggerMenuState same = state.Clone();
                same.Hit = hit.Clone();
                return same;
            }

            var next = state.Clone();
            next.IsOpen = true;
            next.Hit = hit.Clone();
            next.Generation = state.Generation + 1;
            foreach (InputTriggerGroup group in next.Groups)
            {
                group.Status = InputTriggerGroupStatus.Pending;
            }

            return next;
        }

        private static InputTriggerMenuState ApplySettled(
            InputTriggerMenuState state,
            long generation,
            string source,
            IReadOnlyList<InputTriggerCandidate> items)
        {
            if (!state.IsOpen || generation != state.Generation)
            {
                return state;
            }

            InputTriggerGroup group = state.FindGroup(source);
            if (group == null)
            {
                return state;
            }

            var next = state.Clone();
            InputTriggerGroup target = next.FindGroup(source);
            target.Status = InputTriggerGroupStatus.Ready;
            target.Items = items == null
                ? new List<InputTriggerCandidate>()
                : new List<InputTriggerCandidate>(items);

            NormalizeHighlight(next, target);
            AutoCloseIfEmpty(next);
            return next;
        }

        private static InputTriggerMenuState ApplyFailed(InputTriggerMenuState state, long generation, string source)
        {
            if (!state.IsOpen || generation != state.Generation)
            {
                return state;
            }

            var next = state.Clone();
            var groups = new List<InputTriggerGroup>(next.Groups.Count);
            foreach (InputTriggerGroup group in next.Groups)
            {
                if (!string.Equals(group.Source, source, StringComparison.Ordinal))
                {
                    groups.Add(group);
                }
            }

            next.Groups = groups;
            if (next.HighlightSource == source)
            {
                next.HighlightSource = null;
                next.HighlightIndex = -1;
            }

            EnsureHighlight(next);
            AutoCloseIfEmpty(next);
            return next;
        }

        private static InputTriggerMenuState ApplyMove(InputTriggerMenuState state, int delta)
        {
            if (!state.IsOpen)
            {
                return state;
            }

            List<MenuPosition> positions = Flatten(state);
            if (positions.Count == 0)
            {
                return state;
            }

            int current = -1;
            for (int i = 0; i < positions.Count; i++)
            {
                if (string.Equals(positions[i].Source, state.HighlightSource, StringComparison.Ordinal)
                    && positions[i].Index == state.HighlightIndex)
                {
                    current = i;
                    break;
                }
            }

            int nextIndex = current < 0
                ? (delta >= 0 ? 0 : positions.Count - 1)
                : ((current + delta) % positions.Count + positions.Count) % positions.Count;

            var next = state.Clone();
            next.HighlightSource = positions[nextIndex].Source;
            next.HighlightIndex = positions[nextIndex].Index;
            return next;
        }

        private static InputTriggerMenuState ApplyHover(InputTriggerMenuState state, string source, int index)
        {
            if (!state.IsOpen)
            {
                return state;
            }

            InputTriggerGroup group = state.FindGroup(source);
            if (group == null
                || group.Status != InputTriggerGroupStatus.Ready
                || index < 0
                || index >= group.Items.Count)
            {
                return state;
            }

            if (string.Equals(state.HighlightSource, source, StringComparison.Ordinal)
                && state.HighlightIndex == index)
            {
                return state;
            }

            var next = state.Clone();
            next.HighlightSource = source;
            next.HighlightIndex = index;
            return next;
        }

        private static InputTriggerMenuState ApplyClose(InputTriggerMenuState state)
        {
            if (!state.IsOpen)
            {
                return state;
            }

            var next = state.Clone();
            next.IsOpen = false;
            return next;
        }

        /// <summary>某个组换行后，如果高亮失效就落到第一个非空就绪组的第一行。</summary>
        private static void NormalizeHighlight(InputTriggerMenuState state, InputTriggerGroup updated)
        {
            InputTriggerGroup highlighted = state.FindGroup(state.HighlightSource);
            if (highlighted != null
                && highlighted.Status == InputTriggerGroupStatus.Ready
                && state.HighlightIndex >= 0
                && state.HighlightIndex < highlighted.Items.Count)
            {
                return;
            }

            state.HighlightSource = null;
            state.HighlightIndex = -1;
            EnsureHighlight(state);
        }

        private static void EnsureHighlight(InputTriggerMenuState state)
        {
            if (state.HasHighlight)
            {
                return;
            }

            foreach (InputTriggerGroup group in state.Groups)
            {
                if (group.Status == InputTriggerGroupStatus.Ready && group.Items.Count > 0)
                {
                    state.HighlightSource = group.Source;
                    state.HighlightIndex = 0;
                    return;
                }
            }
        }

        private static void AutoCloseIfEmpty(InputTriggerMenuState state)
        {
            if (state.IsSettled && state.IsEmpty)
            {
                state.IsOpen = false;
            }
        }

        private static List<MenuPosition> Flatten(InputTriggerMenuState state)
        {
            var positions = new List<MenuPosition>();
            foreach (InputTriggerGroup group in state.Groups)
            {
                if (group.Status != InputTriggerGroupStatus.Ready)
                {
                    continue;
                }

                for (int i = 0; i < group.Items.Count; i++)
                {
                    positions.Add(new MenuPosition(group.Source, i));
                }
            }

            return positions;
        }

        private sealed class MenuPosition
        {
            public MenuPosition(string source, int index)
            {
                Source = source;
                Index = index;
            }

            public string Source { get; private set; }

            public int Index { get; private set; }
        }
    }
}
