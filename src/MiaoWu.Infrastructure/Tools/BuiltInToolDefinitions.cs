using System;
using System.Collections.Generic;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 内置工具的模型可见声明：JSON Schema、系统提示片段与规则。
    /// 工具名与 Implementation 常量一一对应；新增工具必须同时补 schema。
    /// </summary>
    public static class BuiltInToolSchemas
    {
        public static ToolDefinition Create(IAgentTool tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            switch (tool.Name)
            {
                case ToolNames.ListDirectory:
                    return Build(
                        tool,
                        "列出目录内容",
                        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"相对工作区根的目录，省略为根目录\"},\"depth\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":3,\"description\":\"递归层数，默认 1\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000,\"description\":\"最多返回条目数\"}},\"required\":[]}",
                        "列出工作区目录（目录优先，默认只列当前层）。",
                        new[]
                        {
                            "list_dir：路径相对于工作区根，不要使用 .. 或绝对路径。"
                        });

                case ToolNames.ReadFile:
                    return Build(
                        tool,
                        "读取文本文件",
                        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"相对工作区根的文件路径\"},\"startLine\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"起始行号，默认 1\"},\"maxLines\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2000,\"description\":\"最多读取的行数\"}},\"required\":[\"path\"]}",
                        "读取工作区内文本文件的行；大文件用 startLine/maxLines 分段。",
                        new[]
                        {
                            "read_file：内容带行号；修改文件前先用它确认原文。"
                        });

                case ToolNames.SearchFiles:
                    return Build(
                        tool,
                        "按内容搜索",
                        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"要搜索的文本\"},\"path\":{\"type\":\"string\",\"description\":\"搜索起始目录，默认工作区根\"},\"include\":{\"type\":\"string\",\"description\":\"文件名通配符，例如 *.cs\"},\"maxResults\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"caseSensitive\":{\"type\":\"boolean\"}},\"required\":[\"query\"]}",
                        "按内容搜索工作区文本文件（跳过 .git/bin/obj 等目录）。",
                        new[]
                        {
                            "search_files：先缩小 path/include 再搜，避免扫全仓。"
                        });

                case ToolNames.WriteFile:
                    return Build(
                        tool,
                        "写入文件",
                        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"相对工作区根的文件路径\"},\"content\":{\"type\":\"string\",\"description\":\"完整文件内容（UTF-8）\"}},\"required\":[\"path\",\"content\"]}",
                        "覆盖写入工作区文件（覆盖前备份，审批后执行）。",
                        new[]
                        {
                            "write_file：优先做小范围修改；不要用它生成无关的大文件。",
                            "write_file：覆盖已有文件前先 read_file，保留原有编码与行尾。"
                        });

                case ToolNames.FindFiles:
                    return Build(
                        tool,
                        "按名字找文件",
                        "{\"type\":\"object\",\"properties\":{\"pattern\":{\"type\":\"string\",\"description\":\"glob，例如 **/*.cs\"},\"path\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"required\":[\"pattern\"]}",
                        "按文件名 glob 查找工作区文件。",
                        new[]
                        {
                            "find_files：先按扩展名缩小范围，再读取命中的文件。"
                        });

                case ToolNames.EditFile:
                    return Build(
                        tool,
                        "精确替换",
                        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"edits\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"oldText\":{\"type\":\"string\"},\"newText\":{\"type\":\"string\"}},\"required\":[\"oldText\",\"newText\"]}}},\"required\":[\"path\",\"edits\"]}",
                        "对文件做精确文本替换（oldText 必须唯一匹配）。",
                        new[]
                        {
                            "edit_file：oldText 必须来自 read_file 的原文；匹配不唯一时加长上下文。"
                        });

                case ToolNames.RunCommand:
                    return Build(
                        tool,
                        "执行命令",
                        "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"要执行的命令\"},\"shell\":{\"type\":\"string\",\"enum\":[\"powershell\",\"cmd\"],\"description\":\"默认 powershell\"},\"cwd\":{\"type\":\"string\",\"description\":\"相对工作区根的工作目录\"},\"timeoutSeconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":600,\"description\":\"默认 60 秒\"}},\"required\":[\"command\"]}",
                        "在工作区目录执行命令并返回 stdout/stderr；始终需要用户审批。",
                        new[]
                        {
                            "run_command：优先只读命令（如 dir、Get-ChildItem、git status）；不要在命令里拼接用户输入。",
                            "run_command：写操作、删除、网络访问会被标记为高风险，必须等待审批。"
                        });

                default:
                    return Build(
                        tool,
                        tool.Description,
                        "{\"type\":\"object\",\"properties\":{}}",
                        tool.Description,
                        new string[0]);
            }
        }

        private static ToolDefinition Build(
            IAgentTool tool,
            string snippet,
            string parametersJson,
            string description,
            IReadOnlyList<string> guidelines)
        {
            return new ToolDefinition
            {
                Name = tool.Name,
                Description = string.IsNullOrWhiteSpace(tool.Description) ? description : tool.Description,
                ParametersJson = parametersJson,
                PromptSnippet = snippet,
                PromptGuidelines = guidelines,
                Risk = tool.Risk,
                ExecutionMode = tool.RequiresApproval ? ToolExecutionMode.Sequential : ToolExecutionMode.Parallel,
                Annotations = new ToolAnnotations
                {
                    IsReadOnly = !tool.RequiresApproval && tool.Risk == ToolRisk.Low,
                    IsDestructive = tool.RequiresApproval,
                    IsOpenWorld = tool.Name == ToolNames.RunCommand
                }
            };
        }
    }

    /// <summary>从工具注册表构建模型可见声明；只返回本版本实际注册的工具。</summary>
    public sealed class BuiltInToolDefinitionProvider : IToolDefinitionProvider
    {
        private readonly IToolRegistry _registry;

        public BuiltInToolDefinitionProvider(IToolRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public IReadOnlyList<ToolDefinition> GetDefinitions()
        {
            var definitions = new List<ToolDefinition>();
            foreach (IAgentTool tool in _registry.GetAll())
            {
                if (tool == null)
                {
                    continue;
                }

                try
                {
                    definitions.Add(BuiltInToolSchemas.Create(tool));
                }
                catch (Exception)
                {
                    // 单个工具声明失败不应阻断全部工具。
                }
            }

            definitions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return definitions;
        }
    }
}
