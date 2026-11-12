using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MiaoWu.Core.Workspaces
{
    /// <summary>
    /// 工作区列表的规则：路径规范化、去重、排序、手风琴单选展开。
    /// 不触碰文件系统以外的状态；错误用返回值表达，不抛给 UI。
    /// </summary>
    public static class WorkspacePolicy
    {
        public const string EmptyPathError = "工作区路径不能为空。";
        public const string InvalidPathError = "工作区路径无效。";
        public const string MissingFolderError = "文件夹不存在，无法添加为工作区。";
        public const string DuplicatePathError = "该文件夹已经在工作区列表中。";

        /// <summary>规范化路径：绝对路径、去掉末尾分隔符；根目录保留分隔符。</summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string fullPath = Path.GetFullPath(path.Trim());
            string root = Path.GetPathRoot(fullPath) ?? string.Empty;

            if (fullPath.Length > root.Length)
            {
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return fullPath;
        }

        public static bool IsSamePath(string left, string right)
        {
            string a = NormalizePath(left);
            string b = NormalizePath(right);
            if (a.Length == 0 || b.Length == 0)
            {
                return false;
            }

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>工作区显示名：取文件夹名；根目录显示完整路径。</summary>
        public static string GetDisplayName(string path)
        {
            string normalized = NormalizePath(path);
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            string name = Path.GetFileName(normalized);
            return string.IsNullOrEmpty(name) ? normalized : name;
        }

        public static Workspace FindById(IEnumerable<Workspace> workspaces, string id)
        {
            if (workspaces == null || string.IsNullOrEmpty(id))
            {
                return null;
            }

            return workspaces.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));
        }

        public static Workspace FindByPath(IEnumerable<Workspace> workspaces, string path)
        {
            if (workspaces == null)
            {
                return null;
            }

            return workspaces.FirstOrDefault(w => IsSamePath(w.Path, path));
        }

        /// <summary>添加工作区。重复路径或目录不存在时返回 false 并给出错误文案。</summary>
        public static bool TryAdd(
            IList<Workspace> workspaces,
            string path,
            out Workspace workspace,
            out string error)
        {
            workspace = null;
            error = null;

            if (workspaces == null)
            {
                error = InvalidPathError;
                return false;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                error = EmptyPathError;
                return false;
            }

            string normalized;
            try
            {
                normalized = NormalizePath(path);
            }
            catch (Exception)
            {
                error = InvalidPathError;
                return false;
            }

            if (normalized.Length == 0)
            {
                error = EmptyPathError;
                return false;
            }

            if (!Directory.Exists(normalized))
            {
                error = MissingFolderError;
                return false;
            }

            if (FindByPath(workspaces, normalized) != null)
            {
                error = DuplicatePathError;
                return false;
            }

            int order = workspaces.Count == 0 ? 0 : workspaces.Max(w => w.Order) + 1;
            workspace = Workspace.Create(
                Guid.NewGuid().ToString("N"),
                GetDisplayName(normalized),
                normalized,
                order);

            workspaces.Add(workspace);
            return true;
        }

        /// <summary>手风琴：只展开指定工作区；找不到时全部收起。</summary>
        public static bool ExpandOnly(IEnumerable<Workspace> workspaces, string id)
        {
            if (workspaces == null)
            {
                return false;
            }

            bool changed = false;
            foreach (Workspace workspace in workspaces)
            {
                bool shouldExpand = !string.IsNullOrEmpty(id)
                    && string.Equals(workspace.Id, id, StringComparison.Ordinal);

                if (workspace.IsExpanded != shouldExpand)
                {
                    workspace.IsExpanded = shouldExpand;
                    changed = true;
                }
            }

            return changed;
        }

        public static Workspace GetExpanded(IEnumerable<Workspace> workspaces)
        {
            return workspaces == null ? null : workspaces.FirstOrDefault(w => w.IsExpanded);
        }

        public static IReadOnlyList<Workspace> Sort(IEnumerable<Workspace> workspaces)
        {
            if (workspaces == null)
            {
                return new Workspace[0];
            }

            return workspaces
                .OrderBy(w => w.Order)
                .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void Renumber(IList<Workspace> workspaces)
        {
            if (workspaces == null)
            {
                return;
            }

            for (int index = 0; index < workspaces.Count; index++)
            {
                workspaces[index].Order = index;
            }
        }
    }
}
