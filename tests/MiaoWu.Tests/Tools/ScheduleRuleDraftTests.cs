using System;
using System.Collections.Generic;
using MiaoWu.Core.Agents.Schedules;
using Xunit;

namespace MiaoWu.Tests.Tools
{
    /// <summary>表单规则草稿：生成 DTSTART/RRULE、反解已存规则、等价比较。</summary>
    public sealed class ScheduleRuleDraftTests
    {
        private static readonly DateTimeOffset Now =
            new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.FromHours(8));

        [Fact]
        public void DailyBuildsNextLocalOccurrence()
        {
            var draft = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Daily,
                Hour = 9,
                Minute = 0,
                Interval = 1
            };

            string rule = draft.Build(Now);

            Assert.Contains("DTSTART:20261121T090000", rule);
            Assert.Contains("FREQ=DAILY", rule);
            Assert.Contains("INTERVAL=1", rule);
            Assert.Contains("BYHOUR=9", rule);
            Assert.Contains("BYMINUTE=0", rule);
            Assert.Equal(
                new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.FromHours(8)),
                ScheduleRecurrence.Parse(rule).FirstOnOrAfter(Now));
        }

        [Fact]
        public void DailyPastTimeMovesToTomorrow()
        {
            var draft = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Daily,
                Hour = 7,
                Minute = 30,
                Interval = 2
            };

            string rule = draft.Build(Now);

            Assert.Contains("DTSTART:20261122T073000", rule);
            Assert.Contains("INTERVAL=2", rule);
        }

        [Fact]
        public void WeeklyUsesSelectedWeekdays()
        {
            var draft = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Weekly,
                Hour = 9,
                Minute = 0,
                Interval = 1,
                Weekdays = new List<DayOfWeek> { DayOfWeek.Wednesday, DayOfWeek.Monday }
            };

            string rule = draft.Build(Now);

            // 2026-11-21 是周六，下周一 11-23。
            Assert.Contains("DTSTART:20261123T090000", rule);
            Assert.Contains("BYDAY=", rule);
            Assert.Contains("MO", rule);
            Assert.Contains("WE", rule);
            DateTimeOffset? next = ScheduleRecurrence.Parse(rule).FirstOnOrAfter(Now);
            Assert.Equal(DayOfWeek.Monday, next.Value.DayOfWeek);
        }

        [Fact]
        public void WeeklyRequiresAtLeastOneDay()
        {
            var draft = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Weekly,
                Hour = 9,
                Minute = 0,
                Interval = 1,
                Weekdays = new List<DayOfWeek>()
            };

            ScheduleException error = Assert.Throws<ScheduleException>(() => draft.Build(Now));
            Assert.Equal("invalid_weekdays", error.Code);
        }

        [Fact]
        public void MonthlyClampsToNextMonth()
        {
            var draft = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Monthly,
                Hour = 9,
                Minute = 0,
                Interval = 1
            };

            string rule = draft.Build(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.FromHours(8)));

            Assert.Contains("FREQ=MONTHLY", rule);
            Assert.Contains("DTSTART:20261221T090000", rule);
        }

        [Fact]
        public void OnceBuildsCountOne()
        {
            var draft = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Once,
                Hour = 9,
                Minute = 0
            };

            string rule = draft.Build(Now);

            Assert.Contains("FREQ=DAILY;COUNT=1", rule);
            ScheduleRecurrence recurrence = ScheduleRecurrence.Parse(rule);
            Assert.Equal(1, recurrence.Count);
            Assert.NotNull(recurrence.FirstOnOrAfter(Now));
        }

        [Fact]
        public void InvalidTimeAndIntervalAreRejected()
        {
            var badHour = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Daily,
                Hour = 24,
                Minute = 0
            };
            Assert.Equal("invalid_time", Assert.Throws<ScheduleException>(() => badHour.Build(Now)).Code);

            var badInterval = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Daily,
                Hour = 9,
                Minute = 0,
                Interval = 0
            };
            Assert.Equal(
                "invalid_interval",
                Assert.Throws<ScheduleException>(() => badInterval.Build(Now)).Code);
        }

        [Fact]
        public void CustomRuleIsValidatedAndPreserved()
        {
            string custom = "DTSTART:20261121T090000\nRRULE:FREQ=DAILY;COUNT=3";
            var draft = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Custom,
                CustomRule = custom
            };

            Assert.Equal(custom, draft.Build(Now));

            var invalid = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Custom,
                CustomRule = "RRULE:FREQ=DAILY"
            };
            Assert.Throws<ScheduleException>(() => invalid.Build(Now));
        }

        [Fact]
        public void FromRuleMapsSimpleRulesAndKeepsComplexOnesCustom()
        {
            ScheduleRuleDraft daily = ScheduleRuleDraft.FromRule(
                "DTSTART:20261121T090000\nRRULE:FREQ=DAILY;INTERVAL=3;BYHOUR=9;BYMINUTE=0",
                Now);
            Assert.Equal(ScheduleRepeatMode.Daily, daily.Mode);
            Assert.Equal(3, daily.Interval);
            Assert.Equal(9, daily.Hour);

            ScheduleRuleDraft once = ScheduleRuleDraft.FromRule(
                "DTSTART:20261121T090000\nRRULE:FREQ=DAILY;COUNT=1",
                Now);
            Assert.Equal(ScheduleRepeatMode.Once, once.Mode);

            ScheduleRuleDraft weekly = ScheduleRuleDraft.FromRule(
                "DTSTART:20261121T090000\nRRULE:FREQ=WEEKLY;BYDAY=MO,WE",
                Now);
            Assert.Equal(ScheduleRepeatMode.Weekly, weekly.Mode);
            Assert.Contains(DayOfWeek.Monday, weekly.Weekdays);
            Assert.Contains(DayOfWeek.Wednesday, weekly.Weekdays);

            ScheduleRuleDraft counted = ScheduleRuleDraft.FromRule(
                "DTSTART:20261121T090000\nRRULE:FREQ=DAILY;COUNT=3",
                Now);
            Assert.Equal(ScheduleRepeatMode.Custom, counted.Mode);
            Assert.Contains("COUNT=3", counted.CustomRule);

            ScheduleRuleDraft invalid = ScheduleRuleDraft.FromRule("不是规则", Now);
            Assert.Equal(ScheduleRepeatMode.Custom, invalid.Mode);
        }

        [Fact]
        public void SameAsIgnoresWeekdayOrder()
        {
            var left = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Weekly,
                Hour = 9,
                Minute = 0,
                Interval = 1,
                Weekdays = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Friday }
            };
            var right = new ScheduleRuleDraft
            {
                Mode = ScheduleRepeatMode.Weekly,
                Hour = 9,
                Minute = 0,
                Interval = 1,
                Weekdays = new List<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Monday }
            };

            Assert.True(left.SameAs(right));
            right.Hour = 10;
            Assert.False(left.SameAs(right));
        }
    }
}
