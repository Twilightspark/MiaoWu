using System;
using System.Collections.Generic;
using System.IO;
using MiaoWu.Core.Abstractions;
using MiaoWu.Core.Workspaces;
using MiaoWu.Infrastructure.Json;

namespace MiaoWu.Infrastructure.Storage
{
    /// <summary>
    /// workspaces.json 存取：工作区列表 + 展开项 + 每个工作区的视图/滚动/选中状态。
    /// 路径失效时保留配置，不悄悄删工作区。
    /// </summary>
    public sealed class WorkspaceStore : IWorkspaceStore
    {
        private readonly string _filePath;

        public WorkspaceStore(IAppPaths paths)
            : this(paths == null
                ? throw new ArgumentNullException(nameof(paths))
                : Path.Combine(paths.DataDirectory, "workspaces.json"))
        {
        }

        public WorkspaceStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("workspaces.json 路径不能为空。", nameof(filePath));
            }

            _filePath = filePath;
        }

        public string FilePath
        {
            get { return _filePath; }
        }

        public WorkspaceCollection Load()
        {
            var collection = new WorkspaceCollection();

            if (!File.Exists(_filePath))
            {
                return collection;
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(AtomicFile.ReadAllText(_filePath));
            }
            catch (Exception)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                return collection;
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                AtomicFile.BackupCorrupt(_filePath, "corrupt");
                return collection;
            }

            collection.SchemaVersion = GetInt(root, "schemaVersion", 1);
            collection.ActiveWorkspaceId = GetString(root, "activeWorkspaceId");

            JsonValue workspaces = root.Get("workspaces");
            if (workspaces == null || workspaces.Kind != JsonKind.Array)
            {
                return collection;
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonValue item in workspaces.Items)
            {
                Workspace workspace = ReadWorkspace(item);
                if (workspace == null)
                {
                    continue;
                }

                if (!seenIds.Add(workspace.Id) || !seenPaths.Add(workspace.Path))
                {
                    continue;
                }

                collection.Workspaces.Add(workspace);
            }

            if (!string.IsNullOrEmpty(collection.ActiveWorkspaceId)
                && WorkspacePolicy.FindById(collection.Workspaces, collection.ActiveWorkspaceId) == null)
            {
                collection.ActiveWorkspaceId = null;
            }

            return collection;
        }

        public void Save(WorkspaceCollection collection)
        {
            if (collection == null)
            {
                throw new ArgumentNullException(nameof(collection));
            }

            var root = JsonValue.NewObject();
            root["schemaVersion"] = JsonValue.From(collection.SchemaVersion <= 0 ? 1 : collection.SchemaVersion);
            root["activeWorkspaceId"] = StringValue(collection.ActiveWorkspaceId);

            var workspaces = JsonValue.NewArray();
            IReadOnlyList<Workspace> ordered = WorkspacePolicy.Sort(collection.Workspaces);
            int order = 0;

            foreach (Workspace workspace in ordered)
            {
                if (workspace == null || string.IsNullOrWhiteSpace(workspace.Path))
                {
                    continue;
                }

                var item = JsonValue.NewObject();
                item["id"] = JsonValue.From(workspace.Id ?? Guid.NewGuid().ToString("N"));
                item["name"] = StringValue(workspace.Name);
                item["path"] = StringValue(workspace.Path);
                item["order"] = JsonValue.From(order++);
                item["pinned"] = JsonValue.From(workspace.IsPinned);
                item["expanded"] = JsonValue.From(workspace.IsExpanded);
                item["view"] = JsonValue.From(workspace.View.ToString());
                item["scrollOffset"] = JsonValue.From(workspace.ScrollOffset);
                item["selectedSessionId"] = StringValue(workspace.SelectedSessionId);
                item["selectedFilePath"] = StringValue(workspace.SelectedFilePath);
                workspaces.Add(item);
            }

            root["workspaces"] = workspaces;

            AtomicFile.WriteAllText(_filePath, JsonWriter.WriteIndented(root));
        }

        private static Workspace ReadWorkspace(JsonValue value)
        {
            if (value == null || value.Kind != JsonKind.Object)
            {
                return null;
            }

            string pathText = GetString(value, "path");
            if (string.IsNullOrWhiteSpace(pathText))
            {
                return null;
            }

            string normalized;
            try
            {
                normalized = WorkspacePolicy.NormalizePath(pathText);
            }
            catch (Exception)
            {
                return null;
            }

            if (normalized.Length == 0)
            {
                return null;
            }

            WorkspaceView view;
            string viewText = GetString(value, "view");
            if (string.IsNullOrEmpty(viewText)
                || !Enum.TryParse(viewText, true, out view)
                || !Enum.IsDefined(typeof(WorkspaceView), view))
            {
                view = WorkspaceView.Sessions;
            }

            return new Workspace
            {
                Id = GetString(value, "id") ?? Guid.NewGuid().ToString("N"),
                Name = GetString(value, "name") ?? WorkspacePolicy.GetDisplayName(normalized),
                Path = normalized,
                Order = GetInt(value, "order", 0),
                IsPinned = GetBool(value, "pinned", false),
                IsExpanded = GetBool(value, "expanded", false),
                View = view,
                ScrollOffset = Math.Max(0, GetDouble(value, "scrollOffset", 0)),
                SelectedSessionId = GetString(value, "selectedSessionId"),
                SelectedFilePath = GetString(value, "selectedFilePath")
            };
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
