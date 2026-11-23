using System;
using System.Collections.Generic;
using System.Globalization;

namespace MiaoWu.Core.Agents.Schedules
{
    /// <summary>任务在列表里的三态。</summary>
    public enum ScheduleStatus
    {
        /// <summary>已开启，到点会投递。</summary>
        Active = 0,

        /// <summary>已暂停，保留下一次时刻，但不投递。</summary>
        Paused = 1,

        /// <summary>规则已耗尽或一次性任务已跑完。</summary>
        Ended = 2
    }

    /// <summary>一次投递是到点触发还是用户点了「立即执行」。</summary>
    public enum ScheduleDeliveryKind
    {
        Automatic = 0,
        Manual = 1
    }

    /// <summary>
    /// 一次投递的记录。只证明消息已排进会话（或投递失败），不代表模型执行成功；
    /// <see cref="Prompt"/> 是当时的不可变快照，之后改任务内容不会回填旧记录。
    /// </summary>
    public sealed class ScheduleDeliveryRecord
    {
        public DateTimeOffset DeliveredAt { get; set; }

        public string Prompt { get; set; }

        public ScheduleDeliveryKind Kind { get; set; }

        /// <summary>投递委托报告失败时为 true；任务本身仍会推进下一次。</summary>
        public bool Failed { get; set; }
    }

    /// <summary>定时任务投递到哪里。</summary>
    public enum ScheduleTarget
    {
        /// <summary>投递到创建它的会话；会话不在了就跳过。</summary>
        ThisSession = 0,

        /// <summary>第一次投递时新建会话，之后复用同一个会话。</summary>
        NewOnce = 1,

        /// <summary>每次投递都新建会话。</summary>
        New = 2
    }

    /// <summary>一次定时任务的持久化状态。</summary>
    public sealed class ScheduleDefinition
    {
        public string Id { get; set; }

        public string Title { get; set; }

        /// <summary>到点时作为用户消息投递的正文。</summary>
        public string Prompt { get; set; }

        /// <summary>原始规则文本（DTSTART + RRULE 两行）。</summary>
        public string Rule { get; set; }

        public ScheduleTarget Target { get; set; }

        /// <summary>创建时的会话；NewOnce 复用它，New 每次覆盖。</summary>
        public string SessionId { get; set; }

        public string WorkspaceId { get; set; }

        public bool Active { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? LastRunAt { get; set; }

        /// <summary>下一次到点时间；为空表示没有后续。</summary>
        public DateTimeOffset? NextRunAt { get; set; }

        public int RunCount { get; set; }

        /// <summary>最近若干次投递记录，按发生顺序排列；旧文件没有该字段时为空。</summary>
        public List<ScheduleDeliveryRecord> History { get; set; }

        /// <summary>由 <see cref="Active"/> 与 <see cref="NextRunAt"/> 推出的三态。</summary>
        public ScheduleStatus Status
        {
            get
            {
                if (Active)
                {
                    return ScheduleStatus.Active;
                }

                return NextRunAt.HasValue ? ScheduleStatus.Paused : ScheduleStatus.Ended;
            }
        }
    }

    /// <summary>编辑结果：更新成功、任务不存在、或 <c>expectedUpdatedAt</c> 与当前版本不一致。</summary>
    public enum ScheduleUpdateStatus
    {
        Updated = 0,
        NotFound = 1,
        Conflict = 2
    }

    /// <summary>一次编辑的结果；冲突时不做任何写入。</summary>
    public sealed class ScheduleUpdateResult
    {
        public ScheduleUpdateResult(ScheduleUpdateStatus status, ScheduleDefinition definition)
        {
            Status = status;
            Definition = definition;
        }

        public ScheduleUpdateStatus Status { get; }

        /// <summary>更新成功时是写入后的任务快照；其它情况为 null。</summary>
        public ScheduleDefinition Definition { get; }
    }

    /// <summary>规则、存储或参数错误；工具层把它转成结构化失败结果。</summary>
    public sealed class ScheduleException : Exception
    {
        public ScheduleException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// 定时任务：创建 / 编辑 / 查询。投递由宿主实现（进程内定时器 + 会话入队），
    /// 这里只约定契约，不引用运行时。
    /// </summary>
    public interface IScheduleService
    {
        /// <summary>任一任务增删改后触发。</summary>
        event EventHandler Changed;

        ScheduleDefinition Create(
            string title,
            string prompt,
            string rule,
            ScheduleTarget target,
            string sessionId,
            string workspaceId);

        /// <summary>
        /// 编辑；传 null 表示不改该字段。带 <paramref name="expectedUpdatedAt"/> 时按 CAS 校验，
        /// 与当前版本不一致返回 <see cref="ScheduleUpdateStatus.Conflict"/> 且不写入。
        /// </summary>
        ScheduleUpdateResult Update(
            string id,
            string title,
            string prompt,
            string rule,
            bool? active,
            DateTimeOffset? expectedUpdatedAt = null,
            ScheduleTarget? target = null,
            string sessionId = null,
            string workspaceId = null);

        /// <summary>删除任务及其投递记录；已排进会话的消息不撤回。</summary>
        bool Delete(string id);

        /// <summary>立即投递一次，但不移动下一次时刻；任务不存在时返回 false。</summary>
        bool RunNow(string id);

        /// <summary>
        /// 记下 NewOnce 首次投递创建的会话 id；之后同一任务复用它。
        /// </summary>
        bool UpdateSession(string id, string sessionId);

        IReadOnlyList<ScheduleDefinition> List(bool includeInactive);

        ScheduleDefinition Get(string id);
    }

    /// <summary>
    /// RFC 5545 子集：DTSTART 一行 + RRULE 一行，支持 DAILY / WEEKLY / MONTHLY，
    /// INTERVAL、BYDAY（周几）、BYHOUR、BYMINUTE、COUNT、UNTIL。按本地时间触发。
    /// </summary>
    public sealed class ScheduleRecurrence
    {
        public const int MaxCount = 1000;
        private const int MaxScanPeriods = 4000;

        private ScheduleRecurrence(
            DateTimeOffset start,
            string frequency,
            int interval,
            IReadOnlyList<DayOfWeek> byDay,
            int hour,
            int minute,
            int? count,
            DateTimeOffset? until)
        {
            Start = start;
            Frequency = frequency;
            Interval = interval;
            ByDay = byDay;
            Hour = hour;
            Minute = minute;
            Count = count;
            Until = until;
        }

        public DateTimeOffset Start { get; }

        /// <summary>DAILY / WEEKLY / MONTHLY。</summary>
        public string Frequency { get; }

        public int Interval { get; }

        public IReadOnlyList<DayOfWeek> ByDay { get; }

        public int Hour { get; }

        public int Minute { get; }

        public int? Count { get; }

        public DateTimeOffset? Until { get; }

        public static ScheduleRecurrence Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ScheduleException("invalid_rrule", "规则不能为空；需要 DTSTART 一行加 RRULE 一行。");
            }

            string startText = null;
            string ruleText = null;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("DTSTART", StringComparison.OrdinalIgnoreCase))
                {
                    startText = line;
                }
                else if (line.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
                {
                    ruleText = line;
                }
                else
                {
                    throw new ScheduleException("invalid_rrule", "无法识别的规则行：" + line);
                }
            }

            if (startText == null)
            {
                throw new ScheduleException("invalid_rrule", "缺少 DTSTART。");
            }

            if (ruleText == null)
            {
                throw new ScheduleException("invalid_rrule", "缺少 RRULE。");
            }

            DateTimeOffset start = ParseStart(startText);
            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string body = ruleText.Substring(ruleText.IndexOf(':') + 1);
            string[] segments = body.Split(';');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i].Trim();
                if (segment.Length == 0)
                {
                    continue;
                }

                int equals = segment.IndexOf('=');
                if (equals <= 0 || equals == segment.Length - 1)
                {
                    throw new ScheduleException("invalid_rrule", "RRULE 片段格式不对：" + segment);
                }

                string key = segment.Substring(0, equals).Trim().ToUpperInvariant();
                string value = segment.Substring(equals + 1).Trim().ToUpperInvariant();
                if (parts.ContainsKey(key))
                {
                    throw new ScheduleException("invalid_rrule", "RRULE 字段重复：" + key);
                }

                parts[key] = value;
            }

            string frequency;
            if (!parts.TryGetValue("FREQ", out frequency))
            {
                throw new ScheduleException("invalid_rrule", "缺少 FREQ。");
            }

            if (frequency != "DAILY" && frequency != "WEEKLY" && frequency != "MONTHLY")
            {
                throw new ScheduleException(
                    "invalid_rrule",
                    "只支持 DAILY / WEEKLY / MONTHLY，收到：" + frequency + "。");
            }

            foreach (string key in parts.Keys)
            {
                if (key != "FREQ" && key != "INTERVAL" && key != "BYDAY" && key != "BYHOUR"
                    && key != "BYMINUTE" && key != "COUNT" && key != "UNTIL")
                {
                    throw new ScheduleException(
                        "invalid_rrule",
                        "不支持的 RRULE 字段：" + key + "（只支持 INTERVAL / BYDAY / BYHOUR / BYMINUTE / COUNT / UNTIL）。");
                }
            }

            int interval = ParseInt(parts, "INTERVAL", 1, 1, 365);
            int? count = parts.ContainsKey("COUNT") ? ParseInt(parts, "COUNT", 1, 1, MaxCount) : (int?)null;
            int hour = parts.ContainsKey("BYHOUR") ? ParseInt(parts, "BYHOUR", 0, 0, 23) : start.Hour;
            int minute = parts.ContainsKey("BYMINUTE") ? ParseInt(parts, "BYMINUTE", 0, 0, 59) : start.Minute;

            var byDay = new List<DayOfWeek>();
            if (parts.ContainsKey("BYDAY"))
            {
                if (frequency != "WEEKLY")
                {
                    throw new ScheduleException("invalid_rrule", "BYDAY 只用于 WEEKLY。");
                }

                string[] days = parts["BYDAY"].Split(',');
                for (int i = 0; i < days.Length; i++)
                {
                    DayOfWeek day;
                    if (!TryParseDay(days[i], out day))
                    {
                        throw new ScheduleException("invalid_rrule", "无法识别的 BYDAY：" + days[i]);
                    }

                    if (!byDay.Contains(day))
                    {
                        byDay.Add(day);
                    }
                }
            }
            else if (frequency == "WEEKLY")
            {
                byDay.Add(start.DayOfWeek);
            }

            DateTimeOffset? until = null;
            if (parts.ContainsKey("UNTIL"))
            {
                string raw = parts["UNTIL"];
                DateTimeOffset parsed;
                if (!TryParseIcsTime(raw, out parsed))
                {
                    throw new ScheduleException("invalid_rrule", "无法识别的 UNTIL：" + raw);
                }

                if (parsed < start)
                {
                    throw new ScheduleException("invalid_rrule", "UNTIL 不能早于 DTSTART。");
                }

                until = parsed;
            }

            return new ScheduleRecurrence(start, frequency, interval, byDay, hour, minute, count, until);
        }

        /// <summary>严格晚于 <paramref name="after"/> 的下一次触发；没有后续时返回 null。</summary>
        public DateTimeOffset? NextAfter(DateTimeOffset after)
        {
            return Find(after, false);
        }

        /// <summary>不早于 <paramref name="after"/> 的下一次触发；用于“从现在开始”的第一次。</summary>
        public DateTimeOffset? FirstOnOrAfter(DateTimeOffset after)
        {
            return Find(after, true);
        }

        private DateTimeOffset? Find(DateTimeOffset after, bool inclusive)
        {
            int index = 0;
            foreach (DateTimeOffset occurrence in Enumerate())
            {
                index++;
                if (index > MaxCount)
                {
                    break;
                }

                if (inclusive ? occurrence >= after : occurrence > after)
                {
                    return occurrence;
                }
            }

            return null;
        }

        private IEnumerable<DateTimeOffset> Enumerate()
        {
            int produced = 0;
            int periods = 0;
            while (periods++ < MaxScanPeriods)
            {
                List<DateTimeOffset> batch = Period(periods - 1);
                for (int i = 0; i < batch.Count; i++)
                {
                    DateTimeOffset occurrence = batch[i];
                    if (occurrence < Start)
                    {
                        continue;
                    }

                    if (Until.HasValue && occurrence > Until.Value)
                    {
                        yield break;
                    }

                    yield return occurrence;
                    produced++;
                    if (Count.HasValue && produced >= Count.Value)
                    {
                        yield break;
                    }
                }
            }
        }

        private List<DateTimeOffset> Period(int periodIndex)
        {
            var list = new List<DateTimeOffset>();
            if (Frequency == "DAILY")
            {
                DateTime date = Start.Date.AddDays(periodIndex * Interval);
                list.Add(At(date));
                return list;
            }

            if (Frequency == "WEEKLY")
            {
                DateTime weekStart = Start.Date.AddDays(-(((int)Start.DayOfWeek + 6) % 7))
                    .AddDays(periodIndex * 7 * Interval);
                for (int offset = 0; offset < 7; offset++)
                {
                    DateTime date = weekStart.AddDays(offset);
                    if (ContainsDay(ByDay, date.DayOfWeek))
                    {
                        list.Add(At(date));
                    }
                }

                return list;
            }

            DateTime firstOfMonth = new DateTime(Start.Year, Start.Month, 1).AddMonths(periodIndex * Interval);
            int day = Math.Min(Start.Day, DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month));
            list.Add(At(firstOfMonth.AddDays(day - 1)));
            return list;
        }

        private DateTimeOffset At(DateTime date)
        {
            DateTime local = new DateTime(date.Year, date.Month, date.Day, Hour, Minute, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, Start.Offset);
        }

        private static bool ContainsDay(IReadOnlyList<DayOfWeek> days, DayOfWeek day)
        {
            for (int i = 0; i < days.Count; i++)
            {
                if (days[i] == day)
                {
                    return true;
                }
            }

            return false;
        }

        private static DateTimeOffset ParseStart(string line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                throw new ScheduleException("invalid_rrule", "DTSTART 缺少时间：`DTSTART:20260101T090000`。");
            }

            string raw = line.Substring(colon + 1).Trim();
            DateTimeOffset parsed;
            if (!TryParseIcsTime(raw, out parsed))
            {
                throw new ScheduleException("invalid_rrule", "无法识别 DTSTART 时间：" + raw);
            }

            return parsed.ToLocalTime();
        }

        private static bool TryParseIcsTime(string raw, out DateTimeOffset value)
        {
            value = default(DateTimeOffset);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            bool utc = raw.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
            string body = utc ? raw.Substring(0, raw.Length - 1) : raw;
            string[] formats =
            {
                "yyyyMMdd'T'HHmmss",
                "yyyyMMdd'T'HHmm",
                "yyyy-MM-dd'T'HH:mm:ss",
                "yyyy-MM-dd'T'HH:mm"
            };

            DateTime parsed;
            if (DateTime.TryParseExact(
                body,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed))
            {
                value = utc
                    ? new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc))
                    : new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
                return true;
            }

            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            {
                return true;
            }

            return false;
        }

        private static int ParseInt(
            IDictionary<string, string> parts,
            string key,
            int fallback,
            int min,
            int max)
        {
            string raw;
            if (!parts.TryGetValue(key, out raw))
            {
                return fallback;
            }

            int value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                || value < min
                || value > max)
            {
                throw new ScheduleException(
                    "invalid_rrule",
                    key + " 必须是 " + min + "–" + max + " 的整数，收到：" + raw + "。");
            }

            return value;
        }

        private static bool TryParseDay(string raw, out DayOfWeek day)
        {
            switch ((raw ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "MO": day = DayOfWeek.Monday; return true;
                case "TU": day = DayOfWeek.Tuesday; return true;
                case "WE": day = DayOfWeek.Wednesday; return true;
                case "TH": day = DayOfWeek.Thursday; return true;
                case "FR": day = DayOfWeek.Friday; return true;
                case "SA": day = DayOfWeek.Saturday; return true;
                case "SU": day = DayOfWeek.Sunday; return true;
                default: day = DayOfWeek.Monday; return false;
            }
        }

        /// <summary>人读摘要，用于日程列表与工具返回。</summary>
        public string Describe()
        {
            string span = Frequency == "DAILY"
                ? (Interval == 1 ? "每天" : "每 " + Interval + " 天")
                : Frequency == "WEEKLY"
                    ? (Interval == 1 ? "每周" : "每 " + Interval + " 周")
                    : (Interval == 1 ? "每月" : "每 " + Interval + " 个月");

            string time = Hour.ToString("00", CultureInfo.InvariantCulture)
                + ":" + Minute.ToString("00", CultureInfo.InvariantCulture);
            if (Frequency == "WEEKLY")
            {
                var names = new List<string>();
                string[] week = { "日", "一", "二", "三", "四", "五", "六" };
                for (int i = 0; i < ByDay.Count; i++)
                {
                    names.Add("周" + week[(int)ByDay[i]]);
                }

                if (names.Count > 0)
                {
                    span += " " + string.Join("、", names.ToArray());
                }
            }

            span += " " + time;
            if (Count.HasValue)
            {
                span += "，共 " + Count.Value + " 次";
            }
            else if (Until.HasValue)
            {
                span += "，到 " + Until.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }

            return span;
        }
    }
}
