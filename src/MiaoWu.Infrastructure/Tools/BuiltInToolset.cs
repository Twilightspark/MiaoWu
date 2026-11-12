using System;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// V1 内置工具的组合。AppHost 负责创建审计日志与审批服务，这里只装配工具本身，
    /// 避免工具层反向依赖 UI 或运行时。
    /// </summary>
    public static class BuiltInToolset
    {
        public static ToolRegistry CreateRegistry(IAppPaths appPaths, IClock clock)
        {
            if (appPaths == null)
            {
                throw new ArgumentNullException(nameof(appPaths));
            }

            clock = clock ?? SystemClock.Instance;

            var registry = new ToolRegistry();
            registry.Register(new ListDirectoryTool());
            registry.Register(new ReadFileTool());
            registry.Register(new SearchFilesTool());
            registry.Register(new WriteFileTool(
                new FileBackupService(appPaths.BackupsDirectory),
                clock));
            registry.Register(new RunCommandTool());
            return registry;
        }
    }
}
