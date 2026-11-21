using System;
using System.Globalization;
using System.IO;
using MiaoWu.Core.Agents.Goals;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Storage
{
    /// <summary>
    /// 目标状态的文件实现：<c>&lt;workspace&gt;\.agent\goals\{sessionId}.json</c>，整体原子覆盖写。
    /// 文件缺失或损坏时返回 <c>null</c>（损坏文件先备份），不阻断会话。
    /// </summary>
    public sealed class GoalFileStore : IGoalPersistence
    {
        private readonly string _filePath;

        public GoalFileStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("目标状态文件路径不能为空。", nameof(filePath));
            }

            _filePath = filePath;
        }

        public string FilePath
        {
            get { return _filePath; }
        }

        public AgentGoal Load()
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(AtomicFile.ReadAllText(_filePath));
            }
            catch (Exception)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                return null;
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                return null;
            }

            GoalPhase phase;
            if (!GoalPhaseText.TryParse(GetString(root, "phase"), out phase))
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                return null;
            }

            return new AgentGoal
            {
                Id = GetString(root, "id"),
                Objective = GetString(root, "objective") ?? string.Empty,
                Phase = phase,
                Revision = GetLong(root, "revision", 1),
                RoundsStarted = GetInt(root, "roundsStarted", 0),
                MaxGoalRounds = GetInt(root, "maxGoalRounds", SessionGoalState.DefaultMaxGoalRounds),
                BlockedReason = GetString(root, "blockedReason"),
                CreatedAt = GetTime(root, "createdAt"),
                UpdatedAt = GetTime(root, "updatedAt")
            };
        }

        public void Save(AgentGoal goal)
        {
            if (goal == null)
            {
                return;
            }

            var root = JsonValue.NewObject();
            root["id"] = JsonValue.From(goal.Id);
            root["objective"] = JsonValue.From(goal.Objective);
            root["phase"] = JsonValue.From(GoalPhaseText.ToWire(goal.Phase));
            root["revision"] = JsonValue.From((double)goal.Revision);
            root["roundsStarted"] = JsonValue.From((double)goal.RoundsStarted);
            root["maxGoalRounds"] = JsonValue.From((double)goal.MaxGoalRounds);
            root["blockedReason"] = JsonValue.From(goal.BlockedReason);
            root["createdAt"] = JsonValue.From(FormatTime(goal.CreatedAt));
            root["updatedAt"] = JsonValue.From(FormatTime(goal.UpdatedAt));

            AtomicFile.WriteAllText(_filePath, JsonWriter.WriteIndented(root));
        }

        private static string GetString(JsonValue root, string key)
        {
            JsonValue value = root.Get(key);
            return value == null ? null : value.AsString();
        }

        private static int GetInt(JsonValue root, string key, int fallback)
        {
            JsonValue value = root.Get(key);
            return value == null ? fallback : value.AsInt(fallback);
        }

        private static long GetLong(JsonValue root, string key, long fallback)
        {
            JsonValue value = root.Get(key);
            return value == null ? fallback : value.AsLong(fallback);
        }

        private static DateTimeOffset GetTime(JsonValue root, string key)
        {
            string text = GetString(root, key);
            DateTimeOffset value;
            if (!string.IsNullOrWhiteSpace(text)
                && DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out value))
            {
                return value.ToLocalTime();
            }

            return DateTimeOffset.Now;
        }

        private static string FormatTime(DateTimeOffset value)
        {
            return value.ToString("o", CultureInfo.InvariantCulture);
        }
    }
}
