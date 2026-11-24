using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using MiaoWu.Core.Workspaces;

namespace MiaoWu.Core.Conversations
{
    /// <summary>
    /// 工作区级智能体目录的路径契约。所有会话、技能、附件都跟着工作区走，
    /// 便于随项目一起备份、迁移与审阅；全局配置（Provider、密钥、界面状态）
    /// 仍留在应用数据目录。
    /// </summary>
    /// <remarks>
    /// <code>
    /// &lt;workspace&gt;\.miaowu\
    /// ├─ skills\          # 工作区技能（用户可编辑，可随仓库提交）
    /// ├─ sessions\        # index.json + {sessionId}.jsonl
    /// ├─ goals\           # {sessionId}.json：会话目标状态
    /// ├─ attachments\     # 会话附件（P3.5）
    /// └─ cache\           # 可随时删除的派生缓存
    /// </code>
    /// 本类只计算路径，不创建目录；创建由基础设施层在首次写入时完成。
    /// 工作区数据目录在磁盘上带 Hidden 属性，也不出现在工作区文件视图中。
    /// </remarks>
    public static class AgentWorkspaceLayout
    {
        /// <summary>工作区数据目录名；与全局配置目录同名，但位于各工作区根下。</summary>
        public const string DirectoryName = ".miaowu";

        /// <summary>
        /// 旧版工作区数据目录名。当前版本不再读取或迁移它，只在文件视图与工具遍历中排除，
        /// 避免历史目录混入工作区内容。
        /// </summary>
        public const string LegacyDirectoryName = ".agent";

        public const string SkillsDirectoryName = "skills";
        public const string SessionsDirectoryName = "sessions";
        public const string GoalsDirectoryName = "goals";
        public const string AttachmentsDirectoryName = "attachments";
        public const string CacheDirectoryName = "cache";
        public const string IndexFileName = "index.json";
        public const string SessionFileExtension = ".jsonl";

        private const int MaxSessionIdLength = 64;

        private static readonly Regex SessionIdPattern = new Regex(
            "^[A-Za-z0-9_-]{1,64}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>会话 ID 只允许字母、数字、下划线与连字符，且不能超过 64 字符。</summary>
        public static bool IsValidSessionId(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId)
                && sessionId.Length <= MaxSessionIdLength
                && SessionIdPattern.IsMatch(sessionId);
        }

        /// <summary>
        /// 判断目录名是否属于工作区数据目录（含旧版 <c>.agent</c>，忽略大小写）。
        /// 文件视图、文件索引与工具遍历都据此跳过该目录。
        /// </summary>
        public static bool IsDataDirectoryName(string name)
        {
            return string.Equals(name, DirectoryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, LegacyDirectoryName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 生成一个新会话 ID。带时间前缀便于人工排查，随机后缀避免同一秒碰撞。
        /// </summary>
        public static string CreateSessionId(DateTimeOffset now)
        {
            return "s_" + now.UtcDateTime.ToString("yyyyMMddHHmmss") + "_"
                + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public static string GetAgentDirectory(string workspacePath)
        {
            return Path.Combine(RequirePath(workspacePath), DirectoryName);
        }

        public static string GetSkillsDirectory(string workspacePath)
        {
            return Path.Combine(GetAgentDirectory(workspacePath), SkillsDirectoryName);
        }

        public static string GetSessionsDirectory(string workspacePath)
        {
            return Path.Combine(GetAgentDirectory(workspacePath), SessionsDirectoryName);
        }

        public static string GetAttachmentsDirectory(string workspacePath)
        {
            return Path.Combine(GetAgentDirectory(workspacePath), AttachmentsDirectoryName);
        }

        public static string GetGoalsDirectory(string workspacePath)
        {
            return Path.Combine(GetAgentDirectory(workspacePath), GoalsDirectoryName);
        }

        /// <summary>
        /// 会话目标状态文件的完整路径。和会话 JSONL 一样校验 ID，防止路径逃出
        /// <c>.miaowu\goals</c>。
        /// </summary>
        public static string GetGoalFilePath(string workspacePath, string sessionId)
        {
            if (!IsValidSessionId(sessionId))
            {
                throw new ArgumentException(
                    "会话 ID 只能包含字母、数字、下划线与连字符，且长度不超过 64。",
                    nameof(sessionId));
            }

            return Path.Combine(GetGoalsDirectory(workspacePath), sessionId + ".json");
        }

        public static string GetCacheDirectory(string workspacePath)
        {
            return Path.Combine(GetAgentDirectory(workspacePath), CacheDirectoryName);
        }

        public static string GetIndexFilePath(string workspacePath)
        {
            return Path.Combine(GetSessionsDirectory(workspacePath), IndexFileName);
        }

        /// <summary>
        /// 会话 JSONL 的完整路径。非法 ID 抛 <see cref="ArgumentException"/>，
        /// 防止 <c>..\..\</c> 或绝对路径逃出 <c>.miaowu\sessions</c>。
        /// </summary>
        public static string GetSessionFilePath(string workspacePath, string sessionId)
        {
            if (!IsValidSessionId(sessionId))
            {
                throw new ArgumentException(
                    "会话 ID 只能包含字母、数字、下划线与连字符，且长度不超过 64。",
                    nameof(sessionId));
            }

            return Path.Combine(
                GetSessionsDirectory(workspacePath),
                sessionId + SessionFileExtension);
        }

        /// <summary>首次使用时需要创建的目录，按父目录在前排列。</summary>
        public static IReadOnlyList<string> GetManagedDirectories(string workspacePath)
        {
            string agent = GetAgentDirectory(workspacePath);
            return new[]
            {
                agent,
                Path.Combine(agent, SkillsDirectoryName),
                Path.Combine(agent, SessionsDirectoryName),
                Path.Combine(agent, GoalsDirectoryName),
                Path.Combine(agent, AttachmentsDirectoryName),
                Path.Combine(agent, CacheDirectoryName)
            };
        }

        /// <summary>
        /// 判断路径是否位于工作区的数据目录（<c>.miaowu</c>，兼容旧版 <c>.agent</c>）内，忽略大小写。
        /// </summary>
        public static bool IsInsideAgentDirectory(string workspacePath, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || string.IsNullOrWhiteSpace(candidatePath))
            {
                return false;
            }

            string candidate = Path.GetFullPath(candidatePath);
            foreach (string directoryName in new[] { DirectoryName, LegacyDirectoryName })
            {
                string directory = Path.GetFullPath(
                    Path.Combine(RequirePath(workspacePath), directoryName))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                if (candidate.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>由工作区模型取路径；工作区为空或路径为空时返回 <c>null</c>。</summary>
        public static string GetWorkspacePath(Workspace workspace)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(workspace.Path))
            {
                return null;
            }

            return workspace.Path;
        }

        private static string RequirePath(string workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                throw new ArgumentException("工作区路径不能为空。", nameof(workspacePath));
            }

            return Path.GetFullPath(workspacePath);
        }
    }
}
