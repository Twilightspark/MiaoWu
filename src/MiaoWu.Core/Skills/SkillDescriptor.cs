using System;
using System.Collections.Generic;

namespace MiaoWu.Core.Skills
{
    /// <summary>技能来源。工作区技能优先于用户级技能，同名时后者被覆盖。</summary>
    public enum SkillSource
    {
        /// <summary>当前工作区 <c>.miaowu\skills</c>。</summary>
        Workspace = 0,

        /// <summary>用户级技能目录（对全部工作区可见）。</summary>
        User = 1
    }

    /// <summary>诊断级别：错误会阻止加载，警告只提示。</summary>
    public enum SkillDiagnosticLevel
    {
        Warning = 0,
        Error = 1
    }

    /// <summary>技能目录里的一项诊断；路径是可选的绝对路径。</summary>
    public sealed class SkillDiagnostic
    {
        public SkillDiagnostic(SkillDiagnosticLevel level, string code, string message, string path = null)
        {
            Level = level;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Path = path;
        }

        public SkillDiagnosticLevel Level { get; }

        public string Code { get; }

        public string Message { get; }

        public string Path { get; }
    }

    /// <summary>技能目录内的一个附加文件（相对路径 + 大小）。</summary>
    public sealed class SkillFileEntry
    {
        public SkillFileEntry(string relativePath, string fullPath, long sizeBytes)
        {
            RelativePath = relativePath ?? string.Empty;
            FullPath = fullPath ?? string.Empty;
            SizeBytes = sizeBytes;
        }

        public string RelativePath { get; }

        public string FullPath { get; }

        public long SizeBytes { get; }
    }

    /// <summary>
    /// 一条技能目录项。正文不常驻内存，只在打开「正文」页或工具加载时读取。
    /// </summary>
    public sealed class SkillDescriptor
    {
        public string Name { get; set; }

        public string Description { get; set; }

        /// <summary><c>SKILL.md</c> 或旧式根层 <c>.md</c> 的绝对路径。</summary>
        public string BodyPath { get; set; }

        /// <summary>技能目录；旧式根层 <c>.md</c> 时为 <c>null</c>。</summary>
        public string DirectoryPath { get; set; }

        public SkillSource Source { get; set; }

        /// <summary>是否为旧式 <c>.miaowu\skills\name.md</c>，而不是文件夹 + SKILL.md。</summary>
        public bool IsLegacy { get; set; }

        /// <summary>名称 / 描述齐全且正文是可读 UTF-8 时为 true。</summary>
        public bool IsValid { get; set; }

        /// <summary>无法加载时的一行原因，例如「缺少 description」。</summary>
        public string InvalidReason { get; set; }

        /// <summary>当前工作区是否启用；由 catalog 服务结合 skills.json 填写。</summary>
        public bool IsEnabled { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public long SizeBytes { get; set; }

        public int LineCount { get; set; }

        public IReadOnlyList<SkillFileEntry> Files { get; set; }

        public IReadOnlyList<SkillDiagnostic> Diagnostics { get; set; }

        /// <summary>跨工作区稳定的行标识：来源 + 名称。</summary>
        public string Key
        {
            get
            {
                string prefix = Source == SkillSource.Workspace ? "w:" : "u:";
                return prefix + (Name ?? string.Empty).ToLowerInvariant();
            }
        }
    }

    /// <summary>一次技能扫描的完整结果。</summary>
    public sealed class SkillCatalog
    {
        public SkillCatalog(string workspaceRoot)
        {
            WorkspaceRoot = workspaceRoot;
            Skills = new List<SkillDescriptor>();
            Diagnostics = new List<SkillDiagnostic>();
            Disabled = new List<string>();
            ScannedAt = DateTimeOffset.Now;
        }

        public string WorkspaceRoot { get; }

        public IReadOnlyList<SkillDescriptor> Skills { get; set; }

        public IReadOnlyList<SkillDiagnostic> Diagnostics { get; set; }

        /// <summary><c>skills.json</c> 里的停用名单（小写名称）。</summary>
        public IReadOnlyList<string> Disabled { get; set; }

        public DateTimeOffset ScannedAt { get; set; }
    }

    /// <summary>导入技能文件夹的结果。</summary>
    public enum SkillImportStatus
    {
        Imported = 0,
        NoWorkspace = 1,
        InvalidSource = 2,
        Conflict = 3,
        Failed = 4
    }

    public sealed class SkillImportResult
    {
        public SkillImportStatus Status { get; set; }

        public string Name { get; set; }

        public string Message { get; set; }

        public SkillDescriptor Skill { get; set; }
    }

    /// <summary>
    /// 技能目录服务：扫描工作区 / 用户级技能，读写工作区级启用状态，导入技能文件夹。
    /// 页面与 <c>skill</c> 工具共用同一份扫描与过滤规则，避免界面看到的和模型可用的不一致。
    /// </summary>
    public interface ISkillCatalogService
    {
        /// <summary>用户级技能目录；没有配置时为空串。</summary>
        string UserSkillsDirectory { get; }

        /// <summary>扫描一次。工作区为空时只返回用户级技能；异常写进 catalog 诊断，不抛出。</summary>
        SkillCatalog Load(string workspaceRoot);

        /// <summary>写入 / 移除工作区 <c>.miaowu\skills.json</c> 里的停用名单。</summary>
        bool SetEnabled(string workspaceRoot, string name, bool enabled);

        /// <summary>把一个包含 <c>SKILL.md</c> 的文件夹复制到工作区技能目录。</summary>
        SkillImportResult Import(string workspaceRoot, string sourceDirectory);

        /// <summary>按严格 UTF-8 读取正文；不可读时抛出 <see cref="System.IO.InvalidDataException"/>。</summary>
        string ReadBody(SkillDescriptor skill);
    }
}
