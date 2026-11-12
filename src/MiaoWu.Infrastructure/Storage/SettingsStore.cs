using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Documents;
using MiaoWu.Core.Settings;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Storage
{
    /// <summary>
    /// settings.json 存取：schemaVersion + ui 段；整体原子写。
    /// 未知字段通过保留原始 JsonValue 根对象继续写回，不静默丢弃。
    /// </summary>
    public sealed class SettingsStore : ISettingsStore
    {
        private readonly string _filePath;
        private JsonValue _root;

        public SettingsStore(IAppPaths paths)
            : this(paths == null ? throw new ArgumentNullException(nameof(paths)) : paths.SettingsFilePath)
        {
        }

        public SettingsStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("settings.json 路径不能为空。", nameof(filePath));
            }

            _filePath = filePath;
        }

        public string FilePath
        {
            get { return _filePath; }
        }

        public AppSettings Load()
        {
            if (!File.Exists(_filePath))
            {
                _root = null;
                return new AppSettings();
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(AtomicFile.ReadAllText(_filePath));
            }
            catch (Exception)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                _root = null;
                return new AppSettings();
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                _root = null;
                return new AppSettings();
            }

            _root = root;

            int schemaVersion = GetInt(root, "schemaVersion", AppSettings.CurrentSchemaVersion);
            if (schemaVersion > AppSettings.CurrentSchemaVersion)
            {
                // 不允许旧版本降级覆盖新版本配置，直接备份后使用默认值。
                AtomicFile.BackupCorrupt(_filePath, "unsupported");
                _root = null;
                return new AppSettings();
            }

            var settings = new AppSettings
            {
                SchemaVersion = schemaVersion <= 0 ? AppSettings.CurrentSchemaVersion : schemaVersion
            };

            JsonValue ui = root.Get("ui");
            if (ui != null && ui.Kind == JsonKind.Object)
            {
                UiSettings value = settings.Ui;
                value.WindowWidth = GetDouble(ui, "windowWidth", value.WindowWidth);
                value.WindowHeight = GetDouble(ui, "windowHeight", value.WindowHeight);
                value.WindowMaximized = GetBool(ui, "windowMaximized", false);
                value.LeftPaneWidth = GetDouble(ui, "leftPaneWidth", value.LeftPaneWidth);
                value.RightPaneWidth = GetDouble(ui, "rightPaneWidth", value.RightPaneWidth);
                value.IsRightPaneCollapsed = GetBool(ui, "rightPaneCollapsed", false);
                value.ActiveWorkspaceId = GetString(ui, "activeWorkspaceId");
                value.ActiveSessionId = GetString(ui, "activeSessionId");
                value.ActiveFilePath = GetString(ui, "activeFilePath");
                value.SelectedTabId = GetString(ui, "selectedTabId");
                value.DocumentScrollOffset = GetDouble(ui, "documentScrollOffset", 0);

                JsonValue tabs = ui.Get("tabs");
                if (tabs != null && tabs.Kind == JsonKind.Array)
                {
                    foreach (JsonValue tabValue in tabs.Items)
                    {
                        DocumentTab tab = ReadTab(tabValue);
                        if (tab != null)
                        {
                            value.OpenTabs.Add(tab);
                        }
                    }
                }
            }

            settings.Agent = ReadAgent(root.Get("agent"));
            return settings;
        }

        private static AgentSettings ReadAgent(JsonValue node)
        {
            AgentSettings value = AgentSettings.Normalize(new AgentSettings());
            if (node == null || node.Kind != JsonKind.Object)
            {
                return value;
            }

            value.SystemPrompt = GetString(node, "systemPrompt") ?? value.SystemPrompt;
            value.DefaultProviderId = GetString(node, "defaultProviderId");
            value.DefaultModelId = GetString(node, "defaultModelId");
            value.Temperature = GetDouble(node, "temperature", value.Temperature);
            value.HistoryLimit = GetInt(node, "historyLimit", value.HistoryLimit);
            value.MaxConcurrentRequests = GetInt(node, "maxConcurrentRequests", value.MaxConcurrentRequests);
            value.MaxHotSessions = GetInt(node, "maxHotSessions", value.MaxHotSessions);
            value.KeepRunningInTray = GetBool(node, "keepRunningInTray", value.KeepRunningInTray);
            value.RequestTimeoutSeconds = GetInt(node, "requestTimeoutSeconds", value.RequestTimeoutSeconds);
            value.IdleTimeoutSeconds = GetInt(node, "idleTimeoutSeconds", value.IdleTimeoutSeconds);
            value.ToolsEnabled = GetBool(node, "toolsEnabled", value.ToolsEnabled);
            value.MaxTurnsPerRun = GetInt(node, "maxTurnsPerRun", value.MaxTurnsPerRun);
            value.MaxToolCallsPerTurn = GetInt(node, "maxToolCallsPerTurn", value.MaxToolCallsPerTurn);
            value.MaxParallelReadTools = GetInt(node, "maxParallelReadTools", value.MaxParallelReadTools);
            value.SkillsEnabled = GetBool(node, "skillsEnabled", value.SkillsEnabled);
            value.ContextFilesEnabled = GetBool(node, "contextFilesEnabled", value.ContextFilesEnabled);
            value.AutoTitle = GetBool(node, "autoTitle", value.AutoTitle);
            value.ShowLogOnError = GetBool(node, "showLogOnError", value.ShowLogOnError);
            value.OnboardingCompleted = GetBool(node, "onboardingCompleted", value.OnboardingCompleted);
            value.ProxyMode = GetString(node, "proxyMode") ?? value.ProxyMode;
            value.ProxyUrl = GetString(node, "proxyUrl");
            value.StartMinimized = GetBool(node, "startMinimized", value.StartMinimized);
            value.CheckUpdatesOnStartup = GetBool(node, "checkUpdatesOnStartup", value.CheckUpdatesOnStartup);
            value.UpdateUrl = GetString(node, "updateUrl") ?? value.UpdateUrl;

            JsonValue maxOutput = node.Get("maxOutputTokens");
            if (maxOutput != null && maxOutput.Kind == JsonKind.Number)
            {
                value.MaxOutputTokens = maxOutput.AsInt(value.MaxOutputTokens ?? 0);
            }

            JsonValue toolOutput = node.Get("toolOutput");
            if (toolOutput != null && toolOutput.Kind == JsonKind.Object)
            {
                value.ToolOutput.MaxLines = GetInt(toolOutput, "maxLines", value.ToolOutput.MaxLines);
                value.ToolOutput.MaxBytes = GetInt(toolOutput, "maxBytes", value.ToolOutput.MaxBytes);
                value.ToolOutput.SpillToFile = GetBool(toolOutput, "spillToFile", value.ToolOutput.SpillToFile);
            }

            JsonValue skillPaths = node.Get("skillPaths");
            if (skillPaths != null && skillPaths.Kind == JsonKind.Array)
            {
                value.SkillPaths.Clear();
                foreach (JsonValue item in skillPaths.Items)
                {
                    if (item != null && item.Kind == JsonKind.String
                        && !string.IsNullOrWhiteSpace(item.AsString()))
                    {
                        value.SkillPaths.Add(item.AsString());
                    }
                }
            }

            return AgentSettings.Normalize(value);
        }

        public void Save(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            JsonValue root = _root ?? JsonValue.NewObject();
            root["schemaVersion"] = JsonValue.From(settings.SchemaVersion);

            JsonValue ui = root.Get("ui");
            if (ui == null || ui.Kind != JsonKind.Object)
            {
                ui = JsonValue.NewObject();
                root["ui"] = ui;
            }

            UiSettings value = settings.Ui ?? new UiSettings();
            ui["windowWidth"] = JsonValue.From(value.WindowWidth);
            ui["windowHeight"] = JsonValue.From(value.WindowHeight);
            ui["windowMaximized"] = JsonValue.From(value.WindowMaximized);
            ui["leftPaneWidth"] = JsonValue.From(value.LeftPaneWidth);
            ui["rightPaneWidth"] = JsonValue.From(value.RightPaneWidth);
            ui["rightPaneCollapsed"] = JsonValue.From(value.IsRightPaneCollapsed);
            ui["activeWorkspaceId"] = StringValue(value.ActiveWorkspaceId);
            ui["activeSessionId"] = StringValue(value.ActiveSessionId);
            ui["activeFilePath"] = StringValue(value.ActiveFilePath);
            ui["selectedTabId"] = StringValue(value.SelectedTabId);
            ui["documentScrollOffset"] = JsonValue.From(value.DocumentScrollOffset);

            var tabs = JsonValue.NewArray();
            if (value.OpenTabs != null)
            {
                foreach (DocumentTab tab in value.OpenTabs)
                {
                    if (tab == null)
                    {
                        continue;
                    }

                    var item = JsonValue.NewObject();
                    item["id"] = StringValue(tab.Id);
                    item["kind"] = JsonValue.From(tab.Kind.ToString());
                    item["title"] = StringValue(tab.Title);
                    item["filePath"] = StringValue(tab.FilePath);
                    item["scrollOffset"] = JsonValue.From(tab.ScrollOffset);
                    tabs.Add(item);
                }
            }

            ui["tabs"] = tabs;

            JsonValue agent = root.Get("agent");
            if (agent == null || agent.Kind != JsonKind.Object)
            {
                agent = JsonValue.NewObject();
                root["agent"] = agent;
            }

            AgentSettings agentValue = AgentSettings.Normalize(settings.Agent ?? new AgentSettings());
            agent["systemPrompt"] = JsonValue.From(agentValue.SystemPrompt ?? string.Empty);
            agent["defaultProviderId"] = StringValue(agentValue.DefaultProviderId);
            agent["defaultModelId"] = StringValue(agentValue.DefaultModelId);
            agent["temperature"] = JsonValue.From(agentValue.Temperature);
            agent["maxOutputTokens"] = agentValue.MaxOutputTokens.HasValue
                ? JsonValue.From(agentValue.MaxOutputTokens.Value)
                : JsonValue.Null;
            agent["historyLimit"] = JsonValue.From(agentValue.HistoryLimit);
            agent["maxConcurrentRequests"] = JsonValue.From(agentValue.MaxConcurrentRequests);
            agent["maxHotSessions"] = JsonValue.From(agentValue.MaxHotSessions);
            agent["keepRunningInTray"] = JsonValue.From(agentValue.KeepRunningInTray);
            agent["requestTimeoutSeconds"] = JsonValue.From(agentValue.RequestTimeoutSeconds);
            agent["idleTimeoutSeconds"] = JsonValue.From(agentValue.IdleTimeoutSeconds);
            agent["toolsEnabled"] = JsonValue.From(agentValue.ToolsEnabled);
            agent["maxTurnsPerRun"] = JsonValue.From(agentValue.MaxTurnsPerRun);
            agent["maxToolCallsPerTurn"] = JsonValue.From(agentValue.MaxToolCallsPerTurn);
            agent["maxParallelReadTools"] = JsonValue.From(agentValue.MaxParallelReadTools);
            agent["skillsEnabled"] = JsonValue.From(agentValue.SkillsEnabled);
            agent["contextFilesEnabled"] = JsonValue.From(agentValue.ContextFilesEnabled);
            agent["autoTitle"] = JsonValue.From(agentValue.AutoTitle);
            agent["showLogOnError"] = JsonValue.From(agentValue.ShowLogOnError);
            agent["onboardingCompleted"] = JsonValue.From(agentValue.OnboardingCompleted);
            agent["proxyMode"] = JsonValue.From(agentValue.ProxyMode ?? "system");
            agent["proxyUrl"] = StringValue(agentValue.ProxyUrl);
            agent["startMinimized"] = JsonValue.From(agentValue.StartMinimized);
            agent["checkUpdatesOnStartup"] = JsonValue.From(agentValue.CheckUpdatesOnStartup);
            agent["updateUrl"] = StringValue(agentValue.UpdateUrl);

            JsonValue toolOutput = JsonValue.NewObject();
            toolOutput["maxLines"] = JsonValue.From(agentValue.ToolOutput.MaxLines);
            toolOutput["maxBytes"] = JsonValue.From(agentValue.ToolOutput.MaxBytes);
            toolOutput["spillToFile"] = JsonValue.From(agentValue.ToolOutput.SpillToFile);
            agent["toolOutput"] = toolOutput;

            var skillPaths = JsonValue.NewArray();
            if (agentValue.SkillPaths != null)
            {
                foreach (string path in agentValue.SkillPaths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        skillPaths.Add(JsonValue.From(path));
                    }
                }
            }

            agent["skillPaths"] = skillPaths;

            AtomicFile.WriteAllText(_filePath, JsonWriter.WriteIndented(root));
        }

        private static DocumentTab ReadTab(JsonValue value)
        {
            if (value == null || value.Kind != JsonKind.Object)
            {
                return null;
            }

            string kindText = GetString(value, "kind");
            DocumentTabKind kind;
            if (string.IsNullOrEmpty(kindText)
                || !Enum.TryParse(kindText, true, out kind)
                || !Enum.IsDefined(typeof(DocumentTabKind), kind))
            {
                return null;
            }

            var tab = new DocumentTab
            {
                Id = GetString(value, "id") ?? Guid.NewGuid().ToString("N"),
                Kind = kind,
                Title = GetString(value, "title"),
                FilePath = GetString(value, "filePath"),
                ScrollOffset = GetDouble(value, "scrollOffset", 0)
            };

            if (kind == DocumentTabKind.File && string.IsNullOrWhiteSpace(tab.FilePath))
            {
                return null;
            }

            return tab;
        }

        private static JsonValue StringValue(string value)
        {
            return value == null ? JsonValue.Null : JsonValue.From(value);
        }

        private static string GetString(JsonValue parent, string key)
        {
            JsonValue value = parent == null ? null : parent.Get(key);
            return value == null ? null : value.AsString(null);
        }

        private static double GetDouble(JsonValue parent, string key, double fallback)
        {
            JsonValue value = parent == null ? null : parent.Get(key);
            return value == null ? fallback : value.AsDouble(fallback);
        }

        private static int GetInt(JsonValue parent, string key, int fallback)
        {
            JsonValue value = parent == null ? null : parent.Get(key);
            return value == null ? fallback : value.AsInt(fallback);
        }

        private static bool GetBool(JsonValue parent, string key, bool fallback)
        {
            JsonValue value = parent == null ? null : parent.Get(key);
            return value == null ? fallback : value.AsBoolean(fallback);
        }
    }
}
