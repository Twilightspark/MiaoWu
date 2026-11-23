using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MiaoWu.Core.Agents.Schedules;
using MiaoWu.Infrastructure.Json;
using MiaoWu.Infrastructure.Storage;

namespace MiaoWu.Infrastructure.Tools
{
    /// <summary>
    /// 定时任务的 JSON 存储：<c>&lt;数据目录&gt;\schedules.json</c>，整体原子覆盖写。
    /// 损坏时备份后按空列表启动，不阻断应用启动。
    /// </summary>
    public sealed class ScheduleFileStore
    {
        private readonly string _filePath;

        public ScheduleFileStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("日程文件路径不能为空。", nameof(filePath));
            }

            _filePath = filePath;
        }

        public string FilePath
        {
            get { return _filePath; }
        }

        public IReadOnlyList<ScheduleDefinition> LoadAll()
        {
            var list = new List<ScheduleDefinition>();
            if (!File.Exists(_filePath))
            {
                return list;
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(AtomicFile.ReadAllText(_filePath));
            }
            catch (Exception)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                return list;
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                return list;
            }

            JsonValue schedules = root.Get("schedules");
            if (schedules == null || schedules.Kind != JsonKind.Array)
            {
                return list;
            }

            foreach (JsonValue item in schedules.Items)
            {
                ScheduleDefinition definition = ReadDefinition(item);
                if (definition != null)
                {
                    list.Add(definition);
                }
            }

            return list;
        }

        public void SaveAll(IReadOnlyList<ScheduleDefinition> schedules)
        {
            var root = JsonValue.NewObject();
            JsonValue array = JsonValue.NewArray();
            if (schedules != null)
            {
                for (int i = 0; i < schedules.Count; i++)
                {
                    if (schedules[i] != null)
                    {
                        array.Add(WriteDefinition(schedules[i]));
                    }
                }
            }

            root["schedules"] = array;
            AtomicFile.WriteAllText(_filePath, JsonWriter.WriteIndented(root));
        }

        private static ScheduleDefinition ReadDefinition(JsonValue item)
        {
            if (item == null || item.Kind != JsonKind.Object)
            {
                return null;
            }

            string id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            ScheduleTarget target;
            if (!Enum.TryParse(GetString(item, "target"), true, out target))
            {
                target = ScheduleTarget.ThisSession;
            }

            DateTimeOffset created;
            if (!TryGetTime(item, "createdAt", out created))
            {
                created = DateTimeOffset.Now;
            }

            DateTimeOffset updated;
            if (!TryGetTime(item, "updatedAt", out updated))
            {
                updated = created;
            }

            DateTimeOffset? lastRun = null;
            DateTimeOffset value;
            if (TryGetTime(item, "lastRunAt", out value))
            {
                lastRun = value;
            }

            DateTimeOffset? nextRun = null;
            if (TryGetTime(item, "nextRunAt", out value))
            {
                nextRun = value;
            }

            return new ScheduleDefinition
            {
                Id = id,
                Title = GetString(item, "title") ?? string.Empty,
                Prompt = GetString(item, "prompt") ?? string.Empty,
                Rule = GetString(item, "rule") ?? string.Empty,
                Target = target,
                SessionId = GetString(item, "sessionId"),
                WorkspaceId = GetString(item, "workspaceId"),
                Active = GetBool(item, "active", true),
                CreatedAt = created,
                UpdatedAt = updated,
                LastRunAt = lastRun,
                NextRunAt = nextRun,
                RunCount = GetInt(item, "runCount", 0),
                History = ReadHistory(item)
            };
        }

        private static List<ScheduleDeliveryRecord> ReadHistory(JsonValue item)
        {
            var history = new List<ScheduleDeliveryRecord>();
            JsonValue array = item.Get("history");
            if (array == null || array.Kind != JsonKind.Array)
            {
                return history;
            }

            foreach (JsonValue entry in array.Items)
            {
                if (entry == null || entry.Kind != JsonKind.Object)
                {
                    continue;
                }

                DateTimeOffset at;
                if (!TryGetTime(entry, "at", out at))
                {
                    continue;
                }

                ScheduleDeliveryKind kind;
                if (!Enum.TryParse(GetString(entry, "kind"), true, out kind))
                {
                    kind = ScheduleDeliveryKind.Automatic;
                }

                history.Add(new ScheduleDeliveryRecord
                {
                    DeliveredAt = at,
                    Prompt = GetString(entry, "prompt") ?? string.Empty,
                    Kind = kind,
                    Failed = GetBool(entry, "failed", false)
                });
            }

            return history;
        }

        private static JsonValue WriteDefinition(ScheduleDefinition schedule)
        {
            var item = JsonValue.NewObject();
            item["id"] = JsonValue.From(schedule.Id);
            item["title"] = JsonValue.From(schedule.Title);
            item["prompt"] = JsonValue.From(schedule.Prompt);
            item["rule"] = JsonValue.From(schedule.Rule);
            item["target"] = JsonValue.From(schedule.Target.ToString());
            item["sessionId"] = JsonValue.From(schedule.SessionId);
            item["workspaceId"] = JsonValue.From(schedule.WorkspaceId);
            item["active"] = JsonValue.From(schedule.Active);
            item["createdAt"] = JsonValue.From(FormatTime(schedule.CreatedAt));
            item["updatedAt"] = JsonValue.From(FormatTime(schedule.UpdatedAt));
            item["lastRunAt"] = JsonValue.From(schedule.LastRunAt.HasValue ? FormatTime(schedule.LastRunAt.Value) : null);
            item["nextRunAt"] = JsonValue.From(schedule.NextRunAt.HasValue ? FormatTime(schedule.NextRunAt.Value) : null);
            item["runCount"] = JsonValue.From((double)schedule.RunCount);

            JsonValue history = JsonValue.NewArray();
            if (schedule.History != null)
            {
                for (int i = 0; i < schedule.History.Count; i++)
                {
                    ScheduleDeliveryRecord record = schedule.History[i];
                    if (record == null)
                    {
                        continue;
                    }

                    JsonValue entry = JsonValue.NewObject();
                    entry["at"] = JsonValue.From(FormatTime(record.DeliveredAt));
                    entry["prompt"] = JsonValue.From(record.Prompt);
                    entry["kind"] = JsonValue.From(record.Kind.ToString());
                    entry["failed"] = JsonValue.From(record.Failed);
                    history.Add(entry);
                }
            }

            if (history.Items.Count > 0)
            {
                item["history"] = history;
            }

            return item;
        }

        private static string GetString(JsonValue root, string key)
        {
            JsonValue value = root.Get(key);
            return value == null ? null : value.AsString();
        }

        private static bool GetBool(JsonValue root, string key, bool fallback)
        {
            JsonValue value = root.Get(key);
            return value == null ? fallback : value.AsBoolean(fallback);
        }

        private static int GetInt(JsonValue root, string key, int fallback)
        {
            JsonValue value = root.Get(key);
            return value == null ? fallback : value.AsInt(fallback);
        }

        private static bool TryGetTime(JsonValue root, string key, out DateTimeOffset value)
        {
            value = default(DateTimeOffset);
            string text = GetString(root, key);
            return !string.IsNullOrWhiteSpace(text)
                && DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out value);
        }

        private static string FormatTime(DateTimeOffset value)
        {
            return value.ToString("o", CultureInfo.InvariantCulture);
        }
    }
}
