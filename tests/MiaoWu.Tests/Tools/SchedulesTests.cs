using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Agents.Schedules;
using MiaoWu.Core.Agents.Tools;
using MiaoWu.Infrastructure.Tools;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    /// <summary>定时任务：RFC 5545 子集、持久化、到点投递与四个工具。</summary>
    public sealed class SchedulesTests : IDisposable
    {
        private readonly string _root;
        private readonly MutableClock _clock = new MutableClock(new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.FromHours(8)));
        private readonly List<ScheduleDefinition> _delivered = new List<ScheduleDefinition>();

        public SchedulesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "miaowu-schedules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        // ---- 规则 ----

        [Fact]
        public void DailyRuleReturnsNextLocalOccurrence()
        {
            ScheduleRecurrence rule = ScheduleRecurrence.Parse(
                "DTSTART:20261121T090000\nRRULE:FREQ=DAILY;INTERVAL=1");

            DateTimeOffset? next = rule.FirstOnOrAfter(_clock.Now);

            Assert.NotNull(next);
            Assert.Equal(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.FromHours(8)), next.Value);
            Assert.Contains("每天", rule.Describe());
        }

        [Fact]
        public void WeeklyRuleUsesByDayAndInterval()
        {
            // 2026-11-21 是周六；BYDAY=MO,WE 从周六起下一个周一是 11-23。
            ScheduleRecurrence rule = ScheduleRecurrence.Parse(
                "DTSTART:20261121T090000\nRRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=MO,WE");

            DateTimeOffset? next = rule.FirstOnOrAfter(_clock.Now);

            Assert.NotNull(next);
            Assert.Equal(DayOfWeek.Monday, next.Value.DayOfWeek);
            Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.FromHours(8)), next.Value);
        }

        [Fact]
        public void MonthlyRuleClampsShortMonths()
        {
            ScheduleRecurrence rule = ScheduleRecurrence.Parse(
                "DTSTART:20260131T100000\nRRULE:FREQ=MONTHLY");

            DateTimeOffset? next = rule.FirstOnOrAfter(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(8)));

            Assert.NotNull(next);
            Assert.Equal(new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.FromHours(8)), next.Value);
        }

        [Fact]
        public void CountAndUntilStopTheSeries()
        {
            ScheduleRecurrence counted = ScheduleRecurrence.Parse(
                "DTSTART:20261121T090000\nRRULE:FREQ=DAILY;COUNT=2");
            DateTimeOffset afterFirst = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.FromHours(8));
            DateTimeOffset? second = counted.NextAfter(afterFirst);
            Assert.NotNull(second);
            Assert.Equal(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(8)), second.Value);
            Assert.Null(counted.NextAfter(second.Value));

            ScheduleRecurrence until = ScheduleRecurrence.Parse(
                "DTSTART:20261121T090000\nRRULE:FREQ=DAILY;UNTIL=20261122T090000Z");
            Assert.NotNull(until.NextAfter(afterFirst));
            Assert.Null(until.NextAfter(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(8))));
        }

        [Theory]
        [InlineData("", "invalid_rrule")]
        [InlineData("RRULE:FREQ=DAILY", "invalid_rrule")]
        [InlineData("DTSTART:20261121T090000", "invalid_rrule")]
        [InlineData("DTSTART:20261121T090000\nRRULE:FREQ=HOURLY", "invalid_rrule")]
        [InlineData("DTSTART:20261121T090000\nRRULE:FREQ=DAILY;BYDAY=MO", "invalid_rrule")]
        [InlineData("DTSTART:20261121T090000\nRRULE:FREQ=DAILY;INTERVAL=0", "invalid_rrule")]
        [InlineData("DTSTART:20261121T090000\nRRULE:FREQ=DAILY;BYSETPOS=1", "invalid_rrule")]
        public void InvalidRulesAreRejected(string rule, string code)
        {
            ScheduleException error = Assert.Throws<ScheduleException>(() => ScheduleRecurrence.Parse(rule));
            Assert.Equal(code, error.Code);
        }

        // ---- 服务 ----

        [Fact]
        public void TickDeliversOnceAndReschedules()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleDefinition created = service.Create(
                    "早报",
                    "读一遍今天的日志",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                service.Tick(new DateTimeOffset(2026, 11, 21, 8, 59, 0, TimeSpan.FromHours(8)));
                Assert.Empty(_delivered);

                service.Tick(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.FromHours(8)));
                Assert.Single(_delivered);
                Assert.Equal(created.Id, _delivered[0].Id);

                ScheduleDefinition after = service.Get(created.Id);
                Assert.Equal(1, after.RunCount);
                Assert.Equal(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(8)), after.NextRunAt);

                // 同一时刻再检查不会重复投递。
                service.Tick(new DateTimeOffset(2026, 11, 21, 9, 0, 1, TimeSpan.FromHours(8)));
                Assert.Single(_delivered);
            }
        }

        [Fact]
        public void ExecutionModelAndReasoningPersistAndCanResetToDefault()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleDefinition created = service.Create(
                    "模型任务",
                    "用指定模型执行",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1",
                    "provider-a",
                    "model-a",
                    "high");

                Assert.Equal("provider-a", created.ProviderId);
                Assert.Equal("model-a", created.ModelId);
                Assert.Equal("high", created.ReasoningEffort);
            }

            // 重新装载同一份 schedules.json，三个字段都要保留。
            using (ScheduleService reloaded = CreateService())
            {
                ScheduleDefinition stored = reloaded.List(true)[0];
                Assert.Equal("provider-a", stored.ProviderId);
                Assert.Equal("model-a", stored.ModelId);
                Assert.Equal("high", stored.ReasoningEffort);

                // 空串表示清回默认模型 / 默认推理等级；null 才是“这项不改”。
                ScheduleUpdateResult reset = reloaded.Update(
                    stored.Id,
                    null,
                    null,
                    null,
                    null,
                    providerId: string.Empty,
                    modelId: string.Empty,
                    reasoningEffort: string.Empty);
                Assert.Equal(ScheduleUpdateStatus.Updated, reset.Status);
                Assert.Null(reset.Definition.ProviderId);
                Assert.Null(reset.Definition.ModelId);
                Assert.Null(reset.Definition.ReasoningEffort);
            }
        }

        [Fact]
        public void CountOneDeactivatesAfterDelivery()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleDefinition created = service.Create(
                    "一次性提醒",
                    "只提醒一次",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY;COUNT=1",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                service.Tick(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.FromHours(8)));

                Assert.Single(_delivered);
                ScheduleDefinition after = service.Get(created.Id);
                Assert.False(after.Active);
                Assert.Null(after.NextRunAt);
                Assert.Empty(service.List(false));
                Assert.Single(service.List(true));
            }
        }

        [Fact]
        public void UpdateChangesFieldsAndCanPause()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleDefinition created = service.Create(
                    "旧标题",
                    "旧内容",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                Assert.Equal(
                    ScheduleUpdateStatus.Updated,
                    service.Update(created.Id, "新标题", null, null, false).Status);
                ScheduleDefinition edited = service.Get(created.Id);
                Assert.Equal("新标题", edited.Title);
                Assert.Equal("旧内容", edited.Prompt);
                Assert.False(edited.Active);

                service.Tick(new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.FromHours(8)));
                Assert.Empty(_delivered);

                Assert.Equal(
                    ScheduleUpdateStatus.Updated,
                    service.Update(created.Id, null, null, null, true).Status);
                service.Tick(new DateTimeOffset(2026, 11, 21, 9, 30, 0, TimeSpan.FromHours(8)));
                Assert.Single(_delivered);
            }
        }

        [Fact]
        public void SchedulesSurviveReload()
        {
            string path = Path.Combine(_root, "schedules.json");
            using (var service = new ScheduleService(new ScheduleFileStore(path), _clock, Deliver))
            {
                service.Create(
                    "持久化",
                    "重载后还在",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");
            }

            using (var reloaded = new ScheduleService(new ScheduleFileStore(path), _clock, Deliver))
            {
                Assert.Single(reloaded.List(true));
                Assert.Equal("持久化", reloaded.List(true)[0].Title);
            }
        }

        [Fact]
        public void CorruptFileFallsBackToEmpty()
        {
            string path = Path.Combine(_root, "schedules.json");
            File.WriteAllText(path, "{ not json");

            using (var service = new ScheduleService(new ScheduleFileStore(path), _clock, Deliver))
            {
                Assert.Empty(service.List(true));
            }
        }

        // ---- 工具 ----

        [Fact]
        public async Task CreateToolWritesSchedule()
        {
            using (ScheduleService service = CreateService())
            {
                var tool = new ScheduleCreateTool(service);
                Assert.True(tool.RequiresApproval);

                ToolResult result = await tool.ExecuteAsync(
                    Call(ToolNames.ScheduleCreate,
                        "{\"title\":\"日报\",\"prompt\":\"总结今天的改动\",\"rrule\":\"DTSTART:20261121T090000\\nRRULE:FREQ=DAILY;COUNT=3\"}"),
                    new ToolContext { SessionId = "s1", WorkspaceId = "w1" },
                    CancellationToken.None);

                Assert.Equal(ToolResultStatus.Succeeded, result.Status);
                Assert.Contains("已创建定时任务", result.Content);
                Assert.Contains("日报", result.Content);
                Assert.Single(service.List(true));
            }
        }

        [Fact]
        public void UpdateSessionRecordsTheCreatedSession()
        {
            string path = Path.Combine(_root, "schedules.json");
            string id;
            using (var service = new ScheduleService(new ScheduleFileStore(path), _clock, Deliver))
            {
                ScheduleDefinition created = service.Create(
                    "新会话任务",
                    "投递到新会话",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.NewOnce,
                    null,
                    "w1");

                Assert.True(service.UpdateSession(created.Id, "s_new"));
                Assert.Equal("s_new", service.Get(created.Id).SessionId);
                id = created.Id;
            }

            using (var reloaded = new ScheduleService(new ScheduleFileStore(path), _clock, Deliver))
            {
                Assert.Equal("s_new", reloaded.Get(id).SessionId);
            }
        }

        [Fact]
        public async Task CreateToolAcceptsNewSessionTargets()
        {
            using (ScheduleService service = CreateService())
            {
                ToolResult result = await new ScheduleCreateTool(service).ExecuteAsync(
                    Call(ToolNames.ScheduleCreate,
                        "{\"title\":\"新会话日报\",\"prompt\":\"总结\",\"rrule\":\"DTSTART:20261121T090000\\nRRULE:FREQ=DAILY\",\"target\":\"new_once\"}"),
                    new ToolContext { SessionId = "s1", WorkspaceId = "w1" },
                    CancellationToken.None);

                Assert.Equal(ToolResultStatus.Succeeded, result.Status);
                Assert.Equal(ScheduleTarget.NewOnce, service.List(true)[0].Target);
            }
        }

        [Fact]
        public async Task CreateToolRejectsUnknownTarget()
        {
            using (ScheduleService service = CreateService())
            {
                ToolResult result = await new ScheduleCreateTool(service).ExecuteAsync(
                    Call(ToolNames.ScheduleCreate,
                        "{\"title\":\"x\",\"prompt\":\"y\",\"rrule\":\"DTSTART:20261121T090000\\nRRULE:FREQ=DAILY\",\"target\":\"everywhere\"}"),
                    new ToolContext { SessionId = "s1", WorkspaceId = "w1" },
                    CancellationToken.None);

                Assert.Equal(ToolResultStatus.InvalidArguments, result.Status);
            }
        }

        [Fact]
        public async Task ListAndViewReflectEdits()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleDefinition created = service.Create(
                    "列表",
                    "内容",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                ToolResult list = await new ScheduleListTool(service).ExecuteAsync(
                    Call(ToolNames.ScheduleList, "{}"),
                    new ToolContext(),
                    CancellationToken.None);
                Assert.Contains("启用的定时任务：1 个", list.Content);

                ToolResult view = await new ScheduleViewTool(service).ExecuteAsync(
                    Call(ToolNames.ScheduleView, "{\"id\":\"" + created.Id + "\"}"),
                    new ToolContext(),
                    CancellationToken.None);
                Assert.Contains("规则原文", view.Content);

                ToolResult edit = await new ScheduleEditTool(service).ExecuteAsync(
                    Call(ToolNames.ScheduleEdit, "{\"id\":\"" + created.Id + "\",\"active\":false}"),
                    new ToolContext(),
                    CancellationToken.None);
                Assert.Equal(ToolResultStatus.Succeeded, edit.Status);

                ToolResult hidden = await new ScheduleListTool(service).ExecuteAsync(
                    Call(ToolNames.ScheduleList, "{}"),
                    new ToolContext(),
                    CancellationToken.None);
                Assert.Contains("没有启用的定时任务", hidden.Content);
            }
        }

        [Fact]
        public async Task MissingServiceIsReported()
        {
            ToolResult list = await new ScheduleListTool(null).ExecuteAsync(
                Call(ToolNames.ScheduleList, "{}"),
                new ToolContext(),
                CancellationToken.None);

            Assert.Equal(ToolResultStatus.Failed, list.Status);
            Assert.Equal("schedule_unavailable", list.ErrorCode);
        }

        // ---- 投递回执、立即执行与 CAS ----

        [Fact]
        public void RunNowDeliversWithoutMovingNextRun()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleDefinition created = service.Create(
                    "立即执行",
                    "现在跑一次",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                DateTimeOffset? next = created.NextRunAt;
                Assert.True(service.RunNow(created.Id));

                Assert.Single(_delivered);
                ScheduleDefinition after = service.Get(created.Id);
                Assert.Equal(next, after.NextRunAt);
                Assert.Equal(1, after.RunCount);
                Assert.Single(after.History);
                Assert.Equal(ScheduleDeliveryKind.Manual, after.History[0].Kind);
                Assert.Equal("现在跑一次", after.History[0].Prompt);
                Assert.False(after.History[0].Failed);
            }
        }

        [Fact]
        public void TickAppendsAutomaticHistory()
        {
            using (ScheduleService service = CreateService())
            {
                service.Create(
                    "到点",
                    "到点投递的正文",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                service.Tick(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.FromHours(8)));

                ScheduleDefinition after = service.List(true)[0];
                Assert.Single(after.History);
                Assert.Equal(ScheduleDeliveryKind.Automatic, after.History[0].Kind);
                Assert.Equal("到点投递的正文", after.History[0].Prompt);
            }
        }

        [Fact]
        public void HistoryIsCappedAndSurvivesReload()
        {
            string path = Path.Combine(_root, "schedules.json");
            string id;
            using (var service = new ScheduleService(new ScheduleFileStore(path), _clock, Deliver))
            {
                ScheduleDefinition created = service.Create(
                    "回执上限",
                    "每次投递都记一条",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");
                id = created.Id;

                for (int i = 0; i < ScheduleService.HistoryLimit + 5; i++)
                {
                    service.RunNow(id);
                }

                Assert.Equal(ScheduleService.HistoryLimit, service.Get(id).History.Count);
            }

            using (var reloaded = new ScheduleService(new ScheduleFileStore(path), _clock, Deliver))
            {
                ScheduleDefinition after = reloaded.Get(id);
                Assert.Equal(ScheduleService.HistoryLimit, after.History.Count);
                Assert.Equal(ScheduleDeliveryKind.Manual, after.History[after.History.Count - 1].Kind);
            }
        }

        [Fact]
        public void DeleteRemovesTaskAndHistory()
        {
            string path = Path.Combine(_root, "schedules.json");
            string id;
            using (var service = new ScheduleService(new ScheduleFileStore(path), _clock, Deliver))
            {
                ScheduleDefinition created = service.Create(
                    "待删除",
                    "内容",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");
                id = created.Id;
                service.RunNow(id);

                Assert.True(service.Delete(id));
                Assert.Null(service.Get(id));
                Assert.Empty(service.List(true));
                Assert.False(service.Delete(id));
            }

            using (var reloaded = new ScheduleService(new ScheduleFileStore(path), _clock, Deliver))
            {
                Assert.Empty(reloaded.List(true));
            }
        }

        [Fact]
        public void StaleUpdatedAtIsRejectedWithoutWriting()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleDefinition created = service.Create(
                    "冲突",
                    "原始内容",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                DateTimeOffset stale = created.UpdatedAt;
                ScheduleUpdateResult first = service.Update(created.Id, "先写", null, null, null);
                Assert.Equal(ScheduleUpdateStatus.Updated, first.Status);

                ScheduleUpdateResult second = service.Update(
                    created.Id,
                    "后写",
                    null,
                    null,
                    null,
                    stale);
                Assert.Equal(ScheduleUpdateStatus.Conflict, second.Status);

                ScheduleDefinition after = service.Get(created.Id);
                Assert.Equal("先写", after.Title);
            }
        }

        [Fact]
        public void EquivalentRuleEditKeepsCommittedNextRun()
        {
            using (ScheduleService service = CreateService())
            {
                string rule = "DTSTART:20261121T090000\nRRULE:FREQ=DAILY";
                ScheduleDefinition created = service.Create(
                    "等价编辑",
                    "内容",
                    rule,
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                DateTimeOffset? next = created.NextRunAt;
                ScheduleUpdateResult result = service.Update(created.Id, null, null, rule, null);
                Assert.Equal(ScheduleUpdateStatus.Updated, result.Status);
                Assert.Equal(next, service.Get(created.Id).NextRunAt);
            }
        }

        [Fact]
        public void UpdateCanSwitchTargetAndWorkspace()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleDefinition created = service.Create(
                    "切目标",
                    "内容",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                ScheduleUpdateResult result = service.Update(
                    created.Id,
                    null,
                    null,
                    null,
                    null,
                    null,
                    ScheduleTarget.New,
                    null,
                    "w2");

                Assert.Equal(ScheduleUpdateStatus.Updated, result.Status);
                Assert.Equal(ScheduleTarget.New, result.Definition.Target);
                Assert.Null(result.Definition.SessionId);
                Assert.Equal("w2", result.Definition.WorkspaceId);
            }
        }

        [Fact]
        public void CreateRejectsRuleWithoutFutureOccurrence()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleException error = Assert.Throws<ScheduleException>(() => service.Create(
                    "过期规则",
                    "内容",
                    "DTSTART:20200101T090000\nRRULE:FREQ=DAILY;COUNT=1",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1"));

                Assert.Equal("not_future", error.Code);
                Assert.Empty(service.List(true));
            }
        }

        [Fact]
        public void RestoreEndedOneOffLeavesStateUntouched()
        {
            using (ScheduleService service = CreateService())
            {
                ScheduleDefinition created = service.Create(
                    "一次性",
                    "只跑一次",
                    "DTSTART:20261121T090000\nRRULE:FREQ=DAILY;COUNT=1",
                    ScheduleTarget.ThisSession,
                    "s1",
                    "w1");

                service.Tick(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.FromHours(8)));
                ScheduleDefinition ended = service.Get(created.Id);
                Assert.False(ended.Active);
                Assert.Null(ended.NextRunAt);

                // 时钟走到发生时刻之后，恢复已耗尽的一次性任务没有未来时点。
                _clock.UtcNow = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.FromHours(8))
                    .ToUniversalTime();

                ScheduleException error = Assert.Throws<ScheduleException>(() =>
                    service.Update(created.Id, null, null, null, true));
                Assert.Equal("not_future", error.Code);

                ScheduleDefinition after = service.Get(created.Id);
                Assert.False(after.Active);
                Assert.Null(after.NextRunAt);
            }
        }

        private ScheduleService CreateService()
        {
            return new ScheduleService(
                new ScheduleFileStore(Path.Combine(_root, "schedules.json")),
                _clock,
                delivery =>
                {
                    lock (_delivered)
                    {
                        _delivered.Add(delivery);
                    }

                    return Task.FromResult(0);
                });
        }

        private Task Deliver(ScheduleDefinition definition)
        {
            lock (_delivered)
            {
                _delivered.Add(definition);
            }

            return Task.FromResult(0);
        }

        private static ToolCall Call(string name, string argumentsJson)
        {
            return new ToolCall { Id = "call_" + name, Name = name, ArgumentsJson = argumentsJson };
        }

        private sealed class MutableClock : IClock
        {
            public MutableClock(DateTimeOffset now)
            {
                UtcNow = now.ToUniversalTime();
            }

            public DateTimeOffset UtcNow { get; set; }

            public DateTimeOffset Now
            {
                get { return UtcNow.ToLocalTime(); }
            }
        }
    }
}
