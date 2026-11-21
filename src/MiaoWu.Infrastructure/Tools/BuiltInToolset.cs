using System;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Tools;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 内置工具的组合。AppHost 负责创建审计日志与审批服务，这里只装配工具本身，
    /// 避免工具层反向依赖 UI 或运行时。
    /// </summary>
    public static class BuiltInToolset
    {
        public static ToolRegistry CreateRegistry(
            IAppPaths appPaths,
            IClock clock,
            BackgroundJobRegistry jobs = null,
            IUserQuestionService questions = null,
            IWebSearchProvider search = null,
            MiaoWu.Core.Agents.Schedules.IScheduleService schedules = null)
        {
            if (appPaths == null)
            {
                throw new ArgumentNullException(nameof(appPaths));
            }

            clock = clock ?? SystemClock.Instance;
            jobs = jobs ?? new BackgroundJobRegistry();

            var registry = new ToolRegistry();
            registry.Register(new ReadTool());
            registry.Register(new GlobTool());
            registry.Register(new GrepTool());

            var writer = new WriteTool(
                new FileBackupService(appPaths.BackupsDirectory),
                clock);
            registry.Register(writer);
            registry.Register(new EditTool(writer));
            registry.Register(new PwshTool(jobs));
            registry.Register(new SkillTool(appPaths.UserSkillsDirectory));
            registry.Register(new WorkingDirectoryTool());
            registry.Register(new WebFetchTool(new HttpWebPageFetcher()));
            registry.Register(new WebSearchTool(
                search ?? new DuckDuckGoSearchProvider(new HttpWebPageFetcher())));
            registry.Register(new JobListTool(jobs));
            registry.Register(new JobOutputTool(jobs));
            registry.Register(new JobKillTool(jobs));
            registry.Register(new PresentTool());
            registry.Register(new CreateGoalTool());
            registry.Register(new GetGoalTool());
            registry.Register(new UpdateGoalTool());
            registry.Register(new ExitPlanModeTool());

            // 定时任务需要宿主提供投递通道；没有服务时不注册，避免模型调用后必然拿到 schedule_unavailable。
            if (schedules != null)
            {
                registry.Register(new ScheduleCreateTool(schedules));
                registry.Register(new ScheduleEditTool(schedules));
                registry.Register(new ScheduleListTool(schedules));
                registry.Register(new ScheduleViewTool(schedules));
            }

            // 提问通道是宿主能力：没有通道时不注册，避免模型调用后必然拿到 question_unavailable。
            if (questions != null)
            {
                registry.Register(new AskUserQuestionTool(questions));
            }

            return registry;
        }
    }
}
