using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MiaoWu.Core.Agents.Schedules
{
    /// <summary>编辑器里的重复方式。</summary>
    public enum ScheduleRepeatMode
    {
        /// <summary>仅一次（<c>FREQ=DAILY;COUNT=1</c>）。</summary>
        Once = 0,

        /// <summary>每 N 天。</summary>
        Daily = 1,

        /// <summary>每 N 周的指定星期。</summary>
        Weekly = 2,

        /// <summary>每 N 月。</summary>
        Monthly = 3,

        /// <summary>自定义规则原文，原样保存。</summary>
        Custom = 4
    }

    /// <summary>
    /// 表单化的定时规则：把「重复方式 + 时间 + 间隔 + 星期」生成为
    /// <see cref="ScheduleRecurrence"/> 能解析的 <c>DTSTART</c> + <c>RRULE</c> 两行，
    /// 也能把已存规则反解回表单。纯逻辑，便于 xUnit 直接覆盖。
    /// </summary>
    public sealed class ScheduleRuleDraft
    {
        private static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

        public ScheduleRepeatMode Mode { get; set; }

        /// <summary>本地钟点 0–23。</summary>
        public int Hour { get; set; }

        /// <summary>本地分钟 0–59。</summary>
        public int Minute { get; set; }

        /// <summary>DAILY / WEEKLY / MONTHLY 的 INTERVAL，1–365。</summary>
        public int Interval { get; set; }

        /// <summary>WEEKLY 选中的星期；至少一天。</summary>
        public IList<DayOfWeek> Weekdays { get; set; }

        /// <summary>自定义规则原文（含 DTSTART 与 RRULE 两行）。</summary>
        public string CustomRule { get; set; }

        public ScheduleRuleDraft()
        {
            Mode = ScheduleRepeatMode.Daily;
            Hour = 9;
            Minute = 0;
            Interval = 1;
            Weekdays = new List<DayOfWeek>();
        }

        /// <summary>生成可持久化的规则文本；参数不合法或没有未来发生时抛 <see cref="ScheduleException"/>。</summary>
        public string Build(DateTimeOffset now)
        {
            if (Mode == ScheduleRepeatMode.Custom)
            {
                string custom = (CustomRule ?? string.Empty).Trim();
                ScheduleRecurrence.Parse(custom);
                return custom;
            }

            if (Hour < 0 || Hour > 23)
            {
                throw new ScheduleException("invalid_time", "小时必须是 0–23。");
            }

            if (Minute < 0 || Minute > 59)
            {
                throw new ScheduleException("invalid_time", "分钟必须是 0–59。");
            }

            if (Interval < 1 || Interval > 365)
            {
                throw new ScheduleException("invalid_interval", "间隔必须是 1–365 之间的整数。");
            }

            if (Mode == ScheduleRepeatMode.Weekly && (Weekdays == null || Weekdays.Count == 0))
            {
                throw new ScheduleException("invalid_weekdays", "每周至少选择一天。");
            }

            DateTimeOffset start = FirstOccurrence(now);
            var builder = new StringBuilder();
            builder.Append("DTSTART:")
                .Append(start.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture))
                .Append('\n');
            builder.Append("RRULE:");

            switch (Mode)
            {
                case ScheduleRepeatMode.Once:
                    builder.Append("FREQ=DAILY;COUNT=1");
                    break;
                case ScheduleRepeatMode.Daily:
                    AppendFrequency(builder, "DAILY");
                    break;
                case ScheduleRepeatMode.Weekly:
                    AppendFrequency(builder, "WEEKLY");
                    builder.Append(";BYDAY=").Append(FormatWeekdays());
                    break;
                case ScheduleRepeatMode.Monthly:
                    AppendFrequency(builder, "MONTHLY");
                    break;
            }

            builder.Append(";BYHOUR=").Append(Hour.ToString(CultureInfo.InvariantCulture));
            builder.Append(";BYMINUTE=").Append(Minute.ToString(CultureInfo.InvariantCulture));

            string rule = builder.ToString();
            ScheduleRecurrence.Parse(rule);
            return rule;
        }

        /// <summary>把已存规则尽量还原成表单；控件表达不了的（COUNT/UNTIL 等）退回自定义原文。</summary>
        public static ScheduleRuleDraft FromRule(string rule, DateTimeOffset now)
        {
            var draft = new ScheduleRuleDraft();
            string text = (rule ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return draft;
            }

            ScheduleRecurrence recurrence;
            try
            {
                recurrence = ScheduleRecurrence.Parse(text);
            }
            catch (ScheduleException)
            {
                draft.Mode = ScheduleRepeatMode.Custom;
                draft.CustomRule = text;
                return draft;
            }

            draft.Hour = recurrence.Hour;
            draft.Minute = recurrence.Minute;
            draft.Interval = recurrence.Interval;
            draft.CustomRule = text;
            draft.Weekdays = new List<DayOfWeek>(recurrence.ByDay);

            bool repeating = !recurrence.Count.HasValue && !recurrence.Until.HasValue;
            if (repeating && recurrence.Frequency == "DAILY")
            {
                draft.Mode = ScheduleRepeatMode.Daily;
            }
            else if (repeating && recurrence.Frequency == "WEEKLY")
            {
                draft.Mode = ScheduleRepeatMode.Weekly;
            }
            else if (repeating && recurrence.Frequency == "MONTHLY")
            {
                draft.Mode = ScheduleRepeatMode.Monthly;
            }
            else if (recurrence.Frequency == "DAILY"
                && recurrence.Count.HasValue
                && recurrence.Count.Value == 1)
            {
                draft.Mode = ScheduleRepeatMode.Once;
            }
            else
            {
                // 带 COUNT/UNTIL 的规则仍然合法，只是表单表达不全：保留原文，按自定义编辑。
                draft.Mode = ScheduleRepeatMode.Custom;
            }

            return draft;
        }

        /// <summary>表单的下一步摘要；真实下一次由已保存规则决定，这里只用于编辑时预览。</summary>
        public string Describe()
        {
            if (Mode == ScheduleRepeatMode.Custom)
            {
                return "自定义规则";
            }

            string time = Hour.ToString("00", CultureInfo.InvariantCulture)
                + ":" + Minute.ToString("00", CultureInfo.InvariantCulture);
            switch (Mode)
            {
                case ScheduleRepeatMode.Once:
                    return "仅一次 " + time;
                case ScheduleRepeatMode.Weekly:
                    return (Interval == 1 ? "每周" : "每 " + Interval + " 周")
                        + " " + FormatWeekdayNames() + " " + time;
                case ScheduleRepeatMode.Monthly:
                    return (Interval == 1 ? "每月" : "每 " + Interval + " 个月") + " " + time;
                default:
                    return (Interval == 1 ? "每天" : "每 " + Interval + " 天") + " " + time;
            }
        }

        /// <summary>深比较，用于判断表单是否有改动。</summary>
        public bool SameAs(ScheduleRuleDraft other)
        {
            if (other == null)
            {
                return false;
            }

            if (Mode != other.Mode
                || Hour != other.Hour
                || Minute != other.Minute
                || (Mode != ScheduleRepeatMode.Custom && Interval != other.Interval))
            {
                return false;
            }

            if (Mode == ScheduleRepeatMode.Custom)
            {
                return string.Equals(
                    (CustomRule ?? string.Empty).Trim(),
                    (other.CustomRule ?? string.Empty).Trim(),
                    StringComparison.Ordinal);
            }

            return SameWeekdays(Weekdays, other.Weekdays);
        }

        private DateTimeOffset FirstOccurrence(DateTimeOffset now)
        {
            if (Mode == ScheduleRepeatMode.Weekly)
            {
                for (int offset = 0; offset <= 7; offset++)
                {
                    DateTime date = now.Date.AddDays(offset);
                    if (!ContainsDay(Weekdays, date.DayOfWeek))
                    {
                        continue;
                    }

                    DateTimeOffset candidate = At(date, now.Offset);
                    if (candidate > now)
                    {
                        return candidate;
                    }
                }

                throw new ScheduleException("invalid_weekdays", "每周规则找不到未来的运行时间。");
            }

            if (Mode == ScheduleRepeatMode.Monthly)
            {
                DateTime thisMonth = new DateTime(now.Year, now.Month, 1);
                DateTimeOffset candidate = At(ClampDay(thisMonth, now), now.Offset);
                if (candidate > now)
                {
                    return candidate;
                }

                DateTime nextMonth = thisMonth.AddMonths(1);
                return At(ClampDay(nextMonth, now), now.Offset);
            }

            // 仅一次与每天：当天该钟点还没到就是今天，否则明天。
            DateTimeOffset first = At(now.Date, now.Offset);
            return first > now ? first : At(now.Date.AddDays(1), now.Offset);
        }

        private DateTimeOffset At(DateTime date, TimeSpan offset)
        {
            var local = new DateTime(date.Year, date.Month, date.Day, Hour, Minute, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, offset);
        }

        private static DateTime ClampDay(DateTime firstOfMonth, DateTimeOffset now)
        {
            int day = Math.Min(now.Day, DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month));
            return firstOfMonth.AddDays(day - 1);
        }

        private void AppendFrequency(StringBuilder builder, string frequency)
        {
            builder.Append("FREQ=").Append(frequency);
            builder.Append(";INTERVAL=").Append(Interval.ToString(CultureInfo.InvariantCulture));
        }

        private string FormatWeekdays()
        {
            var parts = new List<string>();
            for (int i = 0; i < DayCodes.Length; i++)
            {
                var day = (DayOfWeek)i;
                if (ContainsDay(Weekdays, day))
                {
                    parts.Add(DayCodes[i]);
                }
            }

            return string.Join(",", parts.ToArray());
        }

        private string FormatWeekdayNames()
        {
            string[] names = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            var parts = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                if (ContainsDay(Weekdays, (DayOfWeek)i))
                {
                    parts.Add(names[i]);
                }
            }

            return string.Join("、", parts.ToArray());
        }

        private static bool ContainsDay(IList<DayOfWeek> days, DayOfWeek day)
        {
            if (days == null)
            {
                return false;
            }

            for (int i = 0; i < days.Count; i++)
            {
                if (days[i] == day)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameWeekdays(IList<DayOfWeek> left, IList<DayOfWeek> right)
        {
            int leftCount = left == null ? 0 : left.Count;
            int rightCount = right == null ? 0 : right.Count;
            if (leftCount != rightCount)
            {
                return false;
            }

            for (int day = 0; day < 7; day++)
            {
                if (ContainsDay(left, (DayOfWeek)day) != ContainsDay(right, (DayOfWeek)day))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
