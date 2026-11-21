using System;
using System.Collections.Generic;

namespace MiaoWu.Core.Input
{
    /// <summary>`/` 菜单里的命令标识。图标由界面层映射，Core 不依赖资源键。</summary>
    public enum ComposerCommandKind
    {
        Compact,
        Plan,
        Work
    }

    /// <summary>一条输入框命令。命令只描述“能做什么”，执行由宿主完成。</summary>
    public sealed class ComposerCommand
    {
        public ComposerCommand(ComposerCommandKind kind, string name, string title, string description)
        {
            Kind = kind;
            Name = name;
            Title = title;
            Description = description;
        }

        public ComposerCommandKind Kind { get; private set; }

        /// <summary>ASCII 命令名，宿主永远只认这个名字。</summary>
        public string Name { get; private set; }

        /// <summary>菜单主标题（中文）。</summary>
        public string Title { get; private set; }

        public string Description { get; private set; }

        /// <summary>菜单里右侧的别名，与标题不同的命令名。</summary>
        public string Alias
        {
            get { return "/" + Name; }
        }
    }

    /// <summary>
    /// MiaoWu 的输入框命令目录。刻意保持很小：压缩、进入计划模式、退出计划模式。
    /// 计划 / 工作按当前状态只出现一个，避免把“已经处于该模式”的无效动作列出来。
    /// </summary>
    public static class ComposerCommandCatalog
    {
        private static readonly ComposerCommand CompactCommand =
            new ComposerCommand(ComposerCommandKind.Compact, "compact", "压缩", "把较早的对话总结成摘要，减少上下文占用");

        private static readonly ComposerCommand PlanCommand =
            new ComposerCommand(ComposerCommandKind.Plan, "plan", "计划模式", "先调研并给出计划，批准后再修改文件");

        private static readonly ComposerCommand WorkCommand =
            new ComposerCommand(ComposerCommandKind.Work, "work", "工作模式", "退出计划模式，恢复完整工具集");

        /// <summary>全部命令（与状态无关）。</summary>
        public static IReadOnlyList<ComposerCommand> All
        {
            get { return new[] { CompactCommand, PlanCommand, WorkCommand }; }
        }

        /// <summary>
        /// 按当前状态与查询词过滤命令。查询为空时按“压缩 → 计划 / 工作”的固定顺序。
        /// </summary>
        public static IReadOnlyList<ComposerCommand> Match(
            string query,
            bool planMode,
            bool hasMessages)
        {
            var result = new List<ComposerCommand>();
            foreach (ComposerCommand command in All)
            {
                if (command.Kind == ComposerCommandKind.Compact && !hasMessages)
                {
                    continue;
                }

                if (command.Kind == ComposerCommandKind.Plan && planMode)
                {
                    continue;
                }

                if (command.Kind == ComposerCommandKind.Work && !planMode)
                {
                    continue;
                }

                if (!Matches(command, query))
                {
                    continue;
                }

                result.Add(command);
            }

            return result;
        }

        /// <summary>按名字精确查找；不区分大小写。</summary>
        public static ComposerCommand Find(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (ComposerCommand command in All)
            {
                if (string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return command;
                }
            }

            return null;
        }

        /// <summary>
        /// 回车裁决：整行是不是一条“裸命令”。只有命令名、后面没有参数时返回 true；
        /// 带参数（例如 `/compact 明天`）按普通消息处理。
        /// </summary>
        public static bool TryParseBareCommand(string line, out ComposerCommand command)
        {
            command = null;
            string text = (line ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '/')
            {
                return false;
            }

            string name = text.Substring(1);
            if (name.IndexOf(' ') >= 0 || name.IndexOf('\t') >= 0 || name.IndexOf('@') >= 0)
            {
                return false;
            }

            command = Find(name);
            return command != null;
        }

        private static bool Matches(ComposerCommand command, string query)
        {
            string term = (query ?? string.Empty).Trim();
            if (term.Length == 0)
            {
                return true;
            }

            string lowered = term.ToLowerInvariant();
            if (command.Name.StartsWith(lowered, StringComparison.Ordinal)
                || command.Title.StartsWith(term, StringComparison.Ordinal))
            {
                return true;
            }

            return MentionSearch.IsSubsequence(command.Name.ToLowerInvariant(), lowered)
                || command.Title.IndexOf(term, StringComparison.Ordinal) >= 0;
        }
    }
}
